using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Wifinian.Models.History;

namespace Wifinian.Models.Measure;

/// <summary>
/// Size of a speed measurement
/// </summary>
internal enum SpeedTestSize
{
	Quick = 0,
	Full
}

/// <summary>
/// Measurer of speed of a connected network through a specified network interface
/// </summary>
internal sealed class SpeedTester
{
	#region Constant

	private const string ConnectivityContent = "Microsoft Connect Test";
	private const string UserAgent = "Wifinian";

	private const int ConnectivityTimeout = 4000; // ms
	private const int ConnectivityRetryDelay = 1000; // ms
	private const int PingCount = 3;
	private const int PingTimeout = 1000; // ms
	private const int ResolveTimeout = 5000; // ms
	private const int ConnectCount = 5;
	private const int ConnectTimeout = 2000; // ms
	private const int RequestTimeout = 10000; // ms
	private const int StopTimeout = 2000; // ms
	private const int SampleInterval = 100; // ms
	private const int BufferSize = 64 * 1024;
	private const int ConnectionLimit = 8;

	private const int DownloadCount = 4;
	private const int DownloadWarmup = 1000; // ms
	internal const int UploadCount = 3;
	internal const int UploadWarmup = 1000; // ms

	/// <summary>
	/// Minimum time (ms) after warm-up over which upload speed is measured
	/// </summary>
	internal const int UploadMinWindow = 3000;

	/// <summary>
	/// Size of the send buffer of a socket for upload
	/// </summary>
	/// <remarks>
	/// Uploaded bytes are counted when written to the socket. Bytes written but not sent yet stay
	/// in this buffer and so it is kept small and fixed: once it is full, which happens within
	/// warm-up, bytes can be written only as fast as they are sent and acknowledged. The default
	/// buffer grows with the amount written and would let megabytes be counted as uploaded in
	/// a moment, which made upload look several times faster than the link.
	/// </remarks>
	private const int UploadSendBuffer = 256 * 1024;

	/// <summary>
	/// Ratio of bytes received by the interface to downloaded bytes below which the download is
	/// regarded as having gone through another interface
	/// </summary>
	private const double RouteRatio = 0.8;

	/// <summary>
	/// Ratio of bytes sent by the interface to uploaded bytes below which the upload is regarded
	/// as having gone through another interface
	/// </summary>
	/// <remarks>
	/// This is lower than that of download because uploaded bytes are counted when written and
	/// so include those which remained in the send buffers when the upload was stopped.
	/// </remarks>
	private const double UploadRouteRatio = 0.5;

	/// <summary>
	/// Gets the caps of upload. The time includes warm-up and is long enough for the minimum
	/// window after it regardless of the interval of sampling.
	/// </summary>
	/// <remarks>
	/// The bytes of a quick measurement, which automatic ones are, are capped as low as those of
	/// its download so that hourly measurements fit in the daily budget. The cap is reached only
	/// by a link faster than about 40 Mbps, on which the window after warm-up becomes shorter
	/// than the minimum (about a second at 80 Mbps).
	/// </remarks>
	internal static (int capTime, long capBytes) GetUploadCap(SpeedTestSize size) => size switch
	{
		SpeedTestSize.Full => (UploadWarmup + 5000 + 2 * SampleInterval, 80_000_000L),
		_ => (UploadWarmup + UploadMinWindow + 2 * SampleInterval, QuickUploadBytes)
	};

	/// <summary>
	/// Maximum bytes uploaded by a quick measurement
	/// </summary>
	internal const long QuickUploadBytes = 20_000_000L;

	/// <summary>
	/// Maximum bytes downloaded by a quick measurement
	/// </summary>
	internal const long QuickDownloadBytes = 20_000_000L;

	private static (int capTime, long capBytes) GetDownloadCap(SpeedTestSize size) => size switch
	{
		SpeedTestSize.Full => (8000, 50_000_000L),
		_ => (5000, QuickDownloadBytes)
	};

	#endregion

	static SpeedTester()
	{
		try
		{
			// The default value (SystemDefault) follows the operating system, which includes
			// TLS 1.2 and later, and so must be left as it is.
			if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
				ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to enable TLS 1.2." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Measures speed of a connected network.
	/// </summary>
	/// <param name="template">Speed sample which has the time, the interface, the profile and the trigger</param>
	/// <param name="size">Size of measurement</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The same speed sample filled with measured values and the result</returns>
	/// <remarks>This method never throws an exception.</remarks>
	public Task<SpeedSample> MeasureAsync(SpeedSample template, SpeedTestSize size, CancellationToken cancellationToken)
	{
		if (template is null)
			return Task.FromResult<SpeedSample>(null);

		// HttpWebRequest can block the calling thread for name resolution and proxy detection.
		return Task.Run(() => MeasureBaseAsync(template, size, cancellationToken));
	}

	private static async Task<SpeedSample> MeasureBaseAsync(SpeedSample sample, SpeedTestSize size, CancellationToken token)
	{
		var stopwatch = Stopwatch.StartNew();
		try
		{
			token.ThrowIfCancellationRequested();

			var connectivityUri = GetUri(Settings.Current.ConnectivityCheckUrl, Settings.DefaultConnectivityCheckUrl);
			var downloadUri = GetUri(Settings.Current.SpeedTestDownloadUrl, Settings.DefaultSpeedTestDownloadUrl);
			var uploadUri = GetUri(Settings.Current.SpeedTestUploadUrl, Settings.DefaultSpeedTestUploadUrl);

			// (1) Link
			var networkInterface = LinkInspector.Find(sample.InterfaceId);
			var localAddress = LinkInspector.GetLocalAddress(networkInterface);
			if ((networkInterface is not { OperationalStatus: OperationalStatus.Up }) || (localAddress is null))
			{
				sample.Result = SpeedResult.Error;
				return sample;
			}

			var localAddressV6 = LinkInspector.GetGlobalAddressV6(networkInterface);

			// All requests are bound to the address of the interface so that another interface
			// (Ethernet, another wireless adapter) is not measured.
			IPEndPoint Bind(ServicePoint servicePoint, IPEndPoint remoteEndPoint, int retryCount)
			{
				if (retryCount >= 2)
					return null;

				return remoteEndPoint.AddressFamily switch
				{
					AddressFamily.InterNetwork => new IPEndPoint(localAddress, 0),
					AddressFamily.InterNetworkV6 when (localAddressV6 is not null) => new IPEndPoint(localAddressV6, 0),
					_ => null
				};
			}

			// (2) Connectivity
			var connectivity = await CheckConnectivityAsync(connectivityUri, Bind, token).ConfigureAwait(false);
			if (connectivity is Connectivity.None)
			{
				await Task.Delay(ConnectivityRetryDelay, token).ConfigureAwait(false);
				connectivity = await CheckConnectivityAsync(connectivityUri, Bind, token).ConfigureAwait(false);
			}

			switch (connectivity)
			{
				case Connectivity.Portal:
					sample.Result = SpeedResult.CaptivePortal;
					sample.DownMbps = 0D;
					return sample;

				case Connectivity.None:
					// No internet access is a finding on this network only if the request really
					// went through it. If the same request succeeds without binding, the failure
					// is of binding or routing.
					var unbound = await CheckConnectivityAsync(connectivityUri, null, token).ConfigureAwait(false);
					if (unbound is Connectivity.None)
					{
						sample.Result = SpeedResult.NoInternet;
						sample.DownMbps = 0D;
					}
					else
					{
						Trace.WriteLine($"Connectivity check failed through {networkInterface.Name} ({localAddress}) but succeeded without binding.");
						sample.Result = SpeedResult.RouteMismatch;
					}
					return sample;
			}

			// (3) Gateway
			sample.GatewayMs = Round(await PingAsync(LinkInspector.GetGateway(networkInterface), token).ConfigureAwait(false), 1);

			// (4) Latency
			var remoteAddress = await ResolveAsync(downloadUri.Host, token).ConfigureAwait(false);
			if (remoteAddress is null)
			{
				sample.Result = SpeedResult.EndpointFailed;
				return sample;
			}

			var connectTimes = await ConnectAsync(localAddress, new IPEndPoint(remoteAddress, downloadUri.Port), token).ConfigureAwait(false);
			var latency = GetMedian(connectTimes);
			if (latency is null)
			{
				sample.Result = SpeedResult.EndpointFailed;
				return sample;
			}

			sample.LatencyMs = Round(latency, 1);
			sample.JitterMs = Round(GetMeanDeviation(connectTimes, latency.Value), 1);

			// (5) Download
			var oldBytes = LinkInspector.GetBytes(networkInterface);
			var oldReceived = GetReceivedBytes();

			var (downloadCapTime, downloadCapBytes) = GetDownloadCap(size);
			var downloadCounter = new Counter();
			try
			{
				sample.DownMbps = Round(await TransferAsync(
					(stop) => DownloadAsync(downloadUri, Bind, downloadCounter, stop),
					DownloadCount,
					downloadCounter,
					DownloadWarmup,
					downloadCapTime,
					downloadCapBytes,
					() => LinkInspector.GetBytes(networkInterface).received,
					token).ConfigureAwait(false), 2);
			}
			finally
			{
				// The bytes are recorded even if aborted because they count for the daily budget.
				sample.DownBytes = downloadCounter.Value;
			}

			if ((sample.DownBytes == 0L) || (sample.DownMbps is null))
			{
				sample.DownMbps = null;
				sample.Result = SpeedResult.EndpointFailed;
				return sample;
			}

			var newBytes = LinkInspector.GetBytes(networkInterface);
			if (newBytes.received - oldBytes.received < RouteRatio * sample.DownBytes)
			{
				TraceCarrier(networkInterface, oldReceived, GetReceivedBytes(), sample.DownBytes);
				sample.Result = SpeedResult.RouteMismatch;
				return sample;
			}

			// (6) Upload
			var (uploadCapTime, uploadCapBytes) = GetUploadCap(size);
			var uploadCounter = new Counter();
			var oldSent = LinkInspector.GetBytes(networkInterface).sent;
			try
			{
				var upMbps = await MeasureUploadAsync(
					uploadUri,
					localAddress,
					uploadCounter,
					uploadCapTime,
					uploadCapBytes,
					token).ConfigureAwait(false);

				sample.UpMbps = (uploadCounter.Value > 0L) ? Round(upMbps, 2) : null;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// Failure of upload alone leaves the upload speed blank.
				Trace.WriteLine("Failed to measure upload speed." + Environment.NewLine
					+ ex);
			}
			finally
			{
				sample.UpBytes = uploadCounter.Value;
			}

			// The upload is bound to the address of the interface. This check is for the case
			// where the system routed it through another interface regardless.
			if (sample.UpMbps.HasValue)
			{
				var sent = LinkInspector.GetBytes(networkInterface).sent - oldSent;
				if (sent < UploadRouteRatio * sample.UpBytes)
				{
					Trace.WriteLine($"Upload of {sample.UpBytes} bytes was not sent through {networkInterface.Name}. It sent {sent} bytes.");
					sample.UpMbps = null;
				}
			}

			// (7) Result
			sample.Result = SpeedResult.Ok;
			return sample;
		}
		catch (OperationCanceledException)
		{
			sample.Result = SpeedResult.Aborted;
			return sample;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to measure speed." + Environment.NewLine
				+ ex);

			sample.Result = SpeedResult.Error;
			return sample;
		}
		finally
		{
			sample.DurationMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
		}
	}

	private static Uri GetUri(string url, string defaultUrl)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
			&& ((uri.Scheme == Uri.UriSchemeHttp) || (uri.Scheme == Uri.UriSchemeHttps)))
			return uri;

		return new Uri(defaultUrl);
	}

	#region Connectivity

	private enum Connectivity
	{
		/// <summary>
		/// No response
		/// </summary>
		None = 0,

		/// <summary>
		/// Expected response
		/// </summary>
		Pass,

		/// <summary>
		/// Response other than expected (redirect, sign-in page)
		/// </summary>
		Portal
	}

	private static async Task<Connectivity> CheckConnectivityAsync(Uri uri, BindIPEndPoint bind, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();

		HttpWebRequest request = null;
		try
		{
			request = CreateRequest(uri, bind, ConnectivityTimeout);
			request.AllowAutoRedirect = false;

			// Timeout of HttpWebRequest does not work in asynchronous calls.
			using var scope = new AbortScope(request, ConnectivityTimeout, token);
			using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);

			switch (response.StatusCode)
			{
				case HttpStatusCode.NoContent:
					return Connectivity.Pass;

				case HttpStatusCode.OK:
					using (var stream = response.GetResponseStream())
					{
						var buffer = new byte[256];
						int length = 0;
						int read;
						while ((length < buffer.Length)
							&& ((read = await stream.ReadAsync(buffer, length, buffer.Length - length).ConfigureAwait(false)) > 0))
						{
							length += read;
						}

						var content = Encoding.ASCII.GetString(buffer, 0, length).Trim();
						return string.Equals(content, ConnectivityContent, StringComparison.Ordinal)
							? Connectivity.Pass
							: Connectivity.Portal;
					}

				default:
					return Connectivity.Portal;
			}
		}
		catch (WebException ex) when (ex.Response is not null)
		{
			token.ThrowIfCancellationRequested();

			// An error status is from something other than the expected server.
			ex.Response.Dispose();
			return Connectivity.Portal;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			token.ThrowIfCancellationRequested();

			Debug.WriteLine($"Connectivity check failed. {ex.Message}");
			return Connectivity.None;
		}
		finally
		{
			CloseConnection(request);
		}
	}

	#endregion

	#region Gateway/Latency

	private static async Task<double?> PingAsync(IPAddress address, CancellationToken token)
	{
		if (address is null)
			return null;

		var times = new List<double>();

		for (int i = 0; i < PingCount; i++)
		{
			token.ThrowIfCancellationRequested();

			try
			{
				using var ping = new Ping();
				var reply = await ping.SendPingAsync(address, PingTimeout).ConfigureAwait(false);
				if (reply.Status == IPStatus.Success)
					times.Add(reply.RoundtripTime);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Ping failed. {ex.Message}");
			}
		}
		return GetMedian(times);
	}

	private static async Task<IPAddress> ResolveAsync(string host, CancellationToken token)
	{
		try
		{
			if (IPAddress.TryParse(host, out IPAddress address))
				return (address.AddressFamily == AddressFamily.InterNetwork) ? address : null;

			var resolveTask = Dns.GetHostAddressesAsync(host);
			if (!await WaitAsync(resolveTask, ResolveTimeout, token).ConfigureAwait(false))
				return null;

			return (await resolveTask.ConfigureAwait(false))
				.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Debug.WriteLine($"Name resolution failed. {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Measures the times (ms) of TCP connection to a remote end point from a local address.
	/// </summary>
	private static async Task<List<double>> ConnectAsync(IPAddress localAddress, IPEndPoint remoteEndPoint, CancellationToken token)
	{
		var times = new List<double>();

		for (int i = 0; i < ConnectCount; i++)
		{
			token.ThrowIfCancellationRequested();

			try
			{
				using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				socket.Bind(new IPEndPoint(localAddress, 0));

				var stopwatch = Stopwatch.StartNew();
				var connectTask = socket.ConnectAsync(remoteEndPoint.Address, remoteEndPoint.Port);
				if (!await WaitAsync(connectTask, ConnectTimeout, token).ConfigureAwait(false))
					continue;

				await connectTask.ConfigureAwait(false);
				times.Add(stopwatch.Elapsed.TotalMilliseconds);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Debug.WriteLine($"Connection failed. {ex.Message}");
			}
		}
		return times;
	}

	#endregion

	#region Download/Upload

	/// <summary>
	/// Counter of bytes shared by parallel transfers
	/// </summary>
	internal sealed class Counter
	{
		private long _value;

		public long Value => Interlocked.Read(ref _value);

		public void Add(int count) => Interlocked.Add(ref _value, count);
	}

	/// <summary>
	/// Runs parallel transfers until a cap is reached and computes the rate (Mbps).
	/// </summary>
	/// <param name="work">Function to run a transfer until stopped (it must not throw)</param>
	/// <param name="count">Number of parallel transfers</param>
	/// <param name="counter">Counter of bytes to which the transfers add</param>
	/// <param name="warmupTime">Time (ms) from the first byte to be discarded</param>
	/// <param name="capTime">Maximum time (ms) from the first byte</param>
	/// <param name="capBytes">Maximum bytes</param>
	/// <param name="getAdapterBytes">Function to get the counter of the network interface</param>
	/// <param name="token">Cancellation token</param>
	/// <param name="minWindow">Minimum time (ms) over which the rate must be measured unless the cap of bytes is reached</param>
	/// <returns>Rate (Mbps) if any byte is transferred, null otherwise</returns>
	private static async Task<double?> TransferAsync(
		Func<CancellationToken, Task> work,
		int count,
		Counter counter,
		int warmupTime,
		int capTime,
		long capBytes,
		Func<long> getAdapterBytes,
		CancellationToken token,
		int minWindow = 0)
	{
		using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
		var stopToken = stop.Token;

		var workTask = Task.WhenAll(Enumerable.Range(0, count)
			.Select(_ => Task.Run(() => work.Invoke(stopToken)))
			.ToArray());
		try
		{
			// Wait for the first byte so that time for establishing connections is not included.
			var waitStopwatch = Stopwatch.StartNew();
			while (counter.Value == 0L)
			{
				if (workTask.IsCompleted || (waitStopwatch.ElapsedMilliseconds >= RequestTimeout))
					return null;

				await Task.Delay(20, token).ConfigureAwait(false);
			}

			var stopwatch = Stopwatch.StartNew();
			var points = new List<RatePoint> { new(0L, counter.Value, getAdapterBytes.Invoke()) };
			bool isCapBytesReached = false;

			while (true)
			{
				await Task.Delay(SampleInterval, token).ConfigureAwait(false);

				var point = new RatePoint(stopwatch.ElapsedMilliseconds, counter.Value, getAdapterBytes.Invoke());
				points.Add(point);

				if (point.Bytes >= capBytes)
				{
					isCapBytesReached = true;
					break;
				}
				if ((point.Time >= capTime) || workTask.IsCompleted)
					break;
			}

			return GetRateMbps(points, warmupTime, isCapBytesReached, minWindow);
		}
		finally
		{
			stop.Cancel();

			// The transfers are aborted and so will end soon.
			await Task.WhenAny(workTask, Task.Delay(StopTimeout)).ConfigureAwait(false);
			Forget(workTask);
		}
	}

	/// <summary>
	/// Point of time and bytes during a transfer
	/// </summary>
	internal readonly struct RatePoint
	{
		/// <summary>
		/// Time (ms) from the first byte
		/// </summary>
		public long Time { get; }

		/// <summary>
		/// Bytes transferred by this measurement
		/// </summary>
		public long Bytes { get; }

		/// <summary>
		/// Bytes counted by the network interface (cumulative)
		/// </summary>
		public long AdapterBytes { get; }

		public RatePoint(long time, long bytes, long adapterBytes)
		{
			this.Time = time;
			this.Bytes = bytes;
			this.AdapterBytes = adapterBytes;
		}
	}

	/// <summary>
	/// Computes the rate (Mbps) from points during a transfer.
	/// </summary>
	/// <param name="points">Points in order of time, the first of which is at the first byte</param>
	/// <param name="warmupTime">Time (ms) from the first byte to be discarded</param>
	/// <param name="isCapBytesReached">Whether the transfer ended because of the cap of bytes</param>
	/// <param name="minWindow">Minimum time (ms) over which the rate must be measured unless the cap of bytes is reached</param>
	/// <returns>Rate (Mbps) if computable, null otherwise</returns>
	/// <remarks>
	/// The larger of the bytes of this measurement and those of the network interface is used
	/// so that traffic of other applications during the measurement does not make the network
	/// look slow. Both are differences between the same two points and so nothing is counted twice.
	/// </remarks>
	internal static double? GetRateMbps(IReadOnlyList<RatePoint> points, int warmupTime, bool isCapBytesReached, int minWindow = 0)
	{
		if (points is not { Count: >= 2 })
			return null;

		var first = points[0];
		var last = points[points.Count - 1];
		var start = first;

		int warmupIndex = FindIndex(x => x.Time >= warmupTime);
		if ((0 <= warmupIndex) && (last.Time - points[warmupIndex].Time >= warmupTime))
		{
			// Enough time remains after warm-up.
			start = points[warmupIndex];
		}
		else if (isCapBytesReached)
		{
			// The cap of bytes is reached before warm-up ends. Use the last half of bytes.
			var half = first.Bytes + (last.Bytes - first.Bytes) / 2;
			int halfIndex = FindIndex(x => x.Bytes > half) - 1;
			if (0 < halfIndex)
				start = points[halfIndex];
		}

		var time = last.Time - start.Time;
		if (time <= 0L)
			return null;

		// A rate over a window shorter than required is not reliable. A transfer ended by
		// the cap of bytes is of a link so fast that the window cannot be longer.
		if ((time < minWindow) && !isCapBytesReached)
			return null;

		var bytes = Math.Max(last.Bytes - start.Bytes, last.AdapterBytes - start.AdapterBytes);
		if (bytes < 0L)
			return null;

		return bytes * 8D / (time / 1000D) / 1e6;

		int FindIndex(Func<RatePoint, bool> predicate)
		{
			for (int i = 0; i < points.Count; i++)
			{
				if (predicate.Invoke(points[i]))
					return i;
			}
			return -1;
		}
	}

	private static async Task DownloadAsync(Uri uri, BindIPEndPoint bind, Counter counter, CancellationToken stop)
	{
		try
		{
			var buffer = new byte[BufferSize];

			// If the content is smaller than the cap, it is requested repeatedly.
			while (!stop.IsCancellationRequested)
			{
				long received = 0L;

				var request = CreateRequest(uri, bind, RequestTimeout);
				try
				{
					using var registration = stop.Register(() => Abort(request));

					HttpWebResponse response;
					using (new AbortScope(request, RequestTimeout, CancellationToken.None))
					{
						response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
					}

					using (response)
					using (var stream = response.GetResponseStream())
					{
						if (response.StatusCode != HttpStatusCode.OK)
							return;

						int read;
						while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
						{
							counter.Add(read);
							received += read;

							if (stop.IsCancellationRequested)
							{
								// Abort before disposing so that the remaining content is not read.
								Abort(request);
								return;
							}
						}
					}
				}
				finally
				{
					CloseConnection(request);
				}

				if (received == 0L)
					return;
			}
		}
		catch (Exception ex)
		{
			// An aborted request comes here.
			Debug.WriteLine($"Download ended. {ex.Message}");
		}
	}

	/// <summary>
	/// Measures upload speed (Mbps) by sending random bytes to a URL through parallel connections.
	/// </summary>
	/// <param name="uri">URL which accepts a POST request</param>
	/// <param name="localAddress">Local address to which the connections are bound (null not to bind)</param>
	/// <param name="counter">Counter of bytes to which the uploaded bytes are added</param>
	/// <param name="capTime">Maximum time (ms) from the first byte including warm-up</param>
	/// <param name="capBytes">Maximum bytes</param>
	/// <param name="token">Cancellation token</param>
	/// <returns>Rate (Mbps) if measured over the minimum window, null otherwise</returns>
	/// <remarks>
	/// The rate is that of the bytes which left the send buffers of the sockets, which are small
	/// and fixed, after warm-up. The counter of the network interface is not used because the
	/// bytes of this measurement are already those actually sent.
	/// </remarks>
	internal static async Task<double?> MeasureUploadAsync(
		Uri uri,
		IPAddress localAddress,
		Counter counter,
		int capTime,
		long capBytes,
		CancellationToken token)
	{
		var remoteAddress = await ResolveAsync(uri.Host, token).ConfigureAwait(false);
		if (remoteAddress is null)
			return null;

		return await TransferAsync(
			(stop) => UploadAsync(uri, localAddress, remoteAddress, counter, stop),
			UploadCount,
			counter,
			UploadWarmup,
			capTime,
			capBytes,
			() => 0L,
			token,
			UploadMinWindow).ConfigureAwait(false);
	}

	private static async Task UploadAsync(Uri uri, IPAddress localAddress, IPAddress remoteAddress, Counter counter, CancellationToken stop)
	{
		try
		{
			// A chunk of chunked transfer encoding: the size, the content and the end of line
			var header = Encoding.ASCII.GetBytes(BufferSize.ToString("X") + "\r\n");
			var chunk = new byte[header.Length + BufferSize + 2];
			new Random().NextBytes(chunk);
			Buffer.BlockCopy(header, 0, chunk, 0, header.Length);
			chunk[chunk.Length - 2] = (byte)'\r';
			chunk[chunk.Length - 1] = (byte)'\n';

			var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
			var request = Encoding.ASCII.GetBytes(
				$"POST {uri.PathAndQuery} HTTP/1.1\r\n"
				+ $"Host: {host}\r\n"
				+ $"User-Agent: {UserAgent}\r\n"
				+ "Content-Type: application/octet-stream\r\n"
				+ "Transfer-Encoding: chunked\r\n"
				+ "Connection: close\r\n"
				+ "\r\n");

			// If the server ends a connection, another one is made.
			while (!stop.IsCancellationRequested)
			{
				long sent = 0L;

				var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				Stream stream = null;
				try
				{
					socket.SendBufferSize = UploadSendBuffer;

					if (localAddress is not null)
						socket.Bind(new IPEndPoint(localAddress, 0));

					// Closing the socket ends a pending write. The upload is ended without
					// waiting for a response.
					using var registration = stop.Register(() => Close(socket));

					var connectTask = socket.ConnectAsync(remoteAddress, uri.Port);
					if (!await WaitAsync(connectTask, RequestTimeout, stop).ConfigureAwait(false))
						return;

					await connectTask.ConfigureAwait(false);

					stream = new NetworkStream(socket, false);

					if (uri.Scheme == Uri.UriSchemeHttps)
					{
						var sslStream = new SslStream(stream, false);
						stream = sslStream;

						var authenticateTask = sslStream.AuthenticateAsClientAsync(uri.Host);
						if (!await WaitAsync(authenticateTask, RequestTimeout, stop).ConfigureAwait(false))
							return;

						await authenticateTask.ConfigureAwait(false);
					}

					await stream.WriteAsync(request, 0, request.Length).ConfigureAwait(false);

					while (!stop.IsCancellationRequested)
					{
						await stream.WriteAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
						counter.Add(BufferSize);
						sent += BufferSize;
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// A closed socket comes here.
					Debug.WriteLine($"Upload connection ended. {ex.Message}");
				}
				finally
				{
					Close(socket);
					try
					{
						stream?.Dispose();
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"Upload stream ended. {ex.Message}");
					}
				}

				if (sent == 0L)
					return;
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Upload ended. {ex.Message}");
		}

		static void Close(Socket socket)
		{
			try
			{
				// Bytes left in the send buffer are discarded.
				socket.Close(0);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Failed to close a socket. {ex.Message}");
			}
		}
	}

	#endregion

	#region Request

	private static HttpWebRequest CreateRequest(Uri uri, BindIPEndPoint bind, int timeout)
	{
		var request = (HttpWebRequest)WebRequest.Create(uri);
		request.Method = "GET";
		request.Timeout = timeout;
		request.ReadWriteTimeout = timeout;
		request.KeepAlive = false;
		request.ConnectionGroupName = Guid.NewGuid().ToString();
		request.AutomaticDecompression = DecompressionMethods.None;
		request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
		request.UserAgent = UserAgent;

		if (bind is not null)
			request.Proxy = null; // A proxy would be reached through any interface.

		// The service point is shared by all requests to the same host in this process. Measurements
		// are never run in parallel and so the delegate is always that of the current one.
		var servicePoint = request.ServicePoint;
		servicePoint.BindIPEndPointDelegate = bind;
		servicePoint.ConnectionLimit = Math.Max(servicePoint.ConnectionLimit, ConnectionLimit);
		servicePoint.Expect100Continue = false;

		return request;
	}

	private static void Abort(HttpWebRequest request)
	{
		try
		{
			request?.Abort();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Failed to abort a request. {ex.Message}");
		}
	}

	private static void CloseConnection(HttpWebRequest request)
	{
		try
		{
			if (request is not null)
				request.ServicePoint.CloseConnectionGroup(request.ConnectionGroupName);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Failed to close a connection. {ex.Message}");
		}
	}

	/// <summary>
	/// Scope in which a request is aborted on timeout or cancellation
	/// </summary>
	private sealed class AbortScope : IDisposable
	{
		private readonly CancellationTokenSource _timeout;
		private readonly CancellationTokenRegistration _timeoutRegistration;
		private readonly CancellationTokenRegistration _tokenRegistration;

		public AbortScope(HttpWebRequest request, int timeout, CancellationToken token)
		{
			_timeout = new CancellationTokenSource(timeout);
			_timeoutRegistration = _timeout.Token.Register(() => Abort(request));
			_tokenRegistration = token.Register(() => Abort(request));
		}

		public void Dispose()
		{
			_tokenRegistration.Dispose();
			_timeoutRegistration.Dispose();
			_timeout.Dispose();
		}
	}

	#endregion

	#region Route

	private static Dictionary<string, (string name, long received)> GetReceivedBytes()
	{
		var buffer = new Dictionary<string, (string name, long received)>();
		try
		{
			foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
					continue;

				buffer[networkInterface.Id] = (networkInterface.Name, LinkInspector.GetBytes(networkInterface).received);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Failed to get received bytes. {ex.Message}");
		}
		return buffer;
	}

	private static void TraceCarrier(
		NetworkInterface networkInterface,
		Dictionary<string, (string name, long received)> oldReceived,
		Dictionary<string, (string name, long received)> newReceived,
		long downBytes)
	{
		try
		{
			var carrier = newReceived
				.Where(x => oldReceived.ContainsKey(x.Key))
				.Select(x => (x.Value.name, bytes: x.Value.received - oldReceived[x.Key].received))
				.OrderByDescending(x => x.bytes)
				.FirstOrDefault();

			Trace.WriteLine($"Download of {downBytes} bytes was not received through {networkInterface.Name}."
				+ ((carrier.name is not null) ? $" {carrier.name} received {carrier.bytes} bytes." : string.Empty));
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Failed to trace the carrier. {ex.Message}");
		}
	}

	#endregion

	#region Helper

	/// <summary>
	/// Waits for a task to complete within a timeout.
	/// </summary>
	/// <returns>True if the task completed (successfully or not)</returns>
	/// <remarks>
	/// If not completed, the task is left but its exception is observed because unobserved task
	/// exceptions are escalated in this application.
	/// </remarks>
	private static async Task<bool> WaitAsync(Task task, int timeout, CancellationToken token)
	{
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

		var completedTask = await Task.WhenAny(task, Task.Delay(timeout, cancellation.Token)).ConfigureAwait(false);
		if (completedTask == task)
		{
			cancellation.Cancel();
			return true;
		}

		Forget(task);
		token.ThrowIfCancellationRequested();
		return false;
	}

	private static void Forget(Task task)
	{
		// The exception must be read explicitly to be observed.
		task.ContinueWith(
			x => { _ = x.Exception; },
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	internal static double? GetMedian(IReadOnlyCollection<double> values)
	{
		if (values is not { Count: > 0 })
			return null;

		var sorted = values.OrderBy(x => x).ToArray();
		int middle = sorted.Length / 2;

		return (sorted.Length % 2 == 1)
			? sorted[middle]
			: (sorted[middle - 1] + sorted[middle]) / 2D;
	}

	/// <summary>
	/// Gets the mean absolute deviation from a specified center.
	/// </summary>
	internal static double? GetMeanDeviation(IReadOnlyCollection<double> values, double center)
	{
		if (values is not { Count: > 0 })
			return null;

		return values.Average(x => Math.Abs(x - center));
	}

	private static double? Round(double? value, int digits) =>
		value.HasValue ? Math.Round(value.Value, digits) : null;

	#endregion
}
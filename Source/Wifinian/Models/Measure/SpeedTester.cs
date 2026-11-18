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
/// Size of a speed measurement, which is the time it lasts (no size caps the bytes)
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

	internal const int DownloadCount = 4;
	internal const int DownloadWarmup = 1000; // ms
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
	/// Ceiling of bytes of a transfer in one direction
	/// </summary>
	/// <remarks>
	/// This is not a cap which decides the result: every measurement lasts for a fixed time after
	/// warm-up. It only ends a transfer which, by a defect, would otherwise go on. A link would
	/// have to be faster than 1.7 Gbps to reach it.
	/// </remarks>
	internal const long SafetyBytes = 2_000_000_000L;

	/// <summary>
	/// Time (ms) to wait before the download is tried once more when it got no byte although
	/// internet access and the test server were confirmed just before
	/// </summary>
	internal const int RetryDelay = 3000;

	/// <summary>
	/// Time (ms) before the end of the content of a download at which the next request is made
	/// on a new connection, so that the connection is ready when the content ends
	/// </summary>
	internal const int DownloadPrefetchLead = 1500;

	/// <summary>
	/// Time (ms) a connection of a download waits after a failed request before it requests again
	/// </summary>
	internal const int DownloadRetryDelay = 300;

	/// <summary>
	/// Number of failed requests in a row after which a connection of a download gives up
	/// </summary>
	internal const int DownloadMaxFailures = 3;

	/// <summary>
	/// Maximum length of the detail of a measurement
	/// </summary>
	internal const int MaxDetailLength = 300;

	/// <summary>
	/// Gets the time (ms) after warm-up over which speed is measured in each direction.
	/// </summary>
	/// <remarks>
	/// Measurements were once ended by caps of bytes (20 MB), which left a window of about
	/// a second on a link of 100 Mbps and made the results noisy. The window is now the same
	/// on every link and so the data used is proportional to the speed of the link.
	/// </remarks>
	internal static int GetMeasureWindow(SpeedTestSize size) => size switch
	{
		SpeedTestSize.Full => 8000,
		_ => 4000
	};

	/// <summary>
	/// Gets the limits of upload. The time includes warm-up and is long enough for the window
	/// after it regardless of the interval of sampling.
	/// </summary>
	internal static (int capTime, long capBytes) GetUploadCap(SpeedTestSize size) =>
		(UploadWarmup + GetMeasureWindow(size) + 2 * SampleInterval, SafetyBytes);

	/// <summary>
	/// Gets the limits of download. The time includes warm-up.
	/// </summary>
	internal static (int capTime, long capBytes) GetDownloadCap(SpeedTestSize size) =>
		(DownloadWarmup + GetMeasureWindow(size), SafetyBytes);

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
				sample.Detail = (networkInterface is null)
					? "The adapter was not found"
					: $"The adapter is {networkInterface.OperationalStatus} and has {((localAddress is null) ? "no IPv4 address" : "an address")}";
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
						sample.Detail = $"The connectivity check failed through {networkInterface.Name} ({localAddress}) but succeeded without binding";
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
				sample.Detail = $"Could not resolve {downloadUri.Host}";
				return sample;
			}

			var connectTimes = await ConnectAsync(localAddress, new IPEndPoint(remoteAddress, downloadUri.Port), token).ConfigureAwait(false);
			var latency = GetMedian(connectTimes);
			if (latency is null)
			{
				sample.Result = SpeedResult.EndpointFailed;
				sample.Detail = $"Could not connect to {remoteAddress}:{downloadUri.Port} from {localAddress}";
				return sample;
			}

			sample.LatencyMs = Round(latency, 1);
			sample.JitterMs = Round(GetMeanDeviation(connectTimes, latency.Value), 1);

			// (5) Download
			var oldBytes = LinkInspector.GetBytes(networkInterface);
			var oldReceived = GetReceivedBytes();

			var (downloadCapTime, downloadCapBytes) = GetDownloadCap(size);

			var (downMbps, downBytes, downDetail) = await MeasureDownloadAsync(
				downloadUri,
				Bind,
				downloadCapTime,
				downloadCapBytes,
				() => LinkInspector.GetBytes(networkInterface).received,
				RetryDelay,
				token,
				x => sample.DownBytes = x).ConfigureAwait(false);

			sample.DownBytes = downBytes;
			sample.Detail = downDetail;

			if ((downBytes == 0L) || (downMbps is null))
			{
				sample.Result = SpeedResult.EndpointFailed;
				return sample;
			}

			sample.DownMbps = Round(downMbps, 2);

			var newBytes = LinkInspector.GetBytes(networkInterface);
			if (newBytes.received - oldBytes.received < RouteRatio * sample.DownBytes)
			{
				TraceCarrier(networkInterface, oldReceived, GetReceivedBytes(), sample.DownBytes);
				sample.DownMbps = null;
				sample.Result = SpeedResult.RouteMismatch;
				sample.Detail = Join(sample.Detail, $"The download of {sample.DownBytes} bytes was not received through {networkInterface.Name}, which received {newBytes.received - oldBytes.received} bytes");
				return sample;
			}

			// (6) Upload
			var (uploadCapTime, uploadCapBytes) = GetUploadCap(size);
			var uploadCounter = new Counter();
			var uploadNote = new FailureNote();
			var oldSent = LinkInspector.GetBytes(networkInterface).sent;
			try
			{
				var upMbps = await MeasureUploadAsync(
					uploadUri,
					localAddress,
					uploadCounter,
					uploadCapTime,
					uploadCapBytes,
					token,
					uploadNote).ConfigureAwait(false);

				sample.UpMbps = (uploadCounter.Value > 0L) ? Round(upMbps, 2) : null;

				if (sample.UpMbps is null)
					sample.Detail = Join(sample.Detail, $"Upload not measured: {uploadNote.Value ?? ((uploadCounter.Value > 0L) ? "it ended too early" : "no byte was sent")}");
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// Failure of upload alone leaves the upload speed blank.
				Trace.WriteLine("Failed to measure upload speed." + Environment.NewLine
					+ ex);

				sample.Detail = Join(sample.Detail, $"Upload failed: {Describe(ex)}");
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
					sample.Detail = Join(sample.Detail, $"The upload of {sample.UpBytes} bytes was not sent through {networkInterface.Name}, which sent {sent} bytes");
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
			sample.Detail = Join(sample.Detail, Describe(ex));
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
	/// Runs parallel transfers for a fixed time from the first byte and computes the rate (Mbps).
	/// </summary>
	/// <param name="work">Function to run a transfer until stopped (it must not throw)</param>
	/// <param name="count">Number of parallel transfers</param>
	/// <param name="counter">Counter of bytes to which the transfers add</param>
	/// <param name="warmupTime">Time (ms) from the first byte to be discarded</param>
	/// <param name="capTime">Time (ms) from the first byte after which the transfers are stopped</param>
	/// <param name="capBytes">Ceiling of bytes (a safety limit which no real link is expected to reach)</param>
	/// <param name="getAdapterBytes">Function to get the counter of the network interface</param>
	/// <param name="token">Cancellation token</param>
	/// <param name="minWindow">Minimum time (ms) over which the rate must be measured unless the cap of bytes is reached</param>
	/// <param name="clock">Clock to be started at the first byte so that the transfers know the time left</param>
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
		int minWindow = 0,
		TransferClock clock = null)
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
			clock?.Start(capTime);
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

	/// <summary>
	/// First reason why a transfer failed, shared by parallel transfers
	/// </summary>
	internal sealed class FailureNote
	{
		private string _value;

		public string Value => Volatile.Read(ref _value);

		/// <summary>
		/// Sets the reason unless one has been set already.
		/// </summary>
		public void Set(string value)
		{
			if (!string.IsNullOrWhiteSpace(value))
				Interlocked.CompareExchange(ref _value, value, null);
		}
	}

	/// <summary>
	/// Time left of a transfer, counted from its first byte
	/// </summary>
	internal sealed class TransferClock
	{
		private long _end; // Timestamp of Stopwatch (0 until started)

		public void Start(int duration) =>
			Interlocked.Exchange(ref _end, Math.Max(1L, Stopwatch.GetTimestamp() + duration * Stopwatch.Frequency / 1000L));

		/// <summary>
		/// Time (ms) left, long.MaxValue until started
		/// </summary>
		public long Remaining
		{
			get
			{
				var end = Interlocked.Read(ref _end);
				return (end == 0L)
					? long.MaxValue
					: Math.Max(0L, (end - Stopwatch.GetTimestamp()) * 1000L / Stopwatch.Frequency);
			}
		}
	}

	/// <summary>
	/// What happened to the parallel connections of a download
	/// </summary>
	internal sealed class DownloadWatch
	{
		private int _retries;
		private int _lost;

		/// <summary>
		/// First reason why a request failed
		/// </summary>
		public FailureNote Note { get; } = new();

		public TransferClock Clock { get; } = new();

		/// <summary>
		/// Number of requests made again after a failed one
		/// </summary>
		public int Retries => Volatile.Read(ref _retries);

		/// <summary>
		/// Number of connections which gave up
		/// </summary>
		public int Lost => Volatile.Read(ref _lost);

		public void AddRetry() => Interlocked.Increment(ref _retries);
		public void AddLost() => Interlocked.Increment(ref _lost);
	}

	/// <summary>
	/// Determines whether the next request of a download is to be made now, ahead of the end of
	/// the content being received.
	/// </summary>
	/// <param name="length">Length of the content being received (0 or less if unknown)</param>
	/// <param name="received">Bytes of the content received so far</param>
	/// <param name="elapsed">Time (ms) since the content began</param>
	/// <param name="remaining">Time (ms) left of the download</param>
	/// <remarks>
	/// A connection takes a few round trips to be made (TCP, TLS) and starts slowly. If it were
	/// made only when the content ended, that time would fall in the measured window and the
	/// result would be lower than the link, the more so the faster the link. No request is made
	/// if the content lasts until the download ends because a request not used still counts as
	/// one for the server.
	/// </remarks>
	internal static bool ShouldPrefetch(long length, long received, long elapsed, long remaining)
	{
		if ((length <= 0L) || (received <= 0L) || (elapsed <= 0L) || (received >= length))
			return false;

		var timeToEnd = (double)(length - received) * elapsed / received;

		return (timeToEnd <= DownloadPrefetchLead) && (timeToEnd < remaining);
	}

	/// <summary>
	/// Describes an exception of a request in one line: the HTTP status if the server answered,
	/// the status of the request and the innermost cause otherwise.
	/// </summary>
	internal static string Describe(Exception ex)
	{
		if (ex is null)
			return null;

		while ((ex is AggregateException) && (ex.InnerException is not null))
			ex = ex.InnerException;

		string text;
		if (ex is WebException web)
		{
			if (web.Response is HttpWebResponse response)
			{
				text = DescribeStatus(response);
			}
			else
			{
				Exception inner = web.InnerException;
				while (inner?.InnerException is not null)
					inner = inner.InnerException;

				text = $"{web.Status}: {web.Message}"
					+ ((inner is not null) ? $" ({Name(inner)}: {inner.Message})" : string.Empty);
			}
		}
		else
		{
			text = $"{Name(ex)}: {ex.Message}";
		}
		return Trim(text);

		static string Name(Exception x) => (x is SocketException socket)
			? $"SocketException {socket.SocketErrorCode}"
			: x.GetType().Name;
	}

	/// <summary>
	/// Describes the status of a response which is not a success, with the time the server
	/// asks to wait if it gives one (a server which limits requests does, by 429 or 503).
	/// </summary>
	private static string DescribeStatus(HttpWebResponse response)
	{
		var text = $"HTTP {(int)response.StatusCode} {response.StatusDescription}";
		try
		{
			var retryAfter = response.Headers?["Retry-After"];
			if (!string.IsNullOrWhiteSpace(retryAfter))
				text += $" (Retry-After {retryAfter.Trim()})";
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Failed to read a header. {ex.Message}");
		}
		return text;
	}

	private static string Trim(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return null;

		text = string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
		return (text.Length <= MaxDetailLength) ? text : text.Substring(0, MaxDetailLength);
	}

	private static string Join(string first, string second)
	{
		if (string.IsNullOrWhiteSpace(first))
			return Trim(second);

		if (string.IsNullOrWhiteSpace(second))
			return Trim(first);

		return Trim($"{first}; {second}");
	}

	/// <summary>
	/// Measures download speed (Mbps) by receiving from a URL through parallel connections and,
	/// if not a single byte comes, tries once more after a delay.
	/// </summary>
	/// <param name="uri">URL to download from</param>
	/// <param name="bind">Function to bind the connections to a local address (null not to bind)</param>
	/// <param name="capTime">Time (ms) from the first byte including warm-up</param>
	/// <param name="capBytes">Ceiling of bytes</param>
	/// <param name="getAdapterBytes">Function to get the counter of the network interface</param>
	/// <param name="retryDelay">Time (ms) to wait before the second attempt</param>
	/// <param name="token">Cancellation token</param>
	/// <param name="reportBytes">Action to be given the bytes received by an attempt when it ends, even by cancellation</param>
	/// <returns>Rate (Mbps), received bytes and what went wrong if anything did</returns>
	/// <remarks>
	/// A download which got nothing right after the connectivity check and the connections to
	/// the same server succeeded was seen twice in a row just after the adapter moved to another
	/// network (in 0.1 to 0.2 seconds, far sooner than any timeout). Every request opens its own
	/// connection (no keep-alive, a connection group of its own which is closed afterwards) and
	/// so a connection left from the previous network is not what failed. What failed is given
	/// in the detail from now on: the HTTP status if the server refused (e.g. 403, 429) or
	/// the error of the connection otherwise.
	/// </remarks>
	internal static async Task<(double? mbps, long bytes, string detail)> MeasureDownloadAsync(
		Uri uri,
		BindIPEndPoint bind,
		int capTime,
		long capBytes,
		Func<long> getAdapterBytes,
		int retryDelay,
		CancellationToken token,
		Action<long> reportBytes = null)
	{
		string firstFailure = null;

		for (int attempt = 0; ; attempt++)
		{
			var counter = new Counter();
			var watch = new DownloadWatch();
			double? mbps;
			try
			{
				mbps = await TransferAsync(
					(stop) => DownloadAsync(uri, bind, counter, stop, watch),
					DownloadCount,
					counter,
					DownloadWarmup,
					capTime,
					capBytes,
					getAdapterBytes,
					token,
					clock: watch.Clock).ConfigureAwait(false);
			}
			finally
			{
				// The bytes are reported even if aborted because they are data used.
				reportBytes?.Invoke(counter.Value);
			}

			if (counter.Value > 0L)
			{
				var detail = (firstFailure is not null)
					? $"Download worked at the second attempt; the first got no byte: {firstFailure}"
					: null;

				return GetDownloadOutcome(mbps, counter.Value, watch.Lost, watch.Retries, watch.Note.Value, detail);
			}

			var failure = watch.Note.Value ?? $"no response within {RequestTimeout / 1000} s";

			if (attempt >= 1)
			{
				return (null, 0L, string.Equals(firstFailure, failure, StringComparison.Ordinal)
					? Trim($"Download got no byte in two attempts: {failure}")
					: Trim($"Download got no byte in two attempts: {firstFailure}; then {failure}"));
			}

			firstFailure = failure;

			Trace.WriteLine($"Download of speed measurement got no byte ({failure}). It is tried once more in {retryDelay} ms.");

			await Task.Delay(retryDelay, token).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Gets the outcome of a download which received bytes.
	/// </summary>
	/// <param name="mbps">Rate (Mbps) measured</param>
	/// <param name="bytes">Bytes received</param>
	/// <param name="lost">Number of connections which gave up</param>
	/// <param name="retries">Number of requests made again after a failed one</param>
	/// <param name="failure">First reason why a request failed</param>
	/// <param name="detail">Detail given so far</param>
	/// <remarks>
	/// A rate over a time in which some of the connections were gone for good is lower than
	/// the link and so is not given. A request which failed and was made again is told in
	/// the detail.
	/// </remarks>
	internal static (double? mbps, long bytes, string detail) GetDownloadOutcome(double? mbps, long bytes, int lost, int retries, string failure, string detail)
	{
		if (lost > 0)
		{
			return (null, bytes, Join(detail,
				$"Download not measured: {lost} of {DownloadCount} connections failed and did not come back ({failure ?? "no reason known"})"));
		}

		if (retries > 0)
		{
			return (mbps, bytes, Join(detail,
				$"Download went on after {retries} failed request{((retries == 1) ? string.Empty : "s")}: {failure ?? "no reason known"}"));
		}

		return (mbps, bytes, Trim(detail));
	}

	/// <summary>
	/// Request of a download and its response
	/// </summary>
	private sealed class Fetch : IDisposable
	{
		public HttpWebRequest Request { get; set; }
		public HttpWebResponse Response { get; set; }
		public CancellationTokenRegistration Registration { get; set; }

		/// <summary>
		/// Why the request failed (null if it got a response of 200)
		/// </summary>
		public string Failure { get; set; }

		public void Dispose()
		{
			try
			{
				Registration.Dispose();

				// Abort before disposing so that the remaining content is not read.
				Abort(Request);
				Response?.Dispose();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Failed to end a request. {ex.Message}");
			}
			CloseConnection(Request);
		}
	}

	/// <summary>
	/// Makes a request of a download on a new connection and waits for the response.
	/// </summary>
	/// <remarks>This method never throws an exception.</remarks>
	private static async Task<Fetch> OpenAsync(Uri uri, BindIPEndPoint bind, CancellationToken stop)
	{
		var fetch = new Fetch();
		try
		{
			var request = CreateRequest(uri, bind, RequestTimeout);
			fetch.Request = request;
			fetch.Registration = stop.Register(() => Abort(request));

			using (new AbortScope(request, RequestTimeout, CancellationToken.None))
			{
				fetch.Response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
			}

			if (fetch.Response.StatusCode != HttpStatusCode.OK)
				fetch.Failure = DescribeStatus(fetch.Response);
		}
		catch (Exception ex)
		{
			// An aborted request comes here.
			Debug.WriteLine($"Request of download ended. {ex.Message}");

			fetch.Failure = Describe(ex) ?? "The request failed";

			if (ex is WebException { Response: not null } web)
				web.Response.Dispose();
		}
		return fetch;
	}

	/// <summary>
	/// Receives the content of a URL on one connection after another until stopped.
	/// </summary>
	/// <remarks>
	/// <para>If the content ends before the time is up, it is requested again. The next request
	/// is made shortly before the end, on a connection of its own, so that its content follows
	/// without a gap.</para>
	/// <para>A failed request is made again after a short wait as long as bytes are coming
	/// through any of the connections. After failures in a row the connection gives up, which
	/// is counted. If no byte has come at all after the wait, it gives up at once: the download
	/// as a whole is tried once more by the caller.</para>
	/// <para>This method never throws an exception.</para>
	/// </remarks>
	private static async Task DownloadAsync(Uri uri, BindIPEndPoint bind, Counter counter, CancellationToken stop, DownloadWatch watch = null)
	{
		Task<Fetch> next = null;
		try
		{
			var buffer = new byte[BufferSize];
			int failures = 0;

			while (!stop.IsCancellationRequested)
			{
				string failure;

				var fetchTask = next ?? OpenAsync(uri, bind, stop);
				next = null;

				using (var fetch = await fetchTask.ConfigureAwait(false))
				{
					failure = fetch.Failure;

					if (failure is null)
					{
						try
						{
							long length = fetch.Response.ContentLength;
							long received = 0L;
							var stopwatch = Stopwatch.StartNew();

							using var stream = fetch.Response.GetResponseStream();

							int read;
							while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
							{
								counter.Add(read);
								received += read;

								if (stop.IsCancellationRequested)
								{
									// Abort before disposing so that the remaining content is not read.
									Abort(fetch.Request);
									return;
								}

								if ((next is null)
									&& ShouldPrefetch(length, received, stopwatch.ElapsedMilliseconds, watch?.Clock.Remaining ?? long.MaxValue))
								{
									next = OpenAsync(uri, bind, stop);
								}
							}

							if (received == 0L)
								failure = "HTTP 200 with no content";
						}
						catch (Exception ex)
						{
							// An aborted request comes here. It is not a failure if aborted to stop the transfer.
							Debug.WriteLine($"Download ended. {ex.Message}");

							if (stop.IsCancellationRequested)
								return;

							failure = Describe(ex) ?? "The connection failed";
						}
					}
				}

				if (failure is null)
				{
					failures = 0;
					continue;
				}

				if (stop.IsCancellationRequested)
					return;

				watch?.Note.Set(failure);
				failures++;

				// A request made ahead, if any, is ready and so is not waited for.
				if (next is null)
				{
					try
					{
						await Task.Delay(DownloadRetryDelay, stop).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						return;
					}
				}

				if ((counter.Value == 0L) || (failures >= DownloadMaxFailures))
				{
					watch?.AddLost();
					return;
				}

				watch?.AddRetry();
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Download ended. {ex.Message}");

			if (!stop.IsCancellationRequested)
			{
				watch?.Note.Set(Describe(ex));
				watch?.AddLost();
			}
		}
		finally
		{
			// A request made ahead and not used is ended.
			if (next is not null)
			{
				_ = next.ContinueWith(
					x => x.Result.Dispose(),
					CancellationToken.None,
					TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
			}
		}
	}

	/// <summary>
	/// Measures upload speed (Mbps) by sending random bytes to a URL through parallel connections.
	/// </summary>
	/// <param name="uri">URL which accepts a POST request</param>
	/// <param name="localAddress">Local address to which the connections are bound (null not to bind)</param>
	/// <param name="counter">Counter of bytes to which the uploaded bytes are added</param>
	/// <param name="capTime">Time (ms) from the first byte including warm-up</param>
	/// <param name="capBytes">Ceiling of bytes</param>
	/// <param name="token">Cancellation token</param>
	/// <param name="note">Note to which the first reason of failure is set</param>
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
		CancellationToken token,
		FailureNote note = null)
	{
		var remoteAddress = await ResolveAsync(uri.Host, token).ConfigureAwait(false);
		if (remoteAddress is null)
		{
			note?.Set($"Could not resolve {uri.Host}");
			return null;
		}

		return await TransferAsync(
			(stop) => UploadAsync(uri, localAddress, remoteAddress, counter, stop, note),
			UploadCount,
			counter,
			UploadWarmup,
			capTime,
			capBytes,
			() => 0L,
			token,
			UploadMinWindow).ConfigureAwait(false);
	}

	private static async Task UploadAsync(Uri uri, IPAddress localAddress, IPAddress remoteAddress, Counter counter, CancellationToken stop, FailureNote note = null)
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

					if (!stop.IsCancellationRequested)
						note?.Set(Describe(ex));
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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models.History;
using Wifinian.Models.Measure;
using Wifinian.ViewModels;

namespace Wifinian.Test;

/// <summary>
/// Tests of the download stage of speed measurement against a local server: that it lasts for
/// the time given, that no connection is reused, that it is tried once more when no byte came
/// and that what went wrong is told.
/// </summary>
[TestClass]
public class DownloadTest
{
	#region Server

	/// <summary>
	/// HTTP server which answers each connection with a status decided by the order of
	/// the connection and, if the status is 200, sends content at a fixed rate
	/// </summary>
	private sealed class HttpServer : IDisposable
	{
		private readonly TcpListener _listener;
		private readonly CancellationTokenSource _cancellation = new();
		private readonly Func<int, int> _getStatus;
		private readonly int _bytesPerSecond;
		private readonly List<TcpClient> _clients = new();
		private readonly object _lock = new();
		private int _connections;
		private int _requests;
		private int _closeRequests;
		private int _maxRequestsPerConnection;

		public int Port { get; }
		public int Connections => Volatile.Read(ref _connections);
		public int Requests => Volatile.Read(ref _requests);

		/// <summary>
		/// Number of requests which asked the connection to be closed after the response
		/// </summary>
		public int CloseRequests => Volatile.Read(ref _closeRequests);

		public int MaxRequestsPerConnection => Volatile.Read(ref _maxRequestsPerConnection);

		public HttpServer(Func<int, int> getStatus, int bytesPerSecond = 500_000)
		{
			_getStatus = getStatus;
			_bytesPerSecond = bytesPerSecond;

			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

			_ = Task.Run(AcceptAsync);
		}

		public Uri Uri => new($"http://127.0.0.1:{Port}/__down?bytes=25000000");

		private async Task AcceptAsync()
		{
			try
			{
				while (!_cancellation.IsCancellationRequested)
				{
					var client = await _listener.AcceptTcpClientAsync();
					int index = Interlocked.Increment(ref _connections) - 1;

					lock (_lock)
					{
						_clients.Add(client);
					}

					_ = Task.Run(() => ServeAsync(client, index));
				}
			}
			catch (Exception)
			{
				// The listener has been stopped.
			}
		}

		private async Task ServeAsync(TcpClient client, int index)
		{
			try
			{
				using (client)
				{
					var stream = client.GetStream();
					int requests = 0;

					// The connection is kept open after a response so that a request which reused
					// it would be counted.
					while (!_cancellation.IsCancellationRequested)
					{
						var header = await ReadHeaderAsync(stream);
						if (header is null)
							return;

						Interlocked.Increment(ref _requests);
						requests++;
						InterlockedMax(ref _maxRequestsPerConnection, requests);

						if (header.IndexOf("Connection: close", StringComparison.OrdinalIgnoreCase) >= 0)
							Interlocked.Increment(ref _closeRequests);

						int status = _getStatus(index);
						if (status != 200)
						{
							var response = Encoding.ASCII.GetBytes(
								$"HTTP/1.1 {status} {GetReason(status)}\r\nContent-Length: 0\r\n\r\n");
							await stream.WriteAsync(response, 0, response.Length);
							continue;
						}

						var head = Encoding.ASCII.GetBytes(
							"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 2000000000\r\n\r\n");
						await stream.WriteAsync(head, 0, head.Length);

						var buffer = new byte[4 * 1024];
						var stopwatch = Stopwatch.StartNew();
						long sent = 0L;

						while (!_cancellation.IsCancellationRequested)
						{
							// Send only as much as the rate allows up to now.
							if (sent >= stopwatch.Elapsed.TotalSeconds * _bytesPerSecond)
							{
								await Task.Delay(5);
								continue;
							}

							await stream.WriteAsync(buffer, 0, buffer.Length);
							sent += buffer.Length;
						}
					}
				}
			}
			catch (Exception)
			{
				// The connection has been closed by the client.
			}
		}

		private static async Task<string> ReadHeaderAsync(NetworkStream stream)
		{
			var builder = new StringBuilder();
			var buffer = new byte[1024];

			while (builder.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
			{
				int read = await stream.ReadAsync(buffer, 0, buffer.Length);
				if (read <= 0)
					return null;

				builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
			}
			return builder.ToString();
		}

		private static string GetReason(int status) => status switch
		{
			403 => "Forbidden",
			429 => "Too Many Requests",
			503 => "Service Unavailable",
			_ => "Error"
		};

		private static void InterlockedMax(ref int location, int value)
		{
			int current;
			while ((current = Volatile.Read(ref location)) < value)
				Interlocked.CompareExchange(ref location, value, current);
		}

		/// <summary>
		/// Closes every connection from under the client, as a change of network does.
		/// </summary>
		public void DropConnections()
		{
			lock (_lock)
			{
				foreach (var client in _clients)
				{
					try
					{
						client.Client?.Close(0);
					}
					catch (Exception)
					{
					}
				}
				_clients.Clear();
			}
		}

		public void Dispose()
		{
			_cancellation.Cancel();
			_listener.Stop();
			DropConnections();
		}
	}

	private static IPEndPoint BindLoopback(ServicePoint servicePoint, IPEndPoint remoteEndPoint, int retryCount) =>
		(retryCount == 0) ? new IPEndPoint(IPAddress.Loopback, 0) : null;

	private const int CapTime = SpeedTester.DownloadWarmup + 1500;

	private static Task<(double? mbps, long bytes, string detail)> MeasureAsync(Uri uri, int retryDelay = 300, int capTime = CapTime) =>
		SpeedTester.MeasureDownloadAsync(uri, BindLoopback, capTime, SpeedTester.SafetyBytes, () => 0L, retryDelay, CancellationToken.None);

	#endregion

	[TestMethod]
	public async Task TestDownloadLastsForTheTimeGiven()
	{
		// Each connection is sent 500 kB/s and so the true rate is 4 Mbps per connection.
		const int bytesPerSecond = 500_000;
		var expected = SpeedTester.DownloadCount * bytesPerSecond * 8D / 1e6;

		using var server = new HttpServer(_ => 200, bytesPerSecond);

		var stopwatch = Stopwatch.StartNew();
		var (mbps, bytes, detail) = await MeasureAsync(server.Uri);
		stopwatch.Stop();

		Assert.IsNotNull(mbps);
		Assert.IsTrue(Math.Abs(mbps.Value - expected) <= expected * 0.3, $"Measured {mbps.Value:f2} Mbps, expected {expected:f2} Mbps");
		Assert.IsNull(detail);

		// The download went on for the whole time: the bytes are those of the rate over that
		// time, far more than any small cap would have allowed to decide the result early.
		Assert.IsTrue(stopwatch.ElapsedMilliseconds >= CapTime, $"Lasted {stopwatch.ElapsedMilliseconds} ms");
		Assert.IsTrue(bytes >= SpeedTester.DownloadCount * bytesPerSecond * (CapTime / 1000D) * 0.6, $"{bytes} bytes");

		// One connection per parallel transfer, each asked to be closed after the response.
		Assert.AreEqual(SpeedTester.DownloadCount, server.Connections);
		Assert.AreEqual(server.Requests, server.CloseRequests);
		Assert.AreEqual(1, server.MaxRequestsPerConnection);
	}

	[TestMethod]
	public async Task TestDownloadNeverReusesConnection()
	{
		using var server = new HttpServer(_ => 200);

		var first = await MeasureAsync(server.Uri, capTime: SpeedTester.DownloadWarmup + 300);
		Assert.IsTrue(first.bytes > 0L);
		Assert.AreEqual(SpeedTester.DownloadCount, server.Connections);

		// Every connection of the first measurement is closed from under the client, as happens
		// when the adapter moves to another network. A connection kept in a pool would make
		// the next request fail at once.
		server.DropConnections();

		var second = await MeasureAsync(server.Uri, capTime: SpeedTester.DownloadWarmup + 300);
		Assert.IsNotNull(second.mbps);
		Assert.IsTrue(second.bytes > 0L);
		Assert.IsNull(second.detail);

		// The second measurement opened connections of its own and no connection ever carried
		// more than one request.
		Assert.AreEqual(2 * SpeedTester.DownloadCount, server.Connections);
		Assert.AreEqual(2 * SpeedTester.DownloadCount, server.Requests);
		Assert.AreEqual(server.Requests, server.CloseRequests);
		Assert.AreEqual(1, server.MaxRequestsPerConnection);
	}

	[TestMethod]
	public async Task TestDownloadIsTriedOnceMore()
	{
		// The server refuses the connections of the first attempt and serves those of the second.
		using var server = new HttpServer(index => (index < SpeedTester.DownloadCount) ? 429 : 200);

		var stopwatch = Stopwatch.StartNew();
		var (mbps, bytes, detail) = await MeasureAsync(server.Uri, retryDelay: 400);
		stopwatch.Stop();

		Assert.IsNotNull(mbps);
		Assert.IsTrue(bytes > 0L);
		Assert.AreEqual(2 * SpeedTester.DownloadCount, server.Connections);
		Assert.IsTrue(stopwatch.ElapsedMilliseconds >= 400 + CapTime, $"Lasted {stopwatch.ElapsedMilliseconds} ms");

		// A measurement which needed the second attempt says so and why.
		Assert.IsNotNull(detail);
		StringAssert.Contains(detail, "second attempt");
		StringAssert.Contains(detail, "HTTP 429 Too Many Requests");
	}

	[TestMethod]
	public async Task TestDownloadFailureTellsHttpStatus()
	{
		using var server = new HttpServer(_ => 403);

		var stopwatch = Stopwatch.StartNew();
		var (mbps, bytes, detail) = await MeasureAsync(server.Uri, retryDelay: 400);
		stopwatch.Stop();

		Assert.IsNull(mbps);
		Assert.AreEqual(0L, bytes);
		Assert.AreEqual("Download got no byte in two attempts: HTTP 403 Forbidden", detail);

		// It was tried twice, the delay apart, and not more.
		Assert.AreEqual(2 * SpeedTester.DownloadCount, server.Connections);
		Assert.IsTrue(stopwatch.ElapsedMilliseconds >= 400, $"Lasted {stopwatch.ElapsedMilliseconds} ms");
		Assert.IsTrue(stopwatch.ElapsedMilliseconds < 8000, $"Lasted {stopwatch.ElapsedMilliseconds} ms");
	}

	[TestMethod]
	public async Task TestDownloadFailureTellsConnectionError()
	{
		// Nothing listens on this port.
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();

		var (mbps, bytes, detail) = await MeasureAsync(new Uri($"http://127.0.0.1:{port}/"), retryDelay: 100);

		Assert.IsNull(mbps);
		Assert.AreEqual(0L, bytes);
		Assert.IsNotNull(detail);
		StringAssert.Contains(detail, "ConnectFailure");
		StringAssert.Contains(detail, "ConnectionRefused");
		Assert.IsTrue(detail.Length <= SpeedTester.MaxDetailLength);
	}

	[TestMethod]
	public async Task TestDownloadCanceledDuringRetryDelay()
	{
		using var server = new HttpServer(_ => 503);
		using var cancellation = new CancellationTokenSource(500);

		long reported = -1L;

		await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
			SpeedTester.MeasureDownloadAsync(server.Uri, BindLoopback, CapTime, SpeedTester.SafetyBytes, () => 0L, 30_000, cancellation.Token, x => reported = x));

		// The bytes of the attempt were reported before the delay.
		Assert.AreEqual(0L, reported);
		Assert.AreEqual(SpeedTester.DownloadCount, server.Connections);
	}

	[TestMethod]
	public void TestDescribe()
	{
		Assert.IsNull(SpeedTester.Describe(null));

		Assert.AreEqual("InvalidOperationException: Something failed",
			SpeedTester.Describe(new InvalidOperationException("Something\r\nfailed")));

		var socket = new SocketException((int)SocketError.NetworkUnreachable);
		var web = new WebException("Unable to connect to the remote server", socket, WebExceptionStatus.ConnectFailure, null);
		var text = SpeedTester.Describe(new AggregateException(web));

		StringAssert.StartsWith(text, "ConnectFailure: Unable to connect to the remote server (SocketException NetworkUnreachable: ");

		Assert.IsTrue(SpeedTester.Describe(new Exception(new string('x', 1000))).Length <= SpeedTester.MaxDetailLength);
	}

	[TestMethod]
	public void TestFailureIsShownAndLogged()
	{
		var sample = new SpeedSample
		{
			Profile = "EXT2",
			Trigger = SpeedTrigger.TestAll,
			Result = SpeedResult.EndpointFailed,
			LatencyMs = 40.7,
			DurationMs = 3579,
			Detail = "Download got no byte in two attempts: HTTP 429 Too Many Requests"
		};

		// The History window shows the beginning in the row and the whole in the tooltip.
		Assert.AreEqual("Test server failed: Download got no byte in two attempts: HTTP 429 Too Many Requests",
			HistorySnapshot.FormatResult(sample.Result, sample.Detail));
		Assert.AreEqual("Test server failed", HistorySnapshot.FormatResult(sample.Result, null));
		Assert.AreEqual("Ok", HistorySnapshot.FormatResult(SpeedResult.Ok, "Download worked at the second attempt; the first got no byte: HTTP 503 Service Unavailable"));

		var longText = HistorySnapshot.FormatResult(SpeedResult.Error, new string('x', 500));
		Assert.AreEqual("Error: ".Length + HistorySnapshot.MaxDetailLength + 3, longText.Length);
		StringAssert.EndsWith(longText, "...");

		// The operation log gets a line for a failure and for a success which needed a second attempt.
		Assert.AreEqual(
			"Speed test of EXT2 (TestAll) ended as EndpointFailed after 3579 ms (latency 40.7 ms, 0 bytes down, 0 bytes up): Download got no byte in two attempts: HTTP 429 Too Many Requests",
			AppController.GetMeasureFailureText(sample));

		Assert.IsNull(AppController.GetMeasureFailureText(null));
		Assert.IsNull(AppController.GetMeasureFailureText(new SpeedSample { Profile = "EXT", Result = SpeedResult.Ok }));
		Assert.IsNull(AppController.GetMeasureFailureText(new SpeedSample { Profile = "EXT", Result = SpeedResult.NoInternet }));
		Assert.IsNull(AppController.GetMeasureFailureText(new SpeedSample { Profile = "EXT", Result = SpeedResult.Aborted }));
		Assert.IsNull(AppController.GetMeasureFailureText(new SpeedSample { Profile = "EXT", Result = SpeedResult.NotTested }));

		StringAssert.Contains(
			AppController.GetMeasureFailureText(new SpeedSample { Profile = "EXT", Result = SpeedResult.Ok, Detail = "Download worked at the second attempt; the first got no byte: HTTP 503 Service Unavailable" }),
			"ended as Ok");
		StringAssert.EndsWith(
			AppController.GetMeasureFailureText(new SpeedSample { Profile = "EXT", Result = SpeedResult.Error }),
			"(latency not measured, 0 bytes down, 0 bytes up): no detail");
	}
}

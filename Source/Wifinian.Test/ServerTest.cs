using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Measure;

namespace Wifinian.Test;

/// <summary>
/// Tests of how speed measurement deals with test servers which limit requests or fail, against
/// local servers: that the next server is used within the same measurement, that a server which
/// limits requests is remembered and left alone until the time it asked for is over, across
/// a restart as well, and that upload is left out while its server is blocked.
/// </summary>
[TestClass]
public class ServerTest
{
	#region Server

	/// <summary>
	/// HTTP server which answers a request for download with content at a given rate and
	/// a request for upload by reading it, or refuses either with a given status
	/// </summary>
	private sealed class TestServer : IDisposable
	{
		private readonly TcpListener _listener;
		private readonly CancellationTokenSource _cancellation = new();
		private readonly List<TcpClient> _clients = new();
		private int _connections;
		private int _downloads;
		private int _uploads;

		/// <summary>
		/// Status of the answer to a request for download
		/// </summary>
		public int DownloadStatus { get; set; } = 200;

		/// <summary>
		/// Status of the answer to a request for upload
		/// </summary>
		public int UploadStatus { get; set; } = 200;

		/// <summary>
		/// Value of Retry-After header of a refusal (null for none)
		/// </summary>
		public string RetryAfter { get; set; }

		public int Port { get; }

		/// <summary>
		/// Number of connections made, including those which sent no request (measurement of latency)
		/// </summary>
		public int Connections => Volatile.Read(ref _connections);

		public int Downloads => Volatile.Read(ref _downloads);
		public int Uploads => Volatile.Read(ref _uploads);

		public Uri DownloadUri => new($"http://127.0.0.1:{Port}/down");
		public Uri UploadUri => new($"http://127.0.0.1:{Port}/up");
		public string Name => $"127.0.0.1:{Port}";

		public TestServer()
		{
			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

			_ = Task.Run(AcceptAsync);
		}

		private async Task AcceptAsync()
		{
			try
			{
				while (!_cancellation.IsCancellationRequested)
				{
					var client = await _listener.AcceptTcpClientAsync();
					Interlocked.Increment(ref _connections);

					lock (_clients)
					{
						_clients.Add(client);
					}

					_ = Task.Run(() => ServeAsync(client));
				}
			}
			catch (Exception)
			{
				// The listener has been stopped.
			}
		}

		private async Task ServeAsync(TcpClient client)
		{
			try
			{
				using (client)
				{
					var stream = client.GetStream();
					var buffer = new byte[64 * 1024];

					var header = new StringBuilder();
					while (header.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
					{
						int read = await stream.ReadAsync(buffer, 0, 1024);
						if (read <= 0)
							return;

						header.Append(Encoding.ASCII.GetString(buffer, 0, read));
					}

					var isUpload = header.ToString().StartsWith("POST ", StringComparison.Ordinal);
					int status = isUpload ? UploadStatus : DownloadStatus;

					Interlocked.Increment(ref isUpload ? ref _uploads : ref _downloads);

					if (status != 200)
					{
						var reason = status switch
						{
							403 => "Forbidden",
							429 => "Too Many Requests",
							503 => "Service Unavailable",
							_ => "Error"
						};
						var refusal = Encoding.ASCII.GetBytes(
							$"HTTP/1.1 {status} {reason}\r\n{((RetryAfter is not null) ? $"Retry-After: {RetryAfter}\r\n" : string.Empty)}Content-Length: 0\r\nConnection: close\r\n\r\n");
						await stream.WriteAsync(refusal, 0, refusal.Length);

						if (!isUpload)
							return;
					}

					if (isUpload)
					{
						// What is sent is read at a limited rate until the client ends.
						while (await stream.ReadAsync(buffer, 0, buffer.Length) > 0)
							await Task.Delay(2);

						return;
					}

					var head = Encoding.ASCII.GetBytes(
						"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 2000000000\r\n\r\n");
					await stream.WriteAsync(head, 0, head.Length);

					// About 1 MB a second on each connection
					while (!_cancellation.IsCancellationRequested)
					{
						await stream.WriteAsync(buffer, 0, 16 * 1024);
						await Task.Delay(15);
					}
				}
			}
			catch (Exception)
			{
				// The connection has been closed by the client.
			}
		}

		public void Dispose()
		{
			_cancellation.Cancel();
			_listener.Stop();

			lock (_clients)
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
	}

	private static IPEndPoint BindLoopback(ServicePoint servicePoint, IPEndPoint remoteEndPoint, int retryCount) =>
		(retryCount == 0) ? new IPEndPoint(IPAddress.Loopback, 0) : null;

	private const int DownloadCapTime = SpeedTester.DownloadWarmup + 1200;
	private const int UploadCapTime = SpeedTester.UploadWarmup + SpeedTester.UploadMinWindow + 400;

	/// <summary>
	/// Runs the stages of a measurement which use the test servers, as a measurement of
	/// a network does after it has confirmed internet access.
	/// </summary>
	private static async Task<SpeedSample> MeasureAsync(Uri[] downloadUris, Uri[] uploadUris, ServerBlockList blocks, int retryDelay = 200, string profile = "EXT", int seekTime = SpeedTester.ServerSeekTime)
	{
		var sample = new SpeedSample { Time = DateTime.UtcNow, Profile = profile, Trigger = SpeedTrigger.Periodic };

		await SpeedTester.MeasureTransfersAsync(
			sample,
			downloadUris,
			uploadUris,
			BindLoopback,
			IPAddress.Loopback,
			null,
			"Loopback",
			DownloadCapTime,
			UploadCapTime,
			SpeedTester.SafetyBytes,
			retryDelay,
			blocks,
			CancellationToken.None,
			seekTime);

		return sample;
	}

	private static string GetTempFilePath() =>
		Path.Combine(Path.GetTempPath(), $"WifinianTest-{Guid.NewGuid():N}", "servers.txt");

	private static void DeleteFolder(string filePath)
	{
		try
		{
			var folderPath = Path.GetDirectoryName(filePath);
			if (Directory.Exists(folderPath))
				Directory.Delete(folderPath, true);
		}
		catch (Exception)
		{
		}
	}

	#endregion

	[TestMethod]
	public async Task TestServerWhichLimitsRequestsIsLeftUntilItsTimeIsOver()
	{
		// The first server answers 429 and asks to wait for an hour. The second one works.
		using var first = new TestServer { DownloadStatus = 429, UploadStatus = 429, RetryAfter = "3600" };
		using var second = new TestServer();

		var downloadUris = new[] { first.DownloadUri, second.DownloadUri };
		var uploadUris = new[] { first.UploadUri };

		var filePath = GetTempFilePath();
		var utcNow = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
		var records = new List<string>();
		var until = ServerBlockList.FormatTime(utcNow.AddHours(1));
		try
		{
			var blocks = new ServerBlockList(filePath, () => utcNow, records.Add);

			// (1) The measurement which meets the limit goes on to the second server at once.
			var sample = await MeasureAsync(downloadUris, uploadUris, blocks, retryDelay: 30_000);

			Assert.AreEqual(SpeedResult.Ok, sample.Result);
			Assert.IsTrue(sample.Valid);
			Assert.IsTrue(sample.DownMbps > 0D);
			Assert.IsTrue(sample.DownBytes > 0L);
			Assert.IsNotNull(sample.LatencyMs);

			// The first server was asked once by each connection and not once more after a delay.
			Assert.AreEqual(SpeedTester.DownloadCount, first.Downloads);
			Assert.AreEqual(SpeedTester.DownloadCount, second.Downloads);

			// Upload has only the first server, which is blocked by now: it is left out, without
			// a request, and the measurement is a success by download alone.
			Assert.IsNull(sample.UpMbps);
			Assert.AreEqual(0L, sample.UpBytes);
			Assert.AreEqual(0, first.Uploads);

			Assert.AreEqual(
				$"Server: {second.Name} ({first.Name} failed: Download got no byte: HTTP 429 Too Many Requests (Retry-After 3600)); "
				+ $"Upload skipped: {first.Name} limits requests until {until}",
				sample.Detail);

			// The block is recorded once, with what the server said and until when.
			Assert.AreEqual(1, records.Count);
			Assert.AreEqual(
				$"Speed test server {first.Name} limits requests on EXT (HTTP 429 Too Many Requests (Retry-After 3600)). "
				+ $"It is not used there until {until} (60 min); speed tests use the other servers meanwhile.",
				records[0]);

			// This measurement, which found the limit, gets a line of the operation log.
			Assert.IsFalse(SpeedTester.IsRoutineDetail(sample.Detail));
			Assert.IsNotNull(AppController.GetMeasureFailureText(sample));

			// (2) The next measurement does not touch the first server at all.
			int connections = first.Connections;
			utcNow = utcNow.AddMinutes(10);

			sample = await MeasureAsync(downloadUris, uploadUris, blocks);

			Assert.AreEqual(SpeedResult.Ok, sample.Result);
			Assert.IsTrue(sample.DownMbps > 0D);
			Assert.IsNull(sample.UpMbps);
			Assert.AreEqual(connections, first.Connections);
			Assert.AreEqual(2 * SpeedTester.DownloadCount, second.Downloads);

			Assert.AreEqual(
				$"Server: {second.Name} ({first.Name} limits requests until {until}); "
				+ $"Upload skipped: {first.Name} limits requests until {until}",
				sample.Detail);

			// Nothing more is recorded: neither about the block nor about this measurement.
			Assert.AreEqual(1, records.Count);
			Assert.IsTrue(SpeedTester.IsRoutineDetail(sample.Detail));
			Assert.IsNull(AppController.GetMeasureFailureText(sample));

			// (3) This app is started again: the block has been kept in the file.
			Assert.IsTrue(File.Exists(filePath));

			utcNow = utcNow.AddMinutes(40);
			var restarted = new ServerBlockList(filePath, () => utcNow, records.Add);
			Assert.AreEqual(new DateTime(2026, 11, 18, 13, 0, 0, DateTimeKind.Utc), restarted.GetBlockedUntil(first.DownloadUri, "EXT"));
			Assert.IsNull(restarted.GetBlockedUntil(first.DownloadUri, "HOME"));
			Assert.IsNull(restarted.GetBlockedUntil(second.DownloadUri));

			sample = await MeasureAsync(downloadUris, uploadUris, restarted);

			Assert.AreEqual(SpeedResult.Ok, sample.Result);
			Assert.AreEqual(connections, first.Connections);
			Assert.AreEqual(3 * SpeedTester.DownloadCount, second.Downloads);
			Assert.AreEqual(1, records.Count);

			// (4) The hour is over and the first server answers again: it is used, for upload
			// as well, and the end of the block is recorded once.
			first.DownloadStatus = 200;
			first.UploadStatus = 200;
			utcNow = utcNow.AddMinutes(11);

			sample = await MeasureAsync(downloadUris, uploadUris, restarted);

			Assert.AreEqual(SpeedResult.Ok, sample.Result);
			Assert.IsTrue(sample.DownMbps > 0D);
			Assert.IsTrue(sample.UpMbps > 0D, sample.Detail);
			Assert.IsTrue(sample.UpBytes > 0L);
			Assert.IsNull(sample.Detail);
			Assert.AreEqual(2 * SpeedTester.DownloadCount, first.Downloads);
			Assert.AreEqual(3 * SpeedTester.DownloadCount, second.Downloads);

			Assert.AreEqual(2, records.Count);
			Assert.AreEqual($"Speed test server {first.Name} is used again on EXT: the time it asked to wait is over.", records[1]);
			Assert.IsFalse(File.Exists(filePath));
		}
		finally
		{
			DeleteFolder(filePath);
		}
	}

	[TestMethod]
	public async Task TestEveryServerFailingIsNotAFindingOnTheNetwork()
	{
		// The first server limits requests without telling how long. The second one is out of
		// order, which is not a limit.
		using var first = new TestServer { DownloadStatus = 429 };
		using var second = new TestServer { DownloadStatus = 503 };

		var utcNow = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
		var records = new List<string>();
		var blocks = new ServerBlockList(null, () => utcNow, records.Add);

		var sample = await MeasureAsync([first.DownloadUri, second.DownloadUri], [first.UploadUri], blocks);

		// Internet access was confirmed before and so this says nothing about the network.
		Assert.AreEqual(SpeedResult.EndpointFailed, sample.Result);
		Assert.IsFalse(sample.Valid);
		Assert.IsNull(sample.DownMbps);
		Assert.IsNull(sample.UpMbps);
		Assert.AreEqual(0L, sample.DownBytes);
		Assert.AreEqual(0, first.Uploads);

		Assert.AreEqual(
			$"{first.Name} failed: Download got no byte: HTTP 429 Too Many Requests; "
			+ $"{second.Name} failed: Download got no byte: HTTP 503 Service Unavailable",
			sample.Detail);
		Assert.IsNotNull(AppController.GetMeasureFailureText(sample));

		// Each server was asked once by each connection.
		Assert.AreEqual(SpeedTester.DownloadCount, first.Downloads);
		Assert.AreEqual(SpeedTester.DownloadCount, second.Downloads);

		// Only the first one is blocked, for the default time.
		Assert.AreEqual(utcNow + ServerBlockList.DefaultPeriod, blocks.GetBlockedUntil(first.DownloadUri, "EXT"));
		Assert.IsNull(blocks.GetBlockedUntil(second.DownloadUri));
		Assert.AreEqual(1, records.Count);
		StringAssert.Contains(records[0], "(30 min)");

		// The policy takes no action on it after a switch and repeats the test.
		var landing = new SwitchLanding { FromName = "A", FromScore = 50, ToName = "EXT", Time = sample.Time };
		Assert.AreEqual(LandingAction.None, SwitchPolicy.EvaluateLanding(landing, sample, sample.Time.AddSeconds(20)));
		Assert.IsTrue(AppController.IsLandingUnchecked(sample));
		Assert.IsNull(SpeedSummary.Create([sample], sample.Time.AddSeconds(20)));
	}

	[TestMethod]
	public async Task TestSingleFailingServerIsTriedOnceMore()
	{
		// A server which is out of order (not a limit) and has no other behind it is tried once
		// more after the delay, as before.
		using var server = new TestServer { DownloadStatus = 503 };

		var records = new List<string>();
		var blocks = new ServerBlockList(null, null, records.Add);

		var sample = await MeasureAsync([server.DownloadUri], [server.UploadUri], blocks);

		Assert.AreEqual(SpeedResult.EndpointFailed, sample.Result);
		Assert.AreEqual($"{server.Name} failed: Download got no byte in two attempts: HTTP 503 Service Unavailable", sample.Detail);
		Assert.AreEqual(2 * SpeedTester.DownloadCount, server.Downloads);
		Assert.IsNull(blocks.GetBlockedUntil(server.DownloadUri));
		Assert.AreEqual(0, records.Count);
	}

	[TestMethod]
	public async Task TestUploadRefusedByLimitLeavesUploadBlank()
	{
		// Download works. The server of upload answers 429 and asks to wait for 2 minutes.
		using var down = new TestServer();
		using var up = new TestServer { UploadStatus = 429, RetryAfter = "120" };

		var utcNow = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
		var records = new List<string>();
		var blocks = new ServerBlockList(null, () => utcNow, records.Add);

		var sample = await MeasureAsync([down.DownloadUri], [up.UploadUri], blocks);

		Assert.AreEqual(SpeedResult.Ok, sample.Result);
		Assert.IsTrue(sample.DownMbps > 0D);
		Assert.IsNull(sample.UpMbps);
		Assert.AreEqual("Upload not measured: HTTP 429 Too Many Requests (Retry-After 120)", sample.Detail);

		// Each connection of upload was refused once and did not ask again.
		Assert.AreEqual(SpeedTester.UploadCount, up.Uploads);

		Assert.AreEqual(utcNow.AddMinutes(2), blocks.GetBlockedUntil(up.UploadUri, "EXT"));
		Assert.IsNull(blocks.GetBlockedUntil(down.DownloadUri));
		Assert.AreEqual(1, records.Count);

		// The next measurement leaves upload out without a request.
		sample = await MeasureAsync([down.DownloadUri], [up.UploadUri], blocks);

		Assert.AreEqual(SpeedResult.Ok, sample.Result);
		Assert.IsTrue(sample.DownMbps > 0D);
		Assert.IsNull(sample.UpMbps);
		Assert.AreEqual($"Upload skipped: {up.Name} limits requests until {ServerBlockList.FormatTime(utcNow.AddMinutes(2))}", sample.Detail);
		Assert.AreEqual(SpeedTester.UploadCount, up.Uploads);
		Assert.AreEqual(1, records.Count);
	}

	[TestMethod]
	public void TestRateLimit()
	{
		var utcNow = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

		// Retry-After is a number of seconds or an HTTP date.
		Assert.AreEqual(TimeSpan.FromSeconds(3600), ServerBlockList.ParseRetryAfter(" 3600 ", utcNow));
		Assert.AreEqual(TimeSpan.FromMinutes(45), ServerBlockList.ParseRetryAfter("Wed, 18 Nov 2026 12:45:00 GMT", utcNow));
		Assert.AreEqual(TimeSpan.Zero, ServerBlockList.ParseRetryAfter("Wed, 18 Nov 2026 11:00:00 GMT", utcNow));
		Assert.IsNull(ServerBlockList.ParseRetryAfter(null, utcNow));
		Assert.IsNull(ServerBlockList.ParseRetryAfter("soon", utcNow));
		Assert.IsNull(ServerBlockList.ParseRetryAfter("-5", utcNow));

		// 429 is always a limit: 30 minutes if no time is told.
		Assert.AreEqual(TimeSpan.FromMinutes(30), ServerBlockList.GetRateLimit(429, null, "x", utcNow).Period);
		Assert.AreEqual(TimeSpan.FromMinutes(30), ServerBlockList.GetRateLimit(429, "soon", "x", utcNow).Period);
		Assert.AreEqual(TimeSpan.FromHours(1), ServerBlockList.GetRateLimit(429, "3600", "x", utcNow).Period);
		Assert.AreEqual(TimeSpan.FromMinutes(45), ServerBlockList.GetRateLimit(429, "Wed, 18 Nov 2026 12:45:00 GMT", "x", utcNow).Period);

		// Never longer than 6 hours nor shorter than a minute.
		Assert.AreEqual(TimeSpan.FromHours(6), ServerBlockList.GetRateLimit(429, "86400", "x", utcNow).Period);
		Assert.AreEqual(TimeSpan.FromHours(6), ServerBlockList.GetRateLimit(429, "99999999999999999", "x", utcNow).Period);
		Assert.AreEqual(TimeSpan.FromHours(6), ServerBlockList.GetRateLimit(503, "Fri, 20 Nov 2026 12:00:00 GMT", "x", utcNow).Period);
		Assert.AreEqual(TimeSpan.FromMinutes(1), ServerBlockList.GetRateLimit(429, "0", "x", utcNow).Period);

		// 403 and 503 are limits only with Retry-After.
		Assert.IsNull(ServerBlockList.GetRateLimit(403, null, "x", utcNow));
		Assert.IsNull(ServerBlockList.GetRateLimit(503, " ", "x", utcNow));
		Assert.AreEqual(TimeSpan.FromMinutes(2), ServerBlockList.GetRateLimit(503, "120", "x", utcNow).Period);
		Assert.AreEqual(TimeSpan.FromMinutes(2), ServerBlockList.GetRateLimit(403, "120", "x", utcNow).Period);

		// Other statuses are not.
		Assert.IsNull(ServerBlockList.GetRateLimit(500, "120", "x", utcNow));
		Assert.IsNull(ServerBlockList.GetRateLimit(404, null, "x", utcNow));
		Assert.IsNull(ServerBlockList.GetRateLimit(200, "120", "x", utcNow));

		// The head of the answer to an upload
		var limit = SpeedTester.ParseRefusal("HTTP/1.1 429 Too Many Requests\r\nServer: x\r\nretry-after: 3600\r\nContent-Length: 0\r\n\r\n", utcNow);
		Assert.AreEqual(TimeSpan.FromHours(1), limit.Period);
		Assert.AreEqual("HTTP 429 Too Many Requests (Retry-After 3600)", limit.Reason);

		Assert.AreEqual("HTTP 429 Too Many Requests", SpeedTester.ParseRefusal("HTTP/1.1 429 Too Many Requests\r\n\r\n", utcNow).Reason);
		Assert.IsNull(SpeedTester.ParseRefusal("HTTP/1.1 400 Bad Request\r\nRetry-After: 5\r\n\r\n", utcNow));
		Assert.IsNull(SpeedTester.ParseRefusal("HTTP/1.1 200 OK\r\n\r\n", utcNow));
		Assert.IsNull(SpeedTester.ParseRefusal("garbage", utcNow));
		Assert.IsNull(SpeedTester.ParseRefusal(string.Empty, utcNow));
	}

	[TestMethod]
	public void TestServerBlockList()
	{
		var filePath = GetTempFilePath();
		var utcNow = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
		var records = new List<string>();
		try
		{
			var blocks = new ServerBlockList(filePath, () => utcNow, records.Add);

			var cloudflare = new Uri("https://speed.cloudflare.com/__down?bytes=25000000");
			var upload = new Uri("https://Speed.Cloudflare.com/__up");
			var other = new Uri("https://cachefly.cachefly.net/100mb.test");
			var local = new Uri("http://127.0.0.1:8080/down");

			Assert.AreEqual("speed.cloudflare.com", ServerBlockList.GetKey(cloudflare));
			Assert.AreEqual("127.0.0.1:8080", ServerBlockList.GetKey(local));

			Assert.IsNull(blocks.GetBlockedUntil(cloudflare));
			Assert.IsFalse(File.Exists(filePath));

			// Download and upload on the same host are blocked together.
			Assert.IsTrue(blocks.Block(cloudflare, new RateLimit(TimeSpan.FromHours(1), "HTTP 429 Too Many Requests (Retry-After 3600)")));
			Assert.AreEqual(utcNow.AddHours(1), blocks.GetBlockedUntil(cloudflare));
			Assert.AreEqual(utcNow.AddHours(1), blocks.GetBlockedUntil(upload));
			Assert.IsNull(blocks.GetBlockedUntil(other));
			Assert.IsNull(blocks.GetBlockedUntil(null));
			Assert.AreEqual(1, records.Count);

			// A later answer extends the block but never shortens it, and is not recorded again.
			utcNow = utcNow.AddMinutes(10);
			Assert.IsFalse(blocks.Block(upload, new RateLimit(TimeSpan.FromMinutes(5), "HTTP 429")));
			Assert.AreEqual(utcNow.AddMinutes(50), blocks.GetBlockedUntil(cloudflare));
			Assert.IsFalse(blocks.Block(cloudflare, new RateLimit(TimeSpan.FromHours(2), "HTTP 429")));
			Assert.AreEqual(utcNow.AddHours(2), blocks.GetBlockedUntil(cloudflare));
			Assert.AreEqual(1, records.Count);

			// Whatever is asked for, a block lasts 6 hours at most and a minute at least.
			Assert.IsTrue(blocks.Block(other, new RateLimit(TimeSpan.FromDays(3), "HTTP 429")));
			Assert.AreEqual(utcNow.AddHours(6), blocks.GetBlockedUntil(other));
			Assert.IsTrue(blocks.Block(local, new RateLimit(TimeSpan.Zero, null)));
			Assert.AreEqual(utcNow.AddMinutes(1), blocks.GetBlockedUntil(local));
			Assert.AreEqual(3, records.Count);
			StringAssert.Contains(records[2], "no reason given");

			// Another instance reads the same blocks from the file and records nothing.
			var restarted = new ServerBlockList(filePath, () => utcNow, records.Add);
			Assert.AreEqual(utcNow.AddHours(2), restarted.GetBlockedUntil(cloudflare));
			Assert.AreEqual(utcNow.AddHours(6), restarted.GetBlockedUntil(other));
			Assert.AreEqual(utcNow.AddMinutes(1), restarted.GetBlockedUntil(local));
			Assert.AreEqual(3, records.Count);

			// A block ends at its time, which is recorded once.
			utcNow = utcNow.AddHours(2);
			Assert.IsNull(restarted.GetBlockedUntil(cloudflare));
			Assert.IsNull(restarted.GetBlockedUntil(local));
			Assert.IsNotNull(restarted.GetBlockedUntil(other));
			Assert.AreEqual(5, records.Count);
			Assert.IsTrue(records.Contains("Speed test server speed.cloudflare.com is used again: the time it asked to wait is over."));
			Assert.IsNull(restarted.GetBlockedUntil(cloudflare));
			Assert.AreEqual(5, records.Count);

			// A block which has ended while this app was not running is recorded at the next start.
			var later = new ServerBlockList(filePath, () => utcNow.AddHours(5), records.Add);
			Assert.IsNull(later.GetBlockedUntil(other));
			Assert.AreEqual(6, records.Count);
			Assert.IsFalse(File.Exists(filePath));

			// A time in the file which is further ahead than any block lasts (the clock has been
			// changed) is cut to the longest time. A broken file is tolerated.
			File.WriteAllLines(filePath, [
				"speed.cloudflare.com\t2030-01-01T00:00:00.0000000Z\tHTTP 429",
				"broken line",
				"example.test\tnot a time",
				string.Empty]);

			var loaded = new ServerBlockList(filePath, () => utcNow, records.Add);
			Assert.AreEqual(utcNow.AddHours(6), loaded.GetBlockedUntil(cloudflare));
			Assert.IsNull(loaded.GetBlockedUntil(new Uri("http://example.test/")));

			// Without a file the blocks are kept in memory only.
			var memory = new ServerBlockList(null, () => utcNow, records.Add);
			Assert.IsTrue(memory.Block(cloudflare, new RateLimit(TimeSpan.FromHours(1), "HTTP 429")));
			Assert.IsNotNull(memory.GetBlockedUntil(cloudflare));
			Assert.IsNull(new ServerBlockList(null, () => utcNow, records.Add).GetBlockedUntil(cloudflare));
		}
		finally
		{
			DeleteFolder(filePath);
		}
	}

	[TestMethod]
	public void TestServerUris()
	{
		// By default: the server of the settings, which is the first default one, then the others.
		var uris = SpeedTester.GetServerUris(Settings.DefaultSpeedTestDownloadUrl, Settings.DefaultSpeedTestDownloadUrls);
		CollectionAssert.AreEqual(
			new[] { "speed.cloudflare.com", "cachefly.cachefly.net", "proof.ovh.net" },
			uris.Select(x => x.Host).ToArray());
		Assert.IsTrue(uris.All(x => x.Scheme == Uri.UriSchemeHttps));

		// A URL set by the user goes first and the default ones follow.
		uris = SpeedTester.GetServerUris("http://nas.example.test:8080/big.bin", Settings.DefaultSpeedTestDownloadUrls);
		CollectionAssert.AreEqual(
			new[] { "nas.example.test", "speed.cloudflare.com", "cachefly.cachefly.net", "proof.ovh.net" },
			uris.Select(x => x.Host).ToArray());

		// One of the default ones set by the user is not repeated.
		uris = SpeedTester.GetServerUris("https://proof.ovh.net/files/1Gb.dat", Settings.DefaultSpeedTestDownloadUrls);
		CollectionAssert.AreEqual(
			new[] { "proof.ovh.net", "speed.cloudflare.com", "cachefly.cachefly.net" },
			uris.Select(x => x.Host).ToArray());

		// A server is listed once: a URL set by the user on one of the default servers (an older
		// default kept in the settings, for instance) replaces the default URL of that server.
		uris = SpeedTester.GetServerUris("https://SPEED.cloudflare.com/__down?bytes=100000000", Settings.DefaultSpeedTestDownloadUrls);
		CollectionAssert.AreEqual(
			new[] { "https://speed.cloudflare.com/__down?bytes=100000000", "https://cachefly.cachefly.net/100mb.test", "https://proof.ovh.net/files/1Gb.dat" },
			uris.Select(x => x.AbsoluteUri).ToArray());

		// The same host on another port is another server.
		uris = SpeedTester.GetServerUris("http://speed.cloudflare.com:8080/file", Settings.DefaultSpeedTestDownloadUrls);
		Assert.AreEqual(4, uris.Count);

		// What is not a URL of HTTP(S) is left out.
		foreach (var url in new[] { null, " ", "ftp://example.test/file", "not a url" })
			Assert.AreEqual(3, SpeedTester.GetServerUris(url, Settings.DefaultSpeedTestDownloadUrls).Count);

		// Upload has one server by default.
		uris = SpeedTester.GetServerUris(Settings.DefaultSpeedTestUploadUrl, Settings.DefaultSpeedTestUploadUrls);
		Assert.AreEqual(1, uris.Count);
		Assert.AreEqual("https://speed.cloudflare.com/__up", uris[0].AbsoluteUri);

		uris = SpeedTester.GetServerUris("https://example.test/up", Settings.DefaultSpeedTestUploadUrls);
		CollectionAssert.AreEqual(new[] { "example.test", "speed.cloudflare.com" }, uris.Select(x => x.Host).ToArray());
	}

	[TestMethod]
	public async Task TestLimitIsOfTheNetworkOnWhichItWasMet()
	{
		// A limit is of the public address the requests came from. The server refuses on one
		// network (EXT) and would answer on another (HOME).
		using var first = new TestServer { DownloadStatus = 429, UploadStatus = 429, RetryAfter = "3600" };
		using var second = new TestServer();

		var downloadUris = new[] { first.DownloadUri, second.DownloadUri };
		var uploadUris = new[] { first.UploadUri };

		var filePath = GetTempFilePath();
		var utcNow = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
		var records = new List<string>();
		try
		{
			var blocks = new ServerBlockList(filePath, () => utcNow, records.Add);

			var sample = await MeasureAsync(downloadUris, uploadUris, blocks);
			Assert.AreEqual(SpeedResult.Ok, sample.Result);
			Assert.IsTrue(sample.IsFallback);
			Assert.AreEqual(utcNow.AddHours(1), blocks.GetBlockedUntil(first.DownloadUri, "EXT"));
			Assert.IsNull(blocks.GetBlockedUntil(first.DownloadUri, "HOME"));
			Assert.IsNull(blocks.GetBlockedUntil(first.DownloadUri));

			// On the other network the server is asked and, as it answers there, used for
			// download and upload. The block of the first network stays, across a restart too.
			first.DownloadStatus = 200;
			first.UploadStatus = 200;
			int downloads = first.Downloads;

			var restarted = new ServerBlockList(filePath, () => utcNow, records.Add);
			sample = await MeasureAsync(downloadUris, uploadUris, restarted, profile: "HOME");

			Assert.AreEqual(SpeedResult.Ok, sample.Result);
			Assert.IsFalse(sample.IsFallback);
			Assert.IsNull(sample.Detail);
			Assert.IsTrue(sample.UpMbps > 0D);
			Assert.AreEqual(downloads + SpeedTester.DownloadCount, first.Downloads);
			Assert.AreEqual(utcNow.AddHours(1), restarted.GetBlockedUntil(first.DownloadUri, "EXT"));
			Assert.AreEqual(1, records.Count);

			// The first network still leaves the server alone.
			int connections = first.Connections;
			sample = await MeasureAsync(downloadUris, uploadUris, restarted);
			Assert.IsTrue(sample.IsFallback);
			Assert.AreEqual(connections, first.Connections);

			// If the other network is refused as well, it is blocked in its turn.
			first.DownloadStatus = 429;
			downloads = first.Downloads;
			sample = await MeasureAsync(downloadUris, uploadUris, restarted, profile: "HOME");
			Assert.IsTrue(sample.IsFallback);
			Assert.AreEqual(downloads + SpeedTester.DownloadCount, first.Downloads);
			Assert.AreEqual(utcNow.AddHours(1), restarted.GetBlockedUntil(first.DownloadUri, "HOME"));
			Assert.AreEqual(2, records.Count);
			StringAssert.Contains(records[1], "limits requests on HOME (");

			// A line without a network, as written by an earlier build, holds on every network
			// and is not shortened on one of them by a later answer there.
			File.WriteAllText(filePath, $"{second.Name}\t{utcNow.AddHours(2):o}\tHTTP 429\r\n");
			var loaded = new ServerBlockList(filePath, () => utcNow, records.Add);
			Assert.AreEqual(utcNow.AddHours(2), loaded.GetBlockedUntil(second.DownloadUri));
			Assert.AreEqual(utcNow.AddHours(2), loaded.GetBlockedUntil(second.DownloadUri, "HOME"));

			loaded.Block(second.DownloadUri, new RateLimit(TimeSpan.FromMinutes(5), "HTTP 429"), "HOME");
			Assert.AreEqual(utcNow.AddHours(2), loaded.GetBlockedUntil(second.DownloadUri, "HOME"));
			Assert.AreEqual(utcNow.AddHours(2), loaded.GetBlockedUntil(second.DownloadUri, "EXT"));
		}
		finally
		{
			DeleteFolder(filePath);
		}
	}

	[TestMethod]
	public async Task TestEveryServerLimitingRequestsIsNotAskedOrRepeated()
	{
		using var first = new TestServer { DownloadStatus = 429, RetryAfter = "3600" };
		using var second = new TestServer { DownloadStatus = 429, RetryAfter = "1800" };

		var downloadUris = new[] { first.DownloadUri, second.DownloadUri };
		var utcNow = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
		var records = new List<string>();
		var blocks = new ServerBlockList(null, () => utcNow, records.Add);

		// The measurement which meets the limits is told in the operation log and, after
		// a switch, is repeated like any other in which the servers failed.
		var sample = await MeasureAsync(downloadUris, [first.UploadUri], blocks);
		Assert.AreEqual(SpeedResult.EndpointFailed, sample.Result);
		Assert.IsFalse(SpeedTester.IsLimited(sample));
		Assert.IsNotNull(AppController.GetMeasureFailureText(sample));
		Assert.IsTrue(AppController.IsLandingUnchecked(sample));
		Assert.AreEqual(2, records.Count);

		// From then on no server is asked. Such a measurement would come out the same until
		// the times are over: it gets no line of the operation log (the blocks have theirs) and
		// the test after a switch is not repeated.
		int firstConnections = first.Connections;
		int secondConnections = second.Connections;
		utcNow = utcNow.AddMinutes(10);

		sample = await MeasureAsync(downloadUris, [first.UploadUri], blocks);

		Assert.AreEqual(SpeedResult.EndpointFailed, sample.Result);
		Assert.IsFalse(sample.Valid);
		Assert.AreEqual(
			$"{first.Name} limits requests until {ServerBlockList.FormatTime(utcNow.AddMinutes(50))}; "
			+ $"{second.Name} limits requests until {ServerBlockList.FormatTime(utcNow.AddMinutes(20))}",
			sample.Detail);
		Assert.AreEqual(firstConnections, first.Connections);
		Assert.AreEqual(secondConnections, second.Connections);

		Assert.IsTrue(SpeedTester.IsLimited(sample));
		Assert.IsNull(AppController.GetMeasureFailureText(sample));
		Assert.IsFalse(AppController.IsLandingUnchecked(sample));
		Assert.AreEqual(2, records.Count);

		// It still is no finding on the network.
		var landing = new SwitchLanding { FromName = "A", FromScore = 50, ToName = "EXT", Time = sample.Time };
		Assert.AreEqual(LandingAction.None, SwitchPolicy.EvaluateLanding(landing, sample, sample.Time.AddSeconds(20)));
		Assert.IsNull(SpeedSummary.Create([sample], sample.Time.AddSeconds(20)));
	}

	[TestMethod]
	public async Task TestNoFurtherServerIsAskedOnceTheTimeIsUp()
	{
		// The first server fails. The time within which another server may be asked is over by
		// then (it is none here), and so the measurement ends without asking the second one.
		using var first = new TestServer { DownloadStatus = 503 };
		using var second = new TestServer();

		var blocks = new ServerBlockList(null, null, _ => { });

		var sample = await MeasureAsync([first.DownloadUri, second.DownloadUri], [first.UploadUri], blocks, seekTime: 0);

		Assert.AreEqual(SpeedResult.EndpointFailed, sample.Result);
		StringAssert.StartsWith(sample.Detail,
			$"{first.Name} failed: Download got no byte in two attempts: HTTP 503 Service Unavailable; {second.Name} was not asked: the servers before it took ");
		Assert.AreEqual(0, second.Connections);

		// It is told and, after a switch, repeated: the next one may go better.
		Assert.IsFalse(SpeedTester.IsLimited(sample));
		Assert.IsNotNull(AppController.GetMeasureFailureText(sample));
		Assert.IsTrue(AppController.IsLandingUnchecked(sample));

		// Within the time the second server is asked, as before.
		sample = await MeasureAsync([first.DownloadUri, second.DownloadUri], [second.UploadUri], blocks);
		Assert.AreEqual(SpeedResult.Ok, sample.Result);
		Assert.IsTrue(sample.IsFallback);
	}

	[TestMethod]
	public void TestSpeedThroughAnotherServerIsALowerBound()
	{
		var utcNow = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

		static SpeedSample Create(DateTime time, double down, double latency, string detail, SpeedResult result = SpeedResult.Ok) => new()
		{
			Time = time,
			Profile = "B",
			Signal = 70,
			DownMbps = down,
			UpMbps = 20,
			LatencyMs = latency,
			Result = result,
			Detail = detail
		};

		const string Limited = "Server: proof.ovh.net (speed.cloudflare.com limits requests until 13:00, cachefly.cachefly.net limits requests until 13:10); Upload skipped: speed.cloudflare.com limits requests until 13:00";
		const string Failed = "Server: cachefly.cachefly.net (speed.cloudflare.com failed: Download got no byte in two attempts: no response within 10 s)";

		var limited = Create(utcNow, 250, 110, Limited);
		var failed = Create(utcNow, 250, 30, Failed);
		var direct = Create(utcNow, 250, 12, "Upload not measured: no byte was sent");

		Assert.IsTrue(limited.IsFallback);
		Assert.IsTrue(failed.IsFallback);
		Assert.IsFalse(direct.IsFallback);
		Assert.IsFalse(Create(utcNow, 0, 0, null).IsFallback);
		Assert.IsFalse(Create(utcNow, 0, 0, Limited, SpeedResult.RouteMismatch).IsFallback);

		Assert.IsTrue(SpeedTester.IsLimited(limited));
		Assert.IsFalse(SpeedTester.IsLimited(failed));
		Assert.IsFalse(SpeedTester.IsLimited(direct));
		Assert.IsFalse(SpeedTester.IsLimited(null));

		// (1) After a switch from A (600 Mbps), B measures 250 Mbps through another server.
		// That does not show B to be slower and so B is not switched back from, however often.
		var landing = new SwitchLanding { FromName = "A", FromScore = 600, ToName = "B", Time = utcNow.AddSeconds(-20) };
		foreach (var sample in new[] { limited, failed, limited })
		{
			Assert.AreEqual(LandingAction.None, SwitchPolicy.EvaluateLanding(landing, sample, utcNow));
			Assert.AreEqual(0, landing.LowCount);
			Assert.IsFalse(SwitchPolicy.IsLandingKept(landing, sample));
		}

		// The test is repeated if the first server only failed, not if it limits requests.
		Assert.IsTrue(AppController.IsLandingUnchecked(failed));
		Assert.IsFalse(AppController.IsLandingUnchecked(limited));
		Assert.IsFalse(AppController.IsLandingUnchecked(direct));

		// The same speed through the first server counts as before.
		Assert.AreEqual(LandingAction.Retest, SwitchPolicy.EvaluateLanding(landing, direct, utcNow));
		Assert.AreEqual(LandingAction.Revert, SwitchPolicy.EvaluateLanding(landing, direct, utcNow));

		// A speed through another server which is high enough does show that B is good to keep.
		landing = new SwitchLanding { FromName = "A", FromScore = 300, ToName = "B", Time = utcNow.AddSeconds(-20) };
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(landing, limited, utcNow));
		Assert.IsTrue(SwitchPolicy.IsLandingKept(landing, limited));

		// No internet is found before any server is asked and counts whatever the servers do.
		landing = new SwitchLanding { FromName = "A", FromScore = 600, ToName = "B", Time = utcNow.AddSeconds(-20) };
		Assert.AreEqual(LandingAction.Revert, SwitchPolicy.EvaluateLanding(landing, Create(utcNow, 0, 0, null, SpeedResult.NoInternet), utcNow));

		// (2) The typical speed and latency of a network are of the first server as long as it
		// has such samples: the newer ones through another server do not pull them down.
		var samples = new List<SpeedSample>
		{
			Create(utcNow.AddHours(-5), 700, 12, null),
			Create(utcNow.AddHours(-4), 680, 14, null),
			Create(utcNow.AddHours(-3), 250, 110, Limited),
			Create(utcNow.AddHours(-2), 240, 112, Limited),
			Create(utcNow.AddHours(-1), 260, 108, Limited)
		};

		var summary = SpeedSummary.Create(samples, utcNow);
		Assert.AreEqual(5, summary.Count);
		Assert.AreEqual(680D, summary.DownMbps);
		Assert.AreEqual(14D, summary.LatencyMs);
		Assert.AreEqual(20D, summary.UpMbps);
		Assert.AreEqual(260D, summary.LastDownMbps);

		// A network which has nothing else is given what the other server measured.
		summary = SpeedSummary.Create(samples.Skip(2).ToArray(), utcNow);
		Assert.AreEqual(3, summary.Count);
		Assert.AreEqual(250D, summary.DownMbps);
		Assert.AreEqual(110D, summary.LatencyMs);
	}

	[TestMethod]
	public void TestRoutineDetail()
	{
		Assert.IsTrue(SpeedTester.IsRoutineDetail("Server: cachefly.cachefly.net (speed.cloudflare.com limits requests until 13:00)"));
		Assert.IsTrue(SpeedTester.IsRoutineDetail("Upload skipped: speed.cloudflare.com limits requests until 13:00"));
		Assert.IsTrue(SpeedTester.IsRoutineDetail("Server: cachefly.cachefly.net (speed.cloudflare.com limits requests until 13:00); Upload skipped: speed.cloudflare.com limits requests until 13:00"));

		Assert.IsFalse(SpeedTester.IsRoutineDetail(null));
		Assert.IsFalse(SpeedTester.IsRoutineDetail("Server: cachefly.cachefly.net (speed.cloudflare.com failed: Download got no byte: HTTP 429 Too Many Requests)"));
		Assert.IsFalse(SpeedTester.IsRoutineDetail("Server: cachefly.cachefly.net (speed.cloudflare.com limits requests until 13:00); Download went on after 1 failed request: HTTP 503 Service Unavailable"));
		Assert.IsFalse(SpeedTester.IsRoutineDetail("Upload not measured: no byte was sent"));
	}
}

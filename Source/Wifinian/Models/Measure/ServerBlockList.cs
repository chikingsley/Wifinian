using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Wifinian.Models.Measure;

/// <summary>
/// Answer of a test server which limits requests: how long it is not to be used and what it said
/// </summary>
internal sealed class RateLimit
{
	public TimeSpan Period { get; }
	public string Reason { get; }

	public RateLimit(TimeSpan period, string reason)
	{
		this.Period = period;
		this.Reason = reason;
	}
}

/// <summary>
/// Test servers which limited requests and the times until which they are not to be used
/// </summary>
/// <remarks>
/// <para>A server which answers 429 (or 403/503 with Retry-After) refuses every request for a while
/// and counts those made meanwhile against this PC. Without this list each measurement would ask
/// it again, fail and make every network look untestable.</para>
/// <para>The list is kept in a small file so that starting this app again does not forget it.
/// One line is recorded when a block starts and one when it ends, not one per measurement.</para>
/// <para>No method of this class throws an exception.</para>
/// </remarks>
internal sealed class ServerBlockList
{
	/// <summary>
	/// Time a server is not used when it limits requests without telling how long to wait
	/// </summary>
	internal static readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(30);

	/// <summary>
	/// Longest time a server is not used whatever it asks for
	/// </summary>
	internal static readonly TimeSpan MaxPeriod = TimeSpan.FromHours(6);

	/// <summary>
	/// Shortest time a server is not used once it has limited requests
	/// </summary>
	internal static readonly TimeSpan MinPeriod = TimeSpan.FromMinutes(1);

	private const string FileName = "servers.txt";

	private readonly string _filePath;
	private readonly Func<DateTime> _getUtcNow;
	private readonly Action<string> _record;

	private readonly Dictionary<string, (DateTime until, string reason)> _blocks = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _lock = new();
	private bool _isLoaded;

	/// <summary>
	/// File path of the list of this app (in the subfolder which the installer leaves)
	/// </summary>
	internal static string DefaultFilePath => Path.Combine(AppDataService.FolderPath, "data", FileName);

	/// <param name="filePath">File path in which the list is kept (null to keep it in memory only)</param>
	/// <param name="getUtcNow">Function to get the current time (UTC)</param>
	/// <param name="record">Action to record the start and the end of a block (null for the operation log)</param>
	public ServerBlockList(string filePath, Func<DateTime> getUtcNow = null, Action<string> record = null)
	{
		_filePath = filePath;
		_getUtcNow = getUtcNow ?? (() => DateTime.UtcNow);
		_record = record ?? Logger.RecordOperation;
	}

	/// <summary>
	/// Gets the name by which a server is blocked: the host, with the port unless it is the default one.
	/// </summary>
	/// <remarks>Download and upload on the same host share the block.</remarks>
	public static string GetKey(Uri uri) => uri?.Authority.ToLowerInvariant();

	/// <summary>
	/// Gets the time (UTC) until which a server is not to be used.
	/// </summary>
	/// <returns>Time if the server is blocked now, null otherwise</returns>
	public DateTime? GetBlockedUntil(Uri uri)
	{
		var key = GetKey(uri);
		if (string.IsNullOrEmpty(key))
			return null;

		var ended = new List<string>();
		DateTime? until = null;
		try
		{
			lock (_lock)
			{
				Load();
				Expire(ended);

				if (_blocks.TryGetValue(key, out var block))
					until = block.until;
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to check blocked test servers." + Environment.NewLine
				+ ex);
		}

		RecordEnds(ended);
		return until;
	}

	/// <summary>
	/// Blocks a server which limited requests.
	/// </summary>
	/// <returns>True if the block started now, false if the server was blocked already</returns>
	public bool Block(Uri uri, RateLimit limit)
	{
		var key = GetKey(uri);
		if (string.IsNullOrEmpty(key) || (limit is null))
			return false;

		var ended = new List<string>();
		var isStarted = false;
		DateTime until = default;
		try
		{
			lock (_lock)
			{
				Load();
				Expire(ended);

				var now = _getUtcNow.Invoke();
				until = now + Clamp(limit.Period);

				var isBlocked = _blocks.TryGetValue(key, out var block);
				isStarted = !isBlocked;

				// A block is never shortened by a later answer.
				if (!isBlocked || (block.until < until))
				{
					_blocks[key] = (until, Clean(limit.Reason));
					Save();
				}
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to block a test server." + Environment.NewLine
				+ ex);
		}

		RecordEnds(ended);

		if (isStarted)
		{
			Record($"Speed test server {key} limits requests ({Clean(limit.Reason) ?? "no reason given"}). "
				+ $"It is not used until {FormatTime(until)} ({Math.Ceiling((until - _getUtcNow.Invoke()).TotalMinutes):0} min); "
				+ "speed tests use the other servers meanwhile.");
		}
		return isStarted;
	}

	/// <summary>
	/// Formats a time (UTC) as the local time of day.
	/// </summary>
	internal static string FormatTime(DateTime utcTime) =>
		DateTime.SpecifyKind(utcTime, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

	/// <summary>
	/// Gets the limit told by a response of a status which is not a success.
	/// </summary>
	/// <param name="status">HTTP status</param>
	/// <param name="retryAfter">Value of Retry-After header (seconds or HTTP date, null if none)</param>
	/// <param name="reason">The response in words</param>
	/// <param name="utcNow">Current time (UTC)</param>
	/// <returns>Limit if the response is of a server which limits requests, null otherwise</returns>
	/// <remarks>
	/// 429 is always a limit. 403 and 503 are only with Retry-After because they are given for
	/// other reasons as well, which may be gone at the next request.
	/// </remarks>
	internal static RateLimit GetRateLimit(int status, string retryAfter, string reason, DateTime utcNow)
	{
		if (status is not (429 or 403 or 503))
			return null;

		var period = ParseRetryAfter(retryAfter, utcNow);
		if ((period is null) && (status != 429))
			return null;

		return new RateLimit(Clamp(period ?? DefaultPeriod), reason);
	}

	/// <summary>
	/// Parses the value of Retry-After header, which is a number of seconds or an HTTP date.
	/// </summary>
	internal static TimeSpan? ParseRetryAfter(string value, DateTime utcNow)
	{
		if (string.IsNullOrWhiteSpace(value))
			return null;

		value = value.Trim();

		if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
			return TimeSpan.FromSeconds(Math.Min(seconds, (long)MaxPeriod.TotalSeconds));

		if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
		{
			var period = date - utcNow;
			return (period > TimeSpan.Zero) ? period : TimeSpan.Zero;
		}
		return null;
	}

	private static TimeSpan Clamp(TimeSpan period) =>
		(period < MinPeriod) ? MinPeriod : ((period > MaxPeriod) ? MaxPeriod : period);

	private static DateTime Min(DateTime a, DateTime b) => (a <= b) ? a : b;

	private static string Clean(string text) =>
		string.IsNullOrWhiteSpace(text)
			? null
			: string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

	#region Record

	private void RecordEnds(List<string> ended)
	{
		foreach (var key in ended)
			Record($"Speed test server {key} is used again: the time it asked to wait is over.");
	}

	private void Record(string text)
	{
		try
		{
			_record.Invoke(text);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record a blocked test server." + Environment.NewLine
				+ ex);
		}
	}

	#endregion

	#region File

	/// <summary>
	/// Removes the blocks whose time is over.
	/// </summary>
	private void Expire(List<string> ended)
	{
		var now = _getUtcNow.Invoke();

		var keys = _blocks.Where(x => x.Value.until <= now).Select(x => x.Key).ToArray();
		if (keys.Length == 0)
			return;

		foreach (var key in keys)
			_blocks.Remove(key);

		ended.AddRange(keys);
		Save();
	}

	private void Load()
	{
		if (_isLoaded)
			return;

		_isLoaded = true;

		if (string.IsNullOrEmpty(_filePath))
			return;

		try
		{
			if (!File.Exists(_filePath))
				return;

			var latest = _getUtcNow.Invoke() + MaxPeriod;

			foreach (var line in File.ReadAllLines(_filePath, Encoding.UTF8))
			{
				// Server, time (UTC) until which it is blocked and what it said, separated by tabs
				var fields = line.Split('\t');
				if ((fields.Length < 2) || string.IsNullOrWhiteSpace(fields[0]))
					continue;

				if (!DateTime.TryParseExact(fields[1], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime until))
					continue;

				// A time further ahead than any block lasts is of a clock which has been changed.
				until = Min(DateTime.SpecifyKind(until, DateTimeKind.Utc), latest);

				_blocks[fields[0].Trim()] = (until, (fields.Length >= 3) ? Clean(fields[2]) : null);
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to load blocked test servers." + Environment.NewLine
				+ ex);
		}
	}

	private void Save()
	{
		if (string.IsNullOrEmpty(_filePath))
			return;

		try
		{
			if (_blocks.Count == 0)
			{
				File.Delete(_filePath);
				return;
			}

			var folderPath = Path.GetDirectoryName(_filePath);
			if (!string.IsNullOrEmpty(folderPath) && !Directory.Exists(folderPath))
				Directory.CreateDirectory(folderPath);

			File.WriteAllLines(
				_filePath,
				_blocks.Select(x => $"{x.Key}\t{DateTime.SpecifyKind(x.Value.until, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture)}\t{x.Value.reason}"),
				Encoding.UTF8);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to save blocked test servers." + Environment.NewLine
				+ ex);
		}
	}

	#endregion
}

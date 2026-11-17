using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Wifinian.Models.History;

/// <summary>
/// Kind of history rows
/// </summary>
public enum HistoryKind
{
	Signal = 0,
	Speed,
	Event
}

/// <summary>
/// Store of signal samples, speed samples and switching events in daily CSV files
/// </summary>
/// <remarks>
/// No public method of this class throws an exception. Failures are written to trace.
/// </remarks>
internal class HistoryService : IDisposable
{
	private const string SignalPrefix = "signal";
	private const string SpeedPrefix = "speed";
	private const string EventPrefix = "events";
	private const string DateFormat = "yyyyMMdd";
	private const string NewLine = "\r\n";

	/// <summary>
	/// Maximum number of rows kept in memory while files cannot be written
	/// </summary>
	internal const int MaxPendingCount = 5000;

	/// <summary>
	/// Minimum interval between written signal samples of a profile unless its connection changed
	/// </summary>
	internal static readonly TimeSpan SignalMinInterval = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Maximum interval between written signal samples of a profile
	/// </summary>
	internal static readonly TimeSpan SignalMaxInterval = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Minimum difference of signal quality to write a signal sample
	/// </summary>
	internal const int SignalMinDifference = 3;

	private readonly string _folderPath;
	private readonly Func<DateTime> _utcNow;

	private readonly object _lock = new();
	private readonly Dictionary<string, List<SpeedSample>> _speeds = new();
	private readonly Dictionary<string, SignalSample> _lastSignals = new();
	private readonly Queue<Row> _pending = new();

	/// <summary>
	/// Occurs after rows are appended.
	/// </summary>
	public event EventHandler<HistoryKind> Changed;

	public HistoryService(string folderPath, Func<DateTime> utcNow = null)
	{
		this._folderPath = folderPath;
		this._utcNow = utcNow ?? (() => DateTime.UtcNow);

		try
		{
			Directory.CreateDirectory(_folderPath);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to create history folder." + Environment.NewLine
				+ ex);
		}

		try
		{
			var now = GetNow();

			foreach (var sample in ReadFiles<SpeedSample>(SpeedPrefix, now - SpeedSummary.Period, now.AddDays(1), SpeedSample.TryParse, x => x.Time))
			{
				if (!_speeds.TryGetValue(sample.Key, out List<SpeedSample> samples))
				{
					samples = new List<SpeedSample>();
					_speeds.Add(sample.Key, samples);
				}
				samples.Add(sample);
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to load speed history." + Environment.NewLine
				+ ex);
		}
	}

	public static string GetKey(string profileName, Guid interfaceId) =>
		profileName + "|" + interfaceId.ToString("D");

	#region Append

	/// <summary>
	/// Appends signal samples reducing those which have not changed much.
	/// </summary>
	/// <param name="samples">Signal samples</param>
	public void AppendSignals(IEnumerable<SignalSample> samples)
	{
		bool isWritten = false;

		try
		{
			if (samples is null)
				return;

			lock (_lock)
			{
				var rows = new List<Row>();

				foreach (var sample in samples)
				{
					if (sample is null)
						continue;

					var key = sample.Key;
					_lastSignals.TryGetValue(key, out SignalSample last);

					if (!ShouldWrite(last, sample))
						continue;

					_lastSignals[key] = sample;
					rows.Add(new Row(SignalPrefix, SignalSample.Header, sample.Time, sample.ToCsv()));
				}

				if (rows.Count == 0)
					return;

				isWritten = WriteRows(rows);
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to append signal history." + Environment.NewLine
				+ ex);
		}

		if (isWritten)
			OnChanged(HistoryKind.Signal);
	}

	internal static bool ShouldWrite(SignalSample last, SignalSample sample)
	{
		if (last is null)
			return true;

		// BSSID of a network which is not connected is that of the strongest access point at
		// the moment and so can alternate. It must not get around the minimum interval.
		if ((last.Connected != sample.Connected) ||
			(sample.Connected && !string.Equals(last.Bssid ?? string.Empty, sample.Bssid ?? string.Empty, StringComparison.OrdinalIgnoreCase)))
			return true;

		var elapsed = CsvLine.ToUtc(sample.Time) - CsvLine.ToUtc(last.Time);
		if (elapsed < TimeSpan.Zero) // The clock was set back.
			return true;

		if (elapsed < SignalMinInterval)
			return false;

		return (Math.Abs(sample.Signal - last.Signal) >= SignalMinDifference)
			|| (last.Channel != sample.Channel)
			|| (elapsed >= SignalMaxInterval);
	}

	/// <summary>
	/// Appends a speed sample.
	/// </summary>
	/// <param name="sample">Speed sample</param>
	public void AppendSpeed(SpeedSample sample)
	{
		bool isAdded = false;

		try
		{
			if (sample is null)
				return;

			lock (_lock)
			{
				var key = sample.Key;
				if (!_speeds.TryGetValue(key, out List<SpeedSample> samples))
				{
					samples = new List<SpeedSample>();
					_speeds.Add(key, samples);
				}

				var oldest = GetNow() - SpeedSummary.Period;
				samples.RemoveAll(x => CsvLine.ToUtc(x.Time) < oldest);
				samples.Add(sample);
				isAdded = true;

				WriteRows(new List<Row> { new Row(SpeedPrefix, SpeedSample.Header, sample.Time, sample.ToCsv()) });
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to append speed history." + Environment.NewLine
				+ ex);
		}

		// The sample is held in memory even if it could not be written to the file yet.
		if (isAdded)
			OnChanged(HistoryKind.Speed);
	}

	/// <summary>
	/// Appends a switching event.
	/// </summary>
	/// <param name="switchEvent">Switching event</param>
	public void AppendEvent(SwitchEvent switchEvent)
	{
		bool isWritten = false;

		try
		{
			if (switchEvent is null)
				return;

			lock (_lock)
			{
				isWritten = WriteRows(new List<Row> { new Row(EventPrefix, SwitchEvent.Header, switchEvent.Time, switchEvent.ToCsv()) });
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to append event history." + Environment.NewLine
				+ ex);
		}

		if (isWritten)
			OnChanged(HistoryKind.Event);
	}

	private void OnChanged(HistoryKind kind)
	{
		try
		{
			Changed?.Invoke(this, kind);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to notify history change." + Environment.NewLine
				+ ex);
		}
	}

	#endregion

	#region Write

	private readonly struct Row
	{
		public string Prefix { get; }
		public string Header { get; }
		public DateTime Date { get; }
		public string Line { get; }

		public Row(string prefix, string header, DateTime time, string line)
		{
			this.Prefix = prefix;
			this.Header = header;
			this.Date = CsvLine.ToUtc(time).Date;
			this.Line = line;
		}
	}

	private string GetFilePath(string prefix, DateTime date) =>
		Path.Combine(_folderPath, $"{prefix}-{date.ToString(DateFormat, CultureInfo.InvariantCulture)}.csv");

	/// <summary>
	/// Writes rows to files following the rows which could not be written before.
	/// </summary>
	/// <param name="rows">Rows</param>
	/// <returns>True if any of the rows is written, false if all of the rows are kept in memory for the next attempt</returns>
	/// <remarks>
	/// A file which cannot be written holds back only its own rows. The rows of other files are written.
	/// This method must be called inside the lock.
	/// </remarks>
	private bool WriteRows(List<Row> rows)
	{
		var buffer = (_pending.Count > 0)
			? _pending.Concat(rows).ToList()
			: rows;

		if (buffer.Count == 0)
			return false;

		// File paths which could not be written, null if the folder is not usable
		HashSet<string> failedPaths = null;

		try
		{
			Directory.CreateDirectory(_folderPath);

			failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to create history folder." + Environment.NewLine
				+ ex);
		}

		if (failedPaths is not null)
		{
			// GroupBy method keeps the order of rows in each file.
			foreach (var group in buffer.GroupBy(x => GetFilePath(x.Prefix, x.Date), StringComparer.OrdinalIgnoreCase))
			{
				try
				{
					WriteFile(group.Key, group.First().Header, group.Select(x => x.Line));
				}
				catch (Exception ex)
				{
					Trace.WriteLine("Failed to write history file." + Environment.NewLine
						+ ex);

					failedPaths.Add(group.Key);
				}
			}

			if (failedPaths.Count == 0)
			{
				_pending.Clear();
				return true;
			}
		}

		bool IsFailed(Row row) =>
			(failedPaths is null) || failedPaths.Contains(GetFilePath(row.Prefix, row.Date));

		// Keep the rows which have not been written, oldest dropped, for the next attempt.
		var remaining = buffer.Where(x => IsFailed(x)).ToArray();
		bool isWritten = rows.Any(x => !IsFailed(x));

		_pending.Clear();
		foreach (var row in remaining.Skip(Math.Max(0, remaining.Length - MaxPendingCount)))
			_pending.Enqueue(row);

		return isWritten;
	}

	private static void WriteFile(string filePath, string header, IEnumerable<string> lines)
	{
		// Check the existing header because it will differ if the file was created by another version, and
		// check if the last line is terminated because it will not be if a previous write was interrupted.
		string existingHeader = null;
		bool isTerminated = true;

		var fileInfo = new FileInfo(filePath);
		if (fileInfo.Exists && (fileInfo.Length > 0))
		{
			using var rs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			if (rs.Length > 0)
			{
				rs.Seek(-1, SeekOrigin.End);
				isTerminated = (rs.ReadByte() == '\n');

				rs.Seek(0, SeekOrigin.Begin);
				using var sr = new StreamReader(rs, new UTF8Encoding(false), true);
				existingHeader = sr.ReadLine();
			}
		}

		using var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

		var content = new StringBuilder();
		if (fs.Length == 0)
		{
			content.Append(header).Append(NewLine);
		}
		else
		{
			if (!isTerminated)
				content.Append(NewLine);

			lines = Rearrange(lines, header, existingHeader);
		}

		foreach (var line in lines)
			content.Append(line).Append(NewLine);

		using var sw = new StreamWriter(fs, new UTF8Encoding(false));
		sw.Write(content.ToString());
		sw.Flush();
	}

	/// <summary>
	/// Rearranges lines to match the header of an existing file so that the file remains readable.
	/// </summary>
	private static IEnumerable<string> Rearrange(IEnumerable<string> lines, string header, string existingHeader)
	{
		if (string.IsNullOrWhiteSpace(existingHeader) ||
			string.Equals(header, existingHeader.Trim(), StringComparison.Ordinal))
			return lines;

		var existingNames = CsvLine.Split(existingHeader)?.Select(x => x.Trim()).ToArray();
		if ((existingNames is null) || !existingNames.Contains("Time", StringComparer.OrdinalIgnoreCase)) // Not a header
			return lines;

		var columns = CsvLine.GetColumns(header);

		return lines.Select(line =>
		{
			var fields = CsvLine.Split(line);
			return CsvLine.Join(existingNames.Select(x => CsvLine.GetField(columns, fields, x) ?? string.Empty).ToArray());
		});
	}

	#endregion

	#region Read

	private delegate bool TryParser<T>(IReadOnlyDictionary<string, int> columns, string[] fields, out T item);

	public IReadOnlyList<SignalSample> ReadSignals(DateTime fromUtc, DateTime toUtc)
	{
		try
		{
			return ReadFiles<SignalSample>(SignalPrefix, fromUtc, toUtc, SignalSample.TryParse, x => x.Time);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to read signal history." + Environment.NewLine
				+ ex);
			return Array.Empty<SignalSample>();
		}
	}

	public IReadOnlyList<SpeedSample> ReadSpeeds(DateTime fromUtc, DateTime toUtc)
	{
		try
		{
			return ReadFiles<SpeedSample>(SpeedPrefix, fromUtc, toUtc, SpeedSample.TryParse, x => x.Time);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to read speed history." + Environment.NewLine
				+ ex);
			return Array.Empty<SpeedSample>();
		}
	}

	public IReadOnlyList<SwitchEvent> ReadEvents(DateTime fromUtc, DateTime toUtc)
	{
		try
		{
			return ReadFiles<SwitchEvent>(EventPrefix, fromUtc, toUtc, SwitchEvent.TryParse, x => x.Time);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to read event history." + Environment.NewLine
				+ ex);
			return Array.Empty<SwitchEvent>();
		}
	}

	private List<T> ReadFiles<T>(string prefix, DateTime fromUtc, DateTime toUtc, TryParser<T> tryParse, Func<T, DateTime> getTime)
	{
		fromUtc = CsvLine.ToUtc(fromUtc);
		toUtc = CsvLine.ToUtc(toUtc);

		var items = new List<T>();
		if (toUtc < fromUtc)
			return items;

		var files = EnumerateFiles(prefix)
			.Where(x => (fromUtc.Date <= x.date) && (x.date <= toUtc.Date))
			.OrderBy(x => x.date)
			.ToArray();

		foreach (var (filePath, _) in files)
		{
			try
			{
				ReadFile(filePath, fromUtc, toUtc, tryParse, getTime, items);
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to read history file." + Environment.NewLine
					+ ex);
			}
		}

		// OrderBy method is stable and so the order of rows of the same time is kept.
		return items.OrderBy(x => getTime(x)).ToList();
	}

	private static void ReadFile<T>(string filePath, DateTime fromUtc, DateTime toUtc, TryParser<T> tryParse, Func<T, DateTime> getTime, List<T> items)
	{
		using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var sr = new StreamReader(fs, new UTF8Encoding(false), true);

		var columns = CsvLine.GetColumns(sr.ReadLine());
		if (columns is not { Count: > 0 })
			return;

		string line;
		while ((line = sr.ReadLine()) is not null)
		{
			if (line.Length == 0)
				continue;

			var fields = CsvLine.Split(line);
			if (fields is null)
				continue;

			if (!tryParse(columns, fields, out T item))
				continue;

			var time = getTime(item);
			if ((fromUtc <= time) && (time <= toUtc))
				items.Add(item);
		}
	}

	private IEnumerable<(string filePath, DateTime date)> EnumerateFiles(string prefix)
	{
		if (!Directory.Exists(_folderPath))
			yield break;

		foreach (var filePath in Directory.EnumerateFiles(_folderPath, prefix + "-*.csv"))
		{
			if (!string.Equals(Path.GetExtension(filePath), ".csv", StringComparison.OrdinalIgnoreCase))
				continue;

			var name = Path.GetFileNameWithoutExtension(filePath);
			if (name.Length != prefix.Length + 1 + DateFormat.Length)
				continue;

			if (DateTime.TryParseExact(name.Substring(prefix.Length + 1), DateFormat, CultureInfo.InvariantCulture,
				DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime date))
				yield return (filePath, date);
		}
	}

	#endregion

	#region Speed

	/// <summary>
	/// Gets speed samples of a profile held in memory.
	/// </summary>
	/// <param name="key">Profile key</param>
	/// <returns>Copy of speed samples in order of time</returns>
	public IReadOnlyList<SpeedSample> GetSpeedSamples(string key)
	{
		try
		{
			if (key is null)
				return Array.Empty<SpeedSample>();

			lock (_lock)
			{
				return _speeds.TryGetValue(key, out List<SpeedSample> samples)
					? samples.ToArray()
					: Array.Empty<SpeedSample>();
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to get speed samples." + Environment.NewLine
				+ ex);
			return Array.Empty<SpeedSample>();
		}
	}

	/// <summary>
	/// Gets the summary of speed samples of a profile.
	/// </summary>
	/// <param name="key">Profile key</param>
	/// <returns>Summary if any valid sample exists, null otherwise</returns>
	public SpeedSummary GetSummary(string key)
	{
		try
		{
			return SpeedSummary.Create(GetSpeedSamples(key), GetNow());
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to get speed summary." + Environment.NewLine
				+ ex);
			return null;
		}
	}

	/// <summary>
	/// Gets the total bytes used by speed measurements today (UTC).
	/// </summary>
	public long GetBytesToday()
	{
		try
		{
			var today = GetNow().Date;

			lock (_lock)
			{
				return _speeds.Values
					.SelectMany(x => x)
					.Where(x => CsvLine.ToUtc(x.Time).Date == today)
					.Sum(x => x.DownBytes + x.UpBytes);
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to get bytes of speed measurements." + Environment.NewLine
				+ ex);
			return 0L;
		}
	}

	/// <summary>
	/// Moves samples of a profile held in memory to a new key.
	/// </summary>
	/// <param name="oldKey">Old profile key</param>
	/// <param name="newKey">New profile key</param>
	/// <remarks>Rows in files are not changed.</remarks>
	public void Rename(string oldKey, string newKey)
	{
		try
		{
			if ((oldKey is null) || (newKey is null) || (oldKey == newKey))
				return;

			lock (_lock)
			{
				if (_speeds.TryGetValue(oldKey, out List<SpeedSample> samples))
				{
					_speeds.Remove(oldKey);

					if (_speeds.TryGetValue(newKey, out List<SpeedSample> existing))
						samples = existing.Concat(samples).OrderBy(x => x.Time).ToList();

					_speeds[newKey] = samples;
				}

				if (_lastSignals.TryGetValue(oldKey, out SignalSample last))
				{
					_lastSignals.Remove(oldKey);

					// A sample may have been written under the new key already. Keep the newer one.
					if (!_lastSignals.TryGetValue(newKey, out SignalSample existing) ||
						(CsvLine.ToUtc(existing.Time) < CsvLine.ToUtc(last.Time)))
						_lastSignals[newKey] = last;
				}
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to rename history." + Environment.NewLine
				+ ex);
		}
	}

	#endregion

	#region Purge

	/// <summary>
	/// Deletes files older than retention days.
	/// </summary>
	/// <param name="retentionDays">Retention days</param>
	public void Purge(int retentionDays)
	{
		try
		{
			if (retentionDays < 1)
				return;

			var oldest = GetNow().Date.AddDays(-retentionDays);

			lock (_lock)
			{
				var filePaths = new[] { SignalPrefix, SpeedPrefix, EventPrefix }
					.SelectMany(x => EnumerateFiles(x))
					.Where(x => x.date < oldest)
					.Select(x => x.filePath)
					.ToArray();

				foreach (var filePath in filePaths)
				{
					try
					{
						File.Delete(filePath);
					}
					catch (Exception ex)
					{
						Trace.WriteLine("Failed to delete history file." + Environment.NewLine
							+ ex);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to purge history." + Environment.NewLine
				+ ex);
		}
	}

	#endregion

	private DateTime GetNow() => CsvLine.ToUtc(_utcNow());

	#region IDisposable

	private bool _isDisposed = false;

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (_isDisposed)
			return;

		if (disposing)
		{
			try
			{
				Changed = null;

				lock (_lock)
				{
					// Try to write the rows which could not be written before.
					if (_pending.Count > 0)
						WriteRows(new List<Row>());
				}
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to dispose history." + Environment.NewLine
					+ ex);
			}
		}

		_isDisposed = true;
	}

	#endregion
}
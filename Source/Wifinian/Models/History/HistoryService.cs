using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

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
/// Store of signal samples, speed samples and switching events in a local SQLite database
/// </summary>
/// <remarks>
/// No public method of this class throws an exception. Failures are written to trace.
/// The methods called on the UI thread (Append*, Get* and Rename) only take the lock of the
/// state in memory and never wait for the database: appended rows are queued and written by
/// a single background task. The connection for writing is used only inside its own lock, by
/// that task, Purge, ImportCsv and Dispose. Each reading uses a read-only connection of its
/// own, which write-ahead logging lets run alongside the writer, and holds no lock.
/// The order of locks is the lock of the database and then the lock of the state.
/// Time is stored as UTC ISO 8601 text of fixed length (yyyy-MM-ddTHH:mm:ssZ) and so can be
/// compared as text.
/// </remarks>
internal class HistoryService : IDisposable
{
	private const string DateFormat = "yyyyMMdd";

	private const string ImportKey = "csv_import";
	private const string ImportImported = "imported"; // Rows are imported but the folder is not renamed yet.
	private const string ImportDone = "done";
	private const string ImportedSuffix = "-csv-imported";
	private const string CorruptSuffix = "-corrupt-";

	/// <summary>
	/// Maximum number of rows kept in memory until they are written to the database
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

	/// <summary>
	/// Default time (seconds) to wait for the database locked by another process
	/// </summary>
	internal const int DefaultTimeout = 3;

	/// <summary>
	/// Minimum number of free pages (4 KiB each by default) to rebuild the database when purged
	/// </summary>
	internal const int VacuumMinPageCount = 64;

	/// <summary>
	/// Minimum ratio of free pages to all the pages to rebuild the database when purged
	/// </summary>
	internal const double VacuumMinRatio = 0.25;

	private readonly string _filePath;
	private readonly Func<DateTime> _utcNow;
	private readonly int _timeout;

	// State in memory
	private readonly object _lock = new();
	private readonly Dictionary<string, List<SpeedSample>> _speeds = new();
	private readonly Dictionary<string, SignalSample> _lastSignals = new();
	private readonly Queue<object> _pending = new(); // Rows and renames to be written, in order
	private Task _flushTask;
	private bool _isFlushing;
	private volatile bool _isDisposed;

	// Connection for writing
	private readonly object _dbLock = new();
	private SqliteConnection _connection;
	private bool _isLastWriteFailed;
	private bool _isClosed;

	/// <summary>
	/// Occurs after rows are appended.
	/// </summary>
	public event EventHandler<HistoryKind> Changed;

	/// <param name="filePath">File path of the database</param>
	/// <param name="utcNow">Function to get the current time (UTC)</param>
	/// <param name="timeout">Time (seconds) to wait for the database locked by another process</param>
	public HistoryService(string filePath, Func<DateTime> utcNow = null, int timeout = DefaultTimeout)
	{
		this._filePath = filePath;
		this._utcNow = utcNow ?? (() => DateTime.UtcNow);
		this._timeout = Math.Max(1, timeout);

		// The database is created when the first row is written so that nothing is left on disk
		// as long as nothing is recorded. An existing database is upgraded before it is read
		// because an upgrade may correct rows.
		try
		{
			lock (_dbLock)
			{
				UpgradeExisting();
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to upgrade history database." + Environment.NewLine
				+ ex);
		}

		try
		{
			LoadSpeeds();
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to load speed history." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Folder path of the database
	/// </summary>
	public string FolderPath => Path.GetDirectoryName(_filePath);

	public static string GetKey(string profileName, Guid interfaceId) =>
		profileName + "|" + interfaceId.ToString("D");

	private static bool TrySplitKey(string key, out string profileName, out Guid interfaceId)
	{
		profileName = null;
		interfaceId = default;

		var index = key?.LastIndexOf('|') ?? -1;
		if (index <= 0)
			return false;

		profileName = key.Substring(0, index);
		return Guid.TryParseExact(key.Substring(index + 1), "D", out interfaceId);
	}

	#region Append

	/// <summary>
	/// Appends signal samples reducing those which have not changed much.
	/// </summary>
	/// <param name="samples">Signal samples</param>
	/// <remarks>The samples are written in the background.</remarks>
	public void AppendSignals(IEnumerable<SignalSample> samples)
	{
		try
		{
			if (samples is null)
				return;

			lock (_lock)
			{
				if (_isDisposed)
					return;

				int count = 0;

				foreach (var sample in samples)
				{
					if (sample is null)
						continue;

					var key = sample.Key;
					_lastSignals.TryGetValue(key, out SignalSample last);

					if (!ShouldWrite(last, sample))
						continue;

					_lastSignals[key] = sample;
					_pending.Enqueue(sample);
					count++;
				}

				if (count > 0)
					ScheduleFlush();
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to append signal history." + Environment.NewLine
				+ ex);
		}
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
	/// <remarks>
	/// The sample is held in memory at once, and so is reflected in the summary, and is written
	/// in the background.
	/// </remarks>
	public void AppendSpeed(SpeedSample sample)
	{
		bool isAdded = false;

		try
		{
			if (sample is null)
				return;

			lock (_lock)
			{
				if (_isDisposed)
					return;

				var oldest = GetNow() - SpeedSummary.Period;
				if (_speeds.TryGetValue(sample.Key, out List<SpeedSample> samples))
					samples.RemoveAll(x => CsvLine.ToUtc(x.Time) < oldest);

				HoldSpeed(sample);
				isAdded = true;

				_pending.Enqueue(sample);
				ScheduleFlush();
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to append speed history." + Environment.NewLine
				+ ex);
		}

		// The sample is held in memory even if it has not been written to the database yet.
		if (isAdded)
			OnChanged(HistoryKind.Speed);
	}

	/// <summary>
	/// Appends a switching event.
	/// </summary>
	/// <param name="switchEvent">Switching event</param>
	/// <remarks>The event is written in the background.</remarks>
	public void AppendEvent(SwitchEvent switchEvent)
	{
		try
		{
			if (switchEvent is null)
				return;

			lock (_lock)
			{
				if (_isDisposed)
					return;

				_pending.Enqueue(switchEvent);
				ScheduleFlush();
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to append event history." + Environment.NewLine
				+ ex);
		}
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

	#region Database

	/// <summary>
	/// Version of the schema (PRAGMA user_version) this class writes
	/// </summary>
	internal const int SchemaVersion = 2;

	/// <summary>
	/// Scripts to upgrade the schema, the element of index N upgrading version N to N+1
	/// </summary>
	/// <remarks>
	/// To add a column, append a script of ALTER TABLE ... ADD COLUMN ... here (never change a
	/// script which has been released), add the column to the columns of the table below and
	/// increment <see cref="SchemaVersion"/>. A database of a newer version is used as it is
	/// because columns are always referred to by name.
	/// </remarks>
	private static readonly string[] _migrations =
	{
		// 0 -> 1
		@"CREATE TABLE signal (
			id INTEGER PRIMARY KEY,
			time TEXT NOT NULL,
			interface_id TEXT NOT NULL,
			profile TEXT NOT NULL,
			connected INTEGER NOT NULL DEFAULT 0,
			signal INTEGER NOT NULL DEFAULT 0,
			rssi INTEGER,
			band REAL NOT NULL DEFAULT 0,
			channel INTEGER NOT NULL DEFAULT 0,
			protocol TEXT NOT NULL DEFAULT '',
			bssid TEXT NOT NULL DEFAULT '',
			link_rate INTEGER);
		CREATE INDEX ix_signal_time ON signal (time);
		CREATE INDEX ix_signal_profile_time ON signal (profile, time);

		CREATE TABLE speed (
			id INTEGER PRIMARY KEY,
			time TEXT NOT NULL,
			interface_id TEXT NOT NULL,
			profile TEXT NOT NULL,
			bssid TEXT NOT NULL DEFAULT '',
			band REAL NOT NULL DEFAULT 0,
			channel INTEGER NOT NULL DEFAULT 0,
			signal INTEGER NOT NULL DEFAULT 0,
			link_rate INTEGER,
			gateway_ms REAL,
			latency_ms REAL,
			jitter_ms REAL,
			down_mbps REAL,
			up_mbps REAL,
			down_bytes INTEGER NOT NULL DEFAULT 0,
			up_bytes INTEGER NOT NULL DEFAULT 0,
			duration_ms INTEGER NOT NULL DEFAULT 0,
			trigger TEXT NOT NULL DEFAULT '',
			result TEXT NOT NULL DEFAULT '',
			valid INTEGER NOT NULL DEFAULT 0);
		CREATE INDEX ix_speed_time ON speed (time);
		CREATE INDEX ix_speed_profile_time ON speed (profile, time);

		CREATE TABLE events (
			id INTEGER PRIMARY KEY,
			time TEXT NOT NULL,
			interface_id TEXT NOT NULL,
			kind TEXT NOT NULL,
			from_profile TEXT NOT NULL DEFAULT '',
			to_profile TEXT NOT NULL DEFAULT '',
			from_score REAL,
			to_score REAL,
			reason TEXT NOT NULL DEFAULT '');
		CREATE INDEX ix_events_time ON events (time);
		CREATE INDEX ix_events_profile_time ON events (to_profile, time);

		CREATE TABLE meta (
			key TEXT PRIMARY KEY,
			value TEXT);",

		// 1 -> 2
		// Upload speed was once measured by counting bytes when they were handed to the system,
		// over less than 3 seconds and up to 10 MB, which made it look several times faster than
		// the link (e.g. 124 Mbps on a link of 77 Mbps). Since then it is measured over at least
		// 3 seconds after warm-up unless 40 MB or more is sent. A row whose bytes are fewer than
		// that rate would send in the window is of the old method and its upload speed is blanked.
		// The other values of the row are kept.
		@"UPDATE speed SET up_mbps = NULL
			WHERE up_mbps IS NOT NULL
			AND up_bytes < 40000000
			AND up_bytes < up_mbps * 125000 * 2.7;"
	};

	private const string SignalTable = "signal";
	private const string SpeedTable = "speed";
	private const string EventTable = "events";

	private static readonly string[] _signalColumns =
	{
		"time", "interface_id", "profile", "connected", "signal", "rssi", "band", "channel", "protocol", "bssid", "link_rate"
	};

	private static readonly string[] _speedColumns =
	{
		"time", "interface_id", "profile", "bssid", "band", "channel", "signal", "link_rate", "gateway_ms", "latency_ms", "jitter_ms",
		"down_mbps", "up_mbps", "down_bytes", "up_bytes", "duration_ms", "trigger", "result", "valid"
	};

	private static readonly string[] _eventColumns =
	{
		"time", "interface_id", "kind", "from_profile", "to_profile", "from_score", "to_score", "reason"
	};

	/// <summary>
	/// Gets the connection for writing opening the database if necessary.
	/// </summary>
	/// <param name="creates">Whether to create the database if it does not exist</param>
	/// <returns>Connection, null if the database does not exist and is not to be created or if this instance has been disposed</returns>
	/// <remarks>
	/// A file which is not a database or is corrupt is set aside (renamed, not deleted) and
	/// a new database takes its place.
	/// This method must be called inside the lock of the database and can throw an exception.
	/// </remarks>
	private SqliteConnection GetConnection(bool creates)
	{
		if (_isClosed)
			return null;

		if (_connection is not null)
			return _connection;

		for (int i = 0; ; i++)
		{
			if (!File.Exists(_filePath))
			{
				if (!creates)
					return null;

				var folderPath = Path.GetDirectoryName(_filePath);
				if (!string.IsNullOrEmpty(folderPath))
					Directory.CreateDirectory(folderPath);
			}

			var connection = new SqliteConnection(GetConnectionString(SqliteOpenMode.ReadWriteCreate));
			try
			{
				connection.Open();

				Execute(connection, null, "PRAGMA journal_mode = WAL;");
				Execute(connection, null, "PRAGMA synchronous = NORMAL;");

				Migrate(connection);

				_connection = connection;
				return _connection;
			}
			catch (Exception ex)
			{
				connection.Dispose();

				if ((i > 0) || !IsCorrupt(ex))
					throw;

				Trace.WriteLine("Failed to open history database, which is to be set aside." + Environment.NewLine
					+ ex);
			}

			SetAside();
		}
	}

	private string GetConnectionString(SqliteOpenMode mode) => new SqliteConnectionStringBuilder
	{
		DataSource = _filePath,
		Mode = mode,
		Pooling = false, // The file must be released when the connection is disposed.
		DefaultTimeout = _timeout
	}.ToString();

	/// <summary>
	/// Upgrades an existing database of an older version.
	/// </summary>
	/// <remarks>
	/// Nothing is created and a file which is not a database is left as it is (it is set aside
	/// only when a row is to be written). This method must be called inside the lock of
	/// the database and can throw an exception.
	/// </remarks>
	private void UpgradeExisting()
	{
		if (!File.Exists(_filePath))
			return;

		using var connection = new SqliteConnection(GetConnectionString(SqliteOpenMode.ReadWrite));
		connection.Open();

		// Version 0 is a file with no table yet, which is prepared when a row is written.
		var version = Convert.ToInt32(ExecuteScalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
		if (version >= 1)
			Migrate(connection);
	}

	private static void Migrate(SqliteConnection connection)
	{
		var version = Convert.ToInt32(ExecuteScalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
		if (version >= _migrations.Length)
			return;

		using var transaction = connection.BeginTransaction();

		for (int i = Math.Max(0, version); i < _migrations.Length; i++)
			Execute(connection, transaction, _migrations[i]);

		Execute(connection, transaction, $"PRAGMA user_version = {_migrations.Length.ToString(CultureInfo.InvariantCulture)};");

		transaction.Commit();
	}

	/// <summary>
	/// Closes the connection for writing so that it will be opened anew at the next access.
	/// </summary>
	private void CloseConnection()
	{
		lock (_dbLock)
		{
			try
			{
				_connection?.Dispose();
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to close history database." + Environment.NewLine
					+ ex);
			}

			_connection = null;
		}
	}

	private const int SqliteCorrupt = 11; // SQLITE_CORRUPT
	private const int SqliteNotADb = 26; // SQLITE_NOTADB

	/// <summary>
	/// Determines whether an exception tells that the file is not a database or is corrupt.
	/// </summary>
	private static bool IsCorrupt(Exception ex) =>
		ex is SqliteException { SqliteErrorCode: SqliteCorrupt or SqliteNotADb };

	private static readonly string[] _accompanyingSuffixes = { "-wal", "-shm" };

	/// <summary>
	/// Renames the file of database, and the files which accompany it, to history-corrupt-(local
	/// time).db and so on so that a new database will be created.
	/// </summary>
	/// <remarks>
	/// The files are never deleted. This method must be called inside the lock of the database
	/// after the connection is closed and can throw an exception, in which case it will be
	/// tried again at the next access.
	/// </remarks>
	private void SetAside()
	{
		if (!File.Exists(_filePath))
			return;

		var folderPath = Path.GetDirectoryName(_filePath) ?? string.Empty;
		var name = Path.GetFileNameWithoutExtension(_filePath);
		var extension = Path.GetExtension(_filePath);
		var stamp = GetNow().ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

		var newPath = Path.Combine(folderPath, name + CorruptSuffix + stamp + extension);
		for (int i = 2; File.Exists(newPath) || Directory.Exists(newPath); i++)
			newPath = Path.Combine(folderPath, name + CorruptSuffix + stamp + "-" + i.ToString(CultureInfo.InvariantCulture) + extension);

		File.Move(_filePath, newPath);

		foreach (var suffix in _accompanyingSuffixes)
		{
			try
			{
				if (File.Exists(_filePath + suffix))
					File.Move(_filePath + suffix, newPath + suffix);
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to rename file of history database." + Environment.NewLine
					+ ex);
			}
		}

		Trace.WriteLine($"History database is set aside as {newPath}.");
	}

	/// <summary>
	/// Closes the connection for writing after a failure and sets the database aside if the
	/// failure tells that it is corrupt.
	/// </summary>
	private void HandleFailure(Exception ex)
	{
		lock (_dbLock)
		{
			CloseConnection();

			if (!IsCorrupt(ex))
				return;

			try
			{
				SetAside();
			}
			catch (Exception inner)
			{
				Trace.WriteLine("Failed to set history database aside." + Environment.NewLine
					+ inner);
			}
		}
	}

	/// <summary>
	/// Moves the database, and the files which accompany it, of a previous location to the
	/// current location unless a database exists there already.
	/// </summary>
	/// <param name="oldFilePath">File path of previous location</param>
	/// <param name="newFilePath">File path of current location</param>
	/// <returns>File path to be used, that of previous location if the database could not be moved</returns>
	/// <remarks>This method must be called before the database is opened.</remarks>
	public static string Relocate(string oldFilePath, string newFilePath)
	{
		try
		{
			if (File.Exists(newFilePath) || !File.Exists(oldFilePath))
				return newFilePath;

			var folderPath = Path.GetDirectoryName(newFilePath);
			if (!string.IsNullOrEmpty(folderPath))
				Directory.CreateDirectory(folderPath);

			// The write-ahead log, which is left if the application did not exit normally, holds
			// rows and so must be found beside the database when it is opened.
			foreach (var suffix in _accompanyingSuffixes.Where(x => File.Exists(newFilePath + x)))
				File.Delete(newFilePath + suffix);

			File.Move(oldFilePath, newFilePath);

			try
			{
				foreach (var suffix in _accompanyingSuffixes.Where(x => File.Exists(oldFilePath + x)))
					File.Move(oldFilePath + suffix, newFilePath + suffix);
			}
			catch
			{
				// The database and its write-ahead log must not be separated.
				foreach (var suffix in _accompanyingSuffixes.Where(x => File.Exists(newFilePath + x)))
					File.Move(newFilePath + suffix, oldFilePath + suffix);

				File.Move(newFilePath, oldFilePath);
				throw;
			}

			return newFilePath;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to move history database." + Environment.NewLine
				+ ex);

			return File.Exists(newFilePath) ? newFilePath : oldFilePath;
		}
	}

	private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction, string text, params (string name, object value)[] parameters)
	{
		var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = text;

		foreach (var (name, value) in parameters)
			command.Parameters.AddWithValue(name, value ?? DBNull.Value);

		return command;
	}

	private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string text, params (string name, object value)[] parameters)
	{
		using var command = CreateCommand(connection, transaction, text, parameters);
		command.ExecuteNonQuery();
	}

	private static object ExecuteScalar(SqliteConnection connection, SqliteTransaction transaction, string text, params (string name, object value)[] parameters)
	{
		using var command = CreateCommand(connection, transaction, text, parameters);
		return command.ExecuteScalar();
	}

	private static string GetMeta(SqliteConnection connection, string key) =>
		ExecuteScalar(connection, null, "SELECT value FROM meta WHERE key = $key;", ("$key", key)) as string;

	private static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value) =>
		Execute(connection, transaction, "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);", ("$key", key), ("$value", value));

	#endregion

	#region Write

	/// <summary>
	/// Rename of a profile to be applied to the database in the order of rows
	/// </summary>
	private class RenameOperation
	{
		public Guid InterfaceId { get; set; }
		public string OldName { get; set; }
		public string NewName { get; set; }
	}

	/// <summary>
	/// Starts the background task to write the queued rows unless it is running.
	/// </summary>
	/// <remarks>This method must be called inside the lock of the state.</remarks>
	private void ScheduleFlush()
	{
		// The oldest rows are dropped while the database cannot be written.
		while (_pending.Count > MaxPendingCount)
			_pending.Dequeue();

		if (_isFlushing || _isDisposed)
			return;

		_isFlushing = true;
		_flushTask = Task.Run(() => FlushPending());
	}

	/// <summary>
	/// Writes the queued rows until none is left or writing fails. If it fails, the rows are
	/// kept for the next attempt, which the next append will start.
	/// </summary>
	private void FlushPending()
	{
		try
		{
			while (true)
			{
				bool isWritten;
				bool hasSignal;
				bool hasEvent;

				lock (_dbLock)
				{
					isWritten = WritePending(out hasSignal, out hasEvent);
				}

				if (hasSignal)
					OnChanged(HistoryKind.Signal);
				if (hasEvent)
					OnChanged(HistoryKind.Event);

				lock (_lock)
				{
					if (!isWritten || (_pending.Count == 0) || _isDisposed)
					{
						_isFlushing = false;
						return;
					}
				}
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to write history." + Environment.NewLine
				+ ex);

			lock (_lock)
			{
				_isFlushing = false;
			}
		}
	}

	/// <summary>
	/// Waits for the background task, if running, to finish writing the rows appended so far.
	/// </summary>
	/// <remarks>
	/// This method can take as long as the time to wait for the database locked by another
	/// process and so must not be called on the UI thread.
	/// </remarks>
	internal void Flush()
	{
		try
		{
			Task task;
			lock (_lock)
			{
				task = _flushTask;
			}

			// This method may be called by a handler of the event raised by the task itself.
			if ((task is null) || (task.Id == Task.CurrentId) || Monitor.IsEntered(_dbLock))
				return;

			task.Wait();
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to wait for history to be written." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Writes the queued rows and renames to the database in a transaction.
	/// </summary>
	/// <param name="hasSignal">Whether signal samples are written</param>
	/// <param name="hasEvent">Whether switching events are written</param>
	/// <returns>True if nothing is left to be written, false if the rows are kept for the next attempt</returns>
	/// <remarks>This method must be called inside the lock of the database.</remarks>
	private bool WritePending(out bool hasSignal, out bool hasEvent)
	{
		hasSignal = false;
		hasEvent = false;

		object[] batch;
		lock (_lock)
		{
			if (_pending.Count == 0)
				return true;

			batch = _pending.ToArray();
		}

		try
		{
			// A rename alone does not create the database.
			var hasRows = batch.Any(x => x is not RenameOperation);

			var connection = GetConnection(hasRows);
			if (connection is not null)
			{
				using var transaction = connection.BeginTransaction();

				InsertRows(connection, transaction, batch);
				transaction.Commit();
			}
			else if (hasRows || _isClosed)
			{
				return false;
			}

			lock (_lock)
			{
				// Rows may have been queued, or dropped, in the meantime.
				var written = new HashSet<object>(batch);
				var rest = _pending.Where(x => !written.Contains(x)).ToArray();

				_pending.Clear();
				foreach (var row in rest)
					_pending.Enqueue(row);
			}

			_isLastWriteFailed = false;
			hasSignal = (connection is not null) && batch.Any(x => x is SignalSample);
			hasEvent = (connection is not null) && batch.Any(x => x is SwitchEvent);
			return true;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to write history database." + Environment.NewLine
				+ ex);

			_isLastWriteFailed = true;
			HandleFailure(ex);
			return false;
		}
	}

	private static void InsertRows(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<object> rows)
	{
		SqliteCommand signalCommand = null;
		SqliteCommand speedCommand = null;
		SqliteCommand eventCommand = null;

		try
		{
			foreach (var row in rows)
			{
				switch (row)
				{
					case SignalSample signal:
						signalCommand ??= CreateInsertCommand(connection, transaction, SignalTable, _signalColumns);
						Insert(signalCommand, GetValues(signal));
						break;

					case SpeedSample speed:
						speedCommand ??= CreateInsertCommand(connection, transaction, SpeedTable, _speedColumns);
						Insert(speedCommand, GetValues(speed));
						break;

					case SwitchEvent switchEvent:
						eventCommand ??= CreateInsertCommand(connection, transaction, EventTable, _eventColumns);
						Insert(eventCommand, GetValues(switchEvent));
						break;

					case RenameOperation rename:
						foreach (var table in new[] { SignalTable, SpeedTable })
						{
							Execute(connection, transaction,
								$"UPDATE {table} SET profile = $new WHERE profile = $old AND interface_id = $id;",
								("$new", rename.NewName),
								("$old", rename.OldName),
								("$id", rename.InterfaceId.ToString("D")));
						}
						break;
				}
			}
		}
		finally
		{
			signalCommand?.Dispose();
			speedCommand?.Dispose();
			eventCommand?.Dispose();
		}
	}

	private static SqliteCommand CreateInsertCommand(SqliteConnection connection, SqliteTransaction transaction, string table, string[] columns)
	{
		var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(x => "$" + x))});";

		foreach (var column in columns)
			command.Parameters.AddWithValue("$" + column, DBNull.Value);

		return command;
	}

	private static void Insert(SqliteCommand command, object[] values)
	{
		for (int i = 0; i < values.Length; i++)
			command.Parameters[i].Value = values[i] ?? DBNull.Value;

		command.ExecuteNonQuery();
	}

	private static object[] GetValues(SignalSample sample) => new object[]
	{
		CsvLine.FormatTime(sample.Time),
		sample.InterfaceId.ToString("D"),
		sample.Profile ?? string.Empty,
		sample.Connected ? 1 : 0,
		sample.Signal,
		sample.Rssi,
		ToReal(sample.Band, 3),
		sample.Channel,
		sample.Protocol ?? string.Empty,
		sample.Bssid ?? string.Empty,
		sample.LinkRate
	};

	private static object[] GetValues(SpeedSample sample) => new object[]
	{
		CsvLine.FormatTime(sample.Time),
		sample.InterfaceId.ToString("D"),
		sample.Profile ?? string.Empty,
		sample.Bssid ?? string.Empty,
		ToReal(sample.Band, 3),
		sample.Channel,
		sample.Signal,
		sample.LinkRate,
		ToReal(sample.GatewayMs),
		ToReal(sample.LatencyMs),
		ToReal(sample.JitterMs),
		ToReal(sample.DownMbps),
		ToReal(sample.UpMbps),
		sample.DownBytes,
		sample.UpBytes,
		sample.DurationMs,
		sample.Trigger.ToString(),
		sample.Result.ToString(),
		sample.Valid ? 1 : 0
	};

	private static object[] GetValues(SwitchEvent switchEvent) => new object[]
	{
		CsvLine.FormatTime(switchEvent.Time),
		switchEvent.InterfaceId.ToString("D"),
		switchEvent.Kind.ToString(),
		switchEvent.From ?? string.Empty,
		switchEvent.To ?? string.Empty,
		ToReal(switchEvent.FromScore),
		ToReal(switchEvent.ToScore),
		switchEvent.Reason ?? string.Empty
	};

	/// <summary>
	/// Converts a value to that of a REAL column, null if the value is not a finite number.
	/// </summary>
	private static object ToReal(double? value, int digits = 2)
	{
		if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
			return null;

		return Math.Round(value.Value, digits);
	}

	#endregion

	#region Read

	/// <summary>
	/// Reads signal samples within a range of time in order of time.
	/// </summary>
	/// <remarks>
	/// Read* methods wait for the rows appended so far to be written and take time in proportion
	/// to the number of rows, and so must not be called on the UI thread.
	/// </remarks>
	public IReadOnlyList<SignalSample> ReadSignals(DateTime fromUtc, DateTime toUtc)
	{
		try
		{
			Flush();

			return ReadRows(SignalTable, _signalColumns, fromUtc, toUtc, ReadSignal);
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
			Flush();

			return ReadRows(SpeedTable, _speedColumns, fromUtc, toUtc, ReadSpeed);
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
			Flush();

			return ReadRows(EventTable, _eventColumns, fromUtc, toUtc, ReadEvent);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to read event history." + Environment.NewLine
				+ ex);
			return Array.Empty<SwitchEvent>();
		}
	}

	/// <summary>
	/// Reads the rows of a table within a range of time in order of time.
	/// </summary>
	/// <remarks>
	/// This method uses a read-only connection of its own, which does not wait for the writer,
	/// and can throw an exception.
	/// </remarks>
	private List<T> ReadRows<T>(string table, string[] columns, DateTime fromUtc, DateTime toUtc, Func<SqliteDataReader, T> read) where T : class
	{
		var items = new List<T>();

		// The stored time has no fraction of a second and so the range is narrowed to whole seconds.
		fromUtc = CsvLine.ToUtc(fromUtc);
		toUtc = CsvLine.ToUtc(toUtc);

		var fraction = fromUtc.Ticks % TimeSpan.TicksPerSecond;
		if (fraction > 0)
		{
			if (fromUtc.Ticks - fraction > DateTime.MaxValue.Ticks - TimeSpan.TicksPerSecond)
				return items;

			fromUtc = new DateTime(fromUtc.Ticks - fraction + TimeSpan.TicksPerSecond, DateTimeKind.Utc);
		}

		if (toUtc < fromUtc)
			return items;

		if (_isDisposed || !File.Exists(_filePath))
			return items;

		using var connection = new SqliteConnection(GetConnectionString(SqliteOpenMode.ReadOnly));
		connection.Open();

		// The rows of the same time are in order of insertion.
		using var command = CreateCommand(connection, null,
			$"SELECT {string.Join(", ", columns)} FROM {table} WHERE time >= $from AND time <= $to ORDER BY time, id;",
			("$from", CsvLine.FormatTime(fromUtc)),
			("$to", CsvLine.FormatTime(toUtc)));

		using var reader = command.ExecuteReader();
		while (reader.Read())
		{
			var item = read(reader);
			if (item is not null)
				items.Add(item);
		}
		return items;
	}

	private static SignalSample ReadSignal(SqliteDataReader reader)
	{
		if (!CsvLine.TryParseTime(GetString(reader, 0), out DateTime time) ||
			!Guid.TryParse(GetString(reader, 1), out Guid interfaceId))
			return null;

		return new SignalSample
		{
			Time = time,
			InterfaceId = interfaceId,
			Profile = GetString(reader, 2),
			Connected = (GetLong(reader, 3) ?? 0L) != 0L,
			Signal = (int)(GetLong(reader, 4) ?? 0L),
			Rssi = (int?)GetLong(reader, 5),
			Band = (float)(GetDouble(reader, 6) ?? 0D),
			Channel = (int)(GetLong(reader, 7) ?? 0L),
			Protocol = GetString(reader, 8),
			Bssid = GetString(reader, 9),
			LinkRate = (int?)GetLong(reader, 10)
		};
	}

	private static SpeedSample ReadSpeed(SqliteDataReader reader)
	{
		if (!CsvLine.TryParseTime(GetString(reader, 0), out DateTime time) ||
			!Guid.TryParse(GetString(reader, 1), out Guid interfaceId))
			return null;

		// An unknown trigger or result is tolerated. An unknown result is never regarded as valid.
		if (!SpeedSample.TryParseEnum(GetString(reader, 16), out SpeedTrigger trigger))
			trigger = default;

		if (!SpeedSample.TryParseEnum(GetString(reader, 17), out SpeedResult result))
			result = SpeedResult.None;

		return new SpeedSample
		{
			Time = time,
			InterfaceId = interfaceId,
			Profile = GetString(reader, 2),
			Bssid = GetString(reader, 3),
			Band = (float)(GetDouble(reader, 4) ?? 0D),
			Channel = (int)(GetLong(reader, 5) ?? 0L),
			Signal = (int)(GetLong(reader, 6) ?? 0L),
			LinkRate = (int?)GetLong(reader, 7),
			GatewayMs = GetDouble(reader, 8),
			LatencyMs = GetDouble(reader, 9),
			JitterMs = GetDouble(reader, 10),
			DownMbps = GetDouble(reader, 11),
			UpMbps = GetDouble(reader, 12),
			DownBytes = GetLong(reader, 13) ?? 0L,
			UpBytes = GetLong(reader, 14) ?? 0L,
			DurationMs = (int)(GetLong(reader, 15) ?? 0L),
			Trigger = trigger,
			Result = result
		};
	}

	private static SwitchEvent ReadEvent(SqliteDataReader reader)
	{
		if (!CsvLine.TryParseTime(GetString(reader, 0), out DateTime time) ||
			!Guid.TryParse(GetString(reader, 1), out Guid interfaceId) ||
			!SpeedSample.TryParseEnum(GetString(reader, 2), out SwitchKind kind))
			return null;

		return new SwitchEvent
		{
			Time = time,
			InterfaceId = interfaceId,
			Kind = kind,
			From = GetString(reader, 3),
			To = GetString(reader, 4),
			FromScore = GetDouble(reader, 5),
			ToScore = GetDouble(reader, 6),
			Reason = GetString(reader, 7)
		};
	}

	private static string GetString(SqliteDataReader reader, int ordinal) =>
		reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

	private static long? GetLong(SqliteDataReader reader, int ordinal) =>
		reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

	private static double? GetDouble(SqliteDataReader reader, int ordinal) =>
		reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

	#endregion

	#region Speed

	/// <summary>
	/// Loads speed samples within the period of summary from the database.
	/// </summary>
	/// <remarks>
	/// This method must be called while nothing is being written, that is, inside the lock of
	/// the database or by the constructor, and can throw an exception.
	/// </remarks>
	private void LoadSpeeds()
	{
		var now = GetNow();
		var samples = ReadRows(SpeedTable, _speedColumns, now - SpeedSummary.Period, now.AddDays(1), ReadSpeed);

		lock (_lock)
		{
			_speeds.Clear();

			// The samples which have not been written yet follow those in the database.
			foreach (var sample in samples.Concat(_pending.OfType<SpeedSample>()))
				HoldSpeed(sample);
		}
	}

	private void HoldSpeed(SpeedSample sample)
	{
		var key = sample.Key;
		if (!_speeds.TryGetValue(key, out List<SpeedSample> samples))
		{
			samples = new List<SpeedSample>();
			_speeds.Add(key, samples);
		}
		samples.Add(sample);
	}

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
	/// Moves the history of a profile to a new key.
	/// </summary>
	/// <param name="oldKey">Old profile key</param>
	/// <param name="newKey">New profile key</param>
	/// <remarks>
	/// Signal samples and speed samples in the database are moved, in the background, as well
	/// as those held in memory. Switching events are left as they were recorded.
	/// </remarks>
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

				if (!TrySplitKey(oldKey, out string oldName, out Guid oldId) ||
					!TrySplitKey(newKey, out string newName, out Guid newId) ||
					(oldId != newId))
					return;

				// The samples held in memory and waiting to be written are the same instances.
				foreach (var sample in (samples ?? Enumerable.Empty<SpeedSample>()).Where(x => x.Profile == oldName))
					sample.Profile = newName;

				foreach (var sample in _pending.OfType<SignalSample>().Where(x => (x.InterfaceId == oldId) && (x.Profile == oldName)))
					sample.Profile = newName;

				if (_isDisposed)
					return;

				// The rows in the database are moved after the rows queued before are written.
				_pending.Enqueue(new RenameOperation { InterfaceId = oldId, OldName = oldName, NewName = newName });
				ScheduleFlush();
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
	/// Deletes rows older than retention days and, if the freed space is considerable, rebuilds
	/// the database to give the space back to the disk.
	/// </summary>
	/// <param name="retentionDays">Retention days</param>
	/// <remarks>This method takes time and so must not be called on the UI thread.</remarks>
	public void Purge(int retentionDays)
	{
		try
		{
			if (retentionDays < 1)
				return;

			var oldest = CsvLine.FormatTime(GetNow().Date.AddDays(-retentionDays));

			Flush();

			lock (_dbLock)
			{
				var connection = GetConnection(false);
				if (connection is null)
					return;

				using (var transaction = connection.BeginTransaction())
				{
					foreach (var table in new[] { SignalTable, SpeedTable, EventTable })
						Execute(connection, transaction, $"DELETE FROM {table} WHERE time < $oldest;", ("$oldest", oldest));

					transaction.Commit();
				}

				// Deleting rows leaves free pages in the file, which never shrinks by itself.
				var pageCount = Convert.ToInt64(ExecuteScalar(connection, null, "PRAGMA page_count;"), CultureInfo.InvariantCulture);
				var freeCount = Convert.ToInt64(ExecuteScalar(connection, null, "PRAGMA freelist_count;"), CultureInfo.InvariantCulture);

				if ((freeCount >= VacuumMinPageCount) && (freeCount >= pageCount * VacuumMinRatio))
				{
					try
					{
						Execute(connection, null, "VACUUM;");
					}
					catch (SqliteException ex) when (!IsCorrupt(ex))
					{
						// It will be tried again when purging next time.
						Trace.WriteLine("Failed to rebuild history database." + Environment.NewLine
							+ ex);
					}
				}

				// Let the file of write-ahead log start over.
				Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to purge history." + Environment.NewLine
				+ ex);

			HandleFailure(ex);
		}
	}

	#endregion

	#region Import/Export

	private delegate bool TryParser<T>(IReadOnlyDictionary<string, int> columns, string[] fields, out T item);

	/// <summary>
	/// Imports the daily CSV files written by previous versions (signal-yyyyMMdd.csv,
	/// speed-yyyyMMdd.csv, events-yyyyMMdd.csv) into the database, once, and then renames the folder.
	/// </summary>
	/// <param name="folderPath">Folder path of CSV files</param>
	/// <returns>Number of imported rows</returns>
	/// <remarks>
	/// All the rows are imported in a transaction which records the completion as well, and so
	/// the files are never imported twice even if this method is interrupted or called again.
	/// The folder is renamed (not deleted) to that with "-csv-imported" suffix.
	/// The files are read before the lock of the database is taken, and that lock is never
	/// taken by the methods called on the UI thread.
	/// This method takes time and so must not be called on the UI thread.
	/// </remarks>
	public int ImportCsv(string folderPath)
	{
		int count = 0;

		try
		{
			if (string.IsNullOrEmpty(folderPath))
				return 0;

			// The database may be about to be created by the rows appended so far.
			Flush();

			string state;
			lock (_dbLock)
			{
				var connection = GetConnection(false);
				state = (connection is not null) ? GetMeta(connection, ImportKey) : null;
			}

			if (state == ImportDone)
				return 0;

			if (state != ImportImported)
			{
				var rows = new List<object>();
				rows.AddRange(ReadCsvFiles<SignalSample>(folderPath, "signal", SignalSample.TryParse));
				rows.AddRange(ReadCsvFiles<SpeedSample>(folderPath, "speed", SpeedSample.TryParse));
				rows.AddRange(ReadCsvFiles<SwitchEvent>(folderPath, "events", SwitchEvent.TryParse));

				lock (_dbLock)
				{
					// Nothing is created if there is nothing to be imported.
					var connection = GetConnection(rows.Count > 0);
					if (connection is null)
						return 0;

					if (GetMeta(connection, ImportKey) is not null) // Imported by another call in the meantime
						return 0;

					using (var transaction = connection.BeginTransaction())
					{
						InsertRows(connection, transaction, rows);
						SetMeta(connection, transaction, ImportKey, (rows.Count > 0) ? ImportImported : ImportDone);
						transaction.Commit();
					}

					count = rows.Count;
					if (count == 0)
						return 0;

					LoadSpeeds();
				}
			}

			if (RenameImportedFolder(folderPath))
			{
				lock (_dbLock)
				{
					var connection = GetConnection(false);
					if (connection is not null)
						SetMeta(connection, null, ImportKey, ImportDone);
				}
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to import history files." + Environment.NewLine
				+ ex);

			HandleFailure(ex);
		}

		if (count > 0)
		{
			OnChanged(HistoryKind.Signal);
			OnChanged(HistoryKind.Speed);
			OnChanged(HistoryKind.Event);
		}
		return count;
	}

	/// <summary>
	/// Renames the folder of imported CSV files.
	/// </summary>
	/// <returns>True if the folder is renamed or does not exist</returns>
	private static bool RenameImportedFolder(string folderPath)
	{
		try
		{
			folderPath = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (!Directory.Exists(folderPath))
				return true;

			var newPath = folderPath + ImportedSuffix;
			for (int i = 2; Directory.Exists(newPath) || File.Exists(newPath); i++)
				newPath = folderPath + ImportedSuffix + "-" + i.ToString(CultureInfo.InvariantCulture);

			Directory.Move(folderPath, newPath);
			return true;
		}
		catch (Exception ex)
		{
			// It will be tried again when this application starts next time.
			Trace.WriteLine("Failed to rename folder of imported history files." + Environment.NewLine
				+ ex);
			return false;
		}
	}

	private static List<T> ReadCsvFiles<T>(string folderPath, string prefix, TryParser<T> tryParse)
	{
		var items = new List<T>();

		foreach (var filePath in EnumerateCsvFiles(folderPath, prefix).OrderBy(x => x.date).Select(x => x.filePath))
		{
			try
			{
				ReadCsvFile(filePath, tryParse, items);
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to read history file." + Environment.NewLine
					+ ex);
			}
		}
		return items;
	}

	/// <summary>
	/// Reads a CSV file by its header skipping the lines which cannot be parsed, such as the last
	/// line which was cut off.
	/// </summary>
	private static void ReadCsvFile<T>(string filePath, TryParser<T> tryParse, List<T> items)
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

			if (tryParse(columns, fields, out T item))
				items.Add(item);
		}
	}

	private static IEnumerable<(string filePath, DateTime date)> EnumerateCsvFiles(string folderPath, string prefix)
	{
		if (!Directory.Exists(folderPath))
			yield break;

		foreach (var filePath in Directory.EnumerateFiles(folderPath, prefix + "-*.csv"))
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

	/// <summary>
	/// Exports the rows within a range of time to three CSV files (signal, speed and events).
	/// </summary>
	/// <param name="folderPath">Folder path to export to</param>
	/// <param name="fromUtc">Start of the range (UTC)</param>
	/// <param name="toUtc">End of the range (UTC)</param>
	/// <returns>File paths of exported files, empty if failed</returns>
	/// <remarks>
	/// The file names (export-yyyyMMdd-HHmmss-signal.csv and so on, in local time) never match
	/// those of the files to be imported.
	/// </remarks>
	public IReadOnlyList<string> ExportCsv(string folderPath, DateTime fromUtc, DateTime toUtc)
	{
		try
		{
			var signals = ReadSignals(fromUtc, toUtc);
			var speeds = ReadSpeeds(fromUtc, toUtc);
			var events = ReadEvents(fromUtc, toUtc);

			Directory.CreateDirectory(folderPath);

			var stamp = GetNow().ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			string GetFilePath(string name) => Path.Combine(folderPath, $"export-{stamp}-{name}.csv");

			var filePaths = new[] { GetFilePath("signal"), GetFilePath("speed"), GetFilePath("events") };

			WriteCsvFile(filePaths[0], SignalSample.Header, signals.Select(x => x.ToCsv()));
			WriteCsvFile(filePaths[1], SpeedSample.Header, speeds.Select(x => x.ToCsv()));
			WriteCsvFile(filePaths[2], SwitchEvent.Header, events.Select(x => x.ToCsv()));

			return filePaths;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to export history files." + Environment.NewLine
				+ ex);
			return Array.Empty<string>();
		}
	}

	private static void WriteCsvFile(string filePath, string header, IEnumerable<string> lines)
	{
		// The byte order mark lets spreadsheet applications read names in UTF-8.
		using var sw = new StreamWriter(filePath, false, new UTF8Encoding(true));
		sw.NewLine = "\r\n";

		sw.WriteLine(header);
		foreach (var line in lines)
			sw.WriteLine(line);
	}

	#endregion

	private DateTime GetNow() => CsvLine.ToUtc(_utcNow());

	#region IDisposable

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!disposing)
			return;

		try
		{
			Task task;
			lock (_lock)
			{
				if (_isDisposed)
					return;

				// Nothing is appended or read from now on, and so nothing opens the database again.
				_isDisposed = true;
				task = _flushTask;
			}

			Changed = null;

			var isFlushing = task is { IsCompleted: false };
			task?.Wait();

			lock (_dbLock)
			{
				// Try to write the rows which have not been written, unless it failed just now.
				if (!(isFlushing && _isLastWriteFailed))
					WritePending(out _, out _);

				// The write-ahead log is merged into the database when the last connection is closed.
				CloseConnection();
				_isClosed = true;
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to dispose history." + Environment.NewLine
				+ ex);

			lock (_dbLock)
			{
				CloseConnection();
				_isClosed = true;
			}
		}
	}

	#endregion
}

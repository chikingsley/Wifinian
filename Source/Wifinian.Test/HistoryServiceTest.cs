using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models.History;

namespace Wifinian.Test;

[TestClass]
public class HistoryServiceTest
{
	private const string TrickyName = "Café, \"Guest\" 5G";

	private static readonly Guid _interfaceId = new Guid("11111111-2222-3333-4444-555555555555");
	private static readonly DateTime _start = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);

	private string _folderPath;
	private string _filePath;
	private string _csvFolderPath;
	private DateTime _now;

	[TestInitialize]
	public void Initialize()
	{
		_folderPath = Path.Combine(Path.GetTempPath(), "WifinianTest-" + Guid.NewGuid().ToString("N"));
		_filePath = Path.Combine(_folderPath, "history.db");
		_csvFolderPath = Path.Combine(_folderPath, "history");
		_now = _start;
	}

	[TestCleanup]
	public void Cleanup()
	{
		try
		{
			if (Directory.Exists(_folderPath))
				Directory.Delete(_folderPath, true);
		}
		catch (IOException)
		{
		}
	}

	#region CsvLine

	[TestMethod]
	public void TestCsvLineEscapeAndSplit()
	{
		Assert.AreEqual("", CsvLine.Escape(null));
		Assert.AreEqual("abc", CsvLine.Escape("abc"));
		Assert.AreEqual("\"a,b\"", CsvLine.Escape("a,b"));
		Assert.AreEqual("\"a\"\"b\"", CsvLine.Escape("a\"b"));

		// Line breaks are replaced because a row must not span multiple lines.
		Assert.AreEqual("a b", CsvLine.Escape("a\r\nb"));
		Assert.AreEqual("a b c", CsvLine.Escape("a\rb\nc"));
		Assert.AreEqual("\"a, b\"", CsvLine.Escape("a,\nb"));
		Assert.AreEqual("x,a b", CsvLine.Join("x", "a\r\nb"));

		var fields = new[] { "plain", "", "a,b", "say \"hi\"", " spaced ", TrickyName };
		var line = CsvLine.Join(fields);
		CollectionAssert.AreEqual(fields, CsvLine.Split(line));

		CollectionAssert.AreEqual(new[] { "" }, CsvLine.Split(""));
		CollectionAssert.AreEqual(new[] { "a", "", "" }, CsvLine.Split("a,,"));
		CollectionAssert.AreEqual(new[] { "a", "b,c", "d" }, CsvLine.Split("a,\"b,c\",d"));

		Assert.IsNull(CsvLine.Split(null));
		Assert.IsNull(CsvLine.Split("a,\"b,c"));
		Assert.IsNull(CsvLine.Split("a,\"b\"\""));
	}

	[TestMethod]
	public void TestCsvLineNumbersAndTime()
	{
		Assert.AreEqual("12.3", CsvLine.Format((double?)12.34));
		Assert.AreEqual("0.0", CsvLine.Format((double?)0));
		Assert.AreEqual("", CsvLine.Format((double?)null));
		Assert.AreEqual("-67", CsvLine.Format((int?)-67));
		Assert.AreEqual("", CsvLine.Format((int?)null));

		Assert.AreEqual(12.3, CsvLine.ParseDouble("12.3"));
		Assert.IsNull(CsvLine.ParseDouble(""));
		Assert.IsNull(CsvLine.ParseDouble("abc"));
		Assert.AreEqual(-67, CsvLine.ParseInt("-67"));
		Assert.IsNull(CsvLine.ParseInt(""));
		Assert.IsNull(CsvLine.ParseInt("1.5"));

		var time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		Assert.AreEqual("2026-01-02T03:04:05Z", CsvLine.FormatTime(time));

		Assert.IsTrue(CsvLine.TryParseTime("2026-01-02T03:04:05Z", out DateTime parsed));
		Assert.AreEqual(time, parsed);
		Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);

		Assert.IsFalse(CsvLine.TryParseTime("", out _));
		Assert.IsFalse(CsvLine.TryParseTime("2026-01-02T03:0", out _));
	}

	#endregion

	#region Round trip

	[TestMethod]
	public void TestSignalSampleRoundTrip()
	{
		var source = new SignalSample
		{
			Time = _start,
			InterfaceId = _interfaceId,
			Profile = TrickyName,
			Connected = true,
			Signal = 53,
			Rssi = -67,
			Band = 2.4F,
			Channel = 11,
			Protocol = "ax",
			Bssid = "AA:BB:CC:DD:EE:FF",
			LinkRate = 286
		};

		var columns = CsvLine.GetColumns(SignalSample.Header);
		Assert.IsTrue(SignalSample.TryParse(columns, CsvLine.Split(source.ToCsv()), out SignalSample result));

		Assert.AreEqual(source.Time, result.Time);
		Assert.AreEqual(source.InterfaceId, result.InterfaceId);
		Assert.AreEqual(source.Profile, result.Profile);
		Assert.AreEqual(source.Connected, result.Connected);
		Assert.AreEqual(source.Signal, result.Signal);
		Assert.AreEqual(source.Rssi, result.Rssi);
		Assert.AreEqual(source.Band, result.Band);
		Assert.AreEqual(source.Channel, result.Channel);
		Assert.AreEqual(source.Protocol, result.Protocol);
		Assert.AreEqual(source.Bssid, result.Bssid);
		Assert.AreEqual(source.LinkRate, result.LinkRate);
		Assert.AreEqual(source.Key, result.Key);
		Assert.AreEqual(TrickyName + "|11111111-2222-3333-4444-555555555555", result.Key);

		// Unknown values
		source.Connected = false;
		source.Rssi = null;
		source.LinkRate = null;
		source.Bssid = null;

		Assert.IsTrue(SignalSample.TryParse(columns, CsvLine.Split(source.ToCsv()), out result));
		Assert.IsFalse(result.Connected);
		Assert.IsNull(result.Rssi);
		Assert.IsNull(result.LinkRate);
		Assert.AreEqual("", result.Bssid);
	}

	[TestMethod]
	public void TestSpeedSampleRoundTrip()
	{
		var source = CreateSpeed(_start, TrickyName, SpeedResult.Ok, 77.4);

		var columns = CsvLine.GetColumns(SpeedSample.Header);
		var fields = CsvLine.Split(source.ToCsv());
		Assert.AreEqual(columns.Count, fields.Length);
		Assert.AreEqual("1", fields[columns["Valid"]]);

		Assert.IsTrue(SpeedSample.TryParse(columns, fields, out SpeedSample result));

		Assert.AreEqual(source.Time, result.Time);
		Assert.AreEqual(source.InterfaceId, result.InterfaceId);
		Assert.AreEqual(source.Profile, result.Profile);
		Assert.AreEqual(source.Bssid, result.Bssid);
		Assert.AreEqual(source.Band, result.Band);
		Assert.AreEqual(source.Channel, result.Channel);
		Assert.AreEqual(source.Signal, result.Signal);
		Assert.AreEqual(source.LinkRate, result.LinkRate);
		Assert.AreEqual(source.GatewayMs, result.GatewayMs);
		Assert.AreEqual(source.LatencyMs, result.LatencyMs);
		Assert.AreEqual(source.JitterMs, result.JitterMs);
		Assert.AreEqual(source.DownMbps, result.DownMbps);
		Assert.AreEqual(source.UpMbps, result.UpMbps);
		Assert.AreEqual(source.DownBytes, result.DownBytes);
		Assert.AreEqual(source.UpBytes, result.UpBytes);
		Assert.AreEqual(source.DurationMs, result.DurationMs);
		Assert.AreEqual(source.Trigger, result.Trigger);
		Assert.AreEqual(source.Result, result.Result);
		Assert.IsTrue(result.Valid);
		Assert.AreEqual(source.Key, result.Key);

		// Valid is computed from the result and the column is not trusted.
		source.Result = SpeedResult.RouteMismatch;
		source.UpMbps = null;
		fields = CsvLine.Split(source.ToCsv());
		Assert.AreEqual("0", fields[columns["Valid"]]);
		fields[columns["Valid"]] = "1";

		Assert.IsTrue(SpeedSample.TryParse(columns, fields, out result));
		Assert.AreEqual(SpeedResult.RouteMismatch, result.Result);
		Assert.IsFalse(result.Valid);
		Assert.IsNull(result.UpMbps);
	}

	[TestMethod]
	public void TestSwitchEventRoundTrip()
	{
		var source = new SwitchEvent
		{
			Time = _start,
			InterfaceId = _interfaceId,
			Kind = SwitchKind.WouldSwitch,
			From = TrickyName,
			To = "EXT",
			FromScore = 8.1,
			ToScore = null,
			Reason = "Switched \"EXT2\" -> EXT: about 77 Mbps vs 8 Mbps, as measured"
		};

		var columns = CsvLine.GetColumns(SwitchEvent.Header);
		Assert.IsTrue(SwitchEvent.TryParse(columns, CsvLine.Split(source.ToCsv()), out SwitchEvent result));

		Assert.AreEqual(source.Time, result.Time);
		Assert.AreEqual(source.InterfaceId, result.InterfaceId);
		Assert.AreEqual(source.Kind, result.Kind);
		Assert.AreEqual(source.From, result.From);
		Assert.AreEqual(source.To, result.To);
		Assert.AreEqual(source.FromScore, result.FromScore);
		Assert.IsNull(result.ToScore);
		Assert.AreEqual(source.Reason, result.Reason);
	}

	[TestMethod]
	public void TestTryParseHeaderDriven()
	{
		// Reordered columns with an unknown column and without optional columns
		var columns = CsvLine.GetColumns("Result,Extra,Profile,InterfaceId,Time,DownMbps");
		var fields = new[] { "NoInternet", "?", "Office", _interfaceId.ToString(), "2026-11-17T10:00:00Z", "0.0" };

		Assert.IsTrue(SpeedSample.TryParse(columns, fields, out SpeedSample speed));
		Assert.AreEqual("Office", speed.Profile);
		Assert.AreEqual(_start, speed.Time);
		Assert.AreEqual(SpeedResult.NoInternet, speed.Result);
		Assert.AreEqual(0D, speed.DownMbps);
		Assert.IsNull(speed.UpMbps);
		Assert.IsNull(speed.LinkRate);
		Assert.AreEqual("", speed.Bssid);
		Assert.AreEqual(0L, speed.DownBytes);

		// An unknown result is tolerated but is not valid.
		fields[0] = "Brilliant";
		Assert.IsTrue(SpeedSample.TryParse(columns, fields, out speed));
		Assert.AreEqual(SpeedResult.None, speed.Result);
		Assert.IsFalse(speed.Valid);

		// Mandatory fields
		Assert.IsFalse(SpeedSample.TryParse(columns, new[] { "Ok", "?", "", _interfaceId.ToString(), "2026-11-17T10:00:00Z", "1.0" }, out _));
		Assert.IsFalse(SpeedSample.TryParse(columns, new[] { "Ok", "?", "Office", "1111", "2026-11-17T10:00:00Z", "1.0" }, out _));
		Assert.IsFalse(SpeedSample.TryParse(columns, new[] { "Ok", "?", "Office", _interfaceId.ToString(), "yesterday", "1.0" }, out _));
		Assert.IsFalse(SpeedSample.TryParse(columns, new[] { "Ok", "?", "Office" }, out _));
		Assert.IsFalse(SpeedSample.TryParse(CsvLine.GetColumns("Time,InterfaceId"), new[] { "2026-11-17T10:00:00Z", _interfaceId.ToString() }, out _));

		var signalColumns = CsvLine.GetColumns("Time,InterfaceId,Profile,Signal");
		Assert.IsTrue(SignalSample.TryParse(signalColumns, new[] { "2026-11-17T10:00:00Z", _interfaceId.ToString(), "Office", "44" }, out SignalSample signal));
		Assert.AreEqual(44, signal.Signal);
		Assert.IsFalse(signal.Connected);
		Assert.IsFalse(SignalSample.TryParse(signalColumns, new[] { "2026-11-17T10:00:00Z", _interfaceId.ToString() }, out _));

		var eventColumns = CsvLine.GetColumns("Time,InterfaceId,Kind,Reason");
		Assert.IsTrue(SwitchEvent.TryParse(eventColumns, new[] { "2026-11-17T10:00:00Z", _interfaceId.ToString(), "Stay", "Because" }, out SwitchEvent switchEvent));
		Assert.AreEqual(SwitchKind.Stay, switchEvent.Kind);
		Assert.AreEqual("", switchEvent.From);
		Assert.IsFalse(SwitchEvent.TryParse(eventColumns, new[] { "2026-11-17T10:00:00Z", _interfaceId.ToString(), "Jump", "Because" }, out _));
		Assert.IsFalse(SwitchEvent.TryParse(eventColumns, new[] { "2026-11-17T10:00:00Z", _interfaceId.ToString(), "3", "Because" }, out _));
	}

	#endregion

	#region Signal

	[TestMethod]
	public void TestAppendSignalsReducesWrites()
	{
		using var history = CreateHistory();

		int changedCount = 0;
		history.Changed += (_, kind) =>
		{
			Assert.AreEqual(HistoryKind.Signal, kind);
			changedCount++;
		};

		history.AppendSignals(new[] { CreateSignal(_start, "Office", 50) }); // Written (first)
		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(1), "Office", 50) }); // Same value
		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(2), "Office", 52) }); // Change of 2
		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(4), "Office", 48) }); // Change of 2 from the last written
		Assert.AreEqual(1, ReadAllSignals(history).Count);
		Assert.AreEqual(1, changedCount);

		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(4.5), "Office", 53) }); // Written (change of 3)
		Assert.AreEqual(2, ReadAllSignals(history).Count);

		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(9), "Office", 53) }); // Same value within 5 minutes
		Assert.AreEqual(2, ReadAllSignals(history).Count);

		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(9.5), "Office", 53) }); // Written (5 minutes)
		Assert.AreEqual(3, ReadAllSignals(history).Count);

		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(10.5), "Office", 53, channel: 36) }); // Written (channel)
		Assert.AreEqual(4, ReadAllSignals(history).Count);

		// Another profile and another interface have their own last samples.
		history.AppendSignals(new[]
		{
			CreateSignal(_start.AddMinutes(11), "Office", 53, channel: 36),
			CreateSignal(_start.AddMinutes(11), "EXT", 53, channel: 36),
			CreateSignal(_start.AddMinutes(11), "Office", 53, channel: 36, interfaceId: Guid.NewGuid())
		});

		var signals = ReadAllSignals(history);
		Assert.AreEqual(6, signals.Count);
		CollectionAssert.AreEqual(new[] { 50, 53, 53, 53, 53, 53 }, signals.Select(x => x.Signal).ToArray());
		Assert.AreEqual(4, signals.Count(x => x.Key == HistoryService.GetKey("Office", _interfaceId)));
		Assert.AreEqual(5, changedCount);
	}

	[TestMethod]
	public void TestAppendSignalsMinimumInterval()
	{
		using var history = CreateHistory();

		history.AppendSignals(new[] { CreateSignal(_start, "Office", 50) }); // Written (first)
		history.AppendSignals(new[] { CreateSignal(_start.AddSeconds(10), "Office", 70) }); // Large change within 30 seconds
		Assert.AreEqual(1, ReadAllSignals(history).Count);

		history.AppendSignals(new[] { CreateSignal(_start.AddSeconds(15), "Office", 70, connected: true) }); // Written (connected)
		Assert.AreEqual(2, ReadAllSignals(history).Count);

		history.AppendSignals(new[] { CreateSignal(_start.AddSeconds(20), "Office", 70, connected: true, bssid: "AA:BB:CC:00:00:02") }); // Written (BSSID)
		Assert.AreEqual(3, ReadAllSignals(history).Count);

		history.AppendSignals(new[] { CreateSignal(_start.AddSeconds(25), "Office", 70, connected: true, bssid: "AA:BB:CC:00:00:02", channel: 36) }); // Channel within 30 seconds
		history.AppendSignals(new[] { CreateSignal(_start.AddSeconds(40), "Office", 60, connected: true, bssid: "AA:BB:CC:00:00:02") }); // Large change within 30 seconds
		Assert.AreEqual(3, ReadAllSignals(history).Count);

		history.AppendSignals(new[] { CreateSignal(_start.AddSeconds(50), "Office", 60, connected: true, bssid: "AA:BB:CC:00:00:02") }); // Written (change after 30 seconds)
		Assert.AreEqual(4, ReadAllSignals(history).Count);
	}

	[TestMethod]
	public void TestShouldWriteBssidChange()
	{
		var last = CreateSignal(_start, "Office", 50);

		// BSSID of a network which is not connected obeys the minimum interval.
		Assert.IsFalse(HistoryService.ShouldWrite(last, CreateSignal(_start.AddSeconds(10), "Office", 50, bssid: "AA:BB:CC:00:00:02")));
		Assert.IsFalse(HistoryService.ShouldWrite(last, CreateSignal(_start.AddSeconds(40), "Office", 50, bssid: "AA:BB:CC:00:00:02")));
		Assert.IsTrue(HistoryService.ShouldWrite(last, CreateSignal(_start.AddSeconds(40), "Office", 53, bssid: "AA:BB:CC:00:00:02")));

		// BSSID of a connected network is written immediately.
		last = CreateSignal(_start, "Office", 50, connected: true);
		Assert.IsFalse(HistoryService.ShouldWrite(last, CreateSignal(_start.AddSeconds(10), "Office", 50, connected: true, bssid: "aa:bb:cc:00:00:01")));
		Assert.IsTrue(HistoryService.ShouldWrite(last, CreateSignal(_start.AddSeconds(10), "Office", 50, connected: true, bssid: "AA:BB:CC:00:00:02")));
	}

	[TestMethod]
	public void TestRenameKeepsNewerLastSignal()
	{
		using var history = CreateHistory();

		var oldKey = HistoryService.GetKey("Office", _interfaceId);
		var newKey = HistoryService.GetKey("Office 2", _interfaceId);

		history.AppendSignals(new[] { CreateSignal(_start, "Office", 50) }); // Written (first)
		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(4), "Office 2", 50) }); // Written (first of new name)
		history.Rename(oldKey, newKey);

		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(6), "Office 2", 50) }); // Same value within 5 minutes from the newer
		Assert.AreEqual(2, ReadAllSignals(history).Count);

		// The last sample of old name is taken over when the new name has none.
		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(7), "Home", 50) }); // Written (first)
		history.Rename(HistoryService.GetKey("Home", _interfaceId), HistoryService.GetKey("Home 2", _interfaceId));
		history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(8), "Home 2", 50) }); // Same value
		Assert.AreEqual(3, ReadAllSignals(history).Count);
	}

	#endregion

	#region Database

	[TestMethod]
	public void TestDatabaseRoundTrip()
	{
		var beforeMidnight = new DateTime(2026, 11, 17, 23, 59, 30, DateTimeKind.Utc);
		var afterMidnight = new DateTime(2026, 11, 18, 0, 0, 30, DateTimeKind.Utc);

		var signal = CreateSignal(beforeMidnight, TrickyName, 50, connected: true);
		var speed = CreateSpeed(beforeMidnight, TrickyName, SpeedResult.Ok, 70.25);
		var switchEvent = new SwitchEvent { Time = beforeMidnight, InterfaceId = _interfaceId, Kind = SwitchKind.WouldSwitch, From = TrickyName, To = "EXT", FromScore = 8.1, ToScore = null, Reason = "a, \"b\"\r\nc" };

		using (var history = CreateHistory())
		{
			history.AppendSignals(new[] { signal });
			history.AppendSignals(new[] { CreateSignal(afterMidnight, "Office", 60) });
			history.AppendSpeed(speed);
			history.AppendSpeed(new SpeedSample { Time = afterMidnight, InterfaceId = _interfaceId, Profile = "Office", Result = SpeedResult.Error, Trigger = SpeedTrigger.Manual, DownMbps = double.NaN });
			history.AppendEvent(switchEvent);
			history.AppendEvent(CreateEvent(afterMidnight, "second"));
		}

		Assert.IsTrue(File.Exists(_filePath));

		// The write-ahead log is merged when the database is closed.
		Assert.IsFalse(File.Exists(_filePath + "-wal"));

		using (var history = CreateHistory())
		{
			var signals = history.ReadSignals(DateTime.MinValue, DateTime.MaxValue);
			Assert.AreEqual(2, signals.Count);
			Assert.AreEqual(signal.Time, signals[0].Time);
			Assert.AreEqual(DateTimeKind.Utc, signals[0].Time.Kind);
			Assert.AreEqual(signal.InterfaceId, signals[0].InterfaceId);
			Assert.AreEqual(signal.Profile, signals[0].Profile);
			Assert.AreEqual(signal.Connected, signals[0].Connected);
			Assert.AreEqual(signal.Signal, signals[0].Signal);
			Assert.AreEqual(signal.Rssi, signals[0].Rssi);
			Assert.AreEqual(signal.Band, signals[0].Band);
			Assert.AreEqual(signal.Channel, signals[0].Channel);
			Assert.AreEqual(signal.Protocol, signals[0].Protocol);
			Assert.AreEqual(signal.Bssid, signals[0].Bssid);
			Assert.AreEqual(signal.LinkRate, signals[0].LinkRate);
			Assert.AreEqual(signal.Key, signals[0].Key);
			Assert.IsFalse(signals[1].Connected);
			Assert.IsNull(signals[1].LinkRate);

			var speeds = history.ReadSpeeds(DateTime.MinValue, DateTime.MaxValue);
			Assert.AreEqual(2, speeds.Count);
			Assert.AreEqual(speed.Time, speeds[0].Time);
			Assert.AreEqual(speed.InterfaceId, speeds[0].InterfaceId);
			Assert.AreEqual(speed.Profile, speeds[0].Profile);
			Assert.AreEqual(speed.Bssid, speeds[0].Bssid);
			Assert.AreEqual(speed.Band, speeds[0].Band);
			Assert.AreEqual(speed.Channel, speeds[0].Channel);
			Assert.AreEqual(speed.Signal, speeds[0].Signal);
			Assert.AreEqual(speed.LinkRate, speeds[0].LinkRate);
			Assert.AreEqual(speed.GatewayMs, speeds[0].GatewayMs);
			Assert.AreEqual(speed.LatencyMs, speeds[0].LatencyMs);
			Assert.AreEqual(speed.JitterMs, speeds[0].JitterMs);
			Assert.AreEqual(speed.DownMbps, speeds[0].DownMbps);
			Assert.AreEqual(speed.UpMbps, speeds[0].UpMbps);
			Assert.AreEqual(speed.DownBytes, speeds[0].DownBytes);
			Assert.AreEqual(speed.UpBytes, speeds[0].UpBytes);
			Assert.AreEqual(speed.DurationMs, speeds[0].DurationMs);
			Assert.AreEqual(speed.Trigger, speeds[0].Trigger);
			Assert.AreEqual(speed.Result, speeds[0].Result);
			Assert.IsTrue(speeds[0].Valid);

			// Unknown values
			Assert.AreEqual(SpeedResult.Error, speeds[1].Result);
			Assert.AreEqual(SpeedTrigger.Manual, speeds[1].Trigger);
			Assert.IsFalse(speeds[1].Valid);
			Assert.IsNull(speeds[1].DownMbps);
			Assert.IsNull(speeds[1].LinkRate);
			Assert.AreEqual("", speeds[1].Bssid);

			var switchEvents = history.ReadEvents(DateTime.MinValue, DateTime.MaxValue);
			Assert.AreEqual(2, switchEvents.Count);
			Assert.AreEqual(switchEvent.Time, switchEvents[0].Time);
			Assert.AreEqual(switchEvent.InterfaceId, switchEvents[0].InterfaceId);
			Assert.AreEqual(switchEvent.Kind, switchEvents[0].Kind);
			Assert.AreEqual(switchEvent.From, switchEvents[0].From);
			Assert.AreEqual(switchEvent.To, switchEvents[0].To);
			Assert.AreEqual(switchEvent.FromScore, switchEvents[0].FromScore);
			Assert.IsNull(switchEvents[0].ToScore);
			Assert.AreEqual("a, \"b\"\r\nc", switchEvents[0].Reason); // Line breaks are kept in the database.
			Assert.AreEqual("second", switchEvents[1].Reason);

			// Time range
			Assert.AreEqual(2, history.ReadSpeeds(beforeMidnight, afterMidnight).Count);
			Assert.AreEqual(1, history.ReadSpeeds(beforeMidnight, beforeMidnight.AddSeconds(10)).Count);
			Assert.AreEqual(1, history.ReadSpeeds(beforeMidnight.AddMilliseconds(-1), beforeMidnight.AddMilliseconds(1)).Count);
			Assert.AreEqual(0, history.ReadSpeeds(beforeMidnight.AddMilliseconds(1), afterMidnight.AddMilliseconds(-1)).Count);
			Assert.AreEqual(SpeedResult.Error, history.ReadSpeeds(afterMidnight, afterMidnight).Single().Result);
			Assert.AreEqual(0, history.ReadSpeeds(afterMidnight.AddSeconds(1), afterMidnight.AddDays(1)).Count);
			Assert.AreEqual(0, history.ReadSpeeds(afterMidnight, beforeMidnight).Count);
			Assert.AreEqual(60, history.ReadSignals(afterMidnight, afterMidnight.AddHours(1)).Single().Signal);
			Assert.AreEqual("second", history.ReadEvents(afterMidnight.ToLocalTime(), afterMidnight.ToLocalTime()).Single().Reason);

			Assert.AreEqual(70.25, history.GetSummary(HistoryService.GetKey(TrickyName, _interfaceId)).DownMbps);
		}

		// Schema
		using var connection = OpenDatabase();

		Assert.AreEqual((long)HistoryService.SchemaVersion, Query(connection, "PRAGMA user_version;").Single());
		Assert.AreEqual("wal", Query(connection, "PRAGMA journal_mode;").Single());

		CollectionAssert.AreEqual(new object[] { "events", "meta", "signal", "speed" },
			Query(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"));
		CollectionAssert.AreEqual(new object[]
		{
			"ix_events_profile_time",
			"ix_events_time",
			"ix_signal_profile_time",
			"ix_signal_time",
			"ix_speed_profile_time",
			"ix_speed_time"
		}, Query(connection, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_%' ORDER BY name;"));

		// Typed columns and time as UTC ISO 8601 text
		CollectionAssert.AreEqual(new object[] { "2026-11-17T23:59:30Z", "2026-11-18T00:00:30Z" },
			Query(connection, "SELECT time FROM speed ORDER BY id;"));
		CollectionAssert.AreEqual(new object[] { "text", "text", "real", "integer", "real", "integer", "text", "integer" },
			Query(connection, "SELECT typeof(time), typeof(interface_id), typeof(band), typeof(link_rate), typeof(down_mbps), typeof(down_bytes), typeof(result), typeof(valid) FROM speed WHERE id = 1;"));
		CollectionAssert.AreEqual(new object[] { "null", "null" },
			Query(connection, "SELECT typeof(link_rate), typeof(down_mbps) FROM speed WHERE id = 2;"));
		Assert.AreEqual(2.4, Query(connection, "SELECT band FROM signal WHERE id = 1;").Single());
		Assert.AreEqual("11111111-2222-3333-4444-555555555555", Query(connection, "SELECT interface_id FROM events WHERE id = 1;").Single());
	}

	[TestMethod]
	public void TestRowsAreKeptWhileDatabaseIsLocked()
	{
		using var history = CreateHistory();

		history.AppendEvent(CreateEvent(_start, "first"));

		var changedKinds = new List<HistoryKind>();
		history.Changed += (_, kind) => changedKinds.Add(kind);

		// Another process is writing to the database.
		using (var connection = OpenDatabase())
		{
			Execute(connection, "BEGIN IMMEDIATE;");

			history.AppendEvent(CreateEvent(_start.AddMinutes(1), "second"));
			history.AppendSpeed(CreateSpeed(_start.AddMinutes(2), "Office", SpeedResult.Ok, 71));
			history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(3), "Office", 50) });

			// A speed sample is held in memory and notified even if it could not be written.
			CollectionAssert.AreEqual(new[] { HistoryKind.Speed }, changedKinds);
			Assert.AreEqual(71D, history.GetSummary(HistoryService.GetKey("Office", _interfaceId)).DownMbps);

			// Reading is not blocked.
			Assert.AreEqual("first", history.ReadEvents(_start, _start.AddHours(1)).Single().Reason);
			Assert.AreEqual(0, history.ReadSpeeds(_start, _start.AddHours(1)).Count);

			Execute(connection, "ROLLBACK;");
		}

		// The held rows are written in order by the next append of any kind, only once.
		history.AppendEvent(CreateEvent(_start.AddMinutes(4), "fourth"));

		CollectionAssert.AreEqual(new[] { HistoryKind.Speed, HistoryKind.Event }, changedKinds);
		CollectionAssert.AreEqual(new[] { "first", "second", "fourth" },
			history.ReadEvents(_start, _start.AddHours(1)).Select(x => x.Reason).ToArray());
		Assert.AreEqual(71D, history.ReadSpeeds(_start, _start.AddHours(1)).Single().DownMbps);
		Assert.AreEqual(50, history.ReadSignals(_start, _start.AddHours(1)).Single().Signal);

		history.AppendEvent(CreateEvent(_start.AddMinutes(5), "last"));

		Assert.AreEqual(4, history.ReadEvents(_start, _start.AddHours(1)).Count);
		Assert.AreEqual(1, history.ReadSpeeds(_start, _start.AddHours(1)).Count);
		Assert.AreEqual(1, history.ReadSignals(_start, _start.AddHours(1)).Count);
	}

	[TestMethod]
	public void TestHeldRowsAreBoundedAndWrittenWhenDisposed()
	{
		const int excess = 10;

		using (var history = CreateHistory())
		{
			history.AppendEvent(CreateEvent(_start, "first"));

			using (var connection = OpenDatabase())
			{
				Execute(connection, "BEGIN IMMEDIATE;");

				// Each sample is the first one of its profile and so is to be written.
				history.AppendSignals(Enumerable.Range(0, HistoryService.MaxPendingCount + excess)
					.Select(x => CreateSignal(_start.AddSeconds(x), $"Network {x}", 50)));

				Execute(connection, "ROLLBACK;");
			}

			Assert.AreEqual(0, history.ReadSignals(_start, _start.AddDays(1)).Count);
		}

		// The held rows, oldest dropped, are written when the instance is disposed.
		using (var history = CreateHistory())
		{
			var signals = history.ReadSignals(_start, _start.AddDays(1));
			Assert.AreEqual(HistoryService.MaxPendingCount, signals.Count);
			Assert.AreEqual($"Network {excess}", signals[0].Profile);
			Assert.AreEqual($"Network {HistoryService.MaxPendingCount + excess - 1}", signals.Last().Profile);
		}
	}

	[TestMethod]
	public void TestPurgeDeletesOnlyOldRows()
	{
		using var history = CreateHistory();

		history.AppendSignals(new[] { CreateSignal(_start.AddDays(-8), "Office", 50) });
		history.AppendSpeed(CreateSpeed(_start.AddDays(-8), "Office", SpeedResult.Ok, 70));
		history.AppendEvent(CreateEvent(_start.AddDays(-8), "old"));
		history.AppendSignals(new[] { CreateSignal(_start.Date.AddDays(-7), "Office", 60) }); // Midnight of the oldest day kept
		history.AppendSpeed(CreateSpeed(_start.AddDays(-7), "Office", SpeedResult.Ok, 71));
		history.AppendEvent(CreateEvent(_start.Date.AddDays(-7).AddSeconds(-1), "just before"));
		history.AppendEvent(CreateEvent(_start, "new"));

		history.Purge(0); // Ignored
		Assert.AreEqual(2, history.ReadSignals(DateTime.MinValue, DateTime.MaxValue).Count);
		Assert.AreEqual(2, history.ReadSpeeds(DateTime.MinValue, DateTime.MaxValue).Count);
		Assert.AreEqual(3, history.ReadEvents(DateTime.MinValue, DateTime.MaxValue).Count);

		history.Purge(7);

		Assert.AreEqual(60, history.ReadSignals(DateTime.MinValue, DateTime.MaxValue).Single().Signal);
		Assert.AreEqual(71D, history.ReadSpeeds(DateTime.MinValue, DateTime.MaxValue).Single().DownMbps);
		Assert.AreEqual("new", history.ReadEvents(DateTime.MinValue, DateTime.MaxValue).Single().Reason);

		// Appending works after purging.
		history.AppendEvent(CreateEvent(_start.AddMinutes(1), "newer"));
		Assert.AreEqual(2, history.ReadEvents(DateTime.MinValue, DateTime.MaxValue).Count);
	}

	[TestMethod]
	public void TestRenameMovesRows()
	{
		var oldKey = HistoryService.GetKey(TrickyName, _interfaceId);
		var newKey = HistoryService.GetKey("Office | 2", _interfaceId);
		var otherId = Guid.NewGuid();

		using (var history = CreateHistory())
		{
			history.AppendSignals(new[]
			{
				CreateSignal(_start, TrickyName, 50),
				CreateSignal(_start, TrickyName, 40, interfaceId: otherId),
				CreateSignal(_start, "EXT", 30)
			});
			history.AppendSpeed(CreateSpeed(_start, TrickyName, SpeedResult.Ok, 70));
			history.AppendSpeed(CreateSpeed(_start, "EXT", SpeedResult.Ok, 80));
			history.AppendEvent(new SwitchEvent { Time = _start, InterfaceId = _interfaceId, Kind = SwitchKind.Switch, From = "EXT", To = TrickyName });

			history.Rename(oldKey, newKey);

			Assert.AreEqual(0, history.GetSpeedSamples(oldKey).Count);
			Assert.AreEqual("Office | 2", history.GetSpeedSamples(newKey).Single().Profile);

			history.AppendSpeed(CreateSpeed(_start.AddMinutes(1), "Office | 2", SpeedResult.Ok, 90));
			Assert.AreEqual(2, history.GetSummary(newKey).Count);
		}

		using (var history = CreateHistory())
		{
			// The rows of the same name on another interface and of other names are not moved.
			CollectionAssert.AreEquivalent(new[] { "Office | 2|50", TrickyName + "|40", "EXT|30" },
				history.ReadSignals(_start, _start).Select(x => $"{x.Profile}|{x.Signal}").ToArray());
			CollectionAssert.AreEquivalent(new[] { "Office | 2", "EXT", "Office | 2" },
				history.ReadSpeeds(_start, _start.AddHours(1)).Select(x => x.Profile).ToArray());

			// Events are left as they were recorded.
			Assert.AreEqual(TrickyName, history.ReadEvents(_start, _start).Single().To);

			Assert.IsNull(history.GetSummary(oldKey));
			Assert.AreEqual(2, history.GetSummary(newKey).Count);
			Assert.AreEqual(1, history.GetSummary(HistoryService.GetKey("EXT", _interfaceId)).Count);
		}
	}

	[TestMethod]
	public void TestDatabaseOfNewerVersionIsUsed()
	{
		using (var history = CreateHistory())
		{
			history.AppendSpeed(CreateSpeed(_start, "Office", SpeedResult.Ok, 70));
		}

		// A newer version added a column and a table.
		using (var connection = OpenDatabase())
		{
			Execute(connection, "ALTER TABLE speed ADD COLUMN extra TEXT; CREATE TABLE other (id INTEGER PRIMARY KEY); PRAGMA user_version = 99;");
		}

		using (var history = CreateHistory())
		{
			Assert.AreEqual(70D, history.GetSummary(HistoryService.GetKey("Office", _interfaceId)).DownMbps);

			history.AppendSpeed(CreateSpeed(_start.AddMinutes(1), "Office", SpeedResult.Ok, 80));
			Assert.AreEqual(2, history.ReadSpeeds(_start, _start.AddHours(1)).Count);
		}

		using (var connection = OpenDatabase())
		{
			Assert.AreEqual(99L, Query(connection, "PRAGMA user_version;").Single());
		}
	}

	[TestMethod]
	public void TestBrokenDatabaseDoesNotThrow()
	{
		Directory.CreateDirectory(_folderPath);
		File.WriteAllText(_filePath, new string('x', 5000));

		using var history = CreateHistory();

		history.AppendEvent(CreateEvent(_start, "event"));
		history.AppendSpeed(CreateSpeed(_start, "Office", SpeedResult.Ok, 70));
		history.Purge(7);
		history.Rename(HistoryService.GetKey("Office", _interfaceId), HistoryService.GetKey("Office 2", _interfaceId));

		Assert.AreEqual(0, history.ReadEvents(_start, _start).Count);
		Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));
		Assert.AreEqual(0, history.ExportCsv(Path.Combine(_filePath, "export"), _start, _start).Count);

		// The speed sample is still held in memory.
		Assert.AreEqual(70D, history.GetSummary(HistoryService.GetKey("Office 2", _interfaceId)).DownMbps);
	}

	#endregion

	#region Import/Export

	[TestMethod]
	public void TestImportCsv()
	{
		WriteCsvFiles();

		// Files which are not to be imported
		File.WriteAllText(Path.Combine(_csvFolderPath, "notes-20261117.csv"), "other");
		File.WriteAllText(Path.Combine(_csvFolderPath, "speed-backup.csv"), SpeedSample.Header + "\r\n" + CreateSpeed(_start, "Backup", SpeedResult.Ok, 1).ToCsv() + "\r\n");
		File.WriteAllText(Path.Combine(_csvFolderPath, "export-20261117-100000-speed.csv"), SpeedSample.Header + "\r\n" + CreateSpeed(_start, "Export", SpeedResult.Ok, 1).ToCsv() + "\r\n");

		var importedFolderPath = _csvFolderPath + "-csv-imported";

		using (var history = CreateHistory())
		{
			Assert.IsFalse(File.Exists(_filePath));

			var changedKinds = new List<HistoryKind>();
			history.Changed += (_, kind) => changedKinds.Add(kind);

			Assert.AreEqual(8, history.ImportCsv(_csvFolderPath));

			CollectionAssert.AreEquivalent(new[] { HistoryKind.Signal, HistoryKind.Speed, HistoryKind.Event }, changedKinds);
			AssertImported(history);

			// The summary is rebuilt from the imported rows.
			Assert.AreEqual(70.5, history.GetSummary(HistoryService.GetKey("Office", _interfaceId)).DownMbps);
			Assert.AreEqual(77D, history.GetSummary(HistoryService.GetKey(TrickyName, _interfaceId)).DownMbps);

			// The folder is renamed, not deleted.
			Assert.IsFalse(Directory.Exists(_csvFolderPath));
			Assert.AreEqual(9, Directory.GetFiles(importedFolderPath).Length);
			Assert.IsTrue(File.Exists(Path.Combine(importedFolderPath, "speed-20261117.csv")));

			// Nothing is imported twice even if the same files appear again.
			Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));
			Assert.AreEqual(0, history.ImportCsv(importedFolderPath));

			WriteCsvFiles();
			Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));

			AssertImported(history);
			Assert.AreEqual(6, Directory.GetFiles(_csvFolderPath).Length);
			Assert.IsFalse(Directory.Exists(importedFolderPath + "-2"));

			// Rows are appended after the imported rows.
			history.AppendSpeed(CreateSpeed(_start, "Office", SpeedResult.Ok, 60));
			Assert.AreEqual(4, history.ReadSpeeds(DateTime.MinValue, DateTime.MaxValue).Count);
		}

		using (var history = CreateHistory())
		{
			Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));
			Assert.AreEqual(4, history.ReadSpeeds(DateTime.MinValue, DateTime.MaxValue).Count);
			Assert.AreEqual(2, history.GetSummary(HistoryService.GetKey("Office", _interfaceId)).Count);
		}
	}

	[TestMethod]
	public void TestImportCsvIsRecordedWithRows()
	{
		WriteCsvFiles();

		using (var history = CreateHistory())
		{
			// A file in use prevents the folder from being renamed.
			using (new FileStream(Path.Combine(_csvFolderPath, "speed-20261117.csv"), FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				Assert.AreEqual(8, history.ImportCsv(_csvFolderPath));
			}

			Assert.IsTrue(Directory.Exists(_csvFolderPath));
			AssertImported(history);
		}

		// The folder is renamed at the next time without importing the files again.
		using (var history = CreateHistory())
		{
			Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));

			Assert.IsFalse(Directory.Exists(_csvFolderPath));
			Assert.IsTrue(Directory.Exists(_csvFolderPath + "-csv-imported"));
			AssertImported(history);
		}
	}

	[TestMethod]
	public void TestImportCsvAfterRowsAreWritten()
	{
		WriteCsvFiles();

		// An existing folder of the same name is left as it is.
		Directory.CreateDirectory(_csvFolderPath + "-csv-imported");

		using var history = CreateHistory();

		// The database is created by a row before the files are imported.
		history.AppendSpeed(CreateSpeed(_start, "Office", SpeedResult.Ok, 60));

		Assert.AreEqual(8, history.ImportCsv(_csvFolderPath));
		Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));

		Assert.AreEqual(4, history.ReadSpeeds(DateTime.MinValue, DateTime.MaxValue).Count);
		Assert.AreEqual(2, history.GetSummary(HistoryService.GetKey("Office", _interfaceId)).Count);
		Assert.AreEqual(0, Directory.GetFiles(_csvFolderPath + "-csv-imported").Length);
		Assert.AreEqual(6, Directory.GetFiles(_csvFolderPath + "-csv-imported-2").Length);
	}

	[TestMethod]
	public void TestImportCsvWithoutFiles()
	{
		using var history = CreateHistory();

		// Nothing is created if there is nothing to be imported.
		Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));
		Assert.AreEqual(0, history.ImportCsv(null));
		Assert.IsFalse(Directory.Exists(_folderPath));

		Directory.CreateDirectory(_csvFolderPath);
		File.WriteAllText(Path.Combine(_csvFolderPath, "export-20261117-100000-speed.csv"), SpeedSample.Header + "\r\n" + CreateSpeed(_start, "Export", SpeedResult.Ok, 1).ToCsv() + "\r\n");

		Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));
		Assert.IsFalse(File.Exists(_filePath));

		// Once the database exists without files to be imported, files are not imported any more
		// and the folder, which is that of exported files, is not renamed.
		history.AppendEvent(CreateEvent(_start, "event"));
		Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));

		WriteCsvFiles();
		Assert.AreEqual(0, history.ImportCsv(_csvFolderPath));
		Assert.AreEqual(0, history.ReadSpeeds(DateTime.MinValue, DateTime.MaxValue).Count);
		Assert.AreEqual(7, Directory.GetFiles(_csvFolderPath).Length);
	}

	[TestMethod]
	public void TestExportCsv()
	{
		using var history = CreateHistory();

		history.AppendSignals(new[] { CreateSignal(_start.AddHours(-2), "Office", 40) }); // Out of the range
		history.AppendSignals(new[] { CreateSignal(_start, TrickyName, 50, connected: true) });
		history.AppendSpeed(CreateSpeed(_start, TrickyName, SpeedResult.Ok, 70.25));
		history.AppendSpeed(CreateSpeed(_start.AddMinutes(1), "Office", SpeedResult.RouteMismatch, null));
		history.AppendEvent(new SwitchEvent { Time = _start, InterfaceId = _interfaceId, Kind = SwitchKind.Switch, From = "Off\nice", To = "EXT", Reason = "a\r\nb" });

		_now = new DateTime(2026, 11, 17, 12, 34, 56, DateTimeKind.Local);

		var filePaths = history.ExportCsv(_csvFolderPath, _start.AddHours(-1), _start.AddHours(1));

		CollectionAssert.AreEqual(new[]
		{
			"export-20261117-123456-signal.csv",
			"export-20261117-123456-speed.csv",
			"export-20261117-123456-events.csv"
		}, filePaths.Select(x => Path.GetFileName(x)).ToArray());
		CollectionAssert.AreEquivalent(filePaths.ToArray(), Directory.GetFiles(_csvFolderPath));

		var bytes = File.ReadAllBytes(filePaths[1]);
		CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray()); // BOM

		var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split(new[] { "\r\n" }, StringSplitOptions.None);
		Assert.AreEqual(4, lines.Length); // Header, rows and empty string after the last line end
		Assert.AreEqual("Time,InterfaceId,Profile,Bssid,Band,Channel,Signal,LinkRate,GatewayMs,LatencyMs,JitterMs,DownMbps,UpMbps,DownBytes,UpBytes,DurationMs,Trigger,Result,Valid", lines[0]);
		Assert.AreEqual("2026-11-17T10:00:00Z,11111111-2222-3333-4444-555555555555,\"Café, \"\"Guest\"\" 5G\",AA:BB:CC:00:00:01,5,36,53,866,1.5,18.2,2.1,70.3,12.3,20000000,5000000,9500,Periodic,Ok,1", lines[1]);
		Assert.IsTrue(lines[2].EndsWith(",,12.3,20000000,5000000,9500,Periodic,RouteMismatch,0"));
		Assert.AreEqual("", lines[3]);

		lines = File.ReadAllLines(filePaths[0]);
		Assert.AreEqual(2, lines.Length);
		Assert.AreEqual(SignalSample.Header, lines[0]);
		Assert.AreEqual("2026-11-17T10:00:00Z,11111111-2222-3333-4444-555555555555,\"Café, \"\"Guest\"\" 5G\",1,50,-60,2.4,11,ax,AA:BB:CC:00:00:01,286", lines[1]);

		// Each row is in a single line.
		lines = File.ReadAllLines(filePaths[2]);
		Assert.AreEqual(2, lines.Length);
		Assert.AreEqual(SwitchEvent.Header, lines[0]);
		Assert.AreEqual("2026-11-17T10:00:00Z,11111111-2222-3333-4444-555555555555,Switch,Off ice,EXT,,,a b", lines[1]);

		// The exported files can be parsed by their headers.
		var columns = CsvLine.GetColumns(File.ReadAllLines(filePaths[1])[0]);
		Assert.IsTrue(SpeedSample.TryParse(columns, CsvLine.Split(File.ReadAllLines(filePaths[1])[1]), out SpeedSample speed));
		Assert.AreEqual(TrickyName, speed.Profile);

		// An empty range
		_now = _now.AddSeconds(1);
		filePaths = history.ExportCsv(_csvFolderPath, _start.AddDays(1), _start.AddDays(2));
		Assert.AreEqual(3, filePaths.Count);
		Assert.AreEqual(SpeedSample.Header, File.ReadAllLines(filePaths[1]).Single());
		Assert.AreEqual(6, Directory.GetFiles(_csvFolderPath).Length);
	}

	/// <summary>
	/// Writes CSV files as previous versions did, which have 3 signal samples, 3 speed samples
	/// and 2 switching events to be imported.
	/// </summary>
	private void WriteCsvFiles()
	{
		Directory.CreateDirectory(_csvFolderPath);
		var encoding = new UTF8Encoding(false);

		File.WriteAllText(Path.Combine(_csvFolderPath, "signal-20261116.csv"),
			SignalSample.Header + "\r\n" +
			CreateSignal(_start.AddDays(-1), "Office", 40).ToCsv() + "\r\n",
			encoding);

		File.WriteAllText(Path.Combine(_csvFolderPath, "signal-20261117.csv"),
			SignalSample.Header + "\r\n" +
			CreateSignal(_start.AddHours(-2), "Office", 50, connected: true).ToCsv() + "\r\n" +
			CreateSignal(_start.AddHours(-1), TrickyName, 60).ToCsv() + "\r\n",
			encoding);

		// Columns of another version: an extra unknown column and no optional columns. Then
		// a malformed line, an unterminated quote and the last line which was cut off
		File.WriteAllText(Path.Combine(_csvFolderPath, "speed-20261116.csv"),
			"Time,InterfaceId,Profile,Comment,DownMbps,Result,Valid\r\n" +
			$"2026-11-16T08:00:00Z,{_interfaceId},Office,hello,70.5,Ok,1\r\n" +
			"this is not a row\r\n" +
			$"2026-11-16T08:30:00Z,{_interfaceId},\"Office,broken,1.0,Ok,1\r\n" +
			$"2026-11-16T09:00:00Z,{_interfaceId},EXT,\"a,b\",77.0,Ok,1\r\n" +
			"2026-11-16T09:30:00Z,11111111-2222-33",
			encoding);

		File.WriteAllText(Path.Combine(_csvFolderPath, "speed-20261117.csv"),
			SpeedSample.Header + "\r\n" +
			CreateSpeed(_start.AddHours(-1), TrickyName, SpeedResult.Ok, 77).ToCsv() + "\r\n" +
			CreateSpeed(_start, "Office", SpeedResult.Ok, 99).ToCsv().Substring(0, 30), // Cut off
			encoding);

		File.WriteAllText(Path.Combine(_csvFolderPath, "events-20261117.csv"),
			SwitchEvent.Header + "\r\n" +
			CreateEvent(_start.AddHours(-2), "first, \"quoted\"").ToCsv() + "\r\n" +
			CreateEvent(_start.AddHours(-1), "second").ToCsv() + "\r\n" +
			$"2026-11-17T09:30:00Z,{_interfaceId},Sw",
			encoding);

		File.WriteAllText(Path.Combine(_csvFolderPath, "events-20261118.csv"), string.Empty, encoding);
	}

	private void AssertImported(HistoryService history)
	{
		var signals = history.ReadSignals(DateTime.MinValue, DateTime.MaxValue);
		CollectionAssert.AreEqual(new[] { 40, 50, 60 }, signals.Select(x => x.Signal).ToArray());
		Assert.AreEqual(_start.AddDays(-1), signals[0].Time);
		Assert.IsTrue(signals[1].Connected);
		Assert.AreEqual(286, signals[1].LinkRate);
		Assert.AreEqual(TrickyName, signals[2].Profile);
		Assert.AreEqual(2.4F, signals[2].Band);

		var speeds = history.ReadSpeeds(DateTime.MinValue, _start.AddMinutes(-1));
		CollectionAssert.AreEqual(new double?[] { 70.5, 77, 77 }, speeds.Select(x => x.DownMbps).ToArray());
		CollectionAssert.AreEqual(new[] { "Office", "EXT", TrickyName }, speeds.Select(x => x.Profile).ToArray());
		Assert.IsNull(speeds[0].UpMbps);
		Assert.AreEqual(12.3, speeds[2].UpMbps);
		Assert.AreEqual(20_000_000L, speeds[2].DownBytes);
		Assert.AreEqual(SpeedTrigger.Periodic, speeds[2].Trigger);
		Assert.AreEqual(_interfaceId, speeds[2].InterfaceId);

		var switchEvents = history.ReadEvents(DateTime.MinValue, DateTime.MaxValue);
		CollectionAssert.AreEqual(new[] { "first, \"quoted\"", "second" }, switchEvents.Select(x => x.Reason).ToArray());
		Assert.AreEqual(70D, switchEvents[0].FromScore);
	}

	#endregion

	#region Speed

	[TestMethod]
	public void TestSummaryIsRebuiltFromExistingDatabase()
	{
		var key = HistoryService.GetKey("Office", _interfaceId);

		using (var history = CreateHistory())
		{
			Assert.IsNull(history.GetSummary(key));
			Assert.AreEqual(0, history.GetSpeedSamples(key).Count);

			int changedCount = 0;
			history.Changed += (_, kind) =>
			{
				Assert.AreEqual(HistoryKind.Speed, kind);
				changedCount++;
			};

			history.AppendSpeed(CreateSpeed(_start.AddDays(-20), "Office", SpeedResult.Ok, 500)); // Too old
			history.AppendSpeed(CreateSpeed(_start.AddDays(-2), "Office", SpeedResult.Ok, 60));
			history.AppendSpeed(CreateSpeed(_start.AddHours(-3), "Office", SpeedResult.Ok, 70));
			history.AppendSpeed(CreateSpeed(_start.AddHours(-2), "Office", SpeedResult.Error, null));
			history.AppendSpeed(CreateSpeed(_start.AddHours(-1), "Office", SpeedResult.Ok, 80));
			history.AppendSpeed(CreateSpeed(_start.AddHours(-1), "EXT", SpeedResult.NoInternet, 0));
			Assert.AreEqual(6, changedCount);

			var summary = history.GetSummary(key);
			Assert.AreEqual(3, summary.Count);
			Assert.AreEqual(70D, summary.DownMbps);
			Assert.AreEqual(80D, summary.LastDownMbps);
		}

		using (var history = CreateHistory())
		{
			var summary = history.GetSummary(key);
			Assert.IsNotNull(summary);
			Assert.AreEqual(3, summary.Count);
			Assert.AreEqual(70D, summary.DownMbps);
			Assert.AreEqual(80D, summary.LastDownMbps);
			Assert.AreEqual(_start.AddHours(-1), summary.LastTime);
			Assert.AreEqual(SpeedResult.Ok, summary.LastResult);

			// Invalid samples are held but samples out of the period are not.
			Assert.AreEqual(4, history.GetSpeedSamples(key).Count);

			var other = history.GetSummary(HistoryService.GetKey("EXT", _interfaceId));
			Assert.IsFalse(other.HasData);
			Assert.AreEqual(SpeedResult.NoInternet, other.LastResult);

			Assert.IsNull(history.GetSummary(HistoryService.GetKey("Office", Guid.NewGuid())));
			Assert.IsNull(history.GetSummary(null));

			// The summary depends on the current time.
			_now = _start.AddDays(30);
			Assert.IsNull(history.GetSummary(key));
		}
	}

	[TestMethod]
	public void TestBytesTodayAndRename()
	{
		using var history = CreateHistory();

		history.AppendSpeed(CreateSpeed(_start.AddDays(-1), "Office", SpeedResult.Ok, 70)); // Yesterday
		history.AppendSpeed(CreateSpeed(_start.AddHours(-2), "Office", SpeedResult.Ok, 70));
		history.AppendSpeed(CreateSpeed(_start.AddHours(-1), "EXT", SpeedResult.Aborted, null));

		// Each sample has 20,000,000 bytes down and 5,000,000 bytes up.
		Assert.AreEqual(50_000_000L, history.GetBytesToday());

		_now = _start.AddDays(1);
		Assert.AreEqual(0L, history.GetBytesToday());
		_now = _start;

		var oldKey = HistoryService.GetKey("Office", _interfaceId);
		var newKey = HistoryService.GetKey("Office 2", _interfaceId);

		history.Rename(oldKey, newKey);

		Assert.IsNull(history.GetSummary(oldKey));
		Assert.AreEqual(0, history.GetSpeedSamples(oldKey).Count);
		Assert.AreEqual(2, history.GetSpeedSamples(newKey).Count);
		Assert.AreEqual(70D, history.GetSummary(newKey).DownMbps);
		Assert.AreEqual(50_000_000L, history.GetBytesToday());

		// The returned list is a copy.
		var samples = history.GetSpeedSamples(newKey);
		history.AppendSpeed(CreateSpeed(_start, "Office 2", SpeedResult.Ok, 90));
		Assert.AreEqual(2, samples.Count);
		Assert.AreEqual(3, history.GetSpeedSamples(newKey).Count);

		history.Rename(null, newKey);
		history.Rename("none", newKey);
		Assert.AreEqual(3, history.GetSpeedSamples(newKey).Count);
	}

	[TestMethod]
	public void TestDatabaseIsCreatedOnlyWhenWritten()
	{
		using var history = CreateHistory();

		// Reading, summarizing and purging leave nothing on disk.
		Assert.AreEqual(0, history.ReadSignals(_start, _start).Count);
		Assert.IsNull(history.GetSummary(HistoryService.GetKey("Office", _interfaceId)));
		Assert.AreEqual(0L, history.GetBytesToday());
		history.Purge(7);
		Assert.IsFalse(Directory.Exists(_folderPath));

		history.AppendEvent(CreateEvent(_start, "event"));
		Assert.IsTrue(File.Exists(_filePath));
		Assert.AreEqual(1, history.ReadEvents(_start, _start).Count);
	}

	[TestMethod]
	public void TestNothingThrowsWithUnusableFolder()
	{
		// A file occupies the path of the folder.
		Directory.CreateDirectory(_folderPath);
		var filePath = Path.Combine(_folderPath, "occupied");
		File.WriteAllText(filePath, "file");

		using var history = new HistoryService(Path.Combine(filePath, "history.db"), () => _now);

		history.AppendSignals(null);
		history.AppendSignals(new[] { null, CreateSignal(_start, "Office", 50) });
		history.AppendSpeed(null);
		history.AppendSpeed(CreateSpeed(_start, "Office", SpeedResult.Ok, 70));
		history.AppendEvent(null);
		history.AppendEvent(CreateEvent(_start, "event"));
		history.Purge(7);

		Assert.AreEqual(0, history.ReadSignals(_start, _start).Count);
		Assert.AreEqual(0, history.ReadSpeeds(_start, _start).Count);
		Assert.AreEqual(0, history.ReadEvents(_start, _start).Count);

		// The speed sample is still held in memory.
		Assert.AreEqual(70D, history.GetSummary(HistoryService.GetKey("Office", _interfaceId)).DownMbps);
		Assert.AreEqual("file", File.ReadAllText(filePath));
	}

	#endregion

	#region Base

	private HistoryService CreateHistory() => new HistoryService(_filePath, () => _now, 1);

	private SqliteConnection OpenDatabase()
	{
		var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath, Pooling = false }.ToString());
		connection.Open();
		return connection;
	}

	private static void Execute(SqliteConnection connection, string text)
	{
		using var command = connection.CreateCommand();
		command.CommandText = text;
		command.ExecuteNonQuery();
	}

	/// <summary>
	/// Gets the values of all the rows in sequence.
	/// </summary>
	private static object[] Query(SqliteConnection connection, string text)
	{
		using var command = connection.CreateCommand();
		command.CommandText = text;

		var values = new List<object>();
		using var reader = command.ExecuteReader();
		while (reader.Read())
		{
			for (int i = 0; i < reader.FieldCount; i++)
				values.Add(reader.GetValue(i));
		}
		return values.ToArray();
	}

	private static IReadOnlyList<SignalSample> ReadAllSignals(HistoryService history) =>
		history.ReadSignals(_start.AddDays(-1), _start.AddDays(1));

	private static SignalSample CreateSignal(DateTime time, string profile, int signal, bool connected = false, string bssid = "AA:BB:CC:00:00:01", int channel = 11, Guid? interfaceId = null)
	{
		return new SignalSample
		{
			Time = time,
			InterfaceId = interfaceId ?? _interfaceId,
			Profile = profile,
			Connected = connected,
			Signal = signal,
			Rssi = -60,
			Band = 2.4F,
			Channel = channel,
			Protocol = "ax",
			Bssid = bssid,
			LinkRate = connected ? 286 : null
		};
	}

	private static SpeedSample CreateSpeed(DateTime time, string profile, SpeedResult result, double? down)
	{
		return new SpeedSample
		{
			Time = time,
			InterfaceId = _interfaceId,
			Profile = profile,
			Bssid = "AA:BB:CC:00:00:01",
			Band = 5F,
			Channel = 36,
			Signal = 53,
			LinkRate = 866,
			GatewayMs = 1.5,
			LatencyMs = 18.2,
			JitterMs = 2.1,
			DownMbps = down,
			UpMbps = 12.3,
			DownBytes = 20_000_000L,
			UpBytes = 5_000_000L,
			DurationMs = 9500,
			Trigger = SpeedTrigger.Periodic,
			Result = result
		};
	}

	private static SwitchEvent CreateEvent(DateTime time, string reason)
	{
		return new SwitchEvent
		{
			Time = time,
			InterfaceId = _interfaceId,
			Kind = SwitchKind.Stay,
			From = "Office",
			To = "Office",
			FromScore = 70,
			ToScore = 70,
			Reason = reason
		};
	}

	#endregion
}
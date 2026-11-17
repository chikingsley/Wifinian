using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
	private DateTime _now;

	[TestInitialize]
	public void Initialize()
	{
		_folderPath = Path.Combine(Path.GetTempPath(), "WifinianTest-" + Guid.NewGuid().ToString("N"));
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

	#region File

	[TestMethod]
	public void TestFilesRollOverByUtcDate()
	{
		using var history = CreateHistory();

		var beforeMidnight = new DateTime(2026, 11, 17, 23, 59, 30, DateTimeKind.Utc);
		var afterMidnight = new DateTime(2026, 11, 18, 0, 0, 30, DateTimeKind.Utc);

		history.AppendSignals(new[] { CreateSignal(beforeMidnight, "Office", 50) });
		history.AppendSignals(new[] { CreateSignal(afterMidnight, "Office", 60) });
		history.AppendSpeed(CreateSpeed(beforeMidnight, "Office", SpeedResult.Ok, 70));
		history.AppendSpeed(CreateSpeed(afterMidnight, "Office", SpeedResult.Ok, 71));
		history.AppendEvent(CreateEvent(beforeMidnight, "first"));
		history.AppendEvent(CreateEvent(afterMidnight, "second"));

		var names = Directory.GetFiles(_folderPath).Select(x => Path.GetFileName(x)).OrderBy(x => x).ToArray();
		CollectionAssert.AreEqual(new[]
		{
			"events-20261117.csv",
			"events-20261118.csv",
			"signal-20261117.csv",
			"signal-20261118.csv",
			"speed-20261117.csv",
			"speed-20261118.csv"
		}, names);

		// Header and line ends
		var bytes = File.ReadAllBytes(Path.Combine(_folderPath, "speed-20261117.csv"));
		Assert.AreNotEqual(0xEF, bytes[0]); // No BOM
		var text = Encoding.UTF8.GetString(bytes);
		var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
		Assert.AreEqual(3, lines.Length); // Header, row and empty string after the last line end
		Assert.AreEqual("Time,InterfaceId,Profile,Bssid,Band,Channel,Signal,LinkRate,GatewayMs,LatencyMs,JitterMs,DownMbps,UpMbps,DownBytes,UpBytes,DurationMs,Trigger,Result,Valid", lines[0]);
		Assert.IsTrue(lines[1].StartsWith("2026-11-17T23:59:30Z,11111111-2222-3333-4444-555555555555,Office,"));
		Assert.IsTrue(lines[1].EndsWith(",Periodic,Ok,1"));
		Assert.AreEqual("", lines[2]);

		Assert.AreEqual(SignalSample.Header, File.ReadAllLines(Path.Combine(_folderPath, "signal-20261117.csv"))[0]);
		Assert.AreEqual(SwitchEvent.Header, File.ReadAllLines(Path.Combine(_folderPath, "events-20261117.csv"))[0]);

		// Time range
		Assert.AreEqual(2, history.ReadSpeeds(beforeMidnight, afterMidnight).Count);
		Assert.AreEqual(1, history.ReadSpeeds(beforeMidnight, beforeMidnight.AddSeconds(10)).Count);
		Assert.AreEqual(71D, history.ReadSpeeds(afterMidnight, afterMidnight).Single().DownMbps);
		Assert.AreEqual(0, history.ReadSpeeds(afterMidnight.AddSeconds(1), afterMidnight.AddDays(1)).Count);
		Assert.AreEqual(0, history.ReadSpeeds(afterMidnight, beforeMidnight).Count);
		Assert.AreEqual(60, history.ReadSignals(afterMidnight, afterMidnight.AddHours(1)).Single().Signal);
		CollectionAssert.AreEqual(new[] { "first", "second" }, history.ReadEvents(DateTime.MinValue, DateTime.MaxValue).Select(x => x.Reason).ToArray());
	}

	[TestMethod]
	public void TestTrickyNameThroughFile()
	{
		using var history = CreateHistory();

		history.AppendSpeed(CreateSpeed(_start, TrickyName, SpeedResult.Ok, 12.5));
		history.AppendEvent(new SwitchEvent { Time = _start, InterfaceId = _interfaceId, Kind = SwitchKind.Switch, From = TrickyName, To = "EXT", Reason = "a, \"b\"" });

		Assert.AreEqual(TrickyName, history.ReadSpeeds(_start, _start).Single().Profile);
		Assert.AreEqual(12.5, history.GetSummary(HistoryService.GetKey(TrickyName, _interfaceId)).DownMbps);

		var switchEvent = history.ReadEvents(_start, _start).Single();
		Assert.AreEqual(TrickyName, switchEvent.From);
		Assert.AreEqual("a, \"b\"", switchEvent.Reason);
	}

	[TestMethod]
	public void TestLineBreakThroughFile()
	{
		using var history = CreateHistory();

		history.AppendEvent(new SwitchEvent { Time = _start, InterfaceId = _interfaceId, Kind = SwitchKind.Switch, From = "Off\nice", To = "EXT", Reason = "a\r\nb" });
		history.AppendEvent(CreateEvent(_start.AddMinutes(1), "next"));

		// Header and two rows, each in a single line
		Assert.AreEqual(3, File.ReadAllLines(Path.Combine(_folderPath, "events-20261117.csv")).Length);

		var switchEvents = history.ReadEvents(_start, _start.AddHours(1));
		Assert.AreEqual(2, switchEvents.Count);
		Assert.AreEqual("Off ice", switchEvents[0].From);
		Assert.AreEqual("a b", switchEvents[0].Reason);
		Assert.AreEqual("next", switchEvents[1].Reason);
	}

	[TestMethod]
	public void TestReadSpeedsToleratesMalformedFile()
	{
		Directory.CreateDirectory(_folderPath);
		var filePath = Path.Combine(_folderPath, "speed-20261117.csv");

		// Extra unknown column, missing optional columns, a malformed line, an unterminated quote
		// and a truncated last line without line end
		File.WriteAllText(filePath,
			"Time,InterfaceId,Profile,Comment,DownMbps,Result,Valid\r\n" +
			$"2026-11-17T08:00:00Z,{_interfaceId},Office,hello,70.5,Ok,1\r\n" +
			"this is not a row\r\n" +
			$"2026-11-17T08:30:00Z,{_interfaceId},\"Office,broken,1.0,Ok,1\r\n" +
			$"2026-11-17T09:00:00Z,{_interfaceId},EXT,\"a,b\",77.0,Ok,1\r\n" +
			"2026-11-17T09:30:00Z,11111111-2222-33",
			new UTF8Encoding(false));

		using var history = CreateHistory();

		var speeds = history.ReadSpeeds(_start.AddDays(-1), _start);
		Assert.AreEqual(2, speeds.Count);
		Assert.AreEqual("Office", speeds[0].Profile);
		Assert.AreEqual(70.5, speeds[0].DownMbps);
		Assert.IsNull(speeds[0].UpMbps);
		Assert.AreEqual("EXT", speeds[1].Profile);
		Assert.AreEqual(77D, speeds[1].DownMbps);

		// The samples are loaded at construction as well.
		Assert.AreEqual(70.5, history.GetSummary(HistoryService.GetKey("Office", _interfaceId)).DownMbps);

		// A new row starts on a new line after the truncated line.
		history.AppendSpeed(CreateSpeed(_start, "Office", SpeedResult.Ok, 60));

		var text = File.ReadAllText(filePath);
		Assert.IsTrue(text.Contains("2026-11-17T09:30:00Z,11111111-2222-33\r\n2026-11-17T10:00:00Z,"));
		Assert.IsTrue(text.EndsWith("\r\n"));

		speeds = history.ReadSpeeds(_start.AddDays(-1), _start);
		Assert.AreEqual(3, speeds.Count);
		Assert.AreEqual(60D, speeds[2].DownMbps);
		Assert.AreEqual("Office", speeds[2].Profile);
	}

	[TestMethod]
	public void TestRowsAreKeptWhileFileIsLocked()
	{
		using var history = CreateHistory();

		history.AppendEvent(CreateEvent(_start, "first"));

		int changedCount = 0;
		history.Changed += (_, _) => changedCount++;

		var filePath = Path.Combine(_folderPath, "events-20261117.csv");
		using (new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			history.AppendEvent(CreateEvent(_start.AddMinutes(1), "second"));
			history.AppendEvent(CreateEvent(_start.AddMinutes(2), "third"));
		}

		Assert.AreEqual(0, changedCount);
		Assert.AreEqual(1, history.ReadEvents(_start, _start.AddHours(1)).Count);

		history.AppendEvent(CreateEvent(_start.AddMinutes(3), "fourth"));

		Assert.AreEqual(1, changedCount);
		CollectionAssert.AreEqual(new[] { "first", "second", "third", "fourth" },
			history.ReadEvents(_start, _start.AddHours(1)).Select(x => x.Reason).ToArray());
		Assert.AreEqual(5, File.ReadAllLines(filePath).Length);
	}

	[TestMethod]
	public void TestLockedFileDoesNotBlockOtherFiles()
	{
		using var history = CreateHistory();

		history.AppendSpeed(CreateSpeed(_start, "Office", SpeedResult.Ok, 70));

		var changedKinds = new List<HistoryKind>();
		history.Changed += (_, kind) => changedKinds.Add(kind);

		var speedPath = Path.Combine(_folderPath, "speed-20261117.csv");
		using (new FileStream(speedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			history.AppendSpeed(CreateSpeed(_start.AddMinutes(1), "Office", SpeedResult.Ok, 71));
			history.AppendEvent(CreateEvent(_start.AddMinutes(2), "event"));
			history.AppendSignals(new[] { CreateSignal(_start.AddMinutes(3), "Office", 50) });
			history.AppendSpeed(CreateSpeed(_start.AddMinutes(4), "Office", SpeedResult.Ok, 72));

			// The rows of other files are written while the speed file is locked.
			Assert.AreEqual("event", history.ReadEvents(_start, _start.AddHours(1)).Single().Reason);
			Assert.AreEqual(50, history.ReadSignals(_start, _start.AddHours(1)).Single().Signal);
			CollectionAssert.AreEqual(new[] { HistoryKind.Speed, HistoryKind.Event, HistoryKind.Signal, HistoryKind.Speed }, changedKinds);
		}

		Assert.AreEqual(1, history.ReadSpeeds(_start, _start.AddHours(1)).Count);

		// The held rows are written in order by the next append of any kind, only once.
		history.AppendEvent(CreateEvent(_start.AddMinutes(5), "later"));

		CollectionAssert.AreEqual(new double?[] { 70, 71, 72 },
			history.ReadSpeeds(_start, _start.AddHours(1)).Select(x => x.DownMbps).ToArray());
		Assert.AreEqual(4, File.ReadAllLines(speedPath).Length);

		history.AppendEvent(CreateEvent(_start.AddMinutes(6), "last"));

		Assert.AreEqual(4, File.ReadAllLines(speedPath).Length);
		Assert.AreEqual(3, history.ReadEvents(_start, _start.AddHours(1)).Count);
		Assert.AreEqual(4, File.ReadAllLines(Path.Combine(_folderPath, "events-20261117.csv")).Length);
		Assert.AreEqual(2, File.ReadAllLines(Path.Combine(_folderPath, "signal-20261117.csv")).Length);
	}

	[TestMethod]
	public void TestPurgeRemovesOnlyOldFiles()
	{
		using var history = CreateHistory();

		history.AppendSignals(new[] { CreateSignal(_start.AddDays(-8), "Office", 50) });
		history.AppendSpeed(CreateSpeed(_start.AddDays(-8), "Office", SpeedResult.Ok, 70));
		history.AppendEvent(CreateEvent(_start.AddDays(-8), "old"));
		history.AppendSignals(new[] { CreateSignal(_start.AddDays(-7), "Office", 60) });
		history.AppendSpeed(CreateSpeed(_start.AddDays(-7), "Office", SpeedResult.Ok, 70));
		history.AppendEvent(CreateEvent(_start, "new"));

		var otherPath = Path.Combine(_folderPath, "notes-20200101.csv");
		File.WriteAllText(otherPath, "other");
		var unnamedPath = Path.Combine(_folderPath, "speed-backup.csv");
		File.WriteAllText(unnamedPath, "other");

		history.Purge(0); // Ignored
		Assert.AreEqual(8, Directory.GetFiles(_folderPath).Length);

		history.Purge(7);

		var names = Directory.GetFiles(_folderPath).Select(x => Path.GetFileName(x)).OrderBy(x => x).ToArray();
		CollectionAssert.AreEqual(new[]
		{
			"events-20261117.csv",
			"notes-20200101.csv",
			"signal-20261110.csv",
			"speed-20261110.csv",
			"speed-backup.csv"
		}, names);
	}

	#endregion

	#region Speed

	[TestMethod]
	public void TestSummaryIsRebuiltFromExistingFolder()
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
	public void TestFolderIsCreatedOnlyWhenWritten()
	{
		using var history = CreateHistory();

		// Reading, summarizing and purging leave nothing on disk.
		Assert.AreEqual(0, history.ReadSignals(_start, _start).Count);
		Assert.IsNull(history.GetSummary(HistoryService.GetKey("Office", _interfaceId)));
		Assert.AreEqual(0L, history.GetBytesToday());
		history.Purge(7);
		Assert.IsFalse(Directory.Exists(_folderPath));

		history.AppendEvent(CreateEvent(_start, "event"));
		Assert.IsTrue(Directory.Exists(_folderPath));
		Assert.AreEqual(1, history.ReadEvents(_start, _start).Count);
	}

	[TestMethod]
	public void TestNothingThrowsWithUnusableFolder()
	{
		// A file occupies the path of the folder.
		Directory.CreateDirectory(_folderPath);
		var filePath = Path.Combine(_folderPath, "occupied");
		File.WriteAllText(filePath, "file");

		using var history = new HistoryService(filePath, () => _now);

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

	private HistoryService CreateHistory() => new HistoryService(_folderPath, () => _now);

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
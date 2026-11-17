using System;
using System.Collections.Generic;
using System.Linq;
using ManagedNativeWifi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models.History;
using Wifinian.Models.Wlan;
using Wifinian.ViewModels;
using Wifinian.Views.Controls;

namespace Wifinian.Test;

[TestClass]
public class HistoryWindowTest
{
	private static readonly DateTime _to = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
	private static readonly DateTime _from = _to.AddHours(-24);
	private static readonly Guid _interfaceId = new("11111111-1111-1111-1111-111111111111");
	private static readonly Guid _otherInterfaceId = new("22222222-2222-2222-2222-222222222222");

	// Local time is fixed to UTC+9 so that the tests do not depend on the time zone.
	private static DateTime ToLocal(DateTime utc) => utc.AddHours(9);

	#region Scale

	[TestMethod]
	public void TestMaximum()
	{
		Assert.AreEqual(10D, ChartScale.GetMaximum(0D));
		Assert.AreEqual(10D, ChartScale.GetMaximum(double.NaN));
		Assert.AreEqual(10D, ChartScale.GetMaximum(10D));
		Assert.AreEqual(20D, ChartScale.GetMaximum(10.1));
		Assert.AreEqual(20D, ChartScale.GetMaximum(20D));
		Assert.AreEqual(50D, ChartScale.GetMaximum(21D));
		Assert.AreEqual(100D, ChartScale.GetMaximum(77D));
		Assert.AreEqual(100D, ChartScale.GetMaximum(100D));
		Assert.AreEqual(200D, ChartScale.GetMaximum(101D));
		Assert.AreEqual(1000D, ChartScale.GetMaximum(501D));
	}

	[TestMethod]
	public void TestTicks()
	{
		// 24 hours: every 3 hours on the clock
		var from = new DateTime(2026, 11, 16, 13, 20, 0);
		var ticks = ChartScale.GetTicks(from, from.AddHours(24));
		Assert.AreEqual(8, ticks.Count);
		Assert.AreEqual(new DateTime(2026, 11, 16, 15, 0, 0), ticks[0].time);
		Assert.AreEqual("15:00", ticks[0].label);
		Assert.AreEqual("12:00", ticks[7].label);

		// 7 days: every day
		ticks = ChartScale.GetTicks(from, from.AddDays(7));
		Assert.AreEqual(7, ticks.Count);
		Assert.AreEqual(new DateTime(2026, 11, 17), ticks[0].time);
		Assert.AreEqual("11/17", ticks[0].label);

		// 30 days: every 5th day
		ticks = ChartScale.GetTicks(from, from.AddDays(30));
		Assert.AreEqual(6, ticks.Count);
		Assert.AreEqual("11/22", ticks[1].label);

		Assert.AreEqual(0, ChartScale.GetTicks(from, from).Count);
	}

	[TestMethod]
	public void TestReduce()
	{
		var from = new DateTime(2026, 11, 17, 0, 0, 0);
		var pixelsPerTick = 1D / TimeSpan.FromMinutes(10).Ticks; // 1 pixel for 10 minutes

		ChartPoint Create(double minutes, double value, bool isBreak = false) =>
			new() { Time = from.AddMinutes(minutes), Value = value, IsBreak = isBreak };

		// Points in a column are reduced to the minimum and the maximum in order of appearance.
		var figures = ChartScale.Reduce(
			new[] { Create(0, 50), Create(2, 80), Create(4, 30), Create(6, 60), Create(12, 40), Create(14, 40) },
			from, pixelsPerTick, 0);
		Assert.AreEqual(1, figures.Count);
		CollectionAssert.AreEqual(new[] { 80D, 30D, 40D }, figures[0].Select(x => x.value).ToArray());
		Assert.AreEqual(0.2, figures[0][0].x, 0.0001);

		// A gap breaks the line.
		figures = ChartScale.Reduce(
			new[] { Create(0, 50), Create(10, 60), Create(40, 70), Create(50, 80) },
			from, pixelsPerTick, 15);
		Assert.AreEqual(2, figures.Count);
		CollectionAssert.AreEqual(new[] { 50D, 60D }, figures[0].Select(x => x.value).ToArray());
		CollectionAssert.AreEqual(new[] { 70D, 80D }, figures[1].Select(x => x.value).ToArray());

		// No gap breaks the line if 0 is specified but a point can break it.
		figures = ChartScale.Reduce(
			new[] { Create(0, 50), Create(10, 60), Create(400, 70), Create(410, 80, true) },
			from, pixelsPerTick, 0);
		Assert.AreEqual(2, figures.Count);
		Assert.AreEqual(3, figures[0].Count);
		Assert.AreEqual(1, figures[1].Count);

		Assert.AreEqual(0, ChartScale.Reduce(null, from, pixelsPerTick, 15).Count);
		Assert.AreEqual(0, ChartScale.Reduce(Array.Empty<ChartPoint>(), from, pixelsPerTick, 15).Count);
	}

	#endregion

	#region Snapshot

	[TestMethod]
	public void TestSnapshotEmpty()
	{
		var snapshot = HistorySnapshot.Build(null, null, null, null, null, _from, _to, ToLocal);

		Assert.AreEqual(ToLocal(_from), snapshot.From);
		Assert.AreEqual(ToLocal(_to), snapshot.To);
		Assert.AreEqual(0, snapshot.Networks.Count);
		Assert.AreEqual(0, snapshot.Measurements.Count);
		Assert.AreEqual(0, snapshot.Decisions.Count);
	}

	[TestMethod]
	public void TestSnapshotSummary()
	{
		var signals = new[]
		{
			CreateSignal("Office", 600, 80),
			CreateSignal("Office", 590, 60),
			CreateSignal("EXT2", 600, 40),
			CreateSignal("Guest", 600, 30)
		}.OrderBy(x => x.Time).ToArray();

		var speeds = new[]
		{
			CreateSpeed("EXT2", 300, SpeedResult.Ok, 8, 2, 30),
			CreateSpeed("Office", 290, SpeedResult.Ok, 60, 10, 20),
			CreateSpeed("Office", 280, SpeedResult.Ok, 80, 14, 10),
			CreateSpeed("Office", 270, SpeedResult.NoInternet, 0, null, null),
			CreateSpeed("Office", 260, SpeedResult.Aborted, null, null, null),
			CreateSpeed("EXT", 250, SpeedResult.Ok, 77, 20, 15),
			CreateSpeed("EXT", 240, SpeedResult.Ok, 79, 22, 17),
			CreateSpeed("EXT", 230, SpeedResult.Ok, 75, 18, 16)
		};

		var profiles = new[] { CreateProfileItem("Office", _interfaceId, 72), CreateProfileItem("EXT", _interfaceId, 0) };

		var snapshot = HistorySnapshot.Build(signals, speeds, null, profiles, new[] { "EXT2" }, _from, _to, ToLocal);

		// Fastest first, then those which have no speed
		CollectionAssert.AreEqual(new[] { "EXT", "Office", "EXT2", "Guest" }, snapshot.Networks.Select(x => x.Name).ToArray());

		// Brushes are assigned by alphabetical order of name.
		CollectionAssert.AreEqual(new[] { 0, 3, 1, 2 }, snapshot.Networks.Select(x => x.BrushIndex).ToArray());

		var office = snapshot.Networks[1];
		Assert.AreEqual("72", office.SignalNow);
		Assert.AreEqual("70", office.AverageSignal);
		Assert.AreEqual("70", office.TypicalDown);
		Assert.AreEqual("12", office.TypicalUp);
		Assert.AreEqual("15", office.Latency);
		Assert.AreEqual("3/4", office.Tests);
		Assert.AreEqual(ToLocal(_to.AddMinutes(-260)).ToString("g"), office.LastTested);
		Assert.AreEqual(string.Empty, office.IsAvoided);
		Assert.AreEqual(2, office.SignalPoints.Count);
		Assert.AreEqual(2, office.SpeedPoints.Count);
		Assert.AreEqual(ToLocal(_to.AddMinutes(-290)), office.SpeedPoints[0].Time);
		Assert.AreEqual(60D, office.SpeedPoints[0].Value);
		CollectionAssert.AreEqual(
			new[] { ToLocal(_to.AddMinutes(-270)), ToLocal(_to.AddMinutes(-260)) },
			office.FailedTimes.ToArray());

		var ext = snapshot.Networks[0];
		Assert.AreEqual(string.Empty, ext.SignalNow); // Not in range
		Assert.AreEqual(string.Empty, ext.AverageSignal);
		Assert.AreEqual("77", ext.TypicalDown);
		Assert.AreEqual("3/3", ext.Tests);

		var ext2 = snapshot.Networks[2];
		Assert.AreEqual("8", ext2.TypicalDown);
		Assert.AreEqual("Yes", ext2.IsAvoided);

		var guest = snapshot.Networks[3];
		Assert.AreEqual(string.Empty, guest.TypicalDown);
		Assert.AreEqual(string.Empty, guest.Tests);
		Assert.AreEqual(string.Empty, guest.LastTested);
		Assert.AreEqual(0, guest.SpeedPoints.Count);
	}

	[TestMethod]
	public void TestSnapshotSameNameOnTwoAdapters()
	{
		var signals = new[]
		{
			CreateSignal("Office", 30, 80),
			CreateSignal("Office", 20, 70, _otherInterfaceId),
			CreateSignal("EXT", 10, 60)
		};
		var profiles = new[] { CreateProfileItem("EXT", _interfaceId, 60, "Adapter A") };

		var snapshot = HistorySnapshot.Build(signals, null, null, profiles, null, _from, _to, ToLocal);

		CollectionAssert.AreEquivalent(
			new[] { "EXT", "Office (Adapter A)", "Office (22222222)" },
			snapshot.Networks.Select(x => x.Name).ToArray());
	}

	[TestMethod]
	public void TestSnapshotRows()
	{
		var speeds = Enumerable.Range(0, HistorySnapshot.MaxRowCount + 20)
			.Select(x => CreateSpeed("Office", 1000 - x, SpeedResult.Ok, x, 12.34, 18.26))
			.ToArray();
		speeds[speeds.Length - 1].Bssid = "AA:BB:CC:DD:EE:FF";
		speeds[speeds.Length - 1].Signal = 66;
		speeds[speeds.Length - 1].LinkRate = 390;
		speeds[speeds.Length - 1].Trigger = SpeedTrigger.Manual;

		var events = new[]
		{
			new SwitchEvent { Time = _to.AddMinutes(-50), InterfaceId = _interfaceId, Kind = SwitchKind.Stay, From = "Office", Reason = "Staying on Office: fast enough (70 Mbps)" },
			new SwitchEvent { Time = _to.AddMinutes(-40), InterfaceId = _interfaceId, Kind = SwitchKind.Switch, From = "EXT2", To = "EXT", Reason = "Switched EXT2 -> EXT: about 77 Mbps vs 8 Mbps" }
		};

		var snapshot = HistorySnapshot.Build(null, speeds, events, null, null, _from, _to, ToLocal);

		// Newest first and limited
		Assert.AreEqual(HistorySnapshot.MaxRowCount, snapshot.Measurements.Count);

		var row = snapshot.Measurements[0];
		Assert.AreEqual(ToLocal(speeds[speeds.Length - 1].Time).ToString("yyyy-MM-dd HH:mm:ss"), row.Time);
		Assert.AreEqual("Office", row.Network);
		Assert.AreEqual("AA:BB:CC:DD:EE:FF", row.Bssid);
		Assert.AreEqual("66", row.Signal);
		Assert.AreEqual("390", row.LinkRate);
		Assert.AreEqual("18.3", row.Latency);
		Assert.AreEqual("519", row.Down);
		Assert.AreEqual("12.3", row.Up);
		Assert.AreEqual("Manual", row.Trigger);
		Assert.AreEqual("Ok", row.Result);

		Assert.AreEqual(2, snapshot.Decisions.Count);
		Assert.AreEqual("Switch", snapshot.Decisions[0].Kind);
		Assert.AreEqual("EXT2", snapshot.Decisions[0].From);
		Assert.AreEqual("EXT", snapshot.Decisions[0].To);
		Assert.AreEqual("Switched EXT2 -> EXT: about 77 Mbps vs 8 Mbps", snapshot.Decisions[0].Reason);
		Assert.AreEqual("Stay", snapshot.Decisions[1].Kind);
		Assert.AreEqual(string.Empty, snapshot.Decisions[1].To);
	}

	[TestMethod]
	public void TestReduceSignals()
	{
		// A gap longer than 15 minutes breaks the line.
		var samples = new[]
		{
			CreateSignal("Office", 100, 50),
			CreateSignal("Office", 95, 55),
			CreateSignal("Office", 70, 60),
			CreateSignal("Office", 65, 65)
		};
		var points = HistorySnapshot.ReduceSignals(samples, _from, _to, ToLocal);
		Assert.AreEqual(4, points.Count);
		CollectionAssert.AreEqual(new[] { false, false, true, false }, points.Select(x => x.IsBreak).ToArray());
		Assert.AreEqual(ToLocal(_to.AddMinutes(-100)), points[0].Time);

		// Many samples in a section are reduced to the first, the minimum, the maximum and the last.
		var from = _to.AddDays(-30);
		var section = TimeSpan.FromTicks((_to - from).Ticks / HistorySnapshot.SectionCount);
		Assert.IsTrue(section > TimeSpan.FromMinutes(20));

		var values = new[] { 50, 52, 30, 54, 90, 56, 58 };
		samples = values
			.Select((x, i) => new SignalSample { Time = from + TimeSpan.FromTicks(section.Ticks * 10) + TimeSpan.FromMinutes(i * 2), InterfaceId = _interfaceId, Profile = "Office", Signal = x })
			.ToArray();
		points = HistorySnapshot.ReduceSignals(samples, from, _to, ToLocal);
		CollectionAssert.AreEqual(new[] { 50D, 30D, 90D, 58D }, points.Select(x => x.Value).ToArray());
		Assert.IsTrue(points.All(x => !x.IsBreak));

		// Reduction does not add or lose breaks.
		var random = new Random(1);
		var time = from;
		var many = new List<SignalSample>();
		var gapCount = 0;
		for (int i = 0; i < 20000; i++)
		{
			var isGap = (i > 0) && (random.Next(200) == 0);
			if (isGap)
				gapCount++;

			time += isGap ? TimeSpan.FromMinutes(16) : TimeSpan.FromSeconds(30 + random.Next(60));
			many.Add(new SignalSample { Time = time, InterfaceId = _interfaceId, Profile = "Office", Signal = 40 + random.Next(40) });
		}
		points = HistorySnapshot.ReduceSignals(many, from, _to, ToLocal);
		Assert.IsTrue(points.Count <= HistorySnapshot.SectionCount * 4 + gapCount * 4);
		Assert.IsTrue(points.Count < many.Count);
		Assert.AreEqual(gapCount, points.Count(x => x.IsBreak));
		Assert.IsTrue(points.Zip(points.Skip(1), (a, b) => a.Time <= b.Time).All(x => x));

		Assert.AreEqual(0, HistorySnapshot.ReduceSignals(null, _from, _to, ToLocal).Count);
	}

	[TestMethod]
	public void TestMedian()
	{
		Assert.IsNull(HistorySnapshot.GetMedian(new double?[] { null }));
		Assert.AreEqual(3D, HistorySnapshot.GetMedian(new double?[] { 5, null, 1, 3 }));
		Assert.AreEqual(2D, HistorySnapshot.GetMedian(new double?[] { 3, 1 }));
	}

	#endregion

	#region Helper

	private static SignalSample CreateSignal(string name, int minutesAgo, int signal, Guid? interfaceId = null) =>
		new()
		{
			Time = _to.AddMinutes(-minutesAgo),
			InterfaceId = interfaceId ?? _interfaceId,
			Profile = name,
			Signal = signal
		};

	private static SpeedSample CreateSpeed(string name, int minutesAgo, SpeedResult result, double? down, double? up, double? latency) =>
		new()
		{
			Time = _to.AddMinutes(-minutesAgo),
			InterfaceId = _interfaceId,
			Profile = name,
			Result = result,
			DownMbps = down,
			UpMbps = up,
			LatencyMs = latency
		};

	private static ProfileItem CreateProfileItem(string name, Guid interfaceId, int signal, string description = "Adapter") =>
		new(
			name: name,
			interfaceId: interfaceId,
			interfaceName: "Wi-Fi",
			interfaceDescription: description,
			authentication: AuthenticationMethod.WPA2_Personal,
			encryption: EncryptionType.AES,
			isAutoConnectEnabled: true,
			isAutoSwitchEnabled: true,
			position: 0,
			isRadioOn: true,
			isConnected: false,
			protocol: "ax",
			signal: signal,
			band: 5F,
			channel: 36);

	#endregion
}
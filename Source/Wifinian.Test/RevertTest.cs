using System;
using System.Collections.Generic;
using System.Linq;
using ManagedNativeWifi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Wlan;

namespace Wifinian.Test;

/// <summary>
/// Replay of switching to a network which looks fast in history, finding it slow and switching
/// back, and tests of how a test of networks is summed up.
/// </summary>
[TestClass]
public class RevertTest
{
	private static readonly DateTime _utcNow = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
	private static readonly DateTime _now = new(2026, 11, 18, 5, 0, 0, DateTimeKind.Local);
	private static readonly Guid _interfaceId = new("11111111-2222-3333-4444-555555555555");

	private readonly Dictionary<string, List<SpeedSample>> _samples = new();

	#region Revert

	[TestMethod]
	public void TestReplayRevertDoesNotLoop()
	{
		// The live test of the owner: EXT is connected and measures about 95 Mbps. EXT2 is slow
		// (18 Mbps) but rows saying 300 Mbps have been put into its history, enough of them to
		// keep its typical speed high whatever is measured afterwards.
		var ext = Profile("EXT", 83, 0, isConnected: true);
		var ext2 = Profile("EXT2", 45, 1);
		var profiles = new[] { ext, ext2 };

		AddSample("EXT", TimeSpan.FromMinutes(5), 95, 83);
		AddSample("EXT2", TimeSpan.FromHours(10), 18.18, 45);
		foreach (var minutes in new[] { 4, 3, 2, 1 })
			AddSample("EXT2", TimeSpan.FromMinutes(minutes), 300, 45);

		var state = new SwitchState();
		SwitchPolicy.Track(state, "EXT", _now - TimeSpan.FromMinutes(20));

		// (1) With the default speed regarded as enough (50 Mbps), EXT is never left.
		var decision = SwitchPolicy.Decide(profiles, ext, CreateContext(TimeSpan.Zero, sufficientSpeed: 50), state);
		Assert.IsNull(decision.Target);
		StringAssert.StartsWith(decision.Code, "enough|");

		// (2) With it raised above the speed of EXT, the faster-looking EXT2 is confirmed for
		// 30 seconds and then switched to.
		decision = SwitchPolicy.Decide(profiles, ext, CreateContext(TimeSpan.Zero), state);
		Assert.IsNull(decision.Target);
		Assert.AreEqual("confirm|EXT|EXT2", decision.Code);
		StringAssert.Contains(decision.Reason, "EXT2 looks faster (300 vs 95 Mbps)");

		var elapsed = SwitchPolicy.PersistPeriod + TimeSpan.FromSeconds(1);
		decision = SwitchPolicy.Decide(profiles, ext, CreateContext(elapsed), state);
		Assert.AreSame(ext2, decision.Target);
		Assert.AreEqual("faster|EXT|EXT2", decision.Code);
		Assert.AreEqual("Switched EXT -> EXT2: about 300 Mbps vs 95 Mbps", decision.Reason);

		// The switch is made and recorded as the controller does.
		SwitchPolicy.RecordAttempt(state, "EXT2", true, _now + elapsed, setsCooldown: true);
		var landing = new SwitchLanding
		{
			FromName = "EXT",
			FromScore = SwitchPolicy.GetRevertScore(decision),
			ToName = "EXT2",
			Time = _utcNow + elapsed
		};
		Assert.AreEqual(95D, landing.FromScore);

		Connect(profiles, ext2);
		SwitchPolicy.Track(state, "EXT2", _now + elapsed);

		// (3) 20 seconds later EXT2 measures 18 Mbps, lower than 80% of 95: it is tested again.
		elapsed += TimeSpan.FromSeconds(20);
		var first = AddSample("EXT2", -elapsed, 18, 45, SpeedTrigger.PostSwitch);
		Assert.AreEqual(LandingAction.Retest, SwitchPolicy.EvaluateLanding(landing, first, _utcNow + elapsed));
		Assert.IsFalse(SwitchPolicy.IsLandingKept(landing, first));

		// Meanwhile nothing is done on EXT2: it has just been connected to.
		decision = SwitchPolicy.Decide(profiles, ext2, CreateContext(elapsed), state);
		Assert.IsNull(decision.Target);

		// (4) 60 seconds later it measures low again: switching back.
		elapsed += TimeSpan.FromSeconds(60);
		var second = AddSample("EXT2", -elapsed, 19, 45, SpeedTrigger.PostSwitch);
		Assert.AreEqual(LandingAction.Revert, SwitchPolicy.EvaluateLanding(landing, second, _utcNow + elapsed));

		// The controller excludes EXT2 for a while, connects to EXT and records the attempt.
		state.CooldownUntil["EXT2"] = _now + elapsed + SwitchPolicy.RevertCooldown;
		state.ClearPending();
		SwitchPolicy.RecordAttempt(state, "EXT", true, _now + elapsed, setsCooldown: false);

		Connect(profiles, ext);
		SwitchPolicy.Track(state, "EXT", _now + elapsed);
		AddSample("EXT", -elapsed - TimeSpan.FromSeconds(30), 96, 83);

		// (5) The typical speed of EXT2 is still high: four rows of 300 against three of 18.
		Assert.AreEqual(300D, SpeedSummary.Create(_samples["EXT2"], _utcNow + elapsed).DownMbps);

		// Nevertheless EXT2 is not switched to again, at any time while it is excluded...
		foreach (var minutes in new[] { 1, 11, 30, 60, 119 })
		{
			var later = elapsed + TimeSpan.FromMinutes(minutes);
			AddSample("EXT", -later, 95, 83); // Periodic measurements go on.

			decision = SwitchPolicy.Decide(profiles, ext, CreateContext(later), state);
			Assert.IsNull(decision.Target, $"{minutes} min after switching back");
			Assert.IsNull(state.PendingTarget, $"{minutes} min after switching back");
		}

		// ...nor after the exclusion has ended, because its newest measurement says 19 Mbps...
		foreach (var hours in new[] { 3, 6, 12, 23 })
		{
			var later = elapsed + TimeSpan.FromHours(hours);
			AddSample("EXT", -later, 95, 83);

			decision = SwitchPolicy.Decide(profiles, ext, CreateContext(later), state);
			Assert.IsNull(decision.Target, $"{hours} h after switching back");
			Assert.IsNull(state.PendingTarget, $"{hours} h after switching back");
			StringAssert.Contains(decision.Reason, "no better network in range");
		}

		// ...nor when this app has been started again and so has forgotten the exclusion.
		var restarted = new SwitchState();
		SwitchPolicy.Track(restarted, "EXT", _now + elapsed);

		foreach (var minutes in new[] { 15, 16, 60 })
		{
			var later = elapsed + TimeSpan.FromMinutes(minutes);

			decision = SwitchPolicy.Decide(profiles, ext, CreateContext(later), restarted);
			Assert.IsNull(decision.Target, $"{minutes} min after restart");
			Assert.IsNull(restarted.PendingTarget, $"{minutes} min after restart");
		}

		// The number of switches in the hour was two: there and back.
		Assert.AreEqual(2, state.SwitchTimes.Count);
	}

	[TestMethod]
	public void TestRecentLowSampleOutweighsTypicalSpeed()
	{
		var a = Profile("A", 80, 0, isConnected: true);
		var b = Profile("B", 70, 1);

		AddSample("A", TimeSpan.FromMinutes(5), 20, 80);

		// B was fast for days and its newest measurement, an hour ago, is slow.
		foreach (var days in new[] { 5, 4, 3, 2 })
			AddSample("B", TimeSpan.FromDays(days), 80, 70);
		AddSample("B", TimeSpan.FromHours(1), 12, 70);

		var state = new SwitchState();
		SwitchPolicy.Track(state, "A", _now - TimeSpan.FromMinutes(20));

		Assert.AreEqual(80D, SpeedSummary.Create(_samples["B"], _utcNow).DownMbps);

		var decision = SwitchPolicy.Decide([a, b], a, CreateContext(TimeSpan.Zero, sufficientSpeed: 50), state);
		Assert.IsNull(decision.Target);
		Assert.IsNull(state.PendingTarget);
		StringAssert.Contains(decision.Reason, "no better network in range");

		// Once that measurement is older than a day, the typical speed alone counts again.
		var elapsed = SwitchPolicy.RecentPeriod;
		AddSample("A", -elapsed + TimeSpan.FromMinutes(5), 20, 80);

		decision = SwitchPolicy.Decide([a, b], a, CreateContext(elapsed, sufficientSpeed: 50), state);
		Assert.AreEqual("confirm|A|B", decision.Code);

		// A newest measurement which is as fast as the typical speed changes nothing.
		_samples.Clear();
		AddSample("A", TimeSpan.FromMinutes(5), 20, 80);
		AddSample("B", TimeSpan.FromDays(2), 60, 70);
		AddSample("B", TimeSpan.FromHours(1), 90, 70);

		state = new SwitchState();
		SwitchPolicy.Track(state, "A", _now - TimeSpan.FromMinutes(20));

		decision = SwitchPolicy.Decide([a, b], a, CreateContext(TimeSpan.Zero, sufficientSpeed: 50), state);
		Assert.AreEqual("confirm|A|B", decision.Code);
		StringAssert.Contains(decision.Reason, "B looks faster (90 vs 20 Mbps)");
	}

	[TestMethod]
	public void TestIsLandingKept()
	{
		var landing = new SwitchLanding { FromName = "A", FromScore = 30, ToName = "B", Time = _utcNow };

		Assert.IsTrue(SwitchPolicy.IsLandingKept(landing, Sample("B", TimeSpan.Zero, 24, 70)));
		Assert.IsFalse(SwitchPolicy.IsLandingKept(landing, Sample("B", TimeSpan.Zero, 23.9, 70)));
		Assert.IsFalse(SwitchPolicy.IsLandingKept(landing, Sample("C", TimeSpan.Zero, 90, 70)));
		Assert.IsFalse(SwitchPolicy.IsLandingKept(null, Sample("B", TimeSpan.Zero, 90, 70)));
		Assert.IsFalse(SwitchPolicy.IsLandingKept(landing, null));

		var failed = Sample("B", TimeSpan.Zero, null, 70);
		failed.Result = SpeedResult.EndpointFailed;
		Assert.IsFalse(SwitchPolicy.IsLandingKept(landing, failed));

		// A switch from a network which cannot be switched back to is not compared.
		Assert.IsFalse(SwitchPolicy.IsLandingKept(new SwitchLanding { FromName = "A", FromScore = null, ToName = "B" }, Sample("B", TimeSpan.Zero, 90, 70)));
		Assert.IsFalse(SwitchPolicy.IsLandingKept(new SwitchLanding { FromName = "A", FromScore = 0, ToName = "B" }, Sample("B", TimeSpan.Zero, 90, 70)));
	}

	#endregion

	#region Test of networks

	[TestMethod]
	public void TestProbeSummary()
	{
		static string Get(int total, string[] tested, string[] notTested, string original, AppController.ProbeEnd end, AppController.ProbeReturn result) =>
			AppController.GetProbeSummary(total, tested, notTested, original, end, result);

		var none = Array.Empty<string>();

		Assert.AreEqual("Tested 3 networks, back on EXT",
			Get(3, ["EXT", "EXT2", "Office"], none, "EXT", AppController.ProbeEnd.Completed, AppController.ProbeReturn.Restored));

		Assert.AreEqual("Tested 1 network, still on EXT",
			Get(1, ["EXT"], none, "EXT", AppController.ProbeEnd.Completed, AppController.ProbeReturn.Unchanged));

		Assert.AreEqual("Tested 1 of 3 networks (could not test EXT2, Office), back on EXT",
			Get(3, ["EXT"], ["EXT2", "Office"], "EXT", AppController.ProbeEnd.Completed, AppController.ProbeReturn.Restored));

		Assert.AreEqual("Tested 2 of 3 networks (could not test EXT2), could not return to EXT",
			Get(3, ["EXT", "Office"], ["EXT2"], "EXT", AppController.ProbeEnd.Completed, AppController.ProbeReturn.Failed));

		Assert.AreEqual("Tested 2 networks, no network was connected before",
			Get(2, ["EXT", "Office"], none, null, AppController.ProbeEnd.Completed, AppController.ProbeReturn.NotConnectedBefore));

		Assert.AreEqual("Test stopped after 1 of 3 networks, back on EXT",
			Get(3, ["EXT"], none, "EXT", AppController.ProbeEnd.Canceled, AppController.ProbeReturn.Restored));

		Assert.AreEqual("Test interrupted after 2 of 3 networks, the connection was changed meanwhile and is left as it is",
			Get(3, ["EXT", "EXT2"], none, "EXT", AppController.ProbeEnd.Interrupted, AppController.ProbeReturn.LeftToUser));

		Assert.AreEqual("Test ended by closing this app after 1 of 3 networks, back on EXT",
			Get(3, ["EXT"], none, "EXT", AppController.ProbeEnd.Closing, AppController.ProbeReturn.Restored));

		Assert.AreEqual("Test ended by closing this app after 1 of 3 networks, returning to EXT (asked for, not confirmed)",
			Get(3, ["EXT"], none, "EXT", AppController.ProbeEnd.Closing, AppController.ProbeReturn.RequestedOnClosing));

		Assert.AreEqual("Test ended by closing this app after 0 of 1 network, still on EXT",
			Get(1, null, null, "EXT", AppController.ProbeEnd.Closing, AppController.ProbeReturn.Unchanged));

		// The user connected to another network before the test left the original one: the test
		// did not change the connection but the interface is no longer on that network.
		Assert.AreEqual(AppController.ProbeReturn.LeftToUser, AppController.GetUnchangedReturn(true, true));
		Assert.AreEqual(AppController.ProbeReturn.Unchanged, AppController.GetUnchangedReturn(true, false));
		Assert.AreEqual(AppController.ProbeReturn.NotConnectedBefore, AppController.GetUnchangedReturn(false, true));
		Assert.AreEqual(AppController.ProbeReturn.NotConnectedBefore, AppController.GetUnchangedReturn(false, false));

		Assert.AreEqual("Test interrupted after 0 of 3 networks, the connection was changed meanwhile and is left as it is",
			Get(3, none, none, "EXT", AppController.ProbeEnd.Interrupted, AppController.GetUnchangedReturn(true, true)));
	}

	[TestMethod]
	public void TestPendingTicket()
	{
		var tickets = new AppController.PendingTicket();
		var first = Guid.NewGuid();
		var second = Guid.NewGuid();

		Assert.IsFalse(tickets.IsLatest(first, 0));

		// The measurement which could not run schedules the next one (A) and then the measurement
		// which found the network slow schedules another (B): only B is run.
		var a = tickets.Take(first);
		Assert.IsTrue(tickets.IsLatest(first, a));

		var b = tickets.Take(first);
		Assert.IsFalse(tickets.IsLatest(first, a));
		Assert.IsTrue(tickets.IsLatest(first, b));

		// Another interface is not affected.
		var c = tickets.Take(second);
		Assert.IsTrue(tickets.IsLatest(first, b));
		Assert.IsTrue(tickets.IsLatest(second, c));
	}

	#endregion

	#region Base

	private SwitchContext CreateContext(TimeSpan elapsed, double sufficientSpeed = 1000)
	{
		var utcNow = _utcNow + elapsed;

		return new SwitchContext
		{
			EngagesSpeed = true,
			SignalThreshold = 50,
			SpeedSignalThreshold = 30,
			DwellTime = TimeSpan.FromMinutes(2),
			MarginPercent = 50,
			SufficientSpeed = sufficientSpeed,
			FailureInterval = TimeSpan.FromMinutes(3),
			FreshPeriod = TimeSpan.FromMinutes(120),
			MaxDeferral = TimeSpan.Zero,
			IsLinkBusy = false,
			Now = _now + elapsed,
			UtcNow = utcNow,
			GetSummary = p => _samples.TryGetValue(p.Name, out List<SpeedSample> samples)
				? SpeedSummary.Create(samples.Where(x => x.Time <= utcNow).ToArray(), utcNow)
				: null
		};
	}

	private SpeedSample AddSample(string name, TimeSpan age, double? down, int signal, SpeedTrigger trigger = SpeedTrigger.Periodic)
	{
		if (!_samples.TryGetValue(name, out List<SpeedSample> samples))
		{
			samples = new List<SpeedSample>();
			_samples[name] = samples;
		}

		var sample = Sample(name, age, down, signal);
		sample.Trigger = trigger;
		samples.Add(sample);
		return sample;
	}

	private static SpeedSample Sample(string name, TimeSpan age, double? down, int signal) => new()
	{
		Time = _utcNow - age,
		InterfaceId = _interfaceId,
		Profile = name,
		Signal = signal,
		DownMbps = down,
		Trigger = SpeedTrigger.Periodic,
		Result = SpeedResult.Ok
	};

	private static void Connect(ProfileItem[] profiles, ProfileItem connected)
	{
		foreach (var profile in profiles)
			profile.IsConnected = ReferenceEquals(profile, connected);
	}

	private static ProfileItem Profile(string name, int signal, int position, bool isConnected = false) => new(
		name: name,
		interfaceId: _interfaceId,
		interfaceName: null,
		interfaceDescription: null,
		authentication: default(AuthenticationMethod),
		encryption: default(EncryptionType),
		isAutoConnectEnabled: true,
		isAutoSwitchEnabled: true,
		position: position,
		isRadioOn: true,
		isConnected: isConnected,
		protocol: null,
		signal: signal,
		band: 5F,
		channel: 36);

	#endregion
}

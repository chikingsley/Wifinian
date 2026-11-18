using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManagedNativeWifi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Measure;
using Wifinian.Models.Wlan;

namespace Wifinian.Test;

/// <summary>
/// Tests of which network a speed measurement is recorded for, and replay of the live test in
/// which a measurement after a switch was recorded for the network just left.
/// </summary>
[TestClass]
public class MeasureIdentityTest
{
	private static readonly DateTime _utcNow = new(2026, 11, 17, 16, 33, 26, DateTimeKind.Utc);
	private static readonly DateTime _now = new(2026, 11, 17, 17, 33, 26, DateTimeKind.Local);
	private static readonly Guid _interfaceId = new("11111111-2222-3333-4444-555555555555");

	private const string ExtBssid = "50:3D:D1:51:E1:D1";
	private const string OfficeBssid = "0C:FE:7B:71:BC:59";

	// SSIDs in hexadecimal string as the worker formats them
	private const string ExtSsid = "45-58-54";
	private const string OfficeSsid = "4F-66-66-69-63-65";

	private readonly Dictionary<string, List<SpeedSample>> _samples = new();

	#region Start

	[TestMethod]
	public void TestStaleSnapshotIsOverruledByLiveConnection()
	{
		// The profiles as loaded still show EXT as connected while the adapter is on Office.
		var (status, name, isStale) = MeasureIdentity.ResolveStart("EXT", Live("Office", OfficeSsid, OfficeBssid));

		Assert.AreEqual(IdentityStart.Use, status);
		Assert.AreEqual("Office", name);
		Assert.IsTrue(isStale);

		// The sample is of Office in every respect, whatever the profiles say.
		var ext = Profile("EXT", 80, isConnected: true, bssid: ExtBssid, band: 2.4F, channel: 6, linkRate: 144);
		var office = Profile("Office", 60, bssid: null, band: 5F, channel: 36);

		var live = Live("Office", OfficeSsid, OfficeBssid, band: 5F, channel: 44, signal: 71, linkRate: 866);
		var sample = MeasureIdentity.CreateSample(live, office, SpeedTrigger.PostSwitch, _utcNow);

		Assert.AreEqual("Office", sample.Profile);
		Assert.AreEqual(OfficeBssid, sample.Bssid);
		Assert.AreEqual(5F, sample.Band);
		Assert.AreEqual(44, sample.Channel);
		Assert.AreEqual(71, sample.Signal);
		Assert.AreEqual(866, sample.LinkRate);
		Assert.AreEqual(SpeedTrigger.PostSwitch, sample.Trigger);
		Assert.AreEqual(_interfaceId, sample.InterfaceId);
		Assert.AreEqual(HistoryService.GetKey("Office", _interfaceId), sample.Key);
		Assert.AreNotEqual(ext.Bssid, sample.Bssid);
	}

	[TestMethod]
	public void TestStartAgreesWhenSnapshotIsCurrent()
	{
		var (status, name, isStale) = MeasureIdentity.ResolveStart("Office", Live("Office", OfficeSsid, OfficeBssid));

		Assert.AreEqual(IdentityStart.Use, status);
		Assert.AreEqual("Office", name);
		Assert.IsFalse(isStale);
	}

	[TestMethod]
	public void TestStartNotConnected()
	{
		// The profiles show a network but the adapter is not connected (or is still connecting).
		var (status, name, isStale) = MeasureIdentity.ResolveStart("EXT", new LiveConnection { InterfaceId = _interfaceId });
		Assert.AreEqual(IdentityStart.NotConnected, status);
		Assert.IsNull(name);
		Assert.IsTrue(isStale);

		(status, _, isStale) = MeasureIdentity.ResolveStart(null, new LiveConnection { InterfaceId = _interfaceId });
		Assert.AreEqual(IdentityStart.NotConnected, status);
		Assert.IsFalse(isStale);

		// The profiles show none but the adapter is connected.
		(status, name, isStale) = MeasureIdentity.ResolveStart(null, Live("Office", OfficeSsid, OfficeBssid));
		Assert.AreEqual(IdentityStart.Use, status);
		Assert.AreEqual("Office", name);
		Assert.IsTrue(isStale);
	}

	[TestMethod]
	public void TestStartExpectedTarget()
	{
		// The measurement after a switch is for the network switched to and for no other.
		var (status, name, _) = MeasureIdentity.ResolveStart("EXT", Live("EXT", ExtSsid, ExtBssid), "Office");
		Assert.AreEqual(IdentityStart.OtherNetwork, status);
		Assert.AreEqual("EXT", name);

		(status, _, _) = MeasureIdentity.ResolveStart("EXT", new LiveConnection { InterfaceId = _interfaceId }, "Office");
		Assert.AreEqual(IdentityStart.NotConnected, status);

		// The profiles are behind but the adapter is on the target: the measurement runs.
		(status, name, var isStale) = MeasureIdentity.ResolveStart("EXT", Live("Office", OfficeSsid, OfficeBssid), "Office");
		Assert.AreEqual(IdentityStart.Use, status);
		Assert.AreEqual("Office", name);
		Assert.IsTrue(isStale);
	}

	[TestMethod]
	public void TestStartWithoutLiveQuery()
	{
		// When the system cannot be asked, the profiles as loaded are all there is.
		var (status, name, isStale) = MeasureIdentity.ResolveStart("EXT", null);
		Assert.AreEqual(IdentityStart.Use, status);
		Assert.AreEqual("EXT", name);
		Assert.IsFalse(isStale);

		(status, _, _) = MeasureIdentity.ResolveStart(null, null);
		Assert.AreEqual(IdentityStart.NotConnected, status);

		(status, _, _) = MeasureIdentity.ResolveStart("EXT", null, "Office");
		Assert.AreEqual(IdentityStart.OtherNetwork, status);

		(status, _, _) = MeasureIdentity.ResolveStart("Office", null, "Office");
		Assert.AreEqual(IdentityStart.Use, status);
	}

	[TestMethod]
	public void TestSampleTakesFromSnapshotOnlyWhatIsOfTheSameBss()
	{
		// The list of BSSs lacked the BSS in use and so the connection tells no band or channel.
		var live = Live("Office", OfficeSsid, OfficeBssid, signal: 0, linkRate: 0);

		// The profile as loaded is of the same BSS: its radio values are of this connection.
		var same = Profile("Office", 64, isConnected: true, bssid: OfficeBssid.ToLowerInvariant(), band: 5F, channel: 36, linkRate: 433);
		var sample = MeasureIdentity.CreateSample(live, same, SpeedTrigger.Periodic, _utcNow);
		Assert.AreEqual(5F, sample.Band);
		Assert.AreEqual(36, sample.Channel);
		Assert.AreEqual(64, sample.Signal);
		Assert.AreEqual(433, sample.LinkRate);

		// It is of another BSS of the SSID (the strongest one seen when not connected): its values
		// are not of this connection and are left unknown rather than recorded wrongly.
		var other = Profile("Office", 90, bssid: "0C:FE:7B:71:BC:58", band: 2.4F, channel: 1, linkRate: 0);
		sample = MeasureIdentity.CreateSample(live, other, SpeedTrigger.Periodic, _utcNow);
		Assert.AreEqual(OfficeBssid, sample.Bssid);
		Assert.AreEqual(0F, sample.Band);
		Assert.AreEqual(0, sample.Channel);
		Assert.AreEqual(0, sample.Signal);
		Assert.IsNull(sample.LinkRate);

		// A profile of another name never supplies anything.
		var ext = Profile("EXT", 80, isConnected: true, bssid: OfficeBssid, band: 2.4F, channel: 6, linkRate: 144);
		sample = MeasureIdentity.CreateSample(live, ext, SpeedTrigger.Periodic, _utcNow);
		Assert.AreEqual("Office", sample.Profile);
		Assert.AreEqual(0F, sample.Band);
		Assert.IsNull(sample.LinkRate);

		sample = MeasureIdentity.CreateSample(live, null, SpeedTrigger.Periodic, _utcNow);
		Assert.AreEqual("Office", sample.Profile);
		Assert.AreEqual(OfficeBssid, sample.Bssid);
	}

	#endregion

	#region End

	[TestMethod]
	public void TestNetworkChangedDuringTestIsInvalid()
	{
		var start = Live("EXT", ExtSsid, ExtBssid);

		// The adapter moved to another network while the measurement ran.
		var sample = Measured(start, 150);
		Assert.AreEqual(IdentityEnd.Changed, MeasureIdentity.ApplyEnd(sample, start, Live("Office", OfficeSsid, OfficeBssid)));
		Assert.AreEqual(SpeedResult.Aborted, sample.Result);
		Assert.IsFalse(sample.Valid);
		Assert.AreEqual("Network changed during the test: EXT -> Office", sample.Detail);

		// It is evidence for neither network: the check of a switch ignores it and repeats.
		var landing = new SwitchLanding { FromName = "Office", FromScore = 200, ToName = "EXT", Time = _utcNow };
		Assert.AreEqual(LandingAction.None, SwitchPolicy.EvaluateLanding(landing, sample, _utcNow));
		Assert.IsTrue(AppController.IsLandingUnchecked(sample));

		// The adapter was disconnected.
		sample = Measured(start, 150);
		Assert.AreEqual(IdentityEnd.Disconnected, MeasureIdentity.ApplyEnd(sample, start, new LiveConnection { InterfaceId = _interfaceId }));
		Assert.AreEqual(SpeedResult.Aborted, sample.Result);
		Assert.AreEqual("Network changed during the test: EXT was disconnected", sample.Detail);

		// What the test itself noted is kept after the reason.
		sample = Measured(start, null);
		sample.Result = SpeedResult.EndpointFailed;
		sample.Detail = "HTTP 429";
		MeasureIdentity.ApplyEnd(sample, start, Live("Office", OfficeSsid, OfficeBssid));
		Assert.AreEqual(SpeedResult.Aborted, sample.Result);
		Assert.AreEqual("Network changed during the test: EXT -> Office; HTTP 429", sample.Detail);
	}

	[TestMethod]
	public void TestRoamingWithinSsidStaysValid()
	{
		var start = Live("Office", OfficeSsid, OfficeBssid);
		var roamed = Live("Office", OfficeSsid, "0C:FE:7B:71:BC:5A");

		var sample = Measured(start, 300);
		Assert.AreEqual(IdentityEnd.Roamed, MeasureIdentity.ApplyEnd(sample, start, roamed));
		Assert.AreEqual(SpeedResult.Ok, sample.Result);
		Assert.IsTrue(sample.Valid);
		Assert.AreEqual($"Roamed during the test: {OfficeBssid} -> 0C:FE:7B:71:BC:5A", sample.Detail);

		// The note comes after what the test left, which is read from the beginning.
		sample = Measured(start, 300);
		sample.Detail = SpeedSample.ServerNote + "example";
		MeasureIdentity.ApplyEnd(sample, start, roamed);
		Assert.IsTrue(sample.IsFallback);
		StringAssert.EndsWith(sample.Detail, "; " + MeasureIdentity.RoamNote + $"{OfficeBssid} -> 0C:FE:7B:71:BC:5A");

		// The same BSS in another letter case is no roaming.
		sample = Measured(start, 300);
		Assert.AreEqual(IdentityEnd.Same, MeasureIdentity.ApplyEnd(sample, start, Live("Office", OfficeSsid, OfficeBssid.ToLowerInvariant())));
		Assert.AreEqual(SpeedResult.Ok, sample.Result);
		Assert.IsNull(sample.Detail);
	}

	[TestMethod]
	public void TestEndComparesBySsidThenByProfile()
	{
		var start = Live("Office", OfficeSsid, OfficeBssid);

		// Another profile of the same SSID is the same network.
		Assert.AreEqual(IdentityEnd.Same, MeasureIdentity.ResolveEnd(start, Live("Office 2", OfficeSsid, OfficeBssid)));

		// The same profile name on another SSID is not.
		Assert.AreEqual(IdentityEnd.Changed, MeasureIdentity.ResolveEnd(start, Live("Office", ExtSsid, ExtBssid)));

		// Without SSID (the profiles as loaded), the profile name decides.
		var cached = MeasureIdentity.FromProfile(Profile("Office", 60, isConnected: true, bssid: OfficeBssid), _interfaceId);
		Assert.IsNull(cached.Ssid);
		Assert.AreEqual(IdentityEnd.Same, MeasureIdentity.ResolveEnd(cached, cached));
		Assert.AreEqual(IdentityEnd.Changed, MeasureIdentity.ResolveEnd(cached,
			MeasureIdentity.FromProfile(Profile("EXT", 80, isConnected: true, bssid: ExtBssid), _interfaceId)));
		Assert.AreEqual(IdentityEnd.Disconnected, MeasureIdentity.ResolveEnd(cached, MeasureIdentity.FromProfile(null, _interfaceId)));
		Assert.AreEqual(IdentityEnd.Disconnected, MeasureIdentity.ResolveEnd(cached, null));

		// A profile no longer shown as connected is no connection.
		Assert.IsFalse(MeasureIdentity.FromProfile(Profile("Office", 60), _interfaceId).IsConnected);
	}

	#endregion

	#region Wait

	[TestMethod]
	public async Task TestWaitsUntilTargetIsConnectedThenTests()
	{
		// At the scheduled time the adapter is still connecting, then for a moment still shown
		// on the previous network, then on the target.
		var answers = new Queue<LiveConnection>(new[]
		{
			new LiveConnection { InterfaceId = _interfaceId },
			Live("EXT", ExtSsid, ExtBssid),
			Live("Office", OfficeSsid, OfficeBssid)
		});

		var delays = new List<TimeSpan>();
		var waitings = new List<string>();
		LiveConnection last = null;

		var wait = await MeasureIdentity.WaitForTargetAsync(
			() => last = answers.Dequeue(),
			"Office",
			TimeSpan.FromMinutes(5),
			TimeSpan.FromSeconds(2),
			onWaiting: x => waitings.Add(x.Describe()),
			delay: x => { delays.Add(x); return Task.CompletedTask; });

		Assert.AreEqual(TargetWait.OnTarget, wait);
		Assert.AreEqual(0, answers.Count);
		Assert.AreEqual(2, delays.Count);
		CollectionAssert.AreEqual(new[] { "no network" }, waitings); // Told once

		// Then the measurement starts and is recorded for the target.
		var (status, name, _) = MeasureIdentity.ResolveStart("EXT", last, "Office");
		Assert.AreEqual(IdentityStart.Use, status);
		Assert.AreEqual("Office", name);
	}

	[TestMethod]
	public async Task TestWaitDoesNotWaitWhenAlreadyOnTarget()
	{
		var count = 0;

		var wait = await MeasureIdentity.WaitForTargetAsync(
			() => { count++; return Live("Office", OfficeSsid, OfficeBssid); },
			"Office",
			TimeSpan.FromMinutes(5),
			TimeSpan.FromSeconds(2),
			onWaiting: _ => Assert.Fail(),
			delay: _ => { Assert.Fail(); return Task.CompletedTask; });

		Assert.AreEqual(TargetWait.OnTarget, wait);
		Assert.AreEqual(1, count);
	}

	[TestMethod]
	public async Task TestWaitIsBounded()
	{
		// The adapter stays on another network: the wait ends with the record of the switch.
		var count = 0;

		var wait = await MeasureIdentity.WaitForTargetAsync(
			() => { count++; return Live("EXT", ExtSsid, ExtBssid); },
			"Office",
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(2),
			delay: _ => Task.CompletedTask);

		Assert.AreEqual(TargetWait.TimedOut, wait);
		Assert.AreEqual(6, count); // At 0, 2, 4, 6, 8 and 10 seconds

		// No time is left: the connection is asked once.
		count = 0;
		wait = await MeasureIdentity.WaitForTargetAsync(
			() => { count++; return Live("EXT", ExtSsid, ExtBssid); },
			"Office",
			TimeSpan.FromSeconds(-1),
			TimeSpan.FromSeconds(2),
			delay: _ => Task.CompletedTask);

		Assert.AreEqual(TargetWait.TimedOut, wait);
		Assert.AreEqual(1, count);
	}

	[TestMethod]
	public async Task TestWaitCanceledOrUnknown()
	{
		// The switch has been checked or switched back from in the meantime.
		var count = 0;
		var wait = await MeasureIdentity.WaitForTargetAsync(
			() => { count++; return Live("EXT", ExtSsid, ExtBssid); },
			"Office",
			TimeSpan.FromMinutes(5),
			TimeSpan.FromSeconds(2),
			isCanceled: () => count >= 3,
			delay: _ => Task.CompletedTask);

		Assert.AreEqual(TargetWait.Canceled, wait);
		Assert.AreEqual(3, count);

		// The system cannot be asked: the caller relies on the profiles as loaded.
		wait = await MeasureIdentity.WaitForTargetAsync(() => null, "Office", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
		Assert.AreEqual(TargetWait.Unknown, wait);

		wait = await MeasureIdentity.WaitForTargetAsync(null, "Office", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
		Assert.AreEqual(TargetWait.Unknown, wait);

		// A failing query must not break the caller.
		wait = await MeasureIdentity.WaitForTargetAsync(() => throw new InvalidOperationException(), "Office", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
		Assert.AreEqual(TargetWait.Unknown, wait);
	}

	#endregion

	#region Replay

	[TestMethod]
	public void TestReplayStaleSnapshotAfterSwitchEndsInRevert()
	{
		// The live test of the owner (UTC). EXT is connected and scores 200 Mbps; Office looks
		// faster by its history (400 Mbps) but measures about 90 Mbps when switched to.
		var ext = Profile("EXT", 80, isConnected: true, bssid: ExtBssid, band: 2.4F, channel: 6, position: 0);
		var office = Profile("Office", 70, bssid: OfficeBssid, band: 5F, channel: 44, position: 1);
		var profiles = new[] { ext, office };

		AddSample("EXT", TimeSpan.FromMinutes(5), 200, 80);
		AddSample("Office", TimeSpan.FromMinutes(10), 400, 70);

		var state = new SwitchState();
		SwitchPolicy.Track(state, "EXT", _now - TimeSpan.FromMinutes(20));

		// 16:33:26 "Office looks faster (400 vs 200 Mbps) - confirming"
		var decision = SwitchPolicy.Decide(profiles, ext, CreateContext(TimeSpan.Zero), state);
		Assert.IsNull(decision.Target);
		Assert.AreEqual("confirm|EXT|Office", decision.Code);
		StringAssert.Contains(decision.Reason, "Office looks faster (400 vs 200 Mbps)");

		// 16:34:23 "Switched EXT -> Office"
		var elapsed = TimeSpan.FromSeconds(57);
		decision = SwitchPolicy.Decide(profiles, ext, CreateContext(elapsed), state);
		Assert.AreSame(office, decision.Target);
		Assert.AreEqual("faster|EXT|Office", decision.Code);

		// The switch is recorded as the controller does: the time of connection is that of
		// the switch itself and does not wait for the profiles to be loaded.
		var fromName = state.ConnectedName;
		SwitchPolicy.RecordAttempt(state, "Office", true, _now + elapsed, setsCooldown: true);
		SwitchPolicy.Track(state, "Office", _now + elapsed);
		Assert.AreEqual("EXT", fromName);
		Assert.AreEqual("Office", state.ConnectedName);
		Assert.AreEqual(_now + elapsed, state.ConnectedSince);

		var landing = new SwitchLanding
		{
			FromName = fromName,
			FromScore = SwitchPolicy.GetRevertScore(decision),
			ToName = "Office",
			Time = _utcNow + elapsed
		};
		Assert.AreEqual(200D, landing.FromScore);

		// 16:34:43 The measurement after the switch is due. The load asked for by the connection
		// was skipped and so the profiles still show EXT as connected, with the BSSID of EXT.
		// The adapter itself is on Office.
		elapsed += TimeSpan.FromSeconds(20);
		Assert.IsTrue(ext.IsConnected);
		Assert.IsFalse(office.IsConnected);

		var live = Live("Office", OfficeSsid, OfficeBssid, band: 5F, channel: 44, signal: 70, linkRate: 866);

		// What was recorded then: a sample of EXT, which ended the check without a finding.
		var mislabelled = Sample(ext.Name, -elapsed, 89.57, 80);
		mislabelled.Bssid = ext.Bssid;
		mislabelled.Trigger = SpeedTrigger.PostSwitch;
		var wrongLanding = new SwitchLanding { FromName = landing.FromName, FromScore = landing.FromScore, ToName = landing.ToName, Time = landing.Time };
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(wrongLanding, mislabelled, _utcNow + elapsed));
		Assert.AreEqual(0, wrongLanding.LowCount);

		// What is recorded now: the connection is asked of the system when the measurement starts.
		var cachedName = profiles.First(x => x.IsConnected).Name;
		var (status, name, isStale) = MeasureIdentity.ResolveStart(cachedName, live, landing.ToName);
		Assert.AreEqual(IdentityStart.Use, status);
		Assert.AreEqual("Office", name);
		Assert.IsTrue(isStale); // The profiles are loaded again.

		var first = MeasureIdentity.CreateSample(live, profiles.First(x => x.Name == name), SpeedTrigger.PostSwitch, _utcNow + elapsed);
		first.Result = SpeedResult.Ok;
		first.DownMbps = 89.57;
		Assert.AreEqual(IdentityEnd.Same, MeasureIdentity.ApplyEnd(first, live, live));
		Record(first);

		Assert.AreEqual("Office", first.Profile);
		Assert.AreEqual(OfficeBssid, first.Bssid);
		Assert.IsTrue(first.Valid);

		// Nothing was written into the history of EXT.
		Assert.AreEqual(1, _samples["EXT"].Count);
		Assert.AreEqual(2, _samples["Office"].Count);

		// 89.57 Mbps is far below 80% of 200: tested again in 60 seconds.
		Assert.IsFalse(AppController.IsLandingUnchecked(first));
		Assert.AreEqual(LandingAction.Retest, SwitchPolicy.EvaluateLanding(landing, first, _utcNow + elapsed));
		Assert.AreEqual(1, landing.LowCount);
		Assert.IsFalse(SwitchPolicy.IsLandingKept(landing, first));

		// 16:34:54 The profiles have been loaded: "Staying on Office". Nothing is done meanwhile.
		Connect(profiles, office);
		elapsed += TimeSpan.FromSeconds(11);
		decision = SwitchPolicy.Decide(profiles, office, CreateContext(elapsed), state);
		Assert.IsNull(decision.Target);
		Assert.AreEqual(_now + TimeSpan.FromSeconds(57), state.ConnectedSince);

		// 60 seconds after the first measurement Office measures low again: switching back.
		elapsed += TimeSpan.FromSeconds(49);
		var second = MeasureIdentity.CreateSample(live, office, SpeedTrigger.PostSwitch, _utcNow + elapsed);
		second.Result = SpeedResult.Ok;
		second.DownMbps = 92.3;
		Assert.AreEqual(IdentityEnd.Same, MeasureIdentity.ApplyEnd(second, live, live));
		Record(second);

		Assert.AreEqual(LandingAction.Revert, SwitchPolicy.EvaluateLanding(landing, second, _utcNow + elapsed));

		// The controller excludes Office for a while, connects to EXT and records the attempt.
		state.CooldownUntil["Office"] = _now + elapsed + SwitchPolicy.RevertCooldown;
		state.ClearPending();
		SwitchPolicy.RecordAttempt(state, "EXT", true, _now + elapsed, setsCooldown: false);

		Connect(profiles, ext);
		SwitchPolicy.Track(state, "EXT", _now + elapsed);

		// Office is not switched to again although its typical speed may still look high.
		foreach (var minutes in new[] { 1, 10, 60, 119 })
		{
			var later = elapsed + TimeSpan.FromMinutes(minutes);
			AddSample("EXT", -later, 200, 80);

			decision = SwitchPolicy.Decide(profiles, ext, CreateContext(later), state);
			Assert.IsNull(decision.Target, $"{minutes} min after switching back");
			Assert.IsNull(state.PendingTarget, $"{minutes} min after switching back");
		}

		// There and back.
		Assert.AreEqual(2, state.SwitchTimes.Count);
	}

	[TestMethod]
	public void TestReplayMeasurementAcrossSwitchIsNotEvidence()
	{
		// A measurement which started on EXT and ended on Office (the switch was made while it
		// ran) must neither end the check of the switch nor count as a low result of Office.
		var landing = new SwitchLanding { FromName = "EXT", FromScore = 200, ToName = "Office", Time = _utcNow };

		var start = Live("EXT", ExtSsid, ExtBssid);
		var sample = MeasureIdentity.CreateSample(start, null, SpeedTrigger.Periodic, _utcNow);
		sample.Result = SpeedResult.Ok;
		sample.DownMbps = 40;

		MeasureIdentity.ApplyEnd(sample, start, Live("Office", OfficeSsid, OfficeBssid));

		Assert.IsFalse(sample.Valid);
		Assert.AreEqual(LandingAction.None, SwitchPolicy.EvaluateLanding(landing, sample, _utcNow + TimeSpan.FromSeconds(5)));
		Assert.AreEqual(0, landing.LowCount);
	}

	#endregion

	#region Ignored

	[TestMethod]
	public void TestIgnoredCommandsAreExplained()
	{
		Assert.AreEqual("Test of all networks was not run: the profiles have not been loaded yet.",
			AppController.GetIgnoredText(AppController.GetProbeName(SpeedTrigger.TestAll), "the profiles have not been loaded yet"));

		Assert.AreEqual("Test of a network when idle", AppController.GetProbeName(SpeedTrigger.IdleProbe));

		Assert.AreEqual("a scan or a load of profiles and a speed test was still running after 20 s",
			AppController.GetBusyText(true, false, true, TimeSpan.FromSeconds(20)));
		Assert.AreEqual("a connection or a change of a profile was still running after 20 s",
			AppController.GetBusyText(false, true, false, TimeSpan.FromSeconds(20)));
		Assert.AreEqual("other work was still running after 20 s",
			AppController.GetBusyText(false, false, false, TimeSpan.FromSeconds(20)));
	}

	#endregion

	#region Native Wifi

	[TestMethod]
	public async Task TestLiveConnectionAgreesWithFreshProfiles()
	{
		// Read-only queries of the adapters of this machine: the connection queried for
		// a measurement must name the same profile as a load of the profiles made at the same time.
		using var worker = new NativeWifiWorker();
		if (!worker.IsWorkable)
			Assert.Inconclusive("Native Wifi is not available.");

		worker.IncludesRadioDetails = false;

		for (int attempt = 0; attempt < 3; attempt++)
		{
			var profiles = (await worker.GetProfilesAsync()).ToArray();
			if (profiles.Length == 0)
				Assert.Inconclusive("No wireless profile exists.");

			var isAgreed = true;

			foreach (var subProfiles in profiles.GroupBy(x => x.InterfaceId))
			{
				var live = worker.GetConnection(subProfiles.Key);
				Assert.IsNotNull(live);
				Assert.AreEqual(subProfiles.Key, live.InterfaceId);

				var connectedName = subProfiles.FirstOrDefault(x => x.IsConnected)?.Name;

				if (live.IsConnected)
				{
					Assert.IsFalse(string.IsNullOrEmpty(live.Ssid));
					Assert.IsFalse(string.IsNullOrEmpty(live.Bssid));
					Assert.IsTrue(live.Signal is >= 0 and <= 100);
				}
				else
				{
					Assert.IsNull(live.ProfileName);
				}

				// A connection without profile is not among the profiles.
				if ((connectedName is not null) || subProfiles.Any(x => live.IsOn(x.Name)))
					isAgreed &= live.IsOn(connectedName);
			}

			if (isAgreed)
				return;

			// The connection may have changed between the two queries.
			await Task.Delay(TimeSpan.FromSeconds(1));
		}

		Assert.Fail("The connection queried and the profiles loaded disagree.");
	}

	#endregion

	#region Base

	private SwitchContext CreateContext(TimeSpan elapsed)
	{
		var utcNow = _utcNow + elapsed;

		return new SwitchContext
		{
			EngagesSpeed = true,
			SignalThreshold = 50,
			SpeedSignalThreshold = 30,
			DwellTime = TimeSpan.FromMinutes(2),
			MarginPercent = 50,
			SufficientSpeed = 1000,
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

	private SpeedSample AddSample(string name, TimeSpan age, double? down, int signal)
	{
		var sample = Sample(name, age, down, signal);
		Record(sample);
		return sample;
	}

	private void Record(SpeedSample sample)
	{
		if (!_samples.TryGetValue(sample.Profile, out List<SpeedSample> samples))
		{
			samples = new List<SpeedSample>();
			_samples[sample.Profile] = samples;
		}
		samples.Add(sample);
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

	private static SpeedSample Measured(LiveConnection start, double? down)
	{
		var sample = MeasureIdentity.CreateSample(start, null, SpeedTrigger.Periodic, _utcNow);
		sample.Result = SpeedResult.Ok;
		sample.DownMbps = down;
		return sample;
	}

	private static LiveConnection Live(string name, string ssid, string bssid, float band = 0F, int channel = 0, int signal = 70, int linkRate = 0) => new()
	{
		InterfaceId = _interfaceId,
		IsConnected = true,
		ProfileName = name,
		Ssid = ssid,
		Bssid = bssid,
		Band = band,
		Channel = channel,
		Signal = signal,
		LinkRate = linkRate
	};

	private static void Connect(ProfileItem[] profiles, ProfileItem connected)
	{
		foreach (var profile in profiles)
			profile.IsConnected = ReferenceEquals(profile, connected);
	}

	private static ProfileItem Profile(string name, int signal, bool isConnected = false, string bssid = null, float band = 5F, int channel = 36, int linkRate = 0, int position = 0) => new(
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
		band: band,
		channel: channel)
	{
		Bssid = bssid,
		LinkRate = linkRate
	};

	#endregion
}

using System;
using System.Collections.Generic;
using System.Linq;
using ManagedNativeWifi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Wlan;

namespace Wifinian.Test;

[TestClass]
public class SwitchPolicyTest
{
	private static readonly DateTime _utcNow = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
	private static readonly DateTime _now = new(2026, 11, 17, 21, 0, 0, DateTimeKind.Local);
	private static readonly Guid _interfaceId = new("11111111-2222-3333-4444-555555555555");
	private static readonly TimeSpan _failureInterval = TimeSpan.FromMinutes(3);

	private readonly Dictionary<string, List<SpeedSample>> _samples = new();

	#region Mode A

	[TestMethod]
	public void TestModeAEqualsUpstream()
	{
		var context = CreateContext(engagesSpeed: false);

		// Threshold filter
		var a = Profile("A", 49, 0);
		var b = Profile("B", 50, 1);
		var c = Profile("C", 90, 2);
		AssertModeA(context, [a, b, c], null, b);

		// Position order regardless of the order in the list
		AssertModeA(context, [c, b, a], null, b);

		// Failure demotion
		b.LastFailureTime = _now - TimeSpan.FromMinutes(1);
		AssertModeA(context, [a, b, c], null, c);

		// Failure older than the interval
		b.LastFailureTime = _now - TimeSpan.FromMinutes(4);
		AssertModeA(context, [a, b, c], null, b);

		// All failed
		b.LastFailureTime = _now - TimeSpan.FromMinutes(1);
		c.LastFailureTime = _now - TimeSpan.FromMinutes(2);
		AssertModeA(context, [a, b, c], null, b);

		// Connected one is already the best.
		b.LastFailureTime = default;
		c.LastFailureTime = default;
		var connected = Profile("B", 50, 1, isConnected: true);
		AssertModeA(context, [a, connected, c], connected, null);

		// Connected one is not the best.
		connected = Profile("C", 90, 2, isConnected: true);
		AssertModeA(context, [a, b, connected], connected, b);

		// Connected one is below the threshold.
		connected = Profile("A", 49, 0, isConnected: true);
		AssertModeA(context, [connected, b, c], connected, b);

		// Nothing above the threshold
		AssertModeA(context, [a], null, null);

		// Speed data and state are not used.
		AddSample("C", TimeSpan.FromMinutes(5), SpeedResult.Ok, 500, 90);
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 1, 50);
		AssertModeA(context, [a, b, c], null, b);
		Assert.AreEqual("priority order", SwitchPolicy.Decide([a, b, c], null, context, null).Reason);
	}

	private static void AssertModeA(SwitchContext context, ProfileItem[] candidates, ProfileItem connected, ProfileItem expected)
	{
		// The expression of the original
		var upstream = candidates
			.Where(x => context.SignalThreshold <= x.Signal)
			.OrderBy(x => (x.LastFailureTime.Add(_failureInterval) > context.Now))
			.ThenBy(x => x.Position)
			.FirstOrDefault();
		if (ReferenceEquals(upstream, connected))
			upstream = null;

		Assert.AreSame(expected, upstream);

		var state = new SwitchState();
		var decision = SwitchPolicy.Decide(candidates, connected, context, state);

		Assert.AreSame(expected, decision.Target);
		Assert.IsNull(state.PendingTarget);
		Assert.AreEqual(0, state.ExploredAt.Count);
	}

	[TestMethod]
	public void TestEmptyCandidates()
	{
		// A connected profile without automatic switch gives no candidate.
		var connected = Profile("A", 80, 0, isConnected: true);

		foreach (var engagesSpeed in new[] { false, true })
		{
			var context = CreateContext(engagesSpeed);

			var decision = SwitchPolicy.Decide(Array.Empty<ProfileItem>(), connected, context, new SwitchState());
			Assert.IsNull(decision.Target);
			Assert.IsNull(decision.Reason);
			Assert.IsFalse(decision.RequestsVerify);

			decision = SwitchPolicy.Decide(null, null, context, new SwitchState());
			Assert.IsNull(decision.Target);
			Assert.IsNull(decision.Reason);
		}
	}

	#endregion

	#region Mode B

	[TestMethod]
	public void TestOwnerScenario()
	{
		var (ext2, office, ext) = CreateOwnerProfiles("EXT2");
		ProfileItem[] candidates = [ext, ext2, office];

		var state = CreateState("EXT2", TimeSpan.FromMinutes(15));

		// The first evaluation only makes the faster network pending.
		var decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(), state);
		Assert.IsNull(decision.Target);
		Assert.AreEqual("EXT", state.PendingTarget);
		StringAssert.Contains(decision.Reason, "confirming");

		// Not yet confirmed
		decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromSeconds(20)), state);
		Assert.IsNull(decision.Target);

		decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromSeconds(61)), state);
		Assert.AreSame(ext, decision.Target);
		Assert.AreEqual(SwitchKind.Switch, decision.Kind);
		Assert.AreEqual("Switched EXT2 -> EXT: about 77 Mbps vs 8 Mbps", decision.Reason);
		Assert.AreEqual(8D, decision.FromScore.Value, 0.001);
		Assert.AreEqual(77D, decision.ToScore.Value, 0.001);
	}

	[TestMethod]
	public void TestFastEnoughStays()
	{
		foreach (var name in new[] { "Office", "EXT" })
		{
			_samples.Clear();
			var (ext2, office, ext) = CreateOwnerProfiles(name);
			ProfileItem[] candidates = [ext, ext2, office];
			var connected = candidates.Single(x => x.IsConnected);

			var state = CreateState(name, TimeSpan.FromHours(5));

			for (int i = 0; i < 200; i++)
			{
				var decision = SwitchPolicy.Decide(candidates, connected, CreateContext(elapsed: TimeSpan.FromMinutes(i)), state);
				Assert.IsNull(decision.Target);
				StringAssert.Contains(decision.Reason, "fast enough");
				Assert.IsFalse(decision.RequestsVerify);
				Assert.IsNull(state.PendingTarget);
			}
		}
	}

	[TestMethod]
	public void TestDwell()
	{
		var (ext2, office, ext) = CreateOwnerProfiles("EXT2");
		ProfileItem[] candidates = [ext, ext2, office];

		var state = CreateState("EXT2", TimeSpan.FromMinutes(2));

		var decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(), state);
		Assert.IsNull(decision.Target);
		Assert.AreEqual("Staying on EXT2: connected 2 min ago, waiting 10 min", decision.Reason);

		decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromSeconds(61)), state);
		Assert.IsNull(decision.Target);
		Assert.IsNull(state.PendingTarget);

		// After the dwell time, confirmation on two evaluations is still required.
		decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromMinutes(8)), state);
		Assert.IsNull(decision.Target);
		Assert.AreEqual("EXT", state.PendingTarget);

		decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromMinutes(9)), state);
		Assert.AreSame(ext, decision.Target);
	}

	[TestMethod]
	public void TestStaleSampleRequestsVerify()
	{
		var ext2 = Profile("EXT2", 53, 1, isConnected: true);
		var ext = Profile("EXT", 79, 0);
		AddSample("EXT2", TimeSpan.FromMinutes(45), SpeedResult.Ok, 8, 53);
		AddSample("EXT", TimeSpan.FromDays(2), SpeedResult.Ok, 77, 79);

		var state = CreateState("EXT2", TimeSpan.FromMinutes(15));

		// Older than the period of fresh evidence
		var context = CreateContext();
		context.FreshPeriod = TimeSpan.FromMinutes(30);

		var decision = SwitchPolicy.Decide([ext, ext2], ext2, context, state);
		Assert.IsNull(decision.Target);
		Assert.IsTrue(decision.RequestsVerify);
		Assert.AreEqual("Staying on EXT2: retesting before leaving", decision.Reason);
		Assert.IsNull(state.PendingTarget);

		// Within twice the interval of periodic measurement
		context = CreateContext();
		context.FreshPeriod = TimeSpan.FromMinutes(120);

		decision = SwitchPolicy.Decide([ext, ext2], ext2, context, state);
		Assert.IsNull(decision.Target);
		Assert.IsFalse(decision.RequestsVerify);
		Assert.AreEqual("EXT", state.PendingTarget);
	}

	[TestMethod]
	public void TestUntestedConnectedRequestsVerify()
	{
		var a = Profile("A", 70, 1, isConnected: true);
		var b = Profile("B", 70, 0);
		AddSample("B", TimeSpan.FromMinutes(10), SpeedResult.Ok, 90, 70);

		var state = CreateState("A", TimeSpan.FromHours(1));

		for (int i = 0; i < 5; i++)
		{
			var decision = SwitchPolicy.Decide([a, b], a, CreateContext(elapsed: TimeSpan.FromMinutes(i)), state);
			Assert.IsNull(decision.Target);
			Assert.IsTrue(decision.RequestsVerify);
			Assert.AreEqual("Staying on A until it has been tested", decision.Reason);
		}
	}

	[TestMethod]
	public void TestMargin()
	{
		var a = Profile("A", 70, 1, isConnected: true);
		var b = Profile("B", 70, 0);

		// Below 1.5 times
		AddSample("A", TimeSpan.FromMinutes(5), SpeedResult.Ok, 30, 70);
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 40, 70);
		AssertStays([a, b], a, "B is faster (40 vs 30 Mbps) but not by enough");

		// Less than 10 Mbps faster
		_samples.Clear();
		AddSample("A", TimeSpan.FromMinutes(5), SpeedResult.Ok, 4, 70);
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 9, 70);
		AssertStays([a, b], a, "not by enough");

		// Slower
		_samples.Clear();
		AddSample("A", TimeSpan.FromMinutes(5), SpeedResult.Ok, 30, 70);
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 20, 70);
		AssertStays([a, b], a, "Staying on A: no better network in range");

		// Both conditions met
		_samples.Clear();
		AddSample("A", TimeSpan.FromMinutes(5), SpeedResult.Ok, 30, 70);
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 45, 70);

		var state = CreateState("A", TimeSpan.FromHours(1));
		Assert.IsNull(SwitchPolicy.Decide([a, b], a, CreateContext(), state).Target);
		Assert.AreSame(b, SwitchPolicy.Decide([a, b], a, CreateContext(elapsed: TimeSpan.FromSeconds(61)), state).Target);
	}

	private void AssertStays(ProfileItem[] candidates, ProfileItem connected, string reason)
	{
		var state = CreateState(connected.Name, TimeSpan.FromHours(1));

		for (int i = 0; i < 5; i++)
		{
			var decision = SwitchPolicy.Decide(candidates, connected, CreateContext(elapsed: TimeSpan.FromSeconds(61 * i)), state);
			Assert.IsNull(decision.Target);
			StringAssert.Contains(decision.Reason, reason);
			Assert.IsNull(state.PendingTarget);
		}
	}

	[TestMethod]
	public void TestSignalScalesScore()
	{
		// A network measured at strong signal is expected to be slower at weak signal.
		var a = Profile("A", 70, 1, isConnected: true);
		var b = Profile("B", 40, 0);
		AddSample("A", TimeSpan.FromMinutes(5), SpeedResult.Ok, 30, 70);
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 80, 80);

		// 80 x 40 / 80 = 40 Mbps, which is below 1.5 times.
		AssertStays([a, b], a, "B is faster (40 vs 30 Mbps) but not by enough");
	}

	[TestMethod]
	public void TestNoDataUsesPriorityOrder()
	{
		var a = Profile("A", 60, 2, isConnected: true);
		var b = Profile("B", 70, 1);
		var c = Profile("C", 45, 0);
		ProfileItem[] candidates = [a, b, c];

		var expected = SwitchPolicy.Decide(candidates, a, CreateContext(engagesSpeed: false), null).Target;
		Assert.AreSame(b, expected);

		// It stays while the dwell time has not passed.
		var state = CreateState("A", TimeSpan.FromMinutes(2));
		var decision = SwitchPolicy.Decide(candidates, a, CreateContext(), state);
		Assert.IsNull(decision.Target);
		StringAssert.Contains(decision.Reason, "waiting 10 min");

		state = CreateState("A", TimeSpan.FromMinutes(11));
		decision = SwitchPolicy.Decide(candidates, a, CreateContext(), state);
		Assert.AreSame(expected, decision.Target);
		Assert.AreEqual("No speed data yet - using priority order", decision.Reason);

		// Not connected (cold start with untested networks)
		var a2 = Profile("A", 60, 2);
		decision = SwitchPolicy.Decide([a2, b, c], null, CreateContext(), new SwitchState());
		Assert.AreSame(b, decision.Target);
		Assert.AreEqual("Connecting to B", decision.Reason);

		// Connected one is already the first.
		var b2 = Profile("B", 70, 1, isConnected: true);
		decision = SwitchPolicy.Decide([a2, b2, c], b2, CreateContext(), CreateState("B", TimeSpan.FromHours(1)));
		Assert.IsNull(decision.Target);
		Assert.IsFalse(decision.RequestsVerify);
	}

	[TestMethod]
	public void TestUnknownAndExploration()
	{
		var u = Profile("U", 80, 0);
		var b = Profile("B", 60, 1);
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 30, 60);

		// An untested network never precedes a network of usable speed.
		var decision = SwitchPolicy.Decide([u, b], null, CreateContext(), new SwitchState());
		Assert.AreSame(b, decision.Target);
		Assert.AreEqual("Connecting to B: about 30 Mbps", decision.Reason);

		// But it precedes a network of barely usable speed.
		_samples.Clear();
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 3, 60);
		Assert.AreSame(u, SwitchPolicy.Decide([u, b], null, CreateContext(), new SwitchState()).Target);

		// An untested network never displaces connected network which is fast enough.
		_samples.Clear();
		var c = Profile("C", 60, 1, isConnected: true);
		AddSample("C", TimeSpan.FromMinutes(5), SpeedResult.Ok, 60, 60);

		var state = CreateState("C", TimeSpan.FromHours(1));
		for (int i = 0; i < 10; i++)
			Assert.IsNull(SwitchPolicy.Decide([u, c], c, CreateContext(elapsed: TimeSpan.FromMinutes(i)), state).Target);

		Assert.AreEqual(0, state.ExploredAt.Count);

		// It is tried once when connected network is slower than sufficient.
		foreach (var speed in new[] { 3D, 30D })
		{
			_samples.Clear();
			AddSample("C", TimeSpan.FromMinutes(5), SpeedResult.Ok, speed, 60);

			state = CreateState("C", TimeSpan.FromHours(1));

			decision = SwitchPolicy.Decide([u, c], c, CreateContext(), state);
			Assert.IsNull(decision.Target);
			Assert.AreEqual("U", state.PendingTarget);

			decision = SwitchPolicy.Decide([u, c], c, CreateContext(elapsed: TimeSpan.FromSeconds(61)), state);
			Assert.AreSame(u, decision.Target);
			StringAssert.Contains(decision.Reason, "trying untested U");
			Assert.IsTrue(state.ExploredAt.ContainsKey("U"));

			// Back on the slow network (the untested one could not be measured), it is not tried
			// again within 24 hours.
			for (int i = 2; i < 24 * 60; i += 7)
			{
				decision = SwitchPolicy.Decide([u, c], c, CreateContext(elapsed: TimeSpan.FromMinutes(i), ignoresFreshness: true), state);
				Assert.IsNull(decision.Target);
				StringAssert.Contains(decision.Reason, "U is untested - run Test all networks");
			}

			var later = TimeSpan.FromHours(25);
			Assert.IsNull(SwitchPolicy.Decide([u, c], c, CreateContext(elapsed: later, ignoresFreshness: true), state).Target);
			Assert.AreSame(u, SwitchPolicy.Decide([u, c], c, CreateContext(elapsed: later + TimeSpan.FromSeconds(61), ignoresFreshness: true), state).Target);
		}

		// An untested network below the signal threshold is not tried.
		var weak = Profile("U", 44, 0);
		state = CreateState("C", TimeSpan.FromHours(1));
		for (int i = 0; i < 5; i++)
			Assert.IsNull(SwitchPolicy.Decide([weak, c], c, CreateContext(elapsed: TimeSpan.FromMinutes(i)), state).Target);
	}

	[TestMethod]
	public void TestDeadConnectedLeavesImmediately()
	{
		var a = Profile("A", 80, 0, isConnected: true);
		var b = Profile("B", 60, 1);
		AddSample("A", TimeSpan.FromDays(1), SpeedResult.Ok, 90, 80);
		AddSample("A", TimeSpan.FromMinutes(3), SpeedResult.NoInternet, 0, 80);
		AddSample("A", TimeSpan.FromMinutes(1), SpeedResult.NoInternet, 0, 80);
		AddSample("B", TimeSpan.FromDays(1), SpeedResult.Ok, 20, 60);

		// No dwell, no confirmation
		var state = CreateState("A", TimeSpan.Zero);
		var decision = SwitchPolicy.Decide([a, b], a, CreateContext(), state);
		Assert.AreSame(b, decision.Target);
		Assert.AreEqual("A has no internet - switching to B", decision.Reason);
		Assert.AreEqual(0D, decision.FromScore);

		// One result of no internet is not enough.
		_samples["A"].RemoveAt(1);
		Assert.IsNull(SwitchPolicy.Decide([a, b], a, CreateContext(), CreateState("A", TimeSpan.Zero)).Target);

		// It counts against the budget of switches.
		_samples["A"].Add(Sample("A", TimeSpan.FromMinutes(3), SpeedResult.CaptivePortal, 0, 80));
		state = CreateState("A", TimeSpan.Zero);
		for (int i = 0; i < 4; i++)
			state.SwitchTimes.Add(_now - TimeSpan.FromMinutes(10 * i));

		decision = SwitchPolicy.Decide([a, b], a, CreateContext(), state);
		Assert.IsNull(decision.Target);
		Assert.AreEqual("Too many switches in the last hour - holding", decision.Reason);

		// An untested network is taken as well.
		_samples.Remove("B");
		Assert.AreSame(b, SwitchPolicy.Decide([a, b], a, CreateContext(), CreateState("A", TimeSpan.Zero)).Target);

		// Nothing else in range
		decision = SwitchPolicy.Decide([a], a, CreateContext(), CreateState("A", TimeSpan.Zero));
		Assert.IsNull(decision.Target);
		Assert.AreEqual("A has no internet - no other network in range", decision.Reason);
	}

	[TestMethod]
	public void TestDeadStaysDeadAndBackoff()
	{
		var a = Profile("A", 80, 0, isConnected: true);
		var b = Profile("B", 60, 1);
		AddSample("A", TimeSpan.FromMinutes(3), SpeedResult.NoInternet, 0, 80);
		AddSample("A", TimeSpan.FromMinutes(1), SpeedResult.NoInternet, 0, 80);
		AddSample("B", TimeSpan.FromMinutes(70), SpeedResult.NoInternet, 0, 60);
		AddSample("B", TimeSpan.FromMinutes(60), SpeedResult.NoInternet, 0, 60);

		// Two dead networks do not trade places.
		var state = CreateState("A", TimeSpan.FromHours(1));
		for (int i = 0; i < 59; i++)
			Assert.IsNull(SwitchPolicy.Decide([a, b], a, CreateContext(elapsed: TimeSpan.FromMinutes(i)), state).Target);

		// The other is retried 2 hours after it was found dead.
		var decision = SwitchPolicy.Decide([a, b], a, CreateContext(elapsed: TimeSpan.FromMinutes(61)), state);
		Assert.AreSame(b, decision.Target);
		Assert.AreEqual(1, state.RetryCounts["B"]);

		// Still dead and so the next retry is 4 hours after the last one.
		SwitchPolicy.Track(state, "B", _now + TimeSpan.FromMinutes(61));
		SwitchPolicy.Track(state, "A", _now + TimeSpan.FromMinutes(63));

		for (int i = 64; i < 61 + 240; i += 5)
			Assert.IsNull(SwitchPolicy.Decide([a, b], a, CreateContext(elapsed: TimeSpan.FromMinutes(i)), state).Target);

		Assert.AreSame(b, SwitchPolicy.Decide([a, b], a, CreateContext(elapsed: TimeSpan.FromMinutes(61 + 241)), state).Target);
		Assert.AreEqual(2, state.RetryCounts["B"]);

		// A later Ok result clears it.
		_samples["B"].Add(Sample("B", TimeSpan.Zero, SpeedResult.Ok, 40, 60));
		decision = SwitchPolicy.Decide([a, b], a, CreateContext(elapsed: TimeSpan.FromMinutes(1)), CreateState("A", TimeSpan.Zero));
		Assert.AreSame(b, decision.Target);

		// A dead network is the last resort when not connected.
		_samples["B"].RemoveAt(2);
		var a2 = Profile("A", 80, 0);
		var u = Profile("U", 55, 2);
		Assert.AreSame(u, SwitchPolicy.Decide([a2, b, u], null, CreateContext(), new SwitchState()).Target);
		Assert.AreSame(a2, SwitchPolicy.Decide([a2, b], null, CreateContext(), new SwitchState()).Target);
	}

	[TestMethod]
	public void TestBudget()
	{
		var (ext2, office, ext) = CreateOwnerProfiles("EXT2");
		ProfileItem[] candidates = [ext, ext2, office];

		var state = CreateState("EXT2", TimeSpan.FromMinutes(15));
		for (int i = 0; i < 4; i++)
			state.SwitchTimes.Add(_now - TimeSpan.FromMinutes(14 * (i + 1)));

		SwitchPolicy.Decide(candidates, ext2, CreateContext(), state);

		var decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromSeconds(61)), state);
		Assert.IsNull(decision.Target);
		Assert.AreEqual("Too many switches in the last hour - holding", decision.Reason);

		// The oldest switch is over an hour ago.
		decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromMinutes(5)), state);
		Assert.AreSame(ext, decision.Target);
	}

	[TestMethod]
	public void TestCooldown()
	{
		var (ext2, office, ext) = CreateOwnerProfiles("EXT2");
		ProfileItem[] candidates = [ext, ext2, office];

		var state = CreateState("EXT2", TimeSpan.FromMinutes(15));
		state.CooldownUntil["EXT"] = _now + TimeSpan.FromMinutes(30);

		SwitchPolicy.Decide(candidates, ext2, CreateContext(), state);
		Assert.AreEqual("Office", state.PendingTarget);

		var decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromSeconds(61)), state);
		Assert.AreSame(office, decision.Target);

		// After the cooldown
		state = CreateState("EXT2", TimeSpan.FromMinutes(15));
		state.CooldownUntil["EXT"] = _now - TimeSpan.FromMinutes(1);

		SwitchPolicy.Decide(candidates, ext2, CreateContext(), state);
		Assert.AreEqual("EXT", state.PendingTarget);
	}

	[TestMethod]
	public void TestSignalEligibility()
	{
		// A measured network at 44 % is eligible although the signal threshold is 50.
		var office = Profile("Office", 44, 1);
		var unknown = Profile("Unknown", 44, 0);
		AddSample("Office", TimeSpan.FromHours(1), SpeedResult.Ok, 70, 44);

		var decision = SwitchPolicy.Decide([unknown, office], null, CreateContext(), new SwitchState());
		Assert.AreSame(office, decision.Target);

		// An untested network at 44 % is not.
		decision = SwitchPolicy.Decide([unknown], null, CreateContext(), new SwitchState());
		Assert.IsNull(decision.Target);
		Assert.IsNull(decision.Reason);

		// A measured network enters at the threshold for speed plus 10.
		var low = Profile("Office", 39, 1);
		Assert.IsNull(SwitchPolicy.Decide([low], null, CreateContext(), new SwitchState()).Target);

		var enough = Profile("Office", 40, 1);
		Assert.AreSame(enough, SwitchPolicy.Decide([enough], null, CreateContext(), new SwitchState()).Target);
	}

	[TestMethod]
	public void TestWeakSignalHysteresis()
	{
		var a = Profile("A", 25, 0, isConnected: true);
		var u = Profile("U", 60, 1);
		AddSample("A", TimeSpan.FromMinutes(5), SpeedResult.Ok, 100, 25);

		// Weak signal on a single evaluation is ignored.
		var state = CreateState("A", TimeSpan.Zero);
		var decision = SwitchPolicy.Decide([a, u], a, CreateContext(), state);
		Assert.IsNull(decision.Target);

		decision = SwitchPolicy.Decide([a, u], a, CreateContext(elapsed: TimeSpan.FromSeconds(20)), state);
		Assert.IsNull(decision.Target);

		// It leaves without dwell once weak signal has lasted.
		decision = SwitchPolicy.Decide([a, u], a, CreateContext(elapsed: TimeSpan.FromSeconds(31)), state);
		Assert.AreSame(u, decision.Target);
		Assert.AreEqual("A signal too weak (25%) - switching to U", decision.Reason);

		// Recovery in between starts it over.
		state = CreateState("A", TimeSpan.Zero);
		SwitchPolicy.Decide([a, u], a, CreateContext(), state);

		var recovered = Profile("A", 35, 0, isConnected: true);
		SwitchPolicy.Decide([recovered, u], recovered, CreateContext(elapsed: TimeSpan.FromSeconds(20)), state);
		Assert.AreEqual(default, state.WeakSince);

		Assert.IsNull(SwitchPolicy.Decide([a, u], a, CreateContext(elapsed: TimeSpan.FromSeconds(40)), state).Target);
		Assert.AreSame(u, SwitchPolicy.Decide([a, u], a, CreateContext(elapsed: TimeSpan.FromSeconds(71)), state).Target);
	}

	[TestMethod]
	public void TestAvoidedConnected()
	{
		var a = Profile("A", 80, 0, isConnected: true);
		a.IsAvoided = true;
		var b = Profile("B", 60, 1);
		var c = Profile("C", 60, 2);
		AddSample("A", TimeSpan.FromMinutes(5), SpeedResult.Ok, 200, 80);
		AddSample("C", TimeSpan.FromMinutes(5), SpeedResult.Ok, 10, 60);

		// The caller removes avoided profiles from candidates. No dwell, no margin.
		var decision = SwitchPolicy.Decide([b, c], a, CreateContext(), CreateState("A", TimeSpan.Zero));
		Assert.AreSame(c, decision.Target);
		Assert.AreEqual("A is marked Avoid - switching to C", decision.Reason);

		// Without speed data
		_samples.Clear();
		decision = SwitchPolicy.Decide([b, c], a, CreateContext(), CreateState("A", TimeSpan.Zero));
		Assert.AreSame(b, decision.Target);

		// Avoid never disconnects into nothing.
		var weak = Profile("B", 30, 1);
		decision = SwitchPolicy.Decide([weak], a, CreateContext(), CreateState("A", TimeSpan.Zero));
		Assert.IsNull(decision.Target);
		StringAssert.Contains(decision.Reason, "no other network in range");

		// Priority order only
		decision = SwitchPolicy.Decide([b, c], a, CreateContext(engagesSpeed: false), null);
		Assert.AreSame(b, decision.Target);
	}

	[TestMethod]
	public void TestLullDeferral()
	{
		var (ext2, office, ext) = CreateOwnerProfiles("EXT2");
		ProfileItem[] candidates = [ext, ext2, office];

		var state = CreateState("EXT2", TimeSpan.FromMinutes(15));
		SwitchPolicy.Decide(candidates, ext2, CreateContext(), state);

		// The caller is asked to check traffic only when everything else is met.
		var context = CreateContext(elapsed: TimeSpan.FromSeconds(61));
		context.IsLinkBusy = null;
		var decision = SwitchPolicy.Decide(candidates, ext2, context, state);
		Assert.IsNull(decision.Target);
		Assert.IsTrue(decision.RequestsLinkCheck);
		Assert.AreEqual(default, state.DeferredSince);

		// Busy
		for (int i = 1; i < 16; i++)
		{
			context = CreateContext(elapsed: TimeSpan.FromMinutes(i) + TimeSpan.FromSeconds(1), ignoresFreshness: true);
			context.IsLinkBusy = true;
			decision = SwitchPolicy.Decide(candidates, ext2, context, state);
			Assert.IsNull(decision.Target);
			Assert.IsFalse(decision.RequestsLinkCheck);
			StringAssert.Contains(decision.Reason, "waiting for a pause in traffic");
		}

		// It switches anyway after the maximum deferral.
		context = CreateContext(elapsed: TimeSpan.FromMinutes(16) + TimeSpan.FromSeconds(2), ignoresFreshness: true);
		context.IsLinkBusy = true;
		Assert.AreSame(ext, SwitchPolicy.Decide(candidates, ext2, context, state).Target);

		// A pause in traffic
		state = CreateState("EXT2", TimeSpan.FromMinutes(15));
		SwitchPolicy.Decide(candidates, ext2, CreateContext(), state);

		context = CreateContext(elapsed: TimeSpan.FromSeconds(61));
		context.IsLinkBusy = true;
		Assert.IsNull(SwitchPolicy.Decide(candidates, ext2, context, state).Target);

		context = CreateContext(elapsed: TimeSpan.FromSeconds(122));
		context.IsLinkBusy = false;
		Assert.AreSame(ext, SwitchPolicy.Decide(candidates, ext2, context, state).Target);

		// No deferral
		state = CreateState("EXT2", TimeSpan.FromMinutes(15));
		SwitchPolicy.Decide(candidates, ext2, CreateContext(), state);

		context = CreateContext(elapsed: TimeSpan.FromSeconds(61));
		context.IsLinkBusy = null;
		context.MaxDeferral = TimeSpan.Zero;
		Assert.AreSame(ext, SwitchPolicy.Decide(candidates, ext2, context, state).Target);

		// Leaving a dead network is never deferred.
		_samples["EXT2"].Add(Sample("EXT2", TimeSpan.FromMinutes(2), SpeedResult.NoInternet, 0, 53));
		_samples["EXT2"].Add(Sample("EXT2", TimeSpan.FromMinutes(1), SpeedResult.NoInternet, 0, 53));

		context = CreateContext();
		context.IsLinkBusy = null;
		decision = SwitchPolicy.Decide(candidates, ext2, context, CreateState("EXT2", TimeSpan.Zero));
		Assert.AreSame(ext, decision.Target);
	}

	[TestMethod]
	public void TestHold()
	{
		var (ext2, office, ext) = CreateOwnerProfiles("EXT2");
		ProfileItem[] candidates = [ext, ext2, office];

		var state = CreateState("EXT2", TimeSpan.FromMinutes(15));
		state.HoldUntil = _now + TimeSpan.FromMinutes(10);

		for (int i = 0; i < 10; i++)
		{
			var decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromMinutes(i), ignoresFreshness: true), state);
			Assert.IsNull(decision.Target);
			Assert.IsNull(state.PendingTarget);
		}

		SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromMinutes(10), ignoresFreshness: true), state);
		Assert.AreEqual("EXT", state.PendingTarget);
	}

	#endregion

	#region Replay

	[TestMethod]
	public void TestReplayFlapping()
	{
		var a = Profile("A", 70, 1, isConnected: true);
		var b = Profile("B", 70, 0);

		// Scores alternate within the margin.
		var state = CreateState("A", TimeSpan.FromHours(1));
		int count = 0;

		for (int i = 0; i < 100; i++)
		{
			_samples.Clear();
			AddSample("A", TimeSpan.FromMinutes(1), SpeedResult.Ok, (i % 2 == 0) ? 30 : 40, 70);
			AddSample("B", TimeSpan.FromMinutes(1), SpeedResult.Ok, (i % 2 == 0) ? 40 : 30, 70);

			var context = CreateContext(elapsed: TimeSpan.FromMinutes(i), ignoresFreshness: true);
			SwitchPolicy.Track(state, "A", context.Now);

			if (SwitchPolicy.Decide([a, b], a, context, state).Target is not null)
				count++;
		}

		Assert.AreEqual(0, count);

		// The other is faster by the margin only on every other evaluation.
		state = CreateState("A", TimeSpan.FromHours(1));

		for (int i = 0; i < 100; i++)
		{
			_samples.Clear();
			AddSample("A", TimeSpan.FromMinutes(1), SpeedResult.Ok, 30, 70);
			AddSample("B", TimeSpan.FromMinutes(1), SpeedResult.Ok, (i % 2 == 0) ? 80 : 35, 70);

			var context = CreateContext(elapsed: TimeSpan.FromMinutes(i), ignoresFreshness: true);
			SwitchPolicy.Track(state, "A", context.Now);

			if (SwitchPolicy.Decide([a, b], a, context, state).Target is not null)
				count++;
		}

		Assert.AreEqual(0, count);
	}

	[TestMethod]
	public void TestReplayOwnerDay()
	{
		// The owner's networks evaluated every minute for 8 hours, following each decision and
		// measuring connected network every hour.
		var speeds = new Dictionary<string, double> { ["EXT2"] = 8, ["Office"] = 70, ["EXT"] = 77 };
		var signals = new Dictionary<string, int> { ["EXT2"] = 53, ["Office"] = 44, ["EXT"] = 79 };

		foreach (var name in speeds.Keys)
			AddSample(name, TimeSpan.FromMinutes(20), SpeedResult.Ok, speeds[name], signals[name]);

		var state = new SwitchState();
		var connectedName = "EXT2";
		var switches = new List<string>();

		for (int i = 0; i < 480; i++)
		{
			var elapsed = TimeSpan.FromMinutes(i);

			if (i % 60 == 30)
				_samples[connectedName].Add(Sample(connectedName, -elapsed, SpeedResult.Ok, speeds[connectedName], signals[connectedName]));

			var candidates = speeds.Keys
				.Select((x, index) => Profile(x, signals[x] + ((i % 3) - 1), index, isConnected: (x == connectedName)))
				.ToArray();
			var connected = candidates.Single(x => x.IsConnected);

			var context = CreateContext(elapsed: elapsed, ignoresFreshness: true);
			SwitchPolicy.Track(state, connectedName, context.Now);

			var decision = SwitchPolicy.Decide(candidates, connected, context, state);
			if (decision.Target is null)
				continue;

			SwitchPolicy.RecordAttempt(state, decision.Target.Name, true, context.Now, true);
			connectedName = decision.Target.Name;
			switches.Add(connectedName);
		}

		// One switch to the fastest and then it stays.
		CollectionAssert.AreEqual(new[] { "EXT" }, switches);
	}

	[TestMethod]
	public void TestTrackBriefDisconnection()
	{
		var state = new SwitchState();

		SwitchPolicy.Track(state, "A", _now);
		Assert.AreEqual(_now, state.ConnectedSince);

		state.PendingTarget = "B";
		state.PendingSince = _now;

		// Lost
		SwitchPolicy.Track(state, null, _now + TimeSpan.FromMinutes(20));
		Assert.IsNull(state.ConnectedName);
		Assert.AreEqual("A", state.LastConnectedName);
		Assert.IsNull(state.PendingTarget);

		// The previous network is preferred shortly after disconnection unless another is
		// faster by the margin.
		var a = Profile("A", 70, 1);
		var b = Profile("B", 70, 0);
		AddSample("A", TimeSpan.FromMinutes(5), SpeedResult.Ok, 30, 70);
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 40, 70);

		var decision = SwitchPolicy.Decide([a, b], null, CreateContext(elapsed: TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(10)), state);
		Assert.AreSame(a, decision.Target);
		Assert.AreEqual("Reconnecting to A: about 30 Mbps", decision.Reason);

		_samples["B"].Add(Sample("B", TimeSpan.FromMinutes(1), SpeedResult.Ok, 90, 70));
		_samples["B"].RemoveAt(0);
		decision = SwitchPolicy.Decide([a, b], null, CreateContext(elapsed: TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(10)), state);
		Assert.AreSame(b, decision.Target);

		_samples["B"].Add(Sample("B", TimeSpan.FromMinutes(1), SpeedResult.Ok, 40, 70));
		_samples["B"].RemoveAt(0);

		// After a while, the order is by speed.
		decision = SwitchPolicy.Decide([a, b], null, CreateContext(elapsed: TimeSpan.FromMinutes(22)), state);
		Assert.AreSame(b, decision.Target);

		// The time of connection is kept when the same network comes back shortly.
		SwitchPolicy.Track(state, "A", _now + TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(30));
		Assert.AreEqual(_now, state.ConnectedSince);

		// Otherwise it is renewed.
		SwitchPolicy.Track(state, null, _now + TimeSpan.FromMinutes(30));
		SwitchPolicy.Track(state, "A", _now + TimeSpan.FromMinutes(32));
		Assert.AreEqual(_now + TimeSpan.FromMinutes(32), state.ConnectedSince);

		SwitchPolicy.Track(state, null, _now + TimeSpan.FromMinutes(40));
		SwitchPolicy.Track(state, "B", _now + TimeSpan.FromMinutes(40) + TimeSpan.FromSeconds(10));
		Assert.AreEqual(_now + TimeSpan.FromMinutes(40) + TimeSpan.FromSeconds(10), state.ConnectedSince);

		// A change without disconnection
		SwitchPolicy.Track(state, "A", _now + TimeSpan.FromMinutes(50));
		Assert.AreEqual(_now + TimeSpan.FromMinutes(50), state.ConnectedSince);
	}

	[TestMethod]
	public void TestRecordAttemptFailures()
	{
		var (ext2, office, ext) = CreateOwnerProfiles("EXT2");
		ProfileItem[] candidates = [ext, ext2, office];

		var state = CreateState("EXT2", TimeSpan.FromMinutes(15));
		state.PendingTarget = "EXT";
		state.PendingSince = _now;

		// The first failure excludes the target for 10 minutes and clears confirmation.
		SwitchPolicy.RecordAttempt(state, "EXT", false, _now, true);
		Assert.IsNull(state.PendingTarget);
		Assert.AreEqual(_now + TimeSpan.FromMinutes(10), state.CooldownUntil["EXT"]);
		Assert.AreEqual(1, state.SwitchTimes.Count);

		SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromMinutes(1)), state);
		Assert.AreEqual("Office", state.PendingTarget);

		// The period doubles on repeat up to 2 hours.
		var time = _now;
		foreach (var minutes in new[] { 20, 40, 80, 120, 120 })
		{
			time += TimeSpan.FromHours(3);
			SwitchPolicy.RecordAttempt(state, "EXT", false, time, true);
			Assert.AreEqual(time + TimeSpan.FromMinutes(minutes), state.CooldownUntil["EXT"]);
		}

		// Old attempts are removed from the budget.
		Assert.AreEqual(1, state.SwitchTimes.Count);

		// A success starts it over.
		time += TimeSpan.FromHours(3);
		SwitchPolicy.RecordAttempt(state, "EXT", true, time, true);
		SwitchPolicy.RecordAttempt(state, "EXT", false, time, true);
		Assert.AreEqual(time + TimeSpan.FromMinutes(10), state.CooldownUntil["EXT"]);
		Assert.AreEqual(2, state.SwitchTimes.Count);

		// Failed attempts count in the budget.
		state = CreateState("EXT2", TimeSpan.FromMinutes(15));
		for (int i = 0; i < 4; i++)
			SwitchPolicy.RecordAttempt(state, "Office", false, _now - TimeSpan.FromMinutes(50), false);

		Assert.AreEqual(0, state.CooldownUntil.Count);

		SwitchPolicy.Decide(candidates, ext2, CreateContext(), state);
		var decision = SwitchPolicy.Decide(candidates, ext2, CreateContext(elapsed: TimeSpan.FromSeconds(61)), state);
		Assert.IsNull(decision.Target);
		StringAssert.Contains(decision.Reason, "Too many switches");
	}

	[TestMethod]
	public void TestWeakSignalDepartureIsNotReverted()
	{
		// A fast network whose signal has dropped still has a positive score.
		var a = Profile("A", 25, 0, isConnected: true);
		var b = Profile("B", 60, 1);
		AddSample("A", TimeSpan.FromMinutes(5), SpeedResult.Ok, 77, 80);
		AddSample("B", TimeSpan.FromMinutes(5), SpeedResult.Ok, 18, 60);

		var state = CreateState("A", TimeSpan.Zero);
		SwitchPolicy.Decide([a, b], a, CreateContext(), state);

		var decision = SwitchPolicy.Decide([a, b], a, CreateContext(elapsed: TimeSpan.FromSeconds(31)), state);
		Assert.AreSame(b, decision.Target);
		StringAssert.StartsWith(decision.Code, "weak|");
		Assert.IsTrue(decision.FromScore > 18 / SwitchPolicy.RevertRatio);

		// The network left is not compared with and so never switched back to for low speed.
		Assert.IsNull(SwitchPolicy.GetRevertScore(decision));

		var landing = new SwitchLanding
		{
			FromName = "A",
			FromScore = SwitchPolicy.GetRevertScore(decision),
			ToName = "B",
			Time = _utcNow
		};

		var later = _utcNow + TimeSpan.FromSeconds(30);
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(landing, Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.Ok, 18, 60), later));
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(landing, Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.NoInternet, 0, 60), later));

		// The score of the network left for a faster one or for no internet access is kept.
		Assert.AreEqual(24D, SwitchPolicy.GetRevertScore(new SwitchDecision { Code = "faster|A|B", FromScore = 24 }));
		Assert.AreEqual(0D, SwitchPolicy.GetRevertScore(new SwitchDecision { Code = "offline|A|B", FromScore = 0 }));
		Assert.IsNull(SwitchPolicy.GetRevertScore(new SwitchDecision { Code = "nodata|A|B" }));
		Assert.IsNull(SwitchPolicy.GetRevertScore(null));
	}

	[TestMethod]
	public void TestEvaluateLanding()
	{
		SwitchLanding Create(double? fromScore = 30, DateTime offlineTime = default) => new()
		{
			FromName = "A",
			FromScore = fromScore,
			ToName = "B",
			Time = _utcNow,
			FromOfflineTime = offlineTime
		};

		var later = _utcNow + TimeSpan.FromSeconds(30);

		// As good as expected
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(Create(), Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.Ok, 24, 70), later));

		// Two consecutive low results are required.
		var landing = Create();
		Assert.AreEqual(LandingAction.Retest, SwitchPolicy.EvaluateLanding(landing, Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.Ok, 23.9, 70), later));
		Assert.AreEqual(LandingAction.Revert, SwitchPolicy.EvaluateLanding(landing, Sample("B", TimeSpan.FromSeconds(-100), SpeedResult.Ok, 10, 70), later + TimeSpan.FromSeconds(70)));

		landing = Create();
		Assert.AreEqual(LandingAction.Retest, SwitchPolicy.EvaluateLanding(landing, Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.Ok, 10, 70), later));
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(landing, Sample("B", TimeSpan.FromSeconds(-100), SpeedResult.Ok, 50, 70), later + TimeSpan.FromSeconds(70)));

		// No internet access
		Assert.AreEqual(LandingAction.Revert, SwitchPolicy.EvaluateLanding(Create(), Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.NoInternet, 0, 70), later));
		Assert.AreEqual(LandingAction.Revert, SwitchPolicy.EvaluateLanding(Create(), Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.CaptivePortal, 0, 70), later));

		// Invalid results cause no action.
		foreach (var result in new[] { SpeedResult.Aborted, SpeedResult.EndpointFailed, SpeedResult.RouteMismatch, SpeedResult.Error })
			Assert.AreEqual(LandingAction.None, SwitchPolicy.EvaluateLanding(Create(), Sample("B", TimeSpan.FromSeconds(-30), result, null, 70), later));

		// Never back to a network which was dead or whose speed is unknown.
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(Create(fromScore: 0), Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.Ok, 0.5, 70), later));
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(Create(fromScore: 0, offlineTime: _utcNow - TimeSpan.FromHours(1)), Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.NoInternet, 0, 70), later));
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(Create(fromScore: null), Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.NoInternet, 0, 70), later));

		// Both networks without internet access within 5 minutes (uplink outage)
		Assert.AreEqual(LandingAction.UplinkDown, SwitchPolicy.EvaluateLanding(Create(fromScore: 0, offlineTime: _utcNow - TimeSpan.FromMinutes(2)), Sample("B", TimeSpan.FromSeconds(-30), SpeedResult.NoInternet, 0, 70), later));

		// Another profile
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(Create(), Sample("C", TimeSpan.FromSeconds(-30), SpeedResult.Ok, 1, 70), later));

		// Expired
		Assert.AreEqual(LandingAction.Clear, SwitchPolicy.EvaluateLanding(Create(), Sample("B", TimeSpan.FromMinutes(-5), SpeedResult.Ok, 1, 70), _utcNow + TimeSpan.FromMinutes(5)));

		Assert.AreEqual(LandingAction.None, SwitchPolicy.EvaluateLanding(null, Sample("B", TimeSpan.Zero, SpeedResult.Ok, 1, 70), later));
	}

	[TestMethod]
	public void TestIsOffline()
	{
		// Two results of no internet stay regardless of their age.
		AddSample("A", TimeSpan.FromDays(3), SpeedResult.Ok, 50, 70);
		AddSample("A", TimeSpan.FromHours(5), SpeedResult.NoInternet, 0, 70);
		AddSample("A", TimeSpan.FromHours(4), SpeedResult.NoInternet, 0, 70);

		var summary = SpeedSummary.Create(_samples["A"], _utcNow);
		Assert.IsTrue(summary.IsOffline);
		Assert.IsFalse(summary.IsDead);

		AddSample("A", TimeSpan.FromHours(1), SpeedResult.Ok, 50, 70);
		Assert.IsFalse(SpeedSummary.Create(_samples["A"], _utcNow).IsOffline);
	}

	#endregion

	#region Base

	private (ProfileItem ext2, ProfileItem office, ProfileItem ext) CreateOwnerProfiles(string connectedName)
	{
		var ext2 = Profile("EXT2", 53, 1, isConnected: (connectedName == "EXT2"));
		var office = Profile("Office", 44, 2, isConnected: (connectedName == "Office"));
		var ext = Profile("EXT", 79, 0, isConnected: (connectedName == "EXT"));

		AddSample("EXT2", TimeSpan.FromMinutes(5), SpeedResult.Ok, 8, 53);
		AddSample("Office", TimeSpan.FromHours(20), SpeedResult.Ok, 70, 44);
		AddSample("EXT", TimeSpan.FromHours(30), SpeedResult.Ok, 77, 79);

		return (ext2, office, ext);
	}

	private static SwitchState CreateState(string connectedName, TimeSpan connectedFor)
	{
		var state = new SwitchState();
		SwitchPolicy.Track(state, connectedName, _now - connectedFor);
		return state;
	}

	/// <summary>
	/// Creates a context.
	/// </summary>
	/// <param name="engagesSpeed">Whether measured speed is used</param>
	/// <param name="elapsed">Time elapsed from the base time</param>
	/// <param name="ignoresFreshness">Whether the sample of connected network is accepted regardless of its age</param>
	private SwitchContext CreateContext(bool engagesSpeed = true, TimeSpan elapsed = default, bool ignoresFreshness = false)
	{
		var utcNow = _utcNow + elapsed;

		return new SwitchContext
		{
			EngagesSpeed = engagesSpeed,
			SignalThreshold = 50,
			SpeedSignalThreshold = 30,
			DwellTime = TimeSpan.FromMinutes(10),
			MarginPercent = 50,
			SufficientSpeed = 50,
			FailureInterval = _failureInterval,
			FreshPeriod = ignoresFreshness ? TimeSpan.FromDays(10) : TimeSpan.FromMinutes(120),
			MaxDeferral = TimeSpan.FromMinutes(15),
			IsLinkBusy = false,
			Now = _now + elapsed,
			UtcNow = utcNow,
			GetSummary = p => _samples.TryGetValue(p.Name, out List<SpeedSample> samples)
				? SpeedSummary.Create(samples, utcNow)
				: null
		};
	}

	private void AddSample(string name, TimeSpan age, SpeedResult result, double? down, int signal)
	{
		if (!_samples.TryGetValue(name, out List<SpeedSample> samples))
		{
			samples = new List<SpeedSample>();
			_samples[name] = samples;
		}
		samples.Add(Sample(name, age, result, down, signal));
	}

	private static SpeedSample Sample(string name, TimeSpan age, SpeedResult result, double? down, int signal) => new()
	{
		Time = _utcNow - age,
		InterfaceId = _interfaceId,
		Profile = name,
		Signal = signal,
		DownMbps = down,
		Trigger = SpeedTrigger.Periodic,
		Result = result
	};

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
		band: 2.4F,
		channel: 1);

	#endregion
}
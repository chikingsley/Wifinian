using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Wifinian.Models.History;
using Wifinian.Models.Wlan;

namespace Wifinian.Models;

/// <summary>
/// Conditions given to a switching decision
/// </summary>
internal class SwitchContext
{
	/// <summary>
	/// Whether measured speed is used (if false, the decision is that of priority order only)
	/// </summary>
	public bool EngagesSpeed { get; set; }

	/// <summary>
	/// Signal threshold for a network without speed data
	/// </summary>
	public int SignalThreshold { get; set; }

	/// <summary>
	/// Signal threshold for a network with speed data
	/// </summary>
	public int SpeedSignalThreshold { get; set; }

	/// <summary>
	/// Time to stay on a network after connection
	/// </summary>
	public TimeSpan DwellTime { get; set; }

	/// <summary>
	/// Margin (percent) by which another network must be faster
	/// </summary>
	public int MarginPercent { get; set; }

	/// <summary>
	/// Speed (Mbps) at or above which the connected network is never left for a faster one
	/// </summary>
	public double SufficientSpeed { get; set; }

	/// <summary>
	/// Period in which a network failed to connect is put off
	/// </summary>
	public TimeSpan FailureInterval { get; set; }

	/// <summary>
	/// Period in which a valid speed sample of the connected network is regarded as fresh
	/// </summary>
	public TimeSpan FreshPeriod { get; set; } = TimeSpan.FromMinutes(30);

	/// <summary>
	/// Maximum time to defer a switch from a working network while the link is in use
	/// </summary>
	public TimeSpan MaxDeferral { get; set; }

	/// <summary>
	/// Whether the link is in use (null if not checked yet)
	/// </summary>
	public bool? IsLinkBusy { get; set; } = false;

	/// <summary>
	/// Whether the interface is in the middle of a connection made by someone else (associating
	/// or authenticating, or a connection has been started and not finished yet)
	/// </summary>
	/// <remarks>While this is true, no connection is started.</remarks>
	public bool IsInterfaceBusy { get; set; }

	/// <summary>
	/// Current time (local)
	/// </summary>
	public DateTime Now { get; set; }

	/// <summary>
	/// Current time (UTC)
	/// </summary>
	public DateTime UtcNow { get; set; }

	/// <summary>
	/// Function to get the summary of speed samples of a profile
	/// </summary>
	public Func<ProfileItem, SpeedSummary> GetSummary { get; set; }
}

/// <summary>
/// State of switching of a wireless interface
/// </summary>
/// <remarks>All times are local.</remarks>
internal class SwitchState
{
	public string ConnectedName { get; set; }
	public DateTime ConnectedSince { get; set; }

	public string PendingTarget { get; set; }
	public DateTime PendingSince { get; set; }

	/// <summary>
	/// Times of automatic switches and failed attempts
	/// </summary>
	public List<DateTime> SwitchTimes { get; } = new();

	public Dictionary<string, DateTime> CooldownUntil { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, DateTime> ExploredAt { get; } = new(StringComparer.Ordinal);

	/// <summary>
	/// Numbers of retries of networks without internet access
	/// </summary>
	public Dictionary<string, int> RetryCounts { get; } = new(StringComparer.Ordinal);

	/// <summary>
	/// Numbers of consecutive failures of automatic connection
	/// </summary>
	public Dictionary<string, int> FailureCounts { get; } = new(StringComparer.Ordinal);

	public string LastConnectedName { get; set; }
	public DateTime LastDisconnectedAt { get; set; }

	/// <summary>
	/// Time when an automatic connection by this app last failed
	/// </summary>
	/// <remarks>
	/// A disconnection which follows it is caused by this app and so is not waited after.
	/// </remarks>
	public DateTime OwnFailureAt { get; set; }

	/// <summary>
	/// Time since when a switch has been deferred because the link is in use
	/// </summary>
	public DateTime DeferredSince { get; set; }

	/// <summary>
	/// Time since when the signal of the connected network has been too weak
	/// </summary>
	public DateTime WeakSince { get; set; }

	/// <summary>
	/// Time until when no switch is made because the uplink seems to be down
	/// </summary>
	public DateTime HoldUntil { get; set; }

	public void ClearPending()
	{
		PendingTarget = null;
		PendingSince = default;
		DeferredSince = default;
	}
}

/// <summary>
/// Switching decision
/// </summary>
internal class SwitchDecision
{
	/// <summary>
	/// Profile to connect to (null to stay)
	/// </summary>
	public ProfileItem Target { get; set; }

	public SwitchKind Kind { get; set; }

	/// <summary>
	/// Reason sentence shown to the user (null if there is nothing to tell)
	/// </summary>
	public string Reason { get; set; }

	/// <summary>
	/// Code which identifies the rule and the profiles of this decision regardless of numbers
	/// in the reason sentence
	/// </summary>
	public string Code { get; set; }

	public double? FromScore { get; set; }
	public double? ToScore { get; set; }

	/// <summary>
	/// Whether the connected network needs to be measured before a decision can be made
	/// </summary>
	public bool RequestsVerify { get; set; }

	/// <summary>
	/// Whether traffic of the link needs to be checked before a switch
	/// </summary>
	/// <remarks>The caller is expected to decide again with the result of the check.</remarks>
	public bool RequestsLinkCheck { get; set; }

	/// <summary>
	/// Time after which the decision is to be made again because it only waits (null if none)
	/// </summary>
	/// <remarks>
	/// The caller is expected to load the profiles again after this time because no event may
	/// come in the meantime.
	/// </remarks>
	public TimeSpan? RecheckAfter { get; set; }
}

/// <summary>
/// Record of an automatic switch whose landing network is yet to be checked
/// </summary>
internal class SwitchLanding
{
	public string FromName { get; set; }
	public double? FromScore { get; set; }
	public string ToName { get; set; }

	/// <summary>
	/// Time (UTC) of the switch
	/// </summary>
	public DateTime Time { get; set; }

	/// <summary>
	/// Time (UTC) when the network before the switch was last found without internet access
	/// (default if it had internet access)
	/// </summary>
	public DateTime FromOfflineTime { get; set; }

	/// <summary>
	/// Number of low results so far
	/// </summary>
	public int LowCount { get; set; }
}

internal enum LandingAction
{
	/// <summary>
	/// Keep the record and wait for another sample
	/// </summary>
	None = 0,

	/// <summary>
	/// Discard the record
	/// </summary>
	Clear,

	/// <summary>
	/// Keep the record and measure again
	/// </summary>
	Retest,

	/// <summary>
	/// Discard the record and switch back
	/// </summary>
	Revert,

	/// <summary>
	/// Discard the record and hold switching because the uplink seems to be down
	/// </summary>
	UplinkDown
}

/// <summary>
/// Policy to decide the network to connect to
/// </summary>
/// <remarks>
/// This class performs no I/O and reads neither settings nor clock. All such conditions are
/// given by <see cref="SwitchContext"/> and <see cref="SwitchState"/>.
/// </remarks>
internal static class SwitchPolicy
{
	#region Constant

	/// <summary>
	/// Speed (Mbps) below which a network is regarded as barely usable
	/// </summary>
	public const double SlowSpeed = 5D;

	/// <summary>
	/// Minimum difference (Mbps) by which another network must be faster
	/// </summary>
	public const double MinDifference = 10D;

	/// <summary>
	/// Additional signal quality required for a network with speed data to become a target
	/// </summary>
	public const int EntryMargin = 10;

	/// <summary>
	/// Maximum number of switches in an hour
	/// </summary>
	public const int MaxSwitchCount = 4;

	/// <summary>
	/// Ratio to the score of the previous network below which a landing network is regarded as worse
	/// </summary>
	public const double RevertRatio = 0.8D;

	/// <summary>
	/// Code of the decision to leave a network whose signal is too weak
	/// </summary>
	public const string WeakCode = "weak";

	/// <summary>
	/// Code of the decision to wait while the interface is being connected by someone else
	/// </summary>
	public const string ConnectingCode = "connecting";

	/// <summary>
	/// Code of the decision to wait just after a disconnection not caused by this app
	/// </summary>
	public const string GraceCode = "grace";

	/// <summary>
	/// Period after a disconnection not caused by this app in which no connection is started
	/// </summary>
	/// <remarks>
	/// When the user (or the system) connects to another network, the interface is disconnected
	/// for a moment before it starts associating. A connection started by this app in that moment
	/// would take the interface back from the network being connected to.
	/// </remarks>
	public static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Period after a failed automatic connection in which a disconnection is regarded as
	/// caused by it
	/// </summary>
	public static readonly TimeSpan OwnFailurePeriod = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Time after which the decision is made again while the interface is being connected
	/// </summary>
	public static readonly TimeSpan ConnectingRecheck = TimeSpan.FromSeconds(3);

	public static readonly TimeSpan PersistPeriod = TimeSpan.FromSeconds(30);
	public static readonly TimeSpan BudgetPeriod = TimeSpan.FromHours(1);
	public static readonly TimeSpan ExplorePeriod = TimeSpan.FromHours(24);
	public static readonly TimeSpan RetryPeriod = TimeSpan.FromHours(2);
	public static readonly TimeSpan MaxRetryPeriod = TimeSpan.FromHours(24);
	public static readonly TimeSpan ReturnPeriod = TimeSpan.FromSeconds(60);
	public static readonly TimeSpan LandingPeriod = TimeSpan.FromMinutes(5);
	public static readonly TimeSpan UplinkPeriod = TimeSpan.FromMinutes(5);
	public static readonly TimeSpan HoldPeriod = TimeSpan.FromMinutes(10);
	public static readonly TimeSpan RevertCooldown = TimeSpan.FromHours(2);
	public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);
	public static readonly TimeSpan MaxFailureCooldown = TimeSpan.FromHours(2);

	/// <summary>
	/// Period in which the newest measurement of a network other than the connected one is
	/// required, in addition to its typical speed, to be faster by the margin
	/// </summary>
	/// <remarks>
	/// The typical speed is a median of up to ten samples and so a network which was fast for
	/// days and has become slow still looks fast after it was switched to, measured slow twice
	/// and switched back from. The period of exclusion after switching back is kept in memory
	/// only and ends in two hours. Without this rule the same switch and switching back would
	/// be repeated after that period or after this app is started again.
	/// </remarks>
	public static readonly TimeSpan RecentPeriod = TimeSpan.FromHours(24);

	#endregion

	/// <summary>
	/// Reflects the connected profile of an interface to the state.
	/// </summary>
	/// <param name="state">State of the interface</param>
	/// <param name="connectedName">Name of the connected profile (null if none)</param>
	/// <param name="now">Current time (local)</param>
	/// <remarks>
	/// The time of connection is not renewed when the same profile comes back shortly after
	/// disconnection.
	/// </remarks>
	public static void Track(SwitchState state, string connectedName, DateTime now)
	{
		// A disconnection after the interface is seen connected, to whichever network, is no
		// longer the consequence of an earlier failed connection by this app.
		if (connectedName is not null)
			state.OwnFailureAt = default;

		if (string.Equals(state.ConnectedName, connectedName, StringComparison.Ordinal))
			return;

		if (connectedName is null)
		{
			state.LastConnectedName = state.ConnectedName;
			state.LastDisconnectedAt = now;
		}
		else
		{
			var isReturned = (state.ConnectedName is null)
				&& string.Equals(state.LastConnectedName, connectedName, StringComparison.Ordinal)
				&& IsWithin(now, state.LastDisconnectedAt, ReturnPeriod);

			if (!isReturned)
				state.ConnectedSince = now;

			// Failures of automatic connection are no longer consecutive once the network is
			// connected by any means and so the period of exclusion made by them ends. The period
			// made by switching back for speed has no count of failures and is kept.
			if (state.FailureCounts.Remove(connectedName))
				state.CooldownUntil.Remove(connectedName);
		}

		state.ConnectedName = connectedName;
		state.ClearPending();
		state.WeakSince = default;
	}

	/// <summary>
	/// Records an automatic switch or a failed attempt of it to the state.
	/// </summary>
	/// <param name="state">State of the interface</param>
	/// <param name="targetName">Name of the profile attempted to connect to</param>
	/// <param name="result">True if connected</param>
	/// <param name="now">Current time (local)</param>
	/// <param name="setsCooldown">Whether to exclude a failed profile for a while</param>
	/// <param name="countsBudget">
	/// Whether to count this attempt in the number of switches in an hour (a successful connection
	/// made while no network was connected is not a switch)
	/// </param>
	public static void RecordAttempt(SwitchState state, string targetName, bool result, DateTime now, bool setsCooldown, bool countsBudget = true)
	{
		state.SwitchTimes.RemoveAll(x => !IsWithin(now, x, BudgetPeriod));

		if (countsBudget)
			state.SwitchTimes.Add(now);

		if (result)
		{
			state.FailureCounts.Remove(targetName);
			return;
		}

		state.ClearPending();
		state.OwnFailureAt = now;

		if (!setsCooldown)
			return;

		state.FailureCounts.TryGetValue(targetName, out int count);
		state.FailureCounts[targetName] = ++count;

		var ticks = FailureCooldown.Ticks * (1L << Math.Min(count - 1, 10));
		state.CooldownUntil[targetName] = now.AddTicks(Math.Min(ticks, MaxFailureCooldown.Ticks));
	}

	/// <summary>
	/// Gets the score of the network before a switch with which the network switched to is
	/// compared.
	/// </summary>
	/// <param name="decision">Decision of the switch</param>
	/// <returns>Score if the previous network can be switched back to, null otherwise</returns>
	/// <remarks>
	/// A network left because its signal was too weak is never switched back to. Its score is
	/// positive but returning there would undo the very reason of the switch.
	/// </remarks>
	public static double? GetRevertScore(SwitchDecision decision)
	{
		if (decision is null)
			return null;

		if (decision.Code?.StartsWith(WeakCode + "|", StringComparison.Ordinal) is true)
			return null;

		return decision.FromScore;
	}

	/// <summary>
	/// Decides what to do on a speed sample measured after an automatic switch.
	/// </summary>
	/// <param name="landing">Record of the switch</param>
	/// <param name="sample">Speed sample of the interface</param>
	/// <param name="utcNow">Current time (UTC)</param>
	/// <remarks>This method increments the number of low results of the record.</remarks>
	public static LandingAction EvaluateLanding(SwitchLanding landing, SpeedSample sample, DateTime utcNow)
	{
		if ((landing is null) || (sample is null))
			return LandingAction.None;

		if (!IsWithin(utcNow, landing.Time, LandingPeriod))
			return LandingAction.Clear;

		if (!sample.Valid)
			return LandingAction.None;

		if (!string.Equals(sample.Profile, landing.ToName, StringComparison.Ordinal))
			return LandingAction.Clear;

		// The network before the switch is never switched back to if it had no internet access.
		var canRevert = (landing.FromScore > 0D);

		if (sample.Result is SpeedResult.NoInternet or SpeedResult.CaptivePortal)
		{
			if ((landing.FromOfflineTime != default)
				&& ((sample.Time - landing.FromOfflineTime).Duration() <= UplinkPeriod))
				return LandingAction.UplinkDown;

			return canRevert ? LandingAction.Revert : LandingAction.Clear;
		}

		if (!canRevert || !(sample.DownMbps < RevertRatio * landing.FromScore.Value))
			return LandingAction.Clear;

		// A speed measured through a server other than the first one is a lower bound: the server
		// can be what limited it. It can show that the network is fast enough (above) but not
		// that it is slower, and so the switch is left unchecked by it.
		if (sample.IsFallback)
			return LandingAction.None;

		// A single low result can be by chance.
		return (++landing.LowCount >= 2) ? LandingAction.Revert : LandingAction.Retest;
	}

	/// <summary>
	/// Determines whether a sample measured after an automatic switch, which ended the check of
	/// the switch, shows that the network switched to is good enough to keep.
	/// </summary>
	/// <remarks>
	/// The check also ends without a finding (its time is over, the sample is of another network,
	/// the previous network cannot be switched back to), which is not worth telling.
	/// </remarks>
	public static bool IsLandingKept(SwitchLanding landing, SpeedSample sample) =>
		(landing is not null)
		&& (sample is { Result: SpeedResult.Ok, DownMbps: not null })
		&& string.Equals(sample.Profile, landing.ToName, StringComparison.Ordinal)
		&& (landing.FromScore > 0D)
		&& (sample.DownMbps.Value >= RevertRatio * landing.FromScore.Value);

	/// <summary>
	/// Decides the profile to connect to.
	/// </summary>
	/// <param name="candidates">Profiles allowed to be connected to except avoided ones (this may or may not include connected profile)</param>
	/// <param name="connected">Connected profile (null if none)</param>
	/// <param name="context">Conditions</param>
	/// <param name="state">State of the interface</param>
	/// <returns>Decision whose target is never connected profile</returns>
	public static SwitchDecision Decide(IReadOnlyList<ProfileItem> candidates, ProfileItem connected, SwitchContext context, SwitchState state)
	{
		if (context is null)
			throw new ArgumentNullException(nameof(context));

		if (candidates is not { Count: > 0 })
			return new SwitchDecision { Kind = SwitchKind.Stay };

		var now = context.Now;

		// (-1) Not connected but someone else is connecting or has just disconnected.
		// This applies regardless of whether measured speed is used.
		if (connected is null)
		{
			var wait = GetConnectWait(context, state);
			if (wait is not null)
				return wait;
		}

		bool RecentlyFailed(ProfileItem p) => p.LastFailureTime.Add(context.FailureInterval) > now;

		ProfileItem UpstreamPick(IEnumerable<ProfileItem> list) => list
			.Where(x => context.SignalThreshold <= x.Signal)
			// This interval is for the case where disconnection from wireless LAN is not immediately
			// reflected and take 2 to 3 minutes.
			.OrderBy(x => RecentlyFailed(x))
			.ThenBy(x => x.Position)
			.FirstOrDefault();

		#region Mode A

		if (!context.EngagesSpeed)
		{
			var pick = UpstreamPick(candidates);

			return new SwitchDecision
			{
				Target = ((pick is null) || ReferenceEquals(pick, connected)) ? null : pick,
				Kind = SwitchKind.Switch,
				Reason = "priority order",
				Code = "priority"
			};
		}

		#endregion

		#region Mode B

		if (state is null)
			throw new ArgumentNullException(nameof(state));

		var summaries = new Dictionary<ProfileItem, SpeedSummary>();

		SpeedSummary Summary(ProfileItem p)
		{
			if (!summaries.TryGetValue(p, out SpeedSummary summary))
			{
				summary = context.GetSummary?.Invoke(p);
				summaries[p] = summary;
			}
			return summary;
		}

		// A network found without internet access stays so until a later result clears it.
		bool IsDead(ProfileItem p) => Summary(p) is { IsOffline: true };

		double? Score(ProfileItem p)
		{
			var s = Summary(p);
			if (s is null)
				return null;

			if (s.IsOffline)
				return 0D;

			if (!s.HasData || !s.DownMbps.HasValue)
				return null;

			var factor = (s.MeanSignal > 0D) ? (p.Signal / s.MeanSignal) : 1D;
			return s.DownMbps.Value * Math.Min(Math.Max(factor, 0.3D), 1D);
		}

		// The speed expected of a network to be switched to for speed: its typical speed or, if
		// lower, what its newest measurement found when that is recent.
		double? TargetScore(ProfileItem p)
		{
			var score = Score(p);
			if (!score.HasValue)
				return null;

			var s = Summary(p);
			if ((s is { LastResult: SpeedResult.Ok, LastDownMbps: not null })
				&& (context.UtcNow - s.LastTime < RecentPeriod))
				return Math.Min(score.Value, s.LastDownMbps.Value);

			return score;
		}

		bool IsEligible(ProfileItem p) => Score(p).HasValue
			? (context.SpeedSignalThreshold + EntryMargin <= p.Signal)
			: (context.SignalThreshold <= p.Signal);

		TimeSpan GetRetryPeriod(ProfileItem p)
		{
			state.RetryCounts.TryGetValue(p.Name, out int count);
			var ticks = RetryPeriod.Ticks * (1L << Math.Min(Math.Max(count, 0), 10));
			return TimeSpan.FromTicks(Math.Min(ticks, MaxRetryPeriod.Ticks));
		}

		// A network without internet access is retried with exponential backoff.
		bool IsRetryDue(ProfileItem p)
		{
			var period = GetRetryPeriod(p);
			if (context.UtcNow - Summary(p).LastTime < period)
				return false;

			return !state.ExploredAt.TryGetValue(p.Name, out DateTime exploredAt)
				|| !IsWithin(now, exploredAt, period);
		}

		bool IsUnexplored(ProfileItem p) =>
			!state.ExploredAt.TryGetValue(p.Name, out DateTime exploredAt)
			|| !IsWithin(now, exploredAt, ExplorePeriod);

		// Group 1: Usable speed, faster first
		// Group 2: No speed data, in priority order
		// Group 3: Barely usable speed, faster first
		// Group 4: No internet access (only if allowed)
		int Group(ProfileItem p) => Score(p) switch
		{
			null => 2,
			>= SlowSpeed => 1,
			_ => IsDead(p) ? 4 : 3
		};

		List<ProfileItem> Rank(IEnumerable<ProfileItem> list, bool includesAllDead = false) => list
			.Where(x => IsEligible(x))
			.Where(x => !IsDead(x) || includesAllDead || IsRetryDue(x))
			.OrderBy(x => Group(x))
			.ThenBy(x => RecentlyFailed(x))
			.ThenByDescending(x => Score(x) ?? 0D)
			.ThenBy(x => x.Position)
			.ToList();

		bool IsMarginOk(double target, double current) =>
			(target >= current * (1D + context.MarginPercent / 100D))
			&& (target - current >= MinDifference);

		void MarkExplored(ProfileItem p)
		{
			state.ExploredAt[p.Name] = now;

			if (IsDead(p))
			{
				state.RetryCounts.TryGetValue(p.Name, out int count);
				state.RetryCounts[p.Name] = count + 1;
			}
		}

		// The backoff starts over when a network is found with internet access again.
		foreach (var candidate in candidates)
		{
			if ((state.RetryCounts.Count > 0) && !IsDead(candidate))
				state.RetryCounts.Remove(candidate.Name);
		}

		bool IsCooling(ProfileItem p) => state.CooldownUntil.TryGetValue(p.Name, out DateTime until) && (until > now);

		var others = candidates
			.Where(x => !ReferenceEquals(x, connected))
			.Where(x => !IsCooling(x))
			.ToArray();

		// When there is nothing else to connect to, a network being put off is better than no
		// network or a network which does not work. It is not tried shortly after a failed attempt.
		List<ProfileItem> RankLastResort(bool includesAllDead = false)
		{
			var ranked = Rank(others, includesAllDead);
			if (ranked.Count > 0)
				return ranked;

			return Rank(candidates.Where(x => !ReferenceEquals(x, connected) && IsCooling(x) && !RecentlyFailed(x)), includesAllDead);
		}

		static string F0(double value) => value.ToString("0", CultureInfo.InvariantCulture);
		static string F1(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

		// (1) Not connected
		if (connected is null)
		{
			var ranked = RankLastResort(includesAllDead: true);
			var first = ranked.FirstOrDefault();
			if (first is null)
				return new SwitchDecision { Kind = SwitchKind.Stay };

			var isReturning = false;

			// After a brief disconnection, the previous network is preferred.
			if ((state.LastConnectedName is not null) && IsWithin(now, state.LastDisconnectedAt, ReturnPeriod))
			{
				var previous = ranked.FirstOrDefault(x => string.Equals(x.Name, state.LastConnectedName, StringComparison.Ordinal) && !IsDead(x));
				if ((previous is not null) && !ReferenceEquals(previous, first))
				{
					var previousScore = Score(previous);
					var firstScore = Score(first);

					if (!(previousScore.HasValue && firstScore.HasValue && IsMarginOk(firstScore.Value, previousScore.Value)))
					{
						first = previous;
						isReturning = true;
					}
				}
			}

			var score = Score(first);

			return new SwitchDecision
			{
				Target = first,
				Kind = SwitchKind.Switch,
				Reason = (isReturning ? $"Reconnecting to {first.Name}" : $"Connecting to {first.Name}")
					+ (score.HasValue ? $": about {F0(score.Value)} Mbps" : string.Empty),
				Code = $"connect|{first.Name}",
				ToScore = score
			};
		}

		var name = connected.Name;
		var connectedScore = Score(connected);

		SwitchDecision Stay(string code, string reason, bool requestsVerify = false, bool requestsLinkCheck = false) => new()
		{
			Kind = SwitchKind.Stay,
			Reason = reason,
			Code = code,
			FromScore = connectedScore,
			RequestsVerify = requestsVerify,
			RequestsLinkCheck = requestsLinkCheck
		};

		SwitchDecision Switch(ProfileItem target, string code, string reason) => new()
		{
			Target = target,
			Kind = SwitchKind.Switch,
			Reason = reason,
			Code = $"{code}|{name}|{target.Name}",
			FromScore = connectedScore,
			ToScore = Score(target)
		};

		if (state.HoldUntil > now)
			return Stay($"hold|{name}", $"Staying on {name}: no internet on any network tried - holding for a while");

		var isDwellOk = (now - state.ConnectedSince >= context.DwellTime);
		var isBudgetOk = (state.SwitchTimes.Count(x => IsWithin(now, x, BudgetPeriod)) < MaxSwitchCount);

		SwitchDecision StayForDwell() => Stay($"dwell|{name}",
			$"Staying on {name}: connected {Math.Max(0, (int)(now - state.ConnectedSince).TotalMinutes)} min ago, waiting {F0(context.DwellTime.TotalMinutes)} min");

		SwitchDecision StayForBudget() => Stay($"budget|{name}", "Too many switches in the last hour - holding");

		// (2) Connected profile is avoided.
		if (connected.IsAvoided)
		{
			var target = Rank(others).FirstOrDefault();
			if (target is null)
				return Stay($"avoid|{name}", $"Staying on {name} although it is marked Avoid: no other network in range");

			MarkExploredIfDead(target);
			return Switch(target, "avoid", $"{name} is marked Avoid - switching to {target.Name}");
		}

		void MarkExploredIfDead(ProfileItem p)
		{
			if (IsDead(p))
				MarkExplored(p);
		}

		// (0) No profile has speed data.
		if (!connectedScore.HasValue && candidates.All(x => !Score(x).HasValue))
		{
			const string reason = "No speed data yet - using priority order";

			var target = UpstreamPick(candidates);
			if ((target is null) || ReferenceEquals(target, connected))
				return Stay($"nodata|{name}", reason);

			if (!isDwellOk)
				return StayForDwell();

			if (!isBudgetOk)
				return StayForBudget();

			return Switch(target, "nodata", reason);
		}

		// (3) Connected profile has no internet access or its signal is too weak.
		var isWeak = (connected.Signal < context.SpeedSignalThreshold);
		if (!isWeak)
			state.WeakSince = default;
		else if (state.WeakSince == default)
			state.WeakSince = now;

		// Weak signal must last so that a momentary drop is ignored.
		var isWeakConfirmed = isWeak && (now - state.WeakSince >= PersistPeriod);

		if (connectedScore is 0D || isWeakConfirmed)
		{
			var isOffline = connectedScore is 0D;
			var why = isOffline
				? $"{name} has no internet"
				: $"{name} signal too weak ({connected.Signal}%)";
			var code = isOffline ? "offline" : WeakCode;

			var target = RankLastResort().FirstOrDefault();
			if (target is null)
				return Stay($"{code}|{name}", $"{why} - no other network in range");

			if (!isBudgetOk)
				return StayForBudget();

			MarkExploredIfDead(target);
			return Switch(target, code, $"{why} - switching to {target.Name}");
		}

		// (4) Connected profile has no speed data while others have.
		if (!connectedScore.HasValue)
			return Stay($"untested|{name}", $"Staying on {name} until it has been tested", requestsVerify: true);

		var c = connectedScore.Value;

		// (5) Connected profile is fast enough.
		if (c >= context.SufficientSpeed)
		{
			state.ClearPending();
			return Stay($"enough|{name}", $"Staying on {name}: fast enough ({F0(c)} Mbps)");
		}

		// Persistence, budget and pause in traffic required to leave a working network
		SwitchDecision Leave(ProfileItem target, bool isExploring)
		{
			var b = isExploring ? Score(target) : TargetScore(target);

			var waiting = !isExploring
				? $"{target.Name} looks faster ({F0(b.Value)} vs {F0(c)} Mbps)"
				: IsDead(target)
					? $"{target.Name} may have internet again and {name} is slow ({F1(c)} Mbps)"
					: $"{target.Name} is untested and {name} is slow ({F1(c)} Mbps)";
			var code = isExploring ? "explore" : "faster";

			if (!isDwellOk)
				return StayForDwell();

			// The speed of connected profile must be fresh evidence.
			if (!isExploring && (Summary(connected).LastTime < context.UtcNow - context.FreshPeriod))
				return Stay($"retest|{name}", $"Staying on {name}: retesting before leaving", requestsVerify: true);

			if (!string.Equals(state.PendingTarget, target.Name, StringComparison.Ordinal))
			{
				state.PendingTarget = target.Name;
				state.PendingSince = now;
				state.DeferredSince = default;
				return Stay($"confirm|{name}|{target.Name}", $"{waiting} - confirming");
			}

			if (now - state.PendingSince < PersistPeriod)
				return Stay($"confirm|{name}|{target.Name}", $"{waiting} - confirming");

			if (!isBudgetOk)
				return StayForBudget();

			if (context.MaxDeferral > TimeSpan.Zero)
			{
				if (!context.IsLinkBusy.HasValue)
				{
					// While the switch is deferred, the code is kept so that the same decision is
					// not recorded again every time traffic is checked.
					return (state.DeferredSince != default)
						? Stay($"lull|{name}|{target.Name}", $"{waiting} - waiting for a pause in traffic", requestsLinkCheck: true)
						: Stay($"confirm|{name}|{target.Name}", $"{waiting} - confirming", requestsLinkCheck: true);
				}

				if (context.IsLinkBusy.Value)
				{
					if (state.DeferredSince == default)
						state.DeferredSince = now;

					if (now - state.DeferredSince < context.MaxDeferral)
						return Stay($"lull|{name}|{target.Name}", $"{waiting} - waiting for a pause in traffic");
				}
			}

			if (isExploring)
			{
				MarkExplored(target);
				return Switch(target, code, IsDead(target)
					? $"{name} is slow ({F1(c)} Mbps) - trying {target.Name} again"
					: $"{name} is slow ({F1(c)} Mbps) - trying untested {target.Name}");
			}

			return Switch(target, code, $"Switched {name} -> {target.Name}: about {F0(b.Value)} Mbps vs {F0(c)} Mbps");
		}

		// (6) Another profile is faster by the margin.
		var rankedOthers = Rank(others);
		var best = rankedOthers
			.Where(x => TargetScore(x) >= SlowSpeed)
			.OrderByDescending(x => TargetScore(x).Value) // The order of rank is kept among equals.
			.FirstOrDefault();
		if ((best is not null) && IsMarginOk(TargetScore(best).Value, c))
			return Leave(best, false);

		// (7) Another profile is worth trying.
		var unknown = rankedOthers.FirstOrDefault(x => (!Score(x).HasValue && IsUnexplored(x)) || IsDead(x));
		if (unknown is not null)
			return Leave(unknown, true);

		// (8) Otherwise
		state.ClearPending();

		var stayReason = ((best is not null) && (TargetScore(best).Value > c))
			? $"Staying on {name}: {best.Name} is faster ({F0(TargetScore(best).Value)} vs {F0(c)} Mbps) but not by enough"
			: $"Staying on {name}: no better network in range";

		var untested = rankedOthers.FirstOrDefault(x => !Score(x).HasValue);
		if (untested is not null)
			stayReason += $" - {untested.Name} is untested - run Test all networks";

		return Stay($"stay|{name}|{best?.Name}|{untested?.Name}", stayReason);

		#endregion
	}

	/// <summary>
	/// Decides whether to wait instead of connecting while no profile is connected.
	/// </summary>
	/// <param name="context">Conditions</param>
	/// <param name="state">State of the interface (null if not tracked)</param>
	/// <returns>Decision to wait, null if a connection can be started</returns>
	/// <remarks>
	/// "Not connected" includes the moments in which the interface is on its way to a network
	/// chosen by the user or the system. A connection started then would take the interface back
	/// and so undo that choice again and again.
	/// </remarks>
	public static SwitchDecision GetConnectWait(SwitchContext context, SwitchState state)
	{
		if (context is null)
			throw new ArgumentNullException(nameof(context));

		if (context.IsInterfaceBusy)
		{
			return new SwitchDecision
			{
				Kind = SwitchKind.Stay,
				Reason = "Waiting: the adapter is being connected to a network outside this app",
				Code = ConnectingCode,
				RecheckAfter = ConnectingRecheck
			};
		}

		if (state is null)
			return null;

		var now = context.Now;

		if ((state.LastDisconnectedAt == default) || !IsWithin(now, state.LastDisconnectedAt, DisconnectGrace))
			return null;

		// The clock may have been set back.
		if (now < state.LastDisconnectedAt)
			return null;

		// A disconnection which follows a failed connection by this app is of its own making.
		if ((state.OwnFailureAt != default)
			&& (state.OwnFailureAt <= state.LastDisconnectedAt)
			&& IsWithin(state.LastDisconnectedAt, state.OwnFailureAt, OwnFailurePeriod))
			return null;

		return new SwitchDecision
		{
			Kind = SwitchKind.Stay,
			Reason = $"Waiting {DisconnectGrace.TotalSeconds:0} s after disconnection before connecting",
			Code = GraceCode,
			RecheckAfter = state.LastDisconnectedAt + DisconnectGrace - now
		};
	}

	private static bool IsWithin(DateTime now, DateTime time, TimeSpan period) => (now - time < period);
}
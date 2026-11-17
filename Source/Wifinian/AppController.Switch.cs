using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Measure;
using Wifinian.Models.Wlan;

namespace Wifinian;

internal partial class AppController
{
	#region Switch

	private static readonly TimeSpan _postSwitchDelay = TimeSpan.FromSeconds(20);
	private static readonly TimeSpan _retestDelay = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan _verifyInterval = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan _recheckRecordInterval = TimeSpan.FromMinutes(30);
	private static readonly TimeSpan _dryRunInterval = TimeSpan.FromHours(1);

	private static readonly TimeSpan _lullWindow = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan _linkCheckPeriod = TimeSpan.FromSeconds(60);
	private const double LullRate = 0.2; // Mbps

	private readonly Dictionary<Guid, SwitchState> _switchStates = new();
	private readonly Dictionary<Guid, SwitchState> _dryRunStates = new();
	private readonly Dictionary<Guid, SwitchDecision> _lastDecisions = new();
	private readonly Dictionary<Guid, string> _lastLoggedReasons = new(); // Codes of decisions
	private readonly Dictionary<Guid, (string code, DateTime time)> _lastDryRuns = new(); // UTC
	private readonly Dictionary<Guid, SwitchLanding> _landings = new();
	private readonly HashSet<Guid> _verifyRequests = new(); // Interface IDs
	private readonly HashSet<Guid> _linkCheckRequests = new(); // Interface IDs
	private readonly Dictionary<Guid, (bool isBusy, DateTime time)> _linkChecks = new(); // UTC
	private readonly HashSet<Guid> _revertingIds = new(); // Interface IDs
	private readonly Dictionary<string, DateTime> _lastVerifyTimes = new(); // UTC
	private readonly Dictionary<string, DateTime> _lastOfflineTimes = new(); // UTC
	private readonly object _switchLock = new();

	/// <summary>
	/// Whether networks are being connected to one after another for measurement
	/// </summary>
	/// <remarks>While this is true, no automatic connection is made.</remarks>
	private volatile bool _isProbing = false;

	/// <summary>
	/// Gets the profiles which are allowed to be connected to automatically in an interface.
	/// </summary>
	/// <param name="subProfiles">Profiles of an interface</param>
	private static IEnumerable<ProfileItem> GetCandidates(IEnumerable<ProfileItem> subProfiles) =>
		subProfiles.FirstOrDefault(x => x.IsConnected) switch
		{
			// If no profile is connected, select profiles that are automatic connection enabled.
			null => subProfiles.Where(x => x.IsAutoConnectEnabled),

			// If a profile is connected and it is automatic switch enabled, select profiles
			// that are automatic switch enabled including connected one.
			{ IsAutoSwitchEnabled: true } => subProfiles.Where(x => x.IsAutoSwitchEnabled),

			// If a profile is connected but it is not automatic switch enabled, leave as it is.
			_ => Enumerable.Empty<ProfileItem>()
		};

	private static SwitchState GetSwitchState(Dictionary<Guid, SwitchState> states, Guid interfaceId)
	{
		if (!states.TryGetValue(interfaceId, out SwitchState state))
		{
			state = new SwitchState();
			states[interfaceId] = state;
		}
		return state;
	}

	private SwitchContext CreateSwitchContext(bool engagesSpeed, bool? isLinkBusy) => new()
	{
		EngagesSpeed = engagesSpeed,
		SignalThreshold = Settings.Current.SignalThreshold,
		SpeedSignalThreshold = Settings.Current.SpeedSignalThreshold,
		DwellTime = TimeSpan.FromMinutes(Settings.Current.SwitchDwellTime),
		MarginPercent = Settings.Current.SwitchMargin,
		SufficientSpeed = Settings.Current.SufficientSpeed,
		FailureInterval = _failureInterval,
		// A sample of periodic measurement is accepted until the next one is surely taken.
		FreshPeriod = TimeSpan.FromMinutes(2 * Settings.Current.SpeedTestInterval),
		MaxDeferral = TimeSpan.FromMinutes(Settings.Current.SwitchMaxDeferral),
		IsLinkBusy = isLinkBusy,
		Now = DateTime.Now,
		UtcNow = DateTime.UtcNow,
		GetSummary = p => History.GetSummary(HistoryService.GetKey(p.Name, p.InterfaceId))
	};

	/// <summary>
	/// Appends a switching event to history unless nothing is to be recorded.
	/// </summary>
	private void AppendSwitchEvent(SwitchEvent switchEvent)
	{
		if (!Settings.Current.RecordsHistory && !Settings.Current.EngagesSpeed)
			return;

		History.AppendEvent(switchEvent);
	}

	/// <summary>
	/// Selects the profile to connect to in an interface.
	/// </summary>
	/// <param name="interfaceId">Interface ID</param>
	/// <param name="candidates">Profiles allowed to be connected to except avoided ones</param>
	/// <param name="connected">Connected profile (null if none)</param>
	/// <returns>Profile to connect to (null to stay)</returns>
	/// <remarks>
	/// This method performs no I/O because it is called during an update. Traffic of the link,
	/// if required by the decision, is checked in background and then the profiles are loaded
	/// again. This method never throws an exception.
	/// </remarks>
	private ProfileItem SelectTarget(Guid interfaceId, ProfileItem[] candidates, ProfileItem connected)
	{
		try
		{
			var engagesSpeed = Settings.Current.EngagesSpeed;

			// Leaving a working network is put off while the link is in use.
			var decision = DecideTarget(interfaceId, candidates, connected, engagesSpeed, GetLinkCheck(interfaceId));
			if (decision is null)
				return null;

			if (decision.RequestsLinkCheck && engagesSpeed)
				CheckLink(interfaceId);

			SwitchEvent stayEvent = null;

			lock (_switchLock)
			{
				_lastDecisions[interfaceId] = decision;

				if (engagesSpeed)
				{
					if (decision.Target is not null)
					{
						_lastLoggedReasons.Remove(interfaceId);
					}
					else
					{
						// A decision to stay is recorded only when it changes.
						if (!string.IsNullOrEmpty(decision.Reason)
							&& !(_lastLoggedReasons.TryGetValue(interfaceId, out string code) && string.Equals(code, decision.Code, StringComparison.Ordinal)))
						{
							_lastLoggedReasons[interfaceId] = decision.Code;

							stayEvent = new SwitchEvent
							{
								Time = DateTime.UtcNow,
								InterfaceId = interfaceId,
								Kind = SwitchKind.Stay,
								From = connected?.Name ?? string.Empty,
								To = string.Empty,
								FromScore = decision.FromScore,
								Reason = decision.Reason
							};
						}

						// The measurement is started after this update finishes.
						if (decision.RequestsVerify)
							_verifyRequests.Add(interfaceId);
					}
				}
			}

			if (stayEvent is not null)
				AppendSwitchEvent(stayEvent);

			return decision.Target;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to select the profile to connect to." + Environment.NewLine
				+ ex);
			return null;
		}
	}

	/// <summary>
	/// Gets the latest result of traffic check of an interface.
	/// </summary>
	/// <returns>True if the link is in use, false if not, null if not checked recently</returns>
	private bool? GetLinkCheck(Guid interfaceId)
	{
		lock (_switchLock)
		{
			if (!_linkChecks.TryGetValue(interfaceId, out var check))
				return null;

			if ((DateTime.UtcNow - check.time).Duration() < _linkCheckPeriod)
				return check.isBusy;

			_linkChecks.Remove(interfaceId);
			return null;
		}
	}

	/// <summary>
	/// Checks traffic of an interface in background and then loads the profiles again so that
	/// the decision is made with the result.
	/// </summary>
	/// <remarks>This method returns immediately and never throws an exception.</remarks>
	private void CheckLink(Guid interfaceId)
	{
		lock (_switchLock)
		{
			// A check of the interface is already running.
			if (!_linkCheckRequests.Add(interfaceId))
				return;
		}

		Task.Run(async () =>
		{
			var isChecked = false;
			try
			{
				var isBusy = await LinkInspector.GetRateMbpsAsync(interfaceId, _lullWindow).ConfigureAwait(false) > LullRate;

				lock (_switchLock)
				{
					// The request is gone if the user connected or disconnected in the meantime.
					isChecked = _linkCheckRequests.Remove(interfaceId);
					if (isChecked)
						_linkChecks[interfaceId] = (isBusy, DateTime.UtcNow);
				}

				// If an update is running, this load is skipped and the result is used by
				// the following one as long as it is recent.
				if (isChecked && !_disposed)
					await LoadProfilesAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to check traffic of the link." + Environment.NewLine
					+ ex);
			}
			finally
			{
				if (!isChecked)
				{
					lock (_switchLock)
					{
						_linkCheckRequests.Remove(interfaceId);
					}
				}
			}
		});
	}

	private SwitchDecision DecideTarget(Guid interfaceId, ProfileItem[] candidates, ProfileItem connected, bool engagesSpeed, bool? isLinkBusy)
	{
		lock (_switchLock)
		{
			// Switching back is in progress.
			if (_revertingIds.Contains(interfaceId))
				return null;

			var state = GetSwitchState(_switchStates, interfaceId);
			var context = CreateSwitchContext(engagesSpeed, isLinkBusy);

			SwitchPolicy.Track(state, connected?.Name, context.Now);

			return SwitchPolicy.Decide(candidates, connected, context, state);
		}
	}

	/// <summary>
	/// Records the result of automatic connection to a profile.
	/// </summary>
	/// <remarks>This method never throws an exception.</remarks>
	private void RecordSwitchResult(ProfileItem target, bool result)
	{
		try
		{
			var interfaceId = target.InterfaceId;
			var engagesSpeed = Settings.Current.EngagesSpeed;
			var now = DateTime.Now;

			SwitchDecision decision;
			string fromName;
			SwitchLanding landing = null;

			lock (_switchLock)
			{
				if (_lastDecisions.TryGetValue(interfaceId, out decision) && !ReferenceEquals(decision.Target, target))
					decision = null;

				var state = GetSwitchState(_switchStates, interfaceId);
				fromName = state.ConnectedName;

				SwitchPolicy.RecordAttempt(state, target.Name, result, now, setsCooldown: engagesSpeed);

				_landings.Remove(interfaceId);
			}

			if (result && engagesSpeed && (fromName is not null)
				&& !string.Equals(fromName, target.Name, StringComparison.Ordinal))
			{
				landing = new SwitchLanding
				{
					FromName = fromName,
					// The network left for weak signal is never switched back to for speed.
					FromScore = SwitchPolicy.GetRevertScore(decision),
					ToName = target.Name,
					Time = DateTime.UtcNow,
					FromOfflineTime = GetOfflineTime(fromName, interfaceId)
				};

				lock (_switchLock)
				{
					_landings[interfaceId] = landing;
				}
			}

			AppendSwitchEvent(new SwitchEvent
			{
				Time = DateTime.UtcNow,
				InterfaceId = interfaceId,
				Kind = result ? SwitchKind.Switch : SwitchKind.SwitchFailed,
				From = fromName ?? string.Empty,
				To = target.Name,
				FromScore = decision?.FromScore,
				ToScore = decision?.ToScore,
				Reason = decision?.Reason ?? string.Empty
			});

			// The network connected to is measured and, if worse than expected, switched back from.
			if (landing is not null)
				MeasureAfterSwitch(interfaceId, _postSwitchDelay);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record the result of automatic connection." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Gets the time (UTC) when a profile was last found without internet access.
	/// </summary>
	/// <returns>Time if the latest valid result of the profile is no internet access, default otherwise</returns>
	private DateTime GetOfflineTime(string profileName, Guid interfaceId)
	{
		var key = HistoryService.GetKey(profileName, interfaceId);

		var summary = History.GetSummary(key);
		if (summary is not { LastResult: SpeedResult.NoInternet or SpeedResult.CaptivePortal })
			return default;

		lock (_switchLock)
		{
			return (_lastOfflineTimes.TryGetValue(key, out DateTime time) && (time > summary.LastTime))
				? time
				: summary.LastTime;
		}
	}

	private void MeasureAfterSwitch(Guid interfaceId, TimeSpan delay)
	{
		Task.Run(async () =>
		{
			try
			{
				await Task.Delay(delay).ConfigureAwait(false);

				if (_disposed)
					return;

				await MeasureConnectedAsync(SpeedTrigger.PostSwitch, interfaceId).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to measure speed after switch." + Environment.NewLine
					+ ex);
			}
		});
	}

	/// <summary>
	/// Determines whether an automatic switch in an interface is yet to be checked.
	/// </summary>
	private bool HasLanding(Guid interfaceId)
	{
		lock (_switchLock)
		{
			if (!_landings.TryGetValue(interfaceId, out SwitchLanding landing))
				return false;

			if (DateTime.UtcNow - landing.Time < SwitchPolicy.LandingPeriod)
				return true;

			_landings.Remove(interfaceId);
			return false;
		}
	}

	/// <summary>
	/// Checks a speed sample against the automatic switch made just before and switches back
	/// if the network is worse than the previous one.
	/// </summary>
	/// <remarks>This method never throws an exception.</remarks>
	private async Task CheckLandingAsync(SpeedSample sample)
	{
		try
		{
			var interfaceId = sample.InterfaceId;

			SwitchLanding landing;
			LandingAction action;

			lock (_switchLock)
			{
				if (!_landings.TryGetValue(interfaceId, out landing))
					return;

				action = SwitchPolicy.EvaluateLanding(landing, sample, DateTime.UtcNow);

				if (action is not (LandingAction.None or LandingAction.Retest))
					_landings.Remove(interfaceId);

				if (action is LandingAction.UplinkDown)
					GetSwitchState(_switchStates, interfaceId).HoldUntil = DateTime.Now + SwitchPolicy.HoldPeriod;
			}

			switch (action)
			{
				case LandingAction.Retest:
					MeasureAfterSwitch(interfaceId, _retestDelay);
					break;

				case LandingAction.UplinkDown:
					AppendSwitchEvent(new SwitchEvent
					{
						Time = DateTime.UtcNow,
						InterfaceId = interfaceId,
						Kind = SwitchKind.Stay,
						From = landing.ToName,
						To = string.Empty,
						Reason = $"No internet on both {landing.FromName} and {landing.ToName} - the uplink seems to be down, holding for {SwitchPolicy.HoldPeriod.TotalMinutes:0} min"
					});
					break;

				case LandingAction.Revert:
					await RevertAsync(landing, sample).ConfigureAwait(false);
					break;
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to check the network switched to." + Environment.NewLine
				+ ex);
		}
	}

	private async Task RevertAsync(SwitchLanding landing, SpeedSample sample)
	{
		if (_disposed || _isProbing || !EngagesPriority.Value || !Settings.Current.EngagesSpeed)
			return;

		var interfaceId = sample.InterfaceId;

		// The network to switch back to must satisfy the same rule as any other target so that
		// a network whose signal has become weak is not returned to.
		var entrySignal = Settings.Current.SpeedSignalThreshold + SwitchPolicy.EntryMargin;

		ProfileItem current;
		ProfileItem from;
		lock (_profilesLock)
		{
			var subProfiles = Profiles.Where(x => x.InterfaceId == interfaceId).ToArray();

			current = subProfiles.FirstOrDefault(x => x.IsConnected);
			from = subProfiles.FirstOrDefault(x => !x.IsConnected
				&& !x.IsAvoided
				&& (x.Signal >= entrySignal)
				&& string.Equals(x.Name, landing.FromName, StringComparison.Ordinal));
		}

		if (!string.Equals(current?.Name, landing.ToName, StringComparison.Ordinal))
			return;

		var why = (sample.Result is SpeedResult.Ok)
			? string.Format(CultureInfo.InvariantCulture, "{0} measured {1:0.#} Mbps, worse than {2} ({3:0} Mbps)",
				landing.ToName, sample.DownMbps ?? 0D, landing.FromName, landing.FromScore ?? 0D)
			: $"{landing.ToName} has no internet";

		if (from is null)
		{
			AppendSwitchEvent(new SwitchEvent
			{
				Time = DateTime.UtcNow,
				InterfaceId = interfaceId,
				Kind = SwitchKind.Stay,
				From = landing.ToName,
				To = string.Empty,
				FromScore = sample.DownMbps,
				Reason = $"{why} but {landing.FromName} is no longer in range or too weak"
			});
			return;
		}

		var now = DateTime.Now;

		lock (_switchLock)
		{
			_revertingIds.Add(interfaceId);

			var state = GetSwitchState(_switchStates, interfaceId);
			state.CooldownUntil[landing.ToName] = now + SwitchPolicy.RevertCooldown;
			state.ClearPending();
		}

		var result = false;
		try
		{
			result = await ConnectNetworkAsync(from, false).ConfigureAwait(false);
			from.LastFailureTime = result ? default : DateTime.Now;
		}
		finally
		{
			lock (_switchLock)
			{
				_revertingIds.Remove(interfaceId);

				// Switching back is never followed by another check and so no record is made.
				SwitchPolicy.RecordAttempt(GetSwitchState(_switchStates, interfaceId), from.Name, result, now, setsCooldown: false);
			}
		}

		AppendSwitchEvent(new SwitchEvent
		{
			Time = DateTime.UtcNow,
			InterfaceId = interfaceId,
			Kind = result ? SwitchKind.Revert : SwitchKind.SwitchFailed,
			From = landing.ToName,
			To = landing.FromName,
			FromScore = sample.DownMbps,
			ToScore = landing.FromScore,
			Reason = $"{why} - switching back"
		});
	}

	/// <summary>
	/// Starts measurements of connected networks requested by switching decisions.
	/// </summary>
	/// <remarks>This method never throws an exception.</remarks>
	private async Task MeasureRequestedAsync()
	{
		try
		{
			Guid[] interfaceIds;
			lock (_switchLock)
			{
				if (_verifyRequests.Count == 0)
					return;

				interfaceIds = _verifyRequests.ToArray();
				_verifyRequests.Clear();
			}

			foreach (var interfaceId in interfaceIds)
				await MeasureConnectedAsync(SpeedTrigger.Verify, interfaceId).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to measure speed requested by switching decision." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Determines whether the connected profile of an interface, which this app can switch from,
	/// was found without internet access by its latest valid measurement.
	/// </summary>
	private bool IsConnectedOffline(Guid interfaceId)
	{
		ProfileItem profile;
		lock (_profilesLock)
		{
			profile = Profiles.FirstOrDefault(x => x.IsConnected && (x.InterfaceId == interfaceId));
		}

		return profile is
		{
			IsAutoSwitchEnabled: true,
			IsAvoided: false,
			LastSpeedResult: SpeedResult.NoInternet or SpeedResult.CaptivePortal
		};
	}

	/// <summary>
	/// Forgets what is pending on an interface when the user connects or disconnects manually.
	/// </summary>
	private void ClearPendingSwitch(Guid interfaceId)
	{
		lock (_switchLock)
		{
			if (_switchStates.TryGetValue(interfaceId, out SwitchState state))
				state.ClearPending();

			_landings.Remove(interfaceId);
			_verifyRequests.Remove(interfaceId);
			_linkCheckRequests.Remove(interfaceId);
			_linkChecks.Remove(interfaceId);
		}
	}

	/// <summary>
	/// Restarts the time to stay on connected networks.
	/// </summary>
	private void ResetDwell()
	{
		var now = DateTime.Now;

		lock (_switchLock)
		{
			foreach (var state in _switchStates.Values.Concat(_dryRunStates.Values))
			{
				state.ConnectedSince = now;
				state.ClearPending();
			}
		}
	}

	/// <summary>
	/// Reflects connected profiles to the states of switching and, while switching by speed is
	/// not engaged, records what it would do.
	/// </summary>
	/// <remarks>This method never connects and never throws an exception.</remarks>
	private void EvaluateDryRun()
	{
		try
		{
			if (_isProbing)
				return;

			var isDryRun = Settings.Current.RecordsHistory && !Settings.Current.EngagesSpeed;
			var events = new List<SwitchEvent>();

			foreach (var subProfiles in GetProfiles().GroupBy(x => x.InterfaceId))
			{
				var interfaceId = subProfiles.Key;
				var connected = subProfiles.FirstOrDefault(x => x.IsConnected);

				lock (_switchLock)
				{
					var now = DateTime.Now;

					// The time of connection is kept regardless of settings so that it is correct
					// when switching is engaged.
					if (!_revertingIds.Contains(interfaceId))
						SwitchPolicy.Track(GetSwitchState(_switchStates, interfaceId), connected?.Name, now);

					var state = GetSwitchState(_dryRunStates, interfaceId);
					SwitchPolicy.Track(state, connected?.Name, now);

					if (!isDryRun || (connected is null))
						continue;

					var candidates = GetCandidates(subProfiles)
						.Where(x => !x.IsAvoided)
						.ToArray();

					var context = CreateSwitchContext(true, false);

					var decision = SwitchPolicy.Decide(candidates, connected, context, state);
					if (decision.Target is null)
						continue;

					// The same decision is repeated as long as the situation lasts.
					if (_lastDryRuns.TryGetValue(interfaceId, out var last)
						&& string.Equals(last.code, decision.Code, StringComparison.Ordinal)
						&& (context.UtcNow - last.time < _dryRunInterval))
						continue;

					_lastDryRuns[interfaceId] = (decision.Code, context.UtcNow);

					events.Add(new SwitchEvent
					{
						Time = context.UtcNow,
						InterfaceId = interfaceId,
						Kind = SwitchKind.WouldSwitch,
						From = connected.Name,
						To = decision.Target.Name,
						FromScore = decision.FromScore,
						ToScore = decision.ToScore,
						Reason = (decision.Reason ?? string.Empty).Replace("Switched", "Would switch")
					});
				}
			}

			events.ForEach(x => History.AppendEvent(x));
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to evaluate switching by speed." + Environment.NewLine
				+ ex);
		}
	}

	#endregion
}
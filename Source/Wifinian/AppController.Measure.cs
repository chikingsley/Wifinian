using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedNativeWifi;
using Microsoft.Win32;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;
using Reactive.Bindings.Notifiers;

using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Measure;
using Wifinian.Models.Wlan;

namespace Wifinian;

internal partial class AppController
{
	#region Measure

	private static readonly TimeSpan _afterConnectDelay = TimeSpan.FromSeconds(20);
	private static readonly TimeSpan _afterConnectValidity = TimeSpan.FromMinutes(30);
	private static readonly TimeSpan _addressTimeout = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan _resumeHoldoff = TimeSpan.FromSeconds(30);

	private static readonly TimeSpan _busyWindow = TimeSpan.FromSeconds(2);
	private const double BusyRate = 1.0; // Mbps

	private static readonly TimeSpan _scanBusyWindow = TimeSpan.FromSeconds(1);
	private const double ScanBusyRate = 0.2; // Mbps

	private static readonly TimeSpan _idleWaitTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan _idleWaitInterval = TimeSpan.FromMilliseconds(500);

	private readonly SpeedTester _tester = new();
	private readonly SemaphoreSlim _measureSemaphore = new(1);
	private CancellationTokenSource _measureCancellation;
	private Guid _measureInterfaceId;
	private readonly Dictionary<string, DateTime> _lastAttemptTimes = new(); // UTC
	private readonly HashSet<Guid> _afterConnectPendings = new(); // Interface IDs
	private readonly object _measureLock = new();
	private long _resumeTicks; // UTC

	public BooleanNotifier IsMeasuring { get; } = new();

	public ReactiveCommand MeasureCommand { get; private set; }

	private void InitiateMeasure()
	{
		MeasureCommand = IsMeasuring
			.Inverse()
			.ObserveOnUIDispatcher() // This is for thread access by ReactiveCommand.
			.ToReactiveCommand();
		MeasureCommand
			.Subscribe(async _ => await MeasureConnectedAsync(SpeedTrigger.Manual))
			.AddTo(this.Subscription);

		var connectionChanged = Observable.FromEventPattern<ConnectionChangedEventArgs>(
			h => _worker.ConnectionChanged += h,
			h => _worker.ConnectionChanged -= h)
			.Select(x => x.EventArgs)
			.Where(x => x is not null);

		connectionChanged
			.Where(x => x.ChangedState == ConnectionChangedState.Completed)
			.Where(_ => !_isProbing) // The test of networks measures the networks it connects to.
			.GroupBy(x => x.InterfaceId)
			.SelectMany(x => x.Throttle(_afterConnectDelay))
			.Subscribe(async x => await MeasureConnectedAsync(SpeedTrigger.AfterConnect, x.InterfaceId))
			.AddTo(this.Subscription);

		// A measurement is no longer of the network when the connection of the interface changes.
		connectionChanged
			.Where(x => x.ChangedState is ConnectionChangedState.Started
				or ConnectionChangedState.Failed
				or ConnectionChangedState.Disconnecting
				or ConnectionChangedState.Disconnected)
			.Subscribe(x =>
			{
				SetAfterConnectPending(x.InterfaceId, false);

				// The test of networks handles the changes of connection made by itself.
				if (!_isProbing)
					CancelMeasure(x.InterfaceId);
			})
			.AddTo(this.Subscription);

		Observable.FromEventPattern<PowerModeChangedEventHandler, PowerModeChangedEventArgs>(
			h => SystemEvents.PowerModeChanged += h,
			h => SystemEvents.PowerModeChanged -= h)
			.Where(x => x.EventArgs.Mode == PowerModes.Resume)
			.Subscribe(_ =>
			{
				Interlocked.Exchange(ref _resumeTicks, DateTime.UtcNow.Ticks);

				// The connection is regarded as new after resume.
				ResetDwell();
			})
			.AddTo(this.Subscription);

		InitiateProbe();
	}

	/// <summary>
	/// Whether the system has resumed from sleep just now
	/// </summary>
	/// <remarks>Sample ticks and automatic measurements are held off for a while after resume.</remarks>
	private bool IsResumed
	{
		get
		{
			var elapsedTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref _resumeTicks);
			return (0L <= elapsedTicks) && (elapsedTicks < _resumeHoldoff.Ticks);
		}
	}

	/// <summary>
	/// Sets whether the measurement after connection of an interface is pending.
	/// </summary>
	/// <remarks>
	/// The measurement after connection is triggered only once. If it is put off because of
	/// temporary conditions, it is tried again at sample ticks until performed or no longer
	/// necessary.
	/// </remarks>
	private void SetAfterConnectPending(Guid interfaceId, bool isPending)
	{
		lock (_measureLock)
		{
			if (isPending)
				_afterConnectPendings.Add(interfaceId);
			else
				_afterConnectPendings.Remove(interfaceId);
		}
	}

	private bool IsAfterConnectPending(Guid interfaceId)
	{
		lock (_measureLock)
		{
			return _afterConnectPendings.Contains(interfaceId);
		}
	}

	/// <summary>
	/// Waits for rescan and connection/disconnection in progress to end.
	/// </summary>
	/// <returns>True if none of them is in progress</returns>
	private async Task<bool> WaitForIdleAsync()
	{
		var stopwatch = Stopwatch.StartNew();

		while (IsWorking.Value || IsUpdating.Value)
		{
			if (_disposed || (stopwatch.Elapsed >= _idleWaitTimeout))
				return false;

			await Task.Delay(_idleWaitInterval).ConfigureAwait(false);
		}
		return !_disposed;
	}

	/// <summary>
	/// Cancels the running measurement.
	/// </summary>
	/// <param name="interfaceId">Interface ID (any interface if null)</param>
	private void CancelMeasure(Guid? interfaceId = null)
	{
		try
		{
			CancellationTokenSource cancellation;

			lock (_measureLock)
			{
				if (interfaceId.HasValue && (interfaceId.Value != _measureInterfaceId))
					return;

				cancellation = _measureCancellation;
			}

			cancellation?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// The measurement has just ended.
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to cancel speed measurement." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Measures speed of connected network if conditions for the trigger are met.
	/// </summary>
	/// <param name="trigger">Trigger</param>
	/// <param name="interfaceId">Interface ID (any interface if null)</param>
	/// <param name="isRecheck">Whether this is to check again a network found without internet access</param>
	/// <returns>Speed sample if measured, null otherwise</returns>
	/// <remarks>This method never throws an exception.</remarks>
	private Task<SpeedSample> MeasureConnectedAsync(SpeedTrigger trigger, Guid? interfaceId = null, bool isRecheck = false)
	{
		return Task.Run(() => MeasureConnectedBaseAsync(trigger, interfaceId, isRecheck));
	}

	private async Task<SpeedSample> MeasureConnectedBaseAsync(SpeedTrigger trigger, Guid? interfaceId, bool isRecheck)
	{
		try
		{
			// While networks are being tested one after another, they are measured by the test itself.
			if (_disposed || _isProbing)
				return null;

			// Automatic measurements are for the networks between which this app can switch.
			var isAutomatic = trigger is SpeedTrigger.AfterConnect or SpeedTrigger.Periodic;

			// The measurement after connection put off by temporary conditions is left pending.
			var pendingId = (trigger is SpeedTrigger.AfterConnect) ? interfaceId : null;
			if (pendingId.HasValue)
				SetAfterConnectPending(pendingId.Value, false);

			SpeedSample PutOff()
			{
				if (pendingId.HasValue && !_disposed)
					SetAfterConnectPending(pendingId.Value, true);

				return null;
			}

			if (isAutomatic)
			{
				if (!Settings.Current.MeasuresSpeed)
					return null;

				if (IsResumed)
					return PutOff();
			}

			// The measurements for switching by speed are neither subject to the daily budget
			// nor put off by traffic in progress.
			if ((trigger is SpeedTrigger.Verify or SpeedTrigger.PostSwitch) && !isRecheck)
			{
				if (!Settings.Current.EngagesSpeed)
					return null;
			}

			ProfileItem profile;
			lock (_profilesLock)
			{
				profile = Profiles.FirstOrDefault(x => x.IsConnected && ((interfaceId is null) || (x.InterfaceId == interfaceId)));
			}
			if (profile is null)
				return null;

			var targetId = profile.InterfaceId;
			var key = HistoryService.GetKey(profile.Name, targetId);
			var now = DateTime.UtcNow;

			if (isAutomatic)
			{
				if (!profile.IsAutoSwitchEnabled || profile.IsAvoided)
					return null;

				switch (trigger)
				{
					case SpeedTrigger.AfterConnect:
						// The measurement after an automatic switch takes the place of this one.
						if (HasLanding(targetId))
							return PutOff();

						var summary = History.GetSummary(key);
						if ((summary is not null) && (now - summary.LastTime < _afterConnectValidity))
							return null;
						break;

					case SpeedTrigger.Periodic:
						if (now - GetLastAttemptTime(key) < TimeSpan.FromMinutes(Settings.Current.SpeedTestInterval))
							return null;
						break;
				}

				if (History.GetBytesToday() >= Settings.Current.SpeedTestDailyBudget * 1_000_000L)
					return null;
			}

			if ((trigger is SpeedTrigger.Verify) && !isRecheck)
			{
				// The measurement requested by switching decision is limited in frequency.
				DateTime lastVerifyTime;
				lock (_switchLock)
				{
					_lastVerifyTimes.TryGetValue(key, out lastVerifyTime);
				}
				if (now - lastVerifyTime < _verifyInterval)
					return null;
			}

			if (trigger is not SpeedTrigger.Manual)
			{
				// Rescan and reload of profiles are momentary and so are waited for.
				if (IsWorking.Value || IsUpdating.Value)
				{
					if (!await WaitForIdleAsync().ConfigureAwait(false))
						return PutOff();

					// The connection may have changed in the meantime.
					ProfileItem currentProfile;
					lock (_profilesLock)
					{
						currentProfile = Profiles.FirstOrDefault(x => x.IsConnected && (x.InterfaceId == targetId));
					}
					if (!ReferenceEquals(currentProfile, profile))
						return null;
				}
			}

			if (isAutomatic)
			{
				// Traffic in progress would both distort the result and be disturbed by the measurement.
				if (await LinkInspector.GetRateMbpsAsync(targetId, _busyWindow).ConfigureAwait(false) > BusyRate)
					return PutOff();
			}

			var (sample, isRecorded) = await MeasureProfileAsync(
				profile,
				trigger,
				(trigger is SpeedTrigger.Manual) ? SpeedTestSize.Full : SpeedTestSize.Quick,
				isRecheck).ConfigureAwait(false);

			// Another measurement is running.
			if (sample is null)
				return PutOff();

			if (!isRecorded)
				return sample;

			// If this network was switched to just before and is worse than the previous one,
			// the previous one is switched back to.
			await CheckLandingAsync(sample).ConfigureAwait(false);

			// Reload so that the profiles are evaluated with the new result.
			if (!_disposed)
			{
				await LoadProfilesAsync().ConfigureAwait(false);

				// The decision made in the reload may have requested another measurement.
				_ = Task.Run(() => MeasureRequestedAsync());
			}

			return sample;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to measure speed of connected network." + Environment.NewLine
				+ ex);
			return null;
		}
	}

	/// <summary>
	/// Measures speed of a connected profile and records the result.
	/// </summary>
	/// <param name="profile">Connected profile</param>
	/// <param name="trigger">Trigger</param>
	/// <param name="size">Size of measurement</param>
	/// <param name="isRecheck">Whether this is to check again a network found without internet access</param>
	/// <param name="waitTime">Time to wait for a running measurement to end</param>
	/// <returns>
	/// Speed sample and whether it is recorded in history. The sample is null if not measured
	/// because another measurement is running.
	/// </returns>
	/// <remarks>
	/// No condition for the trigger is checked by this method. If the connection has changed
	/// during the measurement, the result is recorded as aborted.
	/// </remarks>
	private async Task<(SpeedSample sample, bool isRecorded)> MeasureProfileAsync(ProfileItem profile, SpeedTrigger trigger, SpeedTestSize size, bool isRecheck = false, TimeSpan waitTime = default)
	{
		var targetId = profile.InterfaceId;
		var key = HistoryService.GetKey(profile.Name, targetId);

		if (!await _measureSemaphore.WaitAsync(waitTime).ConfigureAwait(false))
			return (null, false);

		SpeedSample sample;
		var isRecorded = true;
		CancellationTokenSource cancellation = null;
		try
		{
			IsMeasuring.TurnOn();
			profile.IsMeasuring = true;

			// The limit of frequency counts only the measurements actually started.
			if ((trigger is SpeedTrigger.Verify) && !isRecheck)
			{
				lock (_switchLock)
				{
					_lastVerifyTimes[key] = DateTime.UtcNow;
				}
			}

			lock (_measureLock)
			{
				_lastAttemptTimes[key] = DateTime.UtcNow;

				cancellation = new CancellationTokenSource();
				_measureCancellation = cancellation;
				_measureInterfaceId = targetId;
			}
			var token = cancellation.Token;

			try
			{
				if (!LinkInspector.IsReady(targetId))
					await LinkInspector.WaitForAddressAsync(targetId, _addressTimeout, token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// The measurement will record this as aborted.
			}

			var linkRate = profile.LinkRate;

			sample = new SpeedSample
			{
				Time = DateTime.UtcNow,
				InterfaceId = targetId,
				Profile = profile.Name,
				Bssid = profile.Bssid ?? string.Empty,
				Band = profile.Band,
				Channel = profile.Channel,
				Signal = profile.Signal,
				LinkRate = (linkRate > 0) ? linkRate : null,
				Trigger = trigger
			};

			await _tester.MeasureAsync(sample, size, token).ConfigureAwait(false);

			// If the connection has changed during the measurement, the result is not of the network.
			ProfileItem currentProfile;
			lock (_profilesLock)
			{
				currentProfile = Profiles.FirstOrDefault(x => x.IsConnected && (x.InterfaceId == targetId));
			}

			if (!ReferenceEquals(currentProfile, profile))
			{
				sample.Result = SpeedResult.Aborted;
			}
			else
			{
				var currentBssid = currentProfile.Bssid;
				if (!string.IsNullOrEmpty(currentBssid)
					&& !string.IsNullOrEmpty(sample.Bssid)
					&& !string.Equals(currentBssid, sample.Bssid, StringComparison.OrdinalIgnoreCase))
				{
					sample.Result = SpeedResult.Aborted;
				}
			}

			var isOffline = sample.Result is SpeedResult.NoInternet or SpeedResult.CaptivePortal;
			if (isOffline)
			{
				lock (_switchLock)
				{
					_lastOfflineTimes[key] = DateTime.UtcNow;
				}
			}

			// A recheck is repeated at every sample tick and so only what it newly finds is recorded.
			if (isRecheck)
			{
				isRecorded = sample.Valid
					&& !(isOffline
						&& (History.GetSummary(key) is { IsOffline: true } recorded)
						&& (DateTime.UtcNow - recorded.LastTime < _recheckRecordInterval));
			}

			if (isRecorded)
			{
				History.AppendSpeed(sample);
				ApplySummary(profile);
			}
		}
		finally
		{
			profile.IsMeasuring = false;

			lock (_measureLock)
			{
				_measureCancellation = null;
			}
			cancellation?.Dispose();

			IsMeasuring.TurnOff();
			_measureSemaphore.Release();
		}

		return (sample, isRecorded);
	}

	/// <summary>
	/// Gets the last time (UTC) when measurement of a profile was attempted.
	/// </summary>
	/// <remarks>
	/// The time is seeded from history so that measurement is not repeated at every start of
	/// this app.
	/// </remarks>
	private DateTime GetLastAttemptTime(string key)
	{
		lock (_measureLock)
		{
			if (_lastAttemptTimes.TryGetValue(key, out DateTime time))
				return time;
		}

		var samples = History.GetSpeedSamples(key);
		var lastTime = (samples.Count > 0) ? samples.Max(x => x.Time) : default;

		lock (_measureLock)
		{
			if (_lastAttemptTimes.TryGetValue(key, out DateTime time))
				return time;

			_lastAttemptTimes[key] = lastTime;
			return lastTime;
		}
	}

	/// <summary>
	/// Applies summary of speed samples in history to a profile.
	/// </summary>
	/// <remarks>This method only reads samples in memory.</remarks>
	private void ApplySummary(ProfileItem profile)
	{
		try
		{
			if (profile is null)
				return;

			var summary = History.GetSummary(HistoryService.GetKey(profile.Name, profile.InterfaceId));
			if (summary is null)
			{
				profile.LastDownMbps = null;
				profile.LastUpMbps = null;
				profile.LastLatencyMs = null;
				profile.LastMeasuredTime = default;
				profile.LastSpeedResult = SpeedResult.None;
				profile.TypicalDownMbps = null;
				profile.SpeedSampleCount = 0;
			}
			else
			{
				profile.LastDownMbps = summary.LastDownMbps;
				profile.LastUpMbps = summary.LastUpMbps;
				profile.LastLatencyMs = summary.LastLatencyMs;
				profile.LastMeasuredTime = summary.LastTime;
				profile.LastSpeedResult = summary.LastResult;
				profile.TypicalDownMbps = summary.HasData ? summary.DownMbps : null;
				profile.SpeedSampleCount = summary.Count;
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to apply speed summary." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Measures speed of connected network of each interface if periodic measurement is due.
	/// </summary>
	/// <remarks>
	/// The measurement after connection which is pending takes precedence. It is not subject to
	/// the interval of periodic measurement.
	/// </remarks>
	private async Task MeasureIfDueAsync()
	{
		var measuresSpeed = Settings.Current.MeasuresSpeed;
		if (!measuresSpeed && !Settings.Current.EngagesSpeed)
			return;

		foreach (var interfaceId in GetConnectedInterfaceIds())
		{
			// While connected network has no internet access, it is checked again at every
			// sample tick so that recovery is noticed soon. The measurement ends at the check
			// of internet access as long as it fails and so uses little data.
			if (IsConnectedOffline(interfaceId))
			{
				await MeasureConnectedAsync(SpeedTrigger.Verify, interfaceId, isRecheck: true);
				continue;
			}

			if (!measuresSpeed)
				continue;

			if (IsAfterConnectPending(interfaceId))
			{
				var sample = await MeasureConnectedAsync(SpeedTrigger.AfterConnect, interfaceId);
				if ((sample is not null) || IsAfterConnectPending(interfaceId))
					continue;
			}

			await MeasureConnectedAsync(SpeedTrigger.Periodic, interfaceId);
		}
	}

	private Guid[] GetConnectedInterfaceIds()
	{
		lock (_profilesLock)
		{
			return Profiles
				.Where(x => x.IsConnected)
				.Select(x => x.InterfaceId)
				.Distinct()
				.ToArray();
		}
	}

	/// <summary>
	/// Determines whether traffic of any connected interface is above the rate at which a scan,
	/// which takes the radio off the channel, should be avoided.
	/// </summary>
	private async Task<bool> IsLinkBusyForScanAsync()
	{
		var interfaceIds = GetConnectedInterfaceIds();
		if (interfaceIds.Length == 0)
			return false;

		var rates = await Task.WhenAll(interfaceIds.Select(x => LinkInspector.GetRateMbpsAsync(x, _scanBusyWindow)));
		return rates.Any(x => x > ScanBusyRate);
	}

	#endregion
}
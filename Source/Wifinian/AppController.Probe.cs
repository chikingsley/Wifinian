using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
	#region Probe

	private static readonly TimeSpan _probeQuietTimeout = TimeSpan.FromSeconds(20);
	private static readonly TimeSpan _probeSettleDelay = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan _probeLoadTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan _probeFreshTimeout = TimeSpan.FromSeconds(8);
	private static readonly TimeSpan _probeCloseTimeout = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan _probeMeasureWait = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan _probeRetryDelay = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan _probePollInterval = TimeSpan.FromMilliseconds(500);

	private static readonly TimeSpan _idleTime = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan _idleRateWindow = TimeSpan.FromSeconds(2);
	private const double IdleRate = 0.1; // Mbps

	/// <summary>
	/// Period after which a measurement of a network is regarded as old enough to test it when idle
	/// </summary>
	internal static readonly TimeSpan ProbeStalePeriod = TimeSpan.FromDays(3);

	/// <summary>
	/// Whether networks are being connected to one after another for measurement
	/// </summary>
	public BooleanNotifier IsProbing { get; } = new();

	/// <summary>
	/// Command to test all networks in range or, while the test is running, to cancel it
	/// </summary>
	public ReactiveCommand ProbeCommand { get; private set; }

	private CancellationTokenSource _probeCancellation;
	private volatile bool _isProbeInterrupted;
	private volatile bool _isClosing;
	private int _probeCount = 0;
	private DateTime? _lastIdleProbeTime; // UTC
	private readonly Dictionary<string, DateTime> _lastProbeTimes = new(); // UTC
	private readonly object _probeLock = new();

	private void InitiateProbe()
	{
		// This command is always enabled because it cancels the test while the test is running.
		ProbeCommand = new ReactiveProperty<bool>(true)
			.ToReactiveCommand();
		ProbeCommand
			.Subscribe(_ =>
			{
				if (_isProbing)
					CancelProbe(false);
				else
					Task.Run(() => ProbeAllAsync());
			})
			.AddTo(this.Subscription);
	}

	/// <summary>
	/// Cancels the running test of networks.
	/// </summary>
	/// <param name="isInterrupted">
	/// Whether the connection has been taken over, by the user or by closing this app, and so
	/// the originally connected network must not be connected to again
	/// </param>
	/// <remarks>
	/// Unless interrupted, the measurement in progress is finished and then the originally
	/// connected network is restored. This method never throws an exception.
	/// </remarks>
	private void CancelProbe(bool isInterrupted)
	{
		try
		{
			if (!_isProbing)
				return;

			CancellationTokenSource cancellation;

			lock (_probeLock)
			{
				if (isInterrupted)
					_isProbeInterrupted = true;

				cancellation = _probeCancellation;
			}

			cancellation?.Cancel();

			// The measurement in progress is no longer of the network being tested.
			if (isInterrupted)
				CancelMeasure();
		}
		catch (ObjectDisposedException)
		{
			// The test has just ended.
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to cancel the test of networks." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Ends the running test of networks when this app is closed and waits, for a limited time,
	/// for the originally connected network to be restored.
	/// </summary>
	/// <remarks>
	/// Without this wait, closing this app during the test would leave the machine on the network
	/// being tested. The test runs on thread pool threads and so the calling thread can wait.
	/// This method never throws an exception.
	/// </remarks>
	private void CloseProbe()
	{
		try
		{
			_isClosing = true;

			if (Volatile.Read(ref _probeCount) == 0)
				return;

			CancelProbe(false);

			// The measurement of the network being tested is of no use any longer.
			CancelMeasure();

			if (!SpinWait.SpinUntil(() => Volatile.Read(ref _probeCount) == 0, _probeCloseTimeout))
				Trace.WriteLine("The test of networks did not end in time on closing.");
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to end the test of networks on closing." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Gets the profiles to be tested in an interface when all networks are tested.
	/// </summary>
	/// <param name="subProfiles">Profiles of an interface</param>
	/// <returns>Profiles in range, connected one first and then in descending order of signal</returns>
	internal static ProfileItem[] GetProbeTargets(IEnumerable<ProfileItem> subProfiles) =>
		subProfiles
			.Where(x => x.IsRadioOn && (x.Signal > 0))
			.OrderByDescending(x => x.IsConnected) // The connected one requires no connection.
			.ThenByDescending(x => x.Signal)
			.ToArray();

	/// <summary>
	/// Selects the profile to be tested in an interface while this machine is idle.
	/// </summary>
	/// <param name="subProfiles">Profiles of an interface</param>
	/// <param name="signalThreshold">Minimum signal of a profile to be tested</param>
	/// <param name="getLastTime">Function to get the last time (UTC) a profile was measured or tested (default if never)</param>
	/// <param name="utcNow">Current time (UTC)</param>
	/// <returns>
	/// Profile, among those this app can switch to, which has been measured least recently
	/// and not within the period, null if none
	/// </returns>
	internal static ProfileItem SelectIdleCandidate(IEnumerable<ProfileItem> subProfiles, int signalThreshold, Func<ProfileItem, DateTime> getLastTime, DateTime utcNow)
	{
		var buffer = subProfiles.ToArray();
		if (!buffer.Any(x => x.IsConnected))
			return null;

		return GetCandidates(buffer)
			.Where(x => !x.IsAvoided && !x.IsConnected && x.IsRadioOn && (x.Signal >= signalThreshold))
			.Select(x => (profile: x, time: getLastTime(x)))
			.Where(x => x.time < utcNow - ProbeStalePeriod)
			.OrderBy(x => x.time) // The one never measured comes first.
			.ThenByDescending(x => x.profile.Signal)
			.Select(x => x.profile)
			.FirstOrDefault();
	}

	/// <summary>
	/// Tests all networks in range of each interface.
	/// </summary>
	/// <remarks>This method never throws an exception.</remarks>
	private async Task ProbeAllAsync()
	{
		try
		{
			var interfaceIds = GetProfiles()
				.Where(x => x.IsRadioOn && (x.Signal > 0))
				.Select(x => x.InterfaceId)
				.Distinct()
				.ToArray();
			if (interfaceIds.Length == 0)
				return;

			await ProbeAsync(interfaceIds, null, SpeedTrigger.TestAll).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to test all networks." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Connects to networks one after another, measures speed of each and then restores
	/// the originally connected network.
	/// </summary>
	/// <param name="interfaceIds">Interface IDs, which are handled sequentially</param>
	/// <param name="targets">Profiles to be tested (all profiles in range if null)</param>
	/// <param name="trigger">Trigger recorded with the measurements</param>
	/// <remarks>
	/// While this method is running, no automatic connection or measurement is made by other
	/// functions. This method never throws an exception.
	/// </remarks>
	private async Task ProbeAsync(IReadOnlyList<Guid> interfaceIds, IReadOnlyList<ProfileItem> targets, SpeedTrigger trigger)
	{
		if (_disposed || _isClosing)
			return;

		// Only one test runs at a time.
		if (Interlocked.CompareExchange(ref _probeCount, 1, 0) != 0)
			return;

		CancellationTokenSource cancellation = null;
		try
		{
			// Closing this app may have started in the meantime.
			if (_isClosing)
				return;

			cancellation = new CancellationTokenSource();

			lock (_probeLock)
			{
				_isProbeInterrupted = false;
				_probeCancellation = cancellation;
			}
			var token = cancellation.Token;

			// From this point, automatic connection by Engage is suppressed.
			_isProbing = true;
			IsProbing.TurnOn();

			// The test requested by the user takes precedence over a running measurement.
			if (trigger is SpeedTrigger.TestAll)
				CancelMeasure();

			// An automatic connection or a measurement started just before is waited for.
			if (!await WaitForQuietAsync(token).ConfigureAwait(false))
				return;

			foreach (var interfaceId in interfaceIds)
			{
				if (token.IsCancellationRequested || _disposed)
					break;

				await ProbeInterfaceAsync(interfaceId, targets, trigger, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to test networks." + Environment.NewLine
				+ ex);
		}
		finally
		{
			try
			{
				lock (_probeLock)
				{
					_probeCancellation = null;
				}
				cancellation?.Dispose();

				// The connection is regarded as new after the test.
				ResetDwell();
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to finish the test of networks." + Environment.NewLine
					+ ex);
			}

			_isProbing = false;
			IsProbing.TurnOff();
			Interlocked.Exchange(ref _probeCount, 0);

			try
			{
				if (!_disposed && !_isClosing)
					await LoadProfilesAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to load profiles after the test of networks." + Environment.NewLine
					+ ex);
			}
		}
	}

	/// <summary>
	/// Waits for update, connection/disconnection and measurement in progress to end.
	/// </summary>
	/// <returns>True if none of them is in progress</returns>
	private async Task<bool> WaitForQuietAsync(CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();

		while (IsWorking.Value || IsUpdating.Value || IsMeasuring.Value)
		{
			if (_disposed || (stopwatch.Elapsed >= _probeQuietTimeout))
				return false;

			await Task.Delay(_probePollInterval, cancellationToken).ConfigureAwait(false);
		}
		return !_disposed;
	}

	private async Task ProbeInterfaceAsync(Guid interfaceId, IReadOnlyList<ProfileItem> targets, SpeedTrigger trigger, CancellationToken cancellationToken)
	{
		var kind = (trigger is SpeedTrigger.TestAll) ? SwitchKind.TestAll : SwitchKind.Probe;

		ProfileItem original = null;
		var isChanged = false;
		try
		{
			// The connected profile to be restored and the profiles which require connection are
			// determined from the profiles and so the profiles must not be older than this point.
			if (!await LoadProfilesFreshAsync(_probeFreshTimeout).ConfigureAwait(false))
			{
				Trace.WriteLine("Failed to load profiles before the test of networks.");
				return;
			}

			if (cancellationToken.IsCancellationRequested || _disposed)
				return;

			var subProfiles = GetProfiles()
				.Where(x => x.InterfaceId == interfaceId)
				.ToArray();

			original = subProfiles.FirstOrDefault(x => x.IsConnected);

			targets = (targets ?? GetProbeTargets(subProfiles))
				.Where(x => x.InterfaceId == interfaceId)
				.ToArray();
			if (targets.Count == 0)
				return;

			// What was pending on the interface is no longer valid after the test.
			ClearPendingSwitch(interfaceId);

			AppendProbeEvent(interfaceId, kind, original?.Name, null,
				(targets.Count == 1) ? "Testing 1 network" : $"Testing {targets.Count} networks");

			foreach (var target in targets)
			{
				if (cancellationToken.IsCancellationRequested || _disposed)
					break;

				lock (_probeLock)
				{
					_lastProbeTimes[HistoryService.GetKey(target.Name, interfaceId)] = DateTime.UtcNow;
				}

				if (!target.IsConnected)
				{
					isChanged = true;

					// This connection is not manual and so Engage is not turned off.
					if (!await ConnectNetworkAsync(target, false).ConfigureAwait(false))
					{
						if (!cancellationToken.IsCancellationRequested)
							AppendProbeEvent(interfaceId, SwitchKind.Probe, original?.Name, target.Name, $"Could not connect to {target.Name}");

						continue;
					}

					if (!await LinkInspector.WaitForAddressAsync(interfaceId, _addressTimeout, cancellationToken).ConfigureAwait(false))
					{
						AppendProbeEvent(interfaceId, SwitchKind.Probe, original?.Name, target.Name, $"No address on {target.Name}");
						continue;
					}

					await Task.Delay(_probeSettleDelay, cancellationToken).ConfigureAwait(false);
				}

				// The measurement requires that the connection is reflected to the profiles.
				if (!await WaitForConnectedAsync(target, cancellationToken).ConfigureAwait(false))
				{
					AppendProbeEvent(interfaceId, SwitchKind.Probe, original?.Name, target.Name, $"Could not test {target.Name}");
					continue;
				}

				var (sample, _) = await MeasureProfileAsync(target, trigger, SpeedTestSize.Quick, false, _probeMeasureWait, isProbe: true).ConfigureAwait(false);
				if (sample is null)
					AppendProbeEvent(interfaceId, SwitchKind.Probe, original?.Name, target.Name, $"Could not test {target.Name}");
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to test networks of an interface." + Environment.NewLine
				+ ex);
		}
		finally
		{
			if (isChanged)
				await RestoreAsync(interfaceId, original, kind).ConfigureAwait(false);

			try
			{
				ClearPendingSwitch(interfaceId);
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Failed to clear pending switch after the test of networks." + Environment.NewLine
					+ ex);
			}
		}
	}

	/// <summary>
	/// Waits until a profile connected to is shown as connected in the profiles.
	/// </summary>
	private async Task<bool> WaitForConnectedAsync(ProfileItem target, CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();

		while (!target.IsConnected)
		{
			if (_disposed || (stopwatch.Elapsed >= _probeLoadTimeout))
				return false;

			// This load is skipped if an update is running.
			await LoadProfilesAsync().ConfigureAwait(false);

			if (target.IsConnected)
				break;

			await Task.Delay(_probePollInterval, cancellationToken).ConfigureAwait(false);
		}
		return true;
	}

	/// <summary>
	/// Connects again to the profile which was connected before the test of networks.
	/// </summary>
	/// <remarks>
	/// If the user connected or disconnected during the test, the choice of the user is left
	/// as it is. This method never throws an exception.
	/// </remarks>
	private async Task RestoreAsync(Guid interfaceId, ProfileItem original, SwitchKind kind)
	{
		try
		{
			if ((original is null) || _disposed || _isProbeInterrupted)
				return;

			// The connection is left as it is only when the original profile is confirmed to be
			// connected by a load performed after the test. A load skipped because of another
			// update would leave the profiles as they were before the test connected to others.
			var isLoaded = await LoadProfilesFreshAsync(_probeFreshTimeout).ConfigureAwait(false);

			if (_disposed || _isProbeInterrupted)
				return;

			if (isLoaded && original.IsConnected)
				return;

			var result = await ConnectNetworkAsync(original, false).ConfigureAwait(false);
			if (!result && !_disposed && !_isProbeInterrupted && !_isClosing)
			{
				await Task.Delay(_probeRetryDelay).ConfigureAwait(false);

				if (_disposed || _isProbeInterrupted)
					return;

				result = await ConnectNetworkAsync(original, false).ConfigureAwait(false);
			}

			if (!result && !_isProbeInterrupted)
				AppendProbeEvent(interfaceId, kind, null, original.Name, $"Could not return to {original.Name}");
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to restore the connection after the test of networks." + Environment.NewLine
				+ ex);
		}
	}

	private void AppendProbeEvent(Guid interfaceId, SwitchKind kind, string from, string to, string reason)
	{
		try
		{
			AppendSwitchEvent(new SwitchEvent
			{
				Time = DateTime.UtcNow,
				InterfaceId = interfaceId,
				Kind = kind,
				From = from ?? string.Empty,
				To = to ?? string.Empty,
				Reason = reason
			});
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record the test of networks." + Environment.NewLine
				+ ex);
		}
	}

	#region Idle

	/// <summary>
	/// Tests a network other than the connected one if this machine and the link are idle.
	/// </summary>
	/// <remarks>
	/// This is performed only when the user opted in and switching by speed is engaged because
	/// the connection drops for a while. This method never throws an exception.
	/// </remarks>
	private async Task TryIdleProbeAsync()
	{
		try
		{
			if (!Settings.Current.ProbesWhenIdle)
				return;

			if (!EngagesPriority.Value || !Settings.Current.EngagesSpeed)
				return;

			if (!CanIdleProbe())
				return;

			if (DateTime.UtcNow - GetLastIdleProbeTime() < TimeSpan.FromHours(Settings.Current.ProbeInterval))
				return;

			// This test is automatic and so is subject to the daily budget.
			if (History.GetBytesToday() >= Settings.Current.SpeedTestDailyBudget * 1_000_000L)
				return;

			var signalThreshold = Settings.Current.SpeedSignalThreshold + SwitchPolicy.EntryMargin;

			foreach (var subProfiles in GetProfiles().GroupBy(x => x.InterfaceId))
			{
				var interfaceId = subProfiles.Key;

				// An automatic switch made just before is yet to be checked.
				if (HasLanding(interfaceId))
					continue;

				var candidate = SelectIdleCandidate(subProfiles, signalThreshold, GetLastProbeTime, DateTime.UtcNow);
				if (candidate is null)
					continue;

				if (await LinkInspector.GetRateMbpsAsync(interfaceId, _idleRateWindow).ConfigureAwait(false) >= IdleRate)
					continue;

				// The conditions may have changed in the meantime.
				if (!CanIdleProbe() || candidate.IsConnected)
					return;

				lock (_probeLock)
				{
					_lastIdleProbeTime = DateTime.UtcNow;
				}

				await ProbeAsync(new[] { interfaceId }, new[] { candidate }, SpeedTrigger.IdleProbe).ConfigureAwait(false);
				return;
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to test a network when idle." + Environment.NewLine
				+ ex);
		}
	}

	private bool CanIdleProbe() =>
		!_disposed
		&& !_isProbing
		&& !IsMeasuring.Value
		&& !IsWorking.Value
		&& !IsUpdating.Value
		&& !IsResumed
		&& (IdleDetector.GetIdleTime() >= _idleTime);

	/// <summary>
	/// Gets the last time (UTC) when a profile was measured or tested.
	/// </summary>
	private DateTime GetLastProbeTime(ProfileItem profile)
	{
		var key = HistoryService.GetKey(profile.Name, profile.InterfaceId);

		var lastTime = History.GetSummary(key)?.LastTime ?? default;

		lock (_probeLock)
		{
			return (_lastProbeTimes.TryGetValue(key, out DateTime time) && (time > lastTime))
				? time
				: lastTime;
		}
	}

	/// <summary>
	/// Gets the last time (UTC) when a network was tested while idle.
	/// </summary>
	/// <remarks>
	/// The time is seeded from history so that the test is not repeated at every start of
	/// this app.
	/// </remarks>
	private DateTime GetLastIdleProbeTime()
	{
		lock (_probeLock)
		{
			if (_lastIdleProbeTime.HasValue)
				return _lastIdleProbeTime.Value;
		}

		var lastTime = GetProfiles()
			.Select(x => HistoryService.GetKey(x.Name, x.InterfaceId))
			.Distinct()
			.SelectMany(x => History.GetSpeedSamples(x))
			.Where(x => x.Trigger is SpeedTrigger.IdleProbe)
			.Select(x => x.Time)
			.DefaultIfEmpty()
			.Max();

		lock (_probeLock)
		{
			_lastIdleProbeTime ??= lastTime;
			return _lastIdleProbeTime.Value;
		}
	}

	#endregion

	#endregion
}
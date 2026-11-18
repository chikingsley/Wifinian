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

	/// <summary>
	/// Time given, when this app is closed, to the test to end by itself or at least to ask for
	/// the originally connected network
	/// </summary>
	/// <remarks>
	/// After this time the closing thread asks for that network by itself. Closing must not take
	/// long: Windows may end a process which does not answer within about 5 seconds at sign-out.
	/// This time and the grace after it are 4 seconds together so as to stay below that.
	/// </remarks>
	private static readonly TimeSpan _probeCloseTimeout = TimeSpan.FromSeconds(2);

	/// <summary>
	/// Time given, when this app is closed, to the connection asked for to complete so that
	/// the outcome can be recorded
	/// </summary>
	/// <remarks>
	/// The request itself is carried out by Windows and so goes on after this app has ended.
	/// </remarks>
	private static readonly TimeSpan _probeCloseGrace = TimeSpan.FromSeconds(2);

	private static readonly TimeSpan _probeCloseLoadTimeout = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan _probeMeasureWait = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan _probeRetryDelay = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan _probePollInterval = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// Time given to a network being tested to associate and to get an address
	/// </summary>
	/// <remarks>
	/// A weak extender was seen to take 16 seconds to associate and then needed an address.
	/// </remarks>
	private static readonly TimeSpan _probeLinkTimeout = TimeSpan.FromSeconds(40);

	/// <summary>
	/// Time after which a connection is given up when the interface is doing nothing for it
	/// </summary>
	private static readonly TimeSpan _probeIdleTimeout = TimeSpan.FromSeconds(6);

	private static readonly TimeSpan _probeLinkInterval = TimeSpan.FromSeconds(1);

	private static readonly TimeSpan _idleTime = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan _idleRateWindow = TimeSpan.FromSeconds(2);
	private const double IdleRate = 0.1; // Mbps

	/// <summary>
	/// Period after which a measurement of a network is regarded as old enough to test it when idle
	/// </summary>
	/// <remarks>
	/// This is shorter than the period within which the newest measurement of a network limits
	/// its score for switching (<see cref="SwitchPolicy.RecentPeriod"/>, 24 hours) so that the next
	/// test can come before that measurement leaves the period. A longer period, three days
	/// originally, left the other networks judged by measurements days old.
	/// </remarks>
	internal static readonly TimeSpan ProbeStalePeriod = TimeSpan.FromHours(18);

	/// <summary>
	/// Whether networks are being connected to one after another for measurement
	/// </summary>
	public BooleanNotifier IsProbing { get; } = new();

	/// <summary>
	/// Command to test all networks in range or, while the test is running, to cancel it
	/// </summary>
	public ReactiveCommand ProbeCommand { get; private set; }

	/// <summary>
	/// How a test of networks of an interface ended
	/// </summary>
	internal enum ProbeEnd
	{
		Completed = 0,

		/// <summary>
		/// Stopped by the command (the measurement in progress was finished)
		/// </summary>
		Canceled,

		/// <summary>
		/// The user connected or disconnected during the test
		/// </summary>
		Interrupted,

		/// <summary>
		/// This app was closed during the test
		/// </summary>
		Closing
	}

	/// <summary>
	/// Where the interface was left after a test of networks
	/// </summary>
	internal enum ProbeReturn
	{
		/// <summary>
		/// The test never left the originally connected network
		/// </summary>
		Unchanged = 0,

		Restored,
		Failed,

		/// <summary>
		/// The user took over the connection and so it is left as it is
		/// </summary>
		LeftToUser,

		NotConnectedBefore,

		/// <summary>
		/// The connection was asked for on closing and its completion was not waited for
		/// </summary>
		RequestedOnClosing
	}

	/// <summary>
	/// What is to be restored by the test of networks in progress
	/// </summary>
	private sealed class ProbeRestore
	{
		public Guid InterfaceId { get; set; }
		public ProfileItem Original { get; set; }
		public SwitchKind Kind { get; set; }
	}

	private ProbeRestore _probeRestore;
	private int _restoreIssued; // 1 once the connection to the original network has been asked for
	private int _closeRecorded; // 1 once the end of the test has been recorded on closing

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
				{
					// The same command stops the running test, which is not obvious when
					// the test was started by this app itself while idle.
					RecordProbeOperation("The command to test all networks stopped the test of networks which was running.");
					CancelProbe(false);
				}
				else
				{
					Task.Run(() => ProbeAllAsync());
				}
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
	/// Ends the running test of networks when this app is closed and makes sure, within a limited
	/// time, that the originally connected network has been asked for.
	/// </summary>
	/// <remarks>
	/// Without this, closing this app (the menu, sign-out, shutdown) during a test of all networks
	/// or a test when idle would leave the machine on the network being tested. The test is
	/// canceled and normally asks for the original network by itself within a second or two.
	/// If it has not done so in time (it can be in the middle of a connection, which is waited
	/// for up to 10 seconds), this thread asks for that network. A connection asked for is
	/// carried out by Windows even after this app has ended. The test runs on thread pool
	/// threads and so the calling thread can wait. This method never throws an exception.
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

			bool IsEnded() => Volatile.Read(ref _probeCount) == 0;

			SpinWait.SpinUntil(() => IsEnded() || (Volatile.Read(ref _restoreIssued) != 0), _probeCloseTimeout);
			if (IsEnded())
				return;

			var pending = Volatile.Read(ref _probeRestore);

			// The test has neither ended nor asked for the original network yet.
			Task<bool> connectTask = null;
			if ((pending?.Original is not null)
				&& !_isProbeInterrupted
				&& (Interlocked.CompareExchange(ref _restoreIssued, 1, 0) == 0))
			{
				var original = pending.Original;
				connectTask = Task.Run(() => ConnectNetworkAsync(original, false));
			}

			// The connection asked for, by the test or by this thread, is given a moment to complete.
			if (connectTask is not null)
			{
				try
				{
					connectTask.Wait(_probeCloseGrace);
				}
				catch (Exception ex)
				{
					Trace.WriteLine("Failed to return to the original network on closing." + Environment.NewLine
						+ ex);
				}

				// The exception of a task left behind must be observed.
				_ = connectTask.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
			}
			else
			{
				SpinWait.SpinUntil(IsEnded, _probeCloseGrace);
			}

			if (IsEnded())
				return;

			// The test could not record its end in time and so it is recorded here.
			if ((pending is not null) && (Interlocked.CompareExchange(ref _closeRecorded, 1, 0) == 0))
			{
				var isIssued = (Volatile.Read(ref _restoreIssued) != 0);
				var isConnected = (connectTask is { Status: TaskStatus.RanToCompletion, Result: true });

				var reason = (pending.Original is null)
					? "Test ended by closing this app, no network was connected before"
					: isConnected
						? $"Test ended by closing this app, back on {pending.Original.Name}"
						: isIssued
							? $"Test ended by closing this app, returning to {pending.Original.Name} (asked for, not confirmed)"
							: $"Test ended by closing this app, the connection was changed meanwhile and is left as it is";

				Logger.RecordOperation(reason + ".");
				AppendProbeEvent(pending.InterfaceId, pending.Kind, null, pending.Original?.Name, reason);
			}

			Trace.WriteLine("The test of networks did not end in time on closing.");
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to end the test of networks on closing." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Gets where the interface was left by a test of networks which never connected to another
	/// network than the original one.
	/// </summary>
	/// <param name="hasOriginal">Whether a network was connected before the test</param>
	/// <param name="isInterrupted">Whether the user connected or disconnected during the test</param>
	/// <remarks>
	/// The test did not change the connection but the user may have: the interface is then no
	/// longer on the original network and so is not said to be.
	/// </remarks>
	internal static ProbeReturn GetUnchangedReturn(bool hasOriginal, bool isInterrupted)
	{
		if (!hasOriginal)
			return ProbeReturn.NotConnectedBefore;

		return isInterrupted ? ProbeReturn.LeftToUser : ProbeReturn.Unchanged;
	}

	/// <summary>
	/// Gets the sentence which tells how a test of networks of an interface ended.
	/// </summary>
	/// <param name="total">Number of networks to be tested</param>
	/// <param name="tested">Names of the networks which gave a finding (speed or no internet access)</param>
	/// <param name="notTested">Names of the networks attempted which gave none (not connected, no address, failed measurement)</param>
	/// <param name="original">Name of the network connected before the test (null if none)</param>
	/// <param name="end">How the test ended</param>
	/// <param name="result">Where the interface was left</param>
	internal static string GetProbeSummary(int total, IReadOnlyList<string> tested, IReadOnlyList<string> notTested, string original, ProbeEnd end, ProbeReturn result)
	{
		var count = tested?.Count ?? 0;
		var networks = (total == 1) ? "1 network" : $"{total} networks";

		var head = end switch
		{
			ProbeEnd.Canceled => $"Test stopped after {count} of {networks}",
			ProbeEnd.Interrupted => $"Test interrupted after {count} of {networks}",
			ProbeEnd.Closing => $"Test ended by closing this app after {count} of {networks}",
			_ => (count == total) ? $"Tested {networks}" : $"Tested {count} of {networks}"
		};

		if (notTested is { Count: > 0 })
			head += $" (could not test {string.Join(", ", notTested)})";

		var tail = result switch
		{
			ProbeReturn.Restored => $"back on {original}",
			ProbeReturn.Failed => $"could not return to {original}",
			ProbeReturn.LeftToUser => "the connection was changed meanwhile and is left as it is",
			ProbeReturn.NotConnectedBefore => "no network was connected before",
			ProbeReturn.RequestedOnClosing => $"returning to {original} (asked for, not confirmed)",
			_ => (original is not null) ? $"still on {original}" : "no network was connected before"
		};

		return $"{head}, {tail}";
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
			{
				RecordIgnored(GetProbeName(SpeedTrigger.TestAll), (GetProfiles().Length == 0)
					? "the profiles have not been loaded yet"
					: "no network is in range (no profile has signal or the radio is off)");
				return;
			}

			await ProbeAsync(interfaceIds, null, SpeedTrigger.TestAll).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to test all networks." + Environment.NewLine
				+ ex);
			RecordIgnored(GetProbeName(SpeedTrigger.TestAll), $"it failed ({ex.Message})");
		}
	}

	/// <summary>
	/// Gets the name of a test of networks for a sentence.
	/// </summary>
	internal static string GetProbeName(SpeedTrigger trigger) =>
		(trigger is SpeedTrigger.TestAll) ? "Test of all networks" : "Test of a network when idle";

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
		var name = GetProbeName(trigger);

		if (_disposed || _isClosing)
		{
			RecordIgnored(name, "this app is being closed");
			return;
		}

		// Only one test runs at a time.
		if (Interlocked.CompareExchange(ref _probeCount, 1, 0) != 0)
		{
			RecordIgnored(name, "another test of networks is running");
			return;
		}

		CancellationTokenSource cancellation = null;
		try
		{
			// Closing this app may have started in the meantime.
			if (_isClosing)
			{
				RecordIgnored(name, "this app is being closed");
				return;
			}

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
			{
				RecordIgnored(name, _disposed
					? "this app is being closed"
					: GetBusyText(IsUpdating.Value, IsWorking.Value, IsMeasuring.Value, _probeQuietTimeout));
				return;
			}

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
			RecordIgnored(name, $"it failed ({ex.Message})");
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
	/// Gets the reason why a test of networks gave up waiting for other work to end.
	/// </summary>
	internal static string GetBusyText(bool isUpdating, bool isWorking, bool isMeasuring, TimeSpan timeout)
	{
		var works = new[]
		{
			isUpdating ? "a scan or a load of profiles" : null,
			isWorking ? "a connection or a change of a profile" : null,
			isMeasuring ? "a speed test" : null
		}.Where(x => x is not null).ToArray();

		return $"{((works.Length > 0) ? string.Join(" and ", works) : "other work")} was still running after {timeout.TotalSeconds:0} s";
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
		var isStarted = false;
		var total = 0;
		var tested = new List<string>();
		var notTested = new List<string>();
		try
		{
			// The connected profile to be restored and the profiles which require connection are
			// determined from the profiles and so the profiles must not be older than this point.
			if (!await LoadProfilesFreshAsync(_probeFreshTimeout).ConfigureAwait(false))
			{
				Trace.WriteLine("Failed to load profiles before the test of networks.");
				RecordIgnored(GetProbeName(trigger), $"the profiles could not be loaded within {_probeFreshTimeout.TotalSeconds:0} s because another update was running");
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
			{
				RecordIgnored(GetProbeName(trigger), "no network to test is in range");
				return;
			}

			// What was pending on the interface is no longer valid after the test.
			ClearPendingSwitch(interfaceId);

			total = targets.Count;
			isStarted = true;

			// From this point, closing this app knows what to return to.
			Interlocked.Exchange(ref _restoreIssued, 0);
			Interlocked.Exchange(ref _closeRecorded, 0);
			Volatile.Write(ref _probeRestore, new ProbeRestore { InterfaceId = interfaceId, Original = original, Kind = kind });

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

				// Whether the network requires connection is asked of the system: the profiles
				// may not show yet the connection made for the previous network.
				if (!IsOnProfile(target))
				{
					isChanged = true;

					var stopwatch = Stopwatch.StartNew();

					// This connection is not manual and so Engage is not turned off. Its result is
					// not final: a weak network may associate after the connection gave up waiting.
					await ConnectNetworkAsync(target, false).ConfigureAwait(false);

					var failure = await WaitForLinkAsync(target, _probeLinkTimeout - stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
					if (failure is not null)
					{
						notTested.Add(target.Name);
						RecordNotTested(target, trigger, original, failure, stopwatch.Elapsed);
						continue;
					}

					await Task.Delay(_probeSettleDelay, cancellationToken).ConfigureAwait(false);
				}

				var measureStopwatch = Stopwatch.StartNew();

				// The measurement requires that the connection is reflected to the profiles.
				if (!await WaitForConnectedAsync(target, cancellationToken).ConfigureAwait(false))
				{
					notTested.Add(target.Name);
					RecordNotTested(target, trigger, original, $"Could not test {target.Name}", measureStopwatch.Elapsed);
					continue;
				}

				var (sample, _) = await MeasureProfileAsync(target, trigger, SpeedTestSize.Quick, false, _probeMeasureWait, isProbe: true).ConfigureAwait(false);
				if (sample is null)
				{
					notTested.Add(target.Name);
					RecordNotTested(target, trigger, original, $"Could not test {target.Name}", measureStopwatch.Elapsed);
				}
				else if (sample.Valid)
				{
					tested.Add(target.Name);
				}
				else if (sample.Result is not SpeedResult.Aborted)
				{
					// The measurement ran but found nothing about the network (the test server
					// failed, for instance). Its row tells why.
					notTested.Add(target.Name);
				}
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
			// Closing this app also interrupts the test but is not the user taking over the connection.
			var result = isChanged
				? await RestoreAsync(original).ConfigureAwait(false)
				: GetUnchangedReturn(original is not null, _isProbeInterrupted && !_isClosing);

			Volatile.Write(ref _probeRestore, null);

			// The end of the test is recorded as its start is. On closing, it may have been
			// recorded already by the closing thread, which could not wait any longer.
			if (isStarted && !(_isClosing && (Interlocked.CompareExchange(ref _closeRecorded, 1, 0) != 0)))
			{
				var end = _isClosing
					? ProbeEnd.Closing
					: _isProbeInterrupted
						? ProbeEnd.Interrupted
						: cancellationToken.IsCancellationRequested ? ProbeEnd.Canceled : ProbeEnd.Completed;

				var summary = GetProbeSummary(total, tested, notTested, original?.Name, end, result);

				AppendProbeEvent(interfaceId, kind, null, (result is ProbeReturn.Restored or ProbeReturn.RequestedOnClosing) ? original?.Name : null, summary);

				if (end is ProbeEnd.Closing)
					RecordProbeOperation(summary + ".");
			}

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
	/// Waits until the interface is connected to a profile being tested and has an address.
	/// </summary>
	/// <param name="target">Profile which has been requested to connect to</param>
	/// <param name="timeout">Time to wait</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>Null if the link is ready, sentence which tells why it is not otherwise</returns>
	/// <remarks>
	/// Association and getting an address are waited for as a whole because either of them can
	/// take long on a weak network. The wait ends early when the interface is not connected
	/// and is doing nothing to connect.
	/// </remarks>
	private async Task<string> WaitForLinkAsync(ProfileItem target, TimeSpan timeout, CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();
		var idleStopwatch = new Stopwatch();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (_disposed)
				return $"Could not connect to {target.Name}";

			// This load is skipped if an update is running and so the connection itself is
			// asked of the system.
			await LoadProfilesAsync().ConfigureAwait(false);

			var isConnected = IsOnProfile(target);
			if (isConnected)
			{
				idleStopwatch.Reset();

				if (LinkInspector.IsReady(target.InterfaceId))
					return null;
			}
			else if (target.IsInterfaceBusy)
			{
				idleStopwatch.Reset();
			}
			else
			{
				idleStopwatch.Start();

				if (idleStopwatch.Elapsed >= _probeIdleTimeout)
					return $"Could not connect to {target.Name}";
			}

			if (stopwatch.Elapsed >= timeout)
			{
				return isConnected
					? $"No address on {target.Name} within {_probeLinkTimeout.TotalSeconds:0} s"
					: $"Could not connect to {target.Name} within {_probeLinkTimeout.TotalSeconds:0} s";
			}

			await Task.Delay(_probeLinkInterval, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Records that a network could not be tested, as a decision and as a speed row which is
	/// not a valid sample, so that the History window shows it along with the measurements.
	/// </summary>
	/// <remarks>This method never throws an exception.</remarks>
	private void RecordNotTested(ProfileItem target, SpeedTrigger trigger, ProfileItem original, string reason, TimeSpan duration)
	{
		try
		{
			AppendProbeEvent(target.InterfaceId, SwitchKind.Probe, original?.Name, target.Name, reason);

			History.AppendSpeed(new SpeedSample
			{
				Time = DateTime.UtcNow,
				InterfaceId = target.InterfaceId,
				Profile = target.Name,
				Bssid = target.Bssid ?? string.Empty,
				Band = target.Band,
				Channel = target.Channel,
				Signal = target.Signal,
				DurationMs = (int)Math.Min(int.MaxValue, duration.TotalMilliseconds),
				Trigger = trigger,
				Result = SpeedResult.NotTested
			});
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record a network which could not be tested." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Waits until the interface is connected by a profile connected to, as the system tells.
	/// </summary>
	/// <remarks>
	/// The profiles are loaded as well but are not what is waited for: a load is skipped if
	/// an update is running and the measurement identifies the network by itself.
	/// </remarks>
	private async Task<bool> WaitForConnectedAsync(ProfileItem target, CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();

		while (!IsOnProfile(target))
		{
			if (_disposed || (stopwatch.Elapsed >= _probeLoadTimeout))
				return false;

			// This load is skipped if an update is running.
			await LoadProfilesAsync().ConfigureAwait(false);

			if (IsOnProfile(target))
				break;

			await Task.Delay(_probePollInterval, cancellationToken).ConfigureAwait(false);
		}
		return true;
	}

	/// <summary>
	/// Connects again to the profile which was connected before the test of networks.
	/// </summary>
	/// <returns>Where the interface was left</returns>
	/// <remarks>
	/// If the user connected or disconnected during the test, the choice of the user is left
	/// as it is. While this app is being closed, the connection is asked for once and without
	/// delay because the closing thread waits only for a few seconds. This method never throws
	/// an exception.
	/// </remarks>
	private async Task<ProbeReturn> RestoreAsync(ProfileItem original)
	{
		try
		{
			if (original is null)
				return ProbeReturn.NotConnectedBefore;

			if (_isProbeInterrupted)
				return ProbeReturn.LeftToUser;

			if (_disposed)
				return ProbeReturn.Failed;

			// The connection is left as it is only when the original profile is confirmed to be
			// connected by a load performed after the test. A load skipped because of another
			// update would leave the profiles as they were before the test connected to others.
			var isLoaded = await LoadProfilesFreshAsync(_isClosing ? _probeCloseLoadTimeout : _probeFreshTimeout).ConfigureAwait(false);

			if (_isProbeInterrupted)
				return ProbeReturn.LeftToUser;

			if (isLoaded && IsOnProfile(original))
				return ProbeReturn.Restored;

			// The closing thread may have asked for the connection in the meantime.
			if (Interlocked.CompareExchange(ref _restoreIssued, 1, 0) != 0)
				return ProbeReturn.RequestedOnClosing;

			if (_disposed)
				return ProbeReturn.Failed;

			var result = await ConnectNetworkAsync(original, false).ConfigureAwait(false);
			if (!result && !_disposed && !_isProbeInterrupted && !_isClosing)
			{
				await Task.Delay(_probeRetryDelay).ConfigureAwait(false);

				if (_isProbeInterrupted)
					return ProbeReturn.LeftToUser;

				if (!_disposed)
					result = await ConnectNetworkAsync(original, false).ConfigureAwait(false);
			}

			if (result)
				return ProbeReturn.Restored;

			// On closing, the connection may only not have completed within the time waited.
			if (_isClosing)
				return ProbeReturn.RequestedOnClosing;

			return _isProbeInterrupted ? ProbeReturn.LeftToUser : ProbeReturn.Failed;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to restore the connection after the test of networks." + Environment.NewLine
				+ ex);
			return ProbeReturn.Failed;
		}
	}

	private static void RecordProbeOperation(string content)
	{
		try
		{
			Logger.RecordOperation(content);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record the end of the test of networks." + Environment.NewLine
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

			// While the user operates this machine, the test is not due and nothing is noted.
			if (IdleDetector.GetIdleTime() < _idleTime)
			{
				ClearIdleNotes();
				return;
			}

			if (!EngagesPriority.Value || !Settings.Current.EngagesSpeed)
			{
				NoteIdle("off", "it runs only while switching by speed is turned on");
				return;
			}

			// Other work in progress ends shortly and so is not noted.
			if (!CanIdleProbe())
				return;

			var interval = TimeSpan.FromHours(Settings.Current.ProbeInterval);
			var elapsed = DateTime.UtcNow - GetLastIdleProbeTime();
			if (elapsed < interval)
			{
				NoteIdle("interval", $"the last one was {elapsed.TotalMinutes:0} min ago and they are at least {interval.TotalHours:0} h apart");
				return;
			}

			// This test is automatic and so is subject to the daily limit if the user set one.
			if (IsDailyLimitReached())
				return;

			var signalThreshold = Settings.Current.SpeedSignalThreshold + SwitchPolicy.EntryMargin;
			var hasCandidate = false;
			var isConnected = false;
			var isPending = false;

			foreach (var subProfiles in GetProfiles().GroupBy(x => x.InterfaceId))
			{
				var interfaceId = subProfiles.Key;

				// An automatic switch made just before is yet to be checked.
				if (HasLanding(interfaceId))
				{
					isPending = true;
					continue;
				}

				isConnected |= subProfiles.Any(x => x.IsConnected);

				var candidate = SelectIdleCandidate(subProfiles, signalThreshold, GetLastProbeTime, DateTime.UtcNow);
				if (candidate is null)
					continue;

				hasCandidate = true;

				var rate = await LinkInspector.GetRateMbpsAsync(interfaceId, _idleRateWindow).ConfigureAwait(false);
				if (rate >= IdleRate)
				{
					NoteIdle("rate", $"the connection is in use ({rate:0.0} Mbps, it must be below {IdleRate:0.0} Mbps)");
					continue;
				}

				// The conditions may have changed in the meantime.
				if (!CanIdleProbe() || candidate.IsConnected)
					return;

				lock (_probeLock)
				{
					_lastIdleProbeTime = DateTime.UtcNow;
				}

				ClearIdleNotes();

				await ProbeAsync(new[] { interfaceId }, new[] { candidate }, SpeedTrigger.IdleProbe).ConfigureAwait(false);
				return;
			}

			if (!hasCandidate && !isPending && !isConnected)
			{
				NoteIdle("offline", "no network is connected (or the profiles are not loaded yet)");
			}
			else if (!hasCandidate && !isPending)
			{
				NoteIdle("none", $"no other network needs it (the connected one has Auto switch off, or each other one is measured within {ProbeStalePeriod.TotalHours:0} h, out of range, too weak, set aside or has Auto switch off)");
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to test a network when idle." + Environment.NewLine
				+ ex);
		}
	}

	private readonly HashSet<string> _idleNotes = new(); // Reasons noted in this idle time

	private void ClearIdleNotes()
	{
		lock (_idleNotes)
		{
			_idleNotes.Clear();
		}
	}

	/// <summary>
	/// Records, once per reason and per time this machine is idle, why the test when idle is
	/// not run although the user is away.
	/// </summary>
	private void NoteIdle(string code, string why)
	{
		lock (_idleNotes)
		{
			if (!_idleNotes.Add(code))
				return;
		}

		RecordIgnored(GetProbeName(SpeedTrigger.IdleProbe), why);
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
using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

using Wifinian.Models;
using Wifinian.Models.Update;

namespace Wifinian;

internal partial class AppController
{
	#region Upgrade

	// "Upgrade" stands for an update of this application itself here because "update" is taken
	// by the update of profiles in this class.

	private static readonly TimeSpan _upgradeCheckDelay = TimeSpan.FromMinutes(2);
	private static readonly TimeSpan _upgradeCheckTick = TimeSpan.FromMinutes(10);

	/// <summary>
	/// Interval of automatic checks for a new version
	/// </summary>
	internal static readonly TimeSpan UpgradeCheckInterval = TimeSpan.FromHours(24);

	/// <summary>
	/// Interval of automatic checks after one which failed (e.g. no internet access at that time)
	/// </summary>
	internal static readonly TimeSpan UpgradeRetryInterval = TimeSpan.FromHours(1);

	private static readonly TimeSpan _upgradeFailureRecordInterval = TimeSpan.FromHours(24);

	private readonly UpdateService _upgrader = new();
	private readonly CancellationTokenSource _upgradeCancellation = new();
	private UpdateManifest _upgradeManifest;
	private DateTime _upgradeCheckTime; // UTC
	private DateTime _upgradeFailureRecordTime; // UTC
	private bool _upgradeCheckFailed;
	private string _upgradeAnnounced;

	/// <summary>
	/// State of looking for and installing a new version, which is changed on the UI thread only
	/// </summary>
	public ReactivePropertySlim<UpdateStatus> UpgradeStatus { get; } = new(UpdateStatus.None);

	/// <summary>
	/// Command to look for a new version or, if one is known, to install it and restart
	/// </summary>
	public ReactiveCommand UpgradeCommand { get; private set; }

	private void InitiateUpgrade()
	{
		// The helper which ran the installer of the previous update leaves how it ended.
		var result = UpdateService.TakeInstallResult();
		if (result is not null)
		{
			Logger.RecordOperation(int.TryParse(result, out int exitCode)
				? $"The installer of an update ended with exit code {exitCode}{((exitCode is 0 or 3010) ? "" : " (not installed)")}. The running version is {ProductInfo.Version}."
				: $"The installer of an update was {result}. The running version is {ProductInfo.Version}.");
		}

		UpgradeCommand = new ReactiveProperty<bool>(true)
			.ToReactiveCommand();
		UpgradeCommand
			.Subscribe(async _ => await UpgradeManuallyAsync())
			.AddTo(this.Subscription);

		Observable.Timer(_upgradeCheckDelay, _upgradeCheckTick)
			.ObserveOnUIDispatcher()
			.Subscribe(async _ => await CheckUpgradeAutomaticallyAsync())
			.AddTo(this.Subscription);
	}

	private void CancelUpgrade()
	{
		try
		{
			_upgradeCancellation.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	/// <summary>
	/// Determines whether an automatic check for a new version is due.
	/// </summary>
	/// <param name="checksUpdates">Whether the setting allows automatic checks</param>
	/// <param name="isBusy">Whether a check or an installation is running</param>
	/// <param name="isTesting">Whether a test of networks or a speed test is running</param>
	/// <param name="lastCheckTime">Time of the last check (UTC), default if none</param>
	/// <param name="lastCheckFailed">Whether the last automatic check failed</param>
	/// <param name="utcNow">Current time (UTC)</param>
	internal static bool IsUpgradeCheckDue(bool checksUpdates, bool isBusy, bool isTesting, DateTime lastCheckTime, bool lastCheckFailed, DateTime utcNow)
	{
		// A test of networks drops the connection and a speed test must not share the link.
		if (!checksUpdates || isBusy || isTesting)
			return false;

		if (lastCheckTime == default)
			return true;

		return (utcNow - lastCheckTime) >= (lastCheckFailed ? UpgradeRetryInterval : UpgradeCheckInterval);
	}

	/// <summary>
	/// Looks for a new version when it is due. This only tells in the menu and in the operation
	/// log that a new version is available. It never installs anything.
	/// </summary>
	private async Task CheckUpgradeAutomaticallyAsync()
	{
		try
		{
			if (_disposed)
				return;

			if (!IsUpgradeCheckDue(Settings.Current.ChecksUpdates, UpgradeStatus.Value.IsBusy, _isProbing || IsMeasuring.Value, _upgradeCheckTime, _upgradeCheckFailed, DateTime.UtcNow))
				return;

			var previous = UpgradeStatus.Value;
			UpgradeStatus.Value = UpdateStatus.Checking;
			_upgradeCheckTime = DateTime.UtcNow;

			try
			{
				var (manifest, isNewer) = await _upgrader.CheckAsync(_upgradeCancellation.Token);
				_upgradeCheckFailed = false;
				AcceptUpgradeCheck(manifest, isNewer);
			}
			catch (UpdateException ex)
			{
				_upgradeCheckFailed = true;

				// A failure of an automatic check is not shown and is recorded once a day at most.
				UpgradeStatus.Value = (previous.Phase is UpdatePhase.Failed) ? UpdateStatus.None : previous;

				if (!_disposed && (DateTime.UtcNow - _upgradeFailureRecordTime >= _upgradeFailureRecordInterval))
				{
					_upgradeFailureRecordTime = DateTime.UtcNow;
					Logger.RecordOperation($"The automatic check for a new version failed: {ex.Message}{GetUpgradeDetail(ex)}. It is tried again in {UpgradeRetryInterval.TotalMinutes:f0} minutes; further failures are not recorded for a day.");
				}
			}
		}
		catch (Exception ex)
		{
			Logger.RecordException(ex, "Upgrade check");
		}
	}

	/// <summary>
	/// Looks for a new version or, if one is known, installs it and restarts, as chosen in the menu.
	/// </summary>
	private async Task UpgradeManuallyAsync()
	{
		try
		{
			if (_disposed || UpgradeStatus.Value.IsBusy)
				return;

			if (_isProbing || IsMeasuring.Value)
			{
				Logger.RecordOperation("A check for a new version or its installation was chosen in the menu but not run because a test of networks or a speed test is running.");

				// A known new version stays offered.
				if (UpgradeStatus.Value.Phase is not UpdatePhase.Available)
					UpgradeStatus.Value = UpdateStatus.Failed("network test running");

				return;
			}

			var installs = (UpgradeStatus.Value.Phase is UpdatePhase.Available) && (_upgradeManifest is not null);

			UpgradeStatus.Value = installs
				? UpdateStatus.Downloading(_upgradeManifest.Version)
				: UpdateStatus.Checking;
			_upgradeCheckTime = DateTime.UtcNow;

			try
			{
				// The manifest is asked for again before installation as well because a version
				// newer than the known one may have been released since.
				var (manifest, isNewer) = await _upgrader.CheckAsync(_upgradeCancellation.Token);
				_upgradeCheckFailed = false;
				AcceptUpgradeCheck(manifest, isNewer);

				if (!installs || !isNewer)
					return;

				UpgradeStatus.Value = UpdateStatus.Downloading(manifest.Version);

				var installerPath = await _upgrader.DownloadAsync(manifest, _upgradeCancellation.Token);
				if (_disposed)
					return;

				Logger.RecordOperation($"Update {manifest.Version} was downloaded to {installerPath} and verified (SHA-256 {manifest.Sha256.ToLowerInvariant()}, signature valid). "
					+ "The installer is run after this app has closed and then the app is started again."
					+ (UpdateService.IsRunningInstalled ? "" : " This app is not running from the installed folder: the update goes to the installed folder, the app is started from there afterward and this copy stays as it is."));

				_upgrader.StartInstaller(installerPath);

				// This is the way Close in the menu takes, and so a running test of networks
				// restores the connection and the shutdown is recorded with this reason.
				App.ExitReason ??= $"Closing to install update {manifest.Version}";
				_current.Shutdown();
			}
			catch (UpdateException ex)
			{
				if (_disposed)
					return;

				UpgradeStatus.Value = UpdateStatus.Failed(ex.Message);
				Logger.RecordOperation($"{(installs ? "The update was refused or failed" : "The check for a new version failed")}: {ex.Message}{GetUpgradeDetail(ex)}. Nothing was installed.");
			}
		}
		catch (Exception ex)
		{
			UpgradeStatus.Value = UpdateStatus.Failed("unexpected error");
			Logger.RecordException(ex, "Upgrade");
		}
	}

	private void AcceptUpgradeCheck(UpdateManifest manifest, bool isNewer)
	{
		if (!isNewer)
		{
			_upgradeManifest = null;
			UpgradeStatus.Value = UpdateStatus.Latest(ProductInfo.Version);
			return;
		}

		_upgradeManifest = manifest;
		UpgradeStatus.Value = UpdateStatus.Available(manifest.Version);

		if (_upgradeAnnounced != manifest.Version)
		{
			_upgradeAnnounced = manifest.Version;
			Logger.RecordOperation($"Update {manifest.Version} is available (running {UpdateVerifier.Normalize(ProductInfo.Version).ToString(3)}). It is installed only when chosen in the menu."
				+ (string.IsNullOrWhiteSpace(manifest.Notes) ? "" : $" Notes: {TrimUpgradeNotes(manifest.Notes)}"));
		}
	}

	private static string GetUpgradeDetail(Exception ex)
	{
		// The innermost exception tells the cause (e.g. the name could not be resolved).
		var inner = ex.InnerException;
		if (inner is null)
			return string.Empty;

		while (inner.InnerException is not null)
			inner = inner.InnerException;

		return $" ({inner.GetType().Name}: {inner.Message.Trim().TrimEnd('.')})";
	}

	private static string TrimUpgradeNotes(string notes)
	{
		const int maxLength = 300;

		notes = notes.Replace("\r", " ").Replace("\n", " ").Trim();
		return (notes.Length <= maxLength) ? notes : notes.Substring(0, maxLength) + "...";
	}

	#endregion
}

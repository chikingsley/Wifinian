using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using ManagedNativeWifi;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;
using Reactive.Bindings.Notifiers;

using ScreenFrame;
using StartupAgency;
using Wifinian.Common;
using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Wlan;
using Wifinian.Views;

namespace Wifinian;

internal partial class AppController : DisposableBase
{
	private readonly Application _current = Application.Current;

	private readonly AppKeeper _keeper;
	internal StartupAgent StartupAgent => _keeper.StartupAgent;

	public ObservableCollection<ProfileItem> Profiles { get; }
	private readonly object _profilesLock = new();

	public NotifyIconContainer NotifyIconContainer { get; }
	public WindowPainter WindowPainter { get; }

	private readonly IWlanWorker _worker;
	public bool IsWorkable => _worker.IsWorkable;

	internal HistoryService History { get; }

	public BooleanNotifier IsUpdating { get; }
	public BooleanNotifier IsWorking { get; }

	public ReactiveProperty<bool> RushesRescan { get; }
	public ReactiveProperty<bool> EngagesPriority { get; }

	private ReactiveTimer RescanTimer { get; }
	private ReactiveTimer SampleTimer { get; }

	public ReactiveCommand RescanCommand { get; }
	public ReactiveCommand CloseCommand { get; }

	public AppController(AppKeeper keeper) : this(keeper,
		//new MockWorker() ??
		//new NetshWorker() ??
		(IWlanWorker)new NativeWifiWorker())
	{ }

	public AppController(AppKeeper keeper, IWlanWorker worker)
	{
		this._keeper = keeper ?? throw new ArgumentNullException(nameof(keeper));

		Profiles = new ObservableCollection<ProfileItem>();
		BindingOperations.EnableCollectionSynchronization(Profiles, _profilesLock);

		NotifyIconContainer = new NotifyIconContainer();
		NotifyIconContainer.MouseLeftButtonClick += OnMainWindowShowRequested;
		NotifyIconContainer.MouseRightButtonClick += OnMenuWindowShowRequested;
		WindowPainter = new WindowPainter();

		this._worker = worker;
		History = new HistoryService(AppDataService.HistoryFolderPath);

		IsUpdating = new BooleanNotifier();
		IsWorking = new BooleanNotifier();

		ShowUpdatingTime(); // For debug
		ShowWorkingTime(); // For debug

		RushesRescan = new ReactiveProperty<bool>()
			.AddTo(this.Subscription);

		EngagesPriority = Settings.Current.ToReactivePropertyAsSynchronized(x => x.EngagesPriority)
			.AddTo(this.Subscription);
		EngagesPriority
			.Subscribe(_ => SetNotifyIconText())
			.AddTo(this.Subscription);
		Settings.Current
			.ObserveProperty(x => x.EngagesSpeed)
			.Subscribe(_ => SetNotifyIconText())
			.AddTo(this.Subscription);

		#region Update

		RescanTimer = new ReactiveTimer(TimeSpan.FromSeconds(Settings.Current.RescanInterval))
			.AddTo(this.Subscription);

		RescanCommand = IsUpdating
			.Inverse()
			.ObserveOnUIDispatcher() // This is for thread access by ReactiveCommand.
			.ToReactiveCommand();
		RescanCommand
			.Merge(EngagesPriority.Where(x => x).Select(x => x as object))
			.Merge(RescanTimer.Select(x => x as object))
			.Subscribe(async _ => await ScanNetworkAsync())
			.AddTo(this.Subscription);

		Settings.Current
			.ObserveProperty(x => x.RescanInterval)
			.Subscribe(rescanInterval => RescanTimer.Interval = TimeSpan.FromSeconds(rescanInterval))
			.AddTo(this.Subscription);

		RushesRescan
			.Subscribe(rushesRescan =>
			{
				if (rushesRescan)
					RescanTimer.Start();
				else
					RescanTimer.Stop();

				SetNotifyIconText();
			})
			.AddTo(this.Subscription);

		SampleTimer = new ReactiveTimer(TimeSpan.FromSeconds(Settings.Current.SampleInterval))
			.AddTo(this.Subscription);
		SampleTimer
			.Subscribe(async _ => await OnSampleTickAsync())
			.AddTo(this.Subscription);

		// The settings are loaded after this constructor and then these sequences emit the loaded
		// values so that the timer follows them.
		Observable.Merge(
			Settings.Current.ObserveProperty(x => x.SampleInterval).Select(_ => Unit.Default),
			Settings.Current.ObserveProperty(x => x.RecordsHistory).Select(_ => Unit.Default),
			Settings.Current.ObserveProperty(x => x.MeasuresSpeed).Select(_ => Unit.Default),
			Settings.Current.ObserveProperty(x => x.EngagesSpeed).Select(_ => Unit.Default),
			Settings.Current.ObserveProperty(x => x.EngagesPriority).Select(_ => Unit.Default))
			.Subscribe(_ => UpdateSampleTimer())
			.AddTo(this.Subscription);

		var networkRefreshed = Observable.FromEventPattern(
			h => _worker.NetworkRefreshed += h,
			h => _worker.NetworkRefreshed -= h);
		var availabilityChanged = Observable.FromEventPattern<AvailabilityChangedEventArgs>(
			h => _worker.AvailabilityChanged += h,
			h => _worker.AvailabilityChanged -= h);
		var interfaceChanged = Observable.FromEventPattern<InterfaceChangedEventArgs>(
			h => _worker.InterfaceChanged += h,
			h => _worker.InterfaceChanged -= h);
		var connectionChanged = Observable.FromEventPattern<ConnectionChangedEventArgs>(
			h => _worker.ConnectionChanged += h,
			h => _worker.ConnectionChanged -= h);
		var profileChanged = Observable.FromEventPattern<ProfileChangedEventArgs>(
			h => _worker.ProfileChanged += h,
			h => _worker.ProfileChanged -= h);
		Observable.Merge<object>(networkRefreshed, availabilityChanged, interfaceChanged, connectionChanged, profileChanged)
			.Throttle(TimeSpan.FromMilliseconds(100))
			.Subscribe(async _ =>
			{
				if (RushesRescan.Value)
					RescanTimer.Start(TimeSpan.FromSeconds(Settings.Current.RescanInterval)); // Wait for due time.

				await LoadProfilesAsync();
			})
			.AddTo(this.Subscription);

		Observable.FromEventPattern<SignalQualityChangedEventArgs>(
			h => _worker.SignalQualityChanged += h,
			h => _worker.SignalQualityChanged -= h)
			.Subscribe(x => UpdateSignalQuality(x.EventArgs))
			.AddTo(this.Subscription);

		#endregion

		#region Close

		CloseCommand = new ReactiveProperty<bool>(true)
			.ToReactiveCommand();
		CloseCommand
			.Subscribe(_ => _current.Shutdown())
			.AddTo(this.Subscription);

		#endregion

		InitiateMeasure();
	}

	public async Task InitiateAsync()
	{
		await LanguageService.InitializeAsync();

		// The lock makes loading the settings and applying them atomic to adding profiles
		// so that no profile is added with avoided state older than the settings.
		lock (_profilesLock)
		{
			Settings.Current.Initiate();

			// Profiles may have been loaded before the settings.
			ApplyAvoided(Profiles.ToArray());
		}

		_ = PurgeHistoryAsync();

		NotifyIconContainer.ShowIcon(WindowPainter.GetIconPath(), ProductInfo.Title);
		WindowPainter.ThemeChanged += (_, _) =>
		{
			NotifyIconContainer.ShowIcon(WindowPainter.GetIconPath());
		};

		Profiles
			.ObserveElementProperty(x => x.IsConnected)
			.Throttle(TimeSpan.FromMilliseconds(100))
			.Subscribe(_ => SetNotifyIconText())
			.AddTo(this.Subscription);

		await LoadProfilesAsync();

		_current.MainWindow = new MainWindow(this);

		if (!StartupAgent.IsStartedOnSignIn())
			_current.MainWindow.Show();

		StartupAgent.HandleRequestAsync = HandleRequestAsync;

		Observable.FromEventPattern(
			h => _current.MainWindow.Activated += h,
			h => _current.MainWindow.Activated -= h)
			.StartWith(new object()) // This is for initial scan.
			.Subscribe(async _ => await ScanNetworkAsync())
			.AddTo(this.Subscription);
	}

	#region Dispose

	private bool _disposed = false;

	protected override void Dispose(bool disposing)
	{
		if (_disposed)
			return;

		if (disposing)
		{
			CancelMeasure();
			_worker?.Dispose();
			NotifyIconContainer.Dispose();
			WindowPainter.Dispose();
			History?.Dispose();
			Settings.Current.Dispose();
		}

		_disposed = true;

		base.Dispose(disposing);
	}

	#endregion

	protected Task<string> HandleRequestAsync(IReadOnlyCollection<string> args)
	{
		OnMainWindowShowRequested(null, EventArgs.Empty);
		return Task.FromResult<string>(null);
	}

	private void OnMainWindowShowRequested(object sender, EventArgs e)
	{
		_current.Dispatcher.Invoke(() => ShowMainWindow());
	}

	private void OnMenuWindowShowRequested(object sender, Point e)
	{
		ShowMenuWindow(e);
	}

	private void ShowMainWindow()
	{
		var window = (MainWindow)_current.MainWindow;
		if (window is null or { CanBeShown: false } or { Visibility: Visibility.Visible, IsForeground: true })
			return;

		window.Show();
		window.Activate();
	}

	private void ShowMenuWindow(Point pivot)
	{
		var window = new MenuWindow(this, pivot);
		window.Show();
	}

	/// <summary>
	/// Sets the text of the notify icon which consists of the title, the modes and the names
	/// of connected profiles.
	/// </summary>
	private void SetNotifyIconText()
	{
		try
		{
			var text = ProductInfo.Title
				+ (RushesRescan.Value ? $"{Environment.NewLine}Rush {Settings.Current.RescanInterval}" : string.Empty)
				+ (EngagesPriority.Value ? $"{Environment.NewLine}Engage {Settings.Current.SignalThreshold}{(Settings.Current.EngagesSpeed ? " Speed" : string.Empty)}" : string.Empty);

			NotifyIconContainer.Text = GetProfiles()
				.Where(x => x.IsConnected)
				.Aggregate(text, (w, n) => $"{w}{Environment.NewLine}{n.Name}");
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to set the text of the notify icon." + Environment.NewLine
				+ ex);
		}
	}

	#region Update

	private static readonly TimeSpan _scanTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan _failureInterval = TimeSpan.FromMinutes(3);

	private Task ScanNetworkAsync()
	{
		// A scan takes the radio off the channel and so would distort a running speed measurement.
		if (IsMeasuring.Value)
			return LoadProfilesAsync();

		Debug.WriteLine("Scan start!");

		return UpdateAsync(() => _worker.ScanNetworkAsync(_scanTimeout));
	}

	private Task LoadProfilesAsync()
	{
		Debug.WriteLine("Load start!");

		return UpdateAsync(() => Task.Run(() => LoadProfilesBaseAsync()));
	}

	private async Task LoadProfilesBaseAsync()
	{
		var oldProfileIndices = Enumerable.Range(0, Profiles.Count).ToList();
		var newProfiles = new List<ProfileItem>();

		foreach (var newProfile in await _worker.GetProfilesAsync())
		{
			var isExisting = false;

			foreach (int index in oldProfileIndices)
			{
				var oldProfile = Profiles[index];
				if (string.Equals(oldProfile.Id, newProfile.Id, StringComparison.Ordinal))
				{
					isExisting = true;
					oldProfile.Copy(newProfile);
					oldProfileIndices.Remove(index);
					break;
				}
			}

			if (!isExisting)
				newProfiles.Add(newProfile);
		}

		newProfiles.ForEach(ApplySummary);

		if ((oldProfileIndices.Count > 0) || (newProfiles.Count > 0))
		{
			lock (_profilesLock)
			{
				oldProfileIndices.Reverse(); // Reverse indices to start removing from the tail.
				oldProfileIndices.ForEach(x => Profiles.RemoveAt(x));

				// Avoided state must be applied in the same lock as adding profiles so that
				// no profile is added with avoided state older than the settings.
				ApplyAvoided(newProfiles);
				newProfiles.ForEach(x => Profiles.Add(x));
			}

			// Calculate count of positions for each interface.
			Profiles
				.GroupBy(x => x.InterfaceId)
				.ToList()
				.ForEach(subProfiles =>
				{
					var count = subProfiles.Count();

					foreach (var profile in subProfiles)
						profile.PositionCount = count;
				});
		}

		RecordSignals();
		EvaluateDryRun();

		Debug.WriteLine(Profiles.Any()
			? Profiles
				.Select(x => $"Profile {x.Name} -> AutoConnect: {x.IsAutoConnectEnabled}, AutoSwitch: {x.IsAutoSwitchEnabled}, Position: {x.Position}, IsRadioOn: {x.IsRadioOn}, Signal: {x.Signal}, IsConnected {x.IsConnected}")
				.Aggregate((w, n) => $"{w}{Environment.NewLine}{n}")
			: "No Profile");

		if (EngagesPriority.Value && !_isProbing)
		{
			// The target of each interface is selected by the switching policy from the profiles
			// allowed to be connected to. Unless speed is engaged, it is the first one in
			// priority order whose signal is at or above the threshold.
			var targetProfiles = Profiles
				.GroupBy(x => x.InterfaceId)
				.Select(subProfiles => SelectTarget(
					subProfiles.Key,
					GetCandidates(subProfiles).Where(x => !x.IsAvoided).ToArray(),
					subProfiles.FirstOrDefault(x => x.IsConnected)))
				.Where(x => x is { IsConnected: false })
				.ToArray();

			if (targetProfiles.Length > 0)
			{
				await Task.WhenAll(targetProfiles.Select(async x =>
				{
					var result = await ConnectNetworkAsync(x, false);
					x.LastFailureTime = result ? default : DateTime.Now;
					RecordSwitchResult(x, result);
				}));
			}
		}
	}

	private readonly SemaphoreSlim _updateSemaphore = new(1);

	private async Task UpdateAsync(Func<Task> perform)
	{
		bool isEntered = false;
		try
		{
			if (!(isEntered = _updateSemaphore.Wait(TimeSpan.Zero)))
				return;

			IsUpdating.TurnOn();

			await perform.Invoke();
		}
		finally
		{
			if (isEntered)
			{
				_updateSemaphore.Release();
				IsUpdating.TurnOff();
			}
		}
	}

	private void UpdateSignalQuality(SignalQualityChangedEventArgs args)
	{
		var targetProfile = Profiles.FirstOrDefault(x => x.IsConnected && (args.InterfaceId == x.InterfaceId));
		if (targetProfile is not null)
		{
			targetProfile.Signal = args.SignalQuality;

			RecordSignals(targetProfile);
		}
	}

	#region Sample

	private static readonly TimeSpan _sampleScanTolerance = TimeSpan.FromSeconds(5);

	private DateTime _lastSampleScanTime = DateTime.UtcNow;
	private DateTime _lastPurgeDate; // UTC
	private readonly object _sampleLock = new();

	/// <summary>
	/// Whether the sample timer is required to run
	/// </summary>
	/// <remarks>If this is false, no periodic work other than that of the original is done.</remarks>
	private bool RequiresSampleTimer => Settings.Current.RecordsHistory
		|| Settings.Current.MeasuresSpeed
		|| (Settings.Current.EngagesSpeed && Settings.Current.EngagesPriority);

	private void UpdateSampleTimer()
	{
		try
		{
			lock (_sampleLock)
			{
				if (_disposed)
					return;

				if (RequiresSampleTimer)
				{
					var interval = TimeSpan.FromSeconds(Settings.Current.SampleInterval);

					// The interval is applied only when the timer is started.
					if (SampleTimer.IsEnabled && (SampleTimer.Interval == interval))
						return;

					SampleTimer.Interval = interval;
					SampleTimer.Start(interval); // Wait for due time.
				}
				else
				{
					SampleTimer.Stop();
				}
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to update the sample timer." + Environment.NewLine
				+ ex);
		}
	}

	private async Task OnSampleTickAsync()
	{
		try
		{
			// The connection is not settled yet just after resume from sleep.
			if (IsResumed)
				return;

			await PurgeHistoryAsync();

			if (await RequiresSampleScanAsync())
			{
				_lastSampleScanTime = DateTime.UtcNow;
				await ScanNetworkAsync();
			}
			else
			{
				await LoadProfilesAsync();
			}

			// The measurements requested by switching decisions are started after the update.
			await MeasureRequestedAsync();
			await MeasureIfDueAsync();
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to handle a sample tick." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Determines whether a scan is to be performed in this sample tick.
	/// </summary>
	/// <remarks>
	/// A scan is performed periodically so that signal qualities of networks other than connected
	/// one are refreshed. When rescan is rushed, the scan is already repeated. A scan is put off
	/// while a speed measurement is running or the link is in use.
	/// </remarks>
	private async Task<bool> RequiresSampleScanAsync()
	{
		var scanInterval = Settings.Current.ScanInterval;
		if ((scanInterval <= 0) || RushesRescan.Value || IsMeasuring.Value)
			return false;

		if (DateTime.UtcNow - _lastSampleScanTime < TimeSpan.FromMinutes(scanInterval) - _sampleScanTolerance)
			return false;

		return !await IsLinkBusyForScanAsync();
	}

	/// <summary>
	/// Purges old history files once a day.
	/// </summary>
	private async Task PurgeHistoryAsync()
	{
		try
		{
			var today = DateTime.UtcNow.Date;

			lock (_sampleLock)
			{
				if (_lastPurgeDate == today)
					return;

				_lastPurgeDate = today;
			}

			var retentionDays = Settings.Current.HistoryRetentionDays;

			await Task.Run(() => History.Purge(retentionDays));
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to purge history." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Records signals of profiles in range.
	/// </summary>
	/// <param name="targetProfile">Profile to be recorded (all profiles if null)</param>
	private void RecordSignals(ProfileItem targetProfile = null)
	{
		if (!Settings.Current.RecordsHistory)
			return;

		try
		{
			ProfileItem[] profiles;
			if (targetProfile is not null)
			{
				profiles = [targetProfile];
			}
			else
			{
				lock (_profilesLock)
				{
					profiles = Profiles.ToArray();
				}
			}

			var now = DateTime.UtcNow;

			var samples = profiles
				.Where(x => x.Signal > 0)
				.Select(x =>
				{
					var isConnected = x.IsConnected;
					var rssi = x.Rssi;
					var linkRate = x.LinkRate;

					return new SignalSample
					{
						Time = now,
						InterfaceId = x.InterfaceId,
						Profile = x.Name,
						Connected = isConnected,
						Signal = x.Signal,
						Rssi = (rssi == 0) ? null : rssi,
						Band = x.Band,
						Channel = x.Channel,
						Protocol = x.Protocol,
						Bssid = x.Bssid,
						LinkRate = (isConnected && (linkRate > 0)) ? linkRate : null
					};
				})
				.ToArray();
			if (samples.Length == 0)
				return;

			History.AppendSignals(samples);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record signals." + Environment.NewLine
				+ ex);
		}
	}

	#endregion

	[Conditional("DEBUG")]
	private void ShowUpdatingTime()
	{
		IsUpdating
			.Select(x => new { Value = x, DateTime.Now.Ticks })
			.Pairwise()
			.Where(x => x.OldItem.Value && !x.NewItem.Value)
			.Select(x => (x.NewItem.Ticks - x.OldItem.Ticks) / TimeSpan.TicksPerMillisecond)
			.Subscribe(x => Debug.WriteLine($"Updating Time: {x}"))
			.AddTo(this.Subscription);
	}

	#endregion

	#region Work

	private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

	public Task<bool> ChangeProfileOptionAsync(ProfileItem targetProfile)
	{
		Debug.WriteLine("ChangeOption start!");

		return WorkAsync(targetProfile, x => _worker.SetProfileOptionAsync(x));
	}

	/// <summary>
	/// Reflects avoided state of a profile to the settings and the profiles of the same name.
	/// </summary>
	/// <param name="targetProfile">Profile whose avoided state has been changed</param>
	/// <remarks>This method does not change the wireless profile itself.</remarks>
	public void ChangeProfileAvoided(ProfileItem targetProfile)
	{
		if (targetProfile is null)
			return;

		try
		{
			var name = targetProfile.Name;
			var value = targetProfile.IsAvoided;

			lock (_avoidedLock)
			{
				var existing = Settings.Current.AvoidedProfiles;
				if (existing.Contains(name, StringComparer.Ordinal) != value)
				{
					Settings.Current.AvoidedProfiles = value
						? existing.Concat(new[] { name }).ToArray()
						: existing.Where(x => !string.Equals(x, name, StringComparison.Ordinal)).ToArray();
				}
			}

			// Avoided profiles are identified by name and so the profiles of the same name
			// in other interfaces follow.
			foreach (var profile in GetProfiles())
			{
				if (!ReferenceEquals(profile, targetProfile) &&
					string.Equals(profile.Name, name, StringComparison.Ordinal))
				{
					profile.IsAvoided = value;
				}
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to change avoided profiles." + Environment.NewLine
				+ ex);
		}
	}
	private readonly object _avoidedLock = new();

	private static void ApplyAvoided(IReadOnlyCollection<ProfileItem> profiles)
	{
		if (profiles.Count == 0)
			return;

		var avoided = Settings.Current.AvoidedProfiles;

		foreach (var profile in profiles)
			profile.IsAvoided = avoided.Contains(profile.Name, StringComparer.Ordinal);
	}

	private ProfileItem[] GetProfiles()
	{
		lock (_profilesLock)
		{
			return Profiles.ToArray();
		}
	}

	public Task<bool> MoveUpProfileAsync()
	{
		Debug.WriteLine("MoveUp start!");

		if (!TryGetTargetProfile(out ProfileItem targetProfile))
			return Task.FromResult(false);

		var oldPosition = targetProfile.Position;
		var newPosition = oldPosition - 1;
		if (newPosition < 0)
			return Task.FromResult(false);

		return WorkAsync(targetProfile, x => _worker.SetProfilePositionAsync(x, newPosition));
	}

	public Task<bool> MoveDownProfileAsync()
	{
		Debug.WriteLine("MoveDown start!");

		if (!TryGetTargetProfile(out ProfileItem targetProfile))
			return Task.FromResult(false);

		var oldPosition = targetProfile.Position;
		var newPosition = oldPosition + 1;
		if (newPosition > targetProfile.PositionCount - 1)
			return Task.FromResult(false);

		return WorkAsync(targetProfile, x => _worker.SetProfilePositionAsync(x, newPosition));
	}

	public bool IsUsableProfileName(Guid interfaceId, string profileName)
	{
		if (string.IsNullOrWhiteSpace(profileName))
			return false;

		return Profiles
			.Where(x => x.InterfaceId == interfaceId)
			.All(x => !string.Equals(profileName, x.Name, StringComparison.Ordinal));
	}

	public async Task<bool> RenameProfileAsync(ProfileItem targetProfile, string profileName)
	{
		Debug.WriteLine("Rename start!");

		var oldName = targetProfile.Name;
		var interfaceId = targetProfile.InterfaceId;

		var result = await WorkAsync(targetProfile, x => _worker.RenameProfileAsync(x, profileName));
		if (result)
		{
			RenameAvoided(oldName, profileName, interfaceId);

			History.Rename(
				HistoryService.GetKey(oldName, interfaceId),
				HistoryService.GetKey(profileName, interfaceId));

			// The renamed profile may have been loaded before the history is renamed.
			ProfileItem renamedProfile;
			lock (_profilesLock)
			{
				renamedProfile = Profiles.FirstOrDefault(x => (x.InterfaceId == interfaceId)
					&& string.Equals(x.Name, profileName, StringComparison.Ordinal));
			}
			ApplySummary(renamedProfile);
		}
		return result;
	}

	private void RenameAvoided(string oldName, string newName, Guid interfaceId)
	{
		try
		{
			// The outer lock makes changing the settings and applying them atomic to adding
			// profiles. The order of locks is always the lock for profiles first.
			lock (_profilesLock)
			{
				var profiles = Profiles.ToArray();

				lock (_avoidedLock)
				{
					var existing = Settings.Current.AvoidedProfiles;
					if (!existing.Contains(oldName, StringComparer.Ordinal))
						return;

					// The old name stays if a profile of that name remains in another interface.
					var remains = profiles.Any(x => (x.InterfaceId != interfaceId)
						&& string.Equals(x.Name, oldName, StringComparison.Ordinal));

					Settings.Current.AvoidedProfiles = existing
						.Where(x => remains || !string.Equals(x, oldName, StringComparison.Ordinal))
						.Concat(new[] { newName })
						.Distinct(StringComparer.Ordinal)
						.ToArray();
				}

				// The renamed profile may have been loaded before the settings are changed.
				ApplyAvoided(profiles
					.Where(x => string.Equals(x.Name, newName, StringComparison.Ordinal))
					.ToArray());
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to rename avoided profile." + Environment.NewLine
				+ ex);
		}
	}

	public Task<bool> DeleteProfileAsync()
	{
		Debug.WriteLine("Delete start!");

		if (!TryGetTargetProfile(out ProfileItem targetProfile))
			return Task.FromResult(false);

		return WorkAsync(targetProfile, x => _worker.DeleteProfileAsync(x));
	}

	public Task<bool> ConnectNetworkAsync(ProfileItem targetProfile, bool isManual = true)
	{
		Debug.WriteLine("Connect start!");

		if (isManual)
		{
			EngagesPriority.Value = false;
			ClearPendingSwitch(targetProfile.InterfaceId);
		}

		return WorkAsync(targetProfile, x => _worker.ConnectNetworkAsync(x, _connectTimeout));
	}

	public Task<bool> DisconnectNetworkAsync(ProfileItem targetProfile, bool isManual = true)
	{
		Debug.WriteLine("Disconnect start!");

		if (isManual)
		{
			EngagesPriority.Value = false;
			ClearPendingSwitch(targetProfile.InterfaceId);
		}

		return WorkAsync(targetProfile, x => _worker.DisconnectNetworkAsync(x, _connectTimeout));
	}

	private bool TryGetTargetProfile(out ProfileItem targetProfile) =>
		((targetProfile = Profiles.FirstOrDefault(x => x.IsTarget)) is not null);

	private int _workCount = 0;

	private async Task<bool> WorkAsync(ProfileItem targetProfile, Func<ProfileItem, Task<bool>> perform)
	{
		try
		{
			Interlocked.Increment(ref _workCount);
			IsWorking.TurnOn();

			return await perform.Invoke(targetProfile);
		}
		finally
		{
			if (Interlocked.Decrement(ref _workCount) == 0)
				IsWorking.TurnOff();
		}
	}

	[Conditional("DEBUG")]
	private void ShowWorkingTime()
	{
		IsWorking
			.Select(x => new { Value = x, DateTime.Now.Ticks })
			.Pairwise()
			.Where(x => x.OldItem.Value && !x.NewItem.Value)
			.Select(x => (x.NewItem.Ticks - x.OldItem.Ticks) / TimeSpan.TicksPerMillisecond)
			.Subscribe(x => Debug.WriteLine($"Working Time: {x}"))
			.AddTo(this.Subscription);
	}

	#endregion
}
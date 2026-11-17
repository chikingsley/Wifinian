using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Windows;
using Reactive.Bindings.Extensions;

using Wifinian.Common;

namespace Wifinian.Models;

/// <summary>
/// Persistent settings
/// </summary>
public class Settings : DisposableBase
{
	public static Settings Current => _current.Value;
	private static readonly Lazy<Settings> _current = new(() => new Settings());

	private Settings()
	{ }

	#region Settings

	private const int MinInterval = 10;
	private const int MaxInterval = 30;

	public int RescanInterval
	{
		get => _rescanInterval;
		set => SetProperty(ref _rescanInterval, value, MinInterval, MaxInterval);
	}
	private int _rescanInterval = 30; // Default

	public void IncrementRescanInterval() =>
		RescanInterval = IncrementLoop(RescanInterval, MinInterval, MaxInterval);

	private const int MinThreshold = 50;
	private const int MaxThreshold = 90;

	public int SignalThreshold
	{
		get => _signalThreshold;
		set => SetProperty(ref _signalThreshold, value, MinThreshold, MaxThreshold);
	}
	private int _signalThreshold = 50; // Default

	public void IncrementSignalThreshold() =>
		SignalThreshold = IncrementLoop(SignalThreshold, MinThreshold, MaxThreshold);

	private static int IncrementLoop(int value, int min, int max)
	{
		var buffer = (value / 10 + 1) * 10;
		return (buffer <= max) ? buffer : min;
	}

	public bool EngagesPriority
	{
		get => _engagesPriority;
		set => SetProperty(ref _engagesPriority, value);
	}
	private bool _engagesPriority;

	public bool ShowsAvailable
	{
		get => _showsAvailable;
		set => SetProperty(ref _showsAvailable, value);
	}
	private bool _showsAvailable;

	public Size MainWindowSize
	{
		get => _mainWindowSize;
		set => SetProperty(ref _mainWindowSize, value);
	}
	private Size _mainWindowSize;

	/// <summary>
	/// Whether to record history of signal
	/// </summary>
	public bool RecordsHistory
	{
		get => _recordsHistory;
		set => SetProperty(ref _recordsHistory, value);
	}
	private bool _recordsHistory = true; // Default

	private const int MinSampleInterval = 30;
	private const int MaxSampleInterval = 300;

	/// <summary>
	/// Interval (sec) of sampling
	/// </summary>
	public int SampleInterval
	{
		get => _sampleInterval;
		set => SetProperty(ref _sampleInterval, value, MinSampleInterval, MaxSampleInterval);
	}
	private int _sampleInterval = 60; // Default

	private const int MinRetentionDays = 7;
	private const int MaxRetentionDays = 365;

	/// <summary>
	/// Days to retain history
	/// </summary>
	public int HistoryRetentionDays
	{
		get => _historyRetentionDays;
		set => SetProperty(ref _historyRetentionDays, value, MinRetentionDays, MaxRetentionDays);
	}
	private int _historyRetentionDays = 90; // Default

	private const int MinScanInterval = 0;
	private const int MaxScanInterval = 1440;

	/// <summary>
	/// Interval (min) of background scan by the sample timer (0 for never)
	/// </summary>
	public int ScanInterval
	{
		get => _scanInterval;
		set => SetProperty(ref _scanInterval, value, MinScanInterval, MaxScanInterval);
	}
	private int _scanInterval = 5; // Default

	/// <summary>
	/// Whether to measure speed of connected network automatically
	/// </summary>
	public bool MeasuresSpeed
	{
		get => _measuresSpeed;
		set => SetProperty(ref _measuresSpeed, value);
	}
	private bool _measuresSpeed = true; // Default

	private const int MinSpeedTestInterval = 15;
	private const int MaxSpeedTestInterval = 240;

	/// <summary>
	/// Interval (min) of periodic speed measurement
	/// </summary>
	public int SpeedTestInterval
	{
		get => _speedTestInterval;
		set => SetProperty(ref _speedTestInterval, value, MinSpeedTestInterval, MaxSpeedTestInterval);
	}
	private int _speedTestInterval = 60; // Default

	private const int MinSpeedTestDailyBudget = 50;
	private const int MaxSpeedTestDailyBudget = 5000;

	/// <summary>
	/// Daily budget (MB) of data used by automatic speed measurements
	/// </summary>
	public int SpeedTestDailyBudget
	{
		get => _speedTestDailyBudget;
		set => SetProperty(ref _speedTestDailyBudget, value, MinSpeedTestDailyBudget, MaxSpeedTestDailyBudget);
	}
	private int _speedTestDailyBudget = 500; // Default

	internal const string DefaultSpeedTestDownloadUrl = "https://speed.cloudflare.com/__down?bytes=25000000";
	internal const string DefaultSpeedTestUploadUrl = "https://speed.cloudflare.com/__up";
	internal const string DefaultConnectivityCheckUrl = "http://www.msftconnecttest.com/connecttest.txt";

	/// <summary>
	/// URL to download from in speed measurement
	/// </summary>
	public string SpeedTestDownloadUrl
	{
		get => _speedTestDownloadUrl;
		set => SetProperty(ref _speedTestDownloadUrl, string.IsNullOrWhiteSpace(value) ? DefaultSpeedTestDownloadUrl : value.Trim());
	}
	private string _speedTestDownloadUrl = DefaultSpeedTestDownloadUrl;

	/// <summary>
	/// URL to upload to in speed measurement
	/// </summary>
	public string SpeedTestUploadUrl
	{
		get => _speedTestUploadUrl;
		set => SetProperty(ref _speedTestUploadUrl, string.IsNullOrWhiteSpace(value) ? DefaultSpeedTestUploadUrl : value.Trim());
	}
	private string _speedTestUploadUrl = DefaultSpeedTestUploadUrl;

	/// <summary>
	/// URL to check internet access
	/// </summary>
	public string ConnectivityCheckUrl
	{
		get => _connectivityCheckUrl;
		set => SetProperty(ref _connectivityCheckUrl, string.IsNullOrWhiteSpace(value) ? DefaultConnectivityCheckUrl : value.Trim());
	}
	private string _connectivityCheckUrl = DefaultConnectivityCheckUrl;

	/// <summary>
	/// Names of profiles avoided in automatic connection by this app
	/// </summary>
	/// <remarks>The array is never modified in place. A new array is always assigned.</remarks>
	public string[] AvoidedProfiles
	{
		get => _avoidedProfiles;
		set => SetProperty(ref _avoidedProfiles, value ?? Array.Empty<string>());
	}
	private string[] _avoidedProfiles = Array.Empty<string>();

	/// <summary>
	/// Whether to select the network to connect to by measured speed
	/// </summary>
	public bool EngagesSpeed
	{
		get => _engagesSpeed;
		set => SetProperty(ref _engagesSpeed, value);
	}
	private bool _engagesSpeed;

	private const int MinSwitchDwellTime = 2;
	private const int MaxSwitchDwellTime = 60;

	/// <summary>
	/// Time (min) to stay on a network after connection before switching by speed
	/// </summary>
	public int SwitchDwellTime
	{
		get => _switchDwellTime;
		set => SetProperty(ref _switchDwellTime, value, MinSwitchDwellTime, MaxSwitchDwellTime);
	}
	private int _switchDwellTime = 10; // Default

	private const int MinSwitchMargin = 20;
	private const int MaxSwitchMargin = 200;

	/// <summary>
	/// Margin (percent) by which another network must be faster to be switched to
	/// </summary>
	public int SwitchMargin
	{
		get => _switchMargin;
		set => SetProperty(ref _switchMargin, value, MinSwitchMargin, MaxSwitchMargin);
	}
	private int _switchMargin = 50; // Default

	private const int MinSufficientSpeed = 5;
	private const int MaxSufficientSpeed = 1000;

	/// <summary>
	/// Speed (Mbps) at or above which connected network is never left for a faster one
	/// </summary>
	public int SufficientSpeed
	{
		get => _sufficientSpeed;
		set => SetProperty(ref _sufficientSpeed, value, MinSufficientSpeed, MaxSufficientSpeed);
	}
	private int _sufficientSpeed = 50; // Default

	private const int MinSpeedSignalThreshold = 20;
	private const int MaxSpeedSignalThreshold = 50;

	/// <summary>
	/// Signal threshold for a network whose speed has been measured
	/// </summary>
	public int SpeedSignalThreshold
	{
		get => _speedSignalThreshold;
		set => SetProperty(ref _speedSignalThreshold, value, MinSpeedSignalThreshold, MaxSpeedSignalThreshold);
	}
	private int _speedSignalThreshold = 30; // Default

	private const int MinSwitchMaxDeferral = 0;
	private const int MaxSwitchMaxDeferral = 120;

	/// <summary>
	/// Maximum time (min) to defer switching by speed while the link is in use (0 for no deferral)
	/// </summary>
	public int SwitchMaxDeferral
	{
		get => _switchMaxDeferral;
		set => SetProperty(ref _switchMaxDeferral, value, MinSwitchMaxDeferral, MaxSwitchMaxDeferral);
	}
	private int _switchMaxDeferral = 15; // Default

	/// <summary>
	/// Whether to test networks other than the connected one while this machine is idle
	/// </summary>
	/// <remarks>The connection drops for a while during the test.</remarks>
	public bool ProbesWhenIdle
	{
		get => _probesWhenIdle;
		set => SetProperty(ref _probesWhenIdle, value);
	}
	private bool _probesWhenIdle;

	private const int MinProbeInterval = 1;
	private const int MaxProbeInterval = 48;

	/// <summary>
	/// Interval (hours) of tests of other networks while this machine is idle
	/// </summary>
	public int ProbeInterval
	{
		get => _probeInterval;
		set => SetProperty(ref _probeInterval, value, MinProbeInterval, MaxProbeInterval);
	}
	private int _probeInterval = 6; // Default

	#endregion

	internal void Initiate()
	{
		Load(this);

		this.PropertyChangedAsObservable()
			.Throttle(TimeSpan.FromMilliseconds(100))
			.Subscribe(_ => Save(this))
			.AddTo(this.Subscription);
	}

	#region Load/Save

	private const string SettingsFileName = "settings.xml";

	private static void Load<T>(T instance) where T : class
	{
		try
		{
			AppDataService.Load(instance, SettingsFileName);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to load settings." + Environment.NewLine
				+ ex);
		}
	}

	private static void Save<T>(T instance) where T : class
	{
		try
		{
			AppDataService.Save(instance, SettingsFileName);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to save settings." + Environment.NewLine
				+ ex);
		}
	}

	#endregion
}
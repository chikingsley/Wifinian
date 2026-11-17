using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

using Wifinian.Common;
using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Wlan;

namespace Wifinian.ViewModels;

public class ProfileItemViewModel : DisposableBase
{
	private readonly AppController _controller;

	public string Name
	{
		get => _name;
		set
		{
			if ((_name is not null) && !_controller.IsUsableProfileName(InterfaceId, value))
				return;

			SetProperty(ref _name, value);
		}
	}
	private string _name;

	private Guid InterfaceId { get; }
	public string InterfaceDescription { get; }
	public string Authentication { get; }
	public string Encryption { get; }
	public bool CanSetOptions { get; }

	public ReactiveProperty<bool> IsAutoConnectEnabled { get; }
	public ReactiveProperty<bool> IsAutoSwitchEnabled { get; }

	public ReadOnlyReactivePropertySlim<int> Position { get; }
	public ReadOnlyReactivePropertySlim<int> PositionCount { get; }

	public ReadOnlyReactivePropertySlim<bool> IsRadioOn { get; }
	public ReadOnlyReactivePropertySlim<bool> IsConnected { get; }

	public ReadOnlyReactivePropertySlim<string> Protocol { get; }
	public ReadOnlyReactivePropertySlim<int> Signal { get; }
	public ReadOnlyReactivePropertySlim<float> Band { get; }
	public ReadOnlyReactivePropertySlim<int> Channel { get; }

	public ReadOnlyReactivePropertySlim<bool> IsAvailable { get; }

	public ReactiveProperty<bool> IsAvoided { get; }

	public ReadOnlyReactivePropertySlim<string> SpeedText { get; }
	public ReadOnlyReactivePropertySlim<string> SpeedToolTip { get; }

	public ReactiveProperty<bool> IsSelected { get; }

	public ReactiveCommand ConnectCommand { get; }
	public ReactiveCommand DisconnectCommand { get; }

	internal ProfileItemViewModel(AppController controller, ProfileItem profileItem)
	{
		this._controller = controller ?? throw new ArgumentNullException(nameof(controller));

		Name = profileItem.Name;
		InterfaceId = profileItem.InterfaceId;
		InterfaceDescription = profileItem.InterfaceDescription;
		Authentication = profileItem.AuthenticationString.Replace("_", "-");
		Encryption = profileItem.Encryption.ToString();
		CanSetOptions = profileItem.CanSetOptions;

		this.ObserveProperty(x => x.Name, false)
			.Subscribe(async x => await _controller.RenameProfileAsync(profileItem, x))
			.AddTo(this.Subscription);

		IsAutoSwitchEnabled = profileItem
			.ToReactivePropertyAsSynchronized(x => x.IsAutoSwitchEnabled, ReactivePropertyMode.DistinctUntilChanged)
			.AddTo(this.Subscription);
		IsAutoConnectEnabled = profileItem
			.ToReactivePropertyAsSynchronized(x => x.IsAutoConnectEnabled, ReactivePropertyMode.DistinctUntilChanged)
			.AddTo(this.Subscription);
		IsAutoConnectEnabled
			.Where(x => !x)
			.Subscribe(_ => IsAutoSwitchEnabled.Value = false)
			.AddTo(this.Subscription);

		Observable.Merge(IsAutoConnectEnabled, IsAutoSwitchEnabled)
			.Subscribe(async _ => await _controller.ChangeProfileOptionAsync(profileItem))
			.AddTo(this.Subscription);

		Position = profileItem
			.ObserveProperty(x => x.Position)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		PositionCount = profileItem
			.ObserveProperty(x => x.PositionCount)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		IsRadioOn = profileItem
			.ObserveProperty(x => x.IsRadioOn)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		IsConnected = profileItem
			.ObserveProperty(x => x.IsConnected)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		Protocol = profileItem
			.ObserveProperty(x => x.Protocol)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		Signal = profileItem
			.ObserveProperty(x => x.Signal)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		Band = profileItem
			.ObserveProperty(x => x.Band)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		Channel = profileItem
			.ObserveProperty(x => x.Channel)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		IsAvailable = Signal
			.Select(x => 0 < x)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		// This option is kept in the settings of this app and so must not trigger change of
		// the wireless profile.
		IsAvoided = profileItem
			.ToReactivePropertyAsSynchronized(x => x.IsAvoided, ReactivePropertyMode.DistinctUntilChanged)
			.AddTo(this.Subscription);
		IsAvoided
			.Subscribe(_ => _controller.ChangeProfileAvoided(profileItem))
			.AddTo(this.Subscription);

		var speedChanged = Observable.Merge(
			profileItem.ObserveProperty(x => x.LastMeasuredTime).Select(_ => Unit.Default),
			profileItem.ObserveProperty(x => x.LastSpeedResult, false).Select(_ => Unit.Default),
			profileItem.ObserveProperty(x => x.LastDownMbps, false).Select(_ => Unit.Default),
			profileItem.ObserveProperty(x => x.LastUpMbps, false).Select(_ => Unit.Default),
			profileItem.ObserveProperty(x => x.LastLatencyMs, false).Select(_ => Unit.Default),
			profileItem.ObserveProperty(x => x.TypicalDownMbps, false).Select(_ => Unit.Default),
			profileItem.ObserveProperty(x => x.SpeedSampleCount, false).Select(_ => Unit.Default),
			profileItem.ObserveProperty(x => x.Bssid, false).Select(_ => Unit.Default),
			profileItem.ObserveProperty(x => x.IsMeasuring, false).Select(_ => Unit.Default),
			Observable.Interval(TimeSpan.FromMinutes(1)).Select(_ => Unit.Default)) // For age
			.Publish();

		SpeedText = speedChanged
			.Select(_ => BuildSpeedText(profileItem, DateTime.UtcNow))
			.ToReadOnlyReactivePropertySlim(string.Empty)
			.AddTo(this.Subscription);

		SpeedToolTip = speedChanged
			.Select(_ => BuildSpeedToolTip(profileItem, DateTime.UtcNow))
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		speedChanged.Connect().AddTo(this.Subscription);

		IsSelected = ReactiveProperty.FromObject(profileItem, x => x.IsTarget)
			.AddTo(this.Subscription);

		#region Work

		var isNotWorking = _controller.IsWorking
			.Inverse()
			.StartWith(true) // This is necessary to start combined sequence.
			.Publish();

		ConnectCommand = new[] { isNotWorking, IsAvailable, IsConnected.Inverse() }
			.CombineLatestValuesAreAllTrue()
			.ObserveOnUIDispatcher() // This is for thread access by ReactiveCommand.
			.ToReactiveCommand();
		ConnectCommand
			.Subscribe(async _ => await _controller.ConnectNetworkAsync(profileItem))
			.AddTo(this.Subscription);

		DisconnectCommand = new[] { isNotWorking.AsObservable(), IsConnected }
			.CombineLatestValuesAreAllTrue()
			.ObserveOnUIDispatcher() // This is for thread access by ReactiveCommand.
			.ToReactiveCommand();
		DisconnectCommand
			.Subscribe(async _ => await _controller.DisconnectNetworkAsync(profileItem))
			.AddTo(this.Subscription);

		isNotWorking.Connect().AddTo(this.Subscription);

		#endregion
	}

	#region Speed

	private static readonly TimeSpan _speedPeriod = TimeSpan.FromDays(14);

	private static bool HasSpeed(ProfileItem profileItem, DateTime now) =>
		(profileItem.LastMeasuredTime != default) && (now - profileItem.LastMeasuredTime <= _speedPeriod);

	internal static string BuildSpeedText(ProfileItem profileItem, DateTime now)
	{
		if (profileItem.IsMeasuring)
			return LanguageService.Testing;

		if (!HasSpeed(profileItem, now))
			return string.Empty;

		var elapsed = now - profileItem.LastMeasuredTime;
		var age = BuildAge(elapsed);

		switch (profileItem.LastSpeedResult)
		{
			case SpeedResult.NoInternet:
				return $"{LanguageService.NoInternet}  {age}";
			case SpeedResult.CaptivePortal:
				return $"{LanguageService.SignInRequired}  {age}";
		}

		if (profileItem.LastDownMbps is not { } down)
			return string.Empty;

		var text = ((elapsed > TimeSpan.FromHours(24)) ? "~" : string.Empty) + FormatNumber(down);

		if (profileItem.LastUpMbps is { } up)
			text += " / " + FormatNumber(up);

		text += " Mbps";

		if (profileItem.LastLatencyMs is { } latency)
			text += "  " + FormatNumber(latency) + " ms";

		return $"{text}  {age}";
	}

	internal static string BuildSpeedToolTip(ProfileItem profileItem, DateTime now)
	{
		if (!HasSpeed(profileItem, now))
			return null;

		var lines = new List<string>();

		if (profileItem.TypicalDownMbps is { } typical)
		{
			lines.Add(string.Format(CultureInfo.InvariantCulture, "Typical {0:0.#} Mbps over {1} {2}",
				typical, profileItem.SpeedSampleCount, (profileItem.SpeedSampleCount == 1) ? "test" : "tests"));
		}

		lines.Add($"Last tested {profileItem.LastMeasuredTime.ToLocalTime():g}");

		if (!string.IsNullOrEmpty(profileItem.Bssid))
			lines.Add($"BSSID {profileItem.Bssid}");

		return string.Join(Environment.NewLine, lines);
	}

	private static string BuildAge(TimeSpan elapsed)
	{
		if (elapsed < TimeSpan.FromMinutes(1))
			return "now";
		if (elapsed < TimeSpan.FromMinutes(60))
			return $"{(int)elapsed.TotalMinutes}m";
		if (elapsed < TimeSpan.FromHours(48))
			return $"{(int)elapsed.TotalHours}h";

		return $"{(int)elapsed.TotalDays}d";
	}

	private static string FormatNumber(double value) =>
		value.ToString((value < 10D) ? "0.#" : "0", CultureInfo.InvariantCulture);

	#endregion
}
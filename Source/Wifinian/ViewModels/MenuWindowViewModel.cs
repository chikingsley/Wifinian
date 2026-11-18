using System;
using System.Reactive.Linq;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

using Wifinian.Common;
using Wifinian.Models;
using Wifinian.Models.Update;

namespace Wifinian.ViewModels;

public class MenuWindowViewModel : DisposableBase
{
	private readonly AppController _controller;

	public ReactiveCommand HistoryCommand => _controller?.HistoryCommand;
	public ReactiveCommand MeasureCommand => _controller?.MeasureCommand;
	public ReactiveCommand ProbeCommand => _controller?.ProbeCommand;
	public ReactiveCommand UpgradeCommand => _controller?.UpgradeCommand;
	public ReactiveCommand CloseCommand => _controller?.CloseCommand;

	public ReadOnlyReactivePropertySlim<string> ProbeText { get; }
	public ReadOnlyReactivePropertySlim<string> UpgradeText { get; }

	internal MenuWindowViewModel(AppController controller)
	{
		this._controller = controller ?? throw new ArgumentNullException(nameof(controller));

		// While the test of all networks is running, the command to start it works as cancel.
		ProbeText = _controller.IsProbing
			.StartWith(_controller.IsProbing.Value)
			.Select(x => x ? LanguageService.CancelTest : LanguageService.TestAll)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);

		// The command to look for a new version tells its state and, once a new version is
		// known, works as the command to install it.
		UpgradeText = _controller.UpgradeStatus
			.Select(x => GetUpgradeText(x))
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);
	}

	/// <summary>
	/// Gets the text of the command to look for or install a new version.
	/// </summary>
	internal static string GetUpgradeText(UpdateStatus status) => status?.Phase switch
	{
		UpdatePhase.Checking => LanguageService.UpdateChecking,
		UpdatePhase.Latest => string.Format(LanguageService.UpdateLatest, status.Version),
		UpdatePhase.Available => string.Format(LanguageService.UpdateInstall, status.Version),
		UpdatePhase.Downloading => string.Format(LanguageService.UpdateDownloading, status.Version),
		UpdatePhase.Failed => string.Format(LanguageService.UpdateFailed, status.Reason),
		_ => LanguageService.UpdateCheck
	};

	#region Startup

	public bool CanRegister => _controller.StartupAgent.CanRegister();

	public bool IsRegistered
	{
		get => _isRegistered ??= _controller.StartupAgent.IsRegistered();
		set
		{
			if (_isRegistered == value)
				return;

			if (value)
			{
				_controller.StartupAgent.Register();
			}
			else
			{
				_controller.StartupAgent.Unregister();
			}
			_isRegistered = value;
			OnPropertyChanged();
		}
	}
	private bool? _isRegistered;

	#endregion
}
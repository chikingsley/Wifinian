using System;
using System.Reactive.Linq;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

using Wifinian.Common;
using Wifinian.Models;

namespace Wifinian.ViewModels;

public class MenuWindowViewModel : DisposableBase
{
	private readonly AppController _controller;

	public ReactiveCommand HistoryCommand => _controller?.HistoryCommand;
	public ReactiveCommand MeasureCommand => _controller?.MeasureCommand;
	public ReactiveCommand ProbeCommand => _controller?.ProbeCommand;
	public ReactiveCommand CloseCommand => _controller?.CloseCommand;

	public ReadOnlyReactivePropertySlim<string> ProbeText { get; }

	internal MenuWindowViewModel(AppController controller)
	{
		this._controller = controller ?? throw new ArgumentNullException(nameof(controller));

		// While the test of all networks is running, the command to start it works as cancel.
		ProbeText = _controller.IsProbing
			.StartWith(_controller.IsProbing.Value)
			.Select(x => x ? LanguageService.CancelTest : LanguageService.TestAll)
			.ToReadOnlyReactivePropertySlim()
			.AddTo(this.Subscription);
	}

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
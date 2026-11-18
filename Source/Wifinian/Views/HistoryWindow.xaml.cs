using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

using Wifinian.ViewModels;

namespace Wifinian.Views;

public partial class HistoryWindow : Window
{
	private HistoryWindowViewModel ViewModel => (HistoryWindowViewModel)this.DataContext;

	internal HistoryWindow(AppController controller)
	{
		InitializeComponent();

		this.DataContext = new HistoryWindowViewModel(controller);
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);

		try
		{
			if (PresentationSource.FromVisual(this) is HwndSource source)
			{
				// The other windows of this app are layered windows (AllowsTransparency), which are
				// always rendered by software. This window is an ordinary one and so would be
				// presented through Direct3D, which can leave the whole client area blank white
				// while the tree of elements is complete (seen on a machine whose display adapter
				// does not present). Software rendering always paints and is enough for this window.
				source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;

				SetDarkTitleBar(source.Handle);
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to prepare the history window." + Environment.NewLine
				+ ex);
		}
	}

	#region Title bar

	[DllImport("Dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(
		IntPtr hwnd,
		uint dwAttribute,
		ref int pvAttribute,
		uint cbAttribute);

	private const uint DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 before version 2004
	private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	/// <summary>
	/// Makes the title bar dark so that it matches the content of this window.
	/// </summary>
	private static void SetDarkTitleBar(IntPtr handle)
	{
		int value = 1;
		if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
			DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
	}

	#endregion

	/// <summary>
	/// Applies the range of a button when it gets checked by any means (mouse, keyboard or
	/// UI Automation, which selects a button without clicking it).
	/// </summary>
	private void OnRangeChecked(object sender, RoutedEventArgs e)
	{
		// The initially checked button raises this event before the view model is set.
		if (this.DataContext is not HistoryWindowViewModel viewModel)
			return;

		// The tag of each button holds the range in hours.
		if ((sender is FrameworkElement { Tag: string tag })
			&& int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours))
		{
			viewModel.RangeHours.Value = hours;
		}
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (!e.Cancel)
		{
			ViewModel.Dispose();
		}

		base.OnClosing(e);
	}
}
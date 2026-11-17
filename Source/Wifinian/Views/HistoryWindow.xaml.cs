using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;

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

	private void OnRangeClick(object sender, RoutedEventArgs e)
	{
		// The tag of each button holds the range in hours.
		if ((sender is FrameworkElement { Tag: string tag })
			&& int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours))
		{
			ViewModel.RangeHours.Value = hours;
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
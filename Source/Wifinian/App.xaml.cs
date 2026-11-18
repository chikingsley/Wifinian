using System;
using System.Windows;

using Wifinian.Models;

namespace Wifinian;

public partial class App : Application
{
	private AppKeeper _keeper;
	private AppController _controller;

	/// <summary>
	/// Reason of shutdown to be recorded in the operation log (null if not given yet)
	/// </summary>
	/// <remarks>The first reason given is kept.</remarks>
	internal static string ExitReason { get; set; }

	private bool _isPrimary;

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		_keeper = new AppKeeper(e);
		if (!_keeper.Start())
		{
			// The running instance has only been asked to show its window. It is never closed
			// or otherwise affected by this instance.
			Logger.RecordOperation($"A second instance (process {Logger.ProcessId}) was started and ends at once. The running instance was asked to show its window.");

			this.Shutdown(0); // This shutdown is expected behavior.
			return;
		}

		_isPrimary = true;
		Logger.RecordStart();
		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

		_controller = new AppController(_keeper);
		await _controller.InitiateAsync();

		//this.MainWindow = new MainWindow();
		//this.MainWindow.Show();
	}

	protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
	{
		ExitReason ??= $"The Windows session is ending ({e.ReasonSessionEnding})";

		base.OnSessionEnding(e);
	}

	protected override void OnExit(ExitEventArgs e)
	{
		// An exception thrown here would end the process with the shutdown left unrecorded.
		try
		{
			_controller?.Dispose();
		}
		catch (Exception ex)
		{
			Logger.RecordException(ex, "Exit");
		}

		if (_isPrimary)
		{
			Logger.RecordEnd(
				ExitReason ?? "Shutdown was requested with no reason given (the last window was closed or Shutdown was called)",
				e.ApplicationExitCode);
		}

		try
		{
			_keeper?.Dispose();
		}
		catch (Exception ex)
		{
			Logger.RecordException(ex, "Exit");
		}

		base.OnExit(e);
	}

	private static void OnProcessExit(object sender, EventArgs e)
	{
		// This is reached when the process ends by a way other than shutdown of the application
		// (e.g. Environment.Exit). A process killed from outside reaches nowhere and is found
		// at the next start instead.
		if (!Logger.IsEndRecorded)
			Logger.RecordOperation($"Process {Logger.ProcessId} is exiting without shutdown of the application.");
	}
}
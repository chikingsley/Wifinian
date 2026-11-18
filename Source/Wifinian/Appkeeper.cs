using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Reactive.Bindings.Extensions;

using StartupAgency;
using Wifinian.Common;
using Wifinian.Models;

namespace Wifinian;

public class AppKeeper : DisposableBase
{
	public StartupAgent StartupAgent { get; }

	public AppKeeper(StartupEventArgs e)
	{
		StartupAgent = new StartupAgent();
	}

	public bool Start()
	{
		#region Exceptions

		var dispatcherUnhandled = Observable.FromEventPattern<DispatcherUnhandledExceptionEventHandler, DispatcherUnhandledExceptionEventArgs>(
			h => Application.Current.DispatcherUnhandledException += h,
			h => Application.Current.DispatcherUnhandledException -= h)
			.Select(x => (exception: x.EventArgs.Exception, source: "Dispatcher"));
		var taskUnobserved = Observable.FromEventPattern<UnobservedTaskExceptionEventArgs>(
			h => TaskScheduler.UnobservedTaskException += h,
			h => TaskScheduler.UnobservedTaskException -= h)
			.SelectMany(x => x.EventArgs.Exception.InnerExceptions)
			.Select(x => (exception: x, source: "Task"));
		var appDomainUnhandled = Observable.FromEventPattern<UnhandledExceptionEventHandler, UnhandledExceptionEventArgs>(
			h => AppDomain.CurrentDomain.UnhandledException += h,
			h => AppDomain.CurrentDomain.UnhandledException -= h)
			.Select(x => (exception: x.EventArgs.ExceptionObject as Exception, source: "AppDomain"));
		Observable.Merge(dispatcherUnhandled, taskUnobserved, appDomainUnhandled)
			.Subscribe(x => OnException(x.exception, x.source))
			.AddTo(this.Subscription);

		#endregion

		var (success, _) = StartupAgent.Start(ProductInfo.Product, ProductInfo.StartupTaskId, null);
		return success;
	}

	private Exception _lastException;
	private int _promptCount = 0;

	/// <summary>
	/// Handles an exception which is about to end the process.
	/// </summary>
	/// <remarks>
	/// Every exception is written to the log file before anything else so that the process
	/// never ends without a trace. Prompts, which need the user, come after that and only once.
	/// This method never throws an exception.
	/// </remarks>
	private void OnException(Exception exception, string source)
	{
		try
		{
			if (exception is null)
				return;

			// An exception unhandled in the dispatcher comes again from the application domain.
			if (ReferenceEquals(Interlocked.Exchange(ref _lastException, exception), exception))
				return;

			Logger.RecordException(exception, source);
			Logger.RecordOperation($"Unhandled exception ({source}) in process {Logger.ProcessId}: {exception.GetType().FullName}: {exception.Message} The process is about to end. See exception.log.");

			if (Interlocked.Exchange(ref _promptCount, 1) != 0)
				return;

			if (LocationInfo.PromptOpenLocation(exception))
				return;

			Logger.PromptException(exception);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to handle an unhandled exception." + Environment.NewLine
				+ ex);
		}
	}
}
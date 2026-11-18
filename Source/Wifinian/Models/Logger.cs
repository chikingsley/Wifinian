using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace Wifinian.Models;

internal class Logger
{
	private const string OperationFileName = "operation.log";
	private const string ExceptionFileName = "exception.log";
	private const string RunFileName = "running.txt";

	private const int OperationCapacity = 200;
	private const int ExceptionCapacity = 10;

	private static readonly object _lock = new();

	/// <summary>
	/// ID of this process
	/// </summary>
	public static int ProcessId => _processId ??= GetProcessId();
	private static int? _processId;

	private static int GetProcessId()
	{
		try
		{
			using var process = Process.GetCurrentProcess();
			return process.Id;
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>
	/// Whether the end of this run has been recorded
	/// </summary>
	public static bool IsEndRecorded { get; private set; }

	private const string HeaderStart = "[Date:";
	private static string ComposeHeader() => $"{HeaderStart} {DateTime.Now} Ver: {ProductInfo.Version}]";

	/// <summary>
	/// Records operation log to AppData.
	/// </summary>
	/// <param name="content">Content</param>
	/// <remarks>
	/// The log file is appended and keeps the latest records regardless of their dates so that
	/// how the previous runs started and ended can be traced. This method never throws an exception.
	/// </remarks>
	public static void RecordOperation(string content)
	{
		content = ComposeHeader() + Environment.NewLine
			+ content + Environment.NewLine + Environment.NewLine;

		RecordToAppData(OperationFileName, content, OperationCapacity, keepsOld: true);
	}

	/// <summary>
	/// Records the start of this run to the operation log and, if the previous run left no record
	/// of its end, records that as well.
	/// </summary>
	/// <remarks>
	/// A process killed from outside (Task Manager, taskkill, Stop-Process) or lost by power
	/// failure cannot record anything by itself. The file which is written at start and deleted
	/// at orderly end tells such an end at the next start. This method never throws an exception.
	/// </remarks>
	public static void RecordStart()
	{
		try
		{
			var runFilePath = Path.Combine(AppDataService.EnsureFolderPath(), RunFileName);

			string previous = null;
			if (File.Exists(runFilePath))
				previous = File.ReadAllText(runFilePath, Encoding.UTF8).Trim();

			if (!string.IsNullOrEmpty(previous))
			{
				RecordOperation($"The previous run ({previous}) left no record of its end: it was killed from outside (Task Manager, taskkill, Stop-Process -Force), crashed natively or the system went down. "
					+ "The time of its last history row tells roughly when.");
			}

			RecordOperation($"Started: process {ProcessId}.");

			File.WriteAllText(runFilePath, $"process {ProcessId} started {DateTime.Now}", Encoding.UTF8);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record the start." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Records the orderly end of this run to the operation log.
	/// </summary>
	/// <param name="reason">Reason of shutdown</param>
	/// <param name="exitCode">Exit code</param>
	/// <remarks>This method never throws an exception.</remarks>
	public static void RecordEnd(string reason, int exitCode)
	{
		try
		{
			RecordOperation($"Shutdown: process {ProcessId}, exit code {exitCode}. Reason: {reason}");
			IsEndRecorded = true;

			File.Delete(Path.Combine(AppDataService.EnsureFolderPath(), RunFileName));
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record the end." + Environment.NewLine
				+ ex);
		}
	}

	/// <summary>
	/// Records exception log to AppData.
	/// </summary>
	/// <param name="exception">Exception</param>
	/// <param name="source">Where the exception was caught</param>
	/// <remarks>
	/// This method shows nothing and so can be called from any thread at any time. The log file
	/// will be appended with new content as long as one day has not yet passed since last write.
	/// Otherwise, the log file will be overwritten. This method never throws an exception.
	/// </remarks>
	public static void RecordException(Exception exception, string source)
	{
		if (exception is null)
			return;

		var content = ComposeHeader() + Environment.NewLine
			+ $"Source: {source} Process: {ProcessId}" + Environment.NewLine
			+ exception.ToString() + Environment.NewLine + Environment.NewLine;

		RecordToAppData(ExceptionFileName, content, ExceptionCapacity);
	}

	/// <summary>
	/// Asks the user whether to record exception log to Desktop and does so if agreed.
	/// </summary>
	/// <param name="exception">Exception</param>
	/// <remarks>The exception is expected to have been recorded to AppData already.</remarks>
	public static void PromptException(Exception exception)
	{
		if (exception is null)
			throw new ArgumentNullException(nameof(exception));

		var content = ComposeHeader() + Environment.NewLine
			+ exception.ToString() + Environment.NewLine + Environment.NewLine;

		if (MessageBox.Show(
			LanguageService.RecordException,
			ProductInfo.Title,
			MessageBoxButton.YesNo,
			MessageBoxImage.Error,
			MessageBoxResult.Yes) != MessageBoxResult.Yes)
			return;

		RecordToDesktop(ExceptionFileName, content, 10);
	}

	#region Helper

	private static void RecordToAppData(string fileName, string content, int capacity = 1, bool keepsOld = false)
	{
		try
		{
			var appDataFolderPath = AppDataService.EnsureFolderPath();
			var appDataFilePath = Path.Combine(appDataFolderPath, fileName);

			lock (_lock)
			{
				UpdateText(appDataFilePath, content, capacity, keepsOld);
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record log to AppData." + Environment.NewLine
				+ ex);
		}
	}

	private static void RecordToDesktop(string fileName, string content, int capacity = 1)
	{
		try
		{
			var desktopFilePath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
				fileName);

			UpdateText(desktopFilePath, content, capacity);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to record log to Desktop." + Environment.NewLine
				+ ex);
		}
	}

	private static void SaveText(string filePath, string content)
	{
		using var sw = new StreamWriter(filePath, false, Encoding.UTF8); // BOM will be emitted.
		sw.Write(content);
	}

	private static void UpdateText(string filePath, string newContent, int capacity, bool keepsOld = false)
	{
		string oldContent = null;

		if ((1 < capacity) && File.Exists(filePath) && (keepsOld || (File.GetLastWriteTime(filePath) > DateTime.Now.AddDays(-1))))
		{
			using (var sr = new StreamReader(filePath, Encoding.UTF8))
				oldContent = sr.ReadToEnd();

			oldContent = TruncateSections(oldContent, HeaderStart, capacity - 1);
		}

		SaveText(filePath, oldContent + newContent);
	}

	private static string TruncateSections(string source, string sectionHeader, int sectionCount)
	{
		if (string.IsNullOrEmpty(sectionHeader))
			throw new ArgumentNullException(nameof(sectionHeader));
		if (sectionCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(sectionCount), sectionCount, "The count must be greater than 0.");

		if (string.IsNullOrEmpty(source))
			return string.Empty;

		var separator = Environment.NewLine + sectionHeader;
		int foundIndex = source.Length - 1;
		int startIndex = 0;

		for (int i = sectionCount; i > 0; i--)
		{
			foundIndex = source.LastIndexOf(separator, foundIndex, StringComparison.Ordinal);
			if (foundIndex < 0)
			{
				if (source.StartsWith(sectionHeader, StringComparison.Ordinal))
				{
					startIndex = 0;
				}
				break;
			}
			startIndex = foundIndex + Environment.NewLine.Length;
		}
		return source.Substring(startIndex);
	}

	#endregion
}
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Wifinian.Models.Update;

/// <summary>
/// Failure of an update with a reason short enough for the menu
/// </summary>
internal class UpdateException : Exception
{
	public UpdateException(string reason, Exception innerException = null) : base(reason, innerException)
	{ }
}

/// <summary>
/// Looks for, downloads and installs a new version of this fork
/// </summary>
internal class UpdateService
{
	private const string UserAgent = "Wifinian-Updater";

	private const int ManifestTimeout = 30; // sec
	private const int DownloadTimeout = 600; // sec
	private const int ManifestCeiling = 64 * 1024; // bytes
	internal const long DownloadCeiling = 100L * 1024 * 1024; // bytes

	private const string FolderName = "update";
	private const string InstallerFileName = "Wifinian.Installer.msi";
	private const string HelperFileName = "install.cmd";
	private const string ResultFileName = "result.txt";

	private const int ExitWaitCount = 300; // Seconds the helper waits for this process to exit

	private static readonly Lazy<HttpClient> _client = new(CreateClient);

	private static HttpClient CreateClient()
	{
		try
		{
			// The default value (SystemDefault) follows the operating system, which includes
			// TLS 1.2 and later, and so must be left as it is.
			if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
				ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to enable TLS 1.2." + Environment.NewLine
				+ ex);
		}

		// Cookies are neither kept nor sent. The time limits are given by cancellation for each request.
		var client = new HttpClient(new HttpClientHandler { UseCookies = false })
		{
			Timeout = Timeout.InfiniteTimeSpan
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
		return client;
	}

	/// <summary>
	/// Folder path of the downloaded installer
	/// </summary>
	/// <remarks>
	/// This is a subfolder because the installer removes the files (not the subfolders) of the
	/// folder of this application when upgrading.
	/// </remarks>
	internal static string FolderPath => Path.Combine(AppDataService.FolderPath, FolderName);

	#region Check

	/// <summary>
	/// Asks for the manifest of the latest release.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>Manifest and whether it offers a version newer than the running one</returns>
	/// <exception cref="UpdateException">The manifest could not be obtained or is refused.</exception>
	public async Task<(UpdateManifest manifest, bool isNewer)> CheckAsync(CancellationToken cancellationToken)
	{
		string json;
		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			cts.CancelAfter(TimeSpan.FromSeconds(ManifestTimeout));

			using var response = await _client.Value.GetAsync(UpdateVerifier.ManifestUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
			if (response.StatusCode == HttpStatusCode.NotFound)
				throw new UpdateException("no release published");
			if (!response.IsSuccessStatusCode)
				throw new UpdateException($"HTTP {(int)response.StatusCode}");

			using var ms = new MemoryStream();
			using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
			{
				await CopyAsync(stream, ms, ManifestCeiling, cts.Token).ConfigureAwait(false);
			}
			json = Encoding.UTF8.GetString(ms.ToArray());
		}
		catch (UpdateException)
		{
			throw;
		}
		catch (OperationCanceledException ex)
		{
			throw new UpdateException(cancellationToken.IsCancellationRequested ? "canceled" : "server timed out", ex);
		}
		catch (Exception ex)
		{
			throw new UpdateException("server unreachable", ex);
		}

		if (!UpdateManifest.TryParse(json, out UpdateManifest manifest, out string reason))
			throw new UpdateException(reason);

		if (!UpdateVerifier.CheckManifest(manifest, ProductInfo.Version, out bool isNewer, out reason))
			throw new UpdateException(reason);

		return (manifest, isNewer);
	}

	#endregion

	#region Download

	/// <summary>
	/// Downloads the installer which a manifest describes and verifies it.
	/// </summary>
	/// <param name="manifest">Manifest which passed the check</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>File path of the verified installer</returns>
	/// <exception cref="UpdateException">The installer could not be downloaded or is refused.</exception>
	/// <remarks>A file which is refused is deleted.</remarks>
	public async Task<string> DownloadAsync(UpdateManifest manifest, CancellationToken cancellationToken)
	{
		// The manifest is checked again because nothing else stands between it and the request.
		if (!UpdateVerifier.CheckManifest(manifest, ProductInfo.Version, out bool isNewer, out string reason))
			throw new UpdateException(reason);
		if (!isNewer)
			throw new UpdateException("version not newer");

		string filePath;
		try
		{
			Directory.CreateDirectory(FolderPath);
			filePath = Path.Combine(FolderPath, InstallerFileName);
			File.Delete(filePath);
		}
		catch (Exception ex)
		{
			throw new UpdateException("folder not writable", ex);
		}

		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			cts.CancelAfter(TimeSpan.FromSeconds(DownloadTimeout));

			using var response = await _client.Value.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
				throw new UpdateException($"HTTP {(int)response.StatusCode}");
			if (response.Content.Headers.ContentLength > DownloadCeiling)
				throw new UpdateException("download too large");

			using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
			using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
			await CopyAsync(stream, fs, DownloadCeiling, cts.Token).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			TryDelete(filePath);

			throw ex switch
			{
				UpdateException => ex,
				OperationCanceledException => new UpdateException(cancellationToken.IsCancellationRequested ? "canceled" : "download timed out", ex),
				IOException or UnauthorizedAccessException => new UpdateException("download not saved", ex),
				_ => new UpdateException("download failed", ex)
			};
		}

		// The hash is computed once and both the hash in the manifest and the signature are
		// checked against it.
		byte[] hash;
		try
		{
			using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			hash = await Task.Run(() => UpdateVerifier.ComputeHash(fs), cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			TryDelete(filePath);
			throw new UpdateException("download not readable", ex);
		}

		if (!UpdateVerifier.CheckFile(manifest, hash, out reason))
		{
			TryDelete(filePath);
			throw new UpdateException(reason);
		}

		// The version is read from the installer itself, which the signature covers, because
		// the version in the manifest is not signed.
		var productVersion = await Task.Run(() => InstallerPackage.GetProductVersion(filePath)).ConfigureAwait(false);
		if (!UpdateVerifier.CheckProduct(manifest, productVersion, ProductInfo.Version, out reason))
		{
			TryDelete(filePath);
			throw new UpdateException(reason);
		}

		return filePath;
	}

	private static async Task CopyAsync(Stream source, Stream destination, long ceiling, CancellationToken cancellationToken)
	{
		var buffer = new byte[81920];
		long total = 0;

		while (true)
		{
			var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
			if (read <= 0)
				return;

			total += read;
			if (total > ceiling)
				throw new UpdateException("download too large");

			await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
		}
	}

	private static void TryDelete(string filePath)
	{
		try
		{
			File.Delete(filePath);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to delete a file." + Environment.NewLine
				+ ex);
		}
	}

	#endregion

	#region Install

	private const string InstallValueName = "InstallDir"; // Written by the installer (Product.wxs)

	/// <summary>
	/// Registry key (under HKEY_CURRENT_USER) in which the installer remembers the installed folder
	/// </summary>
	internal static string InstallKeyName => $@"Software\{ProductInfo.Product}\Install";

	/// <summary>
	/// Gets the file path of the executable which the installer installs by default.
	/// </summary>
	private static string GetDefaultInstalledPath(string fileName) => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Programs", ProductInfo.Product, fileName);

	/// <summary>
	/// Determines whether a file is in the folder to which the installer installs: the one it
	/// remembers in the registry or, if this application has never been installed, its default.
	/// </summary>
	/// <param name="executablePath">File path of an executable</param>
	/// <remarks>This method never throws an exception.</remarks>
	internal static bool IsInstalledPath(string executablePath)
	{
		try
		{
			string folderPath;
			using (var key = Registry.CurrentUser.OpenSubKey(InstallKeyName))
			{
				folderPath = key?.GetValue(InstallValueName) as string;
			}

			var installedPath = string.IsNullOrWhiteSpace(folderPath)
				? GetDefaultInstalledPath(Path.GetFileName(executablePath))
				: Path.Combine(folderPath, Path.GetFileName(executablePath));

			return string.Equals(Path.GetFullPath(installedPath), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to find the installed folder." + Environment.NewLine
				+ ex);
			return false;
		}
	}

	/// <summary>
	/// Whether this process runs from the folder to which the installer installs
	/// </summary>
	/// <remarks>
	/// If not (e.g. a copy in another folder or the output of a build), an update is installed to
	/// the installed folder and the application is started from there afterward. This copy is
	/// left as it is.
	/// </remarks>
	public static bool IsRunningInstalled => IsInstalledPath(Assembly.GetExecutingAssembly().Location);

	/// <summary>
	/// Starts the helper which installs a verified installer after this process has exited and
	/// then starts this application again.
	/// </summary>
	/// <param name="installerPath">File path of the verified installer</param>
	/// <exception cref="UpdateException">The helper could not be started.</exception>
	/// <remarks>
	/// The helper is a command script run by cmd.exe without a window. It is not ended when this
	/// process exits. The caller must close this application after this method returns.
	/// </remarks>
	public void StartInstaller(string installerPath)
	{
		try
		{
			var executablePath = Assembly.GetExecutingAssembly().Location;
			var fileName = Path.GetFileName(executablePath);

			var script = ComposeHelper(Logger.ProcessId, fileName, installerPath,
				Path.Combine(FolderPath, ResultFileName),
				$@"HKCU\{InstallKeyName}", GetDefaultInstalledPath(fileName), executablePath);

			var helperPath = Path.Combine(FolderPath, HelperFileName);
			File.WriteAllText(helperPath, script, new UTF8Encoding(false));

			var system32Path = Environment.GetFolderPath(Environment.SpecialFolder.System);
			var startInfo = new ProcessStartInfo
			{
				FileName = Path.Combine(system32Path, "cmd.exe"),
				Arguments = $"/d /c \"\"{helperPath}\"\"",
				WorkingDirectory = system32Path, // Not a folder which the installer replaces
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using var process = Process.Start(startInfo);
		}
		catch (Exception ex)
		{
			throw new UpdateException("installer not started", ex);
		}
	}

	/// <summary>
	/// Composes the command script of the helper.
	/// </summary>
	/// <param name="processId">ID of this process</param>
	/// <param name="imageName">File name of the executable of this process</param>
	/// <param name="installerPath">File path of the installer</param>
	/// <param name="resultPath">File path to which the exit code of the installer is written</param>
	/// <param name="installKey">Registry key (with HKCU) in which the installer remembers the installed folder</param>
	/// <param name="installedPath">File path of the executable which the installer installs by default</param>
	/// <param name="executablePath">File path of the executable of this process</param>
	/// <remarks>
	/// The script waits until the process is gone and gives up without installing if that takes
	/// too long (the installer could not replace files in use). The application is started again
	/// whether the installer succeeded or not, because on failure the installer puts the old
	/// version back (it removes the old version within its transaction).
	/// When the installer succeeded, the installed executable is started: the one in the folder
	/// the installer remembers in the registry, then the one in the default folder, and only then
	/// the executable of this process, which is not the installed one if this process ran from
	/// another folder. When the installer failed, the executable of this process comes first.
	/// </remarks>
	internal static string ComposeHelper(int processId, string imageName, string installerPath, string resultPath, string installKey, string installedPath, string executablePath)
	{
		// A percent sign in a path would be read as a variable by cmd.exe.
		static string Escape(string path) => path.Replace("%", "%%");

		var buffer = new StringBuilder();
		var labelCount = 0;

		// Starts the executable if it exists. Blocks in parentheses are not used because
		// a parenthesis in the value of a variable would end them.
		void AppendStart(string path, string variable = null)
		{
			var label = $"next{++labelCount}";
			if (variable is not null)
				buffer.AppendLine($"if not defined {variable} goto {label}");
			buffer.AppendLine($"if not exist \"{path}\" goto {label}");
			buffer.AppendLine($"start \"\" \"{path}\"");
			buffer.AppendLine("goto end");
			buffer.AppendLine($":{label}");
		}

		// The folder which the installer remembers, which ends with a backslash, and the file name
		const string rememberedPath = "%folder%%image%";

		// The full path, so that no other program of the same name on the search path is taken.
		const string find = "\"%SystemRoot%\\System32\\find.exe\"";

		buffer.AppendLine("@echo off");
		buffer.AppendLine("chcp 65001 >nul"); // The paths are written in UTF-8.
		buffer.AppendLine("set /a count=0");
		buffer.AppendLine(":wait");
		buffer.AppendLine($"tasklist /FI \"PID eq {processId}\" /FI \"IMAGENAME eq {Escape(imageName)}\" /FO CSV /NH 2>nul | {find} \"\"\"{processId}\"\"\" >nul");
		buffer.AppendLine("if errorlevel 1 goto install");
		buffer.AppendLine("set /a count+=1");
		buffer.AppendLine($"if %count% geq {ExitWaitCount} goto giveup");
		buffer.AppendLine("ping -n 2 127.0.0.1 >nul"); // About a second (timeout command needs a console)
		buffer.AppendLine("goto wait");
		buffer.AppendLine(":install");
		buffer.AppendLine($"start \"\" /wait msiexec.exe /i \"{Escape(installerPath)}\" /passive /norestart");
		buffer.AppendLine("set code=%errorlevel%");
		buffer.AppendLine($">\"{Escape(resultPath)}\" echo %code%");

		// The installed folder is read after the installer has run because the first installation
		// writes it and the folder may have been chosen in the wizard of an earlier one.
		buffer.AppendLine($"set \"image={Escape(imageName)}\"");
		buffer.AppendLine("set \"folder=\"");
		buffer.AppendLine($"for /f \"tokens=2,*\" %%a in ('reg.exe query \"{Escape(installKey)}\" /v {InstallValueName} 2^>nul ^| {find} \"REG_SZ\"') do set \"folder=%%b\"");
		buffer.AppendLine("if not defined folder goto found");
		buffer.AppendLine("if not \"%folder:~-1%\"==\"\\\" set \"folder=%folder%\\\"");
		buffer.AppendLine(":found");

		// 0: installed, 3010: installed and a restart of the system is asked for
		buffer.AppendLine("if \"%code%\"==\"0\" goto installed");
		buffer.AppendLine("if \"%code%\"==\"3010\" goto installed");
		AppendStart(Escape(executablePath));
		buffer.AppendLine(":installed");
		AppendStart(rememberedPath, "folder");
		AppendStart(Escape(installedPath));
		AppendStart(Escape(executablePath));
		buffer.AppendLine("goto end");
		buffer.AppendLine(":giveup");
		buffer.AppendLine($">\"{Escape(resultPath)}\" echo not run because the application did not exit");
		buffer.AppendLine(":end");
		return buffer.ToString();
	}

	/// <summary>
	/// Reads and removes what the helper left on how the installer ended.
	/// </summary>
	/// <returns>Exit code of the installer or other text of the helper, null if there is none</returns>
	/// <remarks>This method never throws an exception.</remarks>
	public static string TakeInstallResult()
	{
		try
		{
			var resultPath = Path.Combine(FolderPath, ResultFileName);
			if (!File.Exists(resultPath))
				return null;

			var result = File.ReadAllText(resultPath).Trim();
			File.Delete(resultPath);

			// The installer is of no use anymore. The helper may still be running its last lines.
			TryDelete(Path.Combine(FolderPath, InstallerFileName));

			return string.IsNullOrEmpty(result) ? null : result;
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to read the result of the installer." + Environment.NewLine
				+ ex);
			return null;
		}
	}

	#endregion
}

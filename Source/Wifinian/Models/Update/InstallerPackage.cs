using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Wifinian.Models.Update;

/// <summary>
/// Reads an installer package (.msi) without running it
/// </summary>
internal static class InstallerPackage
{
	#region Win32

	[DllImport("Msi.dll", CharSet = CharSet.Unicode)]
	private static extern uint MsiOpenDatabase(
		string szDatabasePath,
		IntPtr szPersist,
		out uint phDatabase);

	[DllImport("Msi.dll", CharSet = CharSet.Unicode)]
	private static extern uint MsiDatabaseOpenView(
		uint hDatabase,
		string szQuery,
		out uint phView);

	[DllImport("Msi.dll")]
	private static extern uint MsiViewExecute(
		uint hView,
		uint hRecord);

	[DllImport("Msi.dll")]
	private static extern uint MsiViewFetch(
		uint hView,
		out uint phRecord);

	[DllImport("Msi.dll", CharSet = CharSet.Unicode)]
	private static extern uint MsiRecordGetString(
		uint hRecord,
		uint iField,
		StringBuilder szValueBuf,
		ref uint pcchValueBuf);

	[DllImport("Msi.dll")]
	private static extern uint MsiCloseHandle(uint hAny);

	private static readonly IntPtr MSIDBOPEN_READONLY = IntPtr.Zero;
	private const uint ERROR_SUCCESS = 0;

	#endregion

	private const int ValueCapacity = 256; // Characters, far more than a version takes

	/// <summary>
	/// Gets the version of the product (ProductVersion) which an installer package installs.
	/// </summary>
	/// <param name="filePath">File path of the installer package</param>
	/// <returns>Version as written in the package, null if it could not be read</returns>
	/// <remarks>
	/// The package is opened as a database for reading only. Nothing of it is run.
	/// This method never throws an exception.
	/// </remarks>
	public static string GetProductVersion(string filePath) =>
		GetProperty(filePath, "ProductVersion");

	private static string GetProperty(string filePath, string name)
	{
		uint database = 0;
		uint view = 0;
		uint record = 0;

		try
		{
			if (MsiOpenDatabase(filePath, MSIDBOPEN_READONLY, out database) != ERROR_SUCCESS)
				return null;

			// The name is a constant of this class and so is not escaped.
			if (MsiDatabaseOpenView(database, $"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'", out view) != ERROR_SUCCESS)
				return null;

			if (MsiViewExecute(view, 0) != ERROR_SUCCESS)
				return null;

			if (MsiViewFetch(view, out record) != ERROR_SUCCESS)
				return null;

			var buffer = new StringBuilder(ValueCapacity);
			var length = (uint)buffer.Capacity;
			if (MsiRecordGetString(record, 1, buffer, ref length) != ERROR_SUCCESS)
				return null;

			return buffer.ToString();
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to read an installer package." + Environment.NewLine
				+ ex);
			return null;
		}
		finally
		{
			if (record != 0)
				MsiCloseHandle(record);
			if (view != 0)
				MsiCloseHandle(view);
			if (database != 0)
				MsiCloseHandle(database);
		}
	}
}

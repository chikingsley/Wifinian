using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wifinian.Models.Measure;

/// <summary>
/// Detector of the time for which the user has not operated this machine
/// </summary>
internal static class IdleDetector
{
	[DllImport("User32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

	[StructLayout(LayoutKind.Sequential)]
	private struct LASTINPUTINFO
	{
		public uint cbSize;
		public uint dwTime;
	}

	/// <summary>
	/// Gets the time elapsed since the last input by the user.
	/// </summary>
	/// <returns>Elapsed time if successfully obtained, zero otherwise</returns>
	/// <remarks>This method never throws an exception.</remarks>
	public static TimeSpan GetIdleTime()
	{
		try
		{
			var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
			if (!GetLastInputInfo(ref info))
				return TimeSpan.Zero;

			return GetElapsedTime(unchecked((uint)Environment.TickCount), info.dwTime);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to get idle time." + Environment.NewLine
				+ ex);
			return TimeSpan.Zero;
		}
	}

	/// <summary>
	/// Gets the time elapsed between two tick counts which wrap around.
	/// </summary>
	internal static TimeSpan GetElapsedTime(uint currentTick, uint lastTick) =>
		TimeSpan.FromMilliseconds(unchecked(currentTick - lastTick));
}
using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Wifinian.Models.History;
using Wifinian.Models.Wlan;

namespace Wifinian.Models.Measure;

/// <summary>
/// Connection of an interface as the system reports it at a moment
/// </summary>
/// <remarks>
/// This is queried each time it is needed and is never kept. The profiles shown by this app are
/// a copy made at the last load and can be older than the connection.
/// </remarks>
internal sealed class LiveConnection
{
	public Guid InterfaceId { get; set; }

	/// <summary>
	/// Whether the interface is connected (not connecting, not disconnecting)
	/// </summary>
	public bool IsConnected { get; set; }

	public string ProfileName { get; set; }

	/// <summary>
	/// SSID in hexadecimal string (null if unknown)
	/// </summary>
	public string Ssid { get; set; }

	/// <summary>
	/// BSSID in colon-delimited hexadecimal string (null if unknown)
	/// </summary>
	public string Bssid { get; set; }

	/// <summary>
	/// Frequency band (GHz, 0 if unknown)
	/// </summary>
	public float Band { get; set; }

	public int Channel { get; set; }

	/// <summary>
	/// Signal quality (0-100)
	/// </summary>
	public int Signal { get; set; }

	/// <summary>
	/// Link rate (Mbps, 0 if unknown)
	/// </summary>
	public int LinkRate { get; set; }

	/// <summary>
	/// Whether the interface is connected by the specified profile
	/// </summary>
	public bool IsOn(string profileName) =>
		IsConnected
		&& !string.IsNullOrEmpty(profileName)
		&& string.Equals(ProfileName, profileName, StringComparison.Ordinal);

	/// <summary>
	/// Name of what the interface is on, for a sentence
	/// </summary>
	public string Describe() =>
		(IsConnected && !string.IsNullOrEmpty(ProfileName)) ? ProfileName : "no network";
}

/// <summary>
/// Whether a speed measurement can start on the network the interface is on
/// </summary>
internal enum IdentityStart
{
	/// <summary>
	/// The network is known and is the expected one if any
	/// </summary>
	Use = 0,

	/// <summary>
	/// The interface is not connected (or is still connecting)
	/// </summary>
	NotConnected,

	/// <summary>
	/// The interface is connected to a network other than the expected one
	/// </summary>
	OtherNetwork
}

/// <summary>
/// What happened to the connection during a speed measurement
/// </summary>
internal enum IdentityEnd
{
	Same = 0,

	/// <summary>
	/// Another access point of the same SSID (the measurement is still of the network)
	/// </summary>
	Roamed,

	/// <summary>
	/// Another SSID
	/// </summary>
	Changed,

	Disconnected
}

/// <summary>
/// How waiting for an interface to be on a network ended
/// </summary>
internal enum TargetWait
{
	OnTarget = 0,

	/// <summary>
	/// The system cannot be asked and so the profiles as loaded are to be relied on
	/// </summary>
	Unknown,

	TimedOut,
	Canceled
}

/// <summary>
/// Which network a speed measurement is of
/// </summary>
/// <remarks>
/// A measurement must be recorded for the network the interface was actually on. The profiles
/// loaded by this app can show the previous network as connected for a while after a switch
/// (a load is skipped while another one is running), and a measurement labelled from them goes
/// into the history of the wrong network and is not seen by the check of the switch.
/// </remarks>
internal static class MeasureIdentity
{
	/// <summary>
	/// Decides which profile a measurement about to start is of.
	/// </summary>
	/// <param name="cachedName">Name of the profile shown as connected in the profiles as loaded (null if none)</param>
	/// <param name="live">Connection of the interface queried just now (null if it cannot be queried)</param>
	/// <param name="expectedName">Name of the profile which the measurement is for (null for whichever is connected)</param>
	/// <returns>
	/// Whether to start, name of the profile to record and whether the profiles as loaded are
	/// older than the connection and so are to be loaded again
	/// </returns>
	/// <remarks>The connection queried always wins over the profiles as loaded.</remarks>
	public static (IdentityStart status, string name, bool isStale) ResolveStart(string cachedName, LiveConnection live, string expectedName = null)
	{
		if (live is null)
		{
			// The system cannot be asked (netsh, a failed query): the profiles are all there is.
			if (string.IsNullOrEmpty(cachedName))
				return (IdentityStart.NotConnected, null, false);

			if ((expectedName is not null) && !string.Equals(cachedName, expectedName, StringComparison.Ordinal))
				return (IdentityStart.OtherNetwork, cachedName, false);

			return (IdentityStart.Use, cachedName, false);
		}

		var isStale = !string.Equals(cachedName ?? string.Empty,
			live.IsConnected ? (live.ProfileName ?? string.Empty) : string.Empty,
			StringComparison.Ordinal);

		if (!live.IsConnected || string.IsNullOrEmpty(live.ProfileName))
			return (IdentityStart.NotConnected, null, isStale);

		if ((expectedName is not null) && !string.Equals(live.ProfileName, expectedName, StringComparison.Ordinal))
			return (IdentityStart.OtherNetwork, live.ProfileName, isStale);

		return (IdentityStart.Use, live.ProfileName, isStale);
	}

	/// <summary>
	/// Makes the connection of a profile as loaded, for when the system cannot be asked.
	/// </summary>
	/// <param name="profile">Profile shown as connected (null if none)</param>
	public static LiveConnection FromProfile(ProfileItem profile, Guid interfaceId)
	{
		if (profile is null)
			return new LiveConnection { InterfaceId = interfaceId };

		return new LiveConnection
		{
			InterfaceId = profile.InterfaceId,
			IsConnected = profile.IsConnected,
			ProfileName = profile.Name,
			Bssid = profile.Bssid,
			Band = profile.Band,
			Channel = profile.Channel,
			Signal = profile.Signal,
			LinkRate = profile.LinkRate
		};
	}

	/// <summary>
	/// Creates the sample of a measurement about to start from the connection queried just now.
	/// </summary>
	/// <param name="live">Connection of the interface</param>
	/// <param name="cached">Profile of the same name as loaded, which supplies only what the connection does not tell (null if none)</param>
	public static SpeedSample CreateSample(LiveConnection live, ProfileItem cached, SpeedTrigger trigger, DateTime utcNow)
	{
		if (live is null)
			throw new ArgumentNullException(nameof(live));

		// The values as loaded are of the same radio only if they are of the same BSS.
		var isSameBss = (cached is not null)
			&& string.Equals(cached.Name, live.ProfileName, StringComparison.Ordinal)
			&& (string.IsNullOrEmpty(live.Bssid)
				|| string.Equals(cached.Bssid, live.Bssid, StringComparison.OrdinalIgnoreCase));

		var hasRadio = (live.Band > 0F) || (live.Channel > 0);
		var linkRate = (live.LinkRate > 0) ? live.LinkRate : (isSameBss ? cached.LinkRate : 0);

		return new SpeedSample
		{
			Time = utcNow,
			InterfaceId = live.InterfaceId,
			Profile = live.ProfileName,
			Bssid = !string.IsNullOrEmpty(live.Bssid) ? live.Bssid : ((isSameBss ? cached.Bssid : null) ?? string.Empty),
			Band = hasRadio ? live.Band : (isSameBss ? cached.Band : 0F),
			Channel = hasRadio ? live.Channel : (isSameBss ? cached.Channel : 0),
			Signal = (live.Signal > 0) ? live.Signal : (isSameBss ? cached.Signal : 0),
			LinkRate = (linkRate > 0) ? linkRate : null,
			Trigger = trigger
		};
	}

	/// <summary>
	/// Compares the connection at the end of a measurement with that at its start.
	/// </summary>
	/// <remarks>
	/// Networks are compared by SSID where both are known and by profile name otherwise. Moving
	/// to another access point of the same SSID is roaming and the measurement stays of the network.
	/// </remarks>
	public static IdentityEnd ResolveEnd(LiveConnection start, LiveConnection end)
	{
		if (start is null)
			throw new ArgumentNullException(nameof(start));

		if (end is not { IsConnected: true })
			return IdentityEnd.Disconnected;

		var isSameNetwork = (!string.IsNullOrEmpty(start.Ssid) && !string.IsNullOrEmpty(end.Ssid))
			? string.Equals(start.Ssid, end.Ssid, StringComparison.OrdinalIgnoreCase)
			: string.Equals(start.ProfileName, end.ProfileName, StringComparison.Ordinal);
		if (!isSameNetwork)
			return IdentityEnd.Changed;

		if (!string.IsNullOrEmpty(start.Bssid)
			&& !string.IsNullOrEmpty(end.Bssid)
			&& !string.Equals(start.Bssid, end.Bssid, StringComparison.OrdinalIgnoreCase))
			return IdentityEnd.Roamed;

		return IdentityEnd.Same;
	}

	/// <summary>
	/// Reflects what happened to the connection during a measurement to its sample.
	/// </summary>
	/// <returns>What happened</returns>
	/// <remarks>
	/// If the interface left the network, the result says nothing on either network and so
	/// the sample is made invalid (aborted) with the reason in its detail. Roaming is noted
	/// after the detail, which keeps the beginning of the detail as the test left it.
	/// </remarks>
	public static IdentityEnd ApplyEnd(SpeedSample sample, LiveConnection start, LiveConnection end)
	{
		if (sample is null)
			throw new ArgumentNullException(nameof(sample));

		var status = ResolveEnd(start, end);

		switch (status)
		{
			case IdentityEnd.Disconnected:
				sample.Result = SpeedResult.Aborted;
				sample.Detail = Join($"Network changed during the test: {start.ProfileName} was disconnected", sample.Detail);
				break;

			case IdentityEnd.Changed:
				sample.Result = SpeedResult.Aborted;
				sample.Detail = Join($"Network changed during the test: {start.ProfileName} -> {end.Describe()}", sample.Detail);
				break;

			case IdentityEnd.Roamed:
				sample.Detail = Join(sample.Detail, $"{RoamNote}{start.Bssid} -> {end.Bssid}");
				break;
		}
		return status;

		static string Join(string first, string second) =>
			string.IsNullOrWhiteSpace(first) ? second
			: string.IsNullOrWhiteSpace(second) ? first
			: $"{first}; {second}";
	}

	/// <summary>
	/// Beginning of the note of roaming in the detail of a measurement
	/// </summary>
	internal const string RoamNote = "Roamed during the test: ";

	/// <summary>
	/// Waits until an interface is connected by the specified profile.
	/// </summary>
	/// <param name="query">Function to query the connection of the interface (null if it cannot be queried)</param>
	/// <param name="expectedName">Name of the profile</param>
	/// <param name="timeout">Time to wait</param>
	/// <param name="interval">Interval of queries</param>
	/// <param name="isCanceled">Function to tell that waiting is no longer necessary</param>
	/// <param name="onWaiting">Action called once, with the connection found, when waiting begins</param>
	/// <param name="delay">Function to wait for an interval (for test)</param>
	/// <param name="isExpired">Function to tell that the time to wait is over by the clock</param>
	/// <remarks>
	/// <para>The first query is made immediately. This method never throws an exception.</para>
	/// <para>The time to wait is counted by the intervals waited, which is less than the time
	/// elapsed because the queries take time as well. Where the wait must end with something
	/// which expires by the clock, the clock is to be given: otherwise that thing is gone
	/// before the intervals add up to the time, and the wait would end as canceled instead of
	/// timed out.</para>
	/// </remarks>
	public static async Task<TargetWait> WaitForTargetAsync(
		Func<LiveConnection> query,
		string expectedName,
		TimeSpan timeout,
		TimeSpan interval,
		Func<bool> isCanceled = null,
		Action<LiveConnection> onWaiting = null,
		Func<TimeSpan, Task> delay = null,
		Func<bool> isExpired = null)
	{
		try
		{
			delay ??= Task.Delay;

			var waited = TimeSpan.Zero;
			var isWaiting = false;

			while (true)
			{
				if (isCanceled?.Invoke() is true)
					return TargetWait.Canceled;

				if (isExpired?.Invoke() is true)
					return TargetWait.TimedOut;

				var live = query?.Invoke();
				if (live is null)
					return TargetWait.Unknown;

				if (live.IsOn(expectedName))
					return TargetWait.OnTarget;

				if (waited >= timeout)
					return TargetWait.TimedOut;

				if (!isWaiting)
				{
					isWaiting = true;
					onWaiting?.Invoke(live);
				}

				await delay(interval).ConfigureAwait(false);
				waited += interval;
			}
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to wait for the network to be connected." + Environment.NewLine
				+ ex);
			return TargetWait.Unknown;
		}
	}
}

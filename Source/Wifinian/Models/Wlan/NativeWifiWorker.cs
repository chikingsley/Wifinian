using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedNativeWifi;

namespace Wifinian.Models.Wlan;

internal class NativeWifiWorker : IWlanWorker
{
	private readonly NativeWifiPlayer _player;

	public NativeWifiWorker()
	{
		try
		{
			_player = new NativeWifiPlayer();
		}
		catch (Exception ex)
		{
			Debug.WriteLine("Failed to start NativeWifiPlayer." + Environment.NewLine
				+ ex);
		}
	}

	public bool IsWorkable => (_player is not null);

	#region Dispose

	private bool _disposed = false;

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (_disposed)
			return;

		if (disposing)
		{
			_player?.Dispose();
		}

		_disposed = true;
	}

	#endregion

	public event EventHandler NetworkRefreshed
	{
		add { if (IsWorkable) { _player.NetworkRefreshed += value; } }
		remove { if (IsWorkable) { _player.NetworkRefreshed -= value; } }
	}

	public event EventHandler<AvailabilityChangedEventArgs> AvailabilityChanged
	{
		add { if (IsWorkable) { _player.AvailabilityChanged += value; } }
		remove { if (IsWorkable) { _player.AvailabilityChanged -= value; } }
	}

	public event EventHandler<InterfaceChangedEventArgs> InterfaceChanged
	{
		add { if (IsWorkable) { _player.InterfaceChanged += value; } }
		remove { if (IsWorkable) { _player.InterfaceChanged -= value; } }
	}

	public event EventHandler<ConnectionChangedEventArgs> ConnectionChanged
	{
		add { if (IsWorkable) { _player.ConnectionChanged += value; } }
		remove { if (IsWorkable) { _player.ConnectionChanged -= value; } }
	}

	public event EventHandler<ProfileChangedEventArgs> ProfileChanged
	{
		add { if (IsWorkable) { _player.ProfileChanged += value; } }
		remove { if (IsWorkable) { _player.ProfileChanged -= value; } }
	}

	public event EventHandler<SignalQualityChangedEventArgs> SignalQualityChanged
	{
		add { if (IsWorkable) { _player.SignalQualityChanged += value; } }
		remove { if (IsWorkable) { _player.SignalQualityChanged -= value; } }
	}

	#region Scan networks

	public Task ScanNetworkAsync(TimeSpan timeout)
	{
		if (!IsWorkable)
			return Task.CompletedTask;

		return _player.ScanNetworksAsync(timeout, CancellationToken.None);
	}

	#endregion

	#region Get profiles

	public bool IncludesRadioDetails { get; set; } = true;

	public async Task<IEnumerable<ProfileItem>> GetProfilesAsync()
	{
		if (!IsWorkable)
			return Array.Empty<NativeWifiProfileItem>();

		var includesRadioDetails = IncludesRadioDetails;

		return await Task.Run(() =>
		{
			var profilePacks = _player.EnumerateProfileRadios().ToArray();

			var pairs = profilePacks.Select(x => (item: new NativeWifiProfileItem(
				name: x.Name,
				interfaceId: x.InterfaceInfo.Id,
				interfaceDescription: x.InterfaceInfo.Description,
				profileType: x.ProfileType,
				document: x.Document,
				position: x.Position,
				isRadioOn: x.IsRadioOn,
				isConnected: x.IsConnected,
				protocol: x.PhyType.ToProtocolName(),
				signal: ((x.SignalQuality is >= 0 and <= 100) ? x.SignalQuality : x.LinkQuality),
				band: x.Band,
				channel: x.Channel)
			{
				// The state comes with the profiles and so requires no additional call.
				IsInterfaceBusy = IsBusy(x.InterfaceInfo.State)
			}, ssid: FormatSsid(x.Document.Ssid))).ToArray();

			var items = pairs.Select(x => x.item).ToArray();

			if (includesRadioDetails)
				FillRadioDetails(pairs);

			return items;
		}).ConfigureAwait(false);
	}

	/// <summary>
	/// Determines whether an interface is in the middle of connection or disconnection.
	/// </summary>
	internal static bool IsBusy(InterfaceState state) => state
		is InterfaceState.Associating
		or InterfaceState.Authenticating
		or InterfaceState.Discovering
		or InterfaceState.Disconnecting;

	/// <summary>
	/// Entry of a BSS (a radio of an access point) in range
	/// </summary>
	internal sealed class BssEntry
	{
		public Guid InterfaceId { get; set; }

		/// <summary>
		/// SSID in hexadecimal string
		/// </summary>
		public string Ssid { get; set; }

		/// <summary>
		/// BSSID in colon-delimited hexadecimal string
		/// </summary>
		public string Bssid { get; set; }

		public string Protocol { get; set; }
		public int Rssi { get; set; }
		public int LinkQuality { get; set; }
		public float Band { get; set; }
		public int Channel { get; set; }
	}

	/// <summary>
	/// Selects the BSS entry whose values are shown for a profile.
	/// </summary>
	/// <param name="entries">BSS entries in range</param>
	/// <param name="interfaceId">Interface ID of the profile</param>
	/// <param name="ssid">SSID of the profile in hexadecimal string</param>
	/// <param name="connectedBssid">BSSID which the interface is associated with if the profile is connected, null otherwise</param>
	/// <returns>
	/// For the connected profile, the entry of the BSS which the interface is associated with.
	/// Otherwise, the entry of the strongest BSS of the SSID. Null if there is no such entry.
	/// </returns>
	/// <remarks>
	/// An access point usually has a BSS for each band under the same SSID. Band, channel,
	/// protocol, BSSID and RSSI of a profile must all come from the same entry. Otherwise,
	/// for instance, the band of the strongest BSS (2.4 GHz) would be shown along with the BSSID
	/// of the BSS actually used (5 GHz).
	/// </remarks>
	internal static BssEntry SelectBss(IEnumerable<BssEntry> entries, Guid interfaceId, string ssid, string connectedBssid)
	{
		if (entries is null)
			return null;

		var subEntries = entries.Where(x => (x is not null) && (x.InterfaceId == interfaceId));

		if (!string.IsNullOrEmpty(connectedBssid))
		{
			// The entry of another BSS of the same SSID is never used for the connected profile.
			return subEntries.FirstOrDefault(x => string.Equals(x.Bssid, connectedBssid, StringComparison.OrdinalIgnoreCase));
		}

		if (string.IsNullOrEmpty(ssid))
			return null;

		return subEntries
			.Where(x => string.Equals(x.Ssid, ssid, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(x => x.LinkQuality)
			.ThenByDescending(x => x.Rssi)
			.FirstOrDefault();
	}

	/// <summary>
	/// Applies the values of a BSS entry to a profile so that all of them are of the same BSS.
	/// </summary>
	/// <param name="item">Profile</param>
	/// <param name="entry">BSS entry (null if there is no entry for the profile)</param>
	/// <param name="connectedBssid">BSSID which the interface is associated with if the profile is connected, null otherwise</param>
	/// <param name="connectedProtocol">Protocol of the connection if the profile is connected, null otherwise</param>
	internal static void ApplyBss(ProfileItem item, BssEntry entry, string connectedBssid, string connectedProtocol)
	{
		if (entry is not null)
		{
			item.Bssid = entry.Bssid;
			item.Band = entry.Band;
			item.Channel = entry.Channel;

			if (!string.IsNullOrEmpty(entry.Protocol))
				item.Protocol = entry.Protocol;

			// RSSI of the current association, if already got, is newer than that of the entry.
			if (item.Rssi == 0)
				item.Rssi = entry.Rssi;

			return;
		}

		if (!string.IsNullOrEmpty(connectedBssid))
		{
			// The list has no entry of the BSS in use (it is refreshed only by a scan). The band
			// and the channel got from another BSS of the SSID would be wrong and so are left
			// unknown until the entry appears.
			item.Bssid = connectedBssid;
			item.Band = 0F;
			item.Channel = 0;

			if (!string.IsNullOrEmpty(connectedProtocol))
				item.Protocol = connectedProtocol;

			return;
		}

		item.Bssid = null;
		item.Rssi = 0;
	}

	/// <summary>
	/// Fills BSSID, RSSI and link rate of profiles as far as they are available and makes band,
	/// channel and protocol those of the same BSS.
	/// </summary>
	/// <remarks>
	/// These values are supplementary. They may not be available (e.g. when access to location
	/// information is not permitted) and so any failure must not prevent loading profiles.
	/// </remarks>
	private void FillRadioDetails(IReadOnlyCollection<(NativeWifiProfileItem item, string ssid)> pairs)
	{
		if (pairs.Count == 0)
			return;

		if (!pairs.Any(x => x.item.IsConnected || (x.item.Signal > 0)))
			return;

		BssEntry[] entries;
		try
		{
			// A single list is used for all profiles so that they are of the same moment.
			entries = _player.EnumerateBssNetworks()
				.Select(x => new BssEntry
				{
					InterfaceId = x.InterfaceInfo.Id,
					Ssid = FormatSsid(x.Ssid),
					Bssid = FormatBssid(x.Bssid),
					Protocol = x.PhyType.ToProtocolName(),
					Rssi = x.Rssi,
					LinkQuality = x.LinkQuality,
					Band = x.Band,
					Channel = x.Channel
				})
				.Where(x => x.Bssid is not null)
				.ToArray();
		}
		catch (Exception ex)
		{
			Debug.WriteLine("Failed to enumerate BSS networks." + Environment.NewLine
				+ ex);

			// Without the list, the values got with the profiles are left as they are.
			return;
		}

		foreach (var (item, ssid) in pairs)
		{
			if (item.IsConnected)
			{
				string connectedBssid = null;
				string connectedProtocol = null;

				try
				{
					var (result, info) = _player.GetCurrentConnection(item.InterfaceId);
					if ((result == ActionResult.Success) && (info is not null) &&
						string.Equals(info.ProfileName, item.Name, StringComparison.Ordinal))
					{
						connectedBssid = FormatBssid(info.Bssid);
						connectedProtocol = info.PhyType.ToProtocolName();
						item.LinkRate = info.RxRate / 1000;
					}
				}
				catch (Exception ex)
				{
					Debug.WriteLine("Failed to get current connection." + Environment.NewLine
						+ ex);
				}

				// Without the BSSID in use, which BSS the values got with the profile are of is
				// not known and so nothing is changed.
				if (connectedBssid is null)
					continue;

				try
				{
					var (result, rssi) = _player.GetRssi(item.InterfaceId);
					if (result == ActionResult.Success)
						item.Rssi = rssi;
				}
				catch (Exception ex)
				{
					Debug.WriteLine("Failed to get RSSI." + Environment.NewLine
						+ ex);
				}

				ApplyBss(item, SelectBss(entries, item.InterfaceId, ssid, connectedBssid), connectedBssid, connectedProtocol);
			}
			else if (item.Signal > 0)
			{
				ApplyBss(item, SelectBss(entries, item.InterfaceId, ssid, null), null, null);
			}
		}
	}

	/// <summary>
	/// Formats SSID in hexadecimal string so that it can be compared regardless of encoding.
	/// </summary>
	internal static string FormatSsid(NetworkIdentifier id)
	{
		var bytes = id?.ToBytes();
		if (bytes is not { Length: > 0 })
			return null;

		return BitConverter.ToString(bytes);
	}

	/// <summary>
	/// Formats BSSID in colon-delimited hexadecimal string (e.g. 00:1A:2B:3C:4D:5E).
	/// </summary>
	/// <param name="id">BSSID</param>
	/// <returns>Formatted string if successfully formatted, null otherwise</returns>
	internal static string FormatBssid(NetworkIdentifier id)
	{
		var bytes = id?.ToBytes();
		if (bytes is not { Length: > 0 })
			return null;

		return BitConverter.ToString(bytes).Replace('-', ':');
	}

	#endregion

	#region Set/Rename/Delete profile

	public Task<bool> SetProfileOptionAsync(ProfileItem profileItem)
	{
		if (profileItem is not NativeWifiProfileItem item)
			throw new ArgumentException(nameof(profileItem));

		if (!IsWorkable)
			return Task.FromResult(false);

		return Task.Run(() => _player.SetProfile(item.InterfaceId, item.ProfileType, item.Xml, null, true));
	}

	public Task<bool> SetProfilePositionAsync(ProfileItem profileItem, int position)
	{
		var item = profileItem ?? throw new ArgumentNullException(nameof(profileItem));

		if (position < 0)
			throw new ArgumentOutOfRangeException(nameof(position), position, "The position must not be negative.");

		if (!IsWorkable)
			return Task.FromResult(false);

		return Task.Run(() => _player.SetProfilePosition(item.InterfaceId, item.Name, position));
	}

	public Task<bool> RenameProfileAsync(ProfileItem profileItem, string profileName)
	{
		var item = profileItem ?? throw new ArgumentNullException(nameof(profileItem));

		if (string.IsNullOrWhiteSpace(profileName))
			throw new ArgumentNullException(nameof(profileName));

		if (!IsWorkable)
			return Task.FromResult(false);

		return Task.Run(() => _player.RenameProfile(item.InterfaceId, item.Name, profileName));
	}

	public Task<bool> DeleteProfileAsync(ProfileItem profileItem)
	{
		var item = profileItem ?? throw new ArgumentNullException(nameof(profileItem));

		if (!IsWorkable)
			return Task.FromResult(false);

		return Task.Run(() => _player.DeleteProfile(item.InterfaceId, item.Name));
	}

	#endregion

	#region Connect/Disconnect

	public Task<bool> ConnectNetworkAsync(ProfileItem profileItem, TimeSpan timeout)
	{
		if (profileItem is not NativeWifiProfileItem item)
			throw new ArgumentException(nameof(profileItem));

		if (!IsWorkable)
			return Task.FromResult(false);

		return _player.ConnectNetworkAsync(item.InterfaceId, item.Name, item.BssType, timeout, CancellationToken.None);
	}

	public Task<bool> DisconnectNetworkAsync(ProfileItem profileItem, TimeSpan timeout)
	{
		var item = profileItem ?? throw new ArgumentNullException(nameof(profileItem));

		if (!IsWorkable)
			return Task.FromResult(false);

		return _player.DisconnectNetworkAsync(item.InterfaceId, timeout, CancellationToken.None);
	}

	#endregion
}
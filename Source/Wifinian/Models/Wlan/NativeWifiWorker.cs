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

			var items = profilePacks.Select(x => new NativeWifiProfileItem(
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
				channel: x.Channel)).ToArray();

			if (includesRadioDetails)
				FillRadioDetails(items);

			return items;
		}).ConfigureAwait(false);
	}

	/// <summary>
	/// Fills BSSID, RSSI and link rate of profiles as far as they are available.
	/// </summary>
	/// <remarks>
	/// These values are supplementary. They may not be available (e.g. when access to location
	/// information is not permitted) and so any failure must not prevent loading profiles.
	/// </remarks>
	private void FillRadioDetails(IReadOnlyCollection<NativeWifiProfileItem> items)
	{
		if (items.Count == 0)
			return;

		foreach (var item in items.Where(x => x.IsConnected))
		{
			try
			{
				var (result, info) = _player.GetCurrentConnection(item.InterfaceId);
				if ((result == ActionResult.Success) && (info is not null) &&
					string.Equals(info.ProfileName, item.Name, StringComparison.Ordinal))
				{
					item.Bssid = FormatBssid(info.Bssid);
					item.LinkRate = info.RxRate / 1000;
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine("Failed to get current connection." + Environment.NewLine
					+ ex);
			}

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
		}

		if (!items.Any(x => x is { IsConnected: false, Signal: > 0 }))
			return;

		try
		{
			var groups = _player.EnumerateAvailableNetworkGroups().ToArray();

			foreach (var item in items.Where(x => x is { IsConnected: false, Signal: > 0 }))
			{
				var bssNetwork = groups
					.Where(x => (x.InterfaceInfo.Id == item.InterfaceId)
						&& string.Equals(x.ProfileName, item.Name, StringComparison.Ordinal))
					.SelectMany(x => x.BssNetworks)
					.OrderByDescending(x => x.LinkQuality)
					.FirstOrDefault();
				if (bssNetwork is null)
					continue;

				item.Bssid = FormatBssid(bssNetwork.Bssid);
				item.Rssi = bssNetwork.Rssi;
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine("Failed to enumerate available network groups." + Environment.NewLine
				+ ex);
		}
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
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManagedNativeWifi;

using Wifinian.Models.Measure;

namespace Wifinian.Models.Wlan;

internal interface IWlanWorker : IDisposable
{
	bool IsWorkable { get; }

	event EventHandler NetworkRefreshed;
	event EventHandler<AvailabilityChangedEventArgs> AvailabilityChanged;
	event EventHandler<InterfaceChangedEventArgs> InterfaceChanged;
	event EventHandler<ConnectionChangedEventArgs> ConnectionChanged;
	event EventHandler<ProfileChangedEventArgs> ProfileChanged;
	event EventHandler<SignalQualityChangedEventArgs> SignalQualityChanged;

	Task ScanNetworkAsync(TimeSpan timeout);

	/// <summary>
	/// Whether supplementary values of radio (BSSID, RSSI, link rate) are filled when profiles
	/// are got
	/// </summary>
	/// <remarks>If this is false, getting profiles makes no call other than those of the original.</remarks>
	bool IncludesRadioDetails { get; set; }

	Task<IEnumerable<ProfileItem>> GetProfilesAsync();

	/// <summary>
	/// Queries the current connection of an interface from the system.
	/// </summary>
	/// <returns>Connection (not connected if the interface is not), null if it cannot be queried</returns>
	/// <remarks>
	/// This is a read-only query made at each call and so is never older than the call, unlike
	/// the profiles got before. This method never throws an exception.
	/// </remarks>
	LiveConnection GetConnection(Guid interfaceId);


	Task<bool> SetProfileOptionAsync(ProfileItem profileItem);
	Task<bool> SetProfilePositionAsync(ProfileItem profileItem, int position);
	Task<bool> RenameProfileAsync(ProfileItem profileItem, string profileName);
	Task<bool> DeleteProfileAsync(ProfileItem profileItem);

	Task<bool> ConnectNetworkAsync(ProfileItem profileItem, TimeSpan timeout);
	Task<bool> DisconnectNetworkAsync(ProfileItem profileItem, TimeSpan timeout);
}
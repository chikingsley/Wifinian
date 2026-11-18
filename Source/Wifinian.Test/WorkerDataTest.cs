using System;
using ManagedNativeWifi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models;
using Wifinian.Models.Wlan;

namespace Wifinian.Test;

[TestClass]
public class WorkerDataTest
{
	[TestMethod]
	public void TestFormatBssid()
	{
		Assert.IsNull(NativeWifiWorker.FormatBssid(null));
		Assert.IsNull(NativeWifiWorker.FormatBssid(new NetworkIdentifier(Array.Empty<byte>())));

		Assert.AreEqual("00:1A:2B:3C:4D:5E",
			NativeWifiWorker.FormatBssid(new NetworkIdentifier(new byte[] { 0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E })));
	}

	private static readonly Guid _interfaceId = new("11111111-2222-3333-4444-555555555555");

	/// <summary>
	/// BSS entries seen at the owner's place: each access point has a radio for each band.
	/// </summary>
	private static NativeWifiWorker.BssEntry[] CreateEntries() =>
	[
		new() { InterfaceId = _interfaceId, Ssid = "EXT", Bssid = "50:3D:D1:51:E1:D1", Protocol = "ac", Rssi = -58, LinkQuality = 83, Band = 5F, Channel = 36 },
		new() { InterfaceId = _interfaceId, Ssid = "EXT", Bssid = "50:3D:D1:51:E1:D0", Protocol = "n", Rssi = -56, LinkQuality = 85, Band = 2.4F, Channel = 11 },
		new() { InterfaceId = _interfaceId, Ssid = "OFFICE", Bssid = "0C:FE:7B:71:BC:59", Protocol = "ax", Rssi = -73, LinkQuality = 60, Band = 5F, Channel = 36 },
		new() { InterfaceId = Guid.NewGuid(), Ssid = "EXT", Bssid = "50:3D:D1:51:E1:D2", Protocol = "ax", Rssi = -30, LinkQuality = 100, Band = 6F, Channel = 5 },
	];

	[TestMethod]
	public void TestSelectBss()
	{
		var entries = CreateEntries();

		// The connected profile takes the BSS in use although another BSS of the SSID is stronger.
		var entry = NativeWifiWorker.SelectBss(entries, _interfaceId, "EXT", "50:3d:d1:51:e1:d1");
		Assert.AreEqual("50:3D:D1:51:E1:D1", entry.Bssid);
		Assert.AreEqual(5F, entry.Band);
		Assert.AreEqual(36, entry.Channel);

		// A profile not connected takes the strongest BSS of the SSID in the same interface.
		entry = NativeWifiWorker.SelectBss(entries, _interfaceId, "ext", null);
		Assert.AreEqual("50:3D:D1:51:E1:D0", entry.Bssid);
		Assert.AreEqual(2.4F, entry.Band);
		Assert.AreEqual(11, entry.Channel);

		entry = NativeWifiWorker.SelectBss(entries, _interfaceId, "OFFICE", null);
		Assert.AreEqual("0C:FE:7B:71:BC:59", entry.Bssid);

		// The BSS in use is not in the list: no other BSS takes its place.
		Assert.IsNull(NativeWifiWorker.SelectBss(entries, _interfaceId, "EXT", "50:3D:D1:51:E1:FF"));

		Assert.IsNull(NativeWifiWorker.SelectBss(entries, _interfaceId, "EXT2", null));
		Assert.IsNull(NativeWifiWorker.SelectBss(entries, _interfaceId, null, null));
		Assert.IsNull(NativeWifiWorker.SelectBss(null, _interfaceId, "EXT", null));
	}

	[TestMethod]
	public void TestApplyBssIsOfOneBss()
	{
		var entries = CreateEntries();

		// The values got with the profile are those of the strongest BSS (2.4 GHz, channel 11)
		// while the interface is associated with the 5 GHz one.
		var item = CreateProfileItem("EXT", _interfaceId, isConnected: true, signal: 83);
		item.Band = 2.4F;
		item.Channel = 11;
		item.Protocol = "n";
		item.Rssi = -58; // Of the current association

		const string connectedBssid = "50:3D:D1:51:E1:D1";
		NativeWifiWorker.ApplyBss(item, NativeWifiWorker.SelectBss(entries, _interfaceId, "EXT", connectedBssid), connectedBssid, "ac");

		Assert.AreEqual(connectedBssid, item.Bssid);
		Assert.AreEqual(5F, item.Band);
		Assert.AreEqual(36, item.Channel);
		Assert.AreEqual("ac", item.Protocol);
		Assert.AreEqual(-58, item.Rssi);

		// Not connected: everything is of the strongest BSS.
		item = CreateProfileItem("EXT", _interfaceId, isConnected: false, signal: 85);
		NativeWifiWorker.ApplyBss(item, NativeWifiWorker.SelectBss(entries, _interfaceId, "EXT", null), null, null);

		Assert.AreEqual("50:3D:D1:51:E1:D0", item.Bssid);
		Assert.AreEqual(2.4F, item.Band);
		Assert.AreEqual(11, item.Channel);
		Assert.AreEqual("n", item.Protocol);
		Assert.AreEqual(-56, item.Rssi);

		// The BSS in use has no entry: the band and the channel of another BSS are not shown.
		item = CreateProfileItem("EXT", _interfaceId, isConnected: true, signal: 83);
		item.Band = 2.4F;
		item.Channel = 11;
		NativeWifiWorker.ApplyBss(item, null, "50:3D:D1:51:E1:FF", "ac");

		Assert.AreEqual("50:3D:D1:51:E1:FF", item.Bssid);
		Assert.AreEqual(0F, item.Band);
		Assert.AreEqual(0, item.Channel);
		Assert.AreEqual("ac", item.Protocol);

		// Not connected and no entry: nothing of a BSS is known.
		item = CreateProfileItem("EXT2", _interfaceId, isConnected: false, signal: 40);
		item.Bssid = "50:3D:D1:51:E1:D0";
		item.Rssi = -56;
		NativeWifiWorker.ApplyBss(item, null, null, null);

		Assert.IsNull(item.Bssid);
		Assert.AreEqual(0, item.Rssi);
	}

	[TestMethod]
	public void TestProfileItemCopyKeepsRadioOfSameBss()
	{
		var oldItem = CreateProfileItem("EXT", _interfaceId, isConnected: true, signal: 83);
		oldItem.Bssid = "50:3D:D1:51:E1:D1";

		// The entry of the BSS in use is missing this time.
		var newItem = CreateProfileItem("EXT", _interfaceId, isConnected: true, signal: 80);
		newItem.Bssid = "50:3D:D1:51:E1:D1";
		newItem.Band = 0F;
		newItem.Channel = 0;

		oldItem.Copy(newItem);
		Assert.AreEqual(80, oldItem.Signal);
		Assert.AreEqual(5F, oldItem.Band);
		Assert.AreEqual(36, oldItem.Channel);

		// Another BSS: the unknown values are taken.
		newItem.Bssid = "50:3D:D1:51:E1:D0";
		oldItem.Copy(newItem);
		Assert.AreEqual(0F, oldItem.Band);
		Assert.AreEqual(0, oldItem.Channel);
		Assert.AreEqual("50:3D:D1:51:E1:D0", oldItem.Bssid);
	}

	[TestMethod]
	public void TestIsBusy()
	{
		Assert.IsTrue(NativeWifiWorker.IsBusy(InterfaceState.Associating));
		Assert.IsTrue(NativeWifiWorker.IsBusy(InterfaceState.Authenticating));
		Assert.IsTrue(NativeWifiWorker.IsBusy(InterfaceState.Discovering));
		Assert.IsTrue(NativeWifiWorker.IsBusy(InterfaceState.Disconnecting));
		Assert.IsFalse(NativeWifiWorker.IsBusy(InterfaceState.Connected));
		Assert.IsFalse(NativeWifiWorker.IsBusy(InterfaceState.Disconnected));
		Assert.IsFalse(NativeWifiWorker.IsBusy(InterfaceState.NotReady));
	}

	[TestMethod]
	public void TestProfileItemCopyRadioDetails()
	{
		var interfaceId = Guid.NewGuid();
		var oldItem = CreateProfileItem("Office", interfaceId, isConnected: false, signal: 40);
		Assert.IsNull(oldItem.Bssid);
		Assert.AreEqual(0, oldItem.Rssi);
		Assert.AreEqual(0, oldItem.LinkRate);

		var newItem = CreateProfileItem("Office", interfaceId, isConnected: true, signal: 70);
		newItem.Bssid = "00:1A:2B:3C:4D:5E";
		newItem.Rssi = -55;
		newItem.LinkRate = 866;

		oldItem.Copy(newItem);
		Assert.AreEqual("00:1A:2B:3C:4D:5E", oldItem.Bssid);
		Assert.AreEqual(-55, oldItem.Rssi);
		Assert.AreEqual(866, oldItem.LinkRate);

		// Unknown values clear the previous ones.
		oldItem.Copy(CreateProfileItem("Office", interfaceId, isConnected: false, signal: 0));
		Assert.IsNull(oldItem.Bssid);
		Assert.AreEqual(0, oldItem.Rssi);
		Assert.AreEqual(0, oldItem.LinkRate);
	}

	[TestMethod]
	public void TestSettingsHistoryRange()
	{
		var settings = Settings.Current; // Not initiated and so nothing is loaded or saved.

		var recordsHistory = settings.RecordsHistory;
		var sampleInterval = settings.SampleInterval;
		var historyRetentionDays = settings.HistoryRetentionDays;
		try
		{
			Assert.IsTrue(recordsHistory);
			Assert.AreEqual(60, sampleInterval);
			Assert.AreEqual(90, historyRetentionDays);

			settings.SampleInterval = 1;
			Assert.AreEqual(30, settings.SampleInterval);
			settings.SampleInterval = 1000;
			Assert.AreEqual(300, settings.SampleInterval);
			settings.SampleInterval = 120;
			Assert.AreEqual(120, settings.SampleInterval);

			settings.HistoryRetentionDays = 0;
			Assert.AreEqual(7, settings.HistoryRetentionDays);
			settings.HistoryRetentionDays = 1000;
			Assert.AreEqual(365, settings.HistoryRetentionDays);
			settings.HistoryRetentionDays = 30;
			Assert.AreEqual(30, settings.HistoryRetentionDays);
		}
		finally
		{
			settings.RecordsHistory = recordsHistory;
			settings.SampleInterval = sampleInterval;
			settings.HistoryRetentionDays = historyRetentionDays;
		}
	}

	private static ProfileItem CreateProfileItem(string name, Guid interfaceId, bool isConnected, int signal) =>
		new ProfileItem(
			name: name,
			interfaceId: interfaceId,
			interfaceName: "Wi-Fi",
			interfaceDescription: "Test Adapter",
			authentication: AuthenticationMethod.WPA2_Personal,
			encryption: EncryptionType.AES,
			isAutoConnectEnabled: true,
			isAutoSwitchEnabled: true,
			position: 0,
			isRadioOn: true,
			isConnected: isConnected,
			protocol: "802.11ac",
			signal: signal,
			band: 5F,
			channel: 36);
}
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
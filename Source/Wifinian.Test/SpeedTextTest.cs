using System;
using System.Linq;
using System.Threading.Tasks;
using ManagedNativeWifi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Wlan;
using Wifinian.ViewModels;

namespace Wifinian.Test;

[TestClass]
public class SpeedTextTest
{
	private static readonly DateTime _now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

	[ClassInitialize]
	public static async Task InitializeAsync(TestContext context)
	{
		await LanguageService.InitializeAsync();
	}

	[TestMethod]
	public void TestSpeedTextEmpty()
	{
		var item = CreateProfileItem();
		Assert.AreEqual(string.Empty, ProfileItemViewModel.BuildSpeedText(item, _now));
		Assert.IsNull(ProfileItemViewModel.BuildSpeedToolTip(item, _now));

		// Older than 14 days
		SetSpeed(item, TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1), SpeedResult.Ok, 70, 12, 18);
		Assert.AreEqual(string.Empty, ProfileItemViewModel.BuildSpeedText(item, _now));
		Assert.IsNull(ProfileItemViewModel.BuildSpeedToolTip(item, _now));

		// No download speed
		SetSpeed(item, TimeSpan.FromMinutes(5), SpeedResult.Ok, null, null, null);
		Assert.AreEqual(string.Empty, ProfileItemViewModel.BuildSpeedText(item, _now));
	}

	[TestMethod]
	public void TestSpeedTextOk()
	{
		var item = CreateProfileItem();

		SetSpeed(item, TimeSpan.FromMinutes(5), SpeedResult.Ok, 70.4, 12.2, 18.3);
		Assert.AreEqual("70 / 12 Mbps  18 ms  5m", ProfileItemViewModel.BuildSpeedText(item, _now));

		// Numbers below 10 have one decimal place.
		SetSpeed(item, TimeSpan.FromSeconds(30), SpeedResult.Ok, 8.26, 0.94, 9.5);
		Assert.AreEqual("8.3 / 0.9 Mbps  9.5 ms  now", ProfileItemViewModel.BuildSpeedText(item, _now));

		// Upload speed and latency are optional.
		SetSpeed(item, TimeSpan.FromMinutes(59), SpeedResult.Ok, 100, null, null);
		Assert.AreEqual("100 Mbps  59m", ProfileItemViewModel.BuildSpeedText(item, _now));

		SetSpeed(item, TimeSpan.FromHours(3), SpeedResult.Ok, 100, null, 20);
		Assert.AreEqual("100 Mbps  20 ms  3h", ProfileItemViewModel.BuildSpeedText(item, _now));
	}

	[TestMethod]
	public void TestSpeedTextAge()
	{
		var item = CreateProfileItem();

		SetSpeed(item, TimeSpan.FromHours(24), SpeedResult.Ok, 50, 10, null);
		Assert.AreEqual("50 / 10 Mbps  24h", ProfileItemViewModel.BuildSpeedText(item, _now));

		// Older than 24 hours
		SetSpeed(item, TimeSpan.FromHours(47), SpeedResult.Ok, 50, 10, null);
		Assert.AreEqual("~50 / 10 Mbps  47h", ProfileItemViewModel.BuildSpeedText(item, _now));

		SetSpeed(item, TimeSpan.FromHours(48), SpeedResult.Ok, 50, 10, null);
		Assert.AreEqual("~50 / 10 Mbps  2d", ProfileItemViewModel.BuildSpeedText(item, _now));

		SetSpeed(item, TimeSpan.FromDays(14), SpeedResult.Ok, 50, 10, null);
		Assert.AreEqual("~50 / 10 Mbps  14d", ProfileItemViewModel.BuildSpeedText(item, _now));
	}

	[TestMethod]
	public void TestSpeedTextNoInternet()
	{
		var item = CreateProfileItem();

		SetSpeed(item, TimeSpan.FromMinutes(2), SpeedResult.NoInternet, 0, null, null);
		Assert.AreEqual($"{LanguageService.NoInternet}  2m", ProfileItemViewModel.BuildSpeedText(item, _now));

		SetSpeed(item, TimeSpan.FromHours(30), SpeedResult.CaptivePortal, 0, null, null);
		Assert.AreEqual($"{LanguageService.SignInRequired}  30h", ProfileItemViewModel.BuildSpeedText(item, _now));
	}

	[TestMethod]
	public void TestSpeedTextMeasuring()
	{
		var item = CreateProfileItem();
		item.IsMeasuring = true;
		Assert.AreEqual(LanguageService.Testing, ProfileItemViewModel.BuildSpeedText(item, _now));

		SetSpeed(item, TimeSpan.FromMinutes(5), SpeedResult.Ok, 70, 12, 18);
		Assert.AreEqual(LanguageService.Testing, ProfileItemViewModel.BuildSpeedText(item, _now));

		item.IsMeasuring = false;
		Assert.AreEqual("70 / 12 Mbps  18 ms  5m", ProfileItemViewModel.BuildSpeedText(item, _now));
	}

	[TestMethod]
	public void TestSpeedToolTip()
	{
		var item = CreateProfileItem();
		SetSpeed(item, TimeSpan.FromMinutes(5), SpeedResult.Ok, 70, 12, 18);
		item.TypicalDownMbps = 65.25;
		item.SpeedSampleCount = 4;
		item.Bssid = "00:1A:2B:3C:4D:5E";

		var lines = ProfileItemViewModel.BuildSpeedToolTip(item, _now).Split(new[] { Environment.NewLine }, StringSplitOptions.None);
		Assert.AreEqual(3, lines.Length);
		Assert.AreEqual("Typical 65.3 Mbps over 4 tests", lines[0]);
		Assert.AreEqual($"Last tested {item.LastMeasuredTime.ToLocalTime():g}", lines[1]);
		Assert.AreEqual("BSSID 00:1A:2B:3C:4D:5E", lines[2]);

		// Without typical speed and BSSID
		item.TypicalDownMbps = null;
		item.SpeedSampleCount = 0;
		item.Bssid = null;
		SetSpeed(item, TimeSpan.FromMinutes(5), SpeedResult.NoInternet, 0, null, null);

		lines = ProfileItemViewModel.BuildSpeedToolTip(item, _now).Split(new[] { Environment.NewLine }, StringSplitOptions.None);
		Assert.AreEqual(1, lines.Length);
		Assert.IsTrue(lines[0].StartsWith("Last tested ", StringComparison.Ordinal));
	}

	[TestMethod]
	public void TestAvoidedNotCopied()
	{
		var oldItem = CreateProfileItem();
		Assert.IsFalse(oldItem.IsAvoided);

		oldItem.IsAvoided = true;
		oldItem.Copy(CreateProfileItem(oldItem.InterfaceId));
		Assert.IsTrue(oldItem.IsAvoided);
	}

	[TestMethod]
	public void TestSettingsAvoidedProfiles()
	{
		var settings = Settings.Current; // Not initiated and so nothing is loaded or saved.

		var avoidedProfiles = settings.AvoidedProfiles;
		try
		{
			Assert.IsNotNull(avoidedProfiles);

			settings.AvoidedProfiles = new[] { "Guest", "EXT2" };
			Assert.IsTrue(settings.AvoidedProfiles.SequenceEqual(new[] { "Guest", "EXT2" }));

			settings.AvoidedProfiles = null;
			Assert.IsNotNull(settings.AvoidedProfiles);
			Assert.AreEqual(0, settings.AvoidedProfiles.Length);
		}
		finally
		{
			settings.AvoidedProfiles = avoidedProfiles;
		}
	}

	private static void SetSpeed(ProfileItem item, TimeSpan elapsed, SpeedResult result, double? down, double? up, double? latency)
	{
		item.LastMeasuredTime = _now - elapsed;
		item.LastSpeedResult = result;
		item.LastDownMbps = down;
		item.LastUpMbps = up;
		item.LastLatencyMs = latency;
	}

	private static ProfileItem CreateProfileItem(Guid? interfaceId = null) =>
		new ProfileItem(
			name: "Office",
			interfaceId: interfaceId ?? Guid.NewGuid(),
			interfaceName: "Wi-Fi",
			interfaceDescription: "Test Adapter",
			authentication: AuthenticationMethod.WPA2_Personal,
			encryption: EncryptionType.AES,
			isAutoConnectEnabled: true,
			isAutoSwitchEnabled: true,
			position: 0,
			isRadioOn: true,
			isConnected: true,
			protocol: "802.11ac",
			signal: 70,
			band: 5F,
			channel: 36);
}
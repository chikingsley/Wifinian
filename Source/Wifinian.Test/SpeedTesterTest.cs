using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedNativeWifi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Measure;
using Wifinian.Models.Wlan;

namespace Wifinian.Test;

[TestClass]
public class SpeedTesterTest
{
	#region Statistics

	[TestMethod]
	public void TestGetMedian()
	{
		Assert.IsNull(SpeedTester.GetMedian(null));
		Assert.IsNull(SpeedTester.GetMedian(Array.Empty<double>()));
		Assert.AreEqual(7D, SpeedTester.GetMedian(new[] { 7D }));
		Assert.AreEqual(3D, SpeedTester.GetMedian(new[] { 9D, 1D, 3D }));
		Assert.AreEqual(2.5D, SpeedTester.GetMedian(new[] { 4D, 1D, 2D, 3D }));
	}

	[TestMethod]
	public void TestGetMeanDeviation()
	{
		Assert.IsNull(SpeedTester.GetMeanDeviation(Array.Empty<double>(), 0D));
		Assert.AreEqual(0D, SpeedTester.GetMeanDeviation(new[] { 5D, 5D }, 5D));
		Assert.AreEqual(2D, SpeedTester.GetMeanDeviation(new[] { 10D, 12D, 16D }, 12D)); // (2 + 0 + 4) / 3
	}

	#endregion

	#region Rate

	private static List<SpeedTester.RatePoint> CreatePoints(int lastTime, Func<long, long> getBytes, Func<long, long> getAdapterBytes) =>
		Enumerable.Range(0, lastTime / 100 + 1)
			.Select(x => (long)x * 100)
			.Select(x => new SpeedTester.RatePoint(x, getBytes(x), getAdapterBytes(x)))
			.ToList();

	[TestMethod]
	public void TestGetRateMbpsTooFewPoints()
	{
		Assert.IsNull(SpeedTester.GetRateMbps(null, 1000, false));
		Assert.IsNull(SpeedTester.GetRateMbps(new[] { new SpeedTester.RatePoint(0, 100, 100) }, 1000, false));
	}

	[TestMethod]
	public void TestGetRateMbpsAfterWarmup()
	{
		// 1 Mbps during warm-up of 1 second and then 8 Mbps
		var points = CreatePoints(5000,
			t => (t <= 1000) ? t * 125 : 125_000 + (t - 1000) * 1000,
			t => 0L);

		Assert.AreEqual(8D, SpeedTester.GetRateMbps(points, 1000, false).Value, 0.001);
	}

	[TestMethod]
	public void TestGetRateMbpsAdapterBytes()
	{
		// This measurement gets 8 Mbps while the interface receives 24 Mbps in total.
		var points = CreatePoints(5000, t => t * 1000, t => 5_000_000_000L + t * 3000);

		Assert.AreEqual(24D, SpeedTester.GetRateMbps(points, 1000, false).Value, 0.001);

		// The counter of the interface which has not moved is not used.
		points = CreatePoints(5000, t => t * 1000, t => 5_000_000_000L);

		Assert.AreEqual(8D, SpeedTester.GetRateMbps(points, 1000, false).Value, 0.001);
	}

	[TestMethod]
	public void TestGetRateMbpsCapBytesBeforeWarmup()
	{
		// 8 Mbps in the first half of bytes and then 80 Mbps, ended at 600 ms by the cap of bytes
		var points = CreatePoints(600,
			t => (t <= 300) ? t * 1000 : 300_000 + (t - 300) * 10_000,
			t => 0L);

		// The last half of bytes starts at 400 ms: (3,300,000 - 1,300,000) bytes in 200 ms
		Assert.AreEqual(80D, SpeedTester.GetRateMbps(points, 1000, true).Value, 0.001);
	}

	[TestMethod]
	public void TestGetRateMbpsShort()
	{
		// Ended at 600 ms without reaching the cap of bytes: average over the whole
		var points = CreatePoints(600, t => 50_000 + t * 1000, t => 0L);

		Assert.AreEqual(8D, SpeedTester.GetRateMbps(points, 1000, false).Value, 0.001);

		// Warm-up has ended but the remaining time is shorter than it.
		points = CreatePoints(1500, t => t * 1000, t => 0L);

		Assert.AreEqual(8D, SpeedTester.GetRateMbps(points, 1000, false).Value, 0.001);
	}

	#endregion

	#region Measure

	[TestMethod]
	public async Task TestMeasureUnknownInterface()
	{
		var tester = new SpeedTester();

		Assert.IsNull(await tester.MeasureAsync(null, SpeedTestSize.Quick, CancellationToken.None));

		// No request is sent because no network interface has this ID.
		var template = new SpeedSample
		{
			Time = DateTime.UtcNow,
			InterfaceId = Guid.NewGuid(),
			Profile = "Office",
			Trigger = SpeedTrigger.Manual
		};

		var sample = await tester.MeasureAsync(template, SpeedTestSize.Quick, CancellationToken.None);
		Assert.AreSame(template, sample);
		Assert.AreEqual(SpeedResult.Error, sample.Result);
		Assert.IsFalse(sample.Valid);
		Assert.IsNull(sample.DownMbps);
		Assert.AreEqual(0L, sample.DownBytes);
		Assert.AreEqual(SpeedTrigger.Manual, sample.Trigger);
	}

	[TestMethod]
	public async Task TestMeasureCanceled()
	{
		var tester = new SpeedTester();

		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		var template = new SpeedSample
		{
			Time = DateTime.UtcNow,
			InterfaceId = Guid.NewGuid(),
			Profile = "Office"
		};

		var sample = await tester.MeasureAsync(template, SpeedTestSize.Full, cancellation.Token);
		Assert.AreSame(template, sample);
		Assert.AreEqual(SpeedResult.Aborted, sample.Result);
		Assert.IsFalse(sample.Valid);
	}

	[TestMethod]
	public async Task TestLinkInspectorUnknownInterface()
	{
		var interfaceId = Guid.NewGuid();

		Assert.IsNull(LinkInspector.Find(interfaceId));
		Assert.IsNull(LinkInspector.GetLocalAddress(null));
		Assert.IsNull(LinkInspector.GetGlobalAddressV6(null));
		Assert.IsNull(LinkInspector.GetGateway(null));
		Assert.IsFalse(LinkInspector.IsReady(interfaceId));
		Assert.AreEqual((0L, 0L), LinkInspector.GetBytes(interfaceId));
		Assert.AreEqual(0D, await LinkInspector.GetRateMbpsAsync(interfaceId, TimeSpan.FromMilliseconds(10)));
		Assert.IsFalse(await LinkInspector.WaitForAddressAsync(interfaceId, TimeSpan.Zero, CancellationToken.None));

		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsExactlyAsync<OperationCanceledException>(
			() => LinkInspector.WaitForAddressAsync(interfaceId, TimeSpan.FromSeconds(10), cancellation.Token));
	}

	#endregion

	#region Settings/ProfileItem

	[TestMethod]
	public void TestSettingsSpeedRange()
	{
		var settings = Settings.Current; // Not initiated and so nothing is loaded or saved.

		var measuresSpeed = settings.MeasuresSpeed;
		var speedTestInterval = settings.SpeedTestInterval;
		var speedTestDailyBudget = settings.SpeedTestDailyBudget;
		var scanInterval = settings.ScanInterval;
		var speedTestDownloadUrl = settings.SpeedTestDownloadUrl;
		var speedTestUploadUrl = settings.SpeedTestUploadUrl;
		var connectivityCheckUrl = settings.ConnectivityCheckUrl;
		try
		{
			Assert.IsTrue(measuresSpeed);
			Assert.AreEqual(60, speedTestInterval);
			Assert.AreEqual(500, speedTestDailyBudget);
			Assert.AreEqual(5, scanInterval);
			Assert.AreEqual("https://speed.cloudflare.com/__down?bytes=25000000", speedTestDownloadUrl);
			Assert.AreEqual("https://speed.cloudflare.com/__up", speedTestUploadUrl);
			Assert.AreEqual("http://www.msftconnecttest.com/connecttest.txt", connectivityCheckUrl);

			settings.SpeedTestInterval = 1;
			Assert.AreEqual(15, settings.SpeedTestInterval);
			settings.SpeedTestInterval = 1000;
			Assert.AreEqual(240, settings.SpeedTestInterval);

			settings.SpeedTestDailyBudget = 0;
			Assert.AreEqual(50, settings.SpeedTestDailyBudget);
			settings.SpeedTestDailyBudget = 100000;
			Assert.AreEqual(5000, settings.SpeedTestDailyBudget);

			settings.ScanInterval = -1;
			Assert.AreEqual(0, settings.ScanInterval);
			settings.ScanInterval = 100000;
			Assert.AreEqual(1440, settings.ScanInterval);

			settings.SpeedTestDownloadUrl = " https://example.test/down ";
			Assert.AreEqual("https://example.test/down", settings.SpeedTestDownloadUrl);
			settings.SpeedTestDownloadUrl = null;
			Assert.AreEqual(speedTestDownloadUrl, settings.SpeedTestDownloadUrl);

			settings.SpeedTestUploadUrl = "https://example.test/up";
			settings.SpeedTestUploadUrl = "  ";
			Assert.AreEqual(speedTestUploadUrl, settings.SpeedTestUploadUrl);

			settings.ConnectivityCheckUrl = "http://example.test/check";
			settings.ConnectivityCheckUrl = string.Empty;
			Assert.AreEqual(connectivityCheckUrl, settings.ConnectivityCheckUrl);
		}
		finally
		{
			settings.MeasuresSpeed = measuresSpeed;
			settings.SpeedTestInterval = speedTestInterval;
			settings.SpeedTestDailyBudget = speedTestDailyBudget;
			settings.ScanInterval = scanInterval;
			settings.SpeedTestDownloadUrl = speedTestDownloadUrl;
			settings.SpeedTestUploadUrl = speedTestUploadUrl;
			settings.ConnectivityCheckUrl = connectivityCheckUrl;
		}
	}

	[TestMethod]
	public void TestProfileItemSpeedValuesNotCopied()
	{
		var interfaceId = Guid.NewGuid();
		var time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

		var item = CreateProfileItem("Office", interfaceId);

		var changed = new List<string>();
		item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		item.LastDownMbps = 70.5;
		item.LastUpMbps = 12.25;
		item.LastLatencyMs = 18.3;
		item.LastMeasuredTime = time;
		item.LastSpeedResult = SpeedResult.Ok;
		item.TypicalDownMbps = 68D;
		item.SpeedSampleCount = 4;
		item.IsMeasuring = true;

		CollectionAssert.AreEquivalent(
			new[]
			{
				nameof(ProfileItem.LastDownMbps),
				nameof(ProfileItem.LastUpMbps),
				nameof(ProfileItem.LastLatencyMs),
				nameof(ProfileItem.LastMeasuredTime),
				nameof(ProfileItem.LastSpeedResult),
				nameof(ProfileItem.TypicalDownMbps),
				nameof(ProfileItem.SpeedSampleCount),
				nameof(ProfileItem.IsMeasuring)
			},
			changed);

		// Values of speed belong to this app and are not overwritten by a reloaded profile.
		item.Copy(CreateProfileItem("Office", interfaceId));

		Assert.AreEqual(70.5, item.LastDownMbps);
		Assert.AreEqual(12.25, item.LastUpMbps);
		Assert.AreEqual(18.3, item.LastLatencyMs);
		Assert.AreEqual(time, item.LastMeasuredTime);
		Assert.AreEqual(SpeedResult.Ok, item.LastSpeedResult);
		Assert.AreEqual(68D, item.TypicalDownMbps);
		Assert.AreEqual(4, item.SpeedSampleCount);
		Assert.IsTrue(item.IsMeasuring);
	}

	private static ProfileItem CreateProfileItem(string name, Guid interfaceId) =>
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
			isConnected: true,
			protocol: "802.11ac",
			signal: 70,
			band: 5F,
			channel: 36);

	#endregion
}
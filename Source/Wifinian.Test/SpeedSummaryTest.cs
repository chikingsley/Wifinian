using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Wifinian.Models.History;

namespace Wifinian.Test;

[TestClass]
public class SpeedSummaryTest
{
	private static readonly DateTime _now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
	private static readonly Guid _interfaceId = new Guid("11111111-2222-3333-4444-555555555555");

	[TestMethod]
	public void TestWeightedMedianIgnoresOldOutliers()
	{
		// Two old outliers outnumber one recent sample but weigh 1/16 each.
		var samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromDays(12), SpeedResult.Ok, down: 500, up: 100, latency: 5),
			Create(TimeSpan.FromDays(12.1), SpeedResult.Ok, down: 480, up: 90, latency: 6),
			Create(TimeSpan.FromHours(1), SpeedResult.Ok, down: 50, up: 10, latency: 20)
		};

		var summary = SpeedSummary.Create(samples, _now);

		Assert.IsNotNull(summary);
		Assert.IsTrue(summary.HasData);
		Assert.AreEqual(3, summary.Count);
		Assert.AreEqual(50D, summary.DownMbps);
		Assert.AreEqual(10D, summary.UpMbps);
		Assert.AreEqual(20D, summary.LatencyMs);
		Assert.IsFalse(summary.IsDead);
	}

	[TestMethod]
	public void TestWeightedMedianOfRecentSamples()
	{
		var samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromMinutes(30), SpeedResult.Ok, down: 80, up: null, latency: 12),
			Create(TimeSpan.FromMinutes(90), SpeedResult.Ok, down: 60, up: 20, latency: 10),
			Create(TimeSpan.FromMinutes(150), SpeedResult.Ok, down: 70, up: null, latency: 14)
		};

		var summary = SpeedSummary.Create(samples, _now);

		Assert.AreEqual(70D, summary.DownMbps);
		Assert.AreEqual(20D, summary.UpMbps); // Nulls are ignored per field.
		Assert.AreEqual(12D, summary.LatencyMs);

		// Last values come from the newest sample regardless of the order of the list.
		Assert.AreEqual(_now - TimeSpan.FromMinutes(30), summary.LastTime);
		Assert.AreEqual(SpeedResult.Ok, summary.LastResult);
		Assert.AreEqual(80D, summary.LastDownMbps);
		Assert.IsNull(summary.LastUpMbps);
		Assert.AreEqual(12D, summary.LastLatencyMs);
	}

	[TestMethod]
	public void TestMeanSignal()
	{
		// Samples of the same age weigh the same.
		var samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromHours(1), SpeedResult.Ok, down: 50, signal: 60),
			Create(TimeSpan.FromHours(1), SpeedResult.Ok, down: 50, signal: 80)
		};

		Assert.AreEqual(70D, SpeedSummary.Create(samples, _now).MeanSignal, 0.0001);

		// A sample of 3 days ago weighs half.
		samples = new List<SpeedSample>
		{
			Create(TimeSpan.Zero, SpeedResult.Ok, down: 50, signal: 90),
			Create(TimeSpan.FromDays(3), SpeedResult.Ok, down: 50, signal: 60)
		};

		Assert.AreEqual(80D, SpeedSummary.Create(samples, _now).MeanSignal, 0.0001);
	}

	[TestMethod]
	public void TestIsDead()
	{
		// Two consecutive no internet results
		var samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromDays(1), SpeedResult.Ok, down: 50),
			Create(TimeSpan.FromMinutes(20), SpeedResult.NoInternet, down: 0),
			Create(TimeSpan.FromMinutes(10), SpeedResult.CaptivePortal, down: 0)
		};

		var summary = SpeedSummary.Create(samples, _now);
		Assert.IsTrue(summary.IsDead);
		Assert.IsTrue(summary.HasData);
		Assert.AreEqual(1, summary.Count);
		Assert.AreEqual(50D, summary.DownMbps);
		Assert.AreEqual(SpeedResult.CaptivePortal, summary.LastResult);

		// One no internet result
		samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromMinutes(20), SpeedResult.Ok, down: 50),
			Create(TimeSpan.FromMinutes(10), SpeedResult.NoInternet, down: 0)
		};

		summary = SpeedSummary.Create(samples, _now);
		Assert.IsFalse(summary.IsDead);
		Assert.AreEqual(SpeedResult.NoInternet, summary.LastResult);

		// Only one sample
		samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromMinutes(10), SpeedResult.NoInternet, down: 0)
		};

		summary = SpeedSummary.Create(samples, _now);
		Assert.IsFalse(summary.IsDead);
		Assert.IsFalse(summary.HasData);
		Assert.AreEqual(0, summary.Count);
		Assert.IsNull(summary.DownMbps);

		// Two consecutive no internet results but not recent
		samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromHours(4), SpeedResult.NoInternet, down: 0),
			Create(TimeSpan.FromHours(3), SpeedResult.NoInternet, down: 0)
		};

		Assert.IsFalse(SpeedSummary.Create(samples, _now).IsDead);

		// An invalid sample between two no internet results does not count.
		samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromMinutes(30), SpeedResult.NoInternet, down: 0),
			Create(TimeSpan.FromMinutes(20), SpeedResult.Aborted),
			Create(TimeSpan.FromMinutes(10), SpeedResult.NoInternet, down: 0)
		};

		Assert.IsTrue(SpeedSummary.Create(samples, _now).IsDead);

		// A later Ok result clears it.
		samples.Add(Create(TimeSpan.FromMinutes(5), SpeedResult.Ok, down: 40));

		Assert.IsFalse(SpeedSummary.Create(samples, _now).IsDead);
	}

	[TestMethod]
	public void TestInvalidSamplesAreIgnored()
	{
		var samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromMinutes(50), SpeedResult.Ok, down: 50, signal: 70),
			Create(TimeSpan.FromMinutes(40), SpeedResult.EndpointFailed, down: 1, signal: 10),
			Create(TimeSpan.FromMinutes(30), SpeedResult.RouteMismatch, down: 900, signal: 10),
			Create(TimeSpan.FromMinutes(20), SpeedResult.Aborted, down: 2, signal: 10),
			Create(TimeSpan.FromMinutes(10), SpeedResult.Error, signal: 10),
			Create(TimeSpan.FromMinutes(5), SpeedResult.None, down: 3, signal: 10)
		};

		var summary = SpeedSummary.Create(samples, _now);

		Assert.AreEqual(1, summary.Count);
		Assert.AreEqual(50D, summary.DownMbps);
		Assert.AreEqual(70D, summary.MeanSignal, 0.0001);
		Assert.AreEqual(_now - TimeSpan.FromMinutes(50), summary.LastTime);
		Assert.AreEqual(SpeedResult.Ok, summary.LastResult);
		Assert.IsFalse(summary.IsDead);

		// Invalid samples only
		samples.RemoveAt(0);

		Assert.IsNull(SpeedSummary.Create(samples, _now));
	}

	[TestMethod]
	public void TestNullWhenNothingWithinPeriod()
	{
		Assert.IsNull(SpeedSummary.Create(null, _now));
		Assert.IsNull(SpeedSummary.Create(new List<SpeedSample>(), _now));

		var samples = new List<SpeedSample>
		{
			Create(TimeSpan.FromDays(15), SpeedResult.Ok, down: 50),
			Create(TimeSpan.FromDays(14) + TimeSpan.FromSeconds(1), SpeedResult.Ok, down: 50)
		};

		Assert.IsNull(SpeedSummary.Create(samples, _now));

		samples.Add(Create(TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1), SpeedResult.Ok, down: 30));

		var summary = SpeedSummary.Create(samples, _now);
		Assert.IsNotNull(summary);
		Assert.AreEqual(1, summary.Count);
		Assert.AreEqual(30D, summary.DownMbps);
	}

	[TestMethod]
	public void TestNewestTenAreKept()
	{
		var samples = new List<SpeedSample>();

		// Ten recent samples of 20 Mbps and five older samples of 90 Mbps
		for (int i = 0; i < 10; i++)
			samples.Add(Create(TimeSpan.FromMinutes(10 * (i + 1)), SpeedResult.Ok, down: 20));

		for (int i = 0; i < 5; i++)
			samples.Add(Create(TimeSpan.FromMinutes(200 + 10 * i), SpeedResult.Ok, down: 90));

		var summary = SpeedSummary.Create(samples, _now);

		Assert.AreEqual(10, summary.Count);
		Assert.AreEqual(20D, summary.DownMbps);
	}

	#region Base

	private static SpeedSample Create(TimeSpan age, SpeedResult result, double? down = null, double? up = null, double? latency = null, int signal = 70)
	{
		return new SpeedSample
		{
			Time = _now - age,
			InterfaceId = _interfaceId,
			Profile = "Office",
			Signal = signal,
			DownMbps = down,
			UpMbps = up,
			LatencyMs = latency,
			Trigger = SpeedTrigger.Periodic,
			Result = result
		};
	}

	#endregion
}
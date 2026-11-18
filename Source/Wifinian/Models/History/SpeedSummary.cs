using System;
using System.Collections.Generic;
using System.Linq;

namespace Wifinian.Models.History;

/// <summary>
/// Summary of recent speed samples of a wireless profile
/// </summary>
internal class SpeedSummary
{
	/// <summary>
	/// Period in which samples are used
	/// </summary>
	public static readonly TimeSpan Period = TimeSpan.FromDays(14);

	/// <summary>
	/// Maximum number of samples used
	/// </summary>
	public const int MaxCount = 10;

	/// <summary>
	/// Half-life of the weight of a sample (days)
	/// </summary>
	public const double HalfLifeDays = 3D;

	/// <summary>
	/// Period after the newest sample in which a network can be regarded as dead
	/// </summary>
	public static readonly TimeSpan DeadPeriod = TimeSpan.FromHours(2);

	/// <summary>
	/// Number of Ok samples used
	/// </summary>
	public int Count { get; private set; }

	/// <summary>
	/// Time (UTC) of the newest valid sample
	/// </summary>
	public DateTime LastTime { get; private set; }

	public SpeedResult LastResult { get; private set; }
	public double? LastDownMbps { get; private set; }
	public double? LastUpMbps { get; private set; }
	public double? LastLatencyMs { get; private set; }

	/// <summary>
	/// Typical (weighted median) download speed of Ok samples
	/// </summary>
	public double? DownMbps { get; private set; }

	/// <summary>
	/// Typical (weighted median) upload speed of Ok samples
	/// </summary>
	public double? UpMbps { get; private set; }

	/// <summary>
	/// Typical (weighted median) latency of Ok samples
	/// </summary>
	public double? LatencyMs { get; private set; }

	/// <summary>
	/// Weighted mean of signal quality of Ok samples
	/// </summary>
	public double MeanSignal { get; private set; }

	/// <summary>
	/// Whether the two newest valid samples found no internet access and the newest is recent
	/// </summary>
	public bool IsDead { get; private set; }

	/// <summary>
	/// Whether the two newest valid samples found no internet access regardless of their age
	/// </summary>
	/// <remarks>This stays true until a later valid sample finds internet access.</remarks>
	public bool IsOffline { get; private set; }

	/// <summary>
	/// Whether at least one Ok sample exists
	/// </summary>
	public bool HasData => (Count >= 1);

	private SpeedSummary()
	{ }

	/// <summary>
	/// Creates a summary from speed samples of a wireless profile.
	/// </summary>
	/// <param name="samples">Speed samples</param>
	/// <param name="nowUtc">Current time (UTC)</param>
	/// <returns>Summary if any valid sample exists within the period, null otherwise</returns>
	public static SpeedSummary Create(IReadOnlyList<SpeedSample> samples, DateTime nowUtc)
	{
		if (samples is not { Count: > 0 })
			return null;

		var oldest = nowUtc - Period;

		var kept = samples
			.Where(x => (x is not null) && x.Valid && (x.Time >= oldest))
			.OrderByDescending(x => x.Time)
			.Take(MaxCount)
			.ToArray();
		if (kept.Length == 0)
			return null;

		var newest = kept[0];

		var summary = new SpeedSummary
		{
			LastTime = newest.Time,
			LastResult = newest.Result,
			LastDownMbps = newest.DownMbps,
			LastUpMbps = newest.UpMbps,
			LastLatencyMs = newest.LatencyMs,
			IsOffline = (kept.Length >= 2)
				&& IsNoInternet(kept[0])
				&& IsNoInternet(kept[1])
		};
		summary.IsDead = summary.IsOffline && (nowUtc - newest.Time <= DeadPeriod);

		var oks = kept
			.Where(x => x.Result is SpeedResult.Ok)
			.Select(x => (sample: x, weight: GetWeight(x.Time, nowUtc)))
			.ToArray();

		summary.Count = oks.Length;
		if (oks.Length > 0)
		{
			// Download speed and latency measured through a server other than the first one are
			// not the same quantities as those measured through it (the server can be slower
			// than the link and is farther away). They are used only for a network which has
			// nothing else, so that networks are compared by figures of the same server.
			var rated = oks.Where(x => !x.sample.IsFallback).ToArray();
			if (rated.Length == 0)
				rated = oks;

			summary.DownMbps = GetWeightedMedian(rated.Select(x => (x.sample.DownMbps, x.weight)));
			summary.UpMbps = GetWeightedMedian(oks.Select(x => (x.sample.UpMbps, x.weight)));
			summary.LatencyMs = GetWeightedMedian(rated.Select(x => (x.sample.LatencyMs, x.weight)));

			var totalWeight = oks.Sum(x => x.weight);
			summary.MeanSignal = (totalWeight > 0D)
				? oks.Sum(x => x.sample.Signal * x.weight) / totalWeight
				: oks.Average(x => (double)x.sample.Signal);
		}
		return summary;

		static bool IsNoInternet(SpeedSample sample) =>
			sample.Result is SpeedResult.NoInternet or SpeedResult.CaptivePortal;
	}

	private static double GetWeight(DateTime time, DateTime nowUtc)
	{
		var ageDays = Math.Max(0D, (nowUtc - time).TotalDays);
		return Math.Pow(0.5D, ageDays / HalfLifeDays);
	}

	private static double? GetWeightedMedian(IEnumerable<(double? value, double weight)> source)
	{
		var items = source
			.Where(x => x.value.HasValue)
			.Select(x => (value: x.value.Value, x.weight))
			.OrderBy(x => x.value)
			.ToArray();
		if (items.Length == 0)
			return null;

		var half = items.Sum(x => x.weight) / 2D;
		var cumulative = 0D;

		foreach (var (value, weight) in items)
		{
			cumulative += weight;
			if (cumulative >= half)
				return value;
		}
		return items[items.Length - 1].value;
	}
}
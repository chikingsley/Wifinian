using System;
using System.Collections.Generic;

namespace Wifinian.Models.History;

/// <summary>
/// Trigger of a speed measurement
/// </summary>
public enum SpeedTrigger
{
	AfterConnect = 0,
	Periodic,
	Verify,
	PostSwitch,
	Manual,
	TestAll,
	IdleProbe
}

/// <summary>
/// Result of a speed measurement
/// </summary>
public enum SpeedResult
{
	None = 0,
	Ok,
	NoInternet,
	CaptivePortal,
	EndpointFailed,
	RouteMismatch,
	Aborted,
	Error
}

/// <summary>
/// Speed sample of a wireless profile (a row of speed table)
/// </summary>
internal class SpeedSample
{
	public const string Header = "Time,InterfaceId,Profile,Bssid,Band,Channel,Signal,LinkRate,GatewayMs,LatencyMs,JitterMs,DownMbps,UpMbps,DownBytes,UpBytes,DurationMs,Trigger,Result,Valid";

	/// <summary>
	/// Time (UTC)
	/// </summary>
	public DateTime Time { get; set; }

	public Guid InterfaceId { get; set; }
	public string Profile { get; set; }
	public string Bssid { get; set; }

	/// <summary>
	/// Frequency band (GHz)
	/// </summary>
	public float Band { get; set; }

	public int Channel { get; set; }

	/// <summary>
	/// Signal quality (0-100)
	/// </summary>
	public int Signal { get; set; }

	/// <summary>
	/// Link rate (Mbps)
	/// </summary>
	public int? LinkRate { get; set; }

	public double? GatewayMs { get; set; }
	public double? LatencyMs { get; set; }
	public double? JitterMs { get; set; }
	public double? DownMbps { get; set; }
	public double? UpMbps { get; set; }
	public long DownBytes { get; set; }
	public long UpBytes { get; set; }
	public int DurationMs { get; set; }
	public SpeedTrigger Trigger { get; set; }
	public SpeedResult Result { get; set; }

	/// <summary>
	/// Whether this sample is a true finding on the network and so can be scored
	/// </summary>
	/// <remarks>
	/// This value is written to the database for other readers but is always computed from the result.
	/// </remarks>
	public bool Valid => Result is SpeedResult.Ok or SpeedResult.NoInternet or SpeedResult.CaptivePortal;

	public string Key => HistoryService.GetKey(Profile, InterfaceId);

	public string ToCsv() => CsvLine.Join(
		CsvLine.FormatTime(Time),
		InterfaceId.ToString("D"),
		Profile,
		Bssid,
		CsvLine.Format(Band),
		CsvLine.Format((int?)Channel),
		CsvLine.Format((int?)Signal),
		CsvLine.Format(LinkRate),
		CsvLine.Format(GatewayMs),
		CsvLine.Format(LatencyMs),
		CsvLine.Format(JitterMs),
		CsvLine.Format(DownMbps),
		CsvLine.Format(UpMbps),
		CsvLine.Format(DownBytes),
		CsvLine.Format(UpBytes),
		CsvLine.Format((int?)DurationMs),
		Trigger.ToString(),
		Result.ToString(),
		CsvLine.Format(Valid));

	public static bool TryParse(IReadOnlyDictionary<string, int> columns, string[] fields, out SpeedSample sample)
	{
		sample = null;

		if (!CsvLine.TryParseTime(CsvLine.GetField(columns, fields, nameof(Time)), out DateTime time))
			return false;

		if (!Guid.TryParse(CsvLine.GetField(columns, fields, nameof(InterfaceId)), out Guid interfaceId))
			return false;

		var profile = CsvLine.GetField(columns, fields, nameof(Profile));
		if (string.IsNullOrEmpty(profile))
			return false;

		// An unknown trigger or result is tolerated. An unknown result is never regarded as valid.
		if (!TryParseEnum(CsvLine.GetField(columns, fields, nameof(Trigger)), out SpeedTrigger trigger))
			trigger = default;

		if (!TryParseEnum(CsvLine.GetField(columns, fields, nameof(Result)), out SpeedResult result))
			result = SpeedResult.None;

		sample = new SpeedSample
		{
			Time = time,
			InterfaceId = interfaceId,
			Profile = profile,
			Bssid = CsvLine.GetField(columns, fields, nameof(Bssid)) ?? string.Empty,
			Band = CsvLine.ParseFloat(CsvLine.GetField(columns, fields, nameof(Band))) ?? 0F,
			Channel = CsvLine.ParseInt(CsvLine.GetField(columns, fields, nameof(Channel))) ?? 0,
			Signal = CsvLine.ParseInt(CsvLine.GetField(columns, fields, nameof(Signal))) ?? 0,
			LinkRate = CsvLine.ParseInt(CsvLine.GetField(columns, fields, nameof(LinkRate))),
			GatewayMs = CsvLine.ParseDouble(CsvLine.GetField(columns, fields, nameof(GatewayMs))),
			LatencyMs = CsvLine.ParseDouble(CsvLine.GetField(columns, fields, nameof(LatencyMs))),
			JitterMs = CsvLine.ParseDouble(CsvLine.GetField(columns, fields, nameof(JitterMs))),
			DownMbps = CsvLine.ParseDouble(CsvLine.GetField(columns, fields, nameof(DownMbps))),
			UpMbps = CsvLine.ParseDouble(CsvLine.GetField(columns, fields, nameof(UpMbps))),
			DownBytes = CsvLine.ParseLong(CsvLine.GetField(columns, fields, nameof(DownBytes))) ?? 0L,
			UpBytes = CsvLine.ParseLong(CsvLine.GetField(columns, fields, nameof(UpBytes))) ?? 0L,
			DurationMs = CsvLine.ParseInt(CsvLine.GetField(columns, fields, nameof(DurationMs))) ?? 0,
			Trigger = trigger,
			Result = result
		};
		return true;
	}

	internal static bool TryParseEnum<TEnum>(string field, out TEnum value) where TEnum : struct
	{
		value = default;

		// Numeric strings are rejected because Enum.TryParse accepts any number.
		if (string.IsNullOrWhiteSpace(field) || !char.IsLetter(field.Trim()[0]))
			return false;

		return Enum.TryParse(field.Trim(), true, out value)
			&& Enum.IsDefined(typeof(TEnum), value);
	}
}
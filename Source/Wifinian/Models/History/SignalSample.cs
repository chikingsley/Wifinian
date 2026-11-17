using System;
using System.Collections.Generic;

namespace Wifinian.Models.History;

/// <summary>
/// Signal sample of a wireless profile (a row of signal-yyyyMMdd.csv)
/// </summary>
internal class SignalSample
{
	public const string Header = "Time,InterfaceId,Profile,Connected,Signal,Rssi,Band,Channel,Protocol,Bssid,LinkRate";

	/// <summary>
	/// Time (UTC)
	/// </summary>
	public DateTime Time { get; set; }

	public Guid InterfaceId { get; set; }
	public string Profile { get; set; }
	public bool Connected { get; set; }

	/// <summary>
	/// Signal quality (0-100)
	/// </summary>
	public int Signal { get; set; }

	/// <summary>
	/// RSSI (dBm)
	/// </summary>
	public int? Rssi { get; set; }

	/// <summary>
	/// Frequency band (GHz)
	/// </summary>
	public float Band { get; set; }

	public int Channel { get; set; }
	public string Protocol { get; set; }
	public string Bssid { get; set; }

	/// <summary>
	/// Link rate (Mbps)
	/// </summary>
	public int? LinkRate { get; set; }

	public string Key => HistoryService.GetKey(Profile, InterfaceId);

	public string ToCsv() => CsvLine.Join(
		CsvLine.FormatTime(Time),
		InterfaceId.ToString("D"),
		Profile,
		CsvLine.Format(Connected),
		CsvLine.Format((int?)Signal),
		CsvLine.Format(Rssi),
		CsvLine.Format(Band),
		CsvLine.Format((int?)Channel),
		Protocol,
		Bssid,
		CsvLine.Format(LinkRate));

	public static bool TryParse(IReadOnlyDictionary<string, int> columns, string[] fields, out SignalSample sample)
	{
		sample = null;

		if (!CsvLine.TryParseTime(CsvLine.GetField(columns, fields, nameof(Time)), out DateTime time))
			return false;

		if (!Guid.TryParse(CsvLine.GetField(columns, fields, nameof(InterfaceId)), out Guid interfaceId))
			return false;

		var profile = CsvLine.GetField(columns, fields, nameof(Profile));
		if (string.IsNullOrEmpty(profile))
			return false;

		sample = new SignalSample
		{
			Time = time,
			InterfaceId = interfaceId,
			Profile = profile,
			Connected = CsvLine.ParseBool(CsvLine.GetField(columns, fields, nameof(Connected))),
			Signal = CsvLine.ParseInt(CsvLine.GetField(columns, fields, nameof(Signal))) ?? 0,
			Rssi = CsvLine.ParseInt(CsvLine.GetField(columns, fields, nameof(Rssi))),
			Band = CsvLine.ParseFloat(CsvLine.GetField(columns, fields, nameof(Band))) ?? 0F,
			Channel = CsvLine.ParseInt(CsvLine.GetField(columns, fields, nameof(Channel))) ?? 0,
			Protocol = CsvLine.GetField(columns, fields, nameof(Protocol)) ?? string.Empty,
			Bssid = CsvLine.GetField(columns, fields, nameof(Bssid)) ?? string.Empty,
			LinkRate = CsvLine.ParseInt(CsvLine.GetField(columns, fields, nameof(LinkRate)))
		};
		return true;
	}
}
using System;
using System.Collections.Generic;

namespace Wifinian.Models.History;

/// <summary>
/// Kind of a switching event
/// </summary>
public enum SwitchKind
{
	Switch = 0,
	SwitchFailed,
	Revert,
	Stay,
	WouldSwitch,
	Probe,
	TestAll
}

/// <summary>
/// Switching event or decision (a row of events table)
/// </summary>
internal class SwitchEvent
{
	public const string Header = "Time,InterfaceId,Kind,From,To,FromScore,ToScore,Reason";

	/// <summary>
	/// Time (UTC)
	/// </summary>
	public DateTime Time { get; set; }

	public Guid InterfaceId { get; set; }
	public SwitchKind Kind { get; set; }

	/// <summary>
	/// Name of the profile before this event
	/// </summary>
	public string From { get; set; }

	/// <summary>
	/// Name of the profile after this event
	/// </summary>
	public string To { get; set; }

	public double? FromScore { get; set; }
	public double? ToScore { get; set; }

	/// <summary>
	/// Reason sentence shown to the user
	/// </summary>
	public string Reason { get; set; }

	public string ToCsv() => CsvLine.Join(
		CsvLine.FormatTime(Time),
		InterfaceId.ToString("D"),
		Kind.ToString(),
		From,
		To,
		CsvLine.Format(FromScore),
		CsvLine.Format(ToScore),
		Reason);

	public static bool TryParse(IReadOnlyDictionary<string, int> columns, string[] fields, out SwitchEvent sample)
	{
		sample = null;

		if (!CsvLine.TryParseTime(CsvLine.GetField(columns, fields, nameof(Time)), out DateTime time))
			return false;

		if (!Guid.TryParse(CsvLine.GetField(columns, fields, nameof(InterfaceId)), out Guid interfaceId))
			return false;

		if (!SpeedSample.TryParseEnum(CsvLine.GetField(columns, fields, nameof(Kind)), out SwitchKind kind))
			return false;

		sample = new SwitchEvent
		{
			Time = time,
			InterfaceId = interfaceId,
			Kind = kind,
			From = CsvLine.GetField(columns, fields, nameof(From)) ?? string.Empty,
			To = CsvLine.GetField(columns, fields, nameof(To)) ?? string.Empty,
			FromScore = CsvLine.ParseDouble(CsvLine.GetField(columns, fields, nameof(FromScore))),
			ToScore = CsvLine.ParseDouble(CsvLine.GetField(columns, fields, nameof(ToScore))),
			Reason = CsvLine.GetField(columns, fields, nameof(Reason)) ?? string.Empty
		};
		return true;
	}
}
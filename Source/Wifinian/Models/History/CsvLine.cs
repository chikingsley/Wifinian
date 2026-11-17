using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Wifinian.Models.History;

/// <summary>
/// Helper for a line of CSV (RFC 4180) file
/// </summary>
internal static class CsvLine
{
	private static readonly char[] _specialChars = { ',', '"' };
	private static readonly char[] _lineBreakChars = { '\r', '\n' };

	#region Line

	/// <summary>
	/// Escapes a field.
	/// </summary>
	/// <param name="field">Field</param>
	/// <returns>Escaped field</returns>
	/// <remarks>
	/// Line breaks are replaced with a space because the files are read line by line and so a row
	/// must not span multiple lines.
	/// </remarks>
	public static string Escape(string field)
	{
		if (string.IsNullOrEmpty(field))
			return string.Empty;

		if (field.IndexOfAny(_lineBreakChars) >= 0)
			field = field.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

		if (field.IndexOfAny(_specialChars) < 0)
			return field;

		return '"' + field.Replace("\"", "\"\"") + '"';
	}

	public static string Join(params string[] fields)
	{
		if (fields is null)
			return string.Empty;

		return string.Join(",", fields.Select(x => Escape(x)));
	}

	/// <summary>
	/// Splits a line into fields.
	/// </summary>
	/// <param name="line">Line</param>
	/// <returns>Fields if successfully parsed, null if the line is null or has an unterminated quote</returns>
	public static string[] Split(string line)
	{
		if (line is null)
			return null;

		var fields = new List<string>();
		var buffer = new StringBuilder();
		bool isQuoted = false;

		for (int i = 0; i < line.Length; i++)
		{
			var c = line[i];

			if (isQuoted)
			{
				if (c != '"')
				{
					buffer.Append(c);
				}
				else if ((i + 1 < line.Length) && (line[i + 1] == '"'))
				{
					buffer.Append('"');
					i++;
				}
				else
				{
					isQuoted = false;
				}
			}
			else if (c == ',')
			{
				fields.Add(buffer.ToString());
				buffer.Clear();
			}
			else if ((c == '"') && (buffer.Length == 0))
			{
				isQuoted = true;
			}
			else
			{
				buffer.Append(c);
			}
		}

		if (isQuoted)
			return null;

		fields.Add(buffer.ToString());
		return fields.ToArray();
	}

	/// <summary>
	/// Builds a dictionary of column names and indices from a header line.
	/// </summary>
	/// <param name="headerLine">Header line</param>
	/// <returns>Dictionary if successfully parsed, null otherwise</returns>
	public static IReadOnlyDictionary<string, int> GetColumns(string headerLine)
	{
		var names = Split(headerLine);
		if (names is null)
			return null;

		var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < names.Length; i++)
		{
			var name = names[i].Trim();
			if ((name.Length > 0) && !columns.ContainsKey(name))
				columns.Add(name, i);
		}
		return columns;
	}

	/// <summary>
	/// Gets the field of a specified column.
	/// </summary>
	/// <param name="columns">Dictionary of column names and indices</param>
	/// <param name="fields">Fields</param>
	/// <param name="name">Column name</param>
	/// <returns>Field if the column exists, null otherwise</returns>
	public static string GetField(IReadOnlyDictionary<string, int> columns, string[] fields, string name)
	{
		if ((columns is null) || (fields is null))
			return null;

		if (!columns.TryGetValue(name, out int index) || (index < 0) || (index >= fields.Length))
			return null;

		return fields[index];
	}

	#endregion

	#region Number

	public static string Format(double? value) =>
		value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
			? value.Value.ToString("0.0", CultureInfo.InvariantCulture)
			: string.Empty;

	public static string Format(int? value) =>
		value.HasValue
			? value.Value.ToString(CultureInfo.InvariantCulture)
			: string.Empty;

	public static string Format(long value) =>
		value.ToString(CultureInfo.InvariantCulture);

	public static string Format(float value) =>
		!float.IsNaN(value) && !float.IsInfinity(value)
			? value.ToString("0.###", CultureInfo.InvariantCulture)
			: string.Empty;

	public static string Format(bool value) => value ? "1" : "0";

	public static double? ParseDouble(string field)
	{
		if (string.IsNullOrWhiteSpace(field))
			return null;

		return double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			&& !double.IsNaN(value) && !double.IsInfinity(value)
			? value
			: null;
	}

	public static int? ParseInt(string field)
	{
		if (string.IsNullOrWhiteSpace(field))
			return null;

		return int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
			? value
			: null;
	}

	public static long? ParseLong(string field)
	{
		if (string.IsNullOrWhiteSpace(field))
			return null;

		return long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
			? value
			: null;
	}

	public static float? ParseFloat(string field)
	{
		if (string.IsNullOrWhiteSpace(field))
			return null;

		return float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
			&& !float.IsNaN(value) && !float.IsInfinity(value)
			? value
			: null;
	}

	public static bool ParseBool(string field)
	{
		if (string.IsNullOrWhiteSpace(field))
			return false;

		field = field.Trim();
		return (field == "1") || string.Equals(field, "true", StringComparison.OrdinalIgnoreCase);
	}

	#endregion

	#region Time

	public const string TimeFormat = "yyyy-MM-ddTHH:mm:ssZ";

	public static string FormatTime(DateTime utc) =>
		ToUtc(utc).ToString(TimeFormat, CultureInfo.InvariantCulture);

	public static bool TryParseTime(string field, out DateTime utc)
	{
		if (string.IsNullOrWhiteSpace(field))
		{
			utc = default;
			return false;
		}

		return DateTime.TryParse(field, CultureInfo.InvariantCulture,
			DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
	}

	/// <summary>
	/// Converts a local time to UTC and regards other time as UTC.
	/// </summary>
	public static DateTime ToUtc(DateTime time) => time.Kind switch
	{
		DateTimeKind.Utc => time,
		DateTimeKind.Local => time.ToUniversalTime(),
		_ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
	};

	#endregion
}
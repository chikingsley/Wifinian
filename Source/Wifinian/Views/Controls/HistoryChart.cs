using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Wifinian.Views.Controls;

/// <summary>
/// Point of a series of a chart
/// </summary>
public class ChartPoint
{
	public DateTime Time { get; set; }
	public double Value { get; set; }

	/// <summary>
	/// Whether the line is broken before this point
	/// </summary>
	public bool IsBreak { get; set; }
}

/// <summary>
/// Series of a chart
/// </summary>
public class ChartSeries
{
	public string Name { get; set; }
	public Brush Brush { get; set; }

	/// <summary>
	/// Points in order of time
	/// </summary>
	public IReadOnlyList<ChartPoint> Points { get; set; }

	/// <summary>
	/// Whether to draw a filled marker at each point
	/// </summary>
	public bool ShowMarkers { get; set; }

	/// <summary>
	/// Times of hollow markers drawn on the baseline
	/// </summary>
	public IReadOnlyList<DateTime> HollowMarkers { get; set; }
}

/// <summary>
/// Calculation of scales and lines of a chart
/// </summary>
internal static class ChartScale
{
	/// <summary>
	/// Minimum of the maximum value of the vertical axis decided automatically
	/// </summary>
	public const double MinMaximum = 10D;

	/// <summary>
	/// Gets the maximum value of the vertical axis which is the specified value rounded up
	/// to the next of 1, 2 or 5 multiplied by a power of 10.
	/// </summary>
	/// <param name="largest">Largest value of all series</param>
	/// <returns>Maximum value of the vertical axis</returns>
	public static double GetMaximum(double largest)
	{
		if (double.IsNaN(largest) || double.IsInfinity(largest) || (largest <= MinMaximum))
			return MinMaximum;

		var unit = Math.Pow(10, Math.Floor(Math.Log10(largest)));

		foreach (var step in new[] { 1D, 2D, 5D })
		{
			if (largest <= step * unit)
				return step * unit;
		}
		return 10D * unit;
	}

	/// <summary>
	/// Maximum duration to which ticks of hours are given
	/// </summary>
	public static readonly TimeSpan HourlyRange = TimeSpan.FromHours(26);

	/// <summary>
	/// Gets ticks of the horizontal axis.
	/// </summary>
	/// <param name="from">Start time (local time)</param>
	/// <param name="to">End time (local time)</param>
	/// <returns>Times and labels of ticks</returns>
	public static IReadOnlyList<(DateTime time, string label)> GetTicks(DateTime from, DateTime to)
	{
		var ticks = new List<(DateTime, string)>();
		if (to <= from)
			return ticks;

		if (to - from <= HourlyRange)
		{
			const int hours = 3;

			var time = from.Date.AddHours(from.Hour - (from.Hour % hours));
			for (; time <= to; time = time.AddHours(hours))
			{
				if (from <= time)
					ticks.Add((time, time.ToString("HH:mm", CultureInfo.InvariantCulture)));
			}
		}
		else
		{
			var first = (from == from.Date) ? from : from.Date.AddDays(1);
			var count = (int)Math.Floor((to - first).TotalDays) + 1;
			var days = (count <= 10) ? 1 : ((count <= 20) ? 2 : 5);

			for (var time = first; time <= to; time = time.AddDays(days))
				ticks.Add((time, time.ToString("M/d", CultureInfo.InvariantCulture)));
		}
		return ticks;
	}

	/// <summary>
	/// Reduces points to at most a pair of minimum and maximum values per pixel column and
	/// divides them into lines.
	/// </summary>
	/// <param name="points">Points in order of time</param>
	/// <param name="from">Time at the left end</param>
	/// <param name="pixelsPerTick">Number of pixels per tick of time</param>
	/// <param name="gapMinutes">Gap between consecutive points (in minutes) over which the line is broken (0 for never)</param>
	/// <returns>Lines of points whose x is in pixels from the left end</returns>
	public static List<List<(double x, double value)>> Reduce(IReadOnlyList<ChartPoint> points, DateTime from, double pixelsPerTick, double gapMinutes)
	{
		var figures = new List<List<(double x, double value)>>();
		if (points is null)
			return figures;

		List<(double x, double value)> figure = null;
		var column = 0D;
		var count = 0; // Number of points in the current column
		(double x, double value) min = default, max = default;
		var isMinFirst = true;
		var lastTime = default(DateTime);

		void Flush()
		{
			if (count == 0)
				return;

			if (min.value == max.value)
			{
				figure.Add(min);
			}
			else if (isMinFirst)
			{
				figure.Add(min);
				figure.Add(max);
			}
			else
			{
				figure.Add(max);
				figure.Add(min);
			}
			count = 0;
		}

		foreach (var point in points)
		{
			if (point is null || double.IsNaN(point.Value))
				continue;

			var x = (point.Time - from).Ticks * pixelsPerTick;
			var current = (x, point.Value);
			var currentColumn = Math.Floor(x);

			var isBreak = (figure is null)
				|| point.IsBreak
				|| ((0 < gapMinutes) && (gapMinutes < (point.Time - lastTime).TotalMinutes));
			lastTime = point.Time;

			if (isBreak)
			{
				Flush();
				figure = new List<(double x, double value)>();
				figures.Add(figure);
			}
			else if (currentColumn != column)
			{
				Flush();
			}

			if (count == 0)
			{
				column = currentColumn;
				min = max = current;
				isMinFirst = true;
			}
			else if (point.Value < min.value)
			{
				min = current;
				isMinFirst = false;
			}
			else if (max.value < point.Value)
			{
				max = current;
				isMinFirst = true;
			}
			count++;
		}
		Flush();

		return figures;
	}
}

/// <summary>
/// Chart of series over time
/// </summary>
public class HistoryChart : FrameworkElement
{
	#region Property

	public IReadOnlyList<ChartSeries> Series
	{
		get { return (IReadOnlyList<ChartSeries>)GetValue(SeriesProperty); }
		set { SetValue(SeriesProperty, value); }
	}
	public static readonly DependencyProperty SeriesProperty =
		DependencyProperty.Register(
			"Series",
			typeof(IReadOnlyList<ChartSeries>),
			typeof(HistoryChart),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>
	/// Time at the left end (local time)
	/// </summary>
	public DateTime From
	{
		get { return (DateTime)GetValue(FromProperty); }
		set { SetValue(FromProperty, value); }
	}
	public static readonly DependencyProperty FromProperty =
		DependencyProperty.Register(
			"From",
			typeof(DateTime),
			typeof(HistoryChart),
			new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>
	/// Time at the right end (local time)
	/// </summary>
	public DateTime To
	{
		get { return (DateTime)GetValue(ToProperty); }
		set { SetValue(ToProperty, value); }
	}
	public static readonly DependencyProperty ToProperty =
		DependencyProperty.Register(
			"To",
			typeof(DateTime),
			typeof(HistoryChart),
			new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>
	/// Maximum value of the vertical axis (NaN to decide automatically)
	/// </summary>
	public double Maximum
	{
		get { return (double)GetValue(MaximumProperty); }
		set { SetValue(MaximumProperty, value); }
	}
	public static readonly DependencyProperty MaximumProperty =
		DependencyProperty.Register(
			"Maximum",
			typeof(double),
			typeof(HistoryChart),
			new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

	public string Unit
	{
		get { return (string)GetValue(UnitProperty); }
		set { SetValue(UnitProperty, value); }
	}
	public static readonly DependencyProperty UnitProperty =
		DependencyProperty.Register(
			"Unit",
			typeof(string),
			typeof(HistoryChart),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>
	/// Gap between consecutive points (in minutes) over which the line is broken (0 for never)
	/// </summary>
	public double GapMinutes
	{
		get { return (double)GetValue(GapMinutesProperty); }
		set { SetValue(GapMinutesProperty, value); }
	}
	public static readonly DependencyProperty GapMinutesProperty =
		DependencyProperty.Register(
			"GapMinutes",
			typeof(double),
			typeof(HistoryChart),
			new FrameworkPropertyMetadata(15D, FrameworkPropertyMetadataOptions.AffectsRender));

	#endregion

	private const double MarginLeft = 44D;
	private const double MarginRight = 8D;
	private const double MarginTop = 8D;
	private const double MarginBottom = 22D;
	private const double MinLength = 20D;
	private const double MarkerRadius = 3D;
	private const double FontSize = 11D;

	private static readonly Typeface _typeface = new("Segoe UI");

	protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
	{
		base.OnRenderSizeChanged(sizeInfo);

		InvalidateVisual();
	}

	protected override void OnRender(DrawingContext dc)
	{
		base.OnRender(dc);

		var plot = new Rect(
			MarginLeft,
			MarginTop,
			Math.Max(0D, this.ActualWidth - MarginLeft - MarginRight),
			Math.Max(0D, this.ActualHeight - MarginTop - MarginBottom));

		var from = From;
		var to = To;

		if ((plot.Width < MinLength) || (plot.Height < MinLength) || (to <= from))
			return;

		var series = (Series ?? Array.Empty<ChartSeries>())
			.Where(x => x is not null)
			.ToArray();

		var maximum = Maximum;
		if (double.IsNaN(maximum) || (maximum <= 0D))
		{
			var largest = series
				.SelectMany(x => x.Points ?? Array.Empty<ChartPoint>())
				.Where(x => (x is not null) && !double.IsNaN(x.Value))
				.Select(x => x.Value)
				.DefaultIfEmpty(0D)
				.Max();

			maximum = ChartScale.GetMaximum(largest);
		}

		var gridBrush = (TryFindResource("Chart.Grid") as Brush) ?? Brushes.Gray;
		var axisBrush = (TryFindResource("Chart.Axis") as Brush) ?? Brushes.LightGray;
		var gridPen = new Pen(gridBrush, 1D);
		gridPen.Freeze();

		var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

		FormattedText GetText(string text) =>
			new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, axisBrush, pixelsPerDip);

		// Horizontal lines and labels of values
		const int divisions = 4;
		for (int i = 0; i <= divisions; i++)
		{
			var y = Math.Round(plot.Bottom - plot.Height * i / divisions) + 0.5;
			dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

			var value = (maximum * i / divisions).ToString("0.##", CultureInfo.InvariantCulture);
			var text = GetText(value);

			if ((i == divisions) && !string.IsNullOrEmpty(Unit))
			{
				// The top label carries the unit. If it is too wide for the margin, the unit goes
				// inside the plot area.
				var unitText = GetText($"{value} {Unit}");
				if (unitText.Width <= MarginLeft - 6D)
					text = unitText;
				else
					dc.DrawText(GetText(Unit), new Point(plot.Left + 4D, plot.Top + 2D));
			}
			dc.DrawText(text, new Point(plot.Left - 4D - text.Width, y - text.Height / 2D));
		}

		// Vertical lines and labels of times
		var pixelsPerTick = plot.Width / (to - from).Ticks;

		foreach (var (time, label) in ChartScale.GetTicks(from, to))
		{
			var x = Math.Round(plot.Left + (time - from).Ticks * pixelsPerTick) + 0.5;
			dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

			var text = GetText(label);
			var left = Math.Min(x - text.Width / 2D, this.ActualWidth - text.Width);
			dc.DrawText(text, new Point(Math.Max(0D, left), plot.Bottom + 4D));
		}

		// Series
		var clip = new RectangleGeometry(new Rect(
			plot.Left - MarkerRadius - 1D,
			plot.Top - MarkerRadius - 1D,
			plot.Width + (MarkerRadius + 1D) * 2D,
			plot.Height + (MarkerRadius + 1D) * 2D));
		clip.Freeze();

		dc.PushClip(clip);

		Point GetPoint((double x, double value) point) =>
			new(plot.Left + point.x, plot.Bottom - plot.Height * Math.Min(Math.Max(point.value, 0D), maximum) / maximum);

		foreach (var s in series)
		{
			var brush = s.Brush ?? axisBrush;
			var linePen = new Pen(brush, 1.5) { LineJoin = PenLineJoin.Round };
			linePen.Freeze();

			var figures = ChartScale.Reduce(s.Points, from, pixelsPerTick, GapMinutes);

			var geometry = new StreamGeometry();
			using (var context = geometry.Open())
			{
				foreach (var figure in figures.Where(x => x.Count >= 2))
				{
					context.BeginFigure(GetPoint(figure[0]), false, false);
					context.PolyLineTo(figure.Skip(1).Select(x => GetPoint(x)).ToArray(), true, true);
				}
			}
			geometry.Freeze();
			dc.DrawGeometry(null, linePen, geometry);

			if (s.ShowMarkers)
			{
				foreach (var point in (s.Points ?? Array.Empty<ChartPoint>()).Where(x => (x is not null) && !double.IsNaN(x.Value)))
				{
					if ((point.Time < from) || (to < point.Time))
						continue;

					dc.DrawEllipse(brush, null, GetPoint(((point.Time - from).Ticks * pixelsPerTick, point.Value)), MarkerRadius, MarkerRadius);
				}
			}
			else
			{
				// A lone point has no line and so is shown as a dot.
				foreach (var figure in figures.Where(x => x.Count == 1))
					dc.DrawEllipse(brush, null, GetPoint(figure[0]), 1D, 1D);
			}

			if (s.HollowMarkers is { Count: > 0 })
			{
				var markerPen = new Pen(brush, 1D);
				markerPen.Freeze();

				foreach (var time in s.HollowMarkers)
				{
					if ((time < from) || (to < time))
						continue;

					dc.DrawEllipse(null, markerPen, new Point(plot.Left + (time - from).Ticks * pixelsPerTick, plot.Bottom), MarkerRadius, MarkerRadius);
				}
			}
		}

		dc.Pop();
	}
}
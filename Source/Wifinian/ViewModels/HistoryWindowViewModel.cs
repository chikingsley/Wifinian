using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

using Wifinian.Common;
using Wifinian.Models;
using Wifinian.Models.History;
using Wifinian.Models.Wlan;
using Wifinian.Views.Controls;

namespace Wifinian.ViewModels;

public class HistoryWindowViewModel : DisposableBase
{
	private readonly AppController _controller;

	/// <summary>
	/// Duration of history to be shown (in hours)
	/// </summary>
	public ReactiveProperty<int> RangeHours { get; }

	public ReactiveCommand RefreshCommand { get; }
	public ReactiveCommand OpenFolderCommand { get; }
	public ReactiveCommand ExportCommand { get; }

	public ReactivePropertySlim<bool> IsLoading { get; }

	public ObservableCollection<NetworkRowViewModel> Networks { get; } = new();

	public ReactivePropertySlim<IReadOnlyList<ChartSeries>> SignalSeries { get; }
	public ReactivePropertySlim<IReadOnlyList<ChartSeries>> SpeedSeries { get; }

	/// <summary>
	/// Start time of charts (local time)
	/// </summary>
	public ReactivePropertySlim<DateTime> From { get; }

	/// <summary>
	/// End time of charts (local time)
	/// </summary>
	public ReactivePropertySlim<DateTime> To { get; }

	public ObservableCollection<SpeedRow> Measurements { get; } = new();
	public ObservableCollection<EventRow> Decisions { get; } = new();

	private readonly Brush[] _brushes;
	private readonly HashSet<string> _hiddenKeys = new();
	private readonly SerialDisposable _rowSubscription = new();

	private static readonly TimeSpan _reloadInterval = TimeSpan.FromSeconds(5);

	internal HistoryWindowViewModel(AppController controller)
	{
		this._controller = controller ?? throw new ArgumentNullException(nameof(controller));

		_brushes = Enumerable.Range(1, HistorySnapshot.BrushCount)
			.Select(x => (Application.Current?.TryFindResource($"Chart.Series{x}") as Brush) ?? Brushes.LightGray)
			.ToArray();

		_rowSubscription.AddTo(this.Subscription);

		RangeHours = new ReactiveProperty<int>(24)
			.AddTo(this.Subscription);

		IsLoading = new ReactivePropertySlim<bool>()
			.AddTo(this.Subscription);

		SignalSeries = new ReactivePropertySlim<IReadOnlyList<ChartSeries>>(Array.Empty<ChartSeries>())
			.AddTo(this.Subscription);
		SpeedSeries = new ReactivePropertySlim<IReadOnlyList<ChartSeries>>(Array.Empty<ChartSeries>())
			.AddTo(this.Subscription);

		var now = DateTime.Now;
		From = new ReactivePropertySlim<DateTime>(now.AddHours(-RangeHours.Value))
			.AddTo(this.Subscription);
		To = new ReactivePropertySlim<DateTime>(now)
			.AddTo(this.Subscription);

		RefreshCommand = new ReactiveCommand()
			.AddTo(this.Subscription);

		OpenFolderCommand = new ReactiveCommand()
			.AddTo(this.Subscription);
		OpenFolderCommand
			.Subscribe(_ => OpenFolder(_controller.History.FolderPath))
			.AddTo(this.Subscription);

		ExportCommand = new ReactiveCommand()
			.AddTo(this.Subscription);
		ExportCommand
			.Subscribe(async _ => await ExportAsync())
			.AddTo(this.Subscription);

		// Signal samples are appended so often that they do not cause reloading.
		var historyChanged = Observable.FromEvent<EventHandler<HistoryKind>, HistoryKind>(
			h => (_, e) => h(e),
			h => _controller.History.Changed += h,
			h => _controller.History.Changed -= h)
			.Where(x => x is not HistoryKind.Signal)
			.Sample(_reloadInterval)
			.ObserveOnUIDispatcher();

		// RangeHours emits its value when subscribed and so loads the history at construction.
		Observable.Merge(
			RangeHours.Select(_ => Unit.Default),
			RefreshCommand.Select(_ => Unit.Default),
			historyChanged.Select(_ => Unit.Default))
			.Subscribe(async _ => await LoadAsync())
			.AddTo(this.Subscription);
	}

	/// <summary>
	/// Opens a folder, that of the database of history or that of exported files.
	/// </summary>
	private static void OpenFolder(string folderPath)
	{
		try
		{
			Directory.CreateDirectory(folderPath);
			Process.Start(folderPath);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to open history folder." + Environment.NewLine
				+ ex);
		}
	}

	private bool _isExporting;

	/// <summary>
	/// Exports the history of the current range to CSV files and opens the folder of them.
	/// This method must be called on the UI thread.
	/// </summary>
	private async Task ExportAsync()
	{
		if (_isExporting)
			return;

		try
		{
			_isExporting = true;

			var toUtc = DateTime.UtcNow;
			var fromUtc = toUtc.AddHours(-RangeHours.Value);

			var history = _controller.History;
			var folderPath = AppDataService.HistoryFolderPath;

			var filePaths = await Task.Run(() => history.ExportCsv(folderPath, fromUtc, toUtc));
			if (filePaths.Count > 0)
				OpenFolder(folderPath);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to export history." + Environment.NewLine
				+ ex);
		}
		finally
		{
			_isExporting = false;
		}
	}

	#region Load

	private HistorySnapshot _snapshot;
	private int _loadCount;

	/// <summary>
	/// Loads the history. This method must be called on the UI thread.
	/// </summary>
	private async Task LoadAsync()
	{
		var count = ++_loadCount;

		try
		{
			if (_disposed)
				return;

			IsLoading.Value = true;

			var toUtc = DateTime.UtcNow;
			var fromUtc = toUtc.AddHours(-RangeHours.Value);

			var history = _controller.History;
			var profiles = _controller.GetProfiles();
			var avoidedNames = Settings.Current.AvoidedProfiles;

			// Reading the database, grouping and reducing points are done outside the UI thread.
			var snapshot = await Task.Run(() => HistorySnapshot.Build(
				history.ReadSignals(fromUtc, toUtc),
				history.ReadSpeeds(fromUtc, toUtc),
				history.ReadEvents(fromUtc, toUtc),
				profiles,
				avoidedNames,
				fromUtc,
				toUtc));

			// A newer load will bring newer history.
			if (_disposed || (count != _loadCount))
				return;

			Apply(snapshot);
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to load history." + Environment.NewLine
				+ ex);
		}
		finally
		{
			if (!_disposed && (count == _loadCount))
				IsLoading.Value = false;
		}
	}

	private void Apply(HistorySnapshot snapshot)
	{
		_snapshot = snapshot;

		var subscription = new CompositeDisposable();
		_rowSubscription.Disposable = subscription;

		Networks.Clear();
		foreach (var network in snapshot.Networks)
		{
			var key = network.Key;
			var row = new NetworkRowViewModel(network, _brushes[network.BrushIndex % _brushes.Length], !_hiddenKeys.Contains(key));
			row.IsShown
				.Skip(1)
				.Subscribe(isShown =>
				{
					if (isShown)
						_hiddenKeys.Remove(key);
					else
						_hiddenKeys.Add(key);

					BuildSeries();
				})
				.AddTo(subscription);
			row.AddTo(subscription);

			Networks.Add(row);
		}

		Measurements.Clear();
		foreach (var row in snapshot.Measurements)
			Measurements.Add(row);

		Decisions.Clear();
		foreach (var row in snapshot.Decisions)
			Decisions.Add(row);

		From.Value = snapshot.From;
		To.Value = snapshot.To;

		BuildSeries();
	}

	/// <summary>
	/// Builds series of charts from the loaded history without reloading it.
	/// </summary>
	private void BuildSeries()
	{
		try
		{
			if (_disposed || (_snapshot is null))
				return;

			var networks = _snapshot.Networks
				.Where(x => !_hiddenKeys.Contains(x.Key))
				.OrderBy(x => x.BrushIndex)
				.ToArray();

			SignalSeries.Value = networks
				.Where(x => x.SignalPoints.Count > 0)
				.Select(x => new ChartSeries
				{
					Name = x.Name,
					Brush = _brushes[x.BrushIndex % _brushes.Length],
					Points = x.SignalPoints,
					ShowMarkers = false
				})
				.ToArray();

			SpeedSeries.Value = networks
				.Where(x => (x.SpeedPoints.Count > 0) || (x.FailedTimes.Count > 0))
				.Select(x => new ChartSeries
				{
					Name = x.Name,
					Brush = _brushes[x.BrushIndex % _brushes.Length],
					Points = x.SpeedPoints,
					ShowMarkers = true,
					HollowMarkers = x.FailedTimes
				})
				.ToArray();
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to build series of history." + Environment.NewLine
				+ ex);
		}
	}

	#endregion

	#region Dispose

	private bool _disposed = false;

	protected override void Dispose(bool disposing)
	{
		if (_disposed)
			return;

		_disposed = true;

		base.Dispose(disposing);
	}

	#endregion
}

/// <summary>
/// Row of a network in the summary of history
/// </summary>
public class NetworkRowViewModel : IDisposable
{
	public Brush Brush { get; }

	/// <summary>
	/// Whether the series of this network are shown in charts
	/// </summary>
	public ReactivePropertySlim<bool> IsShown { get; }

	public string Name { get; }
	public string SignalNow { get; }
	public string AverageSignal { get; }
	public string TypicalDown { get; }
	public string TypicalUp { get; }
	public string Latency { get; }
	public string Tests { get; }
	public string LastTested { get; }
	public string IsAvoided { get; }

	internal NetworkRowViewModel(HistoryNetwork network, Brush brush, bool isShown)
	{
		Brush = brush;
		IsShown = new ReactivePropertySlim<bool>(isShown);

		Name = network.Name;
		SignalNow = network.SignalNow;
		AverageSignal = network.AverageSignal;
		TypicalDown = network.TypicalDown;
		TypicalUp = network.TypicalUp;
		Latency = network.Latency;
		Tests = network.Tests;
		LastTested = network.LastTested;
		IsAvoided = network.IsAvoided;
	}

	public void Dispose() => IsShown.Dispose();
}

/// <summary>
/// Row of a speed measurement in history
/// </summary>
public class SpeedRow
{
	public string Time { get; internal set; }
	public string Network { get; internal set; }
	public string Bssid { get; internal set; }
	public string Signal { get; internal set; }
	public string LinkRate { get; internal set; }
	public string Latency { get; internal set; }
	public string Down { get; internal set; }
	public string Up { get; internal set; }
	public string Trigger { get; internal set; }
	public string Result { get; internal set; }
}

/// <summary>
/// Row of a switching event or decision in history
/// </summary>
public class EventRow
{
	public string Time { get; internal set; }
	public string Kind { get; internal set; }
	public string From { get; internal set; }
	public string To { get; internal set; }
	public string Reason { get; internal set; }
}

/// <summary>
/// History of a network within a range of time
/// </summary>
internal class HistoryNetwork
{
	public string Key { get; set; }
	public string ProfileName { get; set; }
	public Guid InterfaceId { get; set; }

	/// <summary>
	/// Name to be shown
	/// </summary>
	public string Name { get; set; }

	/// <summary>
	/// Index of the brush assigned by alphabetical order of name
	/// </summary>
	public int BrushIndex { get; set; }

	public string SignalNow { get; set; }
	public string AverageSignal { get; set; }
	public string TypicalDown { get; set; }
	public string TypicalUp { get; set; }
	public string Latency { get; set; }
	public string Tests { get; set; }
	public string LastTested { get; set; }
	public string IsAvoided { get; set; }

	/// <summary>
	/// Median of download speed of successful measurements (Mbps)
	/// </summary>
	public double? DownMbps { get; set; }

	public IReadOnlyList<ChartPoint> SignalPoints { get; set; } = Array.Empty<ChartPoint>();
	public IReadOnlyList<ChartPoint> SpeedPoints { get; set; } = Array.Empty<ChartPoint>();

	/// <summary>
	/// Times of measurements which did not succeed (local time)
	/// </summary>
	public IReadOnlyList<DateTime> FailedTimes { get; set; } = Array.Empty<DateTime>();
}

/// <summary>
/// History within a range of time arranged for the history window
/// </summary>
internal class HistorySnapshot
{
	/// <summary>
	/// Number of brushes for series
	/// </summary>
	public const int BrushCount = 8;

	/// <summary>
	/// Maximum number of rows of measurements and of decisions
	/// </summary>
	public const int MaxRowCount = 500;

	/// <summary>
	/// Number of time sections into which points of signal are reduced
	/// </summary>
	public const int SectionCount = 2000;

	/// <summary>
	/// Gap between consecutive signal samples over which the network is regarded as having
	/// been out of range
	/// </summary>
	public static readonly TimeSpan SignalGap = TimeSpan.FromMinutes(15);

	private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
	private const string ValueFormat = "0.#";

	/// <summary>
	/// Start time (local time)
	/// </summary>
	public DateTime From { get; private set; }

	/// <summary>
	/// End time (local time)
	/// </summary>
	public DateTime To { get; private set; }

	/// <summary>
	/// Networks in descending order of typical download speed
	/// </summary>
	public IReadOnlyList<HistoryNetwork> Networks { get; private set; }

	/// <summary>
	/// Speed measurements from the newest
	/// </summary>
	public IReadOnlyList<SpeedRow> Measurements { get; private set; }

	/// <summary>
	/// Switching events and decisions from the newest
	/// </summary>
	public IReadOnlyList<EventRow> Decisions { get; private set; }

	/// <summary>
	/// Builds a snapshot from rows of history. This method uses no UI object and so can be
	/// called on any thread.
	/// </summary>
	/// <param name="signals">Signal samples in order of time</param>
	/// <param name="speeds">Speed samples in order of time</param>
	/// <param name="events">Switching events in order of time</param>
	/// <param name="profiles">Current profiles</param>
	/// <param name="avoidedNames">Names of avoided profiles</param>
	/// <param name="fromUtc">Start time (UTC)</param>
	/// <param name="toUtc">End time (UTC)</param>
	/// <param name="toLocal">Function to convert UTC to local time</param>
	/// <returns>Snapshot</returns>
	public static HistorySnapshot Build(
		IReadOnlyList<SignalSample> signals,
		IReadOnlyList<SpeedSample> speeds,
		IReadOnlyList<SwitchEvent> events,
		IReadOnlyList<ProfileItem> profiles,
		IReadOnlyCollection<string> avoidedNames,
		DateTime fromUtc,
		DateTime toUtc,
		Func<DateTime, DateTime> toLocal = null)
	{
		signals ??= Array.Empty<SignalSample>();
		speeds ??= Array.Empty<SpeedSample>();
		events ??= Array.Empty<SwitchEvent>();
		profiles ??= Array.Empty<ProfileItem>();
		avoidedNames ??= Array.Empty<string>();
		toLocal ??= (x => CsvLine.ToUtc(x).ToLocalTime());

		var signalGroups = signals
			.Where(x => x is not null)
			.GroupBy(x => x.Key)
			.ToDictionary(x => x.Key, x => x.ToArray());
		var speedGroups = speeds
			.Where(x => x is not null)
			.GroupBy(x => x.Key)
			.ToDictionary(x => x.Key, x => x.ToArray());

		var networks = new List<HistoryNetwork>();

		foreach (var key in signalGroups.Keys.Union(speedGroups.Keys))
		{
			signalGroups.TryGetValue(key, out SignalSample[] subSignals);
			speedGroups.TryGetValue(key, out SpeedSample[] subSpeeds);
			subSignals ??= Array.Empty<SignalSample>();
			subSpeeds ??= Array.Empty<SpeedSample>();

			var profileName = subSignals.FirstOrDefault()?.Profile ?? subSpeeds[0].Profile;
			var interfaceId = subSignals.FirstOrDefault()?.InterfaceId ?? subSpeeds[0].InterfaceId;

			var profile = profiles.FirstOrDefault(x => (x is not null)
				&& string.Equals(x.Name, profileName, StringComparison.Ordinal)
				&& (x.InterfaceId == interfaceId));

			var successes = subSpeeds.Where(x => x.Result is SpeedResult.Ok).ToArray();
			var downMbps = GetMedian(successes.Select(x => x.DownMbps));

			networks.Add(new HistoryNetwork
			{
				Key = key,
				ProfileName = profileName,
				InterfaceId = interfaceId,
				SignalNow = (profile is { Signal: > 0 }) ? Format(profile.Signal) : string.Empty,
				AverageSignal = (subSignals.Length > 0) ? Format(subSignals.Average(x => x.Signal)) : string.Empty,
				DownMbps = downMbps,
				TypicalDown = Format(downMbps),
				TypicalUp = Format(GetMedian(successes.Select(x => x.UpMbps))),
				Latency = Format(GetMedian(successes.Select(x => x.LatencyMs))),
				Tests = (subSpeeds.Length > 0) ? $"{subSpeeds.Count(x => x.Valid)}/{subSpeeds.Length}" : string.Empty,
				LastTested = (subSpeeds.Length > 0) ? toLocal(subSpeeds.Max(x => x.Time)).ToString("g", CultureInfo.CurrentCulture) : string.Empty,
				IsAvoided = avoidedNames.Contains(profileName) ? "Yes" : string.Empty,
				SignalPoints = ReduceSignals(subSignals, fromUtc, toUtc, toLocal),
				SpeedPoints = successes
					.Where(x => x.DownMbps.HasValue)
					.Select(x => new ChartPoint { Time = toLocal(x.Time), Value = x.DownMbps.Value })
					.ToArray(),
				FailedTimes = subSpeeds
					.Where(x => x.Result is not SpeedResult.Ok)
					.Select(x => toLocal(x.Time))
					.ToArray()
			});
		}

		// Profiles of the same name on two adapters are told apart by the adapters.
		foreach (var group in networks.GroupBy(x => x.ProfileName, StringComparer.Ordinal))
		{
			var isMultiple = (group.Count() > 1);

			foreach (var network in group)
			{
				if (!isMultiple)
				{
					network.Name = network.ProfileName;
					continue;
				}

				var description = profiles
					.FirstOrDefault(x => (x is not null) && (x.InterfaceId == network.InterfaceId))?.InterfaceDescription;
				if (string.IsNullOrWhiteSpace(description))
					description = network.InterfaceId.ToString("D").Substring(0, 8);

				network.Name = $"{network.ProfileName} ({description})";
			}
		}

		int index = 0;
		foreach (var network in networks
			.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => x.Key, StringComparer.Ordinal))
		{
			network.BrushIndex = index++ % BrushCount;
		}

		var names = networks.ToDictionary(x => x.Key, x => x.Name);

		return new HistorySnapshot
		{
			From = toLocal(fromUtc),
			To = toLocal(toUtc),
			Networks = networks
				.OrderByDescending(x => x.DownMbps.HasValue)
				.ThenByDescending(x => x.DownMbps ?? 0D)
				.ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray(),
			Measurements = speeds
				.Where(x => x is not null)
				.Reverse()
				.Take(MaxRowCount)
				.Select(x => new SpeedRow
				{
					Time = toLocal(x.Time).ToString(TimeFormat, CultureInfo.InvariantCulture),
					Network = names.TryGetValue(x.Key, out string name) ? name : x.Profile,
					Bssid = x.Bssid ?? string.Empty,
					Signal = (x.Signal > 0) ? Format(x.Signal) : string.Empty,
					LinkRate = Format(x.LinkRate),
					Latency = Format(x.LatencyMs),
					Down = Format(x.DownMbps),
					Up = Format(x.UpMbps),
					Trigger = x.Trigger.ToString(),
					Result = x.Result.ToString()
				})
				.ToArray(),
			Decisions = events
				.Where(x => x is not null)
				.Reverse()
				.Take(MaxRowCount)
				.Select(x => new EventRow
				{
					Time = toLocal(x.Time).ToString(TimeFormat, CultureInfo.InvariantCulture),
					Kind = x.Kind.ToString(),
					From = x.From ?? string.Empty,
					To = x.To ?? string.Empty,
					Reason = x.Reason ?? string.Empty
				})
				.ToArray()
		};
	}

	/// <summary>
	/// Reduces signal samples to at most the first, the minimum, the maximum and the last
	/// samples per section of time and marks where the network was out of range.
	/// </summary>
	internal static IReadOnlyList<ChartPoint> ReduceSignals(IReadOnlyList<SignalSample> samples, DateTime fromUtc, DateTime toUtc, Func<DateTime, DateTime> toLocal)
	{
		var points = new List<ChartPoint>();
		if (samples is not { Count: > 0 })
			return points;

		var sectionTicks = Math.Max(1L, (toUtc - fromUtc).Ticks / SectionCount);

		var section = new List<SignalSample>();
		var sectionIndex = 0L;
		var isBreak = false; // Whether the line is broken before the current section
		SignalSample last = null;

		void Flush()
		{
			if (section.Count == 0)
				return;

			var min = section[0];
			var max = section[0];
			foreach (var sample in section)
			{
				if (sample.Signal < min.Signal)
					min = sample;
				if (max.Signal < sample.Signal)
					max = sample;
			}

			// Each sample is taken only once and the order of time is kept.
			var count = 0;
			for (int i = 0; i < section.Count; i++)
			{
				var sample = section[i];
				if ((i > 0) && (i < section.Count - 1) && !ReferenceEquals(sample, min) && !ReferenceEquals(sample, max))
					continue;

				points.Add(new ChartPoint
				{
					Time = toLocal(sample.Time),
					Value = sample.Signal,
					IsBreak = isBreak && (count == 0)
				});
				count++;
			}
			section.Clear();
		}

		foreach (var sample in samples)
		{
			var index = (sample.Time - fromUtc).Ticks / sectionTicks;
			var isGap = (last is not null) && (SignalGap < sample.Time - last.Time);

			if (isGap || (index != sectionIndex))
			{
				Flush();
				sectionIndex = index;
				isBreak = isGap;
			}
			section.Add(sample);
			last = sample;
		}
		Flush();

		return points;
	}

	internal static double? GetMedian(IEnumerable<double?> values)
	{
		var buffer = values
			.Where(x => x.HasValue)
			.Select(x => x.Value)
			.OrderBy(x => x)
			.ToArray();
		if (buffer.Length == 0)
			return null;

		var middle = buffer.Length / 2;
		return (buffer.Length % 2 == 1)
			? buffer[middle]
			: (buffer[middle - 1] + buffer[middle]) / 2D;
	}

	private static string Format(double? value) =>
		value.HasValue ? value.Value.ToString(ValueFormat, CultureInfo.InvariantCulture) : string.Empty;

	private static string Format(int? value) =>
		value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
}
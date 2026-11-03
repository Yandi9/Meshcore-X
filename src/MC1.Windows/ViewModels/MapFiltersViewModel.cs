using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Maps;

namespace MC1.Windows.ViewModels;

/// <summary>A preset button of the advanced map filters ("2h", "Direct").</summary>
public sealed partial class MapFilterPreset(string label, object value) : ObservableObject
{
    public string Label { get; } = label;
    public object Value { get; } = value;
    [ObservableProperty] private bool _isSelected;
}

public sealed record MapDurationUnitOption(MapDurationUnit Unit, string Name);

/// <summary>
/// The map's Advanced Filters (iPhone: Advanced Filters sheet): a rolling "last heard" window and a hop-count range,
/// each with presets and a custom minimum/maximum. Edits apply to the map straight away.
/// </summary>
public sealed partial class MapFiltersViewModel : ViewModelBase
{
    private readonly MapViewModel _map;
    private bool _pulling;

    public MapFiltersViewModel(MapViewModel map)
    {
        _map = map;
        foreach (var p in MapLastHeardRange.Presets) LastHeardPresets.Add(new MapFilterPreset(p.PresetLabel, p));
        foreach (var p in MapHopRange.Presets) HopPresets.Add(new MapFilterPreset(p.PresetLabel, p));
        Units = Enum.GetValues<MapDurationUnit>().Select(u => new MapDurationUnitOption(u, MapDuration.UnitName(u))).ToList();
        _minUnit = _maxUnit = Units[(int)MapDurationUnit.Hours];
        Pull();
    }

    /// <summary>Set by the dialog host.</summary>
    public Action? Close { get; set; }

    public ObservableCollection<MapFilterPreset> LastHeardPresets { get; } = new();
    public ObservableCollection<MapFilterPreset> HopPresets { get; } = new();
    public IReadOnlyList<MapDurationUnitOption> Units { get; }
    public decimal DurationMinimum => MapDuration.MinValue;
    public decimal DurationMaximum => MapDuration.MaxValue;
    public decimal HopMaximum => MapHopRange.MaxHops;

    // Empty = no bound.
    [ObservableProperty] private decimal? _minAge;
    [ObservableProperty] private decimal? _maxAge;
    [ObservableProperty] private MapDurationUnitOption _minUnit;
    [ObservableProperty] private MapDurationUnitOption _maxUnit;
    [ObservableProperty] private decimal? _minHops;
    [ObservableProperty] private decimal? _maxHops;
    [ObservableProperty] private string _lastHeardSummary = "";
    [ObservableProperty] private string _hopsSummary = "";
    [ObservableProperty] private bool _canReset;

    private MapAdvancedFilter Filter => _map.Filter;

    /// <summary>Shows the map's current filter (also after a bound was moved to keep the range valid).</summary>
    private void Pull()
    {
        _pulling = true;
        try
        {
            var f = Filter;
            MinAge = f.LastHeard.MinAge?.Value;
            MaxAge = f.LastHeard.MaxAge?.Value;
            if (f.LastHeard.MinAge is { } min) MinUnit = Units[(int)min.Unit];
            if (f.LastHeard.MaxAge is { } max) MaxUnit = Units[(int)max.Unit];
            MinHops = f.Hops.Min > 0 ? f.Hops.Min : null;
            MaxHops = f.Hops.Max;
            foreach (var p in LastHeardPresets) p.IsSelected = Equals(p.Value, f.LastHeard);
            foreach (var p in HopPresets) p.IsSelected = Equals(p.Value, f.Hops);
            LastHeardSummary = f.LastHeard.SummaryText;
            HopsSummary = f.Hops.SummaryText;
            CanReset = f.IsActive;
        }
        finally { _pulling = false; }
    }

    private void Apply(MapAdvancedFilter filter)
    {
        _map.Filter = filter;
        Pull();
    }

    private static MapDuration? Duration(decimal? value, MapDurationUnitOption unit) =>
        value is { } v ? new MapDuration((int)Math.Round(v), unit.Unit) : null;

    partial void OnMinAgeChanged(decimal? value) { if (!_pulling) Apply(Filter with { LastHeard = Filter.LastHeard.WithMinAge(Duration(value, MinUnit)) }); }
    partial void OnMaxAgeChanged(decimal? value) { if (!_pulling) Apply(Filter with { LastHeard = Filter.LastHeard.WithMaxAge(Duration(value, MaxUnit)) }); }
    partial void OnMinUnitChanged(MapDurationUnitOption value) { if (!_pulling && MinAge is not null) Apply(Filter with { LastHeard = Filter.LastHeard.WithMinAge(Duration(MinAge, value)) }); }
    partial void OnMaxUnitChanged(MapDurationUnitOption value) { if (!_pulling && MaxAge is not null) Apply(Filter with { LastHeard = Filter.LastHeard.WithMaxAge(Duration(MaxAge, value)) }); }
    partial void OnMinHopsChanged(decimal? value) { if (!_pulling) Apply(Filter with { Hops = Filter.Hops.WithMin(value is { } v ? (int)Math.Round(v) : 0) }); }
    partial void OnMaxHopsChanged(decimal? value) { if (!_pulling) Apply(Filter with { Hops = Filter.Hops.WithMax(value is { } v ? (int)Math.Round(v) : null) }); }

    [RelayCommand]
    private void SelectPreset(MapFilterPreset preset)
    {
        switch (preset.Value)
        {
            case MapLastHeardRange r: Apply(Filter with { LastHeard = r }); break;
            case MapHopRange h: Apply(Filter with { Hops = h }); break;
        }
    }

    [RelayCommand]
    private void Reset() => Apply(new MapAdvancedFilter());

    [RelayCommand]
    private void Done() => Close?.Invoke();
}

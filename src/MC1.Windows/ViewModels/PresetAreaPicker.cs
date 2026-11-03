using CommunityToolkit.Mvvm.ComponentModel;
using MC1.Core.Utilities;

namespace MC1.Windows.ViewModels;

/// <summary>
/// "Where are you?" — the country and, for the US, Canada and Australia, the state or province used to recommend a
/// radio preset (the iPhone's preset location). Saved in the app's settings; it never changes the radio. Until the user
/// picks one it follows this PC's country.
/// </summary>
public sealed partial class PresetAreaPicker : ViewModelBase
{
    private bool _updating;

    public PresetAreaPicker()
    {
        Countries = RegionalAreas.CountriesByName();
        var saved = Core.Settings.Current;
        var selection = RegionalAreas.Selection(saved.PresetCountry, saved.PresetArea);
        _country = selection is null ? null : Countries.FirstOrDefault(c => c.Id == selection.CountryCode);
        _subdivisions = RegionalAreas.SubdivisionsFor(_country?.Id);
        _subdivision = selection?.AdministrativeAreaCode is { } area ? _subdivisions.FirstOrDefault(s => s.Id == area) : null;
    }

    public IReadOnlyList<RegionalAreas.Country> Countries { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SubdivisionLabel), nameof(SubdivisionPlaceholder), nameof(Selection), nameof(PlaceText), nameof(IsIncomplete))]
    private RegionalAreas.Country? _country;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSubdivisions))]
    private IReadOnlyList<RegionalAreas.Subdivision> _subdivisions;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Selection), nameof(PlaceText), nameof(IsIncomplete))]
    private RegionalAreas.Subdivision? _subdivision;

    /// <summary>Raised after the user picks another country or state/province.</summary>
    public event Action? Changed;

    public bool ShowSubdivisions => Subdivisions.Count > 1;

    public string SubdivisionLabel => RegionalAreas.AdministrativeAreaKindFor(Country?.Id) == RegionalAreas.AdministrativeAreaKind.Province
        ? L.T("Province") : L.T("State");

    public string SubdivisionPlaceholder => RegionalAreas.AdministrativeAreaKindFor(Country?.Id) == RegionalAreas.AdministrativeAreaKind.Province
        ? L.T("Pick your province") : L.T("Pick your state");

    /// <summary>The place picked, or null when the country isn't one with presets.</summary>
    public RegionSelection? Selection => Country is null ? null : new RegionSelection(Country.Id, Subdivision?.Id);

    /// <summary>True when the country has states/provinces and none is picked (an area preset may be missed).</summary>
    public bool IsIncomplete => Selection is { } s && RegionalAreas.IsIncomplete(s);

    /// <summary>"British Columbia", "Queensland, Australia", "Germany" — or empty.</summary>
    public string PlaceText => Selection is { } s ? RegionalAreas.DisplayName(s) : "";

    /// <summary>The preset recommended for the place picked, if any.</summary>
    public RadioPreset? Recommended => Selection is { } s ? RadioPresets.Recommended(s) : null;

    /// <summary>"Recommended for British Columbia: Canada: BC", or why there is none.</summary>
    public string RecommendationText => Selection is not { } s
        ? L.T("Pick your country to see the preset used there.")
        : Recommended is { } p
            ? L.F("Recommended for {0}: {1}", RegionalAreas.DisplayName(s), p.Name)
            : L.F("No community preset is listed for {0} — ask your local mesh which settings they use.", RegionalAreas.DisplayName(s));

    partial void OnCountryChanged(RegionalAreas.Country? value)
    {
        _updating = true;
        try
        {
            Subdivisions = RegionalAreas.SubdivisionsFor(value?.Id);
            Subdivision = null;
        }
        finally { _updating = false; }
        Save();
    }

    partial void OnSubdivisionChanged(RegionalAreas.Subdivision? value)
    {
        if (!_updating) Save();
    }

    private void Save()
    {
        var country = Country?.Id;
        var area = Subdivision?.Id;
        Core.Settings.Update(s =>
        {
            s.PresetCountry = country;
            s.PresetArea = area;
        });
        OnPropertyChanged(nameof(Recommended));
        OnPropertyChanged(nameof(RecommendationText));
        Changed?.Invoke();
    }
}

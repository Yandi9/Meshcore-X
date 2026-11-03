using System.Text.Json.Serialization;
using MC1.Core.Models;
using MeshCore;

namespace MC1.Core.Maps;

// Advanced map filters (port of the iPhone app's MapFilter / MapFilterRanges / MapFilterDescriptions): a rolling
// "last heard" window and a hop-count range. Both are stored in the settings and AND together.

/// <summary>Unit a user picks when entering a relative time bound.</summary>
public enum MapDurationUnit { Minutes, Hours, Days }

/// <summary>A relative time span that keeps the unit the user entered, so it is shown back the same way.</summary>
public sealed record MapDuration
{
    /// <summary>Keeps the span far from overflow while allowing years of history in any unit.</summary>
    public const int MinValue = 1, MaxValue = 9999;

    public MapDuration() { }

    public MapDuration(int value, MapDurationUnit unit)
    {
        Value = Math.Clamp(value, MinValue, MaxValue);
        Unit = unit;
    }

    public int Value { get; init; } = 1;
    public MapDurationUnit Unit { get; init; } = MapDurationUnit.Hours;

    [JsonIgnore]
    public TimeSpan Span => Unit switch
    {
        MapDurationUnit.Minutes => TimeSpan.FromMinutes(Math.Clamp(Value, MinValue, MaxValue)),
        MapDurationUnit.Days => TimeSpan.FromDays(Math.Clamp(Value, MinValue, MaxValue)),
        _ => TimeSpan.FromHours(Math.Clamp(Value, MinValue, MaxValue)),
    };

    /// <summary>Compact form in the entered unit ("15m", "2h", "3d").</summary>
    [JsonIgnore]
    public string Abbreviated => Unit switch
    {
        MapDurationUnit.Minutes => L.F("{0}m", Value),
        MapDurationUnit.Days => L.F("{0}d", Value),
        _ => L.F("{0}h", Value),
    };

    public static string UnitName(MapDurationUnit unit) => unit switch
    {
        MapDurationUnit.Minutes => L.T("Minutes"),
        MapDurationUnit.Days => L.T("Days"),
        _ => L.T("Hours"),
    };

    internal MapDuration Sanitized() => new(Value, Enum.IsDefined(Unit) ? Unit : MapDurationUnit.Hours);
}

/// <summary>"Last heard between <see cref="MinAge"/> and <see cref="MaxAge"/> ago"; both ends inclusive and optional.</summary>
public sealed record MapLastHeardRange
{
    /// <summary>Heard at least this long ago (hides nodes heard more recently).</summary>
    public MapDuration? MinAge { get; init; }
    /// <summary>Heard at most this long ago (hides nodes quiet for longer).</summary>
    public MapDuration? MaxAge { get; init; }

    public static MapLastHeardRange Any { get; } = new();

    public static MapLastHeardRange Within(MapDuration duration) => new() { MaxAge = duration };

    public static IReadOnlyList<MapLastHeardRange> Presets { get; } =
    [
        Any,
        Within(new(15, MapDurationUnit.Minutes)),
        Within(new(1, MapDurationUnit.Hours)),
        Within(new(2, MapDurationUnit.Hours)),
        Within(new(6, MapDurationUnit.Hours)),
        Within(new(24, MapDurationUnit.Hours)),
        Within(new(3, MapDurationUnit.Days)),
        Within(new(7, MapDurationUnit.Days)),
    ];

    [JsonIgnore]
    public bool IsActive => MinAge is not null || MaxAge is not null;

    /// <summary>A node never heard fails any active range. A time in the future (clock skew) counts as just heard.</summary>
    public bool Contains(DateTimeOffset? heardAt, DateTimeOffset now)
    {
        if (!IsActive) return true;
        if (heardAt is not { } at) return false;
        var age = now - at;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (MinAge is { } min && age < min.Span) return false;
        if (MaxAge is { } max && age > max.Span) return false;
        return true;
    }

    /// <summary>Sets the minimum; a maximum below it moves up to match.</summary>
    public MapLastHeardRange WithMinAge(MapDuration? duration)
    {
        var r = this with { MinAge = duration };
        return r.MinAge is { } min && r.MaxAge is { } max && min.Span > max.Span ? r with { MaxAge = min } : r;
    }

    /// <summary>Sets the maximum; a minimum above it moves down to match.</summary>
    public MapLastHeardRange WithMaxAge(MapDuration? duration)
    {
        var r = this with { MaxAge = duration };
        return r.MinAge is { } min && r.MaxAge is { } max && min.Span > max.Span ? r with { MinAge = max } : r;
    }

    internal MapLastHeardRange Sanitized() => new MapLastHeardRange { MaxAge = MaxAge?.Sanitized() }.WithMinAge(MinAge?.Sanitized());

    /// <summary>Text of the chip shown over the map ("Heard ≤ 2h ago").</summary>
    [JsonIgnore]
    public string ChipText => (MinAge, MaxAge) switch
    {
        ({ } min, { } max) => L.F("Heard {0}–{1} ago", min.Abbreviated, max.Abbreviated),
        ({ } min, null) => L.F("Heard ≥ {0} ago", min.Abbreviated),
        (null, { } max) => L.F("Heard ≤ {0} ago", max.Abbreviated),
        _ => L.T("Any Time"),
    };

    /// <summary>Sentence describing what the range shows.</summary>
    [JsonIgnore]
    public string SummaryText => (MinAge, MaxAge) switch
    {
        ({ } min, { } max) => L.F("Showing nodes last heard between {0} and {1} ago.", min.Abbreviated, max.Abbreviated),
        ({ } min, null) => L.F("Showing nodes last heard at least {0} ago.", min.Abbreviated),
        (null, { } max) => L.F("Showing nodes last heard within the last {0}.", max.Abbreviated),
        _ => L.T("Showing nodes no matter when they were last heard."),
    };

    /// <summary>Short label for a preset button.</summary>
    [JsonIgnore]
    public string PresetLabel => MaxAge is { } max && MinAge is null ? max.Abbreviated : L.T("Any Time");
}

/// <summary>Inclusive hop-count range; <see cref="Max"/> null means no upper limit.</summary>
public sealed record MapHopRange
{
    /// <summary>The path length is a 6-bit field on the wire, so 63 hops is the protocol ceiling.</summary>
    public const int MaxHops = 63;

    public int Min { get; init; }
    public int? Max { get; init; }

    public static MapHopRange Any { get; } = new();
    public static MapHopRange Direct { get; } = new() { Max = 0 };

    public static IReadOnlyList<MapHopRange> Presets { get; } = [Any, Direct, Create(0, 1), Create(0, 2), Create(0, 3)];

    /// <summary>A range with both ends clamped to 0…63; a maximum below the minimum moves up to it, and 63 means no limit.</summary>
    public static MapHopRange Create(int min, int? max)
    {
        min = Math.Clamp(min, 0, MaxHops);
        max = max is { } m ? Math.Clamp(m, 0, MaxHops) : null;
        if (max is { } hi && min > hi) max = min;
        return new MapHopRange { Min = min, Max = max == MaxHops ? null : max };
    }

    [JsonIgnore]
    public bool IsActive => Min > 0 || Max is not null;

    /// <summary>An unknown hop count fails any active range.</summary>
    public bool Contains(int? hops)
    {
        if (!IsActive) return true;
        if (hops is not { } h) return false;
        return h >= Min && (Max is not { } max || h <= max);
    }

    /// <summary>Sets the minimum; a maximum below it moves up to match.</summary>
    public MapHopRange WithMin(int value) => Create(value, Max);

    /// <summary>Sets the maximum (null = no limit); a minimum above it moves down to match.</summary>
    public MapHopRange WithMax(int? value)
    {
        var max = value is { } v ? Math.Clamp(v, 0, MaxHops) : (int?)null;
        return Create(max is { } m && Min > m ? m : Min, max);
    }

    internal MapHopRange Sanitized() => Create(Min, Max);

    [JsonIgnore]
    public string ChipText => (Min, Max) switch
    {
        (0, 0) => L.T("Direct only"),
        (var lo, { } hi) when lo == hi => L.F("Hops = {0}", lo),
        (0, { } hi) => L.F("Hops ≤ {0}", hi),
        (var lo, { } hi) => L.F("Hops {0}–{1}", lo, hi),
        (0, null) => L.T("Any"),
        (var lo, null) => L.F("Hops ≥ {0}", lo),
    };

    [JsonIgnore]
    public string SummaryText => (Min, Max) switch
    {
        (0, 0) => L.T("Showing only nodes heard directly, without a repeater."),
        (var lo, { } hi) when lo == hi => L.F("Showing nodes with a hop count of exactly {0}.", lo),
        (0, { } hi) => L.F("Showing nodes with a hop count of at most {0}.", hi),
        (var lo, { } hi) => L.F("Showing nodes with a hop count from {0} to {1}.", lo, hi),
        (0, null) => L.T("Showing nodes at any hop count."),
        (var lo, null) => L.F("Showing nodes with a hop count of at least {0}.", lo),
    };

    [JsonIgnore]
    public string PresetLabel => (Min, Max) switch
    {
        (0, null) => L.T("Any"),
        (0, 0) => L.T("Direct"),
        (0, { } hi) => L.F("≤ {0}", hi),
        _ => ChipText,
    };
}

/// <summary>An advanced filter that can be on and cleared on its own (one chip over the map).</summary>
public enum MapFilterDimension { LastHeard, Hops }

/// <summary>The map's advanced filters, stored in the settings.</summary>
public sealed record MapAdvancedFilter
{
    public MapLastHeardRange LastHeard { get; init; } = MapLastHeardRange.Any;
    public MapHopRange Hops { get; init; } = MapHopRange.Any;

    [JsonIgnore]
    public bool IsActive => LastHeard.IsActive || Hops.IsActive;

    /// <summary>The filters narrowing the map, in display order.</summary>
    [JsonIgnore]
    public IReadOnlyList<MapFilterDimension> ActiveDimensions
    {
        get
        {
            var list = new List<MapFilterDimension>(2);
            if (LastHeard.IsActive) list.Add(MapFilterDimension.LastHeard);
            if (Hops.IsActive) list.Add(MapFilterDimension.Hops);
            return list;
        }
    }

    /// <summary>Every active filter must pass.</summary>
    public bool Allows(DateTimeOffset? heardAt, int? hops, DateTimeOffset now) => LastHeard.Contains(heardAt, now) && Hops.Contains(hops);

    public MapAdvancedFilter Cleared(MapFilterDimension dimension) => dimension switch
    {
        MapFilterDimension.LastHeard => this with { LastHeard = MapLastHeardRange.Any },
        _ => this with { Hops = MapHopRange.Any },
    };

    public string ChipText(MapFilterDimension dimension) => dimension == MapFilterDimension.LastHeard ? LastHeard.ChipText : Hops.ChipText;

    /// <summary>Repairs values from an edited or older settings file (out-of-range numbers, crossed bounds).</summary>
    public MapAdvancedFilter Sanitized() => new() { LastHeard = (LastHeard ?? MapLastHeardRange.Any).Sanitized(), Hops = (Hops ?? MapHopRange.Any).Sanitized() };
}

/// <summary>Which nodes the main map shows: node types from the check boxes, then every active advanced filter.</summary>
public static class MapNodeFilter
{
    public sealed record TypeToggles(bool Companions, bool Repeaters, bool Rooms, bool Discovered);

    public static bool AllowsType(ContactType type, TypeToggles t) => type switch
    {
        ContactType.Repeater => t.Repeaters,
        ContactType.Room => t.Rooms,
        _ => t.Companions,
    };

    public static bool Shows(ContactRecord c, TypeToggles types, MapAdvancedFilter filter, DateTimeOffset now) =>
        c.HasLocation && !c.IsBlocked && AllowsType(c.ContactType, types)
        && filter.Allows(NodeList.HeardAtMs(c, now) is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, NodeList.DisplayedHopCount(c), now);

    public static bool Shows(DiscoveredNodeRecord n, TypeToggles types, MapAdvancedFilter filter, DateTimeOffset now) =>
        types.Discovered && n.HasLocation
        && filter.Allows(n.LastHeard > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(n.LastHeard) : null, NodeList.DisplayedHopCount(n), now);
}

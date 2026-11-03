using MC1.Core.Utilities;

namespace MC1.Core.Models;

/// <summary>How the contacts (nodes) list is ordered. Stored by name in the settings.</summary>
public enum NodeSortOrder { LastHeard, Name, Distance, Hops }

/// <summary>Times, hop counts and ordering of the contacts (nodes) list, as in the iPhone app's Nodes list.</summary>
public static class NodeList
{
    /// <summary>
    /// The time a node's row shows and is sorted by (Unix ms): its last advert when that time is plausible, otherwise
    /// when this PC last heard it; null when neither is known. Adverts carry the sender's clock, so a node with an
    /// unset or skewed clock (1970, or in the future) falls back to last heard.
    /// </summary>
    public static long? TimestampMs(ContactRecord c, DateTimeOffset reference)
    {
        if (AdvertTimestamp.IsPlausible(c.LastAdvert, reference)) return c.LastAdvert * 1000;
        return c.LastHeard > 0 ? c.LastHeard : null;
    }

    /// <summary>
    /// The latest evidence that a node is alive (Unix ms): last heard here or a plausible advert, whichever is later;
    /// null when there is none. Used by the map's "last heard" filter.
    /// </summary>
    public static long? HeardAtMs(ContactRecord c, DateTimeOffset reference)
    {
        var advert = AdvertTimestamp.IsPlausible(c.LastAdvert, reference) ? c.LastAdvert * 1000 : 0;
        var heard = Math.Max(c.LastHeard, advert);
        return heard > 0 ? heard : null;
    }

    /// <summary>Hops to a node: its stored path's hops, or for a flood-routed node the hops its advert took (null when unknown).</summary>
    public static int? DisplayedHopCount(ContactRecord c) => c.IsFloodRouted ? c.InboundHops : c.HopCount;

    /// <summary>Hops to a discovered node: its advert's path, or null when it came by flood.</summary>
    public static int? DisplayedHopCount(DiscoveredNodeRecord n) =>
        n.OutPathLength == 0xFF ? null : MeshCore.PathEncoding.Decode((byte)n.OutPathLength)?.HopCount;

    /// <summary>
    /// Orders contacts. "Distance" needs a location (<paramref name="from"/>) and otherwise sorts by last heard;
    /// nodes without a time, a location or a hop count go last.
    /// </summary>
    public static List<ContactRecord> Sort(IEnumerable<ContactRecord> contacts, NodeSortOrder order, DateTimeOffset reference, (double Lat, double Lon)? from = null)
    {
        var list = contacts.ToList();
        var byName = StringComparer.CurrentCultureIgnoreCase;
        if (order == NodeSortOrder.Distance && from is null) order = NodeSortOrder.LastHeard;
        double? Distance(ContactRecord c) => from is { } me && c.HasLocation ? RfCalculator.Distance(me.Lat, me.Lon, c.Latitude, c.Longitude) : null;
        return order switch
        {
            NodeSortOrder.Name => list.OrderBy(c => c.DisplayName, byName).ToList(),
            NodeSortOrder.Distance => list
                .OrderBy(c => Distance(c) is null)
                .ThenBy(c => Distance(c) ?? 0)
                .ThenBy(c => c.DisplayName, byName).ToList(),
            NodeSortOrder.Hops => list
                .OrderBy(c => DisplayedHopCount(c) is null)
                .ThenBy(c => DisplayedHopCount(c) ?? 0)
                .ThenBy(c => Distance(c) is null)
                .ThenBy(c => Distance(c) ?? 0)
                .ThenBy(c => c.DisplayName, byName).ToList(),
            _ => list
                .OrderBy(c => TimestampMs(c, reference) is null)
                .ThenByDescending(c => TimestampMs(c, reference) ?? 0)
                .ThenBy(c => c.DisplayName, byName).ToList(),
        };
    }
}

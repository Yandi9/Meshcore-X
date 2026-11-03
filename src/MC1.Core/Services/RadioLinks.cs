using MeshCore;

namespace MC1.Core.Services;

/// <summary>
/// Both links of one radio, remembered so the app can switch between them or reconnect through either (port of the
/// Swift <c>Device.connectionMethods</c> Bluetooth / WiFi pair). Kept in <see cref="AppSettings.RadioLinks"/> by radio id.
/// </summary>
public sealed class SavedRadioLinks
{
    /// <summary>The radio's Bluetooth address (the last one connected).</summary>
    public ConnectionTarget? Bluetooth { get; set; }
    /// <summary>The radio's WiFi host and port (the last one connected).</summary>
    public ConnectionTarget? Wifi { get; set; }

    public bool IsEmpty => Bluetooth is null && Wifi is null;
}

/// <summary>One row of the radio's link list: Bluetooth or WiFi (port of Swift <c>ConnectionLinkRows.Row</c>).</summary>
/// <param name="Kind"><see cref="ConnectionKind.Bluetooth"/> or <see cref="ConnectionKind.Tcp"/>.</param>
/// <param name="Target">What a switch to this link dials (null when the link can't be dialled).</param>
/// <param name="IsLive">The app talks to the radio over this link right now.</param>
/// <param name="CanSwitch">The app can (re)connect over this link now.</param>
public sealed record RadioLinkRow(ConnectionKind Kind, ConnectionTarget? Target, bool IsLive, bool CanSwitch)
{
    /// <summary>The host (WiFi) or the radio's Bluetooth name/address, for display.</summary>
    public string? Address => Target is null ? null
        : Kind == ConnectionKind.Tcp ? (Target.Port == TcpTransport.DefaultPort ? Target.Address : $"{Target.Address}:{Target.Port}")
        : Target.DisplayName ?? Target.Address;
}

/// <summary>Which links a radio offers and where each one dials (ports of Swift <c>ConnectionLinkRows</c>,
/// <c>ConnectionLinkWiFiDial</c> and <c>DeviceConnectionChoice</c>).</summary>
public static class RadioLinkRules
{
    public static bool SameEndpoint(ConnectionTarget? a, ConnectionTarget? b) =>
        a is not null && b is not null && a.Kind == b.Kind && string.Equals(a.Address, b.Address, StringComparison.OrdinalIgnoreCase)
        && (a.Kind != ConnectionKind.Tcp || a.Port == b.Port);

    /// <summary>A WiFi target for <paramref name="host"/>:<paramref name="port"/> (named like the Connect dialog does).</summary>
    public static ConnectionTarget WifiTarget(string host, int port = TcpTransport.DefaultPort) =>
        new(ConnectionKind.Tcp, host, $"{host}:{port}", port);

    private static bool IsDialable(ConnectionTarget? t) =>
        t is { Kind: ConnectionKind.Tcp } && !string.IsNullOrWhiteSpace(t.Address) && t.Port is >= 1 and <= 65535;

    /// <summary>
    /// Where a switch to WiFi dials. A live WiFi link keeps its own endpoint. Otherwise the saved host and port stay
    /// together; the address the radio reports is used only when nothing valid is saved (with the default port), and
    /// only while the radio says its WiFi is connected.
    /// </summary>
    public static ConnectionTarget? WifiDial(ConnectionTarget? live, SavedRadioLinks? saved, RadioWifiState? wifi)
    {
        if (live is { Kind: ConnectionKind.Tcp }) return live;
        if (IsDialable(saved?.Wifi)) return saved!.Wifi;
        if (wifi is { IsConnected: true, TcpEndpoint: { } ep } && !string.IsNullOrWhiteSpace(ep.Host)) return WifiTarget(ep.Host.Trim(), ep.Port);
        return null;
    }

    /// <summary>
    /// The Bluetooth row, then the WiFi row. Bluetooth shows when it is live or saved and can be switched to when saved.
    /// WiFi shows when it is live, the radio has WiFi settings, or it is saved; switching needs a dial target and — while
    /// connected — a radio whose WiFi is connected. While disconnected (<paramref name="live"/> null) each saved link can
    /// connect. The demo radio has no real links, so it never switches.
    /// </summary>
    public static IReadOnlyList<RadioLinkRow> Rows(ConnectionTarget? live, SavedRadioLinks? saved, RadioWifiState? wifi)
    {
        var rows = new List<RadioLinkRow>();
        var demo = live is { Kind: ConnectionKind.Simulator };
        var liveBt = live is { Kind: ConnectionKind.Bluetooth };
        if (liveBt || saved?.Bluetooth is not null)
            rows.Add(new RadioLinkRow(ConnectionKind.Bluetooth, liveBt ? live : saved!.Bluetooth, liveBt, !demo && !liveBt && saved?.Bluetooth is not null));

        var liveWifi = live is { Kind: ConnectionKind.Tcp };
        if (liveWifi || wifi is not null || saved?.Wifi is not null)
        {
            var dial = WifiDial(live, saved, wifi);
            var reachable = live is null ? IsDialable(saved?.Wifi) : wifi?.IsConnected == true;
            rows.Add(new RadioLinkRow(ConnectionKind.Tcp, dial, liveWifi, !demo && !liveWifi && dial is not null && reachable));
        }
        return rows;
    }
}

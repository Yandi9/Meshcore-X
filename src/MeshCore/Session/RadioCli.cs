namespace MeshCore;

/// <summary>Radio CLI keys shared by settings pages and terminal command admission (port of Swift <c>RadioCLIKey</c>).</summary>
public static class RadioCliKey
{
    public const string WifiEnabled = "wifi.enabled";
    public const string WifiSsid = "wifi.ssid";
    public const string WifiStatus = "wifi.status";
    public const string WifiIp = "wifi.ip";
    public const string WifiPassword = "wifi.pwd";
    public const string WifiClear = "wifi.clear";
    public const string TimezoneOffset = "tz.offset";
}

/// <summary>One radio CLI reply, classified after trailing CR/LF are removed (port of Swift <c>RadioCLIReply</c>).</summary>
public abstract record RadioCliReply
{
    /// <summary>Firmware prints this when a WiFi field has no saved value. An SSID with this exact text is
    /// indistinguishable from an empty field.</summary>
    public const string NotSet = "(not set)";

    /// <summary>Firmware prints this when WiFi has no association.</summary>
    public const string NotConnected = "(not connected)";

    /// <summary>CommonCLI prints this, then the key, for an unknown <c>get</c> key.</summary>
    public const string UnknownGetPrefix = "??: ";

    /// <summary>"Unknown command".</summary>
    public sealed record UnknownCommand : RadioCliReply;
    /// <summary>Starts with "Error", "ERROR" or "ERR".</summary>
    public sealed record Error(string Text) : RadioCliReply;
    /// <summary>"&gt; value" — the text after the prompt, verbatim (spaces kept).</summary>
    public sealed record Value(string Text) : RadioCliReply;
    /// <summary>Starts with "OK" (e.g. "OK - interval rounded to 16").</summary>
    public sealed record Ok(string Text) : RadioCliReply;
    /// <summary>Anything else (including an empty reply).</summary>
    public sealed record Other(string Text) : RadioCliReply;

    public static RadioCliReply Parse(string text)
    {
        text = StripTrailingLineEndings(text);
        if (text == "Unknown command") return new UnknownCommand();
        if (text.StartsWith("Error", StringComparison.Ordinal) || text.StartsWith("ERROR", StringComparison.Ordinal)
            || text.StartsWith("ERR", StringComparison.Ordinal)) return new Error(text);
        if (text.StartsWith("> ", StringComparison.Ordinal)) return new Value(text[2..]);
        if (text.StartsWith("OK", StringComparison.Ordinal)) return new Ok(text);
        return new Other(text);
    }

    /// <summary>True for a reply that accepted a <c>set</c> (an OK line or a value line).</summary>
    public bool IsAccepted => this is Ok or Value;

    public static string StripTrailingLineEndings(string text) => text.TrimEnd('\r', '\n');
}

/// <summary>Keeps a radio's <c>tz.offset</c> aligned with the computer's UTC offset (port of Swift <c>RadioTimezone</c>).</summary>
public static class RadioTimezone
{
    public const int MinHours = -12;
    public const int MaxHours = 14;

    /// <summary>Whole hours in −12…14. Fractional (e.g. +5:30) and out-of-range offsets return null (nothing is synced).</summary>
    public static int? OffsetHours(TimeSpan utcOffset)
    {
        var seconds = (long)utcOffset.TotalSeconds;
        if (seconds % 3600 != 0) return null;
        var hours = (int)(seconds / 3600);
        return hours is >= MinHours and <= MaxHours ? hours : null;
    }
}

/// <summary>Radio WiFi settings limits and values (firmware 1.18 companion CLI).</summary>
public static class RadioWifi
{
    public const string EnabledValue = "1";
    public const string DisabledValue = "0";
    public const string ConnectedStatus = "connected";
    public const int MinSsidBytes = 1;
    public const int MaxSsidBytes = 32;
    public const int MaxPasswordBytes = 63;
    /// <summary>Companion WiFi TCP port. Firmware <c>TCP_PORT</c> defaults to this and the CLI cannot read it.</summary>
    public const int DefaultTcpPort = TcpTransport.DefaultPort;
}

/// <summary>
/// A snapshot of the radio's WiFi settings read over the radio CLI.
/// </summary>
/// <param name="Enabled"><c>wifi.enabled</c>.</param>
/// <param name="Ssid">Saved network name, "" when none (<c>(not set)</c> or empty).</param>
/// <param name="Status">Raw <c>wifi.status</c> value (e.g. "connected"), or null when the build has no such key.</param>
/// <param name="IpAddress">The radio's WiFi IP address, or null when it has none (<c>(not connected)</c> or empty).</param>
/// <param name="RebootPending">A <c>set wifi.*</c> was accepted on this connection; the radio applies it after a reboot.</param>
public sealed record RadioWifiState(bool Enabled, string Ssid, string? Status, string? IpAddress, bool RebootPending)
{
    public bool IsConnected => Status?.Trim() == RadioWifi.ConnectedStatus;
    public bool HasNetwork => Ssid.Length > 0;
    /// <summary>The endpoint a WiFi dial would use (<see cref="RadioWifi.DefaultTcpPort"/>), when the radio reports an address.</summary>
    public (string Host, int Port)? TcpEndpoint => IpAddress is { Length: > 0 } ip ? (ip, RadioWifi.DefaultTcpPort) : null;
}

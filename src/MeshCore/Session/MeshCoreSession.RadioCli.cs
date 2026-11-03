using System.Text;

namespace MeshCore;

/// <summary>Radio CLI (<c>CMD_RUN_CLI_COMMAND</c>, firmware v14 / 1.18.0) and the settings helpers built on it.</summary>
public sealed partial class MeshCoreSession
{
    private volatile bool _radioWifiRebootPending;

    /// <summary>True after this session's radio accepted a <c>set wifi.*</c> line. The radio applies WiFi changes after a
    /// reboot; a new session (connection) starts false.</summary>
    public bool RadioWifiRebootPending => _radioWifiRebootPending;

    /// <summary>
    /// Runs one UTF-8 CLI line on the paired radio and returns its reply text (e.g. "&gt; Demo Radio", "OK",
    /// "Unknown command"). The line is sent verbatim — no trimming, so <c>"set wifi.pwd "</c> keeps its trailing space.
    /// </summary>
    /// <exception cref="MeshCoreException">
    /// <see cref="MeshCoreErrorKind.InvalidInput"/> for an empty/whitespace line, fewer than 2 or more than 175 UTF-8 bytes
    /// (nothing is sent); <see cref="MeshCoreErrorKind.Unsupported"/> when the radio is known to be older than v14;
    /// <see cref="MeshCoreErrorKind.DeviceError"/> when the radio rejects the frame (code 1 = unsupported);
    /// <see cref="MeshCoreErrorKind.Timeout"/> when no reply arrives.</exception>
    public async Task<string> RunCliCommandAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ValidateCliCommand(command);
        RequireFirmware(DeviceCapabilities.RadioCliFirmwareVersion, "The radio CLI");
        var reply = await SendAndWaitWithErrorAsync(PacketBuilder.RunCliCommand(command),
            e => (e as MeshEvent.CliReply)?.Text, timeout, ct).ConfigureAwait(false);
        NoteWifiRebootPending(command, reply);
        return reply;
    }

    internal static void ValidateCliCommand(string command)
    {
        var bytes = Encoding.UTF8.GetByteCount(command);
        if (string.IsNullOrWhiteSpace(command))
            throw new MeshCoreException(MeshCoreErrorKind.InvalidInput, "CLI command is empty");
        if (bytes < PacketBuilder.RunCliCommandMinUtf8Bytes)
            throw new MeshCoreException(MeshCoreErrorKind.InvalidInput, "CLI command is too short");
        if (bytes > PacketBuilder.RunCliCommandMaxUtf8Bytes)
            throw new MeshCoreException(MeshCoreErrorKind.InvalidInput, "CLI command exceeds maximum length");
    }

    private void NoteWifiRebootPending(string command, string reply)
    {
        if (command.Trim().StartsWith("set wifi.", StringComparison.Ordinal) && RadioCliReply.Parse(reply).IsAccepted)
            _radioWifiRebootPending = true;
    }

    /// <summary>
    /// Reads one radio CLI key with <c>get &lt;key&gt;</c>. Returns the value after the "&gt; " prompt (verbatim), or null when
    /// this firmware build has no such key ("Unknown command" or "??: &lt;key&gt;").
    /// </summary>
    /// <exception cref="MeshCoreException"><see cref="MeshCoreErrorKind.CommandFailed"/> for an error reply (the text is in
    /// <see cref="MeshCoreException.RadioReply"/> and the message); <see cref="MeshCoreErrorKind.InvalidResponse"/> for any other
    /// reply; plus everything <see cref="RunCliCommandAsync"/> throws.</exception>
    public async Task<string?> GetRadioCliValueAsync(string key, CancellationToken ct = default)
    {
        var reply = await RunCliCommandAsync("get " + key, ct: ct).ConfigureAwait(false);
        return RadioCliReply.Parse(reply) switch
        {
            RadioCliReply.Value v => v.Text,
            RadioCliReply.UnknownCommand => null,
            RadioCliReply.Error e => throw CliRejected(e.Text),
            RadioCliReply.Other o when o.Text == RadioCliReply.UnknownGetPrefix + key => null,
            _ => throw CliInvalid(reply),
        };
    }

    /// <summary>
    /// Writes one radio CLI key: <c>set &lt;key&gt; &lt;value&gt;</c>, or <c>set &lt;key&gt;</c> when <paramref name="value"/> is null.
    /// The value is verbatim, so an empty string sends a trailing space. Accepts an "OK…" or "&gt; …" reply.
    /// </summary>
    /// <exception cref="MeshCoreException"><see cref="MeshCoreErrorKind.CommandFailed"/> for an error or "Unknown command" reply;
    /// <see cref="MeshCoreErrorKind.InvalidResponse"/> for any other reply; plus everything <see cref="RunCliCommandAsync"/> throws.</exception>
    public async Task SetRadioCliValueAsync(string key, string? value, CancellationToken ct = default)
    {
        var reply = await RunCliCommandAsync(value is null ? "set " + key : $"set {key} {value}", ct: ct).ConfigureAwait(false);
        switch (RadioCliReply.Parse(reply))
        {
            case RadioCliReply.Ok or RadioCliReply.Value: return;
            case RadioCliReply.Error e: throw CliRejected(e.Text);
            case RadioCliReply.UnknownCommand: throw CliRejected("Unknown command");
            default: throw CliInvalid(reply);
        }
    }

    private static MeshCoreException CliRejected(string text) =>
        new(MeshCoreErrorKind.CommandFailed, text) { RadioReply = text };

    private static MeshCoreException CliInvalid(string reply) =>
        new(MeshCoreErrorKind.InvalidResponse, "The radio sent an unexpected reply: " + RadioCliReply.StripTrailingLineEndings(reply))
        { RadioReply = RadioCliReply.StripTrailingLineEndings(reply) };

    // MARK: WiFi

    /// <summary>
    /// Reads the radio's WiFi settings (<c>wifi.enabled</c>, <c>wifi.ssid</c>, <c>wifi.status</c>, <c>wifi.ip</c>).
    /// Returns null when the radio has no WiFi settings (no <c>wifi.enabled</c> key, e.g. a BLE-only board).
    /// </summary>
    /// <exception cref="MeshCoreException"><see cref="MeshCoreErrorKind.InvalidResponse"/> when <c>wifi.enabled</c> is not 0/1;
    /// plus everything <see cref="GetRadioCliValueAsync"/> throws.</exception>
    public async Task<RadioWifiState?> GetRadioWifiStateAsync(CancellationToken ct = default)
    {
        var enabled = await GetRadioCliValueAsync(RadioCliKey.WifiEnabled, ct).ConfigureAwait(false);
        if (enabled is null) return null;
        var isOn = enabled.Trim() switch
        {
            RadioWifi.EnabledValue => true,
            RadioWifi.DisabledValue => false,
            _ => throw CliInvalid(enabled),
        };
        var ssid = await GetRadioCliValueAsync(RadioCliKey.WifiSsid, ct).ConfigureAwait(false);
        var status = await GetRadioCliValueAsync(RadioCliKey.WifiStatus, ct).ConfigureAwait(false);
        var ip = await GetRadioCliValueAsync(RadioCliKey.WifiIp, ct).ConfigureAwait(false);
        return new RadioWifiState(
            isOn,
            ssid is null || ssid.Length == 0 || ssid == RadioCliReply.NotSet ? "" : ssid,
            status,
            ip is null || ip.Length == 0 || ip == RadioCliReply.NotConnected ? null : ip,
            RadioWifiRebootPending);
    }

    /// <summary>Reads the saved WiFi password verbatim ("" when none; the password has no sentinel).</summary>
    public async Task<string> GetRadioWifiPasswordAsync(CancellationToken ct = default) =>
        await GetRadioCliValueAsync(RadioCliKey.WifiPassword, ct).ConfigureAwait(false) ?? "";

    /// <summary>
    /// Saves the WiFi network: <c>set wifi.ssid</c>, then <c>set wifi.pwd</c> when <paramref name="password"/> is not null
    /// (pass "" for an open network). Applied by the radio after a reboot (<see cref="RadioWifiRebootPending"/>).
    /// </summary>
    /// <exception cref="MeshCoreException"><see cref="MeshCoreErrorKind.InvalidInput"/> for an SSID outside 1–32 UTF-8 bytes or a
    /// password over 63 bytes (nothing is sent); plus everything <see cref="SetRadioCliValueAsync"/> throws.</exception>
    public async Task SetRadioWifiCredentialsAsync(string ssid, string? password, CancellationToken ct = default)
    {
        var ssidBytes = Encoding.UTF8.GetByteCount(ssid);
        if (ssidBytes < RadioWifi.MinSsidBytes || ssidBytes > RadioWifi.MaxSsidBytes)
            throw new MeshCoreException(MeshCoreErrorKind.InvalidInput, $"The network name must be {RadioWifi.MinSsidBytes}–{RadioWifi.MaxSsidBytes} bytes");
        if (password is not null && Encoding.UTF8.GetByteCount(password) > RadioWifi.MaxPasswordBytes)
            throw new MeshCoreException(MeshCoreErrorKind.InvalidInput, $"The password must be at most {RadioWifi.MaxPasswordBytes} bytes");
        await SetRadioCliValueAsync(RadioCliKey.WifiSsid, ssid, ct).ConfigureAwait(false);
        if (password is not null) await SetRadioCliValueAsync(RadioCliKey.WifiPassword, password, ct).ConfigureAwait(false);
    }

    /// <summary>Turns the radio's WiFi on or off (<c>set wifi.enabled 1|0</c>; applied after a reboot). On boards that run
    /// the companion link over WiFi <i>or</i> Bluetooth this is the transport switch.</summary>
    public Task SetRadioWifiEnabledAsync(bool enabled, CancellationToken ct = default) =>
        SetRadioCliValueAsync(RadioCliKey.WifiEnabled, enabled ? RadioWifi.EnabledValue : RadioWifi.DisabledValue, ct);

    /// <summary>Forgets the saved WiFi network (<c>set wifi.clear</c>).</summary>
    public Task ClearRadioWifiAsync(CancellationToken ct = default) =>
        SetRadioCliValueAsync(RadioCliKey.WifiClear, null, ct);

    // MARK: Timezone

    /// <summary>
    /// Keeps the radio's <c>tz.offset</c> aligned with <paramref name="utcOffset"/> (whole hours −12…14 only). Reads the
    /// current value and writes only when it differs. Returns true when a write was sent; false when nothing was needed,
    /// the offset is fractional/out of range, or the radio has no <c>tz.offset</c> key.
    /// </summary>
    /// <exception cref="MeshCoreException">Everything <see cref="GetRadioCliValueAsync"/>/<see cref="SetRadioCliValueAsync"/> throw.</exception>
    public async Task<bool> SyncRadioTimezoneAsync(TimeSpan utcOffset, CancellationToken ct = default)
    {
        if (RadioTimezone.OffsetHours(utcOffset) is not { } hours) return false;
        var current = await GetRadioCliValueAsync(RadioCliKey.TimezoneOffset, ct).ConfigureAwait(false);
        if (current is null) return false;
        if (int.TryParse(current.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed == hours) return false;
        await SetRadioCliValueAsync(RadioCliKey.TimezoneOffset, hours.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return true;
    }
}

using MC1.Core.Models;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>Firmware 1.18 (protocol v14) radio features: radio CLI, WiFi settings, power off and remote CLI grants.</summary>
public sealed partial class DeviceService
{
    /// <summary>The connected radio runs commands sent with <see cref="RunRadioCliAsync"/> (firmware 1.18+).</summary>
    public bool SupportsRadioCli => app.IsConnected && app.Capabilities?.SupportsRadioCli == true;

    /// <summary>Runs one line on the radio's own CLI and returns its reply. The line is sent as typed (spacing kept).</summary>
    public Task<string> RunRadioCliAsync(string line, CancellationToken ct = default) => S.RunCliCommandAsync(line, ct: ct);

    // MARK: WiFi

    private MeshCoreSession? _wifiStateSession;
    private RadioWifiState? _wifiState;

    /// <summary>The radio's WiFi settings, or null when it has none (a board without WiFi). Remembered for this connection
    /// (<see cref="LastRadioWifiState"/>).</summary>
    public async Task<RadioWifiState?> GetRadioWifiStateAsync(CancellationToken ct = default)
    {
        var session = S;
        RadioWifiState? state;
        try { state = await session.GetRadioWifiStateAsync(ct).ConfigureAwait(false); }
        catch
        {
            // The board keeps its WiFi settings, but its association and address can't be trusted (no switch to them).
            if (ReferenceEquals(_wifiStateSession, session) && _wifiState is { } known) _wifiState = known with { Status = null, IpAddress = null };
            throw;
        }
        (_wifiStateSession, _wifiState) = (session, state);
        return state;
    }

    /// <summary>The WiFi settings were read on this connection.</summary>
    public bool RadioWifiStateKnown => _wifiStateSession is not null && ReferenceEquals(_wifiStateSession, app.Session);

    /// <summary>The WiFi settings as last read on this connection (null when not read yet, or the radio has no WiFi).</summary>
    public RadioWifiState? LastRadioWifiState => RadioWifiStateKnown ? _wifiState : null;

    public Task<string> GetRadioWifiPasswordAsync(CancellationToken ct = default) => S.GetRadioWifiPasswordAsync(ct);

    /// <summary>Saves the network name, and the password when it is not null ("" stores none). Applied after a reboot.</summary>
    public async Task SetRadioWifiCredentialsAsync(string ssid, string? password, CancellationToken ct = default)
    {
        await S.SetRadioWifiCredentialsAsync(ssid, password, ct).ConfigureAwait(false);
        app.Log.Info("Device", password is null ? "Saved the radio's WiFi network name" : "Saved the radio's WiFi network");
    }

    /// <summary>Turns the radio's WiFi on or off (applied after a reboot).</summary>
    public async Task SetRadioWifiEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await S.SetRadioWifiEnabledAsync(enabled, ct).ConfigureAwait(false);
        app.Log.Info("Device", enabled ? "Radio WiFi turned on (after reboot)" : "Radio WiFi turned off (after reboot)");
    }

    public async Task ClearRadioWifiAsync(CancellationToken ct = default)
    {
        await S.ClearRadioWifiAsync(ct).ConfigureAwait(false);
        app.Log.Info("Device", "Cleared the radio's WiFi settings");
    }

    /// <summary>A WiFi change was accepted on this connection; the radio applies it after a reboot.</summary>
    public bool RadioWifiRebootPending => app.Session?.RadioWifiRebootPending == true;

    // MARK: Power off

    /// <summary>Turns the radio off (firmware 1.18). The app disconnects and doesn't reconnect.</summary>
    public Task PowerOffAsync(CancellationToken ct = default) => app.PowerOffRadioAsync(ct);

    // MARK: Remote CLI grants

    /// <summary>Contacts that can be allowed to run CLI commands on this radio: companion radios that aren't blocked.</summary>
    public IReadOnlyList<ContactRecord> RemoteCliCandidates() =>
        app.Contacts.GetAll().Where(c => c.TypeRaw == (int)ContactType.Chat && !c.IsBlocked).OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>This radio lets the contact run CLI commands on it.</summary>
    public static bool IsRemoteCliAllowed(ContactRecord c) => c.ContactFlags.HasFlag(ContactFlags.RemoteCliAllowed);

    /// <summary>
    /// Allows or stops a contact running CLI commands on this radio (the contact's Remote CLI flag on the radio; other
    /// flags are kept). Read back from the radio before it's saved here. Runs one at a time with contact syncs.
    /// </summary>
    public async Task<ContactRecord> SetRemoteCliAllowedAsync(ContactRecord c, bool allowed)
    {
        var session = S;
        var saved = await app.RunContactOperationAsync(async () =>
        {
            var current = app.Db.GetContact(c.Id) ?? c;
            var flags = allowed ? current.ContactFlags | ContactFlags.RemoteCliAllowed : current.ContactFlags & ~ContactFlags.RemoteCliAllowed;
            await session.ChangeContactFlagsAsync(current.ToMeshContact(), flags).ConfigureAwait(false);
            var confirmed = await session.GetContactAsync(current.PublicKey).ConfigureAwait(false)
                            ?? throw new MessageServiceException(L.T("The contact is no longer on the radio."));
            if (confirmed.Flags.HasFlag(ContactFlags.RemoteCliAllowed) != allowed)
                throw new MessageServiceException(L.T("The radio didn't confirm the change. Try again."));
            return app.Db.SaveContactFrame(current.RadioId, confirmed);
        }).ConfigureAwait(false);
        c.Flags = saved.Flags;
        app.Log.Info("Device", $"Remote CLI {(allowed ? "allowed for" : "revoked for")} {Convert.ToHexString(c.PublicKey, 0, 4)}");
        app.Notify(DataKind.Contacts, c.Id);
        return saved;
    }
}

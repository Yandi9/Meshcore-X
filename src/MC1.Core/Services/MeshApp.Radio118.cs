using System.Text.Json;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>Firmware 1.18 connection features: timezone sync, owned power off, and the radio's Bluetooth/WiFi links.</summary>
public sealed partial class MeshApp
{
    // MARK: Timezone

    /// <summary>This PC's current UTC offset (tests replace it).</summary>
    public Func<TimeSpan> UtcOffsetProvider { get; set; } = () => DateTimeOffset.Now.Offset;

    /// <summary>
    /// Keeps the radio's <c>tz.offset</c> on this PC's whole-hour UTC offset once the radio is synced (port of Swift
    /// <c>RadioTimezone.syncIfNeeded</c>): written only when it differs; a fractional offset is left alone. A failure is
    /// logged and doesn't hold up the connection.
    /// </summary>
    private async Task SyncRadioTimezoneIfNeededAsync(MeshCoreSession session, CancellationToken ct)
    {
        if (Capabilities?.SupportsRadioCli != true) return;
        var offset = UtcOffsetProvider();
        try
        {
            if (await session.SyncRadioTimezoneAsync(offset, ct).ConfigureAwait(false))
                Log.Info("Connection", $"Set the radio's timezone to UTC{RadioTimezone.OffsetHours(offset):+0;-0;+0}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Warn("Connection", "Could not sync the radio's timezone: " + ex.Message); }
    }

    /// <summary>Work for a firmware 1.18 radio once it's synced: its timezone, and no Remote CLI for blocked contacts.</summary>
    private async Task RunRadio118ReadyTasksAsync(MeshCoreSession session, CancellationToken ct)
    {
        await SyncRadioTimezoneIfNeededAsync(session, ct).ConfigureAwait(false);
        await RevokeRemoteCliForBlockedAsync().ConfigureAwait(false);
    }

    // MARK: Remote CLI and blocked contacts

    /// <summary>
    /// A blocked contact mustn't control this radio: its Remote CLI grant is removed on the radio (port of Swift
    /// <c>revokeRemoteCLIIfRadioAllows</c> / <c>reconcileBlockedRemoteCLI</c>). Runs after each sync for every blocked
    /// contact, and when a contact is blocked.
    /// </summary>
    internal async Task RevokeRemoteCliForBlockedAsync(string? contactId = null)
    {
        if (Session is not { IsRunning: true } || Capabilities?.SupportsRadioCli != true || RadioId is not { } radioId) return;
        IReadOnlyList<Models.ContactRecord> contacts = contactId is null ? Db.GetContacts(radioId) : Db.GetContact(contactId) is { } one ? [one] : [];
        foreach (var c in contacts.Where(c => c.IsBlocked && c.RadioId == radioId && c.ContactFlags.HasFlag(ContactFlags.RemoteCliAllowed)))
        {
            try
            {
                await Device.SetRemoteCliAllowedAsync(c, false).ConfigureAwait(false);
                Log.Info("Contacts", $"Removed Remote CLI from blocked contact {Convert.ToHexString(c.PublicKey, 0, 4)}");
            }
            catch (Exception ex) { Log.Warn("Contacts", "Couldn't remove Remote CLI from a blocked contact: " + ex.Message); }
        }
    }

    /// <summary>Hooked to <see cref="DataChanged"/>: a contact that was just blocked loses its Remote CLI grant.</summary>
    private void RevokeRemoteCliWhenBlocked(DataChange change)
    {
        if (change is not { Kind: DataKind.Contacts, Key: { } id } || Capabilities?.SupportsRadioCli != true) return;
        if (Db.GetContact(id) is not { IsBlocked: true } c || !c.ContactFlags.HasFlag(ContactFlags.RemoteCliAllowed)) return;
        _ = Task.Run(() => RevokeRemoteCliForBlockedAsync(id));
    }

    // MARK: Power off

    private MeshCoreSession? _poweringOff;
    private Task? _powerOffTask;

    /// <summary>A power off is running on <paramref name="session"/>: its link loss must not start a reconnect.</summary>
    private bool PowerOffOwns(MeshCoreSession session) => ReferenceEquals(_poweringOff, session);

    /// <summary>A power off is waiting for the radio.</summary>
    public bool IsPoweringOff => _poweringOff is not null;

    /// <summary>
    /// Asks the connected radio to cut its power (firmware 1.18) and, when it goes silent, disconnects without
    /// reconnecting (port of Swift <c>ConnectionManager.powerOffCurrentLink</c>). A second call waits for the first.
    /// </summary>
    /// <exception cref="MessageServiceException">The radio answered and stayed on (no power switch).</exception>
    /// <exception cref="MeshCoreException">Not connected, too old (<see cref="MeshCoreErrorKind.Unsupported"/>) or rejected.</exception>
    public async Task PowerOffRadioAsync(CancellationToken ct = default)
    {
        if (_powerOffTask is { IsCompleted: false } running)
        {
            await running.ConfigureAwait(false);
            return;
        }
        var session = RequireSession();
        if (Capabilities is { SupportsRadioCli: false })
            throw MeshCoreException.Unsupported(DeviceCapabilities.RadioCliFirmwareVersion, "Power off");
        var task = PowerOffCoreAsync(session, ct);
        _powerOffTask = task;
        await task.ConfigureAwait(false);
    }

    private async Task PowerOffCoreAsync(MeshCoreSession session, CancellationToken ct)
    {
        _poweringOff = session;
        _reconnectCts?.Cancel();
        var poweredOff = false;
        try
        {
            Log.Info("Device", "Power off requested");
            var r = await session.ExchangePowerOffAsync(null, ct).ConfigureAwait(false);
            // Silence after the frame went out means the radio is off. A failed write or a stopped session also counts
            // when the link went down after the frame was handed over.
            poweredOff = r.Outcome == PowerOffOutcome.Silence
                         || (r.Outcome is PowerOffOutcome.SendFailed or PowerOffOutcome.Stopped && r.WasSubmitted && !session.IsRunning);
            if (!poweredOff)
            {
                Log.Warn("Device", $"Power off didn't happen: {r.Outcome}");
                throw r.Outcome switch
                {
                    PowerOffOutcome.StillOn => new MessageServiceException(L.T("This radio can't turn itself off. It is still on and connected.")),
                    PowerOffOutcome.Rejected => MeshCoreException.Device(r.ErrorCode),
                    PowerOffOutcome.NotSubmittedCancelled => new OperationCanceledException(ct),
                    _ => MeshCoreException.NotConnected(),
                };
            }
            await _connectLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(Session, session))
                {
                    _userDisconnect = true;
                    _reconnectCts?.Cancel();
                    await TeardownSessionAsync(LinkStatus.Disconnected, disconnectTransport: true).ConfigureAwait(false);
                }
            }
            finally { _connectLock.Release(); }
            Log.Info("Device", "The radio powered off");
        }
        finally
        {
            _poweringOff = null;
            // It didn't power off, but the link dropped meanwhile: handle that loss now (reconnects as usual).
            if (!poweredOff && ReferenceEquals(Session, session) && !session.IsRunning)
                OnSessionStateChanged(session, ConnectionState.Disconnected);
        }
    }

    // MARK: Radio links (Bluetooth / WiFi)

    /// <summary>Overrides WiFi (TCP) transports (tests connect "WiFi" to the simulated radio).</summary>
    public Func<ConnectionTarget, IMeshTransport>? TcpTransportFactory { get; set; }

    /// <summary>Remembers <paramref name="target"/> as <paramref name="radioId"/>'s Bluetooth or WiFi link. An endpoint
    /// belongs to one radio, so another radio that had it (e.g. a reused IP address) forgets it.</summary>
    private static void RememberRadioLink(AppSettings s, string radioId, ConnectionTarget target)
    {
        if (target.Kind is not (ConnectionKind.Bluetooth or ConnectionKind.Tcp)) return;
        if (!s.RadioLinks.TryGetValue(radioId, out var links)) s.RadioLinks[radioId] = links = new SavedRadioLinks();
        if (target.Kind == ConnectionKind.Bluetooth) links.Bluetooth = target;
        else links.Wifi = target;
        foreach (var (id, other) in s.RadioLinks)
        {
            if (id == radioId) continue;
            if (RadioLinkRules.SameEndpoint(other.Bluetooth, target)) other.Bluetooth = null;
            if (RadioLinkRules.SameEndpoint(other.Wifi, target)) other.Wifi = null;
        }
    }

    /// <summary>
    /// The saved links of a radio. Radios last connected by an older version have only their last connection, which
    /// is used as its one link.
    /// </summary>
    public SavedRadioLinks? RadioLinksFor(string? radioId)
    {
        if (radioId is null) return null;
        if (Settings.Current.RadioLinks.TryGetValue(radioId, out var saved) && !saved.IsEmpty) return saved;
        if (StoredLastConnection(Db.GetRadio(radioId)) is { } last)
            return last.Kind switch
            {
                ConnectionKind.Bluetooth => new SavedRadioLinks { Bluetooth = last },
                ConnectionKind.Tcp => new SavedRadioLinks { Wifi = last },
                _ => saved,
            };
        return saved;
    }

    private static ConnectionTarget? StoredLastConnection(Models.RadioRecord? radio)
    {
        if (radio?.LastConnection is not { Length: > 0 } json) return null;
        try { return JsonSerializer.Deserialize<ConnectionTarget>(json); }
        catch { return null; }
    }

    /// <summary>The radio a saved connection belongs to, when known.</summary>
    public string? RadioIdFor(ConnectionTarget target)
    {
        foreach (var (id, links) in Settings.Current.RadioLinks)
            if (RadioLinkRules.SameEndpoint(links.Bluetooth, target) || RadioLinkRules.SameEndpoint(links.Wifi, target)) return id;
        foreach (var r in Db.GetRadios().OrderByDescending(r => r.LastConnected))
            if (RadioLinkRules.SameEndpoint(StoredLastConnection(r), target)) return r.Id;
        return null;
    }

    /// <summary>The same radio's other link (WiFi for a Bluetooth connection and the reverse), when one is saved.</summary>
    public ConnectionTarget? AlternateLink(ConnectionTarget target)
    {
        if (target.Kind is not (ConnectionKind.Bluetooth or ConnectionKind.Tcp)) return null;
        var links = RadioLinksFor(RadioIdFor(target));
        return target.Kind == ConnectionKind.Bluetooth ? links?.Wifi : links?.Bluetooth;
    }

    /// <summary>The link a switch is connecting right now (null when none).</summary>
    public ConnectionKind? SwitchingTo { get; private set; }

    /// <summary>
    /// Moves the connection to another link of the radio (port of Swift <c>beginLinkSwitch</c>): the current link is
    /// closed and <paramref name="target"/> is connected. On failure the app stays disconnected and the error is
    /// thrown, so the caller can offer either link again.
    /// </summary>
    public async Task SwitchLinkAsync(ConnectionTarget target, CancellationToken ct = default)
    {
        if (SwitchingTo is not null) return;
        if (IsConnected && RadioLinkRules.SameEndpoint(CurrentTarget, target)) return;
        SwitchingTo = target.Kind;
        try { StatusChanged?.Invoke(); } catch { /* ignore */ }
        try
        {
            Log.Info("Connection", $"Switching link to {target.Kind}");
            await ConnectAsync(target, ct).ConfigureAwait(false);
        }
        finally
        {
            SwitchingTo = null;
            try { StatusChanged?.Invoke(); } catch { /* ignore */ }
        }
    }
}

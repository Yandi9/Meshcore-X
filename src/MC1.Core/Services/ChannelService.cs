using System.Security.Cryptography;
using MC1.Core.Models;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>Channel slots on the radio (public, private and #hashtag channels) and per-channel flood scope.</summary>
public sealed class ChannelService(MeshApp app)
{
    /// <summary>Held from setting the session flood scope until the message that needs it has been handed to the radio.</summary>
    private readonly SemaphoreSlim _scopeLock = new(1, 1);
    /// <summary>The scope this app last set, and on which session (a new session starts unknown).</summary>
    private string? _lastPushedScope;
    private MeshCoreSession? _scopeSession;

    public IReadOnlyList<ChannelRecord> GetAll() => app.RadioId is { } id ? app.Db.GetChannels(id) : [];

    public string? DeviceDefaultFloodScope { get; private set; }

    /// <summary>Re-reads every channel slot (and the default flood scope) from the radio.</summary>
    public Task RefreshAsync() => SyncAsync(CancellationToken.None);

    internal async Task SyncAsync(CancellationToken ct)
    {
        var session = app.RequireSession();
        var radioId = app.RequireRadioId();
        var max = app.Capabilities?.MaxChannels is > 0 and var m ? m : 8;
        var indices = Enumerable.Range(0, max).Select(i => (byte)i).ToList();
        var (received, missing) = await session.GetChannelsAsync(indices, ct).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            app.Log.Warn("Channels", $"Retrying {missing.Count} channel slots");
            foreach (var idx in missing)
            {
                try { received = [.. received, await session.GetChannelAsync(idx, ct).ConfigureAwait(false)]; }
                catch (MeshCoreException ex) { app.Log.Warn("Channels", $"Slot {idx} unreadable: {ex.Message}"); }
            }
        }
        var existing = app.Db.GetChannels(radioId).ToDictionary(c => c.Idx);
        foreach (var info in received)
        {
            if (!info.IsConfigured)
            {
                // A restored channel waiting to be written to this empty slot stays until then.
                if (existing.ContainsKey(info.Index) && !app.PendingConfig.ProtectsChannel(radioId, info.Index))
                    app.Db.DeleteChannel(radioId, info.Index, deleteMessages: false);
                continue;
            }
            var rec = existing.GetValueOrDefault(info.Index) ?? new ChannelRecord { RadioId = radioId, Idx = info.Index };
            rec.Name = info.Name;
            rec.Secret = info.Secret;
            app.Db.UpsertChannel(rec);
        }
        app.UpdateRadioRecord(r => r.LastChannelSync = Time.Now());
        app.RxLog.RefreshChannels();
        if ((app.Capabilities?.FirmwareVersion ?? 0) >= 11)
        {
            try { DeviceDefaultFloodScope = (await session.GetDefaultFloodScopeAsync(ct).ConfigureAwait(false))?.Name; }
            catch (Exception ex) { app.Log.Debug("Channels", "Default flood scope unavailable: " + ex.Message); }
        }
        // The radio's default may have changed: set the scope again before the next send.
        _lastPushedScope = null;
        app.Log.Info("Channels", $"Channels synced: {received.Count(c => c.IsConfigured)} configured of {max} slots");
        app.Notify(DataKind.Channels);
        app.Notify(DataKind.Conversations);
    }

    public int? FirstFreeSlot()
    {
        var used = GetAll().Select(c => c.Idx).ToHashSet();
        var max = app.Capabilities?.MaxChannels is > 0 and var m ? m : 8;
        for (var i = 0; i < max; i++) if (!used.Contains(i)) return i;
        return null;
    }

    public async Task<ChannelRecord> SetAsync(int index, string name, byte[] secret)
    {
        var session = app.RequireSession();
        var radioId = app.RequireRadioId();
        if (secret.Length != 16) throw new MessageServiceException(L.T("Channel secrets must be 16 bytes."));
        await session.SetChannelAsync((byte)index, name, secret).ConfigureAwait(false);
        var rec = app.Db.GetChannel(radioId, index) ?? new ChannelRecord { RadioId = radioId, Idx = index };
        var secretChanged = !rec.Secret.SequenceEquals(secret);
        // Another channel in this slot starts fresh: no unread count, draft, pin, region or notification choice carried over.
        if (secretChanged) rec = new ChannelRecord { Id = rec.Id, RadioId = radioId, Idx = index };
        rec.Name = name;
        rec.Secret = secret;
        app.Db.UpsertChannel(rec);
        if (secretChanged) app.Db.ClearChannelMessages(radioId, index);
        app.RxLog.RefreshChannels();
        app.Notify(DataKind.Channels);
        app.Notify(DataKind.Conversations);
        return rec;
    }

    private int RequireFreeSlot() => FirstFreeSlot() ?? throw new MessageServiceException(L.T("All channel slots on the radio are in use. Remove a channel first."));

    public Task<ChannelRecord> AddPublicAsync() =>
        GetAll().FirstOrDefault(c => c.IsPublic) is { } existing ? Task.FromResult(existing) : SetAsync(RequireFreeSlot(), "Public", ChannelSecrets.PublicChannelSecret);

    public Task<ChannelRecord> JoinHashtagAsync(string tag)
    {
        var body = HashtagUtilities.SanitizeHashtagNameInput(tag.TrimStart('#'));
        if (!HashtagUtilities.IsValidHashtagName(body)) throw new MessageServiceException(L.T("Hashtag names may only use letters, numbers and hyphens."));
        var (name, secret) = HashtagUtilities.HashtagChannel(body);
        if (GetAll().FirstOrDefault(c => c.Secret.SequenceEquals(secret)) is { } existing) return Task.FromResult(existing);
        return SetAsync(RequireFreeSlot(), name, secret);
    }

    public Task<ChannelRecord> CreatePrivateAsync(string name) =>
        SetAsync(RequireFreeSlot(), name.Utf8Prefix(31), RandomNumberGenerator.GetBytes(16));

    public Task<ChannelRecord> JoinPrivateAsync(string name, byte[] secret) =>
        GetAll().FirstOrDefault(c => c.Secret.SequenceEquals(secret)) is { } existing ? Task.FromResult(existing) : SetAsync(RequireFreeSlot(), name.Utf8Prefix(31), secret);

    /// <summary>Joins from a meshcore://channel/add link.</summary>
    public async Task<ChannelRecord> JoinFromUriAsync(string uri)
    {
        var link = MeshCoreUrl.ParseChannel(uri.Trim()) ?? throw new MessageServiceException(L.T("That doesn't look like a MeshCore channel link."));
        var ch = await JoinPrivateAsync(link.Name, link.Secret).ConfigureAwait(false);
        if (link.RegionScope is { } region) SetFloodScope(ch, "region:" + region);
        return ch;
    }

    public async Task RemoveAsync(ChannelRecord ch, bool deleteMessages = true)
    {
        var session = app.RequireSession();
        await session.SetChannelAsync((byte)ch.Idx, "", new byte[16]).ConfigureAwait(false);
        app.Db.DeleteChannel(ch.RadioId, ch.Idx, deleteMessages);
        app.RxLog.RefreshChannels();
        app.Notify(DataKind.Channels);
        app.Notify(DataKind.Conversations);
    }

    public Task RenameAsync(ChannelRecord ch, string newName) => SetAsync(ch.Idx, newName.Utf8Prefix(31), ch.Secret);

    public void SetNotificationLevel(ChannelRecord ch, NotificationLevel level)
    {
        ch.NotificationLevel = (int)level;
        app.Db.SetChannelNotificationLevel(ch.RadioId, ch.Idx, level);
        app.Notify(DataKind.Channels);
        app.Notify(DataKind.Conversations);
        // Muted chats don't count towards the unread totals.
        app.Messages.UpdateBadge();
    }

    /// <summary>Pins or unpins the channel in the chat list (this PC only).</summary>
    public void SetPinned(ChannelRecord ch, bool pinned)
    {
        if (!app.Db.SetChannelPinned(ch.RadioId, ch.Idx, pinned)) throw new MessageServiceException(L.T("Couldn't save the pin. Try again."));
        ch.IsPinned = pinned;
        app.Notify(DataKind.Channels);
        app.Notify(DataKind.Conversations);
    }

    public void ClearMessages(ChannelRecord ch)
    {
        app.Db.ClearChannelMessages(ch.RadioId, ch.Idx);
        app.Notify(DataKind.Messages, MessageService.ChannelKey(ch.Idx));
        app.Notify(DataKind.Conversations);
    }

    public static string ShareUri(ChannelRecord ch) => MeshCoreUrl.ChannelUri(ch.Name, ch.Secret, ch.FloodScope?.StartsWith("region:") == true ? ch.FloodScope[7..] : null);

    /// <summary>Sets the per-channel flood scope: null/"inherit", "all" or "region:NAME".</summary>
    public void SetFloodScope(ChannelRecord ch, string? scope)
    {
        ch.FloodScope = scope is null or "inherit" ? null : scope;
        app.Db.SetChannelFloodScope(ch.RadioId, ch.Idx, ch.FloodScope);
        app.Notify(DataKind.Channels);
    }

    // MARK: Flood scope for sends

    /// <summary>Firmware before this has no session flood scope: there a reset to the radio's default is skipped.</summary>
    private const int FloodScopeMinimumFirmware = 8;

    /// <param name="Key">Identifies the scope (to skip setting the same one twice for DMs and rooms).</param>
    /// <param name="ScopeKey">The region key to set; null with <paramref name="Unscoped"/> false means no command.</param>
    private sealed record ScopePlan(string Key, byte[]? ScopeKey, bool Unscoped);

    /// <summary>The session flood scope a send on <paramref name="channelIndex"/> needs (null: DMs and room posts use the radio's default).</summary>
    private ScopePlan PlanScope(int? channelIndex)
    {
        var ch = channelIndex is { } idx && app.RadioId is { } rid ? app.Db.GetChannel(rid, idx) : null;
        var pref = ch?.FloodScope ?? "inherit";
        var fw = app.Capabilities?.FirmwareVersion ?? 0;
        if (pref == "all" && fw >= 12) return new("unscoped", null, true);
        if (pref.StartsWith("region:")) return new(pref, new FloodScope.Region(pref[7..]).ScopeKey(), false);
        if (pref != "all" && !string.IsNullOrEmpty(DeviceDefaultFloodScope))
            return new("default:" + DeviceDefaultFloodScope, new FloodScope.Region(DeviceDefaultFloodScope).ScopeKey(), false);
        // "All regions" before firmware v12, or the radio's own default: reset the session scope.
        return fw < FloodScopeMinimumFirmware ? new("none", null, false) : new("disabled", new FloodScope.Disabled().ScopeKey(), false);
    }

    private async Task PushScopeAsync(MeshCoreSession session, ScopePlan plan)
    {
        _scopeSession = session;
        try
        {
            if (plan.Unscoped) await session.SetFloodScopeUnscopedAsync().ConfigureAwait(false);
            else if (plan.ScopeKey is { } key) await session.SetFloodScopeAsync(key).ConfigureAwait(false);
            _lastPushedScope = plan.Key;
        }
        catch
        {
            _lastPushedScope = null; // what the radio has now is unknown
            throw;
        }
    }

    /// <summary>
    /// Sets the channel's flood scope (its region, or the radio's default) and then sends, with no other send from this
    /// app changing the scope in between. Used for every channel message: new ones, retries, reactions and replies
    /// from a notification. A scope the radio refuses fails the send rather than flooding it outside its region.
    /// </summary>
    internal async Task<T> SendChannelScopedAsync<T>(MeshCoreSession session, int channelIndex, Func<Task<T>> send)
    {
        await _scopeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Always set it: something else (another app on the radio, a restart) may have changed it.
            await PushScopeAsync(session, PlanScope(channelIndex)).ConfigureAwait(false);
            return await send().ConfigureAwait(false);
        }
        finally { _scopeLock.Release(); }
    }

    internal Task SendChannelScopedAsync(MeshCoreSession session, int channelIndex, Func<Task> send) =>
        SendChannelScopedAsync<bool>(session, channelIndex, async () => { await send().ConfigureAwait(false); return true; });

    /// <summary>Sends a direct message (or other non-channel packet) with the radio's default scope set first.</summary>
    internal async Task<T> SendDirectScopedAsync<T>(MeshCoreSession session, Func<Task<T>> send)
    {
        await _scopeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ApplyDefaultScopeLockedAsync(session).ConfigureAwait(false);
            return await send().ConfigureAwait(false);
        }
        finally { _scopeLock.Release(); }
    }

    /// <summary>Puts the radio's default scope back (before a room post, which waits for its ACK on its own).</summary>
    internal async Task ApplyFloodScopeAsync(int? channelIndex)
    {
        var session = app.Session;
        if (session is null || !session.IsRunning) return;
        await _scopeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (channelIndex is { } idx) await PushScopeAsync(session, PlanScope(idx)).ConfigureAwait(false);
            else await ApplyDefaultScopeLockedAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) { app.Log.Warn("Channels", "Could not set flood scope: " + ex.Message); }
        finally { _scopeLock.Release(); }
    }

    private async Task ApplyDefaultScopeLockedAsync(MeshCoreSession session)
    {
        var plan = PlanScope(null);
        if (ReferenceEquals(_scopeSession, session) && plan.Key == _lastPushedScope) return;
        // Best effort: direct messages mostly travel on a path, where the scope doesn't matter.
        try { await PushScopeAsync(session, plan).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { app.Log.Warn("Channels", "Could not set flood scope: " + ex.Message); }
    }
}

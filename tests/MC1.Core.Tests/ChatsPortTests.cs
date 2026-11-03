using System.Net;
using System.Threading.Channels;
using Dapper;
using MC1.Core.Data;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Simulation;
using MC1.Core.Utilities;
using MeshCore;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MC1.Core.Tests;

/// <summary>Records every command the app sends to the simulated radio (in order).</summary>
public sealed class RecordingTransport(SimulatedRadio inner) : IMeshTransport
{
    private readonly List<byte[]> _sent = new();
    public IReadOnlyList<byte[]> Sent { get { lock (_sent) return _sent.ToList(); } }
    public string Description => inner.Description;
    public bool IsConnected => inner.IsConnected;
    public bool SupportsPipelinedReads => inner.SupportsPipelinedReads;
    public ChannelReader<byte[]> Received => inner.Received;
    public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
    public Task DisconnectAsync() => inner.DisconnectAsync();
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    public Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(payload.ToArray());
        return inner.SendAsync(payload, ct);
    }
}

/// <summary>WS3 (chats): pins, read state, drafts, interrupted DMs, notification replies, channel regions, link fetches.</summary>
public class ChatsPortTests
{
    private static ContactRecord Contact(MeshApp app, string name) => app.Contacts.GetAll().First(c => c.Name == name);

    // MARK: Pins (976a712d, 44627d7b, 385a4f40)

    [Fact]
    public async Task PinsAreSeparateFromTheRadioFavouriteAndSurviveOlderCopies()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = Contact(app, "Alice");
        var key = MessageService.DmKey(alice.Id);
        Assert.False(app.Messages.IsPinned(key));

        app.Messages.SetPinned(key, true);
        Assert.True(app.Messages.IsPinned(key));
        Assert.False(app.Db.GetContact(alice.Id)!.IsFavorite); // the radio's flag is untouched

        // Favouriting (a radio write) and saving an older copy of the contact keep the pin.
        await app.Contacts.SetFavoriteAsync(alice, true);
        app.Contacts.SetNickname(alice, "Ally"); // `alice` was read before the pin
        await app.SyncContactsAsync(force: true);
        var now = app.Db.GetContact(alice.Id)!;
        Assert.True(now.IsPinned);
        Assert.True(now.IsFavorite);
        // Un-favouriting doesn't unpin either.
        await app.Contacts.SetFavoriteAsync(now, false);
        Assert.True(app.Messages.IsPinned(key));

        // Channels and rooms pin the same way.
        var channel = app.Channels.GetAll().First(c => c.Idx == 1);
        app.Channels.SetPinned(channel, true);
        app.Channels.SetNotificationLevel(channel, NotificationLevel.MentionsOnly);
        await app.Channels.SyncAsync(CancellationToken.None);
        Assert.True(app.Messages.IsPinned(MessageService.ChannelKey(1)));
        Assert.Equal((int)NotificationLevel.MentionsOnly, app.Db.GetChannel(app.RadioId!, 1)!.NotificationLevel);

        var room = app.Remote.GetOrCreateSession(Contact(app, "Town Square"));
        var roomKey = MessageService.RoomKey(room.Id);
        app.Messages.SetPinned(roomKey, true);
        app.Db.UpsertRemoteSession(room); // older copy, unpinned
        Assert.True(app.Messages.IsPinned(roomKey));
        app.Messages.SetPinned(roomKey, false);
        Assert.False(app.Messages.IsPinned(roomKey));

        Assert.Throws<MessageServiceException>(() => app.Messages.SetPinned("dm:" + Guid.NewGuid(), true));
    }

    [Fact]
    public async Task AnotherChannelInTheSlotStartsUnpinned()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var priv = await app.Channels.CreatePrivateAsync("Family");
        app.Channels.SetPinned(priv, true);
        app.Db.SetChannelDraft(app.RadioId!, priv.Idx, "half-written");
        await app.Channels.RenameAsync(app.Db.GetChannel(app.RadioId!, priv.Idx)!, "Family 2"); // same key: keeps its state
        Assert.True(app.Db.GetChannel(app.RadioId!, priv.Idx)!.IsPinned);
        Assert.Equal("half-written", app.Db.GetChannel(app.RadioId!, priv.Idx)!.DraftText);
        await app.Channels.SetAsync(priv.Idx, "Other", ChannelSecrets.HashSecret("#other")); // another channel in the slot
        var other = app.Db.GetChannel(app.RadioId!, priv.Idx)!;
        Assert.False(other.IsPinned);
        Assert.Null(other.DraftText);
    }

    [Fact]
    public void FavouritesBecomePinnedOnceWhenTheDatabaseIsUpgraded()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc1pin-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "mc1.db");
        try
        {
            // A database as the previous version left it: no pin columns, schema version 1.
            _ = new AppDatabase(path);
            using (var c = new SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                foreach (var (table, column) in new[] { ("Contacts", "IsPinned"), ("Channels", "IsPinned"), ("RemoteSessions", "IsPinned"), ("RemoteSessions", "DraftText") })
                    c.Execute($"ALTER TABLE {table} DROP COLUMN {column}");
                c.Execute("PRAGMA user_version=1");
                c.Execute("INSERT INTO Contacts(Id,RadioId,PublicKey,Name,Flags) VALUES('fav','r',x'01','Fav',1),('plain','r',x'02','Plain',0),('both','r',x'03','Both',3)");
            }
            SqliteConnection.ClearAllPools();

            var db = new AppDatabase(path);
            Assert.Equal(AppDatabase.SchemaVersion, db.CurrentSchemaVersion);
            Assert.True(db.GetContact("fav")!.IsPinned);
            Assert.True(db.GetContact("both")!.IsPinned);
            Assert.False(db.GetContact("plain")!.IsPinned);

            // Recorded: a favourite set later stays unpinned, and an unpinned former favourite stays unpinned.
            using (var c = new SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                c.Execute("UPDATE Contacts SET Flags=1 WHERE Id='plain'");
            }
            db.SetContactPinned("fav", false);
            SqliteConnection.ClearAllPools();
            var again = new AppDatabase(path);
            Assert.False(again.GetContact("plain")!.IsPinned);
            Assert.False(again.GetContact("fav")!.IsPinned);
            // The new columns work on the upgraded file.
            again.UpsertRemoteSession(new RemoteSessionRecord { Id = "s", RadioId = "r", PublicKey = [9], Name = "Room", IsRoom = true });
            again.SetRoomDraft("s", "hello");
            Assert.Equal("hello", again.GetRemoteSession("s")!.DraftText);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    // MARK: Read / unread (122d6af8, bb04b9d7, 90a3f42d)

    [Fact]
    public async Task ChatsCanBeMarkedUnreadAndReadWithTheTotalsFollowing()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = Contact(app, "Alice");
        var key = MessageService.DmKey(alice.Id);
        Assert.True(await AppFixture.Eventually(() => app.Messages.IsUnread(key)), "queued DM arrives unread");
        var read = new List<string>();
        app.Messages.ConversationRead += k => { lock (read) read.Add(k); };

        app.Messages.SetReadState(key, isRead: true);
        Assert.False(app.Messages.IsUnread(key));
        Assert.Contains(key, read); // its notifications can come down
        var total = app.Messages.TotalUnread();

        app.Messages.SetReadState(key, isRead: false);
        Assert.True(app.Messages.IsUnread(key));
        Assert.Equal(1, app.Messages.UnreadCount(key));
        Assert.Equal(total + 1, app.Messages.TotalUnread());

        // Marking unread never lowers a real count.
        app.Db.IncrementContactUnread(alice.Id, false);
        app.Db.IncrementContactUnread(alice.Id, false);
        app.Messages.SetReadState(key, isRead: false);
        Assert.Equal(3, app.Messages.UnreadCount(key));

        // A muted chat marked unread is unread, but not in the totals.
        var channelKey = MessageService.ChannelKey(1);
        app.Messages.MarkRead(channelKey);
        app.Channels.SetNotificationLevel(app.Db.GetChannel(app.RadioId!, 1)!, NotificationLevel.Muted);
        var before = app.Messages.TotalUnread();
        app.Messages.SetReadState(channelKey, isRead: false);
        Assert.True(app.Messages.IsUnread(channelKey));
        Assert.Equal(before, app.Messages.TotalUnread());

        // Rooms too.
        var room = app.Remote.GetOrCreateSession(Contact(app, "Town Square"));
        app.Messages.SetReadState(MessageService.RoomKey(room.Id), isRead: false);
        Assert.Equal(1, app.Messages.UnreadCount(MessageService.RoomKey(room.Id)));
        app.Messages.SetReadState(MessageService.RoomKey(room.Id), isRead: true);
        Assert.Equal(0, app.Messages.UnreadCount(MessageService.RoomKey(room.Id)));

        // An older copy of the contact saved later doesn't put the old count back.
        app.Messages.MarkRead(key);
        app.Contacts.SetNickname(alice, "Ally");
        Assert.Equal(0, app.Messages.UnreadCount(key));
    }

    [Fact]
    public async Task MessagesArrivingInAChatThatIsntSeenStayUnread()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = Contact(app, "Alice");
        var key = MessageService.DmKey(alice.Id);
        app.Messages.SetConversationOpen(key, true);
        app.Messages.MarkRead(key);
        // The window went to the background with the chat still open (810970af).
        app.Messages.AppIsForeground = false;
        app.Messages.QueueDirectMessage(alice, "Still there");
        Assert.True(await AppFixture.Eventually(() => app.Db.GetDirectMessages(alice.Id).Any(m => !m.IsOutgoing && m.Text.Contains("Still there"))));
        Assert.True(await AppFixture.Eventually(() => app.Messages.UnreadCount(key) == 1));
    }

    // MARK: Drafts (5df12d3b, f374e48c)

    [Fact]
    public async Task DraftsSurviveOlderCopiesSyncsAndAConnectionLoss()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = Contact(app, "Alice"); // read before the draft is typed
        app.Db.SetContactDraft(alice.Id, "Meet at 6?");
        app.Db.SetChannelDraft(app.RadioId!, 0, "Hello all");
        var room = app.Remote.GetOrCreateSession(Contact(app, "Town Square"));
        app.Rooms.SaveDraft(room, "Room note");

        app.Contacts.SetNickname(alice, "Ally");
        app.Contacts.SetNotificationLevel(alice, NotificationLevel.MentionsOnly);
        app.Channels.SetNotificationLevel(app.Channels.GetAll().First(c => c.Idx == 0), NotificationLevel.All);
        await app.SyncContactsAsync(force: true);
        await app.Channels.SyncAsync(CancellationToken.None);
        app.Remote.GetOrCreateSession(Contact(app, "Town Square"));

        f.Radio.SimulateDisconnect();
        Assert.True(await AppFixture.Eventually(() => app.Status != LinkStatus.Ready, 5000));
        Assert.True(await AppFixture.Eventually(() => app.Status == LinkStatus.Ready, 20000));

        Assert.Equal("Meet at 6?", app.Db.GetContact(alice.Id)!.DraftText);
        Assert.Equal("Hello all", app.Db.GetChannel(app.RadioId!, 0)!.DraftText);
        Assert.Equal("Room note", app.Db.GetRemoteSession(room.Id)!.DraftText);
    }

    // MARK: Interrupted direct messages (11a708a4)

    [Fact]
    public async Task ADirectMessageCutOffByADisconnectStaysPendingAndIsSentAgain()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var bob = Contact(app, "Bob"); // Bob doesn't hear the first attempt: the app waits for an ACK that doesn't come
        var sent = app.Messages.QueueDirectMessage(bob, "Are you around?");
        Assert.True(await AppFixture.Eventually(() => app.Db.GetMessage(sent.Id)!.MessageStatus == MessageStatus.Sent));
        var stamp = app.Db.GetMessage(sent.Id)!.Timestamp;

        f.Radio.SimulateDisconnect();
        Assert.True(await AppFixture.Eventually(() => app.Status != LinkStatus.Ready, 5000));
        Assert.True(await AppFixture.Eventually(() => app.Messages.InterruptedCount == 1, 3000));
        var during = app.Db.GetMessage(sent.Id)!;
        Assert.Equal(MessageStatus.Pending, during.MessageStatus);
        Assert.Equal(stamp, during.Timestamp);

        Assert.True(await AppFixture.Eventually(() => app.Status == LinkStatus.Ready, 20000));
        Assert.True(await AppFixture.Eventually(() => app.Db.GetMessage(sent.Id)!.MessageStatus == MessageStatus.Delivered, 30000),
            $"status={app.Db.GetMessage(sent.Id)!.MessageStatus}");
        var after = app.Db.GetMessage(sent.Id)!;
        Assert.Equal(stamp, after.Timestamp); // the same message, not a new one
        Assert.Equal(0, app.Messages.InterruptedCount);
        Assert.Single(app.Db.GetDirectMessages(bob.Id), m => m.IsOutgoing);
    }

    // MARK: Replies from notifications (2bb36b84)

    [Fact]
    public async Task ANotificationReplyForAnotherRadioIsRejected()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = Contact(app, "Alice");
        var key = MessageService.DmKey(alice.Id);
        Assert.Equal(app.RadioId, app.Messages.RadioIdFor(key));
        var before = app.Db.GetDirectMessages(alice.Id).Count;

        Assert.Throws<MessageServiceException>(() => app.Messages.SendQuickReply("ab".PadRight(64, '0'), key, "From the toast"));
        Assert.Throws<MessageServiceException>(() => app.Messages.SendQuickReply(null, key, "From the toast"));
        Assert.Throws<MessageServiceException>(() => app.Messages.SendQuickReply("ab".PadRight(64, '0'), MessageService.ChannelKey(0), "Hi all"));
        Assert.Equal(before, app.Db.GetDirectMessages(alice.Id).Count);

        app.Messages.SendQuickReply(app.RadioId, key, "From the toast");
        Assert.Contains(app.Db.GetDirectMessages(alice.Id), m => m.IsOutgoing && m.Text == "From the toast");
        Assert.False(app.Messages.IsUnread(key));

        await app.DisconnectAsync();
        Assert.Throws<MessageServiceException>(() => app.Messages.SendQuickReply(app.RadioId, key, "Offline"));
    }

    // MARK: Channel regions (ae5fa6ec)

    private static readonly byte SetFloodScopeCode = (byte)CommandCode.SetFloodScope;
    private static readonly byte SendChannelCode = (byte)CommandCode.SendChannelMessage;

    /// <summary>For each channel message sent: the flood scope command that came last before it.</summary>
    private static List<(byte Channel, byte[]? Scope)> ScopesBeforeChannelSends(IReadOnlyList<byte[]> sent)
    {
        var result = new List<(byte, byte[]?)>();
        byte[]? scope = null;
        foreach (var cmd in sent)
        {
            if (cmd.Length > 0 && cmd[0] == SetFloodScopeCode) scope = cmd;
            else if (cmd.Length > 2 && cmd[0] == SendChannelCode) result.Add((cmd[2], scope));
        }
        return result;
    }

    [Fact]
    public async Task ChannelRegionIsSetRightBeforeEveryChannelSendIncludingRetriesAndReplies()
    {
        var radio = new SimulatedRadio(chatter: false);
        var recorder = new RecordingTransport(radio);
        await using var f = new AppFixture();
        f.App.SimulatorFactory = () => recorder;
        await f.ConnectAsync();
        var app = f.App;
        app.Channels.SetFloodScope(app.Db.GetChannel(app.RadioId!, 1)!, "region:europe");
        var region = PacketBuilder.SetFloodScope(new FloodScope.Region("europe").ScopeKey());
        var alice = Contact(app, "Alice");

        // DMs (radio default scope) and region-scoped channel messages at the same time.
        var channelMessages = new List<MessageRecord>();
        for (var i = 0; i < 4; i++)
        {
            app.Messages.QueueDirectMessage(alice, $"DM {i}");
            channelMessages.Add(app.Messages.QueueChannelMessage(1, $"Channel {i}"));
            channelMessages.Add(app.Messages.QueueChannelMessage(0, $"Public {i}"));
        }
        Assert.True(await AppFixture.Eventually(() => channelMessages.All(m => app.Db.GetMessage(m.Id)!.MessageStatus == MessageStatus.Sent), 15000),
            string.Join(", ", channelMessages.Select(m => app.Db.GetMessage(m.Id)!.MessageStatus)) + " | " + string.Join(" ", app.Log.Snapshot().TakeLast(40).Select(e => e.Message)));
        // A retry and a reply from a notification.
        Assert.Equal(4, ScopesBeforeChannelSends(recorder.Sent).Count(s => s.Channel == 1));
        app.Messages.QueueDirectMessage(alice, "In between");
        await Task.Delay(300);
        app.Messages.ResendChannel(channelMessages[0].Id);
        app.Messages.SendQuickReply(app.RadioId, MessageService.ChannelKey(1), "From the toast");
        Assert.True(await AppFixture.Eventually(() => ScopesBeforeChannelSends(recorder.Sent).Count(s => s.Channel == 1) == 6, 10000));

        var sends = ScopesBeforeChannelSends(recorder.Sent);
        Assert.All(sends.Where(s => s.Channel == 1), s => Assert.Equal(region, s.Scope));
        Assert.All(sends.Where(s => s.Channel == 0), s => Assert.NotEqual(region, s.Scope));
    }

    // MARK: Reactions (b8a47d40, 304fa471)

    [Fact]
    public async Task ReactionsAreSavedWithTheirSummaryAndNotForDeletedMessages()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        Assert.True(await AppFixture.Eventually(() => app.Db.GetChannelMessages(app.RadioId!, 0).Count == 1));
        var target = app.Db.GetChannelMessages(app.RadioId!, 0)[0];
        ReactionRecord R(string sender, string emoji) => new() { MessageId = target.Id, RadioId = app.RadioId!, Emoji = emoji, SenderName = sender, MessageHash = "h", RawText = "r", ChannelIndex = 0 };

        Assert.True(app.Db.AdmitReaction(R("Ann", "👍"))!.Inserted);
        Assert.False(app.Db.AdmitReaction(R("Ann", "👍"))!.Inserted);
        Assert.Equal("👍:2", app.Db.AdmitReaction(R("Ben", "👍"))!.Summary);
        Assert.Equal("👍:2", app.Db.GetMessage(target.Id)!.ReactionSummary);

        app.Messages.DeleteMessage(target);
        Assert.Null(app.Db.AdmitReaction(R("Cy", "🔥"))); // no foreign-key failure, nothing stored

        // meshcore-open v1 reaction stamped at the largest time stamp (304fa471): no overflow, just no match, and not shown.
        var bob = Contact(app, "Bob");
        var v1 = $"r:{(ulong)uint.MaxValue * 1000}_1_2:👍";
        await app.Messages.IngestDirectAsync(new ContactMessage(bob.PublicKey.Prefix(6), 0xFF, 0, DateTimeOffset.UtcNow, null, v1, null), fromInitialSync: false);
        Assert.DoesNotContain(app.Db.GetDirectMessages(bob.Id), m => m.Text == v1);
    }

    // MARK: Sync status (13519d37, 4eb5f81e, fcb37bf3)

    [Fact]
    public async Task ReadyComesAfterTheMessageCatchUpAndAFailedSyncIsReported()
    {
        await using var f = new AppFixture();
        var app = f.App;
        var sawCatchUp = false;
        app.StatusChanged += () => { if (app.MessageSyncStartedAt is not null && app.Status == LinkStatus.Syncing) sawCatchUp = true; };
        await f.ConnectAsync();
        Assert.True(sawCatchUp);
        Assert.Equal(LinkStatus.Ready, app.Status);
        Assert.Null(app.MessageSyncStartedAt);
        Assert.NotNull(app.LastSyncAt);
        // The queued messages are in by the time the app says it's ready.
        Assert.Single(app.Db.GetChannelMessages(app.RadioId!, 0));
        Assert.Null(app.SyncFailedAt);

        app.MarkSyncFailed("test");
        Assert.NotNull(app.SyncFailedAt);
        Assert.Equal("test", app.SyncFailure);
        await app.ResyncAsync();
        Assert.Null(app.SyncFailedAt);
    }

    // MARK: Link previews and inline images (98e62809, d9709e0d, 246c0d69, c3a91bb9)

    private sealed class FakeWeb(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private static HttpResponseMessage Body(byte[] bytes, string type, long? declaredLength = null)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        content.Headers.ContentLength = declaredLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public void OnlyPublicHttpAddressesAreFetched()
    {
        Assert.True(SafeWebFetcher.IsAllowedUrl(new Uri("https://meshcore.co.uk/x")));
        Assert.True(SafeWebFetcher.IsAllowedUrl(new Uri("http://8.8.8.8/")));
        foreach (var bad in new[] { "http://localhost/", "http://127.0.0.1/", "http://10.1.2.3/", "http://192.168.1.1/", "http://172.20.0.1/", "http://169.254.169.254/latest",
                     "http://[::1]/", "http://[fe80::1]/", "http://[fd00::1]/", "http://100.64.0.1/", "ftp://example.com/", "file:///c:/x", "http://[::ffff:127.0.0.1]/" })
            Assert.False(SafeWebFetcher.IsAllowedUrl(new Uri(bad)), bad);
        Assert.Equal("https://example.com/a?b=1", SafeWebFetcher.HttpsUpgrade(new Uri("http://example.com:80/a?b=1")).AbsoluteUri);
        Assert.Equal("https://example.com:8080/a", SafeWebFetcher.HttpsUpgrade(new Uri("http://example.com:8080/a")).AbsoluteUri);
    }

    [Fact]
    public async Task RedirectsAreCheckedAndPreviewsUseHttps()
    {
        // A public link that redirects to the local network is not followed.
        var web = new FakeWeb(r => r.RequestUri!.Host == "example.com" ? Redirect("http://192.168.1.10/admin") : Body([1], "image/png"));
        using (var fetch = new SafeWebFetcher(web))
        {
            var r = await fetch.GetAsync(new Uri("https://example.com/pic.png"), false, 1000, t => t.StartsWith("image/"));
            Assert.Equal(WebFetchOutcome.Blocked, r.Outcome);
            Assert.Single(web.Requests);
        }

        // Preview fetches go over https; an http redirect hop is upgraded, and one back to the same URL stops.
        var loop = new FakeWeb(r => r.RequestUri!.AbsolutePath == "/page" ? Redirect("http://example.com/page") : Body("<title>x</title>"u8.ToArray(), "text/html"));
        using (var fetch = new SafeWebFetcher(loop))
        {
            var r = await fetch.GetAsync(new Uri("http://example.com/page"), true, 1000, t => t.Contains("html"), truncate: true);
            Assert.Equal(WebFetchOutcome.Failed, r.Outcome);
            Assert.All(loop.Requests, u => Assert.Equal("https", u.Scheme));
            Assert.Single(loop.Requests);
        }
        var hop = new FakeWeb(r => r.RequestUri!.AbsolutePath == "/old" ? Redirect("/new") : Body("<title>x</title>"u8.ToArray(), "text/html"));
        using (var fetch = new SafeWebFetcher(hop))
        {
            var r = await fetch.GetAsync(new Uri("http://example.com/old"), true, 1000, t => t.Contains("html"), truncate: true);
            Assert.True(r.IsOk);
            Assert.Equal("https://example.com/new", r.FinalUri!.AbsoluteUri);
        }
        // Inline images keep their scheme (no upgrade) but redirects are still checked.
        var plain = new FakeWeb(_ => Body([1, 2, 3], "image/png"));
        using (var fetch = new SafeWebFetcher(plain))
        {
            Assert.True((await fetch.GetAsync(new Uri("http://example.com/a.png"), false, 10, t => t.StartsWith("image/"))).IsOk);
            Assert.Equal("http", plain.Requests.Single().Scheme);
        }
    }

    [Fact]
    public async Task ImageDownloadsStopAtTheirLimitWhateverTheHeadersSay()
    {
        var big = new byte[5000];
        // No Content-Length, and a Content-Length that understates the body: both are cut off while arriving.
        foreach (long? declared in new long?[] { null, 100 })
        {
            using var fetch = new SafeWebFetcher(new FakeWeb(_ => Body(big, "image/jpeg", declared)));
            Assert.Equal(WebFetchOutcome.TooLarge, (await fetch.GetAsync(new Uri("https://example.com/a.jpg"), false, 1000, t => t.StartsWith("image/"))).Outcome);
        }
        // Exactly the limit is fine.
        using (var fetch = new SafeWebFetcher(new FakeWeb(_ => Body(new byte[1000], "image/jpeg"))))
            Assert.Equal(1000, (await fetch.GetAsync(new Uri("https://example.com/a.jpg"), false, 1000, t => t.StartsWith("image/"))).Body!.Length);
        // An image link that serves a page is reported as such (it gets a link preview instead).
        using (var fetch = new SafeWebFetcher(new FakeWeb(_ => Body("<html>"u8.ToArray(), "text/html"))))
            Assert.Equal(WebFetchOutcome.ServesHtml, (await fetch.GetAsync(new Uri("https://imgur.com/a.png"), false, 1000, t => t.StartsWith("image/"), htmlMeansPage: true)).Outcome);
        // Pages are read only up to the limit.
        using (var fetch = new SafeWebFetcher(new FakeWeb(_ => Body(big, "text/html"))))
            Assert.Equal(1000, (await fetch.GetAsync(new Uri("https://example.com/"), true, 1000, t => t.Contains("html"), truncate: true)).Body!.Length);
    }
}

using Dapper;
using MC1.Core.Models;
using MC1.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace MC1.Core.Data;

/// <summary>Result of <see cref="AppDatabase.AdmitReaction"/>: the parent's summary after the write.</summary>
public sealed record ReactionAdmission(string MessageId, string? Summary, bool Inserted);

public sealed partial class AppDatabase
{
    /// <summary>The newest schema this build knows (PRAGMA user_version).</summary>
    public const int SchemaVersion = 2;

    /// <summary>Reactions counted in a message's summary (the most recent ones), like the iPhone app.</summary>
    public const int ReactionSummaryRowLimit = 100;

    /// <summary>The database's schema version (for diagnostics and tests).</summary>
    public int CurrentSchemaVersion
    {
        get
        {
            using var c = Open();
            return (int)c.ExecuteScalar<long>("PRAGMA user_version;");
        }
    }

    /// <summary>
    /// Brings an existing database up to <see cref="SchemaVersion"/>. Each step runs once, in one transaction with its
    /// version bump, so an interrupted upgrade is repeated as a whole on the next start. Columns are added only when
    /// missing, so a step never fails on a database that already has them.
    /// </summary>
    private static void Migrate(SqliteConnection c, long version)
    {
        if (version < 2) Step(c, 2, tx =>
        {
            // Chat pins live on this PC, separate from the radio's favourite flag.
            EnsureColumn(c, tx, "Contacts", "IsPinned", "INTEGER DEFAULT 0");
            EnsureColumn(c, tx, "Channels", "IsPinned", "INTEGER DEFAULT 0");
            EnsureColumn(c, tx, "RemoteSessions", "IsPinned", "INTEGER DEFAULT 0");
            // Unsent room text is kept like DM and channel drafts.
            EnsureColumn(c, tx, "RemoteSessions", "DraftText", "TEXT");
            // One-time copy (like the iPhone app): contacts that are favourites now start pinned. A favourite set
            // after this stays unpinned; the step's version bump records that the copy was made.
            c.Execute("UPDATE Contacts SET IsPinned=1 WHERE (Flags & @fav) <> 0", new { fav = (int)MeshCore.ContactFlags.Favorite }, tx);
        });
    }

    private static void Step(SqliteConnection c, int toVersion, Action<SqliteTransaction> apply)
    {
        using var tx = c.BeginTransaction();
        apply(tx);
        c.Execute($"PRAGMA user_version={toVersion};", transaction: tx);
        tx.Commit();
    }

    private static void EnsureColumn(SqliteConnection c, SqliteTransaction tx, string table, string column, string definition)
    {
        var columns = c.Query<string>($"SELECT name FROM pragma_table_info('{table}')", transaction: tx);
        if (columns.Contains(column, StringComparer.OrdinalIgnoreCase)) return;
        c.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}", transaction: tx);
    }

    // MARK: Chat pins (this PC only; not the radio's favourite flag)

    public bool SetContactPinned(string contactId, bool pinned)
    {
        using var c = Open();
        return c.Execute("UPDATE Contacts SET IsPinned=@pinned WHERE Id=@contactId", new { contactId, pinned }) > 0;
    }

    public bool SetChannelPinned(string radioId, int idx, bool pinned)
    {
        using var c = Open();
        return c.Execute("UPDATE Channels SET IsPinned=@pinned WHERE RadioId=@radioId AND Idx=@idx", new { radioId, idx, pinned }) > 0;
    }

    public bool SetRemoteSessionPinned(string sessionId, bool pinned)
    {
        using var c = Open();
        return c.Execute("UPDATE RemoteSessions SET IsPinned=@pinned WHERE Id=@sessionId", new { sessionId, pinned }) > 0;
    }

    // MARK: Marked unread (the chat shows as unread until it is opened)

    public bool MarkContactUnread(string contactId)
    {
        using var c = Open();
        return c.Execute("UPDATE Contacts SET UnreadCount=MAX(UnreadCount, 1) WHERE Id=@contactId", new { contactId }) > 0;
    }

    public bool MarkChannelUnread(string radioId, int idx)
    {
        using var c = Open();
        return c.Execute("UPDATE Channels SET UnreadCount=MAX(UnreadCount, 1) WHERE RadioId=@radioId AND Idx=@idx", new { radioId, idx }) > 0;
    }

    public bool MarkRoomUnread(string sessionId)
    {
        using var c = Open();
        return c.Execute("UPDATE RemoteSessions SET UnreadCount=MAX(UnreadCount, 1) WHERE Id=@sessionId", new { sessionId }) > 0;
    }

    // MARK: Single-field updates (so an older copy of the row can't put back other fields)

    public void SetContactNotificationLevel(string contactId, NotificationLevel level)
    {
        using var c = Open();
        c.Execute("UPDATE Contacts SET NotificationLevel=@level WHERE Id=@contactId", new { contactId, level = (int)level });
    }

    public void SetChannelNotificationLevel(string radioId, int idx, NotificationLevel level)
    {
        using var c = Open();
        c.Execute("UPDATE Channels SET NotificationLevel=@level WHERE RadioId=@radioId AND Idx=@idx", new { radioId, idx, level = (int)level });
    }

    public void SetChannelFloodScope(string radioId, int idx, string? scope)
    {
        using var c = Open();
        c.Execute("UPDATE Channels SET FloodScope=@scope WHERE RadioId=@radioId AND Idx=@idx", new { radioId, idx, scope });
    }

    public void SetRemoteSessionNotificationLevel(string sessionId, NotificationLevel level)
    {
        using var c = Open();
        c.Execute("UPDATE RemoteSessions SET NotificationLevel=@level WHERE Id=@sessionId", new { sessionId, level = (int)level });
    }

    public void SetRoomDraft(string sessionId, string? draft)
    {
        using var c = Open();
        c.Execute("UPDATE RemoteSessions SET DraftText=@draft WHERE Id=@sessionId", new { sessionId, draft });
    }

    // MARK: Reactions

    /// <summary>
    /// Saves a reaction (unless that sender already sent that emoji) and writes the parent's summary from the rows as
    /// they are now, in one transaction. Null when the message is gone (deleted meanwhile).
    /// </summary>
    public ReactionAdmission? AdmitReaction(ReactionRecord r)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM Messages WHERE Id=@MessageId", r, tx) == 0) return null;
        var inserted = c.ExecuteScalar<long>("SELECT COUNT(*) FROM Reactions WHERE MessageId=@MessageId AND SenderName=@SenderName AND Emoji=@Emoji", r, tx) == 0;
        if (inserted)
            c.Execute("""
                INSERT INTO Reactions(Id,MessageId,RadioId,Emoji,SenderName,MessageHash,RawText,ContactId,ChannelIndex,ReceivedAt,IsOutgoing)
                VALUES(@Id,@MessageId,@RadioId,@Emoji,@SenderName,@MessageHash,@RawText,@ContactId,@ChannelIndex,@ReceivedAt,@IsOutgoing)
                """, r, tx);
        var rows = c.Query<ReactionRecord>(
            "SELECT Emoji, ReceivedAt FROM Reactions WHERE MessageId=@MessageId ORDER BY ReceivedAt DESC LIMIT @limit",
            new { r.MessageId, limit = ReactionSummaryRowLimit }, tx).ToList();
        var summary = ReactionParser.BuildSummary(rows.Select(x => (x.Emoji, x.ReceivedAt)));
        var stored = summary.Length == 0 ? null : summary;
        c.Execute("UPDATE Messages SET ReactionSummary=@stored WHERE Id=@MessageId", new { stored, r.MessageId }, tx);
        tx.Commit();
        return new ReactionAdmission(r.MessageId, stored, inserted);
    }
}

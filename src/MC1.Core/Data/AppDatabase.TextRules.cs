using Dapper;
using MC1.Core.Services;

namespace MC1.Core.Data;

public sealed partial class AppDatabase
{
    // MARK: Recently blocked (messages hidden by a filter rule)

    /// <summary>Adds a hidden message and keeps only the newest <paramref name="limit"/>. False when it was already listed.</summary>
    public bool AddRecentlyBlocked(RecentlyBlockedRecord r, int limit)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var added = c.Execute("""
            INSERT OR IGNORE INTO RecentlyBlocked(Id,RadioId,RadioName,ConversationKey,ConversationName,Sender,Body,ArrivedAt,RulePhrase,RuleLookIn,RuleMatch,DedupKey)
            VALUES(@Id,@RadioId,@RadioName,@ConversationKey,@ConversationName,@Sender,@Body,@ArrivedAt,@RulePhrase,@RuleLookIn,@RuleMatch,@DedupKey)
            """, r, tx) > 0;
        if (added)
            c.Execute("DELETE FROM RecentlyBlocked WHERE Id NOT IN (SELECT Id FROM RecentlyBlocked ORDER BY ArrivedAt DESC, Id DESC LIMIT @limit)", new { limit }, tx);
        tx.Commit();
        return added;
    }

    public IReadOnlyList<RecentlyBlockedRecord> GetRecentlyBlocked()
    {
        using var c = Open();
        return c.Query<RecentlyBlockedRecord>("SELECT * FROM RecentlyBlocked ORDER BY ArrivedAt DESC, Id DESC").ToList();
    }

    public int CountRecentlyBlocked()
    {
        using var c = Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM RecentlyBlocked");
    }

    public void DeleteRecentlyBlocked(string id)
    {
        using var c = Open();
        c.Execute("DELETE FROM RecentlyBlocked WHERE Id=@id", new { id });
    }

    public void ClearRecentlyBlocked()
    {
        using var c = Open();
        c.Execute("DELETE FROM RecentlyBlocked");
    }

    /// <summary>Removes one radio's hidden messages (when its data is deleted from this PC).</summary>
    public void ClearRecentlyBlocked(string radioId)
    {
        using var c = Open();
        c.Execute("DELETE FROM RecentlyBlocked WHERE RadioId=@radioId", new { radioId });
    }
}

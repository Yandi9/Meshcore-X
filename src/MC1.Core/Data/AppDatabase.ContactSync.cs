using Dapper;
using MC1.Core.Models;
using MC1.Core.Utilities;

namespace MC1.Core.Data;

public sealed partial class AppDatabase
{
    /// <summary>
    /// Saves the contacts of a contact sync in one transaction (one commit instead of one per contact), keeping the
    /// local-only fields of existing rows. The radio's favourite flag and path win. A plausible advert time also
    /// advances "last heard", so activity heard by the radio while this PC was away counts.
    /// </summary>
    public IReadOnlyList<ContactRecord> SaveSyncedContacts(string radioId, IReadOnlyCollection<MeshCore.MeshContact> frames, DateTimeOffset reference)
    {
        if (frames.Count == 0) return [];
        using var c = Open();
        using var tx = c.BeginTransaction();
        var existing = c.Query<ContactRecord>("SELECT * FROM Contacts WHERE RadioId=@radioId", new { radioId }, tx)
            .GroupBy(r => Convert.ToHexString(r.PublicKey))
            .ToDictionary(g => g.Key, g => g.First());
        var saved = new List<ContactRecord>(frames.Count);
        foreach (var frame in frames)
        {
            var key = Convert.ToHexString(frame.PublicKey);
            if (!existing.TryGetValue(key, out var rec))
            {
                rec = new ContactRecord { RadioId = radioId };
                existing[key] = rec;
            }
            rec.ApplyFrom(frame);
            if (AdvertTimestamp.IsPlausible(rec.LastAdvert, reference)) rec.LastHeard = Math.Max(rec.LastHeard, rec.LastAdvert * 1000);
            UpsertContact(c, rec, tx);
            saved.Add(rec);
        }
        tx.Commit();
        return saved;
    }
}

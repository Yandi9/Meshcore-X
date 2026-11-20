using System.Text.Json.Serialization;
using MC1.Core.Models;

namespace MC1.Core.Services;

/// <summary>The kinds of chat a filter rule can apply to (port of Swift <c>TextRulesPreference.Kind</c>).</summary>
public enum TextRuleKind { PublicChannel, HashtagChannel, PrivateChannel, DirectMessage, Room }

/// <summary>Which part of an incoming message a rule looks at.</summary>
public enum TextRuleLookIn { Message, Sender, MessageOrSender }

/// <summary>How a rule's phrase is compared.</summary>
public enum TextRuleMatch { Contains, WholeField }

/// <summary>Whether a kind of chat uses the rules: not at all, every chat (including ones joined later), or some chats.</summary>
public enum TextRuleSelectionMode { Off, All, Fixed }

/// <summary>A word or phrase that hides incoming messages ("Blocks &amp; Filters", MeshCore One 1.7).</summary>
public sealed class TextRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Phrase { get; set; } = "";
    public TextRuleLookIn LookIn { get; set; } = TextRuleLookIn.Message;
    public TextRuleMatch Match { get; set; } = TextRuleMatch.Contains;

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Phrase);
}

public sealed class TextRuleSelection
{
    public TextRuleSelectionMode Mode { get; set; }
    /// <summary>For <see cref="TextRuleSelectionMode.Fixed"/>: the chosen chats' keys (<see cref="TextRules.ConversationKey"/>).</summary>
    public List<string> Chats { get; set; } = [];

    [JsonIgnore]
    public bool IsOn => Mode != TextRuleSelectionMode.Off;

    public bool Includes(string key) => Mode switch
    {
        TextRuleSelectionMode.All => true,
        TextRuleSelectionMode.Fixed => Chats.Contains(key),
        _ => false,
    };
}

/// <summary>The rules and where they apply; saved with the app's settings on this PC.</summary>
public sealed class TextRulesPreference
{
    public List<TextRule> Rules { get; set; } = [];

    /// <summary>Per kind; a missing kind uses <see cref="DefaultMode"/> (public and hashtag channels on, the rest off).</summary>
    public Dictionary<TextRuleKind, TextRuleSelection> Selections { get; set; } = new();

    public static TextRuleSelectionMode DefaultMode(TextRuleKind kind) =>
        kind is TextRuleKind.PublicChannel or TextRuleKind.HashtagChannel ? TextRuleSelectionMode.All : TextRuleSelectionMode.Off;

    public TextRuleSelection SelectionFor(TextRuleKind kind) =>
        Selections.TryGetValue(kind, out var s) ? s : new TextRuleSelection { Mode = DefaultMode(kind) };

    public bool AppliesTo(TextRuleKind kind, string conversationKey) => SelectionFor(kind).Includes(conversationKey);

    /// <summary>The first rule that matches the message text or sender name (case-insensitive), or null.</summary>
    public TextRule? FirstMatch(string? body, string? sender)
    {
        foreach (var rule in Rules)
        {
            if (!rule.IsValid) continue;
            if (rule.LookIn != TextRuleLookIn.Sender && TextRules.Matches(body, rule)) return rule;
            if (rule.LookIn != TextRuleLookIn.Message && TextRules.Matches(sender, rule)) return rule;
        }
        return null;
    }

    public TextRulesPreference Clone() => new()
    {
        Rules = Rules.Select(r => new TextRule { Id = r.Id, Phrase = r.Phrase, LookIn = r.LookIn, Match = r.Match }).ToList(),
        Selections = Selections.ToDictionary(kv => kv.Key, kv => new TextRuleSelection { Mode = kv.Value.Mode, Chats = [.. kv.Value.Chats] }),
    };
}

/// <summary>A message a rule hid. The newest <see cref="TextRules.RecentlyBlockedLimit"/> are kept on this PC (not in backups).</summary>
public sealed class RecentlyBlockedRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RadioId { get; set; } = "";
    public string? RadioName { get; set; }
    public string ConversationKey { get; set; } = "";
    public string? ConversationName { get; set; }
    public string? Sender { get; set; }
    public string Body { get; set; } = "";
    public long ArrivedAt { get; set; } = Time.Now();
    public string RulePhrase { get; set; } = "";
    public int RuleLookIn { get; set; }
    public int RuleMatch { get; set; }
    /// <summary>Identifies the message, so repeats of the same packet aren't listed twice.</summary>
    public string DedupKey { get; set; } = "";
}

public static class TextRules
{
    public const int RecentlyBlockedLimit = 100;

    public static bool Matches(string? field, TextRule rule)
    {
        if (string.IsNullOrEmpty(field) || string.IsNullOrEmpty(rule.Phrase)) return false;
        return rule.Match == TextRuleMatch.WholeField
            ? string.Equals(field, rule.Phrase, StringComparison.OrdinalIgnoreCase)
            : field.Contains(rule.Phrase, StringComparison.OrdinalIgnoreCase);
    }

    public static TextRuleKind ChannelKind(ChannelRecord channel) =>
        channel.IsPublic ? TextRuleKind.PublicChannel : channel.IsHashtag ? TextRuleKind.HashtagChannel : TextRuleKind.PrivateChannel;

    /// <summary>A chat's stable key for a rule's chat list: the radio, then the channel secret or the node's public key,
    /// so the choice follows the channel or node even when its slot on the radio changes.</summary>
    public static string ConversationKey(string radioId, ChannelRecord channel) => $"{radioId}|ch:{Convert.ToHexString(channel.Secret)}";
    public static string ConversationKey(string radioId, ContactRecord contact) => $"{radioId}|dm:{Convert.ToHexString(contact.PublicKey)}";
    public static string ConversationKey(string radioId, RemoteSessionRecord room) => $"{radioId}|room:{Convert.ToHexString(room.PublicKey)}";

    public static string KindTitle(TextRuleKind kind) => kind switch
    {
        TextRuleKind.PublicChannel => L.T("Public"),
        TextRuleKind.HashtagChannel => L.T("Hashtag Channels"),
        TextRuleKind.PrivateChannel => L.T("Private Channels"),
        TextRuleKind.DirectMessage => L.T("Direct Messages"),
        _ => L.T("Rooms"),
    };

    public static string LookInTitle(TextRuleLookIn lookIn) => lookIn switch
    {
        TextRuleLookIn.Sender => L.T("Sender"),
        TextRuleLookIn.MessageOrSender => L.T("Message or Sender"),
        _ => L.T("Message"),
    };

    public static string MatchTitle(TextRuleLookIn lookIn, TextRuleMatch match) => match switch
    {
        TextRuleMatch.Contains => L.T("Contains"),
        _ => lookIn switch
        {
            TextRuleLookIn.Message => L.T("Whole Message"),
            TextRuleLookIn.Sender => L.T("Whole Name"),
            _ => L.T("Whole Field"),
        },
    };

    /// <summary>"Message · Contains".</summary>
    public static string Summary(TextRule rule) => L.F("{0} · {1}", LookInTitle(rule.LookIn), MatchTitle(rule.LookIn, rule.Match));

    public static string Footer(TextRuleLookIn lookIn, TextRuleMatch match) => (lookIn, match) switch
    {
        (TextRuleLookIn.Message, TextRuleMatch.Contains) => L.T("Matches anywhere in the message, including inside a word."),
        (TextRuleLookIn.Message, _) => L.T("Matches only when the whole message is this phrase."),
        (TextRuleLookIn.Sender, TextRuleMatch.Contains) => L.T("Matches anywhere in the sender’s name, including inside a word. Reactions from that sender are hidden too."),
        (TextRuleLookIn.Sender, _) => L.T("Matches only when the sender’s name is exactly this. Reactions from that sender are hidden too."),
        (_, TextRuleMatch.Contains) => L.T("Matches the message or the sender’s name, including inside a word. Reactions from a matching sender are hidden too."),
        _ => L.T("Matches only when the whole message or the whole sender name is this phrase. Reactions from a matching sender are hidden too."),
    };
}

/// <summary>Applies the filter rules to incoming messages and keeps the "Recently blocked" list.</summary>
public sealed class TextRulesService(MeshApp app)
{
    /// <summary>Raised when the rules or the recently blocked list change.</summary>
    public event Action? Changed;

    public TextRulesPreference Current => app.Settings.Current.TextRules ??= new TextRulesPreference();

    public void Update(Action<TextRulesPreference> change)
    {
        app.Settings.Update(s =>
        {
            var p = (s.TextRules ?? new TextRulesPreference()).Clone();
            change(p);
            s.TextRules = p;
        });
        Changed?.Invoke();
    }

    public void AddRule(string phrase, TextRuleLookIn lookIn, TextRuleMatch match)
    {
        phrase = phrase.Trim();
        if (phrase.Length == 0) return;
        Update(p => p.Rules.Add(new TextRule { Phrase = phrase, LookIn = lookIn, Match = match }));
    }

    public void RemoveRule(Guid id) => Update(p => p.Rules.RemoveAll(r => r.Id == id));

    public void SetSelection(TextRuleKind kind, TextRuleSelectionMode mode, IEnumerable<string>? chats = null) =>
        Update(p => p.Selections[kind] = new TextRuleSelection { Mode = mode, Chats = chats?.Distinct().ToList() ?? [] });

    /// <summary>
    /// True when a rule hides this incoming message. A hidden message (not a reaction) is added to "Recently blocked".
    /// For a reaction, only the sender's name is checked: reactions from a hidden sender are hidden too.
    /// </summary>
    internal bool ShouldHide(TextRuleKind kind, string conversationKey, string? conversationName, string? body, string? sender,
        bool isReaction, string dedupKey)
    {
        var pref = Current;
        if (pref.Rules.Count == 0 || !pref.AppliesTo(kind, conversationKey)) return false;
        if (pref.FirstMatch(isReaction ? null : body, sender) is not { } rule) return false;
        if (!isReaction && app.RadioId is { } radioId)
        {
            try
            {
                var added = app.Db.AddRecentlyBlocked(new RecentlyBlockedRecord
                {
                    RadioId = radioId,
                    RadioName = app.SelfName,
                    ConversationKey = conversationKey,
                    ConversationName = conversationName,
                    Sender = sender,
                    Body = body ?? "",
                    RulePhrase = rule.Phrase,
                    RuleLookIn = (int)rule.LookIn,
                    RuleMatch = (int)rule.Match,
                    DedupKey = dedupKey,
                }, TextRules.RecentlyBlockedLimit);
                if (added) Changed?.Invoke();
            }
            catch (Exception ex) { app.Log.Warn("Filters", "Couldn't keep the hidden message: " + ex.Message); }
        }
        app.Log.Debug("Filters", $"Hid an incoming message ({kind})");
        return true;
    }

    public IReadOnlyList<RecentlyBlockedRecord> RecentlyBlocked() => app.Db.GetRecentlyBlocked();

    public int RecentlyBlockedCount() => app.Db.CountRecentlyBlocked();

    public void ClearRecentlyBlocked()
    {
        app.Db.ClearRecentlyBlocked();
        Changed?.Invoke();
    }

    public void DeleteRecentlyBlocked(string id)
    {
        app.Db.DeleteRecentlyBlocked(id);
        Changed?.Invoke();
    }

    /// <summary>The chats of a kind on the connected (or last) radio, for a rule's chat list.</summary>
    public IReadOnlyList<(string Key, string Name)> Conversations(TextRuleKind kind)
    {
        if (app.RadioId is not { } radioId) return [];
        return kind switch
        {
            TextRuleKind.DirectMessage => app.Db.GetContacts(radioId).Where(c => c.ContactType == MeshCore.ContactType.Chat)
                .Select(c => (TextRules.ConversationKey(radioId, c), c.DisplayName)).OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            TextRuleKind.Room => app.Db.GetRemoteSessions(radioId).Where(r => r.IsRoom)
                .Select(r => (TextRules.ConversationKey(radioId, r), r.Name)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => app.Db.GetChannels(radioId).Where(c => c.IsConfigured && TextRules.ChannelKind(c) == kind)
                .Select(c => (TextRules.ConversationKey(radioId, c), c.DisplayName)).ToList(),
        };
    }
}

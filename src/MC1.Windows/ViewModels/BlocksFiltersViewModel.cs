using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Services;

namespace MC1.Windows.ViewModels;

/// <summary>A choice in a picker: the stored value and its text in the app's language.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record TextRuleRow(Guid Id, string Phrase, string Summary);

public sealed partial class TextRuleChatRow(TextRuleKindRow owner, string key, string name) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => owner.ChatToggled();
}

/// <summary>One kind of chat (public, hashtag, private channels, DMs, rooms): whether the rules apply, and to which chats.</summary>
public sealed partial class TextRuleKindRow : ObservableObject
{
    private readonly BlocksFiltersViewModel _owner;
    private bool _loading;

    public TextRuleKindRow(BlocksFiltersViewModel owner, TextRuleKind kind)
    {
        _owner = owner;
        Kind = kind;
        Title = TextRules.KindTitle(kind);
    }

    public TextRuleKind Kind { get; }
    public string Title { get; }
    public ObservableCollection<TextRuleChatRow> Chats { get; } = new();

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isAll;
    [ObservableProperty] private string _summary = "";

    public bool HasChats => Chats.Count > 0;
    public bool ShowsChatList => IsOn && !IsAll;

    internal void Load(TextRuleSelection selection, IReadOnlyList<(string Key, string Name)> chats)
    {
        _loading = true;
        IsOn = selection.IsOn;
        IsAll = selection.Mode != TextRuleSelectionMode.Fixed;
        Chats.Clear();
        foreach (var (key, name) in chats)
            Chats.Add(new TextRuleChatRow(this, key, name) { IsSelected = selection.Mode == TextRuleSelectionMode.Fixed && selection.Chats.Contains(key) });
        _loading = false;
        OnPropertyChanged(nameof(HasChats));
        OnPropertyChanged(nameof(ShowsChatList));
        UpdateSummary(selection);
    }

    partial void OnIsOnChanged(bool value) { OnPropertyChanged(nameof(ShowsChatList)); Save(); }
    partial void OnIsAllChanged(bool value) { OnPropertyChanged(nameof(ShowsChatList)); Save(); }

    internal void ChatToggled() => Save();

    private void Save()
    {
        if (_loading) return;
        var mode = !IsOn ? TextRuleSelectionMode.Off : IsAll ? TextRuleSelectionMode.All : TextRuleSelectionMode.Fixed;
        var chats = Chats.Where(c => c.IsSelected).Select(c => c.Key).ToList();
        // Keep chats chosen earlier that aren't on this radio's list right now.
        var earlier = _owner.Preference.SelectionFor(Kind).Chats.Where(k => Chats.All(c => c.Key != k));
        _owner.SetSelection(Kind, mode, chats.Concat(earlier));
        UpdateSummary(new TextRuleSelection { Mode = mode, Chats = chats });
    }

    private void UpdateSummary(TextRuleSelection selection)
    {
        if (selection.Mode == TextRuleSelectionMode.Off) { Summary = ""; return; }
        if (selection.Mode == TextRuleSelectionMode.All) { Summary = ""; return; }
        var names = Chats.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        Summary = names.Count switch
        {
            0 => L.T("No chats"),
            1 or 2 => string.Join(", ", names),
            _ => L.Plural(names.Count, "{0} chat", "{0} chats"),
        };
    }
}

public sealed partial class RecentlyBlockedRow(RecentlyBlockedRecord r) : ObservableObject
{
    public string Id { get; } = r.Id;
    public string Text { get; } = string.IsNullOrEmpty(r.Sender) ? r.Body : $"{r.Sender}: {r.Body}";
    public string Caption { get; } = string.Join(" · ", new[]
    {
        r.ConversationName ?? L.T("Unknown conversation"),
        $"“{r.RulePhrase}”",
        Formatters.RelativeTime(DateTimeOffset.FromUnixTimeMilliseconds(r.ArrivedAt)),
    });
}

/// <summary>
/// Settings ▸ Blocks &amp; filters (MeshCore One 1.7): word or phrase rules that hide incoming messages, which chats they
/// apply to, and the most recent messages they hid.
/// </summary>
public sealed partial class BlocksFiltersViewModel : ViewModelBase
{
    private readonly Debouncer _refresh;

    public BlocksFiltersViewModel()
    {
        LookInOptions =
        [
            new(TextRuleLookIn.Message, TextRules.LookInTitle(TextRuleLookIn.Message)),
            new(TextRuleLookIn.Sender, TextRules.LookInTitle(TextRuleLookIn.Sender)),
            new(TextRuleLookIn.MessageOrSender, TextRules.LookInTitle(TextRuleLookIn.MessageOrSender)),
        ];
        _newLookIn = LookInOptions[0];
        BuildMatchOptions();
        foreach (var kind in Enum.GetValues<TextRuleKind>()) Kinds.Add(new TextRuleKindRow(this, kind));
        _refresh = new Debouncer(TimeSpan.FromMilliseconds(250), Refresh);
        Action changed = () => _refresh.Trigger();
        Core.TextRules.Changed += changed;
        Track(() => Core.TextRules.Changed -= changed);
        OnData(c => { if (c.Kind is DataKind.Channels or DataKind.Contacts or DataKind.Sessions or DataKind.Radio) _refresh.Trigger(); });
        Refresh();
    }

    internal TextRulesPreference Preference => Core.TextRules.Current;

    public ObservableCollection<TextRuleRow> Rules { get; } = new();
    public ObservableCollection<TextRuleKindRow> Kinds { get; } = new();
    public ObservableCollection<RecentlyBlockedRow> Blocked { get; } = new();

    public IReadOnlyList<Choice<TextRuleLookIn>> LookInOptions { get; }
    [ObservableProperty] private IReadOnlyList<Choice<TextRuleMatch>> _matchOptions = [];

    [ObservableProperty] private string _newPhrase = "";
    [ObservableProperty] private Choice<TextRuleLookIn> _newLookIn;
    [ObservableProperty] private Choice<TextRuleMatch>? _newMatch;
    [ObservableProperty] private string _newRuleHint = "";
    [ObservableProperty] private string _blockedTitle = "";

    public bool HasRules => Rules.Count > 0;
    public bool HasBlocked => Blocked.Count > 0;
    public string BlockedFooter => L.F("The latest {0} hidden messages are kept on this PC. They aren't included in backups.", TextRules.RecentlyBlockedLimit);

    partial void OnNewLookInChanged(Choice<TextRuleLookIn> value) => BuildMatchOptions();
    partial void OnNewMatchChanged(Choice<TextRuleMatch>? value) => UpdateHint();
    partial void OnNewPhraseChanged(string value) => AddRuleCommand.NotifyCanExecuteChanged();

    private void BuildMatchOptions()
    {
        var lookIn = NewLookIn?.Value ?? TextRuleLookIn.Message;
        var keep = NewMatch?.Value ?? TextRuleMatch.Contains;
        MatchOptions =
        [
            new(TextRuleMatch.Contains, TextRules.MatchTitle(lookIn, TextRuleMatch.Contains)),
            new(TextRuleMatch.WholeField, TextRules.MatchTitle(lookIn, TextRuleMatch.WholeField)),
        ];
        NewMatch = MatchOptions.First(o => o.Value == keep);
        UpdateHint();
    }

    private void UpdateHint() => NewRuleHint = TextRules.Footer(NewLookIn?.Value ?? TextRuleLookIn.Message, NewMatch?.Value ?? TextRuleMatch.Contains);

    private bool CanAddRule() => !string.IsNullOrWhiteSpace(NewPhrase);

    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private void AddRule()
    {
        Core.TextRules.AddRule(NewPhrase, NewLookIn.Value, NewMatch?.Value ?? TextRuleMatch.Contains);
        NewPhrase = "";
    }

    [RelayCommand]
    private void DeleteRule(TextRuleRow row) => Core.TextRules.RemoveRule(row.Id);

    [RelayCommand]
    private void DeleteBlocked(RecentlyBlockedRow row) => Core.TextRules.DeleteRecentlyBlocked(row.Id);

    [RelayCommand]
    private async Task ClearBlocked()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Clear recently blocked?"), L.T("The hidden messages are removed from this list. Your filter rules stay."), L.T("Clear"), destructive: true)) return;
        Core.TextRules.ClearRecentlyBlocked();
    }

    internal void SetSelection(TextRuleKind kind, TextRuleSelectionMode mode, IEnumerable<string> chats) =>
        Core.TextRules.SetSelection(kind, mode, chats);

    private void Refresh()
    {
        var pref = Preference;
        Rules.Clear();
        foreach (var r in pref.Rules.Where(r => r.IsValid)) Rules.Add(new TextRuleRow(r.Id, r.Phrase, TextRules.Summary(r)));
        OnPropertyChanged(nameof(HasRules));
        foreach (var k in Kinds) k.Load(pref.SelectionFor(k.Kind), Core.TextRules.Conversations(k.Kind));
        Blocked.Clear();
        foreach (var b in Core.TextRules.RecentlyBlocked()) Blocked.Add(new RecentlyBlockedRow(b));
        BlockedTitle = Blocked.Count == 0 ? L.T("Recently blocked") : L.F("Recently blocked ({0})", Blocked.Count);
        OnPropertyChanged(nameof(HasBlocked));
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Utilities;

namespace MC1.Windows.ViewModels;

/// <summary>What the CoreScope page asks of its embedded browser (set by the view on Windows).</summary>
public interface ICoreScopeBrowser
{
    void Navigate(string url);
    void GoBack();
    void GoForward();
    void Reload();
}

/// <summary>
/// The CoreScope tab: the user's state or region CoreScope (MeshCore network analyzer) inside the app. It's kept
/// alive while other pages are shown, so the live feed and the place you were looking at stay put.
/// </summary>
public sealed partial class CoreScopeViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public CoreScopeViewModel(MainWindowViewModel main, string url, string? siteName)
    {
        _main = main;
        _url = url;
        _currentAddress = url;
        _siteName = siteName;
    }

    /// <summary>The linked site (the tab's home page).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HostLabel))] private string _url;
    /// <summary>The page the browser is on now.</summary>
    [ObservableProperty] private string _currentAddress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Heading))] private string? _siteName;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Heading))] private string? _pageTitle;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(BackCommand))] private bool _canGoBack;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ForwardCommand))] private bool _canGoForward;
    /// <summary>The page couldn't be loaded (shown instead of the browser, with Retry).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowBrowser))] private string? _loadError;
    /// <summary>The built-in browser can't run on this PC: the page offers the web browser instead.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowBrowser))] private string? _browserUnavailable;
    /// <summary>The tab is the page on screen.</summary>
    [ObservableProperty] private bool _isShown;

    public ICoreScopeBrowser? Browser { get; set; }

    public bool ShowBrowser => LoadError is null && BrowserUnavailable is null;
    public string HostLabel => CoreScopeLink.DisplayHost(Url);

    /// <summary>"Colorado Mesh · Packets": the site's name, then the page it's on.</summary>
    public string Heading
    {
        get
        {
            var site = CoreScopeLink.TabTitle(SiteName);
            var page = PageTitle?.Trim();
            if (string.IsNullOrEmpty(page)) return site;
            // CoreScope titles its pages "Packets | <site name>".
            var bar = page.LastIndexOf(" | ", StringComparison.Ordinal);
            var first = bar > 0 ? page[..bar].Trim() : page;
            if (first.Length == 0 || string.Equals(first, site, StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, CoreScopeLink.DefaultName, StringComparison.OrdinalIgnoreCase)) return site;
            return $"{site} · {first}";
        }
    }

    /// <summary>The link changed in Settings (or its name became known).</summary>
    public void SetLink(string url, string? siteName)
    {
        SiteName = siteName;
        if (url == Url) return;
        Url = url;
        CurrentAddress = url;
        PageTitle = null;
        LoadError = null;
        if (Browser is { } b) b.Navigate(url);
    }

    /// <summary>The browser is ready: open the linked site.</summary>
    public void OnBrowserReady(ICoreScopeBrowser browser)
    {
        Browser = browser;
        BrowserUnavailable = null;
        browser.Navigate(CurrentAddress);
    }

    public void OnBrowserFailed(string reason)
    {
        Browser = null;
        BrowserUnavailable = reason;
        IsLoading = false;
    }

    public void OnNavigationStarted() => IsLoading = true;

    /// <summary>
    /// A page finished loading. <paramref name="ok"/> is false only when the site couldn't be reached at all (no
    /// connection, unknown address, certificate trouble): the site's own error pages are left to show.
    /// </summary>
    public void OnNavigationCompleted(bool ok, string? reason)
    {
        IsLoading = false;
        if (ok)
        {
            LoadError = null;
            return;
        }
        LoadError = reason is null
            ? L.F("{0} didn't load. Check your internet connection and the link.", HostLabel)
            : L.F("{0} didn't load: {1}. Check your internet connection and the link.", HostLabel, reason);
    }

    public void OnHistoryChanged(bool canGoBack, bool canGoForward)
    {
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
    }

    public void OnAddressChanged(string? address)
    {
        if (!string.IsNullOrEmpty(address) && address != "about:blank") CurrentAddress = address;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))] private void Back() => Browser?.GoBack();
    [RelayCommand(CanExecute = nameof(CanGoForward))] private void Forward() => Browser?.GoForward();

    [RelayCommand]
    private void Reload()
    {
        if (LoadError is not null) { Retry(); return; }
        Browser?.Reload();
    }

    [RelayCommand] private void Home() => Browser?.Navigate(Url);

    [RelayCommand]
    private void Retry()
    {
        LoadError = null;
        Browser?.Navigate(CurrentAddress);
    }

    [RelayCommand] private void OpenInBrowser() => AppHost.Dialogs.OpenUrl(CurrentAddress);

    [RelayCommand]
    private async Task ChangeLink()
    {
        var text = await AppHost.Dialogs.Prompt(L.T("CoreScope link"),
            L.T("Paste the link to your state's or region's CoreScope."), Url, "https://…");
        if (text is null) return;
        if (!CoreScopeSettings.Link(Core.Settings, text, out _, out var error))
        {
            await AppHost.Dialogs.ShowError(L.T("CoreScope link"), error ?? "");
            return;
        }
        _ = Task.Run(() => CoreScopeSettings.RefreshNameAsync(Core.Settings));
    }

    [RelayCommand]
    private async Task Remove()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Remove CoreScope?"),
                L.F("The CoreScope tab ({0}) is removed from the sidebar. You can link it again in Settings ▸ General.", HostLabel),
                L.T("Remove"), destructive: true)) return;
        CoreScopeSettings.Unlink(Core.Settings);
    }

    [RelayCommand] private void OpenSettings() => _main.OpenCoreScopeSettings();
}

/// <summary>Settings ▸ General ▸ CoreScope: paste the link to the state's CoreScope; the tab appears when it's linked.</summary>
public sealed partial class CoreScopeLinkViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private int _check;

    public CoreScopeLinkViewModel(MainWindowViewModel main)
    {
        _main = main;
        _linkText = Core.Settings.Current.CoreScopeUrl ?? "";
        Action changed = () => Ui(Refresh);
        Core.Settings.Changed += changed;
        Track(() => Core.Settings.Changed -= changed);
        Refresh();
    }

    [ObservableProperty] private string _linkText;
    [ObservableProperty] private bool _isLinked;
    [ObservableProperty] private string _linkedTitle = "";
    [ObservableProperty] private string _linkedUrl = "";
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsWarning;
    [ObservableProperty] private bool _isChecking;

    /// <summary>The Link button says what it'll do: link a site, or change the one that's linked.</summary>
    public string LinkButtonText => IsLinked ? L.T("Change") : L.T("Link");

    partial void OnIsLinkedChanged(bool value) => OnPropertyChanged(nameof(LinkButtonText));

    private void Refresh()
    {
        var s = Core.Settings.Current;
        IsLinked = s.CoreScopeUrl is not null;
        LinkedUrl = s.CoreScopeUrl ?? "";
        LinkedTitle = L.F("Linked: {0}", CoreScopeLink.TabTitle(s.CoreScopeName));
        if (!IsLinked) { IsChecking = false; }
    }

    [RelayCommand]
    private async Task Link()
    {
        if (!CoreScopeSettings.Link(Core.Settings, LinkText, out var url, out var error))
        {
            Say(error, warning: true);
            return;
        }
        LinkText = url;
        await CheckAsync(url);
    }

    /// <summary>Asks the site for its name; the tab is there either way.</summary>
    private async Task CheckAsync(string url)
    {
        var mine = ++_check;
        IsChecking = true;
        Say(L.T("Checking the link…"), warning: false);
        var probe = await Task.Run(() => CoreScopeSettings.RefreshNameAsync(Core.Settings));
        if (mine != _check || Core.Settings.Current.CoreScopeUrl != url) return;
        IsChecking = false;
        if (probe.IsCoreScope)
            Say(L.F("Linked to {0}. It's in the sidebar as CoreScope.", CoreScopeLink.TabTitle(probe.SiteName)), warning: false);
        else if (probe.Reached)
            Say(L.T("Linked. This site didn't answer like a CoreScope, so check the link if the CoreScope tab looks wrong."), warning: true);
        else
            Say(L.T("Linked, but the site couldn't be reached just now. The CoreScope tab tries again when you open it."), warning: true);
    }

    [RelayCommand]
    private void Remove()
    {
        _check++;
        CoreScopeSettings.Unlink(Core.Settings);
        LinkText = "";
        Say(L.T("CoreScope removed from the sidebar."), warning: false);
    }

    [RelayCommand] private void Open() => _main.Navigate(Page.CoreScope);

    private void Say(string? text, bool warning)
    {
        Status = text;
        StatusIsWarning = warning;
    }
}

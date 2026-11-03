using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MC1.Core.Utilities;
using MC1.Windows.ViewModels;
#if WINDOWS
using Microsoft.Web.WebView2.Core;
#endif

namespace MC1.Windows.Views;

/// <summary>
/// The CoreScope tab. The site runs in the app's built-in browser (Microsoft Edge WebView2); pages of other sites
/// it links to open in the web browser. Without WebView2 the tab offers the web browser instead.
/// </summary>
public partial class CoreScopeView : UserControl
{
    private CoreScopeViewModel? _vm;
#if WINDOWS
    private Platform.Windows.WebView2Host? _host;
    private Browser? _browser;
#endif

    public CoreScopeView()
    {
        InitializeComponent();
#if WINDOWS
        // CoreScope follows the light/dark preference: keep it in step with the app's theme.
        ActualThemeVariantChanged += (_, _) => _browser?.ApplyTheme();
#endif
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Build();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Build();
    }

    private void Build()
    {
        if (DataContext is not CoreScopeViewModel vm || ReferenceEquals(vm, _vm) || VisualRoot is null) return;
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        vm.PropertyChanged += OnVmChanged;
#if WINDOWS
        BrowserArea.Children.Clear();
        var host = new Platform.Windows.WebView2Host { IsVisible = vm.IsShown };
        var browser = new Browser(vm, this);
        host.Ready += web =>
        {
            browser.Attach(web);
            host.SetBrowserVisible(vm.IsShown);
        };
        host.Failed += reason => vm.OnBrowserFailed(L.F("{0} You can still open CoreScope in your web browser.", reason));
        _host = host;
        _browser = browser;
        BrowserArea.Children.Add(host);
#else
        vm.OnBrowserFailed(L.T("The built-in browser is only available on Windows."));
#endif
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
#if WINDOWS
        if (e.PropertyName == nameof(CoreScopeViewModel.IsShown) && _host is { } host && _vm is { } vm)
        {
            host.IsVisible = vm.IsShown;
            host.SetBrowserVisible(vm.IsShown);
            if (vm.IsShown) host.FocusBrowser();
        }
#endif
    }

#if WINDOWS
    /// <summary>The WebView2 page, as the view-model sees it.</summary>
    private sealed class Browser(CoreScopeViewModel vm, CoreScopeView view) : ICoreScopeBrowser
    {
        private CoreWebView2? _web;
        /// <summary>The site's address as it really is (the link may redirect to another address the first time).</summary>
        private string _site = vm.Url;
        private bool _loaded;

        public void Attach(CoreWebView2 web)
        {
            _web = web;
            ApplyTheme();
            web.NavigationStarting += (_, e) =>
            {
                if (e.IsRedirected && !_loaded && Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                    _site = e.Uri; // the link leads here: this is the site
                else if (!IsSite(e.Uri))
                {
                    // Another site (or mailto: and the like): the web browser handles it, the tab stays where it is.
                    e.Cancel = true;
                    AppHost.Dialogs.OpenUrl(e.Uri);
                    return;
                }
                vm.OnNavigationStarted();
            };
            web.NavigationCompleted += (_, e) =>
            {
                if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    vm.IsLoading = false;
                    return;
                }
                var reason = Reason(e.WebErrorStatus);
                // Only "couldn't reach the site" replaces the page; the site's own error pages stay on screen.
                var unreachable = !e.IsSuccess && reason is not null;
                if (!unreachable) _loaded = true;
                vm.OnNavigationCompleted(!unreachable, reason);
            };
            web.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsSite(e.Uri)) web.Navigate(e.Uri);
                else AppHost.Dialogs.OpenUrl(e.Uri);
            };
            web.HistoryChanged += (_, _) => vm.OnHistoryChanged(web.CanGoBack, web.CanGoForward);
            web.DocumentTitleChanged += (_, _) => vm.PageTitle = web.DocumentTitle;
            web.SourceChanged += (_, _) => vm.OnAddressChanged(web.Source);
            vm.OnBrowserReady(this);
        }

        private bool IsSite(string uri) => CoreScopeLink.IsSameSite(_site, uri) || CoreScopeLink.IsSameSite(vm.Url, uri);

        private static string? Reason(CoreWebView2WebErrorStatus status) => status switch
        {
            CoreWebView2WebErrorStatus.HostNameNotResolved => L.T("the address wasn't found"),
            CoreWebView2WebErrorStatus.Disconnected => L.T("this PC is offline"),
            CoreWebView2WebErrorStatus.Timeout => L.T("the site didn't answer in time"),
            CoreWebView2WebErrorStatus.ServerUnreachable or CoreWebView2WebErrorStatus.CannotConnect
                or CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset
                or CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => L.T("the site can't be reached"),
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired
                or CoreWebView2WebErrorStatus.CertificateRevoked or CoreWebView2WebErrorStatus.CertificateIsInvalid
                or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors => L.T("the site's security certificate isn't valid"),
            _ => null,
        };

        /// <summary>CoreScope follows the light/dark preference: match the app's theme.</summary>
        public void ApplyTheme()
        {
            if (_web is null) return;
            try
            {
                _web.Profile.PreferredColorScheme = view.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
            catch { /* older WebView2 runtime */ }
        }

        public void Navigate(string url)
        {
            if (_web is null) return;
            if (!IsSite(url)) _site = url; // a new link from Settings
            _loaded = false;
            try { _web.Navigate(url); }
            catch (ArgumentException) { vm.OnNavigationCompleted(false, null); }
        }

        public void GoBack() { if (_web?.CanGoBack == true) _web.GoBack(); }
        public void GoForward() { if (_web?.CanGoForward == true) _web.GoForward(); }
        public void Reload() => _web?.Reload();
    }
#endif
}

using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Windows.ViewModels;

// MARK: Bluetooth / WiFi links of the radio (port of the iPhone's Connection page and DeviceConnectionMenu)

/// <summary>One link of the radio: Bluetooth or WiFi, in use or available to switch to.</summary>
public sealed class RadioLinkItem(RadioLinkRow row, bool connected, ConnectionKind? switchingTo)
{
    public RadioLinkRow Row { get; } = row;
    public bool IsLive => Row.IsLive;
    public bool IsBluetooth => Row.Kind == ConnectionKind.Bluetooth;
    public string Title => IsBluetooth ? L.T("Bluetooth") : L.T("WiFi");
    public string IconKey => IsBluetooth ? "Icon.Bluetooth" : "Icon.Wifi";
    public string Subtitle => Row.Address ?? (IsBluetooth ? "" : L.T("No address yet"));
    public bool IsSwitching => switchingTo == Row.Kind;
    /// <summary>"Switch to WiFi" while connected over the other link; "Connect over WiFi" while disconnected.</summary>
    public string ActionText => connected
        ? (IsBluetooth ? L.T("Switch to Bluetooth") : L.T("Switch to WiFi"))
        : (IsBluetooth ? L.T("Connect over Bluetooth") : L.T("Connect over WiFi"));
    public bool ShowAction => !IsLive && !IsSwitching;
    public bool CanAct => Row.CanSwitch && switchingTo is null;
}

/// <summary>
/// The radio's Bluetooth and WiFi links with "Switch to WiFi" / "Switch to Bluetooth" (Radio page and Settings), and
/// while disconnected "Connect over …" through either saved link.
/// </summary>
public sealed partial class RadioLinksViewModel : ViewModelBase
{
    public RadioLinksViewModel()
    {
        var refresh = new Debouncer(TimeSpan.FromMilliseconds(150), Refresh);
        OnStatus(refresh.Trigger);
        OnData(c => { if (c.Kind == DataKind.Radio) refresh.Trigger(); });
        Refresh();
    }

    public ObservableCollection<RadioLinkItem> Rows { get; } = new();
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private string? _hint;

    /// <summary>Reads the radio's WiFi settings once per connection (firmware 1.18) unless they are known already, so
    /// the WiFi row knows whether the radio is on a network.</summary>
    public async Task LoadWifiAsync()
    {
        // Read once the radio is synced (not during the sync), once per connection.
        if (Core.IsReady && Core.Device.SupportsRadioCli && !Core.Device.RadioWifiStateKnown)
        {
            try { await Core.Device.GetRadioWifiStateAsync(); }
            catch (Exception ex) { Core.Log.Debug("Radio", "Couldn't read the radio's WiFi settings: " + ex.Message); }
        }
        Refresh();
    }

    public void Refresh()
    {
        var connected = Core.IsConnected;
        var live = connected ? Core.CurrentTarget : null;
        Rows.Clear();
        Hint = null;
        // The demo radio has no real links.
        if (live is { Kind: ConnectionKind.Simulator } || Core.Radio is null) { HasRows = false; return; }
        var rows = RadioLinkRules.Rows(live, Core.RadioLinksFor(Core.RadioId), live is null ? null : Core.Device.LastRadioWifiState);
        // Worth showing only when there is a choice.
        if (rows.Count > 1 || rows.Any(r => !r.IsLive))
            foreach (var r in rows) Rows.Add(new RadioLinkItem(r, connected, Core.SwitchingTo));
        HasRows = Rows.Count > 0;
        if (connected && Rows.FirstOrDefault(r => !r.IsBluetooth && !r.IsLive && !r.Row.CanSwitch) is not null)
            Hint = L.T("To switch to WiFi, the radio must be on a WiFi network (Radio page ▸ Radio WiFi, then reboot).");
    }

    [RelayCommand]
    private async Task Switch(RadioLinkItem item)
    {
        if (item.Row.Target is not { } target || !item.Row.CanSwitch || Core.SwitchingTo is not null) return;
        var wasConnected = Core.IsConnected;
        var links = Core.RadioLinksFor(Core.RadioId);
        // What to offer if it fails: the link in use, or (offline) the other saved link.
        var fallback = wasConnected ? Core.CurrentTarget : target.Kind == ConnectionKind.Bluetooth ? links?.Wifi : links?.Bluetooth;
        try
        {
            if (wasConnected) await Core.SwitchLinkAsync(target);
            else await Core.ConnectAsync(target);
            AppHost.Main?.ShowToast(L.T("Connected"), target.Describe());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var title = target.Kind == ConnectionKind.Bluetooth
                ? (wasConnected ? L.T("Couldn't switch to Bluetooth") : L.T("Couldn't connect over Bluetooth"))
                : (wasConnected ? L.T("Couldn't switch to WiFi") : L.T("Couldn't connect over WiFi"));
            var message = LinkError(target, ex);
            if (fallback is not null && !RadioLinkRules.SameEndpoint(fallback, target))
            {
                if (await AppHost.Dialogs.Confirm(title, message + "\n\n" + L.F("Connect again over {0}?", fallback.Describe()), L.T("Reconnect")))
                    await Guard(() => Core.ConnectAsync(fallback), L.T("Connection problem"));
            }
            else await AppHost.Dialogs.ShowError(title, message);
        }
        finally { Refresh(); }
    }

    internal static string LinkError(ConnectionTarget t, Exception ex) => t.Kind == ConnectionKind.Tcp && ex is System.Net.Sockets.SocketException
        ? L.F("Couldn't reach {0}:{1}. Check the radio is on the same network and runs companion WiFi firmware.", t.Address, t.Port)
        : ex.Message;
}

// MARK: Radio page sections for firmware 1.18 (WiFi, remote CLI grants, power off) and the links

/// <summary>
/// The Radio page's firmware 1.18 sections: the radio's links, its WiFi settings (port of the iPhone's
/// <c>RadioWiFiViewModel</c>), who may run CLI commands on it (Remote CLI), and Power off.
/// </summary>
public sealed partial class RadioDeviceSectionsViewModel : ViewModelBase
{
    private readonly Debouncer _reload;
    private MeshCoreSession? _wifiReadFor;
    private bool _loading;
    private bool _wifiReading;

    public RadioDeviceSectionsViewModel()
    {
        _reload = new Debouncer(TimeSpan.FromMilliseconds(300), Reload);
        OnStatus(_reload.Trigger);
        OnData(c => { if (c.Kind is DataKind.Radio or DataKind.Contacts) _reload.Trigger(); });
        Reload();
    }

    public RadioLinksViewModel Links { get; } = new();

    /// <summary>A firmware 1.18 radio is connected.</summary>
    [ObservableProperty] private bool _supportsRadioCli;
    [ObservableProperty] private bool _isReady;

    // WiFi
    [ObservableProperty] private bool _showWifi;
    [ObservableProperty] private bool _wifiEnabled;
    [ObservableProperty] private string _networkName = "";
    [ObservableProperty] private bool _hasNetwork;
    [ObservableProperty] private string _wifiStatus = "";
    [ObservableProperty] private string? _ipAddress;
    [ObservableProperty] private bool _rebootPending;
    [ObservableProperty] private bool _wifiBusy;
    [ObservableProperty] private string? _wifiError;
    [ObservableProperty] private bool _canRunWifi;
    [ObservableProperty] private bool _canClearWifi;

    // Remote CLI and power
    [ObservableProperty] private string _remoteCliSummary = "";
    [ObservableProperty] private bool _poweringOff;
    [ObservableProperty] private bool _canPowerOff;

    /// <summary>The Radio page was opened: show what's known now and read the WiFi settings for this connection.</summary>
    public void OnShown()
    {
        Reload();
        _ = LoadWifiAsync(force: true);
    }

    private void Reload()
    {
        SupportsRadioCli = Core.Device.SupportsRadioCli;
        IsReady = Core.IsReady;
        CanPowerOff = IsReady && !PoweringOff;
        if (!SupportsRadioCli)
        {
            ShowWifi = false;
            _wifiReadFor = null;
        }
        else if (IsReady && !ReferenceEquals(_wifiReadFor, Core.Session)) _ = LoadWifiAsync();
        var allowed = SupportsRadioCli ? Core.Device.RemoteCliCandidates().Count(DeviceService.IsRemoteCliAllowed) : 0;
        RemoteCliSummary = allowed == 0 ? L.T("No contacts allowed") : L.Plural(allowed, "{0} contact allowed", "{0} contacts allowed");
        UpdateWifiCommands();
        Links.Refresh();
    }

    private void UpdateWifiCommands()
    {
        CanRunWifi = IsReady && ShowWifi && !WifiBusy && Core.SwitchingTo is null;
        CanClearWifi = CanRunWifi && HasNetwork;
    }

    partial void OnWifiBusyChanged(bool value) => UpdateWifiCommands();

    /// <summary>Reads wifi.enabled / ssid / status / ip once per connection (or again when <paramref name="force"/>).</summary>
    private async Task LoadWifiAsync(bool force = false)
    {
        var session = Core.Session;
        if (!Core.IsReady || !Core.Device.SupportsRadioCli || session is null || _wifiReading) return;
        if (!force && ReferenceEquals(_wifiReadFor, session)) return;
        _wifiReading = true;
        _wifiReadFor = session;
        try
        {
            var state = await Core.Device.GetRadioWifiStateAsync();
            if (!ReferenceEquals(session, Core.Session)) return;
            ApplyWifi(state);
            WifiError = null;
        }
        catch (Exception ex)
        {
            Core.Log.Debug("Radio", "Couldn't read the radio's WiFi settings: " + ex.Message);
            // A board that had WiFi keeps the section; the address can't be trusted any more.
            if (ShowWifi) { WifiError = ex.Message; IpAddress = null; }
        }
        finally
        {
            _wifiReading = false;
            UpdateWifiCommands();
        }
    }

    private void ApplyWifi(RadioWifiState? state)
    {
        _loading = true;
        try
        {
            ShowWifi = state is not null;
            WifiEnabled = state?.Enabled ?? false;
            HasNetwork = state?.HasNetwork ?? false;
            NetworkName = HasNetwork ? state!.Ssid : L.T("None");
            WifiStatus = state?.IsConnected == true ? L.T("Connected") : L.T("Not connected");
            IpAddress = state?.IpAddress;
            RebootPending = state?.RebootPending ?? false;
        }
        finally { _loading = false; }
        Links.Refresh();
        UpdateWifiCommands();
    }

    private bool OverWifi => Core.CurrentTarget?.Kind == ConnectionKind.Tcp;

    private static string WifiTransportWarning => L.T("After the radio reboots, it may not be reachable at this address. If that happens, connect over Bluetooth.");

    /// <summary>Runs a WiFi change, then reads the settings back (also after a failure).</summary>
    private async Task RunWifi(Func<Task> change)
    {
        WifiBusy = true;
        WifiError = null;
        string? error = null;
        try { await change(); }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            WifiBusy = false;
            await LoadWifiAsync(force: true);
            if (error is not null) WifiError = error;
        }
    }

    partial void OnWifiEnabledChanged(bool value)
    {
        if (_loading) return;
        _ = SetWifiEnabledAsync(value);
    }

    private async Task SetWifiEnabledAsync(bool enabled)
    {
        if (!enabled && OverWifi && !await AppHost.Dialogs.Confirm(L.T("Turn off WiFi?"), WifiTransportWarning, L.T("Turn off")))
        {
            _loading = true;
            WifiEnabled = true;
            _loading = false;
            return;
        }
        await RunWifi(() => Core.Device.SetRadioWifiEnabledAsync(enabled));
    }

    [RelayCommand]
    private async Task EditNetwork()
    {
        if (!CanRunWifi) return;
        var vm = new RadioWifiEditViewModel(HasNetwork ? NetworkName : "", OverWifi);
        await AppHost.Dialogs.ShowDialog(vm, L.T("Network"), 460, 420);
        if (vm.Saved) await LoadWifiAsync(force: true);
    }

    [RelayCommand]
    private async Task ClearWifi()
    {
        if (!CanClearWifi) return;
        var message = OverWifi
            ? L.T("The radio forgets the saved network name and password after it reboots. It may not be reachable at this address. If that happens, connect over Bluetooth. If its firmware has a built-in network, it joins that network instead.")
            : L.T("The radio forgets the saved network name and password after it reboots. If its firmware has a built-in network, it joins that network instead.");
        if (!await AppHost.Dialogs.Confirm(L.T("Clear WiFi settings?"), message, L.T("Clear"), destructive: true)) return;
        await RunWifi(() => Core.Device.ClearRadioWifiAsync());
    }

    /// <summary>What a reboot does to the WiFi (port of the iPhone's <c>rebootMessage</c>, incl. WiFi turned off).</summary>
    public string RebootMessage => !WifiEnabled
        ? L.T("The radio will restart with WiFi turned off. It will no longer accept WiFi connections.")
        : HasNetwork
            ? L.F("The radio will restart and then join {0}. This PC disconnects until the radio is back.", NetworkName)
            : L.T("The radio will restart. This PC disconnects until the radio is back.");

    [RelayCommand]
    private async Task RebootToApply()
    {
        if (!CanRunWifi || !RebootPending) return;
        if (!await AppHost.Dialogs.Confirm(L.T("Reboot the radio?"), RebootMessage, L.T("Reboot"))) return;
        try { await Core.Device.RebootAsync(); }
        // The radio reboots without a reply and drops the link.
        catch (MeshCoreException ex) when (ex.Kind is MeshCoreErrorKind.NotConnected or MeshCoreErrorKind.Timeout) { }
        catch (Exception ex) { WifiError = ex.Message; }
    }

    // MARK: Remote CLI

    [RelayCommand]
    private async Task ManageRemoteCli()
    {
        if (!IsReady) return;
        await AppHost.Dialogs.ShowDialog(new RemoteCliContactsViewModel(), L.T("Remote CLI"), 480, 620);
        Reload();
    }

    // MARK: Power off

    [RelayCommand]
    private async Task PowerOff()
    {
        if (!CanPowerOff) return;
        if (!await AppHost.Dialogs.Confirm(L.T("Power off the radio?"),
                L.T("The radio will turn off and disconnect. Turn it on with its power button, then connect again."), L.T("Power off"), destructive: true)) return;
        PoweringOff = true;
        CanPowerOff = false;
        try
        {
            await Core.Device.PowerOffAsync();
            AppHost.Main?.ShowToast(L.T("Radio powered off"), L.T("Turn it on with its power button, then connect again."));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await AppHost.Dialogs.ShowError(L.T("Couldn't power off the radio"), ex.Message); }
        finally
        {
            PoweringOff = false;
            Reload();
        }
    }
}

// MARK: Radio WiFi network editor (RadioWiFiEditSheet.swift)

public sealed partial class RadioWifiEditViewModel : ViewModelBase
{
    private readonly bool _overWifi;
    private string _loadedSsid;
    private string _loadedPassword = "";
    private bool _passwordLoaded;

    public RadioWifiEditViewModel(string ssid, bool overWifi)
    {
        _overWifi = overWifi;
        _loadedSsid = ssid;
        _ssid = ssid;
        _ = LoadPasswordAsync();
    }

    public Action? Close { get; set; }
    /// <summary>The network was written to the radio.</summary>
    public bool Saved { get; private set; }

    [ObservableProperty] private string _ssid;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private bool _canSave;

    private async Task LoadPasswordAsync()
    {
        try
        {
            _loadedPassword = await Core.Device.GetRadioWifiPasswordAsync();
            Password = _loadedPassword;
            _passwordLoaded = true;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            Busy = false;
            Validate();
        }
    }

    partial void OnSsidChanged(string value) => Validate();
    partial void OnPasswordChanged(string value) => Validate();
    partial void OnBusyChanged(bool value) => Validate();

    private void Validate()
    {
        var nameBytes = Encoding.UTF8.GetByteCount(Ssid);
        var pwBytes = Encoding.UTF8.GetByteCount(Password);
        Footer = nameBytes < RadioWifi.MinSsidBytes ? L.T("Enter a network name.")
            : nameBytes > RadioWifi.MaxSsidBytes ? L.T("A network name can be up to 32 bytes.")
            : pwBytes > RadioWifi.MaxPasswordBytes ? L.T("A password can be up to 63 bytes.")
            : L.T("The radio uses this network after it reboots. A network name can be up to 32 bytes, and a password up to 63 bytes. Leave the password blank to store no password.");
        CanSave = !Busy && _passwordLoaded && Core.IsReady && (Ssid != _loadedSsid || Password != _loadedPassword)
                  && nameBytes is >= RadioWifi.MinSsidBytes and <= RadioWifi.MaxSsidBytes && pwBytes <= RadioWifi.MaxPasswordBytes;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!CanSave) return;
        // Captured now: later edits don't change what is sent.
        var ssid = Ssid;
        var password = Password;
        var passwordChanged = password != _loadedPassword;
        if (_overWifi && !await AppHost.Dialogs.Confirm(L.T("Save this network?"),
                L.T("After the radio reboots, it may not be reachable at this address. If that happens, connect over Bluetooth."), L.T("Save"))) return;
        Busy = true;
        Error = null;
        try
        {
            await Core.Device.SetRadioWifiCredentialsAsync(ssid, passwordChanged ? password : null);
            _loadedSsid = ssid;
            if (passwordChanged) _loadedPassword = password;
            Saved = true;
            Close?.Invoke();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    [RelayCommand] private void Cancel() => Close?.Invoke();
}

// MARK: Remote CLI grants (RemoteCLIContactsPickerView.swift)

public sealed partial class RemoteCliContactRow(ContactRecord contact, bool allowed) : ObservableObject
{
    public ContactRecord Contact { get; } = contact;
    public string Name => Contact.DisplayName;
    public bool IsFavorite => Contact.IsFavorite;
    public bool InitiallyAllowed { get; set; } = allowed;
    [ObservableProperty] private bool _isAllowed = allowed;
}

/// <summary>Choose the contacts that may run CLI commands on this radio (sets their Remote CLI flag on the radio).</summary>
public sealed partial class RemoteCliContactsViewModel : ViewModelBase
{
    private readonly List<RemoteCliContactRow> _all;

    public RemoteCliContactsViewModel()
    {
        _all = Core.Device.RemoteCliCandidates().Select(c => new RemoteCliContactRow(c, DeviceService.IsRemoteCliAllowed(c))).ToList();
        foreach (var r in _all) r.PropertyChanged += (_, _) => UpdateCanApply();
        Filter();
    }

    public Action? Close { get; set; }
    public ObservableCollection<RemoteCliContactRow> Rows { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _favoritesOnly;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _canApply;

    partial void OnSearchChanged(string value) => Filter();
    partial void OnFavoritesOnlyChanged(bool value) => Filter();
    partial void OnBusyChanged(bool value) => UpdateCanApply();

    private void Filter()
    {
        Rows.Clear();
        var q = Search.Trim();
        foreach (var r in _all.Where(r => (!FavoritesOnly || r.IsFavorite) && (q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))))
            Rows.Add(r);
        IsEmpty = Rows.Count == 0;
        (EmptyTitle, EmptyText) = q.Length > 0 ? (L.T("No results"), L.F("No contacts match \"{0}\"", q))
            : FavoritesOnly ? (L.T("No favorites"), L.T("No favorite contacts to show"))
            : (L.T("No contacts"), L.T("Add contacts to allow remote CLI"));
    }

    private void UpdateCanApply() => CanApply = !Busy && Core.IsReady && _all.Any(r => r.IsAllowed != r.InitiallyAllowed);

    [RelayCommand]
    private async Task Apply()
    {
        if (!CanApply) return;
        Busy = true;
        Error = null;
        try
        {
            // Grants first, then revocations; each one is kept as soon as the radio confirms it.
            foreach (var r in _all.Where(r => r.IsAllowed && !r.InitiallyAllowed).Concat(_all.Where(r => !r.IsAllowed && r.InitiallyAllowed)).ToList())
            {
                await Core.Device.SetRemoteCliAllowedAsync(r.Contact, r.IsAllowed);
                r.InitiallyAllowed = r.IsAllowed;
            }
            Close?.Invoke();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    [RelayCommand] private void Cancel() => Close?.Invoke();
}

// MARK: Remote commands for a contact's radio (RemoteCLISheet.swift / NodeCLIViewModel with the radio target)

/// <summary>A CLI console for a chat contact's radio. Closing it cancels a command that's still waiting.</summary>
public sealed partial class RemoteCliConsoleViewModel : ViewModelBase
{
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private List<string> _matches = [];
    private CancellationTokenSource? _cts;
    private int _epoch;

    public RemoteCliConsoleViewModel(ContactRecord contact)
    {
        Contact = contact;
        _prompt = $"{contact.DisplayName}>";
        Append(L.F("Commands go to {0}'s radio. Without Remote CLI access you get no reply, and they receive each command as a message.", contact.DisplayName), CliLineKind.Info);
        Append(L.T("Type 'help' for available commands."), CliLineKind.Info);
    }

    public ContactRecord Contact { get; }
    public Action? Close { get; set; }
    public ObservableCollection<CliLine> Lines { get; } = new();
    public ObservableCollection<string> Suggestions { get; } = new();
    public event Action? LinesAppended;

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _prompt;
    [ObservableProperty] private bool _isBusy;

    partial void OnInputChanged(string value)
    {
        Suggestions.Clear();
        _matches = [];
        var q = value.TrimStart();
        if (q.Length == 0) return;
        _matches = RadioCliCompletion.ContactRadioLines()
            .Where(s => s.StartsWith(q, StringComparison.OrdinalIgnoreCase) && !s.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var s in _matches.Take(8)) Suggestions.Add(s);
    }

    /// <summary>Tab: completes to the longest common prefix of the matches (a space only while an argument is needed).</summary>
    public void Complete()
    {
        if (_matches.Count == 0) return;
        var prefix = _matches[0];
        foreach (var s in _matches.Skip(1))
        {
            var i = 0;
            while (i < prefix.Length && i < s.Length && char.ToLowerInvariant(prefix[i]) == char.ToLowerInvariant(s[i])) i++;
            prefix = prefix[..i];
        }
        if (prefix.Length > Input.Length) Input = prefix;
    }

    public void HistoryPrevious()
    {
        if (_history.Count == 0) return;
        _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Math.Max(0, _historyIndex - 1);
        Input = _history[_historyIndex];
    }

    public void HistoryNext()
    {
        if (_historyIndex < 0) return;
        _historyIndex++;
        if (_historyIndex >= _history.Count) { _historyIndex = -1; Input = ""; }
        else Input = _history[_historyIndex];
    }

    private void Append(string text, CliLineKind kind)
    {
        foreach (var line in text.Replace("\r", "").Split('\n')) Lines.Add(new CliLine(line, kind));
        while (Lines.Count > 1000) Lines.RemoveAt(0);
        LinesAppended?.Invoke();
    }

    [RelayCommand] private void UseSuggestion(string s) => Input = s;

    [RelayCommand]
    private async Task Submit()
    {
        if (IsBusy) return;
        var original = Input;
        var normalized = original.Trim();
        if (normalized.Length == 0) return;
        Input = "";
        _historyIndex = -1;
        var parts = normalized.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        var appCommand = verb == "help" || (verb == "clear" && parts.Length == 1);
        // The radio gets the line as typed; help and clear are the console's own.
        var transmitted = appCommand ? normalized : original.TrimEnd('\r', '\n');
        if (_history.Count == 0 || _history[^1] != transmitted) _history.Add(transmitted);
        Append($"{Prompt} {transmitted}", CliLineKind.Input);
        if (verb == "help" && appCommand)
        {
            Append(L.T("Commands for this radio:") + "\n" + """
                  ver | board | get <key> | set <key> <value>
                  reboot | poweroff | help | clear
                """ + "\n" + L.T("Any other input is sent to the radio."), CliLineKind.Info);
            return;
        }
        if (appCommand) { Lines.Clear(); return; }
        if (!Core.IsConnected) { Append(L.T("Not connected to a radio."), CliLineKind.Error); return; }
        await SendAsync(transmitted);
    }

    private async Task SendAsync(string command)
    {
        var epoch = ++_epoch;
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        var noReply = RemoteCliNoReply.ForContactRadio(command);
        try
        {
            var reply = await Core.Remote.SendRadioCliAsync(Contact, command, noReply is null ? null : RemoteNodeService.RadioCliNoReplyTimeout, cts.Token);
            if (epoch != _epoch) return;
            if (reply.Length == 0 && noReply is { } k) Append(SentLine(k), CliLineKind.Info);
            else Append(reply.Length == 0 ? L.T("(empty reply)") : reply, CliLineKind.Output);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException)
        {
            if (epoch != _epoch) return;
            if (noReply is { } k) Append(SentLine(k), CliLineKind.Info);
            else Append(L.F("No reply from {0}. They may not have allowed you Remote CLI access, or their node was unreachable.", Contact.DisplayName), CliLineKind.Error);
        }
        catch (Exception ex)
        {
            if (epoch == _epoch) Append(L.F("Error: {0}", ex.Message), CliLineKind.Error);
        }
        finally
        {
            if (epoch == _epoch) { IsBusy = false; _cts = null; }
            cts.Dispose();
        }
    }

    private static string SentLine(RemoteCliNoReplyKind k) => k == RemoteCliNoReplyKind.Reboot ? L.T("Reboot command sent.") : L.T("Power off command sent.");

    /// <summary>Stops waiting for the command in flight (its contact is free for the next one; a late reply is ignored).</summary>
    [RelayCommand]
    private void CancelWaiting()
    {
        if (!IsBusy) return;
        CancelCurrent();
        Append(L.T("Command cancelled"), CliLineKind.Error);
    }

    /// <summary>Called when the console closes, so its command doesn't keep the contact busy until it times out.</summary>
    public void CancelCurrent()
    {
        _epoch++;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts = null;
        IsBusy = false;
    }

    [RelayCommand] private void ClearOutput() => Lines.Clear();
}

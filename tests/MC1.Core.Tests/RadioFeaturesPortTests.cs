using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;
using Xunit;

namespace MC1.Core.Tests;

/// <summary>
/// WS2: firmware 1.18 radio features at app level, against the simulated radio — radio CLI passthrough rules, remote
/// CLI grants and remote commands to contact radios, owned power off, timezone sync, radio WiFi settings and the
/// radio's Bluetooth/WiFi links.
/// </summary>
public class RadioFeaturesPortTests
{
    private static readonly ConnectionTarget Ble = new(ConnectionKind.Bluetooth, "AABBCCDDEEFF", "Demo BLE");

    // MARK: Radio CLI passthrough (RadioCLIPassthrough.swift)

    [Theory]
    [InlineData("get af", true)]
    [InlineData("GET AF", true)]
    [InlineData("set radio.rxps on", true)]
    [InlineData("get radio.rxps", true)]
    [InlineData("get wifi.ip", true)]
    [InlineData("get wifi.status", true)]
    [InlineData("set wifi.status 1", false)]
    [InlineData("set wifi.clear", true)]
    [InlineData("get wifi.clear", false)]
    [InlineData("set wifi.pwd ", true)]
    [InlineData("set pin 123456", true)]
    [InlineData("get tz.offset", true)]
    [InlineData("get _af", false)]
    [InlineData("set _af 2", false)]
    [InlineData("get name", false)]
    [InlineData("set name Field", false)]
    [InlineData("get", false)]
    [InlineData("set", false)]
    [InlineData("ver", false)]
    [InlineData("clock sync", false)]
    [InlineData("advert.zerohop", false)]
    [InlineData("reboot", false)]
    [InlineData("poweroff", false)]
    [InlineData("shutdown", false)]
    [InlineData("help", false)]
    [InlineData("session 2", false)]
    [InlineData("stats", false)]
    [InlineData("neighbors", true)]
    [InlineData("frobnicate now", true)]
    [InlineData("   ", false)]
    public void PassthroughFollowsTheIphoneRules(string line, bool sent) => Assert.Equal(sent, RadioCliPassthrough.ShouldSend(line));

    [Theory]
    [InlineData("reboot", RemoteCliNoReplyKind.Reboot)]
    [InlineData("reboot now", RemoteCliNoReplyKind.Reboot)]
    [InlineData("Reboot", RemoteCliNoReplyKind.Reboot)]
    [InlineData("clkreboot", RemoteCliNoReplyKind.Reboot)]
    [InlineData("poweroff", RemoteCliNoReplyKind.PowerOff)]
    [InlineData("shutdown", RemoteCliNoReplyKind.PowerOff)]
    [InlineData(" SHUTDOWN ", RemoteCliNoReplyKind.PowerOff)]
    [InlineData("rebooted", null)]
    [InlineData("poweroffx", null)]
    [InlineData("get reboot", null)]
    [InlineData("", null)]
    public void NodeNoReplyCommands(string command, RemoteCliNoReplyKind? kind) => Assert.Equal(kind, RemoteCliNoReply.ForNode(command));

    [Fact]
    public void ContactRadioNoReplyMatchesTheWholeCommand()
    {
        Assert.Equal(RemoteCliNoReplyKind.Reboot, RemoteCliNoReply.ForContactRadio("reboot"));
        Assert.Equal(RemoteCliNoReplyKind.PowerOff, RemoteCliNoReply.ForContactRadio("shutdown"));
        Assert.Null(RemoteCliNoReply.ForContactRadio("Reboot"));
        Assert.Null(RemoteCliNoReply.ForContactRadio("reboot now"));
    }

    [Fact]
    public void CompletionAddsASeparatorOnlyWhileAnArgumentIsNeeded()
    {
        Assert.Equal("get ", RadioCliCompletion.Completed("get"));
        Assert.Equal("set ", RadioCliCompletion.Completed("set"));
        Assert.Equal("set wifi.ssid ", RadioCliCompletion.Completed("set wifi.ssid"));
        Assert.Equal("set wifi.clear", RadioCliCompletion.Completed("set wifi.clear"));
        Assert.Equal("get wifi.ip", RadioCliCompletion.Completed("get wifi.ip"));
        Assert.Equal("set wifi.pwd my pass", RadioCliCompletion.Completed("set wifi.pwd my pass"));
        Assert.Equal("ver", RadioCliCompletion.Completed("ver"));

        var local = RadioCliCompletion.LocalRadioLines();
        Assert.Contains("set wifi.clear", local);
        Assert.Contains("set af ", local);
        Assert.Contains("get wifi.status", local);
        Assert.Contains("get radio.rxps", local);
        Assert.Contains("poweroff", local);
        Assert.DoesNotContain("set wifi.status ", local);
        Assert.DoesNotContain("get pin", local);

        var contact = RadioCliCompletion.ContactRadioLines();
        Assert.Contains("get freq", contact);
        Assert.DoesNotContain("set freq ", contact);
        Assert.Contains("set name ", contact);
        Assert.Contains("get wifi.ip", contact);
        Assert.Contains("shutdown", contact);
        Assert.DoesNotContain("advert", contact);
    }

    // MARK: Auto-add detection (ccf40023)

    [Theory]
    [InlineData(9, "PowerSaving18", true, false)]
    [InlineData(10, "PowerSaving18", true, true)]
    [InlineData(8, "v1.12.0", true, false)]
    [InlineData(8, "v1.11.0", false, false)]
    [InlineData(8, "1.14.1-custom", true, true)]
    [InlineData(7, "", false, false)]
    public void AutoAddDetectionUsesTheFirmwareCode(byte code, string version, bool config, bool maxHops)
    {
        var caps = new DeviceCapabilities(code, 100, 8, 0, "", "Board", version);
        Assert.Equal(config, RadioFeatures.SupportsAutoAddConfig(caps));
        Assert.Equal(maxHops, RadioFeatures.SupportsAutoAddMaxHops(caps));
    }

    // MARK: Local radio CLI

    [Fact]
    public async Task RadioCliRunsOnTheConnectedRadio()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        Assert.True(app.Device.SupportsRadioCli);
        Assert.Equal("> 1.0", await app.Device.RunRadioCliAsync("get af"));
        Assert.Equal("OK", await app.Device.RunRadioCliAsync("set af 2.5"));
        Assert.Equal("> 2.5", await app.Device.RunRadioCliAsync("get af"));
        // The submitted spacing reaches the radio: a trailing space is an empty password.
        Assert.StartsWith("OK", await app.Device.RunRadioCliAsync("set wifi.pwd "));
        Assert.Equal("", await app.Device.GetRadioWifiPasswordAsync());
        Assert.StartsWith("OK", await app.Device.RunRadioCliAsync("set wifi.pwd  two spaces "));
        Assert.Equal(" two spaces ", await app.Device.GetRadioWifiPasswordAsync());
    }

    [Fact]
    public async Task OlderFirmwareHasNoRadioCli()
    {
        await using var f = new AppFixture();
        f.Radio.FirmwareCode = 13;
        await f.ConnectAsync();
        Assert.False(f.App.Device.SupportsRadioCli);
        var ex = await Assert.ThrowsAsync<MeshCoreException>(() => f.App.Device.PowerOffAsync());
        Assert.Equal(MeshCoreErrorKind.Unsupported, ex.Kind);
        Assert.Equal(LinkStatus.Ready, f.App.Status);
    }

    // MARK: Remote CLI grants

    [Fact]
    public async Task RemoteCliGrantRoundTripsThroughTheRadio()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var candidates = app.Device.RemoteCliCandidates();
        Assert.Contains(candidates, c => c.Name == "Alice");
        Assert.Contains(candidates, c => c.Name == "Bob");
        Assert.DoesNotContain(candidates, c => c.Name is "Hilltop Repeater" or "Town Square" or "Weather Station");

        var bob = candidates.First(c => c.Name == "Bob");
        Assert.False(DeviceService.IsRemoteCliAllowed(bob));
        await app.Contacts.SetFavoriteAsync(bob, true);
        bob = app.Contacts.Get(bob.Id)!;

        var saved = await app.Device.SetRemoteCliAllowedAsync(bob, true);
        Assert.True(DeviceService.IsRemoteCliAllowed(saved));
        Assert.True(f.Radio.ContactFlagsForTest("Bob").HasFlag(ContactFlags.RemoteCliAllowed));
        Assert.True(f.Radio.ContactFlagsForTest("Bob").HasFlag(ContactFlags.Favorite), "other flags are kept");
        Assert.True(DeviceService.IsRemoteCliAllowed(app.Contacts.Get(bob.Id)!));

        await app.Device.SetRemoteCliAllowedAsync(bob, false);
        Assert.False(f.Radio.ContactFlagsForTest("Bob").HasFlag(ContactFlags.RemoteCliAllowed));
        Assert.False(DeviceService.IsRemoteCliAllowed(app.Contacts.Get(bob.Id)!));
        Assert.True(app.Contacts.Get(bob.Id)!.IsFavorite);

        app.Contacts.SetBlocked(app.Contacts.Get(bob.Id)!, true);
        Assert.DoesNotContain(app.Device.RemoteCliCandidates(), c => c.Name == "Bob");
    }

    [Fact]
    public async Task BlockingAContactRemovesItsRemoteCli()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = app.Contacts.GetAll().First(c => c.Name == "Alice");
        await app.Device.SetRemoteCliAllowedAsync(alice, true);
        Assert.True(f.Radio.ContactFlagsForTest("Alice").HasFlag(ContactFlags.RemoteCliAllowed));
        app.Contacts.SetBlocked(app.Contacts.Get(alice.Id)!, true);
        Assert.True(await AppFixture.Eventually(() => !f.Radio.ContactFlagsForTest("Alice").HasFlag(ContactFlags.RemoteCliAllowed)));
        Assert.True(await AppFixture.Eventually(() => !DeviceService.IsRemoteCliAllowed(app.Contacts.Get(alice.Id)!)));
        Assert.True(app.Contacts.Get(alice.Id)!.IsBlocked);
    }

    [Fact]
    public async Task BlockedContactsLoseRemoteCliWhenTheRadioSyncs()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var bob = app.Contacts.GetAll().First(c => c.Name == "Bob");
        await app.Device.SetRemoteCliAllowedAsync(bob, true);
        await app.DisconnectAsync();
        // Blocked while disconnected (or restored blocked from a backup): the radio still grants it.
        app.Contacts.SetBlocked(app.Contacts.Get(bob.Id)!, true);
        Assert.True(f.Radio.ContactFlagsForTest("Bob").HasFlag(ContactFlags.RemoteCliAllowed));
        await f.ConnectAsync();
        Assert.False(f.Radio.ContactFlagsForTest("Bob").HasFlag(ContactFlags.RemoteCliAllowed));
        Assert.True(app.Contacts.Get(bob.Id)!.IsBlocked);
    }

    [Fact]
    public async Task UnconfirmedGrantIsNotSaved()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var alice = f.App.Contacts.GetAll().First(c => c.Name == "Alice");
        f.Radio.IgnoreContactFlagChangesForTest = true;
        await Assert.ThrowsAsync<MessageServiceException>(() => f.App.Device.SetRemoteCliAllowedAsync(alice, true));
        Assert.False(DeviceService.IsRemoteCliAllowed(f.App.Contacts.Get(alice.Id)!));
    }

    // MARK: Remote commands to contact radios

    [Fact]
    public async Task RemoteCommandToAliceIsAnsweredAndToBobNot()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = app.Contacts.GetAll().First(c => c.Name == "Alice");
        var bob = app.Contacts.GetAll().First(c => c.Name == "Bob");

        Assert.Equal("> Alice", await app.Remote.SendRadioCliAsync(alice, "get name"));
        // Leading spaces are dropped before the wire prefix.
        Assert.Equal("> Alice", await app.Remote.SendRadioCliAsync(alice, "   get name"));
        Assert.StartsWith("v1.18.0", await app.Remote.SendRadioCliAsync(alice, "ver"));
        Assert.Equal("??: nosuch", await app.Remote.SendRadioCliAsync(alice, "get nosuch"));

        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<TimeoutException>(() => app.Remote.SendRadioCliAsync(bob, "get name", TimeSpan.FromSeconds(1)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(16));
        // The reply never turned into a chat message.
        Assert.DoesNotContain(app.Db.GetDirectMessages(alice.Id), m => m.Text.Contains("Alice") && m.Text.StartsWith(">"));
    }

    [Fact]
    public async Task CancellingARemoteCommandFreesTheContact()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var alice = app.Contacts.GetAll().First(c => c.Name == "Alice");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        // Alice's radio gets no reply for a reboot; cancelling stops the wait.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Remote.SendRadioCliAsync(alice, "reboot", ct: cts.Token));
        Assert.Equal("> Alice", await app.Remote.SendRadioCliAsync(alice, "get name").WaitAsync(TimeSpan.FromSeconds(10)));

        // A command cancelled before it could be sent is never sent.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Remote.SendRadioCliAsync(alice, "get name", ct: cancelled.Token));
    }

    [Fact]
    public async Task IncomingCliCommandIsStoredAsAMessage()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var bob = app.Contacts.GetAll().First(c => c.Name == "Bob");
        f.Radio.ReceiveDirectMessageForTest("Bob", "get wifi.ssid", TextType.CliCommand);
        Assert.True(await AppFixture.Eventually(() => app.Db.GetDirectMessages(bob.Id).Any(m => m.Text == "get wifi.ssid")));
    }

    [Fact]
    public async Task RepeaterPowerOffNeedsNoReply()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var repeater = f.App.Contacts.GetAll().First(c => c.ContactType == ContactType.Repeater);
        Assert.Equal("Power off command sent.", await f.App.Remote.SendCliAsync(repeater, "poweroff"));
        Assert.Equal("Reboot command sent.", await f.App.Remote.SendCliAsync(repeater, "clkreboot"));
    }

    // MARK: Power off

    [Fact]
    public async Task PowerOffDisconnectsAndStopsAutoReconnect()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        Assert.True(app.Settings.Current.AutoReconnect);
        var statuses = new List<LinkStatus>();
        app.StatusChanged += () => { lock (statuses) statuses.Add(app.Status); };

        await app.Device.PowerOffAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(f.Radio.PoweredOff);
        Assert.Equal(LinkStatus.Disconnected, app.Status);
        Assert.Null(app.Session);

        // The first reconnect attempt would start within 2 s.
        await Task.Delay(4000);
        Assert.Equal(LinkStatus.Disconnected, app.Status);
        lock (statuses) Assert.DoesNotContain(LinkStatus.Reconnecting, statuses);
        Assert.False(app.IsPoweringOff);
    }

    [Fact]
    public async Task PowerOffOnABoardThatStaysOnKeepsTheLink()
    {
        await using var f = new AppFixture();
        f.Radio.SupportsPowerOff = false;
        await f.ConnectAsync();
        await Assert.ThrowsAsync<MessageServiceException>(() => f.App.Device.PowerOffAsync());
        Assert.False(f.Radio.PoweredOff);
        Assert.Equal(LinkStatus.Ready, f.App.Status);
        Assert.Equal("> 1.0", await f.App.Device.RunRadioCliAsync("get af"));
    }

    // MARK: Timezone (RadioTimezone.swift)

    [Fact]
    public async Task TimezoneIsSyncedWhenTheRadioIsReady()
    {
        await using var f = new AppFixture();
        f.App.UtcOffsetProvider = () => TimeSpan.FromHours(2);
        await f.ConnectAsync();
        Assert.Equal("2", await f.App.Session!.GetRadioCliValueAsync(RadioCliKey.TimezoneOffset));

        // A fractional offset is left alone.
        f.App.UtcOffsetProvider = () => new TimeSpan(5, 30, 0);
        await f.App.DisconnectAsync();
        await f.ConnectAsync();
        Assert.Equal("2", await f.App.Session!.GetRadioCliValueAsync(RadioCliKey.TimezoneOffset));

        f.App.UtcOffsetProvider = () => TimeSpan.FromHours(-7);
        await f.App.DisconnectAsync();
        await f.ConnectAsync();
        Assert.Equal("-7", await f.App.Session!.GetRadioCliValueAsync(RadioCliKey.TimezoneOffset));
    }

    [Fact]
    public async Task TimezoneIsNotTouchedOnOlderFirmware()
    {
        await using var f = new AppFixture();
        f.Radio.FirmwareCode = 13;
        f.App.UtcOffsetProvider = () => TimeSpan.FromHours(3);
        await f.ConnectAsync();
        Assert.Equal(LinkStatus.Ready, f.App.Status);
        // Read it back on 1.18 with a fractional offset (which is never written): still the radio's own 0.
        await f.App.DisconnectAsync();
        f.Radio.FirmwareCode = 14;
        f.App.UtcOffsetProvider = () => new TimeSpan(5, 30, 0);
        await f.ConnectAsync();
        Assert.Equal("0", await f.App.Session!.GetRadioCliValueAsync(RadioCliKey.TimezoneOffset));
    }

    // MARK: Radio WiFi

    [Fact]
    public async Task WifiStateAndCredentialsApplyAfterReboot()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var state = await app.Device.GetRadioWifiStateAsync();
        Assert.NotNull(state);
        Assert.False(state!.Enabled);
        Assert.False(state.HasNetwork);
        Assert.False(state.IsConnected);
        Assert.Null(state.TcpEndpoint);
        Assert.False(app.Device.RadioWifiRebootPending);

        await app.Device.SetRadioWifiCredentialsAsync("Home Net", "secret pass ");
        Assert.True(app.Device.RadioWifiRebootPending);
        Assert.Equal("secret pass ", await app.Device.GetRadioWifiPasswordAsync());
        await app.Device.SetRadioWifiEnabledAsync(true);
        state = await app.Device.GetRadioWifiStateAsync();
        Assert.True(state!.Enabled);
        Assert.Equal("Home Net", state.Ssid);
        Assert.False(state.IsConnected);
        Assert.True(state.RebootPending);

        await Assert.ThrowsAsync<MeshCoreException>(() => app.Device.SetRadioWifiCredentialsAsync(new string('x', 33), null));

        // Reboot: the radio joins the network; the app reconnects by itself.
        await app.Device.RebootAsync();
        Assert.True(await AppFixture.Eventually(() => app.Status == LinkStatus.Reconnecting, 5000));
        Assert.True(await AppFixture.Eventually(() => app.Status == LinkStatus.Ready, 20000), $"status={app.Status}");
        state = await app.Device.GetRadioWifiStateAsync();
        Assert.True(state!.IsConnected);
        Assert.Equal("192.168.4.42", state.IpAddress);
        Assert.Equal(("192.168.4.42", 5000), state.TcpEndpoint);
        Assert.False(app.Device.RadioWifiRebootPending);

        await app.Device.ClearRadioWifiAsync();
        state = await app.Device.GetRadioWifiStateAsync();
        Assert.False(state!.HasNetwork);
        Assert.True(app.Device.RadioWifiRebootPending);
        await app.Device.SetRadioWifiEnabledAsync(false);
        Assert.False((await app.Device.GetRadioWifiStateAsync())!.Enabled);
    }

    // MARK: Bluetooth / WiFi links of one radio

    [Fact]
    public void LinkRowsFollowTheIphoneRules()
    {
        var bt = Ble;
        var savedWifi = RadioLinkRules.WifiTarget("10.0.0.7", 5001);
        var saved = new SavedRadioLinks { Bluetooth = bt, Wifi = savedWifi };
        var associated = new RadioWifiState(true, "Home", "connected", "192.168.4.42", false);
        var notAssociated = associated with { Status = "disconnected", IpAddress = null };

        // Over Bluetooth: WiFi needs an associated radio; the saved host and port win over the reported address.
        var rows = RadioLinkRules.Rows(bt, saved, associated);
        Assert.Equal([ConnectionKind.Bluetooth, ConnectionKind.Tcp], rows.Select(r => r.Kind));
        Assert.True(rows[0].IsLive);
        Assert.False(rows[0].CanSwitch);
        Assert.True(rows[1].CanSwitch);
        Assert.Equal("10.0.0.7:5001", rows[1].Address);
        Assert.False(RadioLinkRules.Rows(bt, saved, notAssociated)[1].CanSwitch);

        // Nothing saved for WiFi: the reported address with the default port.
        var reported = RadioLinkRules.Rows(bt, new SavedRadioLinks { Bluetooth = bt }, associated);
        Assert.Equal("192.168.4.42", reported[1].Target!.Address);
        Assert.Equal(5000, reported[1].Target!.Port);
        Assert.Equal("192.168.4.42", reported[1].Address);

        // A radio without WiFi settings and no saved WiFi has one row.
        Assert.Single(RadioLinkRules.Rows(bt, new SavedRadioLinks { Bluetooth = bt }, null));

        // Over WiFi: the live endpoint is kept; Bluetooth can be switched to when saved.
        var live = RadioLinkRules.WifiTarget("192.168.4.42");
        rows = RadioLinkRules.Rows(live, saved, associated);
        Assert.True(rows[0].CanSwitch);
        Assert.True(rows[1].IsLive);
        Assert.Equal("192.168.4.42", rows[1].Target!.Address);

        // Disconnected: either saved link connects.
        rows = RadioLinkRules.Rows(null, saved, null);
        Assert.All(rows, r => Assert.True(r.CanSwitch));

        // The demo radio never switches.
        Assert.All(RadioLinkRules.Rows(new ConnectionTarget(ConnectionKind.Simulator, "demo"), saved, associated), r => Assert.False(r.CanSwitch));
    }

    [Fact]
    public async Task BothLinksAreRememberedAndEitherConnects()
    {
        await using var f = new AppFixture();
        var app = f.App;
        app.BluetoothTransportFactory = _ => f.Radio;
        app.TcpTransportFactory = _ => f.Radio;
        await app.ConnectAsync(Ble).WaitAsync(TimeSpan.FromSeconds(30));
        var radioId = app.RadioId!;
        Assert.Equal("AABBCCDDEEFF", app.RadioLinksFor(radioId)!.Bluetooth!.Address);
        Assert.Null(app.RadioLinksFor(radioId)!.Wifi);

        // The radio joins WiFi after a reboot; then WiFi can be switched to.
        await app.Device.SetRadioWifiCredentialsAsync("Home Net", "pw");
        await app.Device.SetRadioWifiEnabledAsync(true);
        var rows = RadioLinkRules.Rows(app.CurrentTarget, app.RadioLinksFor(radioId), await app.Device.GetRadioWifiStateAsync());
        Assert.False(rows.Single(r => r.Kind == ConnectionKind.Tcp).CanSwitch);
        await app.Device.RebootAsync();
        Assert.True(await AppFixture.Eventually(() => app.Status == LinkStatus.Reconnecting, 5000));
        Assert.True(await AppFixture.Eventually(() => app.Status == LinkStatus.Ready, 20000));
        rows = RadioLinkRules.Rows(app.CurrentTarget, app.RadioLinksFor(radioId), await app.Device.GetRadioWifiStateAsync());
        var wifi = rows.Single(r => r.Kind == ConnectionKind.Tcp);
        Assert.True(wifi.CanSwitch);

        await app.SwitchLinkAsync(wifi.Target!).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ConnectionKind.Tcp, app.CurrentTarget!.Kind);
        Assert.Equal(LinkStatus.Ready, app.Status);
        Assert.Equal(radioId, app.RadioId);
        Assert.Null(app.SwitchingTo);
        var links = app.RadioLinksFor(radioId)!;
        Assert.Equal("AABBCCDDEEFF", links.Bluetooth!.Address);
        Assert.Equal("192.168.4.42", links.Wifi!.Address);
        Assert.Equal(ConnectionKind.Tcp, app.Settings.Current.LastConnection!.Kind);
        Assert.Equal(radioId, app.RadioIdFor(Ble));
        Assert.Equal("192.168.4.42", app.AlternateLink(Ble)!.Address);
        Assert.Equal("AABBCCDDEEFF", app.AlternateLink(links.Wifi)!.Address);

        // Back to Bluetooth through the saved address.
        rows = RadioLinkRules.Rows(app.CurrentTarget, links, await app.Device.GetRadioWifiStateAsync());
        var back = rows.Single(r => r.Kind == ConnectionKind.Bluetooth);
        Assert.True(back.CanSwitch);
        await app.SwitchLinkAsync(back.Target!).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ConnectionKind.Bluetooth, app.CurrentTarget!.Kind);
        Assert.Equal(LinkStatus.Ready, app.Status);

        // Disconnected, both links still connect.
        await app.DisconnectAsync();
        Assert.All(RadioLinkRules.Rows(null, app.RadioLinksFor(radioId), null), r => Assert.True(r.CanSwitch));
    }

    [Fact]
    public async Task OlderSavedConnectionsStillCount()
    {
        await using var f = new AppFixture();
        var app = f.App;
        app.BluetoothTransportFactory = _ => f.Radio;
        await app.ConnectAsync(Ble).WaitAsync(TimeSpan.FromSeconds(30));
        var radioId = app.RadioId!;
        await app.DisconnectAsync();
        // Settings saved by a version without radio links: only the radio's last connection is known.
        app.Settings.Update(s => s.RadioLinks.Clear());
        Assert.Equal("AABBCCDDEEFF", app.RadioLinksFor(radioId)!.Bluetooth!.Address);
        Assert.Equal(radioId, app.RadioIdFor(Ble));
        Assert.Null(app.AlternateLink(Ble));
        Assert.Equal(ConnectionKind.Bluetooth, app.Settings.Current.LastConnection!.Kind);
        Assert.Contains(app.Settings.Current.RecentConnections, r => r.Address == "AABBCCDDEEFF");
    }

    [Fact]
    public async Task AFailedSwitchLeavesBothLinksToRecoverWith()
    {
        await using var f = new AppFixture();
        var app = f.App;
        app.BluetoothTransportFactory = _ => f.Radio;
        app.TcpTransportFactory = _ => new TcpTransport("127.0.0.1", 1); // refused
        await app.ConnectAsync(Ble).WaitAsync(TimeSpan.FromSeconds(30));
        var radioId = app.RadioId!;
        await Assert.ThrowsAnyAsync<Exception>(() => app.SwitchLinkAsync(RadioLinkRules.WifiTarget("192.168.4.42")));
        Assert.Null(app.SwitchingTo);
        Assert.False(app.IsConnected);
        // The radio (still shown offline) keeps its Bluetooth link, which connects again.
        Assert.Equal(radioId, app.RadioId);
        var bt = RadioLinkRules.Rows(null, app.RadioLinksFor(radioId), null).Single(r => r.Kind == ConnectionKind.Bluetooth);
        Assert.True(bt.CanSwitch);
        await app.ConnectAsync(bt.Target!).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(LinkStatus.Ready, app.Status);
    }
}

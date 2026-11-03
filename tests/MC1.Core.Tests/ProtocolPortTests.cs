using MC1.Core.Simulation;
using MeshCore;
using Xunit;

namespace MC1.Core.Tests;

/// <summary>
/// WS1: the firmware 1.18 / protocol v14 session APIs round-tripped against the simulated radio (radio CLI, WiFi
/// settings, timezone sync, power off, remote CLI for contact radios, 144-byte contact updates, scoped channel sends).
/// </summary>
public class ProtocolPortTests
{
    private static readonly SessionConfiguration Cfg = SessionConfiguration.Default with
    {
        DefaultTimeout = TimeSpan.FromSeconds(5),
        PowerOffReplyWindow = TimeSpan.FromSeconds(2),
    };

    private static async Task<MeshCoreSession> StartAsync(SimulatedRadio radio)
    {
        var s = new MeshCoreSession(radio, Cfg);
        await s.StartAsync();
        await s.QueryDeviceAsync();
        return s;
    }

    private static async Task DrainQueueAsync(MeshCoreSession s)
    {
        while (await s.GetMessageAsync() is not MessageResult.NoMoreMessages) { }
    }

    [Fact]
    public async Task SimulatorReportsProtocolV14()
    {
        var radio = new SimulatedRadio(chatter: false);
        var s = await StartAsync(radio);
        Assert.Equal(14, s.DeviceCapabilities!.FirmwareVersion);
        Assert.True(s.DeviceCapabilities.SupportsRadioCli);
        Assert.StartsWith("v1.18.0", s.DeviceCapabilities.Version);
        await s.StopAsync();
    }

    [Fact]
    public async Task RadioCliAnswersCommonCommands()
    {
        var radio = new SimulatedRadio(chatter: false);
        var s = await StartAsync(radio);
        Assert.Equal("> Demo Radio", await s.RunCliCommandAsync("get name"));
        Assert.StartsWith("v1.18.0", await s.RunCliCommandAsync("ver"));
        Assert.EndsWith("UTC", await s.RunCliCommandAsync("clock"));
        Assert.Equal("> 910.525,62.5,7,5", await s.RunCliCommandAsync("get radio"));
        Assert.Equal("Unknown command", await s.RunCliCommandAsync("frobnicate"));
        Assert.Equal("??: nosuch", await s.RunCliCommandAsync("get nosuch"));
        Assert.Null(await s.GetRadioCliValueAsync("nosuch"));
        Assert.Equal("1.0", await s.GetRadioCliValueAsync("af"));

        await s.SetRadioCliValueAsync("af", "2.5");
        Assert.Equal("2.5", await s.GetRadioCliValueAsync("af"));
        await s.SetRadioCliValueAsync("name", "Field Radio");
        Assert.Equal("Field Radio", (await s.SendAppStartAsync()).Name);

        var rejected = await Assert.ThrowsAsync<MeshCoreException>(() => s.SetRadioCliValueAsync("tz.offset", "20"));
        Assert.Equal(MeshCoreErrorKind.CommandFailed, rejected.Kind);
        Assert.StartsWith("Error", rejected.RadioReply);
        await s.StopAsync();
    }

    [Fact]
    public async Task OldFirmwareRejectsTheRadioCli()
    {
        var radio = new SimulatedRadio(chatter: false) { FirmwareCode = 10 };
        var s = new MeshCoreSession(radio, Cfg);
        await s.StartAsync();
        // Capabilities unknown: the frame goes out and the radio answers "unsupported".
        var ex = await Assert.ThrowsAsync<MeshCoreException>(() => s.RunCliCommandAsync("get name"));
        Assert.Equal(ErrorCode.UnsupportedCommand, ex.FirmwareError);
        await s.QueryDeviceAsync();
        Assert.Equal(MeshCoreErrorKind.Unsupported, (await Assert.ThrowsAsync<MeshCoreException>(() => s.RunCliCommandAsync("get name"))).Kind);
        await s.StopAsync();
    }

    [Fact]
    public async Task WifiSettingsApplyAfterAReboot()
    {
        var radio = new SimulatedRadio(chatter: false);
        var s = await StartAsync(radio);
        var state = (await s.GetRadioWifiStateAsync())!;
        Assert.Equal((false, "", false, (string?)null, false), (state.Enabled, state.Ssid, state.IsConnected, state.IpAddress, state.RebootPending));

        await s.SetRadioWifiCredentialsAsync("Field Net", "secret pass");
        await s.SetRadioWifiEnabledAsync(true);
        state = (await s.GetRadioWifiStateAsync())!;
        Assert.Equal(("Field Net", true, false, true), (state.Ssid, state.Enabled, state.IsConnected, state.RebootPending));
        Assert.Equal("secret pass", await s.GetRadioWifiPasswordAsync());

        await s.RebootAsync();
        await AppFixture.Eventually(() => !radio.IsConnected, 3000);
        await s.StopAsync();

        s = await StartAsync(radio);
        state = (await s.GetRadioWifiStateAsync())!;
        Assert.True(state.IsConnected);
        Assert.False(state.RebootPending);
        Assert.Equal(("192.168.4.42", TcpTransport.DefaultPort), state.TcpEndpoint);

        await s.ClearRadioWifiAsync();
        Assert.Equal("", (await s.GetRadioWifiStateAsync())!.Ssid);
        Assert.Equal("", await s.GetRadioWifiPasswordAsync());
        await s.StopAsync();
    }

    [Fact]
    public async Task TimezoneSyncWritesOnce()
    {
        var radio = new SimulatedRadio(chatter: false);
        var s = await StartAsync(radio);
        Assert.True(await s.SyncRadioTimezoneAsync(TimeSpan.FromHours(-5)));
        Assert.Equal("-5", await s.GetRadioCliValueAsync(RadioCliKey.TimezoneOffset));
        Assert.False(await s.SyncRadioTimezoneAsync(TimeSpan.FromHours(-5)));
        Assert.False(await s.SyncRadioTimezoneAsync(new TimeSpan(5, 30, 0)));
        Assert.Equal("-5", await s.GetRadioCliValueAsync(RadioCliKey.TimezoneOffset));
        await s.StopAsync();
    }

    [Fact]
    public async Task PowerOffSwitchesTheBoardOff()
    {
        var radio = new SimulatedRadio(chatter: false);
        var s = await StartAsync(radio);
        var r = await s.ExchangePowerOffAsync();
        Assert.True(r.PoweredOff, r.ToString());
        Assert.True(radio.PoweredOff);
        await AppFixture.Eventually(() => !radio.IsConnected, 3000);
        await s.StopAsync();
    }

    [Fact]
    public async Task PowerOffOnABoardWithoutAPowerSwitchStaysOn()
    {
        var radio = new SimulatedRadio(chatter: false) { SupportsPowerOff = false };
        var s = await StartAsync(radio);
        Assert.False(await s.PowerOffAsync());
        Assert.False(radio.PoweredOff);
        Assert.True(radio.IsConnected);
        await s.StopAsync();
    }

    [Fact]
    public async Task RemoteCliRunsOnContactRadiosThatGrantedIt()
    {
        var radio = new SimulatedRadio(chatter: false);
        var s = await StartAsync(radio);
        var contacts = (await s.GetContactsAsync()).Contacts;
        var alice = contacts.Single(c => c.AdvertisedName == "Alice");
        var bob = contacts.Single(c => c.AdvertisedName == "Bob");
        await DrainQueueAsync(s);

        using var waiting = s.Subscribe(e => e is MeshEvent.MessagesWaiting);
        await s.SendCommandAsync(alice.PublicKey, "AB|get name", TextType.CliCommand);
        var reply = await ReadReplyAsync(s, waiting);
        Assert.Equal((byte)TextType.CliData, reply.TextType);
        Assert.Equal("AB|> Alice", reply.Text);
        Assert.Equal(alice.PublicKey[..6], reply.SenderPublicKeyPrefix);

        await s.SendCommandAsync(alice.PublicKey, "CD|ver", TextType.CliCommand);
        Assert.StartsWith("CD|v1.18.0", (await ReadReplyAsync(s, waiting)).Text);

        // Bob never granted remote CLI: his radio drops the command.
        await s.SendCommandAsync(bob.PublicKey, "EF|get name", TextType.CliCommand);
        await Task.Delay(1200);
        Assert.IsType<MessageResult.NoMoreMessages>(await s.GetMessageAsync());
        await s.StopAsync();
    }

    private static async Task<ContactMessage> ReadReplyAsync(MeshCoreSession s, EventSubscription waiting)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await waiting.Reader.ReadAsync(cts.Token);
        return Assert.IsType<MessageResult.Contact>(await s.GetMessageAsync()).Message;
    }

    [Fact]
    public async Task ContactUpdatesAre144BytesAndKeepCoordinates()
    {
        var radio = new SimulatedRadio(chatter: false);
        var s = await StartAsync(radio);
        var alice = (await s.GetContactsAsync()).Contacts.Single(c => c.AdvertisedName == "Alice");
        await s.UpdateContactAsync(alice with { Latitude = 12.5, Longitude = -45.25, Flags = ContactFlags.Favorite | ContactFlags.RemoteCliAllowed });
        Assert.Equal(144, radio.LastContactUpdateLength);
        var updated = (await s.GetContactAsync(alice.PublicKey))!;
        Assert.Equal(12.5, updated.Latitude, 5);
        Assert.Equal(-45.25, updated.Longitude, 5);
        Assert.Equal(ContactFlags.Favorite | ContactFlags.RemoteCliAllowed, updated.Flags);
        Assert.True(updated.LastModified > alice.LastModified);
        await s.StopAsync();
    }

    [Fact]
    public async Task ScopedChannelSendAndSessionCacheAgainstTheSimulator()
    {
        var radio = new SimulatedRadio(chatter: false);
        var s = await StartAsync(radio);
        await s.SendChannelMessageAsync(0, "hello region", DateTimeOffset.UtcNow, new ResolvedFloodScope.Scope(new FloodScope.Region("florida")));
        await s.SendChannelMessageAsync(0, "hello all", DateTimeOffset.UtcNow, new ResolvedFloodScope.Unscoped());

        var all = await s.EnsureContactsAsync();
        Assert.Contains(all, c => c.AdvertisedName == "Alice");
        var count = all.Count;
        var bob = all.Single(c => c.AdvertisedName == "Bob");
        await s.RemoveContactAsync(bob.PublicKey);
        Assert.DoesNotContain(s.Contacts.Contacts, c => c.AdvertisedName == "Bob");
        Assert.Equal(count - 1, (await s.EnsureContactsAsync(force: true)).Count);
        await s.StopAsync();
    }
}

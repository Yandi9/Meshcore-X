using System.Text;
using MeshCore;
using Xunit;

namespace MeshCore.Tests;

/// <summary>
/// WS1 port of the MeshCore One v1.5.1 → v1.6.2 protocol changes (Swift MeshCore package): firmware 1.18 / companion
/// protocol v14 commands and the protocol bug fixes. Frame layouts mirror the Swift tests byte for byte.
/// </summary>
public class ProtocolPortTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1704067200);
    private static readonly byte[] Dest = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB];
    private static readonly SessionConfiguration Cfg = SessionConfiguration.Default with
    {
        DefaultTimeout = TimeSpan.FromSeconds(3),
        PowerOffReplyWindow = TimeSpan.FromMilliseconds(300),
        BinaryRequestOverallTimeout = TimeSpan.FromSeconds(3),
        BinaryRequestRetransmitInterval = null,
    };

    // MARK: Helpers

    private static async Task<(MeshCoreSession S, MockTransport T)> Started(Func<byte[], IEnumerable<byte[]>>? onSend = null,
        byte? firmware = null, SessionConfiguration? cfg = null)
    {
        var t = new MockTransport();
        t.OnSend = cmd => cmd[0] == (byte)CommandCode.AppStart ? [ParserTests.SelfInfoFrame()] : onSend?.Invoke(cmd) ?? Array.Empty<byte[]>();
        var s = new MeshCoreSession(t, cfg ?? Cfg);
        await s.StartAsync();
        if (firmware is { } fw) s.HandleReceived(ParserTests.DeviceInfoFrame(fw));
        return (s, t);
    }

    private static List<byte[]> Sent(MockTransport t) { lock (t.Sent) return t.Sent.ToList(); }

    private static async Task Eventually(Func<bool> cond, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(5);
        }
    }

    private static byte[] CliReply(string text) => new byte[] { (byte)ResponseCode.CliReply }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
    private static readonly byte[] OkFrame = [0x00];
    private static byte[] ErrFrame(byte code) => [0x01, code];
    private static byte[] MessageSentFrame(byte[] tag, uint timeoutMs = 1000) => new ByteWriter(0x06, 0).Raw(tag).U32(timeoutMs).ToArray();
    private static byte[] BinaryResponseFrame(byte[] tag, byte[] body) => new byte[] { 0x8C, 0 }.Concat(tag).Concat(body).ToArray();

    // MARK: Codes and frames (V118CommandsTests, V118ParsingTests, PythonReferenceTests)

    [Fact]
    public void NewCodes()
    {
        Assert.Equal(0x42, (byte)CommandCode.RunCliCommand);
        Assert.Equal(0x1D, (byte)ResponseCode.CliReply);
        Assert.Equal(0x03, (byte)TextType.CliCommand);
        Assert.Equal(0x10, (byte)ContactFlags.RemoteCliAllowed);
        Assert.True(new DeviceCapabilities(14, 0, 0, 0, "", "", "").SupportsRadioCli);
        Assert.False(new DeviceCapabilities(13, 0, 0, 0, "", "", "").SupportsRadioCli);
    }

    [Fact]
    public void RunCliCommandFormat()
    {
        var packet = PacketBuilder.RunCliCommand("get cad");
        Assert.Equal(0x42, packet[0]);
        Assert.Equal("get cad"u8.ToArray(), packet[1..]);
        Assert.Equal(PacketBuilder.MaxFrameSize, PacketBuilder.RunCliCommand(new string('a', PacketBuilder.RunCliCommandMaxUtf8Bytes)).Length);
        Assert.Equal("poweroff"u8.ToArray(), PacketBuilder.RunCliCommand(PacketBuilder.PowerOffCommand)[1..]);
    }

    [Fact]
    public void CliReplyParses()
    {
        Assert.Equal("OK", Assert.IsType<MeshEvent.CliReply>(PacketParser.Parse(CliReply("OK"))).Text);
        Assert.Equal("", Assert.IsType<MeshEvent.CliReply>(PacketParser.Parse([0x1D])).Text);
    }

    [Fact]
    public void SendCommandWritesTheTextType()
    {
        byte[] status = [0x02, 0x01, 0x00, 0x80, 0x00, 0x92, 0x65, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0x73, 0x74, 0x61, 0x74, 0x75, 0x73];
        Assert.Equal(status, PacketBuilder.SendCommand(Dest, "status", TextType.CliData, T));
        Assert.Equal(status, PacketBuilder.SendCommand(Dest, "status", T));
        var expected = (byte[])status.Clone();
        expected[1] = 0x03;
        Assert.Equal(expected, PacketBuilder.SendCommand(Dest, "status", TextType.CliCommand, T));
    }

    // MARK: 18f25069 — 144-byte contact updates

    [Fact]
    public void UpdateContactLayoutIs144Bytes()
    {
        var key = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        var c = new MeshContact(key, ContactType.Chat, 0x07, (ContactFlags)0x02, 3, [0x11, 0x22, 0x33], "TestNode", T, 37.7749, -122.4194, T);
        var p = PacketBuilder.UpdateContact(c);
        Assert.Equal(144, p.Length);
        Assert.Equal(0x09, p[0]);
        Assert.Equal(key, p[1..33]);
        Assert.Equal(0x07, p[33]); // raw type byte forwarded verbatim
        Assert.Equal(0x02, p[34]);
        Assert.Equal(3, p[35]);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, p[36..39]);
        Assert.All(p[39..100], b => Assert.Equal(0, b));
        Assert.Equal("TestNode"u8.ToArray(), p[100..108]);
        Assert.Equal(1704067200u, p.ReadUInt32LE(132));
        Assert.Equal(37_774_900, p.ReadInt32LE(136));
        Assert.Equal(-122_419_400, p.ReadInt32LE(140));
    }

    // MARK: 8dcadadf — channel names, LPP current, DM author prefix, neighbours

    [Theory]
    [InlineData(28, false)]
    [InlineData(27, true)]
    public void ChannelNameSurvivesTheFirmwareCStringCopy(int prefix, bool keepsEmoji)
    {
        var name = new string('a', prefix) + "😀";
        var secret = Enumerable.Repeat((byte)0xAA, 16).ToArray();
        var packet = PacketBuilder.SetChannel(2, name, secret);
        Assert.Equal(50, packet.Length);
        Assert.Equal(0, packet[33]);
        var stored = packet[2..33].TrimAtNull().PaddedOrTruncated(32);
        var expected = keepsEmoji ? name : new string('a', prefix);
        Assert.Equal(expected, Encoding.UTF8.GetString(stored.TrimAtNull()));
        var info = Assert.IsType<MeshEvent.ChannelInfoEvent>(Parsers.ParseChannelInfo([2, .. stored, .. secret])).Info;
        Assert.Equal(expected, info.Name);
        Assert.Equal(secret, info.Secret);
    }

    [Theory]
    [InlineData((ushort)32767)]
    [InlineData((ushort)32768)]
    [InlineData((ushort)40000)]
    [InlineData((ushort)65535)]
    public void LppCurrentIsUnsignedMilliamps(ushort milliamps)
    {
        var reading = LppDecoder.Decode(new LppEncoder().AddCurrent(1, milliamps).Encode()).Single();
        Assert.Equal(1, reading.Channel);
        Assert.Equal(LppSensorType.Current, reading.Type);
        Assert.Equal(new LppValue.Float(milliamps / 1000.0), reading.Value);
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0x42, 0x43, 0x44 }, (byte)8)]
    [InlineData(new byte[] { 0x00, 0x42, 0x43, 0x44 }, (byte)8)]
    [InlineData(new byte[] { 0xFF, 0x42, 0x43, 0x44 }, (byte)8)]
    [InlineData(new byte[] { 0x41, 0x42, 0x43, 0x44 }, (byte)11)]
    [InlineData(new byte[] { 0x00, 0x42, 0x43, 0x44 }, (byte)11)]
    [InlineData(new byte[] { 0xFF, 0x42, 0x43, 0x44 }, (byte)11)]
    public void SignedDirectMessageExcludesTheAuthorPrefix(byte[] authorPrefix, byte typeAttempt)
    {
        var me = CryptoTests.NewIdentity();
        var sender = CryptoTests.NewIdentity();
        var shared = DirectMessageCrypto.ComputeSharedSecret(sender.Private64, Ed25519ToX25519.ConvertPublicKey(me.Public)!)!;
        var plain = new ByteWriter().U32(1_703_123_456).U8(typeAttempt).Raw(authorPrefix).Utf8("Hello").ToArray();
        var ct = AesEcb.Encrypt(plain, shared);
        byte[] packet = [0xAA, 0xBB, .. AesEcb.Mac(ct, shared, 2), .. ct];
        var r = Assert.IsType<DirectMessageCrypto.DecryptResult.Success>(
            DirectMessageCrypto.Decrypt(packet, me.Private64, Ed25519ToX25519.ConvertPublicKey(sender.Public)!));
        Assert.Equal(1_703_123_456u, r.Timestamp);
        Assert.Equal(typeAttempt, r.TypeAttempt);
        Assert.Equal("Hello", r.Text);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0xFF, 0xFF })]
    [InlineData(new byte[] { 0xFF, 0xFF, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void NegativeNeighbourCountsProduceAnEmptyResponse(byte[] payload)
    {
        var r = Parsers.ParseNeighbours(payload, [1, 2, 3, 4], [5, 6, 7, 8]);
        Assert.Equal(0, r.TotalCount);
        Assert.Empty(r.Neighbours);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.PublicKeyPrefix);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, r.Tag);
    }

    [Theory]
    [InlineData((byte)0xFF, 0, 165)]
    [InlineData((byte)0x00, 0, 165)]
    [InlineData((byte)0x07, 7, 164)]
    [InlineData((byte)0x44, 8, 163)]
    [InlineData((byte)0x83, 9, 162)]
    public void SendChannelDataEnforcesPayloadAndHostFrameBudgets(byte pathLength, int pathCount, int payloadCount)
    {
        var path = Enumerable.Repeat((byte)0x11, pathCount).ToArray();
        var payload = Enumerable.Repeat((byte)0x55, payloadCount).ToArray();
        var packet = PacketBuilder.SendChannelData(1, 0x00FF, payload, pathLength, path);
        Assert.Equal(5 + pathCount + payloadCount, packet.Length);
        Assert.True(packet.Length <= 176);
        Assert.Equal(payload, packet[^payloadCount..]);
        Assert.Equal(payloadCount, PacketBuilder.ChannelDataPayloadLimit(pathLength, pathCount));
        var ex = Assert.Throws<MeshCoreException>(() => PacketBuilder.SendChannelData(1, 0x00FF, [.. payload, 0x55], pathLength, path));
        Assert.Equal(MeshCoreErrorKind.InvalidInput, ex.Kind);
    }

    [Fact]
    public void SendChannelDataFloodFormatIgnoresPathBytes()
    {
        Assert.Equal(new byte[] { 0x3E, 0x02, 0xFF, 0xFF, 0xFF, 0xAA, 0xBB, 0xCC }, PacketBuilder.SendChannelData(2, 0xFFFF, [0xAA, 0xBB, 0xCC]));
        Assert.Equal(new byte[] { 0x3E, 0x00, 0xFF, 0xFF, 0xFF, 0xAA }, PacketBuilder.SendChannelData(0, 0xFFFF, [0xAA], 0xFF, [0x11, 0x22]));
    }

    // MARK: 6353667d / 8dcadadf — raw data path encoding and frame budget

    [Fact]
    public void SendRawDataWritesTheEncodedPath()
    {
        var p = PacketBuilder.SendRawData(0x43, [0x11, 0x22, 0x33, 0x44, 0x55, 0x66], [0xAA, 0xBB, 0xCC]);
        Assert.Equal(new byte[] { 0x19, 0x43, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0xAA, 0xBB, 0xCC }, p);
        Assert.Equal(new byte[] { 0x19, 0x00, 0xAA }, PacketBuilder.SendRawData(0x00, [], [0xAA]));
        var path63 = Enumerable.Repeat((byte)0x11, 63).ToArray();
        var p63 = PacketBuilder.SendRawData(0x95, path63, [0xAA]);
        Assert.Equal(0x95, p63[1]);
        Assert.Equal(path63, p63[2..65]);
        Assert.Equal(0xAA, p63[65]);
        Assert.Equal(MeshCoreErrorKind.InvalidInput,
            Assert.Throws<MeshCoreException>(() => PacketBuilder.SendRawData(0x43, [0x11, 0x11, 0x11, 0x11], [0xAA])).Kind);
    }

    [Theory]
    [InlineData((byte)0x00, 0, 174)]
    [InlineData((byte)0x07, 7, 167)]
    [InlineData((byte)0x44, 8, 166)]
    [InlineData((byte)0x83, 9, 165)]
    public void SendRawDataEnforcesTheHostFrameBudget(byte pathLength, int pathCount, int payloadCount)
    {
        var path = Enumerable.Repeat((byte)0x11, pathCount + 3).ToArray();
        var payload = Enumerable.Repeat((byte)0x22, payloadCount).ToArray();
        var packet = PacketBuilder.SendRawData(pathLength, path, payload);
        Assert.Equal(176, packet.Length);
        Assert.Equal(payload, packet[^payloadCount..]);
        Assert.Throws<MeshCoreException>(() => PacketBuilder.SendRawData(pathLength, path, [.. payload, 0x22]));
    }

    // MARK: 33893051 — per-hop reversal in anonymous requests

    [Theory]
    [InlineData((byte)0x02, new byte[] { 0x11, 0x22 }, new byte[] { 0x22, 0x11 })]
    [InlineData((byte)0x41, new byte[] { 0x11, 0x22 }, new byte[] { 0x11, 0x22 })]
    [InlineData((byte)0x42, new byte[] { 0x11, 0x22, 0x33, 0x44 }, new byte[] { 0x33, 0x44, 0x11, 0x22 })]
    [InlineData((byte)0x81, new byte[] { 0x11, 0x22, 0x33 }, new byte[] { 0x11, 0x22, 0x33 })]
    [InlineData((byte)0x82, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }, new byte[] { 0x44, 0x55, 0x66, 0x11, 0x22, 0x33 })]
    public void RegionsRequestReversesHopOrderWhilePreservingEachHash(byte pathLength, byte[] path, byte[] replyPath)
    {
        var pubkey = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        var packet = PacketBuilder.SendAnonReq(pubkey, AnonRequestType.Regions, pathLength, path);
        Assert.Equal(0x39, packet[0]);
        Assert.Equal(pubkey, packet[1..33]);
        Assert.Equal(0x01, packet[33]);
        Assert.Equal(pathLength, packet[34]);
        Assert.Equal(replyPath, packet[35..]);
        Assert.Equal(35 + path.Length, packet.Length);
    }

    // MARK: 064555df / 3e7dc727 — unscoped transport codes

    [Fact]
    public void IsUnscopedIsTrueOnlyForALeadingZeroUInt16()
    {
        Assert.True(TransportCodeRegionResolver.IsUnscoped([0x00, 0x00, 0x12, 0x34]));
        Assert.False(TransportCodeRegionResolver.IsUnscoped([0x01, 0x00, 0x12, 0x34]));
        Assert.False(TransportCodeRegionResolver.IsUnscoped([0x00]));
        Assert.False(TransportCodeRegionResolver.IsUnscoped([]));
        Assert.False(TransportCodeRegionResolver.IsUnscoped(null));
        // A transport code sliced out of an RX-log packet is read from its own start.
        var parsed = RxLogParser.Parse(null, null, [0x14, 0x00, 0x00, 0x12, 0x34, 0x00, 0xAA])!;
        Assert.True(TransportCodeRegionResolver.IsUnscoped(parsed.TransportCode));
    }

    // MARK: Radio CLI replies and helpers (RadioCLIReplyTests, RadioTimezoneTests)

    [Fact]
    public void RadioCliReplyClassification()
    {
        Assert.Equal(new RadioCliReply.Value("-7"), RadioCliReply.Parse("> -7"));
        Assert.Equal(new RadioCliReply.Value(""), RadioCliReply.Parse("> "));
        Assert.Equal(new RadioCliReply.Value("my net "), RadioCliReply.Parse("> my net "));
        Assert.Equal(new RadioCliReply.Value("my net "), RadioCliReply.Parse("> my net \r\n"));
        Assert.Equal(new RadioCliReply.Value("-7"), RadioCliReply.Parse("> -7\r\n"));
        Assert.Equal(new RadioCliReply.Ok("OK - interval rounded to 16"), RadioCliReply.Parse("OK - interval rounded to 16"));
        Assert.Equal(new RadioCliReply.Error("Error, must be 0-2"), RadioCliReply.Parse("Error, must be 0-2"));
        Assert.Equal(new RadioCliReply.Error("ERROR: af must be 0-9"), RadioCliReply.Parse("ERROR: af must be 0-9"));
        Assert.Equal(new RadioCliReply.Error("ERR - repeat now deprecated"), RadioCliReply.Parse("ERR - repeat now deprecated"));
        Assert.Equal(new RadioCliReply.UnknownCommand(), RadioCliReply.Parse("Unknown command"));
        Assert.Equal(new RadioCliReply.Other(""), RadioCliReply.Parse(""));
        Assert.Equal(new RadioCliReply.Other(""), RadioCliReply.Parse("\r\n"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5 * 3600, -5)]
    [InlineData(14 * 3600, 14)]
    [InlineData(-12 * 3600, -12)]
    [InlineData(15 * 3600, null)]
    [InlineData(-13 * 3600, null)]
    [InlineData(5 * 3600 + 1800, null)]
    public void TimezoneOffsetHours(int seconds, int? hours) =>
        Assert.Equal(hours, RadioTimezone.OffsetHours(TimeSpan.FromSeconds(seconds)));

    // MARK: Session — radio CLI (V118SessionMethodsTests)

    [Fact]
    public async Task RunCliCommandReturnsTheReplyAndSendsTheLineVerbatim()
    {
        var (s, t) = await Started(cmd => cmd[0] == 0x42 ? [CliReply("Unknown command")] : []);
        Assert.Equal("Unknown command", await s.RunCliCommandAsync("get cad"));
        Assert.Equal(PacketBuilder.RunCliCommand("get cad"), Sent(t)[^1]);
        foreach (var line in new[] { "set wifi.pwd ", "set wifi.ssid Field Net " })
        {
            await s.RunCliCommandAsync(line);
            Assert.Equal(Encoding.UTF8.GetBytes(line), Sent(t)[^1][1..]);
        }
        Assert.Equal(0x20, Sent(t)[^2][^1]);
        await s.StopAsync();
    }

    [Fact]
    public async Task RunCliCommandSendsAMaxLengthCommand()
    {
        var (s, t) = await Started(cmd => cmd[0] == 0x42 ? [CliReply("OK")] : []);
        await s.RunCliCommandAsync(new string('a', PacketBuilder.RunCliCommandMaxUtf8Bytes));
        Assert.Equal(PacketBuilder.MaxFrameSize, Sent(t)[^1].Length);
    }

    [Fact]
    public async Task RunCliCommandPropagatesUnsupportedAsDeviceError()
    {
        var (s, _) = await Started(cmd => cmd[0] == 0x42 ? [ErrFrame(1)] : []);
        var ex = await Assert.ThrowsAsync<MeshCoreException>(() => s.RunCliCommandAsync("get cad"));
        Assert.Equal(MeshCoreErrorKind.DeviceError, ex.Kind);
        Assert.Equal(ErrorCode.UnsupportedCommand, ex.FirmwareError);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("   ")]
    [InlineData("")]
    public async Task RunCliCommandRejectsBadLinesWithoutSending(string line)
    {
        var (s, t) = await Started();
        var before = Sent(t).Count;
        Assert.Equal(MeshCoreErrorKind.InvalidInput, (await Assert.ThrowsAsync<MeshCoreException>(() => s.RunCliCommandAsync(line))).Kind);
        Assert.Equal(MeshCoreErrorKind.InvalidInput,
            (await Assert.ThrowsAsync<MeshCoreException>(() => s.RunCliCommandAsync(new string('a', PacketBuilder.RunCliCommandMaxUtf8Bytes + 1)))).Kind);
        Assert.Equal(before, Sent(t).Count);
    }

    [Fact]
    public async Task RadioCliIsRefusedOnKnownOldFirmwareWithoutSending()
    {
        var (s, t) = await Started(firmware: 13);
        var before = Sent(t).Count;
        var ex = await Assert.ThrowsAsync<MeshCoreException>(() => s.RunCliCommandAsync("get name"));
        Assert.Equal(MeshCoreErrorKind.Unsupported, ex.Kind);
        Assert.Equal((byte)14, ex.MinimumFirmware);
        Assert.Equal(MeshCoreErrorKind.Unsupported, (await Assert.ThrowsAsync<MeshCoreException>(() => s.PowerOffAsync())).Kind);
        Assert.Equal(MeshCoreErrorKind.Unsupported,
            (await Assert.ThrowsAsync<MeshCoreException>(() => s.SendCommandAsync(Dest, "ver", TextType.CliCommand))).Kind);
        Assert.Equal(before, Sent(t).Count);
        s.HandleReceived(ParserTests.DeviceInfoFrame(14));
        Assert.True(s.DeviceCapabilities!.SupportsRadioCli);
    }

    [Fact]
    public async Task CliFramesAreRedactedInTheTrace()
    {
        var (s, _) = await Started(cmd => cmd[0] == 0x42 ? [CliReply("> hunter2")] : []);
        var traced = new List<(bool Out, byte[] Frame)>();
        s.FrameTraced += (o, f) => { lock (traced) traced.Add((o, f)); };
        await s.RunCliCommandAsync("get wifi.pwd");
        await Eventually(() => { lock (traced) return traced.Count == 2; });
        lock (traced)
        {
            Assert.Equal(new byte[] { 0x42 }, traced.Single(x => x.Out).Frame);
            Assert.Equal(new byte[] { 0x1D }, traced.Single(x => !x.Out).Frame);
        }
    }

    [Fact]
    public async Task GetRadioCliValueClassifiesReplies()
    {
        var reply = "";
        var (s, t) = await Started(cmd => cmd[0] == 0x42 ? [CliReply(reply)] : []);
        reply = "> -7";
        Assert.Equal("-7", await s.GetRadioCliValueAsync("tz.offset"));
        reply = "Unknown command";
        Assert.Null(await s.GetRadioCliValueAsync("cad"));
        reply = "??: wifi.enabled";
        Assert.Null(await s.GetRadioCliValueAsync("wifi.enabled"));
        Assert.Equal(new[] { "get tz.offset", "get cad", "get wifi.enabled" }, Sent(t).Skip(1).Select(f => Encoding.UTF8.GetString(f, 1, f.Length - 1)));

        reply = "Error, must be 0-2";
        var rejected = await Assert.ThrowsAsync<MeshCoreException>(() => s.GetRadioCliValueAsync("cad"));
        Assert.Equal(MeshCoreErrorKind.CommandFailed, rejected.Kind);
        Assert.Equal("Error, must be 0-2", rejected.RadioReply);
        foreach (var bad in new[] { "OK", "plain text" })
        {
            reply = bad;
            Assert.Equal(MeshCoreErrorKind.InvalidResponse, (await Assert.ThrowsAsync<MeshCoreException>(() => s.GetRadioCliValueAsync("cad"))).Kind);
        }
        reply = "??: wifi.enabled";
        Assert.Equal(MeshCoreErrorKind.InvalidResponse, (await Assert.ThrowsAsync<MeshCoreException>(() => s.GetRadioCliValueAsync("wifi.ssid"))).Kind);
    }

    [Fact]
    public async Task SetRadioCliValueAcceptsOkAndValueAndRejectsTheRest()
    {
        var reply = "OK";
        var (s, t) = await Started(cmd => cmd[0] == 0x42 ? [CliReply(reply)] : []);
        reply = "OK - interval rounded to 16";
        await s.SetRadioCliValueAsync("agc.reset.interval", "17");
        reply = "> 16";
        await s.SetRadioCliValueAsync("agc.reset.interval", "17");
        reply = "ERROR: af must be 0-9";
        var e1 = await Assert.ThrowsAsync<MeshCoreException>(() => s.SetRadioCliValueAsync("af", "9"));
        Assert.Equal((MeshCoreErrorKind.CommandFailed, "ERROR: af must be 0-9"), (e1.Kind, e1.RadioReply));
        reply = "Unknown command";
        var e2 = await Assert.ThrowsAsync<MeshCoreException>(() => s.SetRadioCliValueAsync("cad", "on"));
        Assert.Equal((MeshCoreErrorKind.CommandFailed, "Unknown command"), (e2.Kind, e2.RadioReply));
        reply = "nope";
        Assert.Equal(MeshCoreErrorKind.InvalidResponse, (await Assert.ThrowsAsync<MeshCoreException>(() => s.SetRadioCliValueAsync("cad", "on"))).Kind);

        reply = "OK";
        await s.SetRadioCliValueAsync("wifi.pwd", "");
        await s.SetRadioCliValueAsync("wifi.clear", null);
        Assert.Equal(new[] { "set wifi.pwd ", "set wifi.clear" }, Sent(t)[^2..].Select(f => Encoding.UTF8.GetString(f, 1, f.Length - 1)));
    }

    [Fact]
    public async Task WifiRebootPendingFollowsAcceptedSetWifiLinesOnly()
    {
        var reply = "Error, bad ssid";
        var (s, _) = await Started(cmd => cmd[0] == 0x42 ? [CliReply(reply)] : []);
        await Assert.ThrowsAsync<MeshCoreException>(() => s.SetRadioCliValueAsync("wifi.ssid", "net"));
        Assert.False(s.RadioWifiRebootPending);
        reply = "OK";
        await s.SetRadioCliValueAsync("cad", "on");
        Assert.False(s.RadioWifiRebootPending);
        await s.RunCliCommandAsync("set wifi.enabled 0");
        Assert.True(s.RadioWifiRebootPending);
    }

    [Fact]
    public async Task WifiStateAndCredentialValidation()
    {
        var values = new Dictionary<string, string> { ["wifi.enabled"] = "> 1", ["wifi.ssid"] = "> (not set)", ["wifi.status"] = "> disconnected", ["wifi.ip"] = "> (not connected)" };
        var (s, t) = await Started(cmd =>
        {
            if (cmd[0] != 0x42) return [];
            var line = Encoding.UTF8.GetString(cmd, 1, cmd.Length - 1);
            return [CliReply(line.StartsWith("get ") ? values.GetValueOrDefault(line[4..], "??: " + line[4..]) : "OK")];
        });
        var st = (await s.GetRadioWifiStateAsync())!;
        Assert.True(st.Enabled);
        Assert.Equal("", st.Ssid);
        Assert.False(st.HasNetwork);
        Assert.Null(st.IpAddress);
        Assert.False(st.IsConnected);
        Assert.Null(st.TcpEndpoint);

        values["wifi.ssid"] = "> Field Net";
        values["wifi.status"] = "> connected";
        values["wifi.ip"] = "> 192.168.1.20";
        st = (await s.GetRadioWifiStateAsync())!;
        Assert.Equal(("Field Net", true, "192.168.1.20"), (st.Ssid, st.IsConnected, st.IpAddress));
        Assert.Equal(("192.168.1.20", 5000), st.TcpEndpoint);

        values["wifi.enabled"] = "> maybe";
        Assert.Equal(MeshCoreErrorKind.InvalidResponse, (await Assert.ThrowsAsync<MeshCoreException>(() => s.GetRadioWifiStateAsync())).Kind);
        values.Remove("wifi.enabled");
        Assert.Null(await s.GetRadioWifiStateAsync());

        var before = Sent(t).Count;
        await Assert.ThrowsAsync<MeshCoreException>(() => s.SetRadioWifiCredentialsAsync("", "x"));
        await Assert.ThrowsAsync<MeshCoreException>(() => s.SetRadioWifiCredentialsAsync(new string('s', 33), "x"));
        await Assert.ThrowsAsync<MeshCoreException>(() => s.SetRadioWifiCredentialsAsync("net", new string('p', 64)));
        Assert.Equal(before, Sent(t).Count);
        await s.SetRadioWifiCredentialsAsync("Field Net", "");
        await s.SetRadioWifiEnabledAsync(false);
        await s.ClearRadioWifiAsync();
        Assert.Equal(new[] { "set wifi.ssid Field Net", "set wifi.pwd ", "set wifi.enabled 0", "set wifi.clear" },
            Sent(t)[^4..].Select(f => Encoding.UTF8.GetString(f, 1, f.Length - 1)));
        Assert.True(s.RadioWifiRebootPending);
    }

    [Fact]
    public async Task TimezoneSyncWritesOnlyWhenDifferent()
    {
        var current = "> -5";
        var (s, t) = await Started(cmd =>
        {
            if (cmd[0] != 0x42) return [];
            var line = Encoding.UTF8.GetString(cmd, 1, cmd.Length - 1);
            if (line.StartsWith("set tz.offset ")) current = "> " + line[14..];
            return [CliReply(line.StartsWith("get ") ? current : "OK")];
        });
        Assert.False(await s.SyncRadioTimezoneAsync(TimeSpan.FromHours(-5)));
        Assert.True(await s.SyncRadioTimezoneAsync(TimeSpan.FromHours(2)));
        Assert.Equal("set tz.offset 2", Encoding.UTF8.GetString(Sent(t)[^1][1..]));
        var before = Sent(t).Count;
        Assert.False(await s.SyncRadioTimezoneAsync(new TimeSpan(5, 30, 0)));
        Assert.Equal(before, Sent(t).Count);
        current = "??: tz.offset";
        Assert.False(await s.SyncRadioTimezoneAsync(TimeSpan.FromHours(1)));
        Assert.Equal(before + 1, Sent(t).Count);
    }

    // MARK: Session — power off (PowerOffExchange)

    [Fact]
    public async Task PowerOffReturnsFalseWhenTheBoardReplies()
    {
        var (s, t) = await Started(cmd => cmd[0] == 0x42 ? [[0x1D]] : []);
        Assert.False(await s.PowerOffAsync());
        Assert.Equal(PacketBuilder.RunCliCommand("poweroff"), Sent(t)[^1]);
    }

    [Fact]
    public async Task PowerOffReturnsTrueWhenTheReplyWindowExpires()
    {
        var (s, t) = await Started();
        Assert.True(await s.PowerOffAsync());
        Assert.Equal(PacketBuilder.RunCliCommand("poweroff"), Sent(t)[^1]);
    }

    [Fact]
    public async Task PowerOffKeepsARepliesBufferedBeforeTheWriteReturns()
    {
        // empty reply while the write is parked
        var (s, t) = await Started();
        t.HoldNextSend();
        var task = s.ExchangePowerOffAsync();
        await Eventually(() => t.SendIsWaiting);
        t.Inject([0x1D]);
        t.ReleaseHeldSend();
        Assert.Equal(new PowerOffExchangeResult(PowerOffOutcome.StillOn, ""), await task);

        // a CLI reply that arrives before a failed write
        t.HoldNextSend();
        task = s.ExchangePowerOffAsync();
        await Eventually(() => t.SendIsWaiting);
        t.Inject(CliReply("busy"));
        await Task.Delay(20);
        t.FailHeldSend();
        t.ReleaseHeldSend();
        Assert.Equal(new PowerOffExchangeResult(PowerOffOutcome.StillOn, "busy"), await task);

        // a firmware error that arrives before a failed write
        t.HoldNextSend();
        task = s.ExchangePowerOffAsync();
        await Eventually(() => t.SendIsWaiting);
        t.Inject(ErrFrame(1));
        await Task.Delay(20);
        t.FailHeldSend();
        t.ReleaseHeldSend();
        var rejected = await task;
        Assert.Equal((PowerOffOutcome.Rejected, (byte?)1), (rejected.Outcome, rejected.ErrorCode));
        Assert.True(rejected.WasSubmitted);
        Assert.False(rejected.PoweredOff);

        // nothing buffered and the write fails
        t.HoldNextSend();
        task = s.ExchangePowerOffAsync();
        await Eventually(() => t.SendIsWaiting);
        t.FailHeldSend();
        t.ReleaseHeldSend();
        Assert.Equal(PowerOffOutcome.SendFailed, (await task).Outcome);
        await s.StopAsync();
    }

    [Fact]
    public async Task PowerOffReportsAStoppedSessionInsteadOfSilence()
    {
        var (s, t) = await Started(cfg: Cfg with { PowerOffReplyWindow = TimeSpan.FromSeconds(10) });
        t.HoldNextSend();
        var task = s.ExchangePowerOffAsync();
        await Eventually(() => t.SendIsWaiting);
        t.ReleaseHeldSend();
        await s.StopAsync();
        Assert.Equal(PowerOffOutcome.Stopped, (await task).Outcome);
    }

    [Fact]
    public async Task PowerOffTreatsALinkDropAfterTheWriteAsPoweredOff()
    {
        var (s, t) = await Started(cfg: Cfg with { PowerOffReplyWindow = TimeSpan.FromSeconds(10) });
        t.HoldNextSend();
        var task = s.ExchangePowerOffAsync();
        await Eventually(() => t.SendIsWaiting);
        t.ReleaseHeldSend();
        t.SimulateDrop();
        var r = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(r.PoweredOff);
    }

    [Fact]
    public async Task PowerOffDoesNotSubmitWhenStoppedOrDisconnected()
    {
        var idle = new MeshCoreSession(new MockTransport(), Cfg);
        Assert.Equal(PowerOffOutcome.NotSubmittedNotRunning, (await idle.ExchangePowerOffAsync()).Outcome);

        var (s, t) = await Started();
        await t.DisconnectAsync();
        var r = await s.ExchangePowerOffAsync();
        Assert.False(r.WasSubmitted);
        // The receive loop may already have stopped the session (cancellation) or only the link is gone (not connected).
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => s.PowerOffAsync());
        Assert.True(ex is OperationCanceledException or MeshCoreException { Kind: MeshCoreErrorKind.NotConnected }, ex.ToString());
    }

    [Fact]
    public async Task PowerOffSendsNothingWhenCancelledWhileQueued()
    {
        var (s, t) = await Started();
        t.HoldNextSend();
        var blocker = s.RunCliCommandAsync("board");
        await Eventually(() => t.SendIsWaiting);
        var gate = new PowerOffSubmissionGate();
        var powerOff = s.ExchangePowerOffAsync(gate);
        await Eventually(() => s.RequestSerializer.WaiterCount == 1);
        Assert.True(gate.CancelIfUnsubmitted());
        t.ReleaseHeldSend();
        t.Inject(CliReply("OK"));
        await blocker;
        Assert.Equal(PowerOffOutcome.NotSubmittedCancelled, (await powerOff).Outcome);
        Assert.Equal(2, Sent(t).Count);
        Assert.False(gate.DidSubmit);

        // a cancellation token cancelled while queued behaves the same
        t.HoldNextSend();
        blocker = s.RunCliCommandAsync("board");
        await Eventually(() => t.SendIsWaiting);
        using var cts = new CancellationTokenSource();
        powerOff = s.ExchangePowerOffAsync(null, cts.Token);
        await Eventually(() => s.RequestSerializer.WaiterCount == 1);
        cts.Cancel();
        Assert.Equal(PowerOffOutcome.NotSubmittedCancelled, (await powerOff).Outcome);
        t.ReleaseHeldSend();
        t.Inject(CliReply("OK"));
        await blocker;
        Assert.DoesNotContain(Sent(t), f => f.SequenceEquals(PacketBuilder.RunCliCommand("poweroff")));
    }

    [Fact]
    public async Task PowerOffStillFinishesWhenCancelLosesTheSubmissionRace()
    {
        var (s, t) = await Started();
        t.HoldNextSend();
        var gate = new PowerOffSubmissionGate();
        var submitted = 0;
        gate.Submitted += () => Interlocked.Increment(ref submitted);
        var task = s.ExchangePowerOffAsync(gate);
        await Eventually(() => t.SendIsWaiting);
        Assert.False(gate.CancelIfUnsubmitted());
        Assert.True(gate.DidSubmit);
        Assert.NotNull(gate.SubmissionTime);
        Assert.Equal(1, submitted);
        t.Inject([0x1D]);
        t.ReleaseHeldSend();
        Assert.Equal(PowerOffOutcome.StillOn, (await task).Outcome);
    }

    // MARK: Session — remote CLI for contact radios (71f54118)

    [Fact]
    public async Task SendCommandWithCliCommandTextType()
    {
        var (s, t) = await Started(cmd => cmd[0] == 0x02 ? [MessageSentFrame([1, 2, 3, 4])] : [], firmware: 14);
        var sent = await s.SendCommandAsync(Dest, "get name", TextType.CliCommand, T);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sent.ExpectedAck);
        Assert.Equal(PacketBuilder.SendCommand(Dest, "get name", TextType.CliCommand, T), Sent(t)[^1]);
        Assert.Equal(0x03, Sent(t)[^1][1]);
        await s.SendCommandAsync(Dest, "ver", T);
        Assert.Equal(0x01, Sent(t)[^1][1]);
        Assert.Equal(MeshCoreErrorKind.InvalidInput, (await Assert.ThrowsAsync<MeshCoreException>(() => s.SendCommandAsync(Dest, "x", TextType.Plain))).Kind);
    }

    // MARK: Session — stopped sessions (38b1d44e / 0f5fbce0)

    [Fact]
    public async Task StoppedSessionCannotWriteOverASurvivingSharedTransport()
    {
        var (s, t) = await Started();
        await s.StopAsync(disconnectTransport: false);
        var before = Sent(t).Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RebootAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.GetContactsAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.GetChannelsAsync([0, 1]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RunCliCommandAsync("ver"));
        Assert.Equal(before, Sent(t).Count);
        Assert.True(t.IsConnected);
    }

    [Fact]
    public async Task StoppingDuringAWaitIsCancellationNotATimeout()
    {
        var (s, _) = await Started();
        var battery = s.GetBatteryAsync();
        var binary = s.RequestTelemetryAsync(Enumerable.Repeat((byte)0x31, 32).ToArray());
        await Task.Delay(50);
        await s.StopAsync(disconnectTransport: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => battery);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => binary);
    }

    // MARK: Session — contact cache (d519aa93, ContactCacheTests)

    private static byte[] Key(byte b) => Enumerable.Repeat(b, 32).ToArray();
    private static byte[] ContactFrame(byte key, string name, uint modified, byte code = 0x03) =>
        [code, .. ParserTests.ContactPayload(Key(key), name, lastMod: modified)];
    private static byte[] Start(uint n) => new ByteWriter(0x02).U32(n).ToArray();
    private static byte[] End(uint m) => new ByteWriter(0x04).U32(m).ToArray();
    private static List<string> Names(IEnumerable<MeshContact> c) => c.OrderBy(x => x.Id).Select(x => x.AdvertisedName).ToList();

    private sealed class ContactRadio
    {
        public readonly Queue<byte[][]> Replies = new();
        public readonly List<byte[]> Commands = new();
        public IEnumerable<byte[]> Handle(byte[] cmd)
        {
            if (cmd[0] == (byte)CommandCode.GetContacts) { lock (Commands) Commands.Add(cmd); return Replies.Dequeue(); }
            if (cmd[0] == (byte)CommandCode.RemoveContact) return Replies.Dequeue();
            return [];
        }
    }

    [Fact]
    public async Task EnsureContactsReturnsTheFullCacheAfterDeltas()
    {
        var radio = new ContactRadio();
        var (s, _) = await Started(radio.Handle);
        radio.Replies.Enqueue([Start(2), ContactFrame(1, "First", 10), ContactFrame(2, "Second", 10), End(10)]);
        Assert.Equal(new[] { "First", "Second" }, Names(await s.EnsureContactsAsync()));
        radio.Replies.Enqueue([Start(2), End(10)]);
        Assert.Equal(new[] { "First", "Second" }, Names(await s.EnsureContactsAsync(force: true)));
        Assert.Equal(PacketBuilder.GetContacts(DateTimeOffset.FromUnixTimeSeconds(10)), radio.Commands[^1]);
        radio.Replies.Enqueue([Start(2), ContactFrame(2, "Updated", 20), End(20)]);
        Assert.Equal(new[] { "First", "Updated" }, Names(await s.EnsureContactsAsync(force: true)));
        var count = radio.Commands.Count;
        Assert.Equal(new[] { "First", "Updated" }, Names(await s.EnsureContactsAsync()));
        Assert.Equal(count, radio.Commands.Count);
    }

    [Fact]
    public async Task CompleteFullFetchPrunesWithoutClearingPendingContacts()
    {
        var radio = new ContactRadio();
        var (s, _) = await Started(radio.Handle);
        radio.Replies.Enqueue([Start(2), ContactFrame(1, "First", 10), ContactFrame(2, "Second", 10), End(10)]);
        await s.GetContactsAsync();
        radio.Replies.Enqueue([Start(1), ContactFrame(2, "Second", 10), ContactFrame(1, "First", 10, 0x8A), End(10)]);
        Assert.Equal(new[] { "Second" }, Names((await s.GetContactsAsync()).Contacts));
        Assert.Equal(new[] { "Second" }, Names(s.Contacts.Contacts));
        Assert.Equal(new[] { "First" }, Names(s.Contacts.PendingContacts));
        Assert.Null(s.Contacts.GetByKeyPrefix(Key(1)));
        radio.Replies.Enqueue([Start(0), End(10)]);
        await s.GetContactsAsync();
        Assert.Empty(s.Contacts.Contacts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IncompleteFullFetchCannotPruneTheCache(int completion)
    {
        var radio = new ContactRadio();
        var (s, _) = await Started(radio.Handle);
        radio.Replies.Enqueue([Start(2), ContactFrame(1, "First", 10), ContactFrame(2, "Second", 10), End(10)]);
        await s.GetContactsAsync();
        var frames = new List<byte[]>();
        if (completion != 2) frames.Add(Start(2));
        frames.Add(ContactFrame(2, "Second", 10));
        frames.Add(completion == 0 ? ErrFrame(3) : End(10));
        radio.Replies.Enqueue(frames.ToArray());
        if (completion == 0)
            Assert.Equal(MeshCoreErrorKind.DeviceError, (await Assert.ThrowsAsync<MeshCoreException>(() => s.GetContactsAsync())).Kind);
        else
        {
            Assert.Equal(new[] { "Second" }, Names((await s.GetContactsAsync()).Contacts));
            Assert.True(s.Contacts.NeedsRefresh);
            Assert.Null(s.Contacts.LastModified);
        }
        Assert.Equal(new[] { "First", "Second" }, Names(s.Contacts.Contacts));
        if (completion != 0)
        {
            radio.Replies.Enqueue([Start(2), ContactFrame(1, "First", 10), ContactFrame(2, "Second", 10), End(10)]);
            Assert.Equal(new[] { "First", "Second" }, Names(await s.EnsureContactsAsync()));
            Assert.Equal(PacketBuilder.GetContacts(), radio.Commands[^1]);
        }
    }

    [Fact]
    public async Task SuccessfulRemovalClearsTheCacheAndFailedRemovalKeepsIt()
    {
        var radio = new ContactRadio();
        var (s, _) = await Started(radio.Handle);
        radio.Replies.Enqueue([Start(1), ContactFrame(1, "First", 10), End(10)]);
        await s.GetContactsAsync();
        radio.Replies.Enqueue([ErrFrame(2)]);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<MeshCoreException>(() => s.RemoveContactAsync(Key(1)))).FirmwareError);
        Assert.NotNull(s.Contacts.GetByKeyPrefix(Key(1)));
        radio.Replies.Enqueue([OkFrame]);
        await s.RemoveContactAsync(Key(1));
        Assert.Empty(s.Contacts.Contacts);
        Assert.Null(s.Contacts.GetByName("First"));
    }

    // MARK: Session — other params intent (b8a47d40, 68be79c4)

    private sealed class OtherParamsRadio
    {
        public OtherParamsConfig Current = new();
        public OtherParamsConfig? LastWritten;
        public readonly List<byte[]> Writes = new();

        public IEnumerable<byte[]> Handle(byte[] cmd)
        {
            if (cmd[0] == (byte)CommandCode.SetOtherParams)
            {
                lock (Writes) Writes.Add(cmd);
                Current = new OtherParamsConfig
                {
                    ManualAddContacts = cmd[1] != 0,
                    TelemetryModeBase = (byte)(cmd[2] & 3),
                    TelemetryModeLocation = (byte)((cmd[2] >> 2) & 3),
                    TelemetryModeEnvironment = (byte)((cmd[2] >> 4) & 3),
                    AdvertisementLocationPolicy = cmd[3],
                    MultiAcks = cmd.Length >= 5 ? cmd[4] : Current.MultiAcks,
                };
                LastWritten = Current;
                return [OkFrame];
            }
            return [];
        }

        public byte[] SelfInfo() => new ByteWriter((byte)ResponseCode.SelfInfo).U8(1).I8(22).I8(22).Raw(Key(1)).I32(0).I32(0)
            .U8(Current.MultiAcks).U8(Current.AdvertisementLocationPolicy)
            .U8((byte)((Current.TelemetryModeEnvironment << 4) | (Current.TelemetryModeLocation << 2) | Current.TelemetryModeBase))
            .U8(Current.ManualAddContacts ? (byte)1 : (byte)0).U32(869_525).U32(250_000).U8(11).U8(5).ToArray();
    }

    private static async Task<(MeshCoreSession, MockTransport, OtherParamsRadio)> StartedOtherParams()
    {
        var radio = new OtherParamsRadio();
        var t = new MockTransport();
        t.OnSend = cmd => cmd[0] == (byte)CommandCode.AppStart ? [radio.SelfInfo()] : radio.Handle(cmd);
        var s = new MeshCoreSession(t, Cfg);
        await s.StartAsync();
        return (s, t, radio);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullAndGranularSettersPreserveAcknowledgedConfiguration(bool concurrent)
    {
        var (s, _, radio) = await StartedOtherParams();
        var full = s.SetOtherParamsAsync(true, 2, 1, 3, 1, 4);
        if (!concurrent) await full;
        var granular = s.SetMultiAcksAsync(7);
        await Task.WhenAll(full, granular);
        var final = radio.LastWritten!;
        Assert.Equal((true, 2, 1, 3, 1, 7), (final.ManualAddContacts, final.TelemetryModeEnvironment, final.TelemetryModeLocation,
            final.TelemetryModeBase, final.AdvertisementLocationPolicy, final.MultiAcks));
    }

    [Fact]
    public async Task OmittingMultiAcksPreservesTheRadioValueAndRefreshesTheCache()
    {
        var (s, _, radio) = await StartedOtherParams();
        await s.SetMultiAcksAsync(7);
        await s.SetOtherParamsAsync(true, 0, 0, 0, 0);
        Assert.True(s.SelfInfo!.ManualAddContacts);
        Assert.Equal(7, s.SelfInfo.MultiAcks);
        Assert.Equal(4, radio.Writes[^1].Length);
    }

    [Fact]
    public async Task ConcurrentGranularSettersAndIntentsDoNotRevertEachOther()
    {
        var (s, _, radio) = await StartedOtherParams();
        await Task.WhenAll(s.SetManualAddContactsAsync(true), s.SetMultiAcksAsync(7));
        Assert.True(radio.LastWritten!.ManualAddContacts);
        Assert.Equal(7, radio.LastWritten.MultiAcks);

        await Task.WhenAll(s.ApplyOtherParamsAsync(new OtherParamsIntent { TelemetryModeBase = 0xFE }), s.SetAdvertisementLocationPolicyAsync(1));
        Assert.Equal((true, 7, 2, 1), (radio.LastWritten.ManualAddContacts, radio.LastWritten.MultiAcks, radio.LastWritten.TelemetryModeBase,
            radio.LastWritten.AdvertisementLocationPolicy));
    }

    [Fact]
    public async Task AnEmptyOrUnchangedIntentSendsNoCommand()
    {
        var (s, t, _) = await StartedOtherParams();
        var before = Sent(t).Count;
        await s.ApplyOtherParamsAsync(new OtherParamsIntent());
        await s.ApplyOtherParamsAsync(new OtherParamsIntent { AdvertisementLocationPolicy = 0, MultiAcks = 0 });
        Assert.Equal(before, Sent(t).Count);
    }

    [Fact]
    public void IntentNormalizesTelemetryBits()
    {
        var i = new OtherParamsIntent { TelemetryModeEnvironment = 0xFF, AdvertisementLocationPolicy = 9 };
        Assert.False(i.IsEmpty);
        Assert.True(new OtherParamsIntent().IsEmpty);
        var cfg = new OtherParamsConfig { MultiAcks = 2 };
        var applied = i.AppliedTo(cfg);
        Assert.Equal((3, 9, 2), (applied.TelemetryModeEnvironment, applied.AdvertisementLocationPolicy, applied.MultiAcks));
        Assert.Equal(0, cfg.TelemetryModeEnvironment); // the input record is not modified
        Assert.Null(i.Normalized().TelemetryModeBase);
    }

    // MARK: Session — binary replies (c3af6bad, ad3ce460, 6353667d)

    [Fact]
    public async Task MmaRequestsRelativeSecondsAndParsesEntriesAfterTheServerClock()
    {
        var tag = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var (s, t) = await Started(cmd => cmd[0] == (byte)CommandCode.BinaryRequest
            ? [MessageSentFrame(tag), BinaryResponseFrame(tag, [0x00, 0xF1, 0x53, 0x65, 0x01, 0x67, 0x00, 0xC8, 0x01, 0x2C, 0x00, 0xFA])]
            : []);
        var key = Key(0x42);
        var before = DateTimeOffset.UtcNow;
        var r = await s.RequestMmaAsync(key, before.AddSeconds(-3600), before.AddSeconds(-60));
        var after = DateTimeOffset.UtcNow;
        var sent = Sent(t)[^1];
        Assert.Equal(44, sent.Length);
        byte[] expectedPrefix = [(byte)CommandCode.BinaryRequest, .. key, (byte)BinaryRequestType.Mma];
        Assert.Equal(expectedPrefix, sent[..34]);
        Assert.InRange(sent.ReadUInt32LE(34), 3600u, (uint)(after - before.AddSeconds(-3600)).TotalSeconds);
        Assert.InRange(sent.ReadUInt32LE(38), 60u, (uint)(after - before.AddSeconds(-60)).TotalSeconds);
        Assert.Equal(new byte[] { 0, 0 }, sent[^2..]);
        Assert.Equal(key.Prefix(6), r.PublicKeyPrefix);
        Assert.Equal(tag, r.Tag);
        Assert.Equal(new[] { new MmaEntry(1, "Temperature", 20, 30, 25) }, r.Data);

        var payload = MeshCoreSession.BuildMmaRequestPayload(DateTimeOffset.MinValue, DateTimeOffset.UtcNow.AddSeconds(60), DateTimeOffset.UtcNow);
        Assert.Equal(uint.MaxValue, payload.ReadUInt32LE(0));
        Assert.Equal(0u, payload.ReadUInt32LE(4));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task MmaRequiresACompleteServerClock(int bodySize)
    {
        var tag = new byte[] { 1, 2, 3, 4 };
        var (s, _) = await Started(cmd => cmd[0] == (byte)CommandCode.BinaryRequest ? [MessageSentFrame(tag), BinaryResponseFrame(tag, new byte[bodySize])] : []);
        var task = s.RequestMmaAsync(Key(0x42), DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);
        if (bodySize == 4) Assert.Empty((await task).Data);
        else Assert.Equal(MeshCoreErrorKind.ParseError, (await Assert.ThrowsAsync<MeshCoreException>(() => task)).Kind);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("", 8)]
    [InlineData("  KD7ABC\nch 31\n", 0)]
    [InlineData("  KD7ABC\nch 31\n", 8)]
    public async Task OwnerInfoStopsAtTheNullTerminatorAndPreservesText(string ownerInfo, int padding)
    {
        var tag = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        byte[] body = [.. Encoding.UTF8.GetBytes($"v1.18.0\nRepeater AB\n{ownerInfo}"), .. new byte[padding]];
        var (s, _) = await Started(cmd => cmd[0] == (byte)CommandCode.BinaryRequest ? [MessageSentFrame(tag), BinaryResponseFrame(tag, body)] : []);
        var info = await s.RequestOwnerInfoAsync(Key(0x31));
        Assert.Equal(("v1.18.0", "Repeater AB", ownerInfo), (info.FirmwareVersion, info.NodeName, info.OwnerInfo));
    }

    [Fact]
    public async Task BinaryResponseMatchesAnyTagFromTheSameExchangeButNotForeignTags()
    {
        var (s, t) = await Started(cfg: Cfg with { BinaryRequestRetransmitInterval = TimeSpan.FromMilliseconds(100) });
        var task = s.RequestTelemetryAsync(Key(0x31));
        await Eventually(() => Sent(t).Count == 2);
        t.Inject(MessageSentFrame([0x11, 0x22, 0x33, 0x44], 50));
        await Eventually(() => Sent(t).Count >= 3);
        t.Inject(MessageSentFrame([0x55, 0x66, 0x77, 0x88], 50));
        t.Inject(BinaryResponseFrame([0x99, 0x88, 0x77, 0x66], [0xBB]));
        t.Inject(BinaryResponseFrame([0x11, 0x22, 0x33, 0x44], [0xA1]));
        Assert.Equal(new byte[] { 0xA1 }, (await task).RawData);
    }

    // MARK: Session — workflow ordering (68be79c4)

    [Fact]
    public async Task RetryObservesAnAcknowledgementReceivedBeforeTheSendCompletes()
    {
        var (s, t) = await Started();
        t.HoldNextSend();
        var sending = s.SendMessageWithRetryAsync(Key(2), "Message", T, maxAttempts: 1, timeout: TimeSpan.FromSeconds(1));
        await Eventually(() => t.SendIsWaiting);
        t.Inject([0x06, 0, 1, 2, 3, 4, 50, 0, 0, 0]);
        t.Inject([0x82, 1, 2, 3, 4, 50, 0, 0, 0]);
        await Task.Delay(50);
        t.ReleaseHeldSend();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, (await sending)!.ExpectedAck);
        Assert.Equal(2, Sent(t).Count);
    }

    [Fact]
    public async Task ConcurrentSigningWorkflowsEachSignTheirOwnInput()
    {
        var buffers = new List<byte[]>();
        List<byte>? current = null;
        var (s, _) = await Started(cmd =>
        {
            switch ((CommandCode)cmd[0])
            {
                case CommandCode.SignStart: current = []; return [new ByteWriter(0x13, 0).U32(8192).ToArray()];
                case CommandCode.SignData: current!.AddRange(cmd[1..]); return [OkFrame];
                case CommandCode.SignFinish:
                    if (current is null) return [ErrFrame((byte)ErrorCode.BadState)];
                    var input = current.ToArray();
                    lock (buffers) buffers.Add(input);
                    current = null;
                    return [[0x14, .. input]]; // "signature" echoes the input
                default: return [];
            }
        });
        var first = s.SignAsync("AAAA"u8.ToArray(), chunkSize: 2);
        var second = s.SignAsync("BBBB"u8.ToArray(), chunkSize: 2);
        Assert.Equal("AAAA"u8.ToArray(), await first);
        Assert.Equal("BBBB"u8.ToArray(), await second);
        Assert.Equal(2, buffers.Count);
    }

    [Fact]
    public async Task CancelledFloodRegionDiscoveryStillRestoresFloodRouting()
    {
        var (s, t) = await Started();
        var contact = new MeshContact(Key(0x42), ContactType.Repeater, 2, ContactFlags.None, 0xFF, [], "Rpt", T, 0, 0, T);
        using var cts = new CancellationTokenSource();
        var regions = s.RequestRegionsAsync(contact, cts.Token);
        await Eventually(() => Sent(t).Count == 2);
        Assert.Equal(0, Sent(t)[1][35]); // temporary zero-hop direct
        cts.Cancel();
        await Eventually(() => Sent(t)[^1][0] == (byte)CommandCode.ResetPath);
        Assert.Equal(Key(0x42), Sent(t)[^1][1..]);
        t.Inject(OkFrame); // the restore's OK
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => regions);
    }

    // MARK: Session — channel sends with a resolved flood scope (ae5fa6ec)

    private static byte[] ScopeFrame(string region) => PacketBuilder.SetFloodScope(new FloodScope.Region(region).ScopeKey());

    [Fact]
    public async Task ConcurrentChannelsAndScopeChangesCannotSplitAScopeAndSendPair()
    {
        var (s, t) = await Started();
        var first = s.SendChannelMessageAsync(1, "first", T, new ResolvedFloodScope.Scope(new FloodScope.Region("Alpha")));
        await Eventually(() => Sent(t).Count == 2);
        Assert.Equal(ScopeFrame("Alpha"), Sent(t)[1]);
        var second = s.SendChannelMessageAsync(2, "second", T, new ResolvedFloodScope.Scope(new FloodScope.Region("Beta")));
        var selection = s.SetFloodScopeAsync(new FloodScope.Region("Gamma"));
        await Eventually(() => s.RequestSerializer.WaiterCount == 2);
        Assert.Equal(2, Sent(t).Count);
        t.Inject(OkFrame);
        await Eventually(() => Sent(t).Count == 3);
        Assert.Equal(PacketBuilder.SendChannelMessage(1, "first", T), Sent(t)[2]);
        t.Inject(OkFrame);
        await first;
        for (var n = 4; n <= 6; n++)
        {
            await Eventually(() => Sent(t).Count >= n);
            t.Inject(OkFrame);
        }
        await Task.WhenAll(second, selection);
        var frames = Sent(t).Skip(1).ToList();
        var beta = frames.FindIndex(f => f.SequenceEquals(ScopeFrame("Beta")));
        Assert.Equal(PacketBuilder.SendChannelMessage(2, "second", T), frames[beta + 1]);
        Assert.Equal(2, frames.Count(f => f[0] == (byte)CommandCode.SendChannelMessage));
    }

    [Fact]
    public async Task ScopeRejectionPreventsTheMessageAndReleasesTheNextCommand()
    {
        var (s, t) = await Started();
        var send = s.SendChannelMessageAsync(1, "hello", T, new ResolvedFloodScope.Scope(new FloodScope.Region("Alpha")));
        await Eventually(() => Sent(t).Count == 2);
        var selection = s.SetFloodScopeAsync(new FloodScope.Region("Gamma"));
        await Eventually(() => s.RequestSerializer.WaiterCount == 1);
        t.Inject(ErrFrame(42));
        Assert.Equal((byte?)42, (await Assert.ThrowsAsync<MeshCoreException>(() => send)).DeviceCode);
        await Eventually(() => Sent(t).Count == 3);
        Assert.Equal(ScopeFrame("Gamma"), Sent(t)[2]);
        t.Inject(OkFrame);
        await selection;
        Assert.DoesNotContain(Sent(t), f => f[0] == (byte)CommandCode.SendChannelMessage);
    }

    [Fact]
    public async Task CancellingWhileQueuedWritesNeitherScopeNorMessage()
    {
        var (s, t) = await Started();
        var selection = s.SetFloodScopeAsync(new FloodScope.Region("Gamma"));
        await Eventually(() => Sent(t).Count == 2);
        using var cts = new CancellationTokenSource();
        var send = s.SendChannelMessageAsync(1, "cancelled", T, new ResolvedFloodScope.Scope(new FloodScope.Region("Alpha")), cts.Token);
        await Eventually(() => s.RequestSerializer.WaiterCount == 1);
        cts.Cancel();
        t.Inject(OkFrame);
        await selection;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Equal(2, Sent(t).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDrainsItsOutstandingAckBeforeReleasingAnotherCommand(bool afterScopeAck)
    {
        var (s, t) = await Started();
        using var cts = new CancellationTokenSource();
        var send = s.SendChannelMessageAsync(1, "cancelled", T, new ResolvedFloodScope.Scope(new FloodScope.Region("Alpha")), cts.Token);
        await Eventually(() => Sent(t).Count == 2);
        if (afterScopeAck)
        {
            t.Inject(OkFrame);
            await Eventually(() => Sent(t).Count == 3);
        }
        var outstanding = afterScopeAck ? 3 : 2;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        var following = s.SetFloodScopeAsync(new FloodScope.Region("Gamma"));
        await Eventually(() => s.RequestSerializer.WaiterCount == 1);
        Assert.Equal(outstanding, Sent(t).Count);
        t.Inject(OkFrame); // the cancelled exchange's own ACK
        await Eventually(() => Sent(t).Count == outstanding + 1);
        Assert.Equal(ScopeFrame("Gamma"), Sent(t)[outstanding]);
        t.Inject(OkFrame);
        await following;
        Assert.Equal(afterScopeAck ? 1 : 0, Sent(t).Count(f => f[0] == (byte)CommandCode.SendChannelMessage));
    }

    [Fact]
    public async Task UnscopedChannelSendNeedsFirmware12()
    {
        var (s, t) = await Started(cmd => [OkFrame], firmware: 11);
        Assert.Equal(MeshCoreErrorKind.Unsupported,
            (await Assert.ThrowsAsync<MeshCoreException>(() => s.SendChannelMessageAsync(0, "x", T, new ResolvedFloodScope.Unscoped()))).Kind);
        s.HandleReceived(ParserTests.DeviceInfoFrame(12));
        await s.SendChannelMessageAsync(0, "x", T, new ResolvedFloodScope.Unscoped());
        Assert.Equal(PacketBuilder.SetFloodScopeUnscoped(), Sent(t)[^2]);
    }

    // MARK: Transport — streams finish on connection loss (45313cad)

    private sealed class DeadWriteStream : Stream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _closed.Task.WaitAsync(Timeout.InfiniteTimeSpan, CancellationToken.None); // a half-open link: the read never ends by itself
            throw new ObjectDisposedException("stream");
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => throw new IOException("Broken pipe");
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Broken pipe");
        public void CloseLink() => _closed.TrySetResult();
    }

    private sealed class DeadWriteTransport : StreamTransportBase
    {
        public DeadWriteStream Link { get; } = new();
        public override string Description => "dead";
        protected override Task<Stream> OpenAsync(CancellationToken ct) => Task.FromResult<Stream>(Link);
        protected override void Close() => Link.CloseLink();
    }

    [Fact]
    public async Task AFailedWriteFinishesTheReceivedStreamAndNotifiesOnce()
    {
        var t = new DeadWriteTransport();
        var notified = 0;
        t.Disconnected += _ => Interlocked.Increment(ref notified);
        await t.ConnectAsync();
        var reader = t.Received;
        await Assert.ThrowsAsync<TransportException>(() => t.SendAsync([0x14]));
        Assert.False(t.IsConnected);
        Assert.True(reader.Completion.Wait(TimeSpan.FromSeconds(2)));
        await Task.Delay(100);
        Assert.Equal(1, notified);
        Assert.NotNull(t.DisconnectedAt);
        await t.DisconnectAsync();
    }

    [Fact]
    public void SerializerCountsWaiters()
    {
        var gate = new RequestResponseSerializer();
        gate.AcquireAsync().Wait();
        Assert.True(gate.IsHeld);
        var waiting = gate.AcquireAsync();
        Assert.Equal(1, gate.WaiterCount);
        gate.Release();
        Assert.True(waiting.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, gate.WaiterCount);
        gate.Release();
        Assert.False(gate.IsHeld);
    }
}

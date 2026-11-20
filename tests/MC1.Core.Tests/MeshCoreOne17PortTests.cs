using MC1.Core.Mecp;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;
using Xunit;

namespace MC1.Core.Tests;

/// <summary>MeshCore One 1.7 features ported to Windows (MeshCore-X 1.2.0).</summary>
public class MeshCoreOne17PortTests
{
    // MARK: DM send budget

    [Fact]
    public void BudgetDefaultsToFourOnPathAndFourByFlood()
    {
        var s = new AppSettings();
        var b = DmSendBudget.From(s);
        Assert.Equal(4, b.DirectAttempts);
        Assert.Equal(4, b.FloodAttempts);
        Assert.Equal(8, b.TotalAttempts);
        Assert.True(s.RemoteFloodFallback);
    }

    [Fact]
    public void BudgetClampsAndResetsThePathOnceAfterTheDirectAttempts()
    {
        var b = new DmSendBudget(0, 9);
        Assert.Equal(1, b.DirectAttempts);
        Assert.Equal(4, b.FloodAttempts);
        Assert.False(b.ResetsPathBefore(0));
        Assert.True(b.ResetsPathBefore(1));
        Assert.False(b.ResetsPathBefore(2));

        var off = new DmSendBudget(8, -1);
        Assert.Equal(4, off.DirectAttempts);
        Assert.Equal(0, off.FloodAttempts);
        Assert.Equal(4, off.TotalAttempts);
        Assert.False(off.FallsBackToFlood);
        Assert.False(off.ResetsPathBefore(4));
    }

    [Fact]
    public void TurningFallbackOffStoresZeroAndOnRestoresTheDefaultOnlyFromZero()
    {
        Assert.Equal(0, DmSendBudget.FloodAttemptsFor(false, 3));
        Assert.Equal(4, DmSendBudget.FloodAttemptsFor(true, 0));
        Assert.Equal(2, DmSendBudget.FloodAttemptsFor(true, 2));
        Assert.Equal(3, DmSendBudget.MenuFloodAttempts(3));
        Assert.Equal(4, DmSendBudget.MenuFloodAttempts(0));
        Assert.Equal(4, DmSendBudget.MenuFloodAttempts(9));
    }

    // MARK: Text rules

    private static TextRulesPreference Rules(params TextRule[] rules) => new() { Rules = [.. rules] };

    [Fact]
    public void ContainsMatchesInsideAWordIgnoringCase()
    {
        var p = Rules(new TextRule { Phrase = "spam", LookIn = TextRuleLookIn.Message });
        Assert.NotNull(p.FirstMatch("Buy SPAMMY things", "Bob"));
        Assert.Null(p.FirstMatch("hello", "Spammer"));
    }

    [Fact]
    public void WholeFieldMatchesOnlyTheEntireField()
    {
        var p = Rules(new TextRule { Phrase = "bot", LookIn = TextRuleLookIn.Sender, Match = TextRuleMatch.WholeField });
        Assert.NotNull(p.FirstMatch("anything", "BOT"));
        Assert.Null(p.FirstMatch("bot", "robot"));
        var both = Rules(new TextRule { Phrase = "ping", LookIn = TextRuleLookIn.MessageOrSender, Match = TextRuleMatch.WholeField });
        Assert.NotNull(both.FirstMatch("Ping", "Alice"));
        Assert.NotNull(both.FirstMatch("hi", "ping"));
        Assert.Null(both.FirstMatch("ping pong", "Alice"));
    }

    [Fact]
    public void PublicAndHashtagChannelsUseTheRulesByDefault()
    {
        var p = new TextRulesPreference();
        Assert.True(p.AppliesTo(TextRuleKind.PublicChannel, "x"));
        Assert.True(p.AppliesTo(TextRuleKind.HashtagChannel, "x"));
        Assert.False(p.AppliesTo(TextRuleKind.PrivateChannel, "x"));
        Assert.False(p.AppliesTo(TextRuleKind.DirectMessage, "x"));
        Assert.False(p.AppliesTo(TextRuleKind.Room, "x"));
        p.Selections[TextRuleKind.DirectMessage] = new TextRuleSelection { Mode = TextRuleSelectionMode.Fixed, Chats = ["a"] };
        Assert.True(p.AppliesTo(TextRuleKind.DirectMessage, "a"));
        Assert.False(p.AppliesTo(TextRuleKind.DirectMessage, "b"));
    }

    [Fact]
    public void RulesSurviveASettingsRoundTrip()
    {
        var s = new AppSettings();
        s.TextRules.Rules.Add(new TextRule { Phrase = "x", LookIn = TextRuleLookIn.Sender, Match = TextRuleMatch.WholeField });
        s.TextRules.Selections[TextRuleKind.Room] = new TextRuleSelection { Mode = TextRuleSelectionMode.Fixed, Chats = ["r1"] };
        var json = System.Text.Json.JsonSerializer.Serialize(s, SettingsStore.JsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
        Assert.Equal("x", back.TextRules.Rules.Single().Phrase);
        Assert.Equal(TextRuleLookIn.Sender, back.TextRules.Rules[0].LookIn);
        Assert.True(back.TextRules.AppliesTo(TextRuleKind.Room, "r1"));
    }

    [Fact]
    public async Task AMatchingChannelMessageIsHiddenAndListedAsRecentlyBlocked()
    {
        await using var f = new AppFixture();
        f.App.TextRules.AddRule("repeater", TextRuleLookIn.Message, TextRuleMatch.Contains);
        await f.ConnectAsync();
        var app = f.App;
        // #test's message still arrives; Public's "…new repeater?" is hidden.
        Assert.True(await AppFixture.Eventually(() => app.Db.GetChannelMessages(app.RadioId!, 1).Count == 1 && app.TextRules.RecentlyBlockedCount() == 1));
        Assert.Empty(app.Db.GetChannelMessages(app.RadioId!, 0));
        var blocked = app.TextRules.RecentlyBlocked().Single();
        Assert.Equal("Bob", blocked.Sender);
        Assert.Equal("Public", blocked.ConversationName);
        Assert.Equal("repeater", blocked.RulePhrase);
        app.TextRules.ClearRecentlyBlocked();
        Assert.Equal(0, app.TextRules.RecentlyBlockedCount());
    }

    [Fact]
    public void RecentlyBlockedKeepsOnlyTheNewestAndSkipsRepeats()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc1rb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Data.AppDatabase(Path.Combine(root, "db.sqlite"));
            for (var i = 0; i < 5; i++)
                Assert.True(db.AddRecentlyBlocked(new RecentlyBlockedRecord { RadioId = "r", ConversationKey = "c", Body = $"m{i}", ArrivedAt = 1000 + i, DedupKey = $"d{i}" }, limit: 3));
            Assert.False(db.AddRecentlyBlocked(new RecentlyBlockedRecord { RadioId = "r", ConversationKey = "c", Body = "again", ArrivedAt = 2000, DedupKey = "d4" }, limit: 3));
            Assert.Equal(["m4", "m3", "m2"], db.GetRecentlyBlocked().Select(r => r.Body).ToArray());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    // MARK: MECP

    [Fact]
    public void MecpDecodesSeverityCodesAndTail()
    {
        var m = MecpDecode.Decode("MECP/0/M01 M02 T04 3pax 37.7849,-122.4094 @1430 @en")!;
        Assert.Equal(MecpSeverity.Mayday, m.Severity);
        Assert.Equal(["M01", "M02", "T04"], m.Codes);
        Assert.Equal(3, m.PersonCount);
        Assert.Equal("1430", m.TimestampHhmm);
        Assert.Equal("37.7849,-122.4094 @en", m.DisplayTail);
        Assert.Null(MecpDecode.Decode("MECP/5/M01"));
        Assert.Null(MecpDecode.Decode("hello"));
        Assert.Equal(15, MecpDecode.Decode("MECP/1/R03 15")!.Eta);
        Assert.Null(MecpDecode.Decode("MECP/1/M01 15")!.Eta);
    }

    [Fact]
    public void MecpReadsAsPhrases()
    {
        var reading = MecpCatalog.Reading("MECP/0/M01 M02 3pax @1430", "en")!;
        var lines = reading.Split('\n');
        Assert.Equal("MAYDAY", lines[0]);
        Assert.Equal("Injury", lines[1]);
        Assert.Equal("Unconscious person", lines[2]);
        Assert.Equal("3 people", lines[3]);
        Assert.Equal("14:30 UTC", lines[4]);
        Assert.Contains("15", MecpCatalog.Reading("MECP/1/R03 15", "en")!);
        Assert.Contains("Z99: Unknown code", MecpCatalog.Reading("MECP/3/Z99", "en")!);
        Assert.Equal("MECP/5/M01", MecpCatalog.DisplayText("MECP/5/M01"));
        Assert.Equal("MAYDAY\nVerletzung", MecpCatalog.Reading("MECP/0/M01", "de"));
        // A language without a pack reads in English.
        Assert.Equal("MAYDAY\nInjury", MecpCatalog.Reading("MECP/0/M01", "ko"));
    }

    // MARK: Sensor settings

    [Fact]
    public void SensorListPagesParse()
    {
        var p1 = SensorSettingsParser.List("4 vars\ngps=1\ngps.interval=900\n... next:2", 0);
        Assert.Equal(4, p1.Total);
        Assert.Equal(2, p1.Next);
        Assert.Equal("900", p1.Settings[1].Value);
        var p2 = SensorSettingsParser.List("4 vars\na=\nb=x=y", 2);
        Assert.Null(p2.Next);
        Assert.Equal("", p2.Settings[0].Value);
        Assert.Equal("x=y", p2.Settings[1].Value);
        Assert.Empty(SensorSettingsParser.List("no custom var", 0).Settings);
        Assert.Throws<SensorSettingsException>(() => SensorSettingsParser.List("3 vars\na=1", 0));
        Assert.Equal("5", SensorSettingsParser.Value("> 5"));
        Assert.Null(SensorSettingsParser.Value("null"));
        Assert.Throws<SensorSettingRejectedException>(() => SensorSettingsParser.Accepted("can't find custom var"));
        Assert.Null(new SensorSetting("k", "v").SetCommand("a b"));
        Assert.Equal("sensor set k 7", new SensorSetting("k", "v").SetCommand("7"));
    }

    [Fact]
    public async Task SensorSettingsCanBeListedChangedAndVerified()
    {
        await using var f = new AppFixture();
        await f.ConnectAsync();
        var app = f.App;
        var sensor = app.Contacts.GetAll().First(c => c.ContactType == ContactType.Sensor);
        var login = await app.Remote.LoginAsync(sensor, "password", remember: false);
        Assert.True(login.IsAdmin);
        var settings = await app.Remote.SensorSettingsAsync(sensor);
        Assert.Equal(["gps", "gps.interval", "temp.offset", "report.interval"], settings.Select(s => s.Key).ToArray());
        var saved = await app.Remote.SetSensorSettingAsync(sensor, settings[1], "600");
        Assert.Equal("600", saved.Value);
        Assert.Equal("600", (await app.Remote.SensorSettingsAsync(sensor))[1].Value);
    }

    // MARK: V-contact, units

    [Fact]
    public void VContactKeyIsDerivedFromTheRadioKey()
    {
        var self = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var v = VContactIdentity.PublicKeyFor(self)!;
        Assert.Equal(32, v.Length);
        Assert.True(VContactIdentity.IsVContact(v, self));
        Assert.True(VContactIdentity.IsVContact(Convert.ToHexString(v).ToLowerInvariant(), [self]));
        Assert.False(VContactIdentity.IsVContact(self, self));
        Assert.False(VContactIdentity.IsVContact("zz", [self]));
    }

    [Fact]
    public void TelemetryFollowsTheUnitsSetting()
    {
        var temp = new LppDataPoint(1, LppSensorType.Temperature, new LppValue.Float(20));
        var pressure = new LppDataPoint(1, LppSensorType.Barometer, new LppValue.Float(1013.25));
        Assert.Contains("°C", TelemetryFormat.Value(temp, metric: true));
        Assert.Contains("68", TelemetryFormat.Value(temp, metric: false));
        Assert.Contains("°F", TelemetryFormat.Value(temp, metric: false));
        Assert.Contains("hPa", TelemetryFormat.Value(pressure, metric: true));
        Assert.Contains("inHg", TelemetryFormat.Value(pressure, metric: false));
    }
}

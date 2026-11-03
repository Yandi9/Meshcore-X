using MC1.Core.Services;
using MeshCore;
using Xunit;

namespace MC1.Core.Tests;

/// <summary>Restoring an iPhone backup: the radio's preset and frequency, pins, the area, and no remote-CLI grants.</summary>
public partial class IntegrationTests
{
    private static Mc1Backup PhoneBackup(Func<string, string>? change = null)
    {
        var json = PhoneBackupJson
            .Replace("\"maxTxPower\":22,", "\"maxTxPower\":22,\"appliedRadioPresetID\":\"us-ca\",")
            .Replace("\"flags\":0,", "\"flags\":16,") // the phone's radio let Dave run CLI commands on it
            .Replace("\"isFavorite\":true,", "\"isFavorite\":true,\"isPinned\":true,")
            .Replace("\"isFavorite\":false,\"floodScopeModeRawValue\"", "\"isPinned\":true,\"floodScopeModeRawValue\"")
            .Replace("\"showInlineImages\":false}", "\"showInlineImages\":false,\"regionSelection\":{\"countryCode\":\"CA\",\"administrativeAreaCode\":\"CA-BC\",\"source\":\"manual\"}}");
        return Mc1Backup.Read(System.Text.Encoding.UTF8.GetBytes(change?.Invoke(json) ?? json));
    }

    [Fact]
    public async Task PhoneBackupRestoredIntoTheConnectedRadioSetsItsPresetAndFrequency()
    {
        await using var f = new AppFixture();
        var app = f.App;
        await f.ConnectAsync();
        var radioId = app.RadioId!;
        await app.Device.SetRadioAsync(915.0, 250, 10, 5); // the firmware's defaults
        var backup = PhoneBackup();
        var d = Assert.Single(backup.Devices);
        Assert.Equal("us-ca", Mc1Backup.PresetOf(d)?.Id);

        var onlyRadio = new Mc1Backup.RestoreOptions(false, true, false, false, false, false, false, false, false);
        var r = backup.Restore(app, d, radioId, onlyRadio); // while connected, into the connected radio
        Assert.True(r.RadioWritePending);
        output.WriteLine(await app.PendingConfig.ApplyAsync()); // what the restore window does next
        Assert.Equal(910.525, app.SelfInfo!.RadioFrequency, 3);
        Assert.Equal(62.5, app.SelfInfo.RadioBandwidth, 1);
        Assert.Equal(7, app.SelfInfo.RadioSpreadingFactor);
        Assert.Equal("us-ca", app.Settings.Current.AppliedRadioPresets[radioId]);
        Assert.Equal("us-ca", app.Device.CurrentPreset()?.Id);
    }

    [Fact]
    public void PresetIsFoundFromTheSettingsWhenTheBackupDoesntNameIt()
    {
        var d = Assert.Single(PhoneBackup(j => j.Replace("\"appliedRadioPresetID\":\"us-ca\",", "")).Devices);
        Assert.Null(d.AppliedPresetId);
        Assert.Equal(910.525, Mc1Backup.PresetOf(d)!.FrequencyMHz, 3);
    }

    [Fact]
    public async Task PhoneBackupPinsChatsSetsTheAreaAndNeverGrantsRemoteCli()
    {
        await using var f = new AppFixture();
        var app = f.App;
        var backup = PhoneBackup();
        var d = Assert.Single(backup.Devices);
        var r = backup.Restore(app, d, null, new Mc1Backup.RestoreOptions(true, true, true, true, true, true, true, true, true));
        var dave = Assert.Single(app.Db.GetContacts(r.RadioId));
        Assert.True(dave.IsPinned);
        Assert.Equal(0, dave.Flags & (int)ContactFlags.RemoteCliAllowed);
        Assert.All(app.PendingConfig.Current!.Contacts, c => Assert.Equal(0, c.Flags & (byte)ContactFlags.RemoteCliAllowed));
        Assert.True(app.Db.GetChannels(r.RadioId).Single(c => c.IsConfigured).IsPinned);
        Assert.Equal("CA", app.Settings.Current.PresetCountry);
        Assert.Equal("CA-BC", app.Settings.Current.PresetArea);

        // A backup made here carries the pins and the area back to the phone.
        var json = Mc1Backup.Decompress(Mc1Backup.Export(app, "1.1.0", out _))!;
        Assert.Contains("\"isPinned\":true", json);
        Assert.Contains("\"administrativeAreaCode\":\"CA-BC\"", json);
        await app.PendingConfig.DiscardAsync();
    }
}

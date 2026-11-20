using MC1.Core.Models;
using MC1.Core.Utilities;

namespace MC1.Core.Services;

/// <summary>Sensor management over the remote CLI (MeshCore One 1.7): list, change and verify settings, reboot.</summary>
public sealed partial class RemoteNodeService
{
    /// <summary>Every setting the sensor lists (paged by the firmware). Requires an admin sign-in.</summary>
    public async Task<IReadOnlyList<SensorSetting>> SensorSettingsAsync(ContactRecord sensor, CancellationToken ct = default)
    {
        var settings = new List<SensorSetting>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        int? total = null;
        var offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var command = offset == 0 ? SensorSettingsParser.ListCommand : $"{SensorSettingsParser.ListCommand} {offset}";
            var page = SensorSettingsParser.List(await SendCliAsync(sensor, command, ct: ct).ConfigureAwait(false), offset);
            if (total is not null && total != page.Total) throw new SensorSettingsException(L.T("The sensor sent an unexpected reply."));
            total = page.Total;
            foreach (var s in page.Settings)
            {
                if (!keys.Add(s.Key)) throw new SensorSettingsException(L.T("The sensor sent an unexpected reply."));
                settings.Add(s);
            }
            if (page.Next is not { } next) return settings;
            offset = next;
        }
    }

    /// <summary>Writes one setting and reads it back. Throws when the sensor rejects it or the change can't be verified.</summary>
    public async Task<SensorSetting> SetSensorSettingAsync(ContactRecord sensor, SensorSetting setting, string value, CancellationToken ct = default)
    {
        var command = setting.SetCommand(value)
            ?? throw new SensorSettingsException(L.T("Use a nonempty key and value without whitespace. The complete command must fit within 160 UTF-8 bytes."));
        string reply;
        try { reply = await SendCliAsync(sensor, command, ct: ct).ConfigureAwait(false); }
        catch (RemoteNodeException) { throw new SensorSettingsException(L.T("The change could not be verified. Reload settings before saving again.")); }
        try { SensorSettingsParser.Accepted(reply); }
        catch (SensorSettingRejectedException) { throw; }
        catch (SensorSettingsException) { throw new SensorSettingsException(L.T("The change could not be verified. Reload settings before saving again.")); }
        try
        {
            var verified = SensorSettingsParser.Value(await SendCliAsync(sensor, SensorSettingsParser.GetPrefix + setting.Key, ct: ct).ConfigureAwait(false));
            if (verified != value) throw new SensorSettingsException("");
            return setting with { Value = value };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new SensorSettingsException(L.T("The change could not be verified. Reload settings before saving again.")); }
    }

    /// <summary>Sends a reboot request; the sensor doesn't confirm it.</summary>
    public Task RebootSensorAsync(ContactRecord sensor, CancellationToken ct = default) =>
        SendCliAsync(sensor, SensorSettingsParser.RebootCommand, ct: ct);
}

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Windows.ViewModels;

public enum CliLineKind { Input, Output, Error, Info }

public sealed record CliLine(string Text, CliLineKind Kind)
{
    public string Color => Kind switch
    {
        CliLineKind.Input => "#60A5FA",
        CliLineKind.Error => "#F87171",
        CliLineKind.Info => "#9CA3AF",
        _ => "#E5E7EB",
    };
}

public sealed record CliTarget(string Name, ContactRecord? Contact)
{
    public bool IsLocal => Contact is null;
    /// <summary>A chat contact's companion radio (remote CLI, firmware 1.18).</summary>
    public bool IsContactRadio => Contact is { TypeRaw: (int)ContactType.Chat };
    public override string ToString() => Name;
}

public sealed partial class CliViewModel : ToolPage
{
    private readonly MainWindowViewModel _main;
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string? _pendingConfirm;
    private List<string> _matches = [];
    private bool _contactRadioTargets;

    private static readonly string[] RemoteCommands =
    [
        "ver", "board", "clock", "clock sync", "time ", "reboot", "advert", "neighbors", "stats-core", "stats-radio", "stats-packets",
        "get name", "set name ", "get radio", "set radio ", "get freq", "set freq ", "get tx", "set tx ", "get repeat", "set repeat on", "set repeat off",
        "get advert.interval", "set advert.interval ", "get flood.advert.interval", "set flood.advert.interval ", "get flood.max", "set flood.max ",
        "get lat", "set lat ", "get lon", "set lon ", "get owner.info", "set owner.info ", "get af", "set af ", "get rxdelay", "set rxdelay ",
        "get txdelay", "set txdelay ", "get direct.txdelay", "set direct.txdelay ", "get guest.password", "set guest.password ", "password ",
        "get allow.read.only", "set allow.read.only on", "set allow.read.only off", "get multi.acks", "set multi.acks ", "get bridge.enabled",
        "get agc.reset.interval", "set agc.reset.interval ", "get int.thresh", "set int.thresh ", "region", "region list", "log start", "log stop", "log erase",
        "erase", "clear stats", "powersaving", "powersaving on", "powersaving off", "get path.hash.mode", "set path.hash.mode ",
    ];

    private static readonly string[] LocalCommands =
    [
        "help", "clear", "ver", "board", "clock", "clock sync", "advert", "advert.zerohop", "floodadv", "reboot", "stats",
        "get name", "get lat", "get lon", "get tx", "get radio", "get freq", "get public.key", "get multi.acks", "get path.hash.mode", "get bat", "get vars",
        "set name ", "set lat ", "set lon ", "set tx ", "set radio ", "set freq ", "set multi.acks ", "set path.hash.mode ", "set var ",
    ];

    /// <summary>App commands for every target (switching targets by number or name).</summary>
    private static readonly string[] SessionCommands = ["session", "session list", "session local"];

    public CliViewModel(MainWindowViewModel main)
    {
        _main = main;
        Action<string, string> onOutput = (prefix, text) => Ui(() =>
        {
            // Unsolicited replies (e.g. late answers) for the selected node.
            if (Target?.Contact is { } c && Convert.ToHexString(c.PublicKey, 0, 6).Equals(prefix, StringComparison.OrdinalIgnoreCase) && !_awaiting)
                Append(text, CliLineKind.Output);
        });
        Core.Remote.CliOutput += onOutput;
        Track(() => Core.Remote.CliOutput -= onOutput);
        // Contact radios become targets once a 1.18 radio is connected (and leave when it isn't).
        OnStatus(() => Ui(() => { if (_contactRadioTargets != Core.Device.SupportsRadioCli && Targets.Count > 0) ReloadTargets(); }));
        Lines.Add(new CliLine(L.T("MeshCore CLI — type 'help' for local commands. Pick a remote node above to administer it."), CliLineKind.Info));
    }

    public ObservableCollection<CliLine> Lines { get; } = new();
    public ObservableCollection<CliTarget> Targets { get; } = new();
    public ObservableCollection<string> Suggestions { get; } = new();

    [ObservableProperty] private CliTarget? _target;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _prompt = "radio>";
    [ObservableProperty] private string? _sessionStatus;
    [ObservableProperty] private bool _needsLogin;
    private bool _awaiting;

    public event Action? LinesAppended;

    public override void OnShown() => ReloadTargets();

    private void ReloadTargets()
    {
        var selected = Target?.Contact?.Id;
        _contactRadioTargets = Core.Device.SupportsRadioCli;
        Targets.Clear();
        Targets.Add(new CliTarget(L.T("This radio (local)"), null));
        foreach (var c in Core.Contacts.GetAll().Where(c => c.ContactType is ContactType.Repeater or ContactType.Room or ContactType.Sensor).OrderBy(c => c.DisplayName))
            Targets.Add(new CliTarget($"{c.DisplayName} · {Formatters.TypeName(c.ContactType)}", c));
        // Firmware 1.18: a contact's own radio runs CLI commands when its owner allowed this radio Remote CLI.
        if (_contactRadioTargets)
            foreach (var c in Core.Contacts.GetAll().Where(c => c.TypeRaw == (int)ContactType.Chat && !c.IsBlocked).OrderBy(c => c.DisplayName))
                Targets.Add(new CliTarget(L.F("{0} · contact's radio", c.DisplayName), c));
        Target = Targets.FirstOrDefault(t => t.Contact?.Id == selected) ?? Targets[0];
    }

    public void SelectTarget(ContactRecord contact)
    {
        ReloadTargets();
        Target = Targets.FirstOrDefault(t => t.Contact?.Id == contact.Id) ?? Target;
    }

    partial void OnTargetChanged(CliTarget? value)
    {
        _pendingConfirm = null;
        if (value is null) return;
        if (value.IsLocal)
        {
            Prompt = $"{(Core.SelfName is { Length: > 0 } n ? n : "radio")}>";
            SessionStatus = Core.Device.SupportsRadioCli
                ? L.T("Commands run on your companion radio. Commands the app doesn't know go to the radio itself.")
                : L.T("Commands run on your companion radio.");
            NeedsLogin = false;
        }
        else if (value.IsContactRadio)
        {
            var c = value.Contact!;
            Prompt = $"{c.DisplayName}>";
            NeedsLogin = false;
            SessionStatus = L.F("Commands go to {0}'s radio. Without Remote CLI access you get no reply, and they receive each command as a message.", c.DisplayName);
            Append($"── {c.DisplayName} ──", CliLineKind.Info);
        }
        else
        {
            var c = value.Contact!;
            Prompt = $"{c.DisplayName}>";
            var session = Core.Remote.Sessions().FirstOrDefault(s => s.PublicKey.SequenceEqual(c.PublicKey));
            NeedsLogin = session is not { IsConnected: true, IsAdmin: true };
            SessionStatus = session switch
            {
                { IsConnected: true, IsAdmin: true } => L.T("Signed in as admin."),
                { IsConnected: true } => L.T("Signed in as guest — most commands need admin access."),
                _ => L.T("Not signed in. Sign in as admin to run commands."),
            };
            Append($"── {c.DisplayName} ──", CliLineKind.Info);
        }
        UpdateSuggestions();
    }

    partial void OnInputChanged(string value) => UpdateSuggestions();

    /// <summary>The completion lines for the selected target. Lines end with a space only while they need an argument.</summary>
    private IEnumerable<string> CompletionSource()
    {
        if (Target?.IsContactRadio == true) return RadioCliCompletion.ContactRadioLines().Concat(SessionCommands);
        if (Target?.IsLocal != false)
        {
            IEnumerable<string> local = LocalCommands.Concat(SessionCommands);
            return Core.Device.SupportsRadioCli ? local.Concat(RadioCliCompletion.LocalRadioLines()) : local;
        }
        // A room never offers repeat mode.
        IEnumerable<string> remote = RemoteCommands;
        if (Target.Contact?.ContactType == ContactType.Room) remote = remote.Where(s => !s.Contains(" repeat", StringComparison.Ordinal));
        return remote.Concat(SessionCommands);
    }

    private void UpdateSuggestions()
    {
        Suggestions.Clear();
        _matches = [];
        var q = Input.TrimStart();
        if (q.Length == 0) return;
        _matches = CompletionSource().Distinct()
            .Where(s => s.StartsWith(q, StringComparison.OrdinalIgnoreCase) && !s.Equals(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var s in _matches.Take(8)) Suggestions.Add(s);
    }

    /// <summary>Tab completion: completes to the longest common prefix of all matches.</summary>
    public void Complete()
    {
        if (_matches.Count == 0) return;
        if (_matches.Count == 1) { Input = _matches[0]; return; }
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
        while (Lines.Count > 3000) Lines.RemoveAt(0);
        LinesAppended?.Invoke();
    }

    [RelayCommand] private void ClearOutput() => Lines.Clear();

    [RelayCommand]
    private void UseSuggestion(string s) => Input = s;

    [RelayCommand]
    private async Task Login()
    {
        if (Target?.Contact is not { } c) return;
        var vm = new LoginViewModel(c);
        await AppHost.Dialogs.ShowDialog(vm, L.F("Sign in to {0}", c.DisplayName), 420, 360);
        OnTargetChanged(Target);
    }

    /// <summary>
    /// What is sent and recorded: the radio CLI gets the line as typed (a value's spaces, e.g. an empty WiFi password,
    /// matter); app commands and repeater commands get the trimmed line.
    /// </summary>
    private string TransmittedLine(string original, string normalized)
    {
        if (Target?.IsContactRadio == true)
        {
            var parts = normalized.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var verb = parts[0].ToLowerInvariant();
            return verb == "help" || (verb == "clear" && parts.Length == 1) ? normalized : original.TrimEnd('\r', '\n');
        }
        if (Target?.IsLocal != false && Core.Device.SupportsRadioCli && RadioCliPassthrough.ShouldSend(normalized)) return original.TrimEnd('\r', '\n');
        return normalized;
    }

    [RelayCommand]
    private async Task Submit()
    {
        var original = Input;
        var command = original.Trim();
        Input = "";
        _historyIndex = -1;
        if (command.Length == 0) return;

        if (_pendingConfirm is not null)
        {
            Append($"{Prompt} {command}", CliLineKind.Input);
            var confirmed = command.Equals("y", StringComparison.OrdinalIgnoreCase) || command.Equals("yes", StringComparison.OrdinalIgnoreCase);
            var pending = _pendingConfirm;
            _pendingConfirm = null;
            if (!confirmed) { Append(L.T("Cancelled."), CliLineKind.Info); return; }
            await Execute(pending, confirmed: true);
            return;
        }
        var transmitted = TransmittedLine(original, command);
        if (_history.Count == 0 || _history[^1] != transmitted) _history.Add(transmitted);
        Append($"{Prompt} {transmitted}", CliLineKind.Input);
        if (command.Equals("clear", StringComparison.OrdinalIgnoreCase)) { Lines.Clear(); return; }
        if (TryHandleSession(command)) return;
        await Execute(transmitted, confirmed: false);
    }

    // MARK: Sessions ("session", "session <n|name|local>", "s<n>")

    /// <summary>Switches targets by number (1 = this radio, then the list above) or name. Like the iPhone app, a
    /// number that isn't a session (0, negative, past the end) is rejected.</summary>
    private bool TryHandleSession(string command)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        if (verb.Length > 1 && verb[0] == 's' && int.TryParse(verb[1..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            SwitchSession(verb[1..]);
            return true;
        }
        if (verb != "session") return false;
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        if (arg.Length == 0 || arg.Equals("list", StringComparison.OrdinalIgnoreCase)) ListSessions();
        else if (arg.Equals("local", StringComparison.OrdinalIgnoreCase)) SwitchSession("1");
        else SwitchSession(arg);
        return true;
    }

    private void ListSessions()
    {
        if (Targets.Count == 0) ReloadTargets();
        Append(L.T("Sessions:"), CliLineKind.Output);
        for (var i = 0; i < Targets.Count; i++)
        {
            var t = Targets[i];
            var name = t.IsLocal ? L.F("{0} (local)", Core.SelfName is { Length: > 0 } n ? n : "radio") : t.Name;
            Append($"  {(t == Target ? '*' : ' ')} {i + 1}. {name}", CliLineKind.Output);
        }
    }

    private void SwitchSession(string arg)
    {
        if (Targets.Count == 0) ReloadTargets();
        CliTarget? found = null;
        if (int.TryParse(arg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            if (number >= 1 && number <= Targets.Count) found = Targets[number - 1];
        }
        else
            found = Targets.FirstOrDefault(t => t.Contact is { } c && (c.DisplayName.Equals(arg, StringComparison.CurrentCultureIgnoreCase) || c.Name.Equals(arg, StringComparison.CurrentCultureIgnoreCase)));
        if (found is null) { Append(L.F("Session not found: {0}", arg), CliLineKind.Error); return; }
        Append(L.F("Switched to {0}", found.IsLocal ? (Core.SelfName is { Length: > 0 } n ? n : "radio") : found.Contact!.DisplayName), CliLineKind.Info);
        Target = found;
    }

    private async Task Execute(string command, bool confirmed)
    {
        if (!Core.IsConnected) { Append(L.T("Not connected to a radio."), CliLineKind.Error); return; }
        IsBusy = true;
        try
        {
            if (Target?.Contact is { } contact) await ExecuteRemote(contact, command, confirmed, Target.IsContactRadio);
            else await ExecuteLocal(command, confirmed);
        }
        catch (Exception ex) { Append(L.F("Error: {0}", ex.Message), CliLineKind.Error); }
        finally { IsBusy = false; }
    }

    private static string NoReplySentLine(RemoteCliNoReplyKind kind) =>
        kind == RemoteCliNoReplyKind.Reboot ? L.T("Reboot command sent.") : L.T("Power off command sent.");

    private async Task ExecuteRemote(ContactRecord contact, string command, bool confirmed, bool contactRadio)
    {
        var lower = command.Trim().ToLowerInvariant();
        if (!confirmed && (lower == "reboot" || lower.StartsWith("set radio") || lower.StartsWith("set freq") || lower == "erase"
                           || (contactRadio && lower is "poweroff" or "shutdown")))
        {
            _pendingConfirm = command;
            // The answer is still checked against 'y' / 'yes' (see Submit).
            Append(L.F("'{0}' can take the node off the air. Type 'y' to confirm.", command), CliLineKind.Info);
            return;
        }
        _awaiting = true;
        try
        {
            if (contactRadio)
            {
                await ExecuteOnContactRadio(contact, command);
                return;
            }
            var reply = await Core.Remote.SendCliAsync(contact, command, TimeSpan.FromSeconds(12));
            Append(reply.Length == 0 ? L.T("(empty reply)") : reply, CliLineKind.Output);
        }
        finally { _awaiting = false; }
    }

    /// <summary>Remote CLI on a contact's radio: reboot and power off often get no reply (a real reply is still shown);
    /// no reply to anything else most likely means no access.</summary>
    private async Task ExecuteOnContactRadio(ContactRecord contact, string command)
    {
        var noReply = RemoteCliNoReply.ForContactRadio(command);
        try
        {
            var reply = await Core.Remote.SendRadioCliAsync(contact, command, noReply is null ? null : RemoteNodeService.RadioCliNoReplyTimeout);
            if (reply.Length == 0 && noReply is { } k) Append(NoReplySentLine(k), CliLineKind.Info);
            else Append(reply.Length == 0 ? L.T("(empty reply)") : reply, CliLineKind.Output);
        }
        catch (TimeoutException)
        {
            if (noReply is { } k) Append(NoReplySentLine(k), CliLineKind.Info);
            else Append(L.F("No reply from {0}. They may not have allowed you Remote CLI access, or their node was unreachable.", contact.DisplayName), CliLineKind.Error);
        }
    }

    private static bool TryDouble(string s, out double v) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private async Task ExecuteLocal(string command, bool confirmed)
    {
        var session = Core.Session ?? throw new InvalidOperationException(L.T("Not connected."));
        // Firmware 1.18: anything the app doesn't handle itself runs on the radio's own CLI, exactly as typed.
        if (Core.Device.SupportsRadioCli && RadioCliPassthrough.ShouldSend(command))
        {
            var reply = await Core.Device.RunRadioCliAsync(command);
            Append(reply.Length == 0 ? L.T("(empty reply)") : reply, CliLineKind.Output);
            return;
        }
        var parts = command.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        var self = Core.SelfInfo;
        var caps = Core.Capabilities;
        switch (verb)
        {
            case "help":
                // The command syntax lines are what the user types, so only the sentences are translated.
                Append(L.T("Local commands (this radio):") + "\n" + """
                      ver | board | clock | clock sync | advert | floodadv | reboot | stats
                      get name|lat|lon|tx|radio|freq|public.key|multi.acks|path.hash.mode|bat|vars
                      set name <text> | set lat <deg> | set lon <deg> | set tx <dBm>
                      set radio <freqMHz>,<bwKHz>,<sf>,<cr> | set freq <MHz>
                      set multi.acks <0|1> | set path.hash.mode <0|1|2> | set var <key> <value>
                      session [list|local|<n>|<name>] | s<n> | clear
                    """ + (Core.Device.SupportsRadioCli
                        ? "\n  poweroff\n" + L.T("Other commands (for example get af, set wifi.ssid <name>, get wifi.status) go to the radio's own CLI.")
                        : "")
                    + "\n" + L.T("Pick a repeater or room above to send its firmware CLI commands."), CliLineKind.Info);
                return;
            case "ver":
                Append(caps is null ? "unknown" : $"{caps.Version} (build {caps.FirmwareBuild}, protocol v{caps.FirmwareVersion})", CliLineKind.Output);
                return;
            case "board":
                Append(caps?.Model ?? "unknown", CliLineKind.Output);
                return;
            case "clock" when parts.Length > 1 && parts[1].Equals("sync", StringComparison.OrdinalIgnoreCase):
                await Core.Device.SyncClockAsync();
                Append("OK - clock set to " + DateTime.Now.ToString("HH:mm:ss dd/MM/yyyy"), CliLineKind.Output);
                return;
            case "clock":
                var t = await session.GetTimeAsync();
                Append($"{t.LocalDateTime:HH:mm:ss - dd/MM/yyyy} (offset {(t - DateTimeOffset.UtcNow).TotalSeconds:+0;-0}s from this PC)", CliLineKind.Output);
                return;
            case "advert" or "advert.zerohop":
                await Core.Device.SendAdvertAsync(false);
                Append("OK - zero-hop advert sent", CliLineKind.Output);
                return;
            case "floodadv":
                await Core.Device.SendAdvertAsync(true);
                Append("OK - flood advert sent", CliLineKind.Output);
                return;
            case "reboot":
                if (!confirmed) { _pendingConfirm = command; Append(L.T("Reboot the radio? The connection will drop. Type 'y' to confirm."), CliLineKind.Info); return; }
                await Core.Device.RebootAsync();
                Append("Rebooting…", CliLineKind.Output);
                return;
            // The radio powers off without a reply, so the app sends it (and doesn't reconnect afterwards).
            case "poweroff" or "shutdown" when Core.Device.SupportsRadioCli:
                if (!confirmed) { _pendingConfirm = command; Append(L.T("Power off the radio? It turns off and disconnects; turn it on with its power button. Type 'y' to confirm."), CliLineKind.Info); return; }
                await Core.Device.PowerOffAsync();
                Append("OK - powering off", CliLineKind.Output);
                return;
            case "stats":
                var (core, radio, packets) = await Core.Device.GetStatsAsync();
                Append($"battery {core.BatteryMV} mV · uptime {Formatters.Duration(core.UptimeSeconds)} · errors {core.Errors} · queue {core.QueueLength}\n" +
                       $"noise floor {radio.NoiseFloor} dBm · last RSSI {radio.LastRssi} dBm · last SNR {radio.LastSnr:0.#} dB · tx air {radio.TxAirtimeSeconds}s · rx air {radio.RxAirtimeSeconds}s\n" +
                       $"packets rx {packets.Received} / tx {packets.Sent} · flood tx {packets.FloodTx} rx {packets.FloodRx} · direct tx {packets.DirectTx} rx {packets.DirectRx}", CliLineKind.Output);
                return;
            case "get" when parts.Length >= 2:
                await LocalGet(parts[1].ToLowerInvariant(), self, caps);
                return;
            case "set" when parts.Length >= 3:
                await LocalSet(parts[1].ToLowerInvariant(), parts[2].Trim(), confirmed, command);
                return;
            default:
                Append(L.F("Unknown command '{0}'. Type 'help'.", verb), CliLineKind.Error);
                return;
        }
    }

    private async Task LocalGet(string key, SelfInfo? self, DeviceCapabilities? caps)
    {
        if (self is null) { Append(L.T("No device info yet."), CliLineKind.Error); return; }
        var inv = CultureInfo.InvariantCulture;
        switch (key)
        {
            case "name": Append(self.Name, CliLineKind.Output); break;
            case "lat": Append(self.Latitude.ToString("0.000000", inv), CliLineKind.Output); break;
            case "lon": Append(self.Longitude.ToString("0.000000", inv), CliLineKind.Output); break;
            case "tx": Append($"{self.TxPower} dBm (max {self.MaxTxPower})", CliLineKind.Output); break;
            case "radio": Append(string.Create(inv, $"{self.RadioFrequency:0.###},{self.RadioBandwidth:0.#},{self.RadioSpreadingFactor},{self.RadioCodingRate}"), CliLineKind.Output); break;
            case "freq": Append(self.RadioFrequency.ToString("0.###", inv), CliLineKind.Output); break;
            case "public.key": Append(Convert.ToHexString(self.PublicKey), CliLineKind.Output); break;
            case "multi.acks": Append(self.MultiAcks.ToString(), CliLineKind.Output); break;
            case "path.hash.mode": Append((caps?.PathHashMode ?? 0).ToString(), CliLineKind.Output); break;
            case "bat":
                await Core.RefreshBatteryAsync();
                Append(Core.Battery is { } b ? $"{b.Level} mV ({Core.BatteryPercent}%)" : "unknown", CliLineKind.Output);
                break;
            case "vars":
                var vars = await Core.Device.GetCustomVarsAsync();
                Append(vars.Count == 0 ? L.T("(no custom variables)") : string.Join("\n", vars.Select(kv => $"{kv.Key} = {kv.Value}")), CliLineKind.Output);
                break;
            default: Append(L.F("Unknown key '{0}'.", key), CliLineKind.Error); break;
        }
    }

    private async Task LocalSet(string key, string value, bool confirmed, string command)
    {
        switch (key)
        {
            case "name":
                await Core.Device.SetNameAsync(value);
                break;
            case "lat" when TryDouble(value, out var lat) && lat is >= -90 and <= 90:
                await Core.Device.SetLocationAsync(lat, Core.SelfInfo?.Longitude ?? 0);
                break;
            case "lon" when TryDouble(value, out var lon) && lon is >= -180 and <= 180:
                await Core.Device.SetLocationAsync(Core.SelfInfo?.Latitude ?? 0, lon);
                break;
            case "tx" when int.TryParse(value, out var tx):
                await Core.Device.SetTxPowerAsync(tx);
                break;
            case "radio":
                var p = value.Split(',', StringSplitOptions.TrimEntries);
                if (p.Length != 4 || !TryDouble(p[0], out var f) || !TryDouble(p[1], out var bw) || !byte.TryParse(p[2], out var sf) || !byte.TryParse(p[3], out var cr))
                { Append(L.F("Usage: {0}", "set radio <freqMHz>,<bwKHz>,<sf>,<cr>"), CliLineKind.Error); return; }
                if (!confirmed) { _pendingConfirm = command; Append(L.T("Changing radio settings can take you off your mesh. Type 'y' to confirm."), CliLineKind.Info); return; }
                await Core.Device.SetRadioAsync(f, bw, sf, cr);
                break;
            case "freq" when TryDouble(value, out var freq):
                if (!confirmed) { _pendingConfirm = command; Append(L.T("Changing frequency can take you off your mesh. Type 'y' to confirm."), CliLineKind.Info); return; }
                var s = Core.SelfInfo!;
                await Core.Device.SetRadioAsync(freq, s.RadioBandwidth, s.RadioSpreadingFactor, s.RadioCodingRate);
                break;
            case "multi.acks" when byte.TryParse(value, out var ma) && ma <= 1:
                await Core.Device.SetOtherParamsAsync(c => c.MultiAcks = ma);
                break;
            case "path.hash.mode" when byte.TryParse(value, out var pm) && pm <= 2:
                await Core.Device.SetPathHashModeAsync(pm);
                break;
            case "var":
                var kv = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (kv.Length != 2) { Append(L.F("Usage: {0}", "set var <key> <value>"), CliLineKind.Error); return; }
                await Core.Device.SetCustomVarAsync(kv[0], kv[1]);
                break;
            default:
                Append(L.F("Invalid value or unknown key '{0}'. Type 'help'.", key), CliLineKind.Error);
                return;
        }
        Append("OK", CliLineKind.Output);
    }
}

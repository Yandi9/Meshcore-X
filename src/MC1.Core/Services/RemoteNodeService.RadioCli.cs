using MC1.Core.Models;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>Remote CLI for a contact's companion radio (firmware 1.18; port of Swift <c>sendRadioCLICommand</c>).</summary>
public sealed partial class RemoteNodeService
{
    /// <summary>How long a reply may take at most, whatever the radio suggests.</summary>
    private static readonly TimeSpan RadioCliMaximum = TimeSpan.FromSeconds(15);

    /// <summary>Default wait for a contact radio's reply.</summary>
    public static readonly TimeSpan RadioCliTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Wait for commands that get no reply by design (reboot, power off): long enough to catch an error reply.</summary>
    public static readonly TimeSpan RadioCliNoReplyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Sends a CLI command to a contact's radio (<see cref="TextType.CliCommand"/>) and waits for its reply. The contact
    /// runs it only if its owner allowed this radio Remote CLI; otherwise no reply comes and they get the command as a
    /// message. Leading spaces are dropped (trailing ones are part of a value). A missing reply is a
    /// <see cref="TimeoutException"/> — the contact's path is left alone. Cancelling stops the wait (or the send, when
    /// it is still queued behind another command to the same contact) and frees the contact for the next command.
    /// </summary>
    public async Task<string> SendRadioCliAsync(ContactRecord contact, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var session = app.RequireSession();
        command = command.TrimStart(' ');
        var prefix = Prefix(contact.PublicKey);
        var slot = _cliSlots.GetOrAdd(prefix, _ => new SemaphoreSlim(1, 1));
        await slot.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var wirePrefix = $"{(byte)Interlocked.Increment(ref _cliCounter):X2}|";
            var pending = new PendingCli(Guid.NewGuid().ToString(), command, wirePrefix, false, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
            _pendingCli[prefix] = pending;
            try
            {
                app.Log.Info("Remote", $"Remote CLI to contact {prefix}: {RedactCli(command)}");
                var sent = await session.SendCommandAsync(contact.PublicKey, wirePrefix + command, TextType.CliCommand, ct: ct).ConfigureAwait(false);
                var requested = timeout ?? RadioCliTimeout;
                var firmware = TimeSpan.FromMilliseconds(sent.SuggestedTimeoutMs * 2.0);
                var effective = TimeSpan.FromTicks(Math.Min(RadioCliMaximum.Ticks, Math.Max(requested.Ticks, firmware.Ticks)));
                var deadline = DateTime.UtcNow + effective;
                while (!pending.Tcs.Task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    // Replies arrive as queued messages; nudge the radio in case no "messages waiting" push came.
                    try { await session.GetMessageAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* one poll can miss the reply; the deadline is the timeout */ }
                    await Task.WhenAny(pending.Tcs.Task, Task.Delay(300, ct)).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                if (!pending.Tcs.Task.IsCompleted) throw new TimeoutException(L.F("No reply from {0}.", contact.DisplayName));
                return await pending.Tcs.Task.ConfigureAwait(false);
            }
            finally { _pendingCli.TryRemove(new KeyValuePair<string, PendingCli>(prefix, pending)); }
        }
        finally { slot.Release(); }
    }

    /// <summary>The command for the log, without secrets (port of Swift <c>LogRedaction.cliCommand</c>).</summary>
    internal static string RedactCli(string command)
    {
        foreach (var p in (string[])["set wifi.pwd ", "set pin ", "set guest.password ", "password "])
            if (command.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return command[..p.Length] + "[REDACTED]";
        return command.Length <= 40 ? command : command[..40] + "...";
    }
}

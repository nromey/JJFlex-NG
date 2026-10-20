using System;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.Listenback;

namespace Radios;

public partial class FlexBase
{
    private Radio _fullDuplexReportedRadio;
    private Guid _fullDuplexReportedConnection;
    private int _fullDuplexChanging;
    // FlexLib has no FullDuplexAllowed flag. Missing state is unknown, not
    // unsupported. A reported full_duplex_enabled field offers the control.
    public bool? FullDuplexAvailable => theRadio?.Connected == true &&
        ReferenceEquals(theRadio, _fullDuplexReportedRadio) &&
        theRadio.CurrentCommandConnection?.Id == _fullDuplexReportedConnection ? true : null;

    // FlexLib sends this subscription during connection setup and has no
    // duplicate-subscription guard. Its parser processes every returned status,
    // including unchanged values. Whether the radio re-sends on this same
    // connection is a bench question; if it does not, this request times out.
    private async Task<string> ReadStatusAsync(Radio radio, CommandConnection connection,
        string subscription, Func<string, bool> accepts, Action<string> log = null)
    {
        bool Live() => radio != null && radio.Connected && ReferenceEquals(theRadio, radio) &&
            ReferenceEquals(connection, radio.CurrentCommandConnection);
        using var tap = new RadioStatusTap(radio);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int issued = 0;
        tap.Status += raw =>
        {
            if (Volatile.Read(ref issued) != 0 && Live() && accepts(raw))
            { log?.Invoke("status " + raw); answer.TrySetResult(raw); }
        };
        await IqQueue(() =>
        {
            if (!Live()) throw new InvalidOperationException("connection-ended");
            Volatile.Write(ref issued, 1);
            int seq = radio.SendReplyCommand((sequence, code, response) =>
            {
                log?.Invoke($"reply {sequence} {code:X8} {response}");
                reply.TrySetResult(code == 0);
            }, subscription);
            log?.Invoke($"sent {seq} {subscription}");
        }).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(ListenbackConfirmationWait);
        if (!await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false)) throw new InvalidOperationException("readback-refused");
        string raw = await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        if (!Live()) throw new InvalidOperationException("connection-ended");
        return raw;
    }

    private async Task<bool> ReadFullDuplexAsync(Radio radio, CommandConnection connection, Action<string> log = null)
    {
        string raw = await ReadStatusAsync(radio, connection, "sub radio all", line =>
        {
            int bar = line.IndexOf('|');
            if (bar < 0 || !line[(bar + 1)..].StartsWith("radio ", StringComparison.OrdinalIgnoreCase)) return false;
            var values = RadioStatusTap.Values(line[(bar + 1)..]);
            return values.TryGetValue("full_duplex_enabled", out string value) && (value == "0" || value == "1");
        }, log).ConfigureAwait(false);
        string value = RadioStatusTap.Values(raw[(raw.IndexOf('|') + 1)..])["full_duplex_enabled"];
        return value == "1";
    }

    private async Task<bool> ReadSettingAsync(Radio radio, CommandConnection connection,
        SavedRadioSetting setting, Action<string> log = null)
    {
        string subscription = setting.Name switch
        {
            "full-duplex" => "sub radio all",
            "tx-antenna" => "sub slice all",
            "rf-power" or "monitor" => "sub tx all",
            "xvtr-drive" => "sub xvtr all",
            _ => throw new ArgumentOutOfRangeException(nameof(setting))
        };
        string raw = await ReadStatusAsync(radio, connection, subscription, line =>
        {
            int bar = line.IndexOf('|');
            if (bar < 0 || !line[(bar + 1)..].StartsWith(setting.StatusPrefix + " ", StringComparison.OrdinalIgnoreCase)) return false;
            return RadioStatusTap.Values(line[(bar + 1)..]).ContainsKey(setting.StatusKey);
        }, log).ConfigureAwait(false);
        return ListenbackRestoreEvidence.Matches(setting, raw);
    }

    private async Task<bool> SetFullDuplexConfirmedAsync(bool value, Radio radio,
        CommandConnection connection, Action<string> log = null)
    {
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RequestFullDuplexAsync(value, (sequence, code, response) =>
        {
            log?.Invoke($"set reply {sequence} {code:X8} {response}");
            reply.TrySetResult(code == 0);
        }, radio, connection).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(ListenbackConfirmationWait);
        if (!await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false)) throw new InvalidOperationException("full-duplex-refused");
        return await ReadFullDuplexAsync(radio, connection, log).ConfigureAwait(false) == value;
    }

    private async Task SetFullDuplexFromPropertyAsync(bool value)
    {
        var radio = theRadio;
        var connection = radio?.CurrentCommandConnection;
        try
        {
            if (!await SetFullDuplexConfirmedAsync(value, radio, connection).ConfigureAwait(false))
                JJTrace.Tracing.TraceLine("Full duplex property request: read-back mismatch", System.Diagnostics.TraceLevel.Error);
        }
        catch (Exception ex)
        { JJTrace.Tracing.TraceLine("Full duplex property request: " + ex, System.Diagnostics.TraceLevel.Error); }
    }

    private async Task RequestFullDuplexAsync(bool value, ReplyHandler reply, Radio expectedRadio = null, CommandConnection expectedConnection = null)
    {
        var radio = expectedRadio ?? theRadio;
        var connection = expectedConnection ?? radio?.CurrentCommandConnection;
        try
        {
            await IqQueue(() =>
            {
                if (radio == null || !radio.Connected || !ReferenceEquals(theRadio, radio) ||
                    !ReferenceEquals(connection, radio.CurrentCommandConnection)) throw new InvalidOperationException("connection-ended");
                string command = "radio set full_duplex_enabled=" + (value ? "1" : "0");
                // The vendor setter changes its getter before the radio answers.
                // Sending the command leaves FullDuplexEnabled radio-reported.
                radio.SendReplyCommand(reply ?? ((sequence, code, text) =>
                    JJTrace.Tracing.TraceLine($"Full duplex request reply {sequence}: {code:X8} {text}", System.Diagnostics.TraceLevel.Info)), command);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Full duplex request: " + ex, System.Diagnostics.TraceLevel.Error);
            if (reply != null) throw;
        }
    }

    public async Task<string> ToggleFullDuplexAsync()
    {
        if (FullDuplexAvailable != true) return "audio.full_duplex.not_reported";
        if (GuardRefuses("settings.guard.action.full_duplex")) return "audio.full_duplex.blocked";
        if (Interlocked.Exchange(ref _fullDuplexChanging, 1) != 0) return "audio.full_duplex.pending";
        try
        {
            var radio = theRadio;
            var connection = radio?.CurrentCommandConnection;
            if (radio == null || connection == null || FullDuplexAvailable != true) return "audio.full_duplex.unconfirmed";
            bool Live() => ReferenceEquals(theRadio, radio) && radio.Connected && ReferenceEquals(connection, radio.CurrentCommandConnection);
            Action<string> trace = note => JJTrace.Tracing.TraceLine("Full duplex control: " + note, System.Diagnostics.TraceLevel.Info);
            bool current = await ReadFullDuplexAsync(radio, connection, trace).ConfigureAwait(false);
            bool wanted = !current;
            if (!await SetFullDuplexConfirmedAsync(wanted, radio, connection, trace).ConfigureAwait(false))
                return "audio.full_duplex.unconfirmed";
            return Live() ? (wanted ? "audio.full_duplex.on" : "audio.full_duplex.off") : "audio.full_duplex.unconfirmed";
        }
        catch (InvalidOperationException ex) when (ex.Message == "full-duplex-refused")
        { return "audio.full_duplex.refused"; }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Full duplex confirmation: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            return "audio.full_duplex.unconfirmed";
        }
        finally { Volatile.Write(ref _fullDuplexChanging, 0); }
    }
}

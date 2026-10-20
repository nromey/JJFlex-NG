using System;
using System.Net;
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
    private sealed class FullDuplexDecision(Radio radio, CommandConnection connection, bool value)
    {
        internal Radio Radio { get; } = radio;
        internal CommandConnection Connection { get; } = connection;
        internal bool Value { get; } = value;
        internal int CheckStarted;
    }
    private FullDuplexDecision _fullDuplexDecision;
    internal Func<IPAddress, CancellationToken, Task<bool?>> FullDuplexLanQuery { get; set; } = FullDuplexLanProbe.ReadAsync;
    internal Task FullDuplexLanCheckTask { get; private set; } = Task.CompletedTask;
    // FlexLib has no FullDuplexAllowed flag. Missing state is unknown, not
    // unsupported. A reported full_duplex_enabled field offers the control.
    public bool? FullDuplexAvailable => theRadio?.Connected == true &&
        ReferenceEquals(theRadio, _fullDuplexReportedRadio) &&
        theRadio.CurrentCommandConnection?.Id == _fullDuplexReportedConnection ? true : null;

    // The 8600 does not resend radio status for a repeated sub radio all on
    // one connection. This helper is used only for xvtr, whose repeat was
    // measured to resend status. Full duplex uses the set reply instead.
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

    private bool FullDuplexState(Radio radio, CommandConnection connection)
    {
        var decision = Volatile.Read(ref _fullDuplexDecision);
        return decision != null && ReferenceEquals(decision.Radio, radio) &&
            ReferenceEquals(decision.Connection, connection) ? decision.Value : radio.FullDuplexEnabled;
    }

    private void AcceptFullDuplexReply(Radio radio, CommandConnection connection, bool value, bool scheduleLanCheck = true)
    {
        if (!radio.Connected || !ReferenceEquals(radio, theRadio) ||
            !ReferenceEquals(connection, radio.CurrentCommandConnection)) return;
        // FlexLib's setter updates this value locally, but would send a second,
        // untracked command. Keep the reply-proven state on the pinned rig instead.
        var decision = new FullDuplexDecision(radio, connection, value);
        Volatile.Write(ref _fullDuplexDecision, decision);
        if (scheduleLanCheck) BeginFullDuplexLanCheck();
    }

    // The control calls this after speaking the accepted result, so a fast
    // LAN disagreement cannot overtake the primary success announcement.
    public void BeginFullDuplexLanCheck()
    {
        var decision = Volatile.Read(ref _fullDuplexDecision);
        if (decision == null || Interlocked.Exchange(ref decision.CheckStarted, 1) != 0) return;
        FullDuplexLanCheckTask = VerifyFullDuplexLanAsync(decision);
    }

    private async Task VerifyFullDuplexLanAsync(FullDuplexDecision decision)
    {
        var radio = decision.Radio;
        if (radio.IsWan || RemoteRig || radio.IP == null) return;
        if (!ReferenceEquals(Volatile.Read(ref _fullDuplexDecision), decision) ||
            !radio.Connected || !ReferenceEquals(theRadio, radio) ||
            !ReferenceEquals(decision.Connection, radio.CurrentCommandConnection)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            bool? actual = await FullDuplexLanQuery(radio.IP, timeout.Token).ConfigureAwait(false);
            if (actual == null || actual == decision.Value ||
                !ReferenceEquals(Volatile.Read(ref _fullDuplexDecision), decision) ||
                !ReferenceEquals(radio, theRadio) || !radio.Connected ||
                !ReferenceEquals(decision.Connection, radio.CurrentCommandConnection)) return;
            // The check is silent on agreement and absence. A real disagreement
            // supersedes the local reply-proven state, and is announced once.
            var corrected = new FullDuplexDecision(radio, decision.Connection, actual.Value) { CheckStarted = 1 };
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _fullDuplexDecision, corrected, decision), decision)) return;
            JJTrace.Tracing.TraceLine($"Full duplex LAN check disagreed: requested={decision.Value} read={actual.Value}", System.Diagnostics.TraceLevel.Warning);
            if (!SuppressSpeech)
                ScreenReaderOutput.Speak(Lexicon.Get(actual.Value
                    ? "audio.full_duplex.disagreed_on" : "audio.full_duplex.disagreed_off"),
                    VerbosityLevel.Critical, interrupt: true);
        }
        catch (Exception ex)
        { JJTrace.Tracing.TraceLine("Full duplex LAN check unavailable: " + ex.Message, System.Diagnostics.TraceLevel.Info); }
    }

    private async Task<bool> ReadSettingAsync(Radio radio, CommandConnection connection,
        SavedRadioSetting setting, Action<string> log = null)
    {
        if (setting.Name != "xvtr-drive") throw new ArgumentOutOfRangeException(nameof(setting));
        string raw = await ReadStatusAsync(radio, connection, "sub xvtr all", line =>
        {
            int bar = line.IndexOf('|');
            if (bar < 0 || !line[(bar + 1)..].StartsWith(setting.StatusPrefix + " ", StringComparison.OrdinalIgnoreCase)) return false;
            return RadioStatusTap.Values(line[(bar + 1)..]).ContainsKey(setting.StatusKey);
        }, log).ConfigureAwait(false);
        return ListenbackRestoreEvidence.Matches(setting, raw);
    }

    private async Task<bool> SetFullDuplexConfirmedAsync(bool value, Radio radio,
        CommandConnection connection, Action<string> log = null, bool scheduleLanCheck = true)
    {
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RequestFullDuplexAsync(value, (sequence, code, response) =>
        {
            log?.Invoke($"set reply {sequence} {code:X8} {response}");
            if (code == 0) AcceptFullDuplexReply(radio, connection, value, scheduleLanCheck);
            reply.TrySetResult(code == 0);
        }, radio, connection).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(ListenbackConfirmationWait);
        if (!await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false)) throw new InvalidOperationException("full-duplex-refused");
        return radio.Connected && ReferenceEquals(radio, theRadio) &&
            ReferenceEquals(connection, radio.CurrentCommandConnection);
    }

    private async Task SetFullDuplexFromPropertyAsync(bool value)
    {
        var radio = theRadio;
        var connection = radio?.CurrentCommandConnection;
        try
        {
            if (!await SetFullDuplexConfirmedAsync(value, radio, connection).ConfigureAwait(false))
                JJTrace.Tracing.TraceLine("Full duplex property request: connection changed", System.Diagnostics.TraceLevel.Error);
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
                // Never update the local state before the reply.
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
            bool current = FullDuplexState(radio, connection);
            bool wanted = !current;
            if (!await SetFullDuplexConfirmedAsync(wanted, radio, connection, trace, scheduleLanCheck: false).ConfigureAwait(false))
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

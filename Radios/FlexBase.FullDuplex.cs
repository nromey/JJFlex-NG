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
        void Outcome(string result) => JJTrace.Tracing.TraceLine("Full duplex LAN check: " + result,
            System.Diagnostics.TraceLevel.Info);
        if (radio.IsWan) { Outcome("skipped: WAN"); return; }
        if (RemoteRig) { Outcome("skipped: RemoteRig"); return; }
        if (radio.IP == null) { Outcome("skipped: no IP"); return; }
        if (!ReferenceEquals(Volatile.Read(ref _fullDuplexDecision), decision))
        { Outcome("skipped: superseded"); return; }
        if (!radio.Connected || !ReferenceEquals(theRadio, radio) ||
            !ReferenceEquals(decision.Connection, radio.CurrentCommandConnection))
        { Outcome("skipped: connection changed"); return; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            bool? actual = await FullDuplexLanQuery(radio.IP, timeout.Token).ConfigureAwait(false);
            if (!ReferenceEquals(Volatile.Read(ref _fullDuplexDecision), decision))
            { Outcome("skipped: superseded"); return; }
            if (!ReferenceEquals(radio, theRadio) || !radio.Connected ||
                !ReferenceEquals(decision.Connection, radio.CurrentCommandConnection))
            { Outcome("skipped: connection changed"); return; }
            if (actual == null) { Outcome("unavailable"); return; }
            if (actual == decision.Value) { Outcome($"agreed: {actual.Value}"); return; }
            // The check is silent on agreement and absence. A real disagreement
            // supersedes the local reply-proven state, and is announced once.
            var corrected = new FullDuplexDecision(radio, decision.Connection, actual.Value) { CheckStarted = 1 };
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _fullDuplexDecision, corrected, decision), decision))
            { Outcome("skipped: superseded"); return; }
            Outcome($"disagreed: requested={decision.Value} read={actual.Value}");
            if (!SuppressSpeech)
                ScreenReaderOutput.Speak(Lexicon.Get(actual.Value
                    ? "audio.full_duplex.disagreed_on" : "audio.full_duplex.disagreed_off"),
                    VerbosityLevel.Critical, interrupt: true);
        }
        catch (OperationCanceledException) { Outcome("timed out"); }
        catch (Exception ex) { Outcome("unavailable: " + ex.Message); }
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

    /// <returns>True when the command went out; false when <paramref name="refuseAtRun"/> refused it at its run, so nothing was sent.</returns>
    private async Task<bool> RequestFullDuplexAsync(bool value, ReplyHandler reply, Radio expectedRadio = null, CommandConnection expectedConnection = null,
        Func<string> refuseAtRun = null)
    {
        var radio = expectedRadio ?? theRadio;
        var connection = expectedConnection ?? radio?.CurrentCommandConnection;
        bool sent = false;
        try
        {
            await IqQueue(() =>
            {
                if (radio == null || !radio.Connected || !ReferenceEquals(theRadio, radio) ||
                    !ReferenceEquals(connection, radio.CurrentCommandConnection)) throw new InvalidOperationException("connection-ended");
                // A check's restore asks again here, at the run (Codex round 2, R2-B1).
                if (refuseAtRun?.Invoke() is string why)
                {
                    JJTrace.Tracing.TraceLine("Full duplex request refused at its run: " + why, System.Diagnostics.TraceLevel.Warning);
                    return;
                }
                sent = true;
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
        return sent;
    }

    /// <summary>
    /// A check's restore of full duplex (the Loopback ending): asked at its
    /// queued run whether the radio transmits, by our Mox or its interlock,
    /// and refused then as every check restore is, so the owner waits for
    /// receive and tries again (Retry). Until Codex's round 2 (R2-B1) the
    /// owner asked before the entry and the queued send went out unasked.
    /// Done only on the radio's success reply, on the same connection.
    /// </summary>
    private async Task<RestoreOutcome> RestoreFullDuplexAsync(bool value, Radio radio, CommandConnection connection, Action<string> log)
    {
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sent;
        try
        {
            sent = await RequestFullDuplexAsync(value, (sequence, code, response) =>
            {
                log?.Invoke($"set reply {sequence} {code:X8} {response}");
                if (code == 0) AcceptFullDuplexReply(radio, connection, value);
                reply.TrySetResult(code == 0);
            }, radio, connection, RestoreRefusalAtRun).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Invoke("not sent: " + ex.Message);
            return RestoreOutcome.Failed;
        }
        if (!sent) return RestoreOutcome.Retry;
        bool accepted;
        try { accepted = await reply.Task.WaitAsync(ListenbackConfirmationWait).ConfigureAwait(false); }
        catch (TimeoutException) { log?.Invoke("no reply"); return RestoreOutcome.Failed; }
        return accepted && radio.Connected && ReferenceEquals(radio, theRadio) &&
            ReferenceEquals(connection, radio.CurrentCommandConnection) ? RestoreOutcome.Done : RestoreOutcome.Failed;
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

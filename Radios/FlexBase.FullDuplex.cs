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
            bool wanted = !radio.FullDuplexEnabled;
            bool Live() => ReferenceEquals(theRadio, radio) && radio.Connected && ReferenceEquals(connection, radio.CurrentCommandConnection);
            using var tap = new RadioStatusTap(radio);
            var evidence = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var setting = new SavedRadioSetting("full-duplex", wanted ? "1" : "0", "", "radio", "full_duplex_enabled");
            tap.Status += raw => { if (Live() && ListenbackRestoreEvidence.Matches(setting, raw)) evidence.TrySetResult(true); };
            await RequestFullDuplexAsync(wanted, (_, code, _) => reply.TrySetResult(code == 0), radio, connection).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(ListenbackConfirmationWait);
            if (!await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false)) return "audio.full_duplex.refused";
            await evidence.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return Live() ? (wanted ? "audio.full_duplex.on" : "audio.full_duplex.off") : "audio.full_duplex.unconfirmed";
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Full duplex confirmation: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            return "audio.full_duplex.unconfirmed";
        }
        finally { Volatile.Write(ref _fullDuplexChanging, 0); }
    }
}

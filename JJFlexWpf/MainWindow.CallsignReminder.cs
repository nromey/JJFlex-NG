using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// The startup reminder that the operator has no callsign (#1306).
/// </summary>
/// <remarks>
/// <para><b>Ruled by Noel 2026-11-24 13:43:</b> a reminder at startup when no
/// callsign is set, with "don't show this again", in his shape: "You currently
/// have no callsign set. To properly use logging and other callsign related
/// functionality, set your callsign using ..." It is about logging and the
/// other callsign features, NOT a transmit refusal, so it shows whatever the
/// callsign check says. Once per launch, through
/// <see cref="Dialogs.AdvisoryDialog.Show"/> with
/// <see cref="AdvisoryKeys.NoCallsignReminder"/>, so "don't show this again"
/// is kept per operator as every message's is since #1073.</para>
/// <para><b>Not on the launch that creates the first operator.</b> That launch
/// has just been through the operator form, which asks for the callsign
/// (<c>PersonalInfo.vb</c>); the application says so through
/// <see cref="OperatorCreatedThisLaunch"/>.</para>
/// <para><b>Where it fires, and why there (the orchestrator asked for this
/// choice to be stated).</b> It is decided once, in <see cref="SpeakWelcome"/>,
/// the first moment Home is visible on every launch, radio or not, after the
/// operator is loaded. The startup advisory chain cannot carry it alone, because
/// that chain only runs once a radio starts. And it takes its turn the way that
/// chain's advisories do, so it never opens a window over another notice or
/// talks over Home's arrival:</para>
/// <list type="bullet">
/// <item><b>No connect in flight at the welcome</b> (no radio, or the startup
/// connect already over): the reminder takes the turn there and then. Home's
/// focus grab, its arrival sentence and any connect statement are parked
/// behind it and replayed when it closes, exactly as the chain replays them.</item>
/// <item><b>A connect in flight</b> (the usual launch with a radio): it waits
/// for the chain and is shown as the chain's LAST advisory, after the SmartLink
/// and firmware ones, so the connect statement is still parked behind it.</item>
/// <item><b>The connect ends with no radio</b>, so the chain never runs: a
/// watch shows it once Home is settled and nothing else has the floor. This
/// is the one path where Home's landing has already been spoken when the
/// reminder opens.</item>
/// </list>
/// <para>In every path it waits while a dialog of ours is up
/// (<see cref="WindowFocusForcer.AttentionWindowOpen"/>) or another window of
/// ours holds the foreground, so it never takes focus from a dialog the
/// operator is in, and it asks again at the moment it would show: a callsign
/// entered meanwhile, or the reminder silenced, means nothing is shown.</para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Reads the current operator's callsign, or null when there is no operator.
    /// Set by the application at startup (globals.vb), as
    /// <c>OpenParms.GetOperatorCallsign</c> is for the radio. Null: no reminder.
    /// </summary>
    public Func<string?>? OperatorCallsignSource { get; set; }

    /// <summary>
    /// True on the launch that created the first operator through the operator
    /// form, which already asked for the callsign. Set by the application.
    /// </summary>
    public bool OperatorCreatedThisLaunch { get; set; }

    /// <summary>Decided once per launch, at the welcome.</summary>
    private bool _callsignReminderDecided;

    /// <summary>Owed and not yet shown (or found no longer needed).</summary>
    private bool _callsignReminderDue;

    /// <summary>True while the reminder holds the startup turn itself, outside the chain.</summary>
    private bool _callsignReminderTurn;

    /// <summary>
    /// Home's arrival, parked behind a reminder that took the turn at the
    /// welcome; replayed with the focus grab when the turn ends. Returns true
    /// only when its where-you-are sentence was admitted for speech
    /// (<see cref="RequestWelcomeArrival"/>).
    /// </summary>
    private Func<bool>? _pendingArrival;

    /// <summary>
    /// True from the moment <see cref="ReplayParkedBringUp"/> runs a parked
    /// arrival whose sentence was admitted for speech, until the next
    /// return-to-app landing takes it, whatever that landing then does
    /// (<see cref="TakeArrivalJustReplayed"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Why (repair round 4).</b> The reminder is a dialog of ours, and
    /// every dialog of ours posts the "a dialog closed back into the app"
    /// landing when it closes, at ApplicationIdle. The reminder's turn ends
    /// synchronously, before that landing runs, and its replay speaks Home's
    /// parked arrival. With no radio the landing then said "JJ Flexible Home,
    /// no radio connected" behind it, about three seconds later: five of five
    /// reminder closes on the bench. So the landing that follows a replayed
    /// arrival leaves out its own where-you-are sentence, and only that: its
    /// focus repair runs as always.</para>
    /// <para><b>Only when the arrival was said (repair round 5).</b> At speech
    /// verbosity Off the arrival, a Terse sentence, is kept back by the
    /// verbosity gate, while the landing's own sentence is Critical and is
    /// spoken. Marking because the callback merely returned silenced both, and
    /// the operator heard nothing about where they were. So the arrival reports
    /// whether the speech channel admitted it, and only that sets the mark.</para>
    /// <para><b>One landing only.</b> The landing takes it first thing, on every
    /// exit, and so does the dialog-close callback when the connect quiet scope
    /// stands the landing down. A turn whose window did not close into a
    /// landing (not shown, suppressed, or a show that failed) posts no landing,
    /// so <see cref="EndCallsignReminderTurn"/> clears it there; it cannot wait
    /// for a later, unrelated dialog close and silence that.</para>
    /// <para><b>The advisory chain.</b> It shares <see cref="ReplayParkedBringUp"/>
    /// but never has an arrival parked: only the reminder's own turn at the
    /// welcome parks one, and that turn's end replays it before the chain can
    /// run. So the chain's replay sets nothing and its landings are unchanged.</para>
    /// </remarks>
    private bool _arrivalJustReplayed;

    /// <summary>Reads and clears <see cref="_arrivalJustReplayed"/>: it belongs to one landing.</summary>
    private bool TakeArrivalJustReplayed()
    {
        bool replayed = _arrivalJustReplayed;
        _arrivalJustReplayed = false;
        return replayed;
    }

    /// <summary>The decision, pure, so a test can hold it without a window.</summary>
    internal static bool CallsignReminderApplies(string? callsign, bool operatorCreatedThisLaunch, bool silenced)
        => !operatorCreatedThisLaunch && !silenced && string.IsNullOrWhiteSpace(callsign);

    /// <summary>
    /// The decision as of now. Anything that cannot be read means no reminder:
    /// a reminder is never worth an error.
    /// </summary>
    private bool CallsignReminderAppliesNow()
    {
        var source = OperatorCallsignSource;
        if (source == null) return false;
        try
        {
            return CallsignReminderApplies(source(), OperatorCreatedThisLaunch,
                AdvisorySuppression.IsSuppressed(AdvisoryKeys.NoCallsignReminder));
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("CallsignReminder: could not decide, so no reminder: " + ex.Message, TraceLevel.Warning);
            return false;
        }
    }

    /// <summary>
    /// Called once, from <see cref="SpeakWelcome"/>, before its focus grab and
    /// its arrival. True when the reminder takes the turn now, so the caller
    /// must park Home's arrival in <see cref="_pendingArrival"/>.
    /// </summary>
    private bool TakeCallsignReminderTurnAtWelcome()
    {
        if (_callsignReminderDecided) return false;
        _callsignReminderDecided = true;
        if (!CallsignReminderAppliesNow()) return false;
        _callsignReminderDue = true;

        if (_advisorySequenceActive || ConnectBriefing.Current.InFlight)
        {
            Tracing.TraceLine("CallsignReminder: owed; waiting for the connect and its advisories", TraceLevel.Info);
            WatchForCallsignReminderTurn();
            return false;
        }

        Tracing.TraceLine("CallsignReminder: owed; taking the startup turn now", TraceLevel.Info);
        _callsignReminderTurn = true;
        _advisorySequenceActive = true;   // parks the welcome's focus grab and any settle
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RunCallsignReminderTurn));
        return true;
    }

    /// <summary>The reminder's own turn, begun at the welcome.</summary>
    private async void RunCallsignReminderTurn()
    {
        bool shown = false;
        try
        {
            await WaitUntilNoDialogOfOursIsUpAsync();
            shown = ShowCallsignReminderIfDue();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("CallsignReminder: " + ex.Message, TraceLevel.Warning);
        }
        finally
        {
            EndCallsignReminderTurn(shown);
        }
    }

    /// <summary>
    /// The chain's last step: shown inside the chain's turn, so the connect
    /// statement stays parked behind it.
    /// </summary>
    private async Task ShowCallsignReminderInChainAsync()
    {
        if (!_callsignReminderDue) return;
        await WaitUntilNoDialogOfOursIsUpAsync();
        ShowCallsignReminderIfDue();
    }

    /// <summary>
    /// The chain waits while the reminder holds its own turn, so two notices
    /// are never stacked on each other.
    /// </summary>
    private async Task WaitForCallsignReminderTurnAsync()
    {
        while (_callsignReminderTurn) await Task.Delay(250);
    }

    /// <summary>
    /// The connect ended with no radio, so no chain will show it: once Home is
    /// settled and nothing else has the floor, take a turn and show it.
    /// </summary>
    private async void WatchForCallsignReminderTurn()
    {
        try
        {
            while (_callsignReminderDue)
            {
                await Task.Delay(500);
                if (!_callsignReminderDue) return;
                if (_advisorySequenceActive || ConnectBriefing.Current.InFlight
                    || WindowFocusForcer.AttentionWindowOpen || AnotherOwnWindowHasForeground())
                    continue;
                _callsignReminderTurn = true;
                _advisorySequenceActive = true;
                bool shown = false;
                try { shown = ShowCallsignReminderIfDue(); }
                finally { EndCallsignReminderTurn(shown); }
                return;
            }
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("CallsignReminder: watch: " + ex.Message, TraceLevel.Warning);
        }
    }

    private async Task WaitUntilNoDialogOfOursIsUpAsync()
    {
        while (WindowFocusForcer.AttentionWindowOpen || AnotherOwnWindowHasForeground())
            await Task.Delay(500);
    }

    /// <summary>
    /// Spends the reminder: shown once, if it still applies. True only when its
    /// window was shown and its close posted the return-to-app landing
    /// (<see cref="Dialogs.AdvisoryDialog.ShowAndReportLanding"/>); suppressed,
    /// or a show that failed, is false.
    /// </summary>
    private bool ShowCallsignReminderIfDue()
    {
        if (!_callsignReminderDue) return false;
        _callsignReminderDue = false;
        if (!CallsignReminderAppliesNow())
        {
            Tracing.TraceLine("CallsignReminder: no longer needed, not shown", TraceLevel.Info);
            return false;
        }
        Tracing.TraceLine("CallsignReminder: shown", TraceLevel.Info);
        // DRAFT words (Lexicon).
        return Dialogs.AdvisoryDialog.ShowAndReportLanding(
            Lexicon.Get("connect.startup.no_callsign_reminder_title"),
            Lexicon.Get("connect.startup.no_callsign_reminder_body"),
            AdvisoryKeys.NoCallsignReminder);
    }

    /// <summary>
    /// Ends the reminder's own turn and replays what waited behind it.
    /// <paramref name="landingPosted"/> false means no window closed into a
    /// landing, so none is coming to take <see cref="_arrivalJustReplayed"/>:
    /// it is cleared here instead.
    /// </summary>
    private void EndCallsignReminderTurn(bool landingPosted)
    {
        _callsignReminderTurn = false;
        _advisorySequenceActive = false;
        ReplayParkedBringUp();
        if (!landingPosted) _arrivalJustReplayed = false;
    }

    /// <summary>
    /// Bring-up that arrived while a startup notice had the floor gets its turn
    /// now that the operator is done reading: focus first (the parked welcome
    /// focus grab), then Home's parked arrival, then the parked connect settle,
    /// which speaks the composed statement and releases the Home arrival behind
    /// it. One policy for the advisory chain and the callsign reminder alike.
    /// </summary>
    private void ReplayParkedBringUp()
    {
        if (_welcomeFocusPending)
        {
            _welcomeFocusPending = false;
            try { FocusHome(); } catch { /* window may be closing */ }
        }
        if (_pendingArrival is { } arrive)
        {
            _pendingArrival = null;
            try
            {
                // Marked only when the arrival was admitted for speech: Home's
                // arrival has then said where the operator is, and the landing
                // the closing window posted must not say it again. Held for a
                // settle, or kept back by the verbosity setting, it said
                // nothing, and the landing's own sentence is the one left.
                if (arrive())
                    _arrivalJustReplayed = true;
                else
                    Tracing.TraceLine("CallsignReminder: Home's arrival was not spoken now; the landing still says where the operator is",
                        TraceLevel.Info);
            }
            catch (Exception ex) { Tracing.TraceLine("CallsignReminder: arrival: " + ex.Message, TraceLevel.Warning); }
        }
        if (_pendingSettle is { } settle)
        {
            _pendingSettle = null;
            ConnectBriefing.Current.Settle(settle.lead, settle.censusFull);
        }
    }
}

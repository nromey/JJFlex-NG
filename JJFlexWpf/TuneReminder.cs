using System;
using System.Diagnostics;
using JJTrace;
using Radios;
using Radios.Speech;

namespace JJFlexWpf;

/// <summary>
/// Tell the operator a tune carrier is still up (#860). Speech and a tone only;
/// nothing here touches the carrier, which stays unheld by Noel's ruling of
/// 2026-10-22.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways it ends, and both matter.</b> The tick reads the radio every
/// second, which catches anything. But a sentence cut off is held and said
/// again about 600 ms later, so a one-second poll is too late to retire it, and
/// turning tune off because of the reminder is the ordinary way this ends. So
/// every real edge (tune off, the radio dropping tune or going to receive, a
/// lost radio, power off, the events being unwired) calls <see cref="End"/> at
/// once.
/// </para>
/// <para>
/// <b>A carrier a check holds is told from the rest by who raised it</b>
/// (<see cref="TransmitKillSwitch.HoldsTuneCarrier"/>), not by something being
/// armed: an ordinary box send is armed too, and a tune pressed during one must
/// still be told.
/// </para>
/// </remarks>
internal sealed class TuneReminder
{
    private readonly TuneReminderClock _clock = new();
    private readonly Action<string, string> _retire;

    /// <summary>For tests: stands in for the arbiter's withdrawal, so the moment it is called can be held.</summary>
    internal TuneReminder(Action<string, string>? retire = null) =>
        _retire = retire ?? ((subject, why) => ScreenReaderOutput.Supersede(subject, why));

    /// <summary>Called each second.</summary>
    /// <param name="up">The radio reports this client's tune on the air.</param>
    /// <param name="ownedByACheck">A transmit check holds that carrier.</param>
    /// <param name="nowMs">A monotonic clock.</param>
    /// <param name="seconds">The set length.</param>
    internal void Tick(bool up, bool ownedByACheck, long nowMs, int seconds)
    {
        switch (_clock.Tick(up, ownedByACheck, nowMs, seconds))
        {
            case TuneReminderStep.Remind:
                Tracing.TraceLine("TuneReminder: the carrier has been up another " + seconds + " s", TraceLevel.Info);
                EarconPlayer.Warning1Beep();
                ScreenReaderOutput.Speak(Lexicon.Get("audio.tune.still_on"), SpeechIntent.Queue,
                    VerbosityLevel.Critical, subject: SpeechSubject.TuneStillOn);
                break;
            case TuneReminderStep.Over:
                _retire(SpeechSubject.TuneStillOn, "the tune carrier going off");
                break;
        }
    }

    /// <summary>The carrier has gone, or the radio has: nothing about it is true any more.</summary>
    internal void End(string why)
    {
        _clock.Reset();
        _retire(SpeechSubject.TuneStillOn, why);
    }
}

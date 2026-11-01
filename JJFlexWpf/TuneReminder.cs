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
/// again about 600 ms later, on the speech arbiter's own timer, so neither a
/// one-second poll nor a post to the UI thread is soon enough to retire it, and
/// turning tune off because of the reminder is the ordinary way this ends. So
/// every real edge calls <see cref="End"/> at once, on whatever thread it was
/// raised on, FlexLib's read loop included. That is safe: the arbiter's
/// withdrawal takes its own short lock and sends nothing to the screen reader,
/// and nothing here touches the UI.
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
    private readonly object _gate = new();
    private readonly Action<string, string> _retire;
    private readonly Action _tone;
    private readonly Action _say;
    private int _ends;

    /// <param name="retire">For tests: stands in for the arbiter's withdrawal, so the moment it is called can be held.</param>
    /// <param name="say">For tests: stands in for the sentence.</param>
    /// <param name="tone">For tests: stands in for the tone.</param>
    internal TuneReminder(Action<string, string>? retire = null, Action? say = null, Action? tone = null)
    {
        _retire = retire ?? ((subject, why) => ScreenReaderOutput.Supersede(subject, why));
        _say = say ?? SayIt;
        _tone = tone ?? EarconPlayer.Warning1Beep;
    }

    private static void SayIt() => ScreenReaderOutput.Speak(Lexicon.Get("audio.tune.still_on"), SpeechIntent.Queue,
        VerbosityLevel.Critical, subject: SpeechSubject.TuneStillOn);

    /// <summary>Called each second, on the UI thread.</summary>
    /// <param name="up">The radio reports this client's tune on the air.</param>
    /// <param name="ownedByACheck">A transmit check holds that carrier.</param>
    /// <param name="nowMs">A monotonic clock.</param>
    /// <param name="seconds">The set length.</param>
    internal void Tick(bool up, bool ownedByACheck, long nowMs, int seconds)
    {
        TuneReminderStep step;
        int ends;
        lock (_gate)
        {
            step = _clock.Tick(up, ownedByACheck, nowMs, seconds);
            ends = _ends;
        }
        switch (step)
        {
            case TuneReminderStep.Remind:
                Tracing.TraceLine("TuneReminder: the carrier has been up another " + seconds + " s", TraceLevel.Info);
                _tone();    // outside the gate: a first sound can open a device, and End runs on the read loop
                // The gate is held through the sentence, so an edge cannot finish
                // between the question and the queueing: it is either already
                // counted here, or its withdrawal comes after and covers it.
                // Safe to hold: a queued Speak takes the arbiter's own lock and
                // returns, with no dispatcher and no call back into this class,
                // and End lets go of this gate before it touches the arbiter, so
                // the two locks are never taken the other way round.
                lock (_gate)
                {
                    if (_ends == ends) _say();
                }
                break;
            case TuneReminderStep.Over:
                _retire(SpeechSubject.TuneStillOn, "the tune carrier going off");
                break;
        }
    }

    /// <summary>The carrier has gone, or the radio has: nothing about it is true any more. Any thread.</summary>
    internal void End(string why)
    {
        lock (_gate)
        {
            _clock.Reset();
            _ends++;
        }
        _retire(SpeechSubject.TuneStillOn, why);
    }
}

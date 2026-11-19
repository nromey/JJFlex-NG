using System;
using System.Collections.Generic;
using System.Diagnostics;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>
    /// The receive warning when a computer engine cannot keep up (Sprint 49
    /// Track L, #866). The pipeline's rule decides when an engine has fallen
    /// behind (<see cref="EngineKeepUpRule"/>); this half decides what the
    /// operator hears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never switches anything.</b> #866, and the memory it cites: the
    /// operator judges which engine to run. The warning names the engine and
    /// suggests a lighter one or a lighter setting; the engine keeps running,
    /// late frames and all, until the operator changes it.
    /// </para>
    /// <para>
    /// <b>Receive only</b> (ruled 2026-11-19, X3): transmit has its own
    /// mid-over notice, and this half never reads the transmit chain.
    /// </para>
    /// <para>
    /// <b>Said once per engine, again only after it has been seen to keep up
    /// and then falls behind afresh</b> (the draft's judgement; for Noel).
    /// "Seen to keep up" is the rule's <see cref="EngineKeepUpChange.Recovered"/>,
    /// or its <see cref="EngineKeepUpChange.KeepingUp"/> after the engine started
    /// again: switching away from a slow engine and back is not a recovery,
    /// and the engine has to show a clean run before it can be warned about
    /// again.
    /// </para>
    /// <para>
    /// <b>The tone</b> is the one transmit's notice plays, the calm "problem
    /// recorded" tone on the alert channel only, so the two notices sound
    /// alike (the draft's judgement; for Noel). Queued behind whatever is being
    /// said, never cutting it: nobody asked for it.
    /// </para>
    /// </remarks>
    public sealed partial class NoiseController
    {
        /// <summary>
        /// Plays the warning's tone; true when it played. The alert channel's
        /// "problem recorded" tone by default; a test gives its own, so no test
        /// opens an audio device.
        /// </summary>
        internal Func<bool> KeepUpTone { get; set; } = EarconPlayer.ProblemRecordedToneOnAlertChannelOnly;

        /// <summary>Engines warned about and not seen to keep up since.</summary>
        private readonly HashSet<string> _keepUpWarned = new(StringComparer.Ordinal);

        /// <summary>The engines warned about and not yet seen to keep up, for tests.</summary>
        internal IReadOnlyCollection<string> KeepUpWarned => _keepUpWarned;

        /// <summary>
        /// A keep-up change from the receive pipeline. The listener marshals to
        /// the UI thread before calling.
        /// </summary>
        public void OnKeepUpResult(EngineKeepUpResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.EngineId)) return;
            switch (result.Change)
            {
                case EngineKeepUpChange.FellBehind:
                    if (!_keepUpWarned.Add(result.EngineId))
                    {
                        Trace.WriteLine($"NoiseController: {result.EngineId} fell behind again without keeping up in between; not said again");
                        return;
                    }
                    bool toned = false;
                    try { toned = KeepUpTone(); }
                    catch (Exception ex) { Trace.WriteLine("NoiseController: the keep-up tone could not play: " + ex.Message); }
                    string name = _catalog.Find(result.EngineId) is { } row ? Lexicon.Get(row.LabelKey) : result.EngineId;
                    Trace.WriteLine($"NoiseController: {result.EngineId} cannot keep up; said once, {(toned ? "with" : "without")} the tone; nothing switched");
                    SayAfter(L("audio.noise_surface.cannot_keep_up", ("engine", name)), SpeechSubject.NoiseKeepUp);
                    break;
                case EngineKeepUpChange.KeepingUp:
                case EngineKeepUpChange.Recovered:
                    if (_keepUpWarned.Remove(result.EngineId))
                        Trace.WriteLine($"NoiseController: {result.EngineId} is keeping up again; a fresh fall may be said");
                    break;
            }
        }
    }
}

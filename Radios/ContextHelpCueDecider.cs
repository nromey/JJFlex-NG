using System;

namespace Radios
{
    /// <summary>
    /// The decision half of the context-help availability earcon (#275):
    /// given the help content resolved at the control focus has settled on,
    /// should the cue sound?
    ///
    /// THE RULE IS "SOUND ONLY WHEN THE HELP CONTENT CHANGES", with a
    /// 1.5 second rate limit as a backstop, not as the primary rule. Tabbing
    /// through five controls with no help, or five sharing the same help,
    /// is silent. The cue is unintrusive because it is RARE, not because it
    /// is quiet — a cue that fires on every control teaches the operator to
    /// ignore it, after which its volume does not matter.
    ///
    /// Pure logic with the clock passed in, so Radios.Tests can prove every
    /// scenario without waiting — the FrequencyEchoGuard pattern. The WPF
    /// half (focus watching, the settle timer, the tone itself) lives in
    /// JJFlexWpf.ContextHelpCue.
    /// </summary>
    public sealed class ContextHelpCueDecider
    {
        /// <summary>
        /// The rate-limit backstop. With the settle delay in front of this
        /// decider, two cues are already at least a settle apart, so in normal
        /// operation this floor is never the deciding factor — it exists so
        /// that no future caller can turn the cue into a machine gun.
        /// </summary>
        public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(1.5);

        private string? _lastCued;
        private DateTime _lastCuedAtUtc = DateTime.MinValue;

        /// <summary>
        /// Decide whether the cue should sound for this content, and record it
        /// as heard when the answer is yes.
        /// </summary>
        /// <param name="helpContent">The help Ctrl+F1 would speak at the
        /// settled focus, or null/empty when there is none.</param>
        /// <param name="nowUtc">The current time, injected for testability.</param>
        public bool ShouldCue(string? helpContent, DateTime nowUtc)
        {
            // No help: silent, and the last-cued memory is deliberately KEPT.
            // Crossing a bare stretch and returning to the same helped control
            // stays silent — the operator already knows that one has help.
            if (string.IsNullOrWhiteSpace(helpContent)) return false;

            // Same content as the last cue: redundancy, not information.
            if (string.Equals(helpContent, _lastCued, StringComparison.Ordinal))
                return false;

            // Backstop only. On suppression the content is NOT recorded as
            // cued — it was never announced, so a later settle on it may still
            // sound.
            if (nowUtc - _lastCuedAtUtc < MinimumGap) return false;

            _lastCued = helpContent;
            _lastCuedAtUtc = nowUtc;
            return true;
        }

        /// <summary>
        /// The operator just HEARD this content — Ctrl+F1 spoke it — so
        /// cueing its availability afterwards would announce what they already
        /// have. Records the content without consuming the rate limit.
        /// </summary>
        public void NoteSpoken(string? helpContent)
        {
            if (string.IsNullOrWhiteSpace(helpContent)) return;
            _lastCued = helpContent;
        }
    }
}

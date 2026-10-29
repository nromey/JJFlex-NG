using System.Threading;

namespace Radios
{
    /// <summary>
    /// Counts the key presses this app's low-level keyboard hooks swallow, so
    /// a window can tell that a key went down which it never saw (#835).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists (Codex, round 1029 Track C, blocker B1).</b> The
    /// system-wide JJ key's hook passes modifiers through and swallows the
    /// letter of a JJ chord, so Ctrl+Shift+J and then Ctrl+K reaches a WPF
    /// window as a Control press and release with nothing between. Check your
    /// audio's countdown skip reads exactly that as a bare Control and keyed.
    /// <c>handledEventsToo</c> sees events WPF marked handled; it cannot see an
    /// event the hook never delivered.
    /// </para>
    /// <para>
    /// <b>Ordering is guaranteed, not hoped for.</b> The count is bumped inside
    /// the hook callback, before the hook returns, on the hook's own thread.
    /// The Control release that follows passes through the same callback
    /// afterwards, and only then reaches the window. So by the time a window
    /// reads the count on that release, every swallowed press before it has
    /// been counted. No posting, no dispatcher order to rely on.
    /// </para>
    /// <para>
    /// An instance, with one process-wide <see cref="Shared"/> the hooks
    /// report to, so a test can drive its own counter without another test's
    /// keys landing in it.
    /// </para>
    /// </remarks>
    public sealed class WithheldKeyCounter
    {
        /// <summary>The counter the app's hooks report to.</summary>
        public static WithheldKeyCounter Shared { get; } = new();

        private long _downs;

        /// <summary>How many key presses have been withheld so far. Only ever grows.</summary>
        public long Downs => Interlocked.Read(ref _downs);

        /// <summary>
        /// One decision of the system-wide key hook. Counts a swallowed PRESS
        /// of a key that is not a modifier; releases, and anything passed on,
        /// are left alone.
        /// </summary>
        public void Report(uint vk, bool isDown, SystemWideDecision decision)
        {
            if (decision.Swallow && isDown && !SystemWideChord.IsModifierVirtualKey(vk))
                NoteWithheldDown();
        }

        /// <summary>A hook swallowed a key press outright.</summary>
        public void NoteWithheldDown() => Interlocked.Increment(ref _downs);
    }
}

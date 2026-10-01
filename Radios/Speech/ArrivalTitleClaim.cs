#nullable enable
using System;

namespace Radios.Speech
{
    /// <summary>
    /// Who owns the title currently pending under
    /// <see cref="SpeechSubject.DialogArrival"/> — the identity a dialog's
    /// close hook checks before it withdraws anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why ownership has to be a fact, not an assumption (#551, Sol's Track J
    /// review).</b> <c>DialogArrival</c> is one global subject, so a close hook
    /// that superseded it unconditionally withdrew whatever title was pending —
    /// including a SUCCESSOR's. The startup handoff overlaps two windows on
    /// purpose: the outgoing search window is closed only after the picker has
    /// rendered, and the picker queues its own title from Loaded first. The
    /// outgoing window's Closed then fired second and took the picker's title
    /// back before it was ever said — deleting a title the operator needs, in
    /// the name of removing one they did not.
    /// </para>
    /// <para>
    /// So the rule is "did I queue the title that is still pending". A window
    /// that spoke its title claims; the next window to speak replaces the
    /// claim; a closing window withdraws only if the claim is still its own. A
    /// window that never spoke a title never claims, and therefore can never
    /// withdraw somebody else's.
    /// </para>
    /// <para>
    /// Lives here rather than in the WPF dialog so the handoff ORDER can be
    /// driven in a test without constructing a window: the rule is about
    /// identity and sequence, and nothing in it is visual. The dialog holds one
    /// static instance and calls it from its UI thread, so no lock is taken —
    /// Loaded and Closed both run there. Owners are compared by reference and
    /// never dereferenced, so a held reference keeps no window alive in any
    /// way that matters.
    /// </para>
    /// </remarks>
    public sealed class ArrivalTitleClaim
    {
        private object? _owner;

        /// <summary>This window has just queued its title; it now owns the pending one.</summary>
        public void Claim(object window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            _owner = window;
        }

        /// <summary>
        /// This window is closing. True when it still owned the pending title —
        /// the caller should then supersede the subject — and false when a
        /// later window has claimed it, in which case nothing is withdrawn.
        /// Either way this window's own claim, if any, is gone.
        /// </summary>
        public bool Release(object window)
        {
            if (!ReferenceEquals(_owner, window)) return false;
            _owner = null;
            return true;
        }

        /// <summary>For the trace and for tests: whether this window currently owns the pending title.</summary>
        public bool IsHeldBy(object window) => ReferenceEquals(_owner, window);

        /// <summary>True while no title is pending under the subject.</summary>
        public bool IsEmpty => _owner == null;
    }
}

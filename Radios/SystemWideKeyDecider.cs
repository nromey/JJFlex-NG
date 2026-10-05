using System;
using System.Windows.Forms;

namespace Radios
{
    /// <summary>What one low-level key event means for the system-wide keys.</summary>
    public enum SystemWideAction
    {
        /// <summary>Nothing of ours.</summary>
        None,

        /// <summary>The system-wide JJ key went down: open the layer and wait for the next key.</summary>
        LeaderArmed,

        /// <summary>Escape while the layer was waiting: close it.</summary>
        LeaderCancelled,

        /// <summary>The key after the JJ key, with its modifiers, in <see cref="SystemWideDecision.Key"/>.</summary>
        LeaderKey,

        /// <summary>First down of the push-to-talk chord: key the transmitter.</summary>
        PttDown,

        /// <summary>The push-to-talk key came up: unkey.</summary>
        PttUp,

        /// <summary>First down of the transmit-lock chord: toggle the lock.</summary>
        LockToggle,
    }

    /// <summary>
    /// The verdict on one key event: whether the program that has the
    /// keyboard gets to see it, and what we do about it.
    /// </summary>
    public readonly struct SystemWideDecision
    {
        public SystemWideDecision(bool swallow, SystemWideAction action, Keys key = Keys.None, bool helpLetGo = false)
        {
            Swallow = swallow;
            Action = action;
            Key = key;
            HelpLetGo = helpLetGo;
        }

        /// <summary>True: the focused program never sees this event.</summary>
        public bool Swallow { get; }

        public SystemWideAction Action { get; }

        /// <summary>For <see cref="SystemWideAction.LeaderKey"/>: the second key with its modifiers.</summary>
        public Keys Key { get; }

        /// <summary>
        /// The layer had said "H for the list, Escape to cancel" and the
        /// operator pressed something else: it has let go, and the host should
        /// clear its own help-armed flag. The event itself is NOT swallowed.
        /// </summary>
        public bool HelpLetGo { get; }

        public static readonly SystemWideDecision Pass = new(false, SystemWideAction.None);
        public static readonly SystemWideDecision Eat = new(true, SystemWideAction.None);
    }

    /// <summary>
    /// The pure decision behind the system-wide keyboard hook (#307): given
    /// one low-level key event and what has gone before, say whether to
    /// swallow it and what it means. No Win32, no timers, no radio — so a
    /// test can replay the probe's measured streams against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two rules the probe paid for, 2026-10-05.</b> First: a held key
    /// repeats its DOWN every ~30 ms and sends ONE UP, so the first down keys,
    /// every repeat is eaten and ignored, and the up unkeys — anything else
    /// keys and unkeys the radio thirty times a second. Second: <b>a release
    /// is matched by KEY CODE ALONE.</b> The probe's second run logged 110
    /// downs and one up because it matched the release against live modifier
    /// state, and an operator lifts a modifier before the main key. A
    /// push-to-talk built that way keys the radio and never hears the release.
    /// </para>
    /// <para>
    /// <b>Modifier events are never ours.</b> Shift, Ctrl, Alt and the
    /// Windows keys pass through untouched in every state, including while
    /// the layer is waiting for its second key — the operator reaching for
    /// Shift+A must not have the Shift read as the second key.
    /// </para>
    /// <para>
    /// <b>The leader eats both edges of the second key.</b> The probe
    /// swallowed only the DOWN of the digit; its UP would then have landed in
    /// the logger as a stray key-up. Both edges are kept here.
    /// </para>
    /// <para>
    /// <b>Parity with the in-app layer after an unknown key.</b> Inside the
    /// window, "Unknown key. H for the list, Escape to cancel" leaves the
    /// layer armed for exactly those keys (#303). The host tells this class
    /// when that happened (<see cref="SetHelpArmed"/>), and the next key is
    /// then H, the slash key or Escape — ours — or anything else, which lets
    /// go and passes through, exactly as the in-app layer does.
    /// </para>
    /// <para>
    /// Not thread-safe by itself. The hook callback and the fail-safe poll
    /// both touch it, and the host holds one lock around every call; the
    /// calls are a handful of comparisons, so the lock is never contended for
    /// longer than that.
    /// </para>
    /// </remarks>
    public sealed class SystemWideKeyDecider
    {
        private const uint VK_ESCAPE = 0x1B;
        private const uint VK_H = 0x48;
        private const uint VK_OEM_2 = 0xBF;   // the slash key, Shift or not

        private SystemWideKeySet _set = SystemWideKeySet.Off;

        private bool _pttHeld;
        private bool _lockDown;
        private bool _leaderDown;
        private bool _leaderArmed;
        private bool _helpArmed;
        private uint _secondKeyVk;

        /// <summary>The chords in force. Replaceable live; held state survives the swap.</summary>
        public SystemWideKeySet Set
        {
            get => _set;
            set => _set = value ?? SystemWideKeySet.Off;
        }

        /// <summary>True while a push-to-talk down has been seen and its up has not.</summary>
        public bool PttHeld => _pttHeld;

        /// <summary>True while the layer is waiting for the key after the JJ key.</summary>
        public bool LeaderArmed => _leaderArmed;

        /// <summary>
        /// The in-app layer answered an unknown key and is waiting for H, the
        /// slash key or Escape — so this class must claim exactly those three
        /// next and nothing else.
        /// </summary>
        public void SetHelpArmed(bool armed) => _helpArmed = armed;

        /// <summary>
        /// The fail-safe decided the release was missed and unkeyed without
        /// us: forget the hold, so the next down is a fresh press and a stale
        /// up is nobody's business.
        /// </summary>
        public void ForceReleasePtt() => _pttHeld = false;

        /// <summary>Forget every edge in flight. Used at teardown.</summary>
        public void Reset()
        {
            _pttHeld = false;
            _lockDown = false;
            _leaderDown = false;
            _leaderArmed = false;
            _helpArmed = false;
            _secondKeyVk = 0;
        }

        /// <summary>
        /// One low-level key event. <paramref name="held"/> is what Windows
        /// says about the modifiers at this instant; it is consulted only on a
        /// DOWN, never to recognise a release.
        /// </summary>
        public SystemWideDecision Decide(uint vk, bool isDown, SystemWideModifiers held)
        {
            var set = _set;
            if (!set.Enabled) return SystemWideDecision.Pass;
            if (SystemWideChord.IsModifierVirtualKey(vk)) return SystemWideDecision.Pass;

            if (!isDown) return DecideUp(vk, set);
            return DecideDown(vk, held, set);
        }

        private SystemWideDecision DecideUp(uint vk, SystemWideKeySet set)
        {
            // Releases are matched by key code, whatever the modifiers are
            // doing by now. More than one owner can release on the same event
            // only if two roles share a key code (Space for both Space chords),
            // and then the first owner's action is the one that matters.
            bool swallow = false;
            var action = SystemWideAction.None;

            if (_pttHeld && vk == SystemWideChord.VirtualKeyOf(set.PushToTalk))
            {
                _pttHeld = false;
                action = SystemWideAction.PttUp;
                swallow = true;
            }
            if (_lockDown && vk == SystemWideChord.VirtualKeyOf(set.TransmitLock))
            {
                _lockDown = false;
                swallow = true;
            }
            if (_leaderDown && vk == SystemWideChord.VirtualKeyOf(set.Leader))
            {
                _leaderDown = false;
                swallow = true;
            }
            if (_secondKeyVk != 0 && vk == _secondKeyVk)
            {
                _secondKeyVk = 0;
                swallow = true;
            }

            return swallow ? new SystemWideDecision(true, action) : SystemWideDecision.Pass;
        }

        private SystemWideDecision DecideDown(uint vk, SystemWideModifiers held, SystemWideKeySet set)
        {
            // A held JJ key repeats its own DOWN every ~30 ms. Those repeats
            // are eaten BEFORE the second-key question is asked, or holding
            // Ctrl+Shift+J a beat too long would hand Ctrl+Shift+J to the
            // layer as its second key. Found by the first run of this
            // class's own tests, not by a person.
            if (_leaderDown && vk == SystemWideChord.VirtualKeyOf(set.Leader))
                return SystemWideDecision.Eat;

            // The key after the JJ key. Anything that is not a modifier,
            // including a chord that would otherwise be ours.
            if (_leaderArmed)
            {
                _leaderArmed = false;
                if (vk == VK_ESCAPE)
                    return new SystemWideDecision(true, SystemWideAction.LeaderCancelled);
                _secondKeyVk = vk;
                return new SystemWideDecision(true, SystemWideAction.LeaderKey, (Keys)vk | held.Flags);
            }

            bool helpLetGo = false;
            if (_helpArmed)
            {
                _helpArmed = false;
                if (vk == VK_ESCAPE)
                    return new SystemWideDecision(true, SystemWideAction.LeaderCancelled);
                if (vk == VK_H || vk == VK_OEM_2)
                {
                    _secondKeyVk = vk;
                    return new SystemWideDecision(true, SystemWideAction.LeaderKey, (Keys)vk | held.Flags);
                }
                // The layer has let go. This key is NOT ours — but it might
                // still be one of the chords below, so keep going.
                helpLetGo = true;
            }

            // A repeat of the second key while we are still eating its edges.
            if (_secondKeyVk != 0 && vk == _secondKeyVk)
                return new SystemWideDecision(true, SystemWideAction.None, helpLetGo: helpLetGo);

            // Push to talk: first down keys, repeats are eaten and ignored.
            if (_pttHeld)
            {
                if (vk == SystemWideChord.VirtualKeyOf(set.PushToTalk))
                    return new SystemWideDecision(true, SystemWideAction.None, helpLetGo: helpLetGo);
            }
            else if (SystemWideChord.MatchesDown(set.PushToTalk, vk, held))
            {
                _pttHeld = true;
                return new SystemWideDecision(true, SystemWideAction.PttDown, helpLetGo: helpLetGo);
            }

            // Transmit lock: toggles once per press.
            if (_lockDown)
            {
                if (vk == SystemWideChord.VirtualKeyOf(set.TransmitLock))
                    return new SystemWideDecision(true, SystemWideAction.None, helpLetGo: helpLetGo);
            }
            else if (SystemWideChord.MatchesDown(set.TransmitLock, vk, held))
            {
                _lockDown = true;
                return new SystemWideDecision(true, SystemWideAction.LockToggle, helpLetGo: helpLetGo);
            }

            // The JJ key: arms once per press.
            if (_leaderDown)
            {
                if (vk == SystemWideChord.VirtualKeyOf(set.Leader))
                    return new SystemWideDecision(true, SystemWideAction.None, helpLetGo: helpLetGo);
            }
            else if (SystemWideChord.MatchesDown(set.Leader, vk, held))
            {
                _leaderDown = true;
                _leaderArmed = true;
                return new SystemWideDecision(true, SystemWideAction.LeaderArmed, helpLetGo: helpLetGo);
            }

            return helpLetGo
                ? new SystemWideDecision(false, SystemWideAction.None, helpLetGo: true)
                : SystemWideDecision.Pass;
        }
    }
}

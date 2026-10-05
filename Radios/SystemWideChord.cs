using System;
using System.Windows.Forms;

namespace Radios
{
    /// <summary>
    /// The three things a system-wide key can mean. "System-wide" is the word
    /// for a chord that fires while ANOTHER PROGRAM has the keyboard — a
    /// contest logger, a browser, anything — and is kept from that program.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="KeyScope.Global"/>, on purpose.</b> That enum member
    /// means "active in every mode INSIDE this application" (Classic, Modern
    /// and Logging alike); it says nothing about other programs, and a reader
    /// who meets the word "global" beside a keyboard hook will assume it does.
    /// The register (#307, #689) ruled that the new concept gets its own word,
    /// so nothing here says global.
    /// </remarks>
    public enum SystemWideRole
    {
        /// <summary>
        /// The JJ key from any program: opens the JJ key layer and reads the
        /// next key, both kept from the program that has the keyboard.
        /// </summary>
        Leader,

        /// <summary>Push to talk from any program: transmit while held.</summary>
        PushToTalk,

        /// <summary>The transmit lock from any program: on or off.</summary>
        TransmitLock,
    }

    /// <summary>Why a proposed system-wide chord was refused, or that it was not.</summary>
    public enum SystemWideChordVerdict
    {
        /// <summary>Two or more of Ctrl, Alt and Shift plus a real key.</summary>
        Ok,

        /// <summary><see cref="Keys.None"/> — nothing was pressed, or the role is unassigned.</summary>
        NoKey,

        /// <summary>The "key" is itself a modifier (a bare Shift, Ctrl, Alt or Windows key).</summary>
        ModifierOnly,

        /// <summary>
        /// A real key with fewer than two of Ctrl, Alt and Shift. Refused
        /// outright: a system-wide chord is taken from every program on the
        /// machine, and an accidental press of the push-to-talk one keys a
        /// transmitter. Ruled by Noel 2026-10-05: <i>"For global keys we need
        /// more than just a two key chord press."</i>
        /// </summary>
        FewerThanTwoModifiers,
    }

    /// <summary>
    /// Rules about one system-wide chord, kept pure so a test can sweep them.
    /// A chord is a <see cref="Keys"/> value exactly as the rest of the key
    /// system writes one: a key code OR-ed with <see cref="Keys.Control"/>,
    /// <see cref="Keys.Alt"/> and <see cref="Keys.Shift"/>.
    /// </summary>
    public static class SystemWideChord
    {
        /// <summary>The virtual-key code of a chord's main key, as the keyboard hook reports it.</summary>
        public static uint VirtualKeyOf(Keys chord) => (uint)(chord & Keys.KeyCode);

        /// <summary>Only the modifier bits of a chord.</summary>
        public static Keys ModifiersOf(Keys chord) => chord & Keys.Modifiers;

        /// <summary>How many of Ctrl, Alt and Shift the chord carries.</summary>
        public static int ModifierCount(Keys chord)
        {
            int n = 0;
            if ((chord & Keys.Control) != 0) n++;
            if ((chord & Keys.Alt) != 0) n++;
            if ((chord & Keys.Shift) != 0) n++;
            return n;
        }

        /// <summary>
        /// True when the virtual-key code is a modifier key, generic or sided,
        /// or a Windows key. These are never a chord's main key, and the hook
        /// never acts on them — an operator pressing Ctrl on the way to
        /// Ctrl+Shift+J must not be read as having pressed a key at all.
        /// </summary>
        public static bool IsModifierVirtualKey(uint vk) => vk switch
        {
            0x10 or 0x11 or 0x12 => true,           // VK_SHIFT, VK_CONTROL, VK_MENU
            >= 0xA0 and <= 0xA5 => true,            // VK_LSHIFT .. VK_RMENU
            0x5B or 0x5C => true,                   // VK_LWIN, VK_RWIN
            _ => false,
        };

        /// <summary>Apply the two-modifier rule to a proposed chord.</summary>
        public static SystemWideChordVerdict Judge(Keys chord)
        {
            if (chord == Keys.None) return SystemWideChordVerdict.NoKey;
            uint vk = VirtualKeyOf(chord);
            if (vk == 0) return SystemWideChordVerdict.NoKey;
            if (IsModifierVirtualKey(vk)) return SystemWideChordVerdict.ModifierOnly;
            return ModifierCount(chord) >= 2
                ? SystemWideChordVerdict.Ok
                : SystemWideChordVerdict.FewerThanTwoModifiers;
        }

        /// <summary>
        /// Does a key-DOWN of <paramref name="vk"/> with these modifiers held
        /// mean this chord? Exact on the modifiers: Ctrl+Alt+Shift+Space is not
        /// Ctrl+Alt+Space, because the former may belong to somebody else and a
        /// system-wide key must claim no more than it was given.
        /// </summary>
        public static bool MatchesDown(Keys chord, uint vk, SystemWideModifiers held)
        {
            if (chord == Keys.None) return false;
            if (VirtualKeyOf(chord) != vk) return false;
            return ModifiersOf(chord) == held.Flags;
        }
    }

    /// <summary>
    /// Which modifiers Windows reports held at the instant of a key event.
    /// Read by the hook from the operating system, never tracked as edges, so
    /// a screen reader that synthesises modifier events has no state here to
    /// corrupt.
    /// </summary>
    public readonly record struct SystemWideModifiers(bool Ctrl, bool Alt, bool Shift)
    {
        /// <summary>The same fact as <see cref="Keys"/> modifier bits.</summary>
        public Keys Flags =>
            (Ctrl ? Keys.Control : Keys.None) |
            (Alt ? Keys.Alt : Keys.None) |
            (Shift ? Keys.Shift : Keys.None);

        public static readonly SystemWideModifiers None = new(false, false, false);
    }

    /// <summary>
    /// The three chords in force, plus the master switch. Immutable so the
    /// keyboard hook can read one consistent snapshot while a dialog is
    /// composing the next one.
    /// </summary>
    public sealed record SystemWideKeySet(bool Enabled, Keys Leader, Keys PushToTalk, Keys TransmitLock)
    {
        /// <summary>
        /// The provisional defaults, ruled by Noel 2026-10-05 and verified
        /// unbound in the in-app table that day. Alt is the marker of a
        /// system-wide twin: Ctrl+J, Ctrl+Space and Shift+Space stay exactly
        /// what they were inside the window.
        /// </summary>
        public static readonly Keys DefaultLeader = Keys.J | Keys.Control | Keys.Shift;
        public static readonly Keys DefaultPushToTalk = Keys.Space | Keys.Control | Keys.Alt;
        public static readonly Keys DefaultTransmitLock = Keys.Space | Keys.Alt | Keys.Shift;

        public static SystemWideKeySet Defaults { get; } =
            new(true, DefaultLeader, DefaultPushToTalk, DefaultTransmitLock);

        /// <summary>A set that claims nothing: the hook passes every key through.</summary>
        public static SystemWideKeySet Off { get; } =
            new(false, Keys.None, Keys.None, Keys.None);

        public Keys For(SystemWideRole role) => role switch
        {
            SystemWideRole.Leader => Leader,
            SystemWideRole.PushToTalk => PushToTalk,
            SystemWideRole.TransmitLock => TransmitLock,
            _ => Keys.None,
        };

        public SystemWideKeySet With(SystemWideRole role, Keys chord) => role switch
        {
            SystemWideRole.Leader => this with { Leader = chord },
            SystemWideRole.PushToTalk => this with { PushToTalk = chord },
            SystemWideRole.TransmitLock => this with { TransmitLock = chord },
            _ => this,
        };

        /// <summary>The role this chord is assigned to, if any.</summary>
        public SystemWideRole? RoleOf(Keys chord)
        {
            if (chord == Keys.None) return null;
            if (chord == Leader) return SystemWideRole.Leader;
            if (chord == PushToTalk) return SystemWideRole.PushToTalk;
            if (chord == TransmitLock) return SystemWideRole.TransmitLock;
            return null;
        }

        public static readonly SystemWideRole[] AllRoles =
        {
            SystemWideRole.Leader, SystemWideRole.PushToTalk, SystemWideRole.TransmitLock,
        };
    }
}

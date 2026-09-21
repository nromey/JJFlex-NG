namespace Radios.KeyOwnership
{
    /// <summary>
    /// The identity of one key, as plain data. The values ARE the Win32
    /// virtual-key codes, on purpose: both hosts already hold one
    /// (<c>Keys.KeyCode</c> on the shell side, <c>KeyInterop.VirtualKeyFromKey</c>
    /// on the WPF side), so an ingress adapter converts with a cast and never
    /// needs a lookup table that can drift. A key with no member here is still
    /// representable — cast the code — it simply has no name.
    /// </summary>
    /// <remarks>
    /// This is deliberately NOT <c>System.Windows.Forms.Keys</c> and not WPF's
    /// <c>Key</c>. The key-ownership types must be constructible by a test with
    /// object initialisers and nothing else, and must mean the same thing on
    /// every host; a UI framework's key enum fails the second even where it
    /// passes the first (WPF reports <c>Key.System</c> while Alt is held and
    /// hides the real key in <c>SystemKey</c> — the adapter resolves that
    /// BEFORE it builds a snapshot, which is what "effective key" means).
    /// Modifiers are never folded into this value; see <see cref="KeyChord"/>.
    /// </remarks>
    public enum GestureKey
    {
        None = 0,
        Back = 0x08,
        Tab = 0x09,
        Enter = 0x0D,
        Shift = 0x10,
        Control = 0x11,
        Alt = 0x12,
        Escape = 0x1B,
        Space = 0x20,
        PageUp = 0x21,
        PageDown = 0x22,
        End = 0x23,
        Home = 0x24,
        Left = 0x25,
        Up = 0x26,
        Right = 0x27,
        Down = 0x28,
        Insert = 0x2D,
        Delete = 0x2E,
        D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,
        A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        LeftWindows = 0x5B,
        RightWindows = 0x5C,
        Applications = 0x5D,
        F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        LeftShift = 0xA0,
        RightShift = 0xA1,
        LeftControl = 0xA2,
        RightControl = 0xA3,
        LeftAlt = 0xA4,
        RightAlt = 0xA5,

        /// <summary>
        /// The slash / question-mark key (VK_OEM_2). "Question mark" is this
        /// key with Shift on a US layout; the existing layer code accepts it
        /// with or without Shift, and the ruled map declares both.
        /// </summary>
        Slash = 0xBF,
    }

    /// <summary>Whether this event is a press, an OS auto-repeat, or a release.</summary>
    /// <remarks>
    /// A screen reader that synthesises down/up pairs for a held key (JAWS,
    /// roughly 250 ms apart) produces genuine <see cref="Down"/> events, not
    /// <see cref="Repeat"/>. The arbiter therefore never treats two
    /// <see cref="Down"/> events as duplicates of each other, whatever their
    /// spacing: only the host knows a repeat, and says so here.
    /// </remarks>
    public enum KeyPhase
    {
        Down,
        Repeat,
        Up,
    }

    /// <summary>
    /// One key with its EXACT modifiers, as it arrived. Shift keeps its side
    /// (<see cref="ShiftSide"/>, reused from the value-layer engine) because
    /// the filter layer reads the side as the target (#516).
    /// </summary>
    public readonly record struct KeyChord(
        GestureKey Key,
        bool Control = false,
        bool Alt = false,
        ShiftSide Shift = ShiftSide.None,
        bool Windows = false)
    {
        /// <summary>True when no modifier at all is held.</summary>
        public bool IsBare => !Control && !Alt && !Windows && Shift == ShiftSide.None;

        /// <summary>True when the key itself is a modifier (a bare modifier change is not a command).</summary>
        public bool IsModifierKey => Key is GestureKey.Shift or GestureKey.Control or GestureKey.Alt
            or GestureKey.LeftShift or GestureKey.RightShift
            or GestureKey.LeftControl or GestureKey.RightControl
            or GestureKey.LeftAlt or GestureKey.RightAlt
            or GestureKey.LeftWindows or GestureKey.RightWindows;

        public override string ToString()
        {
            string s = "";
            if (Control) s += "Ctrl+";
            if (Alt) s += "Alt+";
            if (Windows) s += "Win+";
            if (Shift != ShiftSide.None) s += Shift == ShiftSide.Left ? "LShift+" : Shift == ShiftSide.Right ? "RShift+" : "Shift+";
            return s + Key;
        }
    }

    /// <summary>What a declaration demands of Shift. Never "don't care".</summary>
    public enum ShiftRequirement
    {
        /// <summary>Shift must be UP. A shifted press does not match.</summary>
        None,
        /// <summary>Shift must be down; either side, or both.</summary>
        Either,
        /// <summary>Exactly the left Shift key.</summary>
        Left,
        /// <summary>Exactly the right Shift key.</summary>
        Right,
    }

    /// <summary>A set of keys one declaration covers as a single rule.</summary>
    public enum KeyFamily
    {
        /// <summary>Not a family — the declaration names one <see cref="GestureKey"/>.</summary>
        None,
        /// <summary>
        /// The slice letters, A through H. One declaration of the universal
        /// slice jump covers all eight, so the rule is stated once rather than
        /// eight times and the target slice is read from the key pressed.
        /// </summary>
        SliceLetter,
    }

    /// <summary>
    /// The gesture a declaration answers to. Every modifier is stated and every
    /// modifier is compared: there is no wildcard, because a wildcard is how
    /// <c>Alt+Down</c> came to be read as <c>Down</c>.
    /// </summary>
    public readonly record struct ChordPattern(
        GestureKey Key,
        bool Control = false,
        bool Alt = false,
        ShiftRequirement Shift = ShiftRequirement.None,
        bool Windows = false,
        KeyFamily Family = KeyFamily.None)
    {
        /// <summary>A pattern over a whole <see cref="KeyFamily"/>.</summary>
        public static ChordPattern ForFamily(KeyFamily family, bool control = false, bool alt = false,
            ShiftRequirement shift = ShiftRequirement.None) =>
            new ChordPattern(GestureKey.None, control, alt, shift, false, family);

        public bool Matches(KeyChord chord)
        {
            if (Control != chord.Control || Alt != chord.Alt || Windows != chord.Windows) return false;
            bool shiftOk = Shift switch
            {
                ShiftRequirement.None => chord.Shift == ShiftSide.None,
                ShiftRequirement.Either => chord.Shift != ShiftSide.None,
                ShiftRequirement.Left => chord.Shift == ShiftSide.Left,
                ShiftRequirement.Right => chord.Shift == ShiftSide.Right,
                _ => false,
            };
            return shiftOk && CoversKey(chord.Key);
        }

        public bool CoversKey(GestureKey key) => Family switch
        {
            KeyFamily.SliceLetter => key >= GestureKey.A && key <= GestureKey.H,
            _ => key == Key && key != GestureKey.None,
        };

        /// <summary>True when some single press could match both patterns.</summary>
        public bool Overlaps(ChordPattern other)
        {
            if (Control != other.Control || Alt != other.Alt || Windows != other.Windows) return false;
            if (!ShiftOverlaps(Shift, other.Shift)) return false;
            if (Family == KeyFamily.None && other.Family == KeyFamily.None)
                return Key == other.Key && Key != GestureKey.None;
            if (Family != KeyFamily.None && other.Family != KeyFamily.None)
                return Family == other.Family;
            return Family != KeyFamily.None ? CoversKey(other.Key) : other.CoversKey(Key);
        }

        private static bool ShiftOverlaps(ShiftRequirement a, ShiftRequirement b)
        {
            if (a == b) return true;
            if (a == ShiftRequirement.None || b == ShiftRequirement.None) return false;
            return a == ShiftRequirement.Either || b == ShiftRequirement.Either;
        }

        public override string ToString()
        {
            string s = "";
            if (Control) s += "Ctrl+";
            if (Alt) s += "Alt+";
            if (Windows) s += "Win+";
            if (Shift != ShiftRequirement.None)
                s += Shift == ShiftRequirement.Left ? "LShift+" : Shift == ShiftRequirement.Right ? "RShift+" : "Shift+";
            return s + (Family == KeyFamily.None ? Key.ToString() : Family.ToString());
        }
    }
}

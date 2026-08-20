using System;
using System.Globalization;

namespace JJFlex.RigSurface
{
    /// <summary>
    /// The kind of object a field lives on, as the radio itself names them on
    /// the wire. These are the first token of a status line body: "radio ...",
    /// "transmit ...", "slice 3 ...", and so on.
    /// </summary>
    public enum RigTarget
    {
        /// <summary>Unrecognised object kind. Kept in the model so we can see
        /// what we are not modelling yet, never written to.</summary>
        Unknown = 0,

        /// <summary>"radio ..." — station-wide settings.</summary>
        Radio,

        /// <summary>"transmit ..." — the single transmit section.</summary>
        Transmit,

        /// <summary>"interlock ..." — transmit permission and current key state.</summary>
        Interlock,

        /// <summary>"atu ..." — antenna tuner status.</summary>
        Atu,

        /// <summary>"slice N ..." — an indexed receiver slice, owned by a client.</summary>
        Slice,

        /// <summary>"xvtr N ..." — an indexed transverter band.</summary>
        Xvtr,

        /// <summary>"client 0xHANDLE ..." — a connected station.</summary>
        Client,

        /// <summary>"meter N.key=value ..." — a named telemetry meter.</summary>
        Meter,

        /// <summary>"display pan 0xHANDLE ..." / "display waterfall ..."</summary>
        Display,

        /// <summary>"gps ..."</summary>
        Gps,

        /// <summary>"eq ..." / "eq rxsc ..." / "eq txsc ..."</summary>
        Eq,

        /// <summary>"tnf N ..."</summary>
        Tnf,

        /// <summary>"waveform ..."</summary>
        Waveform,

        /// <summary>"amplifier 0x... ..."</summary>
        Amplifier,
    }

    /// <summary>
    /// Identifies one field of one object on the radio: the object kind, its
    /// index (or -1 where the object is a singleton), and the key exactly as
    /// the radio spells it in its status stream.
    /// <para>
    /// The key is the RADIO'S spelling, not FlexLib's property name and not
    /// the set-command's spelling. Those three differ often enough that
    /// conflating them is the standing bug in this area — the clearest case
    /// being slice frequency, reported as <c>RF_frequency</c> and written with
    /// <c>slice tune</c> rather than <c>slice set</c>. <see cref="StateOwnership"/>
    /// holds the mapping between the two.
    /// </para>
    /// </summary>
    public readonly record struct RigField
    {
        /// <summary>Index value used for objects that have no index.</summary>
        public const int NoIndex = -1;

        public RigField(RigTarget target, int index, string key)
        {
            Target = target;
            Index = index;
            Key = key ?? throw new ArgumentNullException(nameof(key));
        }

        public RigTarget Target { get; }

        /// <summary>Object index, or <see cref="NoIndex"/> for singletons.</summary>
        public int Index { get; }

        /// <summary>The key exactly as the radio spells it on the wire.</summary>
        public string Key { get; }

        public static RigField Radio(string key) => new(RigTarget.Radio, NoIndex, key);

        public static RigField Transmit(string key) => new(RigTarget.Transmit, NoIndex, key);

        public static RigField Interlock(string key) => new(RigTarget.Interlock, NoIndex, key);

        public static RigField Atu(string key) => new(RigTarget.Atu, NoIndex, key);

        public static RigField Slice(int index, string key) => new(RigTarget.Slice, index, key);

        public static RigField Xvtr(int index, string key) => new(RigTarget.Xvtr, index, key);

        /// <summary>
        /// Parses the textual form used on the command line and in trace files:
        /// "radio.nickname", "transmit.mic_level", "slice.3.mode", "atu.status".
        /// </summary>
        public static bool TryParse(string text, out RigField field)
        {
            field = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string[] parts = text.Split('.');
            if (parts.Length < 2) return false;

            if (!Enum.TryParse(parts[0], ignoreCase: true, out RigTarget target)) return false;

            if (parts.Length == 2)
            {
                field = new RigField(target, NoIndex, parts[1]);
                return true;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                // Not an index — the key itself contained a dot, as meters do.
                field = new RigField(target, NoIndex, string.Join('.', parts, 1, parts.Length - 1));
                return true;
            }

            field = new RigField(target, index, string.Join('.', parts, 2, parts.Length - 2));
            return true;
        }

        public override string ToString() =>
            Index == NoIndex
                ? string.Create(CultureInfo.InvariantCulture, $"{Target.ToString().ToLowerInvariant()}.{Key}")
                : string.Create(CultureInfo.InvariantCulture, $"{Target.ToString().ToLowerInvariant()}.{Index}.{Key}");
    }
}

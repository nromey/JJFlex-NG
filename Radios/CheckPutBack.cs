#nullable enable
using System;
using System.Globalization;

namespace Radios
{
    /// <summary>
    /// One station-wide setting an audio check changed and has not yet put back:
    /// what it was before the check (<see cref="Original"/>) and what the check
    /// set it to (<see cref="CheckSet"/>) (#835, fix round 1030, ruled by Noel
    /// 2026-10-29 21:13). Kept with the restore reminder across a disconnect, so
    /// the next connect to the radio can put back each setting that STILL holds
    /// the check's value and leave alone any that something else has changed.
    /// </summary>
    /// <remarks>
    /// Values are the radio's own spellings, invariant culture, with no spaces:
    /// a power level, 0 or 1, an antenna name, a drive in dBm.
    /// <see cref="Target"/> names which instance a per-instance setting belongs
    /// to (the transverter band's index for the drive), and is empty otherwise.
    /// </remarks>
    public sealed record CheckPutBack(string Setting, string Target, string Original, string CheckSet)
    {
        public const string TxAntenna = "tx-antenna";
        public const string RfPower = "rf-power";
        public const string TunePower = "tune-power";
        public const string Monitor = "monitor";
        public const string FullDuplex = "full-duplex";
        public const string XvtrDrive = "xvtr-drive";

        /// <summary>True when the check really changed the setting; an unchanged one has nothing to put back.</summary>
        public bool Changed => !Same(Original, CheckSet);

        /// <summary>The same setting (and instance) as <paramref name="other"/>, whatever the values.</summary>
        public bool SameSettingAs(CheckPutBack other) =>
            string.Equals(Setting, other.Setting, StringComparison.Ordinal) &&
            string.Equals(Target, other.Target, StringComparison.Ordinal);

        /// <summary>
        /// Two readings of this setting are the same value. The drive is a
        /// double the radio rounds, so it compares within a twentieth of a dB;
        /// everything else compares exactly, an antenna name ignoring case.
        /// </summary>
        public bool Same(string? a, string? b)
        {
            if (a == null || b == null) return false;
            if (Setting == XvtrDrive &&
                double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                return Math.Abs(x - y) < 0.05;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static CheckPutBack Antenna(string original, string checkSet) => new(TxAntenna, "", original ?? "", checkSet ?? "");
        public static CheckPutBack Power(int original, int checkSet) => new(RfPower, "", Num(original), Num(checkSet));
        public static CheckPutBack Tune(int original, int checkSet) => new(TunePower, "", Num(original), Num(checkSet));
        public static CheckPutBack MonitorOn(bool original, bool checkSet) => new(Monitor, "", Bit(original), Bit(checkSet));
        public static CheckPutBack Duplex(bool original, bool checkSet) => new(FullDuplex, "", Bit(original), Bit(checkSet));
        public static CheckPutBack Drive(int band, double original, double checkSet) =>
            new(XvtrDrive, Num(band), original.ToString("R", CultureInfo.InvariantCulture), checkSet.ToString("R", CultureInfo.InvariantCulture));

        internal static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
        internal static string Bit(bool value) => value ? "1" : "0";

        public override string ToString() =>
            Setting + (Target.Length > 0 ? " " + Target : "") + ": original=" + Original + " check set=" + CheckSet;
    }
}

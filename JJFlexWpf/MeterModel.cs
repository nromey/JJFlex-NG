using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace JJFlexWpf
{
    /// <summary>
    /// Where a meter's value comes from. The model deliberately allows more
    /// than radio-reported sources: PC-derived measurements (mic LUFS), a
    /// frequency-domain probe (priority watch, next tranche), and derived
    /// stage deltas (stage A minus stage B — what makes signal-chain analysis
    /// possible). If every meter assumed a radio source, those three would
    /// need surgery to add later.
    /// </summary>
    public enum MeterSourceKind
    {
        /// <summary>Whatever the radio's meter list reports. Key is the meter
        /// name. Until the real meter-list accessor lands (Track B), only the
        /// eight legacy FlexBase names are live; the rest are representable
        /// but dormant.</summary>
        RadioReported,

        /// <summary>Computed on this PC — mic LUFS, residual level, anything
        /// we measure locally. Key names the local metric.</summary>
        PcDerived,

        /// <summary>A probe at a chosen frequency or span inside a streaming
        /// panadapter (priority watch). Uses FrequencyHz/SpanHz.</summary>
        FrequencyDomain,

        /// <summary>Stage A minus stage B, both named by key: Key minus
        /// SecondaryKey, in the shared unit of the two stages. "NB
        /// effectiveness" is this and nothing more.</summary>
        Derived,
    }

    /// <summary>
    /// Unit types a meter range is expressed in. Mirrors the units the radio
    /// itself reports (measured 2026-08-16: Dbfs, Volts, Amps, Dbm, SWR,
    /// DegreesC, None) plus the PC-derived units we compute locally. A closed
    /// vocabulary of physical unit kinds — an enum is right here, unlike
    /// voices, because no downstream feature authors new physical units.
    /// </summary>
    public enum MeterUnits
    {
        None, Dbm, Dbfs, Db, Volts, Amps, Swr, DegreesC, DegreesF,
        Watts, SUnits, Lufs, Percent,
    }

    /// <summary>When a meter's tone is allowed to sound.</summary>
    public enum MeterActivation
    {
        Always,
        ReceiveOnly,
        TransmitOnly,
    }

    /// <summary>
    /// Reference to a meter's value source. One class covering all four kinds,
    /// with kind-specific fields simply unused by the kinds that don't need
    /// them — keeps XML serialisation flat and lets a picker edit any meter
    /// with one shape.
    /// </summary>
    public class MeterSourceRef
    {
        public MeterSourceKind Kind { get; set; } = MeterSourceKind.RadioReported;

        /// <summary>Primary source key: the radio meter name, the PC metric
        /// name, or the minuend stage of a derived meter.</summary>
        public string Key { get; set; } = "";

        /// <summary>Derived meters only: the subtrahend stage (value = Key
        /// minus SecondaryKey). Empty otherwise.</summary>
        public string SecondaryKey { get; set; } = "";

        /// <summary>FrequencyDomain only: probe centre frequency in Hz.</summary>
        public double FrequencyHz { get; set; }

        /// <summary>FrequencyDomain only: probe span in Hz. 0 = single bin.</summary>
        public double SpanHz { get; set; }

        /// <summary>Per-slice sources: which slice, or -1 for the active
        /// slice. Radio-wide sources ignore it.</summary>
        public int SliceIndex { get; set; } = -1;

        public MeterSourceRef Clone() => (MeterSourceRef)MemberwiseClone();

        public override string ToString() => Kind switch
        {
            MeterSourceKind.Derived => $"{Key} minus {SecondaryKey}",
            MeterSourceKind.FrequencyDomain => $"probe at {FrequencyHz:F0} Hz",
            _ => Key,
        };
    }

    /// <summary>
    /// The value band a meter maps onto its pitch range, expressed in the
    /// SOURCE'S OWN UNITS — never bare numbers — so it can be validated,
    /// announced sensibly, and narrowed deliberately (S5 to S9+60 for
    /// resolution where it matters; a coarse and a fine SWR meter sharing one
    /// source). The radio supplies range and units for its own meters; we do
    /// not invent a table.
    /// </summary>
    public class MeterRange
    {
        public double Low { get; set; }
        public double High { get; set; } = 1;
        public MeterUnits Units { get; set; } = MeterUnits.None;

        /// <summary>The units text as the source states it (the radio's own
        /// label wins for announcements). Empty = derive from Units.</summary>
        public string UnitsLabel { get; set; } = "";

        /// <summary>Map a raw source value into 0..1 across this range,
        /// clamped. This is the value→pitch mapping's first half.</summary>
        public float Normalize(double raw)
        {
            double span = High - Low;
            if (Math.Abs(span) < 1e-9) return 0f;
            return (float)Math.Clamp((raw - Low) / span, 0.0, 1.0);
        }

        /// <summary>Spoken/displayed units, preferring the source's label.</summary>
        public string DescribeUnits() =>
            !string.IsNullOrWhiteSpace(UnitsLabel) ? UnitsLabel : Units switch
            {
                MeterUnits.Dbm => "dBm",
                MeterUnits.Dbfs => "dBFS",
                MeterUnits.Db => "dB",
                MeterUnits.Volts => "volts",
                MeterUnits.Amps => "amps",
                MeterUnits.Swr => "SWR",
                MeterUnits.DegreesC => "degrees C",
                MeterUnits.DegreesF => "degrees F",
                MeterUnits.Watts => "watts",
                MeterUnits.SUnits => "S units",
                MeterUnits.Lufs => "LUFS",
                MeterUnits.Percent => "percent",
                _ => "",
            };

        public MeterRange Clone() => (MeterRange)MemberwiseClone();
    }

    /// <summary>
    /// A meter: a SOURCE plus a RANGE plus a VOICE, with its own audibility,
    /// readability, pitch mapping and pan. One list serves both the readout
    /// and the tones — "readable" and "audible" are properties of the same
    /// meter, not two systems with different membership. Two meters may share
    /// a source (coarse and fine SWR); nothing here prevents it.
    /// </summary>
    public class MeterDefinition
    {
        /// <summary>Stable identity, survives rename and reorder — operator
        /// muscle memory (leader-layer numbers) keys on this, not on list
        /// position.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Operator-visible name: "SWR", "SWR fine", "Mic loudness".</summary>
        public string Name { get; set; } = "";

        public MeterSourceRef Source { get; set; } = new();

        /// <summary>Range in the source's own units. Default policy: full
        /// range as the source states it; narrowing is an operator choice.</summary>
        public MeterRange Range { get; set; } = new();

        /// <summary>The voice this meter speaks with, by name, resolved via
        /// <see cref="MeterVoiceLibrary"/>. A reference, not a copy — edit the
        /// voice and every meter using it follows.</summary>
        public string VoiceName { get; set; } = MeterVoiceLibrary.DefaultVoiceName;

        /// <summary>
        /// Per-meter live-tweak override, or null. A live tweak clones the
        /// referenced voice here and adjusts the clone — never the shared
        /// voice — with an explicit save-as-new-voice action to promote it.
        /// Keep-as-copy / replace / discard happens on leaving tweak mode.
        /// </summary>
        public MeterVoice? VoiceOverride { get; set; }

        /// <summary>Audible (the tone plays when activation allows). Default
        /// policy: meters ship off; nothing sounds until asked.</summary>
        public bool Enabled { get; set; }

        /// <summary>Shown in the readout list. Independent of audibility.</summary>
        public bool Readable { get; set; } = true;

        public float Volume { get; set; } = 0.5f;

        /// <summary>-1 left .. +1 right. Enhancement only — never the sole
        /// distinction between two meters (mono listeners and asymmetric
        /// hearing loss lose it entirely).</summary>
        public float Pan { get; set; }

        public float PitchLowHz { get; set; } = 200f;
        public float PitchHighHz { get; set; } = 1200f;

        public MeterActivation Activation { get; set; } = MeterActivation.Always;

        /// <summary>The voice actually rendered: the live-tweak override when
        /// present, else the named library voice, else Pure.</summary>
        public MeterVoice EffectiveVoice() =>
            VoiceOverride ?? MeterVoiceLibrary.Resolve(VoiceName);

        /// <summary>Value → tone frequency in Hz across the pitch range.</summary>
        public float PitchForValue(double raw) =>
            PitchLowHz + (PitchHighHz - PitchLowHz) * Range.Normalize(raw);

        public MeterDefinition Clone()
        {
            var c = (MeterDefinition)MemberwiseClone();
            c.Source = Source.Clone();
            c.Range = Range.Clone();
            c.VoiceOverride = VoiceOverride?.Clone();
            return c;
        }
    }

    /// <summary>
    /// The legacy eight-source catalog: key names, default ranges, units and
    /// activation for the sources FlexBase's old eight-value meter event
    /// carried. The ranges replicate the engine's historical normalisation
    /// exactly, so behaviour is unchanged — they are now DATA on the
    /// definition, which is what makes Noel's narrowed S-meter and the
    /// coarse/fine SWR pair possible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sprint 32 Track B moved the live key space onto the radio's OWN meter
    /// names, so this table's <see cref="Entry.Key"/> values are now HISTORICAL
    /// keys — what old config files contain — and <see cref="RadioMeterName"/>
    /// is the translation to what the radio actually calls the same meter.
    /// The mapping is not a guess: it is read straight out of FlexLib's own
    /// dispatch (<c>Radio.AddMeter</c> and <c>Slice.AddMeter</c>), which is the
    /// code that fed the eight named events in the first place.
    /// </para>
    /// <para>
    /// Keep both names resolvable forever. A config written before Sprint 32
    /// says "Power"; one written after says "FWDPWR"; both must land on the
    /// same meter or an operator's tones repoint silently on upgrade.
    /// </para>
    /// </remarks>
    public static class LegacyMeterCatalog
    {
        public sealed record Entry(string Key, string DisplayName,
            MeterUnits Units, string UnitsLabel, double Low, double High,
            MeterActivation Activation);

        public static readonly IReadOnlyList<Entry> Entries = new List<Entry>
        {
            new("SMeter", "S-Meter", MeterUnits.Dbm, "dBm", -127, -34, MeterActivation.ReceiveOnly),
            new("ALC", "ALC", MeterUnits.None, "", 0, 1, MeterActivation.TransmitOnly),
            new("Mic", "Mic", MeterUnits.Db, "dB", -60, 0, MeterActivation.TransmitOnly),
            new("Power", "Forward Power", MeterUnits.Dbm, "dBm", 0, 50, MeterActivation.TransmitOnly),
            new("SWR", "SWR", MeterUnits.Swr, "SWR", 1, 3, MeterActivation.TransmitOnly),
            new("Compression", "Compression", MeterUnits.Db, "dB", -30, 0, MeterActivation.TransmitOnly),
            new("Voltage", "Supply Voltage", MeterUnits.Volts, "volts", 10, 15, MeterActivation.Always),
            new("PATemp", "PA Temperature", MeterUnits.DegreesC, "degrees C", 20, 80, MeterActivation.Always),
        };

        /// <summary>
        /// The eight legacy keys IN THEIR HISTORICAL ORDINAL ORDER — the order
        /// of the deleted <c>MeterSource</c> enum, which is also the order the
        /// old panel's hardcoded name array used and therefore the order any
        /// integer stored in an old config file indexes into. Never reorder.
        /// </summary>
        public static readonly IReadOnlyList<string> LegacyOrdinalKeys = new[]
        {
            "SMeter", "ALC", "Mic", "Power", "SWR", "Compression", "Voltage", "PATemp",
        };

        /// <summary>
        /// What the radio itself calls each legacy source, and which of its
        /// sources reports it. Taken from FlexLib's own name dispatch:
        /// <c>Radio.AddMeter</c> hooks FWDPWR, REFPWR, SWR, PATEMP, MIC,
        /// MICPEAK, COMPPEAK, HWALC, +13.8A and PAEFF; <c>Slice.AddMeter</c>
        /// hooks LEVEL, which is what the S-meter has always been.
        /// </summary>
        private static readonly Dictionary<string, string> LegacyToRadioName =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["SMeter"] = "LEVEL",
                ["ALC"] = "HWALC",
                ["Mic"] = "MIC",
                ["Power"] = "FWDPWR",
                ["SWR"] = "SWR",
                ["Compression"] = "COMPPEAK",
                ["Voltage"] = "+13.8A",
                ["PATemp"] = "PATEMP",
            };

        /// <summary>
        /// The radio's own name for a legacy key, or the key unchanged when it
        /// is not a legacy key (already a radio name, or something we have
        /// never heard of — either way, passing it through is right).
        /// Idempotent: every value it produces maps to itself.
        /// </summary>
        public static string RadioMeterName(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return key ?? "";
            return LegacyToRadioName.TryGetValue(key, out string? radio) ? radio : key;
        }

        /// <summary>
        /// True when this legacy source lives on a SLICE rather than on the
        /// radio itself. Only the S-meter does, but the distinction has to be
        /// carried because a slice meter needs a slice index to match against.
        /// </summary>
        public static bool IsSliceSource(string key) =>
            string.Equals(RadioMeterName(key), "LEVEL", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Look a source up by EITHER its historical key ("Power") or the
        /// radio's own name for it ("FWDPWR"). Both have to resolve: config
        /// files written before Sprint 32 carry the first, everything written
        /// after carries the second.
        /// </summary>
        public static Entry? Find(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            foreach (var e in Entries)
                if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) return e;
            foreach (var e in Entries)
                if (string.Equals(RadioMeterName(e.Key), key, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>Build a fresh definition for a legacy source with its full
        /// default range, disabled, voiced Pure — the neutral starting point.
        /// The source key it writes is the RADIO'S name, because that is what
        /// the engine matches meter readings against from Sprint 32 on.</summary>
        public static MeterDefinition CreateDefinition(string key)
        {
            var e = Find(key) ?? Entries[0];
            return new MeterDefinition
            {
                Name = e.DisplayName,
                Source = new MeterSourceRef
                {
                    Kind = MeterSourceKind.RadioReported,
                    Key = RadioMeterName(e.Key),
                    // -1 means "whichever slice is active" for slice meters and
                    // is simply ignored by radio-wide meters.
                    SliceIndex = -1,
                },
                Range = new MeterRange { Low = e.Low, High = e.High, Units = e.Units, UnitsLabel = e.UnitsLabel },
                Activation = e.Activation,
                Enabled = false,
            };
        }
    }
}

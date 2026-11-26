using System;
using System.Threading;

namespace JJPortaudio
{
    /// <summary>Which harmonics a JJ Clarity band makes (Sprint 49 Track J, #736).</summary>
    public enum ClarityHarmonics
    {
        /// <summary>The second harmonic only (an order-two shaper): the smoother sound.</summary>
        Even = 0,
        /// <summary>The third harmonic only (an order-three shaper): the edgier sound.</summary>
        Odd = 1,
        /// <summary>The second and the third, half of each.</summary>
        Both = 2,
    }

    /// <summary>
    /// One JJ Clarity band's settings: on or off, tune, drive, mix and the
    /// harmonics it makes. Immutable. What tune means depends on the band; see
    /// <see cref="ClaritySettings"/>.
    /// </summary>
    public readonly struct ClarityBand : IEquatable<ClarityBand>
    {
        public ClarityBand(bool enabled, float tuneHz, float driveDb, float mixPercent, ClarityHarmonics harmonics)
        {
            Enabled = enabled; TuneHz = tuneHz; DriveDb = driveDb; MixPercent = mixPercent; Harmonics = harmonics;
        }

        public bool Enabled { get; }
        /// <summary>The band's corner, Hz: for the high band where the added brightness starts; for the low band the top of the fundamentals it enriches.</summary>
        public float TuneHz { get; }
        /// <summary>How strong the harmonics are, dB (0 to 24): 24 makes them at the level of the band they come from, each decibel less a decibel lower, whatever the microphone level (<see cref="ClarityExciter.HarmonicRatio"/>).</summary>
        public float DriveDb { get; }
        /// <summary>How much of what is made is added back, percent (0 to 100).</summary>
        public float MixPercent { get; }
        public ClarityHarmonics Harmonics { get; }

        /// <summary>True when this band adds nothing: off, or mix at zero.</summary>
        public bool AddsNothing => !Enabled || !(MixPercent > 0f);

        public ClarityBand WithEnabled(bool v) => new ClarityBand(v, TuneHz, DriveDb, MixPercent, Harmonics);
        public ClarityBand WithTune(float v) => new ClarityBand(Enabled, v, DriveDb, MixPercent, Harmonics);
        public ClarityBand WithDrive(float v) => new ClarityBand(Enabled, TuneHz, v, MixPercent, Harmonics);
        public ClarityBand WithMix(float v) => new ClarityBand(Enabled, TuneHz, DriveDb, v, Harmonics);
        public ClarityBand WithHarmonics(ClarityHarmonics v) => new ClarityBand(Enabled, TuneHz, DriveDb, MixPercent, v);

        public bool Equals(ClarityBand o) => Enabled == o.Enabled && TuneHz == o.TuneHz && DriveDb == o.DriveDb
            && MixPercent == o.MixPercent && Harmonics == o.Harmonics;
        public override bool Equals(object obj) => obj is ClarityBand b && Equals(b);
        public override int GetHashCode() => HashCode.Combine(Enabled, TuneHz, DriveDb, MixPercent, Harmonics);
        public override string ToString() => FormattableString.Invariant(
            $"{(Enabled ? "on" : "off")} tune {TuneHz} Hz drive {DriveDb} dB mix {MixPercent}% {Harmonics}");
    }

    /// <summary>
    /// The operator's whole JJ Clarity setting, both bands, IMMUTABLE and
    /// clamped to the ranges on construction (Sprint 49 Track J, #736). This is
    /// the INTENT: what is applied may be clamped further by the passband and
    /// the rate (<see cref="ClarityDesign"/>), and the intent is never changed
    /// by that clamp.
    /// </summary>
    /// <remarks>
    /// <para><b>The high band adds brightness:</b> its tune is where the added
    /// brightness STARTS (1 to 8 kHz). The stage derives the band of the voice it
    /// feeds to the shaper from that and from the ceiling, so the lever means
    /// the same thing at every transmit rate and every filter width.</para>
    /// <para><b>The low band adds fullness:</b> its tune is the top of the
    /// fundamentals it enriches (60 to 300 Hz); their harmonics land inside the
    /// passband even when the fundamentals themselves are cut by the radio's
    /// low edge (the missing-fundamental effect; whether that is audible
    /// through an SSB low cut is for the harness to settle).</para>
    /// <para>Ranges and defaults are Track J's judgement, proposed to Noel in
    /// the report. The ranges for drive (0 to 24 dB) and mix (0 to 100 %)
    /// follow a GPL program's exciter header read for its design; nothing was
    /// copied.</para>
    /// </remarks>
    public sealed class ClaritySettings : IEquatable<ClaritySettings>
    {
        public const float HighTuneMinHz = 1000f, HighTuneMaxHz = 8000f, HighTuneDefaultHz = 2000f;
        public const float LowTuneMinHz = 60f, LowTuneMaxHz = 300f, LowTuneDefaultHz = 150f;
        public const float DriveMinDb = 0f, DriveMaxDb = 24f, HighDriveDefaultDb = 12f, LowDriveDefaultDb = 6f;
        public const float MixMinPercent = 0f, MixMaxPercent = 100f, MixDefaultPercent = 25f;

        public ClaritySettings(ClarityBand high, ClarityBand low)
        {
            High = Normalize(high, isHigh: true);
            Low = Normalize(low, isHigh: false);
        }

        /// <summary>The high band (brightness).</summary>
        public ClarityBand High { get; }

        /// <summary>The low band (fullness).</summary>
        public ClarityBand Low { get; }

        /// <summary>A new setting: the high band on at its defaults, the low band off. (Clarity as a whole starts OFF; that switch is the stage's.)</summary>
        public static ClaritySettings Default { get; } = new ClaritySettings(
            new ClarityBand(true, HighTuneDefaultHz, HighDriveDefaultDb, MixDefaultPercent, ClarityHarmonics.Even),
            new ClarityBand(false, LowTuneDefaultHz, LowDriveDefaultDb, MixDefaultPercent, ClarityHarmonics.Both));

        /// <summary>True when neither band adds anything.</summary>
        public bool AddsNothing => High.AddsNothing && Low.AddsNothing;

        public ClaritySettings WithHigh(ClarityBand high) => new ClaritySettings(high, Low);
        public ClaritySettings WithLow(ClarityBand low) => new ClaritySettings(High, low);

        /// <summary>A band clamped into its ranges; a non-finite value takes the default.</summary>
        public static ClarityBand Normalize(ClarityBand b, bool isHigh)
        {
            float tune = Clamp(b.TuneHz, isHigh ? HighTuneMinHz : LowTuneMinHz, isHigh ? HighTuneMaxHz : LowTuneMaxHz,
                isHigh ? HighTuneDefaultHz : LowTuneDefaultHz);
            float drive = Clamp(b.DriveDb, DriveMinDb, DriveMaxDb, isHigh ? HighDriveDefaultDb : LowDriveDefaultDb);
            float mix = Clamp(b.MixPercent, MixMinPercent, MixMaxPercent, MixDefaultPercent);
            var h = Enum.IsDefined(typeof(ClarityHarmonics), b.Harmonics) ? b.Harmonics : ClarityHarmonics.Even;
            return new ClarityBand(b.Enabled, tune, drive, mix, h);
        }

        private static float Clamp(float v, float lo, float hi, float dflt) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : dflt;

        public bool Equals(ClaritySettings o) => o != null && High.Equals(o.High) && Low.Equals(o.Low);
        public override bool Equals(object obj) => Equals(obj as ClaritySettings);
        public override int GetHashCode() => HashCode.Combine(High, Low);
        public override string ToString() => $"high: {High}; low: {Low}";
    }

    /// <summary>
    /// One complete JJ Clarity design, IMMUTABLE once made: the settings, the
    /// passband's two edges, the rate, and every filter coefficient of both
    /// bands at that rate, computed here, off the audio thread (the
    /// conditioner's immutable-publish pattern; Sprint 49 Track J, #736).
    /// </summary>
    /// <remarks>
    /// <para><b>Fold-back, bounded for a steady tone and measured, not
    /// proved in general.</b> A polynomial of order N turns a signal whose
    /// highest frequency is F into one whose highest frequency is N times F.
    /// So each band band-limits the shaper's INPUT (an eighth-order Butterworth
    /// low-pass) to the ceiling divided by the highest order it makes, where
    /// the ceiling (<see cref="Ceiling"/>) is the lower of the passband's high
    /// edge and <see cref="FoldGuard"/> of Nyquist, leaving a quarter of
    /// Nyquist for the filter's transition. That bounds the steady-tone case,
    /// measured to between -115 dBc (8 kHz) and -178 dBc (48 kHz). It is not a
    /// strict band limit: the filter has a finite stopband, and the level
    /// control's peak-dependent gain adds modulation products beyond the
    /// polynomial's order (a two-tone probe at 8 kHz measured them about
    /// -56 dBc, Codex's check, 2026-11-24). Onsets and many tones are for the
    /// harness walk. Where the edge is known the shaper's work also stays
    /// inside what the radio transmits.</para>
    /// <para><b>Intent kept, applied clamped</b> (the de-esser's pattern): the
    /// high band's tune is applied at most <see cref="PassbandFraction"/> of
    /// the ceiling (scaled by the ratio of its lowest to highest harmonic order,
    /// so the band the shaper is fed is never empty). The chosen tune stays in
    /// <see cref="Settings"/>.</para>
    /// <para>System-only and allocation-free in <see cref="Compute"/>, so the
    /// audio thread can redesign in place when the stream's rate changes.</para>
    /// </remarks>
    public sealed class ClarityDesign
    {
        /// <summary>The rate a design is made for unless told otherwise: the transmit stream's default.</summary>
        public const uint DesignRate = 48000;

        /// <summary>The fraction of Nyquist the shaper's products may reach: the rest is the input filter's transition.</summary>
        public const float FoldGuard = 0.75f;

        /// <summary>The high band's tune may be applied at most this fraction of the ceiling (times the order ratio).</summary>
        public const float PassbandFraction = 0.8f;

        /// <summary>Sections per band: [0] the input high-pass, [1..4] the input low-pass (eighth-order Butterworth), [5] the added path's high-pass, [6..9] its low-pass at the ceiling (eighth-order).</summary>
        internal const int SectionsPerBand = 10;

        internal const int HighBand = 0, LowBand = 1;

        // Butterworth pole-pair Qs.
        private static readonly double[] Q8 = { 0.50979558, 0.60134489, 0.89997622, 2.56291545 };
        private const double Q2 = 0.70710678;

        /// <summary>The low band's input high-pass: below this is rumble, not voice.</summary>
        public const float LowBandFloorHz = 30f;

        private readonly BiquadCoefficients[] _co = new BiquadCoefficients[2 * SectionsPerBand];

        private ClarityDesign(ClaritySettings settings, float lowEdge, float highEdge, uint rate, bool bandLimit)
        {
            Settings = settings ?? ClaritySettings.Default;
            PassbandLowEdgeHz = lowEdge;
            PassbandHighEdgeHz = highEdge;
            Rate = rate;
            InputBandLimited = bandLimit;
            Compute(Settings, lowEdge, highEdge, rate, _co, bandLimit);
        }

        /// <summary>Make a design. Edges of 0 (or anything not positive and finite) mean unknown: nothing is clamped to them.</summary>
        public static ClarityDesign Create(ClaritySettings settings, float passbandLowEdgeHz, float passbandHighEdgeHz, uint rate = DesignRate) =>
            new ClarityDesign(settings, Edge(passbandLowEdgeHz), Edge(passbandHighEdgeHz), rate == 0 ? DesignRate : rate, bandLimit: true);

        /// <summary>
        /// A design with the shaper's input band-limit REMOVED, for the fold-back
        /// test's positive control only: it shows the test can see fold-back.
        /// Never used by the stage.
        /// </summary>
        internal static ClarityDesign CreateWithoutInputBandLimitForTest(ClaritySettings settings, float lowEdge, float highEdge, uint rate) =>
            new ClarityDesign(settings, Edge(lowEdge), Edge(highEdge), rate, bandLimit: false);

        private static float Edge(float v) => float.IsFinite(v) && v > 0 ? v : 0f;

        public ClaritySettings Settings { get; }
        public float PassbandLowEdgeHz { get; }
        public float PassbandHighEdgeHz { get; }
        public uint Rate { get; }

        /// <summary>False only for the fold-back test's positive control.</summary>
        public bool InputBandLimited { get; }

        /// <summary>True when the design adds nothing at all (both bands off or at zero mix).</summary>
        public bool AddsNothing => Settings.AddsNothing;

        /// <summary>The ceiling at this design's rate and edge.</summary>
        public float CeilingHz => Ceiling(PassbandHighEdgeHz, Rate);

        /// <summary>Where the high band's added brightness starts, as applied.</summary>
        public float AppliedHighTuneHz => ApplyHighTune(Settings.High.TuneHz, Settings.High.Harmonics, CeilingHz);

        /// <summary>Whether the high band's chosen tune is clamped here.</summary>
        public bool HighTuneClamped => AppliedHighTuneHz < Settings.High.TuneHz;

        /// <summary>The top of the low band's fundamentals, as applied.</summary>
        public float AppliedLowTuneHz => ApplyLowTune(Settings.Low.TuneHz, Settings.Low.Harmonics, CeilingHz);

        /// <summary>Whether the low band's chosen tune is clamped here.</summary>
        public bool LowTuneClamped => AppliedLowTuneHz < Settings.Low.TuneHz;

        internal BiquadCoefficients CoefficientAt(int i) => _co[i];

        /// <summary>The ceiling: the passband's high edge when known, never above <see cref="FoldGuard"/> of Nyquist.</summary>
        public static float Ceiling(float highEdgeHz, uint rate)
        {
            float fold = FoldGuard * 0.5f * (rate == 0 ? DesignRate : rate);
            return highEdgeHz > 0 ? Math.Min(highEdgeHz, fold) : fold;
        }

        /// <summary>The lowest harmonic order a choice makes.</summary>
        public static int LowestOrder(ClarityHarmonics h) => h == ClarityHarmonics.Odd ? 3 : 2;

        /// <summary>The highest harmonic order a choice makes: the one the band limit is set for.</summary>
        public static int HighestOrder(ClarityHarmonics h) => h == ClarityHarmonics.Even ? 2 : 3;

        /// <summary>The high band's clamp, as a rule.</summary>
        public static float ApplyHighTune(float chosenHz, ClarityHarmonics h, float ceilingHz)
        {
            float max = PassbandFraction * ceilingHz * LowestOrder(h) / HighestOrder(h);
            return Math.Max(100f, Math.Min(chosenHz, max));
        }

        /// <summary>The low band's clamp, as a rule: its products must stay under the ceiling too.</summary>
        public static float ApplyLowTune(float chosenHz, ClarityHarmonics h, float ceilingHz)
        {
            float max = PassbandFraction * ceilingHz / HighestOrder(h);
            return Math.Max(LowBandFloorHz * 1.5f, Math.Min(chosenHz, max));
        }

        /// <summary>
        /// Compute both bands' coefficients into <paramref name="into"/> (at
        /// least 2 x <see cref="SectionsPerBand"/>). Allocation-free: the audio
        /// thread calls this only when the stream's rate changes after a publish.
        /// </summary>
        internal static void Compute(ClaritySettings s, float lowEdge, float highEdge, uint rate, BiquadCoefficients[] into, bool bandLimit = true)
        {
            double fs = rate == 0 ? DesignRate : rate;
            float ceiling = Ceiling(highEdge, (uint)fs);

            // The high band: the shaper is fed [tune / lowest order, ceiling / highest order].
            var h = s.High;
            int hLo = LowestOrder(h.Harmonics), hHi = HighestOrder(h.Harmonics);
            float hTune = ApplyHighTune(h.TuneHz, h.Harmonics, ceiling);
            int o = HighBand * SectionsPerBand;
            into[o] = BiquadCoefficients.Design(BiquadKind.HighPass, hTune / hLo, 0, Q2, fs);
            for (int k = 0; k < 4; k++)
                into[o + 1 + k] = bandLimit ? BiquadCoefficients.Design(BiquadKind.LowPass, ceiling / hHi, 0, Q8[k], fs) : BiquadCoefficients.Identity;
            into[o + 5] = BiquadCoefficients.Design(BiquadKind.HighPass, Math.Max(20.0, hTune * 0.7), 0, Q2, fs);
            for (int k = 0; k < 4; k++)
                into[o + 6 + k] = BiquadCoefficients.Design(BiquadKind.LowPass, ceiling, 0, Q8[k], fs);

            // The low band: the shaper is fed [30 Hz, tune]; what it makes below the passband's low edge is wasted, so the added path starts there.
            var l = s.Low;
            float lTune = ApplyLowTune(l.TuneHz, l.Harmonics, ceiling);
            o = LowBand * SectionsPerBand;
            into[o] = BiquadCoefficients.Design(BiquadKind.HighPass, LowBandFloorHz, 0, Q2, fs);
            for (int k = 0; k < 4; k++)
                into[o + 1 + k] = bandLimit ? BiquadCoefficients.Design(BiquadKind.LowPass, lTune, 0, Q8[k], fs) : BiquadCoefficients.Identity;
            double lowCut = lowEdge > 0 ? Math.Min(lowEdge, ceiling * 0.5) : 20.0;
            into[o + 5] = BiquadCoefficients.Design(BiquadKind.HighPass, Math.Max(20.0, lowCut), 0, Q2, fs);
            for (int k = 0; k < 4; k++)
                into[o + 6 + k] = BiquadCoefficients.Design(BiquadKind.LowPass, ceiling, 0, Q8[k], fs);
        }

        /// <summary>The magnitude, dB, of a band's input filter (what feeds its shaper) at a frequency. For tests and readouts.</summary>
        public double InputGainDb(bool highBand, double hz)
        {
            int o = (highBand ? HighBand : LowBand) * SectionsPerBand;
            double db = 0;
            for (int k = 0; k < 5; k++) db += _co[o + k].MagnitudeDb(hz, Rate);
            return db;
        }

        /// <summary>The magnitude, dB, of a band's added-path filter at a frequency. For tests and readouts.</summary>
        public double AddedPathGainDb(bool highBand, double hz)
        {
            int o = (highBand ? HighBand : LowBand) * SectionsPerBand;
            double db = 0;
            for (int k = 5; k < SectionsPerBand; k++) db += _co[o + k].MagnitudeDb(hz, Rate);
            return db;
        }
    }

    /// <summary>Clarity's statistics for one over, taken off the audio thread.</summary>
    public readonly struct ClarityOverStats
    {
        public ClarityOverStats(long samples, double addedRmsDbfs, double addedPeakDbfs, long overFullScale)
        {
            Samples = samples; AddedRmsDbfs = addedRmsDbfs; AddedPeakDbfs = addedPeakDbfs; OverFullScale = overFullScale;
        }

        /// <summary>Samples (both channels) processed with Clarity doing something.</summary>
        public long Samples { get; }
        /// <summary>The RMS of what Clarity added, dBFS; -200 when it added nothing.</summary>
        public double AddedRmsDbfs { get; }
        /// <summary>The largest single sample Clarity added, dBFS; -200 when it added nothing.</summary>
        public double AddedPeakDbfs { get; }
        /// <summary>Samples past full scale at Clarity's output.</summary>
        public long OverFullScale { get; }
    }

    /// <summary>
    /// JJ Clarity (Sprint 49 Track J, #736): an exciter. It splits off a band
    /// of the voice, makes harmonics from it on purpose with a polynomial
    /// shaper, and mixes them back, adding brightness (the high band) and
    /// fullness (the low band) the voice did not have. An equaliser can only
    /// raise or lower what is there; this adds content.
    /// </summary>
    /// <remarks>
    /// <para><b>The added path, per band:</b> the input filter (see
    /// <see cref="ClarityDesign"/>), a level control, the shaper, the added
    /// path's filter, and the mix. The output is the input plus the bands'
    /// added paths. The dry path is never filtered, so off is exact.</para>
    /// <para><b>The level control normalises the band</b> (the level fix,
    /// 2026-11-26, after the prior-art survey and a real voice showed the
    /// first design's effect falling a decibel for every decibel the voice
    /// fell). The detector is the band's peak, linked across both channels:
    /// the largest sample of the last <see cref="HoldMs"/> (the high band) or
    /// <see cref="LowBandHoldMs"/> (the low band, longer than one period of
    /// the lowest frequency its input filter passes), a sliding maximum,
    /// released over <see cref="ReleaseMs"/> when it falls. That peak, eased at
    /// the floors (below), is the envelope e. The shaper is fed v = band / e,
    /// and what it makes is multiplied by e again. So the shaper always works
    /// on the band at unit size whatever the microphone level, and the
    /// harmonic it makes rises and falls LINEARLY with the band: scale the
    /// input by any factor above the floor and the added path scales by the
    /// same factor. The argument is at most one, strictly: the envelope is
    /// never below the band's own sample, because the sliding maximum includes
    /// it (the first design's turn-down-only control could pass one by its
    /// release step, at most about 1.0016 at 8 kHz). Any signal whose cycles
    /// repeat within the window (a steady tone, two tones, a vowel's
    /// harmonics) holds the sliding maximum still, so the shaper's output is a
    /// polynomial of the band with no envelope ripple (to within the few tenths
    /// of a percent its sampled peaks differ by, released at the release rate);
    /// a rising level steps the envelope at the sample that sets a new
    /// maximum, and a falling one releases it, and both modulate the band,
    /// which makes products beyond the polynomial's order (see
    /// <see cref="ClarityDesign"/>). Before the fix that modulation happened
    /// only once the drive had pushed the band into the control; now it
    /// follows the voice's own rise and fall at every level above the
    /// floor.</para>
    /// <para><b>Why a peak and not a smoothed RMS:</b> the bound. A band
    /// divided by its own RMS has the band's crest factor as its peak
    /// argument (three to four for speech in these narrow bands), so the cubic
    /// would see 30 to 60 times its unit-size output at a peak, and a smoothed
    /// detector fast enough to follow syllables ripples at twice the band's
    /// frequency, modulating even a steady tone. <b>Why a sliding maximum and
    /// not the first design's held peak:</b> a held peak renews its hold only
    /// on a new maximum, so a signal whose cycles peak a hair apart (two
    /// tones, or a vowel's harmonics, sampled at slightly different phases)
    /// let the hold run out, released a little and stepped back up, a
    /// sawtooth at the hold's rate. Measured (2026-11-26, two tones 200 Hz
    /// apart, drive 24, mix 100) as energy off the shaper's own product lines:
    /// -37 dB (even) and -21 to -29 dB (odd) relative to them with the held
    /// peak, the first design's figures once its control engaged. A
    /// sliding maximum has no hold to run out, and no onset to stick on: an
    /// onset's overshoot leaves the window after <see cref="HoldMs"/>.</para>
    /// <para><b>The floor</b> (<see cref="LevelFloorDbfs"/>, a band peak in
    /// dBFS): the envelope is sqrt(peak^2 + floor^2 + relative^2), not the
    /// peak itself, where the relative floor (<see cref="RelativeFloorDb"/>)
    /// follows the band's wide signal and keeps leakage from being
    /// normalised. Well
    /// above the floor that is the peak (within 0.04 dB at 20 dB above); well
    /// below it, it is the floor, a constant, so the shaper falls back to a
    /// plain polynomial of the band and the harmonic falls a decibel for every
    /// decibel the band falls (even) or two (odd): pauses and room noise are
    /// not lifted to the band's level. The ease is smooth, never a switch: at
    /// the floor itself the harmonic sits 3 dB (even) or 6 dB (odd) below its
    /// setting, and the slope runs continuously from none to one (or two)
    /// decibels per decibel across about 20 dB.</para>
    /// <para><b>What drive means</b>: the harmonic's level relative to the band
    /// it is made from, for a steady tone above the floor:
    /// <see cref="HarmonicRatio"/>, 10^((drive - 24) / 20). At 24 dB (the top of
    /// the range) the harmonic is at the band's own level, which is the most
    /// the first design could ever make; every decibel less is a decibel lower,
    /// down to 24 dB below the band at zero. Mix then scales it again. Even or
    /// odd, the drive means the same: the shaper's output is weighted by that
    /// ratio after it is made, not by driving its input, so the odd shaper's
    /// harmonic no longer follows the square of the drive. "Both" makes each
    /// harmonic at half that ratio. A real voice reads lower than a tone at
    /// the same drive, because its band sits below its peak between peaks and
    /// several of its harmonics share the band; what is level
    /// independent is the ratio at a given drive, not its exact figure.</para>
    /// <para><b>The shapers are harmonic-only.</b> With v the normalised band
    /// and m the running mean of v squared: even is 2 (v^2 - m) (the second
    /// harmonic, the DC that v^2 makes removed by m); odd is 4 (v^3 - 1.5 m v)
    /// (the third harmonic; the fundamental v^3 makes cancels exactly for a
    /// steady tone). For a steady unit tone each is a unit harmonic. They
    /// replace the first design's division by the drive gain: that division
    /// turned the shaped signal back to the band's scale, which is now the
    /// multiplication by the envelope. The added path's high-pass removes any
    /// DC that is left.</para>
    /// <para><b>Publish, never mutate:</b> <see cref="Use(ClarityDesign)"/>
    /// takes a whole immutable design from any thread without allocating; the
    /// audio thread adopts it at its next buffer and cross-fades the added
    /// path from the old design to the new over <see cref="CrossFadeMs"/>, the
    /// new starting from the old one's state, so a lever moved mid-stream does
    /// not click. A design published mid-fade waits for the fade. Switching
    /// the stage on or off fades the added path in or out the same way.
    /// Whenever a design is adopted at a rate other than the one it was made
    /// for (the stream's rate changed, or a design was published for a
    /// different rate), the audio thread computes its coefficients in place
    /// (no allocation, no lock).</para>
    /// <para><b>Off is exact:</b> off, both bands off, or mix at zero, and no
    /// fade running, <see cref="Process"/> returns without touching a
    /// sample. Delay: zero (recursive sections run in place; nothing looks
    /// ahead).</para>
    /// <para><b>Never clips silently:</b> samples past full scale at its output
    /// and the energy it added are counted on the audio thread as plain
    /// numbers (<see cref="TakeOverStats"/>); whoever owns the trace writes
    /// them, off that thread.</para>
    /// <para>Double-precision state, flushed of subnormals once per buffer.
    /// No lock and no allocation on the audio thread. System-only, so the
    /// numerical harness can link it with <c>Biquad.cs</c>. Direction-agnostic:
    /// the same core would serve receive.</para>
    /// <para><b>Design reference, ideas only, no code taken:</b> a GPL
    /// program's exciter HEADER was read for its shape (two parallel bands, a
    /// high band of high-pass then a soft clip, odd and even or odd only; a low
    /// band of low-pass, dynamics and saturation; drive, tune and mix per band;
    /// a DC block on the asymmetric path; a "what it is adding" meter) and its
    /// ranges. Its source was not opened. The polynomial shapers, the band
    /// limit and the level control here are from the published Chebyshev and
    /// Butterworth formulas.</para>
    /// </remarks>
    public sealed class ClarityExciter
    {
        private const int Channels = 2;

        /// <summary>How long a change of design, or the stage switching on or off, fades the added path.</summary>
        public const float CrossFadeMs = 20f;

        /// <summary>The level control's window for the high band: its envelope is the band's largest sample in the last this-many milliseconds (its input starts above 300 Hz, a period under 4 ms).</summary>
        public const float HoldMs = 20f;

        /// <summary>The level control's window for the low band: longer than one period of its input filter's lowest frequency (<see cref="ClarityDesign.LowBandFloorHz"/>, 33 ms), so a steady low tone's envelope does not fall between its peaks.</summary>
        public const float LowBandHoldMs = 40f;

        /// <summary>The level control's release time constant, once the window's maximum falls.</summary>
        public const float ReleaseMs = 80f;

        /// <summary>
        /// The highest rate the level control's detectors are sized for, with
        /// no allocation: above it their window is the most their rings hold
        /// (a shorter hold). The transmit stream runs at 48 kHz or below.
        /// </summary>
        public const int DetectorMostRate = 96000;

        /// <summary>
        /// The level fix's floor, a band peak in dBFS: below it the envelope
        /// stops following the band, so pauses and room noise are not lifted
        /// (see the class remarks). Chosen from the author's live recording
        /// setup (headset mic), a take of running speech at an everyday
        /// microphone level (2026-11-25): in its pauses the high band's peak
        /// sits near -80 dBFS (95th percentile about -60, the tails of words),
        /// and in speech its median is -35 and its 5th percentile -54. -60 is
        /// 20 dB above that room and below nearly all speech. A design
        /// constant for now; it is meant to follow the room's measured floor
        /// once the room measurement is reliable.
        /// </summary>
        public const double LevelFloorDbfs = -60.0;

        /// <summary>The drive at which the harmonic is at the band's own level.</summary>
        public const float DriveAtBandLevelDb = 24f;

        /// <summary>
        /// The harmonic's amplitude relative to the band it is made from, for a
        /// steady tone above <see cref="LevelFloorDbfs"/>, at a drive:
        /// 10^((drive - <see cref="DriveAtBandLevelDb"/>) / 20). Before mix.
        /// </summary>
        public static double HarmonicRatio(float driveDb) => Math.Pow(10.0, (driveDb - DriveAtBandLevelDb) / 20.0);

        private static readonly double FloorSquared = Math.Pow(10.0, LevelFloorDbfs / 10.0);

        /// <summary>
        /// The relative floor, dB: the envelope is never less than this far
        /// below the peak of the band's WIDE signal, the input after the
        /// band's high-pass and before its band limit. In-band content sits
        /// well above it (for a tone in the band, wide and band are the same
        /// signal, and the floor costs 0.004 dB). What the band limit lets
        /// through from content above it, 40 dB and more down, sits below it,
        /// so it keeps the first design's polynomial suppression instead of
        /// being normalised to full size: without this, a 1.8 kHz tone at an
        /// 8 kHz rate folded back at -54 dBc instead of -115.
        /// </summary>
        public const double RelativeFloorDb = -30.0;

        private static readonly double RelativeFloor = Math.Pow(10.0, RelativeFloorDb / 20.0);

        private volatile bool _enabled;
        private ClarityDesign _wanted;          // published, any thread
        private volatile float _lowEdge, _highEdge;
        private volatile int _lastRate;         // the rate the audio thread last ran at, for publishers

        // Audio thread.
        private readonly Engine _cur = new Engine(), _old = new Engine();
        private ClarityDesign _adopted;
        private bool _adoptedEnabled;
        private uint _rate;
        private bool _fading;
        private int _fadeAt, _fadeLength;

        // Per-over statistics, accumulated on the audio thread as plain numbers.
        private double _addedSquares;
        private double _addedPeak;
        private long _samples;
        private long _overFullScale;
        private volatile float _addingNowDbfs = -200f;

        /// <summary>The whole stage on or off. Starts OFF.</summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>The design most recently published (not necessarily adopted yet).</summary>
        public ClarityDesign Wanted => Volatile.Read(ref _wanted);

        /// <summary>The settings of the design most recently published, or the defaults before any.</summary>
        public ClaritySettings Settings => Wanted?.Settings ?? ClaritySettings.Default;

        /// <summary>
        /// Publish a whole design (null for "adds nothing"). Safe from any
        /// thread, the audio thread included: it stores a reference and
        /// allocates nothing. The audio thread adopts it at its next buffer.
        /// </summary>
        public void Use(ClarityDesign design) => Volatile.Write(ref _wanted, design);

        /// <summary>
        /// Publish settings: a design is built HERE, for the rate the stream last
        /// ran at and the passband edges this stage holds. Allocates: never call
        /// it from the audio thread (use <see cref="Use(ClarityDesign)"/>).
        /// </summary>
        public void Use(ClaritySettings settings) => Use(ClarityDesign.Create(settings, _lowEdge, _highEdge, LastRate));

        /// <summary>The rate the audio thread last ran at (48 kHz before it has run).</summary>
        public uint LastRate { get { int r = Volatile.Read(ref _lastRate); return r > 0 ? (uint)r : ClarityDesign.DesignRate; } }

        /// <summary>
        /// The transmit passband's high edge, Hz, set by the policy layer (on a
        /// Flex, from the transmit filter); 0 when unknown. Setting it republishes
        /// the current settings (allocates; not from the audio thread). The same
        /// input as the de-esser's.
        /// </summary>
        public float PassbandHighEdgeHz
        {
            get => _highEdge;
            set { float v = float.IsFinite(value) && value > 0 ? value : 0f; if (v == _highEdge) return; _highEdge = v; Use(Settings); }
        }

        /// <summary>The transmit passband's low edge, Hz, set the same way; 0 when unknown.</summary>
        public float PassbandLowEdgeHz
        {
            get => _lowEdge;
            set { float v = float.IsFinite(value) && value > 0 ? value : 0f; if (v == _lowEdge) return; _lowEdge = v; Use(Settings); }
        }

        /// <summary>
        /// The rate the clamp readouts use: the rate the stream last ran at, which
        /// is the rate the coefficients in force were computed for, whatever rate
        /// the published design was made for (repair 1, B3); the design's own rate
        /// before the stream has run.
        /// </summary>
        public uint InForceRate
        {
            get { int r = Volatile.Read(ref _lastRate); return r > 0 ? (uint)r : (Wanted?.Rate ?? ClarityDesign.DesignRate); }
        }

        private float InForceCeiling => ClarityDesign.Ceiling(Wanted?.PassbandHighEdgeHz ?? _highEdge, InForceRate);

        /// <summary>Where the high band's brightness starts, as applied at the rate in force.</summary>
        public float AppliedHighTuneHz => ClarityDesign.ApplyHighTune(Settings.High.TuneHz, Settings.High.Harmonics, InForceCeiling);

        /// <summary>Whether the high band's chosen tune is being clamped to the passband or the rate.</summary>
        public bool HighTuneClamped => AppliedHighTuneHz < Settings.High.TuneHz;

        /// <summary>The top of the low band's fundamentals, as applied at the rate in force.</summary>
        public float AppliedLowTuneHz => ClarityDesign.ApplyLowTune(Settings.Low.TuneHz, Settings.Low.Harmonics, InForceCeiling);

        /// <summary>Whether the low band's chosen tune is being clamped.</summary>
        public bool LowTuneClamped => AppliedLowTuneHz < Settings.Low.TuneHz;

        /// <summary>Added delay, frames: always zero.</summary>
        public int LatencyFrames => 0;

        /// <summary>What Clarity added in the last buffer, RMS dBFS (-200 when nothing): the "what Clarity is adding now" reading, read on demand.</summary>
        public float AddingNowDbfs => _addingNowDbfs;

        /// <summary>Whether Clarity is doing anything: on with a design that adds something, or a fade running.</summary>
        public bool IsActive
        {
            get
            {
                var w = Wanted;
                return (_enabled && w != null && !w.AddsNothing) || _fading;
            }
        }

        /// <summary>
        /// AUDIO THREAD ONLY: whether <see cref="Process"/> has anything to do
        /// on the next buffer. <see cref="IsActive"/>, or the audio thread is
        /// still adding with the design it last adopted. The second half is
        /// the buffer straight after a switch-off: the stage reads as off, no
        /// fade has begun yet, and the added path must still be faded out. A
        /// host that skips its shaping stages when none is on asks this, not
        /// <see cref="IsActive"/>, or that fade would never run and the added
        /// path would be cut dead (Sprint 49 Track J phase 2).
        /// </summary>
        public bool RunsOnThisBuffer => IsActive || _cur.Active;

        /// <summary>Statistics since the last take, then zeroed. Any thread.</summary>
        public ClarityOverStats TakeOverStats()
        {
            double sq = Interlocked.Exchange(ref _addedSquares, 0.0);
            double pk = Interlocked.Exchange(ref _addedPeak, 0.0);
            long n = Interlocked.Exchange(ref _samples, 0);
            long over = Interlocked.Exchange(ref _overFullScale, 0);
            double rms = n > 0 && sq > 0 ? 10.0 * Math.Log10(sq / n) : -200.0;
            double peak = pk > 0 ? 20.0 * Math.Log10(pk) : -200.0;
            return new ClarityOverStats(n, rms, peak, over);
        }

        /// <summary>
        /// Process one buffer of interleaved stereo in place.
        /// <paramref name="countStats"/> says whether this buffer counts toward the
        /// over's statistics (the transmit chain counts only keyed buffers).
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate, bool countStats = true)
        {
            if (sampleRate == 0) sampleRate = ClarityDesign.DesignRate;
            var wanted = Volatile.Read(ref _wanted);
            bool enabled = _enabled;
            bool changed = !ReferenceEquals(wanted, _adopted) || enabled != _adoptedEnabled;
            if (sampleRate != _rate) AdoptAtNewRate(wanted, enabled, sampleRate);
            else if (changed && !_fading) Adopt(wanted, enabled);

            if (!_fading && !_cur.Active)
            {
                _addingNowDbfs = -200f;             // nothing added now (repair 1, B3)
                return;                             // exact: not a sample touched
            }

            int frames = count / Channels;
            double sq = 0, peak = 0;
            long over = 0;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                double xl = buffer[i], xr = buffer[i + 1];
                double al = 0, ar = 0;
                if (_cur.Active) _cur.Frame(xl, xr, out al, out ar);
                if (_fading)
                {
                    double ol = 0, oR = 0;
                    if (_old.Active) _old.Frame(xl, xr, out ol, out oR);
                    double t = (double)_fadeAt / _fadeLength;
                    al = ol + (al - ol) * t;
                    ar = oR + (ar - oR) * t;
                    if (++_fadeAt >= _fadeLength) _fading = false;
                }
                float yl = (float)(xl + al), yr = (float)(xr + ar);
                buffer[i] = yl; buffer[i + 1] = yr;
                sq += al * al + ar * ar;
                double m = Math.Max(Math.Abs(al), Math.Abs(ar));
                if (m > peak) peak = m;
                if (yl > 1f || yl < -1f) over++;
                if (yr > 1f || yr < -1f) over++;
                i += Channels;
            }
            _cur.FlushTiny();
            if (_fading) _old.FlushTiny();
            long n = frames * (long)Channels;
            _addingNowDbfs = n > 0 && sq > 0 ? (float)(10.0 * Math.Log10(sq / n)) : -200f;
            if (countStats && n > 0)
            {
                AddDouble(ref _addedSquares, sq);
                MaxDouble(ref _addedPeak, peak);
                Interlocked.Add(ref _samples, n);
                if (over > 0) Interlocked.Add(ref _overFullScale, over);
            }
        }

        /// <summary>Begin a fade from what is in force to the new target. Audio thread; no allocation.</summary>
        private void Adopt(ClarityDesign wanted, bool enabled)
        {
            bool next = enabled && wanted != null && !wanted.AddsNothing;
            _old.CopyFrom(_cur);
            if (next) _cur.Load(wanted, _rate);
            else _cur.Deactivate();
            _adopted = wanted; _adoptedEnabled = enabled;
            if (_old.Active || _cur.Active)
            {
                _fading = true;
                _fadeAt = 0;
                _fadeLength = Math.Max(1, (int)(CrossFadeMs * 0.001 * _rate));
            }
        }

        /// <summary>A rate change: the old state belongs to dead audio; no fade, coefficients for the new rate.</summary>
        private void AdoptAtNewRate(ClarityDesign wanted, bool enabled, uint rate)
        {
            _rate = rate;
            Volatile.Write(ref _lastRate, (int)rate);
            _fading = false;
            _cur.Deactivate();
            _cur.ClearState();
            if (enabled && wanted != null && !wanted.AddsNothing) _cur.Load(wanted, rate);
            _adopted = wanted; _adoptedEnabled = enabled;
        }

        private static void AddDouble(ref double target, double v)
        {
            double was, now;
            do { was = Volatile.Read(ref target); now = was + v; }
            while (Interlocked.CompareExchange(ref target, now, was) != was);
        }

        private static void MaxDouble(ref double target, double v)
        {
            double was;
            while (v > (was = Volatile.Read(ref target)))
                if (Interlocked.CompareExchange(ref target, v, was) == was) break;
        }

        /// <summary>
        /// The level control's detector: the largest value of the last
        /// <c>window</c> frames (a sliding maximum, kept as a monotonic queue in
        /// preallocated rings), released over <see cref="ReleaseMs"/> when it
        /// falls. Allocation-free after construction.
        /// </summary>
        private sealed class SlidingPeak
        {
            private readonly double[] _val;
            private readonly int[] _at;
            private int _head, _count;

            /// <summary>The detector's output: the sliding maximum, or its release when that is higher.</summary>
            public double Envelope;

            public SlidingPeak(int capacity) { _val = new double[capacity]; _at = new int[capacity]; }

            /// <summary>The longest window this detector can hold (one less than its ring).</summary>
            public int MostWindow => _val.Length - 1;

            public void Clear() { _head = 0; _count = 0; Envelope = 0; }

            public void CopyFrom(SlidingPeak o)
            {
                int cap = _val.Length;
                _head = 0; _count = o._count; Envelope = o.Envelope;
                for (int i = 0, j = o._head; i < o._count; i++)
                {
                    _val[i] = o._val[j]; _at[i] = o._at[j];
                    if (++j == cap) j = 0;
                }
            }

            /// <summary>One frame: <paramref name="a"/> enters at <paramref name="frame"/> (a counter rising by one a frame; it may wrap).</summary>
            public double Step(double a, int frame, int window, double release)
            {
                int cap = _val.Length;
                // The values this one is at least as large as can never be the maximum again.
                while (_count > 0)
                {
                    int back = _head + _count - 1;
                    if (back >= cap) back -= cap;
                    if (_val[back] <= a) _count--; else break;
                }
                int slot = _head + _count;
                if (slot >= cap) slot -= cap;
                _val[slot] = a; _at[slot] = frame; _count++;
                // The oldest leaves when it falls out of the window (the newest never does).
                while (_count > 1 && frame - _at[_head] >= window)
                {
                    if (++_head == cap) _head = 0;
                    _count--;
                }
                double most = _val[_head];
                double released = Envelope - Envelope * release;
                Envelope = most > released ? most : released;
                return Envelope;
            }

            public void FlushTiny() { if (Envelope < 1e-20) Envelope = 0; }
        }

        /// <summary>One complete set of coefficients and state for both bands and both channels. Preallocated; nothing here allocates.</summary>
        private sealed class Engine
        {
            private const int S = ClarityDesign.SectionsPerBand;
            private readonly BiquadCoefficients[] _co = new BiquadCoefficients[2 * S];
            private readonly BiquadState[] _st = new BiquadState[2 * S * Channels]; // [band][section][channel]
            private readonly bool[] _bandOn = new bool[2];
            private readonly double[] _wEven = new double[2];   // the even harmonic's ratio to the band (HarmonicRatio, halved for Both)
            private readonly double[] _wOdd = new double[2];    // the odd harmonic's
            private readonly double[] _mix = new double[2];     // fraction
            // The detectors, linked across channels: [band * 2] the band's own peak, [band * 2 + 1] its wide signal's
            // (after the input high-pass, before the band limit). Sized for DetectorMostRate.
            private readonly SlidingPeak[] _det =
            {
                new SlidingPeak((int)(HoldMs * 0.001 * DetectorMostRate) + 2), new SlidingPeak((int)(HoldMs * 0.001 * DetectorMostRate) + 2),
                new SlidingPeak((int)(LowBandHoldMs * 0.001 * DetectorMostRate) + 2), new SlidingPeak((int)(LowBandHoldMs * 0.001 * DetectorMostRate) + 2),
            };
            private readonly double[] _meanSq = new double[Channels * 2]; // [band][channel]
            private readonly double[] _meanAlpha = new double[2];
            private readonly int[] _window = new int[2];
            private double _release;
            private int _frame;

            public bool Active;

            public void Deactivate() => Active = false;

            public void ClearState()
            {
                for (int k = 0; k < _st.Length; k++) _st[k].Clear();
                for (int k = 0; k < _det.Length; k++) _det[k].Clear();
                for (int k = 0; k < _meanSq.Length; k++) _meanSq[k] = 0;
            }

            public void CopyFrom(Engine e)
            {
                Array.Copy(e._co, _co, _co.Length);
                Array.Copy(e._st, _st, _st.Length);
                Array.Copy(e._bandOn, _bandOn, 2);
                Array.Copy(e._wEven, _wEven, 2);
                Array.Copy(e._wOdd, _wOdd, 2);
                Array.Copy(e._mix, _mix, 2);
                for (int k = 0; k < _det.Length; k++) _det[k].CopyFrom(e._det[k]);
                Array.Copy(e._meanSq, _meanSq, _meanSq.Length);
                Array.Copy(e._meanAlpha, _meanAlpha, 2);
                Array.Copy(e._window, _window, 2);
                _release = e._release;
                _frame = e._frame;
                Active = e.Active;
            }

            /// <summary>Take a design's coefficients and levers for this rate, keeping the running state of a band that stays on.</summary>
            public void Load(ClarityDesign d, uint rate)
            {
                bool wasActive = Active;
                if (d.Rate == rate)
                    for (int k = 0; k < _co.Length; k++) _co[k] = d.CoefficientAt(k);
                else
                    ClarityDesign.Compute(d.Settings, d.PassbandLowEdgeHz, d.PassbandHighEdgeHz, rate, _co, d.InputBandLimited);
                Set(ClarityDesign.HighBand, d.Settings.High, wasActive);
                Set(ClarityDesign.LowBand, d.Settings.Low, wasActive);
                // Above DetectorMostRate the window is the most the rings hold (a shorter hold), never an allocation.
                _window[ClarityDesign.HighBand] = Math.Clamp((int)(HoldMs * 0.001 * rate), 1, _det[0].MostWindow);
                _window[ClarityDesign.LowBand] = Math.Clamp((int)(LowBandHoldMs * 0.001 * rate), 1, _det[2].MostWindow);
                _release = 1.0 - Math.Exp(-1.0 / (ReleaseMs * 0.001 * rate));
                // The running mean of v^2: long enough to smooth twice the band's lowest input frequency.
                _meanAlpha[ClarityDesign.HighBand] = 1.0 - Math.Exp(-1.0 / (0.005 * rate));
                _meanAlpha[ClarityDesign.LowBand] = 1.0 - Math.Exp(-1.0 / (0.040 * rate));
                Active = _bandOn[0] || _bandOn[1];
            }

            private void Set(int band, ClarityBand b, bool wasActive)
            {
                bool on = !b.AddsNothing;
                if (on && !(wasActive && _bandOn[band]))
                {
                    // A band coming on starts from rest.
                    for (int k = 0; k < S * Channels; k++) _st[band * S * Channels + k].Clear();
                    _det[band * 2].Clear(); _det[band * 2 + 1].Clear();
                    _meanSq[band * Channels] = 0; _meanSq[band * Channels + 1] = 0;
                }
                _bandOn[band] = on;
                double r = HarmonicRatio(b.DriveDb);
                _wEven[band] = b.Harmonics switch { ClarityHarmonics.Even => r, ClarityHarmonics.Odd => 0.0, _ => 0.5 * r };
                _wOdd[band] = b.Harmonics switch { ClarityHarmonics.Odd => r, ClarityHarmonics.Even => 0.0, _ => 0.5 * r };
                _mix[band] = b.MixPercent / 100.0;
            }

            public void Frame(double xl, double xr, out double al, out double ar)
            {
                al = 0; ar = 0;
                _frame++;
                for (int band = 0; band < 2; band++)
                {
                    if (!_bandOn[band]) continue;
                    int c = band * S;
                    int sl = band * S * Channels, sr = sl + S;
                    // The input filter: what the shaper may be fed. Section 0 is its
                    // high-pass; what leaves it, before the band limit, is the wide signal.
                    double bl = _st[sl].Step(in _co[c], xl), br = _st[sr].Step(in _co[c], xr);
                    double w = Math.Max(Math.Abs(bl), Math.Abs(br));
                    for (int k = 1; k < 5; k++)
                    {
                        bl = _st[sl + k].Step(in _co[c + k], bl);
                        br = _st[sr + k].Step(in _co[c + k], br);
                    }
                    // The level control: the band's sliding peak, linked, eased at the two floors, is the envelope.
                    double a = Math.Max(Math.Abs(bl), Math.Abs(br));
                    double pk = _det[band * 2].Step(a, _frame, _window[band], _release);
                    double pw = _det[band * 2 + 1].Step(w, _frame, _window[band], _release) * RelativeFloor;
                    double e = Math.Sqrt(pk * pk + pw * pw + FloorSquared);
                    double inv = 1.0 / e;
                    // Shape the band at unit size, then put it back at the band's scale.
                    double hl = e * Shape(band, 0, bl * inv);
                    double hr = e * Shape(band, 1, br * inv);
                    // The added path's filter, then the mix.
                    for (int k = 5; k < S; k++)
                    {
                        hl = _st[sl + k].Step(in _co[c + k], hl);
                        hr = _st[sr + k].Step(in _co[c + k], hr);
                    }
                    al += _mix[band] * hl;
                    ar += _mix[band] * hr;
                }
            }

            /// <summary>
            /// The harmonic-only shapers on the normalised band v (at most one
            /// in size), each weighted by its ratio to the band (the drive, see
            /// <see cref="HarmonicRatio"/>). The caller multiplies by the envelope.
            /// </summary>
            private double Shape(int band, int ch, double v)
            {
                int mi = band * Channels + ch;
                double v2 = v * v;
                double m = _meanSq[mi] += (v2 - _meanSq[mi]) * _meanAlpha[band];
                double even = 2.0 * (v2 - m);
                double odd = 4.0 * (v2 * v - 1.5 * m * v);
                return _wEven[band] * even + _wOdd[band] * odd;
            }

            public void FlushTiny()
            {
                for (int k = 0; k < _st.Length; k++) _st[k].FlushTiny();
                for (int k = 0; k < _meanSq.Length; k++) if (_meanSq[k] < 1e-30) _meanSq[k] = 0;
                for (int k = 0; k < _det.Length; k++) _det[k].FlushTiny();
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace Radios.Listenback;

/// <summary>
/// The reference speech spectrum the suggest pass compares a voice against
/// (Sprint 49 Track J, J3, #736): the STANDARD SPEECH SPECTRUM for normal
/// vocal effort, ANSI S3.5-1997 (R2017), "Methods for Calculation of the
/// Speech Intelligibility Index", Table 3, one-third-octave band procedure.
/// </summary>
/// <remarks>
/// <para><b>The numbers</b> are the table's standard speech spectrum LEVELS
/// (dB per hertz) at the nominal centres 160 Hz to 8 kHz. Transcribed from the
/// table as reproduced in a published implementation of the standard
/// (Google's <c>speech_intelligibility_index</c>), checked 2026-11-24, and
/// matching the builder's own recollection of the standard; verify against
/// the standard itself before relying on a figure.</para>
/// <para><b>Levels per hertz are converted to band levels</b> by adding ten
/// times the log of each band's width (<see cref="BandLevelDb"/>), because
/// <see cref="LongTermSpectrumMeter"/> measures band power. Only shape is
/// used: both spectra are normalised over the same bands before they are
/// compared, so the table's absolute level (about 62 dB SPL overall) does not
/// matter.</para>
/// <para><b>Why this one, for a voice heard through an SSB filter</b> (my
/// judgement): it is the spectrum the intelligibility index is built on, the
/// accepted measure of how speech "cuts through"; it averages male and female
/// talkers, so it is a neutral target rather than one voice; and its range,
/// 160 Hz to 8 kHz, covers every SSB passband (about 100 to 300 Hz up to 2.7
/// to 3 kHz) and ESSB's. SPECULATION: whether a published long-term spectrum
/// measured at the mouth is the right target for audio heard through a
/// transmitter and a receiver, rather than a spectrum of good-sounding SSB
/// voices (none is published that I know of), is for the harness walk and
/// Noel's ears to settle. Bands below 160 Hz have no reference and are not
/// compared.</para>
/// </remarks>
public static class ReferenceSpeechSpectrum
{
    /// <summary>The source, cited.</summary>
    public const string Source = "ANSI S3.5-1997 (R2017), Table 3: standard speech spectrum level, normal vocal effort";

    /// <summary>The table's nominal third-octave centres, Hz.</summary>
    public static readonly int[] NominalCentresHz =
        { 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000 };

    /// <summary>The table's standard speech spectrum level, normal vocal effort, dB per hertz.</summary>
    public static readonly double[] SpectrumLevelDb =
        { 32.41, 34.48, 34.75, 33.98, 34.59, 34.27, 32.06, 28.30, 25.01, 23.00, 20.15, 17.32, 13.18, 11.55, 9.33, 5.31, 2.59, 1.13 };

    /// <summary>
    /// The reference's band level, dB, for the third-octave band of
    /// <see cref="LongTermSpectrumMeter"/> with this nominal centre: the
    /// spectrum level plus ten log ten of the band's width. False when the
    /// table has no figure for the band.
    /// </summary>
    public static bool TryBandLevelDb(int nominalHz, out double bandLevelDb)
    {
        bandLevelDb = 0;
        int t = Array.IndexOf(NominalCentresHz, nominalHz);
        int m = Array.IndexOf(LongTermSpectrumMeter.NominalCentresHz, nominalHz);
        if (t < 0 || m < 0) return false;
        double width = LongTermSpectrumMeter.UpperHz(m) - LongTermSpectrumMeter.LowerHz(m);
        bandLevelDb = SpectrumLevelDb[t] + 10.0 * Math.Log10(width);
        return true;
    }

    /// <summary>The reference's band level for a band the table covers; throws otherwise.</summary>
    public static double BandLevelDb(int nominalHz) =>
        TryBandLevelDb(nominalHz, out double db) ? db : throw new ArgumentOutOfRangeException(nameof(nominalHz));

    /// <summary>
    /// The reference's shape over a set of bands: each band's level relative
    /// to the total power of those bands, dB. Bands the table does not cover
    /// are left out of the result.
    /// </summary>
    public static IReadOnlyDictionary<int, double> ShapeOver(IEnumerable<int> nominalHz)
    {
        var levels = new Dictionary<int, double>();
        foreach (int hz in nominalHz) if (TryBandLevelDb(hz, out double db)) levels[hz] = db;
        double total = 0;
        foreach (var v in levels.Values) total += Math.Pow(10.0, v / 10.0);
        var shape = new Dictionary<int, double>();
        foreach (var kv in levels) shape[kv.Key] = kv.Value - 10.0 * Math.Log10(total);
        return shape;
    }
}

using System;
using System.Globalization;
using System.IO;
using JJPortaudio;

namespace Radios.Listenback;

/// <summary>Complex band-pass FIR, fixed scale, real projection. No slice DSP or AGC.</summary>
public sealed class SsbDemodulator
{
    private readonly double[] _hr, _hi, _xr, _xi;
    private readonly double _phaseStep;
    private double _phase;
    private int _head;
    public int Delay => _hr.Length / 2;

    public SsbDemodulator(int rate, string mode, double low, double high, double tuningOffsetHz = 0)
    {
        if (rate < 8000 || mode is not ("USB" or "LSB") || low < 0 || high <= low || high >= rate / 2)
            throw new ArgumentException("unsupported-render");
        int taps = ((int)Math.Ceiling(rate / 100.0) | 1);
        _hr = new double[taps]; _hi = new double[taps]; _xr = new double[taps]; _xi = new double[taps];
        double centre = (mode == "USB" ? 1 : -1) * (high + low) / 2;
        double width = (high - low) / rate;
        double gain = 0;
        for (int i = 0; i < taps; ++i)
        {
            int n = i - Delay;
            double lp = n == 0 ? width : Math.Sin(Math.PI * width * n) / (Math.PI * n);
            lp *= .42 - .5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + .08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            double angle = 2 * Math.PI * centre * n / rate;
            _hr[i] = lp * Math.Cos(angle); _hi[i] = lp * Math.Sin(angle); gain += lp;
        }
        for (int i = 0; i < taps; ++i) { _hr[i] /= gain; _hi[i] /= gain; }
        _phaseStep = -2 * Math.PI * tuningOffsetHz / rate;
    }
    public float Push(float radioI, float radioQ)
    {
        double c = Math.Cos(_phase), s = Math.Sin(_phase);
        _phase = Math.IEEERemainder(_phase + _phaseStep, 2 * Math.PI);
        _xr[_head] = (radioI * c - radioQ * s) / 32768;
        _xi[_head] = (radioI * s + radioQ * c) / 32768;
        double real = 0;
        int j = _head;
        for (int k = 0; k < _hr.Length; ++k)
        {
            real += _hr[k] * _xr[j] - _hi[k] * _xi[j];
            if (--j < 0) j = _hr.Length - 1;
        }
        if (++_head == _hr.Length) _head = 0;
        return (float)real;
    }
}

public sealed record TakeRenderResult(string Path, int Rate, long Samples, double RmsDb, double PeakDb, long MeasuredKeyedSamples);

public static class SsbTakeRenderer
{
    // Optional sample stage is the future playback AGC insertion point. Null
    // is deliberately pass-through; this build supplies no AGC implementation.
    public static TakeRenderResult Render(string directory, int detuneHz = 0, Action<float[], int, int> sampleStage = null)
    {
        if (Math.Abs(detuneHz) > 1000) throw new ArgumentOutOfRangeException(nameof(detuneHz));
        var take = TakeReader.Inspect(directory);
        if (!take.Playable) throw new InvalidDataException(take.Reason);
        int rate = int.Parse(take.Get("rate-echoed") ?? take.Get("rate-requested"), CultureInfo.InvariantCulture);
        double Read(string key) => double.Parse(take.Get(key), CultureInfo.InvariantCulture);
        double centreOffset = (Read("tx-frequency-mhz") - Read("pan-centre-mhz")) * 1e6;
        var demod = new SsbDemodulator(rate, take.Get("mode"), Read("tx-filter-low"), Read("tx-filter-high"), centreOffset + detuneHz);
        string path = System.IO.Path.Combine(directory, detuneHz == 0 ? "heard.wav" : $"heard-detune-{detuneHz}.wav");
        using var input = new BinaryReader(File.OpenRead(System.IO.Path.Combine(directory, "rf.iq")));
        using var output = new WavWriter(path, rate, 1);
        long pairs = input.BaseStream.Length / 8, written = 0;
        var gaps = TakeTimeline.Gaps(take);
        var spans = TakeTimeline.WithoutGaps(TakeTimeline.KeyedSpans(take, pairs), gaps);
        int gapIndex = 0;
        long measured = 0; int spanIndex = 0;
        var buffer = new float[4096]; int count = 0;
        double square = 0, peak = 0;
        void Flush()
        {
            sampleStage?.Invoke(buffer, count, rate);
            for (int j = 0; j < count; ++j)
            {
                long at = written + j;
                while (gapIndex < gaps.Count && at >= gaps[gapIndex].End) gapIndex++;
                if (gapIndex < gaps.Count && at >= gaps[gapIndex].Start) buffer[j] = 0;
                while (spanIndex < spans.Count && at >= spans[spanIndex].End - demod.Delay) spanIndex++;
                if (spanIndex >= spans.Count || at < spans[spanIndex].Start + demod.Delay) continue;
                square += buffer[j] * (double)buffer[j]; peak = Math.Max(peak, Math.Abs(buffer[j])); measured++;
            }
            output.Write(buffer, count); written += count; count = 0;
        }
        // Compensate the FIR's known delay; sample indices remain the IQ clock.
        for (long i = 0; i < pairs + demod.Delay; ++i)
        {
            float value = i < pairs ? demod.Push(input.ReadSingle(), input.ReadSingle()) : demod.Push(0, 0);
            if (i < demod.Delay) continue;
            buffer[count++] = value;
            if (count == buffer.Length) Flush();
        }
        if (count > 0) Flush();
        output.Finish();
        var result = new TakeRenderResult(path, rate, written, 10 * Math.Log10(Math.Max(1e-20, square / Math.Max(1, measured))),
            20 * Math.Log10(Math.Max(1e-10, peak)), measured);
        File.WriteAllText(System.IO.Path.Combine(directory, detuneHz == 0 ? "render.txt" : $"render-detune-{detuneHz}.txt"),
            FormattableString.Invariant($"format=jjflex-render-1\nscale=1/32768\nnormalization=no\nagc-stage={(sampleStage == null ? "none" : "supplied")}\nrate={rate}\ndetune-hz={detuneHz}\nfir-delay-compensated={demod.Delay}\nsamples={written}\nlevel-scope=gap-free-radio-keyed-spans-with-filter-edges-excluded\nmeasured-keyed-samples={measured}\nrms-dbfs={(measured > 0 ? result.RmsDb.ToString("R", CultureInfo.InvariantCulture) : "unavailable")}\npeak-dbfs={(measured > 0 ? result.PeakDb.ToString("R", CultureInfo.InvariantCulture) : "unavailable")}\n"));
        return result;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JJPortaudio;
using NAudio.Wave;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track P2 (#873 phase 2, #473, #1155): renders for Noel's ear, made by
/// the app's REAL receive slot and REAL playback queue render path, today's code
/// beside the lag shifter. Nothing is played: files are written only when
/// <c>JJFLEX_TRACKP2_RENDER_DIR</c> names a folder; otherwise the test says NOT RUN
/// and passes. Windowless.
/// </summary>
public sealed class LagShifterRendersTests
{
    private const int Rate = 48000, F = 480, Bpc = 10;
    private readonly ITestOutputHelper _out;
    public LagShifterRendersTests(ITestOutputHelper output) { NoiseReductionPerChannelTests.AssertWindowless(); _out = output; }

    /// <summary>A stand-in engine: a mild colouring, then a pure delay of the real engine's figure.</summary>
    private sealed class ColourEngine : IDenoiseEngine
    {
        private readonly float[] _ring; private int _at; private float _prev; private readonly float _a, _b;
        public ColourEngine(string id, int delay, float a, float b) { Id = id; _ring = new float[delay]; LatencySamples = delay; _a = a; _b = b; }
        public string Id { get; }
        public int SampleRate => Rate; public int FrameLength => F; public int LatencySamples { get; }
        public bool IsAvailable => true;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < input.Length; i++) { float x = _a * input[i] + _b * _prev; _prev = input[i]; output[i] = _ring[_at]; _ring[_at] = x; if (++_at == _ring.Length) _at = 0; }
            return true;
        }
        public void Reset() { } public void Dispose() { }
    }

    private static float[] Materials(string name, int frames)
    {
        // A stable seed: string.GetHashCode() differs in every process, so the noise,
        // voice and CW materials (and every file made from them) differed on every
        // render, and two renders could not be compared (found in repair 1).
        int seed = 17;
        foreach (char ch in name) seed = unchecked(seed * 31 + ch);
        var rng = new Random(seed & 0x7fff);
        float[] Band(float amp) { var a = new float[frames]; double lp = 0, hpIn = 0, hp = 0; for (int i = 0; i < frames; i++) { double x = rng.NextDouble() * 2 - 1; lp += (x - lp) * 0.3; hp = 0.97 * (hp + lp - hpIn); hpIn = lp; a[i] = (float)(hp * amp); } return a; }
        switch (name)
        {
            case "band-noise": return Band(0.12f);
            case "voice":
            {
                var v = SharedDelayFiguresTests.ReferenceSpeech(startSeconds: 10, seconds: frames / (double)Rate);
                var n = Band(0.03f);
                return Enumerable.Range(0, frames).Select(i => (i < v.Length ? v[i] : 0f) + n[i]).ToArray();
            }
            case "tone500": return Enumerable.Range(0, frames).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * 500 * i / Rate))).ToArray();
            case "tone625": return Enumerable.Range(0, frames).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * 625 * i / Rate))).ToArray();
            default:
            {
                // 20 WPM CW at 700 Hz: PARIS repeated, 60 ms dits, 5 ms edges, over faint band noise.
                var key = new List<bool>(); int unit = 60 * 48;
                while (key.Count < frames)
                    foreach (char ch in ".--.|.-|.-.|..|...| ")
                        key.AddRange(ch == '.' ? Enumerable.Repeat(true, unit).Concat(Enumerable.Repeat(false, unit))
                            : ch == '-' ? Enumerable.Repeat(true, 3 * unit).Concat(Enumerable.Repeat(false, unit))
                            : ch == '|' ? Enumerable.Repeat(false, 2 * unit) : Enumerable.Repeat(false, 4 * unit));
                var n = Band(0.01f); double env = 0;
                return Enumerable.Range(0, frames).Select(i => { env += ((key[i] ? 1.0 : 0.0) - env) / 60.0; return (float)(0.3 * env * Math.Sin(2 * Math.PI * 700 * i / Rate)) + n[i]; }).ToArray();
            }
        }
    }

    private static readonly string[] Names = { "band-noise", "voice", "tone500", "tone625", "cw700-20wpm" };

    // ── Owner one: the slot ──

    private static float[] Slot(float[] signal, string evt, bool shifter)
    {
        DenoiseEngineEntry Row(string id, int d, float a, float b) => new(id, DenoiseSide.Pc, DenoiseFamily.Neural, "label",
            new[] { DenoiseDirection.Receive }, (_, _) => new ColourEngine(id, d, a, b), () => Unavailability.None);
        bool reverse = evt.EndsWith("back");
        int a0 = evt.StartsWith("switch") ? (reverse ? 5024 : 480) : 480, b0 = evt.StartsWith("switch") ? (reverse ? 480 : 5024) : 1440;
        using var p = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[] { Row("rnnoise", a0, 0.85f, 0.15f), Row("b", b0, 1.1f, -0.2f) }));
        var slot = p.NeuralSlot;
        slot.ShifterOverrideForTest = shifter; slot.MuteOverrideForTest = false;
        slot.SetStrength("rnnoise", 1); slot.SetStrength("b", 1);
        if (evt == "retime") slot.SharedPlan = new SharedDelayPlan(3894, new[] { new ReceiveSharedDelay.Member("rnnoise", null) });
        p.RnnEnabled = evt != "turn-on";
        var output = new float[signal.Length];
        var audio = new float[2 * F];
        for (int k = 0; k * F + F <= signal.Length; k++)
        {
            if (k == 150)
            {
                if (evt.StartsWith("switch"))
                {
                    Assert.True(slot.Select("b", out _));
                    var t = Stopwatch.StartNew();
                    while (!slot.BuildDelivered("b") && t.ElapsedMilliseconds < 5000) Thread.Sleep(1);
                }
                else if (evt == "turn-on") p.RnnEnabled = true;
                else if (evt == "turn-off") p.RnnEnabled = false;
                else slot.SharedPlan = new SharedDelayPlan(1440, new[] { new ReceiveSharedDelay.Member("rnnoise", null) });
            }
            for (int i = 0; i < F; i++) audio[2 * i] = audio[2 * i + 1] = signal[k * F + i];
            p.Process(audio);
            for (int i = 0; i < F; i++) output[k * F + i] = audio[2 * i];
        }
        return output;
    }

    // ── Owner two: the queue ──

    private static float[] Queue(float[] signal, string evt, RxCatchUpMode mode, bool playAtOnce = true)
    {
        var q = new RxBufferQueue();
        var pb = new RxPlaybackQueue("receive", Bpc, 10.0, Rate, 60, mode) { PlayAtOnce = playAtOnce };
        var output = new List<float>();
        var outBuf = new float[F * Bpc * 2];
        float[] buffer = Array.Empty<float>(); uint offset = 0;
        long packet = 0; bool seamNext = false;
        int packets = signal.Length / F;
        void Arrive(int n)
        {
            for (int p = 0; p < n && packet < packets; p++, packet++)
            {
                var b = new float[F * 2];
                for (int i = 0; i < F; i++) b[2 * i] = b[2 * i + 1] = signal[packet * F + i];
                pb.NoteEnqueued(b, RxContentClass.Unclassified, seamNext); seamNext = false;
                q.Enqueue(b);
            }
        }
        for (int c = 0; output.Count < signal.Length - F * Bpc; c++)
        {
            if (evt == "stall300" && c >= 15 && c < 18) { }                       // 300 ms with nothing
            else if (evt == "stall300" && c == 18) Arrive(Bpc * 4);               // then all of it at once
            else if (evt == "lost6" && c == 15) { packet += 6; seamNext = mode == RxCatchUpMode.On; Arrive(Bpc); }
            else if (evt == "loss1s" && c >= 15 && c < 25) packet += Bpc;         // a second never arrives
            else Arrive(Bpc);
            pb.RenderCallback(q, ref buffer, ref offset, outBuf, false, 2, () => true);
            for (int i = 0; i < F * Bpc; i++) output.Add(outBuf[2 * i]);
        }
        return output.ToArray();
    }

    // ── Measures: the material's own figures are given beside each, so a change can be told from the material ──

    private static string Measure(float[] y, string material)
    {
        int from = 100 * F, to = Math.Min(y.Length, 400 * F);
        double Rms(int a) { double e = 0; for (int i = a; i < a + 120; i++) e += (double)y[i] * y[i]; return Math.Sqrt(e / 120); }
        var levels = new List<double>(); for (int a = from; a + 120 < to; a += 60) levels.Add(Rms(a));
        double med = levels.OrderBy(v => v).ElementAt(levels.Count / 2), worst = levels.Min();
        string s = $"deepest 2.5 ms dip {20 * Math.Log10(med / Math.Max(worst, 1e-9)):0.0} dB below the median";
        if (material.StartsWith("tone"))
        {
            double hz = material == "tone500" ? 500 : 625, step = 0;
            int w = 192; double prev = double.NaN;
            for (int b = from; b + w < to; b += 48)
            {
                double i = 0, qq = 0;
                for (int k = 0; k < w; k++) { double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (k + 0.5) / w); int n = b + k; i += y[n] * win * Math.Sin(2 * Math.PI * hz * n / Rate); qq += y[n] * win * Math.Cos(2 * Math.PI * hz * n / Rate); }
                double ph = Math.Atan2(qq, i);
                if (!double.IsNaN(prev)) { double d = ph - prev; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; step = Math.Max(step, Math.Abs(d)); }
                prev = ph;
            }
            s += $"; largest phase step {step:0.000} rad";
        }
        else s += " (on this material most of that is its own ups and downs: compare with the other file of the pair)";
        return s;
    }

    private static void WriteWav(string path, float[] mono)
    {
        using var w = new WaveFileWriter(path, new WaveFormat(Rate, 16, 1));
        var bytes = new byte[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) { short v = (short)Math.Clamp(mono[i] * 32767f, -32768f, 32767f); bytes[2 * i] = (byte)v; bytes[2 * i + 1] = (byte)(v >> 8); }
        w.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void Renders_the_lag_shifter_beside_today_s_code_for_listening()
    {
        string? dir = Environment.GetEnvironmentVariable("JJFLEX_TRACKP2_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) { _out.WriteLine("NOT RUN: set JJFLEX_TRACKP2_RENDER_DIR to a folder to write the renders"); return; }
        Directory.CreateDirectory(dir);
        int frames = Rate * 6;
        var measured = new StringBuilder();
        measured.AppendLine("Track P2 renders: what each file measures. Events happen 1.5 s in. The figures are taken from 1 s to 4 s.");
        measured.AppendLine();
        foreach (string m in Names)
        {
            var signal = Materials(m, frames);
            void Save(string file, float[] y) { WriteWav(Path.Combine(dir, file), y); measured.AppendLine($"{file}: {Measure(y, m)}"); }
            foreach (string evt in new[] { "switch-480to5024", "switch-480to5024-back", "turn-on", "turn-off", "retime" })
            {
                string e = evt == "switch-480to5024-back" ? "switch-5024to480" : evt;
                Save($"{m}-{e}-today.wav", Slot(signal, evt, shifter: false));
                Save($"{m}-{e}-{(evt == "retime" ? "drain" : "shifter")}.wav", Slot(signal, evt, shifter: true));
            }
            Save($"{m}-stall300-today.wav", Queue(signal, "stall300", RxCatchUpMode.Off));
            Save($"{m}-stall300-catchup.wav", Queue(signal, "stall300", RxCatchUpMode.On));
            Save($"{m}-lost6-today-splice.wav", Queue(signal, "lost6", RxCatchUpMode.Off));
            Save($"{m}-lost6-seam-join.wav", Queue(signal, "lost6", RxCatchUpMode.On));
            Save($"{m}-loss1s-today.wav", Queue(signal, "loss1s", RxCatchUpMode.Off));
            Save($"{m}-loss1s-bridge-play-at-once.wav", Queue(signal, "loss1s", RxCatchUpMode.On, playAtOnce: true));
            Save($"{m}-loss1s-bridge-wait-full.wav", Queue(signal, "loss1s", RxCatchUpMode.On, playAtOnce: false));
            measured.AppendLine();
        }
        File.WriteAllText(Path.Combine(dir, "measured.txt"), measured.ToString());
        _out.WriteLine(measured.ToString());
    }
}

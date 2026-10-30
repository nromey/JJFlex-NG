using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace JJFlexWpf
{
    public static partial class EarconPlayer
    {
        // ── Pre-rendered sounds (#855, step 5) ─────────────────────────────
        //
        // RULED by Noel 2026-10-30 01:44-01:52: the frequent sounds made once
        // and played from memory; the configurable ones (the countdown's pip
        // and long tone, CW dits and dahs) made again when their setting
        // changes.
        //
        // WHAT THE MEASUREMENT SAID IT BUYS, so nobody expects more of it.
        // Step 3 timed every sound offline: a warm render is 0.1 to 3 ms (the
        // countdown pip 2.3 ms median and 5 ms worst in a Debug build, 0.4 and
        // 1.5 optimised) against 90 to 200 ms of output buffering, and the first
        // sound a process ever makes pays 35 to 40 ms compiling the synthesis
        // code. So this removes a few milliseconds of JITTER from every sound
        // and the one-off compile from the first, which is steadiness, not
        // speed. The buffer setting is the speed.
        //
        // AT STARTUP, NOT COMPILED IN, and that is the measured choice: the
        // whole set renders in tens of milliseconds on a pool thread, while a
        // compiled-in copy would be a second description of every sound that
        // drifts the day a recipe changes. A rendered buffer IS the recipe's
        // output, by construction.
        //
        // HOW "IDENTICAL" IS KEPT. Nothing about a sound's making changes: the
        // same render runs, once, and its samples are kept under a key made of
        // EVERYTHING the render reads (the voice's every property, the steps,
        // the level, and which shaping). A play whose key is held plays the
        // held samples; any other play renders as it always did. A voice with
        // noise in it is never held, because its noise is seeded from the
        // clock and differs on every play today. Level trims and the bench's
        // gain are applied after, at the hand-off, as before. The tests render
        // every sound both ways and compare samples.
        //
        // BOUNDED. The held set is exactly what the warm-up renders, replaced
        // whole by the next warm-up, so a stream of one-off sounds (random
        // typing tones, the bench) can never grow it or push the pip out. CW
        // elements are learned on first use instead, because their lengths come
        // from the sender, in a small set of their own that is cleared if it
        // ever passes its cap.

        /// <summary>For the tests that compare held and freshly made samples.</summary>
        internal static bool SoundCacheEnabled { get; set; } = true;

        private static volatile ConcurrentDictionary<string, float[]> _held = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, float[]> _cwHeld = new(StringComparer.Ordinal);
        private const int CwHeldCap = 64;

        /// <summary>While this thread warms the set, every render it makes is collected here.</summary>
        [ThreadStatic] private static Dictionary<string, float[]>? _warmCollector;

        private static long _heldHits;

        /// <summary>How many plays have come from memory, for the tests.</summary>
        internal static long HeldHits => Interlocked.Read(ref _heldHits);

        /// <summary>How many sounds are held, for the trace and the tests.</summary>
        internal static int HeldSoundCount => _held.Count;

        /// <summary>
        /// The samples for <paramref name="key"/>: held ones when there are,
        /// otherwise <paramref name="render"/>'s, collected if this thread is
        /// warming. A null key means "never hold this one".
        /// </summary>
        private static float[] HeldOrRender(string? key, Func<float[]> render)
        {
            if (key != null && SoundCacheEnabled && _warmCollector == null
                && _held.TryGetValue(key, out var held))
            {
                Interlocked.Increment(ref _heldHits);
                SoundTiming.Source("memory");
                return held;
            }
            long synth = SoundTiming.SynthStart();
            var samples = render();
            SoundTiming.SynthEnd(synth);
            if (key != null && _warmCollector != null) _warmCollector[key] = samples;
            return samples;
        }

        // ── Keys ──────────────────────────────────────────────────────────

        private static readonly PropertyInfo[] VoiceProperties = Array.FindAll(
            typeof(MeterVoice).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => p.CanRead && p.GetIndexParameters().Length == 0);

        /// <summary>
        /// Every public property of a voice, so a voice that differs in any way
        /// a render could read has a different key, including properties added
        /// after this was written. Null for a voice with noise in it.
        /// </summary>
        internal static string? VoiceKey(MeterVoice? voice)
        {
            if (voice == null) return "none";
            if (voice.NoiseLevel > 0f) return null;
            var sb = new StringBuilder(160);
            foreach (var p in VoiceProperties)
            {
                object? v = p.GetValue(voice);
                sb.Append(p.Name).Append('=');
                switch (v)
                {
                    case float[] arr:
                        foreach (var f in arr) sb.Append(f.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                        break;
                    case IFormattable fmt:
                        sb.Append(fmt.ToString(null, CultureInfo.InvariantCulture));
                        break;
                    default:
                        sb.Append(v);
                        break;
                }
                sb.Append(';');
            }
            return sb.ToString();
        }

        private static string? VoicedKey(string shape, MeterVoice voice, (int freq, int ms)[] steps, float volume)
        {
            string? vk = VoiceKey(voice);
            if (vk == null) return null;
            // The voice set too: DecayingOver reads it, so the same base voice
            // can shape differently under Rich and Simple.
            var sb = new StringBuilder(shape).Append('|').Append(EarconVoices.ActiveSet)
                .Append('|').Append(volume.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            foreach (var (f, ms) in steps) sb.Append(f).Append(':').Append(ms).Append(',');
            return sb.Append('|').Append(vk).ToString();
        }

        /// <summary><see cref="RenderVoiced"/>, from memory when held.</summary>
        private static float[] RenderVoicedHeld(MeterVoice voice, (int freq, int ms)[] steps, float volume) =>
            HeldOrRender(VoicedKey("seq", voice, steps, volume), () => RenderVoicedCore(voice, steps, volume));

        /// <summary>A chirp's samples, made in full by the chirp itself, from memory when held.</summary>
        private static float[] RenderChirpHeld(int startHz, int endHz, int durationMs, float volume) =>
            HeldOrRender($"chirp|{startHz}|{endHz}|{durationMs}|{volume.ToString("R", CultureInfo.InvariantCulture)}",
                () => ReadAllMono(new ChirpSampleProvider(SampleRate, startHz, endHz, durationMs, volume)));

        private static float[] ReadAllMono(ISampleProvider p)
        {
            var all = new List<float>(SampleRate / 4);
            var buf = new float[SampleRate / 10];
            int n;
            while ((n = p.Read(buf)) > 0)
                for (int i = 0; i < n; i++) all.Add(buf[i]);
            return all.ToArray();
        }

        /// <summary>
        /// One CW mark as a provider over samples made by
        /// <see cref="CwToneSampleProvider"/> in full, learned on first use per
        /// pitch, length, shaping, level and voice, so the device's thread
        /// copies a mark rather than synthesising it.
        /// </summary>
        internal static ISampleProvider CwMark(int sampleRate, int hz, int durationMs, int riseFallMs,
            float volume, MeterVoice? voice)
        {
            string? vk = VoiceKey(voice);
            if (vk == null || !SoundCacheEnabled || sampleRate != SampleRate)
                return new CwToneSampleProvider(sampleRate, hz, durationMs, riseFallMs, volume, voice);
            string key = $"cw|{hz}|{durationMs}|{riseFallMs}|{volume.ToString("R", CultureInfo.InvariantCulture)}|{vk}";
            if (!_cwHeld.TryGetValue(key, out var samples))
            {
                samples = ReadAllMono(new CwToneSampleProvider(sampleRate, hz, durationMs, riseFallMs, volume, voice));
                if (_cwHeld.Count >= CwHeldCap) _cwHeld.Clear();
                _cwHeld[key] = samples;
            }
            return new RenderedSampleProvider(samples);
        }

        // ── The warm-up ───────────────────────────────────────────────────

        /// <summary>
        /// The sounds held in memory: the ones an operator hears all the time,
        /// and the countdown at its current settings. Each is played offline
        /// exactly as the app plays it, so its key is the key a live play will
        /// look for. A sound whose switch is off is not warmed; switching it on
        /// goes through the settings, which warm again.
        /// </summary>
        private static readonly Action[] WarmSet =
        {
            TxStartTone, TxStopTone, Warning1Beep, Warning2Beep, OhCrapBeep, HardKillTone,
            TuneOnTone, TuneOffTone, ATUSuccessTone, ATUFailTone,
            CountdownRecordTone, CountdownTransmitTone, FixerStageDoneTone,
            () => { var s = Radios.CountdownSettings.Current; CountdownBeatTone(s.PipHz, s.PipMs, false, s.PipSound); },
            () => { var s = Radios.CountdownSettings.Current; CountdownBeatTone(s.PipHz * 2, s.LandingMs, true, s.PipSound); },
            () => ConnectPhaseTone(2), () => ConnectPhaseTone(3), ConnectSuccessTone,
            DialogOpenTone, DialogCloseTone, PlayCollapseAll,
            FeatureOnTone, FeatureOffTone, MuteAllOnTone, MuteAllOffTone,
            LeaderEnterTone, LeaderInvalidTone, LeaderCancelTone, LeaderHelpTone,
            BandBoundaryBeep, DingTone, ProblemRecordedTone, WarningAlarmTone,
            RecorderStartBeep, RecorderStopBeep,
        };

        private static int _warmRequested;
        private static int _warmRunning;

        /// <summary>
        /// Warm the held set again, on a pool thread, soon. Called when the
        /// audio engine starts and whenever the audio settings are applied, so
        /// a changed countdown, voice set or switch is held before it is next
        /// heard. Calls that arrive during a warm-up fold into one more.
        /// </summary>
        public static void PrewarmSoon()
        {
            Interlocked.Exchange(ref _warmRequested, 1);
            if (Interlocked.CompareExchange(ref _warmRunning, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try
                {
                    while (Interlocked.Exchange(ref _warmRequested, 0) == 1)
                        Prewarm();
                }
                finally
                {
                    Interlocked.Exchange(ref _warmRunning, 0);
                    if (Volatile.Read(ref _warmRequested) == 1) PrewarmSoon();
                }
            });
        }

        /// <summary>Warm the held set now, on this thread, and replace the old set whole.</summary>
        internal static void Prewarm()
        {
            long t0 = Stopwatch.GetTimestamp();
            var collected = new Dictionary<string, float[]>(StringComparer.Ordinal);
            var saved = _warmCollector;
            _warmCollector = collected;
            try
            {
                foreach (var play in WarmSet)
                {
                    try { RenderOffline(play); }
                    catch (Exception ex) { Trace.WriteLine($"EarconPlayer.Prewarm: {ex.Message}"); }
                }
            }
            finally
            {
                _warmCollector = saved;
            }
            _held = new ConcurrentDictionary<string, float[]>(collected, StringComparer.Ordinal);
            long samples = 0;
            foreach (var v in collected.Values) samples += v.Length;
            JJTrace.Tracing.TraceLine(
                $"EarconPlayer: {collected.Count} sounds held in memory, {samples * sizeof(float) / 1024} KB, made in "
                + $"{(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency:0.0} ms", TraceLevel.Info);
        }
    }
}

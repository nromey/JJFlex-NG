using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using NAudio.Wave;

namespace JJFlexWpf
{
    /// <summary>
    /// Stage timings for every sound the alert channel plays (#855, step 1):
    /// when it was asked for, how long it took to synthesise, when it was
    /// handed to the mixer, when the mixer first read it, and how much audio
    /// was already queued in the output device at that instant. One trace
    /// line per sound, written when the sound has finished leaving the mixer.
    /// </summary>
    /// <remarks>
    /// <para><b>Why split the lag.</b> Noel, 2026-10-30: <i>"Instrument the hell
    /// out of sound so we know where we need to trim, if memory storage or
    /// pre-gen would help."</i> The countdown measurement of that night found a
    /// pip reaching the speaker 110 to 177 ms after its number, and attributed
    /// most of it to the alert channel's output buffers by experiment. This
    /// record states the split for every sound, so the attribution is read off
    /// a line instead of inferred from a rebuilt binary.</para>
    ///
    /// <para><b>Where each stage is stamped.</b> ASKED: the earcon's gate, the
    /// first thing every named sound does. SYNTH: around the render that turns
    /// parameters into samples (zero for a wav already in memory; "lazy" for a
    /// provider that synthesises inside the mixer's read, whose cost then shows
    /// as mixRead). HANDOFF: the call that adds the sound to the alert mixer.
    /// MIX: the mixer's first read of it, on the output device's thread.
    /// QUEUED: at that read, the audio the device already holds ahead of it,
    /// from the device's own played-bytes count against the frames the mixer has
    /// produced. A newly added mixer input contributes from the start of the
    /// next read, so its first sample leaves the device QUEUED after MIX.
    /// <c>asked2out</c> is therefore the app's own estimate of when the sound
    /// reaches the Windows audio engine; tools/soundtiming measures when it is
    /// actually heard and compares the two.</para>
    ///
    /// <para><b>Behind a switch, because one line per sound is still a line per
    /// keypress.</b> On when the trace is at its detailed level (the detailed
    /// capture), or for a whole run when the process starts with the
    /// environment variable <c>JJFLEX_SOUND_TIMING=1</c> - the harness's way
    /// in, which needs no key press and no change to the operator's settings.
    /// Off, the cost is one field read per sound and nothing is wrapped.</para>
    ///
    /// <para><b>The record follows the calling thread from gate to hand-off.</b>
    /// Every earcon gates and hands off on one thread without returning in
    /// between, so a thread-static pending record attributes the hand-off to the
    /// sound that gated. A sound made of several mixer inputs (the expand sweep
    /// is a chirp and a noise band) counts its later inputs on the same record
    /// when they are handed off within <see cref="SameSoundWindowMs"/> of the
    /// first. A hand-off with no pending record (a sound that never gates, such
    /// as CW or the recorder cues) is recorded under the name its caller gives
    /// through <see cref="Begin"/>, or as <c>unnamed</c>.</para>
    /// </remarks>
    public static class SoundTiming
    {
        /// <summary>Inputs handed off this soon after a sound's first, on the same thread with no gate between, belong to it.</summary>
        internal const double SameSoundWindowMs = 50;

        /// <summary>The trace line's prefix. tools/soundtiming finds lines by it.</summary>
        public const string LinePrefix = "SoundTiming:";

        /// <summary>Format version, the first field of every line.</summary>
        public const int FormatVersion = 1;

        /// <summary>
        /// What these lines are, in this writer's own words (#625). DRAFT for Noel.
        /// </summary>
        public static readonly JJTrace.TraceRecordKind Record = new JJTrace.TraceRecordKind(
            "soundTiming",
            "lines beginning 'SoundTiming:' time one sound each, from the moment the program asked for it to"
            + " the moment its first sample left for the speakers: how long it took to make (synth), when it"
            + " was handed to the sound mixer, when the mixer first read it, and how much sound was already"
            + " waiting in the output device ahead of it (queued). Times are in milliseconds. They are here"
            + " because the detailed recording is on, or because the program was started with sound timing"
            + " switched on for a test.");

        private static readonly bool _forcedByEnvironment = ReadEnvironment();

        private static volatile bool _forced;

        private static bool ReadEnvironment()
        {
            try
            {
                string? v = Environment.GetEnvironmentVariable("JJFLEX_SOUND_TIMING");
                return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Turn timing on regardless of the trace level. For tests and the
        /// harness; the app itself never sets it.
        /// </summary>
        internal static bool Forced
        {
            get => _forced;
            set => _forced = value;
        }

        /// <summary>Whether a sound asked for now gets a timing record.</summary>
        public static bool Enabled
        {
            get
            {
                if (_forced || _forcedByEnvironment) return true;
                try
                {
                    var sw = JJTrace.Tracing.TheSwitch;
                    return sw != null && sw.Level >= TraceLevel.Verbose;
                }
                catch { return false; }
            }
        }

        /// <summary>Where a finished line goes. The trace, unless a test replaces it.</summary>
        internal static Action<string> Sink { get; set; } = DefaultSink;

        private static void DefaultSink(string line)
        {
            ThreadPool.QueueUserWorkItem(static l =>
            {
                try { JJTrace.Tracing.TraceRecord(Record, (string)l!); } catch { }
            }, line);
        }

        /// <summary>
        /// The alert device's state at a mixer read: frames the mixer has
        /// produced so far, frames the device reports as played, and its buffer
        /// geometry. Supplied by the alert channel; null when there is none.
        /// </summary>
        internal static Func<DeviceSnapshot>? AlertDevice { get; set; }

        internal readonly struct DeviceSnapshot
        {
            public DeviceSnapshot(long producedFrames, long playedFrames, int bufferMs, int bufferCount, int sampleRate)
            {
                ProducedFrames = producedFrames;
                PlayedFrames = playedFrames;
                BufferMs = bufferMs;
                BufferCount = bufferCount;
                SampleRate = sampleRate;
            }
            public long ProducedFrames { get; }
            public long PlayedFrames { get; }
            public int BufferMs { get; }
            public int BufferCount { get; }
            public int SampleRate { get; }
        }

        [ThreadStatic] private static SoundTimingRecord? _pending;
        [ThreadStatic] private static SoundTimingRecord? _lastHanded;

        /// <summary>
        /// A sound has been asked for. Starts a record on this thread when
        /// timing is on; otherwise clears any stale one.
        /// </summary>
        public static void Begin(string name, string? detail = null)
            => Begin(name, detail, Stopwatch.GetTimestamp());

        /// <summary>
        /// <see cref="Begin(string, string?)"/> for a sound asked for earlier
        /// than it reaches the player, such as a CW string that waited in its
        /// queue: <paramref name="askedTimestamp"/> is the Stopwatch timestamp of the ask.
        /// </summary>
        public static void Begin(string name, string? detail, long askedTimestamp)
        {
            _lastHanded = null;
            _pending = Enabled ? new SoundTimingRecord(name, detail, askedTimestamp) : null;
        }

        /// <summary>The sound asked for will not play (a switch is off).</summary>
        public static void Cancel()
        {
            _pending = null;
        }

        /// <summary>
        /// What the analyser needs to rebuild this sound when its name alone
        /// does not say (a countdown beat's pitch and length, a CW string's
        /// first mark). No spaces.
        /// </summary>
        public static void Recipe(string recipe)
        {
            var r = _pending;
            if (r != null) r.Recipe = recipe;
        }

        /// <summary>Start timing a synthesis step; pass the result to <see cref="SynthEnd"/>.</summary>
        public static long SynthStart() => _pending != null ? Stopwatch.GetTimestamp() : 0;

        /// <summary>A synthesis step finished; its time adds to the pending record.</summary>
        public static void SynthEnd(long started, string source = "rendered")
        {
            var r = _pending;
            if (r == null || started == 0) return;
            r.SynthTicks += Stopwatch.GetTimestamp() - started;
            r.SynthCount++;
            r.NoteSource(source);
        }

        /// <summary>Where the samples come from, when no synthesis step says so.</summary>
        public static void Source(string source)
        {
            _pending?.NoteSource(source);
        }

        /// <summary>
        /// A sound is being handed to the mixer. Returns the provider to hand
        /// over: the same one when timing is off, else a probe around it that
        /// stamps the mixer's reads. The probe never changes a sample.
        /// </summary>
        internal static ISampleProvider AtHandoff(ISampleProvider input)
        {
            var r = _pending;
            long now = Stopwatch.GetTimestamp();
            if (r == null)
            {
                var last = _lastHanded;
                if (last != null && TicksToMs(now - last.HandoffTs) <= SameSoundWindowMs)
                {
                    Interlocked.Increment(ref last.Inputs);
                    return input;
                }
                if (!Enabled) return input;
                r = new SoundTimingRecord("unnamed", null, now);
            }
            r.HandoffTs = now;
            r.Inputs = 1;
            if (r.Source == null) r.Source = "lazy";
            _pending = null;
            _lastHanded = r;
            return new Probe(input, r);
        }

        internal static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Wraps one mixer input and stamps its reads. Passes every sample
        /// through untouched.
        /// </summary>
        internal sealed class Probe : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly SoundTimingRecord _record;
            private bool _first = true;
            private int _done;

            public Probe(ISampleProvider source, SoundTimingRecord record)
            {
                _source = source;
                _record = record;
            }

            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(Span<float> buffer)
            {
                long t0 = Stopwatch.GetTimestamp();
                if (_first)
                {
                    _first = false;
                    _record.MixTs = t0;
                    try
                    {
                        var dev = AlertDevice;
                        if (dev != null)
                        {
                            var s = dev();
                            _record.BufferMs = s.BufferMs;
                            _record.BufferCount = s.BufferCount;
                            if (s.PlayedFrames >= 0 && s.SampleRate > 0)
                            {
                                long queued = s.ProducedFrames - s.PlayedFrames;
                                if (queued >= 0) _record.QueuedMs = queued * 1000.0 / s.SampleRate;
                            }
                        }
                    }
                    catch { }
                }
                int n = _source.Read(buffer);
                long t1 = Stopwatch.GetTimestamp();
                long spent = t1 - t0;
                _record.MixReadTicks += spent;
                if (spent > _record.MixReadMaxTicks) _record.MixReadMaxTicks = spent;
                _record.Reads++;
                _record.Samples += Math.Max(n, 0);
                if (n < buffer.Length && Interlocked.Exchange(ref _done, 1) == 0)
                {
                    try { Sink(Format(_record)); } catch { }
                }
                return n;
            }
        }

        /// <summary>The one line a finished sound writes. tools/soundtiming parses it with <see cref="SoundTimingLine"/>.</summary>
        internal static string Format(SoundTimingRecord r)
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append(LinePrefix);
            sb.Append(" v=").Append(FormatVersion);
            sb.Append(" sound=").Append(Clean(r.Name));
            if (!string.IsNullOrEmpty(r.Detail)) sb.Append(" detail=").Append(Clean(r.Detail!));
            if (!string.IsNullOrEmpty(r.Recipe)) sb.Append(" recipe=").Append(Clean(r.Recipe!));
            sb.Append(" qpcAsked=").Append(r.AskedTs.ToString(c));
            sb.Append(" qpcMix=").Append(r.MixTs.ToString(c));
            sb.Append(" qpcFreq=").Append(Stopwatch.Frequency.ToString(c));
            sb.Append(" source=").Append(r.Source ?? "lazy");
            sb.Append(" synth=").Append(Ms(TicksToMs(r.SynthTicks)));
            sb.Append(" synthSteps=").Append(r.SynthCount.ToString(c));
            sb.Append(" asked2handoff=").Append(Ms(TicksToMs(r.HandoffTs - r.AskedTs)));
            sb.Append(" handoff2mix=").Append(Ms(TicksToMs(r.MixTs - r.HandoffTs)));
            if (r.QueuedMs >= 0)
            {
                sb.Append(" queued=").Append(Ms(r.QueuedMs));
                sb.Append(" asked2out=").Append(Ms(TicksToMs(r.MixTs - r.AskedTs) + r.QueuedMs));
            }
            else
            {
                sb.Append(" queued=unknown");
            }
            sb.Append(" mixRead=").Append(Ms(TicksToMs(r.MixReadTicks)));
            sb.Append(" mixReadMax=").Append(Ms(TicksToMs(r.MixReadMaxTicks)));
            sb.Append(" reads=").Append(r.Reads.ToString(c));
            sb.Append(" inputs=").Append(Volatile.Read(ref r.Inputs).ToString(c));
            sb.Append(" samples=").Append(r.Samples.ToString(c));
            if (r.BufferMs > 0) sb.Append(" buffer=").Append(r.BufferMs.ToString(c)).Append('x').Append(r.BufferCount.ToString(c));
            return sb.ToString();

            static string Ms(double ms) => ms.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Values are space-delimited, so spaces inside one become semicolons.</summary>
        private static string Clean(string s) => s.Trim().Replace(' ', ';');
    }

    /// <summary>One sound's timing, filled in stage by stage.</summary>
    internal sealed class SoundTimingRecord
    {
        public SoundTimingRecord(string name, string? detail, long askedTs)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "unnamed" : name;
            Detail = detail;
            AskedTs = askedTs;
        }

        public string Name { get; }
        public string? Detail { get; }
        public string? Recipe;
        public long AskedTs;
        public long SynthTicks;
        public int SynthCount;
        public string? Source;
        public long HandoffTs;
        public int Inputs;
        public long MixTs;
        public double QueuedMs = -1;
        public int BufferMs;
        public int BufferCount;
        public long MixReadTicks;
        public long MixReadMaxTicks;
        public int Reads;
        public long Samples;

        public void NoteSource(string source)
        {
            if (Source == null || Source == source) Source = source;
            else Source = "mixed";
        }
    }
}

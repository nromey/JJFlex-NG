using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// What a dropout verdict was measured on (repair round 1, R7): the capture
    /// device's name, its host API, and the format the stream opened at. A pass
    /// on one of these says nothing about another, so each is part of the key.
    /// </summary>
    public sealed record TxCaptureIdentity(string Device, int HostApi, int SampleRate, int Channels);

    /// <summary>One machine's measured answer: did the microphone deliver this callback rate without dropouts?</summary>
    public sealed class TxCallbackVerdict
    {
        /// <summary>The capture device's name, as the stream opened it.</summary>
        public string Device { get; set; } = "";
        /// <summary>PortAudio's host API type id for that device.</summary>
        public int HostApi { get; set; } = -1;
        /// <summary>The rate the stream actually opened at.</summary>
        public int SampleRate { get; set; }
        /// <summary>1 on a mono device, 2 otherwise.</summary>
        public int Channels { get; set; }

        /// <summary>Whether this verdict was measured on exactly this capture.</summary>
        public bool Describes(TxCaptureIdentity capture) => capture != null
            && string.Equals(Device, capture.Device ?? "", StringComparison.OrdinalIgnoreCase)
            && HostApi == capture.HostApi && SampleRate == capture.SampleRate && Channels == capture.Channels;
        public int CallbacksPerSecond { get; set; }
        public bool Passed { get; set; }
        public long Callbacks { get; set; }
        public long Overflows { get; set; }
        public long Underflows { get; set; }
        public DateTime MeasuredUtc { get; set; }
    }

    [XmlRoot("TxCallbackChecks")]
    public sealed class TxCallbackVerdictFile
    {
        public List<TxCallbackVerdict> Verdicts { get; set; } = new();
    }

    /// <summary>
    /// How many callbacks a second the transmit capture runs at (Sprint 49
    /// Track H, item D; ruled S4, 2026-11-18 08:19).
    /// </summary>
    /// <remarks>
    /// <para><b>The problem.</b> The capture has always run ten callbacks a
    /// second, so each callback hands the transmit chain ten 10 ms frames back
    /// to back. A helper engine (NR2, NNR) answers frame n minus k (k is 2 or
    /// 3) without waiting, so inside one burst most frames ask for an answer to
    /// a frame sent microseconds earlier, and come back late. On transmit a
    /// late frame now latches the slot dry for the over (item C), so at ten a
    /// second a helper engine would barely run on transmit at all. Read from
    /// the code by the research, not measured: the per-over trace
    /// (<c>TxNoiseChain over:</c>, its "longest burst") is the measurement.</para>
    /// <para><b>The ruling.</b> Smaller callbacks ("they probably don't need to
    /// be as large as they are now"), but decided by THIS machine's own
    /// measurement, not one setting for everyone. Two checks, both needed: the
    /// helper's own per-frame cost from its self-benchmark (reported in its
    /// handshake), and a dropout check that the microphone really delivers the
    /// smaller bursts here. The benchmark alone cannot answer the second.</para>
    /// <para><b>How the second check is answered without opening anything
    /// extra.</b> A capture stream that ran at a faster rate is judged when it
    /// closes, from PortAudio's own status flags on that stream (any input
    /// overflow or underflow is a dropout) over a long enough run. The verdict
    /// is kept per machine and per device, under the settings root.</para>
    /// <para><b>The try is automatic (S13, ruled 2026-11-18 21:21).</b> Once
    /// the helper's benchmark qualifies and no verdict is held (or it has
    /// expired), the app tries the faster rate on its own while receiving
    /// (<see cref="ShouldTry"/>, <see cref="TryProgress"/>; the stream swap is
    /// FlexBase's, in <c>FlexBase.TxCaptureTry.cs</c>). The faster stream is
    /// opened while the running one carries on, and swapped in only once it is
    /// open, so a failed open leaves the microphone exactly as it was. The
    /// environment variable still overrides, and then nothing is tried.</para>
    /// <para><b>Never a pipeline depth of about eleven</b> (S4): that would add
    /// a hundred milliseconds to every helper engine. A paced exchange thread
    /// is the fallback if smaller callbacks do not hold.</para>
    /// </remarks>
    public static class TxCallbackRate
    {
        /// <summary>The rate that has always shipped: 100 ms of audio per callback.</summary>
        public const int Shipped = JJPortaudio.AudioBuffering.DefaultCallbacksPerSecond;

        /// <summary>The faster rate the machine may earn: 20 ms, two frames per callback, the burst Track A measured the helper keeping up with.</summary>
        public const int Fast = 50;

        /// <summary>
        /// The helper's per-frame cost must be under this at the 99th percentile
        /// (5 ms, half the 10 ms frame), the rule Noel ruled for Premium CW
        /// (Track E design, part four, question 11). The rest of the frame is
        /// the pipe's round trip.
        /// </summary>
        public const long CostBudgetNanos = 5_000_000;

        /// <summary>A faster run shorter than this many seconds proves nothing either way and records no verdict.</summary>
        public const int MinimumSecondsForVerdict = 20;

        /// <summary>A pass older than this is not trusted (repair round 1, R7): drivers, power plans and loads change.</summary>
        public static readonly TimeSpan MaxVerdictAge = TimeSpan.FromDays(30);

        private static long _helperCost = -1;

        /// <summary>
        /// The helper's worst 99th-percentile frame cost over the engines it
        /// offers for transmit, in nanoseconds, from its last handshake; null
        /// until a helper has said hello this session. Set by the app.
        /// </summary>
        public static long? HelperFrameCostP99Nanos
        {
            get { long v = Interlocked.Read(ref _helperCost); return v < 0 ? null : v; }
            set
            {
                Interlocked.Exchange(ref _helperCost, value ?? -1);
                // Off the caller's thread: the helper's handshake arrives under
                // its host's lock, and this reads a file.
                ThreadPool.QueueUserWorkItem(_ => Reconsider());
            }
        }

        /// <summary>The capture the transmit stream opened on this session, set at each open; null before one.</summary>
        public static TxCaptureIdentity? CurrentCapture { get; set; }

        /// <summary>The rate the capture opened at this session (0 before one).</summary>
        public static int CurrentRate { get; set; }

        /// <summary>
        /// What the NEXT capture open would choose, worked out again whenever the
        /// helper's benchmark arrives (repair round 1, R7), since the rate is
        /// otherwise chosen only at connect, often before the helper has started.
        /// It applies from the next capture open; the running stream is never
        /// reopened for it. Null until a capture has opened.
        /// </summary>
        public static Decision? NextOpen { get; private set; }

        /// <summary>Tests only: a store under a scratch root in place of the app's.</summary>
        internal static TxCallbackVerdictStore? StoreForTest { get; set; }

        /// <summary>The store verdicts are read from and kept in: the app's, under the settings root.</summary>
        public static TxCallbackVerdictStore? Store => StoreForTest ?? TxCallbackVerdictStore.Default();

        /// <summary>Work out <see cref="NextOpen"/> again and trace it, once the helper's cost is known.</summary>
        public static void Reconsider()
        {
            var capture = CurrentCapture;
            if (capture == null) return;
            try
            {
                var decision = Decide(JJPortaudio.AudioBuffering.ConfiguredTxCallbacksPerSecond(), HelperFrameCostP99Nanos,
                    Store?.Find(capture, Fast), DateTime.UtcNow);
                NextOpen = decision;
                JJTrace.Tracing.TraceLine("TxCallbackRate: the helper's benchmark arrived; the next transmit capture open would run at "
                    + decision.CallbacksPerSecond + " a second (" + decision.Reason + "); this one stays at " + CurrentRate,
                    System.Diagnostics.TraceLevel.Info);
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("TxCallbackRate: could not reconsider the rate: " + ex.Message, System.Diagnostics.TraceLevel.Error);
            }
        }

        /// <summary>Why the rate was chosen, for the trace.</summary>
        public sealed record Decision(int CallbacksPerSecond, string Reason);

        /// <summary>
        /// The rate for the next capture stream. An environment override (the
        /// bench) wins. Otherwise <see cref="Fast"/> only when BOTH checks pass:
        /// the helper's cost is known and inside the budget, and this device has
        /// a passed dropout verdict at that rate. Anything unknown is the shipped
        /// rate, which is never worse than today.
        /// </summary>
        public static Decision Decide(int? overrideRate, long? helperCostP99Nanos, TxCallbackVerdict? verdictAtFast, DateTime nowUtc)
        {
            if (overrideRate is int forced) return new(forced, "set for this launch by " + JJPortaudio.AudioBuffering.TxCallbackRateEnvironmentVariable);
            if (helperCostP99Nanos is not long cost) return new(Shipped, "no helper benchmark yet this session");
            if (cost >= CostBudgetNanos) return new(Shipped, $"the helper's frame cost ({cost / 1000} microseconds at the 99th percentile) is over the {CostBudgetNanos / 1000} budget");
            if (verdictAtFast == null) return new(Shipped, $"this microphone has not yet been measured at {Fast} a second");
            if (!verdictAtFast.Passed) return new(Shipped, $"this microphone dropped audio at {Fast} a second (measured {verdictAtFast.MeasuredUtc:yyyy-MM-dd})");
            if (nowUtc - verdictAtFast.MeasuredUtc > MaxVerdictAge)
                return new(Shipped, $"this microphone's pass at {Fast} a second is from {verdictAtFast.MeasuredUtc:yyyy-MM-dd}, older than {MaxVerdictAge.TotalDays:0} days; it needs measuring again");
            return new(Fast, $"the helper's cost and this microphone both measured fit at {Fast} a second");
        }

        /// <summary>
        /// S13 (ruled 2026-11-18 21:21): should the app try the faster rate now?
        /// Only once the slow engines qualify (the helper's benchmark is in and
        /// inside the budget) and no verdict is held, or the one held has
        /// expired; only while receiving, never keyed; never during a microphone
        /// check or a recording; only on a running capture; once per connection.
        /// The environment override wins: nothing is tried under it.
        /// </summary>
        public static TxTryDecision ShouldTry(int? overrideRate, long? helperCostP99Nanos, TxCallbackVerdict? verdictAtFast,
            DateTime nowUtc, int currentRate, bool alreadyTriedThisConnection, bool keyed, bool checkOrRecordingRunning,
            bool captureRunning)
        {
            if (overrideRate is int forced) return new(TxTryAnswer.NotThisConnection, "the rate is set for this launch by "
                + JJPortaudio.AudioBuffering.TxCallbackRateEnvironmentVariable + " (" + forced + ")");
            if (alreadyTriedThisConnection) return new(TxTryAnswer.NotThisConnection, "already tried on this connection");
            if (currentRate == Fast) return new(TxTryAnswer.NotThisConnection, $"the capture already runs at {Fast} a second");
            if (verdictAtFast != null && nowUtc - verdictAtFast.MeasuredUtc <= MaxVerdictAge)
                return new(TxTryAnswer.NotThisConnection, (verdictAtFast.Passed ? "a pass" : "a failure")
                    + $" at {Fast} a second is held from {verdictAtFast.MeasuredUtc:yyyy-MM-dd}");
            if (helperCostP99Nanos is not long cost) return new(TxTryAnswer.NotNow, "no helper benchmark yet this session");
            if (cost >= CostBudgetNanos) return new(TxTryAnswer.NotNow, $"the helper's frame cost ({cost / 1000} microseconds at the 99th percentile) is over the {CostBudgetNanos / 1000} budget");
            if (keyed) return new(TxTryAnswer.NotNow, "the radio is keyed");
            if (checkOrRecordingRunning) return new(TxTryAnswer.NotNow, "a microphone check or a recording is running");
            if (!captureRunning) return new(TxTryAnswer.NotNow, "the microphone capture is not running");
            return new(TxTryAnswer.Try, verdictAtFast == null
                ? $"the engines qualify and this microphone has not been measured at {Fast} a second"
                : $"the engines qualify and this microphone's verdict at {Fast} a second has expired");
        }

        /// <summary>
        /// A running try, from the stream's own counts since it opened: any
        /// input overflow or underflow fails it at once; a clean run of
        /// <see cref="MinimumSecondsForVerdict"/> seconds passes it.
        /// </summary>
        public static TxTryProgress TryProgress(long callbacks, long overflows, long underflows, int rate)
        {
            if (overflows > 0 || underflows > 0) return TxTryProgress.Failed;
            if (rate > 0 && callbacks >= (long)rate * MinimumSecondsForVerdict) return TxTryProgress.Passed;
            return TxTryProgress.Running;
        }

        /// <summary>The verdict a closing stream earns, or null when it ran too briefly (or at the shipped rate) to say anything.</summary>
        /// <param name="partialRun">
        /// The run did not measure the rate (Sprint 49 Track H2, rulings round):
        /// the stream died part way (#1145: a device invalidated, unplugged, or
        /// an aborted callback), or a microphone check or recording shared the
        /// device during an automatic try. Such a run earns no verdict either
        /// way: it neither earns the faster rate nor loses it. A death is a
        /// device event and its dropout counts stop where the stream stopped; a
        /// shared device's dropouts may be the other stream's. A verdict already
        /// held stays as it was.
        /// </param>
        public static TxCallbackVerdict? Judge(TxCaptureIdentity capture, int rate, long callbacks, long overflows, long underflows, DateTime nowUtc, bool partialRun = false)
        {
            if (partialRun) return null;
            if (capture == null || rate == Shipped || rate <= 0 || callbacks < (long)rate * MinimumSecondsForVerdict) return null;
            return new TxCallbackVerdict
            {
                Device = capture.Device ?? "",
                HostApi = capture.HostApi,
                SampleRate = capture.SampleRate,
                Channels = capture.Channels,
                CallbacksPerSecond = rate,
                Passed = overflows == 0 && underflows == 0,
                Callbacks = callbacks,
                Overflows = overflows,
                Underflows = underflows,
                MeasuredUtc = nowUtc,
            };
        }
    }

    /// <summary>What the automatic try (S13) should do now.</summary>
    public enum TxTryAnswer
    {
        /// <summary>Open the faster stream now.</summary>
        Try,
        /// <summary>Not yet: ask again later this connection (keyed, a check or recording running, the engines not yet qualified).</summary>
        NotNow,
        /// <summary>Nothing to try on this connection (overridden, already tried, already fast, or a verdict held).</summary>
        NotThisConnection,
    }

    /// <summary>The try's answer and why, for the trace.</summary>
    public sealed record TxTryDecision(TxTryAnswer Answer, string Reason);

    /// <summary>Where a running try stands.</summary>
    public enum TxTryProgress { Running, Passed, Failed }

    /// <summary>
    /// The per-machine verdicts, in <c>{settings root}\txCallbackChecks.xml</c>
    /// (resolved from <see cref="RadioConfig.AppDataRoot"/>). Per machine
    /// because the settings root is; per device and rate because that is what
    /// was measured (name, host API and format, R7). A newer verdict for the same capture and rate replaces the
    /// older one. An unreadable file reads as no verdicts (the shipped rate)
    /// and is overwritten by the next verdict: it holds measurements, not the
    /// operator's choices.
    /// </summary>
    public sealed class TxCallbackVerdictStore
    {
        public const string FileName = "txCallbackChecks.xml";
        private static readonly object Gate = new();
        private static readonly XmlSerializer Serializer = new(typeof(TxCallbackVerdictFile));
        private readonly string _path;

        public TxCallbackVerdictStore(string root) => _path = Path.Combine(root ?? throw new ArgumentNullException(nameof(root)), FileName);

        public static TxCallbackVerdictStore? Default()
        {
            string root = RadioConfig.AppDataRoot;
            return string.IsNullOrEmpty(root) ? null : new TxCallbackVerdictStore(root);
        }

        public TxCallbackVerdict? Find(TxCaptureIdentity capture, int rate)
        {
            lock (Gate) return Read().Verdicts.LastOrDefault(v => v.CallbacksPerSecond == rate && v.Describes(capture));
        }

        public bool Record(TxCallbackVerdict verdict)
        {
            if (verdict == null) return false;
            lock (Gate)
            {
                var file = Read();
                var key = new TxCaptureIdentity(verdict.Device, verdict.HostApi, verdict.SampleRate, verdict.Channels);
                file.Verdicts.RemoveAll(v => v.CallbacksPerSecond == verdict.CallbacksPerSecond && v.Describes(key));
                file.Verdicts.Add(verdict);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    using var fs = File.Create(_path);
                    Serializer.Serialize(fs, file);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("TxCallbackVerdictStore: not saved: " + ex.Message);
                    return false;
                }
            }
        }

        private TxCallbackVerdictFile Read()
        {
            try
            {
                if (!File.Exists(_path)) return new();
                using var fs = File.OpenRead(_path);
                return Serializer.Deserialize(fs) as TxCallbackVerdictFile ?? new();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("TxCallbackVerdictStore: unreadable, read as empty: " + ex.Message);
                return new();
            }
        }
    }
}

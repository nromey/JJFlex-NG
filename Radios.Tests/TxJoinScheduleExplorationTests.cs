using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Flex.Smoothlake.FlexLib;
using JJPortaudio;
using Radios;
using Xunit;
using static Radios.Tests.TxJoinRepairThreeTests;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track H2, join repair 3: the schedule explorations the brief
    /// asked for. Three rounds each fixed their reproductions and left a
    /// neighbouring schedule open, so instead of single outcomes these drive the
    /// REAL types through every interleaving of a small set of events and assert
    /// the invariants of the over's state model (written in the H2 report under
    /// "Join repair 3"):
    /// <list type="bullet">
    /// <item>(a) no microphone sample captured after the release reaches the encoder while the over may still be on air;</item>
    /// <item>(b) every sample captured before the release that enters the pipeline, in a buffer no source touched, reaches the encoder before our unkey is queued, unless a bounded exception (the backstop, a teardown, a stage fault) ended the hold;</item>
    /// <item>(c) our own over always gets our unkey;</item>
    /// <item>(d) no worker operation outlives its run or its audio server;</item>
    /// <item>(e) completed work is published whatever producer the gate selects.</item>
    /// </list>
    /// Each schedule runs on a fresh rig (an unconnected FlexBase, an
    /// uninitialized vendor radio forwarding its changes as the app's handler
    /// does, fake streams). A failure names the schedule. Windowless; no device.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TxJoinScheduleExplorationTests
    {
        // ── Shared: every sequence of `fixedOrder` with `extras` interleaved ──

        /// <summary>
        /// Every interleaving of the events in <paramref name="ordered"/> (kept in
        /// order) with <paramref name="free"/> copies of <paramref name="freeEvent"/>.
        /// </summary>
        private static IEnumerable<List<string>> Interleavings(IReadOnlyList<string> ordered, int free, string freeEvent)
        {
            int n = ordered.Count + free;
            foreach (var mask in Combinations(n, free))
            {
                var seq = new List<string>(n); int o = 0;
                for (int i = 0; i < n; i++) seq.Add(mask.Contains(i) ? freeEvent : ordered[o++]);
                yield return seq;
            }
        }

        private static IEnumerable<HashSet<int>> Combinations(int n, int k)
        {
            var idx = new int[k];
            for (int i = 0; i < k; i++) idx[i] = i;
            if (k == 0) { yield return new HashSet<int>(); yield break; }
            while (true)
            {
                yield return new HashSet<int>(idx);
                int j = k - 1;
                while (j >= 0 && idx[j] == n - k + j) j--;
                if (j < 0) yield break;
                idx[j]++;
                for (int m = j + 1; m < k; m++) idx[m] = idx[m - 1] + 1;
            }
        }

        // ══ Family 1: what the radio reports, against our own requests: (c) ══

        /// <summary>
        /// A radio that processes our commands in order and reports interlock
        /// changes through a delayed, ordered status pipe, as the real one does.
        /// It cannot tell our xmit 0 from anyone's: an xmit 0 ends whatever is on
        /// the air.
        /// </summary>
        private sealed class SimRadio : IDisposable
        {
            public readonly Rig X = new();
            public readonly Radio R;
            public readonly Queue<(InterlockState State, string Cause)> Pipe = new();
            public string? KeyedBy;                    // null, "own" or "foreign": the truth
            public readonly List<string> Log = new();
            public int Step;
            public int UnkeyQueuedAt = -1, UnkeyRanAt = -1;
            public readonly List<(int Step, InterlockState State, string Cause)> Delivered = new();
            private readonly PropertyChangedEventHandler _h;

            public SimRadio(bool staleReceiveInFlight)
            {
                X.Delay(1440);
                R = UnconnectedRadio(false, InterlockState.Ready);
                Set(X.B, "theRadio", R); Set(X.B, "_Transmit", false);
                X.B.OwnUnkeyCommandForTest = _ => Command(false, "its own command");
                _h = Forward(X.B, R); R.PropertyChanged += _h;
                // Status describing the radio BEFORE our request reached it.
                if (staleReceiveInFlight) Pipe.Enqueue((InterlockState.Receive, "stale"));
            }

            private void Command(bool xmit, string how)
            {
                Log.Add("xmit " + (xmit ? 1 : 0) + " (" + how + ")");
                if (xmit)
                {
                    if (KeyedBy != null) return;
                    KeyedBy = "own";
                    Pipe.Enqueue((InterlockState.PTTRequested, "own")); Pipe.Enqueue((InterlockState.Transmitting, "own"));
                }
                else if (KeyedBy != null)
                {
                    KeyedBy = null;
                    Pipe.Enqueue((InterlockState.UnkeyRequested, "unkey")); Pipe.Enqueue((InterlockState.Receive, "unkey"));
                }
            }

            public void RunQueue()
            {
                while (X.B.q.Count > 0)
                {
                    var item = X.B.q.Dequeue();
                    bool before = R.Mox;
                    ((FlexBase.FunctionDel)item.Item)();
                    bool after = R.Mox;
                    if (before != after) Command(after, "the setter");
                    if (item.Name == "Mox" && UnkeyQueuedAt >= 0 && UnkeyRanAt < 0 && !after) UnkeyRanAt = Step;
                }
            }

            public bool Deliver()
            {
                if (Pipe.Count == 0) return false;
                var (state, cause) = Pipe.Dequeue();
                Log.Add("status " + state + " (" + cause + ")");
                Delivered.Add((Step, state, cause));
                int before = X.B.q.Count;
                InterlockProperty.SetValue(R, state);
                if (X.B.q.Count > before && UnkeyQueuedAt < 0) UnkeyQueuedAt = Step;
                return true;
            }

            public void Release()
            {
                Log.Add("release");
                int before = X.B.q.Count;
                X.B.UnkeyAfterNoiseDelay("explore");
                if (X.B.q.Count > before && UnkeyQueuedAt < 0) UnkeyQueuedAt = Step; // unkeyed at once
            }

            public void FireHoldEnds()
            {
                Log.Add("hold ends");
                int before = X.B.q.Count;
                foreach (var end in X.Ends.ToList()) end.End();
                if (X.B.q.Count > before && UnkeyQueuedAt < 0) UnkeyQueuedAt = Step;
            }

            public void RadioEndsOurs()
            {
                Log.Add("the radio ends our over");
                if (KeyedBy == "own") { KeyedBy = null; Pipe.Enqueue((InterlockState.Receive, "ended")); }
            }

            public void ForeignKeys()
            {
                Log.Add("another station keys");
                if (KeyedBy == null) { KeyedBy = "foreign"; Pipe.Enqueue((InterlockState.PTTRequested, "foreign")); Pipe.Enqueue((InterlockState.Transmitting, "foreign")); }
            }

            /// <summary>Run the queue and deliver every report until nothing moves.</summary>
            public void Settle()
            {
                for (int guard = 0; guard < 50; guard++)
                {
                    Step++;
                    RunQueue();
                    if (!Deliver() && X.B.q.Count == 0) return;
                }
            }

            public string Trace => string.Join("; ", Log);

            public void Dispose() { R.PropertyChanged -= _h; X.Dispose(); }
        }

        /// <summary>Run one schedule. False when a "D" found nothing to deliver: that schedule duplicates a shorter one.</summary>
        private static bool Run(SimRadio s, IEnumerable<string> schedule)
        {
            foreach (var e in schedule)
            {
                s.Step++;
                switch (e)
                {
                    case "K": s.Log.Add("key"); s.X.B.Transmit = true; break;
                    case "Q": s.RunQueue(); break;
                    case "R": s.Release(); break;
                    case "E": s.FireHoldEnds(); break;
                    case "D": if (!s.Deliver()) return false; break;
                    case "Y": s.RadioEndsOurs(); break;
                    case "F": s.ForeignKeys(); break;
                    default: throw new ArgumentException(e);
                }
            }
            s.Settle();
            return true;
        }

        [Theory]
        [InlineData(false, "Q,R,E,Q")]
        [InlineData(true, "Q,R,E,Q")]
        [InlineData(false, "R,Q,E,Q")]
        [InlineData(true, "R,Q,E,Q")]
        public void Our_own_over_is_never_left_keyed_whatever_order_the_radio_reports_arrive_in(bool staleReceive, string own)
        {
            var ordered = new[] { "K" }.Concat(own.Split(',')).ToList();
            int runs = 0;
            for (int free = 0; free <= 5; free++)
                foreach (var schedule in Interleavings(ordered.Skip(1).ToList(), free, "D"))
                {
                    using var s = new SimRadio(staleReceive);
                    if (!Run(s, new[] { "K" }.Concat(schedule))) continue;
                    runs++;
                    Assert.True(s.KeyedBy != "own", "(c) our own over was left keyed: " + s.Trace);
                }
            Assert.True(runs >= (staleReceive ? 100 : 30), "the exploration ran " + runs + " schedules");
        }

        [Fact]
        public void Another_station_s_over_is_left_alone_only_on_positive_evidence_and_ours_is_never_left_keyed()
        {
            // Our over is keyed and confirmed; then the release, its end and the
            // queue, the radio ending our over, another station keying, and every
            // report, in every order.
            int runs = 0, withheld = 0;
            var ordered = new List<string> { "R", "E", "Q" };
            for (int free = 0; free <= 5; free++)
                foreach (var withDeliveries in Interleavings(ordered, free, "D"))
                    for (int y = 0; y <= withDeliveries.Count; y++)
                        for (int f = y + 1; f <= withDeliveries.Count + 1; f++)
                        {
                            var schedule = new List<string>(withDeliveries);
                            schedule.Insert(y, "Y"); schedule.Insert(f, "F");
                            using var s = new SimRadio(false);
                            // The prefix: our key, written and confirmed.
                            if (!Run(s, new[] { "K", "Q", "D", "D" }.Concat(schedule))) continue;
                            runs++;
                            Assert.True(s.KeyedBy != "own", "(c) our own over was left keyed: " + s.Trace);
                            // Positive evidence before our unkey ran: after it was queued, the
                            // radio's end of our over, then the other station's key-down.
                            if (s.UnkeyQueuedAt < 0) continue;
                            int ranAt = s.UnkeyRanAt < 0 ? int.MaxValue : s.UnkeyRanAt;
                            var window = s.Delivered.Where(d => d.Step >= s.UnkeyQueuedAt && d.Step < ranAt).ToList();
                            int end = window.FindIndex(d => d.Cause == "ended");
                            bool evidence = end >= 0 && window.Skip(end + 1).Any(d => d.Cause == "foreign");
                            if (!evidence) continue;
                            withheld++;
                            Assert.True(s.KeyedBy == "foreign", "our old unkey ended an over JJ Flexible did not key, on positive evidence: " + s.Trace);
                        }
            Assert.True(runs >= 300, "the exploration ran " + runs + " schedules");
            Assert.True(withheld > 0, "positive control: some schedules carried the evidence");
        }

        // ══ Family 2: the release boundary in the audio: (a) and (b) ══

        private enum Cls { Pre, Straddle, Post, Unknown }

        private sealed class AudioRig : IDisposable
        {
            public readonly Rig X = new();
            public readonly TxFramePipeline P;
            public readonly List<float> Sent = new();
            public readonly TxFilePlayer File = new();
            public long Release;          // 0 until released
            public bool Released, KeyedAgain, Faulted, SourceOn, TornDown, BackstopFired, TailFired;
            public int PreEntered;        // pre-release microphone samples (floats) that entered
            public bool BViolated; public string BWhy = "";
            public readonly List<string> Log = new();
            private bool _fail;
            private readonly Action<float[], int, uint> _line = DelayLine(1440);

            public AudioRig()
            {
                X.Delay(1440);
                X.B.TxConditioner.NoiseReducer = (b, n, r) => { if (_fail) throw new InvalidOperationException("explored fault"); _line(b, n, r); };
                // A connected radio, keyed by us, so the hold's unkey is queued to it.
                var radio = UnconnectedRadio(true, InterlockState.Transmitting);
                Set(X.B, "theRadio", radio);
                File.Load(Enumerable.Repeat(5f, 48000).ToArray(), 48000, "explore");
                P = new TxFramePipeline
                {
                    Source = File, Conditioner = X.B.TxConditioner.Process, KeySource = X.B.TransmitKeySource,
                    Encode = b => { Sent.AddRange(b); return new byte[4]; }, Handler = _ => X.B.NoteTxFrameHandedOn(),
                };
            }

            public void Callback(Cls cls)
            {
                long f = Stopwatch.Frequency;
                long now = Stopwatch.GetTimestamp();
                long captured;
                if (!Released) captured = cls == Cls.Unknown ? 0 : now - 20 * f / 1000;
                else captured = cls switch
                {
                    Cls.Pre => Release - 15 * f / 1000,
                    Cls.Straddle => Release - 5 * f / 1000,
                    Cls.Post => Math.Max(now, Release + 1),
                    _ => 0,
                };
                var buf = new float[960];
                for (int i = 0; i < 480; i++)
                {
                    float v;
                    if (KeyedAgain) v = 4f;
                    else if (!Released) v = 3f;
                    else if (captured == 0) v = 2f;                           // captured now: after the release
                    else v = captured + (long)i * f / 48000 < Release ? 3f : 2f;
                    buf[2 * i] = buf[2 * i + 1] = v;
                }
                // What enters as pre-release microphone audio, in a buffer no source touches.
                if (!SourceOn) PreEntered += buf.Count(v => v == 3f);
                Log.Add("callback " + cls);
                P.Emit(buf, 960, 48000, null, captured);
            }

            public void Do(string e)
            {
                switch (e)
                {
                    case "R":
                        Log.Add("release");
                        X.B.UnkeyAfterNoiseDelay("explore");
                        Release = X.B.TxConditioner.Drain?.ReleaseTimestamp ?? Stopwatch.GetTimestamp();
                        Released = true;
                        break;
                    case "K": Log.Add("key-down"); X.B.Transmit = true; KeyedAgain = true; break;
                    case "F": Log.Add("reducer fault"); _fail = true; Faulted = true; break;
                    case "S":
                        Log.Add("a file is armed");
                        File.Start(); SourceOn = true;
                        break;
                    case "T": Log.Add("teardown"); X.B.CancelHeldUnkey("explore teardown", unkeyNow: true); TornDown = true; break;
                    case "B":
                        Log.Add("backstop");
                        if (X.Ends.Count > 0) { X.Ends[0].End(); BackstopFired = true; }
                        break;
                    default: Callback(Enum.Parse<Cls>(e)); break;
                }
            }

            /// <summary>Feed post-release capture until the tail's end is scheduled, then run it, checking (b) as it queues our unkey.</summary>
            public void Finish()
            {
                for (int i = 0; i < 20 && X.B.HeldUnkeyPending && X.Ends.Count < 2; i++) Callback(Cls.Post);
                if (!X.B.HeldUnkeyPending || X.Ends.Count < 2) return;
                int sentPre = Sent.Count(v => v == 3f);
                bool exempt = Faulted || TornDown || BackstopFired || KeyedAgain;
                X.Ends[1].End();
                TailFired = true;
                if (!exempt && !X.B.HeldUnkeyPending && sentPre != PreEntered)
                {
                    BViolated = true;
                    BWhy = sentPre + " of " + PreEntered + " pre-release samples went out before the unkey";
                }
            }

            public string Trace => string.Join("; ", Log);
            public void Dispose() => X.Dispose();
        }

        private static IEnumerable<Cls[]> CallbackClasses(int n)
        {
            var all = (Cls[])Enum.GetValues(typeof(Cls));
            foreach (var a in all) foreach (var b in all) foreach (var c in all)
            {
                var cls = new[] { a, b, c }.Take(n).ToArray();
                // Capture order: a later callback carries later audio. Only "captured
                // now" repeats (Post, and Unknown, which counts as now); a pre-release
                // or straddling block cannot be captured twice, or after one that was
                // captured later.
                Cls Order(Cls k) => k == Cls.Unknown ? Cls.Post : k;
                bool monotonic = true;
                for (int i = 1; i < cls.Length; i++)
                {
                    Cls was = Order(cls[i - 1]), now = Order(cls[i]);
                    if (now < was || now == was && now != Cls.Post) monotonic = false;
                }
                if (monotonic) yield return cls;
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("F")]
        [InlineData("K")]
        [InlineData("T")]
        [InlineData("B")]
        [InlineData("S")]
        public void After_the_release_no_new_audio_goes_out_and_the_speech_before_it_does(string extra)
        {
            int runs = 0;
            foreach (var cls in CallbackClasses(3).Distinct(new SeqEq()))
            {
                // R and the extra anywhere among the three callbacks; the extra after R.
                for (int r = 0; r <= 3; r++)
                    for (int x = r + 1; x <= (extra == "" ? r + 1 : 4); x++)
                    {
                        var schedule = cls.Select(c => c.ToString()).ToList();
                        schedule.Insert(r, "R");
                        if (extra != "") schedule.Insert(x, extra);
                        using var a = new AudioRig();
                        foreach (var e in schedule) a.Do(e);
                        a.Finish();
                        // Flush the reducer's delay line: anything that went IN before now
                        // comes out within three frames, so a leak cannot hide in it.
                        for (int i = 0; i < 4; i++) a.Callback(Cls.Post);
                        runs++;
                        string at = " [" + string.Join(",", schedule) + "] " + a.Trace;
                        // (a) no microphone audio captured after the release, and no file audio made after it.
                        // (After a key-down the new over's audio is 4, so any 2 or 5 is a leak from before it.)
                        Assert.False(a.Sent.Contains(2f), "(a) post-release microphone audio reached the encoder:" + at);
                        Assert.False(a.Sent.Contains(5f), "(a) generated audio made after the release reached the encoder:" + at);
                        // (b) every pre-release sample that entered went out before the tail's unkey.
                        Assert.False(a.BViolated, "(b) " + a.BWhy + ":" + at);
                        // (c) without a key-down, the over always gets its unkey: by the tail, the backstop or the teardown.
                        if (!a.KeyedAgain)
                        {
                            if (a.X.B.HeldUnkeyPending) { a.X.Ends[0].End(); }
                            Assert.False(a.X.B.HeldUnkeyPending, "(c) the hold never ended:" + at);
                            Assert.True(a.X.B.q.Count >= 1, "(c) no unkey was queued:" + at);
                        }
                    }
            }
            Assert.True(runs > 50, "the exploration ran " + runs + " schedules");
        }

        private sealed class SeqEq : IEqualityComparer<Cls[]>
        {
            public bool Equals(Cls[]? x, Cls[]? y) => x!.SequenceEqual(y!);
            public int GetHashCode(Cls[] obj) => obj.Aggregate(17, (h, c) => h * 31 + (int)c);
        }

        // ══ Family 3: the microphone worker against its run and its server: (d) and (e) ══

        private sealed class WorkerRig : IDisposable
        {
            public readonly Rig X = new();
            public readonly FlexBase.audioChannelData Channel;
            public readonly RemoteAudioWake OldWake = new(), NewWake = new();
            public readonly List<Action> Jobs = new();
            public readonly List<string> Log = new();
            public readonly List<Fake> Opened = new();
            public readonly List<string> Violations = new();
            public long Server;
            public Action? BetweenOps, DuringSecond;
            public int Opens;

            public WorkerRig()
            {
                Channel = RepairTwoChannel(X);
                Channel.PortAudioStream = null; Channel.Started = false; // tier 2: the dead stream is out
                Set(X.B, "_remoteAudioWake", OldWake);
                Set(X.B, "_micReopenPending", true);
                X.B.TxCaptureWorkerForTest = a => Jobs.Add(a);
                X.B.TxCaptureOpenForTest = rate =>
                {
                    Opens++;
                    // (d) every open begins only on its own live server, for a wanted job.
                    var job = Get(X.B, "_txOpenJob");
                    bool wanted = job != null && (int)job.GetType().GetField("State")!.GetValue(job)! == 0
                        && ReferenceEquals(job.GetType().GetField("Wake")!.GetValue(job), Get(X.B, "_remoteAudioWake"))
                        && !(bool)Get(X.B, "stopRemoteAudio")! && !(bool)Get(X.B, "Disconnecting")!;
                    if (Audio.ServerGeneration != Server) Violations.Add("an open began on a server that had ended");
                    if (!wanted) Violations.Add("an open began for a job no longer wanted");
                    Log.Add("open at " + rate);
                    if (Opens == 1) { BetweenOps?.Invoke(); return null!; }   // the first rate will not open; then the event, after its operation
                    DuringSecond?.Invoke();                                   // the event while the second opens
                    var f = new Fake(); Opened.Add(f); return f;
                };
            }

            public void Event(string e)
            {
                Log.Add(e);
                switch (e)
                {
                    case "abandon": Call(X.B, "AbandonTxOpenJob", "explore"); break;
                    case "server ends": Audio.MoveServerGenerationForTest(); break;
                    case "new run": Set(X.B, "_remoteAudioWake", NewWake); break;
                    case "stop": Set(X.B, "stopRemoteAudio", true); break;
                    case "disconnecting": Set(X.B, "Disconnecting", true); break;
                    default: throw new ArgumentException(e);
                }
            }

            public void Dispose()
            {
                Set(X.B, "stopRemoteAudio", false); Set(X.B, "Disconnecting", false);
                OldWake.Dispose(); NewWake.Dispose(); X.Dispose();
            }
        }

        [Fact]
        public void No_worker_operation_outlives_its_run_and_returned_work_is_always_published()
        {
            var events = new[] { "", "abandon", "server ends", "new run", "stop", "disconnecting" };
            var places = new[] { "before", "between", "during", "after" };
            var passes = new[] { "microphone", "self-clock", "loop" };
            int runs = 0, published = 0;
            foreach (var e in events)
                foreach (var place in e == "" ? new[] { "before" } : places)
                    foreach (var pass in passes)
                    {
                        // The server cannot end DURING an operation: the gate excludes it.
                        if (e == "server ends" && place == "during") continue;
                        using var w = new WorkerRig();
                        Call(w.X.B, "StartTxOpenJob", 50, Enum.Parse(typeof(FlexBase).GetNestedType("TxOpenKind", System.Reflection.BindingFlags.NonPublic)!, "Replace"), w.Channel, 10, "explore");
                        w.Server = (long)Get(Get(w.X.B, "_txOpenJob")!, "Server")!;
                        if (e != "" && place == "before") w.Event(e);
                        if (e != "" && place == "between") w.BetweenOps = () => w.Event(e);
                        if (e != "" && place == "during") w.DuringSecond = () => w.Event(e);
                        w.Jobs[0]();
                        if (e != "" && place == "after") w.Event(e);
                        w.Log.Add(pass + " pass");
                        switch (pass)
                        {
                            case "microphone": Call(w.X.B, "startOpusInputChannel"); break;
                            case "self-clock": Call(w.X.B, "startSelfClockedTxInput"); break;
                            default: Call(w.X.B, "TakeReturnedTxReplacement"); break;
                        }
                        foreach (var close in w.Jobs.Skip(1).ToList()) close();
                        runs++;
                        string at = " [" + e + " " + place + ", " + pass + "] " + string.Join("; ", w.Log);
                        Assert.True(w.Violations.Count == 0, "(d) " + string.Join(", ", w.Violations) + ":" + at);
                        // (d) every stream opened is published into its own run, or closed: never left, never leaked into another run.
                        foreach (var f in w.Opened)
                        {
                            bool inChannel = ReferenceEquals(w.Channel.PortAudioStream, f);
                            bool currentRun = ReferenceEquals(Get(w.X.B, "_remoteAudioWake"), w.OldWake);
                            Assert.True(inChannel ^ (f.Closes == 1), "(d) a stream opened was neither published nor closed exactly once:" + at);
                            if (inChannel) Assert.True(currentRun, "(d) a stream was published into a later run:" + at);
                        }
                        // (e) work that came back to a run still current, not abandoned and not closing, is published at the next pass, whatever producer.
                        bool stillWanted = e == "" || place == "after" && e != "abandon" && e != "new run" && e != "server ends";
                        if (stillWanted && w.Opened.Count == 1 && e != "stop" && e != "disconnecting")
                        {
                            Assert.Same(w.Opened[0], w.Channel.PortAudioStream);
                            published++;
                        }
                    }
            Assert.True(runs > 50, "the exploration ran " + runs + " schedules");
            Assert.True(published >= 3, "positive control: the ordinary return was published by every producer (" + published + ")");
        }
    }
}

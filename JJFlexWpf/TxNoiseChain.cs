using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radios;

namespace JJFlexWpf
{
    /// <summary>When a transmit noise change takes effect (Sprint 49 Track H, item B).</summary>
    public enum TxChangeTiming
    {
        /// <summary>Unkeyed: it takes effect on the next buffer.</summary>
        Now,
        /// <summary>Made while keyed: it takes effect at the unkey. The caller says so (placeholder <c>audio.denoise.tx.at_unkey</c>).</summary>
        AtUnkey,
        /// <summary>Not accepted; <see cref="TxChangeResult.Refusal"/> says why, in the slot's words.</summary>
        Refused,
    }

    /// <summary>The answer to a transmit noise change.</summary>
    public sealed record TxChangeResult(TxChangeTiming Timing, string? Refusal = null)
    {
        public static readonly TxChangeResult Now = new(TxChangeTiming.Now);
        public static readonly TxChangeResult AtUnkey = new(TxChangeTiming.AtUnkey);
    }

    /// <summary>
    /// The microphone's noise reduction (Sprint 49 Track H): two transmit
    /// <see cref="PcDenoiseSlot"/>s, spectral then neural, the same class and
    /// the same catalog receive uses (item A; ruled 2026-11-07, "two slots is
    /// fine"). Plugged into the transmit conditioner's noise-reducer seam by
    /// <see cref="TxAudioConditioning"/>, ahead of the gate.
    /// </summary>
    /// <remarks>
    /// <para><b>One mono engine per slot</b> (item A). The transmit stream is
    /// the microphone, mono, duplicated on both channels (a mono device is
    /// duplicated by the capture callback), so the chain folds the pair to one
    /// channel, cleans it once and writes the result to both. Two engines on
    /// identical audio, which is what ran before, cost twice and changed
    /// nothing anyone hears. A stereo device is averaged. When neither slot
    /// processes a buffer, the buffer is left exactly as it came.</para>
    /// <para><b>Changes while keyed wait for the unkey</b> (item B, ruled S2,
    /// 2026-11-18 08:17). "Keyed" is our Mox OR the radio's interlock saying
    /// it is transmitting, the same pair <c>FlexBase.CheckRestoreReminder.cs</c>
    /// distinguishes, because VOX keys only the interlock. Every engine
    /// change, model (rebuild) change and on or off is accepted at once as the
    /// operator's intent, but only the audio thread makes it audible, and only
    /// on a buffer that arrived unkeyed: on and off are adopted there, and a
    /// transmit slot installs a new engine only when <see cref="PcDenoiseSlot.SwapPermitted"/>
    /// says so. So a change cannot land mid-over even when the key goes down
    /// between the request and the next buffer. Strength, and every setting
    /// that does not need a new instance, stays live: they move blend weights
    /// or parameters of aligned audio and do not move the timeline.</para>
    /// <para>Persistent: <see cref="TxAudioConditioning"/> keeps one for the
    /// session, radio or no radio, so a choice made before connecting is held
    /// and applied, not refused (#1077).</para>
    /// </remarks>
    public sealed class TxNoiseChain
    {
        /// <summary>The rate every engine runs at; the chain passes any other rate through untouched.</summary>
        public const int SampleRate = 48000;

        /// <summary>The neural slot's default engine on transmit (S8, 2026-11-18 08:28: RNNoise until Noel has listened).</summary>
        public const string DefaultNeuralEngine = "rnnoise";

        /// <summary>
        /// The spectral slot's starting engine on transmit. Not ruled: the
        /// spectral slot starts off, and NR4 is the in-process spectral engine,
        /// so choosing it costs no helper channel while it is off. Chosen by the
        /// Track H agent; a later ruling changes this line.
        /// </summary>
        public const string DefaultSpectralEngine = "nr4";

        private readonly DenoiseEngineCatalog _catalog;
        private readonly int[] _wantEnabled = new int[2];
        private float[] _mono = Array.Empty<float>();
        private volatile bool _bufferKeyed;

        // Item C, per slot (index = DenoiseFamily): the latch was seen this
        // over; the operator has been told and the slot has not yet recovered.
        private readonly bool[] _latchSeen = new bool[2];
        private readonly int[] _noticeGiven = new int[2];

        // Item D: this over's counts, per slot, and the buffer timing.
        private bool _wasKeyed;
        private readonly OverCounts[] _over = { new(), new() };
        private long _overBuffers, _overStartTicks, _lastBufferTicks, _burst, _maxBurst, _bursts;
        private sealed class OverCounts
        {
            public PerChannelDenoiserBank? Bank;
            public string EngineId = "";
            public long Late, Fallback, Latched, Engine, Latches;
        }

        public PcDenoiseSlot SpectralSlot { get; }
        public PcDenoiseSlot NeuralSlot { get; }

        /// <param name="catalog">The engine catalog; the app's own when null.</param>
        public TxNoiseChain(DenoiseEngineCatalog? catalog = null)
        {
            _catalog = catalog ?? new DenoiseEngineCatalog();
            SpectralSlot = new PcDenoiseSlot(DenoiseFamily.Spectral, DefaultSpectralEngine, SampleRate, 1, _catalog, DenoiseDirection.Transmit);
            NeuralSlot = new PcDenoiseSlot(DenoiseFamily.Neural, DefaultNeuralEngine, SampleRate, 1, _catalog, DenoiseDirection.Transmit);
            // One delegate each, made here: the audio thread asks it every buffer.
            Func<bool> permitted = () => !_bufferKeyed;
            SpectralSlot.SwapPermitted = permitted;
            NeuralSlot.SwapPermitted = permitted;
        }

        public DenoiseEngineCatalog Catalog => _catalog;

        public PcDenoiseSlot SlotOf(DenoiseFamily family) => family == DenoiseFamily.Spectral ? SpectralSlot : NeuralSlot;

        /// <summary>
        /// Asked on the control side to say whether a change made now waits:
        /// our Mox or the radio's interlock. Set by <see cref="TxAudioConditioning.Attach"/>;
        /// null (no radio) reads as unkeyed. A source that throws reads as
        /// keyed, so a fault defers a change rather than landing it mid-over.
        /// </summary>
        public Func<bool>? KeySource { get; set; }

        /// <summary>Whether a change made now would wait for the unkey.</summary>
        public bool IsKeyed
        {
            get
            {
                var source = KeySource;
                if (source == null) return false;
                try { return source(); } catch { return true; }
            }
        }

        /// <summary>The operator's on or off for a slot, which the audio thread adopts at the next unkeyed buffer.</summary>
        public bool WantsEnabled(DenoiseFamily family) => Volatile.Read(ref _wantEnabled[(int)family]) != 0;

        /// <summary>The engine the operator chose for a slot (it runs from the next unkeyed buffer once built).</summary>
        public string ChosenEngineId(DenoiseFamily family) => SlotOf(family).ChosenEngineId;

        /// <summary>True while something the operator asked for is not yet what goes out: an on or off not adopted, or a chosen engine not yet running.</summary>
        public bool ChangesWaiting =>
            WantsEnabled(DenoiseFamily.Spectral) != SpectralSlot.Enabled || WantsEnabled(DenoiseFamily.Neural) != NeuralSlot.Enabled
            || (WantsEnabled(DenoiseFamily.Spectral) && !SpectralSlot.RunningIsCurrent(SpectralSlot.ChosenEngineId))
            || (WantsEnabled(DenoiseFamily.Neural) && !NeuralSlot.RunningIsCurrent(NeuralSlot.ChosenEngineId));

        private TxChangeResult Timing() => IsKeyed ? TxChangeResult.AtUnkey : TxChangeResult.Now;

        // ── What waits for the unkey (item B; repair round 1, R3) ──
        //
        // Two mechanisms, deliberately. On, off, an engine and a rebuild setting
        // are accepted at once and made audible only by the audio thread on an
        // unkeyed buffer (the slot's SwapPermitted, Process below), so they
        // cannot land mid-over even if the key goes down between the request
        // and the next buffer. A WHOLE SETUP, and anything asked for after a
        // setup that is still waiting, is held here as an ordered list of
        // changes and run on the control side at the unkey, so the later
        // request still wins, as it would have unkeyed.

        /// <summary>
        /// THE ONE PLACE that decides whether the continuous levers stay live
        /// while keyed: an engine's settings that need no new instance (NNR's
        /// depth, DeepFilterNet3's limit, NR4's reduction) and the gate's
        /// threshold, margin, attack, hold, release and depth. True today:
        /// Noel ruled strength live and engine changes at unkey, and the others
        /// were left live for now while S15 asks whether they are like strength
        /// (repair round 1, R3). Setting this false sends every one of them
        /// through <see cref="RunOrDefer"/>, so they wait for the unkey; nothing
        /// else changes. Strength stays live either way (the ruling).
        /// </summary>
        public const bool ContinuousLeversLiveWhileKeyed = true;

        private readonly object _deferLock = new();
        private readonly List<Action> _deferred = new();

        /// <summary>True while changes are being held for the unkey (a whole setup, and whatever was asked for after it).</summary>
        public bool HasDeferredChanges => Volatile.Read(ref _deferredCount) > 0;
        private int _deferredCount; // _deferred.Count, readable without the lock (the audio thread asks at the unkey)

        /// <summary>
        /// Run <paramref name="change"/> now when unkeyed and nothing is held, or
        /// hold it, in order, for the unkey. Changes run on the control side; the
        /// chain runs the held ones at the unkey (and the app's policy tick does
        /// too, as a backstop for an over in CW, where the chain is stood aside).
        /// </summary>
        public TxChangeResult RunOrDefer(Action change) => Order(change, holdWhenKeyed: true, () => TxChangeResult.Now);

        /// <summary>
        /// Run the held changes, in order, while the radio stays unkeyed. The key
        /// is asked again before EACH one (repair round 2, R3): an over that
        /// starts part way through leaves the rest held, in order, for its unkey.
        /// Safe to call any time, from any thread but the audio one.
        /// </summary>
        public void ApplyDeferred()
        {
            lock (_deferLock) DrainLocked();
        }

        /// <summary>Run held changes until none remain or the radio is keyed. Under the lock.</summary>
        private void DrainLocked()
        {
            while (_deferred.Count > 0)
            {
                if (IsKeyed) return;
                var next = _deferred[0];
                _deferred.RemoveAt(0);
                _deferredCount = _deferred.Count;
                try { next(); }
                catch (Exception ex) { Trace.WriteLine("TxNoiseChain: a change held for the unkey failed: " + ex.Message); }
            }
        }

        /// <summary>
        /// The one ordering rule every request goes through (repair round 2, R3).
        /// Whatever is already held runs first, while unkeyed; if anything is
        /// still held after that (the radio is keyed, or keyed again part way),
        /// this request is held behind it, keyed or not, so a request made in the
        /// gap between the unkey and the drain is never overtaken by an older
        /// setup. Otherwise it runs now, unless <paramref name="holdWhenKeyed"/>
        /// and the radio is keyed. Runs under the lock, so two callers cannot
        /// interleave their changes.
        /// </summary>
        private TxChangeResult Order(Action change, bool holdWhenKeyed, Func<TxChangeResult> nowResult)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            lock (_deferLock)
            {
                DrainLocked();
                if (_deferred.Count > 0 || (holdWhenKeyed && IsKeyed))
                {
                    _deferred.Add(change);
                    _deferredCount = _deferred.Count;
                    return TxChangeResult.AtUnkey;
                }
                change();
                return nowResult();
            }
        }

        /// <summary>
        /// An audio-gated change: made now (the audio thread makes it audible
        /// only unkeyed), unless something is held, in which case it waits
        /// behind it so it still wins.
        /// </summary>
        private TxChangeResult Gated(Action change) => Order(change, holdWhenKeyed: false, Timing);

        /// <summary>Turn a slot on or off (item B: at the unkey when keyed).</summary>
        public TxChangeResult SetEnabled(DenoiseFamily family, bool on) => Gated(() => SetEnabledNow(family, on));

        private void SetEnabledNow(DenoiseFamily family, bool on)
        {
            Volatile.Write(ref _wantEnabled[(int)family], on ? 1 : 0);
            if (on) SlotOf(family).RememberUse();
        }

        /// <summary>Choose a slot's engine (item B: it starts at the unkey when keyed). Refused for an engine that is not this slot's family, is receive only, or is not in this build.</summary>
        public TxChangeResult ChooseEngine(DenoiseFamily family, string engineId)
        {
            string? refusal = Refusal(family, engineId);
            if (refusal != null) return new(TxChangeTiming.Refused, refusal);
            return Gated(() => SlotOf(family).Select(engineId, out _));
        }

        /// <summary>The slot's own refusal, worked out before anything is held, in the slot's words.</summary>
        private string? Refusal(DenoiseFamily family, string engineId)
        {
            var entry = _catalog.Find(engineId);
            return entry == null ? "unknown-engine" : entry.Side != DenoiseSide.Pc ? "radio-engine"
                : !entry.Serves(DenoiseDirection.Transmit) ? "wrong-direction" : entry.Family != family ? "wrong-family"
                : entry.Factory == null ? "not-in-build" : null;
        }

        /// <summary>
        /// Change one engine setting. A setting that needs a new instance (a
        /// model, a delay) waits for the unkey like an engine change. Any other
        /// setting follows <see cref="ContinuousLeversLiveWhileKeyed"/>: live
        /// today, like strength.
        /// </summary>
        public TxChangeResult SetSetting(string engineId, string name, EngineSettingValue value)
        {
            var slot = SlotFor(engineId);
            if (slot == null) return new(TxChangeTiming.Refused, "wrong-direction");
            var descriptor = slot.SettingsFor(engineId).FirstOrDefault(s => s.Name == name);
            if (descriptor == null) return new(TxChangeTiming.Refused, "unknown-setting");
            if (!descriptor.Accepts(value)) return new(TxChangeTiming.Refused, "refused-value");
            if (descriptor.NeedsRebuild)
            {
                var timing = Gated(() => slot.TrySetSetting(engineId, name, value));
                // Held behind earlier work: it waits. Otherwise a rebuild setting
                // of an engine the slot is not running changes nothing anyone hears.
                return HasDeferredChanges || slot.ChosenEngineId == engineId ? timing : TxChangeResult.Now;
            }
            return Continuous(() => slot.TrySetSetting(engineId, name, value));
        }

        /// <summary>A continuous lever (see <see cref="ContinuousLeversLiveWhileKeyed"/>): live, unless a whole setup is waiting or the policy says to wait.</summary>
        internal TxChangeResult Continuous(Action change)
        {
            if (!ContinuousLeversLiveWhileKeyed) return RunOrDefer(change);
            return Order(change, holdWhenKeyed: false, () => TxChangeResult.Now);
        }

        /// <summary>A transmit engine's strength (stored wet fraction; surfaces show it as a floor through <see cref="NoiseFloor"/>). Live while keyed (the ruling), unless something is held, when it waits behind it.</summary>
        /// <returns>
        /// When it takes effect: Now, or AtUnkey while something is held ahead of
        /// it (Sprint 49 Track H2 repair round 1, part 2: a void until then, which
        /// left the controller to work the timing out from this class's rule).
        /// Refused for an engine transmit does not offer.
        /// </returns>
        public TxChangeResult SetStrength(string engineId, float strength)
        {
            var slot = SlotFor(engineId);
            if (slot == null) return new(TxChangeTiming.Refused, "wrong-direction");
            return Order(() => slot.SetStrength(engineId, strength), holdWhenKeyed: false, () => TxChangeResult.Now);
        }

        public float GetStrength(string engineId) =>
            SlotFor(engineId)?.GetStrength(engineId) ?? NoiseFloor.DefaultStrength(engineId, DenoiseDirection.Transmit);

        /// <summary>The slot an engine belongs to on transmit, or null for an engine transmit does not offer.</summary>
        public PcDenoiseSlot? SlotFor(string engineId)
        {
            var row = _catalog.Find(engineId);
            if (row == null || row.Side != DenoiseSide.Pc || row.Family == null || !row.Serves(DenoiseDirection.Transmit)) return null;
            return SlotOf(row.Family.Value);
        }

        /// <summary>Both slots as a setup carries them: the operator's intent (on, chosen engine, every engine's stored values).</summary>
        public (TxNoiseSlotSetup Spectral, TxNoiseSlotSetup Neural) CaptureSlots() => (Capture(DenoiseFamily.Spectral), Capture(DenoiseFamily.Neural));

        private TxNoiseSlotSetup Capture(DenoiseFamily family)
        {
            var slot = SlotOf(family);
            return new TxNoiseSlotSetup
            {
                Enabled = WantsEnabled(family),
                EngineId = slot.HasBeenEnabled ? slot.ChosenEngineId : "",
                Engines = slot.ExportSettings().ToList(),
            };
        }

        /// <summary>
        /// Put a setup's two slots in place, REPLACING what was there (repair
        /// round 1, R2): every engine's stored values become exactly the setup's,
        /// anything it does not name goes back to its default, an empty engine
        /// choice means the slot's default engine, and on and off are the
        /// setup's. A whole setup waits for the unkey when keyed (R3), as one
        /// change. An engine this build cannot run is refused and the slot takes
        /// its default instead (traced), so no earlier engine survives.
        /// </summary>
        public TxChangeResult ApplySlots(TxNoiseSlotSetup? spectral, TxNoiseSlotSetup? neural) =>
            RunOrDefer(() => ApplySlotsNow(spectral, neural));

        /// <summary>The slot half of a setup, now, on the control side. <see cref="TxAudioConditioning"/> runs it inside its own whole-setup change.</summary>
        internal void ApplySlotsNow(TxNoiseSlotSetup? spectral, TxNoiseSlotSetup? neural)
        {
            Apply(DenoiseFamily.Spectral, spectral);
            Apply(DenoiseFamily.Neural, neural);
        }

        /// <summary>The engine a slot starts on when a setup names none.</summary>
        public static string DefaultEngine(DenoiseFamily family) => family == DenoiseFamily.Spectral ? DefaultSpectralEngine : DefaultNeuralEngine;

        private void Apply(DenoiseFamily family, TxNoiseSlotSetup? want)
        {
            want ??= new TxNoiseSlotSetup();
            var slot = SlotOf(family);
            var records = (want.Engines ?? new()).Where(e => e != null)
                .Select(e => new DenoiseEngineSettings { EngineId = e.EngineId, Direction = DenoiseDirection.Transmit, Strength = e.Strength, Values = e.Values })
                .ToList();
            string id = string.IsNullOrWhiteSpace(want.EngineId) ? DefaultEngine(family) : want.EngineId;
            if (!slot.ReplaceSettingsAndSelect(records, id))
            {
                Trace.WriteLine($"TxNoiseChain: the setup's {family} engine '{id}' cannot run here; the slot takes {DefaultEngine(family)}.");
                slot.Select(DefaultEngine(family), out _);
            }
            if (!string.IsNullOrWhiteSpace(want.EngineId)) slot.RememberUse();
            Volatile.Write(ref _wantEnabled[(int)family], want.Enabled ? 1 : 0);
        }

        /// <summary>
        /// A slot's engine failed or fell behind mid-over and the slot latched
        /// dry for the rest of it (item C). Raised on a pool thread, once until
        /// the slot recovers (S3's refinement, 08:23: "the notice comes once,
        /// not every over, until it recovers"), with the slot and its engine
        /// id. <see cref="TxAudioConditioning"/> answers it with a short earcon
        /// on the alert channel and a few words, never anything on the air.
        /// </summary>
        public event Action<DenoiseFamily, string>? FellBack;

        /// <summary>
        /// One line per over (item D, measure first): for each slot that ran,
        /// its engine and how many frames went late, failed, or out latched
        /// dry; and how the buffers arrived (their count, how many came back
        /// to back as bursts, and the longest burst). Raised on a pool thread
        /// at the unkey; the app traces it. Null text is never sent.
        /// </summary>
        public event Action<string>? OverReport;

        /// <summary>Two buffers this close together (in stopwatch ticks) arrived in the same burst: one capture callback handing over several frames back to back.</summary>
        private static readonly long BurstGapTicks = Stopwatch.Frequency / 500; // 2 ms

        /// <summary>The delay the last buffer went out with, in samples per channel: the two slots' together, 0 when neither processed it.</summary>
        public int AppliedLatencySamples { get; private set; }

        /// <summary>
        /// Clean one buffer of interleaved stereo in place, on the audio thread.
        /// <paramref name="keyed"/> is this buffer's key state (the
        /// conditioner's), read once.
        /// </summary>
        internal void Process(float[] interleaved, int count, uint sampleRate, bool keyed)
        {
            AppliedLatencySamples = 0;
            _bufferKeyed = keyed;
            if (keyed && !_wasKeyed) StartOver();
            else if (!keyed && _wasKeyed)
            {
                EndOver();
                // R3: a whole setup, and what was asked for after it, runs now.
                if (HasDeferredChanges) ThreadPool.QueueUserWorkItem(_ => ApplyDeferred());
            }
            _wasKeyed = keyed;
            if (keyed) NoteBufferTiming();
            if (!keyed)
            {
                // The only place on and off become audible (item B).
                SpectralSlot.Enabled = WantsEnabled(DenoiseFamily.Spectral);
                NeuralSlot.Enabled = WantsEnabled(DenoiseFamily.Neural);
                // Item C: the retry starts at the unkey. A slot that latched
                // during the over is released and rebuilt now, while
                // receiving, so the next over starts processed or cleanly dry.
                Unlatch(DenoiseFamily.Spectral);
                Unlatch(DenoiseFamily.Neural);
            }
            if (sampleRate != SampleRate || count < 2) return;
            int frames = count / 2;
            if (_mono.Length != frames) _mono = new float[frames];
            var mono = _mono;
            for (int f = 0, i = 0; f < frames; f++, i += 2) mono[f] = (interleaved[i] + interleaved[i + 1]) * 0.5f;
            SpectralSlot.Process(mono, true, true, false, null);
            NeuralSlot.Process(mono, true, true, false, null);
            AfterSlot(DenoiseFamily.Spectral, keyed);
            AfterSlot(DenoiseFamily.Neural, keyed);
            bool processed = SpectralSlot.LastBufferProcessed || NeuralSlot.LastBufferProcessed;
            if (!processed) return;
            for (int f = 0, i = 0; f < frames; f++, i += 2) { interleaved[i] = mono[f]; interleaved[i + 1] = mono[f]; }
            AppliedLatencySamples = SpectralSlot.LastAppliedLatencySamples + NeuralSlot.LastAppliedLatencySamples;
        }

        /// <summary>Item C, on the audio thread: tell the operator once when a slot latches; forget the notice once it processes cleanly again.</summary>
        private void AfterSlot(DenoiseFamily family, bool keyed)
        {
            int i = (int)family;
            var slot = SlotOf(family);
            if (slot.LastBufferClean) Volatile.Write(ref _noticeGiven[i], 0);
            if (!keyed || _latchSeen[i] || slot.RunningBank is not { Latched: true }) return;
            _latchSeen[i] = true;
            string engine = slot.RunningEngineId;
            bool tell = Interlocked.Exchange(ref _noticeGiven[i], 1) == 0;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Trace.WriteLine($"TxNoiseChain: transmit {family} slot ({engine}) missed a frame mid-over; latched to the unprocessed microphone at the same delay until the unkey"
                    + (tell ? "; telling the operator." : "; already told, not again until it recovers."));
                if (tell) FellBack?.Invoke(family, engine);
            });
        }

        /// <summary>Item C, at an unkeyed buffer: release a latched bank and rebuild the slot's own transmit engine (never receive's) on a worker.</summary>
        private void Unlatch(DenoiseFamily family)
        {
            int i = (int)family;
            _latchSeen[i] = false;
            var slot = SlotOf(family);
            var bank = slot.RunningBank;
            if (bank == null || !bank.Latched) return;
            bank.ReleaseLatch();
            ThreadPool.QueueUserWorkItem(state =>
            {
                string id = slot.ChosenEngineId;
                Trace.WriteLine($"TxNoiseChain: unkeyed after a latched over; rebuilding the transmit {family} slot's {id} while receiving.");
                slot.Select(id, out _, force: true);
            });
        }

        private void StartOver()
        {
            _overBuffers = 0; _burst = 0; _maxBurst = 0; _bursts = 0;
            _overStartTicks = _lastBufferTicks = 0;
            Snapshot(DenoiseFamily.Spectral);
            Snapshot(DenoiseFamily.Neural);
        }

        private void Snapshot(DenoiseFamily family)
        {
            var o = _over[(int)family];
            var slot = SlotOf(family);
            o.Bank = slot.Enabled ? slot.RunningBank : null;
            o.EngineId = o.Bank != null ? slot.RunningEngineId : "";
            if (o.Bank == null) return;
            o.Late = o.Bank.LateFrames; o.Fallback = o.Bank.FallbackFrames; o.Latched = o.Bank.LatchedFrames;
            o.Engine = o.Bank.EngineFrames; o.Latches = o.Bank.Latches;
        }

        private void NoteBufferTiming()
        {
            long now = Stopwatch.GetTimestamp();
            if (_overBuffers == 0) _overStartTicks = now;
            else if (now - _lastBufferTicks < BurstGapTicks) { if (++_burst == 1) _bursts++; if (_burst + 1 > _maxBurst) _maxBurst = _burst + 1; }
            else _burst = 0;
            _lastBufferTicks = now;
            _overBuffers++;
        }

        /// <summary>At the unkey: the over's counts, read here and worded on a pool thread for <see cref="OverReport"/>.</summary>
        private void EndOver()
        {
            if (OverReport == null) return;
            var counts = new long[2][];
            var ids = new string?[2];
            for (int i = 0; i < 2; i++)
            {
                var o = _over[i];
                var b = o.Bank;
                if (b == null) continue;
                ids[i] = o.EngineId;
                counts[i] = new[] { b.EngineFrames - o.Engine, b.LateFrames - o.Late, b.FallbackFrames - o.Fallback,
                    b.LatchedFrames - o.Latched, b.Latches - o.Latches };
            }
            long buffers = _overBuffers, bursts = _bursts, longest = _maxBurst;
            double seconds = buffers < 2 ? 0 : (double)(_lastBufferTicks - _overStartTicks) / Stopwatch.Frequency;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string Part(int i) => counts[i] == null ? (DenoiseFamily)i + " off"
                    : $"{(DenoiseFamily)i} {ids[i]}: engine frames {counts[i][0]}, late {counts[i][1]}, failed {counts[i][2]}, latched dry {counts[i][3]}"
                      + (counts[i][4] > 0 ? " (latched this over)" : "");
                OverReport?.Invoke($"TxNoiseChain over: {Part(0)}; {Part(1)}; {buffers} buffers in {seconds:0.00} s, "
                    + $"{bursts} bursts, longest burst {longest} buffers back to back");
            });
        }
    }
}

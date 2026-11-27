using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using JJPortaudio;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// The audition's offline twin of the configured transmit chain (#262,
    /// Sprint 49 Track Q phase B, item 2): both noise slots and the gate, exactly
    /// as configured, in instances of its OWN that never touch the live chain.
    /// Check your audio hands it the one take; it renders what the transmit
    /// chain would send, and says what produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It reads the configuration when it runs, not when it is made.</b> The
    /// window builds its take once and the take keeps its processor, so every
    /// input comes through a function asked on each <see cref="Process"/>: the
    /// setup (the app's is <see cref="TxAudioConditioning.CaptureSetup"/>, the
    /// operator's INTENT), the live state the window read from its own radio
    /// (for the verdict only), and the catalog to build engines from.
    /// </para>
    /// <para>
    /// <b>Never auto-apply.</b> It writes nothing to the live chain, the live
    /// conditioner, the saved settings or the radio. It builds a new
    /// <see cref="TxNoiseChain"/> and a new <see cref="TxAudioConditioner"/>
    /// for each render and tears both down before it returns.
    /// </para>
    /// <para>
    /// <b>Two passes, the order the live path runs them.</b> The noise pass runs
    /// the chain over the take, unkeyed, in 10 ms buffers of the microphone
    /// duplicated to stereo, the way the capture delivers it. Unkeyed on purpose:
    /// on the air the engines are built and their pipelines filled while
    /// receiving, and a keyed buffer would latch a helper's bank dry on its
    /// normal first late frames. The gate pass then runs a conditioner keyed for
    /// the whole take, so its first buffer is a key-down and the gate resets
    /// open, as at a real key-down, and a slow offline buffer is never taken for
    /// a stream restart. Each stage sees the same samples in the same order as
    /// the live single pass, which the positive control in
    /// <c>ChainAuditionTwinTests</c> proves sample for sample.
    /// </para>
    /// <para>
    /// <b>Never a partly dry render presented as the chain's.</b> After the
    /// noise pass every enabled slot's bank is read: every frame through the
    /// engine; an in-process engine never late; a helper engine late exactly by
    /// its start-up (its pipeline depth, plus the first answers covering its own
    /// delay, which it discards), at the live delay; no fallback and no
    /// latched frame; and the delay the same on every buffer. Anything else is
    /// unavailable, naming the engine, with the counts traced either way.
    /// </para>
    /// </remarks>
    internal sealed class ChainAuditionTwin : IChainAuditionProcessor
    {
        /// <summary>Frames per buffer: 10 ms at 48 kHz, the capture's own buffer and an engine frame. Claude's judgement (the brief's).</summary>
        internal const int BufferFrames = 480;

        /// <summary>How long a render waits for an engine to be built: past a helper's construction wait and handshake, with room to spare.</summary>
        internal static readonly TimeSpan DefaultBuildWait = TimeSpan.FromSeconds(10);

        // DRAFT PROSE keys (Track Q phase B), for Codex.
        internal const string WrongRateKey = "audio.audition.verdict.wrong_rate";
        internal const string EngineUnavailableKey = "audio.audition.verdict.engine_unavailable";
        internal const string EngineNotReadyKey = "audio.audition.verdict.engine_not_ready";
        internal const string EngineFailedKey = "audio.audition.verdict.engine_failed";
        internal const string IncompleteKey = "audio.audition.verdict.render_incomplete";
        internal const string RenderFailedKey = "audio.audition.verdict.render_failed";

        private readonly Func<TxNoiseSetup> _setup;
        private readonly Func<AuditionLiveState?> _live;
        private readonly Func<RenderEngines> _engines;
        private readonly TimeSpan _buildWait;

        /// <summary>
        /// The transmit passband the in-process stages are rendered inside
        /// (Sprint 49 Track J, item 5): the de-esser's high edge, and JJ
        /// Clarity's low and high edges, as the LIVE chain holds them, so a
        /// render is clamped exactly as what is sent. Zero is unknown: nothing
        /// is clamped to it.
        /// </summary>
        internal readonly record struct Passband(float DeEsserHighEdgeHz, float ClarityLowEdgeHz, float ClarityHighEdgeHz)
        {
            /// <summary>The live conditioner's edges now, or all unknown with no radio.</summary>
            public static Passband Live()
            {
                var cond = TxAudioConditioning.Conditioner;
                return cond == null ? default
                    : new Passband(cond.DeEsser.PassbandHighEdgeHz, cond.ClarityPassbandLowEdgeHz, cond.ClarityPassbandHighEdgeHz);
            }
        }

        /// <summary>Asked as each render starts: the passband. The app's is <see cref="Passband.Live"/>; a test gives its own.</summary>
        internal Func<Passband> PassbandSource { get; init; } = Passband.Live;

        /// <summary>
        /// What one render builds its engines from: the catalog, and the helper
        /// host the twin OWNS for that render (disposed when it ends), or null
        /// when the host belongs to someone else (a test's stub rig).
        /// </summary>
        internal sealed record RenderEngines(DenoiseEngineCatalog Catalog, WdspHost? OwnedHost, WdspHost? ExpanderHost = null);

        /// <summary>
        /// Tests only: run WDSP's expander keyed, as the in-process pass runs, to
        /// show the completeness check sees the latch (phase 2's positive
        /// control). A keyed bank latches dry on the helper's normal first late
        /// frames, which is why the real render runs it unkeyed.
        /// </summary>
        internal bool ForceKeyedExpanderPassForTest { get; set; }

        /// <param name="setup">The configured chain, asked as each render starts.</param>
        /// <param name="live">The live transmit side as the window read it from its own radio; null with no radio. For the verdict, and for the threshold only when the take is too short for its own floor.</param>
        /// <param name="catalog">The catalog to build engines from; the app's is <see cref="DenoiseEngineCatalog.ForOfflineRender"/>.</param>
        /// <param name="buildWait">How long to wait for each enabled slot's engine to be built.</param>
        internal ChainAuditionTwin(Func<TxNoiseSetup> setup, Func<AuditionLiveState?> live, Func<DenoiseEngineCatalog> catalog,
            TimeSpan buildWait)
            : this(setup, live, Wrap(catalog), buildWait) { }

        /// <param name="engines">Asked as each render starts: the catalog, and a helper host the twin owns for that render, or null.</param>
        internal ChainAuditionTwin(Func<TxNoiseSetup> setup, Func<AuditionLiveState?> live, Func<RenderEngines> engines,
            TimeSpan buildWait)
        {
            _setup = setup ?? throw new ArgumentNullException(nameof(setup));
            _live = live ?? throw new ArgumentNullException(nameof(live));
            _engines = engines ?? throw new ArgumentNullException(nameof(engines));
            _buildWait = buildWait;
        }

        private static Func<RenderEngines> Wrap(Func<DenoiseEngineCatalog> catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            return () => new RenderEngines(catalog(), null);
        }

        /// <summary>
        /// The app's twin: the configured chain, its engines from the offline
        /// catalog on a helper host of ITS OWN for each render (repair 1, R1;
        /// Codex's check of 3c61da16). Never <see cref="WdspHost.Default"/>: the
        /// live chain's helper channels share that host's process, connection,
        /// writer, restart count and failure handling, so an audition on it could
        /// take down or restart the live chain's helper, and its attach would reset
        /// the live host's restart count. (It could not revive a host already at
        /// its cap: the catalog's probe refuses a capped host before anything
        /// attaches; repair 2 corrects an earlier comment that said it could.)
        /// </summary>
        internal static ChainAuditionTwin ForApp(Func<AuditionLiveState?> live) =>
            new(() => TxAudioConditioning.CaptureSetup(""), live, () =>
            {
                var host = NewRenderHost();
                return new RenderEngines(DenoiseEngineCatalog.ForOfflineRender(host), host, host);
            }, DefaultBuildWait);

        /// <summary>
        /// A helper host for one render: the app's helper program in a process
        /// of its own (it starts only if a helper engine is chosen), with no
        /// model-cost store, and publishing nothing to the live capture-rate
        /// policy (<see cref="WdspHost.PublishesLiveTelemetry"/>). Disposed when
        /// the render ends.
        /// </summary>
        internal static WdspHost NewRenderHost() =>
            new(null, WdspHostPolicy.Default, null, costStore: null) { PublishesLiveTelemetry = false };

        // ── The live side, read by the window from its own radio (items 5 and 6) ──

        /// <summary>
        /// What the window knows of the live transmit side, read from its own
        /// radio, never from the twin: the live gate (its threshold policy and
        /// figure in force, and whether the live meter measured a floor during
        /// the last over, since it resets at each over's start) and the transmit
        /// mode. Null with no radio. Reads only; changes nothing.
        /// </summary>
        internal static AuditionLiveState? ReadLive(FlexBase? rig)
        {
            if (rig == null) return null;
            try
            {
                var cond = rig.TxConditioner;
                if (cond == null) return null;
                // Sprint 49 Track I (#1252): the live rule's floor is the room
                // meter at the expander's input now, not the meter after the chain.
                var room = cond.RoomFloor;
                string mode;
                try { mode = TxAudioConditioning.TransmitModeOf(rig) ?? ""; }
                catch (Exception) { mode = ""; }
                return LiveState(cond.AutoThresholdInForce, cond.Gate.ThresholdDb, room.HasFloor, room.HasFloor ? room.FloorDb : LufsMeter.Floor,
                    cond.ThresholdMarginInForceDb, mode) with { TransmitRateHz = cond.LastSampleRate };
            }
            catch (Exception ex)
            {
                Trace.WriteLine("ChainAuditionTwin: the live transmit side could not be read: " + ex.GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// The live state from its readings. "Measured an over" means exactly
        /// what the live rule would act on: <see cref="TxAudioConditioning.DerivedThreshold"/>
        /// gives a figure for that profile (unkeyed). The mode clause follows the
        /// live chain's own policy, <see cref="TxAudioConditioning.BypassedIn"/>.
        /// </summary>
        internal static AuditionLiveState LiveState(bool autoInForce, float thresholdDb, bool profileValid, float noiseFloorLufs,
            float marginDb, string transmitMode)
        {
            bool measured = TxAudioConditioning.DerivedThreshold(true, false, profileValid, noiseFloorLufs, marginDb) != null;
            return new AuditionLiveState(new AuditionLiveGate(autoInForce, thresholdDb, measured), transmitMode ?? "",
                TxAudioConditioning.BypassedIn(transmitMode ?? ""));
        }

        // ── The render ──────────────────────────────────────────────────

        /// <summary>One enabled slot, as the setup asks for it.</summary>
        private sealed record SlotPlan(DenoiseFamily Family, string EngineId, string Name);

        public ChainProcessing Process(float[] rawMono, int sampleRate, CancellationToken cancel, Action<double>? progress)
        {
            if (rawMono == null) throw new ArgumentNullException(nameof(rawMono));
            // 1. The configuration now, not when the twin was made.
            var setup = _setup() ?? new TxNoiseSetup();
            AuditionLiveState? live;
            try { live = _live(); }
            catch (Exception) { live = null; }
            var gate = setup.Gate ?? new TxNoiseGateSetup();
            if (sampleRate != TxNoiseChain.SampleRate)
            {
                // The loader makes this unreachable today; the chain would pass
                // such a take through untouched, which must never be called cleaned.
                Trace.WriteLine("ChainAuditionTwin: a take at " + sampleRate + " Hz is not rendered; the chain runs at " + TxNoiseChain.SampleRate);
                return ChainProcessing.Unavailable(WrongRateKey);
            }

            var engines = _engines();
            var catalog = engines.Catalog;
            try
            {
                return Render(rawMono, setup, live, gate, catalog, engines.ExpanderHost, cancel, progress);
            }
            finally
            {
                // R1: the render's own helper host goes with the render, its
                // process and connection included; nothing of it outlives it.
                if (engines.OwnedHost is { } host)
                {
                    try { host.Dispose(); }
                    catch (Exception ex) { Trace.WriteLine("ChainAuditionTwin: the render's helper host did not close: " + ex.GetType().Name); }
                }
            }
        }

        /// <summary>The enabled noise slots a setup asks for, in the order they run.</summary>
        private static List<SlotPlan> PlansOf(TxNoiseSetup setup, DenoiseEngineCatalog catalog)
        {
            var plans = new List<SlotPlan>(2);
            foreach (var (family, slotSetup) in new[] { (DenoiseFamily.Spectral, setup.Spectral), (DenoiseFamily.Neural, setup.Neural) })
            {
                if (slotSetup?.Enabled != true) continue;
                string id = string.IsNullOrWhiteSpace(slotSetup.EngineId) ? TxNoiseChain.DefaultEngine(family) : slotSetup.EngineId;
                var row = catalog.Find(id);
                plans.Add(new SlotPlan(family, id, row != null ? Lexicon.Get(row.LabelKey) : id));
            }
            return plans;
        }

        /// <summary>
        /// What a render has once everything UPSTREAM of the in-process pass
        /// has run: the noise pass's output, the expander's input (WDSP's
        /// expander's output in its mode), the delays, and the threshold. The
        /// in-process pass can then be run over it any number of times with
        /// different settings (the Clarity suggest pass renders several).
        /// </summary>
        internal sealed class UpstreamAudio
        {
            public float[] Noise = Array.Empty<float>();
            public float[] ExpanderInput = Array.Empty<float>();
            public int Buffers;
            /// <summary>The noise pass's delay, samples.</summary>
            public int NoiseDelay;
            /// <summary>WDSP's expander's delay in its mode, samples; else 0.</summary>
            public int ExpanderDelay;
            public bool WdspMode;
            public float? Threshold;
            public AuditionThresholdSource? Source;
            public float? Floor;
        }

        /// <summary>
        /// Steps 3 to 7 and WDSP's expander pass: the engines checked, built
        /// and waited for, the noise pass, its completeness, the threshold, and
        /// the expander in WDSP's mode. Null when it ran, with
        /// <paramref name="upstream"/> filled; otherwise the verdict. The chain
        /// it builds is torn down before it returns, either way.
        /// </summary>
        private ChainProcessing? Upstream(float[] rawMono, TxNoiseSetup setup, AuditionLiveState? live, TxNoiseGateSetup gate,
            DenoiseEngineCatalog catalog, WdspHost? expanderHost, List<SlotPlan> plans, CancellationToken cancel,
            Action<int>? reportBuffer, out UpstreamAudio upstream)
        {
            upstream = new UpstreamAudio();
            // 3. An engine reported unavailable is said at once, in the words
            // the rest of the app already uses for it, with its name.
            foreach (var plan in plans)
            {
                var row = catalog.Find(plan.EngineId);
                var reason = row == null || row.Factory == null || row.Side != DenoiseSide.Pc || row.Family != plan.Family
                             || !row.Serves(DenoiseDirection.Transmit)
                    ? Unavailability.LibraryMissing : row.Availability();
                if (reason != Unavailability.None)
                {
                    Trace.WriteLine("ChainAuditionTwin: " + plan.EngineId + " is unavailable (" + reason + "); nothing rendered");
                    return ChainProcessing.Unavailable(EngineUnavailableKey, ("engine", plan.Name),
                        ("reason", Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(reason))));
                }
            }

            TxNoiseChain? chain = null;
            try
            {
                // 2. Our own chain on the offline catalog, the setup's slots put
                // in place. KeySource stays null: no radio, never keyed.
                chain = new TxNoiseChain(catalog);
                // Every engine reaching here passed the checks above, which are the
                // slot's own reasons to refuse one, so each slot takes the setup's engine.
                chain.ApplySlotsNow(setup.Spectral, setup.Neural);

                // 4. Wait, bounded, for each enabled slot's engine.
                var notReady = WaitForEngines(chain, plans, cancel);
                if (notReady != null) return notReady;

                // 5. The noise pass.
                int buffers = (rawMono.Length + BufferFrames - 1) / BufferFrames;
                var noise = new float[buffers * BufferFrames];
                var stereo = new float[BufferFrames * 2];
                int? latency = null;
                bool latencyMoved = false;
                for (int b = 0; b < buffers; b++)
                {
                    cancel.ThrowIfCancellationRequested();
                    int at = b * BufferFrames;
                    for (int f = 0; f < BufferFrames; f++)
                    {
                        float s = at + f < rawMono.Length ? rawMono[at + f] : 0f;
                        stereo[2 * f] = s;
                        stereo[2 * f + 1] = s;
                    }
                    chain.Process(stereo, stereo.Length, TxNoiseChain.SampleRate, keyed: false);
                    for (int f = 0; f < BufferFrames; f++) noise[at + f] = stereo[2 * f];
                    int applied = chain.AppliedLatencySamples;
                    if (latency == null) latency = applied;
                    else if (applied != latency) latencyMoved = true;
                    reportBuffer?.Invoke(b + 1);
                }

                // 9. Completeness: never a partly dry render presented as the chain's.
                var incomplete = Completeness(chain, plans, buffers, latencyMoved, out string counts);
                Trace.WriteLine("ChainAuditionTwin: noise pass over " + buffers + " buffers, delay " + (latency ?? 0)
                    + " samples" + (latencyMoved ? " (MOVED)" : "") + "; " + counts);
                if (incomplete != null) return incomplete;

                // 6 and 7. The floor the threshold comes from, and the threshold.
                int delay = latency ?? 0;
                var cleaned = Slice(noise, delay, Math.Max(0, rawMono.Length - delay));
                var floorProfile = ChainAudition.Measure(cleaned, cleaned.Length, TxNoiseChain.SampleRate);
                // #1349: JJ Flexible's expander sets its threshold over what its
                // own detector reads on the room, which is above the room's
                // average; the take's own room says by how much.
                bool wdspModeHere = gate.Enabled && ExpanderModes.IsWdsp(gate.ExpanderMode);
                float overAverage = wdspModeHere ? 0f : DetectorOverAverageDb(gate, cleaned);
                var (threshold, source, floor) = Threshold(gate, floorProfile, live, overAverage);

                // Phase 2: the expander in WDSP's mode runs in the helper, so it is
                // NOT in the keyed in-process pass (a keyed bank latches dry on its
                // normal first late frames): its own pass, unkeyed, with the offline
                // wait, counted for completeness like a noise slot. Its input floor
                // is the noise pass's output, as for the JJ expander.
                bool wdspMode = gate.Enabled && ExpanderModes.IsWdsp(gate.ExpanderMode);
                int expanderDelay = 0;
                float[] expanderInput = noise;
                if (wdspMode)
                {
                    var refused = WdspExpanderPass(expanderHost, gate, threshold, noise, buffers, cancel, out expanderInput, out expanderDelay);
                    if (refused != null) return refused;
                }
                upstream.Noise = noise;
                upstream.ExpanderInput = expanderInput;
                upstream.Buffers = buffers;
                upstream.NoiseDelay = delay;
                upstream.ExpanderDelay = expanderDelay;
                upstream.WdspMode = wdspMode;
                upstream.Threshold = threshold;
                upstream.Source = source;
                upstream.Floor = floor;
                return null;
            }
            finally
            {
                // 11. Torn down on success, failure and cancellation alike. A build
                // still on its way when admission closes disposes itself on
                // arrival (PcDenoiseSlot.BuildChoice), so its helper channel
                // closes once that build returns.
                if (chain != null)
                {
                    foreach (var slot in new[] { chain.SpectralSlot, chain.NeuralSlot })
                    {
                        try { slot.CloseAdmission(); slot.DisposeOwned(); }
                        catch (Exception ex) { Trace.WriteLine("ChainAuditionTwin: teardown: " + ex.GetType().Name); }
                    }
                }
            }
        }

        private ChainProcessing Render(float[] rawMono, TxNoiseSetup setup, AuditionLiveState? live, TxNoiseGateSetup gate,
            DenoiseEngineCatalog catalog, WdspHost? expanderHost, CancellationToken cancel, Action<double>? progress)
        {
            var plans = PlansOf(setup, catalog);
            // The in-process stages after the noise slots (Sprint 49 Track I): the
            // expander, the equaliser and the de-esser. Any of them on is a chain
            // to render, the expander or not: rendering without one that is on
            // would offer the operator a chain they are not using as theirs.
            bool eqOn = setup.Eq?.ToDesign() is { IsIdentity: false };
            bool deEssOn = setup.DeEsser?.Enabled == true;
            // Sprint 49 Track CL: the compressor stage and the limiter. Either
            // alone is a chain to render.
            bool compOn = setup.Compressor?.Enabled == true;
            bool limiterOn = setup.Limiter?.Enabled == true;
            // Sprint 49 Track J: JJ Clarity, on with a band that adds something.
            // Alone, it is a chain to render.
            bool clarityOn = setup.Clarity?.AddsSomething == true;
            bool inProcess = gate.Enabled || eqOn || deEssOn || clarityOn || compOn || limiterOn;
            // Nothing on (Noel's S4): render nothing; only the raw take is offered.
            if (plans.Count == 0 && !inProcess)
            {
                Trace.WriteLine("ChainAuditionTwin: both noise slots, the expander, the equaliser, the de-esser, JJ Clarity, the compressor and the limiter are off; nothing rendered");
                return ChainProcessing.NothingIsOn(live);
            }

            try
            {
                int buffers = (rawMono.Length + BufferFrames - 1) / BufferFrames;
                int steps = buffers * (inProcess ? 2 : 1);
                int lastPercent = -1;
                void Report(int done)
                {
                    if (progress == null) return;
                    int percent = (int)((long)done * 100 / Math.Max(1, steps));
                    if (percent == lastPercent) return;
                    lastPercent = percent;
                    progress(Math.Min(1.0, (double)done / Math.Max(1, steps)));
                }

                var refusedUpstream = Upstream(rawMono, setup, live, gate, catalog, expanderHost, plans, cancel, Report, out var up);
                if (refusedUpstream != null) return refusedUpstream;
                var noise = up.Noise;
                var expanderInput = up.ExpanderInput;
                bool wdspMode = up.WdspMode;
                int expanderDelay = up.ExpanderDelay;
                int delay = up.NoiseDelay;
                var threshold = up.Threshold;
                var source = up.Source;
                var floor = up.Floor;
                var stereo = new float[BufferFrames * 2];

                // 8. The in-process pass, keyed for the whole take: the expander,
                // the equaliser, the de-esser, JJ Clarity, the compressor and the
                // limiter, put in force directly through the ONE wiring function
                // the live chain uses (Track I, item 7). The de-esser and JJ
                // Clarity hear the live transmit filter's edges, as the live
                // ones do (Track J, item 5).
                var output = new float[rawMono.Length];
                float[]? cleanupOnly = null;
                float? reported = null;
                if (inProcess)
                {
                    var edge = PassbandSource();
                    // In WDSP's mode the expander has run already; this pass is the
                    // stages after it, the in-process expander switched off.
                    var record = TxAudioConditioning.ToConditioner(setup);
                    if (wdspMode) record.Enabled = false;
                    reported = InProcessPass(record, gate, threshold, edge, expanderInput, output, buffers, stereo, cancel,
                        b => Report(buffers + b + 1));
                    // "What was removed" is the cleanup stages only (S8): the same
                    // pass with the equaliser and the de-esser off. The expander
                    // never hears them, so this is exactly its output.
                    // The limiter's look-ahead delays what is SENT and nothing
                    // upstream of it (Sprint 49 Track CL).
                    int limiterAhead = limiterOn ? TxLimiter.LookAheadFramesFor(setup.Limiter!.LookAheadMs, (uint)TxNoiseChain.SampleRate) : 0;
                    if (eqOn || deEssOn || clarityOn || compOn || limiterOn)
                    {
                        var cleanupRecord = TxAudioConditioning.ToConditioner(setup);
                        cleanupRecord.Eq = null;
                        cleanupRecord.DeEsserEnabled = false;
                        // JJ Clarity adds; it removes nothing (Track J): off here,
                        // so "what was removed" is the same with it on or off.
                        cleanupRecord.ClarityEnabled = false;
                        if (wdspMode) cleanupRecord.Enabled = false;
                        cleanupRecord.Comp = TxCompressorSettings.Default;
                        cleanupRecord.Limiter = TxLimiterSettings.Default;
                        cleanupOnly = new float[rawMono.Length];
                        InProcessPass(cleanupRecord, gate, threshold, edge, expanderInput, cleanupOnly, buffers, new float[BufferFrames * 2], cancel, null);
                        // "What was removed" is read at the SAME latency as the
                        // render (ChainProcessing.CleanupOnly's contract), so the
                        // cleanup pass, which has no limiter, is moved later by
                        // the limiter's look-ahead.
                        if (limiterAhead > 0)
                        {
                            Array.Copy(cleanupOnly, 0, cleanupOnly, limiterAhead, Math.Max(0, cleanupOnly.Length - limiterAhead));
                            Array.Clear(cleanupOnly, 0, Math.Min(limiterAhead, cleanupOnly.Length));
                        }
                    }
                    // The look-ahead is the expander's applied delay (item 9); in
                    // WDSP's mode, its bank's delay in its place. Then the
                    // limiter's look-ahead, which delays what is sent.
                    if (gate.Enabled) delay += wdspMode ? expanderDelay : TxNoiseGate.LookAheadFramesFor(gate.LookAheadMs, (uint)TxNoiseChain.SampleRate);
                    delay += limiterAhead;
                }
                else Array.Copy(noise, output, output.Length);

                var stages = new List<string>(4);
                if (plans.Count > 0) stages.Add(TxAudioConditioner.StageIds.NoiseReduction);
                if (gate.Enabled) stages.Add(gate.GateSetting && !wdspMode ? ChainAudition.GateStageId : TxAudioConditioner.StageIds.Expander);
                if (eqOn) stages.Add(TxAudioConditioner.StageIds.Equaliser);
                if (deEssOn) stages.Add(TxAudioConditioner.StageIds.DeEsser);
                if (clarityOn) stages.Add(TxAudioConditioner.StageIds.Clarity);
                if (compOn) stages.Add(TxAudioConditioner.StageIds.Compressor);
                if (limiterOn) stages.Add(TxAudioConditioner.StageIds.Limiter);
                Trace.WriteLine("ChainAuditionTwin: rendered " + rawMono.Length + " samples, stages " + string.Join(",", stages) + ", gate "
                    + (gate.Enabled ? "on at " + F(reported!.Value) + " dB (" + source
                        + (floor is float fl ? ", floor " + F(fl) + " LUFS" : "") + ")" : "off") + ", delay " + delay);
                return ChainProcessing.Rendered(output, delay, gate.Enabled, gate.Enabled ? reported : null,
                    gate.Enabled ? source : null, gate.Enabled ? floor : null, live, cleanupOnly, stages);
            }
            catch (OperationCanceledException)
            {
                Trace.WriteLine("ChainAuditionTwin: render cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("ChainAuditionTwin: render failed: " + ex.GetType().Name + ": " + ex.Message);
                return ChainProcessing.Unavailable(RenderFailedKey);
            }
        }

        // ── JJ Clarity's suggest pass: the same chain, rendered several times ──

        /// <summary>
        /// The operator's take, taken as far as the in-process stages ONCE, so
        /// the suggest pass can render it with JJ Clarity as it is now and with
        /// each candidate the peak guard tries (Sprint 49 Track J, J7; item 6).
        /// Every render is this twin's chain: the same wiring function, the
        /// same passband edges, the setup's own stages. It writes nothing
        /// anywhere.
        /// </summary>
        internal sealed class ClarityRenders
        {
            private readonly TxNoiseSetup _setup;
            private readonly TxNoiseGateSetup _gate;
            private readonly UpstreamAudio _up;
            private readonly Passband _passband;
            private readonly int _length;
            private readonly CancellationToken _cancel;

            internal ClarityRenders(TxNoiseSetup setup, TxNoiseGateSetup gate, UpstreamAudio up, Passband passband, int length, CancellationToken cancel,
                AuditionLiveState? live)
            {
                _setup = setup; _gate = gate; _up = up; _passband = passband; _length = length; _cancel = cancel;
                Live = live;
            }

            /// <summary>
            /// The live transmit side as it read when the take was prepared, or
            /// null with no radio (repair 1, B2). Every render here is at 48 kHz
            /// with the whole chain running; the live stream may run at another
            /// rate, and in CW and the digital modes the live chain stands
            /// aside. Whoever reports these renders must say so from this.
            /// </summary>
            public AuditionLiveState? Live { get; }

            /// <summary>Whether the setup's limiter is on.</summary>
            public bool LimiterOn => _setup.Limiter?.Enabled == true;

            /// <summary>The limiter's ceiling, dBFS, when it is on: the ceiling the peak guard tests the limiter's input against. Null with the limiter off.</summary>
            public double? LimiterCeilingDbfs => LimiterOn ? _setup.Limiter!.ToSettings().CeilingDb : null;

            /// <summary>How many samples late what is SENT runs behind the take: noise reduction, the expander, the limiter's look-ahead.</summary>
            public int SentLatencySamples =>
                _up.NoiseDelay + ExpanderDelay + (LimiterOn ? TxLimiter.LookAheadFramesFor(_setup.Limiter!.LookAheadMs, (uint)TxNoiseChain.SampleRate) : 0);

            private int ExpanderDelay => !_gate.Enabled ? 0
                : _up.WdspMode ? _up.ExpanderDelay : TxNoiseGate.LookAheadFramesFor(_gate.LookAheadMs, (uint)TxNoiseChain.SampleRate);

            /// <summary>What the chain would SEND with JJ Clarity set this way: every in-process stage, the limiter included. For listening.</summary>
            public float[] Sent(bool clarityOn, ClaritySettings settings) => Run(clarityOn, settings, limiter: true);

            /// <summary>
            /// The audio at the LIMITER'S INPUT with JJ Clarity set this way: the
            /// same chain with the limiter out, the compressor included. What
            /// the peak guard judges (item 6): at the chain's output a limiter
            /// that is on holds every sample to its ceiling, so the energy
            /// Clarity adds would show as nothing there and become limiting.
            /// </summary>
            public float[] AtLimiterInput(bool clarityOn, ClaritySettings settings) => Run(clarityOn, settings, limiter: false);

            private float[] Run(bool clarityOn, ClaritySettings settings, bool limiter)
            {
                var record = TxAudioConditioning.ToConditioner(_setup);
                if (_up.WdspMode) record.Enabled = false;
                record.ClarityEnabled = clarityOn;
                record.Clarity = settings ?? ClaritySettings.Default;
                if (!limiter) record.Limiter = TxLimiterSettings.Default;
                var output = new float[_length];
                InProcessPass(record, _gate, _up.Threshold, _passband, _up.ExpanderInput, output, _up.Buffers, new float[BufferFrames * 2], _cancel, null);
                return output;
            }
        }

        /// <summary>
        /// Prepare a take for JJ Clarity's suggest pass: everything upstream of
        /// the in-process stages, once. Null renders with the verdict in
        /// <paramref name="refusal"/> when an engine the setup asks for cannot
        /// run or the take is at the wrong rate. With nothing on in the chain
        /// it still prepares: the proposal is itself a chain to render.
        /// </summary>
        internal ClarityRenders? PrepareClarity(float[] rawMono, int sampleRate, CancellationToken cancel, Action<double>? progress,
            out ChainProcessing? refusal)
        {
            if (rawMono == null) throw new ArgumentNullException(nameof(rawMono));
            refusal = null;
            if (sampleRate != TxNoiseChain.SampleRate)
            {
                refusal = ChainProcessing.Unavailable(WrongRateKey);
                return null;
            }
            var setup = _setup() ?? new TxNoiseSetup();
            AuditionLiveState? live;
            try { live = _live(); }
            catch (Exception) { live = null; }
            var gate = setup.Gate ?? new TxNoiseGateSetup();
            var engines = _engines();
            try
            {
                var plans = PlansOf(setup, engines.Catalog);
                int buffers = (rawMono.Length + BufferFrames - 1) / BufferFrames;
                refusal = Upstream(rawMono, setup, live, gate, engines.Catalog, engines.ExpanderHost, plans, cancel,
                    progress == null ? null : b => progress(Math.Min(1.0, (double)b / Math.Max(1, buffers))), out var up);
                if (refusal != null) return null;
                return new ClarityRenders(setup, gate, up, PassbandSource(), rawMono.Length, cancel, live);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Trace.WriteLine("ChainAuditionTwin: preparing a take for JJ Clarity failed: " + ex.GetType().Name + ": " + ex.Message);
                refusal = ChainProcessing.Unavailable(RenderFailedKey);
                return null;
            }
            finally
            {
                if (engines.OwnedHost is { } host)
                {
                    try { host.Dispose(); }
                    catch (Exception ex) { Trace.WriteLine("ChainAuditionTwin: the render's helper host did not close: " + ex.GetType().Name); }
                }
            }
        }

        /// <summary>
        /// One keyed pass of the in-process stages over the noise pass's output,
        /// through a fresh conditioner with <paramref name="record"/> in force,
        /// inside <paramref name="passband"/>: the de-esser's edge on the
        /// de-esser, and JJ Clarity's two edges through the conditioner's one
        /// owner (never Clarity's own setters). Returns the threshold the
        /// expander really held (its setter clamps).
        /// </summary>
        internal static float InProcessPass(TxAudioConditioner.StagedGate record, TxNoiseGateSetup gate, float? threshold, Passband passband,
            float[] noise, float[] output, int buffers, float[] stereo, CancellationToken cancel, Action<int>? report)
        {
            var conditioner = new TxAudioConditioner { KeySource = () => true };
            conditioner.SetClarityPassband(passband.ClarityLowEdgeHz, passband.ClarityHighEdgeHz);
            conditioner.SetGateInForce(record);
            conditioner.DeEsser.PassbandHighEdgeHz = passband.DeEsserHighEdgeHz;
            if (gate.Enabled && gate.AutoThreshold && threshold is float t) conditioner.Gate.ThresholdDb = t;
            for (int b = 0; b < buffers; b++)
            {
                cancel.ThrowIfCancellationRequested();
                int at = b * BufferFrames;
                for (int f = 0; f < BufferFrames; f++) { stereo[2 * f] = noise[at + f]; stereo[2 * f + 1] = noise[at + f]; }
                conditioner.Process(stereo, stereo.Length, TxNoiseChain.SampleRate);
                for (int f = 0; f < BufferFrames && at + f < output.Length; f++) output[at + f] = stereo[2 * f];
                report?.Invoke(b);
            }
            return conditioner.Gate.ThresholdDb;
        }

        /// <summary>
        /// The expander in WDSP's mode over the noise pass's output (phase 2):
        /// through <see cref="TxWdspExpander"/>, the live code, on the render's own
        /// helper host, unkeyed (or keyed, for the positive control), with the
        /// offline wait. Null when it ran completely; otherwise the verdict, which
        /// names WDSP's expander, never a partly dry render presented as the chain's.
        /// </summary>
        private ChainProcessing? WdspExpanderPass(WdspHost? host, TxNoiseGateSetup gate, float? threshold, float[] noise, int buffers,
            CancellationToken cancel, out float[] output, out int delay)
        {
            output = noise;
            delay = 0;
            string name = Lexicon.Get("audio.noise_surface.tx.gate.mode_wdsp");
            if (host == null)
            {
                Trace.WriteLine("ChainAuditionTwin: the expander is in WDSP's mode and this render has no helper host; nothing rendered");
                return ChainProcessing.Unavailable(EngineUnavailableKey, ("engine", name),
                    ("reason", Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(Unavailability.HelperMissing))));
            }
            var holder = new TxWdspExpander(v => new WdspHelperEngine(host, TxWdspExpander.EngineId, DenoiseDirection.Transmit, v, offlineWait: true))
            {
                ClearOnGap = false,
            };
            holder.WantWdsp(true);
            var bank = holder.BuildNow(TxWdspExpander.ValuesFor(gate, threshold ?? TxNoiseGate.DefaultThresholdDb));
            try
            {
                if (bank == null || !bank.IsAvailable)
                {
                    var reason = bank?.EngineFor(0).Unavailability ?? Unavailability.InitFailed;
                    Trace.WriteLine("ChainAuditionTwin: WDSP's expander is unavailable (" + reason + "); nothing rendered");
                    return ChainProcessing.Unavailable(EngineUnavailableKey, ("engine", name),
                        ("reason", Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(reason == Unavailability.None ? Unavailability.InitFailed : reason))));
                }
                // The bank comes into force at an unkeyed buffer, as on the air; an
                // empty one, so no audio is spent on it.
                var stereo = new float[BufferFrames * 2];
                holder.Process(stereo, 0, (uint)TxNoiseChain.SampleRate, keyed: false);
                bool keyed = ForceKeyedExpanderPassForTest;
                var result = new float[noise.Length];
                int? latency = null;
                bool moved = false;
                for (int b = 0; b < buffers; b++)
                {
                    cancel.ThrowIfCancellationRequested();
                    int at = b * BufferFrames;
                    for (int f = 0; f < BufferFrames; f++) { float s = at + f < noise.Length ? noise[at + f] : 0f; stereo[2 * f] = s; stereo[2 * f + 1] = s; }
                    holder.Process(stereo, stereo.Length, (uint)TxNoiseChain.SampleRate, keyed);
                    for (int f = 0; f < BufferFrames && at + f < result.Length; f++) result[at + f] = stereo[2 * f];
                    int applied = holder.AppliedLatencySamples();
                    if (latency == null) latency = applied;
                    else if (applied != latency) moved = true;
                }
                long expectedLate = ExpectedLate(bank);
                bool complete = holder.RunningBank == bank && bank.EngineFrames == buffers && bank.LateFrames == expectedLate
                                && bank.FallbackFrames == 0 && bank.LatchedFrames == 0 && !bank.Latched && !moved;
                Trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "ChainAuditionTwin: WDSP's expander pass{0}: engine frames {1} of {2}, late {3} (expected {4}), fallback {5}, latched {6}, delay {7}{8}",
                    keyed ? " (forced keyed, for the test)" : "", bank.EngineFrames, buffers, bank.LateFrames, expectedLate,
                    bank.FallbackFrames, bank.LatchedFrames, latency ?? 0, moved ? " (MOVED)" : ""));
                if (!complete) return ChainProcessing.Unavailable(IncompleteKey, ("engine", name));
                output = result;
                delay = latency ?? 0;
                return null;
            }
            finally
            {
                try { bank?.Dispose(); holder.Dispose(); }
                catch (Exception ex) { Trace.WriteLine("ChainAuditionTwin: teardown of WDSP's expander: " + ex.GetType().Name); }
            }
        }

        /// <summary>
        /// The frames a bank may be late by design: for a helper engine, its
        /// pipeline depth and the first answers covering its own algorithmic
        /// delay, which it discards (WdspChannel.DiscardBelow); none in process.
        /// </summary>
        private static long ExpectedLate(PerChannelDenoiserBank bank) =>
            bank.EngineFor(0) is WdspHelperEngine helper
                ? helper.PipelineDepth + (helper.Channel is { } channel ? Volatile.Read(ref channel.DiscardBelow) : 0)
                : 0;

        /// <summary>
        /// Wait, bounded, until each enabled slot's chosen engine is built (running,
        /// or delivered and installed by the first buffer). A failed build is said
        /// at once; a timeout names the engine. Null when every one is ready.
        /// </summary>
        private ChainProcessing? WaitForEngines(TxNoiseChain chain, List<SlotPlan> plans, CancellationToken cancel)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                SlotPlan? waiting = null;
                foreach (var plan in plans)
                {
                    var slot = chain.SlotOf(plan.Family);
                    if (slot.ChoiceFailure is string failure)
                    {
                        Trace.WriteLine("ChainAuditionTwin: " + plan.EngineId + " could not be built: " + failure);
                        return ChainProcessing.Unavailable(EngineFailedKey, ("engine", plan.Name));
                    }
                    if (!slot.RunningIsCurrent(plan.EngineId) && !slot.BuildDelivered(plan.EngineId)) waiting = plan;
                }
                if (waiting == null) return null;
                if (clock.Elapsed >= _buildWait)
                {
                    Trace.WriteLine("ChainAuditionTwin: " + waiting.EngineId + " was not built within "
                        + _buildWait.TotalMilliseconds + " ms; nothing rendered");
                    return ChainProcessing.Unavailable(EngineNotReadyKey, ("engine", waiting.Name));
                }
                cancel.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }
        }

        /// <summary>
        /// Item 9: every enabled slot's bank, read once the noise pass is done.
        /// Its counts are this render's alone, because the bank was built for this
        /// chain. Null when complete; <paramref name="counts"/> is traced either way.
        /// </summary>
        private static ChainProcessing? Completeness(TxNoiseChain chain, List<SlotPlan> plans, int buffers, bool latencyMoved,
            out string counts)
        {
            var parts = new List<string>(2);
            ChainProcessing? verdict = null;
            foreach (var plan in plans)
            {
                var slot = chain.SlotOf(plan.Family);
                var bank = slot.RunningBank;
                if (bank == null || slot.RunningEngineId != plan.EngineId)
                {
                    parts.Add(plan.Family + " " + plan.EngineId + ": no bank ran (running '" + slot.RunningEngineId + "')");
                    verdict ??= ChainProcessing.Unavailable(IncompleteKey, ("engine", plan.Name));
                    continue;
                }
                // A helper's first answers are late by design: k frames of pipeline,
                // and the answers covering its own algorithmic delay, which the
                // engine discards (WdspChannel.DiscardBelow, set from the open
                // acknowledgement). Both go out dry at the live delay, exactly as
                // when the engine starts on the air. Nothing else may be late.
                long expectedLate = ExpectedLate(bank);
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1}: engine frames {2} of {3}, late {4} (expected {5}), fallback {6}, latched {7}, delay {8}",
                    plan.Family, plan.EngineId, bank.EngineFrames, buffers, bank.LateFrames, expectedLate,
                    bank.FallbackFrames, bank.LatchedFrames, bank.LatencySamples));
                bool complete = bank.EngineFrames == buffers && bank.LateFrames == expectedLate
                                && bank.FallbackFrames == 0 && bank.LatchedFrames == 0;
                if (!complete) verdict ??= ChainProcessing.Unavailable(IncompleteKey, ("engine", plan.Name));
            }
            if (latencyMoved && verdict == null)
                verdict = ChainProcessing.Unavailable(IncompleteKey, ("engine", plans.Count > 0 ? plans[0].Name : ""));
            counts = parts.Count == 0 ? "no noise slot on" : string.Join("; ", parts);
            return verdict;
        }

        /// <summary>
        /// Item 7, as ruled (Noel's S3): automatic is worked out from this take's
        /// own floor by the live rule itself; a take too short for a floor uses
        /// the live figure in force when there is a radio, else the gate's inert
        /// default; by hand is the setup's figure.
        /// </summary>
        /// <param name="detectorOverAverageDb">
        /// How far over the take's average room level the expander's own
        /// detector reads it (#1349; <see cref="DetectorOverAverageDb"/>), added
        /// to a threshold worked out from the take: the live rule's margin is
        /// over that reading. Zero for WDSP's mode, whose detector averages.
        /// </param>
        internal static (float? Threshold, AuditionThresholdSource? Source, float? Floor) Threshold(
            TxNoiseGateSetup gate, LufsMeter.LoudnessProfile floor, AuditionLiveState? live, float detectorOverAverageDb = 0f)
        {
            if (!gate.Enabled) return (null, null, null);
            if (!gate.AutoThreshold) return (gate.ThresholdDb, AuditionThresholdSource.SetByHand, null);
            var derived = TxAudioConditioning.DerivedThreshold(true, false, floor.IsValid, floor.NoiseFloorLufs, gate.ThresholdMarginDb);
            if (derived is float d) return (d + detectorOverAverageDb, AuditionThresholdSource.FromThisTake, floor.NoiseFloorLufs);
            if (live != null) return (live.Gate.ThresholdDb, AuditionThresholdSource.LiveFigure, null);
            return (TxNoiseGate.DefaultThresholdDb, AuditionThresholdSource.Default, null);
        }

        /// <summary>
        /// How far over the room's average the expander's detector reads the
        /// take's own room, dB (#1349): the live room meter
        /// (<see cref="TxRoomFloor"/>), hearing through the expander's
        /// side-chain filter as set, run over the cleaned take as unkeyed
        /// audio; its floor (the detector's reading) less its mean floor. Zero
        /// when the take is too short for a floor.
        /// </summary>
        internal static float DetectorOverAverageDb(TxNoiseGateSetup gate, float[] cleaned)
        {
            var room = new TxRoomFloor();
            room.SetSideChain(gate.SideChainFilter, gate.SideChainLowCutHz, gate.SideChainHighCutHz);
            var stereo = new float[BufferFrames * 2];
            for (int at = 0; at + BufferFrames <= cleaned.Length; at += BufferFrames)
            {
                for (int f = 0; f < BufferFrames; f++) { stereo[2 * f] = cleaned[at + f]; stereo[2 * f + 1] = cleaned[at + f]; }
                room.Process(stereo, stereo.Length, TxNoiseChain.SampleRate);
            }
            if (!room.HasFloor || !float.IsFinite(room.FloorDb) || !float.IsFinite(room.MeanFloorDb)) return 0f;
            return Math.Max(0f, room.FloorDb - room.MeanFloorDb);
        }

        private static float[] Slice(float[] source, int from, int count)
        {
            var part = new float[Math.Max(0, count)];
            if (count > 0) Array.Copy(source, from, part, 0, count);
            return part;
        }

        private static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    }
}

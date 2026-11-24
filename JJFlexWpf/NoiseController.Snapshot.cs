using System;
using System.Collections.Generic;
using System.Linq;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Everything the noise layer can change, as it stood when the layer
    /// opened, so Escape can put it back (E7): every radio switch, the levels
    /// the layer touched, both computer slots and every engine's settings,
    /// favourites and per-mode memory.
    /// </summary>
    public sealed class NoiseSnapshot
    {
        internal Dictionary<string, bool> RadioSwitches { get; } = new();
        internal Dictionary<string, int> RadioLevels { get; } = new();
        /// <summary>
        /// The slice the radio part was actually read from (B1): the radio's
        /// own slice object, as <see cref="INoiseRadio.SliceIdentity"/> gives
        /// it, never the requested VFO number. Repair round 2: a slice jump
        /// changes the VFO number at once but makes the new slice active only
        /// when the queued command runs, so a number would label the OLD
        /// slice's values with the new slice's name. The radio part belongs
        /// to this slice and is never written to another one.
        /// </summary>
        internal object? SliceIdentity { get; set; }
        /// <summary>
        /// The radio switches and levels the layer itself wrote on
        /// <see cref="SliceIdentity"/> since the radio part was read. Escape puts back
        /// only these: a value that merely differs from the picture (another
        /// slice, another program, the radio itself) is not this layer's to
        /// undo.
        /// </summary>
        internal HashSet<string> TouchedSwitches { get; } = new();
        internal HashSet<string> TouchedLevels { get; } = new();
        internal (bool On, string Engine) Spectral { get; set; }
        internal (bool On, string Engine) Neural { get; set; }
        internal Radios.DenoiseEngineSettings[] SpectralSettings { get; set; } = Array.Empty<Radios.DenoiseEngineSettings>();
        internal Radios.DenoiseEngineSettings[] NeuralSettings { get; set; } = Array.Empty<Radios.DenoiseEngineSettings>();
        internal List<NoiseFavourite> Favourites { get; set; } = new();
        internal string MemoryXml { get; set; } = "";
        /// <summary>
        /// The transmit chain's two slots as the layer found them (Sprint 49
        /// Track H2): T turns the layer's keys on the transmit chain, so Escape
        /// puts the transmit slots back too. Null when no chain was reachable.
        /// </summary>
        internal TxNoiseSlotSetup? TransmitSpectral { get; set; }
        internal TxNoiseSlotSetup? TransmitNeural { get; set; }
        /// <summary>
        /// The transmit gate as the layer found it (words round, the
        /// orchestrator's decision): a preset applied inside the layer can change
        /// the gate, so Escape puts the gate back too and "All changes reverted"
        /// stays true. Null when no chain was reachable.
        /// </summary>
        internal TxNoiseGateSetup? TransmitGate { get; set; }

        /// <summary>
        /// What the layer itself last left each transmit part as (Escape
        /// ownership round, like <see cref="TouchedSwitches"/> for the radio):
        /// null for a part the layer has not changed. Escape puts a part back
        /// only while it still reads as the layer left it, so a setup, Home or
        /// the menu changing it afterwards keeps its change. Recorded when the
        /// layer's change really ran: a change held for the unkey records at the
        /// unkey, in order, or never if Escape withdrew it.
        /// </summary>
        internal TxNoiseSlotSetup? LeftSpectral { get; set; }
        internal TxNoiseSlotSetup? LeftNeural { get; set; }
        internal TxNoiseGateSetup? LeftGate { get; set; }

        /// <summary>
        /// The receive twins of <see cref="LeftSpectral"/> and <see cref="LeftNeural"/>
        /// (#1150): what the layer itself last left each receive computer slot as,
        /// null for a slot the layer has not changed. Escape puts a receive slot
        /// back only while it still reads as the layer left it, by the same rule
        /// as transmit, so a change made from Home, the menu, a preset or a mode
        /// group while the layer was open is kept. Receive writes are never held,
        /// so each is recorded as it runs.
        /// </summary>
        internal ReceiveSlotPicture? LeftReceiveSpectral { get; set; }
        internal ReceiveSlotPicture? LeftReceiveNeural { get; set; }

        /// <summary>
        /// The computer slots and the transmit gate exactly as the layer found
        /// them, never rebased (repair 2, R1). <see cref="Spectral"/>,
        /// <see cref="Neural"/>, <see cref="TransmitSpectral"/>,
        /// <see cref="TransmitNeural"/> and <see cref="TransmitGate"/> are the
        /// restore TARGET, and a target is rebased onto a change made elsewhere
        /// before the layer's next edit, so that change is kept. These answer a
        /// different question: whether what Escape leaves standing still differs
        /// from the layer's entry, which the rebased target can no longer tell.
        /// Null when the part could not be read at the entry.
        /// </summary>
        internal ReceiveSlotPicture? EntrySpectral { get; set; }
        internal ReceiveSlotPicture? EntryNeural { get; set; }
        internal TxNoiseSlotSetup? EntryTransmitSpectral { get; set; }
        internal TxNoiseSlotSetup? EntryTransmitNeural { get; set; }
        internal TxNoiseGateSetup? EntryTransmitGate { get; set; }

        // ── Sprint 49 Track I, repair 1 (B1): the equaliser, the de-esser and the
        //    stage order on transmit, and the equaliser on receive, under the same
        //    rule as the slots and the gate: the restore TARGET (rebased onto a
        //    change made elsewhere), what the layer itself LEFT each as (null when
        //    it has not changed it), and the never-rebased ENTRY picture. ──
        //    Repair 2: EACH stage on its own (the equaliser, the de-esser, the
        //    order), as the spectral slot, the neural slot and the gate are, so
        //    a change made elsewhere to one stage never costs the layer its
        //    ownership of another (Codex's recheck of 0a54b6e0).
        internal ParametricEqSetup? TransmitEq { get; set; }
        internal ParametricEqSetup? LeftEq { get; set; }
        internal ParametricEqSetup? EntryTransmitEq { get; set; }
        internal TxDeEsserSetup? TransmitDeEsser { get; set; }
        internal TxDeEsserSetup? LeftDeEsser { get; set; }
        internal TxDeEsserSetup? EntryTransmitDeEsser { get; set; }
        internal string[]? TransmitOrder { get; set; }
        internal string[]? LeftOrder { get; set; }
        internal string[]? EntryTransmitOrder { get; set; }
        // Sprint 49 Track CL, repair 1 (B4): the compressor stage and the
        // limiter, each on its own, by the same rule. A setup or a preset
        // applied from the layer writes both, so the layer's Escape must be
        // able to put both back.
        internal TxCompressorSetup? TransmitCompressor { get; set; }
        internal TxCompressorSetup? LeftCompressor { get; set; }
        internal TxCompressorSetup? EntryTransmitCompressor { get; set; }
        internal TxLimiterSetup? TransmitLimiter { get; set; }
        internal TxLimiterSetup? LeftLimiter { get; set; }
        internal TxLimiterSetup? EntryTransmitLimiter { get; set; }
        internal ParametricEqSetup? ReceiveEq { get; set; }
        internal ParametricEqSetup? LeftReceiveEq { get; set; }
        internal ParametricEqSetup? EntryReceiveEq { get; set; }
    }

    /// <summary>The transmit stages after the gate as one picture: the equaliser, the de-esser and the stage order (Sprint 49 Track I).</summary>
    internal sealed record TxStagesPicture(ParametricEqSetup Eq, TxDeEsserSetup DeEsser, string[] Order,
        TxCompressorSetup Compressor, TxLimiterSetup Limiter)
    {
        public TxStagesPicture Copy() => new(Eq.Clone(), DeEsser.Clone(), (string[])Order.Clone(), Compressor.Clone(), Limiter.Clone());

        /// <summary>The compressor and the limiter compared as the controller compares them everywhere (the preset's id is a note, not a setting).</summary>
        public static bool SameCompressor(TxCompressorSetup? a, TxCompressorSetup? b) => a != null && b != null && NoiseController.SameCompressor(a, b);
        public static bool SameLimiter(TxLimiterSetup? a, TxLimiterSetup? b) => a != null && b != null && NoiseController.SameLimiter(a, b);

        /// <summary>Each stage compared on its own, through the record's own serialized form (repair 2).</summary>
        public static bool SameEq(ParametricEqSetup? a, ParametricEqSetup? b) =>
            a != null && b != null && new TxNoiseSetup { Eq = a }.SameContentAs(new TxNoiseSetup { Eq = b });
        public static bool SameDeEsser(TxDeEsserSetup? a, TxDeEsserSetup? b) =>
            a != null && b != null && new TxNoiseSetup { DeEsser = a }.SameContentAs(new TxNoiseSetup { DeEsser = b });
        public static bool SameOrder(string[]? a, string[]? b) => a != null && b != null && a.SequenceEqual(b);
    }

    /// <summary>A receive computer slot as read: on or off, its chosen engine, and every engine's stored settings (#1150).</summary>
    internal sealed record ReceiveSlotPicture(bool On, string Engine, Radios.DenoiseEngineSettings[] Settings);

    public sealed partial class NoiseController
    {
        /// <summary>Take the picture Escape puts back.</summary>
        public NoiseSnapshot TakeSnapshot()
        {
            var snap = new NoiseSnapshot();
            FollowActiveSlice();   // a picture is only ever of the slice the radio reads, its shadows reconciled first
            ReadRadioPart(snap);
            _live = snap;
            if (_pipeline() is { } p)
            {
                snap.Spectral = (p.SpectralSlot.Enabled, p.SpectralSlot.ChosenEngineId);
                snap.Neural = (p.NeuralSlot.Enabled, p.NeuralSlot.ChosenEngineId);
                snap.SpectralSettings = p.SpectralSlot.ExportSettings();
                snap.NeuralSettings = p.NeuralSlot.ExportSettings();
                // A second export, so nothing the restore target shares can reach the entry picture.
                snap.EntrySpectral = ReadRx(p.SpectralSlot);
                snap.EntryNeural = ReadRx(p.NeuralSlot);
                snap.ReceiveEq = p.EqualizerSetup;
                snap.EntryReceiveEq = p.EqualizerSetup;
            }
            if (_config() is { } c)
            {
                snap.Favourites = c.NoiseFavourites.Select(f => new NoiseFavourite { EngineId = f.EngineId, Model = f.Model, Name = f.Name }).ToList();
                snap.MemoryXml = SerializeMemory(c.NoiseModeMemory);
            }
            if (_txChain() is { } chain)
            {
                (snap.TransmitSpectral, snap.TransmitNeural) = chain.CaptureSlots();
                snap.TransmitGate = TransmitGate.Capture();
                snap.EntryTransmitSpectral = snap.TransmitSpectral?.Clone();
                snap.EntryTransmitNeural = snap.TransmitNeural?.Clone();
                snap.EntryTransmitGate = snap.TransmitGate?.Clone();
                var stages = ReadStages();
                snap.TransmitEq = stages.Eq.Clone(); snap.EntryTransmitEq = stages.Eq.Clone();
                snap.TransmitDeEsser = stages.DeEsser.Clone(); snap.EntryTransmitDeEsser = stages.DeEsser.Clone();
                snap.TransmitOrder = (string[])stages.Order.Clone(); snap.EntryTransmitOrder = (string[])stages.Order.Clone();
                snap.TransmitCompressor = stages.Compressor.Clone(); snap.EntryTransmitCompressor = stages.Compressor.Clone();
                snap.TransmitLimiter = stages.Limiter.Clone(); snap.EntryTransmitLimiter = stages.Limiter.Clone();
            }
            return snap;
        }

        /// <summary>
        /// The snapshot the open noise layer will put back on Escape, if any:
        /// the controller marks on it every radio write made while it is live.
        /// </summary>
        private NoiseSnapshot? _live;

        /// <summary>
        /// Read the radio part of a snapshot afresh, from the slice the radio
        /// is reading now, with nothing touched yet: at the layer's entry, and
        /// once the radio's active slice really has changed under it (B1:
        /// leaving a slice confirms that slice's work, LayerSliceJump's
        /// documented rule, so the new slice starts clean).
        /// </summary>
        private void ReadRadioPart(NoiseSnapshot snap)
        {
            snap.RadioSwitches.Clear();
            snap.RadioLevels.Clear();
            snap.TouchedSwitches.Clear();
            snap.TouchedLevels.Clear();
            // Never marks a slice reconciled: only FollowActiveSlice does, and
            // only after it has forgotten the old slice's shadows (round 3).
            var r = _radio();
            snap.SliceIdentity = r?.SliceIdentity;
            if (r is null) return;
            foreach (var e in Engines().Where(e => e.Side == DenoiseSide.Radio))
            {
                snap.RadioSwitches[e.Id] = IsOn(e.Id);
                if (e.Level != null && ReadRadioLevel(e) is int level) snap.RadioLevels[e.Id] = level;
            }
        }

        /// <summary>A radio write the controller made: marked on the live snapshot, so Escape knows the layer made it.</summary>
        private void NoteRadioTouched(string id, bool level)
        {
            if (_live is not { } snap || _radio() is not { } r || !ReferenceEquals(r.SliceIdentity, snap.SliceIdentity)) return;
            (level ? snap.TouchedLevels : snap.TouchedSwitches).Add(id);
        }

        /// <summary>The slice the controller's shadows and the live snapshot belong to.</summary>
        private object? _readingSlice;

        /// <summary>
        /// Notice that the slice the radio is reading has changed (repair
        /// rounds 2 and 3, B1). Asked on every report of the active slice and
        /// BEFORE every slice-dependent read and write: <see cref="IsOn"/>,
        /// the radio level read, a lever's step, a new picture, a write. The
        /// poll that reports the slice runs only while Home's field panel is
        /// shown, so nothing may wait for it. On a change, in this order: the
        /// old slice's shadows are forgotten (they are keyed by slice as well,
        /// so they could not be read for another one anyway), the new slice is
        /// marked as the one being read, and the open layer's picture is read
        /// again from it, nothing touched.
        /// </summary>
        private void FollowActiveSlice()
        {
            if (_radio() is not { } r) return;
            object? now = r.SliceIdentity;
            if (ReferenceEquals(now, _readingSlice)) return;
            // A sibling observation belongs to the slice it was seen on.
            if (_readingSlice != null) RetireSiblingNotice("the radio now reads another slice");
            _switchShadow.Clear();
            _levelShadow.Clear();
            _readingSlice = now;
            if (_live is { } snap && !ReferenceEquals(now, snap.SliceIdentity)) ReadRadioPart(snap);
        }

        // ── Which transmit changes are the layer's (Escape ownership round) ──

        /// <summary>The noise layer's own source: a transmit write made with it while the layer's picture is live is the layer's to undo.</summary>
        public const string LayerSource = "noise layer";

        private int _ownTxDepth;

        /// <summary>
        /// Open around every transmit write that can carry the layer's source.
        /// Null (nothing to do) unless the source is the layer's and its picture
        /// is live; nested writes share the outermost. While open, every change
        /// the chain holds is tagged with the picture, so Escape can withdraw it.
        /// The compressor and limiter editor's writes are tagged the same way
        /// while it is open (repair 2, B5), with the editor's own tag and none
        /// of the layer's bookkeeping: its Cancel withdraws what it asked for
        /// while keyed and is still waiting.
        /// </summary>
        private IDisposable? OwnTx(string source)
        {
            if (_ownTxDepth > 0) return null;
            if (source == LayerSource && _live is { } snap && _txChain() is { } chain) return new LayerTxWrite(this, snap, chain);
            if (_editorOwner is { } editor && source == _editorSource && _txChain() is { } editorChain) return editorChain.OwnedBy(editor);
            return null;
        }

        /// <summary>
        /// One write by the layer. What the transmit parts read before it and
        /// after it is taken through the chain's ordering, so a change held for
        /// the unkey is measured when it runs, right after the changes held ahead
        /// of it and before anything asked for later; both readings are tagged,
        /// so Escape withdraws them with the change.
        /// </summary>
        private sealed class LayerTxWrite : IDisposable
        {
            private readonly NoiseController _owner;
            private readonly NoiseSnapshot _snap;
            private readonly TxNoiseChain _chain;
            private readonly IDisposable _tag;
            private TxPicture? _before;
            private bool _done;

            public LayerTxWrite(NoiseController owner, NoiseSnapshot snap, TxNoiseChain chain)
            {
                _owner = owner; _snap = snap; _chain = chain;
                owner._ownTxDepth++;
                _tag = chain.OwnedBy(snap);
                chain.AfterHeld(() => _before = owner.ReadTx(chain));
            }

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                try { _chain.AfterHeld(() => _owner.NoteLayerLeft(_snap, _before, _chain)); }
                finally { _tag.Dispose(); _owner._ownTxDepth--; }
            }
        }

        private sealed record TxPicture(TxNoiseSlotSetup Spectral, TxNoiseSlotSetup Neural, TxNoiseGateSetup Gate, TxStagesPicture Stages);

        private TxPicture ReadTx(TxNoiseChain chain)
        {
            var (spectral, neural) = chain.CaptureSlots();
            return new TxPicture(spectral, neural, TransmitGate.Capture(), ReadStages());
        }

        private TxStagesPicture ReadStages()
        {
            var (eq, deEsser, order) = TransmitGate.CaptureStages();
            var (compressor, limiter) = TransmitGate.CaptureDynamics();
            return new TxStagesPicture(eq.Clone(), deEsser.Clone(), (string[])order.Clone(), compressor.Clone(), limiter.Clone());
        }

        private static bool SameEq(ParametricEqSetup? a, ParametricEqSetup? b) =>
            a != null && b != null && new TxNoiseSetup { Eq = a }.SameContentAs(new TxNoiseSetup { Eq = b });

        /// <summary>
        /// After one of the layer's writes ran: each part it changed is the
        /// layer's now, as it left it. A part that had already moved since the
        /// layer last left it (or since the picture, if the layer had not
        /// touched it) was moved by someone else in between: that change is
        /// kept, so it becomes what Escape puts the part back to.
        /// </summary>
        private void NoteLayerLeft(NoiseSnapshot snap, TxPicture? before, TxNoiseChain chain)
        {
            if (before == null || snap.TransmitSpectral == null || snap.TransmitNeural == null) return;
            var now = ReadTx(chain);
            if (DescribeTx(now.Spectral) != DescribeTx(before.Spectral))
            {
                if (DescribeTx(before.Spectral) != DescribeTx(snap.LeftSpectral ?? snap.TransmitSpectral)) snap.TransmitSpectral = before.Spectral;
                snap.LeftSpectral = now.Spectral;
            }
            if (DescribeTx(now.Neural) != DescribeTx(before.Neural))
            {
                if (DescribeTx(before.Neural) != DescribeTx(snap.LeftNeural ?? snap.TransmitNeural)) snap.TransmitNeural = before.Neural;
                snap.LeftNeural = now.Neural;
            }
            if (!SameGate(now.Gate, before.Gate))
            {
                if (snap.TransmitGate != null && !SameGate(before.Gate, snap.LeftGate ?? snap.TransmitGate)) snap.TransmitGate = before.Gate;
                snap.LeftGate = now.Gate;
            }
            // Repair 2: each stage on its own, by the slots' rule.
            if (!TxStagesPicture.SameEq(now.Stages.Eq, before.Stages.Eq))
            {
                if (snap.TransmitEq != null && !TxStagesPicture.SameEq(before.Stages.Eq, snap.LeftEq ?? snap.TransmitEq)) snap.TransmitEq = before.Stages.Eq.Clone();
                snap.LeftEq = now.Stages.Eq.Clone();
            }
            if (!TxStagesPicture.SameDeEsser(now.Stages.DeEsser, before.Stages.DeEsser))
            {
                if (snap.TransmitDeEsser != null && !TxStagesPicture.SameDeEsser(before.Stages.DeEsser, snap.LeftDeEsser ?? snap.TransmitDeEsser)) snap.TransmitDeEsser = before.Stages.DeEsser.Clone();
                snap.LeftDeEsser = now.Stages.DeEsser.Clone();
            }
            if (!TxStagesPicture.SameOrder(now.Stages.Order, before.Stages.Order))
            {
                if (snap.TransmitOrder != null && !TxStagesPicture.SameOrder(before.Stages.Order, snap.LeftOrder ?? snap.TransmitOrder)) snap.TransmitOrder = (string[])before.Stages.Order.Clone();
                snap.LeftOrder = (string[])now.Stages.Order.Clone();
            }
            // Track CL, repair 1 (B4): the compressor and the limiter, the same way.
            if (!TxStagesPicture.SameCompressor(now.Stages.Compressor, before.Stages.Compressor))
            {
                if (snap.TransmitCompressor != null && !TxStagesPicture.SameCompressor(before.Stages.Compressor, snap.LeftCompressor ?? snap.TransmitCompressor))
                    snap.TransmitCompressor = before.Stages.Compressor.Clone();
                snap.LeftCompressor = now.Stages.Compressor.Clone();
            }
            if (!TxStagesPicture.SameLimiter(now.Stages.Limiter, before.Stages.Limiter))
            {
                if (snap.TransmitLimiter != null && !TxStagesPicture.SameLimiter(before.Stages.Limiter, snap.LeftLimiter ?? snap.TransmitLimiter))
                    snap.TransmitLimiter = before.Stages.Limiter.Clone();
                snap.LeftLimiter = now.Stages.Limiter.Clone();
            }
        }

        /// <summary>
        /// Whether Escape leaves a transmit part standing that differs from the
        /// layer's entry because of a change made elsewhere (F4; repair 2, R1).
        /// A part the layer owns goes back to its restore target, which differs
        /// from the entry only when it was rebased onto such a change; a part it
        /// does not own stays as it reads now. Compared with the never-rebased
        /// entry picture, because the rebased target already contains the change
        /// it would be compared for.
        /// </summary>
        private static bool KeptOutsideTx(NoiseSnapshot snap, TxPicture now, TxOwned owned)
        {
            static bool Slot(bool isOwned, TxNoiseSlotSetup? target, TxNoiseSlotSetup? entry, TxNoiseSlotSetup current) =>
                target != null && DescribeTx(isOwned ? target : current) != DescribeTx(entry ?? target);
            bool gate = snap.TransmitGate is { } gateTarget
                && !SameGate(owned.Gate ? gateTarget : now.Gate, snap.EntryTransmitGate ?? gateTarget);
            bool eq = snap.TransmitEq is { } eqTarget
                && !TxStagesPicture.SameEq(owned.Eq ? eqTarget : now.Stages.Eq, snap.EntryTransmitEq ?? eqTarget);
            bool deEsser = snap.TransmitDeEsser is { } deTarget
                && !TxStagesPicture.SameDeEsser(owned.DeEsser ? deTarget : now.Stages.DeEsser, snap.EntryTransmitDeEsser ?? deTarget);
            bool order = snap.TransmitOrder is { } orderTarget
                && !TxStagesPicture.SameOrder(owned.Order ? orderTarget : now.Stages.Order, snap.EntryTransmitOrder ?? orderTarget);
            bool compressor = snap.TransmitCompressor is { } compTarget
                && !TxStagesPicture.SameCompressor(owned.Compressor ? compTarget : now.Stages.Compressor, snap.EntryTransmitCompressor ?? compTarget);
            bool limiter = snap.TransmitLimiter is { } limiterTarget
                && !TxStagesPicture.SameLimiter(owned.Limiter ? limiterTarget : now.Stages.Limiter, snap.EntryTransmitLimiter ?? limiterTarget);
            bool stages = eq || deEsser || order || compressor || limiter;
            return Slot(owned.Spectral, snap.TransmitSpectral, snap.EntryTransmitSpectral, now.Spectral)
                || Slot(owned.Neural, snap.TransmitNeural, snap.EntryTransmitNeural, now.Neural)
                || gate || stages;
        }

        /// <summary>
        /// The transmit parts Escape puts back: those the layer changed, that
        /// still read as the layer left them, and that differ from the picture.
        /// Asked at Escape and again when the put-back runs, so a change someone
        /// else makes in between (held ahead of it, say) is kept.
        /// </summary>
        /// <summary>Which transmit parts Escape puts back, each on its own (repair 2 splits the stages).</summary>
        private readonly record struct TxOwned(bool Spectral, bool Neural, bool Gate, bool Eq, bool DeEsser, bool Order, bool Compressor, bool Limiter)
        {
            public bool AnyStage => Eq || DeEsser || Order || Compressor || Limiter;
            public bool Any => Spectral || Neural || Gate || AnyStage;
        }

        private TxOwned LayerOwnedTx(NoiseSnapshot snap, TxPicture now)
        {
            bool spectral = snap.LeftSpectral != null && snap.TransmitSpectral != null
                && DescribeTx(now.Spectral) == DescribeTx(snap.LeftSpectral) && DescribeTx(now.Spectral) != DescribeTx(snap.TransmitSpectral);
            bool neural = snap.LeftNeural != null && snap.TransmitNeural != null
                && DescribeTx(now.Neural) == DescribeTx(snap.LeftNeural) && DescribeTx(now.Neural) != DescribeTx(snap.TransmitNeural);
            bool gate = snap.LeftGate != null && snap.TransmitGate != null
                && SameGate(now.Gate, snap.LeftGate) && !SameGate(now.Gate, snap.TransmitGate);
            bool eq = snap.LeftEq != null && snap.TransmitEq != null
                && TxStagesPicture.SameEq(now.Stages.Eq, snap.LeftEq) && !TxStagesPicture.SameEq(now.Stages.Eq, snap.TransmitEq);
            bool deEsser = snap.LeftDeEsser != null && snap.TransmitDeEsser != null
                && TxStagesPicture.SameDeEsser(now.Stages.DeEsser, snap.LeftDeEsser) && !TxStagesPicture.SameDeEsser(now.Stages.DeEsser, snap.TransmitDeEsser);
            bool order = snap.LeftOrder != null && snap.TransmitOrder != null
                && TxStagesPicture.SameOrder(now.Stages.Order, snap.LeftOrder) && !TxStagesPicture.SameOrder(now.Stages.Order, snap.TransmitOrder);
            bool compressor = snap.LeftCompressor != null && snap.TransmitCompressor != null
                && TxStagesPicture.SameCompressor(now.Stages.Compressor, snap.LeftCompressor) && !TxStagesPicture.SameCompressor(now.Stages.Compressor, snap.TransmitCompressor);
            bool limiter = snap.LeftLimiter != null && snap.TransmitLimiter != null
                && TxStagesPicture.SameLimiter(now.Stages.Limiter, snap.LeftLimiter) && !TxStagesPicture.SameLimiter(now.Stages.Limiter, snap.TransmitLimiter);
            return new TxOwned(spectral, neural, gate, eq, deEsser, order, compressor, limiter);
        }

        // ── Which receive changes are the layer's (#1150) ──

        private int _ownRxDepth;

        /// <summary>
        /// The receive twin of <see cref="OwnTx"/> (#1150), opened around every
        /// receive road the layer can take: null (nothing to do) unless the
        /// source is the layer's and its picture is live; nested writes share
        /// the outermost. Receive writes are never held for an unkey, so the two
        /// slots are read before the write and after it, here, in order.
        /// </summary>
        private IDisposable? OwnRx(string source)
        {
            if (_ownRxDepth > 0 || source != LayerSource || _live is not { } snap || _pipeline() is not { } p) return null;
            return new LayerRxWrite(this, snap, p);
        }

        private sealed class LayerRxWrite : IDisposable
        {
            private readonly NoiseController _owner;
            private readonly NoiseSnapshot _snap;
            private readonly RxAudioPipeline _pipeline;
            private readonly ReceiveSlotPicture _spectral, _neural;
            private readonly ParametricEqSetup _eq;
            private bool _done;

            public LayerRxWrite(NoiseController owner, NoiseSnapshot snap, RxAudioPipeline pipeline)
            {
                _owner = owner; _snap = snap; _pipeline = pipeline;
                _spectral = ReadRx(pipeline.SpectralSlot);
                _neural = ReadRx(pipeline.NeuralSlot);
                _eq = pipeline.EqualizerSetup;
                owner._ownRxDepth++;
            }

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _owner._ownRxDepth--;
                // The layer closed, or the pipeline changed, during the write: nothing of this picture to record.
                if (!ReferenceEquals(_owner._live, _snap) || !ReferenceEquals(_owner._pipeline(), _pipeline)) return;
                NoteLayerLeftReceive(_snap, DenoiseFamily.Spectral, _spectral, ReadRx(_pipeline.SpectralSlot));
                NoteLayerLeftReceive(_snap, DenoiseFamily.Neural, _neural, ReadRx(_pipeline.NeuralSlot));
                NoteLayerLeftReceiveEq(_snap, _eq, _pipeline.EqualizerSetup);
            }
        }

        private static ReceiveSlotPicture ReadRx(PcDenoiseSlot slot) => new(slot.Enabled, slot.ChosenEngineId, slot.ExportSettings());

        /// <summary>A slot picture as comparable text: on, engine, and the settings as <see cref="Describe"/> reads them (a default-only entry is no entry).</summary>
        private static string DescribeRx(ReceiveSlotPicture s) => (s.On ? "on" : "off") + "|" + s.Engine + "|" + Describe(s.Settings);

        private static ReceiveSlotPicture PictureOf(NoiseSnapshot snap, DenoiseFamily family) => family == DenoiseFamily.Spectral
            ? new(snap.Spectral.On, snap.Spectral.Engine, snap.SpectralSettings)
            : new(snap.Neural.On, snap.Neural.Engine, snap.NeuralSettings);

        /// <summary>
        /// After one of the layer's receive writes: a slot it changed is the
        /// layer's now, as it left it. A slot that had already moved since the
        /// layer last left it (or since the picture) was moved by someone else in
        /// between: that change is kept, so it becomes what Escape puts the slot
        /// back to (transmit's <see cref="NoteLayerLeft"/>, the same rule).
        /// </summary>
        private static void NoteLayerLeftReceive(NoiseSnapshot snap, DenoiseFamily family, ReceiveSlotPicture before, ReceiveSlotPicture now)
        {
            if (DescribeRx(now) == DescribeRx(before)) return;
            bool spectral = family == DenoiseFamily.Spectral;
            var mark = (spectral ? snap.LeftReceiveSpectral : snap.LeftReceiveNeural) ?? PictureOf(snap, family);
            if (DescribeRx(before) != DescribeRx(mark))
            {
                if (spectral) { snap.Spectral = (before.On, before.Engine); snap.SpectralSettings = before.Settings; }
                else { snap.Neural = (before.On, before.Engine); snap.NeuralSettings = before.Settings; }
            }
            if (spectral) snap.LeftReceiveSpectral = now; else snap.LeftReceiveNeural = now;
        }

        /// <summary>The receive equaliser's twin of <see cref="NoteLayerLeftReceive"/> (Sprint 49 Track I, repair 1, B1).</summary>
        private static void NoteLayerLeftReceiveEq(NoiseSnapshot snap, ParametricEqSetup before, ParametricEqSetup now)
        {
            if (snap.ReceiveEq == null || SameEq(now, before)) return;
            if (!SameEq(before, snap.LeftReceiveEq ?? snap.ReceiveEq)) snap.ReceiveEq = before.Clone();
            snap.LeftReceiveEq = now.Clone();
        }

        /// <summary>Whether Escape puts the receive equaliser back: the layer changed it, it still reads as the layer left it, and it differs from the picture.</summary>
        private static bool LayerOwnsReceiveEq(NoiseSnapshot snap, RxAudioPipeline p)
        {
            if (snap.LeftReceiveEq == null || snap.ReceiveEq == null) return false;
            var now = p.EqualizerSetup;
            return SameEq(now, snap.LeftReceiveEq) && !SameEq(now, snap.ReceiveEq);
        }

        /// <summary>
        /// Whether a receive slot stands, after Escape, different from the layer's
        /// entry because of a change made elsewhere (F4; repair 2, R1): an owned
        /// slot as its restore target, which differs from the entry only when it
        /// was rebased onto such a change, and an unowned one as it reads now.
        /// Compared with the never-rebased entry picture.
        /// </summary>
        private static bool KeptOutsideReceive(NoiseSnapshot snap, DenoiseFamily family, bool owned, PcDenoiseSlot slot)
        {
            var target = PictureOf(snap, family);
            var entry = (family == DenoiseFamily.Spectral ? snap.EntrySpectral : snap.EntryNeural) ?? target;
            return DescribeRx(owned ? target : ReadRx(slot)) != DescribeRx(entry);
        }

        /// <summary>Whether Escape puts a receive slot back: the layer changed it, it still reads as the layer left it, and it differs from the picture.</summary>
        private static bool LayerOwnsReceive(NoiseSnapshot snap, DenoiseFamily family, PcDenoiseSlot slot)
        {
            var left = family == DenoiseFamily.Spectral ? snap.LeftReceiveSpectral : snap.LeftReceiveNeural;
            if (left == null) return false;
            string now = DescribeRx(ReadRx(slot));
            return now == DescribeRx(left) && now != DescribeRx(PictureOf(snap, family));
        }

        /// <summary>When the transmit part of the last <see cref="RestoreSnapshot"/> takes effect (Now when it had none).</summary>
        public TxChangeTiming LastRestoreTiming { get; private set; } = TxChangeTiming.Now;

        /// <summary>The layer's closing sentence after Escape, with the at-unkey sentence when the transmit part waits (repair round 1, item 4).</summary>
        public string WithRestoreTiming(string closing) =>
            LastRestoreTiming == TxChangeTiming.AtUnkey ? closing + " " + Lexicon.Get("audio.denoise.tx.at_unkey") : closing;

        /// <summary>
        /// Whether the last <see cref="RestoreSnapshot"/> left standing a change
        /// made outside the layer (repair 1, F4): a receive or transmit computer
        /// slot, or the transmit gate, that after Escape differs from the layer's
        /// entry because of a change the layer did not make. Either the layer
        /// does not own the part, or it does and its restore target was rebased
        /// onto such a change (repair 2, R1). The radio's own switches and levels
        /// are not counted here; see <see cref="LastRestoreLeftRadioDifferences"/>.
        /// </summary>
        public bool LastRestoreKeptOutsideChanges { get; private set; }

        /// <summary>
        /// Whether, after the last <see cref="RestoreSnapshot"/>, a radio switch
        /// or level on the picture's slice still differs from the layer's picture
        /// (repair 2, R2). Its cause is not established: another client, the
        /// operator elsewhere, or the radio itself moving a sibling when the layer
        /// changed something are all possible, so it is a separate fact from
        /// <see cref="LastRestoreKeptOutsideChanges"/> and never called a change
        /// made elsewhere. What it does establish is that something differs from
        /// the layer's entry, so the closing sentence may claim neither that
        /// nothing changed nor that everything was reverted.
        /// </summary>
        public bool LastRestoreLeftRadioDifferences { get; private set; }

        /// <summary>
        /// The lexicon key of the layer's closing sentence after Escape, from the
        /// ownership facts: whether the layer put anything of its own back
        /// (<paramref name="restoredSomething"/>, <see cref="RestoreSnapshot"/>'s
        /// answer) and whether anything that differs from the layer's entry was
        /// left standing: a change made elsewhere
        /// (<see cref="LastRestoreKeptOutsideChanges"/>), or a radio value for a
        /// reason the snapshot cannot establish
        /// (<see cref="LastRestoreLeftRadioDifferences"/>). Either way the two
        /// "kept" keys are chosen, so their words must not say who or what made
        /// the change (repair 2, R2).
        /// </summary>
        public string RestoreClosingKey(bool restoredSomething) => (restoredSomething, LastRestoreKeptOutsideChanges || LastRestoreLeftRadioDifferences) switch
        {
            (true, false) => "audio.noise_layer.restored",
            (true, true) => "audio.noise_layer.restored_kept_elsewhere",
            (false, true) => "audio.noise_layer.restored_nothing_kept_elsewhere",
            _ => "audio.noise_layer.restored_nothing",
        };

        /// <summary>The layer closed: nothing is live to put back any more.</summary>
        public void EndSnapshot() => _live = null;

        /// <summary>
        /// Put back what the layer changed. Radio switches and levels only
        /// where the layer itself wrote them on the slice the picture was read
        /// on, and only where they now differ from it: nothing is written to
        /// a slice the operator jumped to without touching, and an unreported
        /// level is never written back unasked. The computer slots, receive and
        /// transmit, only where the layer itself changed them and they still
        /// read as it left them (#1150 for receive). Favourites and per-mode
        /// memory are app-wide and put back wherever they differ.
        /// Returns whether anything was put back.
        /// </summary>
        public bool RestoreSnapshot(NoiseSnapshot snap, string source)
        {
            bool changed = false;
            LastRestoreKeptOutsideChanges = false;
            LastRestoreLeftRadioDifferences = false;
            if (ReferenceEquals(_live, snap)) _live = null;
            // Escape puts the radio's switches back too, so a queued sibling
            // observation is obsolete whatever else is put back.
            RetireSiblingNotice("the noise settings were put back");
            if (_radio() is { } r && ReferenceEquals(r.SliceIdentity, snap.SliceIdentity))
            {
                foreach (var (id, on) in snap.RadioSwitches)
                {
                    var e = _catalog.Find(id)!;
                    if (!snap.TouchedSwitches.Contains(id) || IsOn(id) == on) continue;
                    if (RadioRefusal(e, r, out _) != null) continue; // a refused write never happened
                    WriteRadioSwitch(e, r, on, source, byApp: false);
                    changed = true;
                }
                foreach (var (id, level) in snap.RadioLevels)
                {
                    var e = _catalog.Find(id)!;
                    if (!snap.TouchedLevels.Contains(id) || ReadRadioLevel(e) == level) continue;
                    if (RadioRefusal(e, r, out _) != null) continue;
                    var lever = Levers(id, false).First();
                    Write(lever, e, new EngineSettingValue(level), source);
                    changed = true;
                }
                // Repair 2, R2: anything on this slice that still differs from the
                // picture after the put-back, touched or not (a refused put-back
                // leaves its value too). A level that reads as unreported now is
                // not evidence either way.
                LastRestoreLeftRadioDifferences =
                    snap.RadioSwitches.Any(kv => IsOn(kv.Key) != kv.Value)
                    || snap.RadioLevels.Any(kv => ReadRadioLevel(_catalog.Find(kv.Key)!) is int now && now != kv.Value);
            }
            // Memory first, so a model the mode's rule swaps while the slots
            // are put back is owed back on leaving CW (B4) and not undone.
            if (_config() is { } c)
            {
                if (!c.NoiseFavourites.Select(f => (f.EngineId, f.Model, f.Name)).SequenceEqual(snap.Favourites.Select(f => (f.EngineId, f.Model, f.Name))))
                {
                    c.NoiseFavourites = snap.Favourites.ToList();
                    changed = true;
                }
                if (snap.MemoryXml.Length > 0 && SerializeMemory(c.NoiseModeMemory) != snap.MemoryXml
                    && DeserializeMemory(snap.MemoryXml) is { } memory)
                {
                    // The group the live settings are in is where the radio is
                    // now, not where it was when the layer opened (B6).
                    memory.LastGroup = c.NoiseModeMemory.LastGroup;
                    c.NoiseModeMemory = memory;
                }
            }
            if (_pipeline() is { } p)
            {
                RetireHelperNotice("the noise settings were put back");
                // Only what the layer itself changed (#1150), as transmit does
                // below: a slot changed from Home, the menu, a preset or a mode
                // group while the layer was open is kept.
                bool spectralOwned = LayerOwnsReceive(snap, DenoiseFamily.Spectral, p.SpectralSlot);
                bool neuralOwned = LayerOwnsReceive(snap, DenoiseFamily.Neural, p.NeuralSlot);
                if (spectralOwned) changed |= RestoreSlot(p.SpectralSlot, snap.Spectral, snap.SpectralSettings, source);
                if (neuralOwned) changed |= RestoreSlot(p.NeuralSlot, snap.Neural, snap.NeuralSettings, source);
                // A kept change is the current mode group's again: the per-mode
                // memory was just put back to the picture above, and the road
                // that made the kept change had remembered it there.
                // Repair 2, R1: an owned slot whose target was rebased is put back
                // onto a change made elsewhere, so it counts as kept, and is
                // remembered for the same reason.
                // Sprint 49 Track I, repair 1 (B1): the receive equaliser, by the same rule.
                bool eqOwned = LayerOwnsReceiveEq(snap, p);
                if (eqOwned)
                {
                    p.SetEqualizer(snap.ReceiveEq!);
                    NoiseSettingsJournal.Record(NoiseJournalKind.Switch, source, "equalizer", "", "layer changes", "put back");
                    changed = true;
                }
                bool eqKept = snap.ReceiveEq != null && !SameEq(eqOwned ? snap.ReceiveEq : p.EqualizerSetup, snap.EntryReceiveEq ?? snap.ReceiveEq);
                bool kept = KeptOutsideReceive(snap, DenoiseFamily.Spectral, spectralOwned, p.SpectralSlot)
                    || KeptOutsideReceive(snap, DenoiseFamily.Neural, neuralOwned, p.NeuralSlot)
                    || eqKept;
                if (kept) RememberModeState();
                LastRestoreKeptOutsideChanges |= kept;
            }
            // The transmit part (Track H2; Escape ownership round): only what the
            // layer itself changed. First its own changes still held for the
            // unkey are withdrawn, so a preset or setup asked for while keyed
            // never lands after Escape; everyone else's held changes stay, in
            // order. Its held records run as they are withdrawn (round 2), so a
            // change that ran before an over cut its record off is still known;
            // only a withdrawn CHANGE counts as something reverted. Then each part the layer changed and that still reads as the
            // layer left it goes back to the picture: the two slots and the gate,
            // through one change held while keyed (repair round 1, item 4), which
            // asks again when it runs. A part someone else changed is kept.
            LastRestoreTiming = TxChangeTiming.Now;
            if (_txChain() is { } chain)
            {
                if (chain.CancelHeld(snap) > 0)
                {
                    NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "transmit", "", "layer changes waiting for the unkey", "withdrawn");
                    changed = true;
                }
                var txNow = ReadTx(chain);
                var owned = LayerOwnedTx(snap, txNow);
                LastRestoreKeptOutsideChanges |= KeptOutsideTx(snap, txNow, owned);
                if (owned.Any)
                {
                    if (owned.Spectral || owned.Neural) { CancelTransmitSettle(DenoiseFamily.Spectral); CancelTransmitSettle(DenoiseFamily.Neural); }
                    var spectralBack = snap.TransmitSpectral?.Clone();
                    var neuralBack = snap.TransmitNeural?.Clone();
                    var gateBack = snap.TransmitGate?.Clone();
                    var eqBack = snap.TransmitEq?.Clone();
                    var deEsserBack = snap.TransmitDeEsser?.Clone();
                    var orderBack = snap.TransmitOrder == null ? null : (string[])snap.TransmitOrder.Clone();
                    var compressorBack = snap.TransmitCompressor?.Clone();
                    var limiterBack = snap.TransmitLimiter?.Clone();
                    LastRestoreTiming = chain.RunOrDefer(() =>
                    {
                        var current = ReadTx(chain);
                        var still = LayerOwnedTx(snap, current);
                        if (still.Spectral) chain.ApplySlotNow(DenoiseFamily.Spectral, spectralBack);
                        if (still.Neural) chain.ApplySlotNow(DenoiseFamily.Neural, neuralBack);
                        // Sprint 49 Track I, repair 1 (B1): the equaliser, the de-esser and
                        // the order go back with the gate as ONE staged record; a gate the
                        // layer did not change stays as it is (null keeps it).
                        // Repair 2: each OWNED stage goes back to its target and each
                        // stage the layer does not own stays exactly as it reads now,
                        // published together as one staged record, asked again here
                        // when a held put-back runs.
                        // Track CL, repair 1 (B4): the compressor and the limiter the
                        // same way, held as intents just before the stages are staged,
                        // so the ONE record carries them (the road a whole setup takes).
                        if (still.AnyStage)
                            TransmitGate.StageDynamics(
                                still.Compressor && compressorBack != null ? compressorBack : current.Stages.Compressor,
                                still.Limiter && limiterBack != null ? limiterBack : current.Stages.Limiter);
                        if (still.AnyStage)
                            TransmitGate.ApplyStagesNow(
                                still.Eq && eqBack != null ? eqBack : current.Stages.Eq,
                                still.DeEsser && deEsserBack != null ? deEsserBack : current.Stages.DeEsser,
                                still.Order && orderBack != null ? orderBack : current.Stages.Order,
                                still.Gate ? gateBack : null);
                        else if (still.Gate && gateBack != null) TransmitGate.ApplyNow(gateBack);
                    }).Timing;
                    NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "transmit", "", "layer changes", "put back");
                    changed = true;
                }
            }
            ClearListening();
            ForgetZero();
            if (changed) _persist();
            return changed;
        }

        private bool RestoreSlot(PcDenoiseSlot slot, (bool On, string Engine) want, Radios.DenoiseEngineSettings[] settings, string source)
        {
            bool changed = false;
            var now = slot.ExportSettings();
            var back = new List<Radios.DenoiseEngineSettings>();
            if (Describe(now) != Describe(settings))
            {
                // An engine first touched inside the layer has no entry in the
                // snapshot: it goes back to its defaults (strength and no values).
                back.AddRange(settings);
                foreach (var extra in now.Where(n => settings.All(s => s.EngineId != n.EngineId)))
                    back.Add(new Radios.DenoiseEngineSettings { EngineId = extra.EngineId, Direction = extra.Direction, Strength = DefaultStrength(extra.EngineId) });
                changed = true;
            }
            string target = want.Engine.Length > 0 ? want.Engine : slot.ChosenEngineId;
            // The mode's rule (B4), then the settings and the choice; an engine
            // still chosen rebuilds when a model or estimator went back (B3).
            if (slot.RestoreAndSelect(AdmitForMode(slot, back, target, source), target)) changed = true;
            if (slot.Enabled != want.On) { slot.Enabled = want.On; changed = true; }
            return changed;
        }

        // An entry with no values and its engine's default strength is the
        // same as no entry: reading a strength creates one, and reading is not
        // a change Escape should report.
        private static string Describe(Radios.DenoiseEngineSettings[] settings) =>
            string.Join("|", settings.Where(s => s.Values.Count > 0 || Math.Abs(s.Strength - DefaultStrength(s.EngineId)) > 1e-6).OrderBy(s => s.EngineId, StringComparer.Ordinal).Select(s => s.EngineId + ":" + s.Strength.ToString("R") + ":" +
                string.Join(",", s.Values.OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => v.Name + "=" + (v.Choice ?? v.Number.ToString("R"))))));

        private static string SerializeMemory(NoiseModeMemoryConfig memory)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(NoiseModeMemoryConfig));
            using var writer = new System.IO.StringWriter();
            serializer.Serialize(writer, memory);
            return writer.ToString();
        }

        private static NoiseModeMemoryConfig? DeserializeMemory(string xml)
        {
            try
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(NoiseModeMemoryConfig));
                using var reader = new System.IO.StringReader(xml);
                return serializer.Deserialize(reader) as NoiseModeMemoryConfig;
            }
            catch (InvalidOperationException) { return null; }
        }
    }
}

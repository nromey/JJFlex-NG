using System;
using System.Collections.Generic;
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

        /// <summary>Turn a slot on or off (item B: at the unkey when keyed).</summary>
        public TxChangeResult SetEnabled(DenoiseFamily family, bool on)
        {
            Volatile.Write(ref _wantEnabled[(int)family], on ? 1 : 0);
            if (on) SlotOf(family).RememberUse();
            return Timing();
        }

        /// <summary>Choose a slot's engine (item B: it starts at the unkey when keyed). Refused for an engine that is not this slot's family, is receive only, or is not in this build.</summary>
        public TxChangeResult ChooseEngine(DenoiseFamily family, string engineId)
        {
            if (!SlotOf(family).Select(engineId, out var refusal)) return new(TxChangeTiming.Refused, refusal);
            return Timing();
        }

        /// <summary>
        /// Change one engine setting. A setting that needs a new instance (a
        /// model, a delay) waits for the unkey like an engine change; any other
        /// setting is live, like strength.
        /// </summary>
        public TxChangeResult SetSetting(string engineId, string name, EngineSettingValue value)
        {
            var slot = SlotFor(engineId);
            if (slot == null) return new(TxChangeTiming.Refused, "wrong-direction");
            var descriptor = slot.SettingsFor(engineId).FirstOrDefault(s => s.Name == name);
            if (descriptor == null) return new(TxChangeTiming.Refused, "unknown-setting");
            if (!slot.TrySetSetting(engineId, name, value)) return new(TxChangeTiming.Refused, "refused-value");
            return descriptor.NeedsRebuild && slot.ChosenEngineId == engineId ? Timing() : TxChangeResult.Now;
        }

        /// <summary>A transmit engine's strength (stored wet fraction; surfaces show it as a floor through <see cref="NoiseFloor"/>). Live while keyed.</summary>
        public void SetStrength(string engineId, float strength) => SlotFor(engineId)?.SetStrength(engineId, strength);

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
        /// Put a setup's two slots in place: every engine's stored values, the
        /// chosen engines, on and off. Like any change, audible only from the
        /// next unkeyed buffer. An engine this build cannot run stays chosen as
        /// the operator's intent, the way a receive slot keeps one.
        /// </summary>
        public TxChangeResult ApplySlots(TxNoiseSlotSetup? spectral, TxNoiseSlotSetup? neural)
        {
            Apply(DenoiseFamily.Spectral, spectral);
            Apply(DenoiseFamily.Neural, neural);
            return Timing();
        }

        private void Apply(DenoiseFamily family, TxNoiseSlotSetup? want)
        {
            if (want == null) return;
            var slot = SlotOf(family);
            var records = (want.Engines ?? new()).Where(e => e != null)
                .Select(e => new DenoiseEngineSettings { EngineId = e.EngineId, Direction = DenoiseDirection.Transmit, Strength = e.Strength, Values = e.Values })
                .ToList();
            slot.RestoreAndSelect(records, want.EngineId ?? "");
            if (!string.IsNullOrEmpty(want.EngineId)) slot.RememberUse();
            Volatile.Write(ref _wantEnabled[(int)family], want.Enabled ? 1 : 0);
        }

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
            if (!keyed)
            {
                // The only place on and off become audible (item B).
                SpectralSlot.Enabled = WantsEnabled(DenoiseFamily.Spectral);
                NeuralSlot.Enabled = WantsEnabled(DenoiseFamily.Neural);
            }
            if (sampleRate != SampleRate || count < 2) return;
            int frames = count / 2;
            if (_mono.Length != frames) _mono = new float[frames];
            var mono = _mono;
            for (int f = 0, i = 0; f < frames; f++, i += 2) mono[f] = (interleaved[i] + interleaved[i + 1]) * 0.5f;
            SpectralSlot.Process(mono, true, true, false, null);
            NeuralSlot.Process(mono, true, true, false, null);
            bool processed = SpectralSlot.LastBufferProcessed || NeuralSlot.LastBufferProcessed;
            if (!processed) return;
            for (int f = 0, i = 0; f < frames; f++, i += 2) { interleaved[i] = mono[f]; interleaved[i + 1] = mono[f]; }
            AppliedLatencySamples = SpectralSlot.LastAppliedLatencySamples + NeuralSlot.LastAppliedLatencySamples;
        }
    }
}

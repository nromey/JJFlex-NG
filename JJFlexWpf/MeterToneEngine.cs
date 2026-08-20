using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Flex.Smoothlake.FlexLib;
using NAudio.Wave.SampleProviders;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Real-time meter sonification engine. Subscribes to FlexBase meter events
    /// and drives VoicedToneSampleProvider instances to render meter values as
    /// continuous pitched tones.
    ///
    /// Track D2 rework: each slot wraps a <see cref="MeterDefinition"/> —
    /// source plus range plus voice — and the synthesis is data-driven through
    /// <see cref="MeterVoice"/>. The grammar: timbre identifies the meter,
    /// pitch carries its value, pan enhances but is never load-bearing.
    /// Includes a Peak Watcher for TX safety alerts and on-demand speech
    /// readout of all active meters.
    /// </summary>
    public static class MeterToneEngine
    {
        private static FlexBase? _rig;
        private static bool _initialized;

        // Per-slot throttle interval: 100ms = 10 Hz update rate. Throttling is
        // per slot (not global) so four active meters each update at 10 Hz
        // instead of sharing one 10 Hz budget between them.
        private const long ThrottleIntervalTicks = TimeSpan.TicksPerMillisecond * 100;

        /// <summary>Global kill switch for all meter tones.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!value)
                {
                    foreach (var slot in Slots)
                        slot.ToneProvider.Active = false;
                }
                else if (_rig != null)
                {
                    // Proactively activate slots so tones start immediately
                    // rather than waiting for the next OnMeterChanged event.
                    bool tx = _rig.Transmit;
                    foreach (var slot in Slots)
                    {
                        if (!slot.Enabled) continue;
                        slot.ToneProvider.Active = ShouldSound(slot.Definition.Activation, tx);
                    }
                }
            }
        }
        private static bool _enabled;

        /// <summary>
        /// Flip the meter tones on or off, announcing and earconning the new
        /// state. The ONE place that vocabulary lives, so the hotkey and the
        /// menu cannot drift apart in what they say.
        /// </summary>
        /// <remarks>
        /// Separated from the meters PANEL in Sprint 32 Track B (#126). Showing
        /// the panel and starting a noise used to be a single action; they are
        /// two now, and this is the noise half.
        /// </remarks>
        public static void ToggleEnabled()
        {
            Enabled = !Enabled;
            string state = Enabled ? "on" : "off";
            ScreenReaderOutput.Speak($"Meter tones {state}", VerbosityLevel.Terse, true);
            if (Enabled) EarconPlayer.FeatureOnTone();
            else EarconPlayer.FeatureOffTone();
        }

        /// <summary>Whether speech readout of meter values is enabled.</summary>
        public static bool SpeechEnabled { get; set; } = true;

        /// <summary>Speech interval in seconds (1-10). How often batched meter values are spoken.</summary>
        public static int SpeechIntervalSeconds { get; set; } = 3;

        /// <summary>Whether the speech timer is actively speaking meter values.</summary>
        public static bool SpeechTimerActive
        {
            get => _speechTimerActive;
            set
            {
                _speechTimerActive = value;
                if (value) StartSpeechTimer();
                else StopSpeechTimer();
            }
        }
        private static bool _speechTimerActive;
        private static System.Windows.Threading.DispatcherTimer? _speechTimer;

        /// <summary>When true, enables default meters when TxTune activates.</summary>
        public static bool AutoEnableOnTune { get; set; }

        /// <summary>Master volume multiplier for all meter tones (0.0–1.0).</summary>
        public static float MasterVolume { get; set; } = 0.5f;

        /// <summary>The meter tone slots.</summary>
        public static List<MeterSlot> Slots { get; } = new();

        /// <summary>
        /// The slot collection changed: one was added, one was removed, or the
        /// whole set was replaced by a preset or by a config load.
        /// </summary>
        /// <remarks>
        /// This event is the fix for #129. The panel used to build its controls
        /// once in its constructor and never again, so a slot added afterwards
        /// existed in this list with no controls anywhere — Noel added a slot,
        /// was told he had slot 5, and could see nothing. Anything that renders
        /// slots must treat this list as LIVE and rebind here rather than
        /// snapshotting it. Raised on whichever thread made the change, which
        /// for a preset applied during config load is not the UI thread.
        /// </remarks>
        public static event EventHandler? SlotsChanged;

        /// <summary>
        /// The radio the engine is attached to, or null. Exposed so a view can
        /// reach <see cref="FlexBase.MeterInventory"/> without being handed a
        /// rig separately and without going stale when the radio changes.
        /// </summary>
        public static FlexBase? Rig => _rig;

        /// <summary>
        /// Everything the connected radio says it can measure, or null when no
        /// radio is attached. Never sample this once — bind to the inventory's
        /// own InventoryChanged, because the list grows during registration.
        /// </summary>
        public static MeterInventory? Inventory => _rig?.MeterInventory;

        /// <summary>A radio was attached or detached.</summary>
        public static event EventHandler? RadioChanged;

        private static void RaiseSlotsChanged() =>
            SlotsChanged?.Invoke(null, EventArgs.Empty);

        /// <summary>Current preset name.</summary>
        public static string CurrentPreset { get; private set; } = "RX Monitor";

        private static readonly string[] PresetNames = { "RX Monitor", "TX Monitor", "Full Monitor" };
        private static int _presetIndex;

        // Peak Watcher state
        public static bool PeakWatcherEnabled { get; set; } = true;
        private static long _lastPeakWarningTicks;
        private static long _alcHighStartTicks;
        private static bool _alcSustainedWarning;
        private const long PeakCooldownTicks = TimeSpan.TicksPerSecond * 10;
        private const long AlcSustainedThresholdTicks = TimeSpan.TicksPerSecond * 3;
        private const float AlcWarningThreshold = 0.5f;
        private const float AlcCriticalThreshold = 0.8f;

        /// <summary>
        /// Initialize the engine and create the default tone slots.
        /// Call after EarconPlayer.Initialize().
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            for (int i = 0; i < 4; i++)
            {
                var slot = new MeterSlot();
                Slots.Add(slot);
                EarconPlayer.RegisterContinuousStereo(slot.ToneProvider);
            }

            ApplyPreset("RX Monitor");
            _initialized = true;
            RaiseSlotsChanged();
        }

        /// <summary>
        /// Wire the engine to a connected radio's meter data.
        /// </summary>
        public static void AttachToRadio(FlexBase rig)
        {
            if (_rig != null)
                DetachFromRadio();

            _rig = rig;
            // The identity-preserving feed (Sprint 32 Track A): every reading of
            // every meter, with the meter itself. The old eight-value event is
            // gone, and with it the ceiling of eight choosable meters.
            _rig.MeterData += OnMeterData;
            _rig.TransmitChange += OnTransmitChanged;
            RadioChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Disconnect from the current radio.</summary>
        public static void DetachFromRadio()
        {
            if (_rig != null)
            {
                _rig.MeterData -= OnMeterData;
                _rig.TransmitChange -= OnTransmitChanged;
                _rig = null;
                RadioChanged?.Invoke(null, EventArgs.Empty);
            }
            // Silence all tones
            foreach (var slot in Slots)
                slot.ToneProvider.Active = false;
        }

        /// <summary>Shut down the engine and clean up.</summary>
        public static void Shutdown()
        {
            DetachFromRadio();
            EarconPlayer.UnregisterAllContinuousTones();
            Slots.Clear();
            _initialized = false;
            RaiseSlotsChanged();
        }

        #region Presets

        /// <summary>
        /// Apply a named preset configuration. Replaces the working set — the
        /// historical semantics, kept. Voice assignments per meter are chosen
        /// so identities differ on timbre AND modulation, never on pan alone:
        /// pan is an enhancement, lost entirely to mono listeners and
        /// asymmetric hearing loss.
        /// </summary>
        public static void ApplyPreset(string presetName)
        {
            // Silence all first
            foreach (var slot in Slots)
            {
                slot.Enabled = false;
                slot.ToneProvider.Active = false;
            }

            CurrentPreset = presetName;
            _presetIndex = Array.IndexOf(PresetNames, presetName);
            if (_presetIndex < 0) _presetIndex = 0;

            switch (presetName)
            {
                case "RX Monitor":
                    ConfigureSlot(0, "SMeter", true, 0.6f, 0f, 200, 1200, "Pure");
                    ConfigureSlot(1, "SWR", false, 0.5f, 0f, 200, 1200, "Trill");
                    ConfigureSlot(2, "ALC", false, 0.5f, 0f, 300, 1500, "Raspy");
                    ConfigureSlot(3, "Mic", false, 0.4f, 0f, 350, 800, "Hollow");
                    break;
                case "TX Monitor":
                    ConfigureSlot(0, "ALC", true, 0.5f, -0.5f, 300, 1500, "Raspy");
                    ConfigureSlot(1, "Mic", true, 0.4f, 0.5f, 350, 800, "Hollow");
                    ConfigureSlot(2, "Power", true, 0.4f, 0f, 200, 1000, "Organ");
                    ConfigureSlot(3, "SWR", true, 0.5f, 0f, 200, 1200, "Trill");
                    break;
                case "Full Monitor":
                    ConfigureSlot(0, "SMeter", true, 0.5f, -0.5f, 200, 1200, "Pure");
                    ConfigureSlot(1, "ALC", true, 0.4f, 0.5f, 300, 1500, "Raspy");
                    ConfigureSlot(2, "SWR", true, 0.5f, 0f, 200, 1200, "Trill");
                    ConfigureSlot(3, "Mic", true, 0.3f, 0f, 350, 800, "Hollow");
                    break;
            }

            RaiseSlotsChanged();
        }

        /// <summary>Cycle to the next preset.</summary>
        public static void CyclePreset()
        {
            _presetIndex = (_presetIndex + 1) % PresetNames.Length;
            ApplyPreset(PresetNames[_presetIndex]);
        }

        private static void ConfigureSlot(int index, string sourceKey, bool enabled,
            float volume, float pan, float pitchLow, float pitchHigh, string voiceName)
        {
            if (index >= Slots.Count) return;
            var slot = Slots[index];
            var def = LegacyMeterCatalog.CreateDefinition(sourceKey);
            def.Enabled = enabled;
            def.Volume = volume;
            def.Pan = pan;
            def.PitchLowHz = pitchLow;
            def.PitchHighHz = pitchHigh;
            def.VoiceName = voiceName;
            slot.SetDefinition(def);
        }

        #endregion

        #region Definition round-trip (persistence)

        /// <summary>
        /// Replace the working set with saved meter definitions (the one meter
        /// list from AudioOutputConfig.Meters). Slots are created or removed
        /// to match, capped at <see cref="MaxSlots"/>.
        /// </summary>
        public static void LoadDefinitions(IEnumerable<MeterDefinition> definitions)
        {
            var defs = definitions.Take(MaxSlots).Select(d => d.Clone()).ToList();
            if (defs.Count == 0) return;

            // Shrink or grow the slot list to fit.
            while (Slots.Count > Math.Max(defs.Count, 1))
            {
                var last = Slots[^1];
                last.ToneProvider.Active = false;
                EarconPlayer.UnregisterContinuousTone(last.ToneProvider);
                Slots.RemoveAt(Slots.Count - 1);
            }
            while (Slots.Count < defs.Count)
            {
                var slot = new MeterSlot();
                Slots.Add(slot);
                EarconPlayer.RegisterContinuousStereo(slot.ToneProvider);
            }

            for (int i = 0; i < defs.Count; i++)
                Slots[i].SetDefinition(defs[i]);

            RaiseSlotsChanged();
        }

        /// <summary>Snapshot the working set for persistence.</summary>
        public static List<MeterDefinition> ExportDefinitions() =>
            Slots.Select(s => s.Definition.Clone()).ToList();

        #endregion

        #region Dynamic Slot Management

        /// <summary>Maximum number of meter tone slots.</summary>
        public const int MaxSlots = 8;

        /// <summary>Add a new meter slot. Returns the slot, or null if at max.</summary>
        public static MeterSlot? AddSlot()
        {
            if (Slots.Count >= MaxSlots) return null;
            var slot = new MeterSlot();
            Slots.Add(slot);
            EarconPlayer.RegisterContinuousStereo(slot.ToneProvider);
            RaiseSlotsChanged();
            return slot;
        }

        /// <summary>Remove a slot by index. Cannot remove if only 1 slot remains.</summary>
        public static bool RemoveSlot(int index)
        {
            if (Slots.Count <= 1 || index < 0 || index >= Slots.Count) return false;
            var slot = Slots[index];
            slot.ToneProvider.Active = false;
            EarconPlayer.UnregisterContinuousTone(slot.ToneProvider);
            Slots.RemoveAt(index);
            RaiseSlotsChanged();
            return true;
        }

        /// <summary>
        /// Tell every view that a slot's CONTENTS changed — its source, name or
        /// voice — as opposed to the set of slots changing. Same event, because
        /// a view that rebuilds from the live list handles both identically,
        /// and one signal is one thing to get wrong.
        /// </summary>
        public static void NotifySlotContentChanged() => RaiseSlotsChanged();

        #endregion

        #region Auto-Enable on Tune

        private static bool _wasEnabledBeforeTune;

        /// <summary>
        /// Call when TxTune is toggled on. If AutoEnableOnTune is set,
        /// enables meters with current config.
        /// </summary>
        public static void OnTuneStarted()
        {
            if (!AutoEnableOnTune) return;
            _wasEnabledBeforeTune = _enabled;
            if (!_enabled) Enabled = true;
        }

        /// <summary>
        /// Call when TxTune is toggled off. Restores previous meter state.
        /// </summary>
        public static void OnTuneStopped()
        {
            if (!AutoEnableOnTune) return;
            if (!_wasEnabledBeforeTune) Enabled = false;
        }

        #endregion

        #region Speech Timer

        private static void StartSpeechTimer()
        {
            if (_speechTimer != null) return;
            _speechTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(SpeechIntervalSeconds)
            };
            _speechTimer.Tick += (s, e) =>
            {
                if (!_speechTimerActive || !SpeechEnabled) return;
                SpeakMeters();
            };
            _speechTimer.Start();
        }

        private static void StopSpeechTimer()
        {
            _speechTimer?.Stop();
            _speechTimer = null;
        }

        /// <summary>Update the speech timer interval (call when SpeechIntervalSeconds changes).</summary>
        public static void UpdateSpeechTimerInterval()
        {
            if (_speechTimer != null)
            {
                _speechTimer.Interval = TimeSpan.FromSeconds(SpeechIntervalSeconds);
            }
        }

        #endregion

        #region Meter Event Handling

        /// <summary>
        /// The meter the Peak Watcher guards. Historically this was
        /// <c>MeterType.ALC</c>, which was fed by the radio's HWALC meter — the
        /// external-amplifier ALC line. Naming it here keeps the behaviour
        /// bit-identical across the move off the eight-value event.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The live question this comment used to pose is now answered, and
        /// the answer is that this watcher does not do what its name promises.
        /// Measured on the bench 8600, 2026-08-20 (Sprint 33 Track D).</b>
        /// </para>
        /// <para>
        /// The radio describes HWALC as source <c>TX-</c> index 5, "Voltage",
        /// units dBFS, range -150 to 20. It is the voltage on the external
        /// amplifier's ALC jack. The radio ALSO publishes a meter named plainly
        /// <c>ALC</c>, source <c>TX-</c> index 0, dBFS, range -150 to 20 — that
        /// one is the transmit drive after software levelling, it is what
        /// <c>FlexBase.SwAlcDb</c> already carries, and nothing watches it.
        /// </para>
        /// <para>
        /// <b>The caveat that had to be cleared, and how.</b> With no station
        /// client connected the radio publishes only eleven meters, HWALC among
        /// them and no plain ALC at all — so a census taken then shows exactly
        /// one ALC-ish meter and supports the wrong conclusion, that the
        /// software ALC does not exist on this radio and the watcher had no
        /// better choice. It does exist. Both were seen together in the
        /// thirty-five-meter state, the one that also carries the per-slice
        /// receive meters and the rest of the transmit signal chain. HWALC is
        /// present in BOTH states; ALC appears with the transmit chain. So the
        /// two are genuinely distinct meters and the watcher is on the wrong
        /// one, rather than on the only one available.
        /// </para>
        /// <para>
        /// So for an operator with no amplifier connected — the default, and
        /// Noel's bench — HWALC sits dead and this watcher can never fire. The
        /// control is labelled "Peak Watcher (ALC safety alerts)" and the
        /// warnings speak as "ALC high" and "ALC warning", so an operator would
        /// reasonably believe they are being guarded against overdriving their
        /// transmitter. They are not being guarded against anything.
        /// </para>
        /// <para>
        /// <b>The thresholds are in the wrong units as well.</b>
        /// <see cref="AlcWarningThreshold"/> and
        /// <see cref="AlcCriticalThreshold"/> are 0.5 and 0.8 — the shape of a
        /// zero-to-one fraction — and they are compared straight against a
        /// reading in decibels relative to full scale. Half a dB ABOVE full
        /// scale is not the trip point anybody chose.
        /// </para>
        /// <para>
        /// <b>What must NOT happen is repointing this constant at ALC.</b>
        /// Noel decided on 2026-08-11 that HWALC stays surfaced as AMPLIFIER
        /// ALC, because older amplifiers without network control genuinely use
        /// the RCA line for overdrive protection and those operators need it.
        /// The transmit-drive guardrail is a SECOND thing, not a replacement,
        /// and it needs its own wording so an operator can tell which of the
        /// two just spoke. That is a design change with user-facing speech in
        /// it, so Track D reported it rather than making it.
        /// </para>
        /// <para>
        /// The same conflation has a second surface:
        /// <see cref="GetMeterSpeechSummary"/> reads <c>_rig.ALC</c>, which is
        /// also HWALC, and announces it as a bare "ALC". Its guard of
        /// <c>&gt; 0.01</c> is the same units mistake, so in practice that line
        /// is never spoken at all.
        /// </para>
        /// </remarks>
        private const string PeakWatcherMeterName = "HWALC";

        private static void OnMeterData(object sender, Meter meter, float value)
        {
            if (meter == null) return;
            long now = DateTime.UtcNow.Ticks;

            // Peak Watcher runs regardless of tone throttling — it has its own
            // cooldown and a safety alert should never wait behind a tone.
            if (_enabled && PeakWatcherEnabled && _rig != null && _rig.Transmit
                && string.Equals(meter.Name, PeakWatcherMeterName, StringComparison.OrdinalIgnoreCase))
            {
                CheckPeakWatcher(value, now);
            }

            if (!_enabled || _rig == null) return;

            bool transmitting = _rig.Transmit;

            // Update each slot whose source matches this meter. More than one
            // may match — two meters sharing a source (coarse and fine SWR) is
            // an explicitly supported shape.
            foreach (var slot in Slots)
            {
                var def = slot.Definition;
                if (!def.Enabled) continue;
                if (!SourceMatches(def.Source, meter)) continue;

                // Per-slot throttle to ~10 Hz to avoid audio glitching.
                if (now - slot.LastUpdateTicks < ThrottleIntervalTicks) continue;
                slot.LastUpdateTicks = now;

                bool shouldSound = ShouldSound(def.Activation, transmitting);
                slot.ToneProvider.Active = shouldSound;

                if (shouldSound)
                {
                    slot.ToneProvider.Frequency = def.PitchForValue(value);
                    slot.ToneProvider.Volume = def.Volume * MasterVolume;
                    slot.ToneProvider.Pan = def.Pan;
                    slot.ToneProvider.Voice = def.EffectiveVoice();
                }
            }
        }

        private static void OnTransmitChanged(object sender, bool transmitting)
        {
            if (!_enabled) return;

            // When TX state changes, update which slots are active
            foreach (var slot in Slots)
            {
                if (!slot.Enabled) continue;
                slot.ToneProvider.Active = ShouldSound(slot.Definition.Activation, transmitting);
            }

            // Reset peak watcher state on TX→RX transition
            if (!transmitting)
            {
                _alcHighStartTicks = 0;
                _alcSustainedWarning = false;
            }
        }

        /// <summary>
        /// Does this source reference name the meter that just reported?
        /// </summary>
        /// <remarks>
        /// <para>
        /// The key space is the radio's OWN meter names now — LEVEL, FWDPWR,
        /// HWALC, SC_MIC, +13.8A — not the eight we used to invent. A config
        /// written before Sprint 32 holds the old names and is translated once,
        /// on load, by <see cref="MeterConfigMigration"/>; nothing here has to
        /// know about that.
        /// </para>
        /// <para>
        /// Slices need the extra hop. A four-slice radio reports four meters
        /// called LEVEL, one per slice, so name alone would let whichever slice
        /// reported last drive the tone. A source index of -1 means "follow the
        /// active slice", which is what a migrated S-meter gets and what an
        /// operator who does not think in slice numbers wants.
        /// </para>
        /// </remarks>
        private static bool SourceMatches(MeterSourceRef source, Meter meter)
        {
            if (source.Kind != MeterSourceKind.RadioReported) return false;
            if (!string.Equals(source.Key, meter.Name, StringComparison.OrdinalIgnoreCase))
                return false;

            bool isSlice = string.Equals(meter.Source, Meter.SOURCE_SLICE,
                                         StringComparison.OrdinalIgnoreCase);
            if (!isSlice) return true;

            if (source.SliceIndex >= 0) return meter.SourceIndex == source.SliceIndex;

            int active = ActiveSliceIndex();
            // No active slice to follow: take the reading rather than falling
            // silent. A silent meter reads as a broken feature.
            return active < 0 || meter.SourceIndex == active;
        }

        /// <summary>
        /// The radio index of the active slice, or -1. Derived from the slice
        /// LETTER because that is the only public route from here — the same
        /// letter-minus-A arithmetic FlexBase's own <c>LetterToVFO</c> uses, so
        /// it is the project's existing convention rather than a new assumption.
        /// </summary>
        private static int ActiveSliceIndex()
        {
            string letter = _rig?.ActiveSliceLetter ?? "";
            if (letter.Length == 0) return -1;
            int index = char.ToUpperInvariant(letter[0]) - 'A';
            return index >= 0 && index < 32 ? index : -1;
        }

        private static bool ShouldSound(MeterActivation activation, bool transmitting) =>
            activation switch
            {
                MeterActivation.ReceiveOnly => !transmitting,
                MeterActivation.TransmitOnly => transmitting,
                _ => true,
            };

        #endregion

        #region Peak Watcher

        private static void CheckPeakWatcher(float alcValue, long nowTicks)
        {
            // Cooldown check
            if (nowTicks - _lastPeakWarningTicks < PeakCooldownTicks) return;

            if (alcValue > AlcCriticalThreshold)
            {
                // Critical: immediate alert
                _lastPeakWarningTicks = nowTicks;
                try { EarconPlayer.Warning2Beep(); } catch { }
                if (SpeechEnabled)
                    ScreenReaderOutput.Speak("ALC high", VerbosityLevel.Critical);
            }
            else if (alcValue > AlcWarningThreshold)
            {
                // Warning: alert after 3 seconds sustained
                if (_alcHighStartTicks == 0)
                {
                    _alcHighStartTicks = nowTicks;
                }
                else if (!_alcSustainedWarning &&
                         nowTicks - _alcHighStartTicks > AlcSustainedThresholdTicks)
                {
                    _alcSustainedWarning = true;
                    _lastPeakWarningTicks = nowTicks;
                    try { EarconPlayer.Warning1Beep(); } catch { }
                    if (SpeechEnabled)
                        ScreenReaderOutput.Speak("ALC warning", VerbosityLevel.Critical);
                }
            }
            else
            {
                // Below threshold — reset
                _alcHighStartTicks = 0;
                _alcSustainedWarning = false;
            }
        }

        #endregion

        #region Speech Readout

        /// <summary>
        /// Generate a speech summary of current meter values.
        /// Works whether tones are on or off.
        /// </summary>
        public static string GetMeterSpeechSummary()
        {
            if (_rig == null) return "No radio connected";

            var sb = new StringBuilder();
            bool tx = _rig.Transmit;

            if (!tx)
            {
                // RX meters
                int sUnits = _rig.SMeter;
                if (sUnits <= 9)
                    sb.Append($"S-meter S{sUnits}. ");
                else
                    // Over S9 the excess is already dB (SMeter returns
                    // dB-over-S9 plus 9); the old x6 inflated it sixfold.
                    sb.Append($"S-meter S9 plus {sUnits - 9} dB. ");
            }
            else
            {
                // TX meters. Forward power comes from the float path — this
                // used to truncate locally, so 174 mW of real RF was spoken
                // as "Forward power 0 watts".
                sb.Append($"Forward power {FlexBase.FormatForwardPowerSpoken(_rig.ForwardPowerWatts)}. ");

                float swr = _rig.SWRValue;
                if (swr > 0)
                    sb.Append($"SWR {swr:F1}. ");

                float alc = _rig.ALC;
                if (alc > 0.01f)
                    sb.Append($"ALC {alc:F2}. ");

                float mic = _rig.MicData;
                sb.Append($"Mic {mic:F1} dB. ");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>Speak the current meter summary via screen reader.</summary>
        public static void SpeakMeters()
        {
            string summary = GetMeterSpeechSummary();
            ScreenReaderOutput.Speak(summary, VerbosityLevel.Terse, true);
        }

        #endregion
    }

    // The eight-value MeterSource enum lived here until Sprint 32 Track B and
    // is deliberately gone. It capped the choosable meters at eight on a radio
    // that reports over a hundred, and because the panel wrote
    // (MeterSource)combo.SelectedIndex, its ORDINAL was load-bearing in saved
    // config files. The source space is MeterSourceRef with a string key drawn
    // from the radio's own meter list; MeterConfigMigration translates anything
    // written before the change. Do not reintroduce an enum here.

    /// <summary>
    /// A meter tone slot: a <see cref="MeterDefinition"/> (source + range +
    /// voice + mapping) bound to a live <see cref="VoicedToneSampleProvider"/>.
    /// The flat properties are thin conveniences that also keep the live
    /// synthesis provider in step; <see cref="Definition"/> is the model.
    /// </summary>
    public class MeterSlot
    {
        /// <summary>The model object this slot renders.</summary>
        public MeterDefinition Definition { get; private set; }

        /// <summary>The live synthesis provider (stereo, live pan).</summary>
        public VoicedToneSampleProvider ToneProvider { get; } = new();

        /// <summary>Per-slot tone update throttle bookkeeping.</summary>
        internal long LastUpdateTicks;

        public MeterSlot()
        {
            Definition = LegacyMeterCatalog.CreateDefinition("SMeter");
            SyncProvider();
        }

        /// <summary>Replace the definition wholesale and resync the provider.</summary>
        public void SetDefinition(MeterDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            SyncProvider();
        }

        private void SyncProvider()
        {
            ToneProvider.Voice = Definition.EffectiveVoice();
            ToneProvider.Pan = Definition.Pan;
        }

        /// <summary>
        /// Point this slot at a different meter, keeping the operator's voice,
        /// volume, pan and pitch mapping — those were chosen for the SLOT, not
        /// for the meter, and re-picking a source should not silently undo an
        /// afternoon of tuning by ear. Name, range, units and activation come
        /// from the new source, because those describe the meter itself.
        /// </summary>
        /// <param name="key">The radio's own meter name, e.g. FWDPWR.</param>
        /// <param name="displayName">What to call it. Empty uses the key.</param>
        /// <param name="range">The new range in the source's own units.</param>
        /// <param name="activation">When the tone is allowed to sound.</param>
        /// <param name="sliceIndex">Which slice, or -1 to follow the active one.</param>
        public void Retarget(string key, string displayName, MeterRange range,
            MeterActivation activation, int sliceIndex)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            Definition.Name = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
            Definition.Source = new MeterSourceRef
            {
                Kind = MeterSourceKind.RadioReported,
                Key = key,
                SliceIndex = sliceIndex,
            };
            if (range != null) Definition.Range = range;
            Definition.Activation = activation;
        }

        public bool Enabled
        {
            get => Definition.Enabled;
            set => Definition.Enabled = value;
        }

        public float Volume
        {
            get => Definition.Volume;
            set
            {
                Definition.Volume = value;
                ToneProvider.Volume = value * MeterToneEngine.MasterVolume;
            }
        }

        /// <summary>Pan, -1..+1. Live: takes effect on the next audio buffer
        /// (the old engine's pan was fixed at mixer registration and changing
        /// it silently did nothing).</summary>
        public float Pan
        {
            get => Definition.Pan;
            set
            {
                Definition.Pan = value;
                ToneProvider.Pan = value;
            }
        }

        public int PitchLow
        {
            get => (int)Definition.PitchLowHz;
            set => Definition.PitchLowHz = value;
        }

        public int PitchHigh
        {
            get => (int)Definition.PitchHighHz;
            set => Definition.PitchHighHz = value;
        }

        /// <summary>The voice this meter speaks with. Setting it clears any
        /// live-tweak override — an explicit voice choice replaces the tweak.</summary>
        public string VoiceName
        {
            get => Definition.VoiceName;
            set
            {
                Definition.VoiceName = value;
                Definition.VoiceOverride = null;
                SyncProvider();
            }
        }

    }
}

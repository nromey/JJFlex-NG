using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Flex.Smoothlake.FlexLib;
using JJTrace;
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

        /// <summary>
        /// The floor below which a dBFS transmit meter is reporting its
        /// sentinel rather than a measurement, and must not be spoken as a
        /// level.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="FlexBase.ScMicDb"/>, <see cref="FlexBase.ScMicMaxDb"/>,
        /// <see cref="FlexBase.ScMicRecentDb"/> and
        /// <see cref="FlexBase.SwAlcDb"/> all initialise to -150 and stay
        /// there until their meter first reports. -150 is not a quiet
        /// microphone; it is "no meter has spoken yet", and the two must never
        /// be read as the same thing.
        /// </para>
        /// <para>
        /// The figure matches the guard <c>ScreenFieldsPanel.UpdateMicVerdict</c>
        /// has used since it was written; it is named here so the two cannot
        /// drift, and that panel should be pointed at this constant rather
        /// than its own literal.
        /// </para>
        /// </remarks>
        public const float DbfsNoReading = -140f;

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
            // Two whole utterances rather than a template plus a bare "on" /
            // "off": the state word alone is not a string anybody can review,
            // and a shared on/off key would be edited from six directions.
            ScreenReaderOutput.Speak(
                Lexicon.Get(Enabled ? "audio.meters.tones_on" : "audio.meters.tones_off"),
                VerbosityLevel.Terse, true);
            if (Enabled) EarconPlayer.FeatureOnTone();
            else EarconPlayer.FeatureOffTone();
        }

        /// <summary>Whether speech readout of meter values is enabled.</summary>
        public static bool SpeechEnabled { get; set; } = true;

        /// <summary>Speech interval in seconds (1-10). How often batched meter values are spoken.</summary>
        public static int SpeechIntervalSeconds { get; set; } = 3;

        /// <summary>
        /// #128: config restore must not chime. The three operator booleans
        /// below tone in their SETTERS — the single mutation point — because
        /// their roads (Home panel checkbox, Meters panel checkbox, menu item)
        /// are bare property writes with no shared funnel, and Peak Watcher
        /// proved the failure mode: the Home panel road toned while the Meters
        /// panel and menu roads flipped the identical state silently
        /// (2026-08-21 sweep audit). Setter-level means a future fourth road
        /// cannot be silent. The one non-operator writer is AudioOutputConfig
        /// restoring saved state at load, which wraps its writes in this flag —
        /// a launch is not a toggle, and a chime storm at startup is the #58
        /// failure all over again.
        ///
        /// <see cref="Enabled"/> deliberately does NOT get this treatment: it
        /// is written by OnTuneStarted/OnTuneStopped as an automatic side
        /// effect of the tune carrier, so its operator tone lives in
        /// <see cref="ToggleEnabled"/> instead, and every operator road goes
        /// through that.
        /// </summary>
        public static bool QuietStateRestore { get; set; }

        /// <summary>Whether the speech timer is actively speaking meter values.</summary>
        public static bool SpeechTimerActive
        {
            get => _speechTimerActive;
            set
            {
                if (_speechTimerActive == value) return;
                _speechTimerActive = value;
                if (value) StartSpeechTimer();
                else StopSpeechTimer();
                // #128: every operator road answers back; see QuietStateRestore.
                if (!QuietStateRestore) EarconPlayer.ToggleTone(value);
            }
        }
        private static bool _speechTimerActive;
        private static System.Windows.Threading.DispatcherTimer? _speechTimer;

        /// <summary>When true, enables default meters when TxTune activates.</summary>
        public static bool AutoEnableOnTune
        {
            get => _autoEnableOnTune;
            set
            {
                if (_autoEnableOnTune == value) return;
                _autoEnableOnTune = value;
                // #128: every operator road answers back; see QuietStateRestore.
                if (!QuietStateRestore) EarconPlayer.ToggleTone(value);
            }
        }
        private static bool _autoEnableOnTune;

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
        //
        // #128: the setter tones because Peak Watcher has THREE operator roads
        // — the Home panel checkbox, the Meters panel checkbox, and the Meter
        // Tones menu item — and until the 2026-08-21 sweep audit only the
        // first of them made a sound. One tone at the mutation point covers
        // all three and any road added later. See QuietStateRestore above.
        public static bool PeakWatcherEnabled
        {
            get => _peakWatcherEnabled;
            set
            {
                if (_peakWatcherEnabled == value) return;
                _peakWatcherEnabled = value;
                if (!QuietStateRestore) EarconPlayer.ToggleTone(value);
            }
        }
        private static bool _peakWatcherEnabled = true;
        private static long _lastPeakWarningTicks;
        private static long _alcHighStartTicks;
        private static bool _alcSustainedWarning;
        private const long PeakCooldownTicks = TimeSpan.TicksPerSecond * 10;
        private const long AlcSustainedThresholdTicks = TimeSpan.TicksPerSecond * 3;
        private const float AlcWarningThreshold = 0.5f;
        private const float AlcCriticalThreshold = 0.8f;

        // Transmit-drive watcher state, kept separate from the amplifier-ALC
        // state above on purpose. They watch different meters for different
        // faults, and one masking the other with a shared cooldown is how a
        // safety alert goes quiet without anybody noticing. See the remarks on
        // DriveWatcherMeterName.
        private static long _lastDriveWarningTicks;
        private static long _driveHighStartTicks;
        private static bool _driveSustainedWarning;
        private static long _driveCriticalStartTicks;
        private static bool _driveSeenThisTransmit;
        private static bool _driveMissingReported;

        // Sustained for a full second before a critical alert. A syllable does
        // not last a second, so this separates real overdrive from the peaks
        // SSB is supposed to produce.
        private const long DriveCriticalSustainedTicks = TimeSpan.TicksPerSecond * 1;

        /// <summary>
        /// dBFS above which transmit drive is high enough to warn about, after
        /// three seconds sustained.
        /// </summary>
        /// <remarks>
        /// <b>Zero is not a chosen number.</b> The SW ALC meter is dBFS with a
        /// declared range of -150 to +20, so 0 is the meter's own full scale —
        /// the point it exists to define. Properly set transmit audio has its
        /// peaks approach full scale; three seconds of sitting above it is not
        /// a peak. That makes the trip point a property of the instrument
        /// rather than a guess, which matters because every OTHER number in
        /// this area was a guess and one of them was in the wrong units.
        /// </remarks>
        private const float DriveWarningThresholdDbfs = 0.0f;

        /// <summary>
        /// dBFS above which transmit drive is bad enough to interrupt for.
        /// Three decibels over full scale is twice the power the meter says is
        /// the maximum, which no correctly set transmitter reaches.
        /// </summary>
        private const float DriveCriticalThresholdDbfs = 3.0f;

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
        /// Noel's bench — HWALC sits dead and this watcher can never fire. That
        /// is correct behaviour for an amplifier watcher with no amplifier
        /// attached; what was wrong was that it was the ONLY watcher, while the
        /// control is labelled "Peak Watcher (ALC safety alerts)" and the
        /// warnings speak as "ALC high" and "ALC warning" — so an operator
        /// would reasonably believe they were being guarded against overdriving
        /// their transmitter, and they were not being guarded against anything.
        /// </para>
        /// <para>
        /// <b>That gap is now closed by a second watcher rather than by moving
        /// this one</b> — see <see cref="DriveWatcherMeterName"/>. This constant
        /// stays on HWALC. The two labels above still say only "ALC", which is
        /// now the LESS specific of the two things the control governs; whether
        /// they should say "amplifier ALC" is user-facing wording and belongs
        /// to whoever owns that copy, not here.
        /// </para>
        /// <para>
        /// <b>The thresholds are in the wrong units as well.</b>
        /// <see cref="AlcWarningThreshold"/> and
        /// <see cref="AlcCriticalThreshold"/> are 0.5 and 0.8 — the shape of a
        /// zero-to-one fraction — and they are compared straight against a
        /// reading in decibels relative to full scale. Half a dB ABOVE full
        /// scale is not the trip point anybody chose.
        /// <b>Still true, and deliberately not corrected here.</b> What voltage
        /// on the amplifier's ALC line means "back off" has never been
        /// measured, and this is a safety alert on a path that only exists once
        /// an amplifier is attached — which is exactly the bench sitting where
        /// it can be measured (#125). Moving the number from one guess to
        /// another would trade a guard that fails silent for one that fails
        /// wrong, and the wrong one is louder.
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
        /// <b>Built on 2026-08-26, exactly that way.</b> The sprint plan that
        /// commissioned the work described the fix as flipping this constant;
        /// this paragraph said not to, and this paragraph carried the operator's
        /// own ruling and a date, so it won. Two watchers, one control, two
        /// sentences.
        /// </para>
        /// <para>
        /// The same conflation HAD a second surface, and that one is fixed:
        /// <see cref="GetMeterSpeechSummary"/> used to read <c>_rig.ALC</c> —
        /// also HWALC — and announce it as a bare "ALC", with a guard of
        /// <c>&gt; 0.01</c> that made the same units mistake, so in practice
        /// the line was never spoken at all. It now reads
        /// <see cref="FlexBase.SwAlcDb"/> and says "TX drive". The watcher
        /// below is still the open half of this.
        /// </para>
        /// </remarks>
        private const string PeakWatcherMeterName = "HWALC";

        /// <summary>
        /// The transmit-drive watcher's meter: the radio's own software ALC,
        /// published plainly as <c>ALC</c>, source <c>TX-</c> index 0, dBFS,
        /// range -150 to +20. This is the level of the audio actually being
        /// transmitted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>#139, and read the paragraph above before changing either
        /// constant.</b> The finding was that the safety alert watched the
        /// external amplifier's ALC jack, so an operator with no amplifier had
        /// a warning that could never fire while being labelled and spoken as
        /// though it guarded their transmit audio. A warning that cannot fire
        /// is worse than no warning, because it is trusted, and nothing
        /// distinguishes "watching and quiet" from "watching the wrong thing".
        /// </para>
        /// <para>
        /// <b>The fix is NOT to repoint HWALC, and that is a ruling rather than
        /// a preference.</b> Noel decided on 2026-08-11 that HWALC stays
        /// surfaced as AMPLIFIER ALC, because older amplifiers without network
        /// control genuinely use the RCA line for overdrive protection and
        /// those operators need it. The transmit-drive guardrail is a SECOND
        /// thing, not a replacement. So there are now two watchers, sharing one
        /// operator control, speaking different sentences — which is what lets
        /// an operator tell which of the two just spoke.
        /// </para>
        /// <para>
        /// <b>What is proven and what is not.</b> Proven on the bench 8600,
        /// 2026-08-11: the two are distinct meters, both present together in
        /// the thirty-five-meter state that appears once a station client is
        /// connected. NOT proven, and it is bench work: that this watcher
        /// actually fires. That needs a keyed transmit into a dummy load with
        /// the drive pushed up, and it is the one thing no amount of reading
        /// settles. Until somebody has watched it speak, treat it as wired
        /// rather than working.
        /// </para>
        /// <para>
        /// <b>If the radio does not publish it, that is traced rather than
        /// silent.</b> A transmit that ends without this meter having reported
        /// once puts a warning in the log, because a guardrail whose meter
        /// never arrives fails in exactly the same invisible way as the one
        /// this replaces.
        /// </para>
        /// </remarks>
        private const string DriveWatcherMeterName = "ALC";

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

            // The second watcher, on the meter that carries transmit drive.
            // Same gate, same cooldown discipline, different meter and
            // different sentence. See DriveWatcherMeterName.
            if (_enabled && PeakWatcherEnabled && _rig != null && _rig.Transmit
                && string.Equals(meter.Name, DriveWatcherMeterName, StringComparison.OrdinalIgnoreCase))
            {
                _driveSeenThisTransmit = true;
                CheckDriveWatcher(value, now);
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

                _driveHighStartTicks = 0;
                _driveCriticalStartTicks = 0;
                _driveSustainedWarning = false;

                // A guardrail whose meter never arrives is indistinguishable
                // from one that is watching and content, which is the whole
                // shape of #139. Say so once per session rather than per
                // transmit — repeated every over it would be noise, and never
                // saying it is how the first one hid for months.
                if (PeakWatcherEnabled && !_driveSeenThisTransmit && !_driveMissingReported)
                {
                    _driveMissingReported = true;
                    Tracing.TraceLine(
                        "DriveWatcher: a transmit ended with no " + DriveWatcherMeterName
                        + " meter reading, so the transmit-drive guard did not watch anything."
                        + " The radio may not be publishing it.", TraceLevel.Warning);
                }
                _driveSeenThisTransmit = false;
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
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.alc_high"), VerbosityLevel.Critical);
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
                        ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.alc_warning"), VerbosityLevel.Critical);
                }
            }
            else
            {
                // Below threshold — reset
                _alcHighStartTicks = 0;
                _alcSustainedWarning = false;
            }
        }

        /// <summary>
        /// The transmit-drive half of the Peak Watcher: the guard against
        /// overdriving your own transmitter, as opposed to your amplifier
        /// asking you to back off.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Both tiers require sustained time, and the amplifier watcher's
        /// critical tier does not.</b> That difference is deliberate. HWALC
        /// moving at all means an amplifier is actively pulling the radio back,
        /// which is an event. Transmit drive touching full scale is what
        /// correctly set SSB audio DOES on peaks, so an immediate critical
        /// alert here would speak over the operator on every strong syllable
        /// and be switched off within a day — which is the same outcome as a
        /// warning that cannot fire, arrived at from the other side.
        /// </para>
        /// <para>
        /// Every fire is traced. The bench session that confirms these
        /// thresholds needs a record of what the meter was doing when the alert
        /// spoke, and the trace is the only place that can come from.
        /// </para>
        /// </remarks>
        private static void CheckDriveWatcher(float driveDbfs, long nowTicks)
        {
            if (nowTicks - _lastDriveWarningTicks < PeakCooldownTicks) return;

            if (driveDbfs > DriveCriticalThresholdDbfs)
            {
                if (_driveCriticalStartTicks == 0)
                {
                    _driveCriticalStartTicks = nowTicks;
                }
                else if (nowTicks - _driveCriticalStartTicks > DriveCriticalSustainedTicks)
                {
                    _driveCriticalStartTicks = 0;
                    _lastDriveWarningTicks = nowTicks;
                    Tracing.TraceLine(
                        "DriveWatcher: critical, " + DriveWatcherMeterName + " = "
                        + driveDbfs.ToString("F1") + " dBFS", TraceLevel.Warning);
                    try { EarconPlayer.Warning2Beep(); } catch { }
                    if (SpeechEnabled)
                        ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.drive_over_scale"),
                                                 VerbosityLevel.Critical);
                }
                return;
            }

            _driveCriticalStartTicks = 0;

            if (driveDbfs > DriveWarningThresholdDbfs)
            {
                if (_driveHighStartTicks == 0)
                {
                    _driveHighStartTicks = nowTicks;
                }
                else if (!_driveSustainedWarning &&
                         nowTicks - _driveHighStartTicks > AlcSustainedThresholdTicks)
                {
                    _driveSustainedWarning = true;
                    _lastDriveWarningTicks = nowTicks;
                    Tracing.TraceLine(
                        "DriveWatcher: warning, " + DriveWatcherMeterName + " = "
                        + driveDbfs.ToString("F1") + " dBFS", TraceLevel.Info);
                    try { EarconPlayer.Warning1Beep(); } catch { }
                    if (SpeechEnabled)
                        ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.drive_high"),
                                                 VerbosityLevel.Critical);
                }
            }
            else
            {
                _driveHighStartTicks = 0;
                _driveSustainedWarning = false;
            }
        }

        #endregion

        #region Speech Readout

        /// <summary>
        /// Generate a speech summary of current meter values.
        /// Works whether tones are on or off.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Three surfaces read this</b> — the Speak Meters key
        /// (Ctrl+Alt+V), its menu item, and the Status dialog's Meters
        /// section — so a wrong meter here is wrong in three places at once.
        /// </para>
        /// <para>
        /// <b>Every transmit figure below is the same one the corrected
        /// surfaces use, and that is the whole point of the method.</b> Until
        /// 2026-08-26 this read <c>SWRValue</c>, <c>ALC</c> and
        /// <c>MicData</c> — the radio's own SWR meter, the external-amplifier
        /// ALC jack, and the analog codec meter. Every one of them had already
        /// been established as the wrong instrument elsewhere in the app, and
        /// the most reflexive way an operator has of asking how transmit is
        /// going was the surface that never got the correction. On a PC-audio
        /// station it said "Mic -120" while the Audio Workshop said the level
        /// was fine; into a bad load it said "SWR 1.0" while the chain check
        /// called the load suspect.
        /// </para>
        /// <para>
        /// A blind operator has no second opinion — they cannot glance at a
        /// needle to sanity-check what they just heard. Do not repoint any of
        /// these at a raw radio meter. <c>MeterSpeechSummarySourceTests</c> in
        /// Radios.Tests fails the build if one of the three comes back.
        /// </para>
        /// </remarks>
        public static string GetMeterSpeechSummary()
        {
            if (_rig == null) return Lexicon.Get("audio.meters.no_radio");

            var sb = new StringBuilder();
            bool tx = _rig.Transmit;

            if (!tx)
            {
                // RX meters
                int sUnits = _rig.SMeter;
                // The sentence lives in the store; the separating space stays
                // in code, where it belongs.
                if (!SMeterReading.IsOverS9(sUnits))
                    sb.Append(Lexicon.Get("audio.meters.s_meter", ("sUnits", sUnits))).Append(' ');
                else
                    // The excess comes from SMeterReading rather than being
                    // subtracted here. The SENTENCE is the store's; the NUMBER
                    // in it is the one every other surface reports.
                    sb.Append(Lexicon.Get("audio.meters.s_meter_over_9",
                        ("overS9", SMeterReading.ExcessOverS9(sUnits)))).Append(' ');
            }
            else
            {
                // TX meters. Forward power comes from the float path — this
                // used to truncate locally, so 174 mW of real RF was spoken
                // as "Forward power 0 watts".
                sb.Append(Lexicon.Get("audio.meters.forward_power",
                    ("power", FlexBase.FormatForwardPowerSpoken(_rig.ForwardPowerWatts)))).Append(' ');

                // SWR is WORKED OUT from forward and reflected power, never
                // read from the radio's own SWR meter. That meter reported
                // 1.008 into an unterminated antenna port on 2026-08-22 while
                // 76% of the power was coming back, then dropped to its -25
                // no-reading sentinel mid-transmit. It is accurate when the
                // antenna system is fine and wrong when it is not, which is
                // exactly backwards for a number nobody consults until
                // something has already gone wrong. FlexBase.ComputedSWR is
                // the same arithmetic the live PTT warning, the transmit
                // chain check and the Fixer all use, so this key can no
                // longer disagree with them.
                //
                // NaN means there is not enough forward power to derive
                // anything — between syllables on SSB, or key-up. Say so
                // rather than going silent: silence is ambiguous, and an
                // invented 1.0 is the failure this replaces.
                float swr = _rig.ComputedSWR;
                sb.Append(float.IsNaN(swr)
                        ? Lexicon.Get("audio.meters.swr_no_reading")
                        : Lexicon.Get("audio.meters.swr", ("swr", $"{swr:F1}")))
                    .Append(' ');

                // Transmit DRIVE is SW ALC. The old code read _rig.ALC, which
                // is HWALC — the voltage on the external-amplifier ALC jack —
                // and announced it as a bare "ALC". Two separate mistakes in
                // one line: the wrong meter, and a guard of > 0.01 applied to
                // a reading in dBFS, which meant the line was in practice
                // never spoken at all.
                //
                // HWALC is deliberately NOT repointed here. Noel ruled on
                // 2026-08-11 that it stays surfaced as AMPLIFIER ALC, because
                // older amplifiers genuinely use that line for overdrive
                // protection. It is a second thing with its own wording, not a
                // replacement — which is why this one says "TX drive", the
                // same words the Audio Workshop's Live Meters already uses.
                float drive = _rig.SwAlcDb;
                if (drive > DbfsNoReading)
                    sb.Append(Lexicon.Get("audio.meters.tx_drive", ("drive", $"{drive:F1}"))).Append(' ');

                // Microphone: SC_MIC, and the wording comes from
                // MicAudioReport so this key cannot drift from the Home audio
                // expander, the Audio Workshop's reading field, or the JJ
                // key's mic check. It also inherits the operator's
                // verdict-output preference, so someone who wants figures
                // without coaching gets figures without coaching.
                //
                // The old code read _rig.MicData, the analog codec meter,
                // which reads -120 for PC audio. On a PC-audio station this
                // key said "Mic -120" while the Workshop two keystrokes away
                // said the level was fine.
                //
                // Live/last selection matches ScreenFieldsPanel.UpdateMicVerdict
                // exactly: the recent peak follows a level back DOWN, so it
                // tracks a mic-gain change made mid-transmit, where the
                // whole-transmit peak-hold only ever grows.
                float recent = _rig.ScMicRecentDb;
                float max = _rig.ScMicMaxDb;
                if (recent > DbfsNoReading)
                    sb.Append(MicAudioReport.Compose(
                        _rig, Lexicon.Get("audio.fields.mic_verdict_now"), recent, live: true));
                else if (max > DbfsNoReading)
                    sb.Append(MicAudioReport.Compose(
                        _rig, Lexicon.Get("audio.fields.mic_verdict_last"), max, live: false));
                else
                    sb.Append(Lexicon.Get("audio.fields.mic_verdict_none"));
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

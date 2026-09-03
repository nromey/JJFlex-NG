using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;

namespace JJFlexWpf
{
    /// <summary>
    /// Persisted audio output settings: earcon device, master volume, and meter tone configuration.
    /// XML serialized per-operator following the same pattern as FilterPresets.
    /// </summary>
    [XmlRoot("AudioOutputConfig")]
    public class AudioOutputConfig
    {
        /// <summary>NAudio device number for earcon output. -1 = Windows default.</summary>
        public int EarconDeviceNumber { get; set; } = -1;

        /// <summary>Master earcon volume 0–100. Kept for backward compatibility with old configs.</summary>
        public int MasterEarconVolume { get; set; } = 80;

        /// <summary>Alert channel volume 0.0–1.0. Replaces int-based MasterEarconVolume.
        /// Defaults to -1 which means "derive from MasterEarconVolume" for backward compat.</summary>
        private float _alertVolume = -1f;
        public float AlertVolume
        {
            get => _alertVolume >= 0 ? _alertVolume : MasterEarconVolume / 100f;
            set => _alertVolume = value;
        }

        /// <summary>Master volume multiplier across all channels 0.0–1.0.</summary>
        public float MasterVolume { get; set; } = 1.0f;

        /// <summary>NAudio device number for meter tone output. -1 = same as alerts.</summary>
        public int MeterDeviceNumber { get; set; } = -1;

        /// <summary>Whether meter tones are enabled.</summary>
        public bool MeterTonesEnabled { get; set; }

        /// <summary>Active meter preset name.</summary>
        public string MeterPreset { get; set; } = "RX Monitor";

        /// <summary>Master meter tone volume 0.0–1.0.</summary>
        public float MeterMasterVolume { get; set; } = 0.5f;

        /// <summary>Whether Peak Watcher ALC alerts are enabled.</summary>
        public bool PeakWatcherEnabled { get; set; } = true;

        /// <summary>Whether meter speech readout is enabled.</summary>
        public bool MeterSpeechEnabled { get; set; } = true;

        /// <summary>Whether the periodic speech timer is active (speaks meter values at interval).</summary>
        public bool MeterSpeechTimerActive { get; set; }

        /// <summary>Speech interval in seconds (1-10).</summary>
        public int MeterSpeechIntervalSeconds { get; set; } = 3;

        /// <summary>Auto-enable meter tones when tune carrier is activated.</summary>
        public bool AutoEnableOnTune { get; set; }

        /// <summary>Speech verbosity level: 0=Off(Critical only), 1=Terse, 2=Chatty (default).</summary>
        public int SpeechVerbosity { get; set; } = 2; // VerbosityLevel.Chatty

        /// <summary>
        /// Whether the radio selector's Network Identity panel is expanded.
        /// Default false: it describes the radio the app is CONNECTED to, so on
        /// the startup path it has nothing to say.
        ///
        /// Persisted because re-opening it every launch would make the operator
        /// ask twice for something they already asked for. Expanding it is an
        /// explicit choice to hear that content, and hearing it thereafter is
        /// the consequence they chose.
        ///
        /// (This class is the per-operator app settings store despite the
        /// audio-flavoured name - the test-tone settings already live here for
        /// the same reason.)
        /// </summary>
        public bool NetworkIdentityExpanded { get; set; }

        /// <summary>Whether alert sounds (earcons, beeps, tones) are enabled. Meter tones are separate.</summary>
        public bool EarconsEnabled { get; set; } = true;

        // Per-category alert-sound switches (Sprint 30, #43), under the master
        // EarconsEnabled gate. One field per EarconPlayer.EarconCategory value.
        /// <summary>Connect-phase counting tones and the success double-beep.</summary>
        public bool EarconConnectionEnabled { get; set; } = true;
        /// <summary>TX start/stop, hard kill, tune carrier, ATU, PTT warnings.</summary>
        public bool EarconTransmitEnabled { get; set; } = true;
        /// <summary>Dialog open/close dings and panel expand/collapse sweeps.</summary>
        public bool EarconDialogsEnabled { get; set; } = true;
        /// <summary>Filter-edge clicks and sweeps, band boundary, frequency-entry dings.</summary>
        public bool EarconTuningEnabled { get; set; } = true;
        /// <summary>JJ-layer tones, feature on/off, mute-all, mode enter/exit, confirmations.</summary>
        public bool EarconCommandsEnabled { get; set; } = true;
        /// <summary>
        /// The warning alarm and the problem-recorded tone (Sprint 31, #111).
        /// Absent from an older audioConfig.xml, which deserializes to the
        /// field initializer — so an operator upgrading gets warnings on, which
        /// is the right default for the one category that speaks unprompted.
        /// </summary>
        public bool EarconWarningsEnabled { get; set; } = true;
        // EarconContextHelpEnabled lived here until #343 removed the cue. Files
        // that still carry the element deserialize fine — XmlSerializer skips
        // unknown elements — and the next Save drops it.

        /// <summary>Frequency entry typing sound mode.</summary>
        public TypingSoundMode TypingSound { get; set; } = TypingSoundMode.Beep;

        /// <summary>Calibration tuning hash — stores verified reference data.</summary>
        public string TuningHash { get; set; } = "";

        /// <summary>
        /// PC output volume in dB of boost (0-24, default 12). This is the
        /// playback gain for radio audio through the computer — the knob that
        /// was a hardcoded 4x (+12 dB) before Audio Arc Track A. App-level,
        /// not per-radio: it describes this computer's speakers, not the rig.
        /// </summary>
        public int PcOutputVolumeDb { get; set; } = Radios.FlexBase.PcOutputVolumeDbDefault;

        /// <summary>
        /// Sample rate the transmit Opus encoder is built at, in hertz. 48000
        /// is the default and the only rate the radio path has been proven at;
        /// the lower Opus rates (24000, 16000, 12000, 8000) are the fallback
        /// for a constrained link. App-level, not per-radio: it describes this
        /// computer's link, not the rig. See
        /// <see cref="Radios.FlexBase.OpusTxSampleRateSetting"/> for why the
        /// device still gets the last word.
        /// </summary>
        public int OpusTxSampleRate { get; set; } = (int)Radios.FlexBase.OpusTxSampleRateDefault;

        /// <summary>Whether tuning speech debounce is enabled. When false, every tuning step speaks immediately.</summary>
        public bool TuneDebounceEnabled { get; set; } = true;

        /// <summary>Tuning speech debounce delay in milliseconds (50-1000, default 300).</summary>
        public int TuneDebounceMs { get; set; } = 300;

        /// <summary>Whether JJ Neural NR (RNNoise) is enabled.</summary>
        public bool RNNoiseEnabled { get; set; }

        /// <summary>RNNoise wet/dry mix strength 0.0-1.0.</summary>
        public float RNNoiseStrength { get; set; } = 0.8f;

        /// <summary>Auto-disable RNNoise in CW/digital modes.</summary>
        public bool RNNoiseAutoDisableNonVoice { get; set; } = true;

        /// <summary>Whether JJ Trained NR (spectral subtraction) is enabled.</summary>
        public bool SpectralSubEnabled { get; set; }

        /// <summary>Spectral subtraction strength 0.0-1.0.</summary>
        public float SpectralSubStrength { get; set; } = 0.7f;

        /// <summary>
        /// Spectral floor 0.0-1.0 — how much of the original audio survives
        /// subtraction, the guard against musical-noise artifacts. DSP
        /// controls track, 2026-08-11.
        /// </summary>
        public float SpectralSubFloor { get; set; } = 0.02f;

        /// <summary>Noise sampling duration in seconds (1-5). Default 3 per the
        /// ratified capture-window decision (was 2 — nothing ever read this
        /// field until the DSP controls track, 2026-08-11).</summary>
        public int SpectralSubSampleDuration { get; set; } = 3;

        /// <summary>
        /// Full path of the last noise profile loaded or captured, so PC
        /// Spectral NR picks up the same profile on the next connect instead
        /// of announcing "no noise profile loaded" forever. Empty = none.
        /// </summary>
        public string NoiseProfileLastPath { get; set; } = "";

        /// <summary>Whether CW Morse code notifications are enabled (AS/BT/SK prosigns).</summary>
        public bool CwNotificationsEnabled { get; set; }

        /// <summary>CW sidetone frequency in Hz (400-1200, default 700).</summary>
        public int CwSidetoneHz { get; set; } = 700;

        /// <summary>
        /// Follow the connected radio's own CW sidetone pitch instead of
        /// <see cref="CwSidetoneHz"/> (#146). Falls back to the configured tone
        /// whenever no radio has reported a pitch — being disconnected is a
        /// normal state, not an error. Default false, which is the behaviour
        /// every existing config file already has.
        /// </summary>
        public bool CwPitchFollowsRadio { get; set; }

        /// <summary>
        /// The spectrum CW notification marks are keyed with (#145), by id from
        /// <see cref="EarconVoices.CwWaveforms"/>. "Sine" is the sound that
        /// shipped. An unrecognised value resolves back to Sine rather than
        /// producing silence.
        /// </summary>
        public string CwWaveform { get; set; } = EarconVoices.DefaultCwWaveformId;

        /// <summary>
        /// Which set of alert-voice definitions is live (#147): 0 = Rich (the
        /// rebuilt sounds, and the default), 1 = Simple (the plain sine-based
        /// set they replaced). Stored as an int for XML serialization; see
        /// <see cref="EarconVoiceSet"/>.
        ///
        /// <b>The int is the persisted form, so the ordinals cannot move.</b>
        /// The names changed from Modern/Classic in Sprint 35; the numbers
        /// deliberately did not, or every saved config would flip sets.
        /// </summary>
        public int EarconVoiceSet { get; set; } = (int)JJFlexWpf.EarconVoiceSet.Rich;

        /// <summary>CW notification speed in WPM (10-30, default 20).</summary>
        public int CwSpeedWpm { get; set; } = 20;

        /// <summary>Announce mode changes in CW when speech verbosity is Off.</summary>
        public bool CwModeAnnounce { get; set; }

        /// <summary>
        /// Speak the settled SWR after manual tune (Ctrl+Shift+T) or ATU auto-tune
        /// completes. Format: "SWR 1.3 to 1". Reads the current SWR value ~200 ms
        /// after the tuner-off transition so mid-sweep transients don't get
        /// announced. Default true.
        /// </summary>
        public bool AnnounceSwrAfterTune { get; set; } = true;

        /// <summary>
        /// Speak connection progress while the connecting modal is up. Phase
        /// announcements ("connected, waiting for slice") and counting earcons
        /// (1 / 1+1 / 1+1+1 tones) only fire when this is true. Critical-level
        /// events (errors, "connection failed", "cancelled") always speak.
        /// Default true so new users hear progress and build confidence the app
        /// is working; frequent connectors who don't need the play-by-play can
        /// turn it off.
        /// </summary>
        public bool SpeakConnectionProgress { get; set; } = true;

        /// <summary>
        /// Offer to save the station layout into the radio's global profile
        /// when the operator disconnects, having changed the slice set during
        /// the session.
        ///
        /// <para>DEFAULT FALSE, deliberately, and it is the one setting here
        /// whose default is an argument rather than a taste. A global profile
        /// is STATION state — one per radio, shared by every client that
        /// connects — so what gets saved is never just "my slices". Sprint 32
        /// weighed a disconnect prompt and shipped a spoken notice instead, on
        /// the grounds that a prompt firing on every disconnect gets dismissed
        /// reflexively. That notice stays the shipped behaviour for everyone;
        /// this switch adds the question for the operator who asks for it, and
        /// the gates in FlexBase.ShouldOfferStationLayoutSave keep it from
        /// firing when nothing changed, when another operator is connected, or
        /// on a radio the operator has not marked as theirs.</para>
        ///
        /// <para>Absent from an older audioConfig.xml this deserialises to
        /// false, which is exactly the behaviour that file was written
        /// under.</para>
        /// </summary>
        public bool OfferStationSaveOnDisconnect { get; set; }

        /// <summary>Whether braille status line is enabled.</summary>
        public bool BrailleEnabled { get; set; }

        /// <summary>Braille display cell count (20, 32, 40, 80).</summary>
        public int BrailleCellCount { get; set; } = 40;

        /// <summary>Braille display enabled fields (flags enum as int for XML serialization).</summary>
        public int BrailleFields { get; set; } = (int)JJFlexWpf.BrailleFields.All;

        /// <summary>
        /// Whether the panadapter / waterfall braille display is visible and in the tab order.
        /// When false, PanadapterPanel is collapsed (removed from layout and focus) and the
        /// per-tile braille callback skips its braille push so braille displays aren't
        /// refreshed with data the user isn't viewing. Default true preserves existing behavior.
        /// </summary>
        public bool ShowPanadapter { get; set; } = true;

        /// <summary>
        /// TX test-tone frequency in hertz (Audio Workshop, Audio Track C).
        /// Per-operator on purpose: the frequency is an accessibility choice —
        /// hearing varies and does not change when you switch rigs — so it
        /// lives here, never in the serial-keyed per-radio config.
        /// </summary>
        public int TxToneFrequencyHz { get; set; } = 440;

        /// <summary>TX test-tone level in dBFS (-40..0). Default -10.</summary>
        public int TxToneLevelDb { get; set; } = -10;

        /// <summary>
        /// Hear the TX test tone locally while it transmits. Both answers are
        /// legitimate — confirm by ear, or keep quiet — so the operator picks.
        /// </summary>
        public bool TxToneLocalMonitor { get; set; } = true;

        /// <summary>
        /// How mic-audio verdicts read (Alt+Shift+S while transmitting, the
        /// Ctrl+J, K mic check, and the two reading fields — those are
        /// read-only edits precisely so a screen reader speaks them, which
        /// makes them a spoken surface too): 0 = plain English plus the
        /// figures (both — the conservative default, exactly what shipped
        /// before this setting), 1 = plain English only, 2 = figures only.
        /// Stored as int for XML serialization; see
        /// <see cref="MicVerdictOutputMode"/>.
        /// Noel asked for this explicitly (Audio Arc, 2026-08-11).
        /// </summary>
        public int MicVerdictOutput { get; set; } = (int)MicVerdictOutputMode.Both;

        /// <summary>Per-slot meter tone configurations. LEGACY: retained so old
        /// config files still deserialize, but the live meter list is
        /// <see cref="Meters"/> (Track D2). Anything found here is folded
        /// forward by <see cref="MeterConfigMigration"/> on load.</summary>
        public List<MeterSlotConfig> MeterSlots { get; set; } = new();

        /// <summary>
        /// Which generation of the meter model this file was written against.
        /// Absent (and therefore 0) in every config written before Sprint 32,
        /// which is exactly how <see cref="MeterConfigMigration"/> recognises a
        /// file whose meter sources are still legacy keys or bare ordinals.
        /// See that class for what each version means; do not bump it by hand.
        /// </summary>
        public int MeterConfigVersion { get; set; }

        /// <summary>
        /// User-authored meter voices (Track D2). Built-in voices ship as data
        /// in code (<see cref="MeterVoiceLibrary.BuiltIns"/>) and are never
        /// persisted, so they can improve between versions; only the
        /// operator's own creations live here.
        /// </summary>
        public List<MeterVoice> UserVoices { get; set; } = new();

        /// <summary>
        /// One earcon's level trim, relative to the tier it already picked.
        /// </summary>
        /// <remarks>
        /// A list of pairs rather than a dictionary because
        /// <c>XmlSerializer</c> will not serialise a Dictionary, and a config
        /// file that silently drops a section is worse than a slightly
        /// wordier one.
        /// </remarks>
        public sealed class EarconLevelTrim
        {
            /// <summary>The earcon's method name — the same stable id the
            /// catalog uses. Never translated, never renamed casually.</summary>
            public string Id { get; set; } = "";

            /// <summary>Trim in dB, bounded by
            /// <see cref="EarconPlayer.MinLevelTrimDb"/> and
            /// <see cref="EarconPlayer.MaxLevelTrimDb"/>.</summary>
            public float Db { get; set; }
        }

        /// <summary>
        /// Per-sound level trims (#115, #119). Only sounds that were actually
        /// trimmed appear here, so an untouched install persists nothing and
        /// every sound keeps whatever its tier says.
        /// </summary>
        /// <remarks>
        /// <b>Distinct from the bench gain, which is deliberately NOT saved.</b>
        /// The bench gain is a listening control — scoped to one Play call, for
        /// comparing sounds against each other and against band noise. This is
        /// a design control: it says how loud a sound should be from now on,
        /// and a decision made by ear is worth nothing if it does not survive a
        /// restart.
        /// </remarks>
        public List<EarconLevelTrim> EarconLevelTrims { get; set; } = new();

        /// <summary>
        /// Whether the radio's PC audio dips while a warning earcon sounds
        /// (#116). Fully defeatable, because this attenuates received audio
        /// and an operator who wants their band untouched is entitled to that.
        /// </summary>
        /// <remarks>
        /// <b>OFF by default since 2026-09-03 (#534), reversing the shipped
        /// default.</b> Ruled by Noel after living with it: in practice the dip
        /// read as a distraction rather than as help, which is the opposite of
        /// what #116 predicted. The dip is a movement in the band that happens
        /// rarely - warnings only - and a rare movement draws more attention to
        /// itself than a constant one does, so the thing meant to clear a path
        /// for the alert ended up being the event.
        /// <para>
        /// The code stays, deliberately: the reasoning behind #116 is unchanged,
        /// and remote operators, for whom PC audio is the only audio path there
        /// is, are exactly the people who may still want it. This is a change of
        /// default, not a retraction. What changes is who has to make a choice -
        /// before, everyone got the dip and had to find the switch to escape it;
        /// now nobody gets it until they ask for it.
        /// </para>
        /// <para>
        /// <b>An existing config keeps whatever it stored.</b> XmlSerializer only
        /// falls back to this initialiser when the element is ABSENT, so this
        /// reaches new installs and operators who last saved before the setting
        /// existed. Anyone whose file already carries a value keeps it, which is
        /// correct - we do not silently reverse a setting somebody may have
        /// chosen - but it does mean a tester on an older config still gets the
        /// dip until they turn it off themselves.
        /// </para>
        /// </remarks>
        public bool RxDuckEnabled { get; set; } = false;

        /// <summary>
        /// How far the band audio dips under a warning, in dB. Modest by
        /// default — an operator copying weak CW under a warning will not
        /// thank us for a large hole in the band.
        /// </summary>
        public float RxDuckDepthDb { get; set; } = RxDuck.DefaultDepthDb;

        /// <summary>
        /// The one meter list (Track D2): each entry is a source plus a range
        /// plus a voice, with audibility and readability as properties of the
        /// same meter. Empty = never configured; the engine seeds from the
        /// preset named in <see cref="MeterPreset"/>.
        /// </summary>
        public List<MeterDefinition> Meters { get; set; } = new();

        private const string FileName = "audioConfig.xml";

        /// <summary>
        /// The one directory this config lives in from 4.1.17 on: the config
        /// root (<c>%AppData%\JJFlexRadio</c>). Callers historically passed
        /// either the root or its <c>Radios</c> subdirectory, so any incoming
        /// directory is normalized here rather than trusting the caller.
        /// </summary>
        /// <remarks>
        /// History, so the next reader does not re-derive it: this file spent
        /// its life written to two places because the callers disagreed about
        /// its home — MainWindow loaded from the root while Settings saved to
        /// <c>Radios\</c>, and FreqOutHandlers patched around the split by
        /// writing both. Found 2026-08-13; made SAFE the same day (Load took
        /// whichever copy was newer, Save wrote both); made CORRECT 2026-08-16
        /// (this migration). The root is canonical because everything in this
        /// config is operator/app-level — verbosity, earcons, CW speed — and
        /// the <c>Radios</c> subdirectory belongs to rig files.
        ///
        /// <para>
        /// The migration contract: <see cref="Save"/> writes ONLY the
        /// canonical root copy. <see cref="Load"/> still honors a newer (or
        /// lone) legacy <c>Radios\audioConfig.xml</c> for ONE release — an
        /// operator upgrading from a build whose last save landed in the
        /// legacy spot must not lose that save — and heals forward by writing
        /// what it read to the canonical path. The legacy file itself is left
        /// in place untouched so a downgrade to the previous release still
        /// finds it.
        /// </para>
        ///
        /// <para>
        /// REMOVE IN THE RELEASE AFTER 4.1.17: the legacy read
        /// (<see cref="LegacyDir"/> and its uses in <see cref="Load"/>), and
        /// delete the stale <c>Radios\audioConfig.xml</c> at that point.
        /// </para>
        /// </remarks>
        private static string CanonicalDir(string configDir)
        {
            if (string.IsNullOrWhiteSpace(configDir)) return configDir;
            string trimmed = configDir.TrimEnd(Path.DirectorySeparatorChar,
                                               Path.AltDirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(trimmed), "Radios",
                              StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(trimmed) ?? trimmed;
            return trimmed;
        }

        /// <summary>
        /// Where the pre-4.1.17 split left a second copy. Read-only support
        /// for one release; never written to.
        /// </summary>
        private static string? LegacyDir(string canonicalDir)
        {
            if (string.IsNullOrWhiteSpace(canonicalDir)) return null;
            return Path.Combine(canonicalDir, "Radios");
        }

        private static AudioOutputConfig? ReadFrom(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var serializer = new XmlSerializer(typeof(AudioOutputConfig));
                using var stream = File.OpenRead(path);
                var loaded = (AudioOutputConfig?)serializer.Deserialize(stream);

                // Meter migration happens HERE rather than at the end of Load,
                // so that the heal-forward path — which copies a legacy file to
                // the canonical location — writes the migrated shape rather
                // than immortalising the old one.
                if (loaded != null) MeterConfigMigration.Migrate(loaded);
                return loaded;
            }
            catch (Exception ex)
            {
                // A corrupt copy must not shadow a good one, so this returns
                // null and lets the caller fall through to the sibling rather
                // than handing back defaults that look like real settings.
                Trace.WriteLine($"AudioOutputConfig.ReadFrom({path}) failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Read the config, bringing its meter settings forward to the current
        /// model on the way through. Every path out of here — a real file, a
        /// healed legacy file, or a first-run default — comes back migrated and
        /// version-stamped, because a config that skipped the migration would
        /// be written back to disk still carrying legacy source keys.
        /// </summary>
        public static AudioOutputConfig Load(string configDir)
        {
            AudioOutputConfig config = LoadCore(configDir);
            // Idempotent and version-guarded: ReadFrom has usually done this
            // already, and this call is what catches the default-construction
            // paths below.
            MeterConfigMigration.Migrate(config);
            return config;
        }

        private static AudioOutputConfig LoadCore(string configDir)
        {
            string canonicalDir = CanonicalDir(configDir);
            string path = Path.Combine(canonicalDir, FileName);
            string? legDir = LegacyDir(canonicalDir);
            string? legPath = legDir == null ? null : Path.Combine(legDir, FileName);

            bool canonicalExists = File.Exists(path);
            bool legacyExists = legPath != null && File.Exists(legPath);

            // Migration window (one release): a build from before the
            // consolidation may have made its last save to the legacy copy, so
            // a strictly-newer legacy file still wins — and gets healed
            // forward to the canonical path so this branch runs at most once
            // per divergence. After the first canonical save, canonical is
            // always newest and the legacy file just sits there for downgrade
            // safety.
            if (canonicalExists && legacyExists)
            {
                DateTime here = File.GetLastWriteTimeUtc(path);
                DateTime there = File.GetLastWriteTimeUtc(legPath!);
                if (there > here)
                {
                    var fromLegacy = ReadFrom(legPath!);
                    if (fromLegacy != null)
                    {
                        Trace.WriteLine("AudioOutputConfig.Load: legacy copy is newer, migrating "
                            + legPath + " forward to " + path);
                        fromLegacy.WriteTo(canonicalDir);
                        return fromLegacy;
                    }
                    return ReadFrom(path) ?? new AudioOutputConfig();
                }
                return ReadFrom(path) ?? ReadFrom(legPath!) ?? new AudioOutputConfig();
            }

            if (canonicalExists) return ReadFrom(path) ?? new AudioOutputConfig();

            if (legacyExists)
            {
                var fromLegacy = ReadFrom(legPath!);
                if (fromLegacy != null)
                {
                    Trace.WriteLine("AudioOutputConfig.Load: only the legacy copy exists, migrating "
                        + legPath + " forward to " + path);
                    fromLegacy.WriteTo(canonicalDir);
                    return fromLegacy;
                }
            }
            return new AudioOutputConfig();
        }

        public void Save(string configDir)
        {
            // Canonical root only. The legacy Radios\ copy is deliberately
            // NOT refreshed: it exists solely so the previous release still
            // has something to read on a downgrade, and Load ignores it once
            // the canonical copy is newer.
            WriteTo(CanonicalDir(configDir));
        }

        private void WriteTo(string configDir)
        {
            try
            {
                Directory.CreateDirectory(configDir);
                string path = Path.Combine(configDir, FileName);
                var serializer = new XmlSerializer(typeof(AudioOutputConfig));
                using var stream = File.Create(path);
                serializer.Serialize(stream, this);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"AudioOutputConfig.Save({configDir}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply ONLY the saved speech verbosity, as early as possible.
        ///
        /// **Why this exists.** The full <see cref="Apply"/> runs from
        /// MainWindow, which does not exist until the operator has chosen a
        /// radio and connected. So until 2026-08-18 the saved verbosity was not
        /// in force for ANY of startup: the greeting, the connect dialog and
        /// the entire connect sequence all spoke at the Chatty default no
        /// matter what the operator had chosen. Set it to Terse, restart, and
        /// startup was still chatty - which looks exactly like the setting
        /// failing to save.
        ///
        /// Verbosity is the one setting that must be live before the first
        /// word is spoken, because it decides whether there IS a first word.
        ///
        /// Derives the config directory itself rather than taking one: this
        /// runs before the application has worked out its own paths, which is
        /// the whole point of it.
        /// </summary>
        /// <summary>Read the saved Network Identity panel state.</summary>
        ///
        /// Derives its own path for the same reason ApplySpeechVerbosityEarly
        /// does: the caller is a dialog that should not have to be handed a
        /// config directory to remember one checkbox.
        public static bool GetNetworkIdentityExpanded()
        {
            try
            {
                string dir = SettingsDirectory();
                if (dir == null) return false;
                return Load(dir)?.NetworkIdentityExpanded ?? false;
            }
            catch { return false; }
        }

        /// <summary>Remember the Network Identity panel state.</summary>
        ///
        /// Read-modify-write so the rest of the config is not clobbered by
        /// saving one flag. Failure is silent by design: a panel that will not
        /// remember its state is a papercut, and an exception thrown out of a
        /// dialog's Expanded handler is a crash.
        public static void SetNetworkIdentityExpanded(bool expanded)
        {
            try
            {
                string dir = SettingsDirectory();
                if (dir == null) return;

                var cfg = Load(dir);
                if (cfg == null || cfg.NetworkIdentityExpanded == expanded) return;

                cfg.NetworkIdentityExpanded = expanded;
                cfg.Save(dir);
            }
            catch { }
        }

        private static string SettingsDirectory()
        {
            string dir = System.IO.Path.Combine(
                Radios.RadioConfig.AppDataRoot);
            return System.IO.Directory.Exists(dir) ? dir : null;
        }

        public static void ApplySpeechVerbosityEarly()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Radios.RadioConfig.AppDataRoot);
                if (!System.IO.Directory.Exists(dir)) return;

                var cfg = Load(dir);
                if (cfg == null) return;

                Radios.ScreenReaderOutput.CurrentVerbosity =
                    (Radios.VerbosityLevel)Math.Clamp(cfg.SpeechVerbosity, 0, 3);
            }
            catch
            {
                // A missing or unreadable config is an ordinary first-run
                // outcome. The Chatty default stands and the app still talks -
                // which is the failure mode to prefer, since the alternative is
                // a blind operator meeting a silent application.
            }
        }

        /// <summary>Apply this config to the MeterToneEngine and EarconPlayer.</summary>
        public void Apply()
        {
            EarconPlayer.EarconsEnabled = EarconsEnabled;
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.Connection, EarconConnectionEnabled);
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.Transmit, EarconTransmitEnabled);
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.DialogsAndPanels, EarconDialogsEnabled);
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.TuningAndFilters, EarconTuningEnabled);
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.CommandsAndConfirmations, EarconCommandsEnabled);
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.Warnings, EarconWarningsEnabled);
            // #147 — which set of voice definitions the earcons resolve
            // against. Applied before anything can make a sound, and clamped
            // rather than cast blind: a hand-edited config saying 7 should get
            // the shipped sounds, not an exception on the audio path.
            EarconVoices.ActiveSet =
                EarconVoiceSet == (int)JJFlexWpf.EarconVoiceSet.Simple
                    ? JJFlexWpf.EarconVoiceSet.Simple
                    : JJFlexWpf.EarconVoiceSet.Rich;

            // Per-sound level trims. Restored wholesale; the player clamps and
            // drops anything meaningless, so a hand-edited file cannot leave an
            // earcon silenced or running at four times its tier.
            var trims = new List<KeyValuePair<string, float>>();
            if (EarconLevelTrims != null)
                foreach (var t in EarconLevelTrims)
                    if (t != null && !string.IsNullOrEmpty(t.Id))
                        trims.Add(new KeyValuePair<string, float>(t.Id, t.Db));
            EarconPlayer.SetAllLevelTrimsDb(trims);

            // #116 — the warning duck. Both clamp in their setters, so a
            // hand-edited file cannot leave the band permanently attenuated.
            RxDuck.Enabled = RxDuckEnabled;
            RxDuck.DepthDb = RxDuckDepthDb;

            EarconPlayer.MasterVolume = MasterVolume;
            EarconPlayer.AlertVolume = AlertVolume;
            EarconPlayer.SetAlertDevice(EarconDeviceNumber);
            EarconPlayer.SetMeterDevice(MeterDeviceNumber);

            // Verbosity
            Radios.ScreenReaderOutput.CurrentVerbosity =
                (Radios.VerbosityLevel)Math.Clamp(SpeechVerbosity, 0, 3);

            // PC output volume — static on FlexBase so it is in place before
            // any radio connects; remote-audio startup reads it from there.
            Radios.FlexBase.PcOutputVolumeDbSetting = PcOutputVolumeDb;

            // Same reasoning: it has to be in place before a radio connects,
            // because the transmit encoder is built during connect. The setter
            // refuses anything Opus cannot encode, so a hand-edited or
            // corrupted file falls back to the default rather than opening a
            // stream the codec cannot follow.
            Radios.FlexBase.OpusTxSampleRateSetting = (uint)OpusTxSampleRate;

            // Same shape again: the disconnect offer is decided inside the
            // Radios layer, which cannot see this class, so the preference
            // crosses as a static. One knob for every radio and every
            // connection — the per-radio half of the decision is ownership,
            // and that already lives in RadioConfig.
            Radios.FlexBase.OfferStationSaveOnDisconnect = OfferStationSaveOnDisconnect;

            // Mic-verdict wording, same reasoning: every surface that reads a
            // level out loud asks MicAudioReport, so the preference lives
            // there rather than being looked up four different ways.
            MicAudioReport.VerdictMode =
                (MicVerdictOutputMode)Math.Clamp(MicVerdictOutput, 0, 2);

            // #128: restoring saved state is not a toggle. The engine's
            // operator booleans tone in their setters (so every road answers
            // back); this is the one writer that must not — a launch that
            // chimed once per restored setting would be the #58 chime storm.
            MeterToneEngine.QuietStateRestore = true;
            try
            {
                MeterToneEngine.Enabled = MeterTonesEnabled;
                MeterToneEngine.MasterVolume = MeterMasterVolume;
                MeterToneEngine.PeakWatcherEnabled = PeakWatcherEnabled;
                MeterToneEngine.SpeechEnabled = MeterSpeechEnabled;
                MeterToneEngine.SpeechIntervalSeconds = Math.Clamp(MeterSpeechIntervalSeconds, 1, 10);
                MeterToneEngine.AutoEnableOnTune = AutoEnableOnTune;
                MeterToneEngine.SpeechTimerActive = MeterSpeechTimerActive;
            }
            finally
            {
                MeterToneEngine.QuietStateRestore = false;
            }

            // Voices before meters: definitions resolve voices by name.
            MeterVoiceLibrary.SetUserVoices(UserVoices);
            if (Meters is { Count: > 0 })
                MeterToneEngine.LoadDefinitions(Meters);
            else
                MeterToneEngine.ApplyPreset(MeterPreset ?? "RX Monitor");
        }

        /// <summary>Capture current state from the engine into this config.</summary>
        public void CaptureFromEngine()
        {
            SpeechVerbosity = (int)Radios.ScreenReaderOutput.CurrentVerbosity;
            PcOutputVolumeDb = Radios.FlexBase.PcOutputVolumeDbSetting;
            OpusTxSampleRate = (int)Radios.FlexBase.OpusTxSampleRateSetting;
            MeterTonesEnabled = MeterToneEngine.Enabled;
            MeterPreset = MeterToneEngine.CurrentPreset;
            MeterMasterVolume = MeterToneEngine.MasterVolume;
            PeakWatcherEnabled = MeterToneEngine.PeakWatcherEnabled;
            MeterSpeechEnabled = MeterToneEngine.SpeechEnabled;
            MeterSpeechTimerActive = MeterToneEngine.SpeechTimerActive;
            MeterSpeechIntervalSeconds = MeterToneEngine.SpeechIntervalSeconds;
            AutoEnableOnTune = MeterToneEngine.AutoEnableOnTune;
            EarconsEnabled = EarconPlayer.EarconsEnabled;
            EarconVoiceSet = (int)EarconVoices.ActiveSet;
            EarconConnectionEnabled = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.Connection);
            EarconTransmitEnabled = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.Transmit);
            EarconDialogsEnabled = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.DialogsAndPanels);
            EarconTuningEnabled = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.TuningAndFilters);
            EarconCommandsEnabled = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.CommandsAndConfirmations);
            EarconWarningsEnabled = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.Warnings);
            MasterVolume = EarconPlayer.MasterVolume;
            AlertVolume = EarconPlayer.AlertVolume;
            MasterEarconVolume = (int)(EarconPlayer.AlertVolume * 100);
            EarconDeviceNumber = EarconPlayer.GetAlertDeviceNumber();
            MeterDeviceNumber = EarconPlayer.GetMeterDeviceNumber();
            Meters = MeterToneEngine.ExportDefinitions();
            UserVoices = MeterVoiceLibrary.GetUserVoices();

            // Sorted by id so the file has a stable order — a config that
            // reshuffles a section on every save makes every diff of it
            // useless for working out what actually changed.
            var trimList = new List<EarconLevelTrim>();
            foreach (var kv in EarconPlayer.GetAllLevelTrimsDb())
                trimList.Add(new EarconLevelTrim { Id = kv.Key, Db = kv.Value });
            trimList.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            EarconLevelTrims = trimList;

            RxDuckEnabled = RxDuck.Enabled;
            RxDuckDepthDb = RxDuck.DepthDb;

            // What the engine hands back is always current-model, so say so.
            // The migration is a no-op on an already-current file even without
            // this stamp — every radio name it produces resolves to itself —
            // but an unstamped save would claim the file had never been
            // migrated, which is a lie about the file's own contents.
            MeterConfigVersion = MeterConfigMigration.CurrentVersion;
        }
    }

    /// <summary>
    /// How spoken mic-audio verdicts read. Values are the stored ints in
    /// <see cref="AudioOutputConfig.MicVerdictOutput"/>.
    /// </summary>
    public enum MicVerdictOutputMode
    {
        /// <summary>Plain English plus the figures (default):
        /// "Good. That's the sweet spot, right there. Peak -9 dBFS, loudness -19 LUFS".</summary>
        Both = 0,
        /// <summary>Plain English only: "Good. That's the sweet spot, right there."</summary>
        Plain = 1,
        /// <summary>Figures only: "peak -12 dBFS, loudness -19 LUFS".</summary>
        Numbers = 2,
    }

    /// <summary>Frequency entry typing sound mode.</summary>
    public enum TypingSoundMode
    {
        /// <summary>Random musical notes from C4-C8 (always available). Display: "Musical notes".</summary>
        Beep,
        /// <summary>No sound on keystrokes.</summary>
        Off,
        /// <summary>Mechanical keyboard sounds (requires calibration unlock).</summary>
        Mechanical,
        /// <summary>DTMF touch-tone sounds (requires calibration unlock).</summary>
        TouchTone,
        /// <summary>Fixed pitch beep every keystroke (always available).</summary>
        SingleTone,
        /// <summary>Random frequency beep, not snapped to musical notes (always available).</summary>
        RandomTones
    }

    /// <summary>
    /// Per-slot configuration for XML serialization. LEGACY — nothing writes
    /// this any more; it exists so a config file from before the one-meter-list
    /// rework still deserializes and can be migrated forward.
    /// </summary>
    /// <remarks>
    /// <see cref="Source"/> is a STRING rather than the old enum on purpose.
    /// The enum is gone, and the field on disk is not reliably one shape: an
    /// XmlSerializer writes an enum by member name, but the same field has been
    /// round-tripped through an ordinal, so a real file may hold either "Power"
    /// or "3". A string accepts both and hands the ambiguity to
    /// <see cref="MeterConfigMigration.ResolveLegacySource"/>, which is the one
    /// place that knows the historical order. Typing it as an enum would make
    /// the deserializer throw on an integer and lose the whole file.
    /// </remarks>
    public class MeterSlotConfig
    {
        public string Source { get; set; } = "";
        public bool Enabled { get; set; }
        public float Volume { get; set; } = 0.5f;
        public float Pan { get; set; }
        public int PitchLow { get; set; } = 200;
        public int PitchHigh { get; set; } = 1200;
        public WaveformType Waveform { get; set; } = WaveformType.Sine;
    }
}

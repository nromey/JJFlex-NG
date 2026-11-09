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

        /// <summary>
        /// The alert channel's output buffer, milliseconds per buffer and how
        /// many (#855, ruled 2026-10-30: an operator setting, the default by
        /// measurement). Clamped by <see cref="EarconPlayer.SetAlertBuffer"/>,
        /// so a hand-edited file cannot set one that stalls the device.
        /// </summary>
        public int AlertBufferMs { get; set; } = EarconPlayer.DefaultAlertBufferMs;
        public int AlertBufferCount { get; set; } = EarconPlayer.DefaultAlertBufferCount;

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
        public bool RecorderBeepsEnabled { get; set; } = true;
        public bool RecorderSpeechEnabled { get; set; } = true;

        /// <summary>
        /// "Warn before a recording on the radio is overwritten", chosen in
        /// Settings before any radio had connected (#999 round 3, ruled C5a by
        /// Noel 2026-11-08): "true", "false", or empty when no choice is waiting.
        /// It waits here, across restarts, until a radio actually connects, which
        /// then takes it into its own config and clears this
        /// (<see cref="Radios.RadioConfig.PendingWarnBeforeSliceRecordingOverwritten"/>).
        /// </summary>
        /// <remarks>
        /// <para><b>Written only by <see cref="PendingSliceOverwriteWarningStore"/>.</b>
        /// Every other save carries over whatever the file already holds
        /// (<see cref="WriteTo"/>), because the live config is loaded once and
        /// saved again much later, at power-off: a value read into it then would
        /// write a choice the connect has since spent back over the cleared one,
        /// and the next start would apply it a second time.</para>
        /// <para>Absent from an older audioConfig.xml, which reads as no choice
        /// waiting; an older build reading a newer file skips the element.</para>
        /// </remarks>
        [XmlElement("PendingWarnBeforeSliceRecordingOverwritten")]
        public string PendingSliceOverwriteWarning { get; set; } = "";

        /// <summary>
        /// The VOX send box (#835): with VOX on, sends key the radio themselves. Off by
        /// default, and absent from an older audioConfig.xml, which deserializes
        /// to this initializer, so nobody upgrading finds it on.
        /// </summary>
        public bool VoxSendKeysRadio { get; set; }

        /// <summary>
        /// Recorded QSOs' step size in seconds, remembered across restarts
        /// (Noel, 2026-10-25). The dialog reads and writes it itself; a value
        /// the combo does not offer falls back to 5 seconds.
        /// </summary>
        public double RecordedQsosStepSeconds { get; set; } = 5;
        /// <summary>The dialog open and close tones (#821). On by default.</summary>
        public bool EarconDialogTonesEnabled { get; set; } = true;
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
        /// <para><b>It stays off because of #499, not in spite of it.</b> #499's
        /// settled design saves the station automatically, and this question
        /// becomes the fallback for an operator who turns that save off. On by
        /// default, the question would ask nearly every session, because
        /// OperatorChangedStationThisSession is true whenever a ham has tuned
        /// around or nudged a filter. A default-on with a one-time migration
        /// was built and backed out on 2026-10-27 (#759); a file that build
        /// wrote carries a StationSaveOfferVersion element, which the
        /// serializer now ignores, so whatever value that build left stands
        /// until the operator changes it.</para>
        ///
        /// <para>Absent from an older audioConfig.xml this deserialises to
        /// false, which is exactly the behaviour that file was written
        /// under.</para>
        /// </summary>
        public bool OfferStationSaveOnDisconnect { get; set; }

        /// <summary>
        /// The operator ticked "don't ask me again" on the disconnect question:
        /// switch the offer off now and on disk. The live config, when there is
        /// one, is changed as well as the file, because it is saved again at
        /// power-off and would otherwise write the old "on" back over this.
        /// There is one audioConfig.xml (see <see cref="CanonicalDir"/>), so
        /// one save covers every radio.
        /// </summary>
        internal static void RecordStationSaveOfferTurnedOff(AudioOutputConfig? live, string configDir)
        {
            Radios.FlexBase.OfferStationSaveOnDisconnect = false;
            var config = live ?? Load(configDir);
            config.OfferStationSaveOnDisconnect = false;
            config.Save(configDir);
            Trace.WriteLine("AudioOutputConfig: the operator turned off the offer to save the station on disconnect");
        }

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
        /// Ctrl+J, Alt+K mic check, and the two reading fields — those are
        /// read-only edits precisely so a screen reader speaks them, which
        /// makes them a spoken surface too): 0 = plain English plus the
        /// figures (both — the conservative default, exactly what shipped
        /// before this setting), 1 = plain English only, 2 = figures only.
        /// Stored as int for XML serialization; see
        /// <see cref="MicVerdictOutputMode"/>.
        /// Noel asked for this explicitly (Audio Arc, 2026-08-11).
        /// </summary>
        public int MicVerdictOutput { get; set; } = (int)MicVerdictOutputMode.Both;

        /// <summary>
        /// Which slice F reads when the transmit slice is not the selected one
        /// (#642): 0 = both, transmit first (the default), 1 = the transmit
        /// slice, 2 = the selected slice. Ruled by Noel 2026-10-18, "Active,
        /// transmit, or both". Stored as int; see
        /// <see cref="Radios.FrequencyKeySlices"/>.
        /// </summary>
        public int FrequencyKeyReads { get; set; } = (int)Radios.FrequencyKeySlices.Both;

        /// <summary>
        /// What a door says and plays when it keys a transmit slice that is
        /// not the selected one (#863): 0 = speak and tone (the default),
        /// 1 = tone only, 2 = off. Ruled by Noel 2026-11-03. Stored as int;
        /// see <see cref="Radios.TransmitElsewhereNotice"/>. Shown on the PTT
        /// tab beside "Name the slice when announcing transmit and receive",
        /// but kept here, with the operator's other speech preferences, so a
        /// change made with no radio connected is saved like they are.
        /// </summary>
        public int TransmitElsewhereNotice { get; set; } = (int)Radios.TransmitElsewhereNotice.SpeakAndTone;

        /// <summary>
        /// The preferences that describe the OPERATOR rather than a radio and
        /// were added without a per-radio meaning: copied from the root config
        /// over a per-radio one, so the root (written by every Settings OK,
        /// with a radio or without) is the one place they are decided. Without
        /// this a choice made with no radio connected was saved, and then the
        /// next connect loaded the radio's own older copy over it (#642).
        /// </summary>
        public void TakeOperatorChoicesFrom(AudioOutputConfig root)
        {
            if (root == null) return;
            FrequencyKeyReads = root.FrequencyKeyReads;
            TransmitElsewhereNotice = root.TransmitElsewhereNotice;
        }

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
        /// Which generation of the countdown's defaults this file carries.
        /// Absent (0) before the default length moved from 5 beats to 4
        /// (2026-10-29); <see cref="Radios.CountdownSettings.UpgradeDefaults"/>
        /// brings it forward on load. Do not bump it by hand.
        /// </summary>
        public int CountdownDefaultsVersion { get; set; }

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
        /// How the dip moves (#535): the name of an
        /// <see cref="RxDuckTimingPreset"/> — "Quick", "Gentle" or "Lingering".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A string, not the enum, on purpose.</b> XmlSerializer throws on an
        /// enum name it does not know, <see cref="ReadFrom"/> swallows the
        /// exception, and <see cref="Load"/> then hands back a fresh config —
        /// so a preset added by a later build would, on a downgrade, silently
        /// reset every audio setting in this file. A string reaches
        /// <see cref="RxDuck.ParseTiming"/>, which falls back to the default
        /// and leaves everything else alone. The same lenience covers a hand
        /// edit.
        /// </para>
        /// <para>
        /// Quick is the shipped timing and the control arm of the #535
        /// hypothesis. This initialiser and <see cref="RxDuck.DefaultTiming"/>
        /// must agree, for the same reason the two duck-enabled defaults must.
        /// </para>
        /// </remarks>
        public string RxDuckTiming { get; set; } = RxDuck.TimingName(RxDuck.DefaultTiming);

        /// <summary>
        /// What the radio's audio does on this computer while a recording plays
        /// back (#787): the name of a <see cref="PlaybackRadioLevel"/>, lowered
        /// 12 dB by default. A string for the reason given on
        /// <see cref="RxDuckTiming"/>.
        /// </summary>
        public string RecordingPlaybackRadioLevel { get; set; } = RxPlaybackLevel.LevelName(RxPlaybackLevel.DefaultLevel);

        /// <summary>
        /// The one meter list (Track D2): each entry is a source plus a range
        /// plus a voice, with audibility and readability as properties of the
        /// same meter. Empty = never configured; the engine seeds from the
        /// preset named in <see cref="MeterPreset"/>.
        /// </summary>
        public List<MeterDefinition> Meters { get; set; } = new();

        /// <summary>
        /// The one countdown (#835): on or off, its length, the pip's pitch and
        /// length, and the lead-in. Saved app-wide, and made live through
        /// <see cref="Radios.CountdownSettings.Current"/>, which clamps it, so a
        /// hand-edited file cannot reach the sound or the keying out of range.
        /// Absent from an older audioConfig.xml, which deserializes to the
        /// ruled defaults: on, five seconds, 300 Hz, 300 ms, lead-in on.
        /// </summary>
        public Radios.CountdownSettings Countdown { get; set; } = new();

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
                if (loaded != null)
                {
                    MeterConfigMigration.Migrate(loaded);
                    loaded.UpgradeCountdownDefaults();
                }
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
            config.UpgradeCountdownDefaults();
            return config;
        }

        /// <summary>Move a countdown still on the old default length to the new one, once (2026-10-29).</summary>
        internal void UpgradeCountdownDefaults()
        {
            int before = CountdownDefaultsVersion;
            int was = Countdown?.Seconds ?? 0;
            CountdownDefaultsVersion = Radios.CountdownSettings.UpgradeDefaults(Countdown, before);
            if (before < Radios.CountdownSettings.CurrentDefaultsVersion && Countdown != null && Countdown.Seconds != was)
                Trace.WriteLine($"AudioOutputConfig: the countdown was on the old default of {was} beats; it is now {Countdown.Seconds}.");
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

        /// <summary>
        /// Settings OK or Apply: carry the user-scope fields of the config the
        /// dialog committed into the root config on disk, leaving the rest of
        /// the root file as it was. Moved here from the menu bar's Settings
        /// handler verbatim (#855) so a test can run the path Settings runs.
        /// </summary>
        /// <remarks>
        /// <para>With no radio connected this is the ONLY persistence a
        /// Settings change gets (the full capture-and-save in
        /// MainWindow.ApplySettingsChanges runs only when connected), so a
        /// field missing from this list is applied and then lost at the next
        /// start, and the next Settings OK commits the old value back over the
        /// live one.</para>
        /// <para>CW fields matter at app startup BEFORE any connect: the
        /// MainWindow constructor loads these from root so CW delegates are
        /// live and CwNotificationsEnabled is set in time for AS to fire at
        /// connect-start.</para>
        /// </remarks>
        public static void PersistUserScope(AudioOutputConfig chosen, string rootConfigDir)
        {
            var rootConfig = Load(rootConfigDir);
            rootConfig.TuningHash = chosen.TuningHash;
            rootConfig.TypingSound = chosen.TypingSound;
            rootConfig.CwNotificationsEnabled = chosen.CwNotificationsEnabled;
            rootConfig.CwModeAnnounce = chosen.CwModeAnnounce;
            rootConfig.CwSidetoneHz = chosen.CwSidetoneHz;
            rootConfig.CwSpeedWpm = chosen.CwSpeedWpm;
            // Sprint 33 Track K. Settings are intents, not commands: the
            // operator may well turn this on while disconnected — it is a
            // preference ABOUT disconnecting — and without this line it
            // would be accepted, announced, and silently lost, which is the
            // exact defect class the intents rule exists to prevent.
            rootConfig.OfferStationSaveOnDisconnect = chosen.OfferStationSaveOnDisconnect;
            // Sprint 33 Track F: the pitch source, the keying waveform and
            // the alert voice set are user-scope for the same reason the four
            // above are — they describe this operator's ears, not this radio.
            // Left out of this list they would apply until the next restart
            // and then quietly revert, which is worse than not offering them.
            rootConfig.CwPitchFollowsRadio = chosen.CwPitchFollowsRadio;
            rootConfig.CwWaveform = chosen.CwWaveform;
            rootConfig.EarconVoiceSet = chosen.EarconVoiceSet;
            // The per-sound loudness trims and the receive duck, which the
            // dialog captures back from the live engine before it commits. Both
            // describe this operator's ears. Missing here, a trim set with no
            // radio connected played at once and was gone at the next start.
            rootConfig.EarconLevelTrims = (chosen.EarconLevelTrims ?? new List<EarconLevelTrim>())
                .ConvertAll(t => new EarconLevelTrim { Id = t.Id, Db = t.Db });
            rootConfig.RxDuckEnabled = chosen.RxDuckEnabled;
            rootConfig.RxDuckDepthDb = chosen.RxDuckDepthDb;
            rootConfig.RxDuckTiming = chosen.RxDuckTiming;
            // #855: the alert sound delay describes this computer's sound output,
            // not the radio. Missing from this list it applied live, then came
            // back as the default on reopening Settings, and the next OK put the
            // default back over the live choice (found by the 2026-10-30 harness run).
            rootConfig.AlertBufferMs = chosen.AlertBufferMs;
            rootConfig.AlertBufferCount = chosen.AlertBufferCount;
            // #835, round 1030 (#856): the countdown is this operator's too, set in
            // the same dialog; missing here, a change made with no radio connected
            // applied live and was then lost. (Carried in when sound timing
            // moved this list out of the menu bar and met Check your audio on main.)
            rootConfig.Countdown = chosen.Countdown.Clone();
            // #642 and #863: which slice F reads, and what a door says when it
            // keys a slice other than the selected one. Both describe what this
            // operator wants to hear; missing here, a change made with no radio
            // connected applied live and was gone when Settings reopened.
            rootConfig.FrequencyKeyReads = chosen.FrequencyKeyReads;
            rootConfig.TransmitElsewhereNotice = chosen.TransmitElsewhereNotice;
            rootConfig.Save(rootConfigDir);
        }

        public void Save(string configDir)
        {
            // Canonical root only. The legacy Radios\ copy is deliberately
            // NOT refreshed: it exists solely so the previous release still
            // has something to read on a downgrade, and Load ignores it once
            // the canonical copy is newer.
            WriteTo(CanonicalDir(configDir));
        }

        private bool WriteTo(string configDir, bool writesPendingSliceOverwriteWarning = false)
        {
            try
            {
                Directory.CreateDirectory(configDir);
                string path = Path.Combine(configDir, FileName);
                // The waiting overwrite-warning choice has one writer (see the
                // property). Every other save keeps what the file holds.
                if (!writesPendingSliceOverwriteWarning && PendingSliceOverwriteWarningOnDisk(path) is string held)
                    PendingSliceOverwriteWarning = held;
                // Anything this build writes is past the four-beat move, so a 5
                // saved now is a choice and must not be "upgraded" on the next
                // load, whichever path built this config (2026-10-29).
                CountdownDefaultsVersion = Radios.CountdownSettings.CurrentDefaultsVersion;
                var serializer = new XmlSerializer(typeof(AudioOutputConfig));
                using var stream = File.Create(path);
                serializer.Serialize(stream, this);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"AudioOutputConfig.Save({configDir}) failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The waiting overwrite-warning choice as the file at <paramref name="path"/>
        /// holds it ("" for none), or null when there is no readable file to keep it from.
        /// </summary>
        private static string? PendingSliceOverwriteWarningOnDisk(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var root = System.Xml.Linq.XDocument.Load(path).Root;
                return root?.Element("PendingWarnBeforeSliceRecordingOverwritten")?.Value ?? "";
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"AudioOutputConfig: could not read the waiting overwrite-warning choice from {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The app-wide home of a "warn before a recording on the radio is
        /// overwritten" choice made before any radio connected (#999 round 3,
        /// C5a), for <see cref="Radios.RadioConfig.PendingWarnStore"/>. Reads and
        /// writes this config's one field through the canonical audioConfig.xml,
        /// changing nothing else in it.
        /// </summary>
        internal sealed class PendingSliceOverwriteWarningStore : Radios.IPendingChoiceStore
        {
            private readonly Func<string> _directory;

            /// <param name="directory">The config root, resolved at each use (the app passes <see cref="Radios.RadioConfig.AppDataRoot"/>).</param>
            public PendingSliceOverwriteWarningStore(Func<string> directory) => _directory = directory;

            public bool? Read()
            {
                string dir = _directory();
                if (string.IsNullOrEmpty(dir)) return null;
                return Load(dir).PendingSliceOverwriteWarning switch
                {
                    "true" => true,
                    "false" => false,
                    _ => null,
                };
            }

            public bool Write(bool? value)
            {
                string dir = _directory();
                if (string.IsNullOrEmpty(dir)) return false;
                var config = Load(dir);
                config.PendingSliceOverwriteWarning = value switch { true => "true", false => "false", null => "" };
                return config.WriteTo(CanonicalDir(dir), writesPendingSliceOverwriteWarning: true);
            }
        }

        /// <summary>
        /// Keep a choice made before any radio connected across a restart (#999
        /// round 3, C5a). Called once, at startup, before Settings can open or a
        /// radio connect.
        /// </summary>
        public static void KeepPendingSliceOverwriteWarningAcrossRestarts() =>
            Radios.RadioConfig.PendingWarnStore = new PendingSliceOverwriteWarningStore(() => Radios.RadioConfig.AppDataRoot);

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
            EarconPlayer.RecorderBeepsEnabled = RecorderBeepsEnabled;
            EarconPlayer.DialogTonesEnabled = EarconDialogTonesEnabled;
            RecorderControls.StatusSpeechEnabled = RecorderSpeechEnabled;
            Radios.VoxSendBox.Ticked = VoxSendKeysRadio;
            // The one countdown (#835); the setter keeps a clamped copy.
            Radios.CountdownSettings.Current = Countdown;
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

            // #116 — the warning duck. Depth clamps in its setter and the timing
            // name parses leniently, so a hand-edited file cannot leave the band
            // permanently attenuated or fail to load.
            RxDuck.Enabled = RxDuckEnabled;
            RxDuck.DepthDb = RxDuckDepthDb;
            RxDuck.Timing = RxDuck.ParseTiming(RxDuckTiming);
            RxPlaybackLevel.Level = RxPlaybackLevel.ParseLevel(RecordingPlaybackRadioLevel);

            EarconPlayer.MasterVolume = MasterVolume;
            EarconPlayer.AlertVolume = AlertVolume;
            // Before the device, so a reopened device opens with the new buffer.
            EarconPlayer.SetAlertBuffer(AlertBufferMs, AlertBufferCount);
            EarconPlayer.SetAlertDevice(EarconDeviceNumber);
            // #855: the held sounds follow the switches, the voice set and the
            // countdown just applied above.
            EarconPlayer.PrewarmSoon();
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

            // Which slice F reads (#642), the same way: the readout lives in
            // Radios, which cannot see this class.
            Radios.FrequencyKeyReadout.Setting =
                (Radios.FrequencyKeySlices)Math.Clamp(FrequencyKeyReads, 0, 2);

            // What a door does when it keys a slice other than the selected
            // one (#863), the same way.
            Radios.TransmitSliceNotice.Setting =
                (Radios.TransmitElsewhereNotice)Math.Clamp(TransmitElsewhereNotice, 0, 2);

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

            EngineHoldsSavedConfig = true;
        }

        /// <summary>
        /// True once a saved audio config has been applied to the engines this
        /// session (<see cref="Apply"/>: the first power-on, or a Settings OK).
        /// Until then the meter engine holds its start-time defaults and no
        /// meter slots, which are not the operator's.
        /// </summary>
        /// <remarks>
        /// The verbosity key works with no radio, and its save ran a full
        /// <see cref="CaptureFromEngine"/> into the root file. Changing verbosity
        /// once before the first connect therefore wrote an empty meter list
        /// and the default meter settings over the saved ones, and the next
        /// connect fell back to the preset. Anything that writes engine state
        /// into a saved config asks this first.
        /// </remarks>
        public static bool EngineHoldsSavedConfig { get; internal set; }

        private static readonly object ChoiceBeforeLoadGate = new();

        /// <summary>
        /// Keep a choice the operator made before the saved config reached the
        /// engine: load the root file, set only that choice, save. Does nothing
        /// once the config is loaded, when the choice lives in the engine and
        /// the ordinary saves carry it.
        /// </summary>
        /// <remarks>
        /// <para>The meter tones switch, Peak Watcher and the VOX send box all
        /// work with no radio, and none of them saved itself: before the first
        /// connect a choice reached disk only if a verbosity change happened to
        /// follow, through that save's whole capture of the engine, which also
        /// wrote every unloaded default over the operator's file. With the
        /// capture limited to verbosity before load, the choice is written here
        /// instead, alone, so the next load applies it.</para>
        /// <para>The root file is the one the first power-on loads
        /// (<c>CanonicalDir</c> folds the radio folder onto it).</para>
        /// </remarks>
        /// <returns>True when the choice was written.</returns>
        public static bool KeepChoiceBeforeLoad(System.Action<AudioOutputConfig> choose) =>
            KeepChoiceBeforeLoad(choose, Radios.RadioConfig.AppDataRoot);

        internal static bool KeepChoiceBeforeLoad(System.Action<AudioOutputConfig> choose, string? rootDir)
        {
            if (EngineHoldsSavedConfig) return false;
            if (string.IsNullOrWhiteSpace(rootDir))
            {
                Trace.WriteLine("AudioOutputConfig: no settings folder; a choice made before the first connect was not saved");
                return false;
            }
            try
            {
                lock (ChoiceBeforeLoadGate)
                {
                    var onDisk = Load(rootDir!);
                    choose(onDisk);
                    onDisk.Save(rootDir!);
                }
                return true;
            }
            catch (System.Exception ex)
            {
                Trace.WriteLine("AudioOutputConfig: saving a choice made before the first connect failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Capture current state from the engine into this config.</summary>
        /// <remarks>
        /// Before the saved config has reached the engine
        /// (<see cref="EngineHoldsSavedConfig"/> false), only the speech
        /// verbosity is captured: it is live from launch, and it is what the one
        /// caller that can run then (the verbosity key's save) is saving. Every
        /// other field is left as loaded, because the engine's value is a
        /// default, not a choice.
        /// </remarks>
        public void CaptureFromEngine()
        {
            SpeechVerbosity = (int)Radios.ScreenReaderOutput.CurrentVerbosity;
            if (!EngineHoldsSavedConfig)
            {
                System.Diagnostics.Trace.WriteLine(
                    "AudioOutputConfig: saved config not yet in the engine; captured verbosity only");
                return;
            }
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
            RecorderBeepsEnabled = EarconPlayer.RecorderBeepsEnabled;
            EarconDialogTonesEnabled = EarconPlayer.DialogTonesEnabled;
            RecorderSpeechEnabled = RecorderControls.StatusSpeechEnabled;
            VoxSendKeysRadio = Radios.VoxSendBox.Ticked;
            Countdown = Radios.CountdownSettings.Current.Clone();
            MasterVolume = EarconPlayer.MasterVolume;
            AlertVolume = EarconPlayer.AlertVolume;
            MasterEarconVolume = (int)(EarconPlayer.AlertVolume * 100);
            EarconDeviceNumber = EarconPlayer.GetAlertDeviceNumber();
            (AlertBufferMs, AlertBufferCount) = EarconPlayer.AlertBuffer;
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
            RxDuckTiming = RxDuck.TimingName(RxDuck.Timing);
            RecordingPlaybackRadioLevel = RxPlaybackLevel.LevelName(RxPlaybackLevel.Level);

            // What the engine hands back is always current-model, so say so.
            // The migration is a no-op on an already-current file even without
            // this stamp — every radio name it produces resolves to itself —
            // but an unstamped save would claim the file had never been
            // migrated, which is a lie about the file's own contents.
            MeterConfigVersion = MeterConfigMigration.CurrentVersion;
            CountdownDefaultsVersion = Radios.CountdownSettings.CurrentDefaultsVersion;
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

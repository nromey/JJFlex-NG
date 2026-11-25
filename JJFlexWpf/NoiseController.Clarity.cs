using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JJPortaudio;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>
    /// JJ Clarity on the noise controller's one rule book (Sprint 49 Track J
    /// phase 2, #736): the Transmit menu, Home and the editor all call these,
    /// so they agree by construction. JJ Clarity is the computer's exciter on
    /// transmit: it adds brightness (the high band) and fullness (the low
    /// band). It is NEVER the radio's Enhanced Signal Clarity, and every label
    /// says "JJ Clarity" in full so the two cannot be mistaken (question 6's
    /// default). Every sentence is a DRAFT PROSE key for Codex. Transmit only.
    /// </summary>
    /// <remarks>
    /// Its levers are <see cref="DynamicsLever"/>s with the stage
    /// <see cref="DynamicsLever.ClarityStage"/>, so the stepping, the saying,
    /// Home's rows and the editor's fields are Track CL's own functions
    /// (<see cref="AdjustDynamics"/>, <see cref="SetDynamics"/>,
    /// <see cref="DynamicsSteps"/>), not a second copy of them.
    /// </remarks>
    public sealed partial class NoiseController
    {
        private const string Cl = DynamicsLever.ClarityStage;
        private const string Percent = "audio.unit.percent";

        /// <summary>
        /// JJ Clarity's levers, in the order an editor offers them: the high
        /// band's (how much brightness, where it starts, how hard it is made),
        /// then the low band's. Every one is continuous, so it is live while
        /// keyed (S15). Home carries the two "how much" levers as flat rows.
        /// </summary>
        public static readonly DynamicsLever[] ClarityLevers =
        {
            new(Cl, "high.mix", "audio.clarity.lever.high_mix", "audio.clarity_help.high_mix", ClaritySettings.MixMinPercent, ClaritySettings.MixMaxPercent, 5, Percent, FineStep: 1, OnHome: true),
            new(Cl, "high.tune", "audio.clarity.lever.high_tune", "audio.clarity_help.high_tune", ClaritySettings.HighTuneMinHz, ClaritySettings.HighTuneMaxHz, 100, Hz, FineStep: 10),
            new(Cl, "high.drive", "audio.clarity.lever.high_drive", "audio.clarity_help.high_drive", ClaritySettings.DriveMinDb, ClaritySettings.DriveMaxDb, 1, Db),
            new(Cl, "low.mix", "audio.clarity.lever.low_mix", "audio.clarity_help.low_mix", ClaritySettings.MixMinPercent, ClaritySettings.MixMaxPercent, 5, Percent, FineStep: 1, OnHome: true),
            new(Cl, "low.tune", "audio.clarity.lever.low_tune", "audio.clarity_help.low_tune", ClaritySettings.LowTuneMinHz, ClaritySettings.LowTuneMaxHz, 10, Hz, FineStep: 1),
            new(Cl, "low.drive", "audio.clarity.lever.low_drive", "audio.clarity_help.low_drive", ClaritySettings.DriveMinDb, ClaritySettings.DriveMaxDb, 1, Db),
        };

        // ────────────────────────────────────────────────────────────────
        //  Switches
        // ────────────────────────────────────────────────────────────────

        /// <summary>Whether JJ Clarity as a whole is on (the operator's intent, as it has RUN: a switch asked for while keyed shows here at the unkey).</summary>
        public bool ClarityOn => TxAudioConditioning.ClaritySetup.Enabled;

        /// <summary>
        /// JJ Clarity as LAST ASKED FOR: the intent with every change still
        /// waiting for the unkey run on it, in order (repair 1, B1). A toggle,
        /// a "next" row and "is this already so?" read this, so the newest
        /// request is what they step from and what wins.
        /// </summary>
        private static TxClaritySetup ClarityAsked => TxAudioConditioning.AskedShaping().Clarity;

        /// <summary>Whether one band is on: the high band (brightness) or the low band (fullness). A band adds nothing while the whole stage is off.</summary>
        public bool ClarityBandOn(bool high) => TxAudioConditioning.ClaritySetup.Band(high).Enabled;

        /// <summary>The harmonics one band makes, as a word of <see cref="TxClarityHarmonics"/>.</summary>
        public string ClarityHarmonicsOf(bool high) => HarmonicsWord(TxAudioConditioning.ClaritySetup.Band(high));

        private static string HarmonicsWord(TxClarityBandSetup band) => TxClarityHarmonics.ToWord(TxClarityHarmonics.ToChoice(band.Harmonics));

        /// <summary>A band's spoken name: the high band is the one that adds brightness, the low band the one that adds fullness.</summary>
        public static string ClarityBandName(bool high) => Lexicon.Get(high ? "audio.clarity.band.high" : "audio.clarity.band.low");

        /// <summary>A harmonics choice's spoken name. Each a whole key, so the lexicon scans see it.</summary>
        public static string ClarityHarmonicsName(string word) => Lexicon.Get(TxClarityHarmonics.ToChoice(word) switch
        {
            JJPortaudio.ClarityHarmonics.Odd => "audio.clarity.harmonics.odd",
            JJPortaudio.ClarityHarmonics.Both => "audio.clarity.harmonics.both",
            _ => "audio.clarity.harmonics.even",
        });

        /// <summary>JJ Clarity on or off, speaking its timing (a switch: it waits for the unkey while keyed).</summary>
        public void SetClarityOn(bool on, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            bool was = ClarityAsked.Enabled;
            var timing = was == on ? AlreadyAsked(ClarityOn == on) : TxAudioConditioning.SetClarityEnabled(on);
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), Cl, "", Words(was), Words(on));
            Say(L(on ? "audio.clarity.on" : "audio.clarity.off"), SpeechSubject.TransmitClarity);
            SayAtUnkey(timing);
        }

        /// <summary>The other way from what was last asked for (not from what has run: two presses while keyed end where they began).</summary>
        public void ToggleClarity(string source) => SetClarityOn(!ClarityAsked.Enabled, source);

        /// <summary>One band on or off, speaking its timing (a switch).</summary>
        public void SetClarityBandOn(bool high, bool on, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            bool was = ClarityAsked.Band(high).Enabled;
            var timing = was == on ? AlreadyAsked(ClarityBandOn(high) == on) : TxAudioConditioning.SetClarityBandEnabled(high, on);
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), Cl, high ? "high" : "low", Words(was), Words(on));
            Say(L(on ? "audio.clarity.band_on" : "audio.clarity.band_off", ("band", ClarityBandName(high))), SpeechSubject.TransmitClarity);
            SayAtUnkey(timing);
        }

        public void ToggleClarityBand(bool high, string source) => SetClarityBandOn(high, !ClarityAsked.Band(high).Enabled, source);

        /// <summary>The harmonics one band makes, speaking its timing (a switch).</summary>
        public void SetClarityHarmonics(bool high, string word, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            string was = HarmonicsWord(ClarityAsked.Band(high));
            string want = TxClarityHarmonics.ToWord(TxClarityHarmonics.ToChoice(word));
            var timing = was == want ? AlreadyAsked(ClarityHarmonicsOf(high) == want) : TxAudioConditioning.SetClarityHarmonics(high, want);
            if (was != want) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), Cl, high ? "high.harmonics" : "low.harmonics", was, want);
            Say(L("audio.clarity.harmonics_set", ("band", ClarityBandName(high)), ("harmonics", ClarityHarmonicsName(want))), SpeechSubject.TransmitClarity);
            SayAtUnkey(timing);
        }

        /// <summary>The next harmonics choice for a band, round the three (the menu's one row per band).</summary>
        public void CycleClarityHarmonics(bool high, string source)
        {
            var all = TxClarityHarmonics.All;
            // From what was last asked for: two presses while keyed move two on.
            int at = Math.Max(0, all.ToList().IndexOf(HarmonicsWord(ClarityAsked.Band(high))));
            SetClarityHarmonics(high, all[(at + 1) % all.Count], source);
        }

        // ────────────────────────────────────────────────────────────────
        //  Presets: starting points
        // ────────────────────────────────────────────────────────────────

        /// <summary>JJ Clarity's presets, in the order offered.</summary>
        public static IReadOnlyList<ClarityPreset> ClarityPresetList => ClarityPresets.All;

        /// <summary>A preset's spoken name. Each a whole key, so the lexicon scans see it.</summary>
        public static string ClarityPresetName(ClarityPreset preset) => Lexicon.Get(preset.Id switch
        {
            ClarityPresets.Gentle => "audio.clarity.preset.gentle",
            ClarityPresets.Presence => "audio.clarity.preset.presence",
            ClarityPresets.Fuller => "audio.clarity.preset.fuller",
            ClarityPresets.Wide => "audio.clarity.preset.wide",
            _ => "audio.clarity.preset_none",
        });

        /// <summary>The preset JJ Clarity's bands were last loaded from and still match, or null. Home's poll asks this, so it compares the settings as values and serializes nothing.</summary>
        public ClarityPreset? ClarityPresetInPlace
        {
            get
            {
                var now = TxAudioConditioning.ClaritySetup;
                var preset = ClarityPresets.Find(now.PresetId ?? "");
                return preset != null && now.ToSettings().Equals(preset.Settings) ? preset : null;
            }
        }

        /// <summary>Load a preset into JJ Clarity (a switch): both bands are filled, and stay the operator's. The whole stage's on or off is left as it was.</summary>
        public void LoadClarityPreset(string presetId, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            var preset = ClarityPresets.Find(presetId ?? "");
            var timing = preset == null ? new TxChangeResult(TxChangeTiming.Refused, "unknown-clarity-preset") : TxAudioConditioning.LoadClarityPreset(presetId!);
            if (preset == null || timing.Timing == TxChangeTiming.Refused)
            {
                Say(L("audio.clarity.preset_unknown"), SpeechSubject.TransmitClarity);
                return;
            }
            NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, TxSource(source), Cl, "", "", preset.Id);
            Say(L("audio.clarity.preset_loaded", ("preset", ClarityPresetName(preset))), SpeechSubject.TransmitClarity);
            SayAtUnkey(timing);
        }

        // ────────────────────────────────────────────────────────────────
        //  "What JJ Clarity is adding now": on demand, never per frame
        // ────────────────────────────────────────────────────────────────

        private static string WholeText(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>
        /// JJ Clarity's reading as one sentence: off; or what it is adding
        /// now, then each band whose tune is being held inside the transmit
        /// filter (the choice is kept), then what it added on the last over
        /// and any samples that left it past full scale. For the "Say" item,
        /// Home's button and the editor's reading line.
        /// </summary>
        public string ClarityNowSentence()
        {
            if (!ClarityOn) return L("audio.clarity.now_off");
            var setup = TxAudioConditioning.ClaritySetup;
            var now = TxAudioConditioning.ReadClarityNow();
            var parts = new List<string>
            {
                !now.Running ? L("audio.clarity.now_not_running")
                : now.AddingDbfs <= -150f ? L("audio.clarity.now_nothing")
                : L("audio.clarity.now", ("adding", TenthsText(now.AddingDbfs))),
            };
            if (setup.Band(true).Enabled && now.HighTuneClamped)
                parts.Add(L("audio.clarity.tune_held_high", ("applied", WholeText(now.AppliedHighTuneHz)), ("chosen", WholeText(setup.Band(true).TuneHz))));
            if (setup.Band(false).Enabled && now.LowTuneClamped)
                parts.Add(L("audio.clarity.tune_held_low", ("applied", WholeText(now.AppliedLowTuneHz)), ("chosen", WholeText(setup.Band(false).TuneHz))));
            if (TxAudioConditioning.LastClarityOver is { Samples: > 0 } over)
                parts.Add(over.OverFullScale > 0
                    ? L("audio.clarity.last_over_past_full_scale", ("added", TenthsText(over.AddedRmsDbfs)), ("samples", over.OverFullScale.ToString(CultureInfo.InvariantCulture)))
                    : L("audio.clarity.last_over", ("added", TenthsText(over.AddedRmsDbfs))));
            return string.Join(" ", parts);
        }

        public void SayClarityNow()
        {
            using var oneUtterance = OneUtterance();
            Say(ClarityNowSentence(), SpeechSubject.TransmitClarity);
        }

        // ────────────────────────────────────────────────────────────────
        //  The suggest pass's Keep
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Keep what the suggest pass offered: the guarded proposal's bands
        /// become JJ Clarity's setting, as the operator's own choice, and JJ
        /// Clarity is switched on if it was off, because what was heard as
        /// "after" had it on (Track J, item 14). ONE switch: held for the
        /// unkey while keyed. It says, as one utterance: kept (and switched on,
        /// when it was off), that it waits for the unkey when it does, and the
        /// next step. Called only from the pass's Keep, with the
        /// offer <c>TryGetGuardedOffer</c> gave: never the raw proposal, and
        /// never by itself (never auto-apply, #262).
        /// </summary>
        public TxChangeResult KeepClaritySuggestion(ClaritySettings offered, string source)
        {
            if (offered == null) throw new ArgumentNullException(nameof(offered));
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            // As last asked for: an "on" still waiting for the unkey is not Keep's to announce.
            var was = ClarityAsked;
            var keep = TxClaritySetup.From(offered, enabled: true);
            var timing = TxAudioConditioning.SetClarityWhole(keep);
            NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, TxSource(source), Cl, "suggestion",
                (was.Enabled ? "on; " : "off; ") + was.ToSettings(), "on; " + keep.ToSettings());
            Say(L(was.Enabled ? "audio.clarity_suggest.kept" : "audio.clarity_suggest.kept_switched_on"), SpeechSubject.ClaritySuggest);
            SayAtUnkey(timing);
            // The next step is part of the SAME utterance: a second sentence on
            // this subject queued behind the first would cover it before it was
            // heard (a subject's newest sentence replaces an unheard older one).
            Say(L("audio.clarity_suggest.next_step"), SpeechSubject.ClaritySuggest);
            return timing;
        }

        /// <summary>
        /// Put a proposal in place as the OPEN editor's own first change (the
        /// suggest pass's Adjust): it is tagged and noted like every other
        /// change the editor makes, so every way out of the editor but its
        /// Close takes it back, with whatever was adjusted on top of it.
        /// Says nothing: the editor that opens says what it is.
        /// </summary>
        public void LoadClarityProposalIntoEditor(ClaritySettings proposed, string editorSource)
        {
            if (proposed == null) throw new ArgumentNullException(nameof(proposed));
            using var owned = OwnTx(editorSource);
            var was = TxAudioConditioning.ClaritySetup;
            var want = TxClaritySetup.From(proposed, enabled: true);
            TxAudioConditioning.SetClarityWhole(want);
            NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, TxSource(editorSource), Cl, "suggestion to adjust",
                (was.Enabled ? "on; " : "off; ") + was.ToSettings(), "on; " + want.ToSettings());
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>
    /// One lever of the compressor stage or the limiter (Sprint 49 Track CL,
    /// #1278): its stage, key, label, range, step, unit, and whether it is a
    /// switch (it waits for the unkey) or a continuous lever (live).
    /// </summary>
    /// <param name="Stage"><see cref="CompressorStage"/>, <see cref="LimiterStage"/> or <see cref="ClarityStage"/>.</param>
    /// <param name="Key">The key <see cref="TxAudioConditioning.SetCompressorLevel"/>, <see cref="TxAudioConditioning.SetLimiterLevel"/> or <see cref="TxAudioConditioning.SetClarityLevel"/> takes.</param>
    /// <param name="Decimals">Decimal places the value is shown and stepped with.</param>
    /// <param name="FineStep">The fine step an editor offers; 0 for none beyond <paramref name="Step"/>.</param>
    /// <param name="OnHome">Whether Home carries it as a flat row (the editor carries every one).</param>
    /// <param name="SplitBandOnly">Whether it acts only in split-band mode.</param>
    /// <param name="HelpKey">The Ctrl+F1 help key for this lever's field (one per lever, DRAFT PROSE). Given whole, never assembled, so every lexicon key is a literal the lexicon scans can see.</param>
    public sealed record DynamicsLever(string Stage, string Key, string LabelKey, string HelpKey, double Min, double Max, double Step, string? UnitKey,
        int Decimals = 0, double FineStep = 0, bool Switch = false, bool OnHome = false, bool SplitBandOnly = false, bool Advanced = false)
    {
        public const string CompressorStage = "compressor";
        public const string LimiterStage = "limiter";
        /// <summary>JJ Clarity's levers (Sprint 49 Track J): the same record, so Home, the menu and the editor step and say them through the same functions.</summary>
        public const string ClarityStage = "clarity";
    }

    /// <summary>
    /// The compressor stage and the limiter on the noise controller's one rule
    /// book (Sprint 49 Track CL, #1278): the Transmit menu, Home and the editor
    /// all call these, so they agree by construction. Every sentence is a DRAFT
    /// PROSE key for Codex. Both stages are transmit only.
    /// </summary>
    public sealed partial class NoiseController
    {
        private const string Db = "audio.unit.decibels", Ms = "audio.unit.milliseconds", Hz = "audio.unit.hertz";

        /// <summary>
        /// The compressor's threshold is in dBFS of the SAMPLE PEAK, and its
        /// unit says so, so it is never confused with the expander's threshold
        /// (which reads both channels' power, about 3 dB above one channel's
        /// average).
        /// </summary>
        private const string DbfsPeak = "audio.unit.dbfs_peak";

        private const string Cs = DynamicsLever.CompressorStage, Ls = DynamicsLever.LimiterStage;

        /// <summary>Every compressor lever, in the order an editor offers them: the single band's, the drive and the rotator, then the split's crossovers and its three bands.</summary>
        public static readonly DynamicsLever[] CompressorLevers =
        {
            new(Cs, "threshold", "audio.compressor.lever.threshold", "audio.dynamics_help.compressor.threshold", -60, 0, 1, DbfsPeak, OnHome: true),
            new(Cs, "ratio", "audio.compressor.lever.ratio", "audio.dynamics_help.compressor.ratio", 1, 20, 0.5, null, Decimals: 1, FineStep: 0.1, OnHome: true),
            new(Cs, "attack", "audio.compressor.lever.attack", "audio.dynamics_help.compressor.attack", 0.1, 300, 1, Ms, Decimals: 1, FineStep: 0.1),
            new(Cs, "release", "audio.compressor.lever.release", "audio.dynamics_help.compressor.release", 5, 2000, 10, Ms, FineStep: 1),
            new(Cs, "knee", "audio.compressor.lever.knee", "audio.dynamics_help.compressor.knee", 0, 24, 1, Db),
            new(Cs, "makeup", "audio.compressor.lever.makeup", "audio.dynamics_help.compressor.makeup", -12, 24, 0.5, Db, Decimals: 1, OnHome: true),
            new(Cs, "drive", "audio.compressor.lever.drive", "audio.dynamics_help.compressor.drive", 0, 18, 1, Db),
            new(Cs, "rotator", "audio.compressor.lever.rotator", "audio.dynamics_help.compressor.rotator", 0, 6, 1, null, Switch: true),
            new(Cs, "crossover_low", "audio.compressor.lever.crossover_low", "audio.dynamics_help.compressor.crossover_low", 60, 1000, 10, Hz, SplitBandOnly: true),
            new(Cs, "crossover_high", "audio.compressor.lever.crossover_high", "audio.dynamics_help.compressor.crossover_high", 800, 6000, 50, Hz, SplitBandOnly: true),

            new(Cs, "low.threshold", "audio.compressor.lever.low_threshold", "audio.dynamics_help.compressor.band_threshold", -60, 0, 1, DbfsPeak, SplitBandOnly: true),
            new(Cs, "low.ratio", "audio.compressor.lever.low_ratio", "audio.dynamics_help.compressor.band_ratio", 1, 20, 0.5, null, Decimals: 1, FineStep: 0.1, SplitBandOnly: true),
            new(Cs, "low.attack", "audio.compressor.lever.low_attack", "audio.dynamics_help.compressor.band_attack", 0.1, 300, 1, Ms, Decimals: 1, FineStep: 0.1, SplitBandOnly: true),
            new(Cs, "low.release", "audio.compressor.lever.low_release", "audio.dynamics_help.compressor.band_release", 5, 2000, 10, Ms, FineStep: 1, SplitBandOnly: true),
            new(Cs, "low.makeup", "audio.compressor.lever.low_makeup", "audio.dynamics_help.compressor.band_makeup", -12, 24, 0.5, Db, Decimals: 1, SplitBandOnly: true),

            new(Cs, "mid.threshold", "audio.compressor.lever.mid_threshold", "audio.dynamics_help.compressor.band_threshold", -60, 0, 1, DbfsPeak, SplitBandOnly: true),
            new(Cs, "mid.ratio", "audio.compressor.lever.mid_ratio", "audio.dynamics_help.compressor.band_ratio", 1, 20, 0.5, null, Decimals: 1, FineStep: 0.1, SplitBandOnly: true),
            new(Cs, "mid.attack", "audio.compressor.lever.mid_attack", "audio.dynamics_help.compressor.band_attack", 0.1, 300, 1, Ms, Decimals: 1, FineStep: 0.1, SplitBandOnly: true),
            new(Cs, "mid.release", "audio.compressor.lever.mid_release", "audio.dynamics_help.compressor.band_release", 5, 2000, 10, Ms, FineStep: 1, SplitBandOnly: true),
            new(Cs, "mid.makeup", "audio.compressor.lever.mid_makeup", "audio.dynamics_help.compressor.band_makeup", -12, 24, 0.5, Db, Decimals: 1, SplitBandOnly: true),

            new(Cs, "high.threshold", "audio.compressor.lever.high_threshold", "audio.dynamics_help.compressor.band_threshold", -60, 0, 1, DbfsPeak, SplitBandOnly: true),
            new(Cs, "high.ratio", "audio.compressor.lever.high_ratio", "audio.dynamics_help.compressor.band_ratio", 1, 20, 0.5, null, Decimals: 1, FineStep: 0.1, SplitBandOnly: true),
            new(Cs, "high.attack", "audio.compressor.lever.high_attack", "audio.dynamics_help.compressor.band_attack", 0.1, 300, 1, Ms, Decimals: 1, FineStep: 0.1, SplitBandOnly: true),
            new(Cs, "high.release", "audio.compressor.lever.high_release", "audio.dynamics_help.compressor.band_release", 5, 2000, 10, Ms, FineStep: 1, SplitBandOnly: true),
            new(Cs, "high.makeup", "audio.compressor.lever.high_makeup", "audio.dynamics_help.compressor.band_makeup", -12, 24, 0.5, Db, Decimals: 1, SplitBandOnly: true),
        };

        /// <summary>The limiter's levers: its ceiling, its release, and (Advanced) its look-ahead, a switch.</summary>
        public static readonly DynamicsLever[] LimiterLevers =
        {
            new(Ls, "ceiling", "audio.limiter.lever.ceiling", "audio.dynamics_help.limiter.ceiling", -12, 0, 0.5, DbfsPeak, Decimals: 1, OnHome: true),
            new(Ls, "release", "audio.limiter.lever.release", "audio.dynamics_help.limiter.release", 10, 1000, 10, Ms, FineStep: 1),
            new(Ls, "lookahead", "audio.limiter.lever.lookahead", "audio.dynamics_help.limiter.lookahead", 0, 5, 0.5, Ms, Decimals: 1, Switch: true, Advanced: true),
        };

        /// <summary>A lever by stage and key, or null.</summary>
        public static DynamicsLever? FindDynamicsLever(string stage, string key) =>
            (stage == DynamicsLever.LimiterStage ? LimiterLevers : stage == DynamicsLever.ClarityStage ? ClarityLevers : CompressorLevers)
                .FirstOrDefault(l => l.Key == key);

        private static string DynamicsSubject(string stage) =>
            stage == DynamicsLever.LimiterStage ? SpeechSubject.TransmitLimiter
            : stage == DynamicsLever.ClarityStage ? SpeechSubject.TransmitClarity
            : SpeechSubject.TransmitCompressor;

        // ────────────────────────────────────────────────────────────────
        //  Switches
        // ────────────────────────────────────────────────────────────────

        public bool CompressorOn => TxAudioConditioning.CompressorSetup.Enabled;
        public bool LimiterOn => TxAudioConditioning.LimiterSetup.Enabled;
        public bool CompressorSplitBand => TxCompressorModes.IsSplit(TxAudioConditioning.CompressorSetup.CompressorMode);

        /// <summary>The compressor on or off, speaking its timing. What happens to the radio's speech processor is the handoff's, said by it when it acts.</summary>
        public void SetCompressorOn(bool on, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            bool was = CompressorOn;
            var timing = was == on ? TxChangeResult.Now : TxAudioConditioning.SetCompressorEnabled(on);
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "compressor", "", Words(was), Words(on));
            Say(L(on ? "audio.compressor.on" : "audio.compressor.off"), SpeechSubject.TransmitCompressor);
            SayAtUnkey(timing);
        }

        public void ToggleCompressor(string source) => SetCompressorOn(!CompressorOn, source);

        /// <summary>The limiter on or off, speaking its timing.</summary>
        public void SetLimiterOn(bool on, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            bool was = LimiterOn;
            var timing = was == on ? TxChangeResult.Now : TxAudioConditioning.SetLimiterEnabled(on);
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "limiter", "", Words(was), Words(on));
            Say(L(on ? "audio.limiter.on" : "audio.limiter.off"), SpeechSubject.TransmitLimiter);
            SayAtUnkey(timing);
        }

        public void ToggleLimiter(string source) => SetLimiterOn(!LimiterOn, source);

        /// <summary>The compressor's mode: one band or split band. A switch.</summary>
        public void SetCompressorSplitBand(bool split, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            bool was = CompressorSplitBand;
            var timing = was == split ? TxChangeResult.Now : TxAudioConditioning.SetCompressorSplitBand(split);
            if (was != split) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "compressor", "mode",
                was ? TxCompressorModes.Split : TxCompressorModes.Single, split ? TxCompressorModes.Split : TxCompressorModes.Single);
            Say(L(split ? "audio.compressor.mode_split" : "audio.compressor.mode_single"), SpeechSubject.TransmitCompressor);
            SayAtUnkey(timing);
        }

        // ────────────────────────────────────────────────────────────────
        //  Presets: starting points
        // ────────────────────────────────────────────────────────────────

        /// <summary>The compressor's presets, in the order offered.</summary>
        public static IReadOnlyList<TxCompressorPreset> CompressorPresets => TxCompressorPresets.All;

        /// <summary>A preset's spoken name.</summary>
        public static string CompressorPresetName(TxCompressorPreset preset) => Lexicon.Get(preset.NameKey);

        /// <summary>The preset the compressor's fields were last loaded from and still match, or null.</summary>
        public TxCompressorPreset? CompressorPresetInPlace
        {
            get
            {
                // Home's poll asks this, so it compares the settings as records
                // (every field, each band included) and serializes nothing.
                var now = TxAudioConditioning.CompressorSetup;
                var preset = TxCompressorPresets.Find(now.PresetId);
                return preset != null && now.ToSettings() with { Enabled = false } == preset.Settings.ToSettings() with { Enabled = false }
                    ? preset : null;
            }
        }

        /// <summary>Load a preset into the compressor (a switch): every field is filled, and stays the operator's. Its on or off is left as it was.</summary>
        public void LoadCompressorPreset(string presetId, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            var preset = TxCompressorPresets.Find(presetId);
            var timing = preset == null ? new TxChangeResult(TxChangeTiming.Refused, "unknown-compressor-preset") : TxAudioConditioning.LoadCompressorPreset(presetId);
            if (preset == null || timing.Timing == TxChangeTiming.Refused)
            {
                Say(L("audio.compressor.preset_unknown"), SpeechSubject.TransmitCompressor);
                return;
            }
            NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, TxSource(source), "compressor", "", "", preset.Id);
            Say(L("audio.compressor.preset_loaded", ("preset", CompressorPresetName(preset))), SpeechSubject.TransmitCompressor);
            SayAtUnkey(timing);
        }

        // ────────────────────────────────────────────────────────────────
        //  Levers
        // ────────────────────────────────────────────────────────────────

        public string DynamicsLeverName(DynamicsLever lever) => Lexicon.Get(lever.LabelKey);

        /// <summary>A lever's value as intended now.</summary>
        public double DynamicsNumber(DynamicsLever lever)
        {
            if (lever.Stage == DynamicsLever.ClarityStage) return TxDynamicsEditorModel.ValueOf(lever, TxAudioConditioning.CaptureClarity());
            var (c, l) = TxAudioConditioning.CaptureDynamics();
            return TxDynamicsEditorModel.ValueOf(lever, c, l);
        }

        /// <summary>A lever's value in words: the number with its unit; a ratio as "n to 1"; the rotator's 0 as off.</summary>
        public string DynamicsValueText(DynamicsLever lever, double value)
        {
            string number = Math.Round(value, lever.Decimals).ToString(lever.Decimals > 0 ? "0." + new string('#', lever.Decimals) : "0", CultureInfo.InvariantCulture);
            if (lever.Key.EndsWith("ratio", StringComparison.Ordinal)) return L("audio.compressor.ratio_value", ("ratio", number));
            if (lever.Key == "rotator")
                return value < 0.5 ? L("audio.compressor.rotator_off") : L("audio.compressor.rotator_stages", ("stages", number));
            return lever.UnitKey == null ? number : WithUnit(number, lever.UnitKey);
        }

        /// <summary>Every stop of a lever at its own step, for a field that cycles through them (Home's flat rows).</summary>
        public IReadOnlyList<(double Value, string Text)> DynamicsSteps(DynamicsLever lever)
        {
            var list = new List<(double, string)>();
            int count = (int)Math.Round((lever.Max - lever.Min) / lever.Step);
            for (int i = 0; i <= count; i++)
            {
                double v = Math.Round(lever.Min + i * lever.Step, 3);
                list.Add((v, DynamicsValueText(lever, v)));
            }
            return list;
        }

        /// <summary>Say a lever and its value.</summary>
        public void SayDynamicsLever(DynamicsLever lever)
        {
            using var oneUtterance = OneUtterance();
            Say(L("audio.noise_surface.tx.gate_lever", ("lever", DynamicsLeverName(lever)), ("value", DynamicsValueText(lever, DynamicsNumber(lever)))), DynamicsSubject(lever.Stage));
        }

        /// <summary>One step up or down, saying the most or the least at an end.</summary>
        public void AdjustDynamics(DynamicsLever lever, int direction, string source)
        {
            double now = DynamicsNumber(lever);
            double next = Math.Clamp(Math.Round(now + lever.Step * Math.Sign(direction), 3), lever.Min, lever.Max);
            if (next == now)
            {
                using var oneUtterance = OneUtterance();
                Say(L(direction > 0 ? "audio.noise_surface.lever_most" : "audio.noise_surface.lever_least",
                    ("lever", DynamicsLeverName(lever)), ("value", DynamicsValueText(lever, now))), DynamicsSubject(lever.Stage));
                return;
            }
            SetDynamics(lever, next, source);
        }

        /// <summary>Set a lever and say it: a continuous lever is live (S15); a switch waits for the unkey and says so.</summary>
        public void SetDynamics(DynamicsLever lever, double value, string source, bool speak = true)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            double was = DynamicsNumber(lever);
            double asked = Math.Clamp(value, lever.Min, lever.Max);
            var timing = lever.Stage == DynamicsLever.LimiterStage
                ? TxAudioConditioning.SetLimiterLevel(lever.Key, (float)asked)
                : lever.Stage == DynamicsLever.ClarityStage
                    ? TxAudioConditioning.SetClarityLevel(lever.Key, (float)asked)
                    : TxAudioConditioning.SetCompressorLevel(lever.Key, (float)asked);
            if (timing.Timing == TxChangeTiming.Refused)
            {
                Say(L("audio.noise_surface.lever_refused", ("lever", DynamicsLeverName(lever))), DynamicsSubject(lever.Stage));
                return;
            }
            NoiseSettingsJournal.Record(NoiseJournalKind.Lever, TxSource(source), lever.Stage, lever.Key,
                was.ToString(CultureInfo.InvariantCulture), asked.ToString(CultureInfo.InvariantCulture));
            if (speak)
                Say(L("audio.noise_surface.tx.gate_lever", ("lever", DynamicsLeverName(lever)), ("value", DynamicsValueText(lever, asked))), DynamicsSubject(lever.Stage));
            SayAtUnkey(timing);
        }

        // ────────────────────────────────────────────────────────────────
        //  "Now": on demand, never per frame
        // ────────────────────────────────────────────────────────────────

        private static string TenthsText(double db) => Math.Round(db, 1).ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>
        /// The compressor's reading as one sentence: off; or the gain reduction
        /// being applied now, and, once an over has ended, that over's deepest
        /// and average reduction. For the "Say" item, Home's reading and the
        /// editor's status line.
        /// </summary>
        public string CompressorNowSentence()
        {
            if (!CompressorOn) return L("audio.compressor.now_off");
            var now = TxAudioConditioning.ReadDynamicsNow();
            var parts = new List<string>
            {
                !now.CompressorRunning ? L("audio.compressor.now_not_running")
                : now.SplitBand ? L("audio.compressor.now_split", ("low", TenthsText(now.LowReductionDb)), ("mid", TenthsText(now.MidReductionDb)), ("high", TenthsText(now.HighReductionDb)))
                : L("audio.compressor.now", ("reduction", TenthsText(now.CompressorReductionDb))),
            };
            if (TxAudioConditioning.LastDynamicsOver is { CompressorFrames: > 0 } over)
                parts.Add(L("audio.compressor.last_over", ("most", TenthsText(over.CompressorMaxDb)), ("average", TenthsText(over.CompressorAverageDb))));
            return string.Join(" ", parts);
        }

        /// <summary>The limiter's reading as one sentence: off; or what it is taking off now, and what it caught on the last over.</summary>
        public string LimiterNowSentence()
        {
            if (!LimiterOn) return L("audio.limiter.now_off");
            var now = TxAudioConditioning.ReadDynamicsNow();
            var parts = new List<string>
            {
                !now.LimiterRunning ? L("audio.limiter.now_not_running") : L("audio.limiter.now", ("reduction", TenthsText(now.LimiterReductionDb))),
            };
            if (TxAudioConditioning.LastDynamicsOver is { } over && (over.CompressorFrames > 0 || over.LimitedFrames > 0 || over.LimiterCaught > 0))
                parts.Add(over.LimiterCaught > 0
                    ? L("audio.limiter.last_over_caught", ("samples", over.LimiterCaught.ToString(CultureInfo.InvariantCulture)), ("most", TenthsText(over.LimiterMaxDb)))
                    : L("audio.limiter.last_over_clean"));
            return string.Join(" ", parts);
        }

        public void SayCompressorNow()
        {
            using var oneUtterance = OneUtterance();
            Say(CompressorNowSentence(), SpeechSubject.TransmitCompressor);
        }

        public void SayLimiterNow()
        {
            using var oneUtterance = OneUtterance();
            Say(LimiterNowSentence(), SpeechSubject.TransmitLimiter);
        }

        // ────────────────────────────────────────────────────────────────
        //  The handoff's setting
        // ────────────────────────────────────────────────────────────────

        /// <summary>"Turn off the radio's speech processor while the compressor is on" (#1278), as in force.</summary>
        public bool ProcessorHandoffSetting => _config()?.RadioProcessorOffWithCompressor ?? AudioOutputConfig.RadioProcessorOffWithCompressorInForce;

        /// <summary>
        /// Switch the handoff's setting and save it, and say so. With a config
        /// loaded it is saved through the controller's own persist; with none
        /// yet (no radio connected this session) the app-wide file is read,
        /// changed and written, so the choice is there for the first radio.
        /// </summary>
        public void SetProcessorHandoffSetting(bool on, string source)
        {
            using var oneUtterance = OneUtterance();
            bool was = ProcessorHandoffSetting;
            WriteProcessorHandoffSetting(on);
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "processor-handoff", "", Words(was), Words(on));
            // The open editor's own change, for its Cancel (repair 3): only what it changed goes back.
            if (_editor is { } editor && source == editor.Source) editor.NoteHandoff(was, on);
            Say(L(on ? "audio.processor_handoff.setting_on" : "audio.processor_handoff.setting_off"), SpeechSubject.ProcessorHandoff);
        }

        private void WriteProcessorHandoffSetting(bool on)
        {
            if (_config() is { } config)
            {
                config.RadioProcessorOffWithCompressor = on;
                _persist();
                return;
            }
            try
            {
                string root = RadioConfig.AppDataRoot;
                if (!string.IsNullOrEmpty(root))
                {
                    var file = AudioOutputConfig.Load(root);
                    file.RadioProcessorOffWithCompressor = on;
                    file.Save(root);
                }
                else AudioOutputConfig.RadioProcessorOffWithCompressorInForce = on;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("NoiseController: the processor handoff setting could not be saved: " + ex.Message);
                AudioOutputConfig.RadioProcessorOffWithCompressorInForce = on;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  The editor's model
        // ────────────────────────────────────────────────────────────────

        /// <summary>A model for the compressor and limiter's editor on this controller: what it shows and how its Cancel puts back.</summary>
        public TxDynamicsEditorModel DynamicsEditor(string source)
        {
            // One editor at a time; a new one takes over as the open one.
            var editor = new EditorOwned(source);
            _editor = editor;
            return new(
                () => TxAudioConditioning.CaptureDynamics(),
                (lever, value) => SetDynamics(lever, value, source, speak: false),
                () => ProcessorHandoffSetting,
                withdraw => EndDynamicsEditor(editor, withdraw),
                () => TxAudioConditioning.CaptureClarity());
        }

        /// <summary>
        /// What ONE open editor has done (repair 2 and 3, B5). It is the tag
        /// its waiting changes are held under, and the note of what its
        /// changes did, kept field by field: for each field it changed, the
        /// value the field had before the editor first changed it (the
        /// floor) and the value the editor last left in it. The editor's
        /// Cancel takes back exactly those fields, and only where they still
        /// read as it left them. So a change somebody else made (Home, an
        /// earlier editor's Close or Cancel) is never the editor's to undo,
        /// whether it landed before the editor's own change, after it, or
        /// while the editor sat open and untouched.
        /// </summary>
        private sealed class EditorOwned
        {
            public readonly string Source;
            public EditorOwned(string source) => Source = source;
            public TxCompressorSetup? CompressorFloor, CompressorLeft;
            public TxLimiterSetup? LimiterFloor, LimiterLeft;
            // JJ Clarity (Sprint 49 Track J): the same notes over its saved
            // class, whose every field is a read-write property the walk sees.
            public TxClaritySetup? ClarityFloor, ClarityLeft;
            public bool? HandoffFloor, HandoffLeft;

            /// <summary>One of the editor's writes ran: what both stages read just before it and just after it, in the chain's order.</summary>
            public void Note((TxCompressorSetup Compressor, TxLimiterSetup Limiter) before, (TxCompressorSetup Compressor, TxLimiterSetup Limiter) after)
            {
                CompressorFloor ??= before.Compressor.Clone();
                CompressorLeft ??= before.Compressor.Clone();
                LimiterFloor ??= before.Limiter.Clone();
                LimiterLeft ??= before.Limiter.Clone();
                OwnedFields.Note(CompressorFloor, CompressorLeft, before.Compressor, after.Compressor);
                OwnedFields.Note(LimiterFloor, LimiterLeft, before.Limiter, after.Limiter);
            }

            /// <summary>The same for JJ Clarity: what it read just before one of the editor's writes and just after it.</summary>
            public void NoteClarity(TxClaritySetup before, TxClaritySetup after)
            {
                ClarityFloor ??= before.Clone();
                ClarityLeft ??= before.Clone();
                OwnedFields.Note(ClarityFloor, ClarityLeft, before, after);
            }

            /// <summary>The editor switched the speech processor setting.</summary>
            public void NoteHandoff(bool was, bool now)
            {
                if (was == now) return;
                if (HandoffLeft != was) HandoffFloor = was; // its first change, or somebody changed it since the editor's last
                HandoffLeft = now;
            }

            /// <summary>Whether anything the editor changed has landed and may still be there to take back.</summary>
            public bool AnythingLanded =>
                (CompressorFloor != null && OwnedFields.Differ(CompressorFloor, CompressorLeft!))
                || (LimiterFloor != null && OwnedFields.Differ(LimiterFloor, LimiterLeft!))
                || (ClarityFloor != null && OwnedFields.Differ(ClarityFloor, ClarityLeft!))
                || (HandoffFloor is bool floor && HandoffLeft is bool left && floor != left);

            public void Forget()
            {
                CompressorFloor = CompressorLeft = null;
                LimiterFloor = LimiterLeft = null;
                ClarityFloor = ClarityLeft = null;
                HandoffFloor = HandoffLeft = null;
            }
        }

        /// <summary>
        /// One write by the open editor. What both stages read just before it
        /// and just after it is taken through the chain's own ordering (as
        /// the noise layer's writes are), so a change held for the unkey is
        /// noted when it runs, after the changes held ahead of it and before
        /// anything asked for later; and all of it carries the editor's tag,
        /// so a Cancel withdraws the change and runs the notes around it,
        /// which then find nothing changed.
        /// </summary>
        private sealed class EditorTxWrite : IDisposable
        {
            private readonly NoiseController _owner;
            private readonly EditorOwned _editor;
            private readonly TxNoiseChain? _chain;
            private readonly IDisposable? _tag;
            private (TxCompressorSetup Compressor, TxLimiterSetup Limiter)? _before;
            private TxClaritySetup? _beforeClarity;
            private bool _done;

            public EditorTxWrite(NoiseController owner, EditorOwned editor, TxNoiseChain? chain)
            {
                _owner = owner; _editor = editor; _chain = chain;
                owner._editorTxDepth++;
                _tag = chain?.OwnedBy(editor);
                After(() => { _before = TxAudioConditioning.CaptureDynamics(); _beforeClarity = TxAudioConditioning.CaptureClarity(); });
            }

            private void After(Action note)
            {
                if (_chain != null) _chain.AfterHeld(note); else note();
            }

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                try
                {
                    After(() =>
                    {
                        if (_before is { } before) _editor.Note(before, TxAudioConditioning.CaptureDynamics());
                        if (_beforeClarity is { } clarity) _editor.NoteClarity(clarity, TxAudioConditioning.CaptureClarity());
                    });
                }
                finally { _tag?.Dispose(); _owner._editorTxDepth--; }
            }
        }

        private EditorOwned? _editor;
        private int _editorTxDepth;

        /// <summary>
        /// The editor is done with. Kept (Close): nothing more is its own,
        /// and what it asked for while keyed stays waiting. Cancelled (every
        /// other way out): what it asked for while keyed and is still waiting
        /// is withdrawn, and everybody else's waiting changes stay, in their
        /// order; then what it did land is taken back, as ONE change held for
        /// the unkey while keyed, behind anything already waiting, which asks
        /// again when it runs what still reads as the editor left it. True
        /// when something was withdrawn, taken back, or is waiting to be.
        /// </summary>
        private bool EndDynamicsEditor(EditorOwned editor, bool withdraw)
        {
            if (ReferenceEquals(_editor, editor)) _editor = null;
            if (!withdraw) return false;
            var chain = _txChain();
            // Its waiting changes go; the notes around them run, so what is noted is only what landed.
            bool any = chain != null && chain.CancelHeld(editor) > 0;
            if (!editor.AnythingLanded) return any;
            bool tookBack = false;
            if (chain == null) tookBack = TakeBackEditors(editor);
            else if (chain.RunOrDefer(() => tookBack = TakeBackEditors(editor)).Timing == TxChangeTiming.AtUnkey) tookBack = true;
            return any || tookBack;
        }

        /// <summary>Take back what the editor changed and nobody has changed since. On the control side, unkeyed, in the chain's order. True when anything went back.</summary>
        private bool TakeBackEditors(EditorOwned editor)
        {
            bool any = false;
            var (compressor, limiter) = TxAudioConditioning.CaptureDynamics();
            if (editor.CompressorFloor != null && OwnedFields.TakeBack(editor.CompressorFloor, editor.CompressorLeft!, compressor) > 0)
            {
                TxAudioConditioning.SetCompressorWholeNow(compressor);
                any = true;
            }
            if (editor.LimiterFloor != null && OwnedFields.TakeBack(editor.LimiterFloor, editor.LimiterLeft!, limiter) > 0)
            {
                TxAudioConditioning.SetLimiterWholeNow(limiter);
                any = true;
            }
            if (editor.ClarityFloor != null)
            {
                var clarity = TxAudioConditioning.CaptureClarity();
                if (OwnedFields.TakeBack(editor.ClarityFloor, editor.ClarityLeft!, clarity) > 0)
                {
                    TxAudioConditioning.SetClarityWholeNow(clarity);
                    any = true;
                }
            }
            if (editor.HandoffFloor is bool floor && editor.HandoffLeft is bool left && floor != left && ProcessorHandoffSetting == left)
            {
                WriteProcessorHandoffSetting(floor);
                NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(editor.Source), "processor-handoff", "", Words(left), Words(floor));
                any = true;
            }
            editor.Forget();
            return any;
        }
    }

    /// <summary>
    /// One writer's changes to a setting, kept and taken back FIELD BY FIELD
    /// (Sprint 49 Track CL repair 3). The settings are plain saved classes
    /// (numbers, switches, words, and smaller settings of the same kind), so
    /// every public field that can be read and written is walked; a field a
    /// later build adds is covered without anything here changing, and a kind
    /// of field this cannot walk (a list) fails loudly rather than being
    /// skipped.
    /// </summary>
    internal static class OwnedFields
    {
        private static IEnumerable<PropertyInfo> Fields(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

        private static bool IsValue(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return true;
            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                throw new NotSupportedException("OwnedFields cannot walk a field of type " + type.Name + ": teach it lists before a setting holds one");
            return false;
        }

        /// <summary>
        /// Note one write by the writer. For every field the write changed:
        /// if the field no longer read as the writer had last left it,
        /// somebody else changed it in between and its value just before
        /// this write is the new floor; and the value after it is what the
        /// writer left.
        /// </summary>
        public static void Note(object floor, object left, object before, object after)
        {
            foreach (var field in Fields(before.GetType()))
            {
                object? was = field.GetValue(before), now = field.GetValue(after);
                if (!IsValue(field.PropertyType))
                {
                    object? innerFloor = field.GetValue(floor), innerLeft = field.GetValue(left);
                    if (was != null && now != null && innerFloor != null && innerLeft != null) Note(innerFloor, innerLeft, was, now);
                    continue;
                }
                if (Equals(was, now)) continue;
                if (!Equals(was, field.GetValue(left))) field.SetValue(floor, was);
                field.SetValue(left, now);
            }
        }

        /// <summary>Whether the writer has left any field other than at its floor.</summary>
        public static bool Differ(object floor, object left)
        {
            foreach (var field in Fields(floor.GetType()))
            {
                object? a = field.GetValue(floor), b = field.GetValue(left);
                if (!IsValue(field.PropertyType)) { if (a != null && b != null && Differ(a, b)) return true; continue; }
                if (!Equals(a, b)) return true;
            }
            return false;
        }

        /// <summary>
        /// Take the writer's changes back out of <paramref name="now"/>, in
        /// place: every field the writer changed that still reads as the
        /// writer left it goes back to its floor; every other field stays as
        /// it reads now. Returns how many fields went back.
        /// </summary>
        public static int TakeBack(object floor, object left, object now)
        {
            int count = 0;
            foreach (var field in Fields(now.GetType()))
            {
                object? a = field.GetValue(floor), b = field.GetValue(left), c = field.GetValue(now);
                if (!IsValue(field.PropertyType)) { if (a != null && b != null && c != null) count += TakeBack(a, b, c); continue; }
                if (Equals(a, b) || !Equals(c, b)) continue;
                field.SetValue(now, a);
                count++;
            }
            return count;
        }
    }

    /// <summary>
    /// The compressor and limiter editor's logic, windowless (Sprint 49 Track
    /// CL): the setting the editor opened with, each lever's value as the
    /// editor's fields hold it (whole numbers scaled by the lever's decimals,
    /// as <c>ValueFieldControl</c> wants them), and Cancel's put-back. The
    /// window itself is the harness walk's to press.
    /// </summary>
    public sealed class TxDynamicsEditorModel
    {
        private readonly Func<(TxCompressorSetup Compressor, TxLimiterSetup Limiter)> _read;
        private readonly Action<DynamicsLever, double> _set;
        private readonly Func<bool, bool> _end;
        private readonly Func<TxClaritySetup> _readClarity;

        /// <param name="end">The editor is done with: called with true to take back what this editor did (withdrawing what it asked for while keyed and is still waiting, and undoing what it landed), answering whether there was anything; with false to keep it all.</param>
        /// <param name="readClarity">JJ Clarity as intended now (Sprint 49 Track J: the editor's Clarity sections read it); null for an editor that shows none, which reads a new setting's.</param>
        public TxDynamicsEditorModel(Func<(TxCompressorSetup, TxLimiterSetup)> read, Action<DynamicsLever, double> set,
            Func<bool> readHandoff, Func<bool, bool> end, Func<TxClaritySetup>? readClarity = null)
        {
            _end = end ?? throw new ArgumentNullException(nameof(end));
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _set = set ?? throw new ArgumentNullException(nameof(set));
            _readClarity = readClarity ?? (() => new TxClaritySetup());
            var (c, l) = _read();
            OpenedWith = (c.Clone(), l.Clone());
            OpenedWithHandoff = (readHandoff ?? throw new ArgumentNullException(nameof(readHandoff)))();
        }

        /// <summary>The speech processor setting as it read when the editor opened (for the record; Cancel takes back the editor's own change to it, not this picture).</summary>
        public bool OpenedWithHandoff { get; }

        /// <summary>An editor on settings of its own, applying nothing anywhere: the dialog harness's road.</summary>
        public static TxDynamicsEditorModel Detached()
        {
            var compressor = new TxCompressorSetup();
            var limiter = TxLimiterSetup.ForNewSetup();
            var clarity = new TxClaritySetup();
            return new TxDynamicsEditorModel(() => (compressor, limiter), (_, _) => { }, () => true, _ => false, () => clarity);
        }

        /// <summary>JJ Clarity's setting now: a copy.</summary>
        public TxClaritySetup ClaritySetting => _readClarity().Clone();

        /// <summary>A JJ Clarity lever's value in a setting ("high.mix", "low.tune" and so on): the one read the controller, Home and the editor share.</summary>
        public static double ValueOf(DynamicsLever lever, TxClaritySetup clarity)
        {
            int dot = lever.Key.IndexOf('.');
            var band = clarity.Band(dot <= 0 || lever.Key[..dot] == "high");
            return (dot > 0 ? lever.Key[(dot + 1)..] : lever.Key) switch
            {
                "tune" => band.TuneHz, "drive" => band.DriveDb, _ => band.MixPercent,
            };
        }

        /// <summary>The setting as it read when the editor opened (for the record). Cancel does NOT put this picture back: changes asked for before the editor opened may land while it is open, and they are not the editor's to undo (repair 3).</summary>
        public (TxCompressorSetup Compressor, TxLimiterSetup Limiter) OpenedWith { get; }

        /// <summary>The setting now: copies.</summary>
        public (TxCompressorSetup Compressor, TxLimiterSetup Limiter) Setting
        {
            get { var (c, l) = _read(); return (c.Clone(), l.Clone()); }
        }

        /// <summary>A lever's value in a setting: the one read the controller, Home and the editor share.</summary>
        public static double ValueOf(DynamicsLever lever, TxCompressorSetup c, TxLimiterSetup l)
        {
            if (lever.Stage == DynamicsLever.LimiterStage)
                return lever.Key switch { "ceiling" => l.CeilingDb, "release" => l.ReleaseMs, _ => l.LookAheadMs };
            int dot = lever.Key.IndexOf('.');
            if (dot > 0)
            {
                var band = c.Band(lever.Key[..dot] switch { "low" => 0, "mid" => 1, _ => 2 });
                return lever.Key[(dot + 1)..] switch
                {
                    "threshold" => band.ThresholdDb, "ratio" => band.RatioToOne, "attack" => band.AttackMs,
                    "release" => band.ReleaseMs, _ => band.MakeupDb,
                };
            }
            return lever.Key switch
            {
                "threshold" => c.ThresholdDb, "ratio" => c.RatioToOne, "attack" => c.AttackMs, "release" => c.ReleaseMs,
                "knee" => c.KneeDb, "makeup" => c.MakeupDb, "drive" => c.DriveDb, "rotator" => c.RotatorStages,
                "crossover_low" => c.CrossoverLowHz, _ => c.CrossoverHighHz,
            };
        }

        /// <summary>The scale between a lever's value and its field's whole number.</summary>
        public static int Scale(DynamicsLever lever) => (int)Math.Pow(10, lever.Decimals);

        /// <summary>A lever's range, step and fine step as its field's whole numbers.</summary>
        public static (int Min, int Max, int Step, int FineStep) FieldRange(DynamicsLever lever)
        {
            int scale = Scale(lever);
            int fine = lever.FineStep > 0 ? Math.Max(1, (int)Math.Round(lever.FineStep * scale)) : 0;
            return ((int)Math.Round(lever.Min * scale), (int)Math.Round(lever.Max * scale), Math.Max(1, (int)Math.Round(lever.Step * scale)), fine);
        }

        /// <summary>A lever's value as its field holds it.</summary>
        public static int ToField(DynamicsLever lever, double value) => (int)Math.Round(Math.Clamp(value, lever.Min, lever.Max) * Scale(lever));

        /// <summary>A field's whole number as the lever's value.</summary>
        public static double FromField(DynamicsLever lever, int field) => Math.Clamp(field / (double)Scale(lever), lever.Min, lever.Max);

        /// <summary>The field changed: set the lever (a continuous lever live, a switch at the unkey).</summary>
        public void Set(DynamicsLever lever, int field) => _set(lever, FromField(lever, field));

        /// <summary>Close: everything the editor changed is kept, what it asked for while keyed and is still waiting included.</summary>
        public void Kept() => _end(false);

        /// <summary>
        /// Every way out but Close: take back what THIS editor did, and
        /// nothing else. What it asked for while keyed and is still waiting
        /// is withdrawn; what it landed goes back, field by field, to what
        /// each field was before the editor changed it, wherever the field
        /// still reads as the editor left it. An editor in which nothing was
        /// changed takes nothing back, whatever landed while it was open.
        /// True when there was anything to take back.
        /// </summary>
        public bool Restore() => _end(true);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>
    /// One lever of the compressor stage or the limiter (Sprint 49 Track CL,
    /// #1278): its stage, key, label, range, step, unit, and whether it is a
    /// switch (it waits for the unkey) or a continuous lever (live).
    /// </summary>
    /// <param name="Stage"><see cref="CompressorStage"/> or <see cref="LimiterStage"/>.</param>
    /// <param name="Key">The key <see cref="TxAudioConditioning.SetCompressorLevel"/> or <see cref="TxAudioConditioning.SetLimiterLevel"/> takes.</param>
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
            (stage == DynamicsLever.LimiterStage ? LimiterLevers : CompressorLevers).FirstOrDefault(l => l.Key == key);

        private static string DynamicsSubject(string stage) =>
            stage == DynamicsLever.LimiterStage ? SpeechSubject.TransmitLimiter : SpeechSubject.TransmitCompressor;

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
        /// Switch the handoff's setting and save it. With a config loaded it is
        /// saved through the controller's own persist; with none yet (no radio
        /// connected this session) the app-wide file is read, changed and
        /// written, so the choice is there for the first radio.
        /// </summary>
        public void SetProcessorHandoffSetting(bool on, string source)
        {
            using var oneUtterance = OneUtterance();
            bool was = ProcessorHandoffSetting;
            if (_config() is { } config)
            {
                config.RadioProcessorOffWithCompressor = on;
                _persist();
            }
            else
            {
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
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "processor-handoff", "", Words(was), Words(on));
            Say(L(on ? "audio.processor_handoff.setting_on" : "audio.processor_handoff.setting_off"), SpeechSubject.ProcessorHandoff);
        }

        // ────────────────────────────────────────────────────────────────
        //  The editor's model
        // ────────────────────────────────────────────────────────────────

        /// <summary>A model for the compressor and limiter's editor on this controller: what it shows and how its Cancel puts back.</summary>
        public TxDynamicsEditorModel DynamicsEditor(string source) => new(
            () => TxAudioConditioning.CaptureDynamics(),
            (lever, value) => SetDynamics(lever, value, source, speak: false),
            (compressor, limiter) =>
            {
                using var owned = OwnTx(source);
                // Each as ONE switch, held for the unkey while keyed, behind anything already held.
                TxAudioConditioning.SetCompressorWhole(compressor);
                TxAudioConditioning.SetLimiterWhole(limiter);
            });
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
        private readonly Action<TxCompressorSetup, TxLimiterSetup> _restore;

        public TxDynamicsEditorModel(Func<(TxCompressorSetup, TxLimiterSetup)> read, Action<DynamicsLever, double> set,
            Action<TxCompressorSetup, TxLimiterSetup> restore)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _set = set ?? throw new ArgumentNullException(nameof(set));
            _restore = restore ?? throw new ArgumentNullException(nameof(restore));
            var (c, l) = _read();
            OpenedWith = (c.Clone(), l.Clone());
        }

        /// <summary>An editor on settings of its own, applying nothing anywhere: the dialog harness's road.</summary>
        public static TxDynamicsEditorModel Detached()
        {
            var compressor = new TxCompressorSetup();
            var limiter = TxLimiterSetup.ForNewSetup();
            return new TxDynamicsEditorModel(() => (compressor, limiter), (_, _) => { }, (_, _) => { });
        }

        /// <summary>The setting the editor opened with: what Cancel and Escape put back.</summary>
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

        /// <summary>Cancel and Escape: put back the setting the editor opened with, whole. True when anything had changed.</summary>
        public bool Restore()
        {
            var (c, l) = _read();
            bool changed = !NoiseController.SameCompressor(c, OpenedWith.Compressor) || c.PresetId != OpenedWith.Compressor.PresetId
                || !NoiseController.SameLimiter(l, OpenedWith.Limiter);
            if (changed) _restore(OpenedWith.Compressor.Clone(), OpenedWith.Limiter.Clone());
            return changed;
        }
    }
}

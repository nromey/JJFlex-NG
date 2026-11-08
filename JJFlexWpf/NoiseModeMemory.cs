using System;
using System.Collections.Generic;
using System.Linq;

namespace JJFlexWpf
{
    /// <summary>The three mode groups per-mode memory keeps apart (ruled 2026-11-02 13:51).</summary>
    public enum NoiseModeGroup { Voice, Cw, Digital }

    /// <summary>One favourite: an engine, and optionally the model it was marked with.</summary>
    /// <remarks>
    /// Ruled by Noel 2026-11-07 21:16: favourites are arrangeable (the list's
    /// order is the bracket order, each entry has a position) and a favourite
    /// may carry an engine's model, so two models of one engine are two
    /// favourites. <see cref="Model"/> is null for an engine with no model
    /// choice. <see cref="Name"/> is the operator's own name when they rename
    /// it; empty means "the engine's name, then its model's".
    /// </remarks>
    public sealed class NoiseFavourite
    {
        public string EngineId { get; set; } = "";
        public string? Model { get; set; }
        public string Name { get; set; } = "";

        public bool SameAs(string engineId, string? model) =>
            string.Equals(EngineId, engineId, StringComparison.Ordinal) && string.Equals(Model ?? "", model ?? "", StringComparison.Ordinal);
    }

    /// <summary>One computer slot as a mode group remembers it: on or off, and which engine.</summary>
    public sealed class NoiseSlotMemory
    {
        public bool On { get; set; }
        public string EngineId { get; set; } = "";
    }

    /// <summary>What one mode group remembers.</summary>
    public sealed class NoiseGroupMemory
    {
        /// <summary>False until computer noise reduction has been used, or a starting point applied, in this group.</summary>
        public bool Known { get; set; }
        public NoiseSlotMemory Spectral { get; set; } = new();
        public NoiseSlotMemory Neural { get; set; } = new();
    }

    /// <summary>
    /// Per-mode memory as stored with the receive config (app-wide, ruled
    /// 2026-10-31 12:45). Engine settings stay app-wide per engine; the two
    /// exceptions are carried here: NR2's noise tracking in CW (Steady by
    /// default, S17) and the voice NNR model to give back on leaving CW
    /// (ruled 2026-11-04 20:23, NNR offers only its CW models in CW).
    /// </summary>
    public sealed class NoiseModeMemoryConfig
    {
        public NoiseGroupMemory Voice { get; set; } = new();
        public NoiseGroupMemory Cw { get; set; } = new();
        public NoiseGroupMemory Digital { get; set; } = new();

        /// <summary>NR2's noise tracking while in CW; empty until NR2 has been used in CW (then Steady).</summary>
        public string Nr2CwTracking { get; set; } = "";

        /// <summary>NR2's noise tracking outside CW, held while CW's own is in force.</summary>
        public string Nr2OtherTracking { get; set; } = "";

        /// <summary>The voice NNR model in force when CW was entered, given back on leaving CW; empty when none is owed.</summary>
        public string NnrVoiceModelOwed { get; set; } = "";

        public NoiseGroupMemory For(NoiseModeGroup group) => group switch
        {
            NoiseModeGroup.Cw => Cw,
            NoiseModeGroup.Digital => Digital,
            _ => Voice,
        };
    }

    /// <summary>
    /// Where the CW starting point's NNR model comes from: the helper's
    /// measured cost of the Premium CW model, when it has one.
    /// </summary>
    public interface INnrCwCostSource
    {
        /// <summary>Premium CW's 99th-percentile frame cost in milliseconds on this machine, or null when nobody has measured it.</summary>
        double? PremiumCw99thPercentileMs { get; }
    }

    /// <summary>The source the app uses today: the helper does not yet measure Premium CW (per-model calibration is held helper work), so it never knows.</summary>
    public sealed class UnmeasuredNnrCwCost : INnrCwCostSource
    {
        public double? PremiumCw99thPercentileMs => null;
    }

    /// <summary>The rules per-mode memory runs by, as pure functions so every surface and test reads one answer.</summary>
    public static class NoiseModeRules
    {
        public const string NnrStandardCw = "standard_cw";
        public const string NnrPremiumCw = "premium_cw";

        /// <summary>
        /// Premium CW is runnable when its 99th-percentile frame cost is under
        /// half the 10 millisecond frame (ruled 2026-11-07, question 11).
        /// </summary>
        public const double PremiumCwBudgetMs = 5.0;

        /// <summary>
        /// The CW starting point's model while the helper cannot measure
        /// Premium CW. Ruled by Noel 2026-11-07 21:10 (question 4), AGAINST the
        /// recommendation of Standard CW: Premium CW. The measured rule above
        /// takes over by itself the day the cost source reports a figure.
        /// PRE-TESTER ITEM: a slower tester machine needs the measured rule
        /// live, or this needs asking again.
        /// </summary>
        public const string UnmeasuredCwModel = NnrPremiumCw;

        /// <summary>The NR2 noise tracking choice CW starts with (S17, reconfirmed 2026-11-06 18:27).</summary>
        public const string Nr2CwTrackingDefault = "nstat";

        /// <summary>Which group a radio mode belongs to. Unknown and empty modes count as voice.</summary>
        public static NoiseModeGroup GroupOf(string? mode)
        {
            mode ??= "";
            if (mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase)) return NoiseModeGroup.Cw;
            if (mode.StartsWith("DIG", StringComparison.OrdinalIgnoreCase) || mode.StartsWith("FDM", StringComparison.OrdinalIgnoreCase)
                || mode.Equals("RTTY", StringComparison.OrdinalIgnoreCase)) return NoiseModeGroup.Digital;
            return NoiseModeGroup.Voice;
        }

        /// <summary>Whether an NNR model is one of the two CW models.</summary>
        public static bool IsCwModel(string? model) => model is NnrStandardCw or NnrPremiumCw;

        /// <summary>The CW model to start with, and whether it rests on a measurement.</summary>
        public static (string Model, bool Measured, bool Fast) ChooseCwModel(INnrCwCostSource source)
        {
            double? cost = source.PremiumCw99thPercentileMs;
            if (cost is not double ms || !double.IsFinite(ms)) return (UnmeasuredCwModel, false, false);
            bool fast = ms < PremiumCwBudgetMs;
            return (fast ? NnrPremiumCw : NnrStandardCw, true, fast);
        }
    }
}

using System;
using System.Diagnostics;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>
    /// The CW model chosen before this machine was measured, looked at once more
    /// when the first figure arrives (Sprint 49 Track K2, #1002; ruled by Noel
    /// 2026-11-19 07:03, X1, "that's fine").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why: the helper measures its model slots about 1.2 to 1.6 s after it
    /// starts, and it starts only when a helper engine is wanted, so on a
    /// machine's first CW session the CW starting point is chosen before any
    /// figure exists: the ruled unmeasured default, Premium CW. Seen on the
    /// bench (Track N2's walk, finding 4): the model was chosen about 0.9 s
    /// before the helper's handshake. On a slow machine that is exactly the case
    /// the measurement exists for.
    /// </para>
    /// <para>
    /// The rule, as ruled: a CW model chosen with no figure is marked
    /// provisional (<see cref="NoiseModeMemoryConfig.NnrCwModelProvisional"/>,
    /// set where the rule is asked, <see cref="ChooseCwModel(string, NnrCwCost)"/>).
    /// When the first figure arrives, if CW's model is still the provisional one
    /// and the operator has not changed it himself since, the rule is run once
    /// more; if it now says another model (Standard CW: the provisional choice is
    /// always the unmeasured default, Premium CW), the model is switched under
    /// the slot's existing warm-up cross-fade and said once. A choice made with a
    /// figure, or one the operator changed, is never looked at; the look itself
    /// clears the mark, so it happens once.
    /// </para>
    /// <para>
    /// <b>When the figure arrives outside CW</b> (my judgement, for Noel; not in
    /// the ruling's words): CW's model is not in force, so nothing is switched
    /// and the mark is kept, and the look happens the next time CW is entered
    /// with a figure on hand. Usually that entry asks the rule afresh anyway,
    /// with the figure (the voice model held outside CW becomes a CW model), and
    /// that clears the mark; the look at entry covers the rarer case in which
    /// CW's model was kept outside CW.
    /// </para>
    /// </remarks>
    public sealed partial class NoiseController
    {
        /// <summary>
        /// The helper's model costs for this machine were remembered (the host
        /// raises <see cref="WdspHost.ModelCostsRemembered"/> once the store holds
        /// them). The listener marshals to the UI thread before calling.
        /// </summary>
        public void OnModelCostsRemembered() => ReviewProvisionalCwModel("this machine's measurement arrived");

        /// <summary>The operator chose CW's model himself: a provisional mark is cleared, never looked at again.</summary>
        private void OperatorChoseCwModel(string source)
        {
            if (Memory is not { } mem || mem.NnrCwModelProvisional.Length == 0) return;
            mem.NnrCwModelProvisional = "";
            Trace.WriteLine($"NoiseController: the operator chose CW's model ({source}); the provisional mark is cleared and it will not be looked at again");
        }

        /// <summary>
        /// The one look at a provisional CW model, when a figure is on hand and
        /// CW's model is in force; otherwise nothing, and the mark stays.
        /// </summary>
        /// <param name="occasion">Trace prose.</param>
        private void ReviewProvisionalCwModel(string occasion)
        {
            if (Memory is not { } mem || mem.NnrCwModelProvisional.Length == 0) return;
            string provisional = mem.NnrCwModelProvisional;
            if (_group != NoiseModeGroup.Cw)
            {
                Trace.WriteLine($"NoiseController: {occasion}; the provisional CW model {provisional} waits for CW to be entered");
                return;
            }
            var p = _pipeline();
            if (p == null || _catalog.Find("nnr") == null) return;
            string held = p.NeuralSlot.GetSetting("nnr", "model").Choice ?? "";
            if (held != provisional)
            {
                // Something other than the rule changed CW's model since: an
                // operator's change by a road that does not report itself.
                mem.NnrCwModelProvisional = "";
                Trace.WriteLine($"NoiseController: {occasion}; CW's model is {held}, not the provisional {provisional}; not looked at again");
                _persist();
                return;
            }
            var cost = _cwCost.ReadPremiumCw();
            if (cost.Ms is null) return;   // still no figure: it stays provisional, silently
            // Asking the rule with a figure clears the mark: this is the once.
            var (model, _, _, why) = ChooseCwModel("first measurement", cost);
            if (model == held)
            {
                Trace.WriteLine($"NoiseController: {occasion}; the provisional CW model {held} stands");
                _persist();
                return;
            }
            RetireHelperNotice("the CW model changed after the first measurement");
            p.NeuralSlot.TrySetSetting("nnr", "model", new EngineSettingValue(0, model));
            JournalModelAdmission("first measurement", "nnr", held, model, why);
            Trace.WriteLine($"NoiseController: {occasion}; the provisional CW model {held} became {model}");
            _persist();
            if (p.NeuralSlot.Enabled && p.NeuralSlot.ChosenEngineId == "nnr")
                SayAfter(L("audio.noise_surface.cw_model_measured", ("model", ModelName(model))), SpeechSubject.NoiseModeStart);
        }
    }
}

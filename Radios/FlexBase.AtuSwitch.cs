using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the ATU switch: on is automatic, off is bypassed (#1136).
    /// </summary>
    /// <remarks>
    /// <para><b>Ruled by Noel 2026-11-18 (J3):</b> ATU on means automatic, where
    /// a tune press runs the ATU's own cycle; ATU off means bypassed, the
    /// manual type, for an outboard tuner, where a tune press sends a plain tune
    /// carrier. "None" is gone from the switch and from ATU Mode.</para>
    /// <para><b>Why this exists.</b> The menu's ATU On/Off and the Fields
    /// panel's ATU box read "on" as "type is not none", and their off wrote
    /// none, which the setter turns into manual. So after an "off" the switch
    /// still read on, and every later press said "ATU off" again: it could
    /// never switch the tuner back on.</para>
    /// <para><b>Never speak a change the setter refused.</b> While the tuner
    /// latch is set the <see cref="FlexTunerType"/> setter ignores every change
    /// without a word. <see cref="SetAtuAutomatic"/> goes through that setter,
    /// unchanged, then reads the type back, so a surface says what is true or
    /// says why nothing changed.</para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>The switch reads on exactly when the type is automatic. Pure.</summary>
        public static bool AtuSwitchReadsOn(FlexTunerTypes type) => type == FlexTunerTypes.auto;

        /// <summary>
        /// Turn the ATU on (automatic) or off (bypassed), through the
        /// <see cref="FlexTunerType"/> setter and its rules. Returns null when
        /// the type now reads as asked, otherwise the sentence saying why it
        /// did not change.
        /// </summary>
        public string SetAtuAutomatic(bool automatic)
        {
            FlexTunerTypes wanted = automatic ? FlexTunerTypes.auto : FlexTunerTypes.manual;
            bool latched = _FlexTunerOn;
            FlexTunerType = wanted;
            if (AtuSwitchReadsOn(FlexTunerType) == automatic) return null;
            Tracing.TraceLine("SetAtuAutomatic: asked " + wanted + ", the type reads " + FlexTunerType
                + (latched ? " (the tuner latch is set)" : ""), TraceLevel.Info);
            return latched
                ? Lexicon.Get("settings.atu.refused_while_tuning")
                : Lexicon.Get("settings.atu.did_not_change");
        }
    }
}

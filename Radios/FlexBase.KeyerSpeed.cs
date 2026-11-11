using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the operator's keyer speed as Settings sets it (#879).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The public route.</b> <see cref="KeyerSpeed"/> is internal, and the
    /// application reaches it through these two members rather than through a
    /// wider <c>InternalsVisibleTo</c>. The keyer speed is the OPERATOR's, saved
    /// in their own <c>issue7620.xml</c> and applied to whichever of their
    /// radios they connect to, at the owner's gated moment
    /// (<c>ApplyKeyerRestoreToRadio</c>).
    /// </para>
    /// <para>
    /// <b>Someone else's radio (#686, Noel's isolation ruling: one operator does
    /// not touch another operator's settings).</b> The keyer speed is radio-wide, so writing it on a
    /// radio that is not the operator's would change its owner's keyer. There,
    /// a change is saved as the operator's own value only, and reaches a radio
    /// at the next connect to one of their own. The decision is the one every
    /// guest write asks, <see cref="StationLayoutSaveGuard.OwnershipRefusal"/>
    /// on <see cref="RadioConfig.OwnershipOf"/>: only a radio declared the
    /// operator's is written. This reading of #686 is the orchestrator's, for
    /// Noel to confirm.
    /// </para>
    /// <para>
    /// <b>Never the file behind this object's back.</b> While connected this
    /// object holds the whole keyer setup and rewrites the file on any keyer
    /// change, so the change goes through it, here; the file is written
    /// directly only with no radio (<see cref="KeyerSpeedStore"/>).
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>The operator's own keyer speed, in words per minute: the one saved and applied at connect.</summary>
        public int OperatorKeyerSpeed => cfgData.KeyerSpeed;

        /// <summary>
        /// Set the operator's keyer speed from Settings: saved as theirs, and
        /// written to the radio only when the radio is declared theirs. True
        /// when it was written to the radio; false when it was saved for the
        /// operator only.
        /// </summary>
        public bool SetOperatorKeyerSpeed(int wordsPerMinute)
        {
            int wpm = Math.Clamp(wordsPerMinute, KeyerSpeedMin, KeyerSpeedMax);
            if (KeyerSpeedWriteIsOurs(out string whose))
            {
                KeyerSpeed = wpm;
                Tracing.TraceLine("Keyer speed: " + wpm + " WPM, saved for the operator and written to the radio", TraceLevel.Info);
                return true;
            }
            cfgData.KeyerSpeed = wpm;
            issue7620(true);
            Tracing.TraceLine("Keyer speed: " + wpm + " WPM, saved for the operator only; the radio's keyer was left alone ("
                + whose + "). It is applied at the next connect to a radio of the operator's own.", TraceLevel.Info);
            return false;
        }

        /// <summary>
        /// May the operator's keyer speed be written to this radio? Only a
        /// connected radio declared the operator's.
        /// </summary>
        private bool KeyerSpeedWriteIsOurs(out string whose)
        {
            var radio = theRadio;
            if (radio == null || !IsConnected)
            {
                whose = "no radio is connected";
                return false;
            }
            var refusal = StationLayoutSaveGuard.OwnershipRefusal(RadioConfig.OwnershipOf(radio.Serial));
            whose = refusal == StationLayoutSaveRefusal.None ? "the operator's own radio" : "radio " + radio.Serial + ": " + refusal;
            return refusal == StationLayoutSaveRefusal.None;
        }
    }
}

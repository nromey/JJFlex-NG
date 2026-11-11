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
        /// <summary>
        /// The operator folder <c>cfgData</c> was read from or written to: whose
        /// keyer setup this object holds (#879 repair one). Null until the
        /// first read or write. The folder itself follows the CURRENT operator
        /// (<c>OperatorsDirectory</c>), and an operator can change while a radio
        /// stays connected, so the two can part.
        /// </summary>
        private string _keyerSetupFolder;

        /// <summary>True when the setup held is this folder's, or nobody's yet.</summary>
        private bool KeyerSetupBelongsTo(string operatorFolder) =>
            _keyerSetupFolder == null
            || string.Equals(_keyerSetupFolder, operatorFolder, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Bring the held keyer setup up to the current operator's before it is
        /// read or changed: when the operator has changed since it was read,
        /// read the new operator's own file (or start them from the defaults).
        /// Writes nothing.
        /// </summary>
        internal void EnsureKeyerSetupIsCurrentOperators()
        {
            string folder;
            try { folder = OperatorsDirectory; }
            catch (Exception ex)
            {
                Tracing.TraceLine("Keyer setup: the operator's folder is not available: " + ex.Message, TraceLevel.Warning);
                return;
            }
            if (_keyerSetupFolder != null && KeyerSetupBelongsTo(folder)) return;
            if (_keyerSetupFolder != null)
                Tracing.TraceLine("Keyer setup: the operator changed (" + _keyerSetupFolder + " to " + folder
                    + "); reading the new operator's own keyer setup", TraceLevel.Info);
            issue7620(false);
        }

        /// <summary>The current operator's own keyer speed, in words per minute: the one saved and applied at connect.</summary>
        public int OperatorKeyerSpeed
        {
            get
            {
                EnsureKeyerSetupIsCurrentOperators();
                return cfgData.KeyerSpeed;
            }
        }

        /// <summary>
        /// Set the current operator's keyer speed from Settings: saved as
        /// theirs, and written to the radio only when the radio is declared
        /// theirs. Says which: <see cref="KeyerSpeedStore.Outcome.WrittenToRadio"/>,
        /// or saved for the operator only because the radio is someone else's
        /// (<see cref="KeyerSpeedStore.Outcome.SavedNotThisRadio"/>), its
        /// ownership was never answered
        /// (<see cref="KeyerSpeedStore.Outcome.SavedOwnershipUnknown"/>), or no
        /// radio is connected (<see cref="KeyerSpeedStore.Outcome.SavedForNextConnect"/>).
        /// </summary>
        public KeyerSpeedStore.Outcome SetOperatorKeyerSpeed(int wordsPerMinute)
        {
            int wpm = Math.Clamp(wordsPerMinute, KeyerSpeedMin, KeyerSpeedMax);
            EnsureKeyerSetupIsCurrentOperators();
            var refusal = KeyerSpeedWriteRefusal(out string whose);
            if (refusal == StationLayoutSaveRefusal.None)
            {
                KeyerSpeed = wpm;
                Tracing.TraceLine("Keyer speed: " + wpm + " WPM, saved for the operator and written to the radio", TraceLevel.Info);
                return KeyerSpeedStore.Outcome.WrittenToRadio;
            }
            cfgData.KeyerSpeed = wpm;
            issue7620(true);
            Tracing.TraceLine("Keyer speed: " + wpm + " WPM, saved for the operator only; the radio's keyer was left alone ("
                + whose + "). It is applied at a later connect to a radio of the operator's own, when that connect puts the operator's settings on it.", TraceLevel.Info);
            switch (refusal)
            {
                case StationLayoutSaveRefusal.NotOurRadio: return KeyerSpeedStore.Outcome.SavedNotThisRadio;
                case StationLayoutSaveRefusal.OwnershipUnknown: return KeyerSpeedStore.Outcome.SavedOwnershipUnknown;
                default: return KeyerSpeedStore.Outcome.SavedForNextConnect;
            }
        }

        /// <summary>
        /// May the operator's keyer speed be written to this radio? Only a
        /// connected radio declared the operator's:
        /// <see cref="StationLayoutSaveRefusal.None"/>. Otherwise why not.
        /// </summary>
        private StationLayoutSaveRefusal KeyerSpeedWriteRefusal(out string whose)
        {
            var radio = theRadio;
            if (radio == null || !IsConnected)
            {
                whose = "no radio is connected";
                return StationLayoutSaveRefusal.NoRadio;
            }
            var refusal = StationLayoutSaveGuard.OwnershipRefusal(RadioConfig.OwnershipOf(radio.Serial));
            whose = refusal == StationLayoutSaveRefusal.None ? "the operator's own radio" : "radio " + radio.Serial + ": " + refusal;
            return refusal;
        }
    }
}

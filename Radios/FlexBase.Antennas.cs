#define CWMonitor
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the antenna properties.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        #region Antenna Properties — Sprint 22

        // ── Why these two setters trace, and the other Sprint 22 ones do not ──
        //
        // #188. Antenna selection was the one transmit-path choice this app
        // could make that left no record anywhere. Nothing in the app logged a
        // change, so EVERY power and standing-wave figure ever captured on the
        // bench has no recorded port: a reader of that capture — including the
        // person who took it, ten minutes later — cannot say which connector
        // the RF left by. That is a first-order fact for a tool whose job
        // includes walking the transmit chain, and it was simply absent.
        //
        // It also made a negative result uninterpretable. On 2026-08-22 the RX
        // antenna was switched between ANT1 and ANT2, no difference was heard,
        // and there was no way to tell whether that meant "both ports sound
        // alike" or "the switch never took". A negative result needs a positive
        // control and the instrument could not even be checked.
        //
        // The only ANT1 strings in a capture came from FlexLib's own
        // ApplyEqualizerActiveStatus logging — vendor code, firing on its own
        // schedule, reporting whatever IT considers current rather than
        // announcing a change.
        //
        // WHAT THE REPORT-BACK CAN AND CANNOT TELL YOU. FlexLib's Slice.RXAnt /
        // TXAnt setters update their own cached value BEFORE sending the
        // command, and their status parser drops any echo that matches the
        // cache (Slice.cs, case "rxant" / "txant": `if (_rxant == value)
        // continue;`). So the radio AGREEING is silent — there is no
        // confirmation event to trace. The only report-back that reaches us is
        // one that DISAGREES with what we cached, which is exactly the
        // acked-but-not-applied hazard of #164 and exactly what is worth a
        // Warning. Absence of a warning is therefore weak evidence, not proof;
        // say so rather than reading silence as confirmation.
        //
        // The last-requested value is remembered per direction so a disagreeing
        // report can name what was asked for. It is deliberately NOT cleared on
        // a match: the match we see is our own optimistic echo, not the radio's
        // answer. And a later disagreement is reported as an OBSERVATION rather
        // than a diagnosis, because another GUI client changing the port
        // produces the same event as a change that never took.
        private string _rxAntRequested;
        private string _txAntRequested;
        // WHICH slice each request went to. The transmit-side echo is only a
        // confirmation when it comes from the slice we asked AND that slice is
        // the one the radio transmits from (#496).
        private int _rxAntRequestedSlice = -1;
        private int _txAntRequestedSlice = -1;

        /// <summary>Current RX antenna name for active slice (e.g. "ANT1", "ANT2", "RX_A").</summary>
        public string RXAntennaName
        {
            get => theRadio?.ActiveSlice?.RXAnt ?? "ANT1";
            set
            {
                var s = theRadio?.ActiveSlice;
                if (s == null)
                {
                    Tracing.TraceLine($"Antenna:RX request '{value}' dropped — no active slice",
                                      TraceLevel.Warning);
                    return;
                }
                Tracing.TraceLine($"Antenna:RX request slice {s.Index}: '{s.RXAnt}' -> '{value}'",
                                  TraceLevel.Info);
                NoteOperatorChangedRadioSetting("RXAntennaName");
                _rxAntRequested = value;
                _rxAntRequestedSlice = s.Index;
                s.RXAnt = value;
            }
        }

        /// <summary>
        /// The transmit antenna — of the TRANSMIT slice, which is where the RF
        /// leaves the radio, not of the slice the operator happens to be
        /// looking at. Falls back to the selected slice only when no slice is
        /// transmitting, and says so in the trace.
        /// </summary>
        /// <remarks>
        /// <para><b>#496, confirmed at the bench 2026-09-01.</b> This bound to
        /// <c>ActiveSlice</c> — the SELECTED slice. On a multi-slice radio that
        /// is a different object from the transmit slice, so the request went
        /// to slice 3, slice 3 truthfully echoed the change, and the RF kept
        /// leaving slice 0 by the old port. Two hours and four wrong
        /// hypotheses, because 100 W into what was believed to be a bare port
        /// came back at 0.1% reflected with no foldback — impossible into an
        /// open circuit, and the clue: the radio was seeing a good match, so
        /// the RF was in a good load. With an amplifier or a band-specific
        /// antenna in the path this is equipment damage, not a wrong reading.</para>
        /// <para>Same class as #502, the SC_MIC copy that never streams: bind
        /// to one instance of a per-slice thing, the radio uses another, every
        /// echo reports success, and the symptom is silence. The rule for both
        /// is the transmit chain's instance — here resolved by
        /// <see cref="SliceForTransmitSetting()"/>, there by
        /// <see cref="TransmitMeterElection"/>.</para>
        /// </remarks>
        public string TXAntennaName
        {
            get => SliceForTransmitSetting()?.TXAnt ?? "ANT1";
            set
            {
                Slice s = SliceForTransmitSetting(out string basis);
                if (s == null)
                {
                    Tracing.TraceLine($"Antenna:TX request '{value}' dropped — no slice to apply it to",
                                      TraceLevel.Warning);
                    // Inside a check's restore a dropped write is a write that
                    // was not made, never a restore that succeeded (Codex round
                    // 2, R2-B2): the restore reads Failed and the debt is kept.
                    RestoreWriteReplies.Ambient?.NotSent("slice set ? txant=" + value, "no transmit slice to apply it to");
                    return;
                }
                Tracing.TraceLine($"Antenna:TX request slice {s.Index} ({basis}): '{s.TXAnt}' -> '{value}'",
                                  TraceLevel.Info);
                NoteRadioSettingWritten("TXAntennaName", value);   // the hold after a cut needs to know whose write this is (#571)
                _txAntRequested = value;
                _txAntRequestedSlice = s.Index;
                // Inside a check's restore the write is queued with its own
                // reply (round 1030 repairs); everywhere else it goes straight
                // out through FlexLib's setter, as it always has.
                if (RestoreWriteReplies.Ambient != null && value != null)
                    EnqueueSettingWrite((FunctionDel)(() => { s.TXAnt = value; }),
                        RadioWrite("tx-antenna", "slice set " + s.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + " txant=" + value,
                            "slice " + s.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), "txant", value), "TXAntennaName");
                else
                    s.TXAnt = value;
            }
        }

        /// <summary>
        /// Trace what the radio now reports an antenna to be, against what this
        /// app last asked for — and, for the transmit port, against WHICH slice
        /// it asked. Called from the slice property-changed handler for RXAnt
        /// and TXAnt.
        /// </summary>
        /// <remarks>
        /// <para>Wording is deliberately observational. "Requested and reported
        /// disagree" is a thing we watched happen; "the change did not take"
        /// would be a conclusion, and another GUI client moving the port
        /// produces the identical event.</para>
        /// <para><b>A match is not a confirmation unless it is the transmit
        /// slice matching (#496).</b> On 2026-09-01 this line reported "matches
        /// the last request from here" on every antenna change, and every one
        /// of them was true — of slice 3, while the RF left slice 0. So the
        /// transmit-side line now says which slice the radio transmits from and
        /// which port that slice is on, and a match on any other slice is
        /// traced as a warning, because that is the exact shape of the defect.</para>
        /// </remarks>
        private void ReportAntennaChange(Slice s, string direction, string reported, string requested,
                                         int requestedSlice)
        {
            string now = string.IsNullOrEmpty(reported) ? "(empty)" : reported;
            string head = $"Antenna:{direction} slice {s.Index} now '{now}'";
            bool tx = string.Equals(direction, "TX", StringComparison.Ordinal);
            string chain = tx ? TransmitChainNote(s) : "";

            if (string.IsNullOrEmpty(requested))
            {
                Tracing.TraceLine(head + " — not requested from here" + chain, TraceLevel.Info);
                return;
            }

            if (requestedSlice >= 0 && requestedSlice != s.Index)
            {
                Tracing.TraceLine(head + $" — the last request from here ('{requested}') was for slice "
                    + $"{requestedSlice}, not this one" + chain, TraceLevel.Info);
                return;
            }

            if (string.Equals(requested, reported, StringComparison.OrdinalIgnoreCase))
            {
                bool matchOnWrongSlice = tx && !IsTransmitChainSlice(s);
                Tracing.TraceLine(head + " — matches the last request from here" + chain,
                    matchOnWrongSlice ? TraceLevel.Warning : TraceLevel.Info);
                return;
            }

            Tracing.TraceLine(head + $" but '{requested}' was requested from here — requested and reported disagree"
                + chain, TraceLevel.Warning);
        }

        /// <summary>For a transmit-port trace: is this the slice the RF leaves
        /// by, and if not, which is and on what port.</summary>
        private string TransmitChainNote(Slice s)
        {
            Slice tx = TransmitChainSliceOrNull();
            if (tx == null) return "; no slice is transmitting";
            if (tx.Index == s.Index) return "; this is the transmit slice, so this is the port the RF leaves by";
            return $"; NOT the transmit slice — the RF leaves slice {tx.Index} by '{tx.TXAnt}'";
        }

        private bool IsTransmitChainSlice(Slice s)
        {
            Slice tx = TransmitChainSliceOrNull();
            return tx != null && s != null && tx.Index == s.Index;
        }

        /// <summary>
        /// The slice the radio transmits from, for this client, or null when
        /// none does. FlexLib's own answer first (the slice flagged tx that
        /// belongs to our client handle), then the VFO this app tracks —
        /// checked against the slice's own flag, so a stale VFO cannot name a
        /// slice that has stopped transmitting.
        /// </summary>
        internal Slice TransmitChainSliceOrNull()
        {
            Radio r = theRadio;
            if (r == null) return null;
            Slice s = null;
            try { s = r.TransmitSlice; }
            catch (Exception ex) { Tracing.TraceLine("TransmitChainSlice: " + ex.Message, TraceLevel.Warning); }
            if (s == null && ValidVFO(TXVFO))
            {
                Slice tracked = VFOToSlice(TXVFO);
                if (tracked != null && tracked.IsTransmitSlice) s = tracked;
            }
            return s;
        }

        /// <summary>
        /// The slice a transmit-chain setting goes to — antenna, transmit
        /// offset, the transverter band — resolved by
        /// <see cref="TransmitSettingTarget"/>: the transmit slice, or the
        /// selected slice only when nothing transmits. Never the selected
        /// slice while a transmit slice exists; that was #496.
        /// </summary>
        internal Slice SliceForTransmitSetting() => SliceForTransmitSetting(out _);

        /// <summary>
        /// As <see cref="SliceForTransmitSetting()"/>, with the basis in words
        /// for the trace, naming the selected slice whenever it is a different
        /// slice from the one the setting went to.
        /// </summary>
        internal Slice SliceForTransmitSetting(out string basis)
        {
            Slice tx = TransmitChainSliceOrNull();
            Slice selected = theRadio?.ActiveSlice;
            Slice target = TransmitSettingTarget.Resolve(tx, selected, out TransmitSettingTarget.Basis b);
            basis = TransmitSettingTarget.Describe(b, tx?.Index ?? -1, selected?.Index ?? -1);
            return target;
        }

        /// <summary>Available RX antenna list from the active slice. Dynamic per radio model.</summary>
        public List<string> RXAntennaList =>
            theRadio?.ActiveSlice?.RXAntList?.ToList() ?? new List<string> { "ANT1", "ANT2" };

        /// <summary>Available TX antenna list — from the transmit slice, or the selected
        /// slice when nothing transmits, like <see cref="TXAntennaName"/>. Dynamic per radio model.</summary>
        public List<string> TXAntennaList =>
            SliceForTransmitSetting()?.TXAntList?.ToList() ?? new List<string> { "ANT1", "ANT2" };

        #endregion
    }
}

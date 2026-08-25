using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using JJTrace;

namespace Radios.ChainChecks
{
    /// <summary>
    /// Puts the radio into a mode the tone ladder can measure, reads the
    /// transmit filter that mode actually uses, derives the ladder from it —
    /// and puts the mode back when disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A disposable rather than a pair of methods, on purpose.</b> The
    /// restore is the part that must never be skipped: an operator left in a
    /// mode they did not choose, on a radio they are about to transmit with, is
    /// a worse outcome than the test simply not running. A <c>using</c> is a
    /// <c>finally</c> that cannot be forgotten, so forgetting is made
    /// structurally hard rather than merely documented.
    /// </para>
    /// <para>
    /// <b>THE ORDER IS THE WHOLE POINT.</b> Capture the mode, switch, and only
    /// THEN read TXFilterLow and TXFilterHigh. Filter cuts are PER MODE. Cuts
    /// read before a switch describe a passband the test is not going to run
    /// in, which puts the ladder's rungs in the wrong places again — the exact
    /// defect this whole task exists to remove (#221), but harder to spot the
    /// second time, because the code would then contain a truthful-looking
    /// "we read the real value" that is nonetheless wrong.
    /// </para>
    /// <para>
    /// The decisions live next door in <see cref="TxToneLadder"/> and are
    /// tested without a radio. This is plumbing and safety only.
    /// </para>
    /// </remarks>
    public sealed class TxToneLadderScope : IDisposable
    {
        /// <summary>
        /// How long to wait for a mode change to take. TXMode is queued through
        /// the command queue like every other write, so setting it is a request
        /// rather than an event.
        /// </summary>
        public const int ModeChangeTimeoutMs = 2000;

        private readonly FlexBase _rig;
        private readonly string _originalMode;
        private readonly bool _switched;
        private bool _disposed;

        /// <summary>What was decided about the mode, and why.</summary>
        public TxToneLadder.ModePlan Plan { get; }

        /// <summary>
        /// The transmit passband READ AFTER any mode change, or
        /// <see cref="TxToneLadder.Passband.Unknown"/> if it could not be read.
        /// </summary>
        public TxToneLadder.Passband Passband { get; }

        /// <summary>
        /// The ladder derived from <see cref="Passband"/>. Empty when the
        /// passband is unknown or the mode was refused — never a default
        /// ladder, because falling back to a remembered one is how the original
        /// bug returns.
        /// </summary>
        public TxToneLadder.Rung[] Rungs { get; }

        /// <summary>True when there is a ladder to run.</summary>
        public bool CanRun => Rungs != null && Rungs.Length > 0;

        /// <summary>
        /// Why there is nothing to run, when <see cref="CanRun"/> is false.
        /// Empty when it can run.
        /// </summary>
        public string BlockedReason { get; }

        private TxToneLadderScope(FlexBase rig, string originalMode, bool switched,
                                  TxToneLadder.ModePlan plan,
                                  TxToneLadder.Passband band,
                                  TxToneLadder.Rung[] rungs,
                                  string blocked)
        {
            _rig = rig;
            _originalMode = originalMode ?? "";
            _switched = switched;
            Plan = plan;
            Passband = band;
            Rungs = rungs ?? Array.Empty<TxToneLadder.Rung>();
            BlockedReason = blocked ?? "";
        }

        /// <summary>
        /// Enter the scope: decide, switch if needed, read the filter, derive
        /// the ladder. Always returns a scope — a blocked one rather than null,
        /// so the caller's <c>using</c> is never conditional and the restore
        /// path is identical whether or not the test ran.
        /// </summary>
        public static TxToneLadderScope Enter(FlexBase rig)
        {
            if (rig == null)
                return Blocked(null, "", TxToneLadder.ModePlan.No("", "no radio"),
                               "there is no radio to test");

            string mode = SafeMode(rig);
            ulong hz = SafeTxFrequency(rig);

            TxToneLadder.ModePlan plan = TxToneLadder.PlanForMode(mode, hz);

            if (plan.Action == TxToneLadder.ModeAction.Refuse)
            {
                Tracing.TraceLine("TxToneLadderScope: not running — " + plan.Reason,
                                  TraceLevel.Info);
                return Blocked(rig, mode, plan, plan.Reason);
            }

            bool switched = false;
            if (plan.Action == TxToneLadder.ModeAction.SwitchAndRestore)
            {
                // Trace the original BEFORE changing anything. Even a hard
                // crash then leaves a record of what to put back.
                Tracing.TraceLine("TxToneLadderScope: mode is " + mode
                    + ", switching to " + plan.SwitchTo + " for the test; will restore",
                    TraceLevel.Info);

                if (!SetModeAndConfirm(rig, plan.SwitchTo))
                {
                    Tracing.TraceLine("TxToneLadderScope: the radio did not take the mode "
                        + "change to " + plan.SwitchTo + " within " + ModeChangeTimeoutMs
                        + " ms", TraceLevel.Warning);
                    return Blocked(rig, mode, plan,
                        "the radio did not accept a change to " + plan.SwitchTo
                        + ", so the test was not run and your mode is unchanged");
                }
                switched = true;
            }

            // ONLY NOW. The cuts belong to the mode the test will actually run
            // in, which may not be the one the operator was in a moment ago.
            TxToneLadder.Passband band = ReadPassband(rig);
            TxToneLadder.Rung[] rungs = TxToneLadder.DeriveRungs(band);

            if (rungs.Length == 0)
            {
                string why = band.Known
                    ? "the transmit filter reads as " + band
                      + ", which is not a passband a tone ladder can be built across"
                    : "the radio did not report its transmit filter, so the tones could not "
                      + "be placed against your actual passband";

                Tracing.TraceLine("TxToneLadderScope: no ladder — " + why, TraceLevel.Warning);
                return new TxToneLadderScope(rig, mode, switched, plan, band, null, why);
            }

            Tracing.TraceLine("TxToneLadderScope: passband " + band + ", "
                + rungs.Length.ToString(CultureInfo.InvariantCulture) + " rungs, airtime "
                + TxToneLadder.TotalMsFor(rungs).ToString(CultureInfo.InvariantCulture) + " ms",
                TraceLevel.Info);

            return new TxToneLadderScope(rig, mode, switched, plan, band, rungs, "");
        }

        /// <summary>
        /// Put the mode back. Idempotent, and never throws — this runs from a
        /// <c>using</c>, and an exception escaping here would replace whatever
        /// actually went wrong with a failure to tidy up.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (!_switched || _rig == null || _originalMode.Length == 0) return;

            try
            {
                if (SetModeAndConfirm(_rig, _originalMode))
                {
                    Tracing.TraceLine("TxToneLadderScope: mode restored to " + _originalMode,
                                      TraceLevel.Info);
                }
                else
                {
                    // Loud, because the operator is now somewhere they did not
                    // ask to be and cannot see it.
                    Tracing.TraceLine("TxToneLadderScope: COULD NOT RESTORE MODE to "
                        + _originalMode + " — the radio may be left in " + Plan.SwitchTo,
                        TraceLevel.Error);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxToneLadderScope: COULD NOT RESTORE MODE to "
                    + _originalMode + " — " + ex.Message, TraceLevel.Error);
            }
        }

        // -------- plumbing --------

        private static TxToneLadderScope Blocked(FlexBase rig, string mode,
                                                 TxToneLadder.ModePlan plan, string why)
            => new TxToneLadderScope(rig, mode, switched: false, plan,
                                     TxToneLadder.Passband.Unknown, null, why);

        /// <summary>
        /// Set the mode and wait for the radio to confirm it. The write is
        /// queued, so "I set it" is not "it happened" — the same trap as
        /// TxTune in the transmitter probe.
        /// </summary>
        private static bool SetModeAndConfirm(FlexBase rig, string mode)
        {
            try { rig.TXMode = mode; }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxToneLadderScope: could not set mode — " + ex.Message,
                                  TraceLevel.Warning);
                return false;
            }

            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ModeChangeTimeoutMs)
            {
                if (string.Equals(SafeMode(rig), mode, StringComparison.OrdinalIgnoreCase))
                    return true;
                Thread.Sleep(25);
            }
            return false;
        }

        private static TxToneLadder.Passband ReadPassband(FlexBase rig)
        {
            try
            {
                int low = rig.TXFilterLow;
                int high = rig.TXFilterHigh;

                // A radio that has not reported yet gives zeros, and zero-width
                // is not a passband. Unknown is the honest answer; DeriveRungs
                // then yields nothing to run rather than a remembered ladder.
                if (high <= low) return TxToneLadder.Passband.Unknown;

                return TxToneLadder.Passband.Read(low, high);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxToneLadderScope: could not read the transmit filter — "
                    + ex.Message, TraceLevel.Warning);
                return TxToneLadder.Passband.Unknown;
            }
        }

        private static string SafeMode(FlexBase rig)
        {
            try { return rig?.TXMode ?? ""; } catch { return ""; }
        }

        private static ulong SafeTxFrequency(FlexBase rig)
        {
            try { return rig?.TXFrequency ?? 0UL; } catch { return 0UL; }
        }
    }
}

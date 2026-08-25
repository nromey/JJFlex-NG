using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using JJTrace;

namespace Radios.ChainChecks
{
    /// <summary>
    /// Keys the radio's own tune carrier and samples the transmit meters
    /// through it. The half of <see cref="TxTuneProbe"/> that touches FlexLib.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept apart from <see cref="TxTuneProbe"/> for the same reason
    /// <see cref="TxDifferentialCapture"/> is kept apart from
    /// <see cref="TxDifferential"/>: the part that decides what an operator is
    /// told must be testable without a radio in the room, and this part cannot
    /// be. Everything here is plumbing and safety; every judgement lives next
    /// door and has thirty tests over it.
    /// </para>
    /// <para>
    /// <b>This transmits.</b> Nothing in here runs without a declared load
    /// (#180), and the carrier is dropped in a finally on every path out —
    /// success, early stop, cancellation, or an exception nobody predicted. A
    /// diagnostic that leaves a transmitter keyed is worse than no diagnostic.
    /// </para>
    /// </remarks>
    public static class TxTuneProbeRunner
    {
        /// <summary>How often to read the meters while the carrier is up.</summary>
        /// <remarks>
        /// The transmit meters arrive with the transmit chain and refresh a few
        /// times a second — measured on the bench 8600 2026-08-20, eleven meters
        /// with no client connected and thirty-five once the chain is up. 150 ms
        /// samples faster than they update, which is deliberate: an unchanged
        /// reading is still a reading, and the early-stop rule counts
        /// consecutive bad samples rather than elapsed time.
        /// </remarks>
        public const int SampleEveryMs = 150;

        /// <summary>
        /// How long to wait for the radio to actually key before giving up on
        /// it. <c>FlexBase.TxTune</c> is QUEUED, not immediate — the setter
        /// enqueues a command rather than writing the radio directly — so
        /// "I set it" is not "it happened", and a probe that assumed otherwise
        /// would measure the moment before anything occurred.
        /// </summary>
        public const int KeyUpTimeoutMs = 1500;

        /// <summary>
        /// Run the probe. Blocking; call it off the UI thread.
        /// </summary>
        /// <param name="rig">The radio.</param>
        /// <param name="loadDeclared">
        /// Has the operator declared what is on the antenna port? FALSE refuses
        /// to transmit. This is a parameter rather than something read from
        /// config on purpose: the caller must have asked, and passing a literal
        /// true here should look as deliberate in a diff as it is in effect.
        /// </param>
        /// <param name="cancel">Stops the carrier and reports Cancelled.</param>
        /// <param name="antennaPort">
        /// Which port to key into, or null to use whatever is selected. The
        /// parameter exists for #190, which walks every port in turn; switching
        /// is NOT implemented here yet, and a non-null value that differs from
        /// the current port is refused rather than silently ignored.
        /// </param>
        /// <param name="onKeyConfirmed">
        /// Called once, when the RADIO confirms it is transmitting — not when
        /// the setter returned. The host uses this to start charging key-down
        /// time, so a grant the radio never honoured is never billed for.
        /// </param>
        /// <param name="onUnkeyed">
        /// Called after the carrier is down, on every path out including a
        /// throw. Safe to receive without a matching key-confirmed: the host's
        /// accounting is written to tolerate that, because an unkey notice is
        /// the one thing that must never be conditional.
        /// </param>
        public static TxTuneProbe.Result Run(FlexBase rig,
                                             bool loadDeclared,
                                             CancellationToken cancel = default,
                                             string antennaPort = null,
                                             Action onKeyConfirmed = null,
                                             Action onUnkeyed = null)
        {
            // --- refusals, before anything is keyed ---
            //
            // The DECISION lives in TxTuneProbe.CheckPreconditions and is
            // tested exhaustively without a radio. This gathers the facts and
            // acts on the answer; it does not decide anything itself.

            bool alreadyTransmitting = false;
            if (rig != null)
            {
                try { alreadyTransmitting = rig.Transmit || rig.TxTune; }
                catch (Exception ex)
                {
                    return Refuse(TxTuneProbe.SkipReason.RadioNotReachable,
                                  "could not read transmit state: " + ex.Message);
                }
            }

            TxTuneProbe.SkipReason refusal = TxTuneProbe.CheckPreconditions(
                haveRadio: rig != null,
                loadDeclared: loadDeclared,
                alreadyTransmitting: alreadyTransmitting,
                cancelled: cancel.IsCancellationRequested);

            if (refusal != TxTuneProbe.SkipReason.None)
                return Refuse(refusal, "precondition");

            if (antennaPort != null)
            {
                string current = SafeAntenna(rig);
                if (!string.Equals(antennaPort, current, StringComparison.OrdinalIgnoreCase))
                    return Refuse(TxTuneProbe.SkipReason.RadioNotReachable,
                                  "port switching is not implemented yet (asked for "
                                  + antennaPort + ", radio is on " + current + ")");
            }

            // --- key, sample, and drop the carrier whatever happens ---

            int tunePower = SafeTunePower(rig);
            var meters = new List<TxTuneProbe.Reading>(TxTuneProbe.Watched.Length);
            double lastComputedSwr = double.NaN;
            bool stoppedEarly = false;
            bool everKeyed = false;

            Tracing.TraceLine("TxTuneProbeRunner: keying tune carrier, power "
                + tunePower.ToString(CultureInfo.InvariantCulture)
                + ", antenna " + SafeAntenna(rig), TraceLevel.Info);

            try
            {
                rig.TxTune = true;

                // The write is queued. Wait for the radio to say it happened
                // rather than believing the setter — and if it never does, that
                // is itself the finding, not a reason to keep waiting.
                everKeyed = WaitForKeyUp(rig, cancel);
                if (everKeyed) Witness(onKeyConfirmed, "onKeyConfirmed");
                if (!everKeyed)
                {
                    Tracing.TraceLine("TxTuneProbeRunner: radio never reported transmitting "
                        + "within " + KeyUpTimeoutMs + " ms", TraceLevel.Warning);
                }

                var elapsed = Stopwatch.StartNew();
                int consecutiveBad = 0;

                while (elapsed.ElapsedMilliseconds < TxTuneProbe.TuneMs)
                {
                    if (cancel.IsCancellationRequested)
                        return Refuse(TxTuneProbe.SkipReason.Cancelled, "cancelled mid-carrier");

                    meters = ReadMeters(rig);
                    lastComputedSwr = SafeComputedSwr(rig);
                    double reflectedPercent = ReflectedPercent(meters);

                    if (TxTuneProbe.ShouldStopEarly(lastComputedSwr, reflectedPercent,
                                                    consecutiveBad))
                    {
                        stoppedEarly = true;
                        Tracing.TraceLine("TxTuneProbeRunner: stopping early at "
                            + elapsed.ElapsedMilliseconds + " ms — computed SWR "
                            + Show(lastComputedSwr) + ", reflected "
                            + Show(reflectedPercent) + " percent", TraceLevel.Warning);
                        break;
                    }

                    consecutiveBad = LooksBad(lastComputedSwr, reflectedPercent)
                        ? consecutiveBad + 1 : 0;

                    cancel.WaitHandle.WaitOne(SampleEveryMs);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxTuneProbeRunner: failed mid-carrier — " + ex.Message,
                                  TraceLevel.Error);
                throw;
            }
            finally
            {
                // Every path out lands here, including the throw above and the
                // early returns inside the loop. Unkey, then confirm it took.
                Unkey(rig);

                // The witness is told AFTER the carrier is down, never before:
                // its whole purpose is to record that transmitting stopped, and
                // a witness notified first would be recording an intention.
                Witness(onUnkeyed, "onUnkeyed");
            }

            // A radio that never reported transmitting produced no measurement.
            // Say that rather than reading its silent meters as zeros.
            if (!everKeyed && !HasAnyReading(meters))
                return Refuse(TxTuneProbe.SkipReason.RadioNotReachable,
                              "the radio never reported transmitting and no meters read");

            TxTuneProbe.Verdict verdict = TxTuneProbe.Assess(meters, lastComputedSwr);

            var result = TxTuneProbe.Result.Ran(verdict, DateTime.UtcNow, meters, tunePower,
                                                lastComputedSwr, stoppedEarly,
                                                SafeFrequency(rig), SafeMode(rig),
                                                SafeAntenna(rig));

            Tracing.TraceLine("TxTuneProbeRunner: " + verdict
                + (stoppedEarly ? " (stopped early)" : "")
                + " — " + Describe(meters) + ", computed SWR " + Show(lastComputedSwr),
                TraceLevel.Info);

            return result;
        }

        /// <summary>
        /// Tell a witness something happened, and never let it break the run.
        /// </summary>
        /// <remarks>
        /// These callbacks exist so the host can account for key-down time and
        /// hold its own record of what the radio actually did — see
        /// <c>FixerTransmitGate</c>. One of them fires inside the unkey
        /// <c>finally</c>, so an exception escaping here would replace whatever
        /// actually went wrong with a failure to keep a note, and could do it
        /// while a carrier was on its way down. Swallow, trace, carry on.
        /// </remarks>
        private static void Witness(Action a, string which)
        {
            if (a == null) return;
            try { a(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxTuneProbeRunner: " + which
                    + " threw and was ignored — " + ex.Message, TraceLevel.Warning);
            }
        }

        // -------- keying --------

        private static bool WaitForKeyUp(FlexBase rig, CancellationToken cancel)
        {
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < KeyUpTimeoutMs)
            {
                if (cancel.IsCancellationRequested) return false;
                try { if (rig.Transmit || rig.TxTune) return true; }
                catch { return false; }
                cancel.WaitHandle.WaitOne(25);
            }
            return false;
        }

        /// <summary>
        /// Drop the carrier and confirm. Never throws: this runs in a finally,
        /// and an exception escaping here would replace whatever actually went
        /// wrong with a failure to tidy up.
        /// </summary>
        private static void Unkey(FlexBase rig)
        {
            try { rig.TxTune = false; }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxTuneProbeRunner: COULD NOT UNKEY — " + ex.Message,
                                  TraceLevel.Error);
                return;
            }

            // The unkey is queued like the key was, so confirm rather than
            // assume. If it will not drop, that is worth an Error line in the
            // log whether or not anyone is watching right now.
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < KeyUpTimeoutMs)
            {
                try { if (!rig.Transmit && !rig.TxTune) return; }
                catch { return; }
                Thread.Sleep(25);
            }

            Tracing.TraceLine("TxTuneProbeRunner: RADIO STILL REPORTS TRANSMITTING after "
                + KeyUpTimeoutMs + " ms — unkey may not have taken", TraceLevel.Error);
        }

        // -------- reading --------

        private static List<TxTuneProbe.Reading> ReadMeters(FlexBase rig)
        {
            var meters = new List<TxTuneProbe.Reading>(TxTuneProbe.Watched.Length);
            MeterInventory inv = SafeInventory(rig);

            foreach (string name in TxTuneProbe.Watched)
            {
                MeterReading r = null;
                try { r = inv?.Find(name); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("TxTuneProbeRunner: could not read " + name
                        + " — " + ex.Message, TraceLevel.Warning);
                }

                // Absent and never-reported are the same fact for a verdict:
                // there is no number here. They must not read as zero.
                bool reported = r != null && r.HasReading;
                meters.Add(reported
                    ? TxTuneProbe.Reading.Got(name, r.Value, r.Units.ToString())
                    : TxTuneProbe.Reading.Missing(name));
            }
            return meters;
        }

        private static double ReflectedPercent(IReadOnlyList<TxTuneProbe.Reading> meters)
        {
            double fwd = ValueOf(meters, "FWDPWR");
            double rev = ValueOf(meters, "REFPWR");
            if (double.IsNaN(fwd) || double.IsNaN(rev) || fwd <= 0.0) return double.NaN;
            return (rev / fwd) * 100.0;
        }

        private static bool LooksBad(double computedSwr, double reflectedPercent)
            => (!double.IsNaN(computedSwr) && computedSwr >= TxTuneProbe.SwrAbort)
            || (double.IsNaN(computedSwr) && !double.IsNaN(reflectedPercent)
                && reflectedPercent >= TxTuneProbe.ReflectedAbortPercent);

        private static double ValueOf(IReadOnlyList<TxTuneProbe.Reading> meters, string name)
        {
            for (int i = 0; i < meters.Count; i++)
                if (string.Equals(meters[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return meters[i].Reported ? meters[i].Value : double.NaN;
            return double.NaN;
        }

        private static bool HasAnyReading(IReadOnlyList<TxTuneProbe.Reading> meters)
        {
            for (int i = 0; i < meters.Count; i++) if (meters[i].Reported) return true;
            return false;
        }

        // -------- defensive accessors --------
        //
        // Conditions travel with the measurement so a reader can reproduce them
        // rather than take our word (#217), and because a reading with no
        // recorded antenna port is a number a support engineer cannot use
        // (#188). A condition we cannot name says so; it never guesses, and it
        // never takes the run down with it.

        private static TxTuneProbe.Result Refuse(TxTuneProbe.SkipReason why, string detail)
        {
            Tracing.TraceLine("TxTuneProbeRunner: not run — " + why + ": " + detail,
                              TraceLevel.Info);
            return TxTuneProbe.Result.NotRun(why);
        }

        private static MeterInventory SafeInventory(FlexBase rig)
        {
            try { return rig?.MeterInventory; } catch { return null; }
        }

        private static int SafeTunePower(FlexBase rig)
        {
            try { return rig?.TunePower ?? 0; } catch { return 0; }
        }

        private static double SafeComputedSwr(FlexBase rig)
        {
            try
            {
                float s = rig?.ComputedSWR ?? float.NaN;
                return float.IsNaN(s) ? double.NaN : s;
            }
            catch { return double.NaN; }
        }

        private static string SafeFrequency(FlexBase rig)
        {
            try
            {
                ulong hz = rig?.TXFrequency ?? 0UL;
                return hz == 0UL ? "not reported"
                    : (hz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture) + " MHz";
            }
            catch { return "could not be read"; }
        }

        private static string SafeMode(FlexBase rig)
        {
            try
            {
                string m = rig?.TXMode;
                return string.IsNullOrWhiteSpace(m) ? "not reported" : m;
            }
            catch { return "could not be read"; }
        }

        private static string SafeAntenna(FlexBase rig)
        {
            try
            {
                string a = rig?.TXAntennaName;
                return string.IsNullOrWhiteSpace(a) ? "not reported" : a;
            }
            catch { return "could not be read"; }
        }

        private static string Show(double v)
            => double.IsNaN(v) ? "not derivable"
                               : v.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Describe(IReadOnlyList<TxTuneProbe.Reading> meters)
        {
            var parts = new List<string>(meters.Count);
            for (int i = 0; i < meters.Count; i++) parts.Add(meters[i].ToString());
            return string.Join(", ", parts);
        }
    }
}

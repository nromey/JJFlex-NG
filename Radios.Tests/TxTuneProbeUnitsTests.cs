using System;
using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;
using static Radios.ChainChecks.TxTuneProbe;

namespace Radios.Tests
{
    /// <summary>
    /// The transmitter check reads forward and reflected power in watts, by
    /// the units the radio declared, and a tune power SET to 0 is "not
    /// judged" rather than a fault.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> The 8600 declares FWDPWR and REFPWR in dBm. The
    /// runner copied the number across unconverted and every judgement after
    /// it reads watts, so at tune power 0 the harness's forward 18.23 dBm
    /// (about 66 mW) and reflected 13.43 dBm were judged as 18.23 W and
    /// 13.43 W: "reflected 73.68 percent", an early stop at 310 ms, and "a
    /// large share of that power came back" into a load the radio's own SWR
    /// put at 1.01.
    /// </para>
    /// <para>
    /// The existing <see cref="TxTuneProbeTests"/> feed watts into
    /// <see cref="TxTuneProbe.Assess(IReadOnlyList{Reading}, double)"/>, which
    /// is correct; the bug lived in the private reader, which nothing reached.
    /// These go through <see cref="TxTuneProbeRunner.ToProbeReading"/>, the
    /// seam the reader now uses, so a mutation of the conversion goes red here.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class TxTuneProbeUnitsTests
    {
        // The harness's own numbers (2026-11-08, bench 8600, tune power 0).
        private const double HarnessForwardDbm = 18.23;
        private const double HarnessReflectedDbm = 13.43;

        private static double Dbm(double watts) => 10.0 * Math.Log10(watts * 1000.0);

        private static List<Reading> Read(params (string Name, double Value, MeterUnits Units)[] raw)
        {
            var list = new List<Reading>();
            foreach (var r in raw) list.Add(TxTuneProbeRunner.ToProbeReading(r.Name, r.Value, r.Units));
            return list;
        }

        private static double SharePercent(IReadOnlyList<Reading> m)
        {
            double fwd = double.NaN, rev = double.NaN;
            foreach (var r in m)
            {
                if (r.Name == "FWDPWR" && r.Reported) fwd = r.Value;
                if (r.Name == "REFPWR" && r.Reported) rev = r.Value;
            }
            return 100.0 * TransmitSafety.ReflectedFractionOf((float)fwd, (float)rev);
        }

        // ---- the conversion ----

        [Fact]
        public void Forward_and_reflected_in_dBm_are_judged_in_watts_and_keep_the_raw_number()
        {
            var fwd = TxTuneProbeRunner.ToProbeReading("FWDPWR", HarnessForwardDbm, MeterUnits.Dbm);
            var rev = TxTuneProbeRunner.ToProbeReading("REFPWR", HarnessReflectedDbm, MeterUnits.Dbm);

            Assert.True(fwd.Reported);
            Assert.InRange(fwd.Value, 0.066, 0.067);           // about 66 mW
            Assert.InRange(rev.Value, 0.021, 0.023);           // about 22 mW
            Assert.Equal("Watts", fwd.Units);
            Assert.Equal(HarnessForwardDbm, fwd.RawValue, 6);
            Assert.Equal("Dbm", fwd.RawUnits);
            // The evidence line carries both, because "0.##" prints 66 mW as 0.07.
            Assert.Contains("read as 18.23 Dbm", fwd.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void The_conversion_is_the_rigs_own_arithmetic()
        {
            var fwd = TxTuneProbeRunner.ToProbeReading("FWDPWR", 40.0, MeterUnits.Dbm);
            Assert.Equal(FlexBase.DBmToWatts(40f), fwd.Value, 6);   // 10 W
            Assert.Equal(10.0, fwd.Value, 3);
        }

        [Fact]
        public void Watts_pass_through_unchanged()
        {
            var fwd = TxTuneProbeRunner.ToProbeReading("FWDPWR", 5.0, MeterUnits.Watts);
            Assert.Equal(5.0, fwd.Value);
            Assert.Equal("Watts", fwd.Units);
            Assert.Equal("", fwd.RawUnits);
        }

        [Theory]
        [InlineData("SWR", 1.01, MeterUnits.SWR)]
        [InlineData("ALC", -150.0, MeterUnits.Dbfs)]
        [InlineData("PATEMP", 31.5, MeterUnits.DegreesC)]
        public void Every_other_meter_passes_through_in_its_own_units(string name, double value, MeterUnits units)
        {
            var r = TxTuneProbeRunner.ToProbeReading(name, value, units);
            Assert.True(r.Reported);
            Assert.Equal(value, r.Value);
            Assert.Equal(units.ToString(), r.Units);
        }

        [Theory]
        [InlineData(MeterUnits.None)]
        [InlineData(MeterUnits.Db)]
        [InlineData(MeterUnits.Volts)]
        public void A_power_in_units_that_are_not_dBm_or_watts_is_not_reported(MeterUnits units)
        {
            // FlexLib leaves a meter at None until its unit arrives and then
            // passes the raw number unscaled; a number in unknown units is not
            // a power, and must not read as one.
            Assert.False(TxTuneProbeRunner.ToProbeReading("FWDPWR", 18.23, units).Reported);
            Assert.False(TxTuneProbeRunner.ToProbeReading("REFPWR", 13.43, units).Reported);
        }

        // ---- the harness's case, end to end through the pure halves ----

        [Fact]
        public void At_tune_power_0_the_harness_reading_no_longer_stops_the_carrier_or_suspects_the_load()
        {
            var m = Read(("FWDPWR", HarnessForwardDbm, MeterUnits.Dbm),
                         ("REFPWR", HarnessReflectedDbm, MeterUnits.Dbm));

            // Under the forward-power floor: no share, so no early stop.
            Assert.True(double.IsNaN(SharePercent(m)));
            Assert.False(LooksBad(double.NaN, SharePercent(m)));

            // The setting was READ as 0: neither transmitter nor load is judged.
            Assert.Equal(Verdict.NotJudgedAtZeroTunePower, Assess(m, double.NaN, tunePowerSetToZero: true));
        }

        [Fact]
        public void Before_the_conversion_the_same_reading_stopped_the_carrier()
        {
            // Positive control: the dBm numbers read as watts are exactly the
            // harness's "reflected 73.68 percent", over the abort bar.
            var asWatts = new List<Reading>
            {
                Reading.Got("FWDPWR", HarnessForwardDbm, "Dbm"),
                Reading.Got("REFPWR", HarnessReflectedDbm, "Dbm"),
            };
            Assert.Equal(73.67, SharePercent(asWatts), 1);
            Assert.True(LooksBad(double.NaN, SharePercent(asWatts)));
            Assert.Equal(Verdict.MakesPowerLoadSuspect, Assess(asWatts, double.NaN));
        }

        [Fact]
        public void An_unreadable_tune_power_is_not_a_setting_of_0()
        {
            var m = Read(("FWDPWR", HarnessForwardDbm, MeterUnits.Dbm),
                         ("REFPWR", HarnessReflectedDbm, MeterUnits.Dbm));
            // Not routed: without a known setting of 0, 66 mW is no power, as
            // it always was in watts.
            Assert.Equal(Verdict.NoPower, Assess(m, double.NaN, tunePowerSetToZero: false));
        }

        [Fact]
        public void At_tune_power_0_a_carrier_over_the_floor_is_still_judged()
        {
            // No new threshold: the floor is the one every reader uses.
            var m = Read(("FWDPWR", Dbm(5.0), MeterUnits.Dbm), ("REFPWR", Dbm(0.05), MeterUnits.Dbm));
            Assert.Equal(Verdict.MakesPower, Assess(m, double.NaN, tunePowerSetToZero: true));
        }

        [Fact]
        public void A_few_watts_into_a_good_load_is_a_working_transmitter()
        {
            // 5 W forward, 0.1 W back: a 2 percent share, SWR about 1.33.
            var m = Read(("FWDPWR", Dbm(5.0), MeterUnits.Dbm), ("REFPWR", Dbm(0.1), MeterUnits.Dbm));
            Assert.Equal(2.0, SharePercent(m), 1);
            Assert.False(LooksBad(double.NaN, SharePercent(m)));
            Assert.Equal(Verdict.MakesPower, Assess(m, double.NaN, tunePowerSetToZero: false));

            // Positive control: read as watts, the same pair looked like an
            // open connector (20 dBm over 37 dBm, 54 percent) and stopped.
            var asWatts = new List<Reading>
            {
                Reading.Got("FWDPWR", Dbm(5.0), "Dbm"),
                Reading.Got("REFPWR", Dbm(0.1), "Dbm"),
            };
            Assert.True(LooksBad(double.NaN, SharePercent(asWatts)));
        }

        [Fact]
        public void A_few_watts_into_an_open_port_still_stops()
        {
            // 5 W out, 4 W back: 80 percent, over the abort rung either way.
            var m = Read(("FWDPWR", Dbm(5.0), MeterUnits.Dbm), ("REFPWR", Dbm(4.0), MeterUnits.Dbm));
            Assert.True(LooksBad(double.NaN, SharePercent(m)));
            Assert.Equal(Verdict.MakesPowerLoadSuspect, Assess(m, double.NaN, tunePowerSetToZero: false));
        }

        // ---- the precise claim: the conversion adds no new stop ----

        [Fact]
        public void From_one_watt_forward_every_pair_that_stops_in_watts_also_stopped_in_dBm()
        {
            // For forward of at least 1 W, a share over the abort rung in
            // watts was over it in dBm too, so the conversion adds no stop.
            // The stops it REMOVES are only (a) forward under 1 W, the floor's
            // documented design, and (b) SWR not derivable with a true share
            // under the abort rung (SWR 5, about 44.4 percent).
            int removedOverOneWatt = 0, added = 0;
            for (double fwdDbm = 30.0; fwdDbm <= 52.0; fwdDbm += 0.25)          // 1 W to about 160 W
            {
                for (double refDbm = -20.0; refDbm <= fwdDbm; refDbm += 0.25)
                {
                    double trueShare = SharePercent(Read(("FWDPWR", fwdDbm, MeterUnits.Dbm), ("REFPWR", refDbm, MeterUnits.Dbm)));
                    double dbmShare = 100.0 * TransmitSafety.ReflectedFractionOf((float)fwdDbm, (float)refDbm);
                    bool stopsNow = LooksBad(double.NaN, trueShare);
                    bool stoppedBefore = LooksBad(double.NaN, dbmShare);
                    if (stopsNow && !stoppedBefore) added++;
                    if (stoppedBefore && !stopsNow)
                    {
                        removedOverOneWatt++;
                        Assert.True(trueShare < ReflectedAbortPercent,
                            "a removed stop at a true share over the abort rung: fwd " + fwdDbm + " dBm, ref " + refDbm + " dBm");
                    }
                }
            }
            Assert.Equal(0, added);
            Assert.True(removedOverOneWatt > 0, "the sweep never reached the (b) case it describes");
        }

        [Fact]
        public void Under_one_watt_forward_no_share_is_computed_so_the_carrier_runs_its_time()
        {
            // (a): the forward floor's design. 0.5 W out, 0.4 W back.
            var m = Read(("FWDPWR", Dbm(0.5), MeterUnits.Dbm), ("REFPWR", Dbm(0.4), MeterUnits.Dbm));
            Assert.True(double.IsNaN(SharePercent(m)));
            Assert.False(LooksBad(double.NaN, SharePercent(m)));
            Assert.Equal(2000, TuneMs);   // the cap is unchanged
        }

        // ---- the runner's wiring, which no radio-free test can drive ----

        [Fact]
        public void The_runner_reads_every_meter_through_the_conversion_and_routes_only_a_read_zero()
        {
            string text = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "Radios", "ChainChecks", "TxTuneProbeRunner.cs"));

            int read = text.IndexOf("private static List<TxTuneProbe.Reading> ReadMeters(", StringComparison.Ordinal);
            Assert.True(read > 0);
            string readBody = text.Substring(read, text.IndexOf("internal static TxTuneProbe.Reading ToProbeReading(", read, StringComparison.Ordinal) - read);
            Assert.Contains("ToProbeReading(name, r.Value, r.Units)", readBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Reading.Got(", readBody, StringComparison.Ordinal);

            // An unreadable setting is null, never 0, and only a read 0 routes.
            int safe = text.IndexOf("private static int? SafeTunePower(FlexBase rig)", StringComparison.Ordinal);
            Assert.True(safe > 0, "the tune power read can no longer say it failed");
            Assert.Contains("catch { return null; }", text.Substring(safe, 250), StringComparison.Ordinal);
            Assert.Contains("tunePowerSetToZero: tunePowerRead == 0", text, StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // ---- what the operator is told ----

        [Fact]
        public void Not_judged_at_zero_says_so_and_is_not_the_critical_no_rf_finding()
        {
            var probe = Result.Ran(Verdict.NotJudgedAtZeroTunePower, DateTime.UtcNow,
                Read(("FWDPWR", HarnessForwardDbm, MeterUnits.Dbm), ("REFPWR", HarnessReflectedDbm, MeterUnits.Dbm)),
                0, double.NaN, false, "14.100", "USB", "ANT1");

            string answer = Explain(probe);
            Assert.False(string.IsNullOrWhiteSpace(answer));
            Assert.DoesNotContain("settings.transmit_check", answer, StringComparison.Ordinal);
            Assert.DoesNotContain("produced no RF", answer, StringComparison.Ordinal);
            Assert.DoesNotContain("transmitter is working", answer, StringComparison.Ordinal);
            Assert.False(probe.AudioTestingHasStanding);

            FixerOutcome o = TransmitStages.Transmitter(probe, "a dummy load");
            Assert.False(o.NothingMeasured);
            var finding = Assert.Single(o.Findings);
            Assert.Equal("tx-zero-tune-power", finding.Id);
            Assert.False(finding.Critical);
            Assert.DoesNotContain(o.Findings, f => f.Id == "tx-no-power");
        }
    }
}

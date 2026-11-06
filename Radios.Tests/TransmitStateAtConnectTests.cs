using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios.ChainChecks;
using Radios.Fixer;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A session in which the operator never changes mode still knows the
    /// transmit mode. FlexLib fires SliceAdded after the slice's status has
    /// been read, and a status of mode=USB is not even a change inside FlexLib,
    /// so on a slice that arrives as the transmit slice no change event ever
    /// reaches <c>FlexBase</c>. These drive the real <c>sliceAdded</c> with
    /// exactly that: a ready slice, and no property notification at all.
    /// </summary>
    public sealed class TransmitStateAtConnectTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly List<FlexBase> _rigs = new();

        public void Dispose()
        {
            foreach (FlexBase r in _rigs) { try { r.Dispose(); } catch { } }
        }

        private FlexBase Rig()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            rig.q.MainLoop = true;
            _rigs.Add(rig);
            return rig;
        }

        private static Slice ReadySlice(bool tx, bool active, string mode = "USB", double mhz = 21.195)
        {
            var radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { false }, null)!;
            var slice = (Slice)Activator.CreateInstance(typeof(Slice), Private, null, new object[] { radio }, null)!;
            Set(slice, "_isTransmitSlice", tx);
            Set(slice, "_active", active);
            Set(slice, "_demodMode", mode);
            Set(slice, "_freq", mhz);
            // A slice from a status is unlocked. FlexLib's own default is
            // locked, and a tune on a locked slice is refused at the press
            // since repair round 5 (#642), so these tests of what a tune
            // leaves behind start from an unlocked slice, as a radio's do.
            Set(slice, "_lock", false);
            return slice;
        }

        private static void Set(object o, string field, object value)
            => o.GetType().GetField(field, Private)!.SetValue(o, value);

        // The frequency caches became properties over one value carrying the
        // number and its slice (#642, repair round 2), so a name may be either.
        private static object? Field(object o, string field)
            // Property first: AllRadios has protected fields of the same names
            // that FlexBase hides and never writes.
            => typeof(FlexBase).GetProperty(field, Private | BindingFlags.DeclaredOnly)?.GetValue(o)
               ?? typeof(FlexBase).GetField(field, Private)!.GetValue(o);

        /// <summary>The real connect path, on a slice that raises nothing.</summary>
        private static void SliceArrives(FlexBase rig, Slice slice)
        {
            typeof(FlexBase).GetField("clientHandle", Private)!.SetValue(rig, slice.ClientHandle);
            var binding = new ObservationBinding(new object(), new ConnectionAttempt("test-serial"));
            typeof(FlexBase).GetMethod("sliceAdded", Private)!.Invoke(rig, new object[] { slice, binding });
        }

        [Fact]
        public void A_transmit_slice_that_arrives_ready_fills_the_transmit_caches()
        {
            FlexBase rig = Rig();

            SliceArrives(rig, ReadySlice(tx: true, active: true));

            // The fields, not the properties: the properties ask the slice when
            // the field is empty, so reading them cannot show the fill.
            Assert.Equal("USB", Field(rig, "_TXMode"));
            Assert.Equal(21_195_000UL, rig.TxCacheHz);
        }

        [Fact]
        public void The_named_readers_report_the_mode_with_no_mode_change_in_the_session()
        {
            FlexBase rig = Rig();

            SliceArrives(rig, ReadySlice(tx: true, active: true));

            Assert.Equal("USB", StationConditions.Mode(rig));
            Assert.StartsWith("21.195000 MHz", StationConditions.Frequency(rig), StringComparison.Ordinal);

            // The ladder's own decision: the bench run stopped here with "the
            // radio did not report which mode it is in".
            using TxToneLadderScope scope = TxToneLadderScope.Enter(rig);
            Assert.NotEqual(TxToneLadder.ModeAction.Refuse, scope.Plan.Action);
            Assert.Equal("USB", scope.Plan.CurrentMode);
        }

        [Fact]
        public void A_mode_other_than_FlexLibs_default_is_read_as_well()
        {
            FlexBase rig = Rig();

            SliceArrives(rig, ReadySlice(tx: true, active: true, mode: "CW"));

            Assert.Equal("CW", Field(rig, "_TXMode"));
            Assert.Equal("CW", Field(rig, "_RXMode"));
        }

        [Fact]
        public void With_the_cache_empty_TXMode_and_TXFrequency_ask_the_slice()
        {
            // A transmit slice this class has never been told about: it is in
            // the list and TXVFO points at it, and the caches hold nothing.
            FlexBase rig = Rig();
            rig.mySlices.Add(ReadySlice(tx: true, active: true, mode: "LSB", mhz: 7.2));
            rig._TXVFO = 0;

            Assert.Equal("", Field(rig, "_TXMode"));   // positive control
            Assert.Equal(0UL, rig.TxCacheHz);

            Assert.Equal("LSB", rig.TXMode);
            Assert.Equal(7_200_000UL, rig.TXFrequency);

            typeof(FlexBase).GetField("_Transmit", Private)!.SetValue(rig, true);
            Assert.Equal("LSB", rig.Mode);
        }

        [Fact]
        public void A_filled_cache_still_wins_over_the_slice()
        {
            // The cache stays the answer while it holds one: this is a fallback
            // for empty, not a change to what a known value means.
            FlexBase rig = Rig();
            Slice slice = ReadySlice(tx: true, active: true);
            SliceArrives(rig, slice);

            Set(slice, "_demodMode", "CW");

            Assert.Equal("USB", rig.TXMode);
        }

        private static void SliceReports(FlexBase rig, Slice slice, string property)
            => typeof(FlexBase).GetMethod("slicePropertyChangedHandler", Private)!
                .Invoke(rig, new object[] { slice, new PropertyChangedEventArgs(property) });

        private static void SliceGoes(FlexBase rig, Slice slice)
        {
            var binding = new ObservationBinding(new object(), new ConnectionAttempt("test-serial"));
            typeof(FlexBase).GetMethod("sliceRemoved", Private)!.Invoke(rig, new object[] { slice, binding });
        }

        private static void AssertNoTransmitFacts(FlexBase rig)
        {
            Assert.Equal("", rig.TXMode);
            Assert.Equal(0UL, rig.TXFrequency);
            Assert.Equal(StationConditions.NotReported, StationConditions.Mode(rig));
        }

        [Fact]
        public void Clearing_the_transmit_slice_clears_what_was_known_about_it()
        {
            FlexBase rig = Rig();
            SliceArrives(rig, ReadySlice(tx: true, active: true));
            Assert.Equal("USB", rig.TXMode); // positive control

            rig.ClearTransmitSlice();

            AssertNoTransmitFacts(rig);
        }

        [Fact]
        public void A_receive_slice_left_behind_is_not_reported_as_the_transmit_slice()
        {
            FlexBase rig = Rig();
            Slice tx = ReadySlice(tx: true, active: false);
            Slice rx = ReadySlice(tx: false, active: true, mode: "CW", mhz: 7.03);
            Set(rx, "_index", 1);
            SliceArrives(rig, tx);
            SliceArrives(rig, rx);

            SliceGoes(rig, tx);

            Assert.True(rig.ValidVFO(rig.TXVFO)); // TXVFO fell back to the receive slice
            AssertNoTransmitFacts(rig);
        }

        [Fact]
        public void When_the_transmit_slice_goes_a_slice_still_designated_answers()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(tx: true, active: false);
            Slice b = ReadySlice(tx: false, active: true, mode: "CW", mhz: 7.03);
            Slice c = ReadySlice(tx: false, active: false, mode: "LSB", mhz: 3.8);
            Set(b, "_index", 1);
            Set(c, "_index", 2);
            SliceArrives(rig, a);
            SliceArrives(rig, b);
            SliceArrives(rig, c);
            Set(c, "_isTransmitSlice", true); // the radio moved it, no report yet

            SliceGoes(rig, a);

            Assert.Equal("LSB", rig.TXMode); // not the slice TXVFO fell back to
            Assert.Equal(3_800_000UL, rig.TXFrequency);
        }

        [Fact]
        public void A_replacement_transmit_slice_takes_over_and_the_old_ones_report_does_not_wipe_it()
        {
            FlexBase rig = Rig();
            typeof(FlexBase).GetProperty("CanTransmit")!.SetValue(rig, true);
            Slice a = ReadySlice(tx: true, active: true);
            Slice b = ReadySlice(tx: false, active: false, mode: "LSB", mhz: 7.2);
            Set(b, "_index", 1);
            SliceArrives(rig, a);
            SliceArrives(rig, b);

            Set(b, "_isTransmitSlice", true);
            SliceReports(rig, b, "IsTransmitSlice");
            Set(a, "_isTransmitSlice", false);
            SliceReports(rig, a, "IsTransmitSlice");

            Assert.Equal("LSB", Field(rig, "_TXMode")); // the field: the getter would ask the slice
            Assert.Equal("LSB", rig.TXMode);
            Assert.Equal(7_200_000UL, rig.TXFrequency);
        }

        [Fact]
        public void The_radio_taking_the_designation_away_forgets_the_values()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(tx: true, active: true);
            SliceArrives(rig, a);

            Set(a, "_isTransmitSlice", false);
            SliceReports(rig, a, "IsTransmitSlice");

            AssertNoTransmitFacts(rig);
        }

        [Fact]
        public void An_object_at_TXVFO_that_is_not_designated_does_not_answer_for_the_transmit_slice()
        {
            FlexBase rig = Rig();
            rig.mySlices.Add(ReadySlice(tx: false, active: true, mode: "CW", mhz: 7.03));
            rig._TXVFO = 0;

            AssertNoTransmitFacts(rig);
        }

        [Fact]
        public void XIT_goes_with_the_slice_it_came_from()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(tx: true, active: true);
            Set(a, "_xitOn", true);
            Set(a, "_xitFreq", -80);
            SliceArrives(rig, a);
            Assert.True(rig.XIT.Active); // positive control

            rig.ClearTransmitSlice();

            Assert.False(rig.XIT.Active);
            Assert.Equal(0, rig.XIT.Value);
        }

        [Fact]
        public void A_written_frequency_stands_optimistically_and_goes_with_its_slice()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(tx: true, active: true);
            SliceArrives(rig, a);

            rig.TXFrequency = 21_300_000UL;
            Assert.Equal(21_300_000UL, rig.TXFrequency); // the deliberate optimism, unchanged

            SliceGoes(rig, a);
            AssertNoTransmitFacts(rig);
        }

        [Fact]
        public void A_frequency_written_after_removal_does_not_promote_the_receive_slice()
        {
            FlexBase rig = Rig();
            Slice tx = ReadySlice(tx: true, active: false);
            Slice rx = ReadySlice(tx: false, active: true, mode: "CW", mhz: 7.03);
            Set(rx, "_index", 1);
            SliceArrives(rig, tx);
            SliceArrives(rig, rx);
            SliceGoes(rig, tx);
            AssertNoTransmitFacts(rig); // positive control: the removal was clean

            rig.TXFrequency = 7_040_000UL;

            AssertNoTransmitFacts(rig);
            Assert.Equal("", Field(rig, "_TXMode"));
            Assert.Equal(0UL, rig.TxCacheHz);
            Assert.Equal(1, rig.q.Count); // the write itself still goes, as it always did
        }

        [Fact]
        public void A_frequency_written_after_the_radio_took_the_designation_away_is_not_a_transmit_fact()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(tx: true, active: true);
            SliceArrives(rig, a);
            Set(a, "_isTransmitSlice", false);
            SliceReports(rig, a, "IsTransmitSlice");

            rig.TXFrequency = 21_300_000UL;

            AssertNoTransmitFacts(rig);
        }

        [Fact]
        public void A_populated_cache_does_not_outlive_its_designation_with_no_report()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(tx: true, active: true);
            SliceArrives(rig, a);

            Set(a, "_isTransmitSlice", false); // no notification at all

            Assert.Equal("USB", Field(rig, "_TXMode")); // positive control: still populated
            AssertNoTransmitFacts(rig);
        }

        [Fact]
        public void A_fresh_connection_starts_with_no_transmit_facts()
        {
            string connect = File.ReadAllText(Path.Combine(Repo(), "Radios", "FlexBase.cs"));
            int fresh = connect.IndexOf("_radioReportedAutosave = false;", StringComparison.Ordinal);
            Assert.True(fresh > 0);
            // Through the one commit since repair round 11 (CommitSliceFacts).
            Assert.Contains("ForgetTransmitCaches(null)", connect.Substring(fresh, 400), StringComparison.Ordinal);
        }

        private static string Repo()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            return d!.FullName;
        }

        [Fact]
        public void A_slice_that_is_neither_active_nor_transmit_fills_nothing()
        {
            FlexBase rig = Rig();

            SliceArrives(rig, ReadySlice(tx: false, active: false, mode: "CW"));

            Assert.Equal("", Field(rig, "_TXMode"));
            Assert.Equal("", Field(rig, "_RXMode"));
            Assert.Equal(0UL, rig.TxCacheHz);
        }

        [Fact]
        public void The_active_slice_as_found_fills_the_receive_side_and_its_RIT()
        {
            FlexBase rig = Rig();
            Slice slice = ReadySlice(tx: false, active: true, mode: "LSB", mhz: 7.2);
            Set(slice, "_ritOn", true);
            Set(slice, "_ritFreq", 120);

            SliceArrives(rig, slice);

            Assert.Equal("LSB", rig.RXMode);
            Assert.Equal(7_200_000UL, rig.RxCacheHz);
            Assert.True(rig.RIT.Active);
            Assert.Equal(120, rig.RIT.Value);
            Assert.False(rig.XIT.Active); // the transmit half stays out of it
        }

        [Fact]
        public void The_transmit_slice_as_found_fills_its_XIT()
        {
            FlexBase rig = Rig();
            Slice slice = ReadySlice(tx: true, active: false);
            Set(slice, "_xitOn", true);
            Set(slice, "_xitFreq", -80);

            SliceArrives(rig, slice);

            Assert.True(rig.XIT.Active);
            Assert.Equal(-80, rig.XIT.Value);
            Assert.False(rig.RIT.Active);
        }

        // ---- a stage that measured nothing is not a pass ----

        private static FixerStageSet SetWith(TransmitStageSet.Hosts hosts) => TransmitStageSet.Build(hosts);

        private static string StageHeading(FixerRun run, string stageId)
        {
            FixerPage.LandingAfterResult(run, stageId); // a real result exists
            return FixerPage.SpokenVerdict(run, stageId);
        }

        [Fact]
        public void A_stage_three_that_refused_to_arm_reads_could_not_run_and_says_why()
        {
            const string refusal = "The test tone could not be armed with VOX active. No transmission was made.";
            var run = new FixerRun(SetWith(new TransmitStageSet.Hosts
            {
                RunInjectedTransmit = () => new InjectedTransmitFacts { Detail = refusal },
            }));

            FixerStageResult r = run.RunStage(TransmitStageSet.InjectedTransmit);

            Assert.Equal(FixerStageStatus.CouldNotRun, r.Status);
            string verdict = StageHeading(run, TransmitStageSet.InjectedTransmit);
            Assert.Contains("could not run", verdict, StringComparison.Ordinal);
            Assert.DoesNotContain("passed", verdict, StringComparison.Ordinal);
            Assert.Contains(refusal, verdict, StringComparison.Ordinal);
            Assert.DoesNotContain("Fewer than two probes", verdict, StringComparison.Ordinal);
        }

        [Fact]
        public void A_stage_three_that_keyed_and_finished_one_probe_is_not_a_pass_either()
        {
            var run = new FixerRun(SetWith(new TransmitStageSet.Hosts
            {
                RunInjectedTransmit = () => new InjectedTransmitFacts
                {
                    Probes = new[]
                    {
                        new TxProbeSet.ProbeResult(TxProbeSet.Probe.SingleTone,
                                                   TxProbeSet.Outcome.ReachedRadio, ""),
                    },
                },
            }));

            FixerStageResult r = run.RunStage(TransmitStageSet.InjectedTransmit);

            // Ran, because the gate has charged it and only Run again reaches it.
            Assert.Equal(FixerStageStatus.Ran, r.Status);
            Assert.Contains(r.Findings, f => f.Id == "tx-too-few-probes");
        }

        [Fact]
        public void A_stage_three_with_a_real_verdict_keeps_it()
        {
            var run = new FixerRun(SetWith(new TransmitStageSet.Hosts
            {
                RunInjectedTransmit = () => new InjectedTransmitFacts
                {
                    Probes = new[]
                    {
                        new TxProbeSet.ProbeResult(TxProbeSet.Probe.SingleTone, TxProbeSet.Outcome.ReachedRadio, ""),
                        new TxProbeSet.ProbeResult(TxProbeSet.Probe.ToneLadder, TxProbeSet.Outcome.ReachedRadio, ""),
                    },
                },
            }));

            FixerStageResult r = run.RunStage(TransmitStageSet.InjectedTransmit);

            Assert.Equal(FixerStageStatus.Ran, r.Status);
            Assert.Empty(r.Findings);
            Assert.Contains("passed", StageHeading(run, TransmitStageSet.InjectedTransmit), StringComparison.Ordinal);
        }

        [Fact]
        public void A_stage_two_refused_before_the_key_reads_could_not_run()
        {
            var run = new FixerRun(SetWith(new TransmitStageSet.Hosts
            {
                ProbeTransmitter = () => TxTuneProbe.Result.NotRun(
                    TxTuneProbe.SkipReason.RefusedByHost, "Nothing was transmitted."),
            }));

            FixerStageResult r = run.RunStage(TransmitStageSet.TransmitterCheck);

            Assert.Equal(FixerStageStatus.CouldNotRun, r.Status);
        }

        [Fact]
        public void A_stage_two_stopped_is_not_a_pass_and_can_still_be_run_again()
        {
            var run = new FixerRun(SetWith(new TransmitStageSet.Hosts
            {
                ProbeTransmitter = () => TxTuneProbe.Result.NotRun(TxTuneProbe.SkipReason.Cancelled),
            }));

            FixerStageResult r = run.RunStage(TransmitStageSet.TransmitterCheck);

            Assert.Equal(FixerStageStatus.Ran, r.Status);
            Assert.Contains(r.Findings, f => f.Id == "tx-stopped");
        }

        [Fact]
        public void A_stage_four_refused_before_the_key_reads_could_not_run_with_its_reason()
        {
            var run = new FixerRun(SetWith(new TransmitStageSet.Hosts
            {
                RunSpokenTransmit = () => new SpokenTransmitFacts { Detail = "Nothing was transmitted. The path is shut." },
            }));

            FixerStageResult r = run.RunStage(TransmitStageSet.SpokenTransmit);

            Assert.Equal(FixerStageStatus.CouldNotRun, r.Status);
            Assert.Contains("The path is shut.", r.Answer, StringComparison.Ordinal);
        }

        [Fact]
        public void A_stage_four_that_keyed_and_measured_nothing_is_not_a_pass_either()
        {
            var run = new FixerRun(SetWith(new TransmitStageSet.Hosts
            {
                RunSpokenTransmit = () => new SpokenTransmitFacts { Keyed = true },
            }));

            FixerStageResult r = run.RunStage(TransmitStageSet.SpokenTransmit);

            Assert.Equal(FixerStageStatus.Ran, r.Status);
            Assert.Contains(r.Findings, f => f.Id == "tx-spoken-not-measured");
        }

        [Fact]
        public void A_stage_one_that_measured_nothing_reads_could_not_run_with_its_reason()
        {
            var run = new FixerRun(SetWith(new TransmitStageSet.Hosts
            {
                MeasureMicrophone = () => new MicCheckFacts { Detail = "Your saved microphone is not connected." },
            }));

            FixerStageResult r = run.RunStage(TransmitStageSet.MicrophoneCheck);

            Assert.Equal(FixerStageStatus.CouldNotRun, r.Status);
            Assert.Contains("Your saved microphone is not connected.", r.Answer, StringComparison.Ordinal);
        }

        // ---- the sentence does not promise a count the stage will skip ----

        public static IEnumerable<object[]> CountingStages()
        {
            yield return new object[] { TransmitStageSet.MicrophoneCheck };
            yield return new object[] { TransmitStageSet.TransmitterCheck };
            yield return new object[] { TransmitStageSet.InjectedTransmit };
            yield return new object[] { TransmitStageSet.SpokenTransmit };
        }

        [Theory]
        [MemberData(nameof(CountingStages))]
        public void With_the_countdown_off_no_stage_describes_one(string stageId)
        {
            string off = TransmitStageSet.Build(new TransmitStageSet.Hosts { CountdownOn = () => false })
                .Find(stageId).DescribeRunAction();

            Assert.DoesNotContain("three tones", off, StringComparison.Ordinal);
            Assert.DoesNotContain("counts", off, StringComparison.Ordinal);
            Assert.StartsWith("Running this ", off, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(CountingStages))]
        public void With_the_countdown_on_or_unread_every_stage_says_so(string stageId)
        {
            Assert.Contains("three tones", TransmitStageSet.Build(new TransmitStageSet.Hosts { CountdownOn = () => true })
                .Find(stageId).DescribeRunAction(), StringComparison.Ordinal);
            Assert.Contains("three tones", TransmitStageSet.Build(new TransmitStageSet.Hosts())
                .Find(stageId).DescribeRunAction(), StringComparison.Ordinal);
            Assert.Contains("three tones", TransmitStageSet.Build(new TransmitStageSet.Hosts { CountdownOn = () => throw new InvalidOperationException() })
                .Find(stageId).DescribeRunAction(), StringComparison.Ordinal);
        }
    }
}

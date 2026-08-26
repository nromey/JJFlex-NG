using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Fixer;
using Radios.Fixer.Evidence;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The settings fingerprint: a declared dependency list per stage — never
    /// snapshot-everything — and invalidation as arithmetic, with every
    /// difference naming itself.
    /// </summary>
    public class FixerFingerprintTests
    {
        private static FixerSettingProbeSet TwoKeySet(FakeSetting tap, FakeSetting heat)
            => new FixerSettingProbeSet(
                new[] { tap.Probe("tap", "Tap"), heat.Probe("heat", "Heat") },
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["fill"] = new[] { "tap" },
                    ["boil"] = new[] { "heat" },
                });

        // -------- the probe set --------

        [Fact]
        public void A_declared_key_nobody_can_read_fails_at_construction()
        {
            var tap = new FakeSetting("open");
            var ex = Assert.Throws<ArgumentException>(() => new FixerSettingProbeSet(
                new[] { tap.Probe("tap", "Tap") },
                new Dictionary<string, IReadOnlyList<string>> { ["fill"] = new[] { "ghost" } }));
            Assert.Contains("ghost", ex.Message);
        }

        [Fact]
        public void Capture_reads_the_declared_settings_and_only_those()
        {
            var tap = new FakeSetting("open");
            var heat = new FakeSetting("high");
            FixerSettingProbeSet probes = TwoKeySet(tap, heat);

            var captured = probes.CaptureFor("fill");

            RecordedSetting s = Assert.Single(captured);
            Assert.Equal("tap", s.Key);
            Assert.Equal("Tap", s.Name);
            Assert.Equal("open", s.Value);
        }

        [Fact]
        public void An_undeclared_stage_captures_nothing()
        {
            FixerSettingProbeSet probes = TwoKeySet(new FakeSetting("a"), new FakeSetting("b"));
            Assert.Empty(probes.CaptureFor("no-such-stage"));
            Assert.Empty(probes.DeclaredFor("no-such-stage"));
        }

        [Fact]
        public void A_throwing_probe_reads_as_unreadable_never_as_a_crash()
        {
            var probe = new FixerSettingProbe("x", "X",
                () => throw new InvalidOperationException("no radio"));
            Assert.Equal("", probe.Read());
        }

        // -------- staleness: the load-bearing design rule --------

        [Fact]
        public void An_unrelated_change_does_not_stale_a_stage_that_never_declared_it()
        {
            var tap = new FakeSetting("open");
            var heat = new FakeSetting("high");
            FixerSettingProbeSet probes = TwoKeySet(tap, heat);

            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1, probes.CaptureFor("fill").ToArray()));
            record.Results.Add(EvidenceRecords.Ran("boil", 2, probes.CaptureFor("boil").ToArray()));

            heat.Value = "low";   // boil's dependency changes; fill's does not

            FixerStalenessReport report = FixerStalenessCheck.Check(record, probes);

            Assert.Equal(FixerStageFreshness.Fresh,
                report.Stages.Single(s => s.StageId == "fill").State);
            FixerStageStaleness boil = report.Stages.Single(s => s.StageId == "boil");
            Assert.Equal(FixerStageFreshness.Stale, boil.State);
            Assert.Equal("Heat changed from high to low.", Assert.Single(boil.Changes));
        }

        [Fact]
        public void The_verdict_names_the_stage_and_the_summary_says_where_to_resume()
        {
            var tap = new FakeSetting("open");
            var heat = new FakeSetting("high");
            FixerSettingProbeSet probes = TwoKeySet(tap, heat);

            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("boil", 1, probes.CaptureFor("boil").ToArray()));

            heat.Value = "low";
            FixerStalenessReport report = FixerStalenessCheck.Check(record, probes);

            FixerStageStaleness boil = report.Stages.Single(s => s.StageId == "boil");
            Assert.Contains("Stage 1 (Boil)", boil.Verdict);
            Assert.Contains("no longer describes this radio", boil.Verdict);

            Assert.Contains("Heat changed from high to low.", report.Summary());
            Assert.Contains("Run again from stage 1 (Boil).", report.Summary());
            Assert.Same(boil, report.EarliestStale);
        }

        [Fact]
        public void One_change_shared_by_two_stages_is_named_once_and_both_stages_are_named()
        {
            var shared = new FakeSetting("10 watts");
            var probes = new FixerSettingProbeSet(
                new[] { shared.Probe("power", "Tune power") },
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["fill"] = new[] { "power" },
                    ["boil"] = new[] { "power" },
                });

            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1, probes.CaptureFor("fill").ToArray()));
            record.Results.Add(EvidenceRecords.Ran("boil", 2, probes.CaptureFor("boil").ToArray()));

            shared.Value = "100 watts";
            string summary = FixerStalenessCheck.Check(record, probes).Summary();

            // The change appears exactly once, though it stales two stages.
            Assert.Equal(summary.IndexOf("Tune power changed from 10 watts to 100 watts.",
                                         StringComparison.Ordinal),
                         summary.LastIndexOf("Tune power changed from 10 watts to 100 watts.",
                                             StringComparison.Ordinal));
            Assert.Contains("Stages 0 (Fill), 1 (Boil)", summary);
            Assert.Contains("Run again from stage 0 (Fill).", summary);
        }

        [Fact]
        public void A_value_unreadable_now_is_cannot_verify_never_stale()
        {
            var heat = new FakeSetting("high");
            FixerSettingProbeSet probes = TwoKeySet(new FakeSetting("open"), heat);

            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("boil", 1, probes.CaptureFor("boil").ToArray()));

            heat.Value = "";   // radio gone: the probe reads nothing

            FixerStageStaleness boil = FixerStalenessCheck.Check(record, probes)
                .Stages.Single(s => s.StageId == "boil");

            Assert.Equal(FixerStageFreshness.CannotVerify, boil.State);
            Assert.Contains("Heat cannot be checked right now; it was high when this stage ran.",
                            boil.CannotCompare);
            Assert.Empty(boil.Changes);
        }

        [Fact]
        public void A_value_unreadable_at_run_time_says_so()
        {
            FixerSettingProbeSet probes = TwoKeySet(new FakeSetting("open"), new FakeSetting("high"));

            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("boil", 1,
                EvidenceRecords.Setting("heat", "Heat", "")));   // stored as unreadable

            FixerStageStaleness boil = FixerStalenessCheck.Check(record, probes)
                .Stages.Single(s => s.StageId == "boil");

            Assert.Equal(FixerStageFreshness.CannotVerify, boil.State);
            Assert.Contains("Heat could not be read when this stage ran, so it cannot be compared.",
                            boil.CannotCompare);
        }

        [Fact]
        public void Skips_and_never_attempted_stages_have_no_measurement_to_stale()
        {
            FixerSettingProbeSet probes = TwoKeySet(new FakeSetting("open"), new FakeSetting("high"));

            FixerRunRecord record = EvidenceRecords.TwoStages();
            var skipped = EvidenceRecords.Ran("fill", 1);
            skipped.Status = "Skipped";
            skipped.SkipChoiceId = "later";
            record.Results.Add(skipped);
            // boil never attempted

            FixerStalenessReport report = FixerStalenessCheck.Check(record, probes);
            Assert.All(report.Stages,
                s => Assert.Equal(FixerStageFreshness.NoMeasurement, s.State));
            Assert.Equal("No stage has a measurement to check.", report.Summary());
        }

        [Fact]
        public void An_old_record_with_no_stored_settings_cannot_be_verified()
        {
            FixerSettingProbeSet probes = TwoKeySet(new FakeSetting("open"), new FakeSetting("high"));

            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("boil", 1));   // no settings stored

            FixerStageStaleness boil = FixerStalenessCheck.Check(record, probes)
                .Stages.Single(s => s.StageId == "boil");
            Assert.Equal(FixerStageFreshness.CannotVerify, boil.State);
        }

        [Fact]
        public void When_nothing_changed_the_summary_says_exactly_that()
        {
            var tap = new FakeSetting("open");
            var heat = new FakeSetting("high");
            FixerSettingProbeSet probes = TwoKeySet(tap, heat);

            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1, probes.CaptureFor("fill").ToArray()));
            record.Results.Add(EvidenceRecords.Ran("boil", 2, probes.CaptureFor("boil").ToArray()));

            FixerStalenessReport report = FixerStalenessCheck.Check(record, probes);
            Assert.False(report.AnythingStale);
            Assert.Equal("Nothing these stages depended on has changed since they ran.",
                         report.Summary());
        }

        // -------- the transmit set's declarations --------

        [Fact]
        public void The_transmit_declarations_name_real_stage_ids_and_leave_none_undeclared()
        {
            // No readers wired at all: every probe must read as unreadable,
            // not throw — the fingerprint machinery has to survive a missing
            // radio and a host that wired nothing.
            FixerSettingProbeSet probes = TransmitSettingProbes.Build(null);

            foreach (string stageId in new[]
            {
                TransmitStageSet.AudioSetup,
                TransmitStageSet.MicrophoneCheck,
                TransmitStageSet.TransmitterCheck,
                TransmitStageSet.InjectedTransmit,
                TransmitStageSet.SpokenTransmit,
            })
            {
                Assert.True(probes.DeclaredFor(stageId).Count > 0,
                    "stage '" + stageId + "' declares nothing — either declare its "
                    + "dependencies or remove it from this list knowingly");

                foreach (RecordedSetting s in probes.CaptureFor(stageId))
                    Assert.Equal("", s.Value);   // nothing to read, honestly empty
            }
        }

        [Fact]
        public void Stage_two_depends_on_what_the_register_says_it_depends_on()
        {
            FixerSettingProbeSet probes = TransmitSettingProbes.Build(null);
            var keys = probes.DeclaredFor(TransmitStageSet.TransmitterCheck);

            Assert.Contains(TransmitSettingProbes.TunePower, keys);
            Assert.Contains(TransmitSettingProbes.TxAntenna, keys);
            Assert.Contains(TransmitSettingProbes.Frequency, keys);
            Assert.Contains(TransmitSettingProbes.Mode, keys);
        }

        [Fact]
        public void The_audio_side_reads_configured_values_first()
        {
            var facts = new AudioSetupFacts
            {
                ConfiguredHostApi = "WASAPI",
                ConfiguredInputDevice = "EVO8",
                OpenHostApi = "MME",
                OpenInputDevice = "Something else",
            };
            FixerSettingProbeSet probes = TransmitSettingProbes.Build(
                new TransmitSettingReaders { AudioSetup = () => facts });

            var captured = probes.CaptureFor(TransmitStageSet.MicrophoneCheck);
            Assert.Equal("EVO8",
                captured.Single(s => s.Key == TransmitSettingProbes.InputDevice).Value);
            Assert.Equal("WASAPI",
                captured.Single(s => s.Key == TransmitSettingProbes.HostApi).Value);
        }

        [Fact]
        public void The_radio_side_readers_format_here_so_no_surface_can_phrase_them_differently()
        {
            var readers = new TransmitSettingReaders
            {
                TunePowerWatts = () => 10,
                RfPowerWatts = () => 100,
                PcAudioOn = () => true,
                MicProfileEmpty = () => false,
                TxAntennaName = () => "ANT1",
                TxFrequencyHz = () => 14_203_000,
                ModeName = () => "USB",
                MicGain = () => 40,
                // A reader that throws must read as unreadable, not as a crash.
                AudioSetup = () => throw new InvalidOperationException("no audio stack"),
            };
            FixerSettingProbeSet probes = TransmitSettingProbes.Build(readers);

            var byKey = new Dictionary<string, string>();
            foreach (RecordedSetting s in probes.CaptureFor(TransmitStageSet.SpokenTransmit))
                byKey[s.Key] = s.Value;
            foreach (RecordedSetting s in probes.CaptureFor(TransmitStageSet.TransmitterCheck))
                byKey[s.Key] = s.Value;
            foreach (RecordedSetting s in probes.CaptureFor(TransmitStageSet.AudioSetup))
                byKey[s.Key] = s.Value;

            Assert.Equal("10 watts", byKey[TransmitSettingProbes.TunePower]);
            Assert.Equal("100 watts", byKey[TransmitSettingProbes.RfPower]);
            Assert.Equal("on", byKey[TransmitSettingProbes.PcAudio]);
            Assert.Equal("has settings", byKey[TransmitSettingProbes.MicProfile]);
            Assert.Equal("ANT1", byKey[TransmitSettingProbes.TxAntenna]);
            Assert.Equal("14.203 MHz", byKey[TransmitSettingProbes.Frequency]);
            Assert.Equal("USB", byKey[TransmitSettingProbes.Mode]);
            Assert.Equal("40", byKey[TransmitSettingProbes.MicGain]);
            Assert.Equal("", byKey[TransmitSettingProbes.InputDevice]);   // the throwing reader
        }

        [Fact]
        public void Frequency_formats_as_MHz_and_zero_reads_as_unreadable()
        {
            Assert.Equal("14.203 MHz", TransmitSettingProbes.FormatMHz(14_203_000));
            Assert.Equal("14.20345 MHz", TransmitSettingProbes.FormatMHz(14_203_450));
            Assert.Equal("7.074 MHz", TransmitSettingProbes.FormatMHz(7_074_000));
            Assert.Equal("", TransmitSettingProbes.FormatMHz(0));
        }
    }
}

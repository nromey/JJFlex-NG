using System;
using System.Collections.Generic;
using System.Linq;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The transmit domain as data: five stages in the pincer order, the two
    /// microphone skip reasons kept distinct, the spoken stage read against
    /// the microphone baseline, and a host that supplies nothing getting
    /// honest refusals rather than improvisation.
    /// </summary>
    public class TransmitStageSetTests
    {
        // ---- the shape of the set ----

        [Fact]
        public void Five_stages_numbered_in_the_pincer_order()
        {
            FixerStageSet set = TransmitStageSet.Build(new TransmitStageSet.Hosts());

            Assert.Equal(5, set.Stages.Count);
            // Numbers are contiguous from zero in listed order — a gap in a
            // numbered set reads as an omission.
            Assert.Equal(Enumerable.Range(0, 5), set.Stages.Select(s => s.Number));
        }

        [Fact]
        public void Only_the_stages_that_key_the_radio_say_they_transmit()
        {
            // The brief says "stages 0-2 do not transmit", and about stage 2
            // it is simply wrong: the transmitter check IS TxTuneProbeRunner,
            // which keys a carrier — its own documentation opens with "This
            // transmits." A stage that keys the radio while marked
            // non-transmitting would be refused by the gate AND would surprise
            // a blind operator with their own RF, so the honest split is 0-1
            // silent, 2-4 transmitting.
            FixerStageSet set = TransmitStageSet.Build(new TransmitStageSet.Hosts());

            foreach (FixerStage s in set.Stages)
                Assert.Equal(s.Number >= 2, s.Transmits);
        }

        [Fact]
        public void Every_stage_asks_its_question_and_can_explain_itself()
        {
            foreach (FixerStage s in TransmitStageSet.Build(new TransmitStageSet.Hosts()).Stages)
            {
                Assert.NotEmpty(s.Question);
                Assert.EndsWith("?", s.Question); // asked like a person
                Assert.NotEmpty(s.Explanation);
                Assert.NotEmpty(s.HelpTopic);
                Assert.NotEmpty(s.SkipChoices);
            }
        }

        // ---- the two microphone skip reasons ----

        [Fact]
        public void Both_microphone_stages_offer_both_distinct_skip_reasons()
        {
            FixerStageSet set = TransmitStageSet.Build(new TransmitStageSet.Hosts());

            foreach (string stageId in new[] { TransmitStageSet.MicrophoneCheck,
                                               TransmitStageSet.SpokenTransmit })
            {
                FixerStage stage = set.Find(stageId);
                FixerSkipChoice remote = stage.FindSkip(TransmitStageSet.SkipRemoteNoDirectSpeech);
                FixerSkipChoice noMic = stage.FindSkip(TransmitStageSet.SkipNoMicrophone);

                Assert.NotNull(remote);
                Assert.NotNull(noMic);

                // One narrows, one leaves open — and their words differ,
                // because a skipped step must never read as a passed one and
                // these two must never read as each other.
                Assert.Equal(FixerSkipEffect.NarrowsFaultDomain, remote.Effect);
                Assert.Equal(FixerSkipEffect.LeavesQuestionOpen, noMic.Effect);
                Assert.NotEqual(remote.EffectText, noMic.EffectText);
            }
        }

        // ---- the transmit boundary, from this side ----

        [Fact]
        public void With_no_host_delegates_every_stage_records_could_not_run()
        {
            var run = new FixerRun(TransmitStageSet.Build(new TransmitStageSet.Hosts()));

            foreach (FixerStage s in run.Set.Stages)
            {
                FixerStageResult r = run.RunStage(s.Id);
                Assert.Equal(FixerStageStatus.CouldNotRun, r.Status);
                if (s.Transmits)
                    Assert.Contains("nothing was transmitted", r.Answer,
                                    StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void The_transmitter_probe_reaches_the_radio_only_through_the_injected_delegate()
        {
            int called = 0;
            var hosts = new TransmitStageSet.Hosts
            {
                ProbeTransmitter = () => { called++; return TxTuneProbe.Result.NotRun(
                    TxTuneProbe.SkipReason.LoadNotDeclared); },
            };
            var run = new FixerRun(TransmitStageSet.Build(hosts));
            FixerStageResult r = run.RunStage(TransmitStageSet.TransmitterCheck);

            Assert.Equal(1, called);
            Assert.Equal(FixerStageStatus.Ran, r.Status);
            // The probe's own refusal wording flows through untranslated.
            Assert.Contains("not known what is connected", r.Answer);
        }

        [Fact]
        public void The_load_declaration_asks_all_four_answers_and_not_sure_fails_closed()
        {
            // #244: four answers, because the earlier binary threw away two
            // of the three facts the operator was stating. "Nothing, or I am
            // not sure" EXISTS as a choice now — the operator who picks it
            // has told us something and gets an explicit refusal — and it
            // maps to the kind that keeps the gate shut. So does any choice
            // id the mapper has never heard of: unknown fails closed.
            FixerStageSet set = TransmitStageSet.Build(new TransmitStageSet.Hosts());
            FixerRunDeclaration decl = Assert.Single(set.RunDeclarations);

            Assert.Equal(TransmitStageSet.LoadDeclaration, decl.Id);
            Assert.EndsWith("?", decl.Question);
            Assert.Equal(4, decl.Choices.Count);

            Assert.Equal(FixerLoadKind.DummyLoad,
                TransmitStageSet.LoadKindFromChoice(TransmitStageSet.LoadDummy));
            Assert.Equal(FixerLoadKind.Antenna,
                TransmitStageSet.LoadKindFromChoice(TransmitStageSet.LoadAntenna));
            Assert.Equal(FixerLoadKind.Amplifier,
                TransmitStageSet.LoadKindFromChoice(TransmitStageSet.LoadAmplifier));
            Assert.Equal(FixerLoadKind.NothingOrUnsure,
                TransmitStageSet.LoadKindFromChoice(TransmitStageSet.LoadNothingUnsure));
            Assert.Equal(FixerLoadKind.NothingOrUnsure,
                TransmitStageSet.LoadKindFromChoice("never-heard-of-it"));
            Assert.Equal(FixerLoadKind.NothingOrUnsure,
                TransmitStageSet.LoadKindFromChoice(null));
        }

        [Fact]
        public void The_declaration_question_names_the_port_and_the_distance()
        {
            // #244: the radio already knows its TX antenna, so the question
            // states it instead of asking the operator to know it. #247: for
            // a remote radio the question says out loud that it is about a
            // station the operator is not at.
            var local = TransmitStageSet.Build(new TransmitStageSet.Hosts
            {
                ReadStation = () => new TransmitStageSet.StationNow { AntennaPort = "ANT1" },
            }).RunDeclarations[0].QuestionNow();
            Assert.Contains("The radio will transmit on ANT1", local);
            Assert.Contains("What is connected to ANT1", local);

            var remote = TransmitStageSet.Build(new TransmitStageSet.Hosts
            {
                ReadStation = () => new TransmitStageSet.StationNow
                { AntennaPort = "ANT2", RemoteRadio = true },
            }).RunDeclarations[0].QuestionNow();
            Assert.Contains("You are connected remotely", remote);
            Assert.Contains("at that station", remote);
            Assert.Contains("ANT2", remote);

            // No station to read: the live question stands down and the
            // page falls back to the static one.
            var unknown = TransmitStageSet.Build(new TransmitStageSet.Hosts())
                .RunDeclarations[0].QuestionNow();
            Assert.Equal("", unknown);
        }

        [Fact]
        public void Only_host_supplied_fixes_are_bound()
        {
            var hosts = new TransmitStageSet.Hosts
            {
                SwitchToWasapi = () => FixerFixOutcome.Done("WASAPI"),
            };
            FixerStageSet set = TransmitStageSet.Build(hosts);

            Assert.True(set.FixActions.ContainsKey(AudioSetupCheck.FixSwitchToWasapi));
            Assert.False(set.FixActions.ContainsKey(AudioSetupCheck.FixEnablePcAudio));
        }

        // ---- stage 2: the tune probe's verdicts become findings ----

        [Fact]
        public void No_power_is_the_critical_interrupt_that_redirects_the_session()
        {
            // The operator opened this tool because transmit audio does not
            // work. "The transmitter made no power at all" is the one sentence
            // that stops them testing microphones — it is THE critical
            // finding, and it must reach the assertive region.
            var probe = TxTuneProbe.Result.Ran(TxTuneProbe.Verdict.NoPower, DateTime.UtcNow,
                new[] { TxTuneProbe.Reading.Got("FWDPWR", 0.0, "W") },
                tunePower: 10, computedSwr: double.NaN, stoppedEarly: false,
                frequency: "", mode: "", antenna: "");

            FixerOutcome outcome = TransmitStages.Transmitter(probe);
            FixerFinding f = Assert.Single(outcome.Findings);
            Assert.True(f.Critical);
            Assert.Equal(FixOwner.Operator, f.Owner);
            Assert.Contains("not an audio problem", f.WhatIsWrong,
                            StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_suspect_load_is_reported_but_does_not_shout()
        {
            // The transmitter WORKS here, and the probe already dropped the
            // carrier early on the bad reading — the immediate hazard is
            // handled before any words render. An assertive region that fires
            // for everything is one an operator learns to ignore.
            var probe = TxTuneProbe.Result.Ran(TxTuneProbe.Verdict.MakesPowerLoadSuspect,
                DateTime.UtcNow,
                new[] { TxTuneProbe.Reading.Got("FWDPWR", 10.0, "W"),
                        TxTuneProbe.Reading.Got("REFPWR", 8.0, "W") },
                tunePower: 10, computedSwr: 9.0, stoppedEarly: true,
                frequency: "", mode: "", antenna: "");

            FixerFinding f = Assert.Single(TransmitStages.Transmitter(probe).Findings);
            Assert.False(f.Critical);
            Assert.Equal(FixOwner.Operator, f.Owner);
            Assert.Contains("antenna", f.WhatToDo, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_stated_load_travels_with_the_transmitter_evidence()
        {
            // A power reading with no stated load cannot be read afterwards by
            // anyone — FlexRadio will ask what the measurement was taken into.
            var probe = TxTuneProbe.Result.Ran(TxTuneProbe.Verdict.MakesPower, DateTime.UtcNow,
                new[] { TxTuneProbe.Reading.Got("FWDPWR", 10.0, "W") },
                tunePower: 10, computedSwr: 1.1, stoppedEarly: false,
                frequency: "", mode: "", antenna: "");
            const string stated = "50 ohm dummy load on ANT1";

            string withLoad = TransmitStages.Transmitter(probe, stated).Evidence;
            Assert.Contains("as stated by the operator: " + stated, withLoad);

            // And nothing at all when nothing was said — an empty line would
            // read as a value.
            string without = TransmitStages.Transmitter(probe, "").Evidence;
            Assert.DoesNotContain("as stated by the operator", without);
        }

        [Fact]
        public void The_stated_load_flows_from_the_host_delegate_to_the_stage_evidence()
        {
            var hosts = new TransmitStageSet.Hosts
            {
                ProbeTransmitter = () => TxTuneProbe.Result.Ran(
                    TxTuneProbe.Verdict.MakesPower, DateTime.UtcNow,
                    new[] { TxTuneProbe.Reading.Got("FWDPWR", 10.0, "W") },
                    tunePower: 10, computedSwr: 1.1, stoppedEarly: false,
                    frequency: "", mode: "", antenna: ""),
                ReadLoadDeclaration = () => "the bench dummy load",
            };
            var run = new FixerRun(TransmitStageSet.Build(hosts));

            FixerStageResult r = run.RunStage(TransmitStageSet.TransmitterCheck);
            Assert.Contains("as stated by the operator: the bench dummy load", r.Evidence);
        }

        [Fact]
        public void A_working_transmitter_produces_no_findings_to_chase()
        {
            var probe = TxTuneProbe.Result.Ran(TxTuneProbe.Verdict.MakesPower, DateTime.UtcNow,
                new[] { TxTuneProbe.Reading.Got("FWDPWR", 10.0, "W") },
                tunePower: 10, computedSwr: 1.1, stoppedEarly: false,
                frequency: "", mode: "", antenna: "");

            FixerOutcome outcome = TransmitStages.Transmitter(probe);
            Assert.Empty(outcome.Findings);
            // The probe's evidence layout travels whole — observations first,
            // interpretation labelled (#217) — rather than being restated.
            Assert.Contains("audio chain not involved", outcome.Evidence);
        }

        // ---- stage 1: the microphone, and what it hands stage 4 ----

        [Fact]
        public void The_microphone_answer_names_the_device_and_the_result_keeps_the_baseline()
        {
            var facts = new MicCheckFacts
            {
                Measured = true, AudioArrived = true,
                Device = "Desk Mic", HostApi = "Windows WASAPI",
                PeakDb = -12.0, NoiseFloorDb = -60.0,
            };
            FixerOutcome outcome = TransmitStages.Microphone(facts);

            Assert.StartsWith("Yes", outcome.Answer);
            Assert.Contains(facts.Device, outcome.Answer);
            Assert.Same(facts, outcome.Payload); // the baseline stage 4 reads
        }

        [Fact]
        public void A_silent_microphone_is_an_operator_finding_not_a_shrug()
        {
            var facts = new MicCheckFacts { Measured = true, AudioArrived = false };
            FixerOutcome outcome = TransmitStages.Microphone(facts);

            Assert.StartsWith("No", outcome.Answer);
            FixerFinding f = Assert.Single(outcome.Findings);
            Assert.Equal(FixOwner.Operator, f.Owner);
        }

        // ---- stage 4: read against the baseline, all four ways ----

        private static SpokenTransmitFacts SpokenFailed() => new SpokenTransmitFacts
        {
            Attempted = true, ReachedRadio = false, Device = "Desk Mic",
        };

        [Fact]
        public void The_same_failure_reads_differently_under_each_baseline()
        {
            var micGood = new MicCheckFacts { Measured = true, AudioArrived = true,
                                              Device = "Desk Mic" };
            var micSilent = new MicCheckFacts { Measured = true, AudioArrived = false };

            string withGood = TransmitStages.Spoken(SpokenFailed(), micGood).Answer;
            string withSilent = TransmitStages.Spoken(SpokenFailed(), micSilent).Answer;
            string withNone = TransmitStages.Spoken(SpokenFailed(), null).Answer;

            // A stage-4 failure means something quite different depending on
            // whether the microphone measured well minutes earlier — so the
            // three conclusions must be three different sentences.
            Assert.NotEqual(withGood, withSilent);
            Assert.NotEqual(withGood, withNone);
            Assert.NotEqual(withSilent, withNone);
        }

        [Fact]
        public void A_good_baseline_moves_suspicion_off_the_microphone()
        {
            var micGood = new MicCheckFacts { Measured = true, AudioArrived = true,
                                              Device = "Desk Mic" };
            string answer = TransmitStages.Spoken(SpokenFailed(), micGood).Answer;

            Assert.Contains("least likely", answer);
            Assert.Contains(micGood.Device, answer);
        }

        [Fact]
        public void No_baseline_says_the_question_cannot_be_split_and_points_at_stage_1()
        {
            string answer = TransmitStages.Spoken(SpokenFailed(), null).Answer;
            Assert.Contains("microphone check was not run", answer);
            Assert.Contains("cannot be separated", answer);
        }

        [Fact]
        public void A_spoken_success_is_a_plain_yes()
        {
            var facts = new SpokenTransmitFacts
            { Attempted = true, ReachedRadio = true, Device = "Desk Mic" };
            Assert.StartsWith("Yes", TransmitStages.Spoken(facts, null).Answer);
        }

        [Fact]
        public void Stage_4_actually_reads_stage_1s_result_through_the_engine()
        {
            // End to end through the run: the wiring, not just the analyzer.
            var hosts = new TransmitStageSet.Hosts
            {
                MeasureMicrophone = () => new MicCheckFacts
                { Measured = true, AudioArrived = true, Device = "Desk Mic" },
                RunSpokenTransmit = () => new SpokenTransmitFacts
                { Attempted = true, ReachedRadio = false, Device = "Desk Mic" },
            };
            var run = new FixerRun(TransmitStageSet.Build(hosts));

            run.RunStage(TransmitStageSet.MicrophoneCheck);
            FixerStageResult spoken = run.RunStage(TransmitStageSet.SpokenTransmit);

            Assert.Contains("least likely", spoken.Answer);
        }

        // ---- stage 3: the probe set speaks, and disagreement is preserved ----

        [Fact]
        public void The_injected_stage_answers_with_the_probe_sets_own_judgement()
        {
            var facts = new InjectedTransmitFacts
            {
                Probes = new[]
                {
                    new TxProbeSet.ProbeResult(TxProbeSet.Probe.SingleTone,
                        TxProbeSet.Outcome.ReachedRadio, "level fine"),
                    new TxProbeSet.ProbeResult(TxProbeSet.Probe.ToneLadder,
                        TxProbeSet.Outcome.ReachedRadio, "all rungs"),
                    new TxProbeSet.ProbeResult(TxProbeSet.Probe.Voice,
                        TxProbeSet.Outcome.DidNotReach, "silence"),
                },
                ConditioningActive = false,
            };

            FixerOutcome outcome = TransmitStages.Injected(facts);
            // TxProbeSet's judgement flows through, including the branch that
            // consults the conditioning setting rather than assuming it.
            Assert.Equal(TxProbeSet.OperatorSummary(facts.Probes, false), outcome.Answer);
            Assert.Contains("NOT the difference", outcome.Answer);
        }

        [Fact]
        public void The_injected_evidence_lists_every_probe_and_the_conditioning_state()
        {
            var facts = new InjectedTransmitFacts
            {
                Probes = new[]
                {
                    new TxProbeSet.ProbeResult(TxProbeSet.Probe.Voice,
                        TxProbeSet.Outcome.Unavailable, "no voice installed"),
                },
                ConditioningActive = null,
            };

            string evidence = TransmitStages.Injected(facts).Evidence;
            Assert.Contains("no voice installed", evidence);
            Assert.Contains("could not be read", evidence);
        }

        // ---- stage 1 is advised by stage 0 (#241) ----

        private static MicCheckFacts SilentMic() => new MicCheckFacts
        { Measured = true, AudioArrived = false, Device = "Desk Mic" };

        [Fact]
        public void Stage_1_points_at_stage_0s_finding_instead_of_repeating_it()
        {
            // Stage 0 measured the mute and reported it definitively; stage 1
            // must not send the operator to go and check it again.
            var setup = new AudioSetupFacts { WindowsInputMuted = true,
                                              MicrophonePrivacyBlocked = false };
            FixerFinding f = Assert.Single(
                TransmitStages.Microphone(SilentMic(), setup).Findings);

            Assert.Contains("Stage 0 has already named the cause", f.WhatToDo);
            Assert.Contains("muted", f.WhatToDo);
            Assert.DoesNotContain("Check the cable, the Windows mute", f.WhatToDo);
        }

        [Fact]
        public void Stage_1_narrows_to_the_cable_when_stage_0_cleared_windows()
        {
            // When both Windows facts are measured CLEAR, the cable and the
            // device are the whole remaining answer — given cleanly, not
            // third in a list of three.
            var setup = new AudioSetupFacts { WindowsInputMuted = false,
                                              MicrophonePrivacyBlocked = false };
            FixerFinding f = Assert.Single(
                TransmitStages.Microphone(SilentMic(), setup).Findings);

            Assert.Contains("stage 0 checked both", f.WhatToDo);
            Assert.Contains("cable", f.WhatToDo);
            Assert.DoesNotContain("Windows microphone privacy setting", f.WhatToDo);
        }

        [Fact]
        public void Stage_1_keeps_the_full_checklist_only_when_stage_0_has_nothing_to_say()
        {
            // Not run, or the Windows facts could not be observed (null is
            // "never observed", not "clear") — the one case where the full
            // checklist is correct.
            FixerFinding withoutStage0 = Assert.Single(
                TransmitStages.Microphone(SilentMic()).Findings);
            Assert.Contains("Check the cable, the Windows mute", withoutStage0.WhatToDo);

            var unobserved = new AudioSetupFacts { WindowsInputMuted = null,
                                                   MicrophonePrivacyBlocked = null };
            FixerFinding withNulls = Assert.Single(
                TransmitStages.Microphone(SilentMic(), unobserved).Findings);
            Assert.Contains("Check the cable, the Windows mute", withNulls.WhatToDo);
        }

        [Fact]
        public void Stage_1_reads_stage_0s_facts_through_the_engine()
        {
            // End to end through the run — the wiring, not just the analyzer.
            var hosts = new TransmitStageSet.Hosts
            {
                ReadAudioSetup = () => new AudioSetupFacts
                {
                    OpenHostApi = "Windows WASAPI",
                    OpenInputDevice = "Desk Mic",
                    InputDeviceSelected = true,
                    WindowsInputMuted = true,
                },
                MeasureMicrophone = SilentMic,
            };
            var run = new FixerRun(TransmitStageSet.Build(hosts));
            run.RunStage(TransmitStageSet.AudioSetup);
            FixerStageResult mic = run.RunStage(TransmitStageSet.MicrophoneCheck);

            Assert.Contains("Stage 0 has already named the cause",
                            mic.Findings.Single(f => f.Id == "mic-silent").WhatToDo);
        }

        // ---- what pressing Run will do (#250) ----

        [Fact]
        public void The_transmitting_stages_say_the_live_power_and_port_before_they_are_pressed()
        {
            var set = TransmitStageSet.Build(new TransmitStageSet.Hosts
            {
                ReadStation = () => new TransmitStageSet.StationNow
                {
                    TunePowerWatts = 25,
                    RfPowerWatts = 40,
                    AntennaPort = "ANT1",
                },
            });

            string tune = set.Find(TransmitStageSet.TransmitterCheck).DescribeRunAction();
            Assert.Contains("at 25 watts into ANT1", tune);
            Assert.Contains("two seconds", tune);

            string spoken = set.Find(TransmitStageSet.SpokenTransmit).DescribeRunAction();
            Assert.Contains("at 40 watts into ANT1", spoken);
            Assert.Contains("eight seconds", spoken);

            string injected = set.Find(TransmitStageSet.InjectedTransmit).DescribeRunAction();
            Assert.Contains("at 40 watts into ANT1", injected);
            Assert.Contains("microphone stays out of the path", injected);
        }

        [Fact]
        public void The_sentences_survive_a_station_that_cannot_be_read()
        {
            // No ReadStation delegate at all: the live half is omitted, never
            // guessed, and the sentence still reads whole.
            var set = TransmitStageSet.Build(new TransmitStageSet.Hosts());

            string tune = set.Find(TransmitStageSet.TransmitterCheck).DescribeRunAction();
            Assert.Contains("tune carrier for about two seconds.", tune);
            Assert.DoesNotContain("watts", tune);
            Assert.DoesNotContain("into", tune);
        }

        [Fact]
        public void The_rf_silent_stages_say_nothing_transmits()
        {
            var set = TransmitStageSet.Build(new TransmitStageSet.Hosts());
            Assert.Contains("Nothing transmits.",
                set.Find(TransmitStageSet.AudioSetup).DescribeRunAction());
            Assert.Contains("Nothing transmits.",
                set.Find(TransmitStageSet.MicrophoneCheck).DescribeRunAction());
        }

        [Fact]
        public void Every_transmitting_stage_offers_the_power_hand_off()
        {
            // The other half of #250: the stage names the power it will use,
            // so it must also offer the way to change it — WITHOUT leaving
            // the modal Fixer, because leaving used to abandon the run. The
            // hand-off goes to the host's own power surface; the page never
            // grows a number box of its own.
            var set = TransmitStageSet.Build(new TransmitStageSet.Hosts());

            foreach (FixerStage s in set.Stages)
            {
                bool offers = s.HostActions.Any(
                    a => a.MessageKind == "open-power-dialog");
                Assert.True(s.Transmits == offers,
                    "stage " + s.Number + " (" + s.Title + ") "
                    + (s.Transmits
                        ? "transmits and does not offer the power hand-off"
                        : "does not transmit and offers the power hand-off anyway"));
            }

            // Stage 2 transmits the tune carrier, so its label names TUNE
            // power; the audio stages transmit at the RF power and say so.
            Assert.Equal("Change the tune power",
                set.Find(TransmitStageSet.TransmitterCheck).HostActions
                   .Single(a => a.MessageKind == "open-power-dialog").Label);
            Assert.Equal("Change the transmit power",
                set.Find(TransmitStageSet.InjectedTransmit).HostActions
                   .Single(a => a.MessageKind == "open-power-dialog").Label);
            Assert.Equal("Change the transmit power",
                set.Find(TransmitStageSet.SpokenTransmit).HostActions
                   .Single(a => a.MessageKind == "open-power-dialog").Label);
        }
    }
}

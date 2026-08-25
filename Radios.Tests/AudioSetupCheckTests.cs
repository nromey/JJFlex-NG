using System;
using System.Linq;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Stage 0's decisions: every detected problem lands in exactly one of the
    /// three fix-ownership classes, fixes are offered only where a button can
    /// actually do the work, and an unknown fact never becomes a finding.
    /// </summary>
    public class AudioSetupCheckTests
    {
        private static AudioSetupFacts Healthy() => new AudioSetupFacts
        {
            OpenHostApi = "Windows WASAPI",
            OpenInputDevice = "Bench Interface In",
            OpenOutputDevice = "Bench Interface Out",
            OpenSampleRateHz = 48000,
            OpenChannels = 1,
            ConfiguredHostApi = "Windows WASAPI",
            ConfiguredInputDevice = "Bench Interface In",
            WasapiAvailable = true,
            InputDeviceSelected = true,
            PcAudioOn = true,
            RemoteRadio = true,
        };

        [Fact]
        public void A_healthy_setup_yields_no_findings_and_an_answer_naming_what_is_open()
        {
            AudioSetupFacts facts = Healthy();
            FixerOutcome outcome = AudioSetupCheck.Analyze(facts);

            Assert.Empty(outcome.Findings);
            // The answer speaks about what is actually open — the facts the
            // host read, flowing through, not values this test invents twice.
            Assert.Contains(facts.OpenInputDevice, outcome.Answer);
            Assert.Contains(facts.OpenHostApi, outcome.Answer);
        }

        // ---- MME ----

        [Fact]
        public void Mme_with_wasapi_available_is_ours_to_fix_with_a_button()
        {
            AudioSetupFacts facts = Healthy();
            facts.OpenHostApi = AudioSetupCheck.MmeApiName;
            facts.ConfiguredHostApi = facts.OpenHostApi; // MME by default, not by drift

            FixerFinding f = Assert.Single(AudioSetupCheck.Analyze(facts).Findings);
            Assert.Equal(FixOwner.Us, f.Owner);
            Assert.Equal(AudioSetupCheck.FixSwitchToWasapi, f.FixActionId);
            // THE INVARIANT: the text must explain that MME MISREPORTS THE
            // FORMAT, not merely that it sounds worse. An operator told "MME is
            // poor quality" swaps a working microphone; an operator told "the
            // rate it reports may not be the rate at the device" understands
            // why the numbers cannot be trusted.
            //
            // Asserted on the word "misreport" until 2026-08-25, which broke
            // the moment the sentence was rewritten to say the same thing
            // better. "converted format" is the load-bearing fact rather than
            // any particular verb, so it is the more durable token — but this
            // is still prose checked against prose. Update it with the wording;
            // do not delete it.
            Assert.Contains("converted format", f.WhatIsWrong);
        }

        [Fact]
        public void Mme_without_wasapi_is_stated_honestly_as_unfixable_here()
        {
            AudioSetupFacts facts = Healthy();
            facts.OpenHostApi = AudioSetupCheck.MmeApiName;
            facts.ConfiguredHostApi = facts.OpenHostApi;
            facts.WasapiAvailable = false;

            FixerFinding f = Assert.Single(AudioSetupCheck.Analyze(facts).Findings);
            Assert.Equal(FixOwner.NobodyHere, f.Owner);
            Assert.Empty(f.FixActionId); // no button that cannot deliver
        }

        [Fact]
        public void The_mme_test_detects_the_api_however_portaudio_spells_it()
        {
            Assert.True(AudioSetupCheck.IsMme("MME"));
            Assert.True(AudioSetupCheck.IsMme("mme"));
            Assert.True(AudioSetupCheck.IsMme("MME (Windows multimedia)"));
            Assert.False(AudioSetupCheck.IsMme("Windows WASAPI"));
            Assert.False(AudioSetupCheck.IsMme(""));
            Assert.False(AudioSetupCheck.IsMme(null));
        }

        // ---- missing input ----

        [Fact]
        public void No_input_with_a_candidate_offers_that_candidate_as_the_fix()
        {
            AudioSetupFacts facts = Healthy();
            facts.InputDeviceSelected = false;
            facts.SuggestedInputDevice = "Desk Mic";

            FixerFinding f = Assert.Single(AudioSetupCheck.Analyze(facts).Findings);
            Assert.Equal(FixOwner.Us, f.Owner);
            Assert.Equal(AudioSetupCheck.FixUseSuggestedInput, f.FixActionId);
            // The button names the specific device it will choose — a
            // detected fix, not a picker.
            Assert.Contains(facts.SuggestedInputDevice, f.WhatToDo);
        }

        [Fact]
        public void No_input_and_nothing_to_offer_belongs_to_the_operator()
        {
            AudioSetupFacts facts = Healthy();
            facts.InputDeviceSelected = false;
            facts.SuggestedInputDevice = "";

            FixerFinding f = Assert.Single(AudioSetupCheck.Analyze(facts).Findings);
            Assert.Equal(FixOwner.Operator, f.Owner);
            Assert.Empty(f.FixActionId);
        }

        // ---- PC audio ----

        [Fact]
        public void Pc_audio_off_on_a_remote_radio_is_ours_to_fix()
        {
            AudioSetupFacts facts = Healthy();
            facts.PcAudioOn = false;

            FixerFinding f = Assert.Single(AudioSetupCheck.Analyze(facts).Findings);
            Assert.Equal(FixOwner.Us, f.Owner);
            Assert.Equal(AudioSetupCheck.FixEnablePcAudio, f.FixActionId);
        }

        [Fact]
        public void Pc_audio_off_on_a_local_radio_is_not_a_finding_at_all()
        {
            // On a LAN radio the transmit audio does not ride this switch.
            // Flagging it would send the operator to fix something that is
            // not in the path.
            AudioSetupFacts facts = Healthy();
            facts.PcAudioOn = false;
            facts.RemoteRadio = false;

            Assert.Empty(AudioSetupCheck.Analyze(facts).Findings);
        }

        // ---- mic profile ----

        [Fact]
        public void An_empty_mic_profile_is_ours_to_fix()
        {
            AudioSetupFacts facts = Healthy();
            facts.MicProfileEmpty = true;

            FixerFinding f = Assert.Single(AudioSetupCheck.Analyze(facts).Findings);
            Assert.Equal(FixOwner.Us, f.Owner);
            Assert.Equal(AudioSetupCheck.FixFillMicProfile, f.FixActionId);
        }

        // ---- Windows-side facts ----

        [Fact]
        public void Windows_side_problems_belong_to_the_operator_with_one_plain_sentence()
        {
            foreach (Action<AudioSetupFacts> observe in new Action<AudioSetupFacts>[]
            {
                f => f.WindowsInputMuted = true,
                f => f.MicrophonePrivacyBlocked = true,
                f => f.InputDeviceUnplugged = true,
            })
            {
                AudioSetupFacts facts = Healthy();
                observe(facts);

                FixerFinding f2 = Assert.Single(AudioSetupCheck.Analyze(facts).Findings);
                Assert.Equal(FixOwner.Operator, f2.Owner);
                Assert.NotEmpty(f2.WhatToDo);
                Assert.Empty(f2.FixActionId); // we observe these; we cannot press them
            }
        }

        [Fact]
        public void An_unknown_fact_never_becomes_a_finding()
        {
            // Null is "could not be read", and a clean bill of health must
            // not be issued from a reading that never happened — but neither
            // may a fault be invented from one.
            AudioSetupFacts facts = Healthy();
            facts.WindowsInputMuted = null;
            facts.MicrophonePrivacyBlocked = null;
            facts.InputDeviceUnplugged = null;

            Assert.Empty(AudioSetupCheck.Analyze(facts).Findings);
        }

        // ---- configuration versus reality ----

        [Fact]
        public void Config_and_open_disagreeing_is_itself_a_finding_naming_both_sides()
        {
            AudioSetupFacts facts = Healthy();
            facts.ConfiguredInputDevice = "Configured Mic";
            facts.OpenInputDevice = "Actually Open Mic";

            FixerFinding f = Assert.Single(AudioSetupCheck.Analyze(facts).Findings);
            Assert.Equal(FixOwner.Us, f.Owner);
            Assert.Equal(AudioSetupCheck.FixReopenConfiguredAudio, f.FixActionId);
            // Both sides of the disagreement are stated — that IS the finding.
            Assert.Contains(facts.ConfiguredInputDevice, f.WhatIsWrong);
            Assert.Contains(facts.OpenInputDevice, f.WhatIsWrong);
        }

        [Fact]
        public void A_configured_device_with_nothing_open_is_not_called_a_mismatch()
        {
            // "Nothing is open" and "something else is open" are different
            // facts; only the second is a disagreement.
            AudioSetupFacts facts = Healthy();
            facts.OpenInputDevice = "";
            facts.OpenHostApi = "";
            facts.OpenSampleRateHz = 0;
            facts.OpenChannels = 0;

            FixerOutcome outcome = AudioSetupCheck.Analyze(facts);
            Assert.DoesNotContain(outcome.Findings,
                f => f.Id == AudioSetupCheck.ConfigOpenMismatch);
            // WORDING-SENSITIVE, deliberately. The invariant is that an absent
            // stream is reported as ABSENT rather than as a disagreement, and
            // the DoesNotContain above guards the half that matters. This half
            // guards that the answer actually says so, and the only way to
            // check prose is against prose — so when the wording changes, change
            // this with it rather than deleting it.
            Assert.Contains("No stream is open", outcome.Answer);
        }

        // ---- the evidence travels ----

        [Fact]
        public void The_evidence_reports_every_fact_including_the_unreadable_ones()
        {
            AudioSetupFacts facts = Healthy();
            facts.WindowsInputMuted = null;

            string evidence = AudioSetupCheck.Analyze(facts).Evidence;
            Assert.Contains(facts.OpenInputDevice, evidence);
            Assert.Contains(facts.ConfiguredHostApi, evidence);
            // Unreadable stays distinguishable from a reading of "no".
            Assert.Contains("could not be read", evidence);
        }
    }
}

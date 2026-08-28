#nullable enable
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  #277 — "Spoke" means we handed it to a backend, not that anyone
    //  heard it.
    //
    //  prism_backend_speak returns a PrismError and the return value was
    //  discarded. So speaking into a reader that had left returned an error
    //  nobody read, Speak returned normally, EmitCore set reachedBackend =
    //  true, and the trace wrote "Spoke" for a sentence no human could have
    //  heard. That one discarded value is why a faithful trace misled four
    //  capture readings in one evening, and why three findings in two days
    //  turned out to be the reader-binding bug wearing a disguise.
    //
    //  THE POSITIVE CONTROL FOR THESE TESTS IS NOT IN THEM. The task's own
    //  warning is that a fix which reports delivery failures on healthy
    //  utterances would be worse than the silence it replaces, so the healthy
    //  return value had to be established rather than assumed. It was read at
    //  the pinned Prism source (v0.18.1, tag commit d2998e9, the build we
    //  actually ship), source/prism.cpp:356:
    //
    //      const auto r = backend->impl->speak(text, interrupt);
    //      return r ? PRISM_OK : to_prism_error(r.error());
    //
    //  PRISM_OK is the only success value, from every backend, always — there
    //  is no second healthy code to accommodate. And to_prism_error is a raw
    //  static_cast from BackendError, whose enum matches PrismError index for
    //  index across all 24 values, so the names below are the right names and
    //  not their neighbours.
    // ────────────────────────────────────────────────────────────────
    public class SpeechDeliveryTests
    {
        [Fact]
        public void Ok_IsTheOnlySuccess_AndCarriesNoFailure()
        {
            var d = PrismScreenReader.Classify(PrismError.Ok, "speak", "NVDA");

            Assert.True(d.Delivered);
            Assert.False(d.Refused);
            Assert.Null(d.Failure);
        }

        [Fact]
        public void SpeakingIntoADeadReader_IsReportedAsARefusal_NamingTheReaderAndTheError()
        {
            // The exact shape of the fault. NVDA's backend maps a failed
            // nvdaController_speakText — the RPC to a reader that has gone —
            // to InternalBackendError, which crosses the C ABI as
            // PRISM_ERROR_INTERNAL.
            var d = PrismScreenReader.Classify(PrismError.Internal, "speak", "NVDA");

            Assert.False(d.Delivered);
            Assert.True(d.Refused);
            Assert.NotNull(d.Failure);

            // The phrase goes straight into a trace and into the transcript, so
            // it has to say WHICH call, to WHICH reader, and WHY — a bare
            // "delivery failed" would leave the next reader of a capture doing
            // the same guessing this task exists to end.
            Assert.Contains("prism_backend_speak", d.Failure!);
            Assert.Contains("NVDA", d.Failure!);
            Assert.Contains("Internal", d.Failure!);
        }

        [Fact]
        public void JawsRefusingAnUtteranceItAccepted_IsAlsoARefusal()
        {
            // JAWS's SayString returns an HRESULT *and* a VARIANT_BOOL, and
            // Prism fails the call unless both say yes. So a JAWS that takes
            // the call and declines the utterance is reportable — which is the
            // discriminator #298 needs for "after a reader switch, interrupt-
            // mode speech vanishes and queued speech arrives", and which could
            // not exist while this value was discarded.
            var d = PrismScreenReader.Classify(PrismError.Internal, "speak", "JAWS");

            Assert.True(d.Refused);
            Assert.Contains("JAWS", d.Failure!);
        }

        [Fact]
        public void NotImplemented_IsARefusal_NotAShrug()
        {
            // A backend that advertises a capability and does not implement it
            // did not speak. The one place NotImplemented is treated as a
            // capability answer rather than a fault is Output's documented
            // fall-through to speak, and that fall-through then reports on the
            // speak.
            var d = PrismScreenReader.Classify(PrismError.NotImplemented, "output", "ZDSR");
            Assert.True(d.Refused);
        }

        [Fact]
        public void AnUnnamedReader_StillProducesAReadablePhrase()
        {
            var d = PrismScreenReader.Classify(PrismError.BackendNotAvailable, "braille", null);

            Assert.True(d.Refused);
            Assert.Contains("an unnamed reader", d.Failure!);
        }

        [Fact]
        public void NotAttempted_IsNotAFailure()
        {
            // The tri-state matters: an empty message, a backend not up yet, or
            // braille on a machine with no display are all ordinary. Reporting
            // them as delivery failures would train a reader to ignore the ones
            // that mean something — the same mechanism that hid a real signal
            // in a haystack of benign build warnings.
            var d = SpeechDelivery.NotAttempted;

            Assert.False(d.Delivered);
            Assert.False(d.Refused);
            Assert.Null(d.Failure);
        }

        [Fact]
        public void TheRenderOffBackend_ReportsDelivery_SoThePolicyLayersStillRun()
        {
            // #171's diverted backend must claim delivery. Every layer above it
            // — the ledger, the salvage rule, the anti-clip gap — keys on
            // delivery, so reporting NotAttempted here would quietly disable
            // the protections a silent test run exists to exercise. Whether
            // anything SOUNDED is a different question, answered by the
            // transcript's rendered flag.
            IScreenReader diverted = new DivertedScreenReader();

            Assert.True(diverted.Speak("anything", interrupt: false).Delivered);
            Assert.True(diverted.Output("anything", interrupt: true).Delivered);

            // Braille is the exception: HasBraille is false, so claiming
            // delivery would assert a display that is not there.
            Assert.False(diverted.HasBraille);
            Assert.False(diverted.Braille("anything").Delivered);
            Assert.False(diverted.Braille("anything").Refused);
        }

        [Fact]
        public void TheNoBackendCase_ReportsNotAttempted_NotAPerUtteranceFailure()
        {
            // Reaching NullScreenReader means prism.dll is missing and a blind
            // operator has an application that cannot talk to them. That is
            // already traced once, loudly, by the factory. Reporting a refusal
            // per utterance as well would bury that one line under thousands of
            // duplicates — which is the failure mode the delivery escalation in
            // ScreenReaderOutput is deliberately built to avoid.
            IScreenReader none = new NullScreenReader();

            Assert.False(none.Speak("anything", interrupt: false).Delivered);
            Assert.False(none.Speak("anything", interrupt: false).Refused);
        }
    }
}

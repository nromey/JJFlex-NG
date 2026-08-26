using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 31 Track P, task #102: the learning is now a setting — how much
    /// evidence it takes, whether it happens at all, and a way to take it back.
    ///
    /// <para>The contract task #79 pinned still holds and is tested next door
    /// in <see cref="ConnectPathPolicyTests"/>: a learned value only ever
    /// prefills, a stored explicit choice always wins. Nothing here may weaken
    /// it. What these tests add is that the OFF state is genuinely off, that
    /// the offered thresholds are the ones the store can actually honour, and
    /// that a reset really does remove the thing the learning reads.</para>
    ///
    /// <para><b>Task #232.</b> The off-switch test below failed once in a
    /// full-suite run and passed five times either side of it, on the same
    /// commit. Its state — the settings directory and the cache derived from it
    /// — is process-wide, and the isolation was a hand-rolled save-and-restore
    /// repeated in six classes. It is now one object,
    /// <see cref="RadioConfigStaticsScope"/>, which takes every piece of that
    /// state together and says so out loud if anything else touches it while
    /// this class is running.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectPathLearningConfigTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ConnectPathLearningConfigTests));
        private string _dir => _scope.Directory;

        public void Dispose() => _scope.Dispose();

        private static void RecordSuccesses(string serial, ConnectPathKind path, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ConnectionHistory.Record(serial, path.ToString(),
                    ConnectPathPolicy.ConnectedOutcome, 900);
            }
        }

        // ------------------------------------------------------------------
        // Defaults: an install that upgrades into this build changes nothing
        // ------------------------------------------------------------------

        [Fact]
        public void WithNoSavedFileTheDefaultsAreExactlyTheOldBehaviour()
        {
            var cfg = ConnectPathLearningConfig.Current;
            Assert.True(cfg.LearnFromHistory);
            Assert.Equal(ConnectPathPolicy.TrendThreshold, cfg.TrendThreshold);
            Assert.Equal(3, cfg.TrendThreshold);
        }

        // ------------------------------------------------------------------
        // The off switch
        // ------------------------------------------------------------------

        [Fact]
        public void TurnedOffMeansNoTrendIsEverReturnedHoweverLoudTheHistory()
        {
            const string serial = "0123-4567-8600-9001";
            RecordSuccesses(serial, ConnectPathKind.SmartLink, 6);

            // On: the history speaks.
            Assert.Equal(ConnectPathKind.SmartLink,
                ConnectPathPolicy.LearnForRadioUsingSettings(serial));

            Assert.True(new ConnectPathLearningConfig { LearnFromHistory = false }.Save(_dir));

            // Off: it does not, and the mechanism-level call still can — the
            // switch is policy, held at one place, not a change to the rule.
            Assert.Null(ConnectPathPolicy.LearnForRadioUsingSettings(serial));
            Assert.Equal(ConnectPathKind.SmartLink, ConnectPathPolicy.LearnForRadio(serial));
        }

        [Fact]
        public void TurningLearningOffDoesNotStopTheHistoryBeingKept()
        {
            // Off means "do not act on it", never "do not keep it" — the ring
            // is the record behind connection timings and answers support
            // questions nothing else can.
            const string serial = "0123-4567-8600-9002";
            Assert.True(new ConnectPathLearningConfig { LearnFromHistory = false }.Save(_dir));

            RecordSuccesses(serial, ConnectPathKind.Local, 3);
            Assert.Equal(3, ConnectionHistory.Load(serial).Count);
        }

        // ------------------------------------------------------------------
        // The threshold
        // ------------------------------------------------------------------

        [Fact]
        public void TheSavedThresholdIsTheOneTheAppUses()
        {
            const string serial = "0123-4567-8600-9003";
            RecordSuccesses(serial, ConnectPathKind.Local, 3);

            Assert.True(new ConnectPathLearningConfig { TrendThreshold = 4 }.Save(_dir));
            Assert.Null(ConnectPathPolicy.LearnForRadioUsingSettings(serial));

            RecordSuccesses(serial, ConnectPathKind.Local, 1);
            Assert.Equal(ConnectPathKind.Local, ConnectPathPolicy.LearnForRadioUsingSettings(serial));
        }

        [Fact]
        public void FiveIsReachableForARadioThatFallsBackEveryTime()
        {
            // The reason the ceiling is five and not six. A chain-walking
            // connect writes TWO entries — the leg that failed, then the leg
            // that worked — and the ring holds ten, so such a radio can show at
            // most five successes. Five must therefore be reachable, and this
            // is the test that says the offered maximum is not a dead setting.
            const string serial = "0123-4567-8600-9004";
            for (int i = 0; i < 5; i++)
            {
                ConnectionHistory.Record(serial, ConnectPathKind.Local.ToString(), "not_found", 400);
                ConnectionHistory.Record(serial, ConnectPathKind.SmartLink.ToString(),
                    ConnectPathPolicy.ConnectedOutcome, 900);
            }

            Assert.Equal(ConnectionHistory.MaxEntries, ConnectionHistory.Load(serial).Count);
            Assert.True(new ConnectPathLearningConfig
            {
                TrendThreshold = ConnectPathLearningConfig.MaxThreshold,
            }.Save(_dir));

            Assert.Equal(ConnectPathKind.SmartLink,
                ConnectPathPolicy.LearnForRadioUsingSettings(serial));
        }

        [Fact]
        public void AHandEditedThresholdIsClampedToWhatTheStoreCanHonour()
        {
            var path = ConnectPathLearningConfig.GetFilePath(_dir);
            File.WriteAllText(path,
                "<?xml version=\"1.0\"?>\n" +
                "<ConnectPathLearningConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <LearnFromHistory>true</LearnFromHistory>\n" +
                "  <TrendThreshold>9</TrendThreshold>\n" +
                "</ConnectPathLearningConfig>\n");
            Assert.Equal(ConnectPathLearningConfig.MaxThreshold,
                ConnectPathLearningConfig.Load(_dir).TrendThreshold);

            File.WriteAllText(path,
                "<?xml version=\"1.0\"?>\n" +
                "<ConnectPathLearningConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <TrendThreshold>1</TrendThreshold>\n" +
                "</ConnectPathLearningConfig>\n");
            Assert.Equal(ConnectPathLearningConfig.MinThreshold,
                ConnectPathLearningConfig.Load(_dir).TrendThreshold);
        }

        [Fact]
        public void AnUnreadableSettingsFileFallsBackToTheDefaultsRatherThanBreaking()
        {
            File.WriteAllText(ConnectPathLearningConfig.GetFilePath(_dir), "this is not xml");
            var cfg = ConnectPathLearningConfig.Load(_dir);
            Assert.True(cfg.LearnFromHistory);
            Assert.Equal(3, cfg.TrendThreshold);
        }

        // ------------------------------------------------------------------
        // The reset
        // ------------------------------------------------------------------

        [Fact]
        public void ClearingTheRingIsWhatUnlearnsThePath()
        {
            // There is no stored "learned path" to clear on its own — it is
            // derived from the ring on every read. This test is the reason the
            // UI does not offer "forget the conclusion but keep the evidence".
            const string serial = "0123-4567-8600-9005";
            RecordSuccesses(serial, ConnectPathKind.SmartLink, 3);
            Assert.Equal(ConnectPathKind.SmartLink,
                ConnectPathPolicy.LearnForRadioUsingSettings(serial));

            Assert.True(ConnectionHistory.Clear(serial));

            Assert.Null(ConnectPathPolicy.LearnForRadioUsingSettings(serial));
            Assert.Empty(ConnectionHistory.Load(serial));
        }

        [Fact]
        public void ClearingARadioWithNoHistoryIsASuccessNotAFailure()
        {
            Assert.True(ConnectionHistory.Clear("0123-4567-8600-9006"));
        }

        [Fact]
        public void ClearAllCountsOnlyTheRadiosThatActuallyHadAHistory()
        {
            // "Cleared 5 radios" when four of them never had a history is a
            // number that sounds like more happened than did.
            RecordSuccesses("0123-4567-8600-9007", ConnectPathKind.Local, 2);
            RecordSuccesses("0123-4567-8600-9008", ConnectPathKind.SmartLink, 2);

            // A radio with a profile but no connection history at all.
            Assert.True(new RadioConfig().Save(_dir, "0123-4567-8600-9009"));

            var (cleared, failed) = ConnectionHistory.ClearAll();
            Assert.Equal(2, cleared);
            Assert.Equal(0, failed);

            Assert.Empty(ConnectionHistory.Load("0123-4567-8600-9007"));
            Assert.Empty(ConnectionHistory.Load("0123-4567-8600-9008"));
        }

        [Fact]
        public void ClearAllReachesARadioThatHasAHistoryButNoProfile()
        {
            // ListKnownRadioIds only lists directories holding a config.xml. A
            // "forget everything" that quietly skipped a radio whose profile is
            // missing would leave it still steering connects — the one outcome
            // the operator pressed the button to prevent.
            const string serial = "0123-4567-8600-9010";
            RecordSuccesses(serial, ConnectPathKind.Local, 3);
            File.Delete(Path.Combine(_dir, "radios", serial, "config.xml")); // never existed, but be explicit
            Assert.Empty(RadioConfig.ListKnownRadioIds(_dir));

            var (cleared, _) = ConnectionHistory.ClearAll();
            Assert.Equal(1, cleared);
            Assert.Empty(ConnectionHistory.Load(serial));
        }

        // ------------------------------------------------------------------
        // The contract, still
        // ------------------------------------------------------------------

        [Fact]
        public void NoneOfThisLetsALearnedValueOutrankAStoredChoice()
        {
            // The rule #79 exists to protect, re-asserted against every knob
            // #102 added: whatever the threshold, whatever the switch, a
            // stored explicit chain is returned untouched.
            var stored = new System.Collections.Generic.List<ConnectPathKind>
            {
                ConnectPathKind.Local, ConnectPathKind.SmartLink,
            };

            foreach (int threshold in new[] { 3, 4, 5 })
            {
                Assert.True(new ConnectPathLearningConfig { TrendThreshold = threshold }.Save(_dir));
                var chain = ConnectPathPolicy.Resolve(
                    stored, ConnectPathKind.SmartLink,
                    lanAvailable: false, wanAvailable: true, lastSeenRemote: true);
                Assert.Equal(stored, chain);
            }
        }
    }
}

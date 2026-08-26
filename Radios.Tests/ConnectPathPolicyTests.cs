using System;
using System.Collections.Generic;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 30 Track A, task #79: learn a connection path from a trend,
    /// never overwrite a choice.
    ///
    /// <para>The first test in this file is the reason the file exists. The
    /// rule "a learned value only ever prefills; a stored explicit choice
    /// always wins" has an invisible failure mode — violating it produces code
    /// that reads exactly like code that honours it, and the symptom does not
    /// appear until an operator's deliberate setting silently evaporates
    /// weeks later, at which point it looks like the app forgetting rather
    /// than the app disagreeing. It cannot be caught by review, so it is
    /// pinned by a test.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectPathPolicyTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _savedBase;

        public ConnectPathPolicyTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "jjflex-pathpolicy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _savedBase = RadioConfig.BaseDirectory;
            RadioConfig.BaseDirectory = _dir;
        }

        public void Dispose()
        {
            RadioConfig.BaseDirectory = _savedBase;
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        }

        private static ConnectionAttemptRecord Rec(ConnectPathKind path, string outcome) =>
            new()
            {
                TimestampUtc = DateTime.UtcNow,
                Path = path.ToString(),
                Outcome = outcome,
                DurationMs = 1200,
            };

        private static ConnectionAttemptRecord Ok(ConnectPathKind path) =>
            Rec(path, ConnectPathPolicy.ConnectedOutcome);

        // ------------------------------------------------------------------
        // The contract
        // ------------------------------------------------------------------

        [Fact]
        public void StoredChoiceSurvivesAContradictingTrend()
        {
            // The operator said local first. The history says the opposite as
            // loudly as it can — every recent connect went over SmartLink.
            var stored = new List<ConnectPathKind>
            {
                ConnectPathKind.Local, ConnectPathKind.SmartLink,
            };
            var history = new List<ConnectionAttemptRecord>
            {
                Ok(ConnectPathKind.SmartLink),
                Ok(ConnectPathKind.SmartLink),
                Ok(ConnectPathKind.SmartLink),
                Ok(ConnectPathKind.SmartLink),
            };

            var learned = ConnectPathPolicy.LearnFrom(history);
            Assert.Equal(ConnectPathKind.SmartLink, learned);

            var chain = ConnectPathPolicy.Resolve(
                stored, learned, lanAvailable: false, wanAvailable: true, lastSeenRemote: true);

            // Untouched. Not reordered, not appended to, not "helpfully"
            // promoted — the trend loses to the choice, completely.
            Assert.Equal(stored, chain);
            Assert.Equal(ConnectPathKind.Local, chain[0]);
        }

        [Fact]
        public void StoredOnePathOnlyChoiceIsNotWidenedByATrend()
        {
            // A one-entry chain means "this path only, never fall back" — the
            // thing that makes force-remote a valid hole-punch test
            // instrument. A trend must not quietly restore the fallback.
            var stored = new List<ConnectPathKind> { ConnectPathKind.SmartLink };
            var learned = ConnectPathKind.Local;

            var chain = ConnectPathPolicy.Resolve(
                stored, learned, lanAvailable: true, wanAvailable: true, lastSeenRemote: false);

            Assert.Single(chain);
            Assert.Equal(ConnectPathKind.SmartLink, chain[0]);
        }

        [Fact]
        public void TrendOnlyPrefillsWhenNoChoiceIsStored()
        {
            var chain = ConnectPathPolicy.Resolve(
                storedChain: new List<ConnectPathKind>(),
                learned: ConnectPathKind.SmartLink,
                lanAvailable: true, wanAvailable: true, lastSeenRemote: false);

            // Availability alone would have said local first; the trend
            // reorders it, because nobody has said otherwise.
            Assert.Equal(ConnectPathKind.SmartLink, chain[0]);
            Assert.Equal(2, chain.Count);
        }

        [Fact]
        public void ALearnedChainAlwaysKeepsBothPathsSoFallbackStillHappens()
        {
            foreach (var learned in new[] { ConnectPathKind.Local, ConnectPathKind.SmartLink })
            {
                var chain = ConnectPathPolicy.Resolve(
                    null, learned, lanAvailable: false, wanAvailable: false, lastSeenRemote: false);

                Assert.Equal(2, chain.Count);
                Assert.Contains(ConnectPathKind.Local, chain);
                Assert.Contains(ConnectPathKind.SmartLink, chain);
            }
        }

        [Fact]
        public void NoTrendFallsBackToTheDerivedDefault()
        {
            var localStory = ConnectPathPolicy.Resolve(
                null, null, lanAvailable: true, wanAvailable: true, lastSeenRemote: false);
            Assert.Equal(ConnectPathKind.Local, localStory[0]);

            var remoteStory = ConnectPathPolicy.Resolve(
                null, null, lanAvailable: false, wanAvailable: true, lastSeenRemote: false);
            Assert.Equal(ConnectPathKind.SmartLink, remoteStory[0]);

            var offlineRemembersRemote = ConnectPathPolicy.Resolve(
                null, null, lanAvailable: false, wanAvailable: false, lastSeenRemote: true);
            Assert.Equal(ConnectPathKind.SmartLink, offlineRemembersRemote[0]);
        }

        // ------------------------------------------------------------------
        // What counts as a trend
        // ------------------------------------------------------------------

        [Fact]
        public void ThreeSuccessesOnOnePathIsATrend()
        {
            var history = new List<ConnectionAttemptRecord>
            {
                Ok(ConnectPathKind.Local), Ok(ConnectPathKind.Local), Ok(ConnectPathKind.Local),
            };
            Assert.Equal(ConnectPathKind.Local, ConnectPathPolicy.LearnFrom(history));
        }

        [Fact]
        public void TwoSuccessesIsNotYetATrend()
        {
            var history = new List<ConnectionAttemptRecord>
            {
                Ok(ConnectPathKind.Local), Ok(ConnectPathKind.Local),
            };
            Assert.Null(ConnectPathPolicy.LearnFrom(history));
        }

        [Fact]
        public void AMixedRecentRunIsNotATrend()
        {
            var history = new List<ConnectionAttemptRecord>
            {
                Ok(ConnectPathKind.Local),
                Ok(ConnectPathKind.SmartLink),
                Ok(ConnectPathKind.Local),
            };
            Assert.Null(ConnectPathPolicy.LearnFrom(history));
        }

        [Fact]
        public void FailedLegsDoNotBreakARunOfSuccesses()
        {
            // A radio only reachable over SmartLink records a failed local leg
            // before every successful remote one, because the chain walks. A
            // rule that reset on any failure would learn nothing about exactly
            // the radios with the strongest habit.
            var history = new List<ConnectionAttemptRecord>
            {
                Rec(ConnectPathKind.Local, "not_found"),
                Ok(ConnectPathKind.SmartLink),
                Rec(ConnectPathKind.Local, "not_found"),
                Ok(ConnectPathKind.SmartLink),
                Rec(ConnectPathKind.Local, "not_found"),
                Ok(ConnectPathKind.SmartLink),
            };
            Assert.Equal(ConnectPathKind.SmartLink, ConnectPathPolicy.LearnFrom(history));
        }

        [Fact]
        public void OnlyTheMostRecentSuccessesCount()
        {
            // Old habit: three local. New habit: three SmartLink. The recent
            // one wins, which is the whole point of reading a trend.
            var history = new List<ConnectionAttemptRecord>
            {
                Ok(ConnectPathKind.Local), Ok(ConnectPathKind.Local), Ok(ConnectPathKind.Local),
                Ok(ConnectPathKind.SmartLink), Ok(ConnectPathKind.SmartLink), Ok(ConnectPathKind.SmartLink),
            };
            Assert.Equal(ConnectPathKind.SmartLink, ConnectPathPolicy.LearnFrom(history));
        }

        [Fact]
        public void FailuresAloneTeachNothing()
        {
            var history = new List<ConnectionAttemptRecord>
            {
                Rec(ConnectPathKind.SmartLink, "AuthenticationFailed"),
                Rec(ConnectPathKind.SmartLink, "failed"),
                Rec(ConnectPathKind.SmartLink, "not_found"),
                Rec(ConnectPathKind.SmartLink, "failed"),
            };
            Assert.Null(ConnectPathPolicy.LearnFrom(history));
        }

        [Fact]
        public void AnUnknownPathNameEndsTheRunRatherThanBeingSkipped()
        {
            // A future build's third path (JJ Flexible Connect) recorded by a
            // newer version, read back by this one. It is a real success on a
            // real path, so pretending it never happened would let the two
            // older successes behind it look like an unbroken run.
            var history = new List<ConnectionAttemptRecord>
            {
                Ok(ConnectPathKind.Local),
                Ok(ConnectPathKind.Local),
                Rec((ConnectPathKind)99, ConnectPathPolicy.ConnectedOutcome),
            };
            history[2].Path = "Connect";
            Assert.Null(ConnectPathPolicy.LearnFrom(history));
        }

        [Fact]
        public void EmptyAndNullHistoriesTeachNothing()
        {
            Assert.Null(ConnectPathPolicy.LearnFrom(null));
            Assert.Null(ConnectPathPolicy.LearnFrom(new List<ConnectionAttemptRecord>()));
        }

        // ------------------------------------------------------------------
        // Against the real store
        // ------------------------------------------------------------------

        [Fact]
        public void LearnForRadioReadsTheRecordedRing()
        {
            const string serial = "0123-4567-8600-0001";
            for (int i = 0; i < 3; i++)
            {
                ConnectionHistory.Record(serial, ConnectPathKind.SmartLink.ToString(),
                    ConnectPathPolicy.ConnectedOutcome, 900);
            }

            Assert.Equal(ConnectPathKind.SmartLink, ConnectPathPolicy.LearnForRadio(serial));
        }

        [Fact]
        public void LearnForRadioIsSilentForARadioWithNoHistory()
        {
            Assert.Null(ConnectPathPolicy.LearnForRadio("9999-9999-9999-9999"));
            Assert.Null(ConnectPathPolicy.LearnForRadio(""));
        }

        // ------------------------------------------------------------------
        // The local-only answer, which shares this store
        // ------------------------------------------------------------------

        [Fact]
        public void SmartLinkIntentRoundTrips()
        {
            var cfg = new RadioConfig { SmartLinkIntent = SmartLinkIntents.LocalOnly };
            Assert.True(cfg.Save(_dir, "8888-0001-6600-0001"));
            Assert.Equal(SmartLinkIntents.LocalOnly,
                RadioConfig.Load(_dir, "8888-0001-6600-0001").SmartLinkIntent);
        }

        [Fact]
        public void AConfigWrittenBeforeTheLocalOnlyAnswerExistedLoadsAsUndecided()
        {
            // The whole point of Undecided being zero: an install that
            // upgrades into this build has answered nothing, and must be
            // ASKED rather than assumed either way.
            var path = Path.Combine(_dir, "radios", "8888-0002", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Version>1</Version>\n" +
                "  <RadioId>8888-0002</RadioId>\n" +
                "  <Nickname>older rig</Nickname>\n" +
                "</RadioConfig>\n");

            Assert.Equal(SmartLinkIntents.Undecided,
                RadioConfig.Load(_dir, "8888-0002").SmartLinkIntent);
        }

        [Fact]
        public void AStoredChoiceStillWinsWhenTheTrendComesOffDisk()
        {
            // The end-to-end version of the first test: real store, real
            // history, real per-radio config.
            const string serial = "0123-4567-8600-0002";
            for (int i = 0; i < 4; i++)
            {
                ConnectionHistory.Record(serial, ConnectPathKind.SmartLink.ToString(),
                    ConnectPathPolicy.ConnectedOutcome, 800);
            }

            var cfg = RadioConfig.LoadForRadio(serial);
            cfg.PathChain = new List<ConnectPathKind>
            {
                ConnectPathKind.Local, ConnectPathKind.SmartLink,
            };
            Assert.True(cfg.SaveForRadio(serial));

            var reloaded = RadioConfig.LoadForRadio(serial);
            var chain = ConnectPathPolicy.Resolve(
                reloaded.PathChain,
                ConnectPathPolicy.LearnForRadio(serial),
                lanAvailable: false, wanAvailable: true, lastSeenRemote: true);

            Assert.Equal(ConnectPathKind.Local, chain[0]);
        }
    }
}

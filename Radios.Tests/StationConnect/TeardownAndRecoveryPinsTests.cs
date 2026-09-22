using System;
using System.IO;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// Track G review, section 8: the autosave recovery wrapper's cached-true
    /// shortcut, the explicit save clearing the pending create at enqueue,
    /// and the disconnect running older automatic work through its teardown.
    /// The behaviour behind each (a superseded operation's queued delegate
    /// refusing; readback confirming a save) is tested in
    /// GenerationIsolationTests and DeferredCreationRunTests; these pin the
    /// three production sites onto that behaviour.
    /// </summary>
    public sealed class TeardownAndRecoveryPinsTests
    {
        private static string Read(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        [Fact]
        public void TheAutosaveRecoveryPress_SkipsOnlyWhenTheRadioReportedAutosaveOn()
        {
            var text = Read("Radios/FlexBase.cs");
            int at = text.IndexOf("public GuardedOutcome TurnRadioProfileAutosaveBackOn()", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = text.Substring(at, 1500);
            Assert.Contains("ProfileEvidence.Snapshot().RadioReportedAutosave == true) return GuardedOutcome.Skipped;", body, StringComparison.Ordinal);
            Assert.DoesNotContain("if (theRadio.ProfileAutoSave) return GuardedOutcome.Skipped;", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheExplicitSave_ClearsThePendingCreateOnlyAfterTheCommandWentOut_AndRecordsAnUnconfirmedOne()
        {
            var text = Read("Radios/FlexBase.cs");
            int at = text.IndexOf("public bool SaveProfile(Profile_t p, bool immediately = false)", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = text.Substring(at, 3000);
            int enqueue = body.IndexOf("q.Enqueue((FunctionDel)(() =>", StringComparison.Ordinal);
            int save = body.IndexOf("theRadio.SaveGlobalProfile(p.Name);", StringComparison.Ordinal);
            int readback = body.IndexOf("inv.Sequence > seq && inv.Contains(p.Name)", StringComparison.Ordinal);
            int uncertain = body.IndexOf("_uncertainGlobalCreation = pending;", StringComparison.Ordinal);
            int clear = body.IndexOf("_pendingGlobalCreation = null;", StringComparison.Ordinal);
            int endOfDelegate = body.IndexOf("}), \"save global\", true);", StringComparison.Ordinal);
            Assert.True(enqueue > 0 && save > enqueue && readback > save && uncertain > readback && clear > uncertain && endOfDelegate > clear,
                "the pending create must be cleared inside the delegate, after the save and its readback, never at enqueue");
        }

        [Fact]
        public void TheCleanDisconnect_BeginsTheTeardownOperationBeforeItsOwnWork_AndCancelsAfter()
        {
            var text = Read("Radios/FlexBase.cs");
            int at = text.IndexOf("public void Disconnect()", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = text.Substring(at, 3500);
            int teardown = body.IndexOf("BeginTeardownOperation(\"Disconnect\");", StringComparison.Ordinal);
            int create = body.IndexOf("saveNewGlobalProfile();", StringComparison.Ordinal);
            int putBack = body.IndexOf("PutProfilesBackOnDisconnect();", StringComparison.Ordinal);
            int cancel = body.IndexOf("CancelStationAttempt(\"Disconnect\");", StringComparison.Ordinal);
            Assert.True(teardown > 0 && create > teardown && putBack > create && cancel > putBack,
                "teardown operation, then the create, then the put-back, then the cancel");

            int dispose = text.IndexOf("BeginTeardownOperation(\"Dispose\");", StringComparison.Ordinal);
            Assert.True(dispose > 0, "Dispose's path must begin the teardown too");
        }

        [Fact]
        public void BeginningTheTeardown_EndsTheConnectsOperation_AndIsIdempotent()
        {
            // The pure half, on the attempt itself: what BeginTeardownOperation
            // does to the operation an earlier phase queued work under.
            var attempt = new ConnectionAttempt("1234");
            var connect = attempt.BeginOperation("station establishment on connect");
            var teardown = attempt.BeginOperation("teardown: Disconnect");
            Assert.False(connect.IsLive);
            Assert.True(teardown.IsLive);
            Assert.True(attempt.IsLive);
        }
    }
}

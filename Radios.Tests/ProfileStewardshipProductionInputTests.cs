using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #563: pins the production facts supplied to ProfileStewardship, not
    /// only the planner's response after a test helper has supplied them.
    /// </summary>
    public sealed class ProfileStewardshipProductionInputTests
    {
        private const string FlexBase = "Radios/FlexBase.cs";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        private static string BracedBlock(string text, string marker)
        {
            int markerAt = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(markerAt >= 0, $"source marker was not found: {marker}");

            int open = text.IndexOf('{', markerAt);
            Assert.True(open >= 0, $"opening brace was not found after: {marker}");

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}') depth--;

                if (depth == 0)
                {
                    return text.Substring(open + 1, i - open - 1);
                }
            }

            Assert.Fail($"closing brace was not found after: {marker}");
            return "";
        }

        [Fact]
        public void StationPresenceIsUnknownUntilProductionEstablishesIt()
        {
            // ProfileStewardshipTests.Situation defaults this fact to true for
            // planner cases. Construct the production input directly so that
            // helper default can never conceal a fail-open production default.
            var situation = new ProfileSituation();

            Assert.False(situation.StationPresent,
                "an omitted station-presence fact must mean not established");
        }

        [Fact]
        public void TheSourceReaderFindsTheProductionMembersItWillInspect()
        {
            // Positive control: the two source-based tests below must fail if
            // the reader stops finding real members rather than pass vacuously.
            var text = Read(FlexBase);

            Assert.Contains("private void sliceAdded(Slice slc)", text, StringComparison.Ordinal);
            Assert.Contains("internal ProfileSituation ReadProfileSituation(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ThisMemberDoesNotExistInFlexBase", text, StringComparison.Ordinal);
        }

        [Fact]
        public void OnlyThisClientsSlicesEnterTheCountUsedForStationPresence()
        {
            // Another MultiFlex client's slices exist in the radio-wide list,
            // but sliceAdded must reject them before mutating mySlices, and
            // MyNumSlices must remain a count of that filtered list.
            var text = Read(FlexBase);
            var sliceAdded = BracedBlock(text, "private void sliceAdded(Slice slc)");
            var mineOnly = BracedBlock(sliceAdded, "if (myClient(slc.ClientHandle))");
            var myNumSlices = BracedBlock(text, "public int MyNumSlices");

            Assert.Contains("mySlices.Insert(pos, slc);", mineOnly, StringComparison.Ordinal);
            Assert.Contains("rv = mySlices.Count;", myNumSlices, StringComparison.Ordinal);
            Assert.DoesNotContain("theRadio.SliceList.Count", myNumSlices, StringComparison.Ordinal);
        }

        [Fact]
        public void ReadProfileSituationWiresStationPresenceToThisClientsSliceCount()
        {
            // Pin the production handoff, not merely the planner. A radio-wide
            // slice count would let somebody else's station suppress our own
            // profile load; omitting the assignment would silently use false.
            var method = BracedBlock(Read(FlexBase),
                "internal ProfileSituation ReadProfileSituation(");

            Assert.Contains("StationPresent = radio != null && MyNumSlices > 0,",
                method, StringComparison.Ordinal);
            Assert.DoesNotContain("StationPresent = radio != null && OtherNumSlices > 0,",
                method, StringComparison.Ordinal);
            Assert.DoesNotContain("StationPresent = radio != null && radio.SliceList.Count > 0,",
                method, StringComparison.Ordinal);
        }
    }
}

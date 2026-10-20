using System;
using System.IO;
using System.Reflection;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one process-wide profile-walk flag, shared by every class that
    /// drives either walking entry point. xUnit runs classes in parallel, and
    /// a class holding the flag on purpose (below) would otherwise refuse a
    /// restore capture another class expected to run.
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class ProfileWalkFlagCollection
    {
        public const string Name = "Profile walk in-flight flag";
    }

    /// <summary>
    /// #469: the profile comparison report walks every stored profile the way
    /// the restore capture does, and now carries the same guards: it takes
    /// the in-flight flag for its whole run and releases it on every exit,
    /// and it refuses while the radio is keyed before anything is read or
    /// loaded.
    ///
    /// <para>No radio under test, so no profile is ever actually loaded here.
    /// What these tests drive is the guard itself, through the real entry
    /// points: each walk is started while the OTHER one genuinely holds the
    /// flag, via <see cref="ProfileReporter.WalkHeldProbe"/>, which production
    /// code calls the moment it has taken the flag.</para>
    /// </summary>
    [Collection(ProfileWalkFlagCollection.Name)]
    public sealed class ComparisonReportGuardTests
    {
        private static FlexBase MakeRig()
            => new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        private static readonly FieldInfo Flag =
            typeof(ProfileReporter).GetField("_walkInFlight", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static void Key(FlexBase rig, bool keyed)
            => typeof(FlexBase).GetField("_Transmit", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(rig, keyed);

        // ------------------------------------------------------------------
        // One walk at a time, from either menu item, in either order
        // ------------------------------------------------------------------

        [Fact]
        public void A_walk_already_running_refuses_the_comparison_report_and_leaves_the_flag_with_its_owner()
        {
            // Red against 4d7cc805: the report ran straight through a held flag.
            Flag.SetValue(null, 1);
            try
            {
                Assert.Null(ProfileReporter.GenerateReport(MakeRig(), null, out var why));
                Assert.Equal(ProfileReporter.WalkRefusal.WalkAlreadyRunning, why);
                // The refuser must not release a flag it never took: that
                // would hand the running walk's protection to the next caller.
                Assert.True(ProfileReporter.WalkInProgress);
            }
            finally { Flag.SetValue(null, 0); }
        }

        [Fact]
        public void A_restore_capture_started_during_a_comparison_report_is_refused()
        {
            var rig = MakeRig();
            int held = 0;
            ProfileReporter.RestoreGradeExport nested = null;
            string nestedReport = "not attempted";
            ProfileReporter.WalkRefusal nestedWhy = ProfileReporter.WalkRefusal.None;
            ProfileReporter.WalkHeldProbe = who =>
            {
                if (who != "comparison report") return;
                held++;
                Assert.True(ProfileReporter.WalkInProgress);
                nested = ProfileReporter.GenerateRestoreGradeExport(rig);
                nestedReport = ProfileReporter.GenerateReport(rig, null, out nestedWhy);
            };
            try
            {
                Assert.NotNull(ProfileReporter.GenerateReport(rig));
            }
            finally { ProfileReporter.WalkHeldProbe = null; }

            Assert.Equal(1, held);
            Assert.Null(nested);
            Assert.Null(nestedReport);
            Assert.Equal(ProfileReporter.WalkRefusal.WalkAlreadyRunning, nestedWhy);
            Assert.False(ProfileReporter.WalkInProgress);
        }

        [Fact]
        public void A_comparison_report_started_during_a_restore_capture_is_refused()
        {
            var rig = MakeRig();
            int held = 0;
            string nestedReport = "not attempted";
            ProfileReporter.WalkRefusal nestedWhy = ProfileReporter.WalkRefusal.None;
            ProfileReporter.WalkHeldProbe = who =>
            {
                if (who != "restore capture") return;
                held++;
                nestedReport = ProfileReporter.GenerateReport(rig, null, out nestedWhy);
            };
            try
            {
                Assert.NotNull(ProfileReporter.GenerateRestoreGradeExport(rig));
            }
            finally { ProfileReporter.WalkHeldProbe = null; }

            Assert.Equal(1, held);
            Assert.Null(nestedReport);
            Assert.Equal(ProfileReporter.WalkRefusal.WalkAlreadyRunning, nestedWhy);
            Assert.False(ProfileReporter.WalkInProgress);
        }

        // ------------------------------------------------------------------
        // Keyed transmit refuses before anything else
        // ------------------------------------------------------------------

        [Fact]
        public void Keyed_transmit_refuses_the_report_before_the_flag_is_taken()
        {
            // Red against 4d7cc805: a keyed radio got the whole report, walk
            // included, because nothing on this path asked.
            var rig = MakeRig();
            Key(rig, true);
            bool held = false;
            ProfileReporter.WalkHeldProbe = who => held = true;
            try
            {
                Assert.Null(ProfileReporter.GenerateReport(rig, null, out var why));
                Assert.Equal(ProfileReporter.WalkRefusal.Transmitting, why);
            }
            finally { ProfileReporter.WalkHeldProbe = null; }

            // Refused before the flag: no walk was ever held, so nothing ran
            // that could load a profile, and nothing was left to release.
            Assert.False(held);
            Assert.False(ProfileReporter.WalkInProgress);

            // Unkeyed, the same rig is reported on — the refusal is about the
            // key, not the rig.
            Key(rig, false);
            Assert.NotNull(ProfileReporter.GenerateReport(rig));
        }

        [Fact]
        public void Transmit_is_named_as_the_refusal_even_while_another_walk_holds_the_flag()
        {
            // Transmit is asked first, so the operator hears the reason that
            // needs their hands (unkey) rather than "wait for the other walk",
            // and the held flag is not touched.
            var rig = MakeRig();
            Key(rig, true);
            Flag.SetValue(null, 1);
            try
            {
                Assert.Null(ProfileReporter.GenerateReport(rig, null, out var why));
                Assert.Equal(ProfileReporter.WalkRefusal.Transmitting, why);
                Assert.True(ProfileReporter.WalkInProgress);
            }
            finally { Flag.SetValue(null, 0); }
        }

        // ------------------------------------------------------------------
        // The flag is released on every exit path
        // ------------------------------------------------------------------

        [Fact]
        public void The_flag_is_released_after_an_ordinary_report_so_the_next_one_runs()
        {
            var rig = MakeRig();
            Assert.NotNull(ProfileReporter.GenerateReport(rig));
            Assert.False(ProfileReporter.WalkInProgress);
            // A leaked flag would refuse every later walk until restart,
            // including the restore capture before a real reset.
            Assert.NotNull(ProfileReporter.GenerateReport(rig));
            Assert.NotNull(ProfileReporter.GenerateRestoreGradeExport(rig));
            Assert.False(ProfileReporter.WalkInProgress);
        }

        [Fact]
        public void The_flag_is_released_when_the_report_throws_mid_walk()
        {
            var rig = MakeRig();
            ProfileReporter.WalkHeldProbe = who =>
            {
                if (who == "comparison report")
                    throw new InvalidOperationException("the radio went away mid-walk");
            };
            try
            {
                Assert.Throws<InvalidOperationException>(() => ProfileReporter.GenerateReport(rig));
            }
            finally { ProfileReporter.WalkHeldProbe = null; }

            Assert.False(ProfileReporter.WalkInProgress);
            Assert.NotNull(ProfileReporter.GenerateReport(rig));
        }
    }
}

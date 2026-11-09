using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #392: one owner for the radio object. Three symptoms of one question,
    /// fixed together as the entry insists ("do not fix these three
    /// separately, and do not patch from a call site"): a new radio object
    /// was assigned in four places with no check the previous one was
    /// disposed; openTheRadio's outer Catch returned without disposing; and
    /// Dispose set its flag last, so a throw part way through left a half
    /// torn-down object the finaliser ran again.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RigOwnershipTests
    {
        private static string Read(params string[] path) =>
            File.ReadAllText(Path.Combine(IntegrationPassTree.Root, Path.Combine(path))).Replace("\r\n", "\n");

        [Fact]
        public void Only_the_owner_makes_a_radio_object_and_it_disposes_the_old_one_first()
        {
            string g = Read("globals.vb");
            Assert.Equal(1, Regex.Matches(g, @"New FlexBase\(OpenParms\)").Count);
            var owner = Regex.Match(g, @"Private Function NewRigControl\(why As String\) As FlexBase(?<body>.*?)End Function", RegexOptions.Singleline);
            Assert.True(owner.Success);
            string body = owner.Groups["body"].Value;
            int release = body.IndexOf("ReleaseRigControl(why)", StringComparison.Ordinal);
            int assign = body.IndexOf("RigControl = New FlexBase(OpenParms)", StringComparison.Ordinal);
            Assert.True(release > 0 && release < assign);

            // The four former sites, through the owner.
            Assert.Contains("NewRigControl(\"startup auto-connect\")", g, StringComparison.Ordinal);
            Assert.Contains("NewRigControl(\"startup auto-connect, try again\")", g, StringComparison.Ordinal);
            Assert.Contains("NewRigControl(\"radio picker\")", g, StringComparison.Ordinal);
            Assert.Contains("NewRigControl(\"auto-connect retry\")", g, StringComparison.Ordinal);
        }

        [Fact]
        public void The_open_that_throws_lets_go_of_its_radio_object()
        {
            string g = Read("globals.vb");
            int fn = g.IndexOf("Private Function openTheRadioCore(initialCall As Boolean) As Boolean", StringComparison.Ordinal);
            int katch = g.IndexOf("Catch ex As Exception\n            Tracing.TraceLine(\"openTheRadio exception:\"", fn, StringComparison.Ordinal);
            int end = g.IndexOf("End Function", katch, StringComparison.Ordinal);
            Assert.True(fn > 0 && katch > fn && end > katch);
            string handler = g.Substring(katch, end - katch);
            int release = handler.IndexOf("ReleaseRigControl(\"openTheRadio exception\")", StringComparison.Ordinal);
            int ret = handler.IndexOf("Return False", StringComparison.Ordinal);
            Assert.True(release > 0 && release < ret);

            var releaseBody = Regex.Match(g, @"Private Sub ReleaseRigControl\(why As String\)(?<body>.*?)End Sub", RegexOptions.Singleline).Groups["body"].Value;
            Assert.Contains("old.Dispose()", releaseBody, StringComparison.Ordinal);
            Assert.Contains("RigControl = Nothing", releaseBody, StringComparison.Ordinal);
            Assert.Contains("WpfMainWindow.RigControl = Nothing", releaseBody, StringComparison.Ordinal);
        }

        /// <summary>
        /// The flag is claimed before the teardown, and the two steps that sat
        /// outside any containment are contained, so a throw cannot leave the
        /// object half torn down with the flag false.
        /// </summary>
        [Fact]
        public void Dispose_claims_its_flag_first_and_contains_every_step()
        {
            string f = Read("Radios", "FlexBase.cs");
            int guard = f.IndexOf("            if (!disposed)\n            {", StringComparison.Ordinal);
            Assert.True(guard > 0);
            string after = f.Substring(guard, 1600);
            int claim = after.IndexOf("disposed = true;", StringComparison.Ordinal);
            int firstStep = after.IndexOf("component.Dispose();", StringComparison.Ordinal);
            Assert.True(claim > 0 && claim < firstStep, "the flag must be claimed before the first teardown step");
            Assert.Equal(1, Regex.Matches(f, @"\bdisposed = true;").Count);
            Assert.Contains("try { API.CloseSession(); }", f, StringComparison.Ordinal);
            Assert.Contains("DisposeStepFailed(\"closing the field controls\", disposing, ex);", f, StringComparison.Ordinal);
        }

        /// <summary>Behaviour, on a radio object with no radio: a second Dispose finds nothing left to do and throws nothing.</summary>
        [Fact]
        public void A_second_dispose_is_harmless()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            rig.Dispose();
            var flag = typeof(FlexBase).GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.True((bool)flag.GetValue(rig)!);
            rig.Dispose();
            Assert.True((bool)flag.GetValue(rig)!);
        }
    }
}

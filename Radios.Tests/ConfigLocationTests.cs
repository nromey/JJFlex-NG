using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Where a run's settings come from, and the guards that stop the answer
    /// from being a surprise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until 2026-08-22 a SPAWNED build could not be pointed anywhere but the
    /// operator's one live <c>%AppData%\JJFlexRadio</c>. Tests could redirect
    /// it because they run in-process; a launched exe could not. So every
    /// instance, from every build in every worktree, read and wrote his real
    /// settings — and on 2026-08-21 a background agent's build rewrote his
    /// KeyDefs.xml, with no copy anywhere to compare against afterwards.
    /// </para>
    /// <para>
    /// The resolution lives in <see cref="RadioConfig.ResolveStartupDirectory"/>
    /// rather than inline in VB startup precisely so it can be tested here. A
    /// decision this consequential should not be reachable only by launching
    /// the program.
    /// </para>
    /// </remarks>
    public sealed class ConfigLocationTests
    {
        private const string Normal = @"C:\Users\someone\AppData\Roaming\JJFlexRadio";

        [Fact]
        public void NoOverrideMeansTheNormalFolder()
        {
            string result = RadioConfig.ResolveStartupDirectory(Normal, null, out bool temp, out string? refusal);

            Assert.Equal(Normal, result);
            Assert.False(temp);
            Assert.Null(refusal);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void AnEmptyOverrideIsNotAnOverride(string value)
        {
            // An environment variable that exists but is blank is how a shell
            // script says "unset" by accident. It must read as absence, not as
            // a request to relocate to nowhere.
            string result = RadioConfig.ResolveStartupDirectory(Normal, value, out bool temp, out string? refusal);

            Assert.Equal(Normal, result);
            Assert.False(temp);
            Assert.Null(refusal);
        }

        [Fact]
        public void AnAbsolutePathRelocatesTheWholeTree()
        {
            string target = Path.Combine(Path.GetTempPath(), "jjflex-test-config");

            string result = RadioConfig.ResolveStartupDirectory(Normal, target, out bool temp, out string? refusal);

            Assert.True(temp);
            Assert.Null(refusal);
            Assert.Equal(Path.GetFullPath(target), result);
        }

        [Fact]
        public void SurroundingWhitespaceIsForgiven()
        {
            string target = Path.Combine(Path.GetTempPath(), "jjflex-test-config");

            string result = RadioConfig.ResolveStartupDirectory(
                Normal, "  " + target + "  ", out bool temp, out string? refusal);

            Assert.True(temp);
            Assert.Equal(Path.GetFullPath(target), result);
            Assert.Null(refusal);
        }

        [Fact]
        public void ARelativePathIsRefusedAndSaidOutLoud()
        {
            // A relative path resolves against the working directory, which for
            // a spawned process is wherever the launcher happened to be. That
            // is not a location, it is a guess — and guessing here means
            // scattering settings trees around the disk.
            string result = RadioConfig.ResolveStartupDirectory(
                Normal, @"temp\config", out bool temp, out string? refusal);

            Assert.Equal(Normal, result);
            Assert.False(temp);
            Assert.NotNull(refusal);
            Assert.Contains("relative", refusal!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PointingTheOverrideAtTheRealFolderIsRefused()
        {
            // The dangerous case. Allowing it would report "temporary settings
            // in use" while writing the operator's live tree — a run that
            // believes it is isolated and is not. Refusing is the only honest
            // answer.
            string result = RadioConfig.ResolveStartupDirectory(
                Normal, Normal, out bool temp, out string? refusal);

            Assert.Equal(Normal, result);
            Assert.False(temp);
            Assert.NotNull(refusal);
        }

        [Fact]
        public void PointingAtTheRealFolderIsRefusedRegardlessOfCasingOrTrailingSlash()
        {
            string result = RadioConfig.ResolveStartupDirectory(
                Normal, Normal.ToUpperInvariant() + @"\", out bool temp, out string? refusal);

            Assert.False(temp);
            Assert.NotNull(refusal);
            Assert.Equal(Normal, result);
        }

        [Fact]
        public void ARefusalAlwaysFallsBackToSomethingUsable()
        {
            // Whatever goes wrong, the app still has a settings directory. A
            // bad environment variable must never leave the program with
            // nowhere to read from — it degrades to normal, loudly.
            foreach (string bad in new[] { @"rel\path", "?<>|", "  " })
            {
                string result = RadioConfig.ResolveStartupDirectory(
                    Normal, bad, out bool temp, out string? _);

                Assert.False(string.IsNullOrEmpty(result));
                Assert.Equal(Normal, result);
                Assert.False(temp);
            }
        }

        [Fact]
        public void TheVariableIsNamedWhereBothSidesCanSeeIt()
        {
            // globals.vb reads the environment using this constant, and the
            // runner sets it by the same name. A literal on either side is how
            // the two quietly stop agreeing.
            Assert.Equal("JJFLEX_CONFIG_DIR", RadioConfig.ConfigDirOverrideVariable);
        }
    }
}

using System;
using System.IO;
using Radios;
using Radios.Fixer.Evidence;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The floor, checked. Every settings root this process can reach points at
    /// a throwaway tree, and none of them points at the operator's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Task #232.</b> <see cref="TestSettingsRoot"/> is a module
    /// initializer, which means it either ran before everything else or it did
    /// not run at all — and nothing downstream would notice the difference,
    /// because every one of these statics SELF-HEALS to the live
    /// <c>%AppData%\JJFlexRadio</c> when it is empty. A test that never binds
    /// them does not fail; it quietly reads, and can write, the machine's real
    /// configuration, and its result then depends on what is sitting in that
    /// folder today.
    /// </para>
    /// <para>
    /// <b>Two roots, and only one of them used to be bound.</b>
    /// <c>RadioConfig.BaseDirectory</c> was; <c>RadioConfig.AppDataRoot</c> was
    /// not, and it is the root under which the Fixer's run store, the output
    /// transcripts, the connection profiles, the profile reports and the
    /// SmartLink account file all live. Loading
    /// <c>SmartLinkAccountManager</c> — which
    /// <c>SmartLinkAccountPortTests</c> does, for two pure string checks — was
    /// enough to bind the live path into a <c>static readonly</c> field for the
    /// rest of the run.
    /// </para>
    /// <para>
    /// In the collection because it READS the shared statics: outside it, a
    /// scope taken by another class would be in force while these assertions
    /// ran and they would describe that class's directory instead.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TestSettingsRootTests
    {
        /// <summary>Where the operator's real settings live on this machine.</summary>
        private static string LiveSettingsFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JJFlexRadio");

        [Fact]
        public void TheThrowawayTreeIsBoundAndSaysSoIfItIsNot()
        {
            Assert.Null(TestSettingsRoot.Failure);
            Assert.False(string.IsNullOrEmpty(TestSettingsRoot.Directory));
            Assert.True(Directory.Exists(TestSettingsRoot.Directory),
                "TestSettingsRoot named '" + TestSettingsRoot.Directory +
                "' but nothing is there, so nothing was ever isolated.");
        }

        [Fact]
        public void TheThrowawayTreeIsNotTheOperatorsSettingsFolder()
        {
            // The positive control for every comparison below. If these two
            // paths were ever the same string, "AppDataRoot is the throwaway
            // tree" would pass while pointing at the live folder — the exact
            // shape of pass this file exists to make impossible.
            Assert.NotEqual(
                Path.TrimEndingDirectorySeparator(LiveSettingsFolder),
                Path.TrimEndingDirectorySeparator(TestSettingsRoot.Directory),
                StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void BothSettingsRootsResolveInsideTheThrowawayTree()
        {
            AssertInsideTheThrowawayTree("RadioConfig.BaseDirectory", RadioConfig.BaseDirectory);
            AssertInsideTheThrowawayTree("RadioConfig.ResolvedBaseDirectory", RadioConfig.ResolvedBaseDirectory);
            AssertInsideTheThrowawayTree("RadioConfig.AppDataRoot", RadioConfig.AppDataRoot);
            AssertInsideTheThrowawayTree("KnownRadioRoster.CacheDirectory", KnownRadioRoster.CacheDirectory);
            AssertInsideTheThrowawayTree("Lexicon.OverlayDirectoryOverride", Lexicon.OverlayDirectoryOverride);
        }

        [Fact]
        public void AStoreThatResolvesFromAppDataRootLandsInTheThrowawayTree()
        {
            // Naming a real store rather than re-asserting the property. The
            // property being right and every store still writing the live
            // folder is precisely what happened one layer down on 2026-08-22,
            // when twenty places built the path themselves; a root that no
            // store actually goes through proves nothing about where files go.
            AssertInsideTheThrowawayTree("FixerRunStore.Default().Root", FixerRunStore.Default().Root);
        }

        private static void AssertInsideTheThrowawayTree(string what, string? actual)
        {
            Assert.False(string.IsNullOrEmpty(actual),
                what + " is empty, so it will self-heal to the operator's live settings folder.");

            string full = Path.GetFullPath(actual!);
            string root = Path.GetFullPath(TestSettingsRoot.Directory);

            Assert.True(
                full.StartsWith(root, StringComparison.OrdinalIgnoreCase),
                what + " is '" + full + "', which is outside the throwaway tree '" + root +
                "'. Tests are reading — and can write — a directory this process does not own; " +
                "if that is " + LiveSettingsFolder + " it is the operator's live configuration. " +
                "See task #232.");
        }
    }
}

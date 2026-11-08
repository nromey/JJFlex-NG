using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track E repair round 1, B7: the four noise levels the radio
    /// never reports. FlexLib's setters for them drop a value equal to their
    /// own cached field; the write path sends the command itself in exactly
    /// that case. Without a radio the queue never runs, so what is tested is
    /// the decision, and that its command is the one FlexLib's own setter
    /// would send, read from the vendored source. The command reaching the
    /// wire is the bench's.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class HeldNoiseLevelWriteTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(HeldNoiseLevelWriteTests));
        public HeldNoiseLevelWriteTests() => Lexicon.Load(Lexicon.Partitions);
        public void Dispose() => _scope.Dispose();

        [Theory]
        [InlineData(HeldNoiseLevel.LegacyNoiseReduction, "lms_nr_level")]
        [InlineData(HeldNoiseLevel.SpectralNoiseReduction, "speex_nr_level")]
        [InlineData(HeldNoiseLevel.NoiseReductionFilter, "nrf_level")]
        [InlineData(HeldNoiseLevel.LegacyAutoNotch, "lms_anf_level")]
        public void A_held_level_equal_to_FlexLibs_cache_is_sent_directly_and_any_other_goes_through_its_setter(HeldNoiseLevel which, string key)
        {
            // Codex's B7: a saved 0 on a new slice, whose cache starts at 0.
            Assert.Equal($"slice set 2 {key}=0", FlexBase.HeldNoiseLevelDirectCommand(2, which, cached: 0, wanted: 0));
            Assert.Equal($"slice set 0 {key}=35", FlexBase.HeldNoiseLevelDirectCommand(0, which, cached: 35, wanted: 35));
            // FlexLib's clamp: 150 is 100 to it, so a cache of 100 drops it.
            Assert.Equal($"slice set 1 {key}=100", FlexBase.HeldNoiseLevelDirectCommand(1, which, cached: 100, wanted: 150));
            // Different from the cache: FlexLib's setter sends it, and keeps its cache honest.
            Assert.Null(FlexBase.HeldNoiseLevelDirectCommand(1, which, cached: 0, wanted: 40));
        }

        [Theory]
        [InlineData(HeldNoiseLevel.LegacyNoiseReduction, "public int NRL_Level", "_nrl_level")]
        [InlineData(HeldNoiseLevel.SpectralNoiseReduction, "public int NRSLevel", "_nrs_level")]
        [InlineData(HeldNoiseLevel.NoiseReductionFilter, "public int NRFLevel", "_nrf_level")]
        [InlineData(HeldNoiseLevel.LegacyAutoNotch, "public int ANFL_Level", "_anfl_level")]
        public void The_direct_command_is_the_one_FlexLibs_own_setter_sends_and_its_setter_drops_an_equal_value(
            HeldNoiseLevel which, string property, string field)
        {
            string slice = File.ReadAllText(Path.Combine(RepoRoot(), "FlexLib_API", "FlexLib", "Slice.cs"));
            int at = slice.IndexOf(property, StringComparison.Ordinal);
            Assert.True(at >= 0, property + " not found in the vendored Slice.cs");
            string setter = slice.Substring(at, Math.Min(900, slice.Length - at));
            // The premise of the repair: the setter returns early on its own cache.
            Assert.Contains($"if (value == {field}) return;", setter);
            // And the command it sends is the key this path sends.
            Assert.Contains($"_radio.SendCommand($\"slice set {{_index}} {FlexBase.HeldNoiseLevelWireKey(which)}={{{field}}}\");", setter);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}

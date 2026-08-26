using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 35 Track I, task #226: the per-radio memory of where the
    /// operator left the radio — the app-side half of "a normal radio comes
    /// back on the frequency you left it".
    ///
    /// <para>Three things worth pinning down. The upgrade guarantee: a
    /// config.xml written before these fields existed must load as "no last
    /// place known", so nothing announces a place that was never recorded.
    /// The skip-when-unchanged contract: an evening parked on one frequency
    /// must not rewrite the file per debounce tick — and critically, the skip
    /// must compare ALL of frequency, mode and slice letter, because a mode
    /// change on the same frequency is still a different place. And the
    /// refusal of empty observations: a half-known place (no frequency, no
    /// mode) is worse than none, because the connect announcement would speak
    /// a fragment.</para>
    ///
    /// <para>Shares the statics collection because it writes
    /// <see cref="RadioConfig.BaseDirectory"/>.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LastPlaceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _savedBase;

        public LastPlaceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "jjflex-lastplace-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _savedBase = RadioConfig.BaseDirectory;
            RadioConfig.BaseDirectory = _dir;
        }

        public void Dispose()
        {
            RadioConfig.BaseDirectory = _savedBase;
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        }

        // ------------------------------------------------------------------
        // The safe default and the upgrade guarantee
        // ------------------------------------------------------------------

        [Fact]
        public void NewConfig_HasNoLastPlace()
        {
            var cfg = new RadioConfig();
            Assert.False(cfg.LastPlaceKnown);
            Assert.Equal(0UL, cfg.LastPlaceFrequencyHz);
            Assert.Equal("", cfg.LastPlaceMode);
        }

        [Fact]
        public void ConfigWrittenBeforeLastPlaceExisted_LoadsAsUnknown()
        {
            // The shape an older install has on disk: no LastPlace elements.
            var dir = Path.Combine(_dir, "radios", "1234-5678-9012-3456");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Version>1</Version>\n" +
                "  <RadioId>1234-5678-9012-3456</RadioId>\n" +
                "  <Nickname>Bench8600</Nickname>\n" +
                "</RadioConfig>\n");

            var cfg = RadioConfig.LoadForRadio("1234-5678-9012-3456");
            Assert.False(cfg.LastPlaceKnown);
        }

        // ------------------------------------------------------------------
        // Recording
        // ------------------------------------------------------------------

        [Fact]
        public void RecordLastPlace_RoundTrips()
        {
            RadioConfig.RecordLastPlace("1111-2222-3333-4444", 14_243_000UL, "USB", "A");

            var cfg = RadioConfig.LoadForRadio("1111-2222-3333-4444");
            Assert.True(cfg.LastPlaceKnown);
            Assert.Equal(14_243_000UL, cfg.LastPlaceFrequencyHz);
            Assert.Equal("USB", cfg.LastPlaceMode);
            Assert.Equal("A", cfg.LastPlaceSliceLetter);
            Assert.NotEqual(default, cfg.LastPlaceRecordedUtc);
        }

        [Fact]
        public void RecordLastPlace_SkipsTheWriteWhenNothingChanged()
        {
            const string id = "5555-6666-7777-8888";
            RadioConfig.RecordLastPlace(id, 7_188_000UL, "LSB", "A");
            var file = Path.Combine(_dir, "radios", id, "config.xml");
            var firstWrite = File.GetLastWriteTimeUtc(file);

            RadioConfig.RecordLastPlace(id, 7_188_000UL, "LSB", "A");
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(file));
        }

        [Fact]
        public void RecordLastPlace_SameFrequencyDifferentMode_IsADifferentPlace()
        {
            const string id = "5555-6666-7777-9999";
            RadioConfig.RecordLastPlace(id, 14_070_000UL, "USB", "A");
            RadioConfig.RecordLastPlace(id, 14_070_000UL, "DIGU", "A");

            var cfg = RadioConfig.LoadForRadio(id);
            Assert.Equal("DIGU", cfg.LastPlaceMode);
        }

        [Fact]
        public void RecordLastPlace_RefusesEmptyObservations()
        {
            const string id = "0000-1111-2222-3333";
            RadioConfig.RecordLastPlace(id, 0UL, "USB", "A");
            RadioConfig.RecordLastPlace(id, 14_000_000UL, "", "A");
            RadioConfig.RecordLastPlace("", 14_000_000UL, "USB", "A");

            Assert.False(RadioConfig.LoadForRadio(id).LastPlaceKnown);
        }

        [Fact]
        public void RecordLastPlace_DoesNotDisturbTheRestOfTheConfig()
        {
            const string id = "9999-8888-7777-6666";
            var cfg = RadioConfig.LoadForRadio(id);
            cfg.UserNickname = "The remote base";
            cfg.Ownership = RadioOwnership.Mine;
            Assert.True(cfg.SaveForRadio(id));

            RadioConfig.RecordLastPlace(id, 3_573_000UL, "DIGU", "B");

            var reloaded = RadioConfig.LoadForRadio(id);
            Assert.Equal("The remote base", reloaded.UserNickname);
            Assert.Equal(RadioOwnership.Mine, reloaded.Ownership);
            Assert.True(reloaded.LastPlaceKnown);
            Assert.Equal(3_573_000UL, reloaded.LastPlaceFrequencyHz);
        }
    }
}

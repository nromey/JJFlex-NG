using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Hear Yourself comparison box ("Tell me how my sound changed compared
    /// to my last take") remembers its tick per radio (listen-back round 14,
    /// Noel 2026-10-21 18:55: "I'd have it remember its setting yes").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned here: the default is OFF, so an operator who never touches it
    /// hears exactly what they heard before this shipped; a config written
    /// before the field existed reads as off rather than failing to load; the
    /// round trip through the per-radio store; and that saving it disturbs
    /// nothing else in the file. The dialog's half (that ticking the box calls
    /// the save, and SetRig reads it back) is WPF and needs a press on the
    /// bench; nothing desk-safe can construct the Audio Workshop.
    /// </para>
    /// <para>
    /// These tests do not compile against the base commit (`e0815a16`): the
    /// property did not exist. That is the red run for this step.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ListenbackComparisonChoiceTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope;
        private string Dir => _scope.Directory;

        public ListenbackComparisonChoiceTests()
        {
            _scope = new RadioConfigStaticsScope(nameof(ListenbackComparisonChoiceTests));
        }

        public void Dispose() => _scope.Dispose();

        [Fact]
        public void NewConfig_DoesNotCompareUnlessAsked()
        {
            Assert.False(new RadioConfig().AudioCheckCompareWithLastTake);
        }

        [Fact]
        public void ConfigWrittenBeforeThisShipped_ReadsAsOff()
        {
            const string id = "1234-5678-9012-3456";
            var dir = Path.Combine(Dir, "radios", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<RadioConfig><Version>1</Version><RadioId>" + id + "</RadioId>" +
                "<AudioCheckListenMethod>OffAir</AudioCheckListenMethod></RadioConfig>");

            var cfg = RadioConfig.LoadForRadio(id);
            Assert.Equal(AudioCheckListenMethods.OffAir, cfg.AudioCheckListenMethod);
            Assert.False(cfg.AudioCheckCompareWithLastTake);
        }

        [Fact]
        public void TheTick_RoundTripsThroughThePerRadioStore_AndDisturbsNothingElse()
        {
            const string id = "9999-8888-7777-6666";
            var cfg = RadioConfig.LoadForRadio(id);
            cfg.UserNickname = "The remote base";
            cfg.AudioCheckListenMethod = AudioCheckListenMethods.OffAir;
            cfg.AudioCheckLowPowerWatts = 3;
            Assert.True(cfg.SaveForRadio(id));

            var again = RadioConfig.LoadForRadio(id);
            again.AudioCheckCompareWithLastTake = true;
            Assert.True(again.SaveForRadio(id));

            var reloaded = RadioConfig.LoadForRadio(id);
            Assert.True(reloaded.AudioCheckCompareWithLastTake);
            Assert.Equal("The remote base", reloaded.UserNickname);
            Assert.Equal(AudioCheckListenMethods.OffAir, reloaded.AudioCheckListenMethod);
            Assert.Equal(3, reloaded.AudioCheckLowPowerWatts);

            // A second radio is a second file: the tick does not leak across serials.
            Assert.False(RadioConfig.LoadForRadio("1111-2222-3333-4444").AudioCheckCompareWithLastTake);
        }
    }
}

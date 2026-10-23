using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RadioConfigReadFailureTests : IDisposable
    {
        private const string Serial = "1234-5678-6300-0001";
        private readonly RadioConfigStaticsScope _scope = new(nameof(RadioConfigReadFailureTests));

        public void Dispose() => _scope.Dispose();

        [Fact]
        public void UnreadableConfigCannotBeReplacedByDefaults()
        {
            string path = ConfigPath();
            byte[] original = { 0x3c, 0x62, 0x72, 0x6f, 0x6b, 0x65, 0x6e, 0x3e };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, original);

            var config = RadioConfig.LoadForRadio(Serial);
            Assert.NotNull(config.ReadFailure);
            config.Nickname = "replacement";

            Assert.False(config.SaveForRadio(Serial));
            Assert.Equal(original, File.ReadAllBytes(path));
        }

        [Fact]
        public void MissingConfigGetsDefaultsAndCanBeSaved()
        {
            Assert.False(File.Exists(ConfigPath()));
            var config = RadioConfig.LoadForRadio(Serial);
            Assert.Null(config.ReadFailure);
            Assert.Equal("", config.Nickname);
            config.Nickname = "first sighting";

            Assert.True(config.SaveForRadio(Serial));
            Assert.Equal("first sighting", RadioConfig.LoadForRadio(Serial).Nickname);
        }

        [Fact]
        public void ReadableExistingConfigCanStillBeSaved()
        {
            Assert.True(new RadioConfig { Nickname = "original" }.SaveForRadio(Serial));
            var config = RadioConfig.LoadForRadio(Serial);
            Assert.Null(config.ReadFailure);
            config.Nickname = "updated";

            Assert.True(config.SaveForRadio(Serial));
            Assert.Equal("updated", RadioConfig.LoadForRadio(Serial).Nickname);
        }

        [Fact]
        public void UnreadableProfileIntentIsNotReportedAsUnanswered()
        {
            WriteInvalidConfig();

            Assert.NotEqual(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(Serial));
        }

        [Fact]
        public void SightingReportsWhenItsSaveIsDeclined()
        {
            WriteInvalidConfig();

            Assert.False(KnownRadioRoster.RecordSighting(
                Serial, "club", "FLEX-6300", isRemote: false, accountEmail: ""));
        }

        private string ConfigPath() => Path.Combine(_scope.Directory, "radios", Serial, "config.xml");

        private void WriteInvalidConfig()
        {
            string path = ConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "<broken>");
        }
    }
}

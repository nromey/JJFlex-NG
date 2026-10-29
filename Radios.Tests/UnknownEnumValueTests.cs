using System;
using System.IO;
using System.Linq;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #847: a per-radio config.xml written by a newer build, holding an enum
    /// value this build does not know, used to fail to read as a whole, so
    /// every per-radio fact was lost for the run, the ownership answer among
    /// them. Now one unknown value degrades that ONE field to its default,
    /// the rest of the file loads, and the unknown value is written back
    /// unchanged when the file is saved.
    /// </summary>
    public sealed class UnknownEnumValueTests : IDisposable
    {
        private const string RadioId = "0000-1111-8600-2222";
        private const int Sentinel = 4999;

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "jjflex-847-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* a temp folder left behind harms nothing */ }
        }

        private string ConfigPath => Path.Combine(_dir, "radios", RadioId, "config.xml");

        /// <summary>The shape seen 2026-10-29, plus every other enum field set to a known value.</summary>
        private void WriteNewerBuildsConfig(string listenMethod = "Loopback", string pathKind = "Connect",
                                            string mute = "Muted")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath,
                "<?xml version=\"1.0\"?>\r\n"
                + "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n"
                + "  <Version>1</Version>\r\n"
                + "  <RadioId>" + RadioId + "</RadioId>\r\n"
                + "  <Nickname>Bench</Nickname>\r\n"
                + "  <PathChain>\r\n"
                + "    <ConnectPathKind>SmartLink</ConnectPathKind>\r\n"
                + "    <ConnectPathKind>" + pathKind + "</ConnectPathKind>\r\n"
                + "  </PathChain>\r\n"
                + "  <FixedHolePunchPort>" + Sentinel + "</FixedHolePunchPort>\r\n"
                + "  <SmartLinkIntent>LocalOnly</SmartLinkIntent>\r\n"
                + "  <AudioCheckListenMethod>" + listenMethod + "</AudioCheckListenMethod>\r\n"
                + "  <LastStationLayoutSlices>\r\n"
                + "    <SliceLayoutEntry><FreqHz>14200000</FreqHz><Mode>USB</Mode><Letter>A</Letter><Mute>" + mute + "</Mute></SliceLayoutEntry>\r\n"
                + "  </LastStationLayoutSlices>\r\n"
                + "  <Ownership>Mine</Ownership>\r\n"
                + "</RadioConfig>\r\n");
        }

        [Fact]
        public void An_unknown_value_loads_as_that_fields_default_with_every_other_field_intact()
        {
            WriteNewerBuildsConfig();

            var cfg = RadioConfig.Load(_dir, RadioId);

            Assert.Null(cfg.ReadFailure);
            Assert.Equal(new RadioConfig().AudioCheckListenMethod, cfg.AudioCheckListenMethod);
            // Everything else in the file, the ownership answer above all.
            Assert.Equal(RadioOwnership.Mine, cfg.Ownership);
            Assert.Equal(Sentinel, cfg.FixedHolePunchPort);
            Assert.Equal(SmartLinkIntents.LocalOnly, cfg.SmartLinkIntent);
            Assert.Equal("Bench", cfg.Nickname);
            Assert.Equal(new[] { ConnectPathKind.SmartLink }, cfg.PathChain.ToArray());
            Assert.Equal(SliceMuteState.Muted, Assert.Single(cfg.LastStationLayoutSlices).Mute);
        }

        [Fact]
        public void The_unknown_value_survives_a_save()
        {
            WriteNewerBuildsConfig();
            var cfg = RadioConfig.Load(_dir, RadioId);

            Assert.True(cfg.Save(_dir, RadioId));

            string written = File.ReadAllText(ConfigPath);
            Assert.Contains("<AudioCheckListenMethod>Loopback</AudioCheckListenMethod>", written, StringComparison.Ordinal);
            Assert.Contains("<ConnectPathKind>Connect</ConnectPathKind>", written, StringComparison.Ordinal);
            Assert.Contains("<Ownership>Mine</Ownership>", written, StringComparison.Ordinal);
        }

        [Fact]
        public void The_unknown_value_survives_an_Update_that_changes_another_field()
        {
            // The read-modify-write path every per-radio fact goes through. It
            // used to refuse outright, which kept the file safe and lost the
            // change; now the change lands and the newer value stays.
            WriteNewerBuildsConfig();

            Assert.True(RadioConfig.Update(_dir, RadioId, c => { c.Nickname = "Renamed"; }));

            string written = File.ReadAllText(ConfigPath);
            Assert.Contains("<AudioCheckListenMethod>Loopback</AudioCheckListenMethod>", written, StringComparison.Ordinal);
            Assert.Contains("<Nickname>Renamed</Nickname>", written, StringComparison.Ordinal);
        }

        [Fact]
        public void A_value_this_build_sets_replaces_the_unknown_one()
        {
            WriteNewerBuildsConfig();
            var cfg = RadioConfig.Load(_dir, RadioId);

            cfg.AudioCheckListenMethod = AudioCheckListenMethods.Monitor;
            cfg.PathChain = new() { ConnectPathKind.Local };
            Assert.True(cfg.Save(_dir, RadioId));

            string written = File.ReadAllText(ConfigPath);
            Assert.DoesNotContain("Loopback", written, StringComparison.Ordinal);
            Assert.DoesNotContain(">Connect<", written, StringComparison.Ordinal);
            Assert.Equal(AudioCheckListenMethods.Monitor, RadioConfig.Load(_dir, RadioId).AudioCheckListenMethod);
        }

        [Fact]
        public void Known_values_are_written_exactly_as_before()
        {
            // Positive control on the wire shape: a file this build writes
            // from known values reads in an older build, element for element.
            var cfg = new RadioConfig
            {
                RadioId = RadioId,
                Ownership = RadioOwnership.SomeoneElses,
                SmartLinkIntent = SmartLinkIntents.WantsSmartLink,
                PathChain = new() { ConnectPathKind.Local, ConnectPathKind.SmartLink },
            };
            Assert.True(cfg.Save(_dir, RadioId));

            string written = File.ReadAllText(ConfigPath);
            Assert.Contains("<Ownership>SomeoneElses</Ownership>", written, StringComparison.Ordinal);
            Assert.Contains("<SmartLinkIntent>WantsSmartLink</SmartLinkIntent>", written, StringComparison.Ordinal);
            Assert.Contains("<PathChain>", written, StringComparison.Ordinal);
            Assert.Contains("<ConnectPathKind>Local</ConnectPathKind>", written, StringComparison.Ordinal);
            Assert.DoesNotContain("Xml>", written, StringComparison.Ordinal);

            var back = RadioConfig.Load(_dir, RadioId);
            Assert.Equal(RadioOwnership.SomeoneElses, back.Ownership);
            Assert.Equal(new[] { ConnectPathKind.Local, ConnectPathKind.SmartLink }, back.PathChain.ToArray());
        }

        /// <summary>
        /// Every enum-typed setting RadioConfig saves goes through the tolerant
        /// reader, so the next enum added cannot bring the whole-file failure
        /// back: an unknown name in ANY of them leaves the rest of the file read.
        /// </summary>
        [Fact]
        public void No_enum_setting_is_saved_as_a_bare_enum()
        {
            var bare = typeof(RadioConfig).GetProperties()
                .Where(p => p.CanRead && p.CanWrite && p.GetSetMethod() != null
                            && !p.IsDefined(typeof(System.Xml.Serialization.XmlIgnoreAttribute), false))
                .Where(p => IsEnumish(p.PropertyType))
                .Select(p => p.Name)
                .ToList();
            Assert.True(bare.Count == 0,
                "These RadioConfig settings would fail the whole file on an unknown value: "
                + string.Join(", ", bare));

            var bareEntry = typeof(SliceLayoutEntry).GetProperties()
                .Where(p => p.CanWrite && p.GetSetMethod() != null
                            && !p.IsDefined(typeof(System.Xml.Serialization.XmlIgnoreAttribute), false))
                .Where(p => IsEnumish(p.PropertyType))
                .Select(p => p.Name)
                .ToList();
            Assert.Empty(bareEntry);
        }

        private static bool IsEnumish(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t.IsEnum) return true;
            if (t.IsArray) return IsEnumish(t.GetElementType()!);
            return t.IsGenericType && t.GetGenericArguments().Any(IsEnumish);
        }
    }
}

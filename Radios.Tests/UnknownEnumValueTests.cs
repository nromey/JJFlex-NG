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

        /// <summary>
        /// The shape seen 2026-10-29: an unknown listen method and an unknown
        /// path kind, beside known ownership, intent and slice mute. The other
        /// scalar enum fields are absent here; each one gets its own unknown
        /// token in <see cref="Each_enum_setting_keeps_its_own_unknown_value_and_the_rest_of_the_file"/>.
        /// </summary>
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
        /// Every scalar enum setting, and the nested slice mute, one at a time:
        /// an unknown token in THAT field reads as its default, every other
        /// field in the file is read, and saving gives the token back.
        /// </summary>
        [Theory]
        [InlineData(nameof(RadioConfig.ConnectionPreference))]
        [InlineData(nameof(RadioConfig.RemOnOnConnect))]
        [InlineData(nameof(RadioConfig.SmartLinkIntent))]
        [InlineData(nameof(RadioConfig.AudioCheckListenMethod))]
        [InlineData(nameof(RadioConfig.AudioCheckPowerMode))]
        [InlineData(nameof(RadioConfig.PcAudioOnConnect))]
        [InlineData(nameof(RadioConfig.Ownership))]
        [InlineData(nameof(RadioConfig.ProfileIntent))]
        [InlineData(nameof(SliceLayoutEntry.Mute))]
        public void Each_enum_setting_keeps_its_own_unknown_value_and_the_rest_of_the_file(string field)
        {
            const string token = "FromANewerBuild";
            bool nested = field == nameof(SliceLayoutEntry.Mute);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath,
                "<?xml version=\"1.0\"?>\r\n"
                + "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n"
                + "  <Version>1</Version>\r\n"
                + "  <RadioId>" + RadioId + "</RadioId>\r\n"
                + "  <Nickname>Bench</Nickname>\r\n"
                + "  <FixedHolePunchPort>" + Sentinel + "</FixedHolePunchPort>\r\n"
                + (nested ? "" : "  <" + field + ">" + token + "</" + field + ">\r\n")
                + "  <LastStationLayoutSlices>\r\n"
                + "    <SliceLayoutEntry><FreqHz>7100000</FreqHz><Mode>LSB</Mode><Letter>B</Letter><Mute>"
                + (nested ? token : "Unmuted") + "</Mute></SliceLayoutEntry>\r\n"
                + "  </LastStationLayoutSlices>\r\n"
                + (field == nameof(RadioConfig.Ownership) ? "" : "  <Ownership>Mine</Ownership>\r\n")
                + "</RadioConfig>\r\n");

            var cfg = RadioConfig.Load(_dir, RadioId);

            Assert.Null(cfg.ReadFailure);
            Assert.Equal(Sentinel, cfg.FixedHolePunchPort);
            Assert.Equal("Bench", cfg.Nickname);
            var slice = Assert.Single(cfg.LastStationLayoutSlices);
            Assert.Equal(7100000L, slice.FreqHz);
            if (nested)
                Assert.Equal(new SliceLayoutEntry().Mute, slice.Mute);
            else
            {
                Assert.Equal(SliceMuteState.Unmuted, slice.Mute);
                var property = typeof(RadioConfig).GetProperty(field)!;
                Assert.Equal(property.GetValue(new RadioConfig()), property.GetValue(cfg));
            }
            if (field != nameof(RadioConfig.Ownership))
                Assert.Equal(RadioOwnership.Mine, cfg.Ownership);

            Assert.True(cfg.Save(_dir, RadioId));
            string written = File.ReadAllText(ConfigPath);
            Assert.Contains("<" + field + ">" + token + "</" + field + ">", written, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every enum-typed setting RadioConfig saves goes through the tolerant
        /// reader, so the next enum added cannot bring the whole-file failure
        /// back. This guard checks only that no enum property is serialized
        /// bare; that each twin really keeps its unknown text is the theory
        /// above, field by field.
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

namespace Radios.Tests
{
    /// <summary>
    /// #847 repair, found by the Codex check: the Audio Workshop saves its
    /// Audio Check preferences through <see cref="RadioConfig.RecordAudioCheckPref"/>,
    /// and a change to one of them must not save the DISPLAYED value of the
    /// others. With a listen method a newer build wrote (Loopback), the
    /// Workshop displays its fallback, Monitor; toggling only "Compare with
    /// previous take" used to save Monitor over Loopback.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AudioCheckPrefPreservationTests : IDisposable
    {
        private const string Serial = "1234-5678-6300-0847";
        private readonly RadioConfigStaticsScope _scope = new(nameof(AudioCheckPrefPreservationTests));
        private string PathToConfig => _scope.PathTo("radios", Serial, "config.xml");

        public void Dispose() => _scope.Dispose();

        private void WriteNewerBuildsConfig(string power = "DummyLoad")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathToConfig)!);
            File.WriteAllText(PathToConfig,
                "<?xml version=\"1.0\"?>\r\n"
                + "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n"
                + "  <Version>1</Version>\r\n"
                + "  <RadioId>" + Serial + "</RadioId>\r\n"
                + "  <AudioCheckListenMethod>Loopback</AudioCheckListenMethod>\r\n"
                + "  <AudioCheckPowerMode>" + power + "</AudioCheckPowerMode>\r\n"
                + "  <AudioCheckLowPowerWatts>10</AudioCheckLowPowerWatts>\r\n"
                + "  <Ownership>Mine</Ownership>\r\n"
                + "</RadioConfig>\r\n");
        }

        /// <summary>What the Workshop shows for this file: the fallbacks, as LoadPerRadioPrefs reads them.</summary>
        private static RadioConfig Shown() => RadioConfig.LoadForRadio(Serial);

        [Fact]
        public void A_comparison_only_change_keeps_Loopback()
        {
            WriteNewerBuildsConfig();
            var shown = Shown();
            Assert.True(RadioConfig.RecordAudioCheckPref(Serial, AudioCheckPref.CompareWithLastTake,
                shown.AudioCheckListenMethod, shown.AudioCheckPowerMode, shown.AudioCheckLowPowerWatts, true));

            string written = File.ReadAllText(PathToConfig);
            Assert.Contains("<AudioCheckListenMethod>Loopback</AudioCheckListenMethod>", written, StringComparison.Ordinal);
            Assert.True(RadioConfig.LoadForRadio(Serial).AudioCheckCompareWithLastTake);
        }

        [Fact]
        public void A_watts_only_change_keeps_Loopback()
        {
            WriteNewerBuildsConfig();
            var shown = Shown();
            Assert.True(RadioConfig.RecordAudioCheckPref(Serial, AudioCheckPref.LowPowerWatts,
                shown.AudioCheckListenMethod, shown.AudioCheckPowerMode, 3, shown.AudioCheckCompareWithLastTake));

            string written = File.ReadAllText(PathToConfig);
            Assert.Contains("<AudioCheckListenMethod>Loopback</AudioCheckListenMethod>", written, StringComparison.Ordinal);
            Assert.Equal(3, RadioConfig.LoadForRadio(Serial).AudioCheckLowPowerWatts);
        }

        [Fact]
        public void An_unknown_power_mode_survives_a_neighbouring_change()
        {
            WriteNewerBuildsConfig(power: "Spectacle");
            var shown = Shown();
            Assert.True(RadioConfig.RecordAudioCheckPref(Serial, AudioCheckPref.CompareWithLastTake,
                shown.AudioCheckListenMethod, shown.AudioCheckPowerMode, shown.AudioCheckLowPowerWatts, true));
            Assert.True(RadioConfig.RecordAudioCheckPref(Serial, AudioCheckPref.LowPowerWatts,
                shown.AudioCheckListenMethod, shown.AudioCheckPowerMode, 4, true));

            string written = File.ReadAllText(PathToConfig);
            Assert.Contains("<AudioCheckPowerMode>Spectacle</AudioCheckPowerMode>", written, StringComparison.Ordinal);
            Assert.Contains("<AudioCheckListenMethod>Loopback</AudioCheckListenMethod>", written, StringComparison.Ordinal);
        }

        [Fact]
        public void Editing_the_listen_method_itself_replaces_Loopback()
        {
            WriteNewerBuildsConfig();
            var shown = Shown();
            Assert.True(RadioConfig.RecordAudioCheckPref(Serial, AudioCheckPref.ListenMethod,
                AudioCheckListenMethods.Monitor, shown.AudioCheckPowerMode, shown.AudioCheckLowPowerWatts,
                shown.AudioCheckCompareWithLastTake));

            string written = File.ReadAllText(PathToConfig);
            Assert.DoesNotContain("Loopback", written, StringComparison.Ordinal);
            Assert.Contains("<AudioCheckListenMethod>Monitor</AudioCheckListenMethod>", written, StringComparison.Ordinal);
        }

        /// <summary>
        /// The Workshop's four handlers each name the preference they edit, so
        /// none of them saves the others' displayed values. Read from source:
        /// the Workshop is a window.
        /// </summary>
        [Theory]
        [InlineData("AudioWorkshopDialog.AudioCheck.cs", "_listenMethodControl.SelectionChanged", "AudioCheckPref.ListenMethod")]
        [InlineData("AudioWorkshopDialog.AudioCheck.cs", "_checkPowerControl.SelectionChanged", "AudioCheckPref.PowerMode")]
        [InlineData("AudioWorkshopDialog.AudioCheck.cs", "_checkWattsControl.ValueChanged", "AudioCheckPref.LowPowerWatts")]
        [InlineData("AudioWorkshopDialog.Listenback.cs", "_iqAutoCompare.Checked", "AudioCheckPref.CompareWithLastTake")]
        [InlineData("AudioWorkshopDialog.Listenback.cs", "_iqAutoCompare.Unchecked", "AudioCheckPref.CompareWithLastTake")]
        public void Each_Workshop_handler_saves_only_the_preference_it_edits(string file, string handler, string edited)
        {
            string code = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "Dialogs", file));
            int at = code.IndexOf(handler + " +=", StringComparison.Ordinal);
            Assert.True(at >= 0, handler + " not found in " + file);
            int save = code.IndexOf("SavePerRadioPrefs(", at, StringComparison.Ordinal);
            Assert.True(save > at);
            int close = code.IndexOf(')', save);
            Assert.Equal("SavePerRadioPrefs(" + edited, code.Substring(save, close - save));

            string dialog = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "Dialogs", "AudioWorkshopDialog.xaml.cs"));
            int body = dialog.IndexOf("private void SavePerRadioPrefs(AudioCheckPref edited)", StringComparison.Ordinal);
            Assert.True(body >= 0);
            string saver = dialog.Substring(body, dialog.IndexOf("\n    }", body, StringComparison.Ordinal) - body);
            Assert.Contains("RadioConfig.RecordAudioCheckPref(_radioCfgSerial, edited,", saver);
            Assert.DoesNotContain("RecordAudioCheckPrefs(", saver);
        }
    }
}

namespace Radios.Tests
{
    /// <summary>
    /// #847 repair, round two: clearing the local transmit-audio choice
    /// leaves the profile intent exactly as it is, including an intent a
    /// newer build saved, which this build reads as NotAnswered.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LocalTransmitAudioClearTests : IDisposable
    {
        private const string Serial = "1234-5678-6300-0848";
        private readonly RadioConfigStaticsScope _scope = new(nameof(LocalTransmitAudioClearTests));
        private string PathToConfig => _scope.PathTo("radios", Serial, "config.xml");

        public void Dispose() => _scope.Dispose();

        [Fact]
        public void Clearing_the_choice_keeps_an_intent_a_newer_build_saved()
        {
            Assert.True(RadioConfig.RecordLocalTransmitAudioChoice(Serial, "My TX"));
            Assert.True(RadioConfig.Update(Serial, c => { c.ProfileIntentXml = "FromANewerBuild"; }));

            Assert.True(RadioConfig.RecordLocalTransmitAudioChoice(Serial, ""));

            string written = File.ReadAllText(PathToConfig);
            Assert.Contains("<ProfileIntent>FromANewerBuild</ProfileIntent>", written, StringComparison.Ordinal);
            Assert.Equal("", RadioConfig.LoadForRadio(Serial).LocalTransmitAudioProfile);
        }

        [Fact]
        public void Choosing_a_preset_still_declares_the_intent()
        {
            // The positive control: a real choice is a decision and replaces
            // whatever intent was there, a newer build's included.
            Assert.True(RadioConfig.Update(Serial, c => { c.ProfileIntentXml = "FromANewerBuild"; }));
            Assert.True(RadioConfig.RecordLocalTransmitAudioChoice(Serial, "My TX"));
            Assert.Equal(ProfileGuestIntent.UseMyTransmitAudio, RadioConfig.ProfileIntentOf(Serial));
            Assert.DoesNotContain("FromANewerBuild", File.ReadAllText(PathToConfig), StringComparison.Ordinal);
        }
    }
}

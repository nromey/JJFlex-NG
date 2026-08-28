using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using JJPortaudio;
using PortAudioSharp;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The three-level device model (#207, Sprint 37 Track H): hardware →
    /// endpoint → transport, plus "follow the Windows default" as a real,
    /// persisted choice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here runs against explicit row lists and literal XML — no
    /// real sound hardware is enumerated, no dialog is constructed. The model
    /// methods take explicit lists for exactly this reason.
    /// </para>
    /// <para>
    /// The migration tests are the load-bearing ones. Operators have saved
    /// devices chosen under the old model, and a redesign that silently
    /// repoints someone's microphone is the exact bug class this task exists
    /// to end — so "an existing audioDevices.xml means exactly what it meant
    /// yesterday" is asserted against a literal file, not against the code's
    /// own serializer output.
    /// </para>
    /// </remarks>
    public class AudioDeviceThreeLevelTests
    {
        // ────────────────────────────────────────────────────────────────
        //  Row construction — the shapes Enumerate would have produced
        // ────────────────────────────────────────────────────────────────

        private static Devices.DeviceInfo Row(
            string name,
            int api,
            Devices.DeviceTypes type = Devices.DeviceTypes.input,
            int inCh = 2,
            int outCh = 0,
            bool isDefault = false,
            int id = 0)
        {
            var d = new Devices.DeviceInfo
            {
                Info = new PortAudio.PaDeviceInfo
                {
                    name = name,
                    maxInputChannels = inCh,
                    maxOutputChannels = outCh,
                    defaultSampleRate = 48000,
                },
                Type = type,
                HostApiTypeId = api,
                HostApiName = Devices.NameOfHostApi(api),
                DeviceID = id,
                IsDefault = isDefault,
            };
            d.GroupOwner = d;
            return d;
        }

        /// <summary>
        /// Wire one endpoint's transport group the way BuildGroups does:
        /// owner points at itself, alternates point at the owner, and the
        /// Windows-default fact spreads to every member.
        /// </summary>
        private static Devices.DeviceInfo Group(
            Devices.DeviceInfo owner, params Devices.DeviceInfo[] alternates)
        {
            owner.GroupOwner = owner;
            owner.Alternates = alternates.ToList();
            bool def = owner.IsDefault;
            foreach (var a in alternates)
            {
                a.GroupOwner = owner;
                if (a.IsDefault) def = true;
            }
            owner.GroupIsSystemDefault = def;
            foreach (var a in alternates) a.GroupIsSystemDefault = def;
            return owner;
        }

        // ────────────────────────────────────────────────────────────────
        //  SplitDeviceName — prove the naive version wrong first
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_first_parenthesis_split_gets_nested_vendor_names_wrong()
        {
            // The positive control: prove the naive split fails on a real
            // vendor name before trusting the balanced scan that replaces it.
            const string name = "Line In (Realtek(R) Audio)";
            int open = name.IndexOf('(');
            int close = name.IndexOf(')', open);
            string naiveInside = name.Substring(open + 1, close - open - 1);
            Assert.Equal("Realtek(R", naiveInside); // the naive answer, and it is wrong

            Devices.SplitDeviceName(name, out _, out string hardware);
            Assert.NotEqual(naiveInside, hardware);
        }

        [Fact]
        public void An_endpoint_name_splits_into_endpoint_and_hardware()
        {
            Assert.True(Devices.SplitDeviceName(
                "Main Output 1/2 (Audient EVO8)", out string endpoint, out string hardware));
            Assert.Equal("Main Output 1/2", endpoint);
            Assert.Equal("Audient EVO8", hardware);
        }

        [Fact]
        public void Nested_parentheses_stay_inside_the_hardware_name()
        {
            Assert.True(Devices.SplitDeviceName(
                "Line In (Realtek(R) Audio)", out string endpoint, out string hardware));
            Assert.Equal("Line In", endpoint);
            Assert.Equal("Realtek(R) Audio", hardware);
        }

        [Fact]
        public void Only_the_trailing_parenthetical_is_the_hardware_name()
        {
            // Two parentheticals: the endpoint's own "(2)" is part of the
            // endpoint, and only the trailing group names the hardware. A scan
            // anchored on the FIRST '(' — any of them — folds the endpoint's
            // parenthetical into the hardware name. This is the case that
            // caught the deliberate-break run: every singly-parenthesised name
            // happens to survive a first-'(' scan that ends at the last ')'.
            Assert.True(Devices.SplitDeviceName(
                "Speakers (2) (Realtek Audio)", out string endpoint, out string hardware));
            Assert.Equal("Speakers (2)", endpoint);
            Assert.Equal("Realtek Audio", hardware);
        }

        [Fact]
        public void A_name_with_no_parenthetical_is_its_own_hardware()
        {
            Assert.False(Devices.SplitDeviceName(
                "USB Audio CODEC", out string endpoint, out string hardware));
            Assert.Equal("USB Audio CODEC", endpoint);
            Assert.Equal("USB Audio CODEC", hardware);
        }

        [Fact]
        public void An_mme_truncated_name_that_lost_its_closing_paren_is_not_guessed_at()
        {
            // MME cuts at 31 characters, mid-parenthetical. Honest answer:
            // no split, the whole name stands.
            Assert.False(Devices.SplitDeviceName(
                "Mic | Line | Instrument 1 (Audi", out string endpoint, out string hardware));
            Assert.Equal("Mic | Line | Instrument 1 (Audi", endpoint);
            Assert.Equal(endpoint, hardware);
        }

        [Fact]
        public void Null_and_empty_names_do_not_throw()
        {
            Assert.False(Devices.SplitDeviceName(null, out string e1, out string h1));
            Assert.Equal("", e1);
            Assert.Equal("", h1);
            Assert.False(Devices.SplitDeviceName("()", out _, out _));
            Assert.False(Devices.SplitDeviceName("(Audient EVO8)", out _, out _));
        }

        // ────────────────────────────────────────────────────────────────
        //  BuildHardwareTree — endpoints shelve under their hardware
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Endpoints_of_one_hardware_shelve_together_sorted_and_hardware_sorts_by_name()
        {
            var rows = new List<Devices.DeviceInfo>
            {
                Row("Speakers (Realtek(R) Audio)", Devices.WasapiTypeId,
                    Devices.DeviceTypes.output, inCh: 0, outCh: 2),
                Row("Main Output 1/2 (Audient EVO8)", Devices.WasapiTypeId,
                    Devices.DeviceTypes.output, inCh: 0, outCh: 2),
                Row("Line Output 3/4 (Audient EVO8)", Devices.WasapiTypeId,
                    Devices.DeviceTypes.output, inCh: 0, outCh: 2),
            };

            var tree = Devices.BuildHardwareTree(rows);

            Assert.Equal(2, tree.Count);
            Assert.Equal("Audient EVO8", tree[0].Name);
            Assert.Equal("Realtek(R) Audio", tree[1].Name);
            Assert.Equal(new[] { "Line Output 3/4", "Main Output 1/2" },
                tree[0].Endpoints.Select(e => e.Label).ToArray());
            Assert.Single(tree[1].Endpoints);
            Assert.Equal("Speakers", tree[1].Endpoints[0].Label);
        }

        [Fact]
        public void Only_group_owners_make_rows_so_transports_never_duplicate_hardware()
        {
            // The same endpoint under WASAPI and MME — one group, one tree row.
            var wasapi = Row("Speakers (Realtek(R) Audio)", Devices.WasapiTypeId,
                Devices.DeviceTypes.output, inCh: 0, outCh: 2);
            var mme = Row("Speakers (Realtek(R) Audio)", Devices.MmeTypeId,
                Devices.DeviceTypes.output, inCh: 0, outCh: 2);
            Group(wasapi, mme);

            var tree = Devices.BuildHardwareTree(new[] { wasapi, mme });

            Assert.Single(tree);
            Assert.Single(tree[0].Endpoints);
            Assert.Same(wasapi, tree[0].Endpoints[0].Endpoint);
        }

        [Fact]
        public void The_follow_default_alias_rows_stay_off_the_tree()
        {
            // "Follow the Windows default" is a real choice now; the MME and
            // DirectSound pseudo-devices that meant the same thing would be a
            // second vocabulary for it.
            var rows = new List<Devices.DeviceInfo>
            {
                Row("Microsoft Sound Mapper - Input", Devices.MmeTypeId),
                Row("Primary Sound Capture Driver", Devices.DirectSoundTypeId),
                Row("Microphone (USB Audio Device)", Devices.WasapiTypeId),
            };

            var tree = Devices.BuildHardwareTree(rows);

            Assert.Single(tree);
            Assert.Equal("USB Audio Device", tree[0].Name);
        }

        [Fact]
        public void Hidden_kinds_stay_off_unless_they_are_the_windows_default()
        {
            var cable = Row("Line 1 (Virtual Audio Cable)", Devices.WasapiTypeId);
            var defaultCable = Row("CABLE Output (VB-Audio Virtual Cable)", Devices.WasapiTypeId,
                isDefault: true);
            defaultCable.GroupIsSystemDefault = true;
            var mic = Row("Microphone (USB Audio Device)", Devices.WasapiTypeId);

            var tree = Devices.BuildHardwareTree(new[] { cable, defaultCable, mic });

            var names = tree.Select(g => g.Name).ToList();
            Assert.Contains("USB Audio Device", names);
            Assert.Contains("VB-Audio Virtual Cable", names);   // default exempt
            Assert.DoesNotContain("Virtual Audio Cable", names); // hidden kind
        }

        [Fact]
        public void When_the_filter_hides_everything_the_hidden_rows_come_back()
        {
            // A machine whose only inputs are virtual cables must not read as
            // "no audio devices" — that is a lie, and the silent-disappearance
            // shape this file exists to never produce.
            var cable = Row("Line 1 (Virtual Audio Cable)", Devices.WasapiTypeId);

            var tree = Devices.BuildHardwareTree(new[] { cable });

            Assert.Single(tree);
            Assert.Equal("Virtual Audio Cable", tree[0].Name);
        }

        [Fact]
        public void The_windows_default_marks_its_hardware_group()
        {
            var mic = Row("Microphone (USB Audio Device)", Devices.WasapiTypeId);
            mic.GroupIsSystemDefault = true;
            var other = Row("Mic | Line | Instrument 1 (Audient EVO8)", Devices.WasapiTypeId);

            var tree = Devices.BuildHardwareTree(new[] { mic, other });

            Assert.True(tree.Single(g => g.Name == "USB Audio Device").IsSystemDefault);
            Assert.False(tree.Single(g => g.Name == "Audient EVO8").IsSystemDefault);
        }

        // ────────────────────────────────────────────────────────────────
        //  EndpointUnder — the transport dimension of one endpoint
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void An_endpoint_resolves_to_its_row_under_each_transport_from_any_member()
        {
            var wasapi = Row("Speakers (Realtek(R) Audio)", Devices.WasapiTypeId,
                Devices.DeviceTypes.output, inCh: 0, outCh: 2);
            var mme = Row("Speakers (Realtek(R) Audio", Devices.MmeTypeId,
                Devices.DeviceTypes.output, inCh: 0, outCh: 2);
            Group(wasapi, mme);

            Assert.Same(mme, Devices.EndpointUnder(wasapi, Devices.MmeTypeId));
            Assert.Same(wasapi, Devices.EndpointUnder(mme, Devices.WasapiTypeId));
            Assert.Null(Devices.EndpointUnder(wasapi, Devices.DirectSoundTypeId));
            // The or-best form never strands a live endpoint: the group's
            // best member answers when the asked-for transport does not.
            Assert.Same(wasapi, Devices.EndpointUnderOrBest(mme, Devices.DirectSoundTypeId));
        }

        // ────────────────────────────────────────────────────────────────
        //  PickSystemDefault — the default hardware wins on the chosen API
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_default_hardware_wins_on_the_selected_api_even_when_portaudio_flagged_its_mme_row()
        {
            // PortAudio put its default flag on the MME endpoint (as on the
            // development machine). Filtered to WASAPI, the promised
            // preference used to never fire and the first usable WASAPI row
            // won instead — the drift this test pins the fix for.
            var otherWasapi = Row("Mic | Line | Instrument 1 (Audient EVO8)", Devices.WasapiTypeId);
            var defaultMme = Row("Microphone (USB Audio Device", Devices.MmeTypeId, isDefault: true);
            var defaultWasapi = Row("Microphone (USB Audio Device)", Devices.WasapiTypeId);
            Group(defaultWasapi, defaultMme);

            // Positive control: list order alone would pick the EVO8 row.
            var list = new List<Devices.DeviceInfo> { otherWasapi, defaultWasapi, defaultMme };
            Assert.Same(otherWasapi, list.First(d => d.HostApiTypeId == Devices.WasapiTypeId));

            Assert.Same(defaultWasapi,
                Devices.PickSystemDefault(list, Devices.WasapiTypeId));
        }

        [Fact]
        public void The_exact_flagged_endpoint_wins_when_it_is_on_the_selected_api()
        {
            var first = Row("Mic | Line | Instrument 1 (Audient EVO8)", Devices.WasapiTypeId);
            var flagged = Row("Microphone (USB Audio Device)", Devices.WasapiTypeId, isDefault: true);

            Assert.Same(flagged, Devices.PickSystemDefault(
                new[] { first, flagged }, Devices.WasapiTypeId));
        }

        [Fact]
        public void An_api_with_nothing_usable_falls_back_to_the_best_answer_anywhere()
        {
            var mme = Row("Microphone (USB Audio Device", Devices.MmeTypeId, isDefault: true);

            Assert.Same(mme, Devices.PickSystemDefault(
                new[] { mme }, Devices.WasapiTypeId));
            Assert.Null(Devices.PickSystemDefault(
                Array.Empty<Devices.DeviceInfo>(), Devices.WasapiTypeId));
        }

        // ────────────────────────────────────────────────────────────────
        //  audioDevices.xml — migration is the load-bearing part
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A literal pre-#207 audioDevices.xml — the shape on operators'
        /// machines today, hostApiTypeId present, no followWindowsDefault, no
        /// explicit audio-system choice.
        /// </summary>
        private const string ExistingFileXml = @"<?xml version=""1.0""?>
<cfg xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <devs>
    <Device>
      <DevinfoID>13</DevinfoID>
      <Type>input</Type>
      <Name>Mic | Line | Instrument 1 (Audi</Name>
      <hostApi>1</hostApi>
      <maxInputChannels>2</maxInputChannels>
      <maxOutputChannels>0</maxOutputChannels>
      <defaultLowInputLatency>0.09</defaultLowInputLatency>
      <defaultLowOutputLatency>0.09</defaultLowOutputLatency>
      <defaultHighInputLatency>0.18</defaultHighInputLatency>
      <defaultHighOutputLatency>0.18</defaultHighOutputLatency>
      <defaultSampleRate>48000</defaultSampleRate>
      <hostApiTypeId>2</hostApiTypeId>
      <hostApiName>MME</hostApiName>
    </Device>
    <Device>
      <DevinfoID>7</DevinfoID>
      <Type>output</Type>
      <Name>Speakers (Realtek(R) Audio)</Name>
      <hostApi>1</hostApi>
      <maxInputChannels>0</maxInputChannels>
      <maxOutputChannels>2</maxOutputChannels>
      <defaultLowInputLatency>0.09</defaultLowInputLatency>
      <defaultLowOutputLatency>0.09</defaultLowOutputLatency>
      <defaultHighInputLatency>0.18</defaultHighInputLatency>
      <defaultHighOutputLatency>0.18</defaultHighOutputLatency>
      <defaultSampleRate>48000</defaultSampleRate>
      <hostApiTypeId>2</hostApiTypeId>
      <hostApiName>MME</hostApiName>
    </Device>
  </devs>
</cfg>";

        private static Devices.cfg ReadCfg(string xml)
        {
            var xs = new XmlSerializer(typeof(Devices.cfg));
            using var reader = new StringReader(xml);
            return (Devices.cfg)xs.Deserialize(reader)!;
        }

        [Fact]
        public void An_existing_audioDevices_xml_means_exactly_what_it_meant_yesterday()
        {
            var cfg = ReadCfg(ExistingFileXml);

            // The saved devices load as concrete, named hardware — NOT as
            // follow-the-default, and with their identity untouched. This is
            // the "nothing silently repoints" guarantee in executable form.
            Assert.False(cfg.devs[0].followWindowsDefault);
            Assert.False(cfg.devs[1].followWindowsDefault);
            Assert.Equal("Mic | Line | Instrument 1 (Audi", cfg.devs[0].Name);
            Assert.Equal(Devices.MmeTypeId, cfg.devs[0].hostApiTypeId);
            Assert.Equal("Speakers (Realtek(R) Audio)", cfg.devs[1].Name);
            // No explicit audio-system choice recorded → -1, resolved to the
            // default at load, exactly as before this change.
            Assert.Equal(-1, cfg.selectedHostApiTypeId);
        }

        [Fact]
        public void A_follow_default_entry_survives_a_round_trip()
        {
            var cfg = new Devices.cfg();
            cfg.devs[0] = new Devices.Device
            {
                Type = Devices.DeviceTypes.input,
                followWindowsDefault = true,
                Name = "Microphone (USB Audio Device)", // advisory snapshot
                hostApiTypeId = Devices.WasapiTypeId,
            };
            cfg.selectedHostApiTypeId = Devices.WasapiTypeId;

            var xs = new XmlSerializer(typeof(Devices.cfg));
            var sb = new StringBuilder();
            using (var w = new StringWriter(sb)) xs.Serialize(w, cfg);
            var back = ReadCfg(sb.ToString());

            Assert.True(back.devs[0].followWindowsDefault);
            Assert.Equal("Microphone (USB Audio Device)", back.devs[0].Name);
            Assert.Equal(Devices.WasapiTypeId, back.selectedHostApiTypeId);
        }

        /// <summary>
        /// The shape of the Device type as builds before #207 knew it — no
        /// followWindowsDefault member.
        /// </summary>
        [XmlRoot("cfg")]
        public class OldBuildCfg
        {
            [XmlArrayItem("Device")]
            public OldBuildDevice[] devs = new OldBuildDevice[2];
            public int selectedHostApiTypeId = -1;
        }

        public class OldBuildDevice
        {
            public int DevinfoID;
            public Devices.DeviceTypes Type;
            public string? Name;
            public int hostApiTypeId = -1;
            public string? hostApiName;
        }

        [Fact]
        public void A_build_older_than_the_field_reads_a_new_file_as_the_advisory_snapshot()
        {
            // Forward compatibility, proven rather than asserted: serialize a
            // follow-default entry with today's type, deserialize it with a
            // shadow of the PRE-#207 type. The unknown element is skipped and
            // the old build sees the concrete snapshot — frozen, but working.
            var cfg = new Devices.cfg();
            cfg.devs[0] = new Devices.Device
            {
                Type = Devices.DeviceTypes.input,
                followWindowsDefault = true,
                Name = "Microphone (USB Audio Device)",
                hostApiTypeId = Devices.WasapiTypeId,
            };

            var xs = new XmlSerializer(typeof(Devices.cfg));
            var sb = new StringBuilder();
            using (var w = new StringWriter(sb)) xs.Serialize(w, cfg);

            var oldXs = new XmlSerializer(typeof(OldBuildCfg));
            using var reader = new StringReader(sb.ToString());
            var old = (OldBuildCfg)oldXs.Deserialize(reader)!;

            Assert.Equal("Microphone (USB Audio Device)", old.devs[0].Name);
            Assert.Equal(Devices.WasapiTypeId, old.devs[0].hostApiTypeId);
        }

        // ────────────────────────────────────────────────────────────────
        //  The dialog's commit guard — read from source, like the picker
        //  order tests, because the method is private and the alternative
        //  is constructing a WPF dialog
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void An_untouched_direction_under_an_untouched_transport_is_never_rewritten()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "JJFlexWpf", "Dialogs", "AudioDevicesDialog.xaml.cs"));

            // Positive control first: the commit method exists to be guarded.
            Assert.Contains("private IEnumerable<string> CommitBasicPick(", src);
            Assert.Contains("!pick.Touched && !_transportTouched", src);
        }

        [Fact]
        public void Follow_default_resolution_happens_at_every_GetConfiguredDevice_call()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "JJPortaudio", "JJPortaudio", "Devices.cs"));

            Assert.Contains("public Device GetConfiguredDevice(", src);
            Assert.Contains("if (dev.followWindowsDefault)", src);
            Assert.Contains("PickSystemDefault(type, SelectedHostApiTypeId)", src);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}

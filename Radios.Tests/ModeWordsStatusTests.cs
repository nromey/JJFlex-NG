using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The status sentences say a mode the way every mode announcement does
    /// (<see cref="ModeWords.Spoken"/>, ruled J41 on 2026-11-07, DFM on
    /// 2026-11-08), and the Status dialog's displayed rows keep the radio's
    /// letters.
    /// </summary>
    /// <remarks>
    /// <para>The defect: <see cref="RadioStatusBuilder"/> put the raw mode
    /// into the status key's sentence (Ctrl+Shift+S) and Home's return status,
    /// so on a digital mode the same session said "Digital upper" on the mode
    /// key and "DIGU" on the status key. The builder is shared with the Status
    /// dialog, whose Slices rows and At connect reference are displayed, so the
    /// spoken form could not go into the shared string unsplit.</para>
    /// <para>The connect census is NOT spoken: <see cref="ConnectBriefing.Settle"/>
    /// keeps it only in <see cref="ConnectBriefing.Reference"/>, which the
    /// Status dialog shows under At connect. The first version of this fix took
    /// it for speech and put words into that displayed row.</para>
    /// <para>Runs on a REAL vendored Radio with a fake transport, as
    /// <c>TuningReceiptTests</c> does: the modes are FlexLib's own slice
    /// caches, read by the production builder. Speech is suppressed and
    /// nothing is sent.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ModeWordsStatusTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const uint Handle = 0x5A5A0042;

        private readonly RadioConfigStaticsScope _scope = new(nameof(ModeWordsStatusTests));
        private readonly List<FlexBase> _rigs = new();

        public ModeWordsStatusTests() => Lexicon.Load(Lexicon.Partitions);

        public void Dispose()
        {
            foreach (FlexBase r in _rigs) { try { r.Dispose(); } catch { } }
            _scope.Dispose();
        }

        [Theory]
        [InlineData("DIGU", "Digital upper")]
        [InlineData("DIGL", "Digital lower")]
        [InlineData("SAM", "Synchronous AM")]
        [InlineData("NFM", "Narrow FM")]
        [InlineData("DFM", "Digital FM")]
        public void The_status_key_says_each_slices_mode_in_words(string mode, string words)
        {
            FlexBase rig = TwoSlices(mode, "USB");

            string spoken = RadioStatusBuilder.BuildFullSliceStatus(rig);

            Assert.Contains("2 slices", spoken);                    // the multi-slice sentence was built
            Assert.Contains(", 14.100 megahertz, " + words + ",", spoken);
            Assert.Contains(", 7.100 megahertz, USB,", spoken);     // letters stay letters
            Assert.DoesNotContain(", " + mode + ",", spoken);
        }

        [Theory]
        [InlineData("DIGU")]
        [InlineData("SAM")]
        [InlineData("DFM")]
        public void The_status_dialogs_rows_keep_the_radios_letters(string mode)
        {
            FlexBase rig = TwoSlices(mode, "NFM");

            string shown = RadioStatusBuilder.BuildFullSliceStatusForDisplay(rig);

            Assert.Contains(", 14.100 megahertz, " + mode + ",", shown);
            Assert.Contains(", 7.100 megahertz, NFM,", shown);

            // The two differ in the modes and nothing else.
            string spoken = RadioStatusBuilder.BuildFullSliceStatus(rig);
            Assert.Equal(shown,
                spoken.Replace(ModeWords.Spoken(mode), mode).Replace(ModeWords.Spoken("NFM"), "NFM"));
        }

        [Theory]
        [InlineData("DIGU", "Digital upper")]
        [InlineData("SAM", "Synchronous AM")]
        [InlineData("DFM", "Digital FM")]
        public void Homes_return_status_says_the_mode_in_words(string mode, string words)
        {
            FlexBase rig = TwoSlices(mode, "USB");

            string spoken = RadioStatusBuilder.BuildSpokenStatus(rig);

            Assert.StartsWith("Listening on ", spoken);
            Assert.Contains(", " + words, spoken);
            Assert.DoesNotContain(", " + mode, spoken);
        }

        [Fact]
        public void One_slice_falls_through_to_the_same_wording_both_ways()
        {
            FlexBase rig = OneSlice("DIGL");

            Assert.Equal(RadioStatusBuilder.BuildSpokenStatus(rig), RadioStatusBuilder.BuildFullSliceStatus(rig));
            Assert.Contains(", Digital lower", RadioStatusBuilder.BuildFullSliceStatus(rig));
            Assert.Contains(", DIGL", RadioStatusBuilder.BuildFullSliceStatusForDisplay(rig));
        }

        /// <summary>
        /// The connect census goes to the Status dialog's At connect section and
        /// nowhere else, and it shows the radio's letters, as the Slices rows
        /// beside it do. Driven through a real <see cref="ConnectBriefing"/>.
        /// </summary>
        [Theory]
        [InlineData("DIGU")]
        [InlineData("SAM")]
        [InlineData("DFM")]
        public void The_at_connect_reference_and_the_slices_rows_both_keep_the_letters(string mode)
        {
            FlexBase rig = TwoSlices(mode, "NFM");
            var said = new List<BriefingUtterance>();
            var briefing = new ConnectBriefing(u => said.Add(u), () => { });

            string census = RadioStatusBuilder.BuildConnectCensusForDisplay(rig, "FLEX-6600", "local");
            var emitted = briefing.Settle("Connected to the FLEX-6600, two slices", census);

            Assert.Equal(census, Assert.Single(briefing.Reference));
            string atConnect = briefing.Reference[0];
            Assert.Contains(", 14.100 megahertz, " + mode + ",", atConnect);
            Assert.Contains(", 7.100 megahertz, NFM,", atConnect);
            Assert.DoesNotContain(ModeWords.Spoken(mode), atConnect);
            Assert.DoesNotContain(ModeWords.Spoken("NFM"), atConnect);

            // The Slices rows, as the dialog splits them.
            string slices = RadioStatusBuilder.BuildFullSliceStatusForDisplay(rig);
            Assert.Contains(", 14.100 megahertz, " + mode + ",", slices);
            Assert.Contains(slices, atConnect);   // one census, one form

            // And nothing the briefing speaks carries the census.
            foreach (var u in emitted)
            {
                Assert.DoesNotContain("megahertz", u.Text);
                Assert.DoesNotContain(mode, u.Text);
            }
        }

        /// <summary>
        /// The callers: the spoken ones take the spoken builder, the displayed
        /// ones the display builders. A source pin, because the status key, Home
        /// and the connect census live in the app, where this project cannot run
        /// them.
        /// </summary>
        [Fact]
        public void Each_caller_takes_the_builder_for_how_it_is_delivered()
        {
            string keys = Read("JJFlexWpf", "KeyCommands.cs");
            int at = keys.IndexOf("private void SpeakStatusHandler()", StringComparison.Ordinal);
            Assert.True(at >= 0, "the status key's handler is gone");
            Assert.Contains("RadioStatusBuilder.BuildFullSliceStatus(rig)", keys.Substring(at, 400));

            string main = Read("JJFlexWpf", "MainWindow.xaml.cs");
            Assert.Contains("censusFull = Radios.RadioStatusBuilder.BuildConnectCensusForDisplay(RigControl, model, connType);", main);
            Assert.DoesNotContain("Radios.RadioStatusBuilder.BuildFullSliceStatus(RigControl)", main);
            Assert.Contains("string status = Radios.RadioStatusBuilder.BuildSpokenStatus(rig);", main);

            string dialog = Read("JJFlexWpf", "Dialogs", "StatusDialog.xaml.cs");
            Assert.Contains("RadioStatusBuilder.BuildFullSliceStatusForDisplay(Rig)", dialog);
            Assert.DoesNotContain("RadioStatusBuilder.BuildFullSliceStatus(Rig)", dialog);

            // The 60 metre channel arrival is spoken too. Every allocation's
            // channels are USB today, so this is a pin, not a behaviour.
            Assert.Contains("(\"mode\", ModeWords.Spoken(ch.Mode))", Read("Radios", "SixtyMeterChannels.cs"));
        }

        private FlexBase TwoSlices(string modeA, string modeB)
        {
            var (rig, vendor) = NewRig();
            Slice a = Add(rig, vendor, 0, tx: true, active: true, mhz: 14.100, modeA);
            Slice b = Add(rig, vendor, 1, tx: false, active: false, mhz: 7.100, modeB);
            Plant(rig, vendor, a, b);
            return rig;
        }

        private FlexBase OneSlice(string mode)
        {
            var (rig, vendor) = NewRig();
            Slice a = Add(rig, vendor, 0, tx: true, active: true, mhz: 14.100, mode);
            Plant(rig, vendor, a);
            return rig;
        }

        private (FlexBase, VendorRadioFixture) NewRig()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            var vendor = new VendorRadioFixture("modewords-" + Guid.NewGuid().ToString("N"));
            rig.q.MainLoop = true;   // parked: nothing drains it
            typeof(FlexBase).GetField("clientHandle", Private)!.SetValue(rig, Handle);
            typeof(FlexBase).GetField("_IsConnected", Private)!.SetValue(rig, true);
            _rigs.Add(rig);
            return (rig, vendor);
        }

        private static Slice Add(FlexBase rig, VendorRadioFixture vendor, int index, bool tx, bool active, double mhz, string mode)
        {
            Slice s = vendor.NewSlice(index, Handle, mhz, mode);
            Set(s, "_letter", ((char)('A' + index)).ToString());
            Set(s, "_isTransmitSlice", tx);
            Set(s, "_active", active);
            typeof(FlexBase).GetMethod("sliceAdded", Private)!
                .Invoke(rig, new object[] { s, new ObservationBinding(new object(), new ConnectionAttempt("test-serial")) });
            return s;
        }

        private static void Plant(FlexBase rig, VendorRadioFixture vendor, params Slice[] slices)
        {
            Set(rig, "theRadio", vendor.Radio);
            var list = (System.Collections.IList)typeof(Radio).GetField("_slices", Private)!.GetValue(vendor.Radio)!;
            foreach (Slice s in slices) list.Add(s);
            Set(rig, "_RXVFO", 0);
            Assert.Equal(slices.Length, rig.MyNumSlices);
        }

        private static void Set(object o, string field, object value)
            => o.GetType().GetField(field, Private)!.SetValue(o, value);

        private static string Read(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d!.FullName, Path.Combine(parts)));
        }
    }
}

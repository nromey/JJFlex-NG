using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// <see cref="SliceRoles.Of"/> on a real <see cref="FlexBase"/>, driven
    /// through the production arrival, removal and report handlers. The pure
    /// sentence tests hand-build their roles, so they cannot see a reading
    /// that names the wrong slice; these create the states in which the old
    /// reading did. F (#642), Transmitting and Receiving (#496) and the notice
    /// before a door keys (#863) all read this one snapshot.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SliceRolesSnapshotTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly List<FlexBase> _rigs = new();

        public SliceRolesSnapshotTests() => Lexicon.Load(Lexicon.Partitions);

        public void Dispose()
        {
            foreach (FlexBase r in _rigs) { try { r.Dispose(); } catch { } }
        }

        private FlexBase Rig()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            rig.q.MainLoop = true;
            _rigs.Add(rig);
            return rig;
        }

        private static Slice ReadySlice(int index, bool tx, bool active, double mhz)
        {
            var radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { false }, null)!;
            var slice = (Slice)Activator.CreateInstance(typeof(Slice), Private, null, new object[] { radio }, null)!;
            Set(slice, "_index", index);
            Set(slice, "_letter", ((char)('A' + index)).ToString());
            Set(slice, "_isTransmitSlice", tx);
            Set(slice, "_active", active);
            Set(slice, "_demodMode", "USB");
            Set(slice, "_freq", mhz);
            return slice;
        }

        private static void Set(object o, string field, object value)
            => o.GetType().GetField(field, Private)!.SetValue(o, value);

        private static ObservationBinding Binding() => new(new object(), new ConnectionAttempt("test-serial"));

        private static void SliceArrives(FlexBase rig, Slice slice)
        {
            typeof(FlexBase).GetField("clientHandle", Private)!.SetValue(rig, slice.ClientHandle);
            typeof(FlexBase).GetMethod("sliceAdded", Private)!.Invoke(rig, new object[] { slice, Binding() });
        }

        private static void SliceGoes(FlexBase rig, Slice slice)
            => typeof(FlexBase).GetMethod("sliceRemoved", Private)!.Invoke(rig, new object[] { slice, Binding() });

        private static void SliceReports(FlexBase rig, Slice slice, string property)
            => typeof(FlexBase).GetMethod("slicePropertyChangedHandler", Private)!
                .Invoke(rig, new object[] { slice, new PropertyChangedEventArgs(property) });

        private static string Mhz(ulong hz) => (hz / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);

        [Fact]
        public void With_both_slices_standing_it_reads_each_off_its_own_slice()
        {
            // Positive control: the fixture produces a split the reading sees.
            FlexBase rig = Rig();
            SliceArrives(rig, ReadySlice(0, tx: true, active: false, mhz: 14.100));
            SliceArrives(rig, ReadySlice(1, tx: false, active: true, mhz: 7.150));

            SliceRoles roles = SliceRoles.Of(rig);

            Assert.True(roles.HasTransmit);
            Assert.Equal("A", roles.TransmitLetter);
            Assert.Equal(14_100_000UL, roles.TransmitHz);
            Assert.Equal("B", roles.SelectedLetter);
            Assert.Equal(7_150_000UL, roles.SelectedHz);
            Assert.True(roles.TransmitsElsewhere);
        }

        [Fact]
        public void The_transmit_slice_removed_with_none_designated_names_no_transmit_slice()
        {
            // A transmits, B is selected, C stands by. A goes: TXVFO falls back
            // to position zero, which is B, and B is NOT designated.
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: false, mhz: 14.100);
            SliceArrives(rig, a);
            SliceArrives(rig, ReadySlice(1, tx: false, active: true, mhz: 7.030));
            SliceArrives(rig, ReadySlice(2, tx: false, active: false, mhz: 3.800));

            SliceGoes(rig, a);

            Assert.True(rig.ValidVFO(rig.TXVFO)); // the position still stands
            SliceRoles roles = SliceRoles.Of(rig);
            Assert.False(roles.HasTransmit);
            Assert.Equal("", roles.TransmitLetter);
            Assert.Equal("B", roles.SelectedLetter);
            Assert.False(roles.TransmitsElsewhere);
            // And no consumer names a transmit slice.
            Assert.Equal(Lexicon.Get("audio.ptt.announce_transmitting"), PttSliceWords.Transmitting(roles, nameSlice: true));
            Assert.Equal(Lexicon.Get("audio.ptt.announce_locked"), PttSliceWords.Locked(roles, nameSlice: true));
            Assert.Null(TransmitSliceNotice.Sentence(roles, Mhz));
            Assert.Equal(Lexicon.Get("connect.home.frequency", ("freqText", "7.030"), ("slice", "B")),
                         FrequencyKeyReadout.Sentence(FrequencyKeySlices.Transmit, roles, Mhz));
        }

        [Fact]
        public void The_transmit_slice_removed_with_another_already_designated_names_that_one()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: false, mhz: 14.100);
            Slice c = ReadySlice(2, tx: false, active: false, mhz: 3.800);
            SliceArrives(rig, a);
            SliceArrives(rig, ReadySlice(1, tx: false, active: true, mhz: 7.030));
            SliceArrives(rig, c);
            Set(c, "_isTransmitSlice", true); // the radio moved it; no report yet

            SliceGoes(rig, a);

            SliceRoles roles = SliceRoles.Of(rig);
            Assert.Equal("C", roles.TransmitLetter); // not B, which TXVFO fell back to
            Assert.Equal(3_800_000UL, roles.TransmitHz);
            Assert.Equal(rig.TXFrequency, roles.TransmitHz); // agrees with TXFrequency
            Assert.Equal("B", roles.SelectedLetter);
            Assert.Equal(Lexicon.Get("audio.ptt.announce_transmitting_on", ("slice", "C")),
                         PttSliceWords.Transmitting(roles, nameSlice: true));
        }

        [Fact]
        public void The_radio_taking_the_designation_away_leaves_no_transmit_slice()
        {
            // The radio's IsTransmitSlice=false report leaves TXVFO standing.
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: false, mhz: 14.100);
            SliceArrives(rig, a);
            SliceArrives(rig, ReadySlice(1, tx: false, active: true, mhz: 7.150));
            Assert.True(SliceRoles.Of(rig).HasTransmit); // positive control

            Set(a, "_isTransmitSlice", false);
            SliceReports(rig, a, "IsTransmitSlice");

            Assert.True(rig.ValidVFO(rig.TXVFO)); // the position is still there
            SliceRoles roles = SliceRoles.Of(rig);
            Assert.False(roles.HasTransmit);
            Assert.Null(TransmitSliceNotice.Sentence(roles, Mhz));
        }

        [Fact]
        public void A_lower_slice_removed_keeps_each_letter_with_its_own_frequency()
        {
            // Removing an unused lower slice shifts every position behind it.
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: false, active: false, mhz: 1.900);
            SliceArrives(rig, a);
            SliceArrives(rig, ReadySlice(1, tx: false, active: true, mhz: 7.150));
            SliceArrives(rig, ReadySlice(2, tx: true, active: false, mhz: 21.300));

            SliceGoes(rig, a);

            SliceRoles roles = SliceRoles.Of(rig);
            Assert.Equal("B", roles.SelectedLetter);
            Assert.Equal(7_150_000UL, roles.SelectedHz);
            Assert.Equal("C", roles.TransmitLetter);
            Assert.Equal(21_300_000UL, roles.TransmitHz);
        }

        [Fact]
        public void The_selected_slice_removed_reads_the_new_selection_off_itself_not_the_old_cache()
        {
            // The receive cache follows the ACTIVE slice. A removes while
            // active: RXVFO falls back to B before the radio reports a new
            // active slice, and the cache still holds A's 7.150.
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, ReadySlice(1, tx: false, active: false, mhz: 14.200));
            Assert.Equal(7_150_000UL, rig.RXFrequency); // the cache, before

            SliceGoes(rig, a);

            Assert.Equal(7_150_000UL, rig.RXFrequency); // the cache is stale
            SliceRoles roles = SliceRoles.Of(rig);
            Assert.Equal("B", roles.SelectedLetter);
            Assert.Equal(14_200_000UL, roles.SelectedHz); // B's own, not A's
        }

        [Fact]
        public void No_slices_reads_as_nothing_and_says_frequency_unavailable()
        {
            FlexBase rig = Rig();
            SliceRoles roles = SliceRoles.Of(rig);
            Assert.False(roles.HasSelected);
            Assert.False(roles.HasTransmit);
            Assert.Equal(Lexicon.Get("connect.home.frequency_unavailable"),
                         FrequencyKeyReadout.Sentence(FrequencyKeySlices.Both, roles, Mhz));
        }

        [Fact]
        public void Every_reading_pairs_a_letter_with_its_own_slice_while_the_roster_changes()
        {
            // A slice added and removed below the two roles, over and over, on
            // another thread: every reading must pair B with 7.150 and C with
            // 21.300, never one slice's letter with another's frequency.
            FlexBase rig = Rig();
            SliceArrives(rig, ReadySlice(1, tx: false, active: true, mhz: 7.150));
            SliceArrives(rig, ReadySlice(2, tx: true, active: false, mhz: 21.300));
            Slice churn = ReadySlice(0, tx: false, active: false, mhz: 1.900);

            using var stop = new CancellationTokenSource();
            var churner = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    SliceArrives(rig, churn);
                    SliceGoes(rig, churn);
                }
            }) { IsBackground = true };
            churner.Start();
            try
            {
                for (int i = 0; i < 20_000; i++)
                {
                    SliceRoles roles = SliceRoles.Of(rig);
                    Assert.Equal("B", roles.SelectedLetter);
                    Assert.Equal(7_150_000UL, roles.SelectedHz);
                    Assert.Equal("C", roles.TransmitLetter);
                    Assert.Equal(21_300_000UL, roles.TransmitHz);
                }
            }
            finally
            {
                stop.Cancel();
                churner.Join();
            }
        }
    }
}

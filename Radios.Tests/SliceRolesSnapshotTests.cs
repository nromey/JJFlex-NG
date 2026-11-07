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
            Assert.Equal("", rig.TXSliceLetter);   // and names nothing (repair round 2)
            SliceRoles roles = SliceRoles.Of(rig);
            Assert.False(roles.HasTransmit);
            Assert.Equal("", roles.TransmitLetter);
            Assert.Equal("B", roles.SelectedLetter);
            Assert.False(roles.TransmitsElsewhere);
            // And no consumer names a transmit slice.
            Assert.Equal(Lexicon.Get("audio.ptt.announce_transmitting"), PttSliceWords.Transmitting(roles, nameSlice: true));
            Assert.Equal(Lexicon.Get("audio.ptt.announce_locked"), PttSliceWords.Locked(roles, nameSlice: true));
            Assert.Null(TransmitSliceNotice.Wording(roles, VerbosityLevel.Chatty, Mhz));
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
            Assert.Equal("C", rig.TXSliceLetter);    // the status surfaces agree (repair round 2)
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
            Assert.Null(TransmitSliceNotice.Wording(roles, VerbosityLevel.Chatty, Mhz));
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

            // The cache is stale, and RXFrequency no longer hands it out for a
            // slice it does not belong to (repair round 2: this line pinned
            // the stale 7.150 as RXFrequency's answer, and the tuning
            // arithmetic reads RXFrequency).
            Assert.Equal(14_200_000UL, rig.RXFrequency);
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
        public void A_door_speaks_once_per_split_and_again_when_the_split_changes()
        {
            // #863, ruled 2026-11-03: once per split, not per key. Driven on a
            // real rig through Prepare and Keyed, the calls every door makes.
            var setting = TransmitSliceNotice.Setting;
            var verbosity = ScreenReaderOutput.CurrentVerbosity;
            FlexBase rig = Rig();
            try
            {
                TransmitSliceNotice.Setting = TransmitElsewhereNotice.SpeakAndTone;
                ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
                Slice a = ReadySlice(0, tx: true, active: false, mhz: 14.100);
                SliceArrives(rig, a);
                SliceArrives(rig, ReadySlice(1, tx: false, active: true, mhz: 7.150));

                var first = TransmitSliceNotice.Prepare(rig, "test", mandatory: false);
                Assert.True(first.Spoke);
                // Not keyed (cancelled): the next attempt is not silenced by it.
                var again = TransmitSliceNotice.Prepare(rig, "test", mandatory: false);
                Assert.True(again.Spoke);
                // Heard whole (repair round 2: only a sentence that finished
                // is remembered; this host has no reader to finish one).
                TransmitSliceNotice.Keyed(Heard(rig, again));

                Assert.False(TransmitSliceNotice.Prepare(rig, "test", mandatory: false).Spoke);   // the same split: tone only

                Set(a, "_freq", 14.105);
                SliceReports(rig, a, "Freq");
                var moved = TransmitSliceNotice.Prepare(rig, "test", mandatory: false);
                Assert.True(moved.Spoke);                 // the transmit frequency moved
                TransmitSliceNotice.Keyed(Heard(rig, moved));

                TransmitSliceNotice.Setting = TransmitElsewhereNotice.Off;
                Set(a, "_freq", 14.110);
                SliceReports(rig, a, "Freq");
                Assert.False(TransmitSliceNotice.Prepare(rig, "test", mandatory: false).Spoke);  // off
                Assert.True(TransmitSliceNotice.Prepare(rig, "check", mandatory: true).Spoke);   // Check your audio still says it
            }
            finally
            {
                TransmitSliceNotice.ForgetFor(rig);
                TransmitSliceNotice.Setting = setting;
                ScreenReaderOutput.CurrentVerbosity = verbosity;
            }
        }

        /// <summary>
        /// #863, repair round 2 (Codex's re-check R2, its probe made a test).
        /// Push to talk in a dialog keys first and says after, so its notice
        /// is remembered when it keys; but "spoke" meant REQUESTED. A short
        /// press unkeyed before the sentence was heard, the unkey retired it,
        /// and the split stayed remembered, so the next box send keyed with no
        /// sentence ever heard. Now the split is remembered only when the
        /// sentence finishes whole.
        /// </summary>
        [Fact]
        public void A_notice_retired_before_it_was_heard_leaves_the_split_owed()
        {
            FlexBase rig = Rig();
            try
            {
                TransmitSliceNotice.Setting = TransmitElsewhereNotice.SpeakAndTone;
                SliceArrives(rig, ReadySlice(0, tx: true, active: false, mhz: 14.100));
                SliceArrives(rig, ReadySlice(1, tx: false, active: true, mhz: 7.150));
                SliceRoles roles = SliceRoles.Of(rig);
                Assert.True(TransmitSliceNotice.Speaks(TransmitSliceNotice.Setting, false, roles, null)); // control

                var r = new TransmitSliceNoticeTests.Reader();
                ICountdownWords words = r.Speech.SayWhole("Transmitting on slice A, 14.100", Speech.SpeechSubject.TransmitSliceNotice);
                var notice = new TransmitSliceNotice.Notice(rig, "dialog push to talk", roles, false, true, words);
                Assert.False(words.Finished);
                TransmitSliceNotice.Keyed(notice);                       // exactly what SayAfterKey does
                r.Arbiter.Supersede(Speech.SpeechSubject.TransmitSliceNotice, "transmission ended", "test"); // the unkey
                Assert.True(words.Failed);

                Assert.Null(WaitForSpoken(rig, expectSet: false));
                Assert.True(TransmitSliceNotice.Speaks(TransmitSliceNotice.Setting, false, SliceRoles.Of(rig),
                    TransmitSliceNotice.LastSpoken(rig)));               // the next door still owes the sentence
            }
            finally { TransmitSliceNotice.ForgetFor(rig); }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_notice_cut_after_its_first_word_leaves_the_split_owed_and_one_heard_whole_does_not(bool cut)
        {
            FlexBase rig = Rig();
            try
            {
                TransmitSliceNotice.Setting = TransmitElsewhereNotice.SpeakAndTone;
                SliceArrives(rig, ReadySlice(0, tx: true, active: false, mhz: 14.100));
                SliceArrives(rig, ReadySlice(1, tx: false, active: true, mhz: 7.150));
                SliceRoles roles = SliceRoles.Of(rig);
                var r = new TransmitSliceNoticeTests.Reader();
                ICountdownWords words = r.Speech.SayWhole("Transmitting on slice A, 14.100", Speech.SpeechSubject.TransmitSliceNotice);
                TransmitSliceNotice.Keyed(new TransmitSliceNotice.Notice(rig, "dialog push to talk", roles, false, true, words));
                r.Arbiter.OnOutcome(r.LastTicket, r.LastText, cut
                    ? Speech.SpeechOutcome.Cancelled(1, 4, true, 200)
                    : Speech.SpeechOutcome.Completed(4, 4, 900));
                SliceRoles? spoken = WaitForSpoken(rig, expectSet: !cut);
                if (cut) Assert.Null(spoken);
                else Assert.True(spoken.HasValue && spoken.Value.SamePlaceAs(roles));   // the positive control
            }
            finally { TransmitSliceNotice.ForgetFor(rig); }
        }

        [Fact]
        public void A_key_on_the_selected_slice_from_any_door_ends_a_remembered_split()
        {
            // Codex's re-check F1: Home's push to talk and the locks never went
            // through a notice, so a key on the selected slice there did not
            // end the split, and returning to it later was silent.
            FlexBase rig = Rig();
            try
            {
                TransmitSliceNotice.Setting = TransmitElsewhereNotice.SpeakAndTone;
                Slice a = ReadySlice(0, tx: true, active: false, mhz: 14.100);
                Slice b = ReadySlice(1, tx: false, active: true, mhz: 7.150);
                SliceArrives(rig, a);
                SliceArrives(rig, b);
                SliceRoles split = SliceRoles.Of(rig);
                TransmitSliceNotice.Keyed(new TransmitSliceNotice.Notice(rig, "box send", split, false, true, null));
                Assert.NotNull(TransmitSliceNotice.LastSpoken(rig));

                TransmitSliceNotice.KeyedWithoutNotice(rig);              // keyed while the split stands: no change
                Assert.NotNull(TransmitSliceNotice.LastSpoken(rig));

                rig.RXVFO = 0;                                            // select A, the transmit slice
                TransmitSliceNotice.KeyedWithoutNotice(rig);              // Home push to talk on it
                Assert.Null(TransmitSliceNotice.LastSpoken(rig));
            }
            finally { TransmitSliceNotice.ForgetFor(rig); }
        }

        /// <summary>A door's notice whose sentence the reader finished whole.</summary>
        private static TransmitSliceNotice.Notice Heard(FlexBase rig, TransmitSliceNotice.Notice said)
        {
            var r = new TransmitSliceNoticeTests.Reader();
            ICountdownWords words = r.Speech.SayWhole("Transmitting on slice A", Speech.SpeechSubject.TransmitSliceNotice);
            r.Arbiter.OnOutcome(r.LastTicket, r.LastText, Speech.SpeechOutcome.Completed(4, 4, 900));
            Assert.True(words.Finished);
            return new TransmitSliceNotice.Notice(rig, said.Door, said.Announced, said.Mandatory, said.Spoke, words);
        }

        /// <summary>The commit after a sentence finishes is asynchronous: wait for it, bounded.</summary>
        private static SliceRoles? WaitForSpoken(FlexBase rig, bool expectSet)
        {
            var until = DateTime.UtcNow.AddSeconds(expectSet ? 5 : 0.5);
            SliceRoles? spoken;
            do
            {
                spoken = TransmitSliceNotice.LastSpoken(rig);
                if (spoken.HasValue == expectSet) return spoken;
                Thread.Sleep(20);
            } while (DateTime.UtcNow < until);
            return spoken;
        }

        /// <summary>
        /// Repair round 2 (Codex's re-check of #642): selecting a slice moves
        /// RXVFO at once and queues Active; FlexLib raises the slice's Active
        /// BEFORE the radio's ActiveSlice, and only the latter refreshed the
        /// receive cache. In between, A was selected and Active while the
        /// cache still held B's frequency, and F, the title and the notice
        /// read A with B's number. Now each cache carries the slice it belongs
        /// to, and a reader whose slice is not the cache's reads the slice.
        /// </summary>
        [Fact]
        public void Selecting_a_slice_whose_Active_arrives_before_the_cache_reads_that_slice_off_itself()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: false, mhz: 14.100);
            Slice b = ReadySlice(1, tx: false, active: true, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);
            SliceRoles before = SliceRoles.Of(rig);
            Assert.Equal("B", before.SelectedLetter);          // positive control: B is selected,
            Assert.Equal(7_150_000UL, before.SelectedHz);       // on its own frequency
            Assert.Equal(7_150_000UL, rig.RXFrequency);

            rig.RXVFO = 0;                                       // the production setter: A selected, Active queued
            Set(b, "_active", false);
            Set(a, "_active", true);
            SliceReports(rig, a, "Active");                      // FlexLib raises A's Active first

            SliceRoles roles = SliceRoles.Of(rig);
            Assert.Equal("A", roles.SelectedLetter);
            Assert.Equal(14_100_000UL, roles.SelectedHz);       // not B's 7.150
            Assert.Equal("A", roles.TransmitLetter);
            Assert.Equal(14_100_000UL, roles.TransmitHz);
            Assert.True(roles.Same);
            Assert.Equal(Lexicon.Get("connect.home.frequency", ("freqText", "14.100"), ("slice", "A")),
                         FrequencyKeyReadout.Sentence(FrequencyKeySlices.Transmit, roles, Mhz));
            // The tuning arithmetic's own reading agrees: a step from here
            // starts from A's frequency, not B's.
            Assert.Equal(14_100_000UL, rig.RXFrequency);

            // And once the radio's ActiveSlice refreshes the cache, the same.
            typeof(FlexBase).GetMethod("NoteActiveSliceAsFound", Private)!.Invoke(rig, new object[] { a });
            SliceRoles after = SliceRoles.Of(rig);
            Assert.Equal(14_100_000UL, after.SelectedHz);
            Assert.Equal(14_100_000UL, rig.RXFrequency);
        }

        /// <summary>
        /// The same for the transmit cache: a cached transmit frequency
        /// belongs to the slice it was written for. When the designation moves
        /// to another slice before that slice reports, TXFrequency and the
        /// reading name the new slice with its own frequency.
        /// </summary>
        [Fact]
        public void A_transmit_cache_written_for_one_slice_is_never_read_as_anothers()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            Slice c = ReadySlice(2, tx: false, active: false, mhz: 21.300);
            SliceArrives(rig, a);
            SliceArrives(rig, c);
            Assert.Equal(14_100_000UL, rig.TXFrequency);        // positive control: the cache is A's

            // The radio moves the designation to C; neither report has reached
            // this class yet, so the cache still holds A's frequency.
            Set(a, "_isTransmitSlice", false);
            Set(c, "_isTransmitSlice", true);

            SliceRoles roles = SliceRoles.Of(rig);
            Assert.Equal("C", roles.TransmitLetter);
            Assert.Equal(21_300_000UL, roles.TransmitHz);
            Assert.Equal(21_300_000UL, rig.TXFrequency);
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
            long churns = 0;
            using var churning = new ManualResetEventSlim();
            var churner = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    SliceArrives(rig, churn);
                    SliceGoes(rig, churn);
                    Interlocked.Increment(ref churns);
                    churning.Set();
                }
            }) { IsBackground = true };
            churner.Start();
            long churnsAtStart = 0, churnsAtEnd = 0;
            try
            {
                // The readings must overlap real roster changes (Codex's
                // re-check of #496): wait for the churn to be running, and
                // count it across the readings.
                Assert.True(churning.Wait(TimeSpan.FromSeconds(10)));
                churnsAtStart = Interlocked.Read(ref churns);
                for (int i = 0; i < 20_000; i++)
                {
                    SliceRoles roles = SliceRoles.Of(rig);
                    Assert.Equal("B", roles.SelectedLetter);
                    Assert.Equal(7_150_000UL, roles.SelectedHz);
                    Assert.Equal("C", roles.TransmitLetter);
                    Assert.Equal(21_300_000UL, roles.TransmitHz);
                }
                churnsAtEnd = Interlocked.Read(ref churns);
            }
            finally
            {
                stop.Cancel();
                churner.Join();
            }
            Assert.True(churnsAtEnd > churnsAtStart, "the roster never changed while the readings ran");
        }
    }
}

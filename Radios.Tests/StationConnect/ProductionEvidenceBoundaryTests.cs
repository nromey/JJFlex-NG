using System;
using System.Collections.Generic;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The production evidence boundary (Track G2 re-review, sections 1.8
    /// and 5; memory project_flexlib_suppresses_equal_value_success): a real
    /// vendored Radio and Slice, a real setter, the real reply routing, the
    /// real status parse, and the PRODUCTION port and feeds on a real
    /// FlexBase. The first two tests pin the vendor fact this whole group
    /// rests on, with a positive control; the rest show the port and feeds
    /// confirm through the reply and record only the field the radio
    /// reported. No network, no radio.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ProductionEvidenceBoundaryTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ProductionEvidenceBoundaryTests));
        private readonly List<RigOnVendorRadio> _rigs = new();
        public void Dispose()
        {
            foreach (var r in _rigs) r.Dispose();
            _scope.Dispose();
        }

        private static int _serialCounter;
        private static string UniqueSerial()
        {
            int n = System.Threading.Interlocked.Increment(ref _serialCounter);
            return "9998-" + (1000 + Environment.ProcessId % 9000).ToString("D4") + "-" + (1000 + n).ToString("D4") + "-7236";
        }

        private RigOnVendorRadio NewRig()
        {
            var rig = new RigOnVendorRadio(UniqueSerial());
            _rigs.Add(rig);
            return rig;
        }

        // ── the vendor fact, pinned against the vendored code ──

        [Fact]
        public void FlexLibsOwnSetterPath_RaisesOnlyItsLocalEcho_AndNeitherTheSuccessReplyNorTheEqualValueStatusRaisesAgain()
        {
            var v = new VendorRadioFixture(UniqueSerial());
            var slice = v.NewSlice(0, 7, 7.1, "LSB");
            int freqNotifications = 0;
            slice.PropertyChanged += (s, e) => { if (e.PropertyName == "Freq") freqNotifications++; };

            slice.Freq = 14.25;                                   // the setter: cache first, then send, then echo
            Assert.Equal(1, freqNotifications);
            Assert.Equal("slice tune 0 14.250000", v.LastCommand);

            v.ReplyOk(v.LastSequence);                            // the radio: done
            Assert.Equal(1, freqNotifications);                   // SetFreqReply returns on 0

            slice.StatusUpdate("rf_frequency=14.250000");         // the radio: here is the value
            Assert.Equal(1, freqNotifications);                   // equal to the cache: skipped

            // The positive control: a DIFFERENT value is raised, so the
            // silence above is the vendor's equal-value skip, not a broken
            // fixture.
            slice.StatusUpdate("rf_frequency=14.300000");
            Assert.Equal(2, freqNotifications);
        }

        [Fact]
        public void FlexLibsProfileSetter_SuppressesTheConfirmingStatus_TheReplyPathDoesNot()
        {
            var setter = new VendorRadioFixture(UniqueSerial());
            setter.Status("profile tx list=Default^K5NER-TX");
            int viaSetter = 0;
            setter.Radio.PropertyChanged += (s, e) => { if (e.PropertyName == "ProfileTXSelection") viaSetter++; };
            setter.Radio.ProfileTXSelection = "K5NER-TX";        // cache first, SendCommand (no handler), echo
            Assert.Equal(1, viaSetter);
            setter.Status("profile tx current=K5NER-TX");          // the radio confirms: equal to the cache, skipped
            Assert.Equal(1, viaSetter);

            var reply = new VendorRadioFixture(UniqueSerial());
            reply.Status("profile tx list=Default^K5NER-TX");
            int viaReply = 0;
            CommandReply got = null;
            reply.Radio.PropertyChanged += (s, e) => { if (e.PropertyName == "ProfileTXSelection") viaReply++; };
            reply.Radio.SendReplyCommand((seq, code, text) => got = new CommandReply("profile tx load \"K5NER-TX\"", code, text),
                "profile tx load \"K5NER-TX\"");
            Assert.Equal(0, viaReply);                             // no echo: the cache is untouched
            reply.ReplyOk(reply.LastSequence);
            Assert.True(got != null && got.Acknowledged);          // the acknowledgment reaches us
            reply.Status("profile tx current=K5NER-TX");
            Assert.Equal(1, viaReply);                             // and the confirming status is a real raise
            Assert.Equal("K5NER-TX", reply.Radio.ProfileTXSelection);
        }

        // ── the production port: the tune goes through the reply path ──

        [Fact]
        public void TheProductionPort_TunesThroughTheReplyPath_AndTheRadiosReplyIsTheConfirmation()
        {
            var rig = NewRig();
            var slice = rig.AddOwnSlice(0, 7.1, "LSB");
            int echoes = 0;
            slice.PropertyChanged += (s, e) => echoes++;
            var replies = new List<CommandReply>();
            long armed = rig.Station.Sequence;

            string refusal = rig.ProductionPort().SetSliceFrequencyAndMode(0, 14_250_000, "USB", r => replies.Add(r));

            Assert.Null(refusal);
            Assert.Equal(new[] { "slice set 0 mode=USB", "slice tune 0 14.250000" }, rig.Vendor.Transport.Commands);
            Assert.Equal(0, echoes);                               // no setter, no local echo
            Assert.Equal(7.1, slice.Freq);                         // the vendor cache is untouched
            Assert.Empty(replies);

            rig.Vendor.ReplyOk(rig.Vendor.Transport.SequenceOf(0));
            rig.Vendor.ReplyOk(rig.Vendor.Transport.SequenceOf(1));
            Assert.Equal(2, replies.Count);
            Assert.All(replies, r => Assert.True(r.Acknowledged));
            Assert.Equal("slice tune 0 14.250000", replies[1].Command);

            // The radio's status for the CHANGED value differs from the
            // untouched cache, so FlexLib raises it and the production feed
            // records a genuine frequency receipt.
            slice.StatusUpdate("rf_frequency=14.250000");
            Assert.True(rig.Station.Snapshot().TunedSince(armed, 0, 14_250_000));
            long afterReport = rig.Station.Sequence;

            // The same status again is equal-value: nothing is raised, and
            // nothing needs to be — the reply already confirmed it.
            slice.StatusUpdate("rf_frequency=14.250000");
            Assert.Equal(afterReport, rig.Station.Sequence);
        }

        [Fact]
        public void TheProductionPort_RefusesALockedSlice_AndSendsNothing()
        {
            var rig = NewRig();
            var slice = rig.AddOwnSlice(0, 7.1, "LSB");
            typeof(Slice).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(slice, true);

            string refusal = rig.ProductionPort().SetSliceFrequencyAndMode(0, 14_250_000, "USB", _ => { });

            Assert.Contains("locked", refusal);
            Assert.Empty(rig.Vendor.Transport.Commands);
        }

        [Fact]
        public void TheProductionPort_ReportsARejectedTune_WithTheRadiosText()
        {
            var rig = NewRig();
            rig.AddOwnSlice(0, 7.1, "LSB");
            CommandReply last = null;
            rig.ProductionPort().SetSliceFrequencyAndMode(0, 14_250_000, "", r => last = r);
            Assert.Equal(new[] { "slice tune 0 14.250000" }, rig.Vendor.Transport.Commands);

            rig.Vendor.Reply(rig.Vendor.LastSequence, 0x50000002, "14.150000");

            Assert.NotNull(last);
            Assert.False(last.Acknowledged);
            Assert.Equal("14.150000", last.Text);
        }

        // ── the production slice feed: one field per notification ──

        [Fact]
        public void TheProductionSliceFeed_RecordsOnlyTheFieldTheRadioReported_AModeReportNeverBlessesALocalFrequency()
        {
            // The vendor cache holds a frequency that was only ever assigned
            // locally (what Slice.Freq's setter leaves behind). The radio
            // then reports a MODE change. Until Track G3 the handler
            // snapshotted both fields and this counted as a tune to 14.25.
            var rig = NewRig();
            var slice = rig.AddOwnSlice(0, 7.1, "LSB");
            long armed = rig.Station.Sequence;
            VendorRadioFixture.SetFrequencyCache(slice, 14.25);

            slice.StatusUpdate("mode=USB");

            var s = rig.Station.Snapshot();
            Assert.True(s.ModeReportedSince(armed, 0, "USB"));
            Assert.False(s.TunedSince(armed, 0, 14_250_000), "a mode report confirmed a frequency the radio never reported");
            Assert.Equal(0, s.Slices[0].FreqSequence);
        }

        // ── the production selection dispatch: reply plus report ──

        [Fact]
        public void TheProductionSelectionDispatch_SendsThroughTheReplyPath_AndTheReportIsGenuine()
        {
            var rig = NewRig();
            rig.Vendor.Status("profile tx list=Default^K5NER-TX");
            rig.Vendor.Status("profile tx current=Default");
            var op = rig.Rig.StationAttempt.BeginOperation("test");
            var dispatch = typeof(FlexBase).GetMethod("DispatchSelectionChecked", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(dispatch);
            var action = new ProfileAction { Kind = ProfileActionKind.LoadOurs, ProfileType = ProfileTypes.tx, ProfileName = "K5NER-TX" };
            CommandReply reply = null;
            long seqBefore = rig.Profiles.Sequence;

            var outcome = (ProfileActionOutcome)dispatch!.Invoke(rig.Rig, new object[]
            {
                action, op, (Func<string>)(() => null), (Action<CommandReply>)(r => reply = r),
            })!;

            Assert.Equal(ProfileActionOutcome.Sent, outcome);
            Assert.Equal(new[] { "profile tx load \"K5NER-TX\"" }, rig.Vendor.Transport.Commands);
            Assert.Null(reply);
            Assert.Equal("Default", rig.Vendor.Radio.ProfileTXSelection);   // cache untouched by the send

            rig.Vendor.ReplyOk(rig.Vendor.LastSequence);
            Assert.True(reply != null && reply.Acknowledged);

            rig.Vendor.Status("profile tx current=K5NER-TX");
            var reported = rig.Profiles.Snapshot().ReportedSelectionOf(ProfileTypes.tx);
            Assert.NotNull(reported);
            Assert.Equal("K5NER-TX", reported.Name);
            Assert.True(reported.Sequence > seqBefore);
            Assert.Equal(ObservationProvenance.RadioReported, reported.Provenance);
        }

        // ── the production inventory ask returns only an answer, never the cache ──

        [Fact]
        public void TheProductionInventoryAsk_ReturnsNullWhenNothingAnswers_EvenWithAListCached()
        {
            var rig = NewRig();
            rig.Vendor.Status("profile global list=Default^K5NER");          // a list from earlier in the session
            Assert.NotNull(rig.Profiles.Snapshot().GlobalList);

            var answer = rig.ProductionPort().RequestGlobalInventory(150);   // the radio does not answer this ask

            Assert.Null(answer);
            Assert.Contains("profile global info", rig.Vendor.Transport.Commands);
        }

        [Fact]
        public void TheProductionInventoryAsk_ReturnsTheAnswerThatArrivedAfterIt()
        {
            var rig = NewRig();
            rig.Vendor.Status("profile global list=Default");
            long seqBefore = rig.Profiles.Sequence;
            var radioAnswers = new System.Threading.Thread(() =>
            {
                for (int i = 0; i < 100 && rig.Vendor.Transport.Written.Count == 0; i++) System.Threading.Thread.Sleep(10);
                rig.Vendor.Status("profile global list=Default^K5NER-8600");
            });
            radioAnswers.Start();

            var answer = rig.ProductionPort().RequestGlobalInventory(2000);
            radioAnswers.Join();

            Assert.NotNull(answer);
            Assert.True(answer.Sequence > seqBefore);
            Assert.True(answer.Contains("K5NER-8600"));
        }

        // ── the evidence log keeps the last radio report through a local echo ──

        [Fact]
        public void ALocalEchoAfterARadioReport_DoesNotEraseTheReport()
        {
            var clock = new FakeStationClock();
            var log = new ProfileEvidenceLog(clock);
            log.Reset(7);
            log.SelectionObserved(ProfileTypes.tx, "Default", ObservationProvenance.RadioReported, 7);
            log.SelectionObserved(ProfileTypes.tx, "K5NER-TX", ObservationProvenance.LocalEcho, 7);

            var snap = log.Snapshot();
            Assert.Equal("K5NER-TX", snap.SelectionOf(ProfileTypes.tx).Name);       // the latest, any provenance
            Assert.NotNull(snap.ReportedSelectionOf(ProfileTypes.tx));
            Assert.Equal("Default", snap.ReportedSelectionOf(ProfileTypes.tx).Name); // the last radio report survives
        }
    }
}

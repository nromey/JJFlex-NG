using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// A command transport the vendored Radio writes to, so a test can read
    /// exactly what FlexLib sent and replay the radio's reply by sequence
    /// number. No socket, no thread.
    /// </summary>
    // Derives from the vendor's producer-identity base (Track H, #637) rather
    // than implementing the interface by hand: the base owns the immutable
    // CommandConnection, the qualified ConnectionChanged/DataReceived events
    // and the identity-returning Connect, so this fake stays a real
    // transport as far as the contract is concerned. Its one connection is
    // begun and published connected at construction, so IsConnected reads
    // true as it always did here; nothing subscribes to the fake's events —
    // the Radio's own subscriptions are on the transport it built, and this
    // one is planted into the field afterwards, as before.
    internal sealed class FakeCommandTransport : CommandCommunicationBase
    {
        public readonly List<string> Written = new List<string>();
        private IPAddress _localIp = IPAddress.Loopback;
        public override IPAddress LocalIp { get => _localIp; set => _localIp = value; }
        public FakeCommandTransport()
        {
            var connection = BeginConnection();
            PublishConnected(connection);
        }
        public override bool Connect(IPAddress radioIp, int radioPort, int srcPort, out CommandConnection connection)
        {
            connection = CurrentConnection;
            return true;
        }
        public override void Disconnect() { }

        /// <summary>
        /// The link falls (repair round 4): FlexLib marks the current
        /// connection Disconnected and KEEPS it as the current one, as the
        /// real transports do.
        /// </summary>
        public void Fall()
        {
            var connection = CurrentConnection;
            if (connection != null) PublishDisconnected(connection);
        }

        /// <summary>The same transport connects again: a NEW command connection, as a same-Radio reconnect makes.</summary>
        public void Reconnect()
        {
            Fall();
            var connection = BeginConnection();
            PublishConnected(connection);
        }

        /// <summary>
        /// A line arriving from the radio, as the real transport delivers one:
        /// to every <c>DataReceived</c> subscriber, with this connection. The
        /// Radio's own parser is not subscribed to this fake (see above); what
        /// is, is the app's own reader of the radio's interlock reports (repair
        /// round 3, <c>Radios.InterlockReports</c>).
        /// </summary>
        public void Deliver(string line)
        {
            var connection = CurrentConnection;
            if (connection != null) PublishData(connection, line);
        }

        public Action<string>? CommandWritten;

        /// <summary>The next admitted write fails before its bytes, as a throwing writer does (repair round 9).</summary>
        public volatile bool FailNextWrite;

        /// <summary>Every admitted write whose command starts with this fails, until cleared (repair round 10).</summary>
        public volatile string? FailEveryWriteOf;
        public override void Write(string msg)
        {
            // The write-time gate (MIGRATION.md item 21) as the real adapters
            // apply it: this fake's one attempt is its current connection. And
            // the outcome, after the "writer" (item 22): a write the test makes
            // fail is not recorded and refuses its pin, as a throwing writer does.
            var pin = CurrentPin;
            if (!AdmitWrite(CurrentConnection)) return;
            // And the write order (item 25), as the real adapters apply it: the
            // pin's question at the bytes and the "writer" under the write gate,
            // the outcome told after it is let go.
            string? failAll = FailEveryWriteOf;
            string? refused = null;
            bool failed = false;
            EnterWriteGate();
            try
            {
                if (!AdmitAtTheBytes(pin, out refused)) { }
                else if (FailNextWrite || (failAll != null && CommandText(msg ?? "").StartsWith(failAll, StringComparison.Ordinal)))
                {
                    FailNextWrite = false;
                    failed = true;
                }
                else lock (Written) Written.Add(msg ?? "");
            }
            finally { ExitWriteGate(); }
            if (refused != null) { ReportRefusal(pin, refused); return; }
            if (failed) { ReportWrite(pin, false, "the write failed (test)"); return; }
            ReportWrite(pin, true);
            CommandWritten?.Invoke(CommandText(msg ?? ""));
        }

        /// <summary>The commands as text, without the "C&lt;seq&gt;|" prefix or the newline.</summary>
        public List<string> Commands
        {
            get { lock (Written) return Written.Select(CommandText).ToList(); }
        }

        /// <summary>The sequence number FlexLib stamped on the i-th write.</summary>
        public int SequenceOf(int i)
        {
            string w;
            lock (Written) w = Written[i];
            int bar = w.IndexOf('|');
            string head = w.Substring(0, bar);
            head = head.TrimStart('C', 'D');
            return int.Parse(head, CultureInfo.InvariantCulture);
        }

        public static string CommandText(string wire)
        {
            int bar = wire.IndexOf('|');
            return (bar < 0 ? wire : wire.Substring(bar + 1)).TrimEnd('\n', '\r');
        }
    }

    /// <summary>
    /// A real vendored <see cref="Radio"/> with a fake transport, reachable
    /// without a network: FlexLib's own setters, parsers, reply routing and
    /// PropertyChanged all run for real. This is what "replay a real setter
    /// plus its reply plus an equal-value status through the production
    /// feed" needs (Track G2 re-review, section 5). RosterProvenanceTests
    /// showed the Radio can be built; this adds the transport, the reply
    /// and status replays, and a Slice.
    /// </summary>
    internal sealed class VendorRadioFixture
    {
        public readonly Radio Radio;
        public readonly FakeCommandTransport Transport = new FakeCommandTransport();

        private static readonly MethodInfo ParseReplyMethod =
            typeof(Radio).GetMethod("ParseReply", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo ParseStatusMethod =
            typeof(Radio).GetMethod("ParseStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo TransportField =
            typeof(Radio).GetField("_commandCommunication", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public VendorRadioFixture(string serial)
        {
            Assert.True(ParseReplyMethod != null && ParseStatusMethod != null && TransportField != null,
                "the vendored Radio no longer has ParseReply/ParseStatus/_commandCommunication where this fixture reaches them; " +
                "every production-seam assertion built on it would be vacuous");
            Radio = (Radio)Activator.CreateInstance(typeof(Radio),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: new object[] { false }, culture: null)!;
            typeof(Radio).GetProperty(nameof(Radio.Serial))!.GetSetMethod(nonPublic: true)!.Invoke(Radio, new object[] { serial });
            TransportField.SetValue(Radio, Transport);
            typeof(Radio).GetProperty(nameof(Radio.Connected))!.GetSetMethod(nonPublic: true)!.Invoke(Radio, new object[] { true });
        }

        /// <summary>The radio answers command <paramref name="seq"/> with success.</summary>
        public void ReplyOk(int seq) => Reply(seq, 0, "");

        /// <summary>The radio answers command <paramref name="seq"/>.</summary>
        public void Reply(int seq, uint code, string text) =>
            ParseReplyMethod.Invoke(Radio, new object[] { "R" + seq + "|" + code.ToString("X") + "|" + (text ?? "") });

        /// <summary>A status line from the radio, e.g. "profile tx current=K5NER-TX".
        /// Parsed by FlexLib, and delivered through the transport as the real
        /// line would be, so the app's own readers of it see it too.</summary>
        public void Status(string body)
        {
            ParseStatusMethod.Invoke(Radio, new object[] { "S0|" + body });
            Transport.Deliver("S0|" + body);
        }

        /// <summary>The last command written, as text.</summary>
        public string LastCommand => Transport.Commands.Last();
        public int LastSequence => Transport.SequenceOf(Transport.Written.Count - 1);

        /// <summary>A vendored Slice on this radio, as FlexLib would have
        /// built it from a status, at the given frequency and mode.</summary>
        public Slice NewSlice(int index, uint clientHandle, double mhz, string mode)
        {
            var slice = (Slice)Activator.CreateInstance(typeof(Slice),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: new object[] { Radio }, culture: null)!;
            typeof(Slice).GetProperty(nameof(Slice.Index))!.GetSetMethod(nonPublic: true)!.Invoke(slice, new object[] { index });
            typeof(Slice).GetProperty(nameof(Slice.ClientHandle))!.GetSetMethod(nonPublic: true)!.Invoke(slice, new object[] { clientHandle });
            SetFrequencyCache(slice, mhz);
            typeof(Slice).GetField("_demodMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(slice, mode);
            // A slice from a status is unlocked; the setter refuses a locked one.
            typeof(Slice).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(slice, false);
            return slice;
        }

        /// <summary>Assign the vendor's frequency cache directly: what
        /// Slice.Freq's setter does before it sends — a value that is only
        /// assigned locally and not yet the radio's.</summary>
        public static void SetFrequencyCache(Slice slice, double mhz) =>
            typeof(Slice).GetField("_freq", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(slice, mhz);
    }

    /// <summary>
    /// A real FlexBase with a vendored Radio planted as theRadio and our
    /// client handle set, so the PRODUCTION observation feeds and the
    /// production IStationPort can be driven. The settings statics belong to
    /// the calling test class, which holds the isolation scope and carries
    /// the collection attribute; this only plants and unplants.
    /// </summary>
    internal sealed class RigOnVendorRadio : IDisposable
    {
        public const uint OurHandle = 0x1A2B3C4D;

        public readonly FlexBase Rig;
        public readonly VendorRadioFixture Vendor;
        public readonly ObservationBinding Binding;

        private static readonly FieldInfo TheRadioField =
            typeof(FlexBase).GetField("theRadio", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo ClientHandleField =
            typeof(FlexBase).GetField("clientHandle", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo MySlicesField =
            typeof(FlexBase).GetField("mySlices", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo ObserveOwnSliceAddedMethod =
            typeof(FlexBase).GetMethod("ObserveOwnSliceAdded", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo ObserveOwnSliceReportedMethod =
            typeof(FlexBase).GetMethod("ObserveOwnSliceReported", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo ObserveRadioProfilePropertyMethod =
            typeof(FlexBase).GetMethod("ObserveRadioProfileProperty", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo StationTrackerField =
            typeof(FlexBase).GetField("_stationTracker", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo ProfileEvidenceField =
            typeof(FlexBase).GetField("_profileEvidence", BindingFlags.NonPublic | BindingFlags.Instance)!;

        public RigOnVendorRadio(string serial)
        {
            Assert.True(TheRadioField != null && ClientHandleField != null && MySlicesField != null
                && ObserveOwnSliceAddedMethod != null && ObserveOwnSliceReportedMethod != null && ObserveRadioProfilePropertyMethod != null
                && StationTrackerField != null && ProfileEvidenceField != null,
                "FlexBase's fields and feeds are not where this fixture reaches them; every production-seam assertion below would be vacuous");
            Rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", StationName = "K5TEST" }) { SuppressSpeech = true };
            Vendor = new VendorRadioFixture(serial);
            TheRadioField.SetValue(Rig, Vendor.Radio);
            ClientHandleField.SetValue(Rig, OurHandle);
            Rig.BeginStationAttempt(Vendor.Radio, "vendor fixture");
            // A connected radio reports its interlock at connect; the app reads
            // that report itself (repair round 3, #861 R3), so the fixture
            // starts where a real connect does: watched, and READY reported.
            // A test of "never reported" builds its own radio.
            Assert.True(Radios.InterlockReports.Watch(Vendor.Radio), "the interlock report watch did not attach to the fixture's transport");
            Vendor.Status("interlock state=READY");
            Binding = Rig.BindingFor(Vendor.Radio);
            // The PRODUCTION radio property feed, subscribed the way
            // radioPropertyChangedHandler subscribes it (pinned in
            // StationFirstWiringTests): every notification, with provenance.
            Vendor.Radio.PropertyChanged += (s, e) =>
                ObserveRadioProfilePropertyMethod.Invoke(Rig, new object[] { Binding, (Radio)s, e.PropertyName });
        }

        public StationTracker Station => (StationTracker)StationTrackerField.GetValue(Rig)!;
        public ProfileEvidenceLog Profiles => (ProfileEvidenceLog)ProfileEvidenceField.GetValue(Rig)!;

        /// <summary>
        /// Put one of our slices where sliceAdded would put it, and subscribe
        /// its PropertyChanged to the PRODUCTION field feed exactly as the
        /// handler's lambda does (the lambda's text is pinned in
        /// StationFirstWiringTests). sliceAdded itself is not called because
        /// it starts the slice-settle timer, which reaches an announcement.
        /// </summary>
        public Slice AddOwnSlice(int index, double mhz, string mode)
        {
            var slice = Vendor.NewSlice(index, OurHandle, mhz, mode);
            var mySlices = (List<Slice>)MySlicesField.GetValue(Rig)!;
            lock (mySlices) mySlices.Add(slice);
            ObserveOwnSliceAddedMethod.Invoke(Rig, new object[] { Binding, slice });
            slice.PropertyChanged += (s2, e2) =>
                ObserveOwnSliceReportedMethod.Invoke(Rig, new object[] { Binding, (Slice)s2, e2.PropertyName });
            return slice;
        }

        /// <summary>The production IStationPort over this rig.</summary>
        public IStationPort ProductionPort()
        {
            var type = typeof(FlexBase).GetNestedType("FlexStationPort", BindingFlags.NonPublic);
            Assert.NotNull(type);
            return (IStationPort)Activator.CreateInstance(type!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null, args: new object[] { Rig }, culture: null)!;
        }

        public void Dispose()
        {
            TheRadioField.SetValue(Rig, null); // never FlexBase's to disconnect
            try { Rig.Dispose(); } catch { }
        }
    }
}

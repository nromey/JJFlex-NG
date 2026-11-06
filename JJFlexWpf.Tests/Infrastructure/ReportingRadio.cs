using System;
using System.Net;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// A vendored <see cref="Radio"/> planted in an unconnected <see cref="FlexBase"/>,
/// with a fake command transport so the radio's interlock REPORTS reach the
/// app's own record of them (<c>Radios.InterlockReports</c>), as they do on a
/// real connection (junk box repair round 4). No socket, no thread of its own,
/// nothing keys: the transport writes nowhere.
/// </summary>
internal sealed class ReportingRadio : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public readonly FlexBase Rig;
    public readonly Radio Radio;
    public readonly FakeTransport Transport = new();

    public ReportingRadio()
    {
        Rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        Radio = (Radio)Activator.CreateInstance(typeof(Radio), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: new object[] { false }, culture: null)!;
        var field = typeof(Radio).GetField("_commandCommunication", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, "the vendored Radio's transport field moved; every report below would go nowhere");
        field!.SetValue(Radio, Transport);
        typeof(Radio).GetProperty(nameof(Radio.Connected))!.GetSetMethod(nonPublic: true)!.Invoke(Radio, new object[] { true });
        typeof(FlexBase).GetField("theRadio", Any)!.SetValue(Rig, Radio);
        // Radios.InterlockReports is internal to Radios; production installs the
        // watch at connect, so the fixture installs it the same way, by name.
        var watch = typeof(FlexBase).Assembly.GetType("Radios.InterlockReports")
            ?.GetMethod("Watch", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(watch != null && (bool)watch.Invoke(null, new object[] { Radio })!,
            "the interlock report watch did not attach");
    }

    /// <summary>Our cached Transmit, which follows FlexLib's Mox.</summary>
    /// <summary>
    /// The rig's command queue kept rather than dropped, and run only by
    /// <see cref="RunQueued"/> (repair round 9): a falling cue for our own key
    /// is anchored to its ON and OFF really being written, so a test decides
    /// when the queue writes them. Undone at dispose.
    /// </summary>
    public void QueueByHand() => SetMainLoop(true);

    /// <summary>Run every queued command now, on this thread, as the main loop runs them.</summary>
    public void RunQueued()
    {
        object q = typeof(FlexBase).GetField("q", Any)!.GetValue(Rig)!;
        var count = q.GetType().GetProperty("Count")!;
        var dequeue = q.GetType().GetMethod("Dequeue")!;
        var run = typeof(FlexBase).GetMethod("RunQueuedItem", Any)!;
        while ((int)count.GetValue(q)! > 0)
            run.Invoke(Rig, new[] { dequeue.Invoke(q, null)! });
    }

    private void SetMainLoop(bool on)
    {
        object q = typeof(FlexBase).GetField("q", Any)!.GetValue(Rig)!;
        q.GetType().GetProperty("MainLoop")!.SetValue(q, on);
    }

    public void MoxCache(bool on) => typeof(FlexBase).GetField("_Transmit", Any)!.SetValue(Rig, on);

    /// <summary>
    /// FlexLib's Mox through the vendor's own setter (its cache, and its
    /// command to a transport that writes nowhere), then the app's production
    /// bridge from that notification, <c>noteRadioMox</c>, as a connected rig
    /// receives it (repair round 5). Unlike <see cref="MoxCache"/>, a rising
    /// edge here begins a transmit episode, as it does in production.
    /// </summary>
    public void VendorMox(bool on)
    {
        Radio.Mox = on;
        typeof(FlexBase).GetMethod("noteRadioMox", Any)!.Invoke(Rig, new object[] { Radio.Mox });
    }

    /// <summary>For tests: the receive wait's fallback interval on this rig, in place of its 250 ms.</summary>
    public void ReceiveFallbackMs(int ms) =>
        typeof(FlexBase).GetField("ReceiveReportFallbackMsOverride", Any)!.SetValue(Rig, ms);

    /// <summary>
    /// The radio reports its interlock: FlexLib's cache first (its own parser
    /// runs first on a real connection), then the line through the transport,
    /// where the app's record reads it.
    /// </summary>
    public void Interlock(InterlockState state)
    {
        typeof(Radio).GetProperty(nameof(Radio.InterlockState))!.GetSetMethod(nonPublic: true)!.Invoke(Radio, new object[] { state });
        Transport.Deliver("S0|interlock state=" + Word(state));
    }

    /// <summary>A report in a word FlexLib does not know: its cache keeps what it had.</summary>
    public void InterlockUnrecognised(string word) => Transport.Deliver("S0|interlock state=" + word);

    private static string Word(InterlockState state) => state switch
    {
        InterlockState.Receive => "RECEIVE",
        InterlockState.Ready => "READY",
        InterlockState.NotReady => "NOT_READY",
        InterlockState.PTTRequested => "PTT_REQUESTED",
        InterlockState.Transmitting => "TRANSMITTING",
        InterlockState.TXFault => "TX_FAULT",
        InterlockState.Timeout => "TIMEOUT",
        InterlockState.StuckInput => "STUCK_INPUT",
        InterlockState.UnkeyRequested => "UNKEY_REQUESTED",
        _ => state.ToString().ToUpperInvariant(),
    };

    public void Dispose()
    {
        try { SetMainLoop(false); } catch { }
        typeof(FlexBase).GetField("theRadio", Any)!.SetValue(Rig, null);   // never FlexBase's to disconnect
        try { Rig.Dispose(); } catch { }
    }

    internal sealed class FakeTransport : CommandCommunicationBase
    {
        private IPAddress _localIp = IPAddress.Loopback;
        public override IPAddress LocalIp { get => _localIp; set => _localIp = value; }

        public FakeTransport()
        {
            var connection = BeginConnection();
            PublishConnected(connection);
        }

        public override bool Connect(IPAddress radioIp, int radioPort, int srcPort, out CommandConnection connection)
        {
            connection = CurrentConnection!;
            return true;
        }

        public override void Disconnect() { }

        // The write-time gate (MIGRATION.md item 21), as the real adapters apply it.
        public override void Write(string msg)
        {
            var pin = CurrentPin;
            if (!AdmitWrite(CurrentConnection)) return;
            ReportWrite(pin, true);
            AfterWrite?.Invoke(msg ?? "");
        }

        /// <summary>
        /// Called with each written command after its writer succeeded and
        /// before the caller resumes (repair round 10): where a radio's prompt
        /// answer can be parsed before the app's own bookkeeping.
        /// </summary>
        public Action<string>? AfterWrite;

        public void Deliver(string line)
        {
            var connection = CurrentConnection;
            if (connection != null) PublishData(connection, line);
        }

        /// <summary>The link falls: the connection is marked ended and kept as the current one, as FlexLib does.</summary>
        public void Fall()
        {
            var connection = CurrentConnection;
            if (connection != null) PublishDisconnected(connection);
        }
    }
}

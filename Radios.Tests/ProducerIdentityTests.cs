using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Xunit;

namespace Radios.Tests;

// Adapted from the two preserved September 26 producer probes. These enter the
// real adapter callback wiring and parser without Connect, sockets or a radio.
internal static class OfflineCommandProducer
{
    internal const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static object Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value)!;
    internal static object Call(object value, string name, params object[] args) =>
        value.GetType().GetMethod(name, Private)!.Invoke(value, args)!;
    internal static CommandCommunicationBase Transport(Radio radio) => (CommandCommunicationBase)Field(radio, "_commandCommunication");
    internal static Radio Radio(bool wan = true)
    {
        var radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { wan }, null)!;
        typeof(Radio).GetField("_ip", Private)!.SetValue(radio, IPAddress.Parse("192.0.2.1"));
        typeof(Radio).GetField("_serial", Private)!.SetValue(radio, "offline-" + Guid.NewGuid().ToString("N"));
        return radio;
    }
    internal static SslClientTls12 StartTls(Radio radio)
    {
        var client = new SslClientTls12("192.0.2.1", "4994", startPingThread: false);
        typeof(SslClientTls12).GetField("_isConnected", Private)!.SetValue(client, true);
        var transport = Transport(radio);
        Call(transport, "StartClient", client);
        Assert.True((bool)Call(transport, "PublishConnected", transport.CurrentConnection!));
        return client;
    }
    internal static void Data(Radio radio, CommandConnection connection, string text) =>
        Call(Transport(radio), "PublishData", connection, text);
    internal static void FinishTls(SslClientTls12 client) => typeof(SslClientTls12)
        .GetProperty("IsConnected")!.GetSetMethod(true)!.Invoke(client, new object[] { false });
    internal static CancellationTokenSource Cancellation(SslClientTls12 client) => (CancellationTokenSource)Field(client, "_cts");
    internal static void Wire(FlexBase rig, Radio radio) => Call(rig, "wireRadioPropertyHandler", radio);
    internal static void Release(FlexBase rig, Radio radio)
    {
        Call(rig, "unwireRadioPropertyHandler", radio);
        rig.theRadio = null;
        rig.Dispose();
    }
}

public sealed class ProducerIdentityTests
{
    [Fact]
    public void An_unmatched_broker_result_does_not_supply_an_account_for_another_transport()
    {
        var radio = OfflineCommandProducer.Radio();
        var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        try
        {
            rig.theRadio = radio;
            radio.WANConnectionHandle = "new-handle";
            OfflineCommandProducer.Call(rig, "rememberCommandRoute", radio, "old-handle", "old-account");
            OfflineCommandProducer.Wire(rig, radio);
            OfflineCommandProducer.StartTls(radio);
            Assert.Null(rig.CurrentConnectionBinding.AccountId);
            Assert.True(rig.CurrentConnectionBinding.IsWan);
        }
        finally { OfflineCommandProducer.Release(rig, radio); }
    }

    [Fact]
    public void Ordinary_TCP_writes_do_not_wait_for_the_reader_dispatch_gate()
    {
        var radio = OfflineCommandProducer.Radio(wan: false);
        var transport = OfflineCommandProducer.Transport(radio);
        var attempt = OfflineCommandProducer.Call(transport, "BeginAttempt", IPAddress.Parse("192.0.2.1"), 4992, 0);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        attempt.GetType().GetField("Writer")!.SetValue(attempt, writer);
        OfflineCommandProducer.Call(transport, "PublishConnected", transport.CurrentConnection!);
        System.Threading.Tasks.Task writing;
        bool completed;
        lock (transport.ConnectionSync)
        {
            writing = System.Threading.Tasks.Task.Run(() => transport.Write("offline-memory-only"));
            completed = writing.Wait(TimeSpan.FromSeconds(5));
        }
        writing.GetAwaiter().GetResult();
        Assert.True(completed, "A command sender holding a vendor collection lock must not wait for reader dispatch.");
        Assert.True(stream.Length > 0);
        transport.Disconnect();
    }

    [Fact]
    public void A_cancelled_TLS_attempt_has_an_identity_but_cannot_claim_an_established_connection_loss()
    {
        var radio = OfflineCommandProducer.Radio();
        var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        try
        {
            rig.theRadio = radio;
            OfflineCommandProducer.Wire(rig, radio);
            var client = new SslClientTls12("192.0.2.1", "4994", startPingThread: false);
            var transport = OfflineCommandProducer.Transport(radio);
            OfflineCommandProducer.Call(transport, "StartClient", client);
            var binding = rig.CurrentConnectionBinding;
            Assert.NotNull(binding);
            int losses = 0;
            rig.ConnectionStateChanged += connected => { if (!connected) losses++; };
            transport.Disconnect();
            Assert.True(OfflineCommandProducer.Cancellation(client).IsCancellationRequested);
            Assert.False(binding.Lifetime.LossClaimed);
            Assert.Equal(0, losses);
            Assert.False((bool)OfflineCommandProducer.Call(transport, "PublishConnected", binding.Connection));
            Assert.False(radio.Connected);
        }
        finally { OfflineCommandProducer.Release(rig, radio); }
    }

    [Fact]
    public void Reentrant_connect_during_a_loss_cannot_install_a_connection_that_old_teardown_would_destroy()
    {
        var radio = OfflineCommandProducer.Radio();
        OfflineCommandProducer.StartTls(radio);
        bool? result = null;
        radio.CommandConnectionChanged += report =>
        {
            // Radio.Connect returns at its teardown guard, before any socket work.
            if (report.State == CommandConnectionState.Disconnected) result = radio.Connect();
        };
        OfflineCommandProducer.Transport(radio).Disconnect();
        Assert.False(result);
        Assert.False(radio.Connected);
        OfflineCommandProducer.StartTls(radio);
        Assert.True(radio.Connected);
        OfflineCommandProducer.Transport(radio).Disconnect();
    }

    [Fact]
    public void Client_report_values_survive_collection_updates_and_reentrant_loss_stops_old_parser_mutation()
    {
        var radio = OfflineCommandProducer.Radio();
        OfflineCommandProducer.StartTls(radio);
        var connection = radio.CurrentCommandConnection;
        RadioClientReport observed = null;
        radio.ClientReported += report => observed = report;
        OfflineCommandProducer.Data(radio, connection,
            "S0|client 7 connected client_id=original program=Test station=ORIGINAL local_ptt=1");
        Assert.NotNull(observed);
        radio.UpdateGuiClientsList(new List<GUIClient> { new GUIClient(7, null, "List", "CHANGED", false) });
        Assert.Equal("ORIGINAL", observed.Station);
        Assert.Equal("original", observed.ClientId);
        Assert.True(observed.LocalPtt);
        radio.ClientReported += _ => radio.Disconnect();
        OfflineCommandProducer.Data(radio, connection, "H99");
        Assert.False(radio.Connected);
        Assert.Equal(0u, radio.ClientHandle);
    }

    [Fact]
    public void Old_TLS_completion_and_saved_application_callback_cannot_claim_the_reused_Radios_new_connection()
    {
        var savedHook = CaptureArchive.ArchiveHook;
        var savedQueue = CaptureArchive.Queue;
        var savedSession = TraceSessionContext.Current;
        var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        var radio = OfflineCommandProducer.Radio();
        int archives = 0;
        try
        {
            CaptureArchive.ForgetClaimForTests();
            TraceCoordinator.ResetClaimsForTests();
            TraceSessionContext.BeginSession();
            CaptureArchive.ArchiveHook = _ => { archives++; return new CaptureArchiveResult(); };
            CaptureArchive.Queue = work => work();
            rig.theRadio = radio;
            OfflineCommandProducer.Wire(rig, radio);
            var oldClient = OfflineCommandProducer.StartTls(radio);
            var first = rig.CurrentConnectionBinding.Lifetime;
            var savedCallback = (Action<CommandConnectionChanged>)OfflineCommandProducer.Field(radio, "CommandConnectionChanged");
            CommandConnectionChanged oldLoss = null;
            radio.CommandConnectionChanged += report =>
            {
                if (report.State == CommandConnectionState.Disconnected) oldLoss = report;
            };
            OfflineCommandProducer.Transport(radio).Disconnect();
            Assert.Equal(1, archives);
            Assert.True(first.LossClaimed);
            Assert.False(radio.Connected);

            var newClient = OfflineCommandProducer.StartTls(radio);
            var second = rig.CurrentConnectionBinding.Lifetime;
            var transport = OfflineCommandProducer.Transport(radio);
            // Already connected: this exercises the public identity-returning contract
            // without opening a socket. The returned reference cannot follow later reuse.
            Assert.True(transport.Connect(IPAddress.None, 4994, 0, out var returnedConnection));
            Assert.Same(rig.CurrentConnectionBinding.Connection, returnedConnection);
            Assert.NotSame(first, second);
            Assert.True(radio.Connected);
            Assert.True(rig.IsConnected);
            savedCallback(oldLoss!);
            Assert.False(second.LossClaimed);
            Assert.True(rig.IsConnected);

            // The expensive probe's decisive step: the old wrapper's read loop completes.
            OfflineCommandProducer.FinishTls(oldClient);
            Assert.False(OfflineCommandProducer.Cancellation(newClient).IsCancellationRequested);
            Assert.True(radio.Connected);
            Assert.True(rig.IsConnected);
            Assert.False(second.LossClaimed);
            Assert.Equal(1, archives);

            OfflineCommandProducer.FinishTls(newClient);
            Assert.False(radio.Connected);
            Assert.False(rig.IsConnected);
            Assert.True(second.LossClaimed);
            Assert.Equal(2, archives);
            OfflineCommandProducer.Transport(radio).Disconnect();
            savedCallback(oldLoss!);
            Assert.Equal(2, archives);
        }
        finally
        {
            OfflineCommandProducer.Release(rig, radio);
            CaptureArchive.ArchiveHook = savedHook;
            CaptureArchive.Queue = savedQueue;
            CaptureArchive.ForgetClaimForTests();
            typeof(TraceCoordinator).GetMethod("RestoreSessionForTests", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { savedSession });
        }
    }

    [Fact]
    public void Departure_after_LAN_omission_still_reaches_the_consumer_with_the_executed_account_and_identity()
    {
        var radio = OfflineCommandProducer.Radio();
        var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        var evidence = new List<ConnectionClientReport>();
        try
        {
            rig.theRadio = radio;
            radio.WANConnectionHandle = "broker-result";
            OfflineCommandProducer.Call(rig, "rememberCommandRoute", radio, "broker-result", "executed-account");
            OfflineCommandProducer.Wire(rig, radio);
            rig.ConnectionClientReported += evidence.Add; // The roster consumer's subscription seam.
            OfflineCommandProducer.StartTls(radio);
            var connection = radio.CurrentCommandConnection;
            void Report(string text) => OfflineCommandProducer.Data(radio, connection, text);
            Report("H7");
            Assert.Equal(RadioClientReportKind.Handle, evidence.Single().Report.Kind);
            Assert.Equal(7u, evidence.Single().Report.Handle);
            Report("S0|client 7 connected client_id=test-id program=Test station=K5TEST local_ptt=0");
            Report("S0|client 7 disconnected forced=0");
            Assert.Single(evidence.Where(x => x.Report.Kind == RadioClientReportKind.Disconnected));
            Report("S0|client 7 connected client_id=test-id program=Test station=K5TEST local_ptt=0");
            var connectedReport = evidence.Last();
            int beforeList = evidence.Count;
            radio.UpdateGuiClientsList(new List<GUIClient>());
            Assert.Equal(beforeList, evidence.Count);
            Assert.Null(radio.FindGUIClientByClientHandle(7));
            Report("S0|client 7 disconnected forced=0");
            Assert.Equal(2, evidence.Count(x => x.Report.Kind == RadioClientReportKind.Disconnected));
            Assert.All(evidence, e =>
            {
                Assert.Same(connection, e.Report.Connection);
                Assert.Same(connection, e.Binding.Connection);
                Assert.Equal("executed-account", e.Binding.AccountId);
            });
            Assert.Equal("K5TEST", connectedReport.Report.Station);
            Assert.Equal("test-id", connectedReport.Report.ClientId);

            OfflineCommandProducer.Transport(radio).Disconnect();
            OfflineCommandProducer.StartTls(radio);
            int beforeStale = evidence.Count;
            Report("H99");
            Report("S0|client 7 connected client_id=stale program=Old station=OLD local_ptt=0");
            Assert.Equal(beforeStale, evidence.Count);
            Assert.Equal(0u, radio.ClientHandle);
        }
        finally { OfflineCommandProducer.Release(rig, radio); }
    }

    [Fact]
    public void TCP_old_reader_cleanup_leaves_new_cancellation_writer_and_completion_untouched()
    {
        var radio = OfflineCommandProducer.Radio(wan: false);
        var transport = OfflineCommandProducer.Transport(radio);
        object Start() => OfflineCommandProducer.Call(transport, "BeginAttempt", IPAddress.Parse("192.0.2.1"), 4992, 0);
        CommandConnection Identity(object attempt) => (CommandConnection)attempt.GetType().GetProperty("Connection")!.GetValue(attempt)!;
        CancellationTokenSource Cancellation(object attempt) => (CancellationTokenSource)attempt.GetType().GetProperty("Cancellation")!.GetValue(attempt)!;
        var old = Start();
        OfflineCommandProducer.Call(transport, "PublishConnected", Identity(old));
        transport.Disconnect();
        var current = Start();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream);
        current.GetType().GetField("Writer")!.SetValue(current, writer);
        OfflineCommandProducer.Call(transport, "PublishConnected", Identity(current));
        var completion = (System.Threading.Tasks.TaskCompletionSource<bool>)current.GetType().GetProperty("Completion")!.GetValue(current)!;
        OfflineCommandProducer.Call(transport, "FinishAttempt", old);
        Assert.False(Cancellation(current).IsCancellationRequested);
        Assert.Same(writer, current.GetType().GetField("Writer")!.GetValue(current));
        Assert.False(completion.Task.IsCompleted);
        Assert.True(radio.Connected);
        int losses = 0;
        radio.CommandConnectionChanged += e => { if (e.State == CommandConnectionState.Disconnected) losses++; };
        OfflineCommandProducer.Call(transport, "FinishAttempt", current);
        OfflineCommandProducer.Call(transport, "FinishAttempt", current);
        Assert.False(radio.Connected);
        Assert.True(Cancellation(current).IsCancellationRequested);
        Assert.True(completion.Task.IsCompletedSuccessfully);
        Assert.Equal(1, losses);
    }

    [Fact]
    public void Every_producer_patch_has_a_guarded_site_and_a_migration_item()
    {
        string root = CaptureMeterSetTests.RepoRoot();
        string Read(string path) => File.ReadAllText(Path.Combine(root, path));
        string migration = Read("MIGRATION.md");
        foreach (int item in Enumerable.Range(14, 5)) Assert.Contains(item + ". **", migration);
        foreach (string path in new[] { "CommandConnection.cs", "Interface/ICommandCommunication.cs", "TlsCommandCommunication.cs", "SslClientTls12.cs", "TcpCommandCommunication.cs", "Radio.cs" })
            Assert.Contains("JJFlex patch:", Read("FlexLib_API/FlexLib/" + path));
        string tls = Read("FlexLib_API/FlexLib/SslClientTls12.cs");
        Assert.Contains("connectTimeoutCts.Token, _cts.Token", tls);
        Assert.Contains("authenticationTimeoutCts.Token, _cts.Token", tls);
        Assert.True(tls.IndexOf("_writer = new StreamWriter", StringComparison.Ordinal)
            < tls.IndexOf("_connectTcs.TrySetResult(true)", StringComparison.Ordinal));
        string radio = Read("FlexLib_API/FlexLib/Radio.cs");
        Assert.Contains("_commandCommunication.ConnectionChanged += _commandCommunication_ConnectionChanged;", radio);
        Assert.DoesNotContain("_commandCommunication.ConnectionChanged -=", radio);
        Assert.Contains("lock (_commandCommunication.ConnectionSync)", radio);
        Assert.Contains("PublicTlsPort, 0, out connection)", radio);
        Assert.Contains("NegotiatedHolePunchPort, NegotiatedHolePunchPort, out connection)", radio);
        Assert.Contains("_ip, 4992, 0, out connection)", radio);
        Assert.DoesNotContain("connection = _commandCommunication.CurrentConnection;", radio);
        Assert.Contains("try { ParseRead(report.Text); }", radio);
        Assert.Contains("RadioClientReportKind.Handle, handle_uint", radio);
        Assert.Contains("RadioClientReportKind.Connected,", radio);
        Assert.Contains("RadioClientReportKind.Disconnected, handle_uint", radio);
        string flex = Read("Radios/FlexBase.cs");
        Assert.Contains("rememberCommandRoute(r, handle, session.AccountId);", flex);
        Assert.Contains("ConnectionLifetime.Bind(report.Connection,", flex);
    }
}

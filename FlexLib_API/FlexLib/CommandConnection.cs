#nullable enable
using System;

namespace Flex.Smoothlake.FlexLib;

// JJFlex patch: producer identity contract. Reapply with MIGRATION.md item 14.
/// <summary>One command transport attempt. Reference identity survives delayed callbacks.</summary>
public sealed class CommandConnection
{
    internal CommandConnection() { }
    public Guid Id { get; } = Guid.NewGuid();
    internal bool WasConnected { get; set; }
    public CommandConnectionState State { get; internal set; } = CommandConnectionState.Connecting;
}

public enum CommandConnectionState { Connecting, Connected, Disconnected }

public sealed class CommandConnectionChanged
{
    internal CommandConnectionChanged(CommandConnection connection, CommandConnectionState state)
    { Connection = connection; State = state; WasConnected = connection.WasConnected; }
    public CommandConnection Connection { get; }
    public CommandConnectionState State { get; }
    public bool WasConnected { get; }
}

public sealed class CommandDataReceived
{
    internal CommandDataReceived(CommandConnection connection, string text)
    { Connection = connection; Text = text; }
    public CommandConnection Connection { get; }
    public string Text { get; }
}

public enum RadioClientReportKind { Handle, Connected, Disconnected }

/// <summary>Immutable command-reader evidence, independent of discovery collection membership.</summary>
public sealed class RadioClientReport
{
    internal RadioClientReport(CommandConnection connection, RadioClientReportKind kind,
        uint handle, string? clientId = null, string? program = null, string? station = null,
        bool localPtt = false)
    {
        Connection = connection; Kind = kind; Handle = handle; ClientId = clientId;
        Program = program; Station = station; LocalPtt = localPtt;
    }
    public CommandConnection Connection { get; }
    public RadioClientReportKind Kind { get; }
    public uint Handle { get; }
    public string? ClientId { get; }
    public string? Program { get; }
    public string? Station { get; }
    public bool LocalPtt { get; }
}

// JJFlex patch: write-time gate (MIGRATION.md item 21). A caller pins its command
// writes, on this thread, to the CommandConnection it checked, with an optional
// predicate. The transport admits a pinned write only to that connection, still
// Connected, and only if the predicate still holds when the write really happens,
// asked under ConnectionSync so a report the reader has dispatched is seen. Nothing
// is held across I/O: the decision is made under the gate, the bytes go out after.
// A refused write is not written; the pin records why and calls the caller back.
// Unpinned writes (the emergency kill, every ordinary command) are untouched.
public sealed class CommandWritePin : IDisposable
{
    [ThreadStatic] private static CommandWritePin? _current;
    private readonly CommandWritePin? _outer;
    private bool _disposed;

    /// <summary>How long a predicate waits for the dispatch gate before the write fails closed.</summary>
    public static TimeSpan SerializeBound { get; set; } = TimeSpan.FromMilliseconds(100);

    private CommandWritePin(CommandConnection connection, Func<string?>? predicate, Action<string>? onRefused, bool sendWhenUnconfirmed,
        Func<string?>? atTheBytes)
    {
        Connection = connection;
        Predicate = predicate;
        OnRefused = onRefused;
        SendWhenUnconfirmed = sendWhenUnconfirmed;
        AtTheBytes = atTheBytes;
        _outer = _current;
        _current = this;
    }

    /// <summary>Pin this thread's command writes, until Dispose, to <paramref name="connection"/>.</summary>
    /// <param name="predicate">Asked at each write, under the transport's dispatch gate: null to write, else why not. It must only read state: no I/O, no locks.</param>
    /// <param name="onRefused">Told why, on the writing thread, when a write is refused.</param>
    /// <param name="sendWhenUnconfirmed">For an UNKEY of the caller's own carrier: when the
    /// dispatch gate cannot be had in time, or the predicate throws, the write still goes,
    /// after the predicate is asked without the gate. Only the predicate's positive answer,
    /// or a connection that is not the pinned one, refuses it. Never refused by contention.</param>
    /// <param name="atTheBytes">JJFlex patch: write order (MIGRATION.md item 25). Asked
    /// under the adapter's write gate, after admission and immediately before the
    /// writer, with no other lock: null to write, else why not. For a restore's
    /// "has the operator chosen since" question, whose answer a local write by the
    /// command loop can change. It must only read memory: no I/O, no locks.</param>
    public static CommandWritePin Pin(CommandConnection connection, Func<string?>? predicate = null, Action<string>? onRefused = null,
        bool sendWhenUnconfirmed = false, Func<string?>? atTheBytes = null) =>
        new CommandWritePin(connection ?? throw new ArgumentNullException(nameof(connection)), predicate, onRefused, sendWhenUnconfirmed,
            atTheBytes);

    internal static CommandWritePin? Current => _current;
    public CommandConnection Connection { get; }
    public Func<string?>? Predicate { get; }
    private Action<string>? OnRefused { get; }

    /// <summary>An unkey: contention or an unanswerable predicate never refuses it.</summary>
    public bool SendWhenUnconfirmed { get; }

    /// <summary>JJFlex patch: write order (MIGRATION.md item 25). The question asked at the bytes, under the write gate; null for none.</summary>
    public Func<string?>? AtTheBytes { get; }

    // JJFlex patch: write order (MIGRATION.md item 25), rulings round two (Codex's R2).
    [ThreadStatic] private static int _urgentDepth;

    /// <summary>JJFlex patch: write order (MIGRATION.md item 25). This thread's writes are urgent: a forced OFF.</summary>
    internal static bool UrgentNow => _urgentDepth > 0;

    /// <summary>
    /// JJFlex patch: write order (MIGRATION.md item 25). Mark this thread's
    /// command writes, until Dispose, as urgent: at the adapter's write gate they
    /// go before every ordinary waiter. Only for a forced OFF.
    /// </summary>
    public static IDisposable UrgentWrites() => new UrgentScope();

    private sealed class UrgentScope : IDisposable
    {
        private bool _disposed;
        public UrgentScope() => _urgentDepth++;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _urgentDepth--;
        }
    }

    /// <summary>The first refusal of a write under this pin, or null.</summary>
    public string? Refusal { get; private set; }

    /// <summary>Writes admitted under this pin (the gate said yes; nothing is yet known about the bytes).</summary>
    public int Admitted { get; private set; }

    /// <summary>
    /// JJFlex patch (MIGRATION.md item 22): writes whose writer RETURNED without an
    /// exception, the bytes handed to the stream. Never counted at admission: a
    /// writer that is missing or throws refuses the pin instead (Codex's #596 B15).
    /// </summary>
    public int Written { get; private set; }

    internal void Refuse(string why)
    {
        Refusal ??= why;
        try { OnRefused?.Invoke(why); } catch { }
    }

    internal void Admit() => Admitted++;

    internal void Delivered(bool ok, string? why)
    {
        if (ok)
        {
            Written++;
            try { OnWritten?.Invoke(); } catch { }
        }
        else Refuse(why ?? "the write failed");
    }

    /// <summary>
    /// JJFlex patch (MIGRATION.md item 22): called on the writing thread at the
    /// moment a pinned write's writer has returned without an exception, before
    /// the caller resumes (round 10, Codex's #863 F2). The app records what the
    /// radio had reported by then, so a report parsed after this point counts as
    /// after the write. It must only read state: no I/O, no locks.
    /// </summary>
    public Action? OnWritten { get; set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current = _outer;
    }
}

// JJFlex patch: write order (MIGRATION.md item 25). One writer at a time per
// adapter; an urgent write (a forced OFF) goes before every ordinary waiter
// (rulings round two, Codex's R2). Monitor-based, so it costs nothing when free.
public sealed class CommandWriteGate
{
    private readonly object _sync = new object();
    private bool _held;
    private int _urgentWaiting;

    internal CommandWriteGate() { }

    /// <summary>Take the gate: an urgent writer waits only for the holder; an ordinary one also for every urgent waiter.</summary>
    public void Enter(bool urgent)
    {
        lock (_sync)
        {
            if (urgent) _urgentWaiting++;
            try
            {
                while (_held || (!urgent && _urgentWaiting > 0))
                    System.Threading.Monitor.Wait(_sync);
            }
            finally
            {
                if (urgent) _urgentWaiting--;
            }
            _held = true;
        }
    }

    /// <summary>Take the gate only if it can be had at once.</summary>
    public bool TryEnter(bool urgent = false)
    {
        lock (_sync)
        {
            if (_held || (!urgent && _urgentWaiting > 0)) return false;
            _held = true;
            return true;
        }
    }

    /// <summary>Let it go; every waiter re-checks, an urgent one first by the rule above.</summary>
    public void Exit()
    {
        lock (_sync)
        {
            _held = false;
            System.Threading.Monitor.PulseAll(_sync);
        }
    }
}

// JJFlex patch: both adapters serialize identity selection and delivery on this gate.
// No socket wait holds it. Radio's teardown uses the same gate, so an old callback
// cannot pass a check and then tear down a replacement installed in the gap.
public abstract class CommandCommunicationBase : ICommandCommunication
{
    public object ConnectionSync { get; } = new object();

    // JJFlex patch: write order (MIGRATION.md item 25). One write at a time per
    // adapter, from its question at the bytes to the writer returning, so two LOCAL
    // writes cannot overtake each other between a decision and its bytes (Codex's
    // #596 B4 of the 2026-11-02 junk box: a VOX restore admitted, preempted before
    // its writer, an operator OFF written by the command loop meanwhile, then the
    // restore's ON). Every write takes it, pinned or not, the emergency kill's OFF
    // included: a StreamWriter is not safe for two writers anyway.
    //
    // WHAT HOLDS IT, AND FOR HOW LONG: one Write or WriteAsync, for (a) its pin's
    // AtTheBytes question, which only reads memory, and (b) handing one command
    // line to the stream (AutoFlush: the socket send, which returns once the
    // kernel has buffered the line). Never the dispatch gate's wait: AdmitWrite's
    // question under ConnectionSync, bounded by SerializeBound, is asked BEFORE
    // this gate is taken, and nothing is taken while it is held. The refusal and
    // delivery callbacks run after it is released.
    //
    // WHO GOES NEXT (rulings round two, Codex's R2): an URGENT write, a forced OFF
    // (Radio.SendMoxOff and SendTuneOff, marked with CommandWritePin.UrgentWrites),
    // goes before every ordinary write already waiting. So an OFF waits for at
    // most the line in progress when it arrives (and any other OFF ahead of it),
    // never behind an ordinary backlog. Ordinary writes wait while an OFF waits.
    // Only a stalled link (the kernel's send buffer full) makes the line in
    // progress long, and then no write can go out on that connection anyway.
    // The OFF is never exempt from the gate: that would let two writers into
    // one StreamWriter and reopen B4.
    private readonly CommandWriteGate _writeGate = new CommandWriteGate();

    /// <summary>JJFlex patch: write order (MIGRATION.md item 25). Take the write gate, an urgent write first.</summary>
    protected void EnterWriteGate() => _writeGate.Enter(CommandWritePin.UrgentNow);

    /// <summary>JJFlex patch: write order (MIGRATION.md item 25). Take the write gate off the caller's thread.</summary>
    protected System.Threading.Tasks.Task EnterWriteGateAsync()
    {
        bool urgent = CommandWritePin.UrgentNow;   // read on the caller's thread, before any await
        return _writeGate.TryEnter(urgent) ? System.Threading.Tasks.Task.CompletedTask
            : System.Threading.Tasks.Task.Run(() => _writeGate.Enter(urgent));
    }

    /// <summary>JJFlex patch: write order (MIGRATION.md item 25). Let the write gate go.</summary>
    protected void ExitWriteGate() => _writeGate.Exit();

    /// <summary>
    /// JJFlex patch: write order (MIGRATION.md item 25). Called UNDER the write
    /// gate, immediately before the writer: the pin's question at the bytes. True
    /// to write. Refused, nothing is written; the caller reports the refusal to
    /// the pin after releasing the gate (<see cref="ReportRefusal"/>) with
    /// <paramref name="why"/>. A question that throws refuses (fails closed),
    /// except for an unkey of the caller's own carrier, which no unanswerable
    /// question may stop.
    /// </summary>
    protected static bool AdmitAtTheBytes(CommandWritePin? pin, out string? why)
    {
        why = null;
        var question = pin?.AtTheBytes;
        if (question == null) return true;
        try { why = question(); }
        catch (Exception ex) { why = pin!.SendWhenUnconfirmed ? null : "the write's own check at the bytes failed (" + ex.Message + ")"; }
        return why == null;
    }

    /// <summary>JJFlex patch: write order (MIGRATION.md item 25). A refusal at the bytes, told to the pin outside the write gate.</summary>
    protected static void ReportRefusal(CommandWritePin? pin, string why) => pin?.Refuse(why);
    public CommandConnection? CurrentConnection { get; private set; }
    public bool IsConnected => CurrentConnection?.State == CommandConnectionState.Connected;
    public abstract System.Net.IPAddress? LocalIp { get; set; }
    public bool Connect(System.Net.IPAddress radioIp, int radioPort = 4992, int srcPort = 0) =>
        Connect(radioIp, radioPort, srcPort, out _);
    public abstract bool Connect(System.Net.IPAddress radioIp, int radioPort, int srcPort, out CommandConnection? connection);
    public abstract void Disconnect();
    public abstract void Write(string msg);

    /// <summary>
    /// JJFlex patch (MIGRATION.md item 22): each adapter reports a pinned write's
    /// outcome AFTER its writer, against the pin that was current when the write
    /// began. <paramref name="ok"/> false (no writer, a writer that threw) refuses the
    /// pin, so the caller knows the command did not go out.
    /// </summary>
    protected static void ReportWrite(CommandWritePin? pin, bool ok, string? why = null) => pin?.Delivered(ok, why);

    /// <summary>JJFlex patch (MIGRATION.md item 22): the pin current on this thread, for an adapter to report its write against.</summary>
    protected static CommandWritePin? CurrentPin => CommandWritePin.Current;

    /// <summary>
    /// JJFlex patch: write-time gate (MIGRATION.md item 21). Called by each
    /// adapter's Write with the connection of the attempt it is about to write
    /// through. True to write. An unpinned write is always admitted.
    /// </summary>
    protected bool AdmitWrite(CommandConnection? target)
    {
        var pin = CommandWritePin.Current;
        if (pin == null) return true;
        if (!ReferenceEquals(target, pin.Connection))
        {
            pin.Refuse("the command connection it was pinned to is no longer the one being written");
            return false;
        }
        if (target!.State != CommandConnectionState.Connected)
        {
            pin.Refuse("the command connection it was pinned to has ended");
            return false;
        }
        var predicate = pin.Predicate;
        if (predicate != null)
        {
            string? why;
            if (!System.Threading.Monitor.TryEnter(ConnectionSync, CommandWritePin.SerializeBound))
            {
                // An unkey of our own carrier is never refused by contention: it asks
                // its question without the gate, and only a positive answer refuses.
                if (pin.SendWhenUnconfirmed)
                {
                    try { why = predicate(); }
                    catch { why = null; }
                }
                else why = "the radio's reports could not be read in time to confirm the write";
            }
            else
            {
                try
                {
                    // Under the gate the reader dispatches under: a report it has
                    // dispatched is seen, and none is dispatched while this decides.
                    why = !ReferenceEquals(CurrentConnection, target) || target.State != CommandConnectionState.Connected
                        ? "the command connection it was pinned to has changed or ended"
                        : predicate();
                }
                catch (Exception ex) { why = pin.SendWhenUnconfirmed ? null : "the write's own check failed (" + ex.Message + ")"; }
                finally { System.Threading.Monitor.Exit(ConnectionSync); }
            }
            if (why != null)
            {
                pin.Refuse(why);
                return false;
            }
        }
        pin.Admit();
        return true;
    }

    public event Action<CommandConnectionChanged>? ConnectionChanged;
    public event Action<CommandDataReceived>? DataReceived;
    public event TcpCommandCommunication.IsConnectedChangedEventHandler? IsConnectedChanged;
    public event TcpCommandCommunication.TcpDataReceivedReadyEventHandler? DataReceivedReady;

    protected CommandConnection BeginConnection()
    {
        lock (ConnectionSync)
        {
            if (CurrentConnection != null && CurrentConnection.State != CommandConnectionState.Disconnected)
                throw new InvalidOperationException("A command connection is already active.");
            var connection = new CommandConnection();
            CurrentConnection = connection;
            ConnectionChanged?.Invoke(new CommandConnectionChanged(connection, connection.State));
            return connection;
        }
    }

    protected bool PublishConnected(CommandConnection connection)
    {
        lock (ConnectionSync)
        {
            if (!ReferenceEquals(CurrentConnection, connection) || connection.State != CommandConnectionState.Connecting)
                return false;
            connection.WasConnected = true;
            connection.State = CommandConnectionState.Connected;
            ConnectionChanged?.Invoke(new CommandConnectionChanged(connection, connection.State));
            if (ReferenceEquals(CurrentConnection, connection) && connection.State == CommandConnectionState.Connected)
                IsConnectedChanged?.Invoke(true);
            return connection.State == CommandConnectionState.Connected;
        }
    }

    protected void PublishDisconnected(CommandConnection connection)
    {
        lock (ConnectionSync)
        {
            if (!ReferenceEquals(CurrentConnection, connection) || connection.State == CommandConnectionState.Disconnected)
                return;
            connection.State = CommandConnectionState.Disconnected;
            ConnectionChanged?.Invoke(new CommandConnectionChanged(connection, connection.State));
            IsConnectedChanged?.Invoke(false);
        }
    }

    protected void PublishData(CommandConnection connection, string text)
    {
        lock (ConnectionSync)
        {
            if (!ReferenceEquals(CurrentConnection, connection) || connection.State == CommandConnectionState.Disconnected)
                return;
            DataReceived?.Invoke(new CommandDataReceived(connection, text));
            DataReceivedReady?.Invoke(text);
        }
    }
}

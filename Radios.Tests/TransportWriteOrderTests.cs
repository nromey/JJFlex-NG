using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Codex's round-three re-check of the 2026-11-02 junk box integration
    /// fixes, B4: two LOCAL writes overtaking each other at the transport. A
    /// VOX or CW break-in restore was admitted by the write-time gate
    /// (<c>AdmitWrite</c>) and preempted before its writer ran; meanwhile the
    /// operator's OFF was queued and written by the command loop; then the
    /// restore's ON went out. The wire read OFF, OFF, ON, the cache said OFF
    /// and nothing was owed. No radio report is involved, so this is not the
    /// accepted interval of a radio change not yet received.
    /// <para>
    /// Driven as Codex drove it: the real FlexBase and its VOX owner, on the
    /// REAL TCP adapter (<see cref="TcpCommandCommunication"/>) with one
    /// attempt published Connected and an in-memory writer that can be paused
    /// inside its <c>Write</c>. No socket, no radio, nothing keys; the queue is
    /// drained by hand, through the production <c>RunQueuedItem</c>.
    /// </para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TransportWriteOrderTests : IDisposable
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const string Serial = "0596-0004-0004-0001";

        private readonly RadioConfigStaticsScope _scope = new(nameof(TransportWriteOrderTests));
        private readonly RigOnVendorRadio _f;
        private TcpCommandCommunication _tcp = null!;
        private PausingWriter _writer = null!;

        public TransportWriteOrderTests()
        {
            _f = new RigOnVendorRadio(Serial);
            _f.Rig.q.MainLoop = true;
        }

        public void Dispose()
        {
            _writer?.Resume.Set();
            _f.Rig.q.MainLoop = false;
            _f.Dispose();
            _scope.Dispose();
        }

        /// <summary>
        /// An in-memory StreamWriter that records every command line and, once,
        /// parks inside <c>Write</c> on the line <paramref name="pauseOn"/>: a
        /// writer thread preempted after the transport admitted the write and
        /// before the bytes left.
        /// </summary>
        private sealed class PausingWriter : StreamWriter
        {
            private readonly string? _pauseOn;
            private readonly List<string> _commands = new();
            private int _paused;
            public ManualResetEventSlim Entered { get; } = new();
            public ManualResetEventSlim Resume { get; } = new();
            public PausingWriter(string? pauseOn) : base(new MemoryStream()) { _pauseOn = pauseOn; AutoFlush = true; }
            public string[] Commands { get { lock (_commands) return _commands.ToArray(); } }
            public override void Write(string? value)
            {
                string line = value ?? "";
                string command = line[(line.IndexOf('|') + 1)..].Trim();
                if (command == _pauseOn && Interlocked.Exchange(ref _paused, 1) == 0)
                {
                    Entered.Set();
                    if (!Resume.Wait(5000)) throw new IOException("the test never resumed the paused writer");
                }
                lock (_commands) _commands.Add(command);
            }
        }

        /// <summary>
        /// The VOX setting (or CW break-in) on, its slice ours, and the radio's
        /// command transport replaced by the real TCP adapter whose writer is
        /// <see cref="_writer"/>; receive reported on that connection. The VOX
        /// owner is first made AFTER the swap, so it is pinned to this
        /// connection. Returns the setting and its ON and OFF command lines.
        /// </summary>
        private (FlexBase.VoxSetting Which, string On, string Off) OnTheRealTcpAdapter(bool cw, bool pauseTheRestore)
        {
            _f.AddOwnSlice(0, 14.1, cw ? "CW" : "USB");
            Set(_f.Rig, "_RXVFO", 0);
            Set(_f.Rig, "_TXVFO", 0);
            Set(_f.Rig, "_IsConnected", true);
            _f.Vendor.Status(cw ? "transmit break_in=1" : "transmit vox_enable=1");
            string on = cw ? "cw break_in 1" : "transmit set vox_enable=1";
            string off = cw ? "cw break_in 0" : "transmit set vox_enable=0";

            _tcp = new TcpCommandCommunication();
            var begin = typeof(TcpCommandCommunication).GetMethod("BeginAttempt", Any)!;
            object attempt;
            lock (_tcp.ConnectionSync) attempt = begin.Invoke(_tcp, new object[] { System.Net.IPAddress.Loopback, 4992, 0 })!;
            _writer = new PausingWriter(pauseTheRestore ? on : null);
            attempt.GetType().GetField("Writer")!.SetValue(attempt, _writer);
            var connection = (CommandConnection)attempt.GetType().GetProperty("Connection")!.GetValue(attempt)!;
            Assert.True((bool)typeof(CommandCommunicationBase).GetMethod("PublishConnected", Any)!.Invoke(_tcp, new object[] { connection })!);
            typeof(Radio).GetField("_commandCommunication", Any)!.SetValue(_f.Vendor.Radio, _tcp);
            Assert.Same(connection, _f.Vendor.Radio.CurrentCommandConnection);

            // Receive, as the reader would report it on this connection, under its gate.
            var received = typeof(InterlockReports).GetMethod("Received", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            var report = Activator.CreateInstance(typeof(CommandDataReceived), Any, null,
                new object[] { connection, "S0|interlock state=READY" }, null)!;
            lock (_tcp.ConnectionSync) received.Invoke(null, new[] { report });
            return (cw ? FlexBase.VoxSetting.CwBreakIn : FlexBase.VoxSetting.SimpleVox, on, off);
        }

        private void Drain()
        {
            while (_f.Rig.q.Count > 0) _f.Rig.RunQueuedItem(_f.Rig.q.Dequeue());
        }

        private string[] Wire(string on, string off) => _writer.Commands.Where(c => c == on || c == off).ToArray();

        private static void Set(object o, string name, object value)
        {
            var f = o.GetType().GetField(name, Any);
            if (f != null) f.SetValue(o, value);
            else o.GetType().GetProperty(name, Any)!.SetValue(o, value);
        }

        private static void WaitFor(Func<bool> condition, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new Xunit.Sdk.XunitException("timed out waiting for " + what);
                Thread.Sleep(2);
            }
        }

        private void OperatorTurnsItOff() => _f.Rig.Vox = FlexBase.OffOnValues.off;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_restore_paused_at_the_writer_cannot_overtake_the_operators_later_off(bool cw)
        {
            // Codex's B4 schedule, exactly: the restore is admitted and parks
            // inside the writer; the operator turns it off; the command loop
            // writes that OFF; the restore's writer then resumes.
            var (which, on, off) = OnTheRealTcpAdapter(cw, pauseTheRestore: true);
            var lease = _f.Rig.VoxPauses.Acquire(which)!;
            Drain();
            Assert.Equal(new[] { off }, Wire(on, off));               // the pause's own OFF

            var restorer = new Thread(() => lease.Release(immediate: true)) { IsBackground = true };
            restorer.Start();
            Assert.True(_writer.Entered.Wait(3000), "the restore never reached the writer after the transport admitted it");

            OperatorTurnsItOff();                                     // only queues, never waits
            var loop = new Thread(Drain) { IsBackground = true };     // the command loop writes the OFF
            loop.Start();
            // The OFF waits for the admitted restore's bytes; it does not go first.
            Assert.False(loop.Join(150), "the operator's OFF was written while an admitted restore was still between its decision and its bytes");
            _writer.Resume.Set();
            Assert.True(restorer.Join(3000));
            Assert.True(loop.Join(3000));

            // The operator's choice is the last word on the wire.
            Assert.Equal(new[] { off, on, off }, Wire(on, off));
            Assert.Equal(off, Wire(on, off).Last());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_opposite_order_is_unchanged_a_restore_that_finished_first_is_followed_by_the_off(bool cw)
        {
            // Codex's positive control: the restore resumes and finishes before
            // the operator's OFF is asked for.
            var (which, on, off) = OnTheRealTcpAdapter(cw, pauseTheRestore: true);
            var lease = _f.Rig.VoxPauses.Acquire(which)!;
            Drain();
            var restorer = new Thread(() => lease.Release(immediate: true)) { IsBackground = true };
            restorer.Start();
            Assert.True(_writer.Entered.Wait(3000));
            _writer.Resume.Set();
            Assert.True(restorer.Join(3000));
            OperatorTurnsItOff();
            Drain();
            Assert.Equal(new[] { off, on, off }, Wire(on, off));
        }

        /// <summary>The transport's write gate (MIGRATION.md item 25), by reflection.</summary>
        private SemaphoreSlim WriteGate() =>
            (SemaphoreSlim)typeof(CommandCommunicationBase).GetField("_writeGate", Any)!.GetValue(_tcp)!;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_restore_admitted_before_the_operators_choice_and_written_after_it_refuses_at_the_bytes(bool cw)
        {
            // The other half of the same interval: the restore passed its
            // question at admission and has not yet reached the bytes when the
            // operator chooses. It must not write an ON at all, because the
            // operator's choice is newer than the restore (integration fix 3:
            // never write a value older than the operator's last choice).
            var (which, on, off) = OnTheRealTcpAdapter(cw, pauseTheRestore: false);
            var lease = _f.Rig.VoxPauses.Acquire(which)!;
            Drain();
            var gate = WriteGate();
            Assert.True(gate.Wait(0), "the write gate was not free");
            Thread restorer;
            try
            {
                restorer = new Thread(() => lease.Release(immediate: true)) { IsBackground = true };
                restorer.Start();
                // Admitted (FlexLib's setter has cached ON), now waiting for the bytes.
                WaitFor(() => cw ? _f.Vendor.Radio.CWBreakIn : _f.Vendor.Radio.SimpleVOXEnable, "the restore inside its write");
                WaitFor(() => (restorer.ThreadState & ThreadState.WaitSleepJoin) != 0, "the restore waiting for the write gate");
                OperatorTurnsItOff();
            }
            finally { gate.Release(); }
            Assert.True(restorer.Join(3000));
            Drain();
            Assert.DoesNotContain(on, _writer.Commands);              // refused at the bytes
            Assert.Equal(off, Wire(on, off).Last());
            Assert.False(_f.Rig.VoxPauses.RestoreOwed(which));        // voided, not kept
        }

        [Fact]
        public void A_mic_put_back_waiting_for_the_bytes_never_overwrites_the_operators_newer_mic_choice()
        {
            // The same class on the mic input (#861): the direct put-back of the
            // saved MIC runs off the command loop (a stop, a teardown), and the
            // operator's own mic choice goes through the loop. Both wait for the
            // write gate here; whichever gets it first, MIC must not be written
            // over the operator's BAL.
            OnTheRealTcpAdapter(cw: false, pauseTheRestore: false);
            _f.Vendor.Status("transmit mic_selection=PC");
            var swapType = typeof(FlexBase).GetNestedType("MicInputSwap", BindingFlags.NonPublic)!;
            object swap = Activator.CreateInstance(swapType, BindingFlags.Public | BindingFlags.Instance, null,
                new object?[] { "MIC", _f.Vendor.Radio, 0L, false }, null)!;
            var putBackNow = typeof(FlexBase).GetMethod("PutBackNow", Any)!;
            var gate = WriteGate();
            Assert.True(gate.Wait(0), "the write gate was not free");
            Thread putBack, loop;
            try
            {
                putBack = new Thread(() => putBackNow.Invoke(_f.Rig, new object[] { _f.Vendor.Radio, swap, "test", 0L })) { IsBackground = true };
                putBack.Start();
                WaitFor(() => (putBack.ThreadState & ThreadState.WaitSleepJoin) != 0, "the put-back admitted and waiting for the write gate");
                _f.Rig.MicSource = "BAL";                              // the operator, through the loop
                loop = new Thread(Drain) { IsBackground = true };
                loop.Start();
                WaitFor(() => _f.Vendor.Radio.MicInput == "BAL", "the operator's choice inside its own write");
            }
            finally { gate.Release(); }
            Assert.True(putBack.Join(3000));
            Assert.True(loop.Join(3000));
            Assert.DoesNotContain("mic input MIC", _writer.Commands);  // never the older value
            Assert.Contains("mic input BAL", _writer.Commands);
        }

        [Fact]
        public void A_pins_question_at_the_bytes_is_asked_and_its_refusal_writes_nothing()
        {
            // The adapter's own contract, unit-sized: a pin whose admission
            // passes but whose question at the bytes refuses writes nothing on
            // either path, and the pin says why.
            OnTheRealTcpAdapter(cw: false, pauseTheRestore: false);
            var connection = (CommandConnection)_f.Vendor.Radio.CurrentCommandConnection!;
            using (var pin = CommandWritePin.Pin(connection, () => null, atTheBytes: () => "the operator chose since (test)"))
            {
                _tcp.Write("C1|transmit set vox_enable=1");
                _tcp.WriteAsync("C2|transmit set vox_enable=1").GetAwaiter().GetResult();
                Assert.Equal("the operator chose since (test)", pin.Refusal);
                Assert.Equal(0, pin.Written);
            }
            Assert.Empty(_writer.Commands);
            using (var pin = CommandWritePin.Pin(connection, () => null, atTheBytes: () => null))
            {
                _tcp.Write("C3|transmit set vox_enable=1");
                Assert.Null(pin.Refusal);
                Assert.Equal(1, pin.Written);
            }
            Assert.Single(_writer.Commands);
        }

        [Fact]
        public void The_write_gate_is_still_patched_into_the_vendor_transport()
        {
            // MIGRATION.md item 25. A FlexLib upgrade that drops any site turns
            // this red rather than reopening B4 in silence.
            string connection = File.ReadAllText(Repo("FlexLib_API/FlexLib/CommandConnection.cs"));
            string tcp = File.ReadAllText(Repo("FlexLib_API/FlexLib/TcpCommandCommunication.cs"));
            string tls = File.ReadAllText(Repo("FlexLib_API/FlexLib/TlsCommandCommunication.cs"));
            Assert.Contains("private readonly System.Threading.SemaphoreSlim _writeGate", connection);
            Assert.Contains("protected static bool AdmitAtTheBytes(CommandWritePin? pin, out string? why)", connection);
            Assert.Contains("Func<string?>? atTheBytes = null", connection);
            Assert.Equal(2, Count(tcp, "if (!AdmitAtTheBytes(pin, out refused))"));    // Write and WriteAsync
            Assert.Equal(1, Count(tls, "if (!AdmitAtTheBytes(pin, out refused))"));
            Assert.Equal(1, Count(tcp, "EnterWriteGate();"));
            Assert.Equal(1, Count(tcp, "await EnterWriteGateAsync()"));
            Assert.Equal(1, Count(tls, "EnterWriteGate();"));
            Assert.True(Count(connection + tcp + tls, "JJFlex patch: write order (MIGRATION.md item 25)") >= 5);
            Assert.Contains("25. **Command write order", File.ReadAllText(Repo("MIGRATION.md")));
        }

        private static int Count(string text, string what)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }

        private static string Repo(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, relative);
        }
    

        // ── Rulings round two, Codex's R1: J10's retry against a new run, at the bytes ──

        /// <summary>
        /// A live J10 retry for session 1, run 0 on the real TCP adapter, the
        /// mic input at MIC, as a PC-audio start that could not record leaves it.
        /// </summary>
        private object ArmedRetryOnTheRealAdapter()
        {
            OnTheRealTcpAdapter(cw: false, pauseTheRestore: false);
            _f.Vendor.Status("transmit mic_selection=MIC");
            var rig = _f.Rig;
            typeof(FlexBase).GetField("_micSessionOpen", Any)!.SetValue(rig, true);
            typeof(FlexBase).GetField("_micSessionId", Any)!.SetValue(rig, 1L);
            var retry = new FlexBase.PcSwitchRetry(_f.Vendor.Radio, (CommandConnection)_f.Vendor.Radio.CurrentCommandConnection!, 0, new object())
                { Session = 1 };
            typeof(FlexBase).GetField("_pcSwitchRetry", Any)!.SetValue(rig, retry);
            return retry;
        }

        private string MicLines() => string.Join("; ", _writer.Commands.Where(x => x.StartsWith("mic input", StringComparison.Ordinal)));

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_retry_waiting_at_the_write_gate_when_a_new_run_begins_writes_no_PC(bool newRun)
        {
            // Codex's retry-run probe and its control: the retry records MIC,
            // installs its swap and waits at the transport's write gate; a new
            // remote-audio run begins (its run-start put-back and reset, the
            // production sequence), then the gate frees.
            var retry = (FlexBase.PcSwitchRetry)ArmedRetryOnTheRealAdapter();
            var gate = WriteGate();
            Assert.True(gate.Wait(0), "the write gate was not free");
            Thread attempt;
            FlexBase.PcSwitchAttempt result = default;
            try
            {
                attempt = new Thread(() => result = _f.Rig.TryPcSwitchOnce(retry)) { IsBackground = true };
                attempt.Start();
                WaitFor(() => typeof(FlexBase).GetField("_micInputSwap", Any)!.GetValue(_f.Rig) != null
                    && (attempt.ThreadState & ThreadState.WaitSleepJoin) != 0, "the retry recorded and waiting at the gate");
                if (newRun)
                {
                    typeof(FlexBase).GetField("_remoteAudioRun", Any)!.SetValue(_f.Rig, 1L);
                    typeof(FlexBase).GetMethod("BeginRemoteAudioRunMicState", Any)!.Invoke(_f.Rig, new object[] { 1L });
                }
            }
            finally { gate.Release(); }
            Assert.True(attempt.Join(3000));
            object? swap = typeof(FlexBase).GetField("_micInputSwap", Any)!.GetValue(_f.Rig);
            if (newRun)
            {
                // Either no PC at all, or a PC with its debt still owed: never a PC nobody will put back.
                Assert.True(MicLines() == "" || swap != null, "PC written after the new run with nothing owed: " + MicLines());
                Assert.Equal("", MicLines());
                Assert.NotEqual(FlexBase.PcSwitchAttempt.OnPc, result);
            }
            else
            {
                Assert.Equal("mic input PC", MicLines());     // control: the retry does its job
                Assert.NotNull(swap);
                Assert.Equal(FlexBase.PcSwitchAttempt.OnPc, result);
            }
        }

    }
}

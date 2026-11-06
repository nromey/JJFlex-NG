#nullable enable
using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.Listenback;

namespace Radios
{
    /// <summary>
    /// FlexBase, the autonomous-keying side of the hold after a reflected-power
    /// cut (#571): which VOX-class setting the transmit slice's mode uses,
    /// switching exactly that setting off and back on as the APPLICATION's
    /// act, and the hold's door for CW keying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why by setting and not through <see cref="Vox"/>.</b> <c>Vox</c>
    /// is an abstraction over two independent radio settings:
    /// <c>CWBreakIn</c> when the TX slice is in CW, <c>SimpleVOXEnable</c>
    /// otherwise, re-resolved by mode at every call. A hold that switched
    /// SimpleVOXEnable off in USB and then wrote <c>Vox = on</c> after the
    /// operator moved to CW would switch CWBreakIn on instead and leave
    /// SimpleVOXEnable off (Astra's review of this track, B5). So the hold
    /// records WHICH setting it switched off and puts back that one.
    /// </para>
    /// <para>
    /// <b>Two ways to write, because of teardown.</b> The ordinary write goes
    /// through the command queue like every other setter here. The IMMEDIATE
    /// write sets the FlexLib property on the calling thread, which
    /// <c>setupFromScratch</c> already does for these same two properties:
    /// <c>FlexBase.Dispose</c> stops the command loop and disconnects, so a
    /// queued restore made as the radio is being closed may never run (B3).
    /// <c>MainWindow.UnwireRadioEvents</c> runs before <c>CloseTheRadio</c>
    /// disposes the rig, with the transport still up, and uses the immediate
    /// form. Either way the return value says whether a write was ISSUED;
    /// nothing here claims it was applied.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>One of the radio's autonomous-keying settings, or none.</summary>
        public enum VoxSetting
        {
            /// <summary>Nothing: VOX is off for this mode, or there is no TX slice.</summary>
            None,
            /// <summary>FlexLib <c>SimpleVOXEnable</c>, the voice modes' VOX.</summary>
            SimpleVox,
            /// <summary>FlexLib <c>CWBreakIn</c>, CW's equivalent.</summary>
            CwBreakIn,
        }

        private readonly object _voxPauseGate = new object();
        private VoxPauseOwner? _voxPauses;
        private VoxPauseOwner.Lease? _toneVoxLease;
        private VoxPauseOwner.Lease? _referenceVoxLease;
        private string? _voxPausedAtDisconnectSerial;
        private object? _voxPauseRadio;
        /// <summary>The command connection the shared VOX owner belongs to (repair round 4, #596 B6).</summary>
        private object? _voxPauseConnection;
        private readonly object _voxReplyGate = new object();
        private readonly System.Collections.Generic.Dictionary<VoxSetting, (Radio Radio, bool On, Task<bool> Reply)> _voxReplies = new();

        internal Task<bool> LastVoxWriteReply(VoxSetting which, Radio radio, bool on)
        {
            lock (_voxReplyGate)
                return _voxReplies.TryGetValue(which, out var pending) &&
                    ReferenceEquals(pending.Radio, radio) && pending.On == on
                    ? pending.Reply : Task.FromResult(false);
        }

        /// <summary>
        /// Shared by every temporary VOX holder on this rig. Acquire the exact
        /// setting needed, then release the returned lease on every exit path.
        /// </summary>
        public VoxPauseOwner VoxPauses
        {
            get
            {
                lock (_voxPauseGate)
                {
                    var current = theRadio;
                    object? currentConnection = current?.CurrentCommandConnection;
                    if (_voxPauses == null || !ReferenceEquals(_voxPauseRadio, current)
                        || !ReferenceEquals(_voxPauseConnection, currentConnection))
                    {
                        // An old lease must never restore its snapshot into a
                        // different radio, nor onto another command connection
                        // of the same radio (repair round 4, Codex's B6): VOX
                        // debt belongs to the connection that incurred it, and
                        // writing it onto a new one would turn on autonomous
                        // keying nobody asked for there. So the old owner is
                        // abandoned, never flushed, and what it still owed is
                        // remembered by serial for the next connect's sentence.
                        if (_voxPauses != null)
                        {
                            string? oldSerial = (_voxPauseRadio as Radio)?.Serial;
                            if (_voxPauses.Abandon() && oldSerial != null)
                            {
                                _voxPausedAtDisconnectSerial = oldSerial;
                                Tracing.TraceLine("VoxPauses: the radio or its command connection changed with a VOX "
                                    + "restore still owed; it is NOT written on the new one, and is remembered", TraceLevel.Warning);
                            }
                        }
                        var radio = current;
                        object? connection = currentConnection;
                        _voxPauseRadio = radio;
                        _voxPauseConnection = connection;
                        bool Ours() => radio != null && ReferenceEquals(radio, theRadio)
                            && ReferenceEquals(radio.CurrentCommandConnection, connection);
                        _voxPauses = new VoxPauseOwner(
                            which => Ours()
                                ? (which == VoxSetting.CwBreakIn ? radio!.CWBreakIn : radio!.SimpleVOXEnable)
                                : throw new InvalidOperationException("The radio or its command connection is no longer this one."),
                            // To THIS owner's radio, never whatever is current at
                            // the write (repair round 5).
                            (which, on, immediate) => Ours() && WriteVoxSettingTo(radio!, which, on, immediate),
                            // The queue's own answer (integration fix).
                            (action, dropped) => q.Enqueue((FunctionDel)(() => action()), "VoxPause", dropped),
                            // The family's one question, at every restore write
                            // (repair round 3), pinned to this owner's radio AND
                            // command connection (repair round 4).
                            () => radio == null ? "there is no radio"
                                : RestoreRefusedAtWrite(radio, (object?)connection ?? NoConnection),
                            // ...and asked WITH the write, as one step that a new
                            // command connection waits for (repair round 5,
                            // Codex's B8): a release whose last check passed
                            // cannot then go out on a replacement connection.
                            // Round 6: pinned at the transport's own write, and a
                            // restore ON asked receive there again, under the
                            // reader's dispatch gate (Codex's B9/B10 family).
                            // Integration fix 3 (Codex's #596 B3): and asked there
                            // first whether the operator has chosen since it was owed.
                            (on, step, operatorOverruled) => StepOnPinnedTarget(radio, connection, step,
                                on && radio != null
                                    ? () => operatorOverruled?.Invoke() ?? RestoreRefusedAtWrite(radio, (object?)connection ?? NoConnection)
                                    : null));
                        OperatorChangedRadioSetting -= NoteVoxPauseOperatorChange;
                        OperatorChangedRadioSetting += NoteVoxPauseOperatorChange;
                        WatchVoxRetry(_voxPauses, connection as CommandConnection);
                    }
                    return _voxPauses;
                }
            }
        }

        /// <summary>The watch that retries a withheld VOX restore; null when none.</summary>
        private Action<CommandConnection>? _voxRetryWatch;

        /// <summary>
        /// A VOX restore withheld at its write (a carrier, or the report gate not
        /// had within its bound) is retried when the radio next reports receive
        /// on that owner's connection (integration fix, 2026-11-06), as the mic
        /// debt is. Off the reader. Replaced with the owner; ended at disconnect.
        /// The two debt machines are kept apart this round (register).
        /// </summary>
        private void WatchVoxRetry(VoxPauseOwner owner, CommandConnection? connection)
        {
            StopVoxRetryWatch();
            if (connection == null) return;
            Action<CommandConnection> watch = c =>
            {
                if (!ReferenceEquals(c, connection) || !owner.HasWithheld) return;
                if (InterlockReports.LastOn(c)?.IsReceive != true) return;
                _ = System.Threading.Tasks.Task.Run(owner.RetryWithheld);
            };
            _voxRetryWatch = watch;
            InterlockReports.Reported += watch;
        }

        private void StopVoxRetryWatch()
        {
            var watch = _voxRetryWatch;
            _voxRetryWatch = null;
            if (watch != null) InterlockReports.Reported -= watch;
        }

        private void NoteVoxPauseOperatorChange(string what)
        {
            if (what != "Vox") return;
            _voxPauses?.OperatorChanged(TXModeAsReported == "CW" ? VoxSetting.CwBreakIn : VoxSetting.SimpleVox);
        }

        /// <param name="restore">False when the teardown could not confirm
        /// receive (#596, repair round 2, N3): VOX is NOT written back on under
        /// a carrier. The pause is abandoned and remembered by serial, so the
        /// next connect says VOX was paused, as it already does for a pause
        /// the link took down.</param>
        private void DisconnectVoxPauses(bool restore)
        {
            // Read slice leases before the VOX gate. Slice status can dispose
            // a lease while holding its own gate; reversing that order here
            // would put disconnect into the same inversion as that status path.
            VoxPauseOwner.Lease?[] sliceLeases;
            lock (_sliceRecorders)
                sliceLeases = _sliceRecorders.Values.Select(s => s.PlaybackVoxLease).ToArray();
            bool slicePaused = sliceLeases.Any(lease => lease?.Paused == true);
            lock (_voxPauseGate)
            {
                if ((_toneVoxLease?.Paused == true || _referenceVoxLease?.Paused == true || slicePaused
                    || _localPlaybackVoxLeases.Any(lease => lease.Paused))
                    && theRadio?.Serial is string serial)
                    _voxPausedAtDisconnectSerial = serial;
                txToneGen.Stop();
                TxFileStop();
                if (restore)
                {
                    // Each restore asks at its own write; one withheld there
                    // is remembered, as an abandoned one is (repair round 3).
                    if (_voxPauses?.Disconnect() == true && theRadio?.Serial is string withheldSerial)
                        _voxPausedAtDisconnectSerial = withheldSerial;
                }
                else if (_voxPauses?.Abandon() == true && theRadio?.Serial is string owedSerial)
                    _voxPausedAtDisconnectSerial = owedSerial;
                _localPlaybackVoxLeases.Clear();
                _referenceVoxLease?.Dispose();
                _referenceVoxLease = null;
                _toneVoxLease?.Dispose();
                _toneVoxLease = null;
                _voxPauses = null;
                StopVoxRetryWatch();
                _voxPauseRadio = null;
                _voxPauseConnection = null;
            }
        }

        private void AnnounceVoxPauseAfterReconnect(Radio radio)
        {
            lock (_voxPauseGate)
            {
                if (radio?.Serial == null || !string.Equals(radio.Serial, _voxPausedAtDisconnectSerial,
                    StringComparison.OrdinalIgnoreCase)) return;
                _voxPausedAtDisconnectSerial = null;
            }
            ScreenReaderOutput.Speak(
                "VOX was paused when this radio disconnected. Check whether VOX is on before transmitting.",
                VerbosityLevel.Critical, interrupt: true);
        }

        /// <summary>
        /// The setting the TX slice's mode keys on, if it is ON right now;
        /// <see cref="VoxSetting.None"/> when it is off or unreadable. Read
        /// live from FlexLib, never from a cache.
        /// </summary>
        public VoxSetting CurrentVoxSettingIfOn()
        {
            try
            {
                if (theRadio == null || !ValidVFO(TXVFO)) return VoxSetting.None;
                var s = VFOToSlice(TXVFO);
                if (s == null) return VoxSetting.None;
                if (s.DemodMode == "CW") return theRadio.CWBreakIn ? VoxSetting.CwBreakIn : VoxSetting.None;
                return theRadio.SimpleVOXEnable ? VoxSetting.SimpleVox : VoxSetting.None;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CurrentVoxSettingIfOn: " + ex.Message, TraceLevel.Warning);
                return VoxSetting.None;
            }
        }

        /// <summary>
        /// Write one autonomous-keying setting as the APPLICATION's act — inside
        /// <see cref="AppInitiatedSettingChanges"/>, so the operator-changed
        /// hook does not fire and the disconnect save offer is not armed.
        /// </summary>
        /// <param name="which">Which setting; <see cref="VoxSetting.None"/> writes nothing.</param>
        /// <param name="on">The value.</param>
        /// <param name="immediate">
        /// True writes the FlexLib property on this thread, for a teardown
        /// where the command loop may already be stopping. False enqueues it
        /// like every other setter.
        /// </param>
        /// <returns>
        /// True when a write was ISSUED to a radio that is present. False when
        /// there is no radio, or the queue is not running and the write was
        /// not immediate. Never "applied": nothing here reads the echo.
        /// </returns>
        public bool WriteVoxSetting(VoxSetting which, bool on, bool immediate) =>
            WriteVoxSettingTo(theRadio, which, on, immediate);

        /// <summary><see cref="WriteVoxSetting"/> to a named radio: the VOX owner's own (repair round 5).</summary>
        private bool WriteVoxSettingTo(Radio? radio, VoxSetting which, bool on, bool immediate)
        {
            if (which == VoxSetting.None) return false;
            if (radio == null)
            {
                Tracing.TraceLine("WriteVoxSetting: no radio, so " + which + "=" + on + " was NOT issued", TraceLevel.Warning);
                return false;
            }
            using (AppInitiatedSettingChanges())
            {
                bool wrote = true;
                FunctionDel write = () =>
                {
                    using var appWrite = AppInitiatedSettingChanges();
                    string command = which == VoxSetting.CwBreakIn
                        ? $"cw break_in {(on ? 1 : 0)}"
                        : $"transmit set vox_enable={(on ? 1 : 0)}";
                    // The FlexLib setter sends the command but does not attach a
                    // reply handler. Its transmit trace records that command's
                    // sequence and the matching reply without a second write.
                    var reply = new VoxWriteReply(radio, command);
                    try
                    {
                        // Inside a pinned step its writes are counted (integration
                        // fix 2, Codex's F1): the setter skips a value its cache
                        // already holds, and a refused earlier write leaves that
                        // cache poisoned, so the same command then goes out raw.
                        int before = PinnedWritesSoFar;
                        if (which == VoxSetting.CwBreakIn) radio.CWBreakIn = on;
                        else radio.SimpleVOXEnable = on;
                        // Only for a restore (on): an off the cache already reads
                        // off is the radio's own state, and sends nothing as before.
                        if (on && t_stepPin != null && PinnedWritesSoFar == before)
                            SendRadioCommandWithReply(radio, command, null);
                        if (on && t_stepPin != null) wrote = PinnedWritesSoFar > before;
                        lock (_voxReplyGate) _voxReplies[which] = (radio, on, reply.Result);
                        reply.FinishWrite();
                    }
                    catch { reply.Dispose(); throw; }
                };
                if (immediate)
                {
                    try { write(); }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("WriteVoxSetting: immediate " + which + "=" + on + " threw: " + ex.Message, TraceLevel.Error);
                        return false;
                    }
                    if (!wrote)
                    {
                        Tracing.TraceLine("WriteVoxSetting: " + which + "=" + on + " was NOT written", TraceLevel.Warning);
                        return false;
                    }
                    Tracing.TraceLine("WriteVoxSetting: " + which + "=" + on + " issued immediately (app-initiated)", TraceLevel.Info);
                    return true;
                }
                if (!q.Enqueue(write, which == VoxSetting.CwBreakIn ? "BreakIn" : "SimpleVOXEnable"))
                {
                    Tracing.TraceLine("WriteVoxSetting: command loop not running, so " + which + "=" + on + " was NOT issued", TraceLevel.Warning);
                    return false;
                }
            }
            Tracing.TraceLine("WriteVoxSetting: " + which + "=" + on + " queued (app-initiated)", TraceLevel.Info);
            return true;
        }

        private sealed class VoxWriteReply : IDisposable
        {
            private readonly Radio _radio;
            private readonly string _command;
            private readonly RadioStatusTap _traceLease;
            private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _sequence;
            private bool _disposed;
            internal Task<bool> Result => _result.Task;

            internal VoxWriteReply(Radio radio, string command)
            {
                _radio = radio;
                _command = command;
                _traceLease = new RadioStatusTap(radio);
                radio.PropertyChanged += Changed;
            }

            private void Changed(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(Radio.TelnetTraceOutput) || _disposed) return;
                string? line;
                var output = _radio.TelnetTraceOutput;
                lock (output) line = output.Count == 0 ? null : output[^1];
                if (line == _command)
                {
                    var sequences = _radio.TelnetTraceResponseSeqNums;
                    lock (sequences)
                        if (sequences.Count > 0)
                            _sequence = Convert.ToInt32(sequences[^1], CultureInfo.InvariantCulture);
                    Tracing.TraceLine($"VOX write traced: sequence={_sequence} command={_command}", TraceLevel.Info);
                }
                else if (_sequence != 0 && line?.StartsWith($"R{_sequence}|", StringComparison.Ordinal) == true)
                {
                    string[] parts = line.Split('|', 3);
                    uint code = 0;
                    bool parsed = parts.Length >= 2 && uint.TryParse(parts[1], NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out code);
                    Tracing.TraceLine($"VOX write reply: sequence={_sequence} code={(parsed ? code.ToString("X8") : "invalid")} text={(parts.Length > 2 ? parts[2] : "")} command={_command}", TraceLevel.Info);
                    _result.TrySetResult(parsed && code == 0);
                    DisposeOffTheTraceThread();
                }
            }

            /// <summary>
            /// Let go from inside a trace notification (#835, Codex round 2 on
            /// the Track B fix round). <c>Radio.ParseReply</c> raises the reply's
            /// change while HOLDING the trace's sequence and output lists, and
            /// this tap may be the last lease, whose release removes FlexLib
            /// topics and so takes the TOPIC list. <c>Radio.SendCommand</c> takes
            /// those in the other order, topics then sequences, so a release made
            /// here could deadlock the receive thread against any traced command.
            /// The subscription is dropped now (an event removal takes no lock)
            /// and the tap is released on the thread pool, holding nothing.
            /// </summary>
            private void DisposeOffTheTraceThread()
            {
                if (_disposed) return;
                _disposed = true;
                _radio.PropertyChanged -= Changed;
                System.Threading.ThreadPool.QueueUserWorkItem(_ => _traceLease.Dispose());
            }

            internal void FinishWrite()
            {
                if (_sequence == 0)
                {
                    Tracing.TraceLine($"VOX write had no traced command: {_command}", TraceLevel.Warning);
                    _result.TrySetResult(false);
                    Dispose();
                    return;
                }
                _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
                {
                    if (_result.TrySetResult(false))
                        Tracing.TraceLine($"VOX write reply timed out: sequence={_sequence} command={_command}", TraceLevel.Warning);
                    Dispose();
                });
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _radio.PropertyChanged -= Changed;
                _traceLease.Dispose();
            }
        }

        /// <summary>
        /// The hold's door for CW keying (#571, Astra B5; Noel's 21:16 "nothing
        /// re-keys"). Asked by <see cref="SendCW(string)"/> and
        /// <see cref="SendCW(char)"/> with the door's name; speaks the refusal
        /// itself and returns true when keying is held. Null means no hold
        /// exists, which reads as not held.
        /// </summary>
        public static Func<string, bool>? RefuseIfTransmitHeld { get; set; }

        /// <summary>
        /// The same question without the words, for the per-character and
        /// per-string senders on the command thread: a message already in the
        /// queue when the cut landed must not go out a character at a time,
        /// and must not be refused aloud once per character either.
        /// </summary>
        public static Func<bool>? IsTransmitHeld { get; set; }

        private static bool CwKeyingRefused(string door)
        {
            try { return RefuseIfTransmitHeld?.Invoke(door) == true; }
            catch (Exception ex)
            {
                Tracing.TraceLine("RefuseIfTransmitHeld threw, so CW keying is NOT held: " + ex.Message, TraceLevel.Error);
                return false;
            }
        }

        private static bool CwKeyingHeldSilently()
        {
            try { return IsTransmitHeld?.Invoke() == true; }
            catch { return false; }
        }
    }
}

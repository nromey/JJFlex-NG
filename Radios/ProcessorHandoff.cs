#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Radios
{
    /// <summary>
    /// "The radio's speech processor", as the handoff sees it (Sprint 49 Track
    /// CL, #1278): radio-neutral, so the policy is tested against a fake and a
    /// radio other than a Flex can implement it.
    /// </summary>
    public interface IRadioSpeechProcessor
    {
        /// <summary>The radio's serial: the key the record is kept under. Empty when there is none.</summary>
        string Serial { get; }

        /// <summary>Whether the radio is connected and can be read and written.</summary>
        bool Connected { get; }

        /// <summary>
        /// Whether the radio's speech processor is on, or null when it cannot
        /// be read now. This is a READING, kept on the computer's side: it is
        /// the radio's word for changes the app did not write, but once a
        /// write of the app's is <see cref="ProcessorWriteStep.Sent"/> it may
        /// show the written value at once, with no radio in it. It never
        /// confirms a write.
        /// </summary>
        bool? ProcessorOn { get; }

        /// <summary>The processor's level as the radio reports it (a number whose meaning is the radio's), for the record and the trace.</summary>
        int ProcessorLevel { get; }

        /// <summary>Whether the radio is transmitting: nothing is written while it is.</summary>
        bool Transmitting { get; }

        /// <summary>Whether another client may be connected to this radio (anything but "only us" for certain).</summary>
        bool AnotherClientMayBeConnected { get; }

        /// <summary>Whether the operator has declared this radio theirs.</summary>
        bool RadioIsOperators { get; }

        /// <summary>
        /// ASK for the processor to be turned on or off, as the APPLICATION's
        /// change (never read as the operator's). The write may be queued: a
        /// true answer means it was asked for, NOT that it was written, and a
        /// written command is not a change the radio has made. At the moment
        /// it is really written the implementation checks again that the
        /// radio is connected and not transmitting and that
        /// <paramref name="stillWanted"/> still answers true. What becomes of
        /// it is told through <paramref name="report"/>, from whatever thread
        /// learns it: <see cref="ProcessorWriteStep.NotMade"/> when the moment
        /// came and nothing went to the radio (the reading is untouched);
        /// <see cref="ProcessorWriteStep.Sent"/> when the command went out,
        /// reported BEFORE <see cref="ProcessorOn"/> is allowed to show the
        /// written value; and then the radio's own answer,
        /// <see cref="ProcessorWriteStep.Accepted"/> or
        /// <see cref="ProcessorWriteStep.Refused"/>, if one ever comes. Every
        /// asking that is sent reaches the radio, whether or not the reading
        /// already shows the value. The level is never written.
        /// </summary>
        bool WriteProcessorOn(bool on, Func<bool> stillWanted, Action<ProcessorWriteStep> report);

        /// <summary>
        /// Send it NOW, on the caller's thread, for the leave before a
        /// teardown, when a queue may never run again and no answer can be
        /// waited for. False when the radio is not connected, is transmitting,
        /// or the command could not be sent; nothing is written then.
        /// </summary>
        bool WriteProcessorOnNow(bool on);
    }

    /// <summary>What became of one write asked of the radio's speech processor (<see cref="IRadioSpeechProcessor.WriteProcessorOn"/>).</summary>
    public enum ProcessorWriteStep
    {
        /// <summary>The moment came and nothing went to the radio: it was no longer connected, it was transmitting, the write was no longer wanted, or the command link was down. The reading is untouched.</summary>
        NotMade,
        /// <summary>The command went to the radio. From here the reading may show the written value, which proves nothing.</summary>
        Sent,
        /// <summary>The radio answered that it took the command: the only confirmation there is.</summary>
        Accepted,
        /// <summary>The radio answered that it did not take the command.</summary>
        Refused,
    }

    /// <summary>What the handoff needs to know from the computer's side, read fresh on every tick.</summary>
    /// <param name="SettingOn">The setting "turn off the radio's speech processor while the compressor is on".</param>
    /// <param name="CompressorOn">Our compressor is on, IN FORCE on the chain (a switch still waiting for the unkey is not on yet).</param>
    /// <param name="PcMicrophone">The computer's microphone is the transmit source (not the radio's own).</param>
    /// <param name="VoiceMode">The transmit mode is a voice mode (the chain is not standing aside for CW or digital).</param>
    /// <param name="ChainFaults">A count that rises when the transmit chain faults mid-over and is skipped.</param>
    public sealed record ProcessorHandoffInputs(bool SettingOn, bool CompressorOn, bool PcMicrophone, bool VoiceMode, long ChainFaults);

    /// <summary>What the handoff did, for whoever says it. Each is said once, when it happens.</summary>
    public enum ProcessorHandoffNotice
    {
        /// <summary>Our compressor came on and the radio's speech processor was turned off.</summary>
        TurnedOff,
        /// <summary>The same, and another client may be connected: it is the radio's processor, shared by every client.</summary>
        TurnedOffShared,
        /// <summary>Our compressor went off (or the microphone became the radio's own, or the setting was cleared) and the processor was put back on.</summary>
        PutBack,
        /// <summary>The same, said with the shared clause.</summary>
        PutBackShared,
        /// <summary>At connect: an earlier session left the processor off, our compressor is not on now, and the processor was put back on.</summary>
        PutBackAtConnect,
        /// <summary>At connect: an earlier session left the processor off, our compressor is on, so it stays off and is still ours to put back.</summary>
        StillOffAtConnect,
        /// <summary>The transmit chain faulted mid-over, so the processor was put back on at the unkey.</summary>
        PutBackAfterFault,
        /// <summary>Our compressor is on and the radio's processor is on, and it was left alone: this radio is not marked as the operator's own.</summary>
        LeftAloneNotYours,
    }

    /// <summary>What the handoff changed on one radio and has not yet put back.</summary>
    /// <param name="Serial">The radio.</param>
    /// <param name="PreviousOn">The processor's state before the app changed it (always true today: the app only ever turns it off).</param>
    /// <param name="PreviousLevel">The processor's level then, for the record; never written back.</param>
    /// <param name="AppSetOn">The value the app set (false). The put-back happens only while the radio still holds it.</param>
    public sealed record ProcessorHandoffRecord(string Serial, bool PreviousOn, int PreviousLevel, bool AppSetOn);

    /// <summary>
    /// The handoff's records, one line per radio, in a small file under the
    /// settings root, so a record survives the app closing and the radio
    /// object being made again. The shape of <see cref="CheckRestoreReminderStore"/>,
    /// kept separate on purpose: that store's records are an audio check's and
    /// are cleared by its own rules. A read or write failure is traced and
    /// reads as "nothing recorded"; a record must never stop a connect.
    /// </summary>
    public sealed class ProcessorHandoffStore
    {
        public const string FileName = "processorHandoff.txt";
        private static readonly object Gate = new();
        private readonly string _path;

        /// <summary>A store in <paramref name="directory"/>; production passes <see cref="RadioConfig.AppDataRoot"/>.</summary>
        public ProcessorHandoffStore(string directory)
        {
            _path = Path.Combine(directory ?? throw new ArgumentNullException(nameof(directory)), FileName);
        }

        /// <summary>The record for this radio, or null.</summary>
        public ProcessorHandoffRecord? Find(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return null;
            lock (Gate) return Read().FirstOrDefault(r => Same(r.Serial, serial));
        }

        /// <summary>Record what the app is about to change on this radio. A record already there is kept: it holds the operator's own value.</summary>
        public bool Save(ProcessorHandoffRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Serial)) return false;
            lock (Gate)
            {
                var all = Read();
                if (all.Any(r => Same(r.Serial, record.Serial))) return true;
                all.Add(record with { Serial = record.Serial.Trim() });
                return Write(all);
            }
        }

        /// <summary>Forget this radio's record: it was put back, or the radio no longer holds the app's value.</summary>
        public void Remove(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return;
            lock (Gate)
            {
                var all = Read();
                if (all.RemoveAll(r => Same(r.Serial, serial)) > 0) Write(all);
            }
        }

        private static bool Same(string a, string? b) => string.Equals(a.Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private List<ProcessorHandoffRecord> Read()
        {
            var list = new List<ProcessorHandoffRecord>();
            try
            {
                if (!File.Exists(_path)) return list;
                foreach (string line in File.ReadAllLines(_path))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length != 4 || string.IsNullOrWhiteSpace(parts[0])) continue;
                    if (!bool.TryParse(parts[1], out bool previousOn) || !bool.TryParse(parts[3], out bool appSet)) continue;
                    if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)) continue;
                    list.Add(new ProcessorHandoffRecord(parts[0].Trim(), previousOn, level, appSet));
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("ProcessorHandoffStore: " + _path + " could not be read: " + ex.Message);
            }
            return list;
        }

        private bool Write(List<ProcessorHandoffRecord> all)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (all.Count == 0)
                {
                    if (File.Exists(_path)) File.Delete(_path);
                    return true;
                }
                File.WriteAllLines(_path, all.Select(r => string.Join("\t", r.Serial,
                    r.PreviousOn.ToString(), r.PreviousLevel.ToString(CultureInfo.InvariantCulture), r.AppSetOn.ToString())));
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("ProcessorHandoffStore: " + _path + " could not be written: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// The handoff between our transmit compressor and the radio's speech
    /// processor (Sprint 49 Track CL, #1278; Noel 2026-11-23 20:42: "if we turn
    /// on compressor, we turn off speech processing settings either that or
    /// have a setting"). Radio-neutral: it knows a radio only through
    /// <see cref="IRadioSpeechProcessor"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>The rules.</b></para>
    /// <list type="bullet">
    /// <item>A <b>session</b> is the stretch while the setting is on, our
    /// compressor is on in force, and the computer's microphone is the
    /// transmit source. It starts on that edge, however the edge came (the
    /// compressor's switch, a setup, a preset, a microphone change, the
    /// setting itself being switched on).</item>
    /// <item><b>At the start of a session</b>, if the radio's processor is on,
    /// the app asks for it to be turned off and records what it is changing
    /// (per radio serial). It says so when the RADIO ANSWERS that it took the
    /// command, never before. If the processor is already off there is nothing
    /// to do, and nothing to put back later.</item>
    /// <item><b>Only the radio's answer confirms a write</b> (repair 2, B3a).
    /// A radio's own software may show a value in its reading the moment it
    /// is written, with no radio in it; so the reading, however long after
    /// the write it is read, is not confirmation. A write is ASKED (queued),
    /// then SENT (on the wire), then ACCEPTED or REFUSED by the radio
    /// (<see cref="ProcessorWriteStep"/>). The sentences and the record wait
    /// for ACCEPTED. A write that is sent and never answered is asked for
    /// again, on the wire, at growing intervals; the record of what the app
    /// changed is kept all the while. A write the radio refuses is not asked
    /// again: a refused off is undone (the reading set back, nothing said,
    /// not tried again until the compressor is next switched on), and after a
    /// refused put-back the record waits for the next connection's first
    /// reading.</item>
    /// <item><b>Somebody else's change wins.</b> The processor being set
    /// through the rig's own setter (the operator's key, a menu, a field, a
    /// preset) is told to the handoff at once (<see cref="TakenOver"/>), and
    /// wins whether or not the app's off has been sent, answered or seen
    /// (repair 2, B3b): whatever the app asked for is withdrawn, its record
    /// is dropped, and the processor is not turned off again in that session.
    /// A processor that reads on after the radio accepted the app's off, with
    /// no such call (another station's doing), is treated the same way. It
    /// is never fought.</item>
    /// <item><b>At the end of a session</b> the processor is put back on only
    /// if the app's off was sent and the reading still holds the value the app
    /// set (or holds only the app's own unanswered put-back). The level is
    /// never written: the app never changed it.</item>
    /// <item><b>Asked is not written</b> (repair 1, B3). Every queued write
    /// carries a ticket: if the policy no longer wants that write when the
    /// queue reaches it (the session ended, somebody took over, the app is
    /// leaving), it is not made.</item>
    /// <item><b>Nothing is written while the radio is transmitting</b>: not
    /// asked for while it transmits, and checked again by the radio's side at
    /// the moment of the write (whether the radio accepts a processor change
    /// while keyed without a click is a bench question, #737).</item>
    /// <item><b>A fault in the transmit chain mid-over</b> ends the session:
    /// the processor is put back at the unkey, said once, and not turned off
    /// again until the compressor is next switched on.</item>
    /// <item><b>Never turned off:</b> with the setting off; with the radio's
    /// own microphone; in CW and digital (the chain stands aside there, and
    /// the processor is the operator's); and on a radio not marked as the
    /// operator's own, where the compressor still runs and the app says once
    /// that the processor was left alone. Putting back what the app itself
    /// changed is done in any mode.</item>
    /// <item><b>Across a disconnect</b> the record stays on disk. The leave
    /// before a teardown writes the put-back at once, on the caller's thread
    /// (<see cref="Leaving"/>), and keeps the record. At the next connect to
    /// that radio, the connection's first reading settles it: if the
    /// processor no longer holds the app's value, the record is dropped (and,
    /// our compressor being on, that connection's session then starts like
    /// any other); if it does and our compressor is not on, it is put back
    /// and said; if it does and our compressor is on, it stays off, the
    /// record is kept, and that is said.</item>
    /// </list>
    /// <para><b>Time is counted in ticks</b>, never a clock, so every rule is
    /// testable.</para>
    /// <para>One caller at a time: the policy tick, the connect path, the
    /// leave and <see cref="TakenOver"/> each take the same lock. What the
    /// radio's side reports about a write arrives on other threads (the
    /// command queue's, the radio's reader) and is only posted; the tick reads
    /// it. A tick reads the radio's reading FIRST and that news after it, and
    /// the radio's side reports a write as sent BEFORE its reading can show
    /// it: so a reading taken in a tick that found no news of a write cannot
    /// be that write. What is said is said after the lock is let go.</para>
    /// </remarks>
    public sealed class ProcessorHandoff
    {
        /// <summary>Ticks an asked write is given to be made, and a sent one to be answered, before the first asking again (two seconds at the app's half-second tick).</summary>
        public const int SettleTicks = 4;

        /// <summary>The longest wait between askings of a write the radio does not answer (a minute at the app's half-second tick).</summary>
        public const int LongestWaitTicks = 120;

        private readonly IRadioSpeechProcessor _radio;
        private readonly ProcessorHandoffStore _store;
        private readonly Action<ProcessorHandoffNotice> _say;
        private readonly Action<string> _trace;

        private enum Pending { None, Off, PutBack }

        private bool _atConnect, _connectPutBack;
        private bool _session, _declined, _settled, _faulted, _faultPutBack, _saidNotYours;
        private long _faultsSeen = -1;
        private readonly object _sync = new();

        // One write on its way, through however many askings it takes (an episode).
        private Pending _pending;
        private bool _sent;         // the newest asking of it has gone to the radio
        private int _wait;          // ticks left for the newest asking: to be made (not sent yet) or answered (sent)
        private int _asks;          // askings in this episode
        private int _episodeFirst;  // the ticket of the episode's first asking: news of anything older is nobody's now
        private int _newest;        // the ticket of the newest asking
        // The ticket of the newest write asked for. A queued write is made
        // only while its own ticket is still the newest.
        private int _ticket;

        // What is known of the radio and of the reading.
        private bool _offSent;      // the app's off went to the radio (on this connection, or before it for a record read from disk)
        private bool _offAccepted;  // the radio answered that it took the app's off, or a connection's first reading showed it still off
        private bool _backSent;     // the app's put-back went to the radio and has not been answered: the reading may show the app's own write
        private bool _offRefused;   // the radio refused the app's off: not tried again until the compressor is next switched on
        private bool _backRefused;  // the radio refused the app's put-back: the record waits for the next connection
        private bool _quietBack;    // the put-back undoes an off that was never said, so it is not said either

        // What the radio's side reported about a write, posted from its threads and read by the tick.
        private readonly System.Collections.Concurrent.ConcurrentQueue<(int Ticket, ProcessorWriteStep Step)> _news = new();
        // What this tick has to say, said once the lock is let go.
        private readonly List<ProcessorHandoffNotice> _toSay = new();

        // The record, read from the store once per connection and kept here,
        // so a tick twice a second does not read a file twice a second. This
        // policy is the only writer of its radio's record.
        private ProcessorHandoffRecord? _record;
        private bool _recordRead;

        private ProcessorHandoffRecord? Record(string serial)
        {
            if (!_recordRead)
            {
                _record = _store.Find(serial);
                _recordRead = true;
                // A record from an earlier connection: its off is taken as sent, and the
                // connect rule settles it from this connection's first reading either way.
                _offSent = _record != null;
            }
            return _record;
        }

        private bool Keep(ProcessorHandoffRecord record)
        {
            if (!_store.Save(record)) return false;
            _record = record; _recordRead = true;
            return true;
        }

        private void Drop(string serial)
        {
            _store.Remove(serial);
            _record = null; _recordRead = true;
            _pending = Pending.None;
            _offSent = _offAccepted = _backSent = _backRefused = _quietBack = false;
            Withdraw();
        }

        /// <summary>No write asked for so far may still be made.</summary>
        private void Withdraw() => Interlocked.Increment(ref _ticket);

        /// <summary>Ask for a write, with a ticket of its own. True when it was asked for. An asking again of the same write stays in its episode.</summary>
        private bool Ask(bool on, Pending kind)
        {
            int mine = Interlocked.Increment(ref _ticket);
            bool again = _pending == kind;
            if (!_radio.WriteProcessorOn(on, () => Volatile.Read(ref _ticket) == mine, step => _news.Enqueue((mine, step))))
                return false;
            if (!again) { _episodeFirst = mine; _asks = 0; }
            _pending = kind;
            _newest = mine;
            _sent = false;
            _asks++;
            _wait = SettleTicks;
            return true;
        }

        public ProcessorHandoff(IRadioSpeechProcessor radio, ProcessorHandoffStore store,
            Action<ProcessorHandoffNotice> say, Action<string>? trace = null)
        {
            _radio = radio ?? throw new ArgumentNullException(nameof(radio));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _say = say ?? throw new ArgumentNullException(nameof(say));
            _trace = trace ?? (line => Trace.WriteLine(line));
        }

        /// <summary>Whether the app has turned this radio's processor off (or asked to) and the radio has not yet answered that it is back.</summary>
        public bool HoldsRecord { get { lock (_sync) return Record(_radio.Serial) != null; } }

        /// <summary>The radio has just connected: the next tick applies the connect rule to any record an earlier session left.</summary>
        public void Connected()
        {
            lock (_sync)
            {
                _atConnect = true;
                _session = _declined = _settled = _faulted = _faultPutBack = _saidNotYours = _connectPutBack = false;
                _faultsSeen = -1;
                _pending = Pending.None;
                _offSent = _offAccepted = _backSent = _offRefused = _backRefused = _quietBack = false;
                Withdraw();
                _recordRead = false; // read again from the store: another session may have written it
            }
        }

        /// <summary>
        /// The processor is being set by somebody other than this handoff,
        /// through the rig's own setter: the operator's key, a menu, a field,
        /// a preset (repair 2, B3b). Theirs from here, at once, whatever the
        /// app's own write has or has not done yet: nothing the app asked for
        /// earlier may still go out, its record is dropped (there is nothing
        /// of the app's left to put back), and the processor is not turned
        /// off again in this session. Call it BEFORE their write is queued,
        /// so the app's own queued write, ahead of theirs, is already
        /// withdrawn when the queue reaches it.
        /// </summary>
        public void TakenOver()
        {
            lock (_sync)
            {
                if (!_radio.Connected) return;
                string serial = _radio.Serial;
                if (string.IsNullOrWhiteSpace(serial)) return;
                Withdraw();
                bool held = _pending != Pending.None || Record(serial) != null;
                _pending = Pending.None;
                if (Record(serial) != null) Drop(serial);
                _connectPutBack = false; _faultPutBack = false;
                if (_session) _declined = true;
                if (held)
                    _trace("ProcessorHandoff: the processor of " + serial + " was set through the rig's own setter; it is theirs now, the app's record is dropped and nothing the app asked for goes out");
            }
        }

        /// <summary>One policy tick: apply the rules to what is true now.</summary>
        public void Tick(ProcessorHandoffInputs inputs)
        {
            ProcessorHandoffNotice[] say;
            lock (_sync)
            {
                TickLocked(inputs);
                if (_toSay.Count == 0) return;
                say = _toSay.ToArray();
                _toSay.Clear();
            }
            foreach (var notice in say) _say(notice);
        }

        private void TickLocked(ProcessorHandoffInputs inputs)
        {
            if (inputs == null || !_radio.Connected) return;
            string serial = _radio.Serial;
            if (string.IsNullOrWhiteSpace(serial)) return;
            if (_faultsSeen < 0) _faultsSeen = inputs.ChainFaults; // the first reading is the baseline, never a fault
            if (inputs.ChainFaults < _faultsSeen) _faultsSeen = inputs.ChainFaults; // a new stream counts from zero again

            // The reading FIRST, the news of the app's own writes after it (see the remarks).
            bool? reading = _radio.ProcessorOn;
            bool want = inputs.SettingOn && inputs.CompressorOn && inputs.PcMicrophone;
            var record = Record(serial);

            // The connect rule, once, as soon as the processor can be read.
            if (_atConnect)
            {
                if (reading is not bool atConnect) return;
                _atConnect = false;
                if (record != null)
                {
                    if (atConnect != record.AppSetOn)
                    {
                        _trace("ProcessorHandoff: at connect the processor of " + serial + " no longer holds the value the app set, so the app's record is dropped"
                            + (want ? "; the compressor is on, so this connection's session starts like any other and may turn it off again" : "; nothing is written"));
                        Drop(serial);
                        record = null;
                    }
                    else if (want)
                    {
                        // Still off as we left it, and our compressor is on: the session carries on.
                        _session = true; _settled = true; _declined = false; _faulted = false; _offAccepted = true;
                        _trace("ProcessorHandoff: at connect the processor of " + serial + " is still off as the app left it and the compressor is on; the record is kept");
                        _toSay.Add(ProcessorHandoffNotice.StillOffAtConnect);
                    }
                    else _connectPutBack = true;
                }
            }

            // A fault in the chain mid-over ends the session; the put-back below waits for the unkey.
            if (inputs.ChainFaults > _faultsSeen)
            {
                _faultsSeen = inputs.ChainFaults;
                if (_session && record != null)
                {
                    _faulted = true;
                    _faultPutBack = true;
                    _trace("ProcessorHandoff: the transmit chain faulted while the app held the processor of " + serial + " off; it is put back at the unkey");
                }
            }

            if (!want) { _faulted = false; _offRefused = false; } // the compressor next coming on is a new session
            bool active = want && !_faulted && !_offRefused;

            // What became of the app's own writes. After any news, the rules read the new state on the next tick:
            // this tick's reading was taken before the news was read, and may be older than the write it tells of.
            if (ReadNews(serial, record)) return;

            // A write on its way.
            if (_pending != Pending.None)
            {
                // If the policy no longer wants that write, it must not go out late.
                bool wanted = _pending == Pending.Off ? active && !_declined : !active;
                if (!wanted) Withdraw();
                if (_sent && !wanted)
                {
                    // It went out and is no longer wanted: the rules below undo it, or assert the other.
                    _pending = Pending.None;
                }
                else if (_sent && _pending == Pending.PutBack && record != null && reading == record.AppSetOn && !_radio.Transmitting)
                {
                    // The app's put-back was sent, and the reading holds the app's off again: not the app's own
                    // write, which put it on. The radio reports it still off, so the put-back is asked for again now.
                    _trace("ProcessorHandoff: the processor of " + serial + " still reads off after the app's put-back was sent; it is asked for again");
                    if (!Ask(record.PreviousOn, Pending.PutBack)) _pending = Pending.None;
                    return;
                }
                else if (_wait > 0) { _wait--; return; }
                else if (!_sent)
                {
                    _trace("ProcessorHandoff: a write asked of " + serial + " was never made; it is asked for again if it is still wanted");
                    _pending = Pending.None;
                }
                else
                {
                    if (_radio.Transmitting) return;              // never while transmitting: the next unkeyed tick
                    _trace("ProcessorHandoff: the radio has not answered a write sent to " + serial + "; it is sent again, and nothing is said or forgotten until it answers");
                    if (!Ask(_pending == Pending.Off ? false : (record?.PreviousOn ?? true), _pending)) _pending = Pending.None;
                    return;
                }
            }

            if (active)
            {
                if (!_session)
                {
                    _session = true; _declined = false; _settled = false; _saidNotYours = false;
                }
                if (reading is not bool on) return;
                if (record != null)
                {
                    if (_offAccepted)
                    {
                        if (on == record.AppSetOn) return;        // held off, as the radio answered
                        // On again after the radio took the app's off, and not by the rig's own setter: another
                        // station's doing. Their intent: never fought, and not turned off again this session.
                        _trace("ProcessorHandoff: the processor of " + serial + " was turned on while the compressor is on; the app's record is dropped and it is left on for this session");
                        Drop(serial);
                        _declined = true;
                        return;
                    }
                    if (_declined) return;
                    // The radio has not answered that it took the app's off (it was never made, or a put-back went
                    // out since): it is asked for, under the same conditions as the first time. The reading is not
                    // consulted: until the radio answers, it may be the app's own write or an older report.
                    if (!inputs.VoiceMode || !_radio.RadioIsOperators || _radio.Transmitting) return;
                    Ask(false, Pending.Off);
                    return;
                }
                if (_settled || _declined) return;
                if (!on) { _settled = true; return; }        // nothing to turn off, and nothing to put back later
                if (!inputs.VoiceMode) return;                // CW and digital: the processor is the operator's
                if (!_radio.RadioIsOperators)
                {
                    if (!_saidNotYours)
                    {
                        _saidNotYours = true;
                        _trace("ProcessorHandoff: the processor of " + serial + " is left alone: the radio is not marked as the operator's own");
                        _toSay.Add(ProcessorHandoffNotice.LeftAloneNotYours);
                    }
                    return;
                }
                if (_radio.Transmitting) return;              // never while transmitting: the next unkeyed tick
                // The record first: if the app stops between the two, the next
                // connect finds a record and a processor that is still on, and drops it.
                if (!Keep(new ProcessorHandoffRecord(serial, PreviousOn: true, _radio.ProcessorLevel, AppSetOn: false)))
                {
                    _trace("ProcessorHandoff: the record for " + serial + " could not be written, so the processor is left alone");
                    _settled = true;
                    return;
                }
                _offSent = _offAccepted = _backSent = _backRefused = _quietBack = false;
                if (!Ask(false, Pending.Off))
                {
                    Drop(serial);
                    return;                                   // tried again on the next tick
                }
                _settled = true;
                _trace("ProcessorHandoff: the compressor is on, so the processor of " + serial + " is being turned off");
                return;
            }

            // No session: put back what the app changed.
            _session = false;
            if (record == null) { _connectPutBack = false; _faultPutBack = false; return; }
            if (!_offSent)
            {
                // Whatever the reading says, it is not the app's doing: nothing went to the radio.
                _trace("ProcessorHandoff: the app's off was never written to " + serial + ", so there is nothing to put back");
                Drop(serial);
                _connectPutBack = false; _faultPutBack = false;
                return;
            }
            if (_backRefused) return;                         // the radio refused: the record waits for the next connection's first reading
            if (reading is not bool now) return;
            if (now != record.AppSetOn && !_backSent)
            {
                // Not the app's own put-back (none has gone out): somebody else's change.
                _trace("ProcessorHandoff: the processor of " + serial + " no longer holds the value the app set; nothing is put back");
                Drop(serial);
                _connectPutBack = false; _faultPutBack = false;
                return;
            }
            if (_radio.Transmitting) return;                  // at the unkey
            // Asked, not written, and written is not answered: the record stays until the radio answers.
            Ask(record.PreviousOn, Pending.PutBack);
        }

        /// <summary>Read what the radio's side reported about the app's writes. True when there was news of the write now on its way.</summary>
        private bool ReadNews(string serial, ProcessorHandoffRecord? record)
        {
            bool news = false;
            while (_news.TryDequeue(out var item))
            {
                // News of a write nobody is waiting on any more (withdrawn, taken over, an older episode) changes nothing.
                if (_pending == Pending.None || item.Ticket < _episodeFirst) continue;
                news = true;
                switch (item.Step)
                {
                    case ProcessorWriteStep.NotMade:
                        // The newest asking came to nothing: the rules ask again, when they allow it.
                        if (item.Ticket == _newest && !_sent) _pending = Pending.None;
                        break;
                    case ProcessorWriteStep.Sent:
                        NoteSent(item.Ticket);
                        break;
                    case ProcessorWriteStep.Accepted:
                        NoteSent(item.Ticket);
                        Accepted(serial, record);
                        break;
                    case ProcessorWriteStep.Refused:
                        NoteSent(item.Ticket);
                        Refused(serial);
                        break;
                }
            }
            return news;
        }

        /// <summary>A write of this episode is on the wire: from here the reading may show it, which proves nothing.</summary>
        private void NoteSent(int ticket)
        {
            if (_pending == Pending.Off) { _offSent = true; _backSent = false; _backRefused = false; }
            else { _backSent = true; _offAccepted = false; }
            if (ticket != _newest || _sent) return;
            _sent = true;
            _wait = Math.Min(LongestWaitTicks, SettleTicks * Math.Max(1, _asks));
        }

        /// <summary>The radio answered that it took the write: the only confirmation there is.</summary>
        private void Accepted(string serial, ProcessorHandoffRecord? record)
        {
            var kind = _pending;
            _pending = Pending.None;
            Withdraw(); // an asking again of the same write, still in the queue, is not needed
            if (kind == Pending.Off)
            {
                _offAccepted = true;
                _quietBack = false;
                _trace("ProcessorHandoff: the compressor is on, and the radio answered that the processor of " + serial + " is off (it was on at level "
                    + (record?.PreviousLevel ?? 0).ToString(CultureInfo.InvariantCulture) + ")");
                _toSay.Add(_radio.AnotherClientMayBeConnected ? ProcessorHandoffNotice.TurnedOffShared : ProcessorHandoffNotice.TurnedOff);
                return;
            }
            // Answered: only now is the debt paid and the record forgotten.
            bool quiet = _quietBack;
            _trace("ProcessorHandoff: the radio answered that the processor of " + serial + " is " + ((record?.PreviousOn ?? true) ? "on" : "off") + " again"
                + (quiet ? " (the radio had refused the app's off, so nothing is said)" : _faultPutBack ? " after the transmit chain faulted" : _connectPutBack ? " at connect" : ""));
            var notice = _faultPutBack ? ProcessorHandoffNotice.PutBackAfterFault
                : _connectPutBack ? ProcessorHandoffNotice.PutBackAtConnect
                : _radio.AnotherClientMayBeConnected ? ProcessorHandoffNotice.PutBackShared
                : ProcessorHandoffNotice.PutBack;
            _connectPutBack = false; _faultPutBack = false;
            Drop(serial);
            if (!quiet) _toSay.Add(notice);
        }

        /// <summary>The radio answered that it did not take the write. It is not asked again.</summary>
        private void Refused(string serial)
        {
            var kind = _pending;
            _pending = Pending.None;
            Withdraw();
            if (kind == Pending.Off)
            {
                // Nothing was turned off, so nothing is said. The app's own write is still in the
                // reading: the put-back road sets it right, quietly, and the off is not tried again
                // until the compressor is next switched on.
                _offRefused = true;
                _quietBack = true;
                _trace("ProcessorHandoff: the radio refused to turn the processor of " + serial + " off; the processor's reading is set back, nothing is said, and it is not tried again until the compressor is next switched on");
                return;
            }
            _backRefused = true;
            _trace("ProcessorHandoff: the radio refused to put the processor of " + serial + " back; nothing is said, the record is kept, and the next connection's first reading settles it");
        }

        /// <summary>
        /// The live link is about to be torn down: put the processor back if
        /// the app turned it off, so the radio is left as the operator had it
        /// for any other program (the brief's question 8; default yes). Written
        /// at once on the caller's thread, because the command queue may never
        /// run again; and nothing asked for earlier may go out after it. The
        /// record is KEPT, because the radio's answer cannot be waited for
        /// here; the next connect finds the processor on and drops it, or finds
        /// it still off and applies the connect rule. Says nothing: a
        /// disconnect moves focus between windows, which flushes the screen
        /// reader.
        /// </summary>
        public void Leaving()
        {
            lock (_sync) LeavingLocked();
        }

        private void LeavingLocked()
        {
            try
            {
                Withdraw();
                _pending = Pending.None;
                if (!_radio.Connected) return;
                var record = Record(_radio.Serial);
                if (record == null || !_offSent) return;
                // Still the app's off, or the app's own put-back that the radio never answered (sent again here).
                if (_radio.ProcessorOn is not bool on || (on != record.AppSetOn && !_backSent)) return;
                if (_radio.Transmitting)
                {
                    _trace("ProcessorHandoff: leaving " + _radio.Serial + " while it transmits; the processor is put back at the next connect");
                    return;
                }
                bool sent = _radio.WriteProcessorOnNow(record.PreviousOn);
                _trace("ProcessorHandoff: leaving " + _radio.Serial + "; the processor's put-back was " + (sent ? "sent" : "not sent")
                    + ", and the record is kept until the next connect confirms it");
            }
            catch (Exception ex)
            {
                _trace("ProcessorHandoff: the put-back at disconnect failed: " + ex.Message);
            }
        }
    }
}

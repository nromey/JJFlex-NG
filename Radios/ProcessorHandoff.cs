#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

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

        /// <summary>Whether the radio's speech processor is on, or null when it cannot be read now.</summary>
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
        /// Turn the processor on or off, as the APPLICATION's change (never
        /// read as the operator's). False when it could not be sent. The level
        /// is never written: the handoff does not change it.
        /// </summary>
        bool WriteProcessorOn(bool on);
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
    /// the app turns it off, records what it changed (per radio serial), and
    /// says so once. If the processor is already off there is nothing to do,
    /// and nothing to put back later.</item>
    /// <item><b>The operator's own turn-on wins.</b> If the processor is found
    /// on again during the session (by any door, a preset, another client),
    /// that is somebody's intent: the record is dropped, nothing is fought,
    /// and it is not turned off again in that session.</item>
    /// <item><b>At the end of a session</b> the processor is put back on only
    /// if it still holds the value the app set, and the app says so. The
    /// level is never written: the app never changed it.</item>
    /// <item><b>Nothing is written while the radio is transmitting</b> (the
    /// rule the audio check's put-back keeps; whether the radio accepts a
    /// processor change while keyed without a click is a bench question,
    /// #737). A change that has to wait is made on a later tick.</item>
    /// <item><b>A fault in the transmit chain mid-over</b> ends the session:
    /// the processor is put back at the unkey, said once, and not turned off
    /// again until the compressor is next switched on.</item>
    /// <item><b>Never turned off:</b> with the setting off; with the radio's
    /// own microphone; in CW and digital (the chain stands aside there, and
    /// the processor is the operator's); and on a radio not marked as the
    /// operator's own, where the compressor still runs and the app says once
    /// that the processor was left alone. Putting back what the app itself
    /// changed is done in any mode.</item>
    /// <item><b>Across a disconnect</b> the record stays on disk. An orderly
    /// disconnect tries the put-back before leaving
    /// (<see cref="Leaving"/>). At the next connect to that radio: if the
    /// processor no longer holds the app's value, the record is dropped; if
    /// it does and our compressor is not on, it is put back and said; if it
    /// does and our compressor is on, it stays off, the record is kept, and
    /// that is said.</item>
    /// </list>
    /// <para><b>Time is counted in ticks</b>, never a clock, so every rule is
    /// testable: after each write the radio's answer is not judged for
    /// <see cref="SettleTicks"/> ticks, because a write is queued and the
    /// radio's own report of it arrives a little later.</para>
    /// <para>Not thread-safe: one caller, the policy tick.</para>
    /// </remarks>
    public sealed class ProcessorHandoff
    {
        /// <summary>Ticks after a write during which the radio's reading is not judged (two seconds at the app's half-second tick).</summary>
        public const int SettleTicks = 4;

        private readonly IRadioSpeechProcessor _radio;
        private readonly ProcessorHandoffStore _store;
        private readonly Action<ProcessorHandoffNotice> _say;
        private readonly Action<string> _trace;

        private bool _atConnect, _connectPutBack;
        private bool _session, _declined, _settled, _faulted, _faultPutBack, _saidNotYours;
        private long _faultsSeen = -1;
        private int _settle;

        // The record, read from the store once per connection and kept here,
        // so a tick twice a second does not read a file twice a second. This
        // policy is the only writer of its radio's record.
        private ProcessorHandoffRecord? _record;
        private bool _recordRead;

        private ProcessorHandoffRecord? Record(string serial)
        {
            if (!_recordRead) { _record = _store.Find(serial); _recordRead = true; }
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
        }

        public ProcessorHandoff(IRadioSpeechProcessor radio, ProcessorHandoffStore store,
            Action<ProcessorHandoffNotice> say, Action<string>? trace = null)
        {
            _radio = radio ?? throw new ArgumentNullException(nameof(radio));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _say = say ?? throw new ArgumentNullException(nameof(say));
            _trace = trace ?? (line => Trace.WriteLine(line));
        }

        /// <summary>Whether the app has turned this radio's processor off and not yet put it back.</summary>
        public bool HoldsRecord => Record(_radio.Serial) != null;

        /// <summary>The radio has just connected: the next tick applies the connect rule to any record an earlier session left.</summary>
        public void Connected()
        {
            _atConnect = true;
            _session = _declined = _settled = _faulted = _faultPutBack = _saidNotYours = _connectPutBack = false;
            _faultsSeen = -1;
            _settle = 0;
            _recordRead = false; // read again from the store: another session may have written it
        }

        private void Wrote() => _settle = SettleTicks;

        /// <summary>One policy tick: apply the rules to what is true now.</summary>
        public void Tick(ProcessorHandoffInputs inputs)
        {
            if (inputs == null || !_radio.Connected) return;
            string serial = _radio.Serial;
            if (string.IsNullOrWhiteSpace(serial)) return;
            if (_faultsSeen < 0) _faultsSeen = inputs.ChainFaults; // the first reading is the baseline, never a fault
            if (inputs.ChainFaults < _faultsSeen) _faultsSeen = inputs.ChainFaults; // a new stream counts from zero again
            if (_settle > 0) { _settle--; return; }

            bool want = inputs.SettingOn && inputs.CompressorOn && inputs.PcMicrophone;
            var record = Record(serial);

            // The connect rule, once, as soon as the processor can be read.
            if (_atConnect)
            {
                if (_radio.ProcessorOn is not bool atConnect) return;
                _atConnect = false;
                if (record != null)
                {
                    if (atConnect != record.AppSetOn)
                    {
                        _trace("ProcessorHandoff: at connect the processor of " + serial + " no longer holds the value the app set; the record is dropped and the processor left as it is");
                        Drop(serial);
                        record = null;
                    }
                    else if (want)
                    {
                        // Still off as we left it, and our compressor is on: the session carries on.
                        _session = true; _settled = true; _declined = false; _faulted = false;
                        _trace("ProcessorHandoff: at connect the processor of " + serial + " is still off as the app left it and the compressor is on; the record is kept");
                        _say(ProcessorHandoffNotice.StillOffAtConnect);
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

            if (!want) _faulted = false; // the compressor next coming on is a new session
            bool active = want && !_faulted;

            if (active)
            {
                if (!_session)
                {
                    _session = true; _declined = false; _settled = false; _saidNotYours = false;
                }
                if (_radio.ProcessorOn is not bool on) return;
                if (record != null)
                {
                    if (on != record.AppSetOn)
                    {
                        // Somebody turned it back on: their intent. Never fought, and not turned off again this session.
                        _trace("ProcessorHandoff: the processor of " + serial + " was turned on while the compressor is on; the app's record is dropped and it is left on for this session");
                        Drop(serial);
                        _declined = true;
                    }
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
                        _say(ProcessorHandoffNotice.LeftAloneNotYours);
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
                if (!_radio.WriteProcessorOn(false))
                {
                    Drop(serial);
                    return;                                   // tried again on the next tick
                }
                Wrote();
                _settled = true;
                _trace("ProcessorHandoff: the compressor is on, so the processor of " + serial + " was turned off (it was on at level "
                    + _radio.ProcessorLevel.ToString(CultureInfo.InvariantCulture) + ")");
                _say(_radio.AnotherClientMayBeConnected ? ProcessorHandoffNotice.TurnedOffShared : ProcessorHandoffNotice.TurnedOff);
                return;
            }

            // No session: put back what the app changed.
            _session = false;
            if (record == null) { _connectPutBack = false; _faultPutBack = false; return; }
            if (_radio.ProcessorOn is not bool now) return;
            if (now != record.AppSetOn)
            {
                _trace("ProcessorHandoff: the processor of " + serial + " no longer holds the value the app set; nothing is put back");
                Drop(serial);
                _connectPutBack = false; _faultPutBack = false;
                return;
            }
            if (_radio.Transmitting) return;                  // at the unkey
            if (!_radio.WriteProcessorOn(record.PreviousOn)) return;
            Wrote();
            Drop(serial);
            _trace("ProcessorHandoff: the processor of " + serial + " was put back " + (record.PreviousOn ? "on" : "off")
                + (_faultPutBack ? " after the transmit chain faulted" : _connectPutBack ? " at connect" : ""));
            var notice = _faultPutBack ? ProcessorHandoffNotice.PutBackAfterFault
                : _connectPutBack ? ProcessorHandoffNotice.PutBackAtConnect
                : _radio.AnotherClientMayBeConnected ? ProcessorHandoffNotice.PutBackShared
                : ProcessorHandoffNotice.PutBack;
            _connectPutBack = false; _faultPutBack = false;
            _say(notice);
        }

        /// <summary>
        /// An orderly disconnect is about to happen: put the processor back if
        /// the app turned it off, so the radio is left as the operator had it
        /// for any other program (the brief's question 8; default yes). The
        /// record is KEPT, because the write cannot be confirmed from here; the
        /// next connect finds the processor on and drops it, or finds it still
        /// off and applies the connect rule. Says nothing: a disconnect moves
        /// focus between windows, which flushes the screen reader.
        /// </summary>
        public void Leaving()
        {
            try
            {
                if (!_radio.Connected) return;
                var record = Record(_radio.Serial);
                if (record == null) return;
                if (_radio.ProcessorOn is not bool on || on != record.AppSetOn) return;
                if (_radio.Transmitting)
                {
                    _trace("ProcessorHandoff: leaving " + _radio.Serial + " while it transmits; the processor is put back at the next connect");
                    return;
                }
                bool sent = _radio.WriteProcessorOn(record.PreviousOn);
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

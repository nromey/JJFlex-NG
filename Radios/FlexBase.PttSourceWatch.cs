#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, reading the radio's interlock source as the radio sent it, for
    /// as long as a send needs it (#835, bench 2026-10-28; see
    /// <see cref="PttSourceReading"/> for why FlexLib's own answer goes stale).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How the raw line is seen without touching FlexLib.</b> FlexLib's own
    /// telnet trace: while a topic is selected, <c>Radio.ParseStatus</c> copies
    /// every status line whose object is that topic into
    /// <c>Radio.TelnetTraceOutput</c> BEFORE it parses the line, and raises
    /// <c>PropertyChanged</c> for it. This is vendor-stock behaviour, and the
    /// same door <c>Listenback.RadioStatusTap</c> already uses for its topics.
    /// So no carried FlexLib patch is needed for this; the topic here is
    /// <c>interlock</c>, which that tap does not select, so the two never
    /// remove each other's topic.
    /// </para>
    /// <para>
    /// <b>Locks, in ONE order: the lease gate, then FlexLib's topic list, then
    /// FlexLib's output list, then the read gate.</b> FlexLib raises the
    /// trace's change from three places: <c>ParseStatus</c> and
    /// <c>SendCommand</c> while holding the topic list, and <c>ParseReply</c>
    /// while holding the OUTPUT list. So the change handler takes the output
    /// list first and its own read gate second, and nothing here ever takes
    /// the output list while holding the read gate. Until Codex's round on the
    /// 2026-10-28 fix round the handler took them the other way round, and a
    /// reply raised under the output lock could deadlock against a command
    /// raised outside it (reproduced in <c>PttSourceWatchSafetyTests</c>).
    /// </para>
    /// <para>
    /// <b>Leased, revoked, and trimmed.</b> Two vendor costs come with any
    /// selected topic. While one is selected, <c>Radio.SendCommand</c> reads
    /// each command's first word with <c>Substring(0, IndexOf(' '))</c>, which
    /// throws for a command with no space: the connect fence's bare
    /// <c>ping</c>, the connect-time <c>info</c> and <c>version</c>. So the
    /// watch is held only by a send piece, and <see cref="RevokePttWatch"/>
    /// ends every lease at once on a disconnect and before every connect. And
    /// <c>TelnetTraceOutput</c> is a list nobody trims, so the watch removes
    /// the interlock lines it caused once read, keeping only the newest, which
    /// another trace reader may still be reading as "the last line". Lines of
    /// other topics are never touched, and nothing is trimmed when someone else
    /// had the topic selected first.
    /// </para>
    /// <para>
    /// <b>Every line is read, not just the last.</b> Commands from other
    /// threads land in the same list, so the last entry at a change event can
    /// already be someone else's; the watch keeps its own place in the list
    /// and reads everything after it.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        private const string InterlockTraceTopic = "interlock";

        // See the lock order above. _pttWatchGate: leases. _pttWatchReadGate:
        // the fields the change handler reads, always taken INSIDE the output list's lock.
        private readonly object _pttWatchGate = new();
        private readonly object _pttWatchReadGate = new();
        private int _pttWatchLeases;
        private int _pttWatchGeneration;
        private bool _pttWatchAddedTopic;
        private Radio? _pttWatchRadio;
        private List<string>? _pttWatchList;
        private int _pttWatchSeen;
        private int _pttWatchTrimFrom;
        private bool _pttWatchTrims;
        // The one interlock line a lease left behind because it was the list's
        // last line when the lease ended, and where it is: other topics may
        // append after it before the next lease (#835, Codex round 2, finding 3).
        private List<string>? _pttWatchKeptList;
        private int _pttWatchKeptIndex = -1;
        private volatile string? _rawPttSource;

        /// <summary>
        /// The radio's own last <c>source=</c> value while a watch is held, or
        /// null when no watch is held or none has arrived since it began.
        /// </summary>
        internal string? RawPttSource => _rawPttSource;

        /// <summary>
        /// Read the radio's interlock source line by line until the returned
        /// lease is disposed, so <see cref="PttSourceIsHardware"/> sees a
        /// hardware line pressed under the app's own key. Leases nest; the
        /// topic is selected for the first and released with the last, or by
        /// <see cref="RevokePttWatch"/>. Disposing twice is harmless, and a
        /// lease from before a revoke does nothing when disposed.
        /// </summary>
        public IDisposable WatchPttSource()
        {
            lock (_pttWatchGate)
            {
                if (_pttWatchLeases++ == 0) StartPttWatch();
                return new PttWatchLease(this, _pttWatchGeneration);
            }
        }

        /// <summary>
        /// End every lease now (#835, Codex round 1, finding 4): the radio is
        /// disconnecting or a connection is about to be opened, and the topic
        /// must not stay selected across it, where a space-less command would
        /// throw in <c>Radio.SendCommand</c>. Leases taken before this become
        /// inert. Safe when none is held.
        /// </summary>
        internal void RevokePttWatch(string why)
        {
            lock (_pttWatchGate)
            {
                if (_pttWatchLeases == 0) return;
                Tracing.TraceLine("PttSourceWatch: revoked (" + why + "), " + _pttWatchLeases + " lease(s) ended",
                    TraceLevel.Info);
                _pttWatchLeases = 0;
                _pttWatchGeneration++;
                StopPttWatch();
            }
        }

        // Called with _pttWatchGate held.
        private void StartPttWatch()
        {
            Radio? radio = theRadio;
            _pttWatchAddedTopic = false;
            lock (_pttWatchReadGate)
            {
                _rawPttSource = null;
                _pttWatchRadio = null;
            }
            if (radio == null) return;
            try
            {
                lock (radio.SelectedTelnetTraceTopics)
                {
                    if (!radio.SelectedTelnetTraceTopics.Contains(InterlockTraceTopic))
                    {
                        radio.AddTelnetTraceTopic(InterlockTraceTopic);
                        _pttWatchAddedTopic = true;
                    }
                }
                List<string> output = radio.TelnetTraceOutput;
                lock (output)
                lock (_pttWatchReadGate)
                {
                    _pttWatchList = output;
                    _pttWatchSeen = output.Count;
                    // Ours to trim only if nobody else had the topic. Then any
                    // interlock line already in the list is left from an earlier
                    // lease of ours (the topic was unselected since), so the
                    // newest, kept last time, may go too.
                    _pttWatchTrims = _pttWatchAddedTopic;
                    // The line the last lease kept as "last", if other topics
                    // have since appended after it, is no longer anyone's last
                    // line: it goes now. If it is still last, the trim below
                    // starts at it and takes it later.
                    if (_pttWatchTrims && ReferenceEquals(output, _pttWatchKeptList)
                        && _pttWatchKeptIndex >= 0 && _pttWatchKeptIndex < output.Count - 1
                        && IsInterlockStatus(output[_pttWatchKeptIndex]))
                        output.RemoveAt(_pttWatchKeptIndex);
                    _pttWatchKeptList = null;
                    _pttWatchKeptIndex = -1;
                    // The cursor is set AFTER that removal: set before it, it
                    // pointed one past the end, and the lease's first status
                    // went unread (Codex round 3).
                    _pttWatchSeen = output.Count;
                    _pttWatchTrimFrom = Math.Max(0, output.Count - 1);
                    _pttWatchRadio = radio;
                }
                radio.PropertyChanged += OnPttWatchTrace;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("PttSourceWatch: could not start: " + ex.Message, TraceLevel.Warning);
            }
        }

        // Called with _pttWatchGate held.
        private void StopPttWatch()
        {
            Radio? radio;
            lock (_pttWatchReadGate) radio = _pttWatchRadio;
            if (radio != null)
            {
                try
                {
                    radio.PropertyChanged -= OnPttWatchTrace;
                    List<string> output = radio.TelnetTraceOutput;
                    lock (output)
                    lock (_pttWatchReadGate)
                    {
                        TrimLocked(output);
                        if (_pttWatchTrims && output.Count > 0 && IsInterlockStatus(output[^1]))
                        {
                            _pttWatchKeptList = output;
                            _pttWatchKeptIndex = output.Count - 1;
                        }
                        _pttWatchRadio = null;
                        _pttWatchList = null;
                        _rawPttSource = null;
                    }
                    if (_pttWatchAddedTopic) radio.RemoveTelnetTraceTopic(InterlockTraceTopic);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("PttSourceWatch: could not stop cleanly: " + ex.Message, TraceLevel.Warning);
                }
            }
            lock (_pttWatchReadGate)
            {
                _pttWatchRadio = null;
                _pttWatchList = null;
                _rawPttSource = null;
            }
            _pttWatchAddedTopic = false;
        }

        private void OnPttWatchTrace(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Radio.TelnetTraceOutput)) return;
            if (sender is not Radio radio) return;
            string? latest = null;
            List<string> output = radio.TelnetTraceOutput;
            // FlexLib's list FIRST: ParseReply raises this while already holding it.
            lock (output)
            lock (_pttWatchReadGate)
            {
                if (!ReferenceEquals(radio, _pttWatchRadio)) return;
                // A cleared trace is a new list; start again at its top.
                if (!ReferenceEquals(output, _pttWatchList))
                {
                    _pttWatchList = output;
                    _pttWatchSeen = 0;
                    _pttWatchTrimFrom = 0;
                }
                for (int i = _pttWatchSeen; i < output.Count; i++)
                {
                    string? source = PttSourceReading.SourceFromInterlockStatus(output[i]);
                    if (source != null) latest = source;
                }
                _pttWatchSeen = output.Count;
                TrimLocked(output);
                if (latest == null || string.Equals(latest, _rawPttSource, StringComparison.Ordinal)) return;
                _rawPttSource = latest;
            }
            Tracing.TraceLine("PttSourceWatch: the radio reports source=" + latest
                + (latest.IndexOf(',') >= 0 ? " (FlexLib reads a list as no source; read here as given)" : ""),
                TraceLevel.Info);
        }

        /// <summary>
        /// Remove the interlock status lines this watch has read, except the
        /// list's last line. Called holding the output list and the read gate.
        /// </summary>
        private void TrimLocked(List<string> output)
        {
            if (!_pttWatchTrims || !ReferenceEquals(output, _pttWatchList)) return;
            // Every line up to the last has been read by the time this runs
            // (the handler scans first; a stop no longer needs to read).
            for (int i = output.Count - 2; i >= _pttWatchTrimFrom && i >= 0; i--)
                if (IsInterlockStatus(output[i])) output.RemoveAt(i);
            // The newest line was kept as "last"; the next trim starts there.
            _pttWatchTrimFrom = Math.Max(0, output.Count - 1);
            _pttWatchSeen = output.Count;
        }

        private static bool IsInterlockStatus(string line)
        {
            if (string.IsNullOrEmpty(line) || line[0] != 'S') return false;
            int bar = line.IndexOf('|');
            return bar >= 0 && string.CompareOrdinal(line, bar + 1, "interlock ", 0, "interlock ".Length) == 0;
        }

        private sealed class PttWatchLease : IDisposable
        {
            private FlexBase? _owner;
            private readonly int _generation;
            internal PttWatchLease(FlexBase owner, int generation) { _owner = owner; _generation = generation; }
            public void Dispose()
            {
                FlexBase? owner = System.Threading.Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                lock (owner._pttWatchGate)
                {
                    if (_generation != owner._pttWatchGeneration) return;   // revoked: nothing of ours left
                    if (owner._pttWatchLeases > 0 && --owner._pttWatchLeases == 0) owner.StopPttWatch();
                }
            }
        }
    }
}

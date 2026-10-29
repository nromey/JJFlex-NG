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
    /// <b>Why leased, not held for the whole connection.</b> Two vendor costs
    /// come with any selected topic, and both are bounded by a send's life
    /// rather than paid all evening: <c>TelnetTraceOutput</c> is a list nobody
    /// trims, and while any topic is selected <c>Radio.SendCommand</c> reads
    /// each command's first word with <c>Substring(0, IndexOf(' '))</c>, which
    /// throws for a command with no space (the connect-time <c>info</c> and
    /// <c>version</c>, and a Maestro's bare <c>ping</c>). None of those is sent
    /// during a send. Outside a lease, <see cref="PttSourceIsHardware"/> is
    /// FlexLib's parse, exactly as before.
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

        // Two locks, in this order only: the lease gate may be held while
        // FlexLib's topic list or the read gate is taken, never the reverse.
        // FlexLib raises the trace's change event while it holds its topic
        // list, so the change handler takes the read gate alone.
        private readonly object _pttWatchGate = new();
        private readonly object _pttWatchReadGate = new();
        private int _pttWatchLeases;
        private Radio? _pttWatchRadio;
        private bool _pttWatchAddedTopic;
        private List<string>? _pttWatchList;
        private int _pttWatchSeen;
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
        /// topic is selected for the first and released with the last.
        /// Disposing twice is harmless. With no radio the lease holds nothing.
        /// </summary>
        public IDisposable WatchPttSource()
        {
            lock (_pttWatchGate)
            {
                if (_pttWatchLeases++ == 0) StartPttWatch();
            }
            return new PttWatchLease(this);
        }

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
                lock (_pttWatchReadGate)
                {
                    List<string> output = radio.TelnetTraceOutput;
                    lock (output)
                    {
                        _pttWatchList = output;
                        _pttWatchSeen = output.Count;
                    }
                    _pttWatchRadio = radio;
                }
                radio.PropertyChanged += OnPttWatchTrace;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("PttSourceWatch: could not start: " + ex.Message, TraceLevel.Warning);
            }
        }

        private void StopPttWatch()
        {
            Radio? radio;
            lock (_pttWatchReadGate)
            {
                radio = _pttWatchRadio;
                _pttWatchRadio = null;
                _rawPttSource = null;
                _pttWatchList = null;
            }
            if (radio == null) return;
            try
            {
                radio.PropertyChanged -= OnPttWatchTrace;
                if (_pttWatchAddedTopic) radio.RemoveTelnetTraceTopic(InterlockTraceTopic);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("PttSourceWatch: could not stop cleanly: " + ex.Message, TraceLevel.Warning);
            }
            _pttWatchAddedTopic = false;
        }

        private void OnPttWatchTrace(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Radio.TelnetTraceOutput)) return;
            if (sender is not Radio radio) return;
            string? latest = null;
            lock (_pttWatchReadGate)
            {
                if (!ReferenceEquals(radio, _pttWatchRadio)) return;
                List<string> output = radio.TelnetTraceOutput;
                lock (output)
                {
                    // A cleared trace is a new list; start again at its top.
                    if (!ReferenceEquals(output, _pttWatchList))
                    {
                        _pttWatchList = output;
                        _pttWatchSeen = 0;
                    }
                    for (int i = _pttWatchSeen; i < output.Count; i++)
                    {
                        string? source = PttSourceReading.SourceFromInterlockStatus(output[i]);
                        if (source != null) latest = source;
                    }
                    _pttWatchSeen = output.Count;
                }
                if (latest == null) return;
                if (string.Equals(latest, _rawPttSource, StringComparison.Ordinal)) return;
                _rawPttSource = latest;
            }
            Tracing.TraceLine("PttSourceWatch: the radio reports source=" + latest
                + (latest.IndexOf(',') >= 0 ? " (FlexLib reads a list as no source; read here as given)" : ""),
                TraceLevel.Info);
        }

        private sealed class PttWatchLease : IDisposable
        {
            private FlexBase? _owner;
            internal PttWatchLease(FlexBase owner) { _owner = owner; }
            public void Dispose()
            {
                FlexBase? owner = System.Threading.Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                lock (owner._pttWatchGate)
                {
                    if (owner._pttWatchLeases > 0 && --owner._pttWatchLeases == 0) owner.StopPttWatch();
                }
            }
        }
    }
}

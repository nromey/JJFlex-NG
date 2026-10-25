using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Flex.Smoothlake.FlexLib;

namespace Radios.Listenback;

/// <summary>Reference-counted lease of the existing raw status trace.</summary>
internal sealed class RadioStatusTap : IDisposable
{
    private static readonly ConditionalWeakTable<Radio, Hub> Hubs = new();
    private readonly Hub _hub;
    private int _disposed;
    public event Action<string> Status;
    internal RadioStatusTap(Radio radio)
    {
        _hub = Hubs.GetValue(radio, r => new Hub(r));
        _hub.Acquire(Receive);
    }
    private void Receive(string line)
    { if (Volatile.Read(ref _disposed) == 0) Status?.Invoke(line); }
    public void Dispose()
    { if (Interlocked.Exchange(ref _disposed, 1) == 0) _hub.Release(Receive); }
    public static Dictionary<string, string> Values(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = token.IndexOf('=');
            if (at > 0) result[token[..at]] = token[(at + 1)..];
        }
        return result;
    }
    private sealed class Hub(Radio radio)
    {
        private readonly object _gate = new();
        private readonly List<string> _added = new();
        private Action<string> _listeners;
        private int _leases;
        internal void Acquire(Action<string> listener)
        {
            lock (_gate)
            {
                _listeners += listener;
                if (_leases++ != 0) return;
                radio.PropertyChanged += Changed;
                lock (radio.SelectedTelnetTraceTopics)
                    foreach (string topic in new[] { "stream", "display", "slice", "radio", "transmit", "cw", "xvtr" })
                        if (!radio.SelectedTelnetTraceTopics.Contains(topic))
                        { radio.AddTelnetTraceTopic(topic); _added.Add(topic); }
            }
        }
        internal void Release(Action<string> listener)
        {
            lock (_gate)
            {
                _listeners -= listener;
                if (--_leases != 0) return;
                radio.PropertyChanged -= Changed;
                foreach (string topic in _added) radio.RemoveTelnetTraceTopic(topic);
                _added.Clear();
            }
        }
        private void Changed(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Radio.TelnetTraceOutput)) return;
            string line;
            var output = radio.TelnetTraceOutput;
            lock (output) line = output.LastOrDefault();
            if (line == null || !line.StartsWith("S", StringComparison.Ordinal)) return;
            var listeners = _listeners;
            if (listeners == null) return;
            foreach (Action<string> listener in listeners.GetInvocationList())
                try { listener(line); }
                catch (Exception ex) { JJTrace.Tracing.TraceLine("Listenback status observer: " + ex.Message, System.Diagnostics.TraceLevel.Error); }
        }
    }
}

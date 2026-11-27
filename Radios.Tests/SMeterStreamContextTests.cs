#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JJTrace;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #758, ruled by Noel 2026-11-26 (question 10 of the 41-question sheet): the
/// everyday log stays clean, and the opt-in meter stream's <c>sMeterData</c>
/// line carries the slice's frequency and filter width after <c>n=</c>, so the
/// shared prefix stays the contract readers parse. The extra fields are read
/// only when a line is written, never per reading.
/// </summary>
public sealed class SMeterStreamContextTests
{
    [Fact]
    public void The_context_is_the_frequency_in_hertz_the_filter_edges_and_the_width()
    {
        Assert.Equal(" freq=14200000 filter=100,2900 width=2800", FlexBase.SMeterTraceContext(14.2, 100, 2900));
        Assert.Equal(" freq=7150000 filter=-2700,-150 width=2550", FlexBase.SMeterTraceContext(7.15, -2700, -150));
        Assert.Equal(" freq=146520000 filter=-8000,8000 width=16000", FlexBase.SMeterTraceContext(146.52, -8000, 8000));
    }

    [Fact]
    public void The_context_goes_after_the_count_once_per_line_and_never_per_reading()
    {
        string channel = "sMeterData:test-" + Guid.NewGuid().ToString("N");
        var captured = new List<string>();
        var listener = new CaptureMeterSetTests.CapturingListener(captured);
        bool wasOn = Tracing.On;
        bool streamWas = MeterTraceStream.Enabled;
        Trace.Listeners.Add(listener);
        try
        {
            Tracing.On = true;
            int asked = 0;
            Func<string> context = () => { asked++; return " freq=1 filter=2,3 width=1"; };
            var stream = new MeterTraceStream();
            string? line = null;
            // MeterTraceStream.Enabled is process-wide and other classes switch
            // it, so the window is tried up to three times rather than once.
            for (int attempt = 0; attempt < 3 && line == null; attempt++)
            {
                asked = 0;
                MeterTraceStream.Enabled = true;
                for (int i = 0; i < 50; i++) stream.Report(channel, -90f - i % 3, context);
                Assert.Equal(0, asked);                   // nothing read while the second is open
                System.Threading.Thread.Sleep(1_050);
                MeterTraceStream.Enabled = true;
                stream.Report(channel, -91f, context);
                lock (captured) line = captured.LastOrDefault(l => l.Contains(channel, StringComparison.Ordinal));
            }
            Assert.NotNull(line);
            Assert.Equal(1, asked);
            int count = line!.IndexOf(" n=", StringComparison.Ordinal);
            int freq = line.IndexOf(" freq=1 filter=2,3 width=1", StringComparison.Ordinal);
            Assert.True(count > 0 && freq > count, "the context follows n=, so the shared prefix is unchanged: " + line);
            Assert.Contains(channel + " min=", line, StringComparison.Ordinal);
        }
        finally
        {
            MeterTraceStream.Enabled = streamWas;
            Tracing.On = wasOn;
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void With_the_stream_off_the_context_is_never_read()
    {
        var stream = new MeterTraceStream();
        bool was = MeterTraceStream.Enabled;
        int asked = 0;
        try
        {
            MeterTraceStream.Enabled = false;
            for (int i = 0; i < 20; i++) stream.Report("sMeterData:off-test", -90f, () => { asked++; return ""; });
        }
        finally { MeterTraceStream.Enabled = was; }
        Assert.Equal(0, asked);
    }

    [Fact]
    public void The_S_meter_handler_passes_its_context_built_once()
    {
        string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios", "FlexBase.cs")).Replace("\r\n", "\n");
        int start = src.IndexOf("private class sMeter_t", StringComparison.Ordinal);
        string body = src.Substring(start, src.IndexOf("private bool _Transmit;", start, StringComparison.Ordinal) - start);
        Assert.Contains("parent.meterTrace.Report(traceChannel, data, traceContext);", body, StringComparison.Ordinal);
        Assert.Contains("traceContext = () => SMeterTraceContext(s.Freq, s.FilterLow, s.FilterHigh);", body, StringComparison.Ordinal);
    }
}

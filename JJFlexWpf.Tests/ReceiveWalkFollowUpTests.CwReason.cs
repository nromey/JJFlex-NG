using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>The CW starting point says why it chose its model, in the trace and the noise journal (Sprint 49 Track N2).</summary>
public sealed partial class ReceiveWalkFollowUpTests
{
    // ── The CW starting point says why, in the trace and the journal ──

    // Since Sprint 49 Track K2 the figure is the median of the last few
    // measurements, and the reason says of how many.
    [Theory]
    [InlineData(4.17, 3, "premium_cw", "Premium CW measured 4.17 ms at the 99th percentile, the median of 3 measurements, under the 5 ms line")]
    [InlineData(8.0, 1, "standard_cw", "Premium CW measured 8.00 ms at the 99th percentile, the median of 1 measurement, not under the 5 ms line")]
    [InlineData(null, 0, "premium_cw", "unmeasured default, no Premium CW figure on this machine")]
    public void The_CW_starting_point_traces_and_journals_why_it_chose_its_model(double? cost, int measurements, string model, string why)
    {
        using var f = new NoiseFixture();
        f.Cost.PremiumCw99thPercentileMs = cost;
        f.Cost.Measurements = measurements;
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        var (journal, trace) = Capture(() => f.Noise.OnModeChanged("CW"));

        Assert.Equal(model, f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.Equal(why, NoiseModeRules.CwModelReason(cost, measurements));
        // One trace line naming the model and why.
        Assert.Single(trace, l => l == $"NoiseController: the CW model rule chose {model} (CW starting point): {why}");
        // One journal entry for the model, after the starting point's own, labelled with why.
        int start = journal.FindIndex(e => e.Kind == NoiseJournalKind.StartingPoint);
        int chose = journal.FindIndex(e => e.Kind == NoiseJournalKind.Model);
        Assert.True(start >= 0 && chose > start, "starting point at " + start + ", model at " + chose);
        var entry = journal[chose];
        Assert.True(entry.ByJjFlexible);
        Assert.Equal(why, entry.Label);
        Assert.Equal(new NoiseJournalChange("nnr", "model", "standard", model), entry.Changes.Single());
        Assert.Single(journal, e => e.Kind == NoiseJournalKind.Model);
    }

    [Fact]
    public void A_CW_model_the_rule_chose_on_another_road_carries_why_too()
    {
        // Choosing NNR in CW while it holds a voice model (the known-group and
        // choice roads, through EnsureCwModel): the existing entry gains the label.
        using var f = new NoiseFixture();
        f.Cost.PremiumCw99thPercentileMs = 4.17;
        f.Radio.Mode = "CW";
        f.Noise.Attach("CW");
        f.Noise.Toggle("rnnoise", "test");
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
        var (journal, trace) = Capture(() => f.Noise.Toggle("nnr", "n2-choice"));
        var entry = journal.Single(e => e.Kind == NoiseJournalKind.Model);
        Assert.Equal("n2-choice", entry.Source);
        Assert.Equal("Premium CW measured 4.17 ms at the 99th percentile, the median of 1 measurement, under the 5 ms line", entry.Label);
        Assert.Contains(trace, l => l.StartsWith("NoiseController: the CW model rule chose premium_cw (n2-choice): ", StringComparison.Ordinal));
    }

    /// <summary>The journal entries and trace lines this thread produced while acting.</summary>
    private static (List<NoiseJournalEntry> Journal, List<string> Trace) Capture(Action act)
    {
        int me = Environment.CurrentManagedThreadId;
        var journal = new List<NoiseJournalEntry>();
        var listener = new ThreadLines(me);
        void Listen(NoiseJournalEntry e) { if (Environment.CurrentManagedThreadId == me) journal.Add(e); }
        NoiseSettingsJournal.Recorded += Listen;
        System.Diagnostics.Trace.Listeners.Add(listener);
        try { act(); }
        finally
        {
            NoiseSettingsJournal.Recorded -= Listen;
            System.Diagnostics.Trace.Listeners.Remove(listener);
        }
        return (journal, listener.Lines);
    }

    private sealed class ThreadLines : System.Diagnostics.TraceListener
    {
        private readonly int _thread;
        public List<string> Lines { get; } = new();
        public ThreadLines(int thread) => _thread = thread;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (Environment.CurrentManagedThreadId != _thread || message == null) return;
            lock (Lines) Lines.Add(message);
        }
    }
}

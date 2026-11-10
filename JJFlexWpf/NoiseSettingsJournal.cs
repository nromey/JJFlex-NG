using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace JJFlexWpf
{
    /// <summary>What kind of noise change a journal entry records (#884, design part nine).</summary>
    public enum NoiseJournalKind
    {
        // Floor was Strength until #1019 flipped it; the journal writes the trace
        // only, so no stored history names the old kind.
        Switch, Lever, Floor, Model, Favourite, PresetApplied, ReturnToDefaults,
        StartingPoint, ModeRestore, IntentKept, DefaultMoved,
    }

    /// <summary>One value that changed: which engine, which setting, from what, to what.</summary>
    public readonly record struct NoiseJournalChange(string EngineId, string Setting, string OldValue, string NewValue);

    /// <summary>One journal entry. A compound entry (a preset, defaults, a starting point) carries every change it made.</summary>
    public sealed record NoiseJournalEntry(NoiseJournalKind Kind, string Source, bool ByJjFlexible,
        IReadOnlyList<NoiseJournalChange> Changes, string Label = "");

    /// <summary>
    /// The ONE recorder every app-side noise change goes through (#884, ruled
    /// 2026-11-06 18:53): called from <see cref="NoiseController"/>, never from
    /// a key, a row or a checkbox, so every surface is covered by construction.
    /// Until the change journal exists it writes to the trace only; the day it
    /// lands, it subscribes to <see cref="Recorded"/> and takes every entry
    /// without this track being rebuilt. Changes made by JJ Flexible itself
    /// (a starting point, a mode restore) are marked so undo can neither
    /// reverse them unannounced nor jump over them.
    /// </summary>
    public static class NoiseSettingsJournal
    {
        /// <summary>Raised for every entry, after it is traced. The journal sprint subscribes here; tests do too.</summary>
        public static event Action<NoiseJournalEntry>? Recorded;

        public static void Record(NoiseJournalEntry entry)
        {
            if (entry.Changes.Count == 0) return;
            TraceLine("NoiseJournal: " + entry.Kind + (entry.ByJjFlexible ? " by JJ Flexible" : "") + " from " + entry.Source
                + (entry.Label.Length > 0 ? " (" + entry.Label + ")" : "") + ": "
                + string.Join("; ", entry.Changes.Select(c => c.EngineId + (c.Setting.Length > 0 ? "." + c.Setting : "") + " " + c.OldValue + " -> " + c.NewValue)));
            try { Recorded?.Invoke(entry); }
            catch (Exception ex) { Trace.WriteLine("NoiseJournal: a listener threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        public static void Record(NoiseJournalKind kind, string source, string engineId, string setting, string oldValue, string newValue, bool byJjFlexible = false) =>
            Record(new NoiseJournalEntry(kind, source, byJjFlexible, new[] { new NoiseJournalChange(engineId, setting, oldValue, newValue) }));

        // ── Reaching the log when none is open yet (repair rounds 5 and 6) ──
        //
        // The one-time noise floor move (ReceiveNoiseFloorMove) runs inside the
        // first AudioOutputConfig.Load, which the main window's constructor makes
        // during startup, before any diagnostic log opens, and a trace line
        // written with no log open is dropped. So journal lines go through the
        // trace's KEPT route (JJTrace.Tracing.TraceLineKept): written now when a
        // log is recording, otherwise kept, in order and bounded, and written by
        // whichever log opens next (boot, a detailed capture, the standing log
        // switched on later, a restart), with a line saying how many were left
        // out past the bound. Each line carries its own timestamp.

        /// <summary>Where a journal line goes. Tests replace it to see the line; production keeps it.</summary>
        internal static Action<string> TraceLine = JJTrace.Tracing.TraceLineKept;
    }
}

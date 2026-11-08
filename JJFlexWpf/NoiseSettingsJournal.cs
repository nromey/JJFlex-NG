using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace JJFlexWpf
{
    /// <summary>What kind of noise change a journal entry records (#884, design part nine).</summary>
    public enum NoiseJournalKind
    {
        Switch, Lever, Strength, Model, Favourite, PresetApplied, ReturnToDefaults,
        StartingPoint, ModeRestore, IntentKept,
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
            Trace.WriteLine("NoiseJournal: " + entry.Kind + (entry.ByJjFlexible ? " by JJ Flexible" : "") + " from " + entry.Source
                + (entry.Label.Length > 0 ? " (" + entry.Label + ")" : "") + ": "
                + string.Join("; ", entry.Changes.Select(c => c.EngineId + (c.Setting.Length > 0 ? "." + c.Setting : "") + " " + c.OldValue + " -> " + c.NewValue)));
            try { Recorded?.Invoke(entry); }
            catch (Exception ex) { Trace.WriteLine("NoiseJournal: a listener threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        public static void Record(NoiseJournalKind kind, string source, string engineId, string setting, string oldValue, string newValue, bool byJjFlexible = false) =>
            Record(new NoiseJournalEntry(kind, source, byJjFlexible, new[] { new NoiseJournalChange(engineId, setting, oldValue, newValue) }));
    }
}

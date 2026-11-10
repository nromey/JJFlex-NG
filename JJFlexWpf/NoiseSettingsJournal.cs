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
            TraceOrHold("NoiseJournal: " + entry.Kind + (entry.ByJjFlexible ? " by JJ Flexible" : "") + " from " + entry.Source
                + (entry.Label.Length > 0 ? " (" + entry.Label + ")" : "") + ": "
                + string.Join("; ", entry.Changes.Select(c => c.EngineId + (c.Setting.Length > 0 ? "." + c.Setting : "") + " " + c.OldValue + " -> " + c.NewValue)));
            try { Recorded?.Invoke(entry); }
            catch (Exception ex) { Trace.WriteLine("NoiseJournal: a listener threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        public static void Record(NoiseJournalKind kind, string source, string engineId, string setting, string oldValue, string newValue, bool byJjFlexible = false) =>
            Record(new NoiseJournalEntry(kind, source, byJjFlexible, new[] { new NoiseJournalChange(engineId, setting, oldValue, newValue) }));

        // ── Entries made before the diagnostic log has started (repair round 5) ──
        //
        // The one-time noise floor move (ReceiveNoiseFloorMove) runs inside the
        // first AudioOutputConfig.Load, which the main window's constructor makes
        // during startup, before GetConfigInfo opens the diagnostic log. A trace
        // line written with no log open is dropped by the trace router (there is
        // no sink to take it), so on the 2026-11-09 radio walk the move ran and
        // left no NoiseJournal line, while every later change did. Until the log
        // has been seen recording once, lines are held here and written when it
        // is: by the boot sequence right after the log opens, or by the next
        // entry. After that, nothing is held and every line goes straight out,
        // exactly as before.

        /// <summary>The most lines held before the log starts; anything past it is dropped, as it always was.</summary>
        internal const int MostHeldLines = 32;

        private static readonly object _heldGate = new();
        /// <summary>Lines waiting for the log; null once the log has been seen recording.</summary>
        private static List<string>? _held = new();

        /// <summary>Whether the diagnostic log is recording now. Read without waiting on a log transition.</summary>
        internal static Func<bool> LogIsRecording = JJTrace.TraceCoordinator.RecordingWithoutWaiting;

        /// <summary>Tests only: start again as at process start, before the log has been seen recording.</summary>
        internal static void ResetHeldForTest(Func<bool> logIsRecording)
        {
            lock (_heldGate) { _held = new List<string>(); LogIsRecording = logIsRecording; }
        }

        private static void TraceOrHold(string line)
        {
            WriteHeldLines();
            lock (_heldGate)
            {
                if (_held != null && !LogIsRecording())
                {
                    if (_held.Count < MostHeldLines) _held.Add(line);
                    return;
                }
            }
            Trace.WriteLine(line);
        }

        /// <summary>
        /// Write any entries made before the diagnostic log started, once it is
        /// recording. Called by the boot sequence right after the log opens; does
        /// nothing when the log is not recording yet or nothing is held.
        /// </summary>
        public static void WriteHeldLines()
        {
            List<string> lines;
            lock (_heldGate)
            {
                if (_held == null || !LogIsRecording()) return;
                lines = _held;
                _held = null;
            }
            foreach (var line in lines) Trace.WriteLine(line + " (recorded at startup, before the diagnostic log opened)");
        }
    }
}

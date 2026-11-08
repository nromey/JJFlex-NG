using System;
using System.IO;
using System.Linq;

namespace Radios;

/// <summary>What starting a recording on a slice should do about the recording already there (#999).</summary>
public enum SliceOverwriteDecision
{
    /// <summary>Nothing to lose, or the operator turned the warning off: record at once.</summary>
    RecordNow,
    /// <summary>A recording is there and a copy of it is on this computer: warn, offering Record and Cancel.</summary>
    WarnBackedUp,
    /// <summary>A recording is there and nothing on this computer holds it: warn, offering Back up first, Record and Cancel.</summary>
    WarnNotBackedUp,
}

/// <summary>
/// Whether F11, or anything else that starts a slice recording, warns before it
/// overwrites the recording already in that slice (#999). A pure decision, so
/// every case is tested without a radio or a window; the radio's facts come in
/// through <see cref="FlexBase.SliceOverwriteFactsFor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Backed up is checked live, never stored as a flag</b> (ruled 2026-11-07).
/// A slice recording counts as backed up only while all of these hold: the
/// radio reports a length for it; the stamp for that slice still matches that
/// length within <see cref="RadioConfig.SliceTakeLengthTolerance"/>, so it
/// still describes what the radio holds; the stamp names a backup take folder;
/// and that folder still exists. Delete the backup in Play takes, or let
/// another client record over the slice, and the recording is unprotected
/// again, so the warning goes back to offering a backup. A flag kept forever
/// would say "safe" about a file that is gone.
/// </para>
/// <para>
/// <b>The warning setting turns both forms off</b>, the backed-up one as well
/// as the other: "warn before a recording on the radio is overwritten", on by
/// default and saved per radio.
/// </para>
/// </remarks>
public static class SliceOverwriteGate
{
    /// <summary>
    /// True when the stamp proves a copy of the recording the radio now reports
    /// is on this computer. <paramref name="stamp"/> is the stamp saved for the
    /// slice's letter, whether or not it still matches.
    /// </summary>
    public static bool IsBackedUp(SliceTakeStamp stamp, double? radioSeconds, Func<string, bool> folderExists)
    {
        if (stamp == null || radioSeconds is not double seconds || !double.IsFinite(seconds) || seconds <= 0) return false;
        // A damaged stamp's length can match nothing (repair round 1).
        if (!double.IsFinite(stamp.RecordedSeconds) || stamp.RecordedSeconds <= 0) return false;
        if (Math.Abs(stamp.RecordedSeconds - seconds) > RadioConfig.SliceTakeLengthTolerance) return false;
        if (string.IsNullOrWhiteSpace(stamp.BackupTakeDirectory)) return false;
        try { return folderExists?.Invoke(stamp.BackupTakeDirectory) == true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>The decision, from what the radio reports, the slice's stamp, the disk and the setting.</summary>
    /// <param name="radioHasRecording">The radio reports a playable recording on the slice.</param>
    /// <param name="radioSeconds">Its length as the radio reports it, or null when not reported.</param>
    /// <param name="stamp">The stamp saved for the slice's letter, or null.</param>
    /// <param name="folderExists">Whether a take folder is still on disk.</param>
    /// <param name="warn">The "warn before a recording on the radio is overwritten" setting.</param>
    public static SliceOverwriteDecision Decide(bool radioHasRecording, double? radioSeconds, SliceTakeStamp stamp,
        Func<string, bool> folderExists, bool warn)
    {
        if (!radioHasRecording || !warn) return SliceOverwriteDecision.RecordNow;
        return IsBackedUp(stamp, radioSeconds, folderExists)
            ? SliceOverwriteDecision.WarnBackedUp : SliceOverwriteDecision.WarnNotBackedUp;
    }

    /// <summary>The decision for the facts a radio reported, against the real disk.</summary>
    public static SliceOverwriteDecision Decide(SliceOverwriteFacts facts) =>
        facts == null ? SliceOverwriteDecision.RecordNow
            : Decide(facts.HasRecording, facts.RadioSeconds, facts.Stamp, BackupTakeReadable, facts.Warn);

    /// <summary>
    /// The live disk test for a backup (repair round 1): the take folder exists
    /// AND holds a readable, complete take, cleanly closed and playable. A folder
    /// whose take file is missing or damaged protects nothing.
    /// </summary>
    public static bool BackupTakeReadable(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
        if (!File.Exists(Path.Combine(folder, "take.txt"))) return false;
        try
        {
            var take = Listenback.TakeReader.Inspect(folder);
            return take.State == "clean" && take.CanPlay;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// What the radio and this radio's config say about one slice's recording, at
/// the moment a recording is about to start there (#999).
/// </summary>
/// <param name="Serial">The radio's serial, which keys its config.</param>
/// <param name="Letter">The slice's letter.</param>
/// <param name="HasRecording">The radio reports a playable recording on the slice.</param>
/// <param name="RadioSeconds">Its length as the radio reports it, or null.</param>
/// <param name="Stamp">The stamp saved for that letter, matching or not, or null.</param>
/// <param name="Warn">The "warn before a recording on the radio is overwritten" setting for this radio.</param>
public sealed record SliceOverwriteFacts(string Serial, string Letter, bool HasRecording, double? RadioSeconds,
    SliceTakeStamp Stamp, bool Warn);

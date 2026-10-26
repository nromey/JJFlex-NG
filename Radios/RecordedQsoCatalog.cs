#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Radios;

/// <summary>
/// One computer recording in the Recorded QSOs folder, as the recordings
/// dialog lists it (#787, Tape Deck track 4).
/// </summary>
/// <param name="Path">The full path.</param>
/// <param name="Name">The file name without its extension.</param>
/// <param name="RecordedAt">
/// Local time the recording began: from the recorder's own name when the file
/// still carries it, otherwise from the file's times (see
/// <see cref="RecordedQsoCatalog.Scan(string)"/>).
/// </param>
/// <param name="NamedByRecorder">True while the file keeps the name the recorder gave it.</param>
/// <param name="Seconds">The length, or null when the header does not say (an unfinished recording).</param>
/// <param name="Bytes">The file's size on disk.</param>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="Channels">One or two.</param>
/// <param name="TotalFrames">Sample frames, or zero when the header does not say.</param>
public sealed record RecordedQso(string Path, string Name, DateTime RecordedAt, bool NamedByRecorder,
    double? Seconds, long Bytes, int SampleRate, int Channels, long TotalFrames)
{
    /// <summary>
    /// True for a FLAC file, the recorder's own format. Only these can be
    /// trimmed or converted with Save As; the others are listed so they can
    /// be played, deleted and pruned (Noel, 2026-10-25).
    /// </summary>
    public bool IsFlac => System.IO.Path.GetExtension(Path).Equals(".flac", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A scan of the folder: what could be listed, and how many files could not be read.</summary>
public sealed record RecordedQsoScan(IReadOnlyList<RecordedQso> Recordings, int Unreadable);

/// <summary>What a prune would move, what it leaves because the file is in use, and how much space it frees.</summary>
public sealed record RecordedQsoPrunePlan(IReadOnlyList<RecordedQso> Move, IReadOnlyList<RecordedQso> InUse, long Bytes);

/// <summary>
/// The computer recordings this program made: the FLAC files under
/// <c>Recorded QSOs\year\month</c> in the settings root (#787), and the files
/// Save As wrote beside them in the other supported types. The recordings
/// dialog lists them, and "play from computer" falls back to the newest
/// recording when the file it remembered has gone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not <c>RecordingStore</c>.</b> That store is the older Recordings folder
/// of plain WAV files that feeds Reference Audio and the voice messages; Noel
/// gave computer recordings their own folder (2026-10-21 10:56) so those
/// pickers stay short. Nothing here reads or changes that folder.
/// </para>
/// <para>
/// <b>Nothing is silently dropped.</b> A file this cannot read is counted, so
/// the dialog can say how many it could not list rather than showing a shorter
/// list as if it were the whole one. A file whose header gives no length (a
/// recording still being made, or one cut off by a crash) is listed with an
/// unknown length rather than counted as unreadable: its audio is on disk.
/// </para>
/// <para>
/// <b>Order is by when the recording began, newest first</b>, from the name
/// the recorder gave it while it keeps that name, so renaming a file does not
/// move it. A renamed file uses the earlier of its creation and last-write
/// times: for a file renamed in place both survive the rename, and for a copy
/// the last-write time carries over while the creation time is the copy's.
/// </para>
/// </remarks>
public static class RecordedQsoCatalog
{
    /// <summary>The folder's name under the settings root. The recorder writes the same name.</summary>
    public const string FolderName = "Recorded QSOs";

    /// <summary>The prefix Save As gives a conversion in progress; never listed.</summary>
    public const string ExportTemporaryPrefix = ".qso-export-";

    /// <summary>The folder, resolved from the settings root so a throwaway settings tree relocates it too.</summary>
    public static string FolderPath => Path.Combine(RadioConfig.AppDataRoot, FolderName);

    /// <summary>
    /// The types listed, and so the only types Delete and Prune can reach:
    /// the recorder's FLAC and every type Save As writes. Anything else in
    /// the folder is left alone (Noel, 2026-10-25).
    /// </summary>
    public static readonly string[] Extensions = { ".flac", ".wav", ".mp3", ".opus", ".m4a" };

    /// <summary>
    /// Reads a non-FLAC file's rate, channels and length in frames, or throws.
    /// Installed at startup by the WPF side, which has the decoders. Unset,
    /// or failing, the file is still listed, with its length unknown: its
    /// audio is on disk and it can still be deleted.
    /// </summary>
    public static Func<string, (int Rate, int Channels, long Frames)>? OtherReader { get; set; }

    /// <summary>Scan the real folder.</summary>
    public static RecordedQsoScan Scan() => Scan(FolderPath);

    /// <summary>Scan a folder and everything under it.</summary>
    public static RecordedQsoScan Scan(string root)
    {
        var found = new List<RecordedQso>();
        int unreadable = 0;
        IEnumerable<string> files;
        try
        {
            if (!Directory.Exists(root)) return new(found, 0);
            files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            }).Where(IsListedType).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            JJTrace.Tracing.TraceLine("RecordedQsoCatalog: could not list " + root + ": " + ex.Message,
                System.Diagnostics.TraceLevel.Warning);
            return new(found, 0);
        }

        foreach (string path in files)
        {
            string file = System.IO.Path.GetFileName(path);
            if (file.StartsWith(ExportTemporaryPrefix, StringComparison.Ordinal)) continue;
            var item = Read(path);
            if (item == null) unreadable++;
            else found.Add(item);
        }
        found.Sort(NewestFirst);
        return new(found, unreadable);
    }

    /// <summary>True when the file's type is one the folder lists.</summary>
    public static bool IsListedType(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Read one file's entry, or null when it cannot be read as a recording.</summary>
    public static RecordedQso? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var (rate, channels, frames) = System.IO.Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase)
                ? RecordedQsoFlacDecoder.ReadStreamInfo(path)
                : ReadOther(path);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            bool generated = TryParseRecorderName(name, out DateTime started);
            // A file Save As wrote is listed by when it was SAVED, even when its
            // name still carries the recording's stamp (retest 2026-10-25: "11:25
            // AM saved as opus"). A FLAC trim's "-trim" name already fails the
            // recorder-name test above.
            bool savedCopy = !System.IO.Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase);
            if (!generated || savedCopy)
            {
                DateTime created = info.CreationTime, written = info.LastWriteTime;
                started = created < written ? created : written;
            }
            double? seconds = frames > 0 && rate > 0 ? frames / (double)rate : null;
            return new RecordedQso(path, name, started, generated, seconds, info.Length, rate, channels, frames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or EndOfStreamException or ArgumentException)
        {
            JJTrace.Tracing.TraceLine("RecordedQsoCatalog: could not read " + path + ": " + ex.Message,
                System.Diagnostics.TraceLevel.Warning);
            return null;
        }
    }

    /// <summary>
    /// A file another program wrote: its header through <see cref="OtherReader"/>,
    /// or an unknown length when that cannot read it. Never null: a file Save
    /// As made must stay deletable even when this computer cannot decode it.
    /// </summary>
    private static (int Rate, int Channels, long Frames) ReadOther(string path)
    {
        var reader = OtherReader;
        if (reader == null) return (0, 0, 0);
        try { return reader(path); }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("RecordedQsoCatalog: length of " + path + " unknown: " + ex.Message,
                System.Diagnostics.TraceLevel.Info);
            return (0, 0, 0);
        }
    }

    /// <summary>The newest recording the recorder made, or null when there is none. Save As files are not recordings.</summary>
    /// <remarks>
    /// The recorder's own name AND FLAC, not FLAC alone: Save As writes FLAC
    /// too, so "newest FLAC" let a trimmed or operator-named copy become Play
    /// from computer's default after a restart (Codex review, 2026-10-25). A
    /// recording the operator renamed can still be chosen in Recorded QSOs.
    /// </remarks>
    public static RecordedQso? Newest() => Scan().Recordings.FirstOrDefault(r => r.IsFlac && r.NamedByRecorder);

    /// <summary>
    /// The recorder's own name, <c>QSO-yyyy-MM-dd-HHmmss</c>, with the
    /// <c>-2</c>, <c>-3</c> it adds when two recordings start in one second.
    /// Anything else, including a Save As copy's <c>-trim</c>, is not the
    /// recorder's name.
    /// </summary>
    public static bool TryParseRecorderName(string name, out DateTime recordedAt)
    {
        recordedAt = default;
        if (name == null || !name.StartsWith("QSO-", StringComparison.Ordinal)) return false;
        const int stamp = 17; // yyyy-MM-dd-HHmmss
        if (name.Length < 4 + stamp) return false;
        string tail = name.Substring(4 + stamp);
        if (tail.Length > 0 && (tail[0] != '-' || tail.Length == 1 || !tail.Skip(1).All(char.IsAsciiDigit)))
            return false;
        return DateTime.TryParseExact(name.Substring(4, stamp), "yyyy-MM-dd-HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out recordedAt);
    }

    /// <summary>
    /// A name Save As made up itself: the recorder's stamp, then "-trim" or
    /// "-copy", then optionally "-2", "-3". A row for such a file leaves the name off, as a
    /// recording's row does, because it only repeats what the row already says.
    /// </summary>
    public static bool IsSaveAsName(string name)
    {
        // The recorder's stamp, then "-trim" and/or "-copy", then a number:
        // "-copy2" since 2026-10-25, or the "-2" style Save As used before.
        if (name == null || name.Length < 21 || !TryParseRecorderName(name.Substring(0, 21), out _)) return false;
        string tail = name.Substring(21);
        bool saved = false;
        if (tail.StartsWith("-trim", StringComparison.Ordinal)) { tail = tail.Substring(5); saved = true; }
        if (tail.StartsWith("-copy", StringComparison.Ordinal)) { tail = tail.Substring(5); saved = true; }
        if (!saved) return false;
        if (tail.StartsWith("-", StringComparison.Ordinal)) tail = tail.Substring(1);
        return tail.All(char.IsAsciiDigit);
    }

    /// <summary>True when the path is a file inside the folder, at any depth.</summary>
    public static bool IsInFolder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        string full = System.IO.Path.GetFullPath(path);
        string folder = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    private static int NewestFirst(RecordedQso a, RecordedQso b)
    {
        int byTime = b.RecordedAt.CompareTo(a.RecordedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(b.Name, a.Name);
    }

    /// <summary>
    /// Prune by count (Noel, 2026-10-24 16:01: "the oldest 20 or days old"):
    /// the <paramref name="count"/> oldest recordings. One in use among them is
    /// left where it is and named, never swapped for a newer one, so the
    /// operator gets at most the count they asked for and never a surprise.
    /// </summary>
    public static RecordedQsoPrunePlan PlanOldest(IReadOnlyList<RecordedQso> recordings, int count,
        Func<string, bool> inUse)
    {
        var oldest = recordings.OrderBy(r => r.RecordedAt).ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, count));
        return Plan(oldest, inUse);
    }

    /// <summary>
    /// Prune by keeping (Noel, retest 2026-10-25: "keep x of the newest
    /// recordings"): everything except the <paramref name="keep"/> newest,
    /// oldest first. A file in use among them stays and is named, as for the
    /// other two.
    /// </summary>
    public static RecordedQsoPrunePlan PlanKeepNewest(IReadOnlyList<RecordedQso> recordings, int keep,
        Func<string, bool> inUse)
    {
        var older = recordings.OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.Name, StringComparer.Ordinal)
            .Skip(Math.Max(0, keep))
            .OrderBy(r => r.RecordedAt).ThenBy(r => r.Name, StringComparer.Ordinal);
        return Plan(older, inUse);
    }

    /// <summary>
    /// Prune by age: every recording that began more than <paramref name="days"/>
    /// days before <paramref name="now"/>. No date picker (Noel: "Picking a date
    /// can be kind of finicky").
    /// </summary>
    public static RecordedQsoPrunePlan PlanOlderThan(IReadOnlyList<RecordedQso> recordings, int days,
        DateTime now, Func<string, bool> inUse)
    {
        DateTime cutoff = now - TimeSpan.FromDays(Math.Max(0, days));
        return Plan(recordings.Where(r => r.RecordedAt < cutoff)
            .OrderBy(r => r.RecordedAt).ThenBy(r => r.Name, StringComparer.Ordinal), inUse);
    }

    private static RecordedQsoPrunePlan Plan(IEnumerable<RecordedQso> chosen, Func<string, bool> inUse)
    {
        var move = new List<RecordedQso>();
        var busy = new List<RecordedQso>();
        foreach (var r in chosen)
        {
            bool held;
            try { held = inUse?.Invoke(r.Path) == true; }
            catch { held = true; }
            (held ? busy : move).Add(r);
        }
        return new(move, busy, move.Sum(r => r.Bytes));
    }
}

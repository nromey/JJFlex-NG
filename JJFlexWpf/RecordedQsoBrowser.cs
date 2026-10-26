using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// The words the recordings dialog uses (#787, Tape Deck track 4), in one
/// place so a row, a position and a trim range read the same wherever they
/// appear. Every sentence here is DRAFT PROSE (#787): awaiting Noel; the text
/// lives in the lexicon under <c>audio.qsos.*</c>.
/// </summary>
// DRAFT PROSE (#787): awaiting Noel.
internal static class RecordedQsoWords
{
    /// <summary>
    /// When a recording began, with the date said once: "Today at 3:30 PM",
    /// "Yesterday at 8:15 PM", "Thursday, October 22 at 3:30 PM", and the year
    /// added only when it is not this year. Noel's reading (2026-10-24): the
    /// time reads as a time, never as a number like "153012".
    /// </summary>
    internal static string When(DateTime at, DateTime now)
    {
        string time = at.ToString("h:mm tt", CultureInfo.CurrentCulture);
        if (at.Date == now.Date) return Lexicon.Get("audio.qsos.when_today", ("time", time));
        if (at.Date == now.Date.AddDays(-1)) return Lexicon.Get("audio.qsos.when_yesterday", ("time", time));
        string date = at.ToString(at.Year == now.Year ? "dddd, MMMM d" : "dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
        return Lexicon.Get("audio.qsos.when_date", ("date", date), ("time", time));
    }

    /// <summary>The same, for the middle of a sentence: "today at 3:30 PM".</summary>
    internal static string WhenInSentence(DateTime at, DateTime now)
    {
        string when = When(at, now);
        if (at.Date == now.Date || at.Date == now.Date.AddDays(-1))
            return char.ToLower(when[0], CultureInfo.CurrentCulture) + when.Substring(1);
        return when;
    }

    /// <summary>A length the way a person says it, or "length unknown".</summary>
    internal static string Length(double? seconds) =>
        seconds is double s ? RecordingStore.DescribeLength(s) : Lexicon.Get("audio.qsos.length_unknown");

    /// <summary>
    /// One row: the date once, the time as a time, the length, and nothing
    /// else. "FLAC" is left off because the recorder's files are FLAC; the
    /// recorder's file name is left off because it only repeats the date. A
    /// file the operator renamed leads with its new name. A file Save As wrote
    /// in another type says its type, so "-trim.opus" and "-trim.mp3" can be
    /// told apart (Noel, 2026-10-25). Any file Save As wrote reads as when it
    /// was saved and what as: "Today at 11:02 AM, saved as Opus, 1.3 seconds"
    /// (retest 2026-10-25).
    /// </summary>
    internal static string Row(RecordedQso q, DateTime now, bool stillRecording = false)
    {
        string length = stillRecording ? Lexicon.Get("audio.qsos.recording_now") : Length(q.Seconds);
        string? type = Kind(q.Path);
        // A file Save As wrote: when it was saved, its type, its length, and
        // its name only when the operator chose one (retest 2026-10-25: "can it
        // just read time and duration with file type like '11:25 AM saved as
        // opus'"). The name is one Shift+Tab away, in the file name box.
        bool saveAsName = RecordedQsoCatalog.IsSaveAsName(q.Name);
        if (type != null || saveAsName)
        {
            string saved = type ?? Lexicon.Get("audio.qsos.type_flac");
            return q.NamedByRecorder || saveAsName
                ? Lexicon.Get("audio.qsos.row_saved", ("when", When(q.RecordedAt, now)), ("type", saved), ("length", length))
                : Lexicon.Get("audio.qsos.row_named_saved", ("name", q.Name), ("type", saved),
                    ("when_lower", WhenInSentence(q.RecordedAt, now)), ("length", length));
        }
        return q.NamedByRecorder
            ? Lexicon.Get("audio.qsos.row", ("when", When(q.RecordedAt, now)), ("length", length))
            : Lexicon.Get("audio.qsos.row_named", ("name", q.Name),
                ("when_lower", WhenInSentence(q.RecordedAt, now)), ("length", length));
    }

    /// <summary>The spoken type of a non-FLAC file ("Opus", "MP3", "WAV", "M4A"), or null for FLAC.</summary>
    internal static string? Kind(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => Lexicon.Get("audio.qsos.type_wav"),
        ".mp3" => Lexicon.Get("audio.qsos.type_mp3"),
        ".opus" => Lexicon.Get("audio.qsos.type_opus"),
        ".m4a" => Lexicon.Get("audio.qsos.type_m4a"),
        _ => null,
    };

    /// <summary>
    /// A point in a recording to the tenth of a second: "the start", "the
    /// end", "1.3 seconds" or "2 minutes 5.3 seconds". Noel, 2026-10-25: whole
    /// seconds were too coarse once marks and steps go to a tenth ("from 1
    /// second to 2 seconds, under a second" read as wrong arithmetic).
    /// </summary>
    internal static string Position(double seconds, double? total)
    {
        if (seconds < 0.05) return Lexicon.Get("audio.qsos.the_start");
        if (total is double t && seconds >= t - 0.05) return Lexicon.Get("audio.qsos.the_end");
        return Precise(seconds);
    }

    /// <summary>
    /// A duration to the tenth of a second, as words: "0.8 seconds", "1
    /// second", "12.5 seconds", "1 minute", "2 minutes 5.3 seconds". Positions,
    /// marks and selections use it; a row's length stays in whole seconds.
    /// </summary>
    internal static string Precise(double seconds)
    {
        long tenths = (long)Math.Round(Math.Max(0, seconds) * 10, MidpointRounding.AwayFromZero);
        long minutes = tenths / 600;
        double rest = (tenths % 600) / 10.0;
        string secs = rest == 1 ? Lexicon.Get("audio.qsos.one_second")
            : Lexicon.Get("audio.qsos.n_seconds", ("n", rest.ToString("0.#", CultureInfo.CurrentCulture)));
        if (minutes == 0) return secs;
        string mins = minutes == 1 ? Lexicon.Get("audio.qsos.one_minute")
            : Lexicon.Get("audio.qsos.n_minutes", ("n", minutes));
        return rest == 0 ? mins : mins + " " + secs;
    }

    /// <summary>A step size's label: "0.1 seconds", "1 second", "5 seconds", "1 minute".</summary>
    internal static string Step(double seconds) =>
        seconds == 1 ? Lexicon.Get("audio.qsos.step_one_second")
        : seconds == 60 ? Lexicon.Get("audio.qsos.step_one_minute")
        : Lexicon.Get("audio.qsos.step_seconds", ("seconds", seconds.ToString("0.#", CultureInfo.CurrentCulture)));

    /// <summary>A file size in words: "800 kilobytes", "34 megabytes", "1.2 gigabytes".</summary>
    internal static string Size(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        if (bytes < mb)
            return Lexicon.Get("audio.qsos.size_kilobytes", ("n", Math.Max(1, (long)Math.Round(bytes / kb))));
        if (bytes < gb)
        {
            double m = bytes / mb;
            return Lexicon.Get("audio.qsos.size_megabytes",
                ("n", m < 10 ? m.ToString("0.#", CultureInfo.CurrentCulture) : Math.Round(m).ToString(CultureInfo.CurrentCulture)));
        }
        return Lexicon.Get("audio.qsos.size_gigabytes", ("n", (bytes / gb).ToString("0.#", CultureInfo.CurrentCulture)));
    }
}

/// <summary>
/// What the recordings dialog knows about the one recording in front of the
/// operator: where they are in it, the step size, and the trim marks. No
/// window, so every rule here is tested without one (#787, Tape Deck track 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The trim is two marks on one recording</b> (Noel, 2026-10-21 09:19: "I'm
/// not looking for Reaper functionality here"). Until a mark is set the
/// selection is the whole recording. A start at or after the end, or an end
/// at or before the start, is refused and neither mark moves. Marks never
/// edit the file; Save As writes the selection to a new one.
/// </para>
/// <para>
/// <b>The step size is remembered</b>, across restarts too (Noel, 2026-10-25,
/// reversing 2026-10-24 16:03's "I don't think it needs to remember" at the
/// bench). The dialog keeps it in the audio settings; this class only holds it.
/// </para>
/// </remarks>
// DRAFT PROSE (#787): awaiting Noel.
internal sealed class RecordedQsoBrowser
{
    /// <summary>
    /// The step sizes the combo offers, in seconds: a tenth of a second up to
    /// a minute (Noel, 2026-10-21 09:25; 0.1 and 0.5 added 2026-10-25).
    /// </summary>
    internal static readonly double[] Steps = { 0.1, 0.5, 1, 5, 10, 30, 60 };

    /// <summary>The step before one has ever been chosen.</summary>
    internal const double DefaultStep = 5;

    /// <summary>A remembered step, or the default when it is not one the combo offers.</summary>
    internal static double ValidStep(double seconds)
    {
        foreach (double s in Steps)
            if (Math.Abs(s - seconds) < 0.0001) return s;
        return DefaultStep;
    }

    internal RecordedQso? Current { get; private set; }
    internal double Cursor { get; private set; }
    internal double TrimStart { get; private set; }
    internal double? TrimEnd { get; private set; }
    internal double StepSeconds { get; set; } = DefaultStep;

    /// <summary>The recording's length, or null when its header does not say.</summary>
    internal double? Duration => Current?.Seconds;

    /// <summary>True when the position can move and the recording can be trimmed: its length is known.</summary>
    internal bool CanMove => Duration is > 0;

    /// <summary>True while the selection is still the whole recording.</summary>
    internal bool IsWhole => TrimStart <= 0 && TrimEnd == null;

    /// <summary>The selection's end, the recording's end until "]" is pressed.</summary>
    internal double SelectionEnd => TrimEnd ?? Duration ?? 0;

    /// <summary>Choose a recording. Position and marks start again, at the start and the whole recording.</summary>
    internal void Select(RecordedQso? recording)
    {
        if (ReferenceEquals(recording, Current) ||
            (recording != null && Current != null && recording.Path == Current.Path && recording.Seconds == Current.Seconds))
        {
            Current = recording;
            return;
        }
        Current = recording;
        Cursor = 0;
        TrimStart = 0;
        TrimEnd = null;
    }

    /// <summary>Put the position somewhere, clamped to the recording. A stop or a pause lands here.</summary>
    internal void MoveTo(double seconds)
    {
        double end = Duration ?? double.MaxValue;
        Cursor = Math.Clamp(seconds, 0, end);
    }

    /// <summary>
    /// Step the position back (-1) or forward (+1) by the step size, and say
    /// where it landed, as a complete sentence. Refused when the length is
    /// unknown.
    /// </summary>
    internal string Step(int direction)
    {
        if (!CanMove) return Lexicon.Get("audio.qsos.no_position");
        double total = Duration!.Value;
        double next = Cursor + Math.Sign(direction) * StepSeconds;
        if (next <= 0)
        {
            Cursor = 0;
            return Lexicon.Get("audio.qsos.at_start");
        }
        if (next >= total)
        {
            Cursor = total;
            return Lexicon.Get("audio.qsos.at_end");
        }
        Cursor = next;
        return At();
    }

    /// <summary>Jump to the start or the end of the recording.</summary>
    internal string Edge(bool toEnd)
    {
        if (!CanMove) return Lexicon.Get("audio.qsos.no_position");
        Cursor = toEnd ? Duration!.Value : 0;
        return Lexicon.Get(toEnd ? "audio.qsos.at_end" : "audio.qsos.at_start");
    }

    /// <summary>"You are at 1 minute 5 seconds of 12 minutes 8 seconds."</summary>
    internal string At() => Lexicon.Get("audio.qsos.at",
        ("position", RecordedQsoWords.Position(Cursor, Duration)), ("length", RecordedQsoWords.Length(Duration)));

    /// <summary>The position control's value: "1 minute 5 seconds of 12 minutes 8 seconds".</summary>
    /// <param name="at">Where the play head is, when it is not the browser's position (a preview sounding).</param>
    internal string PositionValue(double? at = null) => Lexicon.Get("audio.qsos.position_value",
        ("position", RecordedQsoWords.Position(at ?? Cursor, Duration)), ("length", RecordedQsoWords.Length(Duration)));

    /// <summary>Mark the trim start at <paramref name="at"/> seconds. Returns the sentence to speak.</summary>
    internal (bool Changed, string Sentence) SetStart(double at)
    {
        if (!CanMove) return (false, Lexicon.Get("audio.qsos.no_position"));
        at = Math.Clamp(at, 0, Duration!.Value);
        if (at >= SelectionEnd) return (false, Lexicon.Get("audio.qsos.trim_start_refused"));
        TrimStart = at;
        return (true, Lexicon.Get("audio.qsos.trim_start_set", Range(("start", RecordedQsoWords.Position(at, Duration)))));
    }

    /// <summary>Mark the trim end at <paramref name="at"/> seconds. Returns the sentence to speak.</summary>
    internal (bool Changed, string Sentence) SetEnd(double at)
    {
        if (!CanMove) return (false, Lexicon.Get("audio.qsos.no_position"));
        at = Math.Clamp(at, 0, Duration!.Value);
        if (at <= TrimStart) return (false, Lexicon.Get("audio.qsos.trim_end_refused"));
        // A mark at the very end is no mark at all: the selection runs to the end.
        TrimEnd = at >= Duration.Value - 0.0005 ? null : at;
        return (true, Lexicon.Get("audio.qsos.trim_end_set", Range(("end", RecordedQsoWords.Position(at, Duration)))));
    }

    /// <summary>
    /// Clear both marks: the selection is the whole recording again (Backspace,
    /// and Clear selection). Says only that the marks went (Noel, retest
    /// 2026-10-25: "I'd say trim marks cleared. The whole recording is kind of
    /// understood at that point"); the selection line still reads in full.
    /// </summary>
    internal string ClearSelection()
    {
        TrimStart = 0;
        TrimEnd = null;
        return Lexicon.Get("audio.qsos.marks_cleared");
    }

    /// <summary>Clear the trim start mark (Shift+Left bracket). Returns the sentence to speak.</summary>
    internal (bool Changed, string Sentence) ClearStart()
    {
        if (TrimStart <= 0) return (false, Lexicon.Get("audio.qsos.no_start_mark"));
        TrimStart = 0;
        return (true, Lexicon.Get("audio.qsos.start_cleared", ("selection", Selection())));
    }

    /// <summary>Clear the trim end mark (Shift+Right bracket). Returns the sentence to speak.</summary>
    internal (bool Changed, string Sentence) ClearEnd()
    {
        if (TrimEnd == null) return (false, Lexicon.Get("audio.qsos.no_end_mark"));
        TrimEnd = null;
        return (true, Lexicon.Get("audio.qsos.end_cleared", ("selection", Selection())));
    }

    /// <summary>The selection, as the read-only line and as a sentence.</summary>
    internal string Selection()
    {
        if (Current == null) return "";
        if (!CanMove) return Lexicon.Get("audio.qsos.whole_length_unknown");
        if (IsWhole) return Lexicon.Get("audio.qsos.whole", ("length", RecordedQsoWords.Precise(Duration!.Value)));
        return Lexicon.Get("audio.qsos.selection", Range());
    }

    /// <summary>The selection in the recording's own sample frames: start, and end or null for "to the end".</summary>
    internal (long StartFrame, long? EndFrame) Frames()
    {
        int rate = Current?.SampleRate ?? 0;
        if (rate <= 0) return (0, null);
        long start = (long)Math.Round(TrimStart * rate);
        long? end = TrimEnd is double e ? (long)Math.Round(e * rate) : null;
        if (Current!.TotalFrames > 0)
        {
            start = Math.Min(start, Current.TotalFrames);
            if (end is long last) end = Math.Min(last, Current.TotalFrames);
        }
        return (start, end);
    }

    /// <summary>The selection's length in seconds.</summary>
    internal double SelectionSeconds => Math.Max(0, SelectionEnd - TrimStart);

    private (string, object?)[] Range(params (string, object?)[] lead)
    {
        var parts = new List<(string, object?)>(lead);
        if (!parts.Any(p => p.Item1 == "start"))
            parts.Add(("start", RecordedQsoWords.Position(TrimStart, Duration)));
        if (!parts.Any(p => p.Item1 == "end"))
            parts.Add(("end", RecordedQsoWords.Position(SelectionEnd, Duration)));
        parts.Add(("length", RecordedQsoWords.Precise(SelectionSeconds)));
        return parts.ToArray();
    }

    /// <summary>
    /// Which row the dialog opens on: Play from computer's choice while it
    /// still exists, otherwise the newest. Opening never changes what Play from
    /// computer plays. (The newest-recording box this used to tick came out on
    /// Noel's ruling, 2026-10-25: "just enter plays the currently selected.")
    /// </summary>
    internal static int Opening(IReadOnlyList<RecordedQso> rows, string? chosen)
    {
        if (!string.IsNullOrEmpty(chosen))
            for (int i = 0; i < rows.Count; i++)
                if (string.Equals(rows[i].Path, chosen, StringComparison.OrdinalIgnoreCase))
                    return i;
        return rows.Count > 0 ? 0 : -1;
    }
}

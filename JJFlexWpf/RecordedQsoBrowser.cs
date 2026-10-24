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
    /// else. "FLAC" is left off because every file here is FLAC; the
    /// recorder's file name is left off because it only repeats the date. A
    /// file the operator renamed leads with its new name.
    /// </summary>
    internal static string Row(RecordedQso q, DateTime now, bool stillRecording = false)
    {
        string length = stillRecording ? Lexicon.Get("audio.qsos.recording_now") : Length(q.Seconds);
        return q.NamedByRecorder
            ? Lexicon.Get("audio.qsos.row", ("when", When(q.RecordedAt, now)), ("length", length))
            : Lexicon.Get("audio.qsos.row_named", ("name", q.Name),
                ("when_lower", WhenInSentence(q.RecordedAt, now)), ("length", length));
    }

    /// <summary>A point in a recording: "the start", "the end", or "1 minute 5 seconds".</summary>
    internal static string Position(double seconds, double? total)
    {
        if (seconds < 0.5) return Lexicon.Get("audio.qsos.the_start");
        if (total is double t && seconds >= t - 0.5) return Lexicon.Get("audio.qsos.the_end");
        return RecordingStore.DescribeLength(seconds);
    }

    /// <summary>A step size's label: "1 second", "5 seconds", "1 minute".</summary>
    internal static string Step(int seconds) =>
        seconds == 1 ? Lexicon.Get("audio.qsos.step_one_second")
        : seconds == 60 ? Lexicon.Get("audio.qsos.step_one_minute")
        : Lexicon.Get("audio.qsos.step_seconds", ("seconds", seconds));

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
/// <b>The step size is not remembered</b> between openings (Noel, 2026-10-24
/// 16:03: "I don't think it needs to remember"): each dialog starts at five
/// seconds.
/// </para>
/// </remarks>
// DRAFT PROSE (#787): awaiting Noel.
internal sealed class RecordedQsoBrowser
{
    /// <summary>The step sizes the combo offers, in seconds: 1 second up to a minute (Noel, 2026-10-21 09:25).</summary>
    internal static readonly int[] Steps = { 1, 5, 10, 30, 60 };

    /// <summary>The step every opening starts at.</summary>
    internal const int DefaultStep = 5;

    internal RecordedQso? Current { get; private set; }
    internal double Cursor { get; private set; }
    internal double TrimStart { get; private set; }
    internal double? TrimEnd { get; private set; }
    internal int StepSeconds { get; set; } = DefaultStep;

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
    internal string PositionValue() => Lexicon.Get("audio.qsos.position_value",
        ("position", RecordedQsoWords.Position(Cursor, Duration)), ("length", RecordedQsoWords.Length(Duration)));

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

    /// <summary>Put the selection back to the whole recording.</summary>
    internal string UseWhole()
    {
        TrimStart = 0;
        TrimEnd = null;
        return Selection();
    }

    /// <summary>The selection, as the read-only line and as a sentence.</summary>
    internal string Selection()
    {
        if (Current == null) return "";
        if (IsWhole || !CanMove) return Lexicon.Get("audio.qsos.whole", ("length", RecordedQsoWords.Length(Duration)));
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
        parts.Add(("length", RecordingStore.DescribeLength(SelectionSeconds)));
        return parts.ToArray();
    }

    /// <summary>
    /// Which row the dialog opens on, and whether the newest-recording box is
    /// ticked (Noel, 2026-10-24 15:55-15:56). A choice that still exists is
    /// kept and shown, box unticked; otherwise the newest is selected and the
    /// box is ticked. Opening never changes what Play from computer plays.
    /// </summary>
    internal static (int Index, bool NewestTicked) Opening(IReadOnlyList<RecordedQso> rows, string? chosen)
    {
        if (!string.IsNullOrEmpty(chosen))
            for (int i = 0; i < rows.Count; i++)
                if (string.Equals(rows[i].Path, chosen, StringComparison.OrdinalIgnoreCase))
                    return (i, false);
        return (rows.Count > 0 ? 0 : -1, true);
    }
}

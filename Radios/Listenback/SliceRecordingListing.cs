#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Radios.Listenback;

/// <summary>
/// One of the radio's slice recordings, as Play takes lists it (#999): the
/// slice, the length the radio reports, the stamp when it still describes the
/// recording, and whether a copy is on this computer, checked now.
/// </summary>
public sealed record SliceRecordingRow(string Letter, double Seconds, SliceTakeStamp? Stamp, bool BackedUp, string Sentence)
{
    /// <summary>The recorder target for this row's slice, by its letter.</summary>
    public SliceRecorderTarget Target => SliceRecorderTarget.Letter(Letter[0]);

    /// <summary>Made outside JJ Flexible: no stamp describes it, or the stamp says so (ruled S1).</summary>
    public bool MadeOutside => Stamp == null || Stamp.Source == SliceTakeSource.Outside;
}

/// <summary>
/// The rows Play takes shows for the radio's slice recordings, above the
/// computer takes, and the sentence each row reads (#999). Only the radio can
/// say what it holds, so with no radio there are no rows. Kept out of the window
/// so it is tested without one.
/// </summary>
/// <remarks>PLACEHOLDER: Codex writes (#999). Every sentence is an <c>audio.takes.slice_*</c> key.</remarks>
public static class SliceRecordingListing
{
    /// <summary>A row for every slice of ours the radio reports a recording on, in slice-letter order.</summary>
    public static IReadOnlyList<SliceRecordingRow> Rows(FlexBase? rig, DateTime nowLocal, Func<string, bool>? folderExists = null)
    {
        if (rig == null) return Array.Empty<SliceRecordingRow>();
        folderExists ??= Directory.Exists;
        var rows = new List<SliceRecordingRow>();
        foreach (string letter in rig.SliceRecorderLetters())
        {
            var facts = rig.SliceOverwriteFactsFor(SliceRecorderTarget.Letter(letter[0]));
            if (facts == null || !facts.HasRecording || facts.RadioSeconds is not double seconds) continue;
            var stamp = facts.Stamp != null && Math.Abs(facts.Stamp.RecordedSeconds - seconds) <= RadioConfig.SliceTakeLengthTolerance
                ? facts.Stamp : null;
            bool backedUp = SliceOverwriteGate.IsBackedUp(stamp, seconds, folderExists);
            rows.Add(new(facts.Letter, seconds, stamp, backedUp, Sentence(facts.Letter, seconds, stamp, backedUp, nowLocal)));
        }
        return rows.OrderBy(r => r.Letter, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// "Slice B recording, 30 seconds, made earlier today at 8:15 PM on 14.200
    /// megahertz USB. Not backed up." A recording no stamp describes says it was
    /// made outside JJ Flexible (S1).
    /// </summary>
    public static string Sentence(string letter, double seconds, SliceTakeStamp? stamp, bool backedUp, DateTime nowLocal)
    {
        string length = ((int)Math.Round(seconds)).ToString(CultureInfo.CurrentCulture);
        string made;
        if (stamp == null || stamp.Source == SliceTakeSource.Outside)
            made = Lexicon.Get("audio.takes.slice_made_outside");
        else
        {
            string when = stamp.MadeUtc.Kind == DateTimeKind.Utc
                ? TakeListing.When(stamp.MadeUtc, nowLocal) : Lexicon.Get("audio.takes.when_unknown");
            bool check = stamp.Source == SliceTakeSource.AudioCheck;
            if (stamp.FrequencyMHz > 0)
                made = Lexicon.Get(check ? "audio.takes.slice_made_check_at" : "audio.takes.slice_made_at", ("when", when),
                    ("frequency", stamp.FrequencyMHz.ToString("0.000###", CultureInfo.CurrentCulture)), ("mode", stamp.Mode ?? ""));
            else
                made = Lexicon.Get(check ? "audio.takes.slice_made_check" : "audio.takes.slice_made", ("when", when));
        }
        return Lexicon.Get("audio.takes.slice_row", ("letter", letter), ("seconds", length), ("made", made),
            ("backup", Lexicon.Get(backedUp ? "audio.takes.slice_backed_up" : "audio.takes.slice_not_backed_up")));
    }
}

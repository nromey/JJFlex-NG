using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Radios.Listenback;

/// <summary>One row of the Play takes window: the take, and the sentence a screen reader reads for it.</summary>
public sealed record TakeRow(TakeCatalogEntry Entry, string Sentence, string TestId, string TestName, int? TakeNumber, string Note)
{
    public string Directory => Entry.Directory;
    public string Kind => Entry.Kind;
}

/// <summary>
/// What the Play takes window shows, kept out of the window so it can be
/// tested without one (Unified takes, plan Phase 4 and Q6): one list, newest
/// first, one sentence per row; a Show selector offering All takes, then only
/// the kinds the operator has takes of; and the details of one take.
/// </summary>
/// <remarks>DRAFT PROSE throughout: awaiting Noel. Every sentence is an <c>audio.takes.*</c> lexicon key.</remarks>
public static class TakeListing
{
    /// <summary>The Show selector's first entry, and its default.</summary>
    public const string ShowAll = "all";

    /// <summary>Every take under the root as a row, newest first.</summary>
    public static IReadOnlyList<TakeRow> Rows(string root, DateTime nowLocal) =>
        TakeCatalog.List(root).Select(entry => Row(root, entry, nowLocal)).ToList();

    public static TakeRow Row(string root, TakeCatalogEntry entry, DateTime nowLocal)
    {
        var member = TakeSession.Membership(root, entry.Directory);
        string note = "";
        try { note = TakeSession.Metadata(entry.Directory)?.Note ?? ""; }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is System.Text.Json.JsonException) { }
        string sentence = Sentence(entry, member?.Test.Name, member?.Number, note, nowLocal);
        return new(entry, sentence, member?.Test.Id ?? "", member?.Test.Name ?? "", member?.Number, note);
    }

    /// <summary>
    /// "Radio recording, earlier today at 8:15 PM, W6R-23T take 3, on
    /// FLEX-8600 Bench, mic profile Default. Note: new windscreen."
    /// </summary>
    public static string Sentence(TakeCatalogEntry entry, string testName, int? number, string note, DateTime nowLocal)
    {
        string kind = TakeKindWords.Name(entry.Kind);
        string when = When(entry.MadeUtc, nowLocal);
        if (!entry.HeaderReadable || entry.Kind == TakeKind.Damaged)
            return Lexicon.Get("audio.takes.row_damaged", ("kind", kind), ("when", when));
        string membership = testName == null ? Lexicon.Get("audio.takes.row_no_test")
            : Lexicon.Get("audio.takes.row_member", ("test", testName), ("number", (number ?? 0).ToString(CultureInfo.CurrentCulture)));
        string radio = string.Join(" ", new[] { entry.Get("model"), entry.Get("nickname") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (radio.Length == 0) radio = Lexicon.Get("audio.takes.unknown_radio");
        string profile = string.IsNullOrWhiteSpace(entry.Get("mic-profile")) ? Lexicon.Get("audio.iq.no_mic_profile") : entry.Get("mic-profile");
        string row = Lexicon.Get("audio.takes.row", ("kind", kind), ("when", when), ("membership", membership),
            ("radio", radio), ("profile", profile));
        return string.IsNullOrWhiteSpace(note) ? row : row + " " + Lexicon.Get("audio.takes.row_note", ("note", note));
    }

    /// <summary>When a take was made, in the words the comparisons already use.</summary>
    public static string When(DateTime madeUtc, DateTime nowLocal)
    {
        if (madeUtc == DateTime.MinValue) return Lexicon.Get("audio.takes.when_unknown");
        var local = madeUtc.ToLocalTime();
        string time = local.ToString("h:mm tt", CultureInfo.CurrentCulture);
        return local.Date == nowLocal.Date ? Lexicon.Get("audio.iq.when_today", ("time", time))
            : local.Date == nowLocal.Date.AddDays(-1) ? Lexicon.Get("audio.iq.when_yesterday", ("time", time))
            : Lexicon.Get("audio.iq.when_date", ("date", local.ToString("dddd, MMMM d", CultureInfo.CurrentCulture)), ("time", time));
    }

    /// <summary>
    /// The Show selector's choices: All takes, then each kind the operator
    /// has at least one take of, in the ruled order. A kind with no takes is
    /// not offered (Q6).
    /// </summary>
    public static IReadOnlyList<string> ShowOptions(IEnumerable<TakeRow> rows)
    {
        var present = new HashSet<string>(rows.Select(r => r.Kind));
        return new[] { ShowAll }.Concat(TakeKind.Known.Where(present.Contains)).ToList();
    }

    public static string ShowName(string option) =>
        option == ShowAll ? Lexicon.Get("audio.takes.show_all") : TakeKindWords.Name(option);

    public static IEnumerable<TakeRow> Filter(IEnumerable<TakeRow> rows, string option) =>
        option == ShowAll ? rows : rows.Where(r => r.Kind == option);

    /// <summary>
    /// A take's details, one sentence per line: what it is, how it was made,
    /// its state, and its own figures, attributed to it (never a difference).
    /// </summary>
    public static string Details(TakeRow row, TakeInspection take, TakeStatistics stats)
    {
        var lines = new List<string> { row.Sentence };
        string radio = string.Join(" ", new[] { take.Get("model"), take.Get("nickname") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        lines.Add(Lexicon.Get("audio.takes.details_made", ("radio", radio.Length == 0 ? Lexicon.Get("audio.takes.unknown_radio") : radio),
            ("serial", take.Get("serial") ?? Lexicon.Get("audio.iq.unknown")),
            ("transport", take.Get("transport") ?? Lexicon.Get("audio.iq.unknown"))));
        lines.Add(Lexicon.Get("audio.takes.details_state", ("state", Lexicon.Get(StateKey(take.State))),
            ("seconds", take.CanPlay ? TakeFacts.Seconds(take).ToString("F1", CultureInfo.CurrentCulture) : "0")));
        if (take.State != "clean" && take.Reason.Length > 0)
            lines.Add(Lexicon.Get("audio.takes.details_reason", ("reason", take.Reason)));
        if (take.IsAudio)
        {
            lines.Add(Lexicon.Get("audio.takes.details_provenance"));
            if (take.Get(AudioTakeFields.BoundaryQuality) != AudioTakeFields.Verified)
                lines.Add(Lexicon.Get("audio.takes.details_boundary_unverified"));
            if (take.Get(TakeAnalysis.CaptureIsolation) != TakeAnalysis.Isolated)
                lines.Add(Lexicon.Get("audio.takes.details_contaminated"));
            if (take.Get("playback-slice-muted") == "True")
                lines.Add(Lexicon.Get("audio.takes.details_playback_muted"));
        }
        if (stats == null || !stats.Available)
            lines.Add(Lexicon.Get("audio.takes.details_no_figures", ("reason", stats?.Reason ?? Lexicon.Get("audio.iq.unknown"))));
        else
        {
            string figures = Lexicon.Get("audio.takes.details_figures",
                ("lufs", stats.Lufs.ToString("F1", CultureInfo.CurrentCulture)), ("peak", stats.PeakDbfs.ToString("F1", CultureInfo.CurrentCulture)),
                ("rms", stats.RmsDbfs.ToString("F1", CultureInfo.CurrentCulture)),
                ("brightness", stats.BrightnessDb.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)),
                ("punch", stats.PunchDb.ToString("F1", CultureInfo.CurrentCulture)));
            if (stats.SplatterAvailable)
                figures += " " + Lexicon.Get("audio.takes.details_splatter", ("splatter", stats.SplatterDbfs.ToString("F1", CultureInfo.CurrentCulture)));
            lines.Add(figures);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string StateKey(string state) => state switch
    {
        "clean" => "audio.iq.state.clean",
        "incomplete" => "audio.iq.state.incomplete",
        _ => "audio.iq.state.corrupt"
    };

    /// <summary>
    /// The file to play or export: an audio take's capture; an IQ take's
    /// trimmed playback render when one exists, else its heard.wav.
    /// </summary>
    public static string AudioFile(string directory, TakeInspection take)
    {
        if (take.IsAudio) return Path.Combine(directory, AudioTakeFields.WavName);
        string play = Path.Combine(directory, "heard-play.wav");
        return File.Exists(play) ? play : Path.Combine(directory, "heard.wav");
    }

    /// <summary>
    /// Save a copy of a take's audio as WAV or FLAC (Q3: the format is chosen
    /// when saving a copy, not when recording). Never touches the take: an IQ
    /// take is rendered first if it has never been, an audio take is read as
    /// it is. Returns false when the take cannot be played.
    /// </summary>
    public static bool SaveCopy(string directory, string destination, bool flac)
    {
        var take = TakeReader.Inspect(directory);
        if (!take.CanPlay) return false;
        if (!take.IsAudio && !File.Exists(Path.Combine(directory, "heard.wav"))) SsbTakeRenderer.Render(directory);
        string source = AudioFile(directory, take);
        if (!flac) { File.Copy(source, destination, overwrite: true); return true; }
        var (rate, samples) = TakeAudio.ReadMono(source);
        using var file = new RecordedQsoFlac(destination, rate, 1);
        file.Write(samples);
        return true;
    }
}

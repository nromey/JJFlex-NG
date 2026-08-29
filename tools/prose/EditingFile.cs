using System.Text;

namespace Prose;

/// <summary>
/// The file the operator actually edits, and the reader that takes it back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The format is the deliverable.</b> Every rule below exists because this
/// file is READ WITH A SCREEN READER, by someone changing three words in the
/// middle of a lot of prose. So:
/// </para>
/// <para>
/// Two heading levels and nothing else, because that is two navigation planes:
/// 1 walks the sections, 2 walks the sentences. Under a heading, the very next
/// thing is the words — no key, no file name, no line number in the way. All
/// the machinery sits on lines that begin with a quote mark, AFTER the words,
/// where heading navigation skips it entirely and linear reading meets it as a
/// short separator between one sentence and the next.
/// </para>
/// <para>
/// A sentence is ONE LINE, however long. Hard wrapping would mean an edit to
/// three words rewraps a paragraph, and it would make "did he mean a line
/// break here" a question this tool has to guess at. A screen reader does not
/// care about line width; an editor fixing a word does.
/// </para>
/// <para>
/// Where a sentence has moving parts, a "Reads as" line shows it with real
/// values in place — because a stem and a suffix each read perfectly and can
/// still join into nonsense, and the only way to hear that is to hear the
/// whole thing assembled.
/// </para>
/// </remarks>
public static class EditingFile
{
    private const string ProvenanceHeading = "Where these words live";
    private const string HowToHeading = "How to use this file";

    // ────────────────────────────────────────────────────────────────
    //  Writing it
    // ────────────────────────────────────────────────────────────────

    public static string Render(Surface surface, IReadOnlyList<Entry> entries, string stamp)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("# " + surface.Title);
        Line();
        Line(surface.Blurb);
        Line();

        Line("## " + HowToHeading);
        Line();
        Line("Every heading below is one thing the tool says. Under the heading is what it "
           + "says, as one paragraph. Change the words, save the file, and run "
           + "`tools\\prose\\prose apply` to put them back into the program. Nothing else "
           + "you do here has any effect, so you can read straight through it without "
           + "worrying about breaking anything.");
        Line();
        Line("Your screen reader's heading keys are the way around. Level one walks the "
           + "sections; level two walks the sentences, one heading per sentence.");
        Line();
        Line("Lines that begin with a quote mark are for the tool, not for you. They carry "
           + "the name it files each sentence under, so it can find its way back to the "
           + "code. Leave them alone and ignore them; nothing in them is worth hearing.");
        Line();
        Line("Some sentences have a value the program fills in when it speaks — a power "
           + "level, a test ID, an antenna port. Those show in braces, like `{RunId}`. "
           + "Keep every one of them, and keep them in the order they appear; you can "
           + "change every word around them. Those entries also carry a `Reads as` line "
           + "showing the sentence with real values in it, which is what an operator "
           + "actually hears.");
        Line();
        Line("If you change something the tool cannot write back, it refuses the whole run "
           + "and tells you which sentence and what is wrong with it. It never writes a "
           + "half-applied file.");
        Line();
        Line("To throw away everything you have changed here and start again from the "
           + "program's current words, run `tools\\prose\\prose extract --force`.");
        Line();

        foreach (SurfaceFile file in surface.Files)
        {
            List<Entry> mine = entries.Where(e => e.File == file.Path).ToList();
            if (mine.Count == 0) continue;

            Line("# " + file.Section);
            Line();
            if (file.About.Length > 0) { Line(file.About); Line(); }

            foreach (Entry e in mine)
            {
                Line("## " + e.Label);
                Line();
                Line(e.Text);
                Line();

                if (e.PlaceholderOrder.Count > 0)
                {
                    Line("> Reads as: " + e.Reads);
                    Line("> Keep " + Naming.List(e.PlaceholderOrder.Distinct())
                       + (e.PlaceholderOrder.Count > 1 ? ", in that order." : "."));
                }
                if (e.HasInlineMarkup)
                    Line("> Keep the markup exactly as it is — it builds the page.");

                // Where a fragment is glued onto its neighbour, say so. This
                // is precisely where a sentence loses a word: two halves that
                // each read perfectly and join badly, and nothing in either
                // half shows it.
                string join = JoinNote(e);
                if (join.Length > 0) Line("> " + join);

                Line("> " + e.Key);
                Line();
            }
        }

        Line("# " + ProvenanceHeading);
        Line();
        Line("Nothing to read here. This is how the tool finds each sentence again.");
        Line();
        Line("> " + stamp);
        foreach (Entry e in entries)
            Line("> " + e.Key + " · " + e.File + " " + e.Line + " · " + e.Fingerprint);
        Line();

        return sb.ToString();
    }

    /// <summary>
    /// "Runs on from the words before it." — said only where it is true, and
    /// worth a line because a join is where a connective goes missing.
    /// </summary>
    private static string JoinNote(Entry e)
    {
        bool before = e.ShellOpen.EndsWith(' ');
        bool after = e.ShellClose.StartsWith(' ');
        return (before, after) switch
        {
            (true, true) => "This runs on from the words before it and into the words after "
                          + "it, so it is spoken as part of a longer sentence.",
            (true, false) => "This runs on from the words before it, so it is spoken as part "
                           + "of a longer sentence.",
            (false, true) => "The words after this run straight on from it, so it is spoken "
                           + "as part of a longer sentence.",
            _ => "",
        };
    }

    /// <summary>
    /// The read-aloud file: headings and assembled sentences, nothing else.
    /// </summary>
    /// <remarks>
    /// Stiltedness is far more obvious spoken than on a page, and a full read
    /// of the editing file would put a dotted key between every two sentences.
    /// This one is for listening to the whole surface end to end. It applies
    /// nothing and is regenerated every time.
    /// </remarks>
    public static string RenderForReading(Surface surface, IReadOnlyList<Entry> entries)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("# " + surface.Title + ", read aloud");
        Line();
        Line("Everything the tool says, in the order an operator meets it, with the values "
           + "it fills in shown as realistic examples. This file is for listening to. "
           + "Editing it does nothing — the file to edit is "
           + surface.EditingFile + ".");
        Line();

        foreach (SurfaceFile file in surface.Files)
        {
            List<Entry> mine = entries.Where(e => e.File == file.Path).ToList();
            if (mine.Count == 0) continue;

            Line("# " + file.Section);
            Line();
            foreach (Entry e in mine)
            {
                Line("## " + e.Label);
                Line();
                Line(Naming.StripTags(e.Reads));
                Line();
            }
        }

        return sb.ToString();
    }

    // ────────────────────────────────────────────────────────────────
    //  Reading it back
    // ────────────────────────────────────────────────────────────────

    public sealed class Parsed
    {
        /// <summary>Key to the words as the file now has them.</summary>
        public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

        /// <summary>Key to the fingerprint recorded when the file was made.</summary>
        public Dictionary<string, string> Fingerprints { get; } = new(StringComparer.Ordinal);

        /// <summary>Headings whose block carried no key — a sign of hand editing.</summary>
        public List<string> Orphans { get; } = [];
    }

    public static Parsed Parse(string markdown)
    {
        var result = new Parsed();
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');

        string heading = "";
        var body = new List<string>();
        string key = "";
        bool inProvenance = false;

        void Close()
        {
            if (heading.Length > 0)
            {
                string text = string.Join(" ", body.Select(b => b.Trim())
                                                   .Where(b => b.Length > 0)).Trim();
                if (key.Length > 0) result.Texts[key] = text;
                else if (text.Length > 0) result.Orphans.Add(heading);
            }
            heading = "";
            key = "";
            body.Clear();
        }

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Close();
                inProvenance = line[2..].Trim() == ProvenanceHeading;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                // A sentence heading ends the provenance section wherever it
                // appears: the provenance never contains one, and an entry
                // added below it must be read rather than silently dropped.
                inProvenance = false;
                // The instructions are prose for the reader, not a sentence
                // the program says.
                string h = line[3..].Trim();
                heading = h == HowToHeading ? "" : h;
                continue;
            }

            if (line.StartsWith('>'))
            {
                string note = line[1..].Trim();

                if (inProvenance)
                {
                    string[] bits = note.Split('·', StringSplitOptions.TrimEntries);
                    if (bits.Length == 3) result.Fingerprints[bits[0]] = bits[2];
                    continue;
                }

                // The bare key line: no spaces, dotted, lowercase.
                if (!note.Contains(' ') && note.Contains('.')) key = note;
                continue;
            }

            if (heading.Length > 0 && !inProvenance) body.Add(line);
        }

        Close();
        return result;
    }
}

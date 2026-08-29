namespace Prose.Tests;

/// <summary>
/// The file itself, judged as something a person reads with a screen reader.
/// </summary>
/// <remarks>
/// These are not cosmetic. The whole track fails if the file is unpleasant to
/// move through, however correct the code behind it is — so the properties
/// that make it navigable are asserted like any other behaviour.
/// </remarks>
public sealed class EditingFileTests
{
    [Fact]
    public void EveryEntryPutsItsWordsImmediatelyUnderItsHeading()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        string[] lines = w.Markdown.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("## ", StringComparison.Ordinal)) continue;
            if (lines[i] == "## How to use this file") continue;

            Assert.Equal("", lines[i + 1]);

            // The very next thing after the heading is the sentence: no key,
            // no file name, no line number between the editor and the words.
            Assert.False(lines[i + 2].StartsWith('>'),
                         "Machinery before the words under: " + lines[i]);
            Assert.NotEqual("", lines[i + 2]);
        }
    }

    [Fact]
    public void OnlyTwoHeadingLevelsAreUsedSoBothNavigationPlanesStayMeaningful()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        foreach (string line in w.Markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith('#')) continue;
            int level = line.TakeWhile(c => c == '#').Count();
            Assert.InRange(level, 1, 2);
        }
    }

    /// <summary>
    /// Every machine line begins with a quote mark, so heading navigation
    /// skips it and a linear read meets it as a short, obvious separator.
    /// </summary>
    [Fact]
    public void AllMachineryIsOnQuotedLines()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        foreach (Entry e in w.Open().Entries)
            Assert.Contains("> " + e.Key, w.Markdown, StringComparison.Ordinal);

        // And no key ever appears as ordinary text.
        foreach (string line in w.Markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith('>') || line.StartsWith('#')) continue;
            Assert.DoesNotContain("fixer.stage.", line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A sentence is one line however long. Hard wrapping would mean changing
    /// three words rewraps a paragraph, and it would leave the tool guessing
    /// whether a line break was meant.
    /// </summary>
    [Fact]
    public void EachSentenceIsOneLine()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        foreach (Entry e in w.Open().Entries)
            Assert.Contains("\r\n" + e.Text + "\r\n", w.Markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The listen-through file carries the sentences and nothing else: no
    /// keys, no braces, no markup. Stiltedness is far more obvious spoken than
    /// on a page, and this is the file for hearing it.
    /// </summary>
    [Fact]
    public void TheReadAloudFileHasNoMachineryInItAtAll()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        string read = File.ReadAllText(w.Path_("tools/prose/fixer-prose-read.md"));

        Assert.DoesNotContain("fixer.stage.", read, StringComparison.Ordinal);
        Assert.DoesNotContain("{", read, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", read, StringComparison.Ordinal);
        Assert.DoesNotContain("\n>", read, StringComparison.Ordinal);

        // But it does carry the words, with the values filled in.
        Assert.Contains("at 25 watts into ANT1", read, StringComparison.Ordinal);
        Assert.Contains("Nothing here keys the radio.", read, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where a fragment is glued onto its neighbour the file says so, because
    /// a join is exactly where a connective goes missing and neither half
    /// shows it.
    /// </summary>
    [Fact]
    public void FragmentsThatRunOnSayThatTheyDo()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        Entry glued = w.Open().Entries.First(e => e.ShellOpen.EndsWith(' '));
        int at = w.Markdown.IndexOf("> " + glued.Key, StringComparison.Ordinal);
        Assert.True(at > 0);

        Assert.Contains("runs on from the words before it",
                        w.Markdown[Math.Max(0, at - 400)..at], StringComparison.Ordinal);
    }

    /// <summary>
    /// Reading the file back gives exactly what was written into it. This is
    /// the parser's half of the round trip, tested on the real file rather
    /// than on something shaped like it.
    /// </summary>
    [Fact]
    public void ParsingTheFileBackGivesEverySentenceUnchanged()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        EditingFile.Parsed parsed = EditingFile.Parse(w.Markdown);
        List<Entry> entries = [.. w.Open().Entries];

        Assert.Equal(entries.Count, parsed.Texts.Count);
        Assert.Empty(parsed.Orphans);

        foreach (Entry e in entries)
        {
            Assert.Equal(e.Text, parsed.Texts[e.Key]);
            Assert.Equal(e.Fingerprint, parsed.Fingerprints[e.Key]);
        }
    }

    /// <summary>
    /// An editor who hard-wraps a paragraph anyway gets what he meant, not a
    /// refusal about line breaks.
    /// </summary>
    [Fact]
    public void AHardWrappedParagraphIsReadBackAsOneSentence()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        const string key = "fixer.stage.audio-setup.question";
        w.Edit(key, "What is your audio\r\nactually doing\r\nright now?");

        Assert.Equal("What is your audio actually doing right now?", w.TextOf(key));
        Assert.Equal(0, w.Run("apply").Code);
    }
}

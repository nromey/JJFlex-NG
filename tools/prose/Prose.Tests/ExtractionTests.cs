namespace Prose.Tests;

/// <summary>
/// What comes out, and what deliberately does not.
/// </summary>
public sealed class ExtractionTests
{
    /// <summary>
    /// THE reason this tool parses rather than greps. This sentence is written
    /// as one <c>Append</c> chain with a live value spliced into the middle of
    /// it and its second half wrapped across two lines. An extractor working
    /// literal by literal would hand the editor "Your test ID is " and
    /// ". Everything this run records carries it, so keep it with " and "any
    /// email about this problem." — three fragments, each of which reads
    /// perfectly, and none of which shows whether they join into a sentence.
    /// </summary>
    [Fact]
    public void ASentenceSplitAcrossAppendCallsComesOutWhole()
    {
        using var w = new Workspace();
        Entry e = Find(w, "Your test ID is");

        Assert.Equal(
            "Your test ID is <strong>{RunId}</strong>. Everything this run records carries "
            + "it, so keep it with any email about this problem.",
            e.Text);

        // And the editor is shown what an operator will hear.
        Assert.Contains("TX-4K2P", e.Reads, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same, for a four-literal concatenation in an object initializer —
    /// the shape most of the stage prose is written in.
    /// </summary>
    [Fact]
    public void AConcatenationAcrossFourLinesComesOutAsOneSentence()
    {
        using var w = new Workspace();
        Entry e = w.Open().Entries.Single(x => x.Key == "fixer.stage.audio-setup.explanation");

        Assert.StartsWith("Reads the open audio stream directly", e.Text, StringComparison.Ordinal);
        Assert.EndsWith("Nothing here keys the radio.", e.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', e.Text);
    }

    /// <summary>
    /// A sentence built from live facts shows them named, and shows what it
    /// reads like with real values in place — which is the only way a join is
    /// audible before it ships.
    /// </summary>
    [Fact]
    public void ASentenceWithLiveFactsShowsThemNamedAndFilledIn()
    {
        using var w = new Workspace();
        Entry e = w.Open().Entries
            .Single(x => x.Key == "fixer.stage.transmitter-check.describe-run-action");

        Assert.Contains("{AtInto}", e.Text, StringComparison.Ordinal);
        Assert.Contains("{SecondsPhrase}", e.Text, StringComparison.Ordinal);
        Assert.Equal(
            "Running this counts down with three tones, then keys the radio's own tune "
            + "carrier at 25 watts into ANT1 for about two seconds.",
            e.Reads);
    }

    /// <summary>
    /// The wrapping markup comes off, so what the editor meets is the words.
    /// </summary>
    [Fact]
    public void WrappingMarkupIsTakenOffTheWords()
    {
        using var w = new Workspace();
        List<Entry> entries = [.. w.Open().Entries];

        Entry stop = entries.Single(e => e.Text == "Stop everything");
        Assert.Equal("button", stop.ShellKind);
        Assert.Contains("<button", stop.ShellOpen, StringComparison.Ordinal);

        Assert.Contains(entries, e => e.ShellKind == "bullet"
                                   && e.Text.StartsWith("Each check is a heading",
                                                        StringComparison.Ordinal));
    }

    /// <summary>
    /// The positive control for every "it did not extract that" claim below:
    /// prove the extractor DOES find the things it is supposed to, on the real
    /// files, before trusting it about the things it left behind.
    /// </summary>
    [Theory]
    [InlineData("fixer.stage.audio-setup.title", "Audio setup")]
    [InlineData("fixer.stage.spoken-transmit.question",
                "Does your voice reach the radio through your microphone?")]
    [InlineData("fixer.finding.pc-audio-off.us.what-to-do", "Turn PC audio on")]
    [InlineData("fixer.declaration.radio-hearing.question", "Can you hear the radio right now?")]
    [InlineData("fixer.answer.antenna-load.choices.dummy-load.label", "A dummy load")]
    [InlineData("fixer.answer.antenna-load.choices-now.dummy-load.label",
                "A dummy load — someone at the station has confirmed it is connected")]
    public void KnownSentencesAreOfferedUnderKnownKeys(string key, string words)
    {
        using var w = new Workspace();
        Assert.Equal(words, w.Open().Entries.Single(e => e.Key == key).Text);
    }

    [Fact]
    public void MachineryIsNotOfferedAsWriting()
    {
        using var w = new Workspace();
        List<string> texts = [.. w.Open().Entries.Select(e => e.Text)];

        // The page's own script and stylesheet.
        Assert.DoesNotContain(texts, t => t.Contains("prefers-color-scheme", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("postMessage", StringComparison.Ordinal));

        // Wire values, ids, help topics and date formats.
        Assert.DoesNotContain(texts, t => t.Contains("fixer/transmit/", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("yyyy-MM-dd", StringComparison.Ordinal));
        Assert.DoesNotContain("notrun", texts);

        // Developer text: an exception message reads like prose and is heard
        // by nobody.
        Assert.DoesNotContain(texts, t => t.Contains("a finding needs an id", StringComparison.Ordinal));
    }

    /// <summary>
    /// A switch that maps a machine word to the word for it has one string a
    /// person hears and one nobody ever does. Both are spelled the same.
    /// </summary>
    [Fact]
    public void TheSpokenHalfOfAStatusSwitchIsOfferedAndTheMachineHalfIsNot()
    {
        using var w = new Workspace();
        List<Entry> spoken = [.. w.Open().Entries.Where(e => e.Key.Contains("status-phrase",
                                                                            StringComparison.Ordinal))];

        Assert.Equal(5, spoken.Count);
        Assert.Contains(spoken, e => e.Text == "problems found");
        Assert.Contains(spoken, e => e.Text == "not yet run");
    }

    /// <summary>
    /// Two wordings of the same finding get keys that say which is which,
    /// rather than one of them being "the second one".
    /// </summary>
    [Fact]
    public void TwoWordingsOfOneFindingAreToldApartByWhoCanFixIt()
    {
        using var w = new Workspace();
        List<string> keys = [.. w.Open().Entries.Select(e => e.Key)
                                    .Where(k => k.StartsWith("fixer.finding.mme-in-use",
                                                             StringComparison.Ordinal))];

        Assert.Contains("fixer.finding.mme-in-use.us.what-is-wrong", keys);
        Assert.Contains("fixer.finding.mme-in-use.nobody-here.what-is-wrong", keys);
    }

    /// <summary>
    /// Every key is unique, and none of them falls back to a bare number —
    /// a numbered key is one the editor cannot recognise and one that moves
    /// when its neighbours do.
    /// </summary>
    [Fact]
    public void KeysAreUniqueAcrossTheWholeSurface()
    {
        using var w = new Workspace();
        List<Entry> entries = [.. w.Open().Entries];

        List<string> duplicated = [.. entries.GroupBy(e => e.Key, StringComparer.Ordinal)
                                             .Where(g => g.Count() > 1).Select(g => g.Key)];
        Assert.Empty(duplicated);
        Assert.All(entries, e => Assert.StartsWith("fixer.", e.Key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Editing one sentence does not move any other sentence's key. Without
    /// this an editor part-way through a pass would find his next edit landing
    /// on somebody else's words.
    /// </summary>
    [Fact]
    public void EditingOneSentenceLeavesEveryOtherKeyWhereItWas()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        List<string> before = [.. w.Open().Entries.Select(e => e.Key)];

        w.Edit("fixer.stage.audio-setup.explanation", "Reads what the audio is really doing.");
        Assert.Equal(0, w.Run("apply").Code);

        Assert.Equal(before, [.. w.Open().Entries.Select(e => e.Key)]);
    }

    /// <summary>
    /// Everything the tool declines to offer is NAMED. A string that vanishes
    /// silently is a string nobody can ever fix.
    /// </summary>
    [Fact]
    public void EverythingLeftBehindIsReportedWithAReason()
    {
        using var w = new Workspace();
        Session s = w.Open();

        Assert.All(s.SkippedStrings, r =>
        {
            Assert.NotEmpty(r.Key);          // the file and line
            Assert.Contains("left in the code", r.Message, StringComparison.Ordinal);
        });

        (int code, string outp, _) = w.Run("skipped");
        Assert.Equal(0, code);
        Assert.All(s.SkippedStrings, r => Assert.Contains(r.Key, outp, StringComparison.Ordinal));
    }

    private static Entry Find(Workspace w, string startsWith) =>
        w.Open().Entries.Single(e => e.Text.StartsWith(startsWith, StringComparison.Ordinal));
}

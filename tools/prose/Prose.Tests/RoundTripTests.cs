namespace Prose.Tests;

/// <summary>
/// The round trip, against the real Fixer prose.
/// </summary>
public sealed class RoundTripTests
{
    /// <summary>
    /// THE regression test. Pull every sentence out, put every sentence back
    /// without touching one of them, and not a byte of any source file may
    /// move.
    /// </summary>
    /// <remarks>
    /// This is what makes the tool safe to run: an editor who changes three
    /// words in a file of three hundred sentences has to know that the other
    /// two hundred and ninety-seven are not being silently reformatted around
    /// his edit. Hashing every file rather than the edited one is deliberate —
    /// a tool that rewrites a file it was never asked to touch is exactly the
    /// failure this proves against.
    /// </remarks>
    [Fact]
    public void ApplyingAnUneditedFileChangesNoByteOfAnySourceFile()
    {
        using var w = new Workspace();
        Dictionary<string, string> before = w.Hashes();

        Assert.Equal(0, w.Run("extract").Code);

        (int code, string outp, string err) = w.Run("apply");

        Assert.Equal(0, code);
        Assert.Contains("No changes", outp, StringComparison.Ordinal);
        Assert.Empty(err);
        Assert.Equal(before, w.Hashes());
    }

    /// <summary>
    /// The positive control the byte-identity test needs: prove the tool would
    /// have NOTICED a change. A round trip that writes nothing is only good
    /// news if writing something is possible.
    /// </summary>
    [Fact]
    public void ChangingOneSentenceWritesThatSentenceAndLeavesEveryOtherFileAlone()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        Dictionary<string, string> before = w.Hashes();

        const string key = "fixer.stage.audio-setup.question";
        const string words = "What is your audio doing right now, really?";
        w.Edit(key, words);

        (int code, string outp, _) = w.Run("apply");
        Assert.Equal(0, code);
        Assert.Contains(key, outp, StringComparison.Ordinal);

        Dictionary<string, string> after = w.Hashes();
        List<string> moved = before.Where(kv => after[kv.Key] != kv.Value)
                                   .Select(kv => kv.Key).ToList();
        Assert.Equal(["Radios/Fixer/TransmitStageSet.cs"], moved);

        // And the change is really there, in the code, as words.
        Assert.Contains(words, File.ReadAllText(w.Path_("Radios/Fixer/TransmitStageSet.cs")),
                        StringComparison.Ordinal);

        // Re-extracting reads back exactly what was written — the trip closes.
        Assert.Equal(0, w.Run("extract", "--force").Code);
        Assert.Equal(words, w.TextOf(key));
    }

    /// <summary>
    /// A long sentence is wrapped back across lines the way the file already
    /// wraps them, and the result is still valid C#.
    /// </summary>
    [Fact]
    public void ALongSentenceIsWrappedAndTheFileStillParses()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        const string key = "fixer.stage.audio-setup.explanation";
        string words = string.Join(" ", Enumerable.Repeat(
            "This check reads what your audio is really doing rather than what it was told "
            + "to do.", 4));
        w.Edit(key, words);

        Assert.Equal(0, w.Run("apply").Code);   // apply re-parses before writing

        string source = File.ReadAllText(w.Path_("Radios/Fixer/TransmitStageSet.cs"));
        Assert.DoesNotContain(source.Split('\n').Select(l => l.TrimEnd('\r')),
                              l => l.Length > 120);

        Assert.Equal(0, w.Run("extract", "--force").Code);
        Assert.Equal(words, w.TextOf(key));
    }

    /// <summary>
    /// A quotation mark in the words is escaped on the way in, and comes back
    /// as itself. Prose about a radio has quotes in it.
    /// </summary>
    [Fact]
    public void QuotesAndBackslashesSurviveTheTrip()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        const string key = "fixer.stage.microphone-check.question";
        const string words = "Is sound from your \"microphone\" arriving here? Try C:\\Windows.";
        w.Edit(key, words);

        Assert.Equal(0, w.Run("apply").Code);
        Assert.Equal(0, w.Run("extract", "--force").Code);
        Assert.Equal(words, w.TextOf(key));
    }

    /// <summary>
    /// Words can be ADDED beside a value even where the code left no literal
    /// to put them in. "Use {device}" becoming "Use the {device} instead" is
    /// half of what a prose pass is for, and it is the case a naive
    /// replace-the-literal design cannot do at all.
    /// </summary>
    [Fact]
    public void WordsCanBeAddedWhereTheCodeHasNoLiteralForThem()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        const string key = "fixer.finding.no-input-selected.us.what-to-do";
        Assert.Equal("Use {SuggestedInputDevice}", w.TextOf(key));

        const string words = "Use the {SuggestedInputDevice} that Windows is offering";
        w.Edit(key, words);

        Assert.Equal(0, w.Run("apply").Code);
        Assert.Equal(0, w.Run("extract", "--force").Code);
        Assert.Equal(words, w.TextOf(key));
    }

    /// <summary>
    /// Two edits in two files land together, and nothing else moves.
    /// </summary>
    [Fact]
    public void EditsAcrossFilesAreAppliedTogether()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        Dictionary<string, string> before = w.Hashes();

        w.Edit("fixer.stage.spoken-transmit.title", "Speaking into the radio");
        w.Edit("fixer.finding.pc-audio-off.us.what-to-do", "Switch PC audio on");

        Assert.Equal(0, w.Run("apply").Code);

        List<string> moved = before.Where(kv => w.Hashes()[kv.Key] != kv.Value)
                                   .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Radios/Fixer/AudioSetupCheck.cs", "Radios/Fixer/TransmitStageSet.cs"],
                     moved);
    }

    /// <summary>
    /// `check` says what would happen and writes nothing — including into the
    /// source files it is reporting on.
    /// </summary>
    [Fact]
    public void CheckReportsWithoutWriting()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        Dictionary<string, string> before = w.Hashes();

        w.Edit("fixer.stage.audio-setup.title", "Audio setup, checked");

        (int code, string outp, _) = w.Run("check");
        Assert.Equal(0, code);
        Assert.Contains("would be written", outp, StringComparison.Ordinal);
        Assert.Equal(before, w.Hashes());
    }
}

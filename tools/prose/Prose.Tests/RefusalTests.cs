using System.Text;

namespace Prose.Tests;

/// <summary>
/// What the tool will not write, and how it says so.
/// </summary>
/// <remarks>
/// Every test here asserts TWO things: that the run was refused, and that
/// nothing was written. A refusal that half-applied the file would be worse
/// than no refusal at all, because the editor would be told to fix one
/// sentence while the other two hundred had already moved.
/// </remarks>
public sealed class RefusalTests
{
    [Fact]
    public void DroppingAValueTheProgramFillsInIsRefusedByName()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        Dictionary<string, string> before = w.Hashes();

        const string key = "fixer.finding.no-input-selected.us.what-to-do";
        w.Edit(key, "Use the device Windows suggests");

        (int code, _, string err) = w.Run("apply");

        Assert.NotEqual(0, code);
        Assert.Contains(key, err, StringComparison.Ordinal);
        Assert.Contains("{SuggestedInputDevice}", err, StringComparison.Ordinal);
        Assert.Contains("dropped", err, StringComparison.Ordinal);
        Assert.Equal(before, w.Hashes());
    }

    [Fact]
    public void InventingAValueTheProgramCannotFillInIsRefusedByName()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        Dictionary<string, string> before = w.Hashes();

        const string key = "fixer.stage.audio-setup.title";
        w.Edit(key, "Audio setup on {Whatever}");

        (int code, _, string err) = w.Run("apply");

        Assert.NotEqual(0, code);
        Assert.Contains(key, err, StringComparison.Ordinal);
        Assert.Contains("{Whatever}", err, StringComparison.Ordinal);
        Assert.Equal(before, w.Hashes());
    }

    /// <summary>
    /// The same rule the Lexicon enforces on its own overlay files, for the
    /// same reason: empty is the one case the missing-key fallback cannot
    /// cover, because the key IS there and the program simply says nothing.
    /// </summary>
    [Fact]
    public void EmptyingASentenceIsRefused()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        Dictionary<string, string> before = w.Hashes();

        const string key = "fixer.stage.audio-setup.title";
        w.Edit(key, "");

        (int code, _, string err) = w.Run("apply");

        Assert.NotEqual(0, code);
        Assert.Contains(key, err, StringComparison.Ordinal);
        Assert.Contains("empty", err, StringComparison.Ordinal);
        Assert.Equal(before, w.Hashes());
    }

    [Fact]
    public void ChangingMarkupInsideASentenceIsRefused()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        Dictionary<string, string> before = w.Hashes();

        // The one entry on the page with markup left inside its words.
        Entry marked = w.Open().Entries.First(e => e.HasInlineMarkup);
        w.Edit(marked.Key, Naming.StripTags(marked.Text));

        (int code, _, string err) = w.Run("apply");

        Assert.NotEqual(0, code);
        Assert.Contains(marked.Key, err, StringComparison.Ordinal);
        Assert.Contains("markup", err, StringComparison.Ordinal);
        Assert.Equal(before, w.Hashes());
    }

    /// <summary>
    /// The case that matters most while another track is rewriting the same
    /// file: the editor changed a sentence AND so did the code. Nobody but a
    /// person can settle that, so nothing is written and the new wording is
    /// put in front of him.
    /// </summary>
    [Fact]
    public void AnEditOntoWordsTheCodeHasMovedOnFromIsRefusedAndShowsTheNewWording()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        const string key = "fixer.stage.microphone-check.title";
        string file = w.Path_("Radios/Fixer/TransmitStageSet.cs");

        // Somebody else edits the code under him.
        File.WriteAllText(file,
            File.ReadAllText(file).Replace("Title = \"Microphone check\"",
                                           "Title = \"Microphone test\"", StringComparison.Ordinal),
            new UTF8Encoding(false));

        Dictionary<string, string> before = w.Hashes();
        w.Edit(key, "Microphone, checked");

        (int code, _, string err) = w.Run("apply");

        Assert.NotEqual(0, code);
        Assert.Contains(key, err, StringComparison.Ordinal);
        Assert.Contains("Microphone test", err, StringComparison.Ordinal);
        Assert.Equal(before, w.Hashes());
    }

    /// <summary>
    /// The other half of the same situation, and the reason the tool compares
    /// three things rather than two: a sentence the editor did NOT touch is
    /// left exactly as the code now has it. Somebody else's work in the same
    /// file survives a run of this tool untouched.
    /// </summary>
    [Fact]
    public void SentencesTheEditorDidNotTouchAreLeftAsTheCodeNowHasThem()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        string file = w.Path_("Radios/Fixer/TransmitStageSet.cs");
        File.WriteAllText(file,
            File.ReadAllText(file).Replace("Title = \"Microphone check\"",
                                           "Title = \"Microphone test\"", StringComparison.Ordinal),
            new UTF8Encoding(false));

        // He edits a DIFFERENT sentence in the same file.
        w.Edit("fixer.stage.audio-setup.title", "Audio setup, checked");

        Assert.Equal(0, w.Run("apply").Code);

        string after = File.ReadAllText(file);
        Assert.Contains("Microphone test", after, StringComparison.Ordinal);
        Assert.Contains("Audio setup, checked", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-extracting over unapplied edits would destroy them with nothing to
    /// show for it, so it is refused until asked twice.
    /// </summary>
    [Fact]
    public void ExtractingOverUnappliedEditsIsRefusedUntilForced()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        w.Edit("fixer.stage.audio-setup.title", "Audio setup, checked");

        (int code, _, string err) = w.Run("extract");
        Assert.NotEqual(0, code);
        Assert.Contains("not been applied", err, StringComparison.Ordinal);
        Assert.Equal("Audio setup, checked", w.TextOf("fixer.stage.audio-setup.title"));

        Assert.Equal(0, w.Run("extract", "--force").Code);
        Assert.Equal("Audio setup", w.TextOf("fixer.stage.audio-setup.title"));
    }

    /// <summary>
    /// A key that no longer exists is reported and stepped over, not treated
    /// as an error: an editing file outlives the code it came from.
    /// </summary>
    [Fact]
    public void AKeyThatIsNoLongerInTheCodeIsReportedAndSteppedOver()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);

        w.WriteMarkdown(w.Markdown
            + "\r\n## Something that used to be said\r\n\r\nWords.\r\n\r\n"
            + "> fixer.stage.gone-away.title\r\n");

        (int code, string outp, _) = w.Run("apply");

        Assert.Equal(0, code);
        Assert.Contains("fixer.stage.gone-away.title", outp, StringComparison.Ordinal);
        Assert.Contains("no longer in the code", outp, StringComparison.Ordinal);
    }
}

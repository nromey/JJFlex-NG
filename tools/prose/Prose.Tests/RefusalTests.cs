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

        // Somebody else edits the code under him. Positive control: the
        // replacement really happened, or this test would prove nothing.
        string code0 = File.ReadAllText(file);
        string moved = code0.Replace("Title = \"Microphone test\"",
                                     "Title = \"Microphone check\"", StringComparison.Ordinal);
        Assert.NotEqual(code0, moved);
        File.WriteAllText(file, moved, new UTF8Encoding(false));

        Dictionary<string, string> before = w.Hashes();
        w.Edit(key, "Microphone, checked");

        (int code, _, string err) = w.Run("apply");

        Assert.NotEqual(0, code);
        Assert.Contains(key, err, StringComparison.Ordinal);
        Assert.Contains("Microphone check", err, StringComparison.Ordinal);
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
        string code0 = File.ReadAllText(file);
        string moved = code0.Replace("Title = \"Microphone test\"",
                                     "Title = \"Microphone check\"", StringComparison.Ordinal);
        Assert.NotEqual(code0, moved);   // positive control: the code really moved
        File.WriteAllText(file, moved, new UTF8Encoding(false));

        // He edits a DIFFERENT sentence in the same file.
        w.Edit("fixer.stage.audio-setup.title", "Audio setup, checked");

        Assert.Equal(0, w.Run("apply").Code);

        string after = File.ReadAllText(file);
        Assert.Contains("Microphone check", after, StringComparison.Ordinal);
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
        Assert.Equal("Audio setup and receive", w.TextOf("fixer.stage.audio-setup.title"));
    }

    /// <summary>
    /// The step the tool itself recommends after an apply ("run `prose
    /// extract` again so this file matches the code") is not refused (#390).
    /// An applied edit differs from the fingerprint recorded when the file was
    /// made, but it is what the code now says, so it is not an unapplied edit.
    /// Until this, extract counted it as one and refused without --force.
    /// </summary>
    [Fact]
    public void ExtractingAfterAnApplyIsNotRefused()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        w.Edit("fixer.stage.audio-setup.title", "Audio setup, checked");
        (int applied, string outp, _) = w.Run("apply");
        Assert.Equal(0, applied);
        Assert.Contains("run `prose extract` again", outp, StringComparison.Ordinal);   // the advice it gives

        (int code, _, string err) = w.Run("extract");
        Assert.True(code == 0, "extract after apply was refused: " + err);
        Assert.Equal("Audio setup, checked", w.TextOf("fixer.stage.audio-setup.title"));
    }

    /// <summary>
    /// And an edit that was applied, then changed again in the file, is
    /// unapplied once more and still protected.
    /// </summary>
    [Fact]
    public void AnEditMadeAfterAnApplyIsStillProtected()
    {
        using var w = new Workspace();
        Assert.Equal(0, w.Run("extract").Code);
        w.Edit("fixer.stage.audio-setup.title", "Audio setup, checked");
        Assert.Equal(0, w.Run("apply").Code);
        w.Edit("fixer.stage.audio-setup.title", "Audio setup, checked twice");

        (int code, _, string err) = w.Run("extract");
        Assert.NotEqual(0, code);
        Assert.Contains("not been applied", err, StringComparison.Ordinal);
        Assert.Equal("Audio setup, checked twice", w.TextOf("fixer.stage.audio-setup.title"));
    }

    /// <summary>
    /// An ordinary undo after an apply (#390 repair 1, Codex's reproduction):
    /// the file goes back to the original wording A after B was applied.
    /// That is a pending edit, so apply puts A back into the code rather than
    /// saying "No changes" while the code still says B.
    /// </summary>
    [Fact]
    public void UndoingAnAppliedEditAppliesTheUndo()
    {
        using var w = new Workspace();
        const string key = "fixer.stage.audio-setup.title";
        Assert.Equal(0, w.Run("extract").Code);
        string original = w.TextOf(key);
        string file = w.Path_("Radios/Fixer/TransmitStageSet.cs");

        w.Edit(key, "Audio setup, checked");
        Assert.Equal(0, w.Run("apply").Code);
        Assert.Contains("Audio setup, checked", File.ReadAllText(file), StringComparison.Ordinal);

        w.Edit(key, original);                                   // the undo
        (int code, string outp, _) = w.Run("apply");
        Assert.Equal(0, code);
        Assert.DoesNotContain("No changes", outp, StringComparison.Ordinal);
        string code1 = File.ReadAllText(file);
        Assert.Contains("Title = \"" + original + "\"", code1, StringComparison.Ordinal);
        Assert.DoesNotContain("Audio setup, checked", code1, StringComparison.Ordinal);
    }

    /// <summary>
    /// After an apply, the code is changed independently and the file is not
    /// touched (#390 repair 2, Codex's reproduction): applying again keeps the
    /// code's new wording, and does NOT claim the code already says what the
    /// file says. It names the sentence and what the code now says.
    /// </summary>
    [Fact]
    public void AnApplyAfterTheCodeMovedOnKeepsTheCodeAndDoesNotClaimTheyMatch()
    {
        using var w = new Workspace();
        const string key = "fixer.stage.audio-setup.title";
        string file = w.Path_("Radios/Fixer/TransmitStageSet.cs");
        Assert.Equal(0, w.Run("extract").Code);
        w.Edit(key, "Audio setup, checked");
        Assert.Equal(0, w.Run("apply").Code);

        string code0 = File.ReadAllText(file);
        string moved = code0.Replace("Title = \"Audio setup, checked\"",
                                     "Title = \"Audio setup from elsewhere\"", StringComparison.Ordinal);
        Assert.NotEqual(code0, moved);   // positive control: the code really moved
        File.WriteAllText(file, moved, new UTF8Encoding(false));
        string markdown = w.Markdown;

        (int code, string outp, string err) = w.Run("apply");

        Assert.Equal(0, code);
        Assert.Empty(err);
        Assert.DoesNotContain("already says exactly what this file says", outp, StringComparison.Ordinal);
        Assert.Contains(key, outp, StringComparison.Ordinal);
        Assert.Contains("Audio setup from elsewhere", outp, StringComparison.Ordinal);
        Assert.Equal(moved, File.ReadAllText(file));             // the code's own wording is kept
        Assert.Equal(markdown, w.Markdown);                      // and the file is not touched
    }

    /// <summary>
    /// And the same undo, not yet applied, is protected: extract refuses
    /// without --force rather than replacing it with the applied wording.
    /// </summary>
    [Fact]
    public void AnUnappliedUndoAfterAnApplyIsProtectedFromExtract()
    {
        using var w = new Workspace();
        const string key = "fixer.stage.audio-setup.title";
        Assert.Equal(0, w.Run("extract").Code);
        string original = w.TextOf(key);

        w.Edit(key, "Audio setup, checked");
        Assert.Equal(0, w.Run("apply").Code);
        w.Edit(key, original);                                   // the undo

        (int code, _, string err) = w.Run("extract");
        Assert.NotEqual(0, code);
        Assert.Contains("not been applied", err, StringComparison.Ordinal);
        Assert.Equal(original, w.TextOf(key));
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

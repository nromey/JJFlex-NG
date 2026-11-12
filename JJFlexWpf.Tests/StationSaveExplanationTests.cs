using System.IO;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #776: Save Station Setup to Radio's explanation can be hidden ("don't
/// show this again"), and once hidden Save saves at once and still speaks its
/// receipt (ruled by Noel 2026-10-19). The guard that refuses another person's
/// radio (#777) still runs first, hidden or not.
/// </summary>
/// <remarks>
/// The flow is <c>NativeMenuBar.StationSaveFlow</c>, driven with delegates, so
/// no dialog is built: "the explanation" is a delegate that records it was
/// asked. Every test asserts the thread is MTA, where WPF builds no window.
/// The suppression store is a private file under the temp folder.
/// </remarks>
public sealed class StationSaveExplanationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-776-" + Guid.NewGuid().ToString("N"));

    public StationSaveExplanationTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
    }

    private sealed class Run
    {
        public int Asked;
        public int Saved;
        public bool Answer = true;
        public string? Blocker;
        public string? SaveError;

        public (string Sentence, VerbosityLevel Level) Go(bool hidden) =>
            NativeMenuBar.StationSaveFlow(
                () => Blocker,
                () => "Contest",
                () => hidden,
                name => { Asked++; return Answer; },
                () => { Saved++; return SaveError; });
    }

    private static string Receipt => Lexicon.Get("settings.station_layout.saved", ("profileName", "Contest"));

    [Fact]
    public void Hidden_saves_at_once_and_still_speaks_its_receipt()
    {
        AssertTheDeskIsSafe();
        var run = new Run();
        var said = run.Go(hidden: true);
        Assert.Equal(0, run.Asked);                 // the mutation: the explanation shows anyway
        Assert.Equal(1, run.Saved);
        Assert.Equal(Receipt, said.Sentence);
        Assert.Equal(VerbosityLevel.Critical, said.Level);
    }

    [Fact]
    public void Not_hidden_shows_the_explanation_and_saves_only_on_yes()
    {
        AssertTheDeskIsSafe();
        var yes = new Run();
        Assert.Equal(Receipt, yes.Go(hidden: false).Sentence);
        Assert.Equal(1, yes.Asked);
        Assert.Equal(1, yes.Saved);

        var no = new Run { Answer = false };
        Assert.Equal(Lexicon.Get("settings.station_layout.nothing_saved"), no.Go(hidden: false).Sentence);
        Assert.Equal(1, no.Asked);
        Assert.Equal(0, no.Saved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_guard_refuses_before_any_explanation_hidden_or_not(bool hidden)
    {
        AssertTheDeskIsSafe();
        var run = new Run { Blocker = "This radio is not yours, so it was not saved." };
        var said = run.Go(hidden);
        Assert.Equal(run.Blocker, said.Sentence);
        Assert.Equal(VerbosityLevel.Critical, said.Level);
        Assert.Equal(0, run.Asked);
        Assert.Equal(0, run.Saved);
    }

    [Fact]
    public void A_refusal_at_the_write_is_said_in_place_of_the_receipt()
    {
        AssertTheDeskIsSafe();
        var run = new Run { SaveError = "refused at the write" };
        Assert.Equal("refused at the write", run.Go(hidden: true).Sentence);
    }

    [Fact]
    public void The_hidden_explanation_is_listed_by_name_and_showing_it_again_brings_it_back()
    {
        AssertTheDeskIsSafe();
        var key = AdvisoryKeys.StationSaveExplanation;
        var store = new AdvisorySuppressionStore(Path.Combine(_dir, "suppressed-advisories.json"));
        Assert.False(store.IsSuppressed(key));
        store.Suppress(key);
        Assert.True(store.IsSuppressed(key));
        var listed = Assert.Single(store.Snapshot());
        Assert.Equal(Lexicon.Get("settings.silenced.station_save"), listed.Label);
        Assert.Equal(listed.Label, AdvisoryKeys.Describe(key.Value));
        Assert.EndsWith("-v1", key.Value);

        Assert.True(store.Unsuppress(key.Value));
        Assert.False(store.IsSuppressed(key));
    }

    [Fact]
    public void The_menu_passes_the_one_key_to_both_the_check_and_the_dialog_and_the_dialog_is_unchanged()
    {
        string root = RepoRoot();
        string menu = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "NativeMenuBar.cs"));
        Assert.Contains("() => Radios.AdvisorySuppression.IsSuppressed(Radios.AdvisoryKeys.StationSaveExplanation)", menu);
        Assert.Contains("suppressKey: Radios.AdvisoryKeys.StationSaveExplanation", menu);
        Assert.Contains("rig.StationLayoutSaveBlocker,", menu);
        Assert.Contains("rig.SaveCurrentStationLayout);", menu);

        // The shared question box still honours a hidden explanation on Yes only.
        string dialog = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "Dialogs", "ConfirmActionDialog.xaml.cs"));
        int yes = dialog.IndexOf("private void YesButton_Click", StringComparison.Ordinal);
        int no = dialog.IndexOf("private void NoButton_Click", StringComparison.Ordinal);
        Assert.True(yes > 0 && no > yes);
        Assert.Contains("Radios.AdvisorySuppression.Suppress(_suppressKey);", dialog.Substring(yes, no - yes));
        Assert.DoesNotContain("Suppress(", dialog.Substring(no, Math.Min(800, dialog.Length - no)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

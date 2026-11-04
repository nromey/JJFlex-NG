#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round two, part 4c: "start a new test or add to this one"
/// (DRAFT), asked before the countdown and never while keyed, as its sentences
/// are heard, and where Start asks it.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesAdmissionTests : IDisposable
{
    private readonly string _root = UnifiedTakeFixtures.NewRoot();
    public void Dispose() { TakeSession.Series.Reset(); UnifiedTakeFixtures.Remove(_root); }

    /// <summary>A made take in the active test, with the given profile and device.</summary>
    private TakeTest MadeTake(string profile = "Default", string device = "Yeti", string serial = "same-radio")
    {
        Directory.CreateDirectory(_root);
        string folder = UnifiedTakeFixtures.AudioTake(_root, facts: new Dictionary<string, string?>
            { ["mic-profile"] = profile, ["pc-microphone-device"] = device, ["serial"] = serial });
        TakeSession.Series.Connected(serial);
        var (test, _) = TakeSession.Series.Reserve(_root, folder);
        TakeSession.MarkReservation(_root, test.Id, folder, TakeReservation.Made);
        return TakeSession.Test(_root, test.Id)!;
    }

    [Fact]
    public void One_change_is_named_in_one_sentence_with_the_test_and_its_last_take()
    {
        TakeSession.Series.Reset();
        var test = MadeTake();
        var admission = TakeSession.Series.Admit(_root, new TakeAdmissionFacts("same-radio", "Heil", "PC", "Yeti"));
        Assert.True(admission.Ask);
        Assert.Equal("Since take 1 of " + test.ReadableId + ", your mic profile has changed.",
            TakeAdmissionText.Message(admission.Test, admission.Changed));
        Assert.Equal("Start a new test, or add this take to " + test.ReadableId + "?", TakeAdmissionText.Question(admission.Test));
        Assert.Equal("Start a new test", TakeAdmissionText.NewTestLabel);
        Assert.Equal("Add to this test", TakeAdmissionText.AddLabel);
    }

    [Fact]
    public void Several_changes_are_named_microphone_first_never_as_the_microphone()
    {
        TakeSession.Series.Reset();
        var test = MadeTake();
        var admission = TakeSession.Series.Admit(_root, new TakeAdmissionFacts("same-radio", "Heil", "PC", "Shure"));
        Assert.Equal("Since take 1 of " + test.ReadableId + ", the computer's microphone and your mic profile have changed.",
            TakeAdmissionText.Message(admission.Test, admission.Changed));
    }

    [Fact]
    public void A_renamed_test_is_asked_about_by_its_name()
    {
        TakeSession.Series.Reset();
        var test = MadeTake();
        Assert.Equal("", TakeSession.Rename(_root, test.Id, "Heil test"));
        var admission = TakeSession.Series.Admit(_root, new TakeAdmissionFacts("same-radio", "Heil", "PC", "Yeti"));
        Assert.Equal("Start a new test, or add this take to Heil test?", TakeAdmissionText.Question(admission.Test));
    }

    [Fact]
    public void Start_asks_after_every_refusal_that_covers_keying_and_before_any_method_starts_its_countdown()
    {
        string source = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"));
        int toggle = source.IndexOf("public void ToggleAudioCheck()", StringComparison.Ordinal);
        int keyed = source.IndexOf("if (ptt.IsTransmitting)", toggle, StringComparison.Ordinal);
        int ask = source.IndexOf("if (!AdmitTakeBeforeCountdown(SelectedMethod, ptt)) return;", toggle, StringComparison.Ordinal);
        int offAir = source.IndexOf("_ = StartOffAirAsync(ptt)", toggle, StringComparison.Ordinal);
        int loopback = source.IndexOf("_ = StartLoopbackCheckAsync()", toggle, StringComparison.Ordinal);
        int session = source.IndexOf("_ = StartAudioCheckSessionAsync(", toggle, StringComparison.Ordinal);
        Assert.True(keyed > toggle && ask > keyed, "the question must come after the already-transmitting refusal");
        Assert.True(ask < offAir && ask < loopback && ask < session, "the question must come before any countdown begins");
    }
}

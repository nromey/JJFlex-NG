using System;
using System.IO;
using System.Threading;
using Radios.Speech;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Push to talk inside a dialog says the transmit notice after the key, and
/// remembers the split only once the sentence was heard whole (#952, #1022
/// item 1). On the bench it was never remembered: after every one of four
/// dialog presses the trace read "the sentence after the key was not heard
/// whole; this split is still owed to the next door", so every dialog key on
/// the split said the long notice.
/// </summary>
/// <remarks>
/// <para><b>Why the commit never came.</b> The release supersedes the notice's
/// subject ("transmission ended"). On a reader with no completion channel the
/// bench's was one: no "paced #" on its line), a sentence already said stays in
/// the arbiter's ledger until the next emission tidies it, so the supersession
/// marked a heard sentence as one that will not be said, and the waiting commit
/// read that as a failure. The arbiter now brings its ledger up to date before
/// it supersedes, so only what is still owed is marked.</para>
/// <para>Runs the real <see cref="TransmitSliceNotice.Keyed"/> and its late
/// commit over a real arbiter on a fake clock, with an untracked reader whose
/// "are you speaking?" answer the test sets. No radio, no window.</para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class DialogNoticeCommitTests : IDisposable
{
    private const string Sentence = "Transmitting on slice B, 21.275.000";

    private readonly FakeSpeechClock _clock = new();
    private readonly SpeechArbiter _arbiter;
    private readonly CountdownSpeech _speech;
    private readonly FlexBase _rig;
    private bool? _readerSpeaking = true;

    public DialogNoticeCommitTests()
    {
        _arbiter = new SpeechArbiter(_clock, () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) => SpeechHandoff.Untracked,
            () => { }, (m, l, i, o) => { }, new SpeechRateModel(), () => _readerSpeaking, (t, r) => false);
        _speech = new CountdownSpeech(_arbiter);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        TransmitSliceNotice.ForgetFor(_rig);
    }

    public void Dispose()
    {
        TransmitSliceNotice.ForgetFor(_rig);
        _rig.Dispose();
    }

    /// <summary>Key, as the dialog door does: say after the key, then commit when heard.</summary>
    private ICountdownWords KeyWithNotice()
    {
        ICountdownWords words = _speech.SayWhole(Sentence, SpeechSubject.TransmitSliceNotice, ahead: true);
        var notice = new TransmitSliceNotice.Notice(_rig, "push to talk in a dialog", default,
            mandatory: false, spoke: true, words, Sentence);
        TransmitSliceNotice.Keyed(notice);
        return words;
    }

    /// <summary>The release's own supersession, as <c>PttSafetyController.GoIdle</c> makes it.</summary>
    private void Release() => _arbiter.Supersede(SpeechSubject.TransmitSliceNotice, "transmission ended", "GoIdle");

    private bool Remembered(int withinMs = 3000)
        => SpinWait.SpinUntil(() => TransmitSliceNotice.LastSpoken(_rig) != null, withinMs);

    [Fact]
    public void A_notice_the_reader_finished_before_the_release_is_remembered()
    {
        var words = KeyWithNotice();
        // The hold: the reader has said it all, past the estimate's end but
        // inside its margin, which is where the bench's releases fell.
        _clock.Advance(SpeechArbiter.EstimateSpokenMs(Sentence) + 300);
        _readerSpeaking = false;
        Release();
        Assert.False(words.Failed, "the release marked a sentence already said as never said");
        _clock.Advance(CountdownSpeech.UntrackedMarginMs(SpeechArbiter.EstimateSpokenMs(Sentence)) + 100);
        Assert.True(Remembered(), "the split was never remembered, so the next dialog key says it again");
    }

    [Fact]
    public void A_long_hold_whose_estimate_has_run_out_is_remembered_even_when_the_reader_cannot_say()
    {
        _readerSpeaking = null;   // a reader that cannot answer: the estimate stands
        var words = KeyWithNotice();
        _clock.Advance(SpeechArbiter.EstimateSpokenMs(Sentence) + 50);
        Release();
        Assert.False(words.Failed);
        _clock.Advance(CountdownSpeech.UntrackedMarginMs(SpeechArbiter.EstimateSpokenMs(Sentence)) + 100);
        Assert.True(Remembered());
    }

    [Fact]
    public void A_release_while_the_reader_is_still_saying_it_leaves_the_split_owed()
    {
        // Positive control: the release's own interrupt will cut a sentence
        // still being said, so it was not heard whole and the next door owes it.
        var words = KeyWithNotice();
        _clock.Advance(800);
        _readerSpeaking = true;
        Release();
        Assert.True(words.Failed);
        _clock.Advance(10_000);
        Assert.False(Remembered(400), "a notice cut by the release was remembered as heard");
    }

    [Fact]
    public void Supersede_still_retires_what_is_owed()
    {
        // The arbiter half alone: a queued sentence not yet said is marked.
        ICountdownWords words = _speech.SayWhole(Sentence, SpeechSubject.TransmitSliceNotice);
        _clock.Advance(100);
        Release();
        Assert.True(words.Failed);
    }

    [Fact]
    public void The_arbiter_brings_its_ledger_up_to_date_before_it_supersedes()
    {
        string src = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "Speech", "SpeechArbiter.cs"));
        int at = src.IndexOf("public void Supersede(string subject, string by, string? origin)", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = src.Substring(at, src.IndexOf("\n        }", at, StringComparison.Ordinal) - at);
        int prune = body.IndexOf("PruneLedgerLocked(now);", StringComparison.Ordinal);
        int mark = body.IndexOf("MarkSupersededLocked(subject, by, origin, now);", StringComparison.Ordinal);
        Assert.True(prune > 0 && mark > prune, "Supersede marks before it prunes");
    }
}

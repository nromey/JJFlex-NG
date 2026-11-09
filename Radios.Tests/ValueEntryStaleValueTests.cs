using System;
using System.Collections.Generic;
using System.IO;
using Radios.Speech;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// A value field does not speak its old value between the digits of a new
/// one (#1023 item 5). The harness typed 150, Enter, then 100 into the Audio
/// Workshop's bench level and NVDA said "entering 10", "Bench level 150", "0".
/// </summary>
/// <remarks>
/// <para><b>Established from the harness trace</b>, not inferred: the commit's
/// "Bench level 150" was still owed when the next entry's "Enter Bench level
/// value" interrupted, and the arbiter held it as a salvage and released it
/// 598 ms later behind the new digits ("holding 1 salvage(s) for 600 ms behind
/// 'Enter Bench level value' ...: 'Bench level 150'", then "released ...; 2 went
/// first: '1', '0'"). A new entry now retires the field's value subject before
/// its interrupt, so there is nothing owed to replay.</para>
/// <para>The arbiter half runs a real arbiter on a fake clock with an
/// untracked reader, as the bench's was. The field is pinned from source:
/// constructing it needs a window thread.</para>
/// </remarks>
public sealed class ValueEntryStaleValueTests
{
    private const string Label = "Bench level";

    private sealed class Rig
    {
        public readonly FakeSpeechClock Clock = new();
        public readonly SpeechArbiter Arbiter;
        public readonly List<(string Text, bool Salvaged)> Said = new();

        public Rig()
        {
            Arbiter = new SpeechArbiter(Clock, () => VerbosityLevel.Chatty,
                (message, interrupt, intent, level, origin, salvaged) =>
                {
                    Said.Add((message, salvaged));
                    return SpeechHandoff.Untracked;
                },
                () => { }, (m, l, i, o) => { }, new SpeechRateModel(), () => true, (t, r) => false);
        }

        /// <summary>Type "100" over a just-committed 150, as the field does.</summary>
        public void TypeAfterCommit(bool supersedeFirst)
        {
            // The commit: queued, on the field's value subject.
            Arbiter.Emit(Label + " 150", false, SpeechIntent.Queue, VerbosityLevel.Terse, "commit",
                subject: SpeechSubject.ValueField(Label));
            Clock.Advance(300);    // inside the old value's estimate, as the bench's was (its estimates ran about 2 s)
            if (supersedeFirst)
                Arbiter.Supersede(SpeechSubject.ValueField(Label), "a new value being typed", "BeginNumberEntry");
            Arbiter.Emit("Enter " + Label + " value", true, SpeechIntent.Interrupt, VerbosityLevel.Chatty, "begin");
            foreach (string digit in new[] { "1", "0", "0" })
            {
                Arbiter.Emit(digit, false, SpeechIntent.Queue, VerbosityLevel.Critical, "echo",
                    subject: SpeechSubject.ValueEntry(Label), additive: true);
                Clock.Advance(250);
            }
            Clock.Advance(SpeechArbiter.SalvageSettleMs + 500);
        }
    }

    [Fact]
    public void Before_the_fix_the_old_value_came_back_between_the_digits()
    {
        // Positive control: the replay the harness heard, reproduced.
        var r = new Rig();
        r.TypeAfterCommit(supersedeFirst: false);
        Assert.Contains(r.Said, s => s.Text == Label + " 150" && s.Salvaged);
    }

    [Fact]
    public void A_new_entry_retires_the_old_value_so_it_is_not_replayed()
    {
        var r = new Rig();
        r.TypeAfterCommit(supersedeFirst: true);
        Assert.DoesNotContain(r.Said, s => s.Text == Label + " 150" && s.Salvaged);
        // The digits themselves are untouched.
        Assert.Equal(new[] { "1", "0", "0" }, r.Said.FindAll(s => s.Text is "1" or "0").ConvertAll(s => s.Text));
    }

    [Fact]
    public void The_field_retires_its_value_before_it_interrupts_and_keeps_the_entry_on_screen()
    {
        string src = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "JJFlexWpf", "Controls", "ValueFieldControl.xaml.cs"))
            .Replace("\r\n", "\n");
        int begin = src.IndexOf("private void BeginNumberEntry(Key firstKey)", StringComparison.Ordinal);
        Assert.True(begin > 0);
        string body = src.Substring(begin, src.IndexOf("\n    }", begin, StringComparison.Ordinal) - begin);
        int retire = body.IndexOf("ScreenReaderOutput.Supersede(ValueSubject,", StringComparison.Ordinal);
        int speak = body.IndexOf("ScreenReaderOutput.Speak(Lexicon.Get(\"audio.field.enter_value\"", StringComparison.Ordinal);
        Assert.True(retire > 0 && speak > retire, "the new entry's interrupt is spoken before the old value is retired");

        // The typed VALUE is not lost mid-entry, and nothing puts the old one
        // back in view: a value arriving during entry and a label change both
        // leave the entry's display alone.
        Assert.Contains("if (_numberEntryMode) ReaderAway.Stale(this);\n            else if (IsKeyboardFocusWithin", src, StringComparison.Ordinal);
        Assert.Contains("if (_numberEntryMode) UpdateNumberEntryDisplay();\n            else UpdateDisplay();", src, StringComparison.Ordinal);
    }
}

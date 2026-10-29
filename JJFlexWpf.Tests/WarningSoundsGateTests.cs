using System.Threading;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #810: critical alarms play even with alert sounds off, under their own
/// switch. Ruled by Noel 2026-10-22: the whole Warnings category (the warning
/// alarm and the problem-recorded tone) and the hard-kill tone leave the
/// master switch and answer only to the Warning sounds switch; the
/// long-transmission warnings stay behind the master switch, because their
/// beeps are shared with everyday out-of-range nudges.
/// </summary>
/// <remarks>
/// Windowless: the gate is static state and the catalog is reflection over
/// <see cref="EarconPlayer"/>; nothing here builds a WPF element. Each test
/// restores the switches it touched.
/// </remarks>
public sealed class WarningSoundsGateTests
{
    private static void AssertWindowless() =>
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());

    private static void WithSwitches(bool master, bool warnings, System.Action body)
    {
        bool masterBefore = EarconPlayer.EarconsEnabled;
        bool warningsBefore = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.Warnings);
        bool transmitBefore = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.Transmit);
        try
        {
            EarconPlayer.EarconsEnabled = master;
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.Warnings, warnings);
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.Transmit, true);
            body();
        }
        finally
        {
            EarconPlayer.EarconsEnabled = masterBefore;
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.Warnings, warningsBefore);
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.Transmit, transmitBefore);
        }
    }

    [Fact]
    public void With_alert_sounds_off_a_warning_plays_and_a_long_transmission_beep_does_not()
    {
        AssertWindowless();
        WithSwitches(master: false, warnings: true, () =>
        {
            Assert.True(EarconPlayer.IsOn(EarconPlayer.EarconCategory.Warnings),
                "with alert sounds off and Warning sounds on, a warning must still play");
            Assert.False(EarconPlayer.IsOn(EarconPlayer.EarconCategory.Transmit),
                "the long-transmission beeps stay behind the master switch");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_Warning_sounds_off_a_warning_does_not_play(bool master)
    {
        AssertWindowless();
        WithSwitches(master, warnings: false, () =>
            Assert.False(EarconPlayer.IsOn(EarconPlayer.EarconCategory.Warnings)));
    }

    [Fact]
    public void With_both_on_everything_plays_as_before()
    {
        AssertWindowless();
        WithSwitches(master: true, warnings: true, () =>
        {
            Assert.True(EarconPlayer.IsOn(EarconPlayer.EarconCategory.Warnings));
            Assert.True(EarconPlayer.IsOn(EarconPlayer.EarconCategory.Transmit));
        });
    }

    /// <summary>
    /// Which earcons the rule covers is decided by their category, so pin it:
    /// the three critical ones are Warnings, the three long-transmission
    /// warnings are Transmit.
    /// </summary>
    [Theory]
    [InlineData(nameof(EarconPlayer.WarningAlarmTone), EarconPlayer.EarconCategory.Warnings)]
    [InlineData(nameof(EarconPlayer.ProblemRecordedTone), EarconPlayer.EarconCategory.Warnings)]
    [InlineData(nameof(EarconPlayer.HardKillTone), EarconPlayer.EarconCategory.Warnings)]
    [InlineData(nameof(EarconPlayer.Warning1Beep), EarconPlayer.EarconCategory.Transmit)]
    [InlineData(nameof(EarconPlayer.Warning2Beep), EarconPlayer.EarconCategory.Transmit)]
    [InlineData(nameof(EarconPlayer.OhCrapBeep), EarconPlayer.EarconCategory.Transmit)]
    public void Each_alarm_sits_in_the_category_the_ruling_gives_it(string earcon, EarconPlayer.EarconCategory category)
    {
        AssertWindowless();
        var entry = EarconCatalog.Find(earcon);
        Assert.NotNull(entry);
        Assert.Equal(category, entry!.Category);
    }

    // ── What actually reaches the mixer (the #810 repair) ─────────────────
    //
    // The gate tests above all passed while every warning was still silent
    // with alert sounds off: the voiced renderers and the mixer admission
    // helpers each read the master switch again after the gate had let the
    // sound through. These watch the output seam instead — a mixer standing
    // in for the alert channel's, which opens no device (and this project's
    // QuietRun has render off besides) — and count what is submitted to it.

    private static int Submitted(bool master, bool warnings, System.Action play)
    {
        var mixer = new NAudio.Wave.SampleProviders.MixingSampleProvider(
            NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(EarconPlayer.MixerSampleRate, 2));
        int count = 0;
        WithSwitches(master, warnings, () =>
        {
            EarconPlayer.TestAlertMixer = mixer;
            try
            {
                play();
                count = System.Linq.Enumerable.Count(mixer.MixerInputs);
            }
            finally { EarconPlayer.TestAlertMixer = null; }
        });
        return count;
    }

    public static System.Collections.Generic.IEnumerable<object[]> TheThreeWarnings()
    {
        yield return new object[] { nameof(EarconPlayer.WarningAlarmTone) };
        yield return new object[] { nameof(EarconPlayer.ProblemRecordedTone) };
        yield return new object[] { nameof(EarconPlayer.HardKillTone) };
    }

    private static System.Action Play(string earcon) => earcon switch
    {
        nameof(EarconPlayer.WarningAlarmTone) => EarconPlayer.WarningAlarmTone,
        nameof(EarconPlayer.ProblemRecordedTone) => EarconPlayer.ProblemRecordedTone,
        nameof(EarconPlayer.HardKillTone) => EarconPlayer.HardKillTone,
        _ => throw new System.ArgumentException(earcon),
    };

    [Theory]
    [MemberData(nameof(TheThreeWarnings))]
    public void With_alert_sounds_off_each_warning_is_actually_submitted_for_playback(string earcon)
    {
        AssertWindowless();
        Assert.True(Submitted(master: false, warnings: true, Play(earcon)) > 0,
            earcon + " passed the gate with alert sounds off and then never reached the mixer");
    }

    [Theory]
    [MemberData(nameof(TheThreeWarnings))]
    public void With_Warning_sounds_off_nothing_is_submitted_whatever_the_master_says(string earcon)
    {
        AssertWindowless();
        Assert.Equal(0, Submitted(master: true, warnings: false, Play(earcon)));
        Assert.Equal(0, Submitted(master: false, warnings: false, Play(earcon)));
    }

    /// <summary>
    /// Round two (the Codex re-check, B2): the speech-unavailable alarm skips
    /// the gate by design, and the mixer still applied the master mute to it.
    /// It fires when the program cannot speak, so it must sound whatever the
    /// switches say.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void The_speech_unavailable_alarm_is_submitted_whatever_the_switches_say(bool master, bool warnings)
    {
        AssertWindowless();
        Assert.True(Submitted(master, warnings, EarconPlayer.SpeechUnavailableAlarm) > 0,
            "the speech-unavailable alarm was not submitted with master=" + master + ", warnings=" + warnings);
    }

    /// <summary>
    /// Round two (F2): the panned route. A bench pan sends the sound through
    /// AddToMixerPanned rather than AddToMixer, and that helper read the
    /// master switch too.
    /// </summary>
    [Fact]
    public void A_panned_warning_is_submitted_with_alert_sounds_off()
    {
        AssertWindowless();
        Assert.True(Submitted(master: false, warnings: true,
                () => EarconPlayer.PlayWithBenchSettings(EarconPlayer.WarningAlarmTone, 1f, 0.6f)) > 0,
            "a panned warning passed the gate and never reached the mixer");
    }

    [Fact]
    public void A_long_transmission_beep_still_meets_the_master_mute_and_the_seam_sees_it_when_on()
    {
        AssertWindowless();
        // Positive control first: the seam does see a Transmit sound.
        Assert.True(Submitted(master: true, warnings: true, EarconPlayer.Warning1Beep) > 0,
            "the test mixer saw nothing even with every switch on, so a zero below would prove nothing");
        Assert.Equal(0, Submitted(master: false, warnings: true, EarconPlayer.Warning1Beep));
    }

    [Fact]
    public void The_admission_ends_with_the_warning_and_does_not_leak_into_the_next_sound()
    {
        AssertWindowless();
        int raw = Submitted(master: false, warnings: true, () =>
        {
            EarconPlayer.WarningAlarmTone();
            // A raw voiced note on the same thread, outside any warning: it
            // must still meet the master mute.
            EarconPlayer.PlayVoiced(EarconVoices.Plain, 440, 50, 0.5f);
        });
        Assert.Equal(1, raw);   // the warning's own submission, and nothing else
    }

    /// <summary>
    /// F1: Settings' per-sound preview and Play button refuse by the same
    /// master rule the sounds follow, so a warning auditions with alert sounds
    /// off and an ordinary sound still does not.
    /// </summary>
    [Fact]
    public void The_master_holds_every_family_but_warnings()
    {
        AssertWindowless();
        WithSwitches(master: false, warnings: true, () =>
        {
            Assert.False(EarconPlayer.MasterHolds(EarconPlayer.EarconCategory.Warnings));
            Assert.True(EarconPlayer.MasterHolds(EarconPlayer.EarconCategory.Transmit));
            Assert.True(EarconPlayer.MasterHolds(null));
        });
        WithSwitches(master: true, warnings: true, () =>
            Assert.False(EarconPlayer.MasterHolds(EarconPlayer.EarconCategory.Transmit)));

        string settings = System.IO.File.ReadAllText(System.IO.Path.Combine(
            Infrastructure.RepoPaths.RequireRoot(), "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
        foreach (string handler in new[] { "EarconTrimSoundCombo_SelectionChanged", "EarconTrimPlayButton_Click" })
        {
            int at = settings.IndexOf("private void " + handler, System.StringComparison.Ordinal);
            Assert.True(at >= 0, handler + " not found");
            int end = settings.IndexOf("\n        }", at, System.StringComparison.Ordinal);
            string body = settings.Substring(at, end - at);
            Assert.Contains("EarconPlayer.MasterHolds(entry.Category)", body);
            Assert.DoesNotContain("EarconPlayer.EarconsEnabled", body);
        }
    }

    /// <summary>
    /// The class: every earcon that gates on Warnings opens the admission
    /// right after its gate, so the next warning added cannot pass the gate
    /// and stay silent the way these three did.
    /// </summary>
    [Fact]
    public void Every_warning_earcon_opens_the_admission_after_its_gate()
    {
        AssertWindowless();
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            Infrastructure.RepoPaths.RequireRoot(), "JJFlexWpf", "EarconPlayer.cs"));
        var gate = new System.Text.RegularExpressions.Regex(
            @"if \(!Gate\(EarconCategory\.Warnings\)\) return;(\s*)(\S[^\r\n]*)");
        int seen = 0;
        foreach (System.Text.RegularExpressions.Match m in gate.Matches(source))
        {
            seen++;
            Assert.Equal("using var admitted = AdmitWarnings();", m.Groups[2].Value.Trim());
        }
        Assert.True(seen >= 3, "only " + seen + " Warnings gates were found");

        // And every earcon LISTED under Warnings, gated or not, opens it: the
        // speech-unavailable alarm has no gate and was missed by the check
        // above until round two.
        var listed = new System.Text.RegularExpressions.Regex(
            @"\[Earcon\(""[^""]*"",\s*EarconCategory\.Warnings(?:(?!\[Earcon\()[\s\S])*?public static void (\w+)\(\)\s*\{");
        int warnings = 0;
        foreach (System.Text.RegularExpressions.Match m in listed.Matches(source))
        {
            warnings++;
            int bodyStart = m.Index + m.Length;
            int next = source.IndexOf("public static", bodyStart, System.StringComparison.Ordinal);
            string body = source.Substring(bodyStart, (next < 0 ? source.Length : next) - bodyStart);
            Assert.True(body.Contains("using var admitted = AdmitWarnings();", System.StringComparison.Ordinal),
                m.Groups[1].Value + " is listed under Warnings but never opens the admission");
        }
        Assert.True(warnings >= 4, "only " + warnings + " Warnings earcons were found");
    }

    /// <summary>
    /// The attribute decides which switch the Settings list shows a sound
    /// under; the Gate call decides which switch actually silences it. The
    /// hard kill moved category under #810 and both had to move together, so
    /// hold every earcon to it: a gated earcon's first Gate call names the
    /// category its attribute declares.
    /// </summary>
    [Fact]
    public void Every_earcon_gates_on_the_category_its_attribute_declares()
    {
        AssertWindowless();
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            Infrastructure.RepoPaths.RequireRoot(), "JJFlexWpf", "EarconPlayer.cs"));
        var attribute = new System.Text.RegularExpressions.Regex(
            @"\[Earcon\(""[^""]*"",\s*EarconCategory\.(\w+)(?:(?!\[Earcon\()[\s\S])*?public static void (\w+)\(\)\s*\{");
        int checkedCount = 0;
        var wrong = new System.Collections.Generic.List<string>();
        foreach (System.Text.RegularExpressions.Match m in attribute.Matches(source))
        {
            string declared = m.Groups[1].Value, method = m.Groups[2].Value;
            int bodyStart = m.Index + m.Length;
            int next = source.IndexOf("public static", bodyStart, System.StringComparison.Ordinal);
            string body = source.Substring(bodyStart, (next < 0 ? source.Length : next) - bodyStart);
            var gate = System.Text.RegularExpressions.Regex.Match(body, @"\bGate(?:Detailed)?\(EarconCategory\.(\w+)");
            if (!gate.Success) continue;   // the ungated ones, such as SpeechUnavailableAlarm
            checkedCount++;
            if (gate.Groups[1].Value != declared)
                wrong.Add(method + " is listed under " + declared + " but gates on " + gate.Groups[1].Value);
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        // Positive control: the scan reached the gated earcons, the hard kill among them.
        Assert.True(checkedCount > 30, "only " + checkedCount + " gated earcons were read");
        Assert.Matches(@"Earcon\(""Hard kill"", EarconCategory\.Warnings", source);
    }
}

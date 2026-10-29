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

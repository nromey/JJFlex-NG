using System;
using System.IO;
using System.Windows.Input;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// With no radio, a Home key answers in one sentence wherever it lands (#372,
/// #356), and a Home field never swallows a letter in silence (#338).
/// </summary>
/// <remarks>
/// <b>Windowless.</b> Every test calls a static decision on
/// <see cref="MainWindow"/> or <see cref="FreqOutHandlers"/>, or reads source;
/// no window, no key event, no dispatcher. Each asserts it runs on an MTA
/// thread, where WPF refuses to build any element.
/// </remarks>
public sealed class NoRadioHomeKeysTests
{
    public NoRadioHomeKeysTests() => Lexicon.Load(Lexicon.Partitions);

    private static readonly Key[] TheSix = { Key.M, Key.V, Key.R, Key.X, Key.Q, Key.OemPlus };

    // ── #372 and #356: the universal Home keys with no radio ──

    [Fact]
    public void The_six_are_answered_on_Home_and_on_the_rescue_page_with_no_radio()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        foreach (Key k in TheSix)
            Assert.True(MainWindow.ClaimsNoRadioHomeKey(k, ModifierKeys.None, haveRig: false, focusOnHomeOrRescue: true), k + " went unanswered");
    }

    [Fact]
    public void Nothing_is_claimed_with_a_radio_elsewhere_with_a_modifier_or_for_another_key()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        foreach (Key k in TheSix)
        {
            Assert.False(MainWindow.ClaimsNoRadioHomeKey(k, ModifierKeys.None, haveRig: true, focusOnHomeOrRescue: true));
            Assert.False(MainWindow.ClaimsNoRadioHomeKey(k, ModifierKeys.None, haveRig: false, focusOnHomeOrRescue: false));
            Assert.False(MainWindow.ClaimsNoRadioHomeKey(k, ModifierKeys.Shift, haveRig: false, focusOnHomeOrRescue: true));
            Assert.False(MainWindow.ClaimsNoRadioHomeKey(k, ModifierKeys.Control, haveRig: false, focusOnHomeOrRescue: true));
        }
        // A key that does nothing anywhere is not claimed.
        Assert.False(MainWindow.ClaimsNoRadioHomeKey(Key.B, ModifierKeys.None, haveRig: false, focusOnHomeOrRescue: true));
    }

    private static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void The_guard_says_the_field_handlers_sentence()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string main = Source("JJFlexWpf/MainWindow.xaml.cs");
        int guard = main.IndexOf("if (ClaimsNoRadioHomeKey(rawKey, Keyboard.Modifiers", StringComparison.Ordinal);
        Assert.True(guard > 0, "step 1c no longer asks ClaimsNoRadioHomeKey");
        string body = main.Substring(guard, 500);
        Assert.Contains("Radios.Lexicon.Get(\"connect.command_needs_radio\")", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SpeakNoRadioConnected", body, StringComparison.Ordinal);

        string handlers = Source("JJFlexWpf/FreqOutHandlers.cs");
        Assert.Contains("Lexicon.Get(\"connect.command_needs_radio\")", handlers, StringComparison.Ordinal);

        // The stale claim the old comment made is gone.
        Assert.DoesNotContain("FrequencyDisplay's _fieldDict is empty so", main, StringComparison.Ordinal);
    }

    // ── #338: a Home field never swallows a letter in silence ──

    private static FreqOutHandlers.DeadHomeKeyAnswer Answer(Key key, ModifierKeys mods, bool haveRig, bool bound) =>
        FreqOutHandlers.DeadHomeKeyAnswerFor(key, mods, haveRig, () => bound);

    [Fact]
    public void With_a_radio_an_unbound_letter_names_itself()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.Unbound, Answer(Key.K, ModifierKeys.None, haveRig: true, bound: false));
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.Unbound, Answer(Key.K, ModifierKeys.Shift, haveRig: true, bound: false));
        // A registry chord is the dispatcher's, and a digit is not a letter to name.
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.None, Answer(Key.K, ModifierKeys.None, haveRig: true, bound: true));
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.None, Answer(Key.D5, ModifierKeys.None, haveRig: true, bound: false));
    }

    [Fact]
    public void With_no_radio_a_field_key_says_no_radio_connected()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        foreach (Key k in new[] { Key.K, Key.D5, Key.Space, Key.Up, Key.Down, Key.OemPeriod })
            Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.NeedsRadio, Answer(k, ModifierKeys.None, haveRig: false, bound: false));
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.NeedsRadio, Answer(Key.K, ModifierKeys.Shift, haveRig: false, bound: false));
    }

    [Fact]
    public void Chords_that_belong_to_someone_else_are_left_to_them()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // Ctrl with no radio is the registry's to explain; Alt and the
        // Windows key are the menu bar's and the shell's, and are never asked
        // about the registry at all.
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.None, Answer(Key.K, ModifierKeys.Control, haveRig: false, bound: false));
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.None,
            FreqOutHandlers.DeadHomeKeyAnswerFor(Key.K, ModifierKeys.Alt, haveRig: true, () => throw new InvalidOperationException("asked")));
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.None,
            FreqOutHandlers.DeadHomeKeyAnswerFor(Key.K, ModifierKeys.Windows, haveRig: false, () => throw new InvalidOperationException("asked")));
        // A registry chord with no radio is the dispatcher's too.
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.None, Answer(Key.K, ModifierKeys.None, haveRig: false, bound: true));
        // A key no field uses is not claimed.
        Assert.Equal(FreqOutHandlers.DeadHomeKeyAnswer.None, Answer(Key.F5, ModifierKeys.None, haveRig: false, bound: false));
    }

    [Fact]
    public void The_two_sentences_exist_and_differ()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal("No radio connected", Lexicon.Get("connect.command_needs_radio"));
        Assert.Contains("does nothing on the", Lexicon.Get("settings.home.key_unbound", ("key", "K"), ("field", "Frequency")));
    }
}

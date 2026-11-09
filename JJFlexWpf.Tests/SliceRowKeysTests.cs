using System.Windows.Input;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The pure decisions behind the keys every field on Home's slice row answers
/// the same way (#703, #877): which arrow chords the slice row gives no
/// meaning to, and how such a chord is named back.
/// </summary>
/// <remarks>
/// <b>Windowless.</b> Every test calls a static decision on
/// <see cref="FreqOutHandlers"/> and asserts it runs on an MTA thread, where
/// WPF refuses to build any element. The routing itself is held from source in
/// <c>Radios.Tests/SliceRowTuningKeysTests</c>.
/// </remarks>
public sealed class SliceRowKeysTests
{
    [Fact]
    public void Control_alone_Shift_alone_and_the_bare_arrow_are_not_the_unbound_class()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        foreach (var key in new[] { Key.Up, Key.Down })
        {
            Assert.False(FreqOutHandlers.IsUnboundArrowChord(key, ModifierKeys.None));
            Assert.False(FreqOutHandlers.IsUnboundArrowChord(key, ModifierKeys.Control));
            Assert.False(FreqOutHandlers.IsUnboundArrowChord(key, ModifierKeys.Shift));
        }
    }

    /// <summary>
    /// #877's class: every other modifier set on an arrow is NOT the bare
    /// arrow. Before this, Control+Shift+Up toggled VOX on the VOX field and
    /// moved transmit on the Transmit slice field.
    /// </summary>
    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)]
    public void Every_other_modifier_set_on_an_arrow_is_unbound(ModifierKeys mods)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(FreqOutHandlers.IsUnboundArrowChord(Key.Up, mods));
        Assert.True(FreqOutHandlers.IsUnboundArrowChord(Key.Down, mods));
    }

    [Fact]
    public void The_Windows_key_and_keys_that_are_not_arrows_are_never_claimed()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.False(FreqOutHandlers.IsUnboundArrowChord(Key.Up, ModifierKeys.Windows));
        Assert.False(FreqOutHandlers.IsUnboundArrowChord(Key.Down, ModifierKeys.Windows | ModifierKeys.Shift));
        Assert.False(FreqOutHandlers.IsUnboundArrowChord(Key.Left, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(FreqOutHandlers.IsUnboundArrowChord(Key.PageUp, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(FreqOutHandlers.IsUnboundArrowChord(Key.A, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.Up, "Control Shift Up arrow")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.Down, "Control Alt Down arrow")]
    [InlineData(ModifierKeys.Alt, Key.Up, "Alt Up arrow")]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Shift, Key.Down, "Alt Shift Down arrow")]
    public void An_unbound_arrow_chord_is_named_in_words(ModifierKeys mods, Key key, string spoken)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(spoken, FreqOutHandlers.SpokenArrowChord(mods, key));
    }
}

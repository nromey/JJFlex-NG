using System.Windows.Input;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The Home field handlers dispatch on ONE character, and a character means
/// the bare key. #546 is why that is asserted rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this pins.</b> <c>FreqOutHandlers.KeyToChar</c> blanked only
/// Alt, so on the Slice field <c>A</c>, <c>Shift+A</c> and <c>Ctrl+A</c>
/// were the same value down the same branch — Noel, at the keyboard: "a,
/// shift, and ctrl a all do slice changes" — and <c>Ctrl+1</c> on the
/// frequency field typed a digit. Nothing failed: it compiled, every key
/// was bound, and each chord did something plausible. The author had seen
/// the hazard and guarded <c>Shift+M</c> and <c>Shift+Comma</c> one
/// collision at a time; the general letter and digit paths never got a
/// guard, so every future collision was silent too.
/// </para>
/// <para>
/// <b>What is asserted:</b> the rule itself, over the whole key enum and
/// every modifier state, not the handful of chords somebody happened to
/// think of. <see cref="FreqOutHandlers.CharFor"/> is the pure form of the
/// helper every handler calls, so these need no dispatcher, no window and
/// no desktop — exactly like <see cref="DeskGuardDecisionTests"/>, and for
/// the same reason.
/// </para>
/// <para>
/// <b>What is NOT proved:</b> that pressing the key does the right thing on
/// a real build. A mapping can be perfect and a handler can still consume
/// the wrong chord somewhere else. Press them.
/// </para>
/// </remarks>
public sealed class HomeFieldChordTests
{
    private static readonly Key[] EveryKey = Enum.GetValues<Key>().Distinct().ToArray();

    // All sixteen combinations of Alt, Control, Shift and Windows.
    private static readonly ModifierKeys[] EveryModifierState =
        Enumerable.Range(0, 16).Select(i => (ModifierKeys)i).ToArray();

    [Fact]
    public void ABareLetterIsItsLetter()
    {
        for (var key = Key.A; key <= Key.Z; key++)
        {
            char expected = (char)('A' + (key - Key.A));
            Assert.Equal(expected, FreqOutHandlers.CharFor(key, ModifierKeys.None));
        }
    }

    [Fact]
    public void ABareDigitIsItsDigitOnBothRows()
    {
        for (int i = 0; i <= 9; i++)
        {
            char expected = (char)('0' + i);
            Assert.Equal(expected, FreqOutHandlers.CharFor(Key.D0 + i, ModifierKeys.None));
            Assert.Equal(expected, FreqOutHandlers.CharFor(Key.NumPad0 + i, ModifierKeys.None));
        }
    }

    [Fact]
    public void TheFieldPunctuationIsBareOnly()
    {
        Assert.Equal('+', FreqOutHandlers.CharFor(Key.Add, ModifierKeys.None));
        Assert.Equal('-', FreqOutHandlers.CharFor(Key.OemMinus, ModifierKeys.None));
        Assert.Equal('-', FreqOutHandlers.CharFor(Key.Subtract, ModifierKeys.None));
        Assert.Equal('.', FreqOutHandlers.CharFor(Key.OemPeriod, ModifierKeys.None));
        Assert.Equal('.', FreqOutHandlers.CharFor(Key.Decimal, ModifierKeys.None));
        Assert.Equal(',', FreqOutHandlers.CharFor(Key.OemComma, ModifierKeys.None));
        Assert.Equal(' ', FreqOutHandlers.CharFor(Key.Space, ModifierKeys.None));

        // Shift+comma is '<' on a US keyboard, not ','. The field maps bind
        // that chord by its KEY, never by the character.
        Assert.Equal('\0', FreqOutHandlers.CharFor(Key.OemComma, ModifierKeys.Shift));
        Assert.Equal('\0', FreqOutHandlers.CharFor(Key.Space, ModifierKeys.Shift));
        Assert.Equal('\0', FreqOutHandlers.CharFor(Key.OemMinus, ModifierKeys.Shift));
    }

    /// <summary>
    /// The operator's exact report, as three distinct values.
    /// </summary>
    [Fact]
    public void AShiftAAndCtrlAAreThreeDifferentThings()
    {
        Assert.Equal('A', FreqOutHandlers.CharFor(Key.A, ModifierKeys.None));
        Assert.Equal('\0', FreqOutHandlers.CharFor(Key.A, ModifierKeys.Shift));
        Assert.Equal('\0', FreqOutHandlers.CharFor(Key.A, ModifierKeys.Control));
        Assert.Equal('\0', FreqOutHandlers.CharFor(Key.A, ModifierKeys.Control | ModifierKeys.Shift));
    }

    /// <summary>
    /// The digits carried the same defect: Ctrl+1 on the frequency field
    /// typed a 1, and Shift+1 on the Slice field jumped to slice B.
    /// </summary>
    [Fact]
    public void AModifiedDigitIsNoDigit()
    {
        for (int i = 0; i <= 9; i++)
        {
            Assert.Equal('\0', FreqOutHandlers.CharFor(Key.D0 + i, ModifierKeys.Shift));
            Assert.Equal('\0', FreqOutHandlers.CharFor(Key.D0 + i, ModifierKeys.Control));
            Assert.Equal('\0', FreqOutHandlers.CharFor(Key.NumPad0 + i, ModifierKeys.Control));
        }
    }

    /// <summary>
    /// The one place Shift changes the CHARACTER rather than making a chord:
    /// the physical =/+ key. Unshifted it is transceive, shifted it opens
    /// step entry, and both are characters the field maps use. Ctrl on the
    /// same key is a chord like any other.
    /// </summary>
    [Fact]
    public void TheEqualsKeyIsTheOneShiftException()
    {
        Assert.Equal('=', FreqOutHandlers.CharFor(Key.OemPlus, ModifierKeys.None));
        Assert.Equal('+', FreqOutHandlers.CharFor(Key.OemPlus, ModifierKeys.Shift));
        Assert.Equal('\0', FreqOutHandlers.CharFor(Key.OemPlus, ModifierKeys.Control));
        Assert.Equal('\0', FreqOutHandlers.CharFor(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal('\0', FreqOutHandlers.CharFor(Key.OemPlus, ModifierKeys.Alt | ModifierKeys.Shift));
    }

    /// <summary>
    /// The invariant, stated once over everything: a non-zero character
    /// means no modifier is held. Every <c>ch == 'X'</c> test in every field
    /// handler is a bare-key test because of this, so this is the assertion
    /// that makes the rest of the file's guards redundant rather than
    /// load-bearing.
    /// </summary>
    [Fact]
    public void EveryModifiedChordIsNoCharacterExceptThePlusKey()
    {
        var offenders = new List<string>();
        foreach (var key in EveryKey)
        {
            foreach (var mods in EveryModifierState)
            {
                if (mods == ModifierKeys.None) continue;
                if (key == Key.OemPlus && mods == ModifierKeys.Shift) continue;

                char ch = FreqOutHandlers.CharFor(key, mods);
                if (ch != '\0') offenders.Add(mods + "+" + key + " -> '" + ch + "'");
            }
        }

        Assert.True(offenders.Count == 0,
            "a modified chord produced a character, so a field handler would read it as the bare key (#546):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Positive control. A mapping that returned <c>'\0'</c> for everything
    /// would pass the invariant above while breaking every Home key, so the
    /// bare map is required to be the size the handlers depend on: twenty-six
    /// letters, ten digits on each row, and the seven punctuation keys.
    /// </summary>
    [Fact]
    public void TheBareMapIsNotEmpty()
    {
        int mapped = EveryKey.Count(k => FreqOutHandlers.CharFor(k, ModifierKeys.None) != '\0');
        Assert.True(mapped >= 26 + 10 + 10 + 7,
            "only " + mapped + " bare keys map to a character — the bare map has shrunk and every "
            + "assertion about modified chords in this file is passing on an empty function");
    }
}

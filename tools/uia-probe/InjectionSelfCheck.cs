using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JJFlex.UiaProbe;

/// <summary>What the injection self-check concluded. Only <see cref="Passed"/> lets a sweep go on.</summary>
internal enum SelfCheckVerdict
{
    /// <summary>The key reached the app's key dispatcher, and the trace says so.</summary>
    Passed,
    /// <summary>Not pressed: the chosen key is bound, so pressing it would DO something.</summary>
    RefusedBoundKey,
    /// <summary>Not pressed: the build's key inventory could not be read, so nobody can say the key is harmless.</summary>
    RefusedBindingUnknown,
    /// <summary>Not pressed: a sequence, where one keystroke is wanted.</summary>
    RefusedSequence,
    /// <summary>The target window was not in front when the key would have gone, so it went nowhere (or somewhere else).</summary>
    NotForeground,
    /// <summary>Windows refused the keystroke, or the press stopped before it.</summary>
    NotSent,
    /// <summary>The target window lost the foreground during the press: the key may have gone to whatever took it.</summary>
    LostForeground,
    /// <summary>No trace file to read, so the effect cannot be seen either way.</summary>
    NoTraceChannel,
    /// <summary>Pressed, in front, observed, and the dispatcher never logged the key: injection is unproven.</summary>
    EffectNotSeen,
}

/// <summary>
/// The injection self-check's decisions, with no Win32 in them, so they can
/// be tested without a desktop. jjprobe once reported
/// <c>foregrounded: true</c> with <c>routed: []</c>, a clean outcome it had
/// not earned, and an empty routing list cannot be told apart from "the app
/// ignored the key". So before a sweep trusts any press, it presses one key
/// whose only effect is known and checks for it.
/// </summary>
/// <remarks>
/// <para><b>The key and its effect.</b> An UNBOUND chord, so pressing it does
/// nothing to the app or the radio. Its one effect is the line the key
/// dispatcher writes for every key that reaches it, at Info level and so
/// always on: <c>DoCommand:</c> and the key as eight hex digits, in WinForms
/// <c>Keys</c> terms (the key code, plus 0x10000 for Shift, 0x20000 for
/// Control, 0x40000 for Alt). Seeing that line proves the keystroke arrived;
/// not seeing it, with everything else in order, proves it did not.</para>
/// <para>This file is linked into <c>Radios.Tests</c> on its own, so it must
/// use no other jjprobe type.</para>
/// </remarks>
internal static class InjectionSelfCheck
{
    /// <summary>
    /// The default self-check key. Unbound in the app's key table today; the
    /// self-check confirms that against the build's own inventory before it
    /// presses, and refuses if it ever becomes bound.
    /// </summary>
    public const string DefaultChord = "Ctrl+Shift+F12";

    private const int ShiftBit = 0x10000;
    private const int ControlBit = 0x20000;
    private const int AltBit = 0x40000;

    /// <summary>The key as the app's dispatcher sees it: WinForms <c>Keys</c> data.</summary>
    public static int WinFormsKeyData(ushort virtualKey, bool ctrl, bool shift, bool alt)
        => virtualKey | (ctrl ? ControlBit : 0) | (shift ? ShiftBit : 0) | (alt ? AltBit : 0);

    /// <summary>The dispatcher's line for that key, which is the effect looked for.</summary>
    public static string ExpectedRoutingLine(int keyData)
        => "DoCommand:" + keyData.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>
    /// May this key be pressed as the self-check? Null when it may; otherwise
    /// the refusal. <paramref name="chordBound"/> is null when the inventory
    /// could not say, which refuses too: "probably harmless" is not a check.
    /// </summary>
    public static SelfCheckVerdict? RefuseBeforePress(int stepCount, bool? chordBound)
    {
        if (stepCount != 1) return SelfCheckVerdict.RefusedSequence;
        if (chordBound == null) return SelfCheckVerdict.RefusedBindingUnknown;
        if (chordBound == true) return SelfCheckVerdict.RefusedBoundKey;
        return null;
    }

    /// <summary>
    /// Judge a press. Foreground first: a key sent while another window is in
    /// front proves nothing about the target, whatever else is seen.
    /// </summary>
    /// <param name="foregroundAtPress">The target window was in front when the key was sent.</param>
    /// <param name="sent">The keystroke left the probe (Windows took it).</param>
    /// <param name="foregroundAfterPress">The target window was still in front once the press settled.</param>
    /// <param name="traceChannel">A trace file was being read for the effect.</param>
    /// <param name="routedLines">The dispatcher lines the press produced, in order.</param>
    /// <param name="expectedLine">From <see cref="ExpectedRoutingLine"/>.</param>
    public static SelfCheckVerdict Judge(bool foregroundAtPress, bool sent, bool foregroundAfterPress,
        bool traceChannel, IEnumerable<string>? routedLines, string expectedLine)
    {
        if (!foregroundAtPress) return SelfCheckVerdict.NotForeground;
        if (!sent) return SelfCheckVerdict.NotSent;
        if (!foregroundAfterPress) return SelfCheckVerdict.LostForeground;
        if (!traceChannel) return SelfCheckVerdict.NoTraceChannel;
        bool seen = (routedLines ?? Enumerable.Empty<string>())
            .Any(l => l != null && l.StartsWith(expectedLine, StringComparison.OrdinalIgnoreCase)
                      && (l.Length == expectedLine.Length || !Uri.IsHexDigit(l[expectedLine.Length])));
        return seen ? SelfCheckVerdict.Passed : SelfCheckVerdict.EffectNotSeen;
    }

    /// <summary>One sentence for the console, naming what to do next where there is something.</summary>
    public static string Describe(SelfCheckVerdict verdict, string chord, string expectedLine) => verdict switch
    {
        SelfCheckVerdict.Passed =>
            $"self-check passed: {chord} reached the app's key dispatcher ({expectedLine} in the trace).",
        SelfCheckVerdict.RefusedBoundKey =>
            $"self-check REFUSED: {chord} is bound in this build, so pressing it would do something. "
            + "Choose an unbound chord with --selfcheck-chord.",
        SelfCheckVerdict.RefusedBindingUnknown =>
            $"self-check REFUSED: this build's key inventory could not be read, so nothing says {chord} is harmless. "
            + "Pass --appdir for the build under test.",
        SelfCheckVerdict.RefusedSequence =>
            $"self-check REFUSED: {chord} is a sequence; the self-check presses exactly one keystroke.",
        SelfCheckVerdict.NotForeground =>
            $"self-check FAILED: the target window was not in front, so {chord} was not delivered to it. "
            + "Every press after this would be reported against a window that never got the key.",
        SelfCheckVerdict.NotSent =>
            $"self-check FAILED: {chord} was not sent (Windows refused it, or the press stopped first).",
        SelfCheckVerdict.LostForeground =>
            $"self-check FAILED: the target window lost the foreground during the press, so {chord} may have "
            + "gone to whatever took it.",
        SelfCheckVerdict.NoTraceChannel =>
            "self-check FAILED: no trace file is being read, so the key's effect cannot be seen. "
            + "Run jjprobe with the same JJFLEX_CONFIG_DIR as the app (or --config-dir).",
        SelfCheckVerdict.EffectNotSeen =>
            $"self-check FAILED: {chord} was pressed with the window in front and the trace being read, and the "
            + $"dispatcher never logged it ({expectedLine} is missing). Injection is not reaching the app; no "
            + "press result from this run could be trusted.",
        _ => "self-check: unknown verdict " + verdict,
    };
}

using System;
using System.Diagnostics;
using JJTrace;
using Keys = System.Windows.Forms.Keys;

namespace JJFlexWpf;

/// <summary>
/// One diagnostic line per Space key down and up, so the bench session the
/// dialog transmit lock waits on can read how a screen reader delivers a
/// quick Shift+Space (#778c repair round three; Noel, 2026-10-21 10:55:
/// "wait for the bench"). Written from Home's two preview handlers and from
/// the dialog door's two entries, and from nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate.</b> The line is written at <see cref="TraceLevel.Verbose"/>,
/// which is how a detailed capture (Ctrl+J, Ctrl+D) gates everything it
/// adds: <c>StartDetailedCapture</c> in <c>globals.vb</c> raises
/// <c>Tracing.TheSwitch.Level</c> to Verbose and <c>StopDetailedCapture</c>
/// puts the standing level back. The standing level is Verbose too when the
/// Diagnostics tab's detail is set to Detailed (<c>DiagnosticsConfig.TraceLevel</c>),
/// so the line also appears under that setting; at Normal it is not written
/// at all. There is no second gate: a line that needs a capture AND a
/// setting is the switch that silently does nothing on the bench.
/// </para>
/// <para>
/// <b>What it records and what it must not.</b> Down or up, the modifiers
/// held at that moment, the repeat flag, <see cref="Environment.TickCount64"/>
/// (the clock the hold filter's measurements already use), and the door.
/// Only the Space key, on purpose: tracing other keys would put typed text
/// in the log. The call has no side effects and sits ahead of every branch
/// that could return, so it never changes what the key does.
/// </para>
/// </remarks>
internal static class SpaceKeyTimingTrace
{
    /// <summary>The tag every line starts with, for grep.</summary>
    internal const string Tag = "KEYTIME";

    /// <summary>The main window's preview handlers.</summary>
    internal const string HomeDoor = "home";

    /// <summary>The dialog door, <see cref="DialogTransmitKeys"/>.</summary>
    internal const string DialogDoor = "dialog";

    /// <summary>
    /// A Space key event. <paramref name="modifiers"/> is masked to the
    /// WinForms modifier flags, so Home (WPF modifiers, converted) and the
    /// dialog door (a WinForms chord) write the same vocabulary.
    /// </summary>
    internal static void Note(bool down, Keys modifiers, bool isRepeat, string door)
    {
        Tracing.TraceLine(Tag + " space " + (down ? "down" : "up")
            + " mods=" + (modifiers & Keys.Modifiers)
            + " repeat=" + isRepeat
            + " tick=" + Environment.TickCount64
            + " door=" + door, TraceLevel.Verbose);
    }
}

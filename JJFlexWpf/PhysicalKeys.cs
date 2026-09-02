using System.Runtime.InteropServices;

namespace JJFlexWpf;

/// <summary>
/// Which physical modifier key is down — the fact <c>System.Windows.Forms.Keys</c>
/// cannot carry. Its <c>Keys.Shift</c> bit says that A shift is held and not
/// which one, and the filter layer (#516) needs exactly that distinction:
/// Left Shift grabs the low edge, Right Shift the high edge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read at the instant of the press, never tracked as a hold.</b>
/// <c>GetKeyState</c> reports the keyboard as of the message the thread is
/// currently processing — so called from inside the arrow's key-down
/// dispatch it answers "which Shift was down when THIS arrow arrived", which
/// is the question. Nothing here remembers a Shift going down or coming up,
/// so JAWS synthesising down/up pairs for a held key has no state to
/// corrupt. That is the design's defence against the divergence between
/// JAWS and NVDA held-key delivery; it is still a claim to be pressed under
/// both, not a proof.
/// </para>
/// <para>
/// Win32 rather than WPF's <c>Keyboard.IsKeyDown</c> because the same
/// dispatcher serves the WinForms-hosted main window and the WPF dialogs,
/// and a query that works on both paths is one fewer place for the two to
/// disagree.
/// </para>
/// </remarks>
internal static class PhysicalKeys
{
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    private static bool IsDown(int vKey) => (GetKeyState(vKey) & 0x8000) != 0;

    /// <summary>Which Shift key is physically down right now.</summary>
    public static Radios.ShiftSide ShiftSideNow()
    {
        bool left = IsDown(VK_LSHIFT);
        bool right = IsDown(VK_RSHIFT);
        if (left && right) return Radios.ShiftSide.Both;
        if (left) return Radios.ShiftSide.Left;
        if (right) return Radios.ShiftSide.Right;
        return Radios.ShiftSide.None;
    }
}

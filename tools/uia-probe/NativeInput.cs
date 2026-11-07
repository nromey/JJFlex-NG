using System.ComponentModel;
using System.Runtime.InteropServices;

namespace JJFlex.UiaProbe;

/// <summary>
/// The structures <c>SendInput</c> takes, at their real Win32 layouts, in a
/// file of their own so a test can compile and measure exactly this source.
/// </summary>
/// <remarks>
/// <para><b>Why the whole union (#914).</b> Until 2026-11-07 the union held
/// <c>KEYBDINPUT</c> alone. On x64 that made <c>INPUT</c> 32 bytes, while
/// Windows' <c>sizeof(INPUT)</c> is 40, because the real union is as large as
/// its largest member, <c>MOUSEINPUT</c>. <c>SendInput</c> rejects a call whose
/// size argument does not match, with ERROR_INVALID_PARAMETER, and injects
/// nothing. The return value was ignored, so <c>press</c> foregrounded the
/// window, sent nothing, and reported the key "silent". Found by reading
/// (Claude) and confirmed independently (Codex); the harness run is what
/// proves the press now arrives.</para>
/// <para>Sizes: 40 bytes in a 64-bit process, 28 in a 32-bit one.
/// <c>Radios.Tests/JjprobeInputLayoutTests</c> compiles this file and
/// measures it.</para>
/// </remarks>
internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    /// <summary>The size passed to <c>SendInput</c>: Windows' own sizeof(INPUT).</summary>
    internal static int InputSize => Marshal.SizeOf<INPUT>();

    /// <summary>What <see cref="InputSize"/> must be in a process of this bitness.</summary>
    internal static int ExpectedInputSize => IntPtr.Size == 8 ? 40 : 28;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>
    /// Send one keyboard event, and say so loudly if Windows did not take it.
    /// A rejected or partial send is an ERROR, never a silent press: a probe
    /// that reports "silent" for a key it never delivered is reporting on
    /// itself, not on the app.
    /// </summary>
    private static void SendKeyboardInput(ushort vk, ushort scan, uint flags, IntPtr extraInfo)
    {
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = extraInfo },
            },
        };
        uint sent = SendInput(1, new[] { inp }, InputSize);
        if (sent != 1)
        {
            int error = Marshal.GetLastWin32Error();
            throw new SendInputFailedException(
                $"SendInput did not take the key event (vk 0x{vk:X2}, {((flags & KEYEVENTF_KEYUP) != 0 ? "up" : "down")}): "
                + $"it accepted {sent} of 1, Win32 error {error} ({new Win32Exception(error).Message}). "
                + $"Input size passed was {InputSize} bytes; this process expects {ExpectedInputSize}. "
                + "Nothing about the app can be concluded from this press.");
        }
    }
}

/// <summary>Windows refused a synthetic key event. Reported as an error, never as a silent press.</summary>
internal sealed class SendInputFailedException : Exception
{
    public SendInputFailedException(string message) : base(message) { }
}

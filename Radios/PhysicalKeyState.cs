using System;
using System.Runtime.InteropServices;

namespace Radios
{
    /// <summary>
    /// The operating system's own answer to "is that key down right now",
    /// asked below the window message stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not ask the UI framework.</b> WPF's keyboard state is built from
    /// the messages the window received, and under a screen reader that
    /// synthesises key-down/key-up pairs for a held key those messages are the
    /// very thing that is lying (#216). Asking WPF whether the key is down
    /// returns the reader's account of it. This asks Windows.
    /// </para>
    /// <para>
    /// <b>How much this is trusted, and how much it is not.</b> Injected input
    /// can also move the async key state, so a synthesising reader MIGHT be
    /// able to make this say "up" while the operator is still holding the key.
    /// Nobody has measured that, and this code does not assume either answer:
    /// <see cref="PttHoldFilter"/> uses it only to EXTEND a hold, never to end
    /// one, so a probe that is wrong in the pessimistic direction does nothing
    /// at all and a probe that is wrong in the optimistic direction costs a
    /// bounded fraction of a second. What it says is traced, so the next bench
    /// run under JAWS settles the question with evidence instead of argument.
    /// </para>
    /// <para>
    /// Only the high bit is read. The low bit of <c>GetAsyncKeyState</c> means
    /// "pressed since the last call" and is shared process-wide, so reading it
    /// would both mislead us and consume the answer for anyone else asking.
    /// </para>
    /// </remarks>
    public static class PhysicalKeyState
    {
        /// <summary>Virtual-key code for the space bar.</summary>
        public const int VkSpace = 0x20;

        [DllImport("user32.dll", SetLastError = false)]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// True when Windows currently considers the key held. False on any
        /// failure — a probe that cannot answer must never be able to hold a
        /// transmitter open.
        /// </summary>
        public static bool IsDown(int virtualKey)
        {
            try { return (GetAsyncKeyState(virtualKey) & 0x8000) != 0; }
            catch { return false; }
        }
    }
}

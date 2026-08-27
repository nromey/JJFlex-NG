using System;
using System.Runtime.InteropServices;

namespace Radios
{
    /// <summary>
    /// Which controller screen reader is running on this desktop RIGHT NOW,
    /// asked cheaply enough to ask forever (#283).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not asked of Prism.</b> Prism knows, but only tells us
    /// through its availability callback, and that callback is the thing that
    /// went missing — a reader that is already available never rises again, so
    /// the one fact we need arrives exactly never. Everything else Prism
    /// exposes about availability needs its context, which belongs to the
    /// backend instance and is not reachable from policy code. So the question
    /// is put to the operating system instead, where it can be asked at any
    /// moment rather than only at a transition.
    /// </para>
    /// <para>
    /// <b>Cheapness.</b> One <c>FindWindowW</c> per known reader. That is a
    /// single kernel call each, microseconds, no allocation, no process
    /// enumeration. A process sweep would have been more obviously correct and
    /// is far too expensive to run for the life of the application, which is
    /// the constraint the task set.
    /// </para>
    /// <para>
    /// <b>The positive control, and why there is one.</b> A probe that returns
    /// "no reader" is making two claims at once: that no reader is running, and
    /// that the probe would have seen one if it were. The second claim is the
    /// one that quietly fails — in a session with no shell, or if these window
    /// signatures ever stop being true. So <see cref="ProbeWorks"/> looks for
    /// something that must be there, and a caller that cannot get a positive
    /// answer from it must treat every negative as unknown rather than as
    /// absence. This is the whole reason the watchdog has a stand-down state.
    /// </para>
    /// <para>
    /// <b>What is NOT observed, stated plainly.</b> Prism reaches eight
    /// controller readers; the table below names two. NVDA and JAWS are the
    /// pair the operator actually swaps between, the pair the 2026-08-26
    /// evidence is about, and the pair whose window signatures are stable and
    /// widely relied on. The other six (ZDSR, ZoomText, System Access,
    /// PC-Talker, Sense Reader, BoyPC) are deliberately absent rather than
    /// guessed at: an entry with the wrong class name is worse than no entry,
    /// because it turns "I did not see it" into a confident wrong answer.
    /// A reader that is not in this table simply never triggers a rebind, which
    /// is precisely today's behaviour, so nothing regresses for its users.
    /// Adding one is a two-line change once its window class is verified on a
    /// machine that runs it.
    /// </para>
    /// </remarks>
    public static class ScreenReaderPresence
    {
        /// <summary>
        /// A window signature that identifies one reader. <c>Title</c> is null
        /// when the class alone is specific enough — JAWS's window text carries
        /// its version, so matching on it would break every year.
        /// </summary>
        private readonly struct Signature
        {
            public readonly string Name;
            public readonly string ClassName;
            public readonly string? Title;

            public Signature(string name, string className, string? title)
            {
                Name = name;
                ClassName = className;
                Title = title;
            }
        }

        private static readonly Signature[] Signatures =
        {
            // NVDA's main frame. wxWidgets classes are shared by other
            // applications, so the title is required here to keep this
            // specific.
            new Signature("NVDA", "wxWindowClassNR", "NVDA"),

            // JAWS's own UI class. Not shared, and stable across versions in a
            // way the window text is not.
            new Signature("JAWS", "JFWUI2", null),
        };

        /// <summary>
        /// The shell's taskbar window. Present in every interactive desktop
        /// session, which is the only kind this application runs in.
        /// </summary>
        private const string PositiveControlClass = "Shell_TrayWnd";

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

        /// <summary>
        /// Whether a negative result from <see cref="Detect"/> can be believed.
        ///
        /// Finds a window that must exist. If even that fails, the mechanism is
        /// unusable here and "no reader found" carries no information at all.
        /// </summary>
        public static bool ProbeWorks()
        {
            try { return FindWindowW(PositiveControlClass, null) != IntPtr.Zero; }
            catch { return false; }
        }

        /// <summary>
        /// The controller screen reader running on this desktop, or null when
        /// none of the readers we can identify is running.
        ///
        /// Null means "none of the readers in the table", NOT "no reader" —
        /// see the remarks on this class. Callers pair it with
        /// <see cref="ProbeWorks"/> and with the policy in
        /// <see cref="ScreenReaderWatch"/>, which never tears a binding down on
        /// a null it cannot corroborate.
        /// </summary>
        public static string? Detect()
        {
            try
            {
                foreach (var sig in Signatures)
                {
                    if (FindWindowW(sig.ClassName, sig.Title) != IntPtr.Zero)
                        return sig.Name;
                }
            }
            catch
            {
                // A P/Invoke failure is not evidence of absence either. The
                // caller's ProbeWorks check is what decides whether to believe
                // this, and it will fail for the same reason.
            }
            return null;
        }

        /// <summary>
        /// The reader names this probe is able to recognise, for tracing and
        /// for the diagnostics page. Naming them is how a reader of a trace
        /// tells "no reader running" from "a reader we cannot see".
        /// </summary>
        public static string ObservableReaders
        {
            get
            {
                var names = new string[Signatures.Length];
                for (int i = 0; i < Signatures.Length; i++) names[i] = Signatures[i].Name;
                return string.Join(", ", names);
            }
        }
    }
}

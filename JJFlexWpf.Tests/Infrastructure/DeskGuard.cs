using System;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// Whether this test run may create windows at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-08-25, the morning a bare <c>dotnet test</c> put a stream of
/// dialogs on the operator's screen while he was working.</b> He was not at the
/// window and pressed no keys; an agent ran the solution-wide test command,
/// which built and ran this project, and this project constructs real WPF
/// dialogs.
/// </para>
/// <para>
/// <b>Two guards already existed and neither held.</b> <c>radiocheck</c>
/// refuses the foreground tiers without <c>-DeskFree</c> — but radiocheck is a
/// WRAPPER, and a wrapper is bypassed by anyone who types the underlying
/// command, which is every agent and every person checking their own work.
/// And <see cref="UiThread.RequestPrivateDesktop"/> is on by default — but its
/// result was assigned to a property and never read, so a failed isolation
/// carried straight on and showed the windows anyway.
/// </para>
/// <para>
/// So the rule now lives ON the path rather than beside it: no window is
/// created unless this says so, and it is consulted by the one thread that
/// could create one.
/// </para>
/// <para>
/// <b>FAIL CLOSED.</b> Anything other than a confirmed isolation, or an
/// explicit human declaration, refuses. A guard whose uncertain case is "carry
/// on" is the guard that just failed.
/// </para>
/// </remarks>
internal static class DeskGuard
{
    /// <summary>
    /// Set to 1 by a human who has stepped away from the machine, or by
    /// <c>radiocheck -DeskFree</c> on their behalf.
    /// </summary>
    /// <remarks>
    /// Deliberately an environment variable and not a setting file. It must be
    /// per-run and evaporate with the process: a persisted "windows may appear"
    /// switch is a footgun pointed at the thing it protects, the same argument
    /// as <c>JJFLEX_CONFIG_DIR</c>.
    /// </remarks>
    public const string DeskFreeVariable = "JJFLEX_TIER1_DESK_FREE";

    /// <summary>What the guard decided, and why.</summary>
    public enum Verdict
    {
        /// <summary>The UI thread is on a desktop nobody is looking at.</summary>
        AllowedIsolated,

        /// <summary>A human said the desk is free. Windows may be visible.</summary>
        AllowedDeskDeclaredFree,

        /// <summary>Isolation was asked for and did not happen. Refuse.</summary>
        RefusedIsolationFailed,

        /// <summary>Isolation was switched off and nobody declared the desk free.</summary>
        RefusedIsolationDisabled,
    }

    public static bool IsAllowed(Verdict v)
        => v == Verdict.AllowedIsolated || v == Verdict.AllowedDeskDeclaredFree;

    /// <summary>
    /// Decide, from the three facts that matter. Pure, so the rule can be read
    /// and reasoned about without starting a UI thread.
    /// </summary>
    /// <param name="isolationRequested">Was a private desktop asked for?</param>
    /// <param name="isolation">What actually happened when it was attempted.</param>
    /// <param name="deskDeclaredFree">Did a human say the screen is theirs to use?</param>
    /// <remarks>
    /// The human declaration is checked FIRST and wins outright. Someone who
    /// has stepped away has said the strongest thing available, and a test run
    /// that refuses after being told it may proceed is a test run nobody can
    /// use.
    /// </remarks>
    public static Verdict Decide(bool isolationRequested,
                                 DesktopIsolation isolation,
                                 bool deskDeclaredFree)
    {
        if (deskDeclaredFree) return Verdict.AllowedDeskDeclaredFree;

        if (!isolationRequested) return Verdict.RefusedIsolationDisabled;

        return isolation == DesktopIsolation.Isolated
            ? Verdict.AllowedIsolated
            : Verdict.RefusedIsolationFailed;
    }

    /// <summary>True when a human has declared the desk free for this run.</summary>
    public static bool DeskDeclaredFree
        => Environment.GetEnvironmentVariable(DeskFreeVariable) == "1";

    /// <summary>
    /// What to tell whoever is reading the failure. Written for a person who
    /// did not expect this and does not know what Tier 1 is.
    /// </summary>
    public static string Explain(Verdict v, int lastWin32Error) => v switch
    {
        Verdict.RefusedIsolationFailed =>
            "These tests build real application windows, so they run on a private "
            + "desktop that nobody can see. Creating that desktop FAILED (Windows "
            + "error " + lastWin32Error + "), and rather than put the windows on "
            + "your actual screen, the run stopped.\r\n\r\n"
            + "If the screen is yours to use — you have stepped away, or you do not "
            + "mind windows appearing — set " + DeskFreeVariable + "=1 for this run "
            + "and they will proceed.",

        Verdict.RefusedIsolationDisabled =>
            "These tests build real application windows. Private-desktop isolation "
            + "is switched off (JJFLEX_TIER1_PRIVATE_DESKTOP=0) and nobody has said "
            + "the screen is free to use, so the run stopped rather than show them.\r\n\r\n"
            + "Set " + DeskFreeVariable + "=1 for this run if that is what you want.",

        _ => "",
    };

    /// <summary>
    /// A one-line summary for the run report, so an allowed run says WHY it was
    /// allowed. "It worked" and "somebody waived the check" must never look the
    /// same afterwards.
    /// </summary>
    public static string Describe(Verdict v) => v switch
    {
        Verdict.AllowedIsolated => "windows on a private desktop",
        Verdict.AllowedDeskDeclaredFree => "windows VISIBLE — desk declared free",
        Verdict.RefusedIsolationFailed => "refused — private desktop could not be created",
        Verdict.RefusedIsolationDisabled => "refused — isolation off and desk not declared free",
        _ => "unknown",
    };
}

/// <summary>
/// Thrown when the guard refuses. A distinct type so it reads as a deliberate
/// stop in the test output rather than as a crash in the code under test.
/// </summary>
internal sealed class DeskNotFreeException : Exception
{
    public DeskNotFreeException(string message) : base(message) { }
}

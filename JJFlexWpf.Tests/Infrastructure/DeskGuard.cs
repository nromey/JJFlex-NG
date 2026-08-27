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
/// <para>
/// <b>Isolation means invisible AND inaudible, since task #233.</b> Until then
/// this asked one question — is the UI thread on a desktop nobody is looking at
/// — and answered "allowed" on the strength of it. That is a sighted person's
/// definition of isolated. <see cref="PrivateDesktop"/> does nothing whatever
/// about sound, so a run could pass this guard and then play earcons and speech
/// at whoever was sitting there, which for this project's users is the WORSE of
/// the two failures: a noise arriving from something that cannot be found,
/// focused or dismissed. A half-working guard is more dangerous than none
/// because it is trusted, so both facts are now conditions rather than one
/// being a condition and the other a line in a report.
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

        /// <summary>
        /// The windows would be invisible and the run would still be audible.
        /// Refuse — this is the failure the operator actually experiences.
        /// </summary>
        RefusedAudioNotSuppressed,

        /// <summary>
        /// The run would read and write the operator's own settings folder.
        /// Refuse, and this one is not waivable.
        /// </summary>
        RefusedSettingsNotIsolated,
    }

    public static bool IsAllowed(Verdict v)
        => v == Verdict.AllowedIsolated || v == Verdict.AllowedDeskDeclaredFree;

    /// <summary>
    /// Decide, from the four facts that matter. Pure, so the rule can be read
    /// and reasoned about without starting a UI thread.
    /// </summary>
    /// <param name="isolationRequested">Was a private desktop asked for?</param>
    /// <param name="isolation">What actually happened when it was attempted.</param>
    /// <param name="deskDeclaredFree">Did a human say the screen is theirs to use?</param>
    /// <param name="audioSuppressed">
    /// Did <see cref="QuietRun"/> confirm, by reading it back, that rendering
    /// is off for this process?
    /// </param>
    /// <param name="settingsIsolated">
    /// Did <see cref="TestSettingsRoot"/> confirm, by reading it back, that the
    /// whole settings tree points somewhere throwaway?
    /// </param>
    /// <remarks>
    /// <para>The human declaration is checked before the desk conditions and
    /// wins over them outright. Someone who has stepped away has said the
    /// strongest thing available, and a test run that refuses after being told
    /// it may proceed is a test run nobody can use. That declaration covers
    /// sound as well as windows: "the machine is mine to use" is not a
    /// statement about one sense.</para>
    /// <para><b>It does NOT cover the settings, which is why that one is
    /// checked first and cannot be waived.</b> <c>JJFLEX_TIER1_DESK_FREE</c>
    /// says the SCREEN and the SPEAKERS are free — a statement about a person
    /// having stepped away from a desk. It is not consent to rewrite their
    /// configuration, nobody would read it as that, and the damage outlives the
    /// run rather than evaporating with it. Consent to be disturbed and consent
    /// to be modified are different consents, and a guard that treats one as
    /// the other has stopped asking the question it was written to ask.</para>
    /// <para>Sound is checked LAST of the refusals, so a run that fails both
    /// reports the desktop failure. Both are true, and the desktop one is the
    /// one that has to be fixed first for the other question to even arise.</para>
    /// </remarks>
    public static Verdict Decide(bool isolationRequested,
                                 DesktopIsolation isolation,
                                 bool deskDeclaredFree,
                                 bool audioSuppressed,
                                 bool settingsIsolated)
    {
        if (!settingsIsolated) return Verdict.RefusedSettingsNotIsolated;

        if (deskDeclaredFree) return Verdict.AllowedDeskDeclaredFree;

        if (!isolationRequested) return Verdict.RefusedIsolationDisabled;

        if (isolation != DesktopIsolation.Isolated) return Verdict.RefusedIsolationFailed;

        if (!audioSuppressed) return Verdict.RefusedAudioNotSuppressed;

        return Verdict.AllowedIsolated;
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

        Verdict.RefusedAudioNotSuppressed =>
            "The windows would have been invisible, but this run would still have "
            + "made NOISE. Building these dialogs drives earcons and speech, and "
            + "turning that off for the test process FAILED"
            + (QuietRun.Failure == null ? "" : " (" + QuietRun.Failure + ")")
            + ".\r\n\r\n"
            + "A run you cannot see but can hear is worse than one you can see: the "
            + "sound arrives from something that cannot be found, focused or "
            + "dismissed. So the run stopped.\r\n\r\n"
            + "If the machine is yours to use — you have stepped away, or you do not "
            + "mind the noise — set " + DeskFreeVariable + "=1 for this run.",

        Verdict.RefusedSettingsNotIsolated =>
            "This run would have read and written YOUR OWN settings folder. These "
            + "tests construct every dialog in the application, and a dialog reads "
            + "and rewrites configuration as it builds itself — key maps, per-radio "
            + "profiles, connection entries.\r\n\r\n"
            + (TestSettingsRoot.Failure ?? "The throwaway settings tree was not bound.")
            + "\r\n\r\n"
            + DeskFreeVariable + " does NOT lift this. That variable says the screen "
            + "and speakers are yours to disturb; it is not consent to change your "
            + "configuration, and the damage would outlast the run.",

        _ => "",
    };

    /// <summary>
    /// A one-line summary for the run report, so an allowed run says WHY it was
    /// allowed. "It worked" and "somebody waived the check" must never look the
    /// same afterwards.
    /// </summary>
    public static string Describe(Verdict v) => v switch
    {
        Verdict.AllowedIsolated => "windows on a private desktop, audio suppressed",
        Verdict.AllowedDeskDeclaredFree => "windows VISIBLE and audio UNCHECKED — desk declared free",
        Verdict.RefusedIsolationFailed => "refused — private desktop could not be created",
        Verdict.RefusedIsolationDisabled => "refused — isolation off and desk not declared free",
        Verdict.RefusedAudioNotSuppressed => "refused — invisible but audible: audio could not be suppressed",
        Verdict.RefusedSettingsNotIsolated => "refused — the operator's own settings folder was in scope",
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

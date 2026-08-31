using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RadioInTheLoop;

/// <summary>
/// Whether this run may touch a radio at all, and which one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-08-30, the night a connect fault could only be diagnosed
/// from trace files hours after the fact.</b> A connect blocked the UI thread
/// for 45 seconds, three times; the operator's whole desktop stopped taking
/// keystrokes; and the only instrument that saw it was a trace read long
/// afterwards. This harness exists so that class of fault is caught by a
/// machine, against a real radio, before a person pays for it.
/// </para>
/// <para>
/// <b>A radio is a bigger blast radius than a screen.</b> This guard follows
/// the discipline of <c>JJFlexWpf.Tests/Infrastructure/DeskGuard.cs</c> - the
/// guard written the morning a bare <c>dotnet test</c> put dialogs on the
/// operator's desktop - and tightens it, because the resource here is a
/// transceiver that may belong to somebody else and may have somebody on it:
/// </para>
/// <para>
/// 1. The run refuses unless a human has declared a radio free, per run, by
/// environment variable. 2. The declaration NAMES the radio by serial - you
/// cannot say "whatever you find". 3. Even a named, declared radio is refused
/// if anything is connected to it. 4. The run's settings tree is throwaway and
/// verified by read-back, and that check is not waivable. 5. Every refusal
/// says what it wanted and what it found.
/// </para>
/// <para>
/// <b>FAIL CLOSED.</b> Anything other than a named radio, declared free,
/// found alone, on an isolated settings tree, refuses. A guard whose
/// uncertain case is "carry on" is the guard that just failed.
/// </para>
/// </remarks>
internal static class RadioFreeGuard
{
    /// <summary>
    /// Set - to the serial of the radio being offered - by a human who knows
    /// that radio is theirs to disturb for the next few minutes.
    /// </summary>
    /// <remarks>
    /// Deliberately an environment variable and not a setting file, for the
    /// same reason as <c>JJFLEX_TIER1_DESK_FREE</c> and
    /// <c>JJFLEX_CONFIG_DIR</c>: it must be per-run and evaporate with the
    /// terminal. Never set it in a script, never set it in an agent brief,
    /// never persist it in a profile. The value doubles as the declaration
    /// and the target: there is no way to declare a radio free without
    /// naming exactly which one.
    /// </remarks>
    public const string DeclarationVariable = "JJFLEX_RADIO_IN_THE_LOOP";

    /// <summary>What the guard decided, and why.</summary>
    public enum Verdict
    {
        /// <summary>A human named this serial for this run.</summary>
        Allowed,

        /// <summary>
        /// The run would have used a settings tree that is not throwaway.
        /// Refuse, and this one is not waivable by any declaration.
        /// </summary>
        RefusedSettingsNotIsolated,

        /// <summary>Nobody declared a radio free. Refuse.</summary>
        RefusedNoDeclaration,

        /// <summary>The declaration does not look like a radio serial. Refuse.</summary>
        RefusedMalformedSerial,

        /// <summary>
        /// The change-nothing hold could not be armed for the named radio, so
        /// a connect might write to it. Refuse.
        /// </summary>
        RefusedHoldNotArmed,

        /// <summary>
        /// The heartbeat instrument failed its own positive control, so any
        /// responsiveness verdict it produced would be meaningless. Refuse.
        /// </summary>
        RefusedInstrumentBroken,

        /// <summary>Discovery never showed the named radio. Refuse.</summary>
        RefusedRadioNotFound,

        /// <summary>The named radio has clients on it right now. Refuse.</summary>
        RefusedRadioOccupied,
    }

    public static bool IsAllowed(Verdict v) => v == Verdict.Allowed;

    /// <summary>
    /// Read and validate the declaration. Pure over its input so the rule can
    /// be read without an environment.
    /// </summary>
    /// <returns>The verdict, plus the trimmed serial when allowed.</returns>
    public static (Verdict verdict, string serial) DecideDeclaration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (Verdict.RefusedNoDeclaration, "");

        string serial = raw.Trim();

        // Flex serials look like 0621-1104-6601-1425: dash-separated groups of
        // letters and digits. The shape check is deliberately permissive - the
        // REAL check is that discovery finds this exact serial - but a value
        // like "1" or "yes" is a reflex, not a declaration, and refusing it
        // early gives a clearer message than "radio 1 was not found".
        if (!Regex.IsMatch(serial, "^[0-9A-Za-z]+(-[0-9A-Za-z]+){2,}$",
                RegexOptions.None, TimeSpan.FromSeconds(1))
            || serial.Length < 10 || serial.Length > 40)
        {
            return (Verdict.RefusedMalformedSerial, serial);
        }

        return (Verdict.Allowed, serial);
    }

    /// <summary>
    /// What to tell whoever is reading the refusal. Written for a person who
    /// did not expect it, hears it through a screen reader, and needs the next
    /// step in the same breath.
    /// </summary>
    public static string Explain(Verdict v, GuardFacts f) => v switch
    {
        Verdict.RefusedSettingsNotIsolated =>
            "This run would have read and written a settings folder that is not "
            + "throwaway. The harness always creates its own scratch settings tree "
            + "and verifies, by reading it back, that the radio layer is actually "
            + "using it. That verification FAILED.\r\n"
            + "Wanted settings root: " + f.WantedSettingsRoot + "\r\n"
            + "Actual settings root: " + f.ActualSettingsRoot + "\r\n"
            + "No declaration lifts this. It protects your configuration, and the "
            + "damage would outlast the run.",

        Verdict.RefusedNoDeclaration =>
            "No radio has been declared free for this run, so the harness "
            + "stopped before touching the network at all.\r\n"
            + "This harness connects to a REAL radio: it will register a GUI "
            + "client named " + f.StationName + ", hold a slice for a minute or "
            + "two, disconnect, and verify it left no trace. That only happens "
            + "when a person says a specific radio is theirs to use, for one "
            + "run, like this:\r\n"
            + "  set " + DeclarationVariable + " to your radio's serial number, "
            + "then run this program again from the same terminal.\r\n"
            + "In PowerShell: $env:" + DeclarationVariable + " = \"0621-1104-6601-1425\" "
            + "(with your real serial).\r\n"
            + "Never set that variable in a script or a profile. It is a "
            + "statement by a human, about this radio, for this run.",

        Verdict.RefusedMalformedSerial =>
            "The declaration in " + DeclarationVariable + " was '" + f.DeclaredValue
            + "', which does not look like a radio serial number. Expected the "
            + "dash-separated serial from the radio itself, like "
            + "0621-1104-6601-1425. The variable must name the exact radio being "
            + "offered - that is what keeps this harness off a radio that "
            + "belongs to somebody else.",

        Verdict.RefusedHoldNotArmed =>
            "The change-nothing hold could not be armed for radio " + f.Serial
            + ", so a connect might have written settings to it (TNF, VOX, CW "
            + "break-in, profiles - the writes a normal connect performs). The "
            + "harness promises to leave the radio exactly as found, and without "
            + "the hold it cannot keep that promise, so it stopped before "
            + "connecting.\r\n"
            + (f.Detail ?? ""),

        Verdict.RefusedInstrumentBroken =>
            "The heartbeat instrument failed its own self-check: a deliberate "
            + f.Detail + " block posted to the pumped thread was not observed. "
            + "Every responsiveness number this run produced would have been "
            + "meaningless - a blocked UI thread could have passed - so the run "
            + "stopped before touching the radio.",

        Verdict.RefusedRadioNotFound =>
            "Radio " + f.Serial + " was declared free, but local discovery did "
            + "not see it within " + f.WaitedSeconds + " seconds.\r\n"
            + (f.RadiosSeen.Count == 0
                ? "No radios were seen at all. If JJ Flexible or SmartSDR is "
                  + "running on this machine, close it first - only one program "
                  + "can listen for radios at a time. Otherwise check that the "
                  + "radio is powered on and on this network."
                : "Radios that WERE seen: " + string.Join("; ", f.RadiosSeen) + ". "
                  + "If one of those is yours, set " + DeclarationVariable
                  + " to that serial and run again.")
            + "\r\nThis harness never connects to a radio it was not given by "
            + "name, so it stopped.",

        Verdict.RefusedRadioOccupied =>
            "Radio " + f.Serial + " has " + f.Occupancy + " connected right "
            + "now, so the harness refused to connect. A connect test on a "
            + "radio somebody is operating is not acceptable - it would take a "
            + "slice and could disturb their session.\r\n"
            + "If that client is you (JJ Flexible or SmartSDR open on this "
            + "radio), disconnect it and run again. If it is a leftover "
            + "JJHarness registration from a crashed run, wait about a minute "
            + "for the radio to drop the dead connection and run again.",

        _ => "",
    };

    /// <summary>
    /// One line for the run report, so an allowed run says WHY it was allowed.
    /// "It worked" and "somebody waived a check" must never look the same
    /// afterwards - and here nothing is waivable, so allowed always means the
    /// same thing: a human named this radio for this run and every check held.
    /// </summary>
    public static string Describe(Verdict v, string serial) => v switch
    {
        Verdict.Allowed => "allowed - radio " + serial + " declared free by "
                           + DeclarationVariable + " for this run",
        Verdict.RefusedSettingsNotIsolated => "the settings tree is not isolated (not waivable)",
        Verdict.RefusedNoDeclaration => "no radio has been declared free",
        Verdict.RefusedMalformedSerial => "the declaration is not a serial",
        Verdict.RefusedHoldNotArmed => "the change-nothing hold did not arm",
        Verdict.RefusedInstrumentBroken => "the heartbeat instrument failed its self-check",
        Verdict.RefusedRadioNotFound => "the declared radio was not discovered",
        Verdict.RefusedRadioOccupied => "the declared radio is occupied",
        _ => "unknown",
    };

    /// <summary>The facts a refusal message draws on. Fill what applies.</summary>
    public sealed class GuardFacts
    {
        public string Serial = "";
        public string DeclaredValue = "";
        public string StationName = "";
        public string WantedSettingsRoot = "";
        public string ActualSettingsRoot = "";
        public int WaitedSeconds;
        public List<string> RadiosSeen = new();
        public string Occupancy = "";
        public string? Detail;
    }
}

/// <summary>
/// Thrown when the guard refuses. A distinct type so it reads as a deliberate
/// stop in the output rather than as a crash in the code under test.
/// </summary>
internal sealed class RadioNotFreeException : Exception
{
    public RadioFreeGuard.Verdict Verdict { get; }
    public RadioNotFreeException(RadioFreeGuard.Verdict verdict, string message)
        : base(message)
    {
        Verdict = verdict;
    }
}

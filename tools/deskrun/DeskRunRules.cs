using System.Collections;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace JJFlex.DeskRun;

/// <summary>
/// The launcher's decisions, kept free of Win32 so they can be read and tested
/// without starting anything (task #906).
/// </summary>
/// <remarks>
/// <para>
/// This file is compiled into <c>JJFlexWpf.Tests</c> as a link, the same way
/// that project already shares a fixture with <c>Radios.Tests</c>, so the tests
/// exercise the very source the launcher runs rather than a copy of it.
/// </para>
/// <para>
/// <b>The guarantee these rules hold: a run through deskrun is allowed by
/// isolation and by nothing else, and leaves nothing behind.</b> Repair round 1
/// (Codex's check of <c>06834bae</c>) showed that stripping the desk-free
/// declaration from dotnet's environment is not enough: VSTest can install
/// environment variables in testhost AFTER that, from <c>--environment</c>, from
/// a runsettings file, from inline runsettings after <c>--</c>, and from MSBuild
/// properties that select a settings file. So the arguments are an ALLOWLIST,
/// the child's environment loses every variable that MSBuild or VSTest would
/// read as settings, and the project's own build files are checked for a
/// settings route before anything is created.
/// </para>
/// <para>
/// <b>Round 2 moved the guarantee to the guard; these rules are now the
/// second layer.</b> Codex's re-check found three more ways to put the
/// declaration back (an option value dotnet re-reads as <c>--environment</c>,
/// a multiline <c>&lt;Import</c> the scan below misses, and an inherited
/// <c>MSBuildUserExtensionsPath</c> whose <c>ImportAfter</c> files nothing
/// here reads). Chasing MSBuild's import graph from outside has no end, so
/// <c>DeskGuard.Decide</c> no longer consults the declaration at all when the
/// process started on a hidden desktop. The first of the three is also refused
/// here, because it costs a few lines; the other two are closed at the guard
/// and deliberately not chased further.
/// </para>
/// </remarks>
internal static class DeskRunRules
{
    /// <summary>
    /// The human declaration that the desk is free. Named here only so it can be
    /// REMOVED. The launcher never sets it and never passes it on.
    /// </summary>
    public const string DeskFreeVariable = "JJFLEX_TIER1_DESK_FREE";

    /// <summary>
    /// Set in the child purely so a log reader can see which desktop the
    /// launcher created. The guard inside the test process does NOT read it:
    /// it asks Windows which desktop its thread is on. A test forges this
    /// variable to prove that.
    /// </summary>
    public const string AnnouncedDesktopVariable = "JJFLEX_DESKRUN_DESKTOP";

    /// <summary>Default timeout, in seconds, after which the whole tree is killed.</summary>
    public const int DefaultTimeoutSeconds = 600;

    /// <summary>
    /// Points at which the launcher can be told to stop and wait, so a test can
    /// kill it there and check the child died with it (repair round 1, B2).
    /// Only the launcher's own option sets one; it is never a dotnet argument.
    /// </summary>
    public static readonly IReadOnlyList<string> LifecycleBarriers = new[] { "after-create", "after-resume" };

    /// <summary>The arguments, once checked.</summary>
    internal sealed record Parsed(string Project, int TimeoutSeconds, IReadOnlyList<string> PassThrough,
                                  string? LifecycleBarrier = null);

    /// <summary>
    /// Check the command line. Returns null and an error when the run must not
    /// start. There is always exactly one named project, because a bare
    /// <c>dotnet test</c> builds and runs every test project in the solution,
    /// and every argument after it must be on the allowlist below.
    /// </summary>
    public static Parsed? Parse(IReadOnlyList<string> args, Func<string, bool> fileExists, out string error)
    {
        error = "";
        int timeout = DefaultTimeoutSeconds;
        string? barrier = null;
        int i = 0;

        // The launcher's OWN options, before the project. None of these reach
        // dotnet.
        while (i < args.Count)
        {
            string a = args[i];
            if (a == "--")
            {
                i++;
                break;
            }
            if (a == "--timeout")
            {
                if (i + 1 >= args.Count
                    || !int.TryParse(args[i + 1], System.Globalization.NumberStyles.None,
                                     System.Globalization.CultureInfo.InvariantCulture, out timeout)
                    || timeout < 1)
                {
                    error = "--timeout needs a whole number of seconds, 1 or more.";
                    return null;
                }
                i += 2;
                continue;
            }
            if (a == "--lifecycle-barrier")
            {
                // Test-only. Makes the launcher stop at a named point and wait
                // to be killed. Inert unless given, and it is a launcher option:
                // it is consumed here and never forwarded.
                if (i + 1 >= args.Count || !LifecycleBarriers.Contains(args[i + 1]))
                {
                    error = "--lifecycle-barrier needs one of: " + string.Join(", ", LifecycleBarriers) + ".";
                    return null;
                }
                barrier = args[i + 1];
                i += 2;
                continue;
            }
            break;
        }

        if (i >= args.Count)
        {
            error = "Name the test project. deskrun never runs dotnet test without one.";
            return null;
        }

        string project = args[i];
        if (!IsProjectFile(project))
        {
            error = "The first argument after the options must be a project file "
                    + "(.csproj, .vbproj or .fsproj), not '" + project + "'.";
            return null;
        }
        if (!fileExists(project))
        {
            error = "The project '" + project + "' does not exist.";
            return null;
        }

        var rest = args.Skip(i + 1).ToList();
        string? refused = CheckPassThrough(rest);
        if (refused != null)
        {
            error = refused;
            return null;
        }

        return new Parsed(project, timeout, rest, barrier);
    }

    // ─────────────────────────── the allowlist ───────────────────────────

    /// <summary>Configuration names: Debug, Release, or another plain word.</summary>
    private static readonly Regex PlainWord = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    /// <summary>A platform: x64, x86, AnyCPU. Letters and digits only, so no second property can ride along.</summary>
    private static readonly Regex PlatformValue = new("^[A-Za-z0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A test filter: names, operators and grouping. No semicolon, comma,
    /// percent, quote, dollar or at sign, any of which could start a second
    /// MSBuild property or a substitution.
    /// </summary>
    private static readonly Regex FilterValue = new(@"^[A-Za-z0-9_.~|&!=()<>\- ]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A logger: console or trx only, each with plain key=value parameters
    /// (for example <c>console;verbosity=detailed</c>). A logger cannot set an
    /// environment variable, but an arbitrary logger name can load an arbitrary
    /// extension, so only the two built-in ones are allowed.
    /// </summary>
    private static readonly Regex LoggerValue = new(
        @"^(?i:console|trx)(;[A-Za-z]+=[A-Za-z0-9_.\-]+)*$", RegexOptions.CultureInvariant);

    /// <summary>A results folder: an ordinary path, with nothing MSBuild would read as syntax.</summary>
    private static readonly Regex PathValue = new(@"^[^;%$@""*?<>|]+$", RegexOptions.CultureInvariant);

    /// <summary>A blame-hang timeout: a number, optionally with ms, s, m or h.</summary>
    private static readonly Regex DurationValue = new("^[0-9]+(?i:ms|s|m|h)?$", RegexOptions.CultureInvariant);

    private static readonly string[] Verbosities =
        { "q", "quiet", "m", "minimal", "n", "normal", "d", "detailed", "diag", "diagnostic" };

    /// <summary>
    /// Options that take a value, by every spelling, with the check for that
    /// value. Anything not here and not a bare flag below is REFUSED.
    /// </summary>
    private static readonly (string[] Names, Func<string, bool> Valid, string What)[] ValueOptions =
    {
        (new[] { "-c", "--configuration" }, v => PlainWord.IsMatch(v), "a configuration name"),
        (new[] { "--filter" }, v => FilterValue.IsMatch(v), "a test filter"),
        (new[] { "--logger", "-l" }, v => LoggerValue.IsMatch(v), "console or trx, with plain key=value parameters"),
        (new[] { "--results-directory" }, v => PathValue.IsMatch(v), "a folder path"),
        (new[] { "--blame-hang-timeout" }, v => DurationValue.IsMatch(v), "a duration such as 60s or 5m"),
        (new[] { "-v", "--verbosity" },
            v => Verbosities.Contains(v, StringComparer.OrdinalIgnoreCase), "a verbosity level"),
    };

    /// <summary>
    /// Flags that take no value. <c>--list-tests</c> is added beyond the brief's
    /// list: it runs nothing, and it is how a filter is checked before a run.
    /// <c>--nologo</c> only removes a banner.
    /// </summary>
    /// <remarks>
    /// <c>--disable-build-servers</c> was added in round 2 (2026-11-06): the
    /// verification briefs require it on every dotnet invocation, and Codex's
    /// re-check found the allowlist refusing it. It only stops dotnet starting
    /// or reusing compiler and MSBuild servers, which a <c>--no-build</c> run
    /// should not touch anyway; it cannot set testhost's environment or
    /// settings.
    /// </remarks>
    private static readonly string[] Flags =
        { "--no-build", "--no-restore", "--blame-hang", "--list-tests", "--nologo", "--disable-build-servers" };

    /// <summary>
    /// The one MSBuild property allowed: Platform, with a plain value. Every
    /// other property is refused, because RunSettingsFilePath, VSTestSetting,
    /// VSTestCLIRunSettings, VSTestCollect, VSTestTestAdapterPath and the rest
    /// each change what testhost runs with.
    /// </summary>
    private static readonly Regex PlatformProperty = new(
        "^(?:-p|/p|-property|/property|--property):(?i:Platform)=(?<v>.*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Null when every argument after the project is allowed; otherwise a
    /// sentence naming the first one refused.
    /// </summary>
    public static string? CheckPassThrough(IReadOnlyList<string> rest)
    {
        for (int i = 0; i < rest.Count; i++)
        {
            string a = rest[i];

            if (a == "--")
                return Refuse(a, "inline run settings after a bare -- can set testhost's environment");

            if (Flags.Contains(a, StringComparer.Ordinal))
                continue;

            var platform = PlatformProperty.Match(a);
            if (platform.Success)
            {
                if (!PlatformValue.IsMatch(platform.Groups["v"].Value))
                    return Refuse(a, "Platform must be a plain name such as x64; nothing else may ride along");
                continue;
            }

            bool matched = false;
            foreach (var (names, valid, what) in ValueOptions)
            {
                string? value = null;
                foreach (var name in names)
                {
                    if (a == name)
                    {
                        if (i + 1 >= rest.Count)
                            return Refuse(a, "it needs " + what);
                        value = rest[++i];
                        break;
                    }
                    if (name.StartsWith("--", StringComparison.Ordinal)
                        && (a.StartsWith(name + "=", StringComparison.Ordinal)
                            || a.StartsWith(name + ":", StringComparison.Ordinal)))
                    {
                        value = a.Substring(name.Length + 1);
                        break;
                    }
                }

                if (value == null) continue;

                // ROUND 2, route (a): a value that itself looks like an option.
                // "--filter --environment=X=Y" passed the filter check here as
                // a value, while dotnet's own parser read the second token as
                // --environment (Codex reproduced it). No legitimate value of
                // any of these options starts with a dash or a slash, so
                // refuse rather than guess which parser is right. This is a
                // second layer: the guard no longer consults the declaration
                // on the hidden route at all.
                if (value.Length == 0 || value[0] == '-' || value[0] == '/')
                    return Refuse(a + " " + value,
                                  "its value starts like an option, and dotnet could read it as one");
                if (!valid(value))
                    return Refuse(a + " " + value, "the value must be " + what);
                matched = true;
                break;
            }

            if (!matched)
                return Refuse(a, "it is not on the launcher's allowlist");
        }

        return null;
    }

    private static string Refuse(string argument, string why)
        => "refused argument '" + argument + "': " + why + ". deskrun forwards only "
           + "-c/--configuration, -p:Platform=, --filter, --logger (console or trx), "
           + "--results-directory, --blame-hang, --blame-hang-timeout, --no-build, "
           + "--no-restore, --list-tests, --nologo, --disable-build-servers and "
           + "-v/--verbosity. Anything that "
           + "could set testhost's environment or settings (--environment, --settings, "
           + "other properties, response files, inline settings) is refused.";

    private static bool IsProjectFile(string s)
        => s.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
           || s.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
           || s.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────── the project's own build files ───────────────────────

    /// <summary>
    /// Words in a build file that mean it can choose testhost's settings or
    /// environment: a runsettings file, any VSTest property, or a redirection
    /// of the build's own imports.
    /// </summary>
    private static readonly string[] SettingsWords =
    {
        "runsettings", "VSTest", "RunSettingsFilePath", "CustomBeforeMicrosoft", "CustomAfterMicrosoft",
        "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath",
    };

    /// <summary>
    /// Null when nothing in the project's own build files can choose testhost's
    /// settings; otherwise why the run must not start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MSBuild reads, for the test project: the project file,
    /// <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> from its
    /// folder upward (the nearest of each), and <c>Directory.Build.rsp</c> from
    /// its folder upward, which adds command-line arguments of its own. Any of
    /// those can set a VSTest property or name a runsettings file. None does
    /// today; this refuses the day one does, rather than trusting it.
    /// </para>
    /// <para>
    /// An explicit <c>&lt;Import&gt;</c> in the project file is refused outright,
    /// because what it brings in is not read here.
    /// </para>
    /// </remarks>
    public static string? ProjectSettingsProblem(string projectPath, Func<string, string?> readFile)
    {
        string? projectText = readFile(projectPath);
        if (projectText == null)
            return "The project '" + projectPath + "' could not be read.";

        string? word = FindSettingsWord(projectText);
        if (word != null)
            return "The project file mentions '" + word + "', which can choose testhost's settings or environment.";
        if (projectText.Contains("<Import ", StringComparison.OrdinalIgnoreCase))
            return "The project file has an explicit <Import>, whose contents deskrun does not read. "
                   + "Refusing rather than trusting it.";

        string? dir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        while (!string.IsNullOrEmpty(dir))
        {
            string rsp = Path.Combine(dir, "Directory.Build.rsp");
            if (readFile(rsp) != null)
                return "'" + rsp + "' adds arguments to every build beneath it, and could choose testhost's settings.";

            // Every ancestor's, not only the nearest: the nearest may import
            // the next one up, so the superset is read. Fail closed.
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                string path = Path.Combine(dir, name);
                string? text = readFile(path);
                if (text == null) continue;

                string? w = FindSettingsWord(text);
                if (w != null)
                    return "'" + path + "' mentions '" + w + "', which can choose testhost's settings or environment.";
                if (text.Contains("<Import ", StringComparison.OrdinalIgnoreCase))
                    return "'" + path + "' has an explicit <Import>, whose contents deskrun does not read. "
                           + "Refusing rather than trusting it.";
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static string? FindSettingsWord(string text)
        => SettingsWords.FirstOrDefault(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The arguments given to <c>dotnet</c>: <c>test &lt;project&gt; --no-build</c>
    /// and then everything the caller passed through. <c>--no-build</c> because a
    /// build on a desktop nobody can see has no business happening there, and
    /// because a build leaves compiler and MSBuild servers behind.
    /// </summary>
    public static IReadOnlyList<string> DotnetArguments(Parsed parsed)
    {
        var list = new List<string> { "test", parsed.Project };
        if (!parsed.PassThrough.Any(a => string.Equals(a, "--no-build", StringComparison.OrdinalIgnoreCase)))
            list.Add("--no-build");
        list.AddRange(parsed.PassThrough);
        return list;
    }

    // ─────────────────────────── the child's environment ───────────────────────────

    /// <summary>
    /// Environment variables never passed to the child, by prefix,
    /// case-insensitively. MSBuild reads EVERY environment variable as a
    /// property, so a variable named like a VSTest property selects settings
    /// as surely as <c>-p:</c> does; VSTEST_ variables change how vstest.console
    /// and testhost run; and the startup-hook and profiler variables put code
    /// into testhost, which could then set anything.
    /// </summary>
    private static readonly string[] StrippedPrefixes =
    {
        "VSTest",                     // VSTest* properties and VSTEST_* switches alike
        "RunSettingsFilePath",
        "CustomBeforeMicrosoft", "CustomAfterMicrosoft",
        "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath",
        "ImportDirectoryBuild",
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_ADDITIONAL_DEPS",
        "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER",
        "COR_ENABLE_PROFILING", "COR_PROFILER",
        "DOTNET_PROFILER",
    };

    /// <summary>
    /// The child's environment: the launcher's own, MINUS the desk-free
    /// declaration and every variable that MSBuild or VSTest would read as
    /// settings, plus a diagnostic note of the desktop name and a switch that
    /// stops MSBuild leaving worker nodes alive after the run.
    /// </summary>
    /// <remarks>
    /// Windows environment names are case-insensitive, so every match is
    /// case-insensitive.
    /// </remarks>
    public static SortedDictionary<string, string> ChildEnvironment(IDictionary parent, string announcedDesktop)
        => ChildEnvironment(parent, announcedDesktop, out _);

    /// <inheritdoc cref="ChildEnvironment(IDictionary, string)"/>
    public static SortedDictionary<string, string> ChildEnvironment(
        IDictionary parent, string announcedDesktop, out List<string> removed)
    {
        removed = new List<string>();
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in parent)
        {
            string? key = e.Key as string;
            if (string.IsNullOrEmpty(key)) continue;
            if (string.Equals(key, DeskFreeVariable, StringComparison.OrdinalIgnoreCase)
                || StrippedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                removed.Add(key);
                continue;
            }
            env[key] = e.Value as string ?? "";
        }

        env[AnnouncedDesktopVariable] = announcedDesktop;
        env["MSBUILDDISABLENODEREUSE"] = "1";
        removed.Sort(StringComparer.OrdinalIgnoreCase);
        return env;
    }

    /// <summary>
    /// True when the launcher's own environment carries the declaration, so it
    /// can say plainly that it was removed rather than leave a reader guessing.
    /// </summary>
    public static bool CarriesDeskFree(IDictionary parent)
    {
        foreach (DictionaryEntry e in parent)
            if (e.Key is string k && string.Equals(k, DeskFreeVariable, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// The Unicode environment block CreateProcess wants: <c>name=value</c>
    /// strings, each NUL-terminated, sorted, with a final NUL. (The marshaller
    /// adds the very last NUL.)
    /// </summary>
    public static string EnvironmentBlock(SortedDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var kv in env)
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        return sb.ToString();
    }

    // ─────────────── round 2: what the watchers conclude ───────────────

    /// <summary>Where a sampled top-level window stands.</summary>
    public enum WindowPlace
    {
        /// <summary>Not owned by a process in the job: not ours to judge.</summary>
        NotTheJobs,

        /// <summary>Owned by the job and on the launcher's hidden desktop: where it belongs.</summary>
        OnTheHiddenDesktop,

        /// <summary>Owned by the job and on ANY other desktop. The run is killed at once.</summary>
        Escaped,
    }

    /// <summary>
    /// Judge one top-level window the watcher saw (task #906 round 2,
    /// condition 1). A job window is acceptable on exactly one desktop, the one
    /// this launcher created; every other desktop, the operator's input desktop
    /// above all, and a desktop whose name could not be read, is an escape.
    /// </summary>
    public static WindowPlace JudgeWindow(string? desktopName, string hiddenDesktopName, bool ownedByJob)
    {
        if (!ownedByJob) return WindowPlace.NotTheJobs;
        if (desktopName != null
            && string.Equals(desktopName, hiddenDesktopName, StringComparison.OrdinalIgnoreCase))
            return WindowPlace.OnTheHiddenDesktop;
        return WindowPlace.Escaped;
    }

    /// <summary>
    /// True when an audio session belongs to the job and its meter ever left
    /// zero (task #906 round 2, condition 2). A session from the job whose
    /// peak stayed at exactly zero rendered digital silence and passes: the
    /// measure is what reached the speakers, not whether a device was opened.
    /// </summary>
    public static bool AudibleFromTheJob(bool ownedByJob, float maxPeak) => ownedByJob && maxPeak > 0f;

    /// <summary>A name nobody else can be using: process id and a Guid fragment.</summary>
    public static string NewDesktopName(int processId, Guid guid)
        => "JJFlexDeskRun_" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + "_" + guid.ToString("N").Substring(0, 12);

    /// <summary>
    /// Quote one argument for a Windows command line so that the C runtime's
    /// parser in the child reads back exactly the string given.
    /// </summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.AsSpan().IndexOfAny(NeedsQuoting) < 0)
            return arg;

        var sb = new StringBuilder("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(arg[i]);
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static readonly System.Buffers.SearchValues<char> NeedsQuoting =
        System.Buffers.SearchValues.Create(" \t\n\v\"");

    /// <summary>The whole command line: the program, then each argument quoted.</summary>
    public static string CommandLine(string program, IEnumerable<string> args)
        => string.Join(" ", new[] { Quote(program) }.Concat(args.Select(Quote)));
}

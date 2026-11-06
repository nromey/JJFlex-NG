using System.Collections;
using System.Text;

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

    /// <summary>The arguments, once checked.</summary>
    internal sealed record Parsed(string Project, int TimeoutSeconds, IReadOnlyList<string> PassThrough);

    /// <summary>
    /// Check the command line. Returns null and an error when the run must not
    /// start. The one rule that matters most: there is always exactly one named
    /// project, because a bare <c>dotnet test</c> builds and runs every test
    /// project in the solution, which is how windows reached the operator's
    /// screen twice in August.
    /// </summary>
    public static Parsed? Parse(IReadOnlyList<string> args, Func<string, bool> fileExists, out string error)
    {
        error = "";
        int timeout = DefaultTimeoutSeconds;
        int i = 0;

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

        var rest = new List<string>();
        for (int j = i + 1; j < args.Count; j++)
        {
            string r = args[j];
            if (IsSolutionFile(r) || IsProjectFile(r)
                || string.Equals(r, "--solution", StringComparison.OrdinalIgnoreCase)
                || string.Equals(r, "--project", StringComparison.OrdinalIgnoreCase))
            {
                error = "Only one project may be named, and never a solution: '" + r + "'.";
                return null;
            }
            rest.Add(r);
        }

        return new Parsed(project, timeout, rest);
    }

    private static bool IsProjectFile(string s)
        => s.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
           || s.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
           || s.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsSolutionFile(string s)
        => s.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
           || s.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
           || s.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// The child's environment: the launcher's own, MINUS the desk-free
    /// declaration, plus a diagnostic note of the desktop name and a switch that
    /// stops MSBuild leaving worker nodes alive after the run.
    /// </summary>
    /// <remarks>
    /// Windows environment names are case-insensitive, so the declaration is
    /// removed in every spelling. A run through this launcher can therefore be
    /// allowed by isolation and by nothing else.
    /// </remarks>
    public static SortedDictionary<string, string> ChildEnvironment(IDictionary parent, string announcedDesktop)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in parent)
        {
            string? key = e.Key as string;
            if (string.IsNullOrEmpty(key)) continue;
            if (string.Equals(key, DeskFreeVariable, StringComparison.OrdinalIgnoreCase)) continue;
            env[key] = e.Value as string ?? "";
        }

        env[AnnouncedDesktopVariable] = announcedDesktop;
        env["MSBUILDDISABLENODEREUSE"] = "1";
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

using System.IO;
using System.Runtime.CompilerServices;
using Radios;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// Tier 1 does not touch the operator's settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>This tier constructs every dialog in the application, and until this was
/// written it did so against <c>%AppData%\JJFlexRadio</c> — the real one.</b>
/// Nothing here bound a settings root, and every store in the app self-heals to
/// the live folder when it is not told otherwise. So a sweep could be invisible
/// (<see cref="PrivateDesktop"/>) and silent (<see cref="QuietRun"/>) and still
/// rewrite the operator's configuration on its way past.
/// </para>
/// <para>
/// That harm is not hypothetical and it is already in CLAUDE.md: on 2026-08-21
/// a background agent's worktree build rewrote Noel's <c>KeyDefs.xml</c>, and
/// because no copy existed anywhere, "did that damage anything?" could not be
/// answered even afterwards. An ordinary launch was later measured modifying 17
/// files in that folder; the same launch under <c>JJFLEX_CONFIG_DIR</c>
/// modified 0 of 702.
/// </para>
/// <para>
/// <b>Why the environment variable and not the setters.</b> Several stores hold
/// their folder in a <c>static readonly</c> field evaluated at type load, and
/// <see cref="RadioConfig.AppDataRoot"/> is deliberately resolved from the
/// environment so that order cannot matter. The setters are bound too, because
/// they govern a different root — there are two, and binding one of them is the
/// classic version of this failure: an isolation that truthfully reports itself
/// isolated for the one directory it governs.
/// </para>
/// <para>
/// <b>A module initializer, for the same reason <see cref="QuietRun"/> is
/// one:</b> it has to be true before the first dialog type loads, and being
/// late is the same as not doing it.
/// </para>
/// <para>
/// <b>Sibling, deliberately not shared:</b> <c>Radios.Tests.TestSettingsRoot</c>
/// does the same job for that assembly and additionally owns
/// <c>RadioConfigStaticsScope</c>, which this tier has no use for. The two
/// cannot share an implementation without one test project referencing the
/// other, and pointing <c>Radios.Tests</c> at <c>JJFlexWpf</c> is precisely what
/// Sprint 36 Track B declined to do — it would put every dialog type one
/// careless <c>new</c> away from a project that has no <see cref="DeskGuard"/>.
/// </para>
/// </remarks>
internal static class TestSettingsRoot
{
    /// <summary>The throwaway tree bound for the life of the test process.</summary>
    public static string Directory { get; private set; } = "";

    /// <summary>
    /// Why the operator's settings are still in the blast radius, or null when
    /// they are not. <see cref="DeskGuard"/> refuses the run on anything but
    /// null.
    /// </summary>
    public static string? Failure { get; private set; }

    /// <summary>True when the whole settings tree is confirmed redirected.</summary>
    public static bool Isolated => Failure == null;

    [ModuleInitializer]
    internal static void Bind()
    {
        try
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                "jjflex-tier1-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);

            Environment.SetEnvironmentVariable(RadioConfig.ConfigDirOverrideVariable, Directory);

            RadioConfig.BaseDirectory = Directory;
            KnownRadioRoster.CacheDirectory = Directory;
            Lexicon.OverlayDirectoryOverride = Directory;

            // Read it back. A redirection that reports itself done without
            // being done is the exact shape of the defect this closes, and
            // AppDataRoot caches on first read — so if something in this
            // process had already resolved it, the variable changed nothing
            // and only this comparison would know.
            string actual = RadioConfig.AppDataRoot;
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(actual),
                    Path.TrimEndingDirectorySeparator(Directory),
                    StringComparison.OrdinalIgnoreCase))
            {
                Failure = "RadioConfig.AppDataRoot is '" + actual + "', not the throwaway tree '"
                          + Directory + "'. Dialogs constructed by this tier would read and write "
                          + "that directory.";
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { System.IO.Directory.Delete(Directory, recursive: true); }
                catch (IOException) { /* a temp dir; the OS sweeps it */ }
                catch (UnauthorizedAccessException) { }
            };
        }
        catch (Exception ex)
        {
            Failure = ex.GetType().Name + ": " + ex.Message;
        }

        if (Failure != null)
        {
            Console.Error.WriteLine(
                "TestSettingsRoot: THIS RUN MAY WRITE THE OPERATOR'S SETTINGS. " + Failure);
        }
    }

    /// <summary>One line for the run report, written whether or not it worked.</summary>
    public static string Describe()
        => Isolated
            ? "settings redirected to a throwaway tree (" + Directory + ")"
            : "settings NOT isolated — this run can write the operator's own configuration"
              + (Failure == null ? "" : " (" + Failure + ")");
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Radios.Tests
{
    /// <summary>
    /// The source tree, read from disk, for the integration pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every sweep in the pass reads files rather than types</b>, because
    /// the defects it hunts are not expressible as compiled shapes: a sentence
    /// written twice, a helper nobody reused, an instruction naming a symbol
    /// that does not exist. None of those are visible to reflection and none of
    /// them break a build.
    /// </para>
    /// <para>
    /// <b>The positive control is in the constructor, on purpose.</b> A sweep
    /// over a tree the root-finder failed to locate scans zero files and
    /// reports a clean bill of health, and that reads exactly like success —
    /// the failure mode this whole track exists to refuse. So the static
    /// constructor throws rather than yielding an empty list, and no caller can
    /// opt out of the check by forgetting to write it. See
    /// <c>LexiconKeyCoverageTests</c>, which had to assert
    /// <c>filesScanned &gt; 100</c> by hand in each of its sweeps.
    /// </para>
    /// <para>
    /// <b>Vendored trees are excluded from the authored corpus.</b> FlexLib,
    /// Opus and PortAudioSharp are third-party source drops; duplication inside
    /// them is not ours to resolve and reporting it would bury the findings that
    /// are. They stay in <see cref="AllFiles"/>, because a symbol an
    /// instruction names legitimately lives in vendor code.
    /// </para>
    /// </remarks>
    internal static class IntegrationPassTree
    {
        /// <summary>
        /// Below this, the sweep is assumed to have failed to find the tree.
        /// Measured at 900 source files and 1100 corpus files on 2026-08-26;
        /// the floor is deliberately far below that so ordinary growth and
        /// deletion never trip it, and far above zero so a broken root-finder
        /// always does.
        /// </summary>
        private const int MinimumCorpusFiles = 400;

        /// <summary>Directories that are not ours. Compared against the first
        /// path segment below the repo root.</summary>
        private static readonly string[] VendorRoots =
        {
            "FlexLib_API",                  // vendored FlexRadio API
            "P-Opus-master",                // Opus codec wrapper, third party
            "PortAudioSharp-src-0.19.3",    // PortAudioSharp source drop
            "runtimes",                     // native binaries
            "tools",
        };

        /// <summary>Extensions worth reading. Wider than the source languages:
        /// an instruction naming <c>RootNamespace</c> or <c>cleanupPeriodDays</c>
        /// is naming something real, and it lives in a project file or a JSON
        /// settings file rather than in C#.</summary>
        private static readonly string[] CorpusExtensions =
        {
            ".cs", ".vb", ".xaml", ".csproj", ".vbproj", ".sln", ".props",
            ".targets", ".json", ".xml", ".bat", ".ps1", ".txt", ".resx", ".nsi",
        };

        private static readonly string[] SourceExtensions = { ".cs", ".vb" };

        static IntegrationPassTree()
        {
            Root = FindRoot();

            var corpus = new List<string>();
            var everyName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in TrackedFiles(Root))
            {
                if (IsExcluded(file)) continue;
                everyName.Add(Path.GetFileName(file));
                if (CorpusExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    corpus.Add(file);
            }

            if (corpus.Count < MinimumCorpusFiles)
                throw new InvalidOperationException(
                    "The integration pass found only " + corpus.Count + " files under \"" + Root
                    + "\", which means it did not find the source tree and every sweep built on it "
                    + "would report a clean result having looked at nothing. Expected at least "
                    + MinimumCorpusFiles + ".");

            AllFiles = corpus;
            FileNames = everyName;
            AuthoredSource = corpus
                .Where(f => SourceExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => !IsVendor(f))
                .ToArray();

            if (AuthoredSource.Count < 100)
                throw new InvalidOperationException(
                    "Only " + AuthoredSource.Count + " authored source files were found under \""
                    + Root + "\". Either the vendor exclusions have swallowed the tree or the "
                    + "root-finder is wrong; either way the dedup sweep would prove nothing.");
        }

        /// <summary>
        /// Every file git tracks under <paramref name="root"/>, absolute paths.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Deliberately not a filesystem walk.</b> A walk makes the pass's
        /// verdict depend on which checkout you happen to run in. Found
        /// 2026-08-26 merging Sprint 35: the same commit came back clean in a
        /// worktree and reported 29 findings in the main clone, because that
        /// clone carries a gitignored <c>flexlib4218/</c> reference copy of
        /// FlexLib 4.2.18 in its root. Every finding was FlexLib's own code
        /// compared against our vendored copy — none of them ours.
        /// </para>
        /// <para>
        /// An instrument that answers differently in two checkouts of one
        /// commit cannot be believed in either, and this one is read at
        /// exactly the moment nobody has spare attention to doubt it. Scoping
        /// to <c>git ls-files</c> fixes that by construction, rather than by
        /// an exclusion list somebody has to extend for the next stray
        /// directory after first working out why the gate went red.
        /// </para>
        /// <para>
        /// If git cannot answer, this throws — same reason the corpus floor
        /// throws. A wrong answer here reads as success.
        /// </para>
        /// </remarks>
        private static List<string> TrackedFiles(string root)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "ls-files -z")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            string listing;
            try
            {
                using var git = System.Diagnostics.Process.Start(psi)
                    ?? throw new InvalidOperationException("git did not start");
                listing = git.StandardOutput.ReadToEnd();
                git.WaitForExit();
                if (git.ExitCode != 0)
                    throw new InvalidOperationException("git ls-files exited " + git.ExitCode);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "The integration pass could not ask git which files are tracked under \""
                    + root + "\", so it has no trustworthy corpus. It refuses to fall back to a "
                    + "filesystem walk: that walk reads gitignored trees, and a sweep whose "
                    + "answer depends on what is lying around the checkout is worse than no "
                    + "sweep, because it is read as a verdict. Underlying: " + ex.Message, ex);
            }

            var files = new List<string>();
            foreach (string rel in listing.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string full = Path.GetFullPath(Path.Combine(root, rel));
                if (File.Exists(full)) files.Add(full);
            }
            return files;
        }

        /// <summary>The repo root — the directory holding JJFlexRadio.sln.</summary>
        internal static string Root { get; }

        /// <summary>Every readable file in the tree, vendor included.</summary>
        internal static IReadOnlyList<string> AllFiles { get; }

        /// <summary>Every file name in the tree, vendor and binary included.
        /// Bare-name matching is what lets an instruction say "FlexBase.cs"
        /// without a path and still resolve.</summary>
        internal static IReadOnlySet<string> FileNames { get; }

        /// <summary>C# and VB we wrote. The dedup sweep's corpus.</summary>
        internal static IReadOnlyList<string> AuthoredSource { get; }

        /// <summary>True for a file under a vendored tree.</summary>
        internal static bool IsVendor(string path)
        {
            string rel = Relative(path);
            int cut = rel.IndexOfAny(new[] { '\\', '/' });
            string first = cut < 0 ? rel : rel.Substring(0, cut);
            return VendorRoots.Contains(first, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for a test file. Used to keep test fixtures out of the prose
        /// dedup sweep: a test that quotes the sentence it is asserting on is
        /// doing its job, not duplicating a concept.
        /// </summary>
        internal static bool IsTest(string path)
        {
            string rel = Relative(path).Replace('/', '\\');
            return rel.Contains(".Tests\\", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("TestKit", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Path relative to the repo root, for a readable finding.</summary>
        internal static string Relative(string path)
            => path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(Root.Length).TrimStart('\\', '/')
                : path;

        /// <summary>Read a file, tolerating whatever encoding it carries.</summary>
        internal static string Read(string path) => File.ReadAllText(path);

        /// <summary>The absolute path of a file named relative to the root.</summary>
        internal static string At(string relativePath)
            => Path.Combine(Root, relativePath.Replace('/', '\\'));

        private static bool IsExcluded(string path)
        {
            string p = path.Replace('/', '\\');
            return p.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\.claude\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\packages\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                "Walked up from \"" + AppContext.BaseDirectory + "\" without finding "
                + "JJFlexRadio.sln, so there is no source tree to sweep.");
        }
    }
}

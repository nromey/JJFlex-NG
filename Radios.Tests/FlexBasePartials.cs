using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Radios.Tests
{
    /// <summary>
    /// The source of the <c>FlexBase</c> class, read from disk: <c>Radios/FlexBase.cs</c>
    /// and every <c>Radios/FlexBase.*.cs</c> partial beside it, one entry per file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> FlexBase is one class split across several partial
    /// files, and more of it moves out of <c>FlexBase.cs</c> over time. A source
    /// guard that reads <c>FlexBase.cs</c> by name keeps PASSING when the code it
    /// sweeps moves to a partial — it simply sees fewer sites, and a guard that
    /// watches half as much is still green. Reading the class through here means
    /// a move between partials changes nothing a guard can see.
    /// </para>
    /// <para>
    /// <b>Each file is returned separately, never glued together.</b> A parsing
    /// test must parse each file on its own: concatenated files are not valid C#,
    /// and a <c>#define</c> arriving mid-text is ignored, so every <c>#if</c> block
    /// that depends on it would silently become disabled trivia. A text-searching
    /// test may join the texts itself, which <see cref="JoinedText"/> does.
    /// </para>
    /// <para>
    /// <b>The order is stable:</b> <c>FlexBase.cs</c> first, then the partials in
    /// ordinal order of file name.
    /// </para>
    /// <para>
    /// <b>Some tests read ONE partial on purpose</b> and must keep doing so — for
    /// example a test asserting a name is absent from <c>FlexBase.Capabilities.cs</c>.
    /// Those read their file by name and do not come through here.
    /// </para>
    /// </remarks>
    internal static class FlexBasePartials
    {
        /// <summary>Repository-relative path of the class's original file.</summary>
        public const string MainFile = "Radios/FlexBase.cs";

        /// <summary>
        /// Every file of the FlexBase class, as (repository-relative path with
        /// forward slashes, full text) pairs, <c>FlexBase.cs</c> first and the
        /// partials after it in ordinal order of name.
        /// </summary>
        public static IReadOnlyList<(string Path, string Text)> Files()
        {
            string root = RepoRoot();
            string dir = Path.Combine(root, "Radios");
            string main = Path.Combine(dir, "FlexBase.cs");
            if (!File.Exists(main))
                throw new InvalidOperationException(main + " is missing, so every FlexBase source guard would read nothing.");

            var partials = Directory.GetFiles(dir, "FlexBase.*.cs", SearchOption.TopDirectoryOnly)
                .Select(p => Path.GetFileName(p))
                .OrderBy(n => n, StringComparer.Ordinal);

            var files = new List<(string Path, string Text)> { (MainFile, File.ReadAllText(main)) };
            foreach (string name in partials)
                files.Add(("Radios/" + name, File.ReadAllText(Path.Combine(dir, name))));
            return files;
        }

        /// <summary>
        /// Every file's text joined with a newline, in <see cref="Files"/> order —
        /// for tests that search text. Never parse this: see the class remarks.
        /// </summary>
        public static string JoinedText() => string.Join("\n", Files().Select(f => f.Text));

        /// <summary>The repository root: the directory holding <c>JJFlexRadio.sln</c>.</summary>
        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException("JJFlexRadio.sln was not found above " + AppContext.BaseDirectory);
            return dir.FullName;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JJFlex.TxFactAudit
{
    /// <summary>
    /// One run of JJ Flexible, as its own trace file declares itself.
    /// </summary>
    public sealed record TraceSessionFile
    {
        public required string Path { get; init; }

        /// <summary>Which copy of the app is running: 1 for the first instance,
        /// higher for the ones that write JJFlexRadio2Trace and friends.</summary>
        public required int Instance { get; init; }

        /// <summary>The assembly the running app was loaded from. This is what
        /// says WHICH WORKTREE'S BUILD wrote the file, and it is the only
        /// reliable way to tell one track's session from another's.</summary>
        public required string AssemblyPath { get; init; }

        public required string Version { get; init; }

        /// <summary>When the session started, taken from the header the app
        /// wrote — NOT from the file's timestamps. See the note on
        /// <see cref="TraceSessions.Discover"/> for why that distinction is
        /// load-bearing.</summary>
        public required DateTime StartedAt { get; init; }

        /// <summary>The trace level the session booted at. Verbose is the one
        /// that carries per-meter lines; Info carries only the correlated
        /// transmit snapshot.</summary>
        public required string Level { get; init; }

        /// <summary>True for the fixed-name live log, which exists while the app
        /// runs and is archived under a stamped name when it exits.</summary>
        public required bool IsLiveName { get; init; }

        public bool IsVerbose =>
            string.Equals(Level, "Verbose", StringComparison.OrdinalIgnoreCase);

        /// <summary>The worktree or install directory the build came from.</summary>
        public string BuildRoot
        {
            get
            {
                // ...\<root>\bin\x64\Debug\net10.0-windows\win-x64\jjflexible.dll
                string dir = System.IO.Path.GetDirectoryName(AssemblyPath) ?? "";
                int bin = dir.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase);
                return bin > 0 ? dir[..bin] : dir;
            }
        }

        public string Describe()
        {
            string live = IsLiveName ? ", and this is the live fixed-name log" : "";
            return string.Format(CultureInfo.InvariantCulture,
                "{0} — instance {1}, version {2}, level {3}, started {4:yyyy-MM-dd HH:mm:ss}, built from {5}{6}",
                System.IO.Path.GetFileName(Path), Instance, Version, Level, StartedAt, BuildRoot, live);
        }
    }

    /// <summary>
    /// Finds the trace file a given build actually wrote.
    ///
    /// <para><b>Why this is not "the newest file in the folder".</b> Track B
    /// found exactly that heuristic attaching to another track's dead session,
    /// and the reason is worse than a tie-break: Windows does not reliably
    /// update a directory entry while a file is held open, so the LIVE log —
    /// the one being written to right now — can sort BELOW one closed twenty
    /// minutes earlier. Modification time is not merely a weak signal here, it
    /// is actively wrong in the one case that matters.</para>
    ///
    /// <para>So sessions are identified by the header the app writes about
    /// itself: instance number, the assembly it loaded from, its version, its
    /// start time and its trace level. The assembly path is what separates one
    /// worktree's build from another's, and on a machine running eleven tracks
    /// at once that is the whole problem. Ordering is by the START TIME IN THE
    /// HEADER, which the app stated and nothing later rewrites.</para>
    /// </summary>
    public static class TraceSessions
    {
        private static readonly Regex Header = new(
            @"Boot Tracing on instance:(?<instance>\d+)\s+(?<asm>.+?\.dll)\s+(?<ver>[\d.]+)\s+(?<when>.+?)\s+level=(?<level>\w+)",
            RegexOptions.Compiled);

        /// <summary>
        /// The folder the app writes traces to. Note the app keeps its
        /// JJFlexRadio identity here on purpose even though the executable was
        /// renamed to jjflexible.
        /// </summary>
        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "JJFlexRadio");

        /// <summary>
        /// Every trace file that declares a session, newest session first by the
        /// app's own start time.
        /// </summary>
        public static IReadOnlyList<TraceSessionFile> Discover(string? directory = null)
        {
            directory ??= DefaultDirectory;
            var found = new List<TraceSessionFile>();
            if (!Directory.Exists(directory)) return found;

            foreach (string path in Directory.EnumerateFiles(directory, "*Trace*.txt"))
            {
                TraceSessionFile? session = ReadHeader(path);
                if (session is not null) found.Add(session);
            }

            return found.OrderByDescending(s => s.StartedAt).ToList();
        }

        /// <summary>
        /// Reads the self-declaration at the top of a trace. Returns null for a
        /// file that carries no header — a rotated part file, a stray log —
        /// rather than guessing at what it might be.
        /// </summary>
        public static TraceSessionFile? ReadHeader(string path)
        {
            try
            {
                // Share every access: the live log is held open for writing by
                // the running app, and opening it any other way fails outright.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                // The header is written early but not necessarily first.
                for (int line = 0; line < 400; line++)
                {
                    string? text = reader.ReadLine();
                    if (text is null) break;

                    Match m = Header.Match(text);
                    if (!m.Success) continue;

                    if (!DateTime.TryParse(m.Groups["when"].Value, CultureInfo.CurrentCulture,
                                           DateTimeStyles.None, out DateTime started)
                        && !DateTime.TryParse(m.Groups["when"].Value, CultureInfo.InvariantCulture,
                                              DateTimeStyles.None, out started))
                    {
                        // A header we cannot date is a header we cannot order,
                        // and ordering is the entire job. Fall back to the file
                        // system only here, and only having said so.
                        started = File.GetCreationTime(path);
                    }

                    return new TraceSessionFile
                    {
                        Path = path,
                        Instance = int.Parse(m.Groups["instance"].Value, CultureInfo.InvariantCulture),
                        AssemblyPath = m.Groups["asm"].Value.Trim(),
                        Version = m.Groups["ver"].Value,
                        StartedAt = started,
                        Level = m.Groups["level"].Value,
                        IsLiveName = Path.GetFileName(path)
                            .Equals("JJFlexRadioTrace.txt", StringComparison.OrdinalIgnoreCase),
                    };
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return null;
        }

        /// <summary>
        /// The most recent session written by a build under
        /// <paramref name="buildRoot"/>, or null.
        ///
        /// <para>Matching on the build root rather than taking the newest file
        /// is the whole point: on this machine several worktrees run their own
        /// build of the same app into the same AppData folder, so "the newest
        /// trace" and "the trace my build wrote" are routinely different files.</para>
        /// </summary>
        public static TraceSessionFile? ForBuild(string buildRoot, string? directory = null)
        {
            string wanted = Path.GetFullPath(buildRoot).TrimEnd('\\');
            return Discover(directory).FirstOrDefault(s =>
                s.BuildRoot.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        }
    }
}

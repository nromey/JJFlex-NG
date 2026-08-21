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

        /// <summary>The trace level the session booted at. In traces from
        /// before 2026-08-21 Verbose is the level that carried the raw
        /// per-meter lines; since task #170 those lines follow the operator's
        /// "Record the meter stream" switch instead and no level brings them
        /// back. Every level carries the correlated transmit snapshot.</summary>
        public required string Level { get; init; }

        /// <summary>True for the fixed-name live log, which exists while the app
        /// runs and is archived under a stamped name when it exits.</summary>
        public required bool IsLiveName { get; init; }

        /// <summary>
        /// True when this file is a DETAILED CAPTURE rather than a boot session.
        /// <para>A capture is started mid-run by Ctrl+J Ctrl+D and declares
        /// itself with "Detailed capture started ... level=Verbose". It carries
        /// NO "Boot Tracing on instance" line, because no boot happened - so
        /// anything that recognises only the boot header skips the one file
        /// containing the operator's actual reproduction.</para>
        /// </summary>
        public bool IsCapture { get; init; }

        /// <summary>True when a capture has no matching stop line, meaning it was
        /// still recording when we read it. Its tail may not be flushed, so a
        /// line's ABSENCE proves nothing about the radio.</summary>
        public bool StillRecording { get; init; }

        /// <summary>How the build was established. A capture carries no assembly
        /// path, so it is attributed to the boot session that was running when it
        /// started - an inference, and it says so rather than implying certainty.</summary>
        public string Attribution { get; init; } = "from its own boot header";

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
                "{0} — {1}instance {2}, version {3}, level {4}, started {5:yyyy-MM-dd HH:mm:ss}, " +
                "built from {6} ({7}){8}{9}",
                System.IO.Path.GetFileName(Path), IsCapture ? "DETAILED CAPTURE, " : "", Instance,
                Version, Level, StartedAt, BuildRoot, Attribution, live,
                StillRecording ? ", STILL RECORDING" : "");
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
        /// <summary>
        /// A Detailed capture's own opening line. It is NOT the boot header and
        /// shares none of its fields - no instance, no assembly path, no version.
        /// </summary>
        private static readonly Regex CaptureHeader = new(
            @"Detailed capture started\s+(?<when>\S+)\s+reason=(?<reason>.*?)\s+level=(?<level>\w+)",
            RegexOptions.Compiled);

        private static readonly Regex CaptureStopped = new(
            @"Detailed capture stopped", RegexOptions.Compiled);

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

            var captures = new List<TraceSessionFile>();
            foreach (string path in Directory.EnumerateFiles(directory, "*Trace*.txt"))
            {
                TraceSessionFile? session = ReadHeader(path);
                if (session is not null) { found.Add(session); continue; }

                // No boot header. Before giving up - which is exactly what
                // skipped the ONLY file containing the operator's reproduction
                // on 2026-08-20 - check whether it is a Detailed capture, which
                // declares itself differently because no boot happened.
                TraceSessionFile? capture = ReadCaptureHeader(path);
                if (capture is not null) captures.Add(capture);
            }

            // A capture names no assembly, so attribute it to the boot session
            // that was running when it started: the latest one begun before it.
            foreach (TraceSessionFile c in captures)
            {
                TraceSessionFile? host = found
                    .Where(b => !b.IsCapture && b.StartedAt <= c.StartedAt)
                    .OrderByDescending(b => b.StartedAt)
                    .FirstOrDefault();

                found.Add(host is null
                    ? c with { Attribution = "no boot session precedes it, so the build is UNKNOWN" }
                    : c with
                    {
                        AssemblyPath = host.AssemblyPath,
                        Instance = host.Instance,
                        Version = host.Version,
                        Attribution = "attributed by time to the session that was running when it started",
                    });
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
        /// Reads a Detailed capture's self-declaration, and whether it is still
        /// recording. Fields a capture does not carry are left blank for the
        /// caller to fill in by attribution.
        /// </summary>
        public static TraceSessionFile? ReadCaptureHeader(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                TraceSessionFile? capture = null;
                bool stopped = false;

                string? text;
                while ((text = reader.ReadLine()) is not null)
                {
                    if (capture is null)
                    {
                        Match m = CaptureHeader.Match(text);
                        if (m.Success)
                        {
                            DateTime.TryParse(m.Groups["when"].Value, CultureInfo.InvariantCulture,
                                              DateTimeStyles.RoundtripKind, out DateTime started);
                            capture = new TraceSessionFile
                            {
                                Path = path,
                                Instance = 0,
                                AssemblyPath = "",
                                Version = "",
                                StartedAt = started,
                                Level = m.Groups["level"].Value,
                                IsLiveName = System.IO.Path.GetFileName(path)
                                    .Equals("JJFlexRadioTrace.txt", StringComparison.OrdinalIgnoreCase),
                                IsCapture = true,
                            };
                            continue;
                        }

                        // Only the opening of the file is worth scanning for a
                        // start line; a multi-megabyte capture should not be
                        // read end to end just to decide it is not one.
                        if (reader.BaseStream.Position > 200_000) break;
                    }
                    else if (CaptureStopped.IsMatch(text))
                    {
                        stopped = true;
                        break;
                    }
                }

                return capture is null ? null : capture with { StillRecording = !stopped };
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

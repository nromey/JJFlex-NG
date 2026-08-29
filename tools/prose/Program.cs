using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Prose;

/// <summary>
/// The round trip: source to an editing file, and back.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // The words this tool reports on have em dashes and curly quotes in
        // them, and a refusal that names a sentence has to be able to quote it
        // back. A console left on the ANSI code page turns those into
        // question marks.
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
        return Run(args, Console.Out, Console.Error);
    }

    public static int Run(string[] args, TextWriter outp, TextWriter err)
    {
        string command = args.FirstOrDefault() ?? "help";
        bool force = args.Contains("--force");
        string surfaceName = ArgAfter(args, "--surface") ?? "fixer";
        string? rootOverride = ArgAfter(args, "--root");

        if (command is "help" or "-h" or "--help")
        {
            Usage(outp);
            return 0;
        }

        try
        {
            string root = rootOverride ?? FindRepoRoot();
            var session = new Session(root, surfaceName);

            return command switch
            {
                "extract" => session.Extract(outp, err, force),
                "read" => session.ReadAloud(outp),
                "check" => session.Apply(outp, err, write: false, force),
                "apply" => session.Apply(outp, err, write: true, force),
                "skipped" => session.Skipped(outp),
                _ => Unknown(command, err),
            };
        }
        catch (Exception ex)
        {
            err.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Unknown(string command, TextWriter err)
    {
        err.WriteLine("There is no command called '" + command + "'. The commands are "
                    + "extract, read, check, apply and skipped.");
        return 2;
    }

    private static void Usage(TextWriter o)
    {
        o.WriteLine("prose — edit the words the application says as writing, not as code.");
        o.WriteLine();
        o.WriteLine("  prose extract   Pull the words out into the editing file.");
        o.WriteLine("  prose read      Write a listen-through file: sentences only.");
        o.WriteLine("  prose check     Say what applying would do. Writes nothing.");
        o.WriteLine("  prose apply     Put the edited words back into the code.");
        o.WriteLine("  prose skipped   List the strings this tool left behind, and why.");
        o.WriteLine();
        o.WriteLine("  --surface NAME  Which surface (default: fixer).");
        o.WriteLine("  --force         extract: discard unapplied edits.");
        o.WriteLine("                  apply:   write even where the code has moved on.");
        o.WriteLine("  --root PATH     Use this repository instead of finding one.");
    }

    private static string? ArgAfter(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Walk up for the solution file, so the tool runs from anywhere.</summary>
    private static string FindRepoRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? d = new(start); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) return d.FullName;
            }
        }
        throw new InvalidOperationException(
            "Could not find the repository — no JJFlexRadio.sln above this folder. Run this "
            + "from inside a working copy, or pass --root with the path to one.");
    }
}

/// <summary>One run of the tool against one surface.</summary>
public sealed class Session
{
    private readonly string _root;
    private readonly Surface _surface;
    private readonly List<Entry> _entries = [];
    private readonly List<Refusal> _skipped = [];
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);

    public Session(string root, string surfaceName)
    {
        _root = root;

        string path = Path.Combine(root, "tools", "prose", "surfaces", surfaceName + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException("There is no surface called '" + surfaceName
                                          + "'. Expected " + path + ".");
        _surface = Surface.Load(path);

        var reader = new CSharpSource(_surface);

        // Learn the constants and the constructor parameter names FIRST, from
        // every file including the ones nothing is extracted from — a key like
        // fixer.finding.mme-in-use.what-is-wrong needs both, and they are
        // declared in files that hold no prose of their own.
        foreach (string rel in _surface.Context.Concat(_surface.Files.Select(f => f.Path)))
            reader.LearnFrom(Read(rel));

        foreach (SurfaceFile file in _surface.Files)
            _entries.AddRange(reader.Read(file.Path, Read(file.Path), file, _skipped));

        SettleKeys(_entries);
    }

    /// <summary>
    /// Give every entry the shortest key nothing else wants.
    /// </summary>
    /// <remarks>
    /// Order-independent on purpose. Handing out keys first-come-first-served
    /// would mean two entries that collide swap keys the day their order in
    /// the file changes — and an editor part-way through a re-word would find
    /// his edits land on each other's sentences. So a collision moves EVERY
    /// entry in it to the next candidate together, and only a set that still
    /// cannot be told apart falls back to numbering.
    /// </remarks>
    internal static void SettleKeys(List<Entry> entries)
    {
        var pending = new List<Entry>(entries);

        for (int round = 0; pending.Count > 0 && round < 6; round++)
        {
            var next = new List<Entry>();

            foreach (IGrouping<string, Entry> g in pending
                         .GroupBy(e => e.KeyCandidates[Math.Min(round, e.KeyCandidates.Count - 1)],
                                  StringComparer.Ordinal))
            {
                List<Entry> members = g.ToList();
                if (members.Count == 1)
                {
                    members[0].Key = g.Key;
                    continue;
                }

                bool anyLeft = members.Any(e => e.KeyCandidates.Count > round + 1);
                if (anyLeft) { next.AddRange(members); continue; }

                // Genuinely indistinguishable: number them in source order and
                // let the headings tell them apart.
                for (int i = 0; i < members.Count; i++)
                    members[i].Key = i == 0 ? g.Key : g.Key + "." + (i + 1);
            }

            pending = next;
        }

        foreach (Entry e in pending.Where(e => e.Key.Length == 0))
            e.Key = e.KeyCandidates[^1];
    }

    private string Read(string rel)
    {
        if (_sources.TryGetValue(rel, out string? cached)) return cached;
        string full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
            throw new FileNotFoundException("The surface names " + rel + ", which is not "
                                          + "in this working copy.");
        string text = File.ReadAllText(full, Encoding.UTF8);
        _sources[rel] = text;
        return text;
    }

    private string EditingPath => Path.Combine(_root,
        _surface.EditingFile.Replace('/', Path.DirectorySeparatorChar));

    private string ReadingPath => Path.Combine(_root,
        _surface.ReadingFile.Replace('/', Path.DirectorySeparatorChar));

    // ────────────────────────────────────────────────────────────────

    public int Extract(TextWriter o, TextWriter err, bool force)
    {
        // Never quietly overwrite work in progress. Re-extracting on top of
        // unapplied edits would destroy them with nothing to show for it.
        if (!force && File.Exists(EditingPath))
        {
            EditingFile.Parsed old = EditingFile.Parse(File.ReadAllText(EditingPath, Encoding.UTF8));
            int pending = old.Texts.Count(kv =>
                old.Fingerprints.TryGetValue(kv.Key, out string? f)
                && Entry.Hash(kv.Value) != f);

            if (pending > 0)
            {
                err.WriteLine(EditingPath + " has " + pending + " edit"
                    + (pending == 1 ? "" : "s") + " in it that have not been applied yet. "
                    + "Run `prose apply` to put them into the code first, or "
                    + "`prose extract --force` to throw them away and start again.");
                return 3;
            }
        }

        string stamp = "Made " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " · "
                     + _entries.Count + " sentences · " + _surface.Files.Count + " files";

        Write(EditingPath, EditingFile.Render(_surface, _entries, stamp));
        Write(ReadingPath, EditingFile.RenderForReading(_surface, _entries));

        o.WriteLine(_entries.Count + " sentences from " + _surface.Files.Count + " files.");
        o.WriteLine("Edit:   " + _surface.EditingFile);
        o.WriteLine("Listen: " + _surface.ReadingFile);
        if (_skipped.Count > 0)
            o.WriteLine(_skipped.Count + " string" + (_skipped.Count == 1 ? " was" : "s were")
                      + " left in the code — `prose skipped` says which and why.");
        o.WriteLine("When you are done editing, run: prose apply");
        return 0;
    }

    public int ReadAloud(TextWriter o)
    {
        Write(ReadingPath, EditingFile.RenderForReading(_surface, _entries));
        o.WriteLine(_entries.Count + " sentences written to " + _surface.ReadingFile + ".");
        return 0;
    }

    public int Skipped(TextWriter o)
    {
        if (_skipped.Count == 0)
        {
            o.WriteLine("Nothing was left behind.");
            return 0;
        }
        foreach (Refusal r in _skipped) o.WriteLine(r.ToString());
        return 0;
    }

    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Put the edited words back. Nothing is written unless everything can be
    /// written: a refusal anywhere stops the whole run, because a half-applied
    /// edit is worse than none at all.
    /// </summary>
    public int Apply(TextWriter o, TextWriter err, bool write, bool force)
    {
        if (!File.Exists(EditingPath))
        {
            err.WriteLine("There is no editing file at " + _surface.EditingFile
                        + ". Run `prose extract` first.");
            return 3;
        }

        EditingFile.Parsed edited =
            EditingFile.Parse(File.ReadAllText(EditingPath, Encoding.UTF8));
        Dictionary<string, Entry> current =
            _entries.ToDictionary(e => e.Key, StringComparer.Ordinal);

        var refusals = new List<Refusal>();
        var conflicts = new List<Refusal>();
        var gone = new List<string>();
        var byFile = new Dictionary<string, List<Splice>>(StringComparer.Ordinal);
        var changed = new List<Entry>();

        foreach ((string key, string text) in edited.Texts)
        {
            if (!current.TryGetValue(key, out Entry? entry)) { gone.Add(key); continue; }
            if (!edited.Fingerprints.TryGetValue(key, out string? was)) { gone.Add(key); continue; }

            // Unchanged by the operator: leave the code alone, whatever has
            // happened to it since. That is what lets somebody else edit the
            // same file while this file is open.
            if (Entry.Hash(text) == was) continue;

            // Changed by the operator AND changed in the code: nobody can
            // decide that but a person.
            if (entry.Fingerprint != was && !force)
            {
                conflicts.Add(new Refusal(key,
                    "You changed these words, and so did the code, since this file was "
                    + "made. The code now says: " + entry.Text));
                continue;
            }

            string newline = Read(entry.File).Contains("\r\n") ? "\r\n" : "\n";
            IReadOnlyList<Splice> splices =
                CSharpSource.Splices(entry, text, newline, out Refusal? refusal);

            if (refusal != null) { refusals.Add(refusal); continue; }

            if (!byFile.TryGetValue(entry.File, out List<Splice>? l)) byFile[entry.File] = l = [];
            l.AddRange(splices);
            changed.Add(entry);
        }

        foreach (string key in gone)
            o.WriteLine("Left alone: " + key + " is no longer in the code under that name.");

        if (edited.Orphans.Count > 0)
            foreach (string h in edited.Orphans)
                o.WriteLine("Left alone: the section \"" + h + "\" has no name line, so there "
                          + "is nothing to write it back to.");

        if (conflicts.Count > 0 || refusals.Count > 0)
        {
            err.WriteLine("Nothing was written. " + (conflicts.Count + refusals.Count)
                        + " sentence" + (conflicts.Count + refusals.Count == 1 ? "" : "s")
                        + " cannot be applied as they stand:");
            err.WriteLine();
            foreach (Refusal r in refusals) { err.WriteLine(r.Key); err.WriteLine("  " + r.Message); }
            foreach (Refusal r in conflicts) { err.WriteLine(r.Key); err.WriteLine("  " + r.Message); }
            if (conflicts.Count > 0)
                err.WriteLine();
            if (conflicts.Count > 0)
                err.WriteLine("For the ones the code has moved on from: re-read the new wording, "
                            + "then either re-do your edit on top of it after `prose extract "
                            + "--force`, or run `prose apply --force` to keep yours.");
            return 4;
        }

        if (changed.Count == 0)
        {
            o.WriteLine("No changes. The code already says exactly what this file says.");
            return 0;
        }

        // Build every file in memory and PARSE it before a byte reaches disk.
        // A rewritten literal that does not compile must never exist on the
        // operator's machine, not even for the moment before the tool notices.
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string file, List<Splice> splices) in byFile)
        {
            string source = Read(file);
            string updated = Apply(source, splices);

            SyntaxTree tree = CSharpSyntaxTree.ParseText(updated);
            List<Diagnostic> errors = tree.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

            if (errors.Count > 0)
            {
                err.WriteLine("Nothing was written. Putting these words back into " + file
                            + " would not have produced valid code — " + errors[0].GetMessage()
                            + " This is a fault in the tool, not in your writing; please say "
                            + "which sentence you were editing.");
                return 5;
            }

            results[file] = updated;
        }

        if (!write)
        {
            o.WriteLine(changed.Count + " sentence" + (changed.Count == 1 ? "" : "s")
                      + " would be written, across " + results.Count + " file"
                      + (results.Count == 1 ? "" : "s") + ":");
            foreach (Entry e in changed) o.WriteLine("  " + e.Key);
            return 0;
        }

        foreach ((string file, string text) in results)
        {
            string full = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(full, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        o.WriteLine(changed.Count + " sentence" + (changed.Count == 1 ? "" : "s")
                  + " written, across " + results.Count + " file"
                  + (results.Count == 1 ? "" : "s") + ".");
        foreach (Entry e in changed) o.WriteLine("  " + e.Key);
        o.WriteLine("Build before you trust it, then run `prose extract` again so this file "
                  + "matches the code.");
        return 0;
    }

    /// <summary>Splice replacements in from the back, so earlier offsets hold.</summary>
    public static string Apply(string source, IReadOnlyList<Splice> splices)
    {
        var sb = new StringBuilder(source);
        foreach (Splice s in splices.OrderByDescending(s => s.Span.Start)
                                    .ThenByDescending(s => s.Span.Length))
        {
            sb.Remove(s.Span.Start, s.Span.Length);
            sb.Insert(s.Span.Start, s.Text);
        }
        return sb.ToString();
    }

    private static void Write(string path, string text)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text.Replace("\n", "\r\n"),
                          new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>For tests: the entries as extracted.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>For tests: what was left behind, and why.</summary>
    public IReadOnlyList<Refusal> SkippedStrings => _skipped;

    public Surface SurfaceDefinition => _surface;
}

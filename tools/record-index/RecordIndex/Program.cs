using System.Diagnostics;
using System.Text.Json;
using JJFlex.RecordIndex;
using JJFlex.RecordIndex.Crypto;
using JJFlex.RecordIndex.Extract;
using JJFlex.RecordIndex.Search;

// record-index: an encrypted, searchable index of the JJ Flexible historical record (#854).
// Output is plain lines for a screen reader: no tables, no box drawing.

var prompt = new ConsolePrompter();
try
{
    return Cli.Run(args, prompt);
}
catch (Exception e) when (e is UnlockException or IOException or FormatException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
{
    prompt.Say(e.Message);
    return 1;
}

internal static class Cli
{
    private const string Usage = """
        record-index: search the historical record without unpacking it.

        Searching and reading
          record-index search WORDS [--limit 10] [--kind KIND] [--since DATE] [--until DATE] [--path TEXT] [--any] [--fts] [--json]
              With no words, asks for one search after another until you press Enter on an empty line,
              so one unlock serves many searches.
          record-index extract ARCHIVE MEMBER --out FILE
          record-index extract ARCHIVE MEMBER --line N [--context 5] [--raw]
              Needs no key: it reads the record directly.

        Keeping it current
          record-index catch-up [--only SUBFOLDER] [--limit N] [--dry-run]
          record-index add ARCHIVE        (the seal's hook: stages one archive, needs no key)
          record-index merge
          record-index gc
          record-index status [--full]

        Keys
          record-index init [--recovery-to clipboard|file:PATH] [--yubikey [--slot 9d] [--existing] [--touch always|cached|never]]
          record-index keys list
          record-index keys add-recovery [--recovery-to clipboard|file:PATH]
          record-index keys add-yubikey [--slot 9d] [--existing] [--touch always|cached|never]
          record-index keys remove ID
          record-index keys rotate

        Everywhere
          --home FOLDER     where the index lives (default \\nas.macaw-jazz.ts.net\jjflex\record-index,
                            or the JJFLEX_RECORD_INDEX_HOME environment variable)
          --record FOLDER   the historical record (default \\nas.macaw-jazz.ts.net\jjflex\historical)
          --unlock auto|yubikey|recovery
          --recovery-key-from prompt|clipboard|file:PATH
        """;

    public static int Run(string[] argv, IPrompter prompt)
    {
        var a = new Args(argv);
        if (a.Positional.Count == 0 || a.Flag("help") || a.Positional[0] is "help" or "-h" or "/?")
        {
            Console.WriteLine(Usage);
            return a.Positional.Count == 0 ? 2 : 0;
        }
        var home = new IndexHome(a.Opt("home") ?? Environment.GetEnvironmentVariable("JJFLEX_RECORD_INDEX_HOME") ?? IndexHome.DefaultHome);
        var record = a.Opt("record") ?? IndexHome.DefaultRecord;
        var unlock = new UnlockOptions
        {
            Method = a.Opt("unlock") ?? "auto",
            RecoverySource = a.Opt("recovery-key-from") ?? "prompt",
        };

        switch (a.Positional[0])
        {
            case "init":
                home.Init(new IndexHome.InitOptions
                {
                    RecordRoot = record,
                    RecoveryTo = a.Opt("recovery-to") ?? "clipboard",
                    WithYubiKey = a.Flag("yubikey"),
                    Slot = PivSlots.Parse(a.Opt("slot") ?? "9d"),
                    UseExistingKey = a.Flag("existing"),
                    Touch = ParseTouch(a.Opt("touch")),
                }, unlock, prompt);
                prompt.Say("Next: record-index catch-up, to build the index from the record.");
                return 0;

            case "search":
                return Search(a, home, unlock, prompt);

            case "extract":
                return Extract(a, record);

            case "catch-up":
            {
                using var ix = home.Open(unlock, prompt);
                var limit = a.Opt("limit") is string l ? int.Parse(l) : (int?)null;
                var rep = ix.CatchUp(prompt, a.Opt("only"), limit, a.Flag("dry-run"));
                if (rep.Indexed > 0 && !a.Flag("dry-run") && limit is null) { prompt.Say("Optimizing the full-text index."); ix.Optimize(); }
                prompt.Say($"Done in {Minutes(rep.Elapsed)}. {rep.Indexed} archives indexed, {rep.Unchanged} unchanged, {rep.Removed} dropped, {rep.Merged} merged from staging, {rep.Failed} failed. Read {rep.BytesRead / 1048576:N0} MB of members; {rep.NewChunks:N0} new chunks.");
                return rep.Failed > 0 ? 3 : 0;
            }

            case "add":
            {
                if (a.Positional.Count < 2) throw new ArgumentException("Name the archive to add.");
                home.Stage(record, a.Positional[1], prompt);
                return 0;
            }

            case "merge":
            {
                using var ix = home.Open(unlock, prompt);
                int n = ix.Merge(prompt);
                prompt.Say(n == 0 ? "Nothing was staged." : $"Merged {n} staged archive{(n == 1 ? "" : "s")}.");
                return 0;
            }

            case "gc":
            {
                using var ix = home.Open(unlock, prompt);
                var (b, c) = ix.CollectGarbage();
                prompt.Say($"Dropped {b} unused member contents and {c} unused chunks.");
                return 0;
            }

            case "status":
                return Status(a, home, record, unlock, prompt);

            case "keys":
                return Keys(a, home, unlock, prompt);

            default:
                prompt.Say($"Unknown command {a.Positional[0]}. Run record-index help.");
                return 2;
        }
    }

    private static int Search(Args a, IndexHome home, UnlockOptions unlock, IPrompter prompt)
    {
        var words = string.Join(' ', a.Positional.Skip(1));
        bool json = a.Flag("json");
        using var ix = home.Open(unlock, prompt, readOnly: true);
        int RunOne(string text)
        {
            var q = new SearchQuery
            {
                Text = text,
                Limit = a.Opt("limit") is string l ? int.Parse(l) : 10,
                Kind = a.Opt("kind"),
                Since = a.Opt("since"),
                Until = a.Opt("until"),
                PathContains = a.Opt("path"),
                Raw = a.Flag("fts"),
                Any = a.Flag("any"),
            };
            var res = Searcher.Search(ix.Db, q);
            if (json) { Console.WriteLine(JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true })); return res.Hits.Count; }
            Console.WriteLine(res.Hits.Count == 0
                ? $"Nothing found for {text}, in {res.ElapsedMs} milliseconds."
                : $"{res.Hits.Count} result{(res.Hits.Count == 1 ? "" : "s")} for {text}, in {res.ElapsedMs} milliseconds.");
            foreach (var h in res.Hits)
            {
                var n = h.Newest;
                Console.WriteLine();
                Console.WriteLine($"{h.Rank}. {n.ArchivePath}, dated {n.ArchiveDate}.");
                Console.WriteLine($"   Member {n.MemberPath}, {Describe(n.MemberKind)}, line {n.Line:N0}.");
                if (h.Timestamp is not null) Console.WriteLine($"   Written around {h.Timestamp}.");
                if (h.Archives > 1) Console.WriteLine($"   Also in {h.Archives - 1} other archive{(h.Archives == 2 ? "" : "s")}; first seen in {h.Oldest.ArchivePath}, dated {h.Oldest.ArchiveDate}.");
                Console.WriteLine($"   {h.Snippet}");
                Console.WriteLine($"   To read it: record-index extract \"{n.ArchivePath}\" \"{n.MemberPath}\" --line {n.Line}");
            }
            return res.Hits.Count;
        }

        if (words.Length > 0) return RunOne(words) > 0 ? 0 : 4;
        prompt.Say("The index is open. Type a search and press Enter. Press Enter on an empty line to finish and lock it again.");
        while (true)
        {
            Console.Error.Write("Search: ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            try { RunOne(line); }
            catch (Exception e) when (e is ArgumentException or Microsoft.Data.Sqlite.SqliteException) { prompt.Say(e.Message); }
        }
        return 0;
    }

    private static string Describe(string kind) => kind switch
    {
        "transcript" => "a session transcript",
        "memory-entry" => "a memory entry",
        "trace" => "a JJ Flexible trace",
        "nvda-log" => "an NVDA log",
        "build-notes" => "build notes",
        "test-results" => "test results",
        "config" => "a settings file",
        "source" => "source code",
        "markdown" => "a document",
        _ => "a " + kind + " file",
    };

    private static int Extract(Args a, string record)
    {
        if (a.Positional.Count < 3) throw new ArgumentException("Name the archive and the member: record-index extract ARCHIVE MEMBER --out FILE, or --line N.");
        var archive = a.Positional[1]; var member = a.Positional[2];
        if (a.Opt("line") is string ls)
        {
            long line = long.Parse(ls);
            int ctx = a.Opt("context") is string c ? int.Parse(c) : 5;
            foreach (var (no, text) in MemberExtractor.Lines(record, archive, member, line, ctx, a.Flag("raw")))
                Console.WriteLine($"Line {no:N0}{(no == line ? ", the hit" : "")}: {text}");
            return 0;
        }
        var outPath = a.Opt("out") ?? throw new ArgumentException("Say where to write it with --out FILE, or show a few lines with --line N.");
        long bytes = MemberExtractor.ToFile(record, archive, member, outPath);
        Console.WriteLine($"Wrote {bytes:N0} bytes to {Path.GetFullPath(outPath)}.");
        return 0;
    }

    private static int Status(Args a, IndexHome home, string record, UnlockOptions unlock, IPrompter prompt)
    {
        Console.WriteLine($"Index folder: {home.Folder}");
        Console.WriteLine($"Record: {record}");
        if (!home.Exists) { Console.WriteLine("No index has been created here yet. Run record-index init."); return 0; }
        var ring = home.LoadKeyring();
        Console.WriteLine($"Database file: {new FileInfo(home.DatabasePath).Length / 1048576.0:N1} MB, encrypted.");
        Console.WriteLine($"Unlocked by {ring.Recipients.Count} key{(ring.Recipients.Count == 1 ? "" : "s")}:");
        foreach (var r in ring.Recipients) Console.WriteLine("  " + r.Describe());
        var staged = home.StagedFiles().Count();
        Console.WriteLine(staged == 0 ? "Nothing is staged." : $"{staged} archive{(staged == 1 ? " is" : "s are")} staged and not yet searchable; the next catch-up merges them.");
        if (a.Flag("full"))
        {
            using var ix = home.Open(unlock, prompt, readOnly: true);
            foreach (var (name, value) in ix.Counts()) Console.WriteLine($"{name}: {value:N0}");
        }
        return 0;
    }

    private static int Keys(Args a, IndexHome home, UnlockOptions unlock, IPrompter prompt)
    {
        var sub = a.Positional.Count > 1 ? a.Positional[1] : "list";
        if (sub == "list")
        {
            var ring = home.LoadKeyring();
            Console.WriteLine($"{ring.Recipients.Count} key{(ring.Recipients.Count == 1 ? "" : "s")} can unlock this index:");
            foreach (var r in ring.Recipients) Console.WriteLine($"  {r.Describe()}, added {r.Added}");
            return 0;
        }
        using var ix = home.Open(unlock, prompt);
        switch (sub)
        {
            case "add-recovery":
            {
                using var k = RecoveryKey.Generate();
                var spki = k.ExportSubjectPublicKeyInfo();
                RecoveryHandoff.Hand(RecoveryKey.ToText(k), a.Opt("recovery-to") ?? "clipboard", prompt);
                var verifyFrom = new UnlockOptions { RecoverySource = a.Opt("recovery-key-from") ?? "prompt" };
                if (!RecoveryHandoff.Verify(spki, verifyFrom, prompt)) throw new UnlockException("The new recovery key was not confirmed, so it was not enrolled.");
                var r = ix.AddRecipient(ix.NewRecoveryRecipient("Recovery key (1Password)", spki));
                prompt.Say($"Enrolled {r.Describe()}. The database was not re-encrypted; only its key was wrapped once more.");
                return 0;
            }
            case "add-yubikey":
            {
                var slot = PivSlots.Parse(a.Opt("slot") ?? "9d");
                using var dev = (unlock.OpenPiv ?? Unlocker.DefaultOpenPiv(prompt))(null);
                var existing = dev.GetPublicKey(slot);
                byte[] spki;
                if (existing is not null)
                {
                    if (!a.Flag("existing")) throw new UnlockException($"PIV slot {PivSlots.Name(slot)} already holds a key. Nothing was changed. Add --existing to use it, or choose an empty slot such as 82 with --slot.");
                    spki = existing;
                }
                else
                {
                    prompt.Say($"Generating a new key inside the YubiKey, in slot {PivSlots.Name(slot)}. It cannot be copied out.");
                    spki = dev.GenerateP256(slot, ParseTouch(a.Opt("touch")));
                }
                var trial = Envelope.NewKey();
                var dbId = ix.Ring.DatabaseIdBytes;
                prompt.Say("Testing the YubiKey key once before enrolling it.");
                var back = Envelope.Unwrap(Envelope.Wrap(trial, spki, dbId), spki, dbId, eph => dev.KeyAgree(slot, eph));
                if (!back.AsSpan().SequenceEqual(trial)) throw new UnlockException("The YubiKey test failed; nothing was enrolled.");
                var r = ix.AddRecipient(ix.NewYubiKeyRecipient(spki, dev.Serial, slot));
                prompt.Say($"Enrolled {r.Describe()}.");
                return 0;
            }
            case "remove":
            {
                if (a.Positional.Count < 3) throw new ArgumentException("Name the key's id. Run record-index keys list to see them.");
                var r = ix.RemoveRecipient(a.Positional[2]);
                prompt.Say($"Removed {r.Describe()}. It can no longer unlock this keyring. If that key may have been exposed, also run record-index keys rotate.");
                return 0;
            }
            case "rotate":
                ix.RotateDatabaseKey(prompt);
                prompt.Say("The database key is replaced and every enrolled key re-wrapped to it.");
                return 0;
            default:
                prompt.Say($"Unknown keys command {sub}.");
                return 2;
        }
    }

    private static PivTouch ParseTouch(string? s) => (s ?? "always").ToLowerInvariant() switch
    {
        "never" => PivTouch.Never,
        "cached" => PivTouch.Cached,
        "always" => PivTouch.Always,
        _ => throw new ArgumentException("--touch is always, cached or never."),
    };

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} minutes {t.Seconds} seconds" : $"{t.TotalSeconds:0.0} seconds";
}

internal sealed class Args
{
    public List<string> Positional { get; } = new();
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "json", "fts", "any", "raw", "dry-run", "full", "yubikey", "existing", "help" };

    public Args(string[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            var s = argv[i];
            if (s.StartsWith("--", StringComparison.Ordinal))
            {
                var name = s[2..];
                if (Flags.Contains(name)) _opts[name] = null;
                else if (i + 1 < argv.Length) _opts[name] = argv[++i];
                else throw new ArgumentException($"--{name} needs a value.");
            }
            else Positional.Add(s);
        }
    }

    public string? Opt(string name) => _opts.TryGetValue(name, out var v) ? v : null;
    public bool Flag(string name) => _opts.ContainsKey(name);
}

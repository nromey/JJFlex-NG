using System.Text;
using JJFlex.RecordIndex.Extract;
using JJFlex.RecordIndex.Indexing;
using JJFlex.RecordIndex.Search;
using Xunit;

namespace JJFlex.RecordIndex.Tests;

public class IndexingTests
{
    private static Sandbox RecordWithEverything()
    {
        var s = new Sandbox();
        s.Zip(@"memory\memory-20260101-010101.zip",
            ("feedback_alpha.md", "Always say you, not Noel. The quokka rule."),
            ("project_beta.md", "The wombat ledger lives here."));
        s.Zip(@"memory\memory-20260102-010101.zip",
            ("feedback_alpha.md", "Always say you, not Noel. The quokka rule."),
            ("project_beta.md", "The wombat ledger lives here, now with an addendum."));
        s.Zip(@"claude-state\claude-state-20260103-020202.zip",
            (".claude/projects/c--dev-JJFlex-NG/s1.jsonl", Sandbox.Transcript(
                ("user", "Can sqlite use public private key pairing?", "2026-01-03T01:34:00Z"),
                ("assistant", "Yes: a random database key\nlocked to several public keys, envelope encryption.", "2026-01-03T01:35:00Z"),
                ("user", "We use 1pw and the yubikey.", "2026-01-03T01:38:00Z"))));
        s.ZipBytes(@"4.1.16.9\x64-debug\JJFlex_4.1.16.9_x64_debug_20260104-0101.zip",
            ("jjflexible.exe", new byte[] { 0x4D, 0x5A, 0, 0, 1, 2, 3 }),
            ("BUILD-INFO.txt", Encoding.UTF8.GetBytes("Commit: abc123 Built: narwhal build")),
            ("mystery.blob", new byte[] { 1, 0, 2, 0, 3, 0, 4 }));
        s.Loose(@"4.1.16.9\x64-debug\NOTES-4.1.16.9-debug_20260104-0101.txt", "Test the axolotl fix.");
        s.Zip(@"dev-mirror\ignored.zip", ("x.md", "the platypus must never be indexed"));
        s.Loose(@"old\Setup notes.txt", "platypus");
        return s;
    }

    private static SearchResult Find(Sandbox s, string text, Action<SearchQuery>? tweak = null)
    {
        using var ix = s.Home.Open(s.Unlock("recovery"), s.Prompt, readOnly: true);
        var q = new SearchQuery { Text = text };
        tweak?.Invoke(q);
        return Searcher.Search(ix.Db, q);
    }

    [Fact]
    public void Catch_up_indexes_the_record_and_honours_the_exclusions()
    {
        using var s = RecordWithEverything();
        s.Init(withYubiKey: false);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt))
        {
            var rep = ix.CatchUp(s.Prompt);
            Assert.Equal(5, rep.Archives);
            Assert.Equal(5, rep.Indexed);
            var counts = ix.Counts().ToDictionary(c => c.Name, c => c.Value);
            Assert.Equal(5, counts["archives"]);
            // feedback_alpha.md is identical in both memory snapshots: one body.
            Assert.Equal(counts["members with text"] - 1, counts["distinct member contents"]);
        }
        Assert.Empty(Find(s, "platypus").Hits);
        Assert.Empty(Find(s, "jjflexible").Hits);   // binary member: listed, not read
        Assert.Single(Find(s, "narwhal").Hits);
        Assert.Single(Find(s, "axolotl").Hits);
    }

    [Fact]
    public void A_transcript_hit_points_at_the_member_the_line_and_the_time()
    {
        using var s = RecordWithEverything();
        s.Init(withYubiKey: false);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) ix.CatchUp(s.Prompt);

        var h = Assert.Single(Find(s, "envelope encryption").Hits);
        Assert.Equal(@"claude-state\claude-state-20260103-020202.zip", h.Newest.ArchivePath);
        Assert.Equal(".claude/projects/c--dev-JJFlex-NG/s1.jsonl", h.Newest.MemberPath);
        Assert.Equal("transcript", h.Newest.MemberKind);
        Assert.Equal(2, h.Newest.Line);
        Assert.Contains("envelope encryption", h.Snippet);
        Assert.DoesNotContain("\\n", h.Snippet);   // decoded, not raw JSON
        Assert.Equal("2026-01-03T01:34:00Z", h.Timestamp);

        // And the byte offset really is the start of line 2.
        var lines = MemberExtractor.Lines(s.Record, h.Newest.ArchivePath, h.Newest.MemberPath, h.Newest.Line, 0, raw: true);
        var raw = Sandbox.Transcript(("user", "Can sqlite use public private key pairing?", "2026-01-03T01:34:00Z"));
        Assert.Equal(Encoding.UTF8.GetByteCount(raw), h.Newest.ByteOffset);
        Assert.Contains("envelope encryption", Assert.Single(lines).Text);
    }

    [Fact]
    public void Content_repeated_across_snapshots_is_one_hit_naming_first_and_last_sight()
    {
        using var s = RecordWithEverything();
        s.Init(withYubiKey: false);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) ix.CatchUp(s.Prompt);

        var h = Assert.Single(Find(s, "quokka").Hits);
        Assert.Equal(2, h.Archives);
        Assert.Equal(@"memory\memory-20260102-010101.zip", h.Newest.ArchivePath);
        Assert.Equal(@"memory\memory-20260101-010101.zip", h.Oldest.ArchivePath);
        Assert.Equal(2, Find(s, "wombat").Hits.Count);   // the two versions differ, so two hits
    }

    [Fact]
    public void Incremental_catch_up_skips_what_is_unchanged_reindexes_what_changed_and_forgets_what_was_pruned()
    {
        using var s = RecordWithEverything();
        s.Init(withYubiKey: false);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) ix.CatchUp(s.Prompt);

        using (var ix = s.Home.Open(s.Unlock(), s.Prompt))
        {
            var again = ix.CatchUp(s.Prompt);
            Assert.Equal(0, again.Indexed);
            Assert.Equal(5, again.Unchanged);
        }

        var notes = Path.Combine(s.Record, @"4.1.16.9\x64-debug\NOTES-4.1.16.9-debug_20260104-0101.txt");
        File.WriteAllText(notes, "Test the salamander fix.");
        File.SetLastWriteTimeUtc(notes, DateTime.UtcNow.AddMinutes(5));
        File.Delete(Path.Combine(s.Record, @"memory\memory-20260101-010101.zip"));

        using (var ix = s.Home.Open(s.Unlock(), s.Prompt))
        {
            var rep = ix.CatchUp(s.Prompt);
            Assert.Equal(1, rep.Indexed);
            Assert.Equal(1, rep.Removed);
        }
        Assert.Empty(Find(s, "axolotl").Hits);
        Assert.Single(Find(s, "salamander").Hits);
        var q = Assert.Single(Find(s, "quokka").Hits);   // still in the surviving snapshot
        Assert.Equal(1, q.Archives);
        Assert.Single(Find(s, "wombat").Hits);            // the pruned version's unique text is gone
    }

    [Fact]
    public void Ranking_puts_the_denser_match_first_and_punctuation_is_not_syntax()
    {
        using var s = new Sandbox();
        s.Zip(@"private\private-20260101-010101.zip",
            ("once.md", "A note that mentions rigmeter once, among many other words about the shack and the antenna and the weather."),
            ("many.md", "rigmeter rigmeter rigmeter: the rigmeter snapshot and rigmeter today."),
            ("code.md", "Edit JJFlexWpf/KeyCommands.cs for #854."));
        s.Init(withYubiKey: false);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) ix.CatchUp(s.Prompt);

        var hits = Find(s, "rigmeter").Hits;
        Assert.Equal(2, hits.Count);
        Assert.Equal("many.md", hits[0].Newest.MemberPath);
        Assert.Equal("code.md", Assert.Single(Find(s, "KeyCommands.cs").Hits).Newest.MemberPath);
        Assert.Single(Find(s, "#854").Hits);
        Assert.Single(Find(s, "\"snapshot and rigmeter\"").Hits);
        Assert.Empty(Find(s, "\"rigmeter and snapshot\"").Hits);
        Assert.Single(Find(s, "rigm*").Hits.Where(h => h.Newest.MemberPath == "many.md"));
        Assert.Equal(3, Find(s, "antenna rigmeter KeyCommands", q => q.Any = true).Hits.Count);
    }

    [Fact]
    public void Filters_by_kind_date_and_path()
    {
        using var s = RecordWithEverything();
        s.Init(withYubiKey: false);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) ix.CatchUp(s.Prompt);

        Assert.Single(Find(s, "yubikey", q => q.Kind = "transcript").Hits);
        Assert.Empty(Find(s, "yubikey", q => q.Kind = "memory").Hits);
        Assert.Single(Find(s, "quokka", q => q.Kind = "memory-entry").Hits);
        var since = Assert.Single(Find(s, "quokka", q => q.Since = "2026-01-02").Hits);
        Assert.Equal(1, since.Archives);
        Assert.Single(Find(s, "quokka", q => q.Until = "2026-01-01").Hits);
        Assert.Single(Find(s, "wombat", q => q.PathContains = "20260102").Hits);
    }

    [Fact]
    public void Extract_writes_one_member_and_refuses_a_repository()
    {
        using var s = RecordWithEverything();
        var out1 = Path.Combine(s.Root, "out", "notes.md");
        MemberExtractor.ToFile(s.Record, @"memory\memory-20260102-010101.zip", "project_beta.md", out1);
        Assert.Equal("The wombat ledger lives here, now with an addendum.", File.ReadAllText(out1));
        Assert.Throws<IOException>(() => MemberExtractor.ToFile(s.Record, @"memory\memory-20260102-010101.zip", "project_beta.md",
            Path.Combine(AppContext.BaseDirectory, "leak.md")));
        Assert.Throws<FileNotFoundException>(() => MemberExtractor.ToFile(s.Record, @"memory\memory-20260102-010101.zip", "nope.md", Path.Combine(s.Root, "out", "x")));
    }

    [Fact]
    public void Reading_the_record_never_changes_it()
    {
        using var s = RecordWithEverything();
        var before = Directory.EnumerateFiles(s.Record, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.GetLastWriteTimeUtc(f), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
        s.Init(withYubiKey: false);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) ix.CatchUp(s.Prompt);
        MemberExtractor.Lines(s.Record, @"memory\memory-20260101-010101.zip", "feedback_alpha.md", 1, 2, false);
        var after = Directory.EnumerateFiles(s.Record, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.GetLastWriteTimeUtc(f), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
        Assert.Equal(before, after);
    }

    [Fact]
    public void The_seal_hook_stages_without_any_unlock_and_catch_up_merges_it()
    {
        using var s = new Sandbox();
        s.Init(withYubiKey: true);
        var zip = s.Zip(@"claude-state\claude-state-20260105-050505.zip",
            (".claude/projects/p/s.jsonl", Sandbox.Transcript(("assistant", "the ocelot staging test", "2026-01-05T05:05:05Z"))));

        int agreeBefore = s.Yubi.AgreeCount;
        var stage = s.Home.Stage(s.Record, zip, s.Prompt);   // no UnlockOptions exists on this path at all
        Assert.Equal(agreeBefore, s.Yubi.AgreeCount);
        Assert.True(File.Exists(stage));
        Assert.Equal(-1, File.ReadAllBytes(stage).AsSpan().IndexOf(Encoding.UTF8.GetBytes("ocelot")));
        Assert.Single(s.Home.StagedFiles());

        using (var ix = s.Home.Open(s.Unlock("yubikey"), s.Prompt))
        {
            var rep = ix.CatchUp(s.Prompt);
            Assert.Equal(1, rep.Merged);
            Assert.Equal(0, rep.Indexed);   // the merge already brought it level
        }
        Assert.Empty(s.Home.StagedFiles());
        var h = Assert.Single(Find(s, "ocelot").Hits);
        Assert.Equal(".claude/projects/p/s.jsonl", h.Newest.MemberPath);
        Assert.Equal(1, h.Newest.Line);
    }

    [Fact]
    public void A_stage_carries_only_what_changed_since_the_previous_snapshot()
    {
        using var s = new Sandbox();
        s.Zip(@"private\private-20260101-010101.zip", ("same.md", "the unchanged pangolin"), ("moves.md", "first heron"));
        s.Init(withYubiKey: false);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) ix.CatchUp(s.Prompt);

        var next = s.Zip(@"private\private-20260102-010101.zip", ("same.md", "the unchanged pangolin"), ("moves.md", "second egret"));
        var stage = s.Home.Stage(s.Record, next, s.Prompt);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<StageManifest>(File.ReadAllText(stage + ".json"))!;
        Assert.Equal(@"private\private-20260101-010101.zip", manifest.Predecessor);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt))
        {
            // The stage holds the changed member's text and not the unchanged one's.
            var w = ix.Counts().Single(c => c.Name == "distinct chunks").Value;
            Assert.Equal(1, ix.Merge(s.Prompt));
            Assert.Equal(w + 1, ix.Counts().Single(c => c.Name == "distinct chunks").Value);
        }
        Assert.Equal(2, Assert.Single(Find(s, "pangolin").Hits).Archives);
        Assert.Single(Find(s, "egret").Hits);
        Assert.Single(Find(s, "heron").Hits);
    }

    [Fact]
    public void Unchanged_members_are_not_read_into_a_stage()
    {
        using var s = new Sandbox();
        var prev = s.Zip(@"private\private-20260101-010101.zip", ("same.md", "the unchanged pangolin"), ("moves.md", "first heron"));
        var next = s.Zip(@"private\private-20260102-010101.zip", ("same.md", "the unchanged pangolin"), ("moves.md", "second egret"));
        Assert.Equal(prev, ArchiveIndexer.Predecessor(next));
        Assert.Null(ArchiveIndexer.Predecessor(prev));

        using var db = JJFlex.RecordIndex.Store.IndexDatabase.Create(Path.Combine(s.Root, "stage.db"), JJFlex.RecordIndex.Crypto.Envelope.NewKey(), withFts: false);
        using (var w = new JJFlex.RecordIndex.Store.IndexWriter(db))
        {
            ArchiveIndexer.Index(w, ArchiveIndexer.Describe(s.Record, next), ArchiveIndexer.Signatures(prev));
            w.Commit();
        }
        using var c = db.Command("SELECT path, body_id IS NULL, note FROM member ORDER BY path");
        using var r = c.ExecuteReader();
        Assert.True(r.Read()); Assert.Equal("moves.md", r.GetString(0)); Assert.Equal(0L, r.GetInt64(1));
        Assert.True(r.Read()); Assert.Equal("same.md", r.GetString(0)); Assert.Equal(1L, r.GetInt64(1)); Assert.Equal(ArchiveIndexer.SameAsPredecessor, r.GetString(2));
        using var n = db.Command("SELECT count(*) FROM chunk");
        Assert.Equal(1L, n.ExecuteScalar());
    }

    [Fact]
    public void A_stage_whose_predecessor_was_never_indexed_is_set_aside_and_indexed_from_the_record()
    {
        using var s = new Sandbox();
        s.Init(withYubiKey: false);
        s.Zip(@"private\private-20260101-010101.zip", ("same.md", "the unchanged pangolin"));
        var next = s.Zip(@"private\private-20260102-010101.zip", ("same.md", "the unchanged pangolin"), ("new.md", "a new kestrel"));
        s.Home.Stage(s.Record, next, s.Prompt);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt))
        {
            var rep = ix.CatchUp(s.Prompt);
            Assert.Equal(0, rep.Merged);
            Assert.Equal(2, rep.Indexed);
        }
        Assert.Empty(s.Home.StagedFiles());
        Assert.Equal(2, Assert.Single(Find(s, "pangolin").Hits).Archives);
        Assert.Single(Find(s, "kestrel").Hits);
    }

    [Fact]
    public void A_grown_transcript_reuses_every_chunk_but_the_last()
    {
        using var s = new Sandbox();
        var lines = Enumerable.Range(1, 400).Select(i => ("assistant", $"Message number {i} about the trellis and the lantern and the river, padded out with enough words to fill a chunk sooner.", $"2026-01-01T00:{i % 60:00}:00Z")).ToArray();
        s.Zip(@"claude-state\claude-state-20260101-000000.zip", (".claude/projects/p/s.jsonl", Sandbox.Transcript(lines)));
        s.Init(withYubiKey: false);
        long firstChunks;
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) { ix.CatchUp(s.Prompt); firstChunks = ix.Counts().Single(c => c.Name == "distinct chunks").Value; }
        Assert.True(firstChunks > 3);

        // The next day's snapshot has the same transcript with 20 more lines on the end.
        // Same lines byte for byte: the earlier member is read back and appended to.
        var grown = ReadMember(s, @"claude-state\claude-state-20260101-000000.zip", ".claude/projects/p/s.jsonl")
            + Sandbox.Transcript(Enumerable.Range(401, 20).Select(i => ("assistant", $"Later message {i}", "2026-01-02T00:00:00Z")).ToArray());
        s.Zip(@"claude-state\claude-state-20260102-000000.zip", (".claude/projects/p/s.jsonl", grown));
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt))
        {
            var rep = ix.CatchUp(s.Prompt);
            Assert.InRange(rep.NewChunks, 1, 2);
        }
    }

    [Fact]
    public void The_line_map_locates_every_line_exactly()
    {
        var chunks = new List<ChunkData>();
        var c = new Chunker(chunks.Add);
        var text = new StringBuilder();
        long offset = 0;
        var starts = new List<long>();
        for (int i = 1; i <= 3000; i++)
        {
            var line = i % 500 == 0 ? new string('x', 20000) + $" mark{i}" : $"line {i} mark{i}";
            starts.Add(offset);
            c.AddLine(i, offset, Encoding.UTF8.GetByteCount(line) + 1, line, null);
            offset += Encoding.UTF8.GetByteCount(line) + 1;
        }
        c.Flush();
        Assert.True(chunks.Count > 5);
        foreach (var ch in chunks)
        {
            int at = 0;
            while ((at = ch.Text.IndexOf("mark", at, StringComparison.Ordinal)) >= 0)
            {
                int end = at + 4;
                while (end < ch.Text.Length && char.IsDigit(ch.Text[end])) end++;
                long n = long.Parse(ch.Text[(at + 4)..end]);
                var (ld, bd) = Chunker.Locate(ch.Map, at);
                Assert.Equal(n, ch.LineStart + ld);
                Assert.Equal(starts[(int)n - 1], ch.ByteOffset + bd);
                at = end;
            }
        }
    }

    [Theory]
    [InlineData("claude-state-20261027-214839.zip", "2026-10-27 21:48")]
    [InlineData("JJFlex_4.1.16.2568_x64_debug_20261025-2148.zip", "2026-10-25 21:48")]
    [InlineData("2026-04-19-6d6a0084.json", "2026-04-19")]
    [InlineData("nvda-old-20261005.log", "2026-10-05")]
    [InlineData("nvda-20260905-195946-pid24124.log", "2026-09-05 19:59")]
    public void Archive_dates_come_from_the_name(string name, string expected) =>
        Assert.Equal(expected, ArchiveIndexer.DateFromName(name));

    private static string ReadMember(Sandbox s, string archive, string member)
    {
        var st = MemberExtractor.OpenMember(s.Record, archive, member, out var done);
        try { using var r = new StreamReader(st); return r.ReadToEnd(); }
        finally { done(); }
    }
}

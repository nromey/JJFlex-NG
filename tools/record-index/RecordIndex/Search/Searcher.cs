using System.Diagnostics;
using System.Text;
using JJFlex.RecordIndex.Indexing;
using JJFlex.RecordIndex.Store;

namespace JJFlex.RecordIndex.Search;

public sealed class SearchQuery
{
    public string Text { get; set; } = "";
    public int Limit { get; set; } = 10;
    /// <summary>Matches an archive kind (claude-state, memory, private, appdata, build, nvda-log, stats) or a member kind (transcript, memory-entry, trace...).</summary>
    public string? Kind { get; set; }
    public string? Since { get; set; }
    public string? Until { get; set; }
    /// <summary>Only members whose path contains this text.</summary>
    public string? PathContains { get; set; }
    /// <summary>Pass the query to FTS5 as written (its own syntax: AND, OR, NOT, NEAR, "phrases", prefix*).</summary>
    public bool Raw { get; set; }
    /// <summary>Any word may match, rather than all of them.</summary>
    public bool Any { get; set; }
}

public sealed record Occurrence(string ArchivePath, string ArchiveKind, string? ArchiveDate, string MemberPath, string MemberKind, long Line, long ByteOffset);

public sealed record Hit(int Rank, double Score, long ChunkId, string Snippet, string? Timestamp, Occurrence Newest, Occurrence Oldest, int Occurrences, int Archives);

public sealed record SearchResult(string Match, IReadOnlyList<Hit> Hits, long ElapsedMs, int ChunksExamined);

public static class Searcher
{
    public static SearchResult Search(IndexDatabase db, SearchQuery q)
    {
        if (!db.HasFts) throw new InvalidOperationException("This database has no full-text index.");
        var sw = Stopwatch.StartNew();
        string match = q.Raw ? q.Text : BuildMatch(q.Text, q.Any);
        var terms = Terms(q.Text);
        var hits = new List<Hit>();
        int examined = 0;
        // Unfiltered, the first page is exactly the hits wanted. Filtered, chunks
        // are read in pages and the filters applied to where each one occurs,
        // until enough survive or 5,000 candidates have been looked at.
        int page = Filtered(q) ? 200 : q.Limit;
        for (int offset = 0; hits.Count < q.Limit && offset < 5000; offset += page)
        {
            var batch = new List<(long Id, double Score)>();
            using (var c = db.Command("SELECT rowid, bm25(chunk_fts) FROM chunk_fts WHERE chunk_fts MATCH $q ORDER BY bm25(chunk_fts) LIMIT $n OFFSET $o"))
            {
                c.Parameters.AddWithValue("$q", match);
                c.Parameters.AddWithValue("$n", page);
                c.Parameters.AddWithValue("$o", offset);
                using var r = c.ExecuteReader();
                while (r.Read()) batch.Add((r.GetInt64(0), r.GetDouble(1)));
            }
            if (batch.Count == 0) break;
            foreach (var (id, score) in batch)
            {
                examined++;
                var occ = Occurrences(db, id, q);
                if (occ.Count == 0) continue;
                var (text, map, ts) = ChunkText(db, id);
                int pos = BestPosition(text, terms);
                var (lineDelta, byteDelta) = Chunker.Locate(map, pos);
                Occurrence At(OccRow o) => new(o.ArchivePath, o.ArchiveKind, o.ArchiveDate, o.MemberPath, o.MemberKind, o.LineStart + lineDelta, o.ByteOffset + byteDelta);
                hits.Add(new Hit(hits.Count + 1, score, id, Snippet(text, pos), ts, At(occ[0]), At(occ[^1]), occ.Count,
                    occ.Select(o => o.ArchivePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
                if (hits.Count >= q.Limit) break;
            }
            if (batch.Count < page) break;
        }
        return new SearchResult(match, hits, sw.ElapsedMilliseconds, examined);
    }

    private static bool Filtered(SearchQuery q) => q.Kind is not null || q.Since is not null || q.Until is not null || q.PathContains is not null;

    private sealed record OccRow(string ArchivePath, string ArchiveKind, string? ArchiveDate, string MemberPath, string MemberKind, long LineStart, long ByteOffset);

    /// <summary>Every place a chunk appears, newest archive first, after the filters.</summary>
    private static List<OccRow> Occurrences(IndexDatabase db, long chunkId, SearchQuery q)
    {
        var sql = new StringBuilder("""
            SELECT a.path, a.kind, a.archive_date, m.path, m.kind, bc.line_start, bc.byte_offset
            FROM body_chunk bc
            JOIN member m ON m.body_id = bc.body_id
            JOIN archive a ON a.id = m.archive_id
            WHERE bc.chunk_id = $c
            """);
        using var c = db.Command("");
        c.Parameters.AddWithValue("$c", chunkId);
        if (q.Kind is not null) { sql.Append(" AND (a.kind = $k OR m.kind = $k)"); c.Parameters.AddWithValue("$k", q.Kind); }
        if (q.Since is not null) { sql.Append(" AND a.archive_date >= $s"); c.Parameters.AddWithValue("$s", q.Since); }
        if (q.Until is not null) { sql.Append(" AND a.archive_date < $u"); c.Parameters.AddWithValue("$u", UntilExclusive(q.Until)); }
        if (q.PathContains is not null) { sql.Append(" AND (instr(lower(m.path), lower($p)) > 0 OR instr(lower(a.path), lower($p)) > 0)"); c.Parameters.AddWithValue("$p", q.PathContains); }
        sql.Append(" ORDER BY a.archive_date DESC, a.path DESC");
        c.CommandText = sql.ToString();
        var list = new List<OccRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new OccRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
        return list;
    }

    /// <summary>"--until 2026-10-20" includes the whole of that day.</summary>
    private static string UntilExclusive(string until) =>
        DateTime.TryParse(until, out var d) && until.Length <= 10 ? d.AddDays(1).ToString("yyyy-MM-dd") : until;

    private static (string Text, byte[] Map, string? Ts) ChunkText(IndexDatabase db, long id)
    {
        using var c = db.Command("SELECT text_z, line_map, first_ts FROM chunk WHERE id=$id");
        c.Parameters.AddWithValue("$id", id);
        using var r = c.ExecuteReader();
        r.Read();
        return (IndexWriter.Decompress((byte[])r[0]), (byte[])r[1], r.IsDBNull(2) ? null : r.GetString(2));
    }

    /// <summary>
    /// Plain words become an AND of quoted strings, so punctuation such as
    /// KeyCommands.cs or #854 is not read as query syntax. "Quoted phrases" stay
    /// phrases. A trailing * keeps its prefix meaning.
    /// </summary>
    public static string BuildMatch(string text, bool any)
    {
        var parts = new List<string>();
        foreach (var (token, quoted) in Tokens(text))
        {
            bool prefix = !quoted && token.EndsWith('*') && token.Length > 1;
            var body = (prefix ? token[..^1] : token).Replace("\"", "\"\"");
            if (body.Trim().Length == 0) continue;
            parts.Add("\"" + body + "\"" + (prefix ? "*" : ""));
        }
        if (parts.Count == 0) throw new ArgumentException("Give at least one word to search for.");
        return string.Join(any ? " OR " : " AND ", parts);
    }

    private static IEnumerable<(string Token, bool Quoted)> Tokens(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length;
                yield return (text[(i + 1)..end], true);
                i = end + 1;
                continue;
            }
            int s = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"') i++;
            yield return (text[s..i], false);
        }
    }

    /// <summary>The words to look for when placing a snippet, trimmed to a rough stem.</summary>
    internal static List<string> Terms(string text)
    {
        var words = new List<string>();
        foreach (var (token, _) in Tokens(text))
            foreach (var w in System.Text.RegularExpressions.Regex.Split(token, @"[^\p{L}\p{Nd}]+"))
            {
                if (w.Length == 0 || w is "AND" or "OR" or "NOT" or "NEAR") continue;
                var lw = w.ToLowerInvariant();
                foreach (var suffix in new[] { "ations", "ation", "ings", "ing", "ions", "ion", "ed", "es", "s" })
                    if (lw.Length - suffix.Length >= 4 && lw.EndsWith(suffix, StringComparison.Ordinal)) { lw = lw[..^suffix.Length]; break; }
                words.Add(lw);
            }
        return words.Distinct().ToList();
    }

    /// <summary>The position in the chunk where the most distinct query words fall close together.</summary>
    internal static int BestPosition(string text, List<string> terms)
    {
        var positions = new List<(int Pos, int Term)>();
        for (int t = 0; t < terms.Count; t++)
        {
            int at = 0, found = 0;
            while (found < 200 && (at = text.IndexOf(terms[t], at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (at == 0 || !char.IsLetterOrDigit(text[at - 1])) { positions.Add((at, t)); found++; }
                at += terms[t].Length;
            }
        }
        if (positions.Count == 0) return 0;
        positions.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        int best = positions[0].Pos, bestCount = 0;
        foreach (var (pos, _) in positions)
        {
            int count = positions.Where(p => p.Pos >= pos && p.Pos < pos + 200).Select(p => p.Term).Distinct().Count();
            if (count > bestCount) { bestCount = count; best = pos; }
        }
        return best;
    }

    internal static string Snippet(string text, int pos, int before = 70, int after = 230)
    {
        int s = Math.Max(0, pos - before);
        int e = Math.Min(text.Length, pos + after);
        while (s > 0 && s > pos - before - 20 && !char.IsWhiteSpace(text[s - 1])) s--;
        while (e < text.Length && e < pos + after + 20 && !char.IsWhiteSpace(text[e])) e++;
        var sb = new StringBuilder();
        if (s > 0) sb.Append("... ");
        bool space = false;
        for (int i = s; i < e; i++)
        {
            char ch = text[i];
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        if (e < text.Length) sb.Append(" ...");
        return sb.ToString();
    }
}

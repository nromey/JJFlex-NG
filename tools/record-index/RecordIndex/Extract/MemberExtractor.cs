using System.IO.Compression;
using System.Text;
using JJFlex.RecordIndex.Crypto;
using JJFlex.RecordIndex.Indexing;

namespace JJFlex.RecordIndex.Extract;

/// <summary>
/// Pulls ONE member out of one archive of the record, without unpacking the
/// rest. Needs no key: it reads the record, not the index. Read-only on the
/// archive, always.
/// </summary>
public static class MemberExtractor
{
    public static Stream OpenMember(string recordRoot, string archive, string member, out Action dispose)
    {
        var full = Path.IsPathRooted(archive) ? archive : Path.Combine(recordRoot, archive);
        if (!File.Exists(full)) throw new FileNotFoundException($"There is no archive at {full}. It may have been pruned from the record since it was indexed.", full);
        var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            dispose = fs.Dispose;
            return fs;
        }
        var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var want = member.Replace('\\', '/');
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == want)
            ?? zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(want, StringComparison.OrdinalIgnoreCase));
        if (entry is null) { zip.Dispose(); throw new FileNotFoundException($"{archive} has no member named {member}."); }
        var s = entry.Open();
        dispose = () => { s.Dispose(); zip.Dispose(); };
        return s;
    }

    /// <summary>Copies the whole member to a file. Refuses a path inside a git working tree: the record is private.</summary>
    public static long ToFile(string recordRoot, string archive, string member, string outPath)
    {
        var full = Path.GetFullPath(outPath);
        if (Repository.IsInsideGitWorkTree(Path.GetDirectoryName(full)!))
            throw new IOException($"Not writing private record content inside a git working tree ({full}). Choose a folder outside any repository.");
        if (File.Exists(full)) throw new IOException($"{full} already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var s = OpenMember(recordRoot, archive, member, out var done);
        try
        {
            using var o = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
            s.CopyTo(o);
            return o.Length;
        }
        finally { done(); }
    }

    /// <summary>
    /// Returns the lines around one line of a member, numbered the way the index
    /// numbers them. A session transcript's lines are shown as their extracted text
    /// unless <paramref name="raw"/> is set.
    /// </summary>
    public static IReadOnlyList<(long Line, string Text)> Lines(string recordRoot, string archive, string member, long line, int context, bool raw)
    {
        var s = OpenMember(recordRoot, archive, member, out var done);
        try
        {
            var (_, how, _) = MemberClassifier.Classify("", member);
            var head = new byte[2];
            int n = s.Read(head, 0, 2);
            Stream stream = new PrefixedStream(head, n, s);
            if (n == 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
            {
                using var r = new StreamReader(stream, head[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode, true);
                stream = new MemoryStream(Encoding.UTF8.GetBytes(r.ReadToEnd()));
            }
            var reader = new ByteLineReader(stream);
            var result = new List<(long, string)>();
            long no = 0, from = Math.Max(1, line - context), to = line + context;
            while (no < to && reader.Next(out var bytes, out _, out _))
            {
                no++;
                if (no < from) continue;
                string text = raw || how != Treatment.Jsonl ? LineText.Plain(bytes) : LineText.Jsonl(bytes).Text;
                result.Add((no, text));
            }
            return result;
        }
        finally { done(); }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Prose.Tests;

/// <summary>
/// A throwaway copy of the real surface: the same source files, the same
/// surface definition, and nothing else.
/// </summary>
/// <remarks>
/// The tests run against THE REAL FIXER PROSE rather than an invented fixture.
/// A fixture would prove the tool handles the C# its author imagined; this
/// proves it handles the file the operator is about to edit — the four-literal
/// concatenations, the Append chains with live values spliced between them,
/// the conditional branches, the em dashes and the smart quotes.
/// </remarks>
public sealed class Workspace : IDisposable
{
    public string Root { get; }
    public IReadOnlyList<string> SourceFiles { get; }

    public Workspace()
    {
        string repo = FindRepo();
        Root = Path.Combine(Path.GetTempPath(), "prose-tests-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "JJFlexRadio.sln"), "");

        string surfaces = Path.Combine(Root, "tools", "prose", "surfaces");
        Directory.CreateDirectory(surfaces);
        string surfaceJson = Path.Combine(repo, "tools", "prose", "surfaces", "fixer.json");
        File.Copy(surfaceJson, Path.Combine(surfaces, "fixer.json"));

        Surface surface = Surface.Load(surfaceJson);
        var files = new List<string>();
        foreach (string rel in surface.Files.Select(f => f.Path).Concat(surface.Context))
        {
            Copy(repo, rel);
            files.Add(rel);
        }
        SourceFiles = files;
    }

    private void Copy(string repo, string rel)
    {
        string from = Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
        string to = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    public string Path_(string rel) =>
        Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    public string EditingFilePath => Path_("tools/prose/fixer-prose.md");

    public Session Open() => new(Root, "fixer");

    /// <summary>Every source file's bytes, by path — the byte-identity check.</summary>
    public Dictionary<string, string> Hashes()
    {
        var h = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string rel in SourceFiles)
            h[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path_(rel))));
        return h;
    }

    public string Markdown => File.ReadAllText(EditingFilePath, Encoding.UTF8);

    public void WriteMarkdown(string text) =>
        File.WriteAllText(EditingFilePath, text,
                          new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// Replace the words under one key, the way an editor would: find the
    /// entry, change its paragraph, leave everything else alone.
    /// </summary>
    public void Edit(string key, string newText)
    {
        string[] lines = Markdown.Replace("\r\n", "\n").Split('\n');
        int keyLine = Array.FindIndex(lines, l => l.Trim() == "> " + key);
        Assert.True(keyLine >= 0, "No entry in the editing file is filed under " + key + ".");

        // The words are the last ordinary line above the quote lines.
        int at = keyLine - 1;
        while (at > 0 && (lines[at].StartsWith('>') || lines[at].Trim().Length == 0)) at--;
        Assert.True(at > 0 && !lines[at].StartsWith("#"),
                    "Could not find the words for " + key + ".");

        lines[at] = newText;
        WriteMarkdown(string.Join("\r\n", lines));
    }

    /// <summary>The words currently under one key in the editing file.</summary>
    public string TextOf(string key) =>
        EditingFile.Parse(Markdown).Texts.TryGetValue(key, out string? t) ? t : "";

    public (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = Program.Run([.. args, "--root", Root], o, e);
        return (code, o.ToString(), e.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    private static string FindRepo()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) return d.FullName;
        throw new InvalidOperationException("These tests must run inside a working copy.");
    }
}

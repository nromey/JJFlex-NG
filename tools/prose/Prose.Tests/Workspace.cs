using System.Security.Cryptography;
using System.Text;

namespace Prose.Tests;

/// <summary>
/// A throwaway tree holding one surface: its source files, its surface
/// definition, and nothing else. By default the surface is the suite's OWN
/// frozen copy of the Fixer's (<c>Prose.Tests/Fixture</c>); <c>live: true</c>
/// copies the real one from the working copy instead.
/// </summary>
/// <remarks>
/// <para><b>Why a frozen copy (#390).</b> The suite used to copy the LIVE Fixer
/// prose and assert its exact sentences, so every successful use of the tool
/// turned the tool's own suite red: within a day five tests failed, touched by
/// nobody but the people editing the words. The tests pin the MECHANISM (a
/// concatenation assembles into one sentence, markup comes off, an edit onto
/// moved words is refused), and those need stable words to pin it on.</para>
/// <para><b>Why a copy of the real code, not invented C#.</b> The tool keys
/// sentences by the shapes the Fixer is written in (its stage, finding and
/// choice types, constructor parameters, Append chains, conditional halves),
/// and a fixture of invented C# would prove only the shapes its author
/// imagined. The copy is taken as it stood on 2026-11-22 and kept as
/// <c>.cs.txt</c>, so nothing compiles it and no source scan of the repository
/// mistakes it for code. Re-freeze it only to cover a new shape, never to
/// make a test pass.</para>
/// <para><b>The one property that holds for ANY prose</b> (an unedited round
/// trip changes no byte) is also run against the live tree.</para>
/// </remarks>
public sealed class Workspace : IDisposable
{
    public string Root { get; }
    public IReadOnlyList<string> SourceFiles { get; }

    /// <summary>Where the suite's own frozen surface lives, under the working copy.</summary>
    public const string FixtureFolder = "tools/prose/Prose.Tests/Fixture";

    /// <summary>The suffix the frozen source files carry, so nothing compiles them.</summary>
    public const string FrozenSuffix = ".txt";

    public Workspace(bool live = false)
    {
        string repo = FindRepo();
        string from = live ? repo : Path.Combine(repo, FixtureFolder.Replace('/', Path.DirectorySeparatorChar));
        string suffix = live ? "" : FrozenSuffix;
        Root = Path.Combine(Path.GetTempPath(), "prose-tests-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "JJFlexRadio.sln"), "");

        string surfaces = Path.Combine(Root, "tools", "prose", "surfaces");
        Directory.CreateDirectory(surfaces);
        string surfaceJson = Path.Combine(from, "tools", "prose", "surfaces", "fixer.json");
        File.Copy(surfaceJson, Path.Combine(surfaces, "fixer.json"));

        Surface surface = Surface.Load(surfaceJson);
        var files = new List<string>();
        foreach (string rel in surface.Files.Select(f => f.Path).Concat(surface.Context))
        {
            Copy(from, rel, suffix);
            files.Add(rel);
        }
        SourceFiles = files;
    }

    private void Copy(string from, string rel, string suffix)
    {
        string source = Path.Combine(from, rel.Replace('/', Path.DirectorySeparatorChar)) + suffix;
        string to = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(source, to, overwrite: true);
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

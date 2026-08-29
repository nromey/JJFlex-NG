using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prose;

/// <summary>
/// Which files hold a surface's words, in what order a person meets them, and
/// the handful of judgements a syntax tree cannot make on its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the pluggable seam.</b> Everything above it — the editing file,
/// the assembled sentences, the round trip, the refusals — is about WORDS and
/// knows nothing about where they are stored. A surface says where to look.
/// Today the only reader is <see cref="CSharpSource"/>; a Lexicon-JSON reader
/// drops in beside it without the editing view changing at all, which is the
/// point: the migration described in the track report moves the storage and
/// leaves this whole tool standing.
/// </para>
/// <para>
/// <b>Why any configuration at all.</b> Three questions genuinely cannot be
/// answered from syntax. Which files are in scope, because a repo is not a
/// surface. Which one-word strings are prose, because
/// <c>StatusPhrase</c> returning "passed" and <c>StatusOf</c> returning
/// "notrun" are the same shape and one is heard by a person. And what a
/// realistic value looks like, because "at 25 watts into ANT1" is a fact about
/// a radio, not about a program. Everything else is inferred.
/// </para>
/// </remarks>
public sealed class Surface
{
    /// <summary>Short name, and the first segment of every key: <c>fixer</c>.</summary>
    public string Id { get; set; } = "";

    /// <summary>What the editing file calls itself.</summary>
    public string Title { get; set; } = "";

    /// <summary>One sentence under that title.</summary>
    public string Blurb { get; set; } = "";

    /// <summary>Where the editing file is written, repo-relative.</summary>
    public string EditingFile { get; set; } = "";

    /// <summary>Where the read-aloud file is written, repo-relative.</summary>
    public string ReadingFile { get; set; } = "";

    /// <summary>The source files, in the order an operator meets their words.</summary>
    public List<SurfaceFile> Files { get; set; } = [];

    /// <summary>
    /// Extra files parsed for context but never extracted from — where the
    /// constants and the constructor parameter names live, so a key can read
    /// <c>fixer.finding.mme-in-use.what-is-wrong</c> instead of
    /// <c>AudioSetupCheck.Analyze.arg2</c>.
    /// </summary>
    public List<string> Context { get; set; } = [];

    /// <summary>Members whose short, spaceless strings ARE prose.</summary>
    public List<string> ShortProseMembers { get; set; } = [];

    /// <summary>Members whose strings are never prose — scripts, stylesheets.</summary>
    public List<string> SkipMembers { get; set; } = [];

    /// <summary>
    /// A realistic value for each placeholder, so the "Reads as" line is a
    /// sentence rather than a template. A name with nothing here shows in
    /// capitals so the gap is visible rather than quietly plausible.
    /// </summary>
    public Dictionary<string, string> Examples { get; set; } = [];

    /// <summary>Slot name to the words a heading uses for it.</summary>
    public Dictionary<string, string> SlotLabels { get; set; } = [];

    public static Surface Load(string path)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        Surface? s = JsonSerializer.Deserialize<Surface>(File.ReadAllText(path), opts);
        return s ?? throw new InvalidOperationException("Surface file " + path + " is empty.");
    }
}

/// <summary>One source file in a surface, with the words a person uses for it.</summary>
public sealed class SurfaceFile
{
    /// <summary>Repo-relative path, forward slashes.</summary>
    public string Path { get; set; } = "";

    /// <summary>The section heading in the editing file.</summary>
    public string Section { get; set; } = "";

    /// <summary>A sentence under that heading saying what lives here.</summary>
    public string About { get; set; } = "";

    /// <summary>Second key segment for entries with no better group — the
    /// kind of thing this file holds. "page", "exit", "stage".</summary>
    public string Group { get; set; } = "";
}

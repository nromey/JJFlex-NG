using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace Prose;

/// <summary>
/// One piece of a sentence as the code holds it: either words, or a value the
/// program fills in when it speaks.
/// </summary>
/// <remarks>
/// The distinction is the whole reason this tool can show an ASSEMBLED
/// sentence. A stem and a suffix each read perfectly on their own and can
/// still join into nonsense; the only way to see that is to put them back
/// together with the moving parts standing in, which is what a chunk list is
/// for.
/// </remarks>
public sealed class Chunk
{
    /// <summary>Words, from a string literal in the source.</summary>
    public static Chunk Literal(string text, TextSpan span, bool writable) =>
        new() { IsLiteral = true, Text = text, Span = span, Writable = writable };

    /// <summary>A value the program fills in — <c>{port}</c>, <c>{RunId}</c>.</summary>
    public static Chunk Placeholder(string name, string expression, TextSpan span) =>
        new() { IsLiteral = false, Text = name, Expression = expression, Span = span };

    public bool IsLiteral { get; private init; }

    /// <summary>For a literal, its decoded words. For a placeholder, its name.</summary>
    public string Text { get; private init; } = "";

    /// <summary>For a placeholder, the source text of the expression it stands for.</summary>
    public string Expression { get; private init; } = "";

    /// <summary>The exact span in the source file — the literal token, or the
    /// whole expression for a placeholder.</summary>
    public TextSpan Span { get; private init; }

    /// <summary>
    /// False for a literal this tool must not rewrite — a character literal or
    /// a verbatim string, where re-emitting the words would change the form as
    /// well as the content.
    /// </summary>
    public bool Writable { get; private init; } = true;

    public override string ToString() => IsLiteral ? Text : "{" + Text + "}";
}

/// <summary>
/// A maximal run of literals that sit next to each other in the source with
/// nothing but whitespace and <c>+</c> between them — so the whole run can be
/// replaced as one span without disturbing anything around it.
/// </summary>
public sealed class Region
{
    public required TextSpan Span { get; init; }

    /// <summary>Column of the first literal's opening quote, for re-wrapping.</summary>
    public required int QuoteColumn { get; init; }

    /// <summary>
    /// The exact prefix the source uses on continuation lines — the whitespace
    /// and the <c>+</c>. Read from the source when the run already spans lines,
    /// so a re-wrap matches the file's own hand rather than this tool's taste.
    /// </summary>
    public string ContinuationPrefix { get; init; } = "";

    public bool Writable { get; init; } = true;
}

/// <summary>One editable sentence, and everything needed to put it back.</summary>
public sealed class Entry
{
    /// <summary>Dotted, lowercase, Lexicon-shaped: <c>fixer.stage.audio-setup.explanation</c>.</summary>
    /// <remarks>
    /// Settled once the whole surface has been read, from
    /// <see cref="KeyCandidates"/>: the shortest candidate nothing else wants.
    /// Doing it across the surface rather than per entry is what keeps a key
    /// from depending on which of two colliding entries happened to be read
    /// first.
    /// </remarks>
    public string Key { get; set; } = "";

    /// <summary>Keys this entry would accept, shortest and plainest first.</summary>
    public required IReadOnlyList<string> KeyCandidates { get; init; }

    /// <summary>The heading a person reads — "Stage 0 Audio setup — what this check does".</summary>
    public required string Label { get; init; }

    /// <summary>Repo-relative, forward slashes.</summary>
    public required string File { get; init; }

    public required int Line { get; init; }

    /// <summary>The markup wrapping the words, stripped from the editing view
    /// and put back on the way out. Empty when there was none.</summary>
    public string ShellOpen { get; init; } = "";
    public string ShellClose { get; init; } = "";

    /// <summary>What the shell makes this — "bullet", "button", "paragraph".</summary>
    public string ShellKind { get; init; } = "";

    public required IReadOnlyList<Chunk> Chunks { get; init; }
    public required IReadOnlyList<Region> Regions { get; init; }

    /// <summary>
    /// Where a gap between placeholders has no literal to write into, the
    /// offset at which new words are inserted. Keyed by gap index.
    /// </summary>
    public required IReadOnlyDictionary<int, int> EmptyGapAnchors { get; init; }

    /// <summary>The sentence, assembled, shell stripped, placeholders as
    /// <c>{name}</c>. This is what the operator edits.</summary>
    public required string Text { get; init; }

    /// <summary>The same sentence with example values filled in — what an
    /// operator will actually hear.</summary>
    public required string Reads { get; init; }

    /// <summary>True when markup survives inside the words and must be kept.</summary>
    public bool HasInlineMarkup { get; init; }

    /// <summary>Placeholder names in the order they appear.</summary>
    public IReadOnlyList<string> PlaceholderOrder =>
        Chunks.Where(c => !c.IsLiteral).Select(c => c.Text).ToList();

    /// <summary>
    /// Six hex characters of the text as extracted. Carried in the editing
    /// file's provenance so <c>apply</c> can tell the operator's edit from a
    /// change somebody else made to the code underneath him.
    /// </summary>
    public string Fingerprint => Hash(Text);

    public static string Hash(string s)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h)[..6].ToLowerInvariant();
    }
}

/// <summary>A replacement to splice into a source file.</summary>
public readonly record struct Splice(TextSpan Span, string Text);

/// <summary>Something the tool will not do, and why, in a sentence.</summary>
public sealed class Refusal(string key, string message)
{
    public string Key { get; } = key;
    public string Message { get; } = message;
    public override string ToString() => Key.Length > 0 ? Key + ": " + Message : Message;
}

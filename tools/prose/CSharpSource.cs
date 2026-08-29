using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Prose;

/// <summary>
/// Reads the words out of C# source, and writes edited words back into the
/// exact literals they came from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The unit is a SENTENCE, not a literal.</b> A single sentence in this
/// codebase is routinely four literals joined with <c>+</c> across four lines,
/// or several <c>.Append()</c> calls in one chain with a live value spliced
/// between them. Extracting per literal would hand the editor exactly the
/// fragments that hide the defect: a stem and a suffix each read perfectly and
/// still join into "last seen remote via SmartLink". So a whole
/// <c>StringBuilder</c> chain, or a whole <c>+</c> tree, is one entry, and the
/// values between the halves become named placeholders the editor can see.
/// </para>
/// <para>
/// <b>Writing back touches literals only.</b> An entry records the exact span
/// of every literal run it is made of. Applying an edit replaces those spans
/// and nothing else — no reformatting, no re-emitting the statement, no
/// touching a line the editor did not change. An entry whose words are
/// unchanged produces no splice at all, which is why a round trip with no
/// edits cannot alter a byte.
/// </para>
/// </remarks>
public sealed class CSharpSource
{
    private readonly SourceFacts _facts = new();
    private readonly Surface _surface;

    public CSharpSource(Surface surface) => _surface = surface;

    /// <summary>
    /// Learn the constants and the constructor parameter names. Both exist for
    /// the KEYS: without them a finding's words are filed under
    /// <c>AudioSetupCheck.Analyze.arg2</c>, and with them under
    /// <c>fixer.finding.mme-in-use.what-is-wrong</c>. A key nobody can read is
    /// a key nobody can navigate by.
    /// </summary>
    public void LearnFrom(string sourceText)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(sourceText).GetRoot();

        foreach (FieldDeclarationSyntax f in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))) continue;
            foreach (VariableDeclaratorSyntax v in f.Declaration.Variables)
            {
                if (v.Initializer?.Value is LiteralExpressionSyntax lit
                    && lit.Token.IsKind(SyntaxKind.StringLiteralToken))
                {
                    _facts.LearnConst(v.Identifier.Text, lit.Token.ValueText);
                }
            }
        }

        foreach (ConstructorDeclarationSyntax c in
                 root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            string type = c.Identifier.Text;
            var names = c.ParameterList.Parameters.Select(p => p.Identifier.Text).ToList();
            // Every arity this constructor can be called at, so an invocation
            // that leaves optional arguments off still finds its names.
            int required = c.ParameterList.Parameters.Count(p => p.Default == null);
            for (int n = required; n <= names.Count; n++)
                _facts.LearnCtor(type, n, names);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Reading
    // ────────────────────────────────────────────────────────────────

    /// <summary>Every editable sentence in one file, in source order.</summary>
    public List<Entry> Read(string repoPath, string sourceText, SurfaceFile file,
                            List<Refusal> skipped)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(sourceText).GetRoot();
        SourceText text = SourceText.From(sourceText);

        // Every string-ish literal, climbed to the largest expression it is
        // part of, then to the whole StringBuilder chain when it sits in one.
        var roots = new List<SyntaxNode>();
        var seen = new HashSet<SyntaxNode>();
        foreach (LiteralExpressionSyntax lit in root.DescendantNodes()
                     .OfType<LiteralExpressionSyntax>())
        {
            if (!lit.Token.IsKind(SyntaxKind.StringLiteralToken)) continue;
            SyntaxNode entryRoot = EntryRootFor(lit);
            if (seen.Add(entryRoot)) roots.Add(entryRoot);
        }

        var entries = new List<Entry>();
        var groupCounters = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (SyntaxNode node in roots.OrderBy(n => n.SpanStart))
        {
            List<Chunk> chunks = ChunksOf(node, sourceText);
            if (chunks.Count == 0) continue;

            Entry? e = BuildEntry(repoPath, node, chunks, sourceText, text, file,
                                  groupCounters, skipped);
            if (e != null) entries.Add(e);
        }

        return entries;
    }

    /// <summary>
    /// The largest node this literal's sentence belongs to: its whole <c>+</c>
    /// tree, and then — if that tree is an argument to <c>Append</c> or
    /// <c>AppendLine</c> — the whole fluent chain, because that chain is one
    /// sentence however many calls it was written as.
    /// </summary>
    private static SyntaxNode EntryRootFor(LiteralExpressionSyntax lit)
    {
        SyntaxNode node = lit;
        while (node.Parent is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.AddExpression))
            node = b;
        while (node.Parent is ParenthesizedExpressionSyntax p) node = p;

        if (node.Parent is ArgumentSyntax arg
            && arg.Parent is ArgumentListSyntax list
            && list.Parent is InvocationExpressionSyntax inv
            && IsBuilderCall(inv))
        {
            SyntaxNode chain = inv;
            while (chain.Parent is MemberAccessExpressionSyntax ma
                   && ma.Parent is InvocationExpressionSyntax outer
                   && ma.Expression == chain
                   && IsBuilderCall(outer))
            {
                chain = outer;
            }
            return chain;
        }

        return node;
    }

    private static bool IsBuilderCall(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax ma
        && (ma.Name.Identifier.Text == "Append" || ma.Name.Identifier.Text == "AppendLine");

    /// <summary>The sentence's pieces, in the order they are spoken.</summary>
    private List<Chunk> ChunksOf(SyntaxNode node, string sourceText)
    {
        var chunks = new List<Chunk>();

        if (node is InvocationExpressionSyntax inv && IsBuilderCall(inv))
        {
            // Walk the chain outward-in, then replay it in call order.
            var calls = new List<InvocationExpressionSyntax>();
            SyntaxNode cursor = inv;
            while (cursor is InvocationExpressionSyntax i && IsBuilderCall(i))
            {
                calls.Add(i);
                cursor = ((MemberAccessExpressionSyntax)i.Expression).Expression;
            }
            calls.Reverse();
            foreach (InvocationExpressionSyntax call in calls)
            {
                if (call.ArgumentList.Arguments.Count != 1) continue;
                Flatten(call.ArgumentList.Arguments[0].Expression, chunks);
            }
        }
        else if (node is ExpressionSyntax expr)
        {
            Flatten(expr, chunks);
        }

        NamePlaceholders(chunks);
        return chunks;
    }

    private static void Flatten(ExpressionSyntax e, List<Chunk> outp)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                Flatten(p.Expression, outp);
                return;

            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression):
                Flatten(b.Left, outp);
                Flatten(b.Right, outp);
                return;

            case LiteralExpressionSyntax l when l.Token.IsKind(SyntaxKind.StringLiteralToken):
                // A verbatim string is left read-only: re-emitting its words
                // would change its FORM as well as its content, and nothing in
                // a surface's prose is written that way.
                bool verbatim = l.Token.Text.StartsWith('@') || l.Token.Text.StartsWith("\"\"\"");
                outp.Add(Chunk.Literal(l.Token.ValueText, l.Span, writable: !verbatim));
                return;

            // A character literal is words too — `.Append('.')` ends a real
            // sentence. Rewriting it emits a string in its place, which every
            // caller here takes just as happily, so a full stop stays editable
            // instead of quietly dropping the sentence it ends out of the file.
            case LiteralExpressionSyntax c when c.Token.IsKind(SyntaxKind.CharacterLiteralToken):
                outp.Add(Chunk.Literal(c.Token.ValueText, c.Span, writable: true));
                return;

            default:
                outp.Add(Chunk.Placeholder("", e.ToString(), e.Span));
                return;
        }
    }

    /// <summary>
    /// Give each moving part a name a person can hold on to. The same
    /// expression always gets the same name, so a value that appears twice in
    /// a sentence reads as the same thing twice.
    /// </summary>
    private static void NamePlaceholders(List<Chunk> chunks)
    {
        var byExpression = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < chunks.Count; i++)
        {
            Chunk c = chunks[i];
            if (c.IsLiteral) continue;

            if (!byExpression.TryGetValue(c.Expression, out string? name))
            {
                name = Naming.PlaceholderName(c.Expression);
                string candidate = name;
                int n = 2;
                while (!taken.Add(candidate)) candidate = name + n++;
                name = candidate;
                byExpression[c.Expression] = name;
            }

            chunks[i] = Chunk.Placeholder(name, c.Expression, c.Span);
        }
    }

    private Entry? BuildEntry(string repoPath, SyntaxNode node, List<Chunk> chunks,
                              string sourceText, SourceText text, SurfaceFile file,
                              Dictionary<string, int> groupCounters,
                              List<Refusal> skipped)
    {
        int line = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;
        string where = repoPath + " " + line;

        // Developer text is not operator text. Exception messages, trace lines
        // and nameof arguments all read like prose and none of them is ever
        // heard by anybody using a radio.
        if (Naming.IsDeveloperText(node))
            return null;

        string member = Naming.EnclosingMemberName(node);
        if (_surface.SkipMembers.Contains(member)) return null;

        string assembled = string.Concat(chunks.Select(c => c.IsLiteral ? c.Text : "{" + c.Text + "}"));

        (string open, string close, string kind, string body) = Naming.StripShell(assembled);

        if (!Naming.LooksLikeProse(body, allowShort: _surface.ShortProseMembers.Contains(member)))
            return null;

        // A FRAGMENT of markup, not a sentence wrapped in it: an element built
        // across several statements leaves half a tag on each. Those halves
        // are not editable prose — an editor moving three words inside one
        // would be editing an HTML attribute — so they are reported rather
        // than offered.
        string withoutTags = Naming.StripTags(body);
        if (withoutTags.Contains('<') || withoutTags.Contains('>'))
        {
            skipped.Add(new Refusal(where,
                "This is part of a piece of markup rather than a whole sentence, so its "
                + "words are not offered for editing: \"" + Naming.Trim(withoutTags, 60)
                + "\". It has been left in the code."));
            return null;
        }

        if (body.Contains('\n') || body.Contains('\r'))
        {
            skipped.Add(new Refusal(where,
                "This text has a line break in it, which the editing file has no way to "
                + "show. It has been left in the code."));
            return null;
        }

        if (chunks.Any(c => c.IsLiteral && !c.Writable))
        {
            skipped.Add(new Refusal(where,
                "This text is built from a character literal or a verbatim string, which "
                + "this tool will not rewrite. It has been left in the code."));
            return null;
        }

        List<Region> regions = RegionsOf(chunks, sourceText, text);
        if (regions.Count == 0) return null;

        if (regions.Any(r => !r.Writable))
        {
            skipped.Add(new Refusal(where, "Part of this text is not safe to rewrite. It "
                                         + "has been left in the code."));
            return null;
        }

        Dictionary<int, int> anchors = EmptyGapAnchors(chunks, regions);

        // Examples the tool can work out for itself, with the surface's own
        // winning wherever it has an opinion.
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Chunk c in chunks.Where(c => !c.IsLiteral))
        {
            string? auto = Naming.AutoExample(c.Expression);
            if (auto != null) examples[c.Text] = auto;
        }
        foreach ((string k, string v) in _surface.Examples)
            if (!k.Contains('#')) examples[k] = v;

        (List<string> candidates, string title, string tail) = Naming.KeyAndLabel(
            node, file, _surface, _facts, kind, groupCounters);

        // One name can stand for different things in different sentences —
        // the same helper called twice. A "key#name" entry in the surface
        // settles the ones a single global example cannot.
        foreach (string candidate in candidates)
            foreach ((string k, string v) in _surface.Examples)
                if (k.StartsWith(candidate + "#", StringComparison.Ordinal))
                    examples[k[(candidate.Length + 1)..]] = v;

        string reads = Naming.FillExamples(body, examples);

        // Where nothing around the words names them, the sentence itself is
        // the heading — with its values FILLED IN, because "Transmit checks —
        // Test TX-4K2P" is something to steer by and "{Name} checks — Test
        // {RunId}" is something to decode.
        if (title.Length == 0) title = Naming.Trim(Naming.StripTags(reads).Trim(), 58);
        if (title.Length == 0) title = Naming.Humanize(member);
        string label = Naming.Trim(title, 70) + " — " + tail;

        return new Entry
        {
            KeyCandidates = candidates,
            Label = label,
            File = repoPath,
            Line = line,
            ShellOpen = open,
            ShellClose = close,
            ShellKind = kind,
            Chunks = chunks,
            Regions = regions,
            EmptyGapAnchors = anchors,
            Text = body,
            Reads = reads,
            HasInlineMarkup = body.Contains('<') && body.Contains('>'),
        };
    }

    /// <summary>
    /// Maximal runs of literals with nothing but whitespace and <c>+</c>
    /// between them — the spans an edit is allowed to replace.
    /// </summary>
    private static List<Region> RegionsOf(List<Chunk> chunks, string sourceText, SourceText text)
    {
        var regions = new List<Region>();

        for (int i = 0; i < chunks.Count; i++)
        {
            if (!chunks[i].IsLiteral) continue;

            int j = i;
            while (j + 1 < chunks.Count && chunks[j + 1].IsLiteral
                   && JoinedOnlyByPlus(sourceText, chunks[j].Span.End, chunks[j + 1].Span.Start))
            {
                j++;
            }

            var span = TextSpan.FromBounds(chunks[i].Span.Start, chunks[j].Span.End);
            LinePosition start = text.Lines.GetLinePosition(span.Start);

            string continuation = "";
            if (j > i)
            {
                // Reproduce the file's own hand for continuation lines rather
                // than imposing this tool's taste on it.
                int secondStart = chunks[i + 1].Span.Start;
                LinePosition second = text.Lines.GetLinePosition(secondStart);
                if (second.Line > start.Line)
                {
                    int lineStart = text.Lines[second.Line].Start;
                    continuation = sourceText[lineStart..secondStart];
                }
            }

            regions.Add(new Region
            {
                Span = span,
                QuoteColumn = start.Character,
                ContinuationPrefix = continuation,
                Writable = true,
            });

            i = j;
        }

        return regions;
    }

    private static bool JoinedOnlyByPlus(string source, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            char c = source[i];
            if (c != '+' && !char.IsWhiteSpace(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Where a gap between moving parts holds no literal at all, the offset new
    /// words are inserted at. Without this, "Use {SuggestedInputDevice}" could
    /// never become "Use the {SuggestedInputDevice} device" — and adding a
    /// missing word is half of what this tool exists for.
    /// </summary>
    private static Dictionary<int, int> EmptyGapAnchors(List<Chunk> chunks, List<Region> regions)
    {
        var placeholders = new List<Chunk>();
        var gapHasRegion = new HashSet<int>();

        int gap = 0;
        foreach (Chunk c in chunks)
        {
            if (c.IsLiteral) gapHasRegion.Add(gap);
            else { placeholders.Add(c); gap++; }
        }

        var anchors = new Dictionary<int, int>();
        for (int g = 0; g <= placeholders.Count; g++)
        {
            if (gapHasRegion.Contains(g)) continue;
            anchors[g] = g < placeholders.Count
                ? placeholders[g].Span.Start        // insert before this moving part
                : placeholders[^1].Span.End;        // or after the last one
        }
        return anchors;
    }

    // ────────────────────────────────────────────────────────────────
    //  Writing
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The edits that turn one entry's words into <paramref name="newText"/>,
    /// or a refusal saying exactly what is wrong with them. Nothing is written
    /// here; the caller collects every file's splices, re-parses the result and
    /// only then commits it.
    /// </summary>
    public static IReadOnlyList<Splice> Splices(Entry entry, string newText, string newline,
                                                out Refusal? refusal)
    {
        refusal = Validate(entry, newText);
        if (refusal != null) return [];

        string full = entry.ShellOpen + newText + entry.ShellClose;
        List<string> gaps = SplitOnPlaceholders(full, entry.PlaceholderOrder);

        var splices = new List<Splice>();

        // Which regions belong to which gap.
        var regionsByGap = new Dictionary<int, List<Region>>();
        int gap = 0, r = 0;
        foreach (Chunk c in entry.Chunks)
        {
            if (c.IsLiteral)
            {
                bool startsRegion = r < entry.Regions.Count
                                    && entry.Regions[r].Span.Start == c.Span.Start;
                if (startsRegion)
                {
                    if (!regionsByGap.TryGetValue(gap, out List<Region>? l))
                        regionsByGap[gap] = l = [];
                    l.Add(entry.Regions[r]);
                    r++;
                }
            }
            else gap++;
        }

        for (int g = 0; g < gaps.Count; g++)
        {
            string words = gaps[g];

            if (regionsByGap.TryGetValue(g, out List<Region>? rs))
            {
                // All of this gap's words go in its first run; any further run
                // in the same gap empties out. In practice a gap has exactly
                // one run — the tool reports it if that ever stops being true.
                splices.Add(new Splice(rs[0].Span, Emit(words, rs[0], newline)));
                for (int k = 1; k < rs.Count; k++)
                    splices.Add(new Splice(rs[k].Span, "\"\""));
            }
            else if (words.Length > 0 && entry.EmptyGapAnchors.TryGetValue(g, out int anchor))
            {
                // No literal here at all: add one beside the moving part.
                bool trailing = g == gaps.Count - 1;
                string added = trailing
                    ? " + " + Quote(words)
                    : Quote(words) + " + ";
                splices.Add(new Splice(new TextSpan(anchor, 0), added));
            }
        }

        return splices;
    }

    /// <summary>
    /// What this tool will not write, said as a sentence naming the key and the
    /// problem. A refusal always leaves the source untouched.
    /// </summary>
    private static Refusal? Validate(Entry entry, string newText)
    {
        if (string.IsNullOrWhiteSpace(newText))
        {
            return new Refusal(entry.Key,
                "The text is empty. An empty string would make the tool say nothing at "
                + "all at this point, which is never the right answer. Put the words back, "
                + "or delete the whole entry from this file to leave the code alone.");
        }

        List<string> want = entry.PlaceholderOrder.ToList();
        List<string> got = Naming.PlaceholdersIn(newText);

        foreach (string name in got.Distinct(StringComparer.Ordinal))
        {
            if (!want.Contains(name, StringComparer.Ordinal))
            {
                return new Refusal(entry.Key,
                    "This text now contains {" + name + "}, which is not one of its moving "
                    + "parts. The parts it has are " + Naming.List(want.Distinct()) + ". If "
                    + "you meant to write a brace as a character, this tool cannot tell the "
                    + "difference — take it out.");
            }
        }

        if (!want.SequenceEqual(got, StringComparer.Ordinal))
        {
            string missing = Naming.List(want.Except(got, StringComparer.Ordinal));
            return new Refusal(entry.Key,
                missing.Length > 0
                    ? "The moving part " + missing + " has been dropped. Every one of them "
                      + "has to stay, because the tool fills it in when it speaks — without "
                      + "it the sentence loses a fact. Put it back where it belongs."
                    : "The moving parts have been reordered or repeated differently. They "
                      + "have to appear in the same order and the same number of times: "
                      + Naming.List(want) + ". Change every word around them freely.");
        }

        if (entry.HasInlineMarkup)
        {
            List<string> before = Naming.TagsIn(entry.Text);
            List<string> after = Naming.TagsIn(newText);
            if (!before.SequenceEqual(after, StringComparer.Ordinal))
            {
                return new Refusal(entry.Key,
                    "The markup in this one has changed. It carries " + Naming.List(before)
                    + " and has to keep exactly those, in that order — they are how the page "
                    + "is built, not part of the sentence.");
            }
        }

        return null;
    }

    /// <summary>
    /// Cut the sentence at its moving parts, giving the words that belong in
    /// each gap. The order is already known to match, so this is a straight
    /// walk.
    /// </summary>
    private static List<string> SplitOnPlaceholders(string text, IReadOnlyList<string> order)
    {
        var gaps = new List<string>();
        int at = 0;
        foreach (string name in order)
        {
            string token = "{" + name + "}";
            int i = text.IndexOf(token, at, StringComparison.Ordinal);
            if (i < 0) { gaps.Add(text[at..]); at = text.Length; continue; }
            gaps.Add(text[at..i]);
            at = i + token.Length;
        }
        gaps.Add(text[at..]);
        return gaps;
    }

    /// <summary>
    /// One region's replacement: a C# string, wrapped across lines the way the
    /// file already wraps them.
    /// </summary>
    private static string Emit(string words, Region region, string newline)
    {
        const int RightMargin = 84;

        string oneLine = Quote(words);
        if (region.QuoteColumn + oneLine.Length <= RightMargin) return oneLine;

        string continuation = region.ContinuationPrefix.Length > 0
            ? region.ContinuationPrefix
            : new string(' ', Math.Max(0, region.QuoteColumn - 2)) + "+ ";

        // Wrap on spaces, keeping the space at the END of a piece so the pieces
        // still concatenate to exactly the same sentence.
        var pieces = new List<string>();
        var current = new StringBuilder();
        int budget = RightMargin - region.QuoteColumn - 3;   // quotes and a little slack
        int contBudget = RightMargin - continuation.Length - 3;

        foreach (string word in SplitKeepingSpaces(words))
        {
            int limit = pieces.Count == 0 ? budget : contBudget;
            if (current.Length > 0 && Escape(current + word).Length > limit)
            {
                pieces.Add(current.ToString());
                current.Clear();
            }
            current.Append(word);
        }
        if (current.Length > 0) pieces.Add(current.ToString());
        if (pieces.Count == 0) pieces.Add(words);

        var sb = new StringBuilder();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (i > 0) sb.Append(newline).Append(continuation);
            sb.Append(Quote(pieces[i]));
        }
        return sb.ToString();
    }

    /// <summary>Words with their trailing space attached, so joining is lossless.</summary>
    private static IEnumerable<string> SplitKeepingSpaces(string s)
    {
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != ' ') continue;
            // Consume a run of spaces with the word before it.
            int end = i;
            while (end < s.Length && s[end] == ' ') end++;
            yield return s[start..end];
            start = end;
            i = end - 1;
        }
        if (start < s.Length) yield return s[start..];
    }

    private static string Quote(string s) => "\"" + Escape(s) + "\"";

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

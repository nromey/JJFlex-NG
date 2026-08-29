using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Prose;

/// <summary>
/// The judgements that turn a syntax node into something a person can read: a
/// key worth navigating by, a heading worth hearing, and a decision about
/// whether this string is words at all.
/// </summary>
public static partial class Naming
{
    // ────────────────────────────────────────────────────────────────
    //  Is this prose?
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Developer text — an exception message, a trace line, a nameof. It reads
    /// exactly like prose and is heard by nobody.
    /// </summary>
    public static bool IsDeveloperText(SyntaxNode node)
    {
        for (SyntaxNode? n = node; n != null; n = n.Parent)
        {
            switch (n)
            {
                case ThrowStatementSyntax:
                case ThrowExpressionSyntax:
                    return true;

                // A switch label is a machine value that happens to be spelled
                // like the word it selects. `case "passed": return "passed";`
                // has one string a person hears and one nobody ever does.
                case SwitchLabelSyntax:
                case ConstantPatternSyntax:
                    return true;

                case ObjectCreationExpressionSyntax oc
                    when oc.Type.ToString().EndsWith("Exception", StringComparison.Ordinal):
                    return true;

                case InvocationExpressionSyntax inv:
                    string name = MethodName(inv);
                    if (name is "nameof" or "TraceLine" or "Trace" or "WriteLine"
                             or "Assert" or "Fail")
                        return true;
                    // A format string or a machine comparison. "yyyy-MM-dd
                    // HH:mm 'UTC'" has spaces and letters and reads as prose
                    // to every test that is not this one.
                    if (name is "ToString" or "Format" or "AppendFormat" or "ParseExact"
                             or "TryParseExact" or "StartsWith" or "EndsWith" or "Contains"
                             or "IndexOf" or "LastIndexOf" or "Split" or "Equals"
                             or "CompareTo" or "Substring")
                        return true;
                    if (inv.Expression.ToString().StartsWith("Tracing.", StringComparison.Ordinal)
                        || inv.Expression.ToString().StartsWith("Debug.", StringComparison.Ordinal))
                        return true;
                    break;

                case MethodDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case ClassDeclarationSyntax:
                    return false;   // stop climbing at the member
            }
        }
        return false;
    }

    /// <summary>
    /// Words a person will hear, as opposed to an id, a CSS class, a path or a
    /// wire value.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative, and paired with a report of everything it
    /// turned away: a false negative leaves a string un-editable and visible in
    /// the skipped list, while a false positive puts machine values in front of
    /// an editor as if they were sentences.
    /// </remarks>
    public static bool LooksLikeProse(string body, bool allowShort)
    {
        // A space in the WHOLE text is the test, placeholders included: a
        // template with a moving part is a sentence ("{Count} passed"), while
        // an id built the same way is not ("fix-{stageId}-{findingId}"), and
        // the difference between them is exactly whether a person would ever
        // put a space in it.
        bool hasSpace = body.Trim().Contains(' ');

        // What is left once the markup and the moving parts are gone: the
        // actual words.
        string bare = PlaceholderPattern().Replace(StripTags(body), "").Trim();
        if (bare.Length == 0) return false;
        if (!bare.Any(char.IsLetter)) return false;
        if (bare.Count(char.IsLetter) < 2) return false;

        // No space anywhere: an id, a path, a wire value — unless this member
        // is one the surface has named as speaking in single words. There is
        // no heuristic for that last case and there should not be one:
        // StatusPhrase returning "passed" and StatusOf returning "notrun" are
        // the same shape, and only one of them is heard by a person.
        return hasSpace || allowShort;
    }

    // ────────────────────────────────────────────────────────────────
    //  Markup
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Take the markup that merely WRAPS the words off the front and back, and
    /// remember it. An editor should meet "Stop everything", not
    /// <c>&lt;p&gt;&lt;button type="button" data-action="stop"&gt;Stop
    /// everything&lt;/button&gt;&lt;/p&gt;</c>. Markup that sits INSIDE the
    /// words stays visible, because moving it is an edit to the sentence.
    /// </summary>
    public static (string Open, string Close, string Kind, string Body) StripShell(string text)
    {
        var opens = new StringBuilder();
        var closes = new StringBuilder();
        string kind = "";
        string body = text;

        while (true)
        {
            // Leading and trailing spaces are JOINING GLUE, not words — this
            // fragment is about to be stuck onto the one before or after it.
            // They come off so the editing file shows a clean sentence, and go
            // back on untouched, because a lost space at a join is one of the
            // ways a sentence quietly loses a word.
            string trimmed = body.Trim(' ');
            if (trimmed.Length != body.Length && trimmed.Length > 0)
            {
                int lead = body.Length - body.TrimStart(' ').Length;
                opens.Append(body[..lead]);
                closes.Insert(0, body[(lead + trimmed.Length)..]);
                body = trimmed;
                continue;
            }

            Match m = WrapPattern().Match(body);
            if (!m.Success) break;

            string tag = m.Groups["tag"].Value.ToLowerInvariant();
            opens.Append(m.Groups["open"].Value);
            closes.Insert(0, m.Groups["close"].Value);
            if (kind.Length == 0 || kind == "paragraph") kind = KindOf(tag);
            body = m.Groups["body"].Value;
        }

        return (opens.ToString(), closes.ToString(), kind.Length > 0 ? kind : "text", body);
    }

    private static string KindOf(string tag) => tag switch
    {
        "p" => "paragraph",
        "li" => "bullet",
        "button" => "button",
        "summary" => "disclosure summary",
        "legend" => "question above the choices",
        "label" => "choice",
        "h1" or "h2" or "h3" => "heading",
        "strong" or "em" => "text",
        "a" => "link",
        "td" or "th" => "cell",
        _ => tag,
    };

    /// <summary>Every tag in the text, in order, for the keep-the-markup check.</summary>
    public static List<string> TagsIn(string text) =>
        AnyTagPattern().Matches(text).Select(m => m.Value).ToList();

    public static string StripTags(string text) => AnyTagPattern().Replace(text, "");

    // ────────────────────────────────────────────────────────────────
    //  Placeholders
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A name for a value the program fills in, taken from the expression that
    /// produces it. Wrappers that do not change the VALUE — escaping, trimming,
    /// formatting — are seen through, so <c>Esc(run.RunId)</c> reads as
    /// <c>{RunId}</c> rather than <c>{Esc}</c>.
    /// </summary>
    public static string PlaceholderName(string expression)
    {
        ExpressionSyntax e = SyntaxFactory.ParseExpression(expression);
        string name = NameOf(e, depth: 0);
        name = NonWordPattern().Replace(name, "");
        return name.Length > 0 ? name : "value";
    }

    private static readonly HashSet<string> Transparent = new(StringComparer.Ordinal)
    {
        "Esc", "Attr", "Cap", "ToString", "Trim", "TrimStart", "TrimEnd",
        "ToUpperInvariant", "ToLowerInvariant", "ToUpper", "ToLower",
    };

    private static string NameOf(ExpressionSyntax e, int depth)
    {
        if (depth > 6) return "value";

        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                return NameOf(p.Expression, depth + 1);

            case IdentifierNameSyntax id:
                return id.Identifier.Text;

            case MemberAccessExpressionSyntax ma:
                return ma.Name.Identifier.Text;

            case ConditionalAccessExpressionSyntax ca:
                return NameOf(ca.WhenNotNull, depth + 1) is { Length: > 0 } n && n != "value"
                    ? n : NameOf(ca.Expression, depth + 1);

            case MemberBindingExpressionSyntax mb:
                return mb.Name.Identifier.Text;

            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression):
                return NameOf(b.Left, depth + 1);

            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression):
                // A joined-up expression: name it after the first part that
                // actually varies, since the fixed parts say nothing.
                return IsAllText(b.Left) ? NameOf(b.Right, depth + 1) : NameOf(b.Left, depth + 1);

            case ConditionalExpressionSyntax c when IsAllText(c.WhenTrue) && IsAllText(c.WhenFalse):
                // "watts" — or, where the two wordings are unrelated,
                // "resultOrNothing". Named after what it can SAY, which beats
                // anything about the test that picks between them.
                return EitherName(AllText(c.WhenTrue), AllText(c.WhenFalse));

            case ConditionalExpressionSyntax c:
                return NameOf(c.WhenTrue, depth + 1);

            case InvocationExpressionSyntax inv:
            {
                string method = MethodName(inv);
                if (Transparent.Contains(method))
                {
                    if (inv.ArgumentList.Arguments.Count > 0 && method is "Esc" or "Attr" or "Cap")
                        return NameOf(inv.ArgumentList.Arguments[0].Expression, depth + 1);
                    if (inv.Expression is MemberAccessExpressionSyntax target)
                        return NameOf(target.Expression, depth + 1);
                }
                return method;
            }

            default:
                return "value";
        }
    }

    private static bool IsText(ExpressionSyntax e) =>
        e is LiteralExpressionSyntax l && l.Token.IsKind(SyntaxKind.StringLiteralToken);

    /// <summary>
    /// Words all the way down: a literal, or literals joined with <c>+</c>. A
    /// wording wrapped across three source lines is still one wording, and
    /// treating it as an opaque expression is how a whole branch of a sentence
    /// ends up named "value".
    /// </summary>
    private static bool IsAllText(ExpressionSyntax e) => e switch
    {
        ParenthesizedExpressionSyntax p => IsAllText(p.Expression),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression)
            => IsAllText(b.Left) && IsAllText(b.Right),
        _ => IsText(e),
    };

    private static string AllText(ExpressionSyntax e) => e switch
    {
        ParenthesizedExpressionSyntax p => AllText(p.Expression),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression)
            => AllText(b.Left) + AllText(b.Right),
        LiteralExpressionSyntax l => l.Token.ValueText,
        _ => "",
    };

    private static string Text(ExpressionSyntax e) =>
        e is LiteralExpressionSyntax l ? l.Token.ValueText : "";

    /// <summary>The last real word in a piece of text, as an identifier.</summary>
    private static string WordIn(string s)
    {
        string[] words = s.Split([' ', ',', '.', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        string last = words.LastOrDefault(w => w.Any(char.IsLetter)) ?? "";
        last = NonWordPattern().Replace(last, "");
        return last.Length == 0 ? "" : char.ToLowerInvariant(last[0]) + last[1..];
    }

    /// <summary>A name for one of two wordings.</summary>
    private static string EitherName(string a, string b)
    {
        string x = WordIn(a), y = WordIn(b);
        if (x.Length == 0) return y.Length == 0 ? "text" : y;
        if (y.Length == 0) return x;

        // "watt" and "watts" are the same word twice — take the whole one.
        if (x.StartsWith(y, StringComparison.Ordinal)) return x;
        if (y.StartsWith(x, StringComparison.Ordinal)) return y;

        return x + "Or" + char.ToUpperInvariant(y[0]) + y[1..];
    }

    /// <summary>
    /// A value this tool can work out for itself, so the "Reads as" line is a
    /// real sentence without anybody hand-writing an example: a constant's own
    /// text, or the general case of a two-way choice between two wordings.
    /// </summary>
    public static string? AutoExample(string expression)
    {
        ExpressionSyntax e = SyntaxFactory.ParseExpression(expression);

        if (e is ConditionalExpressionSyntax c && IsAllText(c.WhenTrue) && IsAllText(c.WhenFalse))
        {
            // The longer branch is the general case nearly every time — "watts"
            // rather than the singular "watt" a count of one would take.
            string a = AllText(c.WhenTrue), b = AllText(c.WhenFalse);
            return a.Length >= b.Length ? a : b;
        }

        return IsAllText(e) ? AllText(e) : null;
    }

    public static string MethodName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        _ => "value",
    };

    /// <summary>The <c>{name}</c> tokens in a piece of text, in order.</summary>
    public static List<string> PlaceholdersIn(string text) =>
        PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).ToList();

    /// <summary>
    /// The sentence as an operator will actually hear it, with realistic values
    /// in place of the moving parts. A name with no example shows in capitals,
    /// so a missing example is visible rather than quietly plausible.
    /// </summary>
    public static string FillExamples(string text, IDictionary<string, string> examples) =>
        PlaceholderPattern().Replace(text, m =>
        {
            string name = m.Groups[1].Value;
            return examples.TryGetValue(name, out string? v) ? v : name.ToUpperInvariant();
        });

    // ────────────────────────────────────────────────────────────────
    //  Keys and headings
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A key shaped like a Lexicon key — <c>fixer.stage.audio-setup.explanation</c>
    /// — and the heading a person navigates by.
    /// </summary>
    /// <remarks>
    /// The key is built from the SEMANTICS around the string, never from a line
    /// number: the id of the stage it belongs to, the constructor parameter it
    /// fills, the property it is assigned to. That is what lets an edit survive
    /// the file being rewritten underneath it.
    /// </remarks>
    public static (List<string> Candidates, string Title, string Tail) KeyAndLabel(
        SyntaxNode node, SurfaceFile file, Surface surface,
        SourceFacts facts, string shellKind,
        Dictionary<string, int> counters)
    {
        List<GroupInfo> chain = GroupChain(node, file, facts);
        GroupInfo inner = chain[0];
        GroupInfo? outer = chain.Count > 1 ? chain[1] : null;

        string slot = Slot(node, facts);
        string condition = ConditionSuffix(node);
        string branch = condition.Length > 0 ? Kebab(condition.Replace(' ', '-')) : "";

        var head = new List<string> { surface.Id, inner.Kind };
        var tailParts = new List<string>();
        if (inner.Id.Length > 0) tailParts.Add(inner.Id);
        if (inner.Discriminator.Length > 0) tailParts.Add(inner.Discriminator);

        string label;
        if (slot.Length > 0)
        {
            label = surface.SlotLabels.TryGetValue(Kebab(slot), out string? l) ? l : Humanize(slot);
            tailParts.Add(slot);
        }
        else
        {
            // No named slot: number within the group, so a page paragraph is
            // still findable and still stable while its neighbours are edited.
            string bucket = string.Join(".", head.Concat(tailParts)) + "/" + shellKind;
            counters.TryGetValue(bucket, out int n);
            counters[bucket] = ++n;
            tailParts.Add(Kebab(shellKind) + "-" + n);
            label = shellKind + " " + n;
        }

        // Candidates, shortest first. Whichever is the shortest one nothing
        // else wants gets used — worked out across the whole surface, never in
        // the order the files happened to be read, so a key does not depend on
        // which of two colliding entries came first.
        string Join(IEnumerable<string> extra) =>
            string.Join(".", head.Concat(extra).Concat(tailParts).Select(Kebab));

        var candidates = new List<string> { Join([]) };
        if (branch.Length > 0) candidates.Add(Join([branch]));
        if (outer is { Id.Length: > 0 })
        {
            candidates.Add(Join(branch.Length > 0 ? [outer.Id, branch] : [outer.Id]));
            if (inner.LinkSlot.Length > 0)
                candidates.Add(Join(branch.Length > 0
                    ? [outer.Id, inner.LinkSlot, branch]
                    : [outer.Id, inner.LinkSlot]));
        }

        // Where nothing around the words names them, the WORDS name them —
        // an empty title asks the caller to preview the sentence itself. A
        // heading of "Request — text 4" is useless for finding the sentence
        // you want to fix, and heading navigation is how this file is walked.
        string title = inner.Name.Length > 0 ? inner.Name
                     : !inner.FromMember && inner.Id.Length > 0 ? Humanize(inner.Id)
                     : "";

        if (outer is { Name.Length: > 0 } && inner.Name.Length > 0)
            title = Trim(outer.Name, 40) + " · " + title;

        if (inner.Discriminator.Length > 0)
            label += " (" + Humanize(inner.Discriminator).ToLowerInvariant() + ")";
        if (condition.Length > 0) label += ", " + condition;

        return (candidates, title, label);
    }

    /// <summary>What a set of words belongs to, and what that thing calls itself.</summary>
    public sealed record GroupInfo(string Kind, string Id, string Name,
                                   string Discriminator, string LinkSlot,
                                   bool FromMember = false);

    /// <summary>
    /// The thing these words belong to: a stage, a finding, a choice — with its
    /// stable id, and the words it calls itself by.
    /// </summary>
    /// <summary>
    /// The things these words belong to, innermost first — a choice inside a
    /// declaration inside a stage. At most two, because a key nobody can say
    /// out loud is a key nobody navigates by.
    /// </summary>
    private static List<GroupInfo> GroupChain(SyntaxNode node, SurfaceFile file,
                                              SourceFacts facts)
    {
        var chain = new List<GroupInfo>();

        for (SyntaxNode? n = node; n != null && chain.Count < 2; n = n.Parent)
        {
            if (n is not BaseObjectCreationExpressionSyntax oc) continue;

            string type = oc is ObjectCreationExpressionSyntax o ? o.Type.ToString() : "";
            string kind = GroupKind(type);
            if (kind.Length == 0) continue;

            GroupInfo? info = Describe(oc, type, kind, facts);
            if (info != null) chain.Add(info);
        }

        if (chain.Count == 0)
        {
            // Nothing named around it: the file's own group and the member it
            // is in. The member name is what makes a page paragraph findable.
            chain.Add(new GroupInfo(file.Group.Length > 0 ? file.Group : "text",
                                    Kebab(EnclosingMemberName(node)), "", "", "",
                                    FromMember: true));
        }

        return chain;
    }

    private static GroupInfo? Describe(BaseObjectCreationExpressionSyntax oc, string type,
                                       string kind, SourceFacts facts)
    {
        string id = "", name = "", disc = "", number = "";

        // An Id in the initializer, or the first constructor argument.
        if (oc.Initializer != null)
        {
            foreach (ExpressionSyntax expr in oc.Initializer.Expressions)
            {
                if (expr is not AssignmentExpressionSyntax a) continue;
                string prop = a.Left.ToString();

                if (prop == "Number") { number = a.Right.ToString().Trim(); continue; }

                string? value = ConstOf(a.Right, facts);
                if (value == null) continue;
                if (prop == "Id") id = value;
                else if (prop is "Title" or "Label" or "Name") name = value;
            }
        }

        if (oc.ArgumentList is { Arguments.Count: > 0 } args)
        {
            if (id.Length == 0)
            {
                string? first = ConstOf(args.Arguments[0].Expression, facts);
                if (first != null) id = first;
            }

            for (int i = 1; i < args.Arguments.Count; i++)
            {
                ArgumentSyntax arg = args.Arguments[i];

                // The words a thing calls itself by come from the parameter
                // NAMED as its label — never from "the first argument with a
                // space in it", which happily grabbed a finding's whole
                // what-is-wrong sentence and used it as the heading for that
                // very sentence.
                string param = arg.NameColon?.Name.Identifier.Text
                            ?? facts.CtorParam(type, args.Arguments.Count, i) ?? "";
                if (name.Length == 0 && param is "label" or "title" or "name" or "question"
                    && arg.Expression is LiteralExpressionSyntax lit
                    && lit.Token.IsKind(SyntaxKind.StringLiteralToken))
                {
                    name = lit.Token.ValueText;
                }

                // An enum argument tells two entries with the same id apart —
                // the MME finding we can fix from the one nobody can.
                if (disc.Length == 0
                    && arg.Expression is MemberAccessExpressionSyntax ma
                    && char.IsUpper(ma.Name.Identifier.Text.FirstOrDefault('a')))
                {
                    disc = ma.Name.Identifier.Text;
                }
            }
        }

        // A stage says its own number out loud, because that is how the page
        // names it and how an operator refers to it.
        if (number.Length > 0 && name.Length > 0) name = "Stage " + number + " " + name;

        if (id.Length == 0 && name.Length == 0) return null;
        return new GroupInfo(kind, id, name, disc, Slot(oc, facts));
    }

    private static string GroupKind(string type) => type switch
    {
        "FixerStage" => "stage",
        "FixerRunDeclaration" => "declaration",
        "FixerDeclarationChoice" => "answer",
        "FixerSkipChoice" => "skip",
        "FixerFinding" => "finding",
        "FixerHostAction" => "action",
        "FixerStageSet" => "set",
        "FixerOutcome" => "outcome",
        _ => "",
    };

    /// <summary>
    /// What this string IS within its group: the property it is assigned to, or
    /// the constructor parameter it fills.
    /// </summary>
    private static string Slot(SyntaxNode node, SourceFacts facts)
    {
        for (SyntaxNode? n = node; n != null; n = n.Parent)
        {
            if (n is AssignmentExpressionSyntax a && a.Parent is InitializerExpressionSyntax)
                return Kebab(a.Left.ToString());

            if (n is ArgumentSyntax arg)
            {
                if (arg.NameColon != null) return Kebab(arg.NameColon.Name.Identifier.Text);
                if (arg.Parent is ArgumentListSyntax list
                    && list.Parent is BaseObjectCreationExpressionSyntax oc)
                {
                    string type = oc is ObjectCreationExpressionSyntax o ? o.Type.ToString() : "";
                    int index = list.Arguments.IndexOf(arg);
                    string? param = facts.CtorParam(type, list.Arguments.Count, index);
                    if (param != null) return Kebab(param);
                }
                return "";
            }

            // A property or method name is NOT a slot: the group already falls
            // back to it, and "Load declaration for report — load declaration
            // for report" is what naming a thing twice sounds like.
            if (n is PropertyDeclarationSyntax or MethodDeclarationSyntax) return "";
        }
        return "";
    }

    /// <summary>
    /// "when the run is saved" / "otherwise", for the two halves of a
    /// conditional. Two wordings that differ only by a condition are otherwise
    /// impossible to tell apart by heading.
    /// </summary>
    private static string ConditionSuffix(SyntaxNode node)
    {
        for (SyntaxNode? n = node; n != null; n = n.Parent)
        {
            if (n.Parent is not ConditionalExpressionSyntax c) continue;
            if (c.WhenTrue == n) return "when " + Condition(c.Condition);
            if (c.WhenFalse == n) return "otherwise";
        }
        return "";
    }

    /// <summary>
    /// A condition as words. Not clever — it drops the object it hangs off,
    /// splits the name, and says the operator out loud — but "when the
    /// transmit count is at most 0" is a heading and
    /// <c>state.TransmitCount &lt;= 0</c> is not.
    /// </summary>
    public static string Condition(ExpressionSyntax e) => (e switch
    {
        ParenthesizedExpressionSyntax p => Condition(p.Expression),
        PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression)
            => "not " + Condition(u.Operand),
        MemberAccessExpressionSyntax ma => Humanize(ma.Name.Identifier.Text),
        IdentifierNameSyntax id => Humanize(id.Identifier.Text),
        InvocationExpressionSyntax inv => Humanize(MethodName(inv)),
        BinaryExpressionSyntax b =>
            Condition(b.Left) + " " + Operator(b) + " " + Condition(b.Right),
        LiteralExpressionSyntax l => l.Token.ValueText,
        _ => e.ToString(),
    }).ToLowerInvariant();

    private static string Operator(BinaryExpressionSyntax b) => b.Kind() switch
    {
        SyntaxKind.EqualsExpression => "is",
        SyntaxKind.NotEqualsExpression => "is not",
        SyntaxKind.LessThanOrEqualExpression => "is at most",
        SyntaxKind.GreaterThanOrEqualExpression => "is at least",
        SyntaxKind.LessThanExpression => "is under",
        SyntaxKind.GreaterThanExpression => "is over",
        SyntaxKind.LogicalAndExpression => "and",
        SyntaxKind.LogicalOrExpression => "or",
        _ => b.OperatorToken.Text,
    };

    private static string? ConstOf(ExpressionSyntax e, SourceFacts facts)
    {
        if (e is LiteralExpressionSyntax lit && lit.Token.IsKind(SyntaxKind.StringLiteralToken))
            return lit.Token.ValueText;
        if (e is IdentifierNameSyntax id) return facts.Const(id.Identifier.Text);
        if (e is MemberAccessExpressionSyntax ma) return facts.Const(ma.Name.Identifier.Text);
        return null;
    }

    public static string EnclosingMemberName(SyntaxNode node)
    {
        for (SyntaxNode? n = node; n != null; n = n.Parent)
        {
            switch (n)
            {
                case MethodDeclarationSyntax m: return m.Identifier.Text;
                case PropertyDeclarationSyntax p: return p.Identifier.Text;
                case ConstructorDeclarationSyntax c: return c.Identifier.Text;

                // A FIELD's name is a real name for the words in it. A local's
                // is not — "sb" and "id" say nothing about what is being said,
                // and they were the headings until this case learned to tell
                // the two apart.
                case VariableDeclaratorSyntax v
                    when v.Parent?.Parent is FieldDeclarationSyntax:
                    return v.Identifier.Text;
            }
        }
        return "";
    }

    // ────────────────────────────────────────────────────────────────
    //  Words
    // ────────────────────────────────────────────────────────────────

    public static string Kebab(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c is '_' or ' ' or '.') sb.Append('-');
            else sb.Append(c);
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>"what-is-wrong" and "WhatIsWrong" both read back as "what is wrong".</summary>
    public static string Humanize(string s)
    {
        string spaced = Kebab(s).Replace('-', ' ').Trim();
        return spaced.Length == 0 ? "" : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    public static string Trim(string s, int max = 62)
    {
        s = s.Trim();
        if (s.Length <= max) return s;
        int cut = s.LastIndexOf(' ', max - 1);
        return (cut > 20 ? s[..cut] : s[..(max - 1)]) + "…";
    }

    public static string List(IEnumerable<string> names)
    {
        List<string> l = names.Select(n => "{" + n + "}").ToList();
        return l.Count switch
        {
            0 => "",
            1 => l[0],
            2 => l[0] + " and " + l[1],
            _ => string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1],
        };
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^(?<open><(?<tag>[a-zA-Z][a-zA-Z0-9]*)(?:\s[^<>]*)?>)(?<body>.*)(?<close></\k<tag>>)$",
                    RegexOptions.Singleline)]
    private static partial Regex WrapPattern();

    [GeneratedRegex(@"</?[a-zA-Z][^<>]*>|<![^<>]*>", RegexOptions.ExplicitCapture)]
    private static partial Regex AnyTagPattern();

    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex NonWordPattern();
}

/// <summary>
/// What one reading of the source learned about itself: the string constants,
/// and the constructor parameter names.
/// </summary>
/// <remarks>
/// <b>Carried, not static.</b> This was a static side table until the suite
/// caught it: two runs of the tool in one process — which is exactly what a
/// test suite is — cleared and refilled it under each other, and keys came out
/// different depending on the interleaving. A fact about the codebase is still
/// a fact about the READING of it, and readings can overlap.
/// </remarks>
public sealed class SourceFacts
{
    private readonly Dictionary<string, string> _consts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _ctors = new(StringComparer.Ordinal);

    public void LearnConst(string name, string value) => _consts[name] = value;

    public void LearnCtor(string type, int arity, List<string> names) =>
        _ctors.TryAdd(type + "/" + arity, names);

    public string? Const(string name) =>
        _consts.TryGetValue(name, out string? v) ? v : null;

    public string? CtorParam(string type, int arity, int index) =>
        _ctors.TryGetValue(type + "/" + arity, out List<string>? names) && index < names.Count
            ? names[index]
            : null;
}

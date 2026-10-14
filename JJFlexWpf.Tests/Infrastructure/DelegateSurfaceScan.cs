using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>One delegate-shaped hook, who owns it, and what the codebase does with it.</summary>
/// <param name="Name">The hook's member name.</param>
/// <param name="Owner">The short name of the type that declares it. Two hooks
/// called <c>TraceAction</c> on two dialogs are two surfaces, not one.</param>
/// <param name="DeclaredIn">File and line of the declaration, repo-relative.</param>
/// <param name="IsStatic">Declared <c>static</c>, so reachable without the owner
/// being constructed.</param>
/// <param name="Invoked">At least one callsite whose receiver resolves to the owner.</param>
/// <param name="Assigned">At least one non-null assignment whose receiver resolves to the owner.</param>
/// <param name="OwnerConstructed">Something in product code constructs the owner
/// (or the hook is static). False means no operator can reach the surface
/// at all, whatever its hooks do.</param>
/// <param name="InvokedFrom">Attributed callsites, repo-relative file:line.</param>
/// <param name="AssignedFrom">Attributed assignment sites.</param>
/// <param name="NameOnlyMentions">Matches on the bare name whose receiver could
/// not be tied to the owner. Reported so a reader can see what was NOT
/// counted; never evidence either way.</param>
public sealed record DelegateSurface(
    string Name,
    string Owner,
    string DeclaredIn,
    bool IsStatic,
    bool Invoked,
    bool Assigned,
    bool OwnerConstructed,
    IReadOnlyList<string> InvokedFrom,
    IReadOnlyList<string> AssignedFrom,
    IReadOnlyList<string> NameOnlyMentions)
{
    /// <summary><c>Owner.Name</c>, the key the exception lists use.</summary>
    public string Key => Owner + "." + Name;

    /// <summary>
    /// Declared, called at a real callsite, and never given a value anywhere in
    /// the tree. The menu item exists, the key works, the call happens, and
    /// nothing occurs. This is the shape of the TX Controls defect (#109).
    /// </summary>
    public bool IsDeadSurface => Invoked && !Assigned;

    /// <summary>A dead hook on a surface an operator can actually open. The
    /// #109 and #700 class: fix it, then press it.</summary>
    public bool IsReachableDeadSurface => IsDeadSurface && OwnerConstructed;

    /// <summary>A dead hook on a surface nothing constructs. The #482 class:
    /// the dialog itself has no door, so the hook is moot until somebody
    /// decides the dialog's fate.</summary>
    public bool IsOrphanedDeadSurface => IsDeadSurface && !OwnerConstructed;
}

/// <summary>
/// Invariant 7, and the one check in this suite that reads source rather than
/// walking a tree.
///
/// <para><b>Why it is here at all.</b> The tree walk answers "is this dialog's
/// content real"; it cannot answer "does anything ever open this dialog". A hook
/// that is declared, invoked from a menu handler, and never assigned produces a
/// menu item that does nothing - and there is no window to walk, because no
/// window is ever created. Confirming that class needs the callsites, so this
/// check reads them.</para>
///
/// <para><b>Rewritten for #591 (Sprint 48 Track D), and the four things that
/// changed are each a defect the first version had.</b></para>
///
/// <para><b>1. It reads the commit, not the disk.</b> The first version walked
/// <c>"."</c> with a three-name vendor exclusion, so a gitignored
/// <c>flexlib4218/</c> reference copy lying in one checkout made the same
/// commit report 100 findings there and 101 in a clean worktree. An instrument
/// that answers differently in two checkouts of one commit cannot be believed
/// in either. The corpus is now <c>git ls-files</c> plus untracked files that
/// are not ignored - so a brand-new file counts at the moment somebody most
/// wants to be told, and a stray reference clone never does. Same reasoning
/// and same mechanism as <c>Radios.Tests.IntegrationPassTree</c>.</para>
///
/// <para><b>2. The unit is (owner type, hook name), not the bare name.</b> The
/// first version grouped every declaration of a name into one row, so
/// <c>TraceAction</c> on three dialogs was one surface, and an assignment to
/// any class's <c>SetName</c> anywhere in the tree counted as wiring
/// <c>MemoriesDialog.SetName</c>. Four of <c>RadioPaneControl</c>'s dead
/// hooks were masked exactly that way.</para>
///
/// <para><b>3. Evidence is attributed to a receiver, not matched on a word.</b>
/// An assignment or a call counts only when its receiver can be tied to the
/// owner: the owner's own files, an object-initialiser block of the owner, a
/// receiver variable declared with the owner's type (or a type in its
/// inheritance chain), a VB <c>With</c> block on such a variable, an
/// <c>x:Name</c>d XAML element of the type, or the type itself for statics.
/// Everything else is a <see cref="DelegateSurface.NameOnlyMentions"/> entry:
/// shown, never counted. This is what stops <c>FlexBase.Disconnect()</c>
/// being read as a call to <c>ClusterDialog.Disconnect</c>, and it is what
/// stops <c>KeyCommands.SetMode(...)</c> appearing as a callsite of the
/// Memories dialog.</para>
///
/// <para><b>4. <c>X = null</c> is not an assignment.</b> The first version's
/// pattern counted a teardown as wiring, so a control whose <c>Cleanup()</c>
/// nulls its own hooks looked wired by its own disconnect code. Nine of
/// <c>RadioPaneControl</c>'s ten hooks were invisible for that reason - the
/// very instance #591 was opened to find. #482 had already documented the
/// trap; this is the scan finally honouring it.</para>
///
/// <para>Comments and string literals are blanked by a small tokenizer rather
/// than cut at the first <c>//</c>, which used to truncate any line carrying a
/// URL in a string.</para>
///
/// <para><b>What it still cannot do, said plainly.</b> It is a regex scan with
/// a symbol table, not a compiler. It resolves a receiver by the nearest
/// declaration of that identifier above the use in the same file, and failing
/// that by a globally unique declaration; an identifier declared with two
/// different types in two files and used in a third is unresolved and goes to
/// the name-only list. Target-typed <c>new()</c> counts as construction only
/// when the declaration names the type on the same line. A hook handed on as a
/// method group (<c>Guarded(dialog.Hook, ...)</c>) is not counted as a call.
/// Each of those errs toward "not evidence", which for the assignment side
/// means a possible false finding somebody will read and dismiss, and for the
/// invocation side means a possible miss. The positive controls in
/// <c>DelegateSurfaceTests</c> pin the shapes this codebase actually uses.</para>
/// </summary>
public static class DelegateSurfaceScan
{
    /// <summary>Where hooks are declared. The VB application declares none of
    /// this shape; it consumes them.</summary>
    private static readonly string[] DeclarationRoots = { "JJFlexWpf", "Radios", "JJFlexControl", "JJFlexUpdater" };

    /// <summary>First path segments that are not ours. Mirrors
    /// <c>IntegrationPassTree.VendorRoots</c>.</summary>
    private static readonly string[] VendorRoots =
        { "FlexLib_API", "P-Opus-master", "PortAudioSharp-src-0.19.3", "runtimes", "tools" };

    /// <summary>
    /// Below this the corpus is assumed missing. Measured 2026-10-14 at about
    /// 1,000 product source files; the floor is far below that so growth and
    /// deletion never trip it, and far above zero so a broken root-finder or a
    /// failed git call always does.
    /// </summary>
    private const int MinimumCorpusFiles = 300;

    private static readonly Regex DeclarationPattern = new(
        @"^\s*public\s+(?:static\s+)?(?:required\s+)?(?:Action|Func)\b[^=;]*?\b(?<name>[A-Za-z_]\w*)\s*\{\s*get;\s*(?:private\s+|init\s+|protected\s+)?(?:set|init);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FieldPattern = new(
        @"^\s*public\s+(?:static\s+)?(?:Action|Func)\b[^=;]*?\b(?<name>[A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TypeDeclarationPattern = new(
        @"^(?<indent>\s*)(?:(?:public|internal|private|protected|sealed|abstract|static|partial|new)\s+)*(?:class|record(?:\s+class|\s+struct)?|struct)\s+(?<name>[A-Z]\w*)(?:<[^>]*>)?\s*(?::\s*(?<base>[\w.]+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VbClassPattern = new(
        @"^\s*(?:(?:Public|Friend|Private|Protected|NotInheritable|MustInherit|Partial)\s+)*Class\s+(?<name>[A-Z]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VbInheritsPattern = new(
        @"^\s*Inherits\s+(?<base>[\w.]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---- symbol-table shapes --------------------------------------------

    /// <summary>C#: <c>Type name</c> as a field, property, local or parameter.
    /// The type must start uppercase, which keeps <c>var</c>, <c>return</c>
    /// and friends out without a keyword list.</summary>
    private static readonly Regex CsTypedIdentifier = new(
        @"(?<![\w.])(?<type>[A-Z]\w*)(?:<[^<>;]*>)?\??\s+(?<name>[A-Za-z_]\w*)\s*(?:[;=,)]|\{\s*get|\{\s*$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>C#: <c>x = new T(</c>, <c>var x = new T {</c>.</summary>
    private static readonly Regex CsNewAssignment = new(
        @"(?<![\w.])(?<name>[A-Za-z_]\w*)\s*=\s*new\s+(?:[\w.]+\.)?(?<type>[A-Z]\w*)\s*[({]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>C#: <c>T x = new()</c>, the target-typed form.</summary>
    private static readonly Regex CsTargetTypedNew = new(
        @"(?<![\w.])(?<type>[A-Z]\w*)(?:<[^<>;]*>)?\??\s+(?<name>[A-Za-z_]\w*)\s*(?:\{\s*get;[^}]*\}\s*)?=\s*new\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>VB: <c>name As Type</c>, <c>name As New Type</c>.</summary>
    private static readonly Regex VbTypedIdentifier = new(
        @"(?<![\w.])(?<name>[A-Za-z_]\w*)\s+As\s+(?:New\s+)?(?:[\w.]+\.)?(?<type>[A-Z]\w*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>VB: <c>name = New Type(</c>.</summary>
    private static readonly Regex VbNewAssignment = new(
        @"(?<![\w.])(?<name>[A-Za-z_]\w*)\s*=\s*New\s+(?:[\w.]+\.)?(?<type>[A-Z]\w*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>XAML: an element with an <c>x:Name</c> or <c>Name</c>.</summary>
    private static readonly Regex XamlNamedElement = new(
        @"<(?:\w+:)?(?<type>[A-Z]\w*)\b[^>]*?\b(?:x:)?Name\s*=\s*""(?<name>\w+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>XAML: any element of the type, named or not. Construction.</summary>
    private static readonly Regex XamlElement = new(
        @"<(?:\w+:)?(?<type>[A-Z]\w*)[\s/>]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>C#: the start of an object initialiser, <c>new T(...) {</c> or <c>new T {</c>.</summary>
    private static readonly Regex CsInitialiserStart = new(
        @"\bnew\s+(?:[\w.]+\.)?(?<type>[A-Z]\w*)(?:<[^<>]*>)?\s*(?:\((?:[^()]|\([^()]*\))*\))?\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>VB: <c>New T(...) With {</c>.</summary>
    private static readonly Regex VbInitialiserStart = new(
        @"\bNew\s+(?:[\w.]+\.)?(?<type>[A-Z]\w*)(?:\((?:[^()]|\([^()]*\))*\))?\s+With\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>C#: <c>new T(</c> or <c>new T {</c>. VB: <c>New T</c>.</summary>
    private static readonly Regex CsConstruction = new(
        @"\bnew\s+(?:[\w.]+\.)?(?<type>[A-Z]\w*)(?:<[^<>]*>)?\s*[({]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VbConstruction = new(
        @"\bNew\s+(?:[\w.]+\.)?(?<type>[A-Z]\w*)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>C#: an <c>Action&lt;...&gt;</c> or <c>Func&lt;...&gt;</c> member, with its type arguments.</summary>
    private static readonly Regex DelegateTypedMember = new(
        @"(?<![\w.])(?<kind>Action|Func)<(?<args>[^<>]*(?:<[^<>]*(?:<[^<>]*>[^<>]*)*>[^<>]*)*)>\??\s+(?<name>[A-Za-z_]\w*)\s*(?:\{|;|=)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>VB: <c>Member = Sub(a, b)</c> or <c>Member = Function(a)</c>; also the <c>.Member =</c> With form.</summary>
    private static readonly Regex VbLambdaAssignment = new(
        @"\.?(?<member>[A-Za-z_]\w*)\s*=\s*(?:Sub|Function)\s*\((?<params>[^()]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>C#: <c>Member = (a, b) =&gt;</c> or <c>Member = a =&gt;</c>.</summary>
    private static readonly Regex CsLambdaAssignment = new(
        @"\.?(?<member>[A-Za-z_]\w*)\s*=\s*(?:\((?<params>[^()]*)\)|(?<params>[A-Za-z_]\w*))\s*=>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VbWithStart = new(
        @"^\s*With\s+(?<recv>[\w.]+)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex VbWithEnd = new(
        @"^\s*End\s+With\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Lazy<IReadOnlyList<DelegateSurface>> LazyScan = new(Scan, isThreadSafe: true);

    public static IReadOnlyList<DelegateSurface> Surfaces => LazyScan.Value;

    /// <summary>How many product files the scan read. The positive control
    /// for "it looked at the tree at all".</summary>
    public static int CorpusFileCount { get; private set; }

    // ------------------------------------------------------------------

    private sealed class SourceFile
    {
        public string Relative = "";
        public string Code = "";         // comments and strings blanked, offsets preserved
        public string[] Lines = Array.Empty<string>();
        public int[] LineStarts = Array.Empty<int>();
        public Language Language;
        /// <summary>Types this file declares (C# and VB).</summary>
        public HashSet<string> DeclaredTypes = new(StringComparer.Ordinal);
        /// <summary>Object-initialiser blocks: owner type and the [start, end) of the braces.</summary>
        public List<(string Type, int Open, int Close)> Initialisers = new();
        /// <summary>Identifier declarations in this file: (name, type, offset).</summary>
        public List<(string Name, string Type, int Offset)> Symbols = new();

        public int LineOf(int offset)
        {
            int lo = 0, hi = LineStarts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (LineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
            }
            return lo + 1;
        }
    }

    private enum Language { CSharp, VisualBasic, Xaml }

    private sealed record Declaration(string Name, string Owner, string File, int Line, bool IsStatic, bool HasDefault);

    private static IReadOnlyList<DelegateSurface> Scan()
    {
        var root = RepoPaths.RequireRoot();

        // ---- 1. corpus --------------------------------------------------
        var files = new List<SourceFile>();
        foreach (var relative in CorpusPaths(root))
        {
            var ext = Path.GetExtension(relative);
            Language language;
            if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)) language = Language.CSharp;
            else if (ext.Equals(".vb", StringComparison.OrdinalIgnoreCase)) language = Language.VisualBasic;
            else if (ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase)) language = Language.Xaml;
            else continue;

            if (IsGenerated(relative) || IsVendor(relative) || IsTest(relative)) continue;

            string text;
            try { text = File.ReadAllText(Path.Combine(root, relative)); }
            catch (IOException) { continue; }

            var file = new SourceFile
            {
                Relative = relative.Replace('\\', '/'),
                Language = language,
                Code = Blank(text, language),
            };
            file.Lines = file.Code.Split('\n');
            file.LineStarts = new int[file.Lines.Length];
            for (int i = 0, pos = 0; i < file.Lines.Length; i++)
            {
                file.LineStarts[i] = pos;
                pos += file.Lines[i].Length + 1;
            }
            files.Add(file);
        }

        CorpusFileCount = files.Count;
        if (files.Count < MinimumCorpusFiles)
            throw new InvalidOperationException(
                $"The delegate scan found only {files.Count} product source files under \"{root}\", which means it "
                + $"did not find the tree and every verdict built on it would be a clean result having looked at "
                + $"nothing. Expected at least {MinimumCorpusFiles}.");

        // ---- 2. types, inheritance, symbols, initialisers -----------------
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var typeFiles = new Dictionary<string, List<SourceFile>>(StringComparer.Ordinal);
        var globalSymbols = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            IndexTypes(file, parents);
            foreach (var type in file.DeclaredTypes)
            {
                if (!typeFiles.TryGetValue(type, out var list)) typeFiles[type] = list = new List<SourceFile>();
                list.Add(file);
            }
            IndexSymbols(file);
            foreach (var (name, type, _) in file.Symbols)
            {
                if (!globalSymbols.TryGetValue(name, out var set)) globalSymbols[name] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(type);
            }
            IndexInitialisers(file);
        }

        // A XAML file's x:Name'd elements are fields of its code-behind class, so
        // `FieldsPanel.Hook = ...` in MainWindow.xaml.cs resolves against
        // MainWindow.xaml rather than against every x:Name="FieldsPanel" in the
        // tree (LogTemplateDialog.xaml has one too, of another type).
        var byRelative = files.ToDictionary(f => f.Relative, StringComparer.OrdinalIgnoreCase);
        foreach (var xaml in files.Where(f => f.Language == Language.Xaml))
        {
            if (!byRelative.TryGetValue(xaml.Relative + ".cs", out var codeBehind)) continue;
            foreach (var (name, type, _) in xaml.Symbols)
                codeBehind.Symbols.Add((name, type, -1));
        }

        // An untyped lambda parameter takes its type from the delegate-typed
        // member the lambda is assigned to: `FreqOutHandlersWireCallback =
        // Sub(handlers) handlers.GetRXFrequency = ...` types `handlers` as
        // FreqOutHandlers because the callback is declared Action<FreqOutHandlers>.
        var delegateMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => f.Language == Language.CSharp))
        {
            foreach (Match m in DelegateTypedMember.Matches(file.Code))
            {
                var args = SplitTypeArguments(m.Groups["args"].Value);
                if (m.Groups["kind"].Value == "Func") args.RemoveAt(args.Count - 1);
                if (args.Count > 0) delegateMembers.TryAdd(m.Groups["name"].Value, args);
            }
        }
        foreach (var file in files.Where(f => f.Language != Language.Xaml))
        {
            var pattern = file.Language == Language.VisualBasic ? VbLambdaAssignment : CsLambdaAssignment;
            foreach (Match m in pattern.Matches(file.Code))
            {
                if (!delegateMembers.TryGetValue(m.Groups["member"].Value, out var types)) continue;
                var parameters = m.Groups["params"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (var i = 0; i < parameters.Length && i < types.Count; i++)
                {
                    var p = parameters[i];
                    if (p.Contains(' ')) continue; // already typed
                    file.Symbols.Add((p, types[i], m.Index));
                }
            }
        }

        // ---- 3. declarations ---------------------------------------------
        var declarations = new List<Declaration>();
        foreach (var file in files)
        {
            if (file.Language != Language.CSharp) continue;
            if (!DeclarationRoots.Any(r => file.Relative.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))) continue;

            for (var i = 0; i < file.Lines.Length; i++)
            {
                var line = file.Lines[i];
                var match = DeclarationPattern.Match(line);
                if (!match.Success) match = FieldPattern.Match(line);
                if (!match.Success) continue;

                var isStatic = Regex.IsMatch(line, @"^\s*public\s+static\b");
                // `{ get; init; } = () => null;` - a hook with a default is always
                // callable, so it cannot be dead in the #109 sense.
                var hasDefault = Regex.IsMatch(line, @"\}\s*=\s*\S");
                var owner = EnclosingType(file, i) ?? Path.GetFileNameWithoutExtension(file.Relative).Split('.')[0];
                declarations.Add(new Declaration(match.Groups["name"].Value, owner, file.Relative, i + 1, isStatic, hasDefault));
            }
        }

        // ---- 4. construction sites per owner -------------------------------
        var constructionIndex = new ConstructionIndex(files, typeFiles, parents);
        var constructed = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var owner in declarations.Select(d => d.Owner).Distinct(StringComparer.Ordinal))
            constructed[owner] = constructionIndex.IsConstructed(owner);

        // ---- 5. evidence per (owner, name) ---------------------------------
        var resolver = new Resolver(parents, globalSymbols, typeFiles);
        var results = new List<DelegateSurface>();

        foreach (var group in declarations.GroupBy(d => (d.Owner, d.Name)))
        {
            var (owner, name) = group.Key;
            var first = group.First();
            var declaredIn = string.Join(", ", group.Select(d => $"{d.File}:{d.Line}").Distinct(StringComparer.Ordinal));
            var declarationLines = new HashSet<(string, int)>(group.Select(d => (d.File, d.Line)));

            var assignPattern = new Regex(
                $@"(?<recv>(?:[A-Za-z_]\w*\s*\.\s*)*)\b{Regex.Escape(name)}\s*(?<op>\+|\?\?)?=(?!=)\s*(?<rhs>null\b|Nothing\b)?",
                RegexOptions.CultureInvariant);
            var invokePattern = new Regex(
                $@"(?<recv>(?:[A-Za-z_]\w*\s*\.\s*)*)\b{Regex.Escape(name)}\s*\??\s*(?:\.\s*Invoke\s*\(|\()",
                RegexOptions.CultureInvariant);
            // VB `With` members: a leading dot and no identifier before it.
            var vbWithAssign = new Regex($@"^\s*\.{Regex.Escape(name)}\s*=(?!=)\s*(?<rhs>Nothing\b)?", RegexOptions.CultureInvariant);
            var vbWithInvoke = new Regex($@"^\s*\.{Regex.Escape(name)}\s*\??\s*(?:\.\s*Invoke\s*\(|\()", RegexOptions.CultureInvariant);

            var invokedFrom = new List<string>();
            var assignedFrom = new List<string>();
            var nameOnly = new List<string>();
            if (group.Any(d => d.HasDefault))
                assignedFrom.Add($"{first.File}:{first.Line} (default value at declaration)");

            foreach (var file in files)
            {
                if (file.Language == Language.Xaml) continue;
                if (file.Code.IndexOf(name, StringComparison.Ordinal) < 0) continue;

                var ownerFile = file.DeclaredTypes.Contains(owner);
                var withStack = new Stack<string>();

                for (var i = 0; i < file.Lines.Length; i++)
                {
                    var line = file.Lines[i];
                    if (line.Length == 0) continue;
                    var lineNo = i + 1;
                    var isDeclaration = declarationLines.Contains((file.Relative, lineNo));

                    if (file.Language == Language.VisualBasic)
                    {
                        var ws = VbWithStart.Match(line);
                        if (ws.Success) { withStack.Push(ws.Groups["recv"].Value); continue; }
                        if (VbWithEnd.IsMatch(line)) { if (withStack.Count > 0) withStack.Pop(); continue; }

                        var wa = vbWithAssign.Match(line);
                        if (wa.Success && withStack.Count > 0)
                        {
                            var recvType = resolver.Resolve(withStack.Peek(), file, file.LineStarts[i]);
                            var site = $"{file.Relative}:{lineNo}";
                            if (recvType != null && resolver.Compatible(recvType, owner))
                            {
                                if (!wa.Groups["rhs"].Success) assignedFrom.Add(site);
                            }
                            else nameOnly.Add(site + " (With ." + name + " =, receiver unresolved)");
                        }
                        var wi = vbWithInvoke.Match(line);
                        if (wi.Success && withStack.Count > 0)
                        {
                            var recvType = resolver.Resolve(withStack.Peek(), file, file.LineStarts[i]);
                            var site = $"{file.Relative}:{lineNo}";
                            if (recvType != null && resolver.Compatible(recvType, owner)) invokedFrom.Add(site);
                            else nameOnly.Add(site + " (With ." + name + "(, receiver unresolved)");
                        }
                    }

                    if (line.IndexOf(name, StringComparison.Ordinal) < 0) continue;

                    foreach (Match m in assignPattern.Matches(line))
                    {
                        if (isDeclaration) break;
                        var offset = file.LineStarts[i] + m.Index;
                        var site = $"{file.Relative}:{lineNo}";
                        var verdict = Attribute(m.Groups["recv"].Value, owner, ownerFile, file, offset, resolver);
                        if (verdict == Verdict.Owner)
                        {
                            if (!m.Groups["rhs"].Success) assignedFrom.Add(site);
                            // `= null` is teardown; it is neither wiring nor a mention worth listing.
                        }
                        else if (verdict == Verdict.Unresolved)
                            nameOnly.Add(site + " (assignment, receiver unresolved)");
                    }

                    foreach (Match m in invokePattern.Matches(line))
                    {
                        if (isDeclaration) break;
                        var offset = file.LineStarts[i] + m.Index;
                        var site = $"{file.Relative}:{lineNo}";
                        var verdict = Attribute(m.Groups["recv"].Value, owner, ownerFile, file, offset, resolver);
                        if (verdict == Verdict.Owner) invokedFrom.Add(site);
                        else if (verdict == Verdict.Unresolved) nameOnly.Add(site + " (call, receiver unresolved)");
                    }
                }
            }

            results.Add(new DelegateSurface(
                name,
                owner,
                declaredIn,
                first.IsStatic,
                invokedFrom.Count > 0,
                assignedFrom.Count > 0,
                first.IsStatic || constructed[owner],
                invokedFrom.Distinct().Take(8).ToList(),
                assignedFrom.Distinct().Take(8).ToList(),
                nameOnly.Distinct().Take(8).ToList()));
        }

        return results.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    private enum Verdict { Owner, Other, Unresolved }

    /// <summary>
    /// Decide whom a <c>recv.Name</c> (or bare <c>Name</c>) belongs to.
    /// </summary>
    private static Verdict Attribute(string receiverChain, string owner, bool ownerFile, SourceFile file, int offset, Resolver resolver)
    {
        var chain = receiverChain.Replace(" ", "").TrimEnd('.');
        if (chain.Length == 0)
        {
            // Unqualified. The owner's own code, or an initialiser of the owner.
            if (ownerFile && !InsideSomeInitialiser(file, offset)) return Verdict.Owner;
            var initialiserType = InnermostInitialiser(file, offset);
            if (initialiserType != null)
                return resolver.Compatible(initialiserType, owner) ? Verdict.Owner : Verdict.Other;
            return ownerFile ? Verdict.Owner : Verdict.Unresolved;
        }

        var last = chain.Contains('.') ? chain[(chain.LastIndexOf('.') + 1)..] : chain;
        if (last is "this" or "Me" or "base" or "MyBase")
            return ownerFile ? Verdict.Owner : Verdict.Other;

        var type = resolver.Resolve(chain, file, offset);
        if (type == null) return Verdict.Unresolved;
        return resolver.Compatible(type, owner) ? Verdict.Owner : Verdict.Other;
    }

    private static bool InsideSomeInitialiser(SourceFile file, int offset)
        => InnermostInitialiser(file, offset) != null;

    /// <summary>
    /// The type of the innermost object initialiser whose braces contain
    /// <paramref name="offset"/> at depth one. A lambda body or a nested
    /// initialiser opens its own braces and takes the assignment out of the
    /// outer block's depth-one, so it is not credited to the outer type.
    /// </summary>
    private static string? InnermostInitialiser(SourceFile file, int offset)
    {
        string? best = null;
        var bestOpen = -1;
        foreach (var (type, open, close) in file.Initialisers)
        {
            if (offset <= open || offset >= close) continue;
            if (open < bestOpen) continue;
            // depth one only
            var depth = 0;
            for (var i = open + 1; i < offset; i++)
            {
                var c = file.Code[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            if (depth != 0) continue;
            best = type;
            bestOpen = open;
        }
        return best;
    }

    // ------------------------------------------------------------------

    private sealed class Resolver
    {
        private readonly Dictionary<string, string> _parents;
        private readonly Dictionary<string, HashSet<string>> _globals;
        private readonly Dictionary<string, List<SourceFile>> _typeFiles;

        public Resolver(Dictionary<string, string> parents, Dictionary<string, HashSet<string>> globals, Dictionary<string, List<SourceFile>> typeFiles)
        {
            _parents = parents;
            _globals = globals;
            _typeFiles = typeFiles;
        }

        /// <summary>The declared type of a receiver expression, or null.</summary>
        public string? Resolve(string chain, SourceFile file, int offset)
        {
            var parts = chain.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var last = parts[^1];

            // A known type name used as a receiver is a static access.
            if (_typeFiles.ContainsKey(last)) return last;

            // Nearest declaration of the identifier above the use, same file.
            string? local = null;
            var localOffset = -1;
            foreach (var (name, type, declOffset) in file.Symbols)
            {
                if (!string.Equals(name, last, StringComparison.Ordinal)) continue;
                if (declOffset > offset) continue;
                if (declOffset > localOffset) { localOffset = declOffset; local = type; }
            }
            if (local != null) return local;

            // Any declaration in the file (a field declared below its use).
            var inFile = file.Symbols.Where(s => s.Name == last).Select(s => s.Type).Distinct().ToList();
            if (inFile.Count == 1) return inFile[0];
            if (inFile.Count > 1) return null;

            // A globally unique declaration (`RigControl As FlexBase`).
            if (_globals.TryGetValue(last, out var set) && set.Count == 1) return set.First();
            return null;
        }

        /// <summary>True when <paramref name="type"/> is the owner or in its inheritance chain, either way.</summary>
        public bool Compatible(string type, string owner)
        {
            if (string.Equals(type, owner, StringComparison.Ordinal)) return true;
            return Ancestors(type).Contains(owner) || Ancestors(owner).Contains(type);
        }

        private IEnumerable<string> Ancestors(string type)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cur = type;
            while (_parents.TryGetValue(cur, out var parent) && seen.Add(parent))
            {
                yield return parent;
                cur = parent;
            }
        }
    }

    // ------------------------------------------------------------------

    private static void IndexTypes(SourceFile file, Dictionary<string, string> parents)
    {
        if (file.Language == Language.CSharp)
        {
            foreach (var line in file.Lines)
            {
                var m = TypeDeclarationPattern.Match(line);
                if (!m.Success) continue;
                var name = m.Groups["name"].Value;
                file.DeclaredTypes.Add(name);
                if (m.Groups["base"].Success)
                {
                    var b = m.Groups["base"].Value;
                    b = b.Contains('.') ? b[(b.LastIndexOf('.') + 1)..] : b;
                    // Interfaces start with I + uppercase; a base class does not have to,
                    // but excluding the obvious interfaces keeps the chain honest.
                    if (!(b.Length > 1 && b[0] == 'I' && char.IsUpper(b[1]))) parents.TryAdd(name, b);
                }
            }
        }
        else if (file.Language == Language.VisualBasic)
        {
            string? current = null;
            foreach (var line in file.Lines)
            {
                var c = VbClassPattern.Match(line);
                if (c.Success) { current = c.Groups["name"].Value; file.DeclaredTypes.Add(current); continue; }
                var inh = VbInheritsPattern.Match(line);
                if (inh.Success && current != null)
                {
                    var b = inh.Groups["base"].Value;
                    b = b.Contains('.') ? b[(b.LastIndexOf('.') + 1)..] : b;
                    parents.TryAdd(current, b);
                }
            }
        }
    }

    private static void IndexSymbols(SourceFile file)
    {
        switch (file.Language)
        {
            case Language.CSharp:
                foreach (Match m in CsTypedIdentifier.Matches(file.Code))
                    file.Symbols.Add((m.Groups["name"].Value, m.Groups["type"].Value, m.Index));
                foreach (Match m in CsNewAssignment.Matches(file.Code))
                    file.Symbols.Add((m.Groups["name"].Value, m.Groups["type"].Value, m.Index));
                foreach (Match m in CsTargetTypedNew.Matches(file.Code))
                    file.Symbols.Add((m.Groups["name"].Value, m.Groups["type"].Value, m.Index));
                break;
            case Language.VisualBasic:
                foreach (Match m in VbTypedIdentifier.Matches(file.Code))
                    file.Symbols.Add((m.Groups["name"].Value, m.Groups["type"].Value, m.Index));
                foreach (Match m in VbNewAssignment.Matches(file.Code))
                    file.Symbols.Add((m.Groups["name"].Value, m.Groups["type"].Value, m.Index));
                break;
            case Language.Xaml:
                foreach (Match m in XamlNamedElement.Matches(file.Code))
                    file.Symbols.Add((m.Groups["name"].Value, m.Groups["type"].Value, m.Index));
                break;
        }
    }

    private static void IndexInitialisers(SourceFile file)
    {
        var pattern = file.Language switch
        {
            Language.CSharp => CsInitialiserStart,
            Language.VisualBasic => VbInitialiserStart,
            _ => null,
        };
        if (pattern == null) return;

        foreach (Match m in pattern.Matches(file.Code))
        {
            var open = m.Index + m.Length - 1; // the '{'
            var depth = 0;
            for (var i = open; i < file.Code.Length; i++)
            {
                var c = file.Code[i];
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    file.Initialisers.Add((m.Groups["type"].Value, open, i));
                    break;
                }
            }
        }
    }

    /// <summary>Nearest enclosing C# type above <paramref name="lineIndex"/>, by indentation.</summary>
    private static string? EnclosingType(SourceFile file, int lineIndex)
    {
        var indent = file.Lines[lineIndex].Length - file.Lines[lineIndex].TrimStart().Length;
        for (var i = lineIndex - 1; i >= 0; i--)
        {
            var m = TypeDeclarationPattern.Match(file.Lines[i]);
            if (!m.Success) continue;
            if (m.Groups["indent"].Value.Length < indent) return m.Groups["name"].Value;
        }
        return null;
    }

    /// <summary>Top-level comma split of a generic argument list.</summary>
    private static List<string> SplitTypeArguments(string args)
    {
        var result = new List<string>();
        var depth = 0; var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == '<') depth++;
            else if (args[i] == '>') depth--;
            else if (args[i] == ',' && depth == 0) { result.Add(Clean(args[start..i])); start = i + 1; }
        }
        result.Add(Clean(args[start..]));
        return result;

        static string Clean(string t)
        {
            t = t.Trim().TrimEnd('?');
            var lt = t.IndexOf('<'); if (lt >= 0) t = t[..lt];
            var dot = t.LastIndexOf('.'); if (dot >= 0) t = t[(dot + 1)..];
            return t;
        }
    }

    /// <summary>
    /// Does product code build this type anywhere a person can get to?
    /// </summary>
    /// <remarks>
    /// <para>Direct construction outside the type's own files, a XAML element,
    /// or a static factory inside the type's own file that some other file
    /// calls - and, since #591, <b>transitively</b>: a construction site only
    /// counts if the file holding it is itself live. <c>RadioNumberBox</c> is
    /// built by <c>FiltersDspControl.xaml</c> and nothing builds
    /// <c>FiltersDspControl</c> (#482), so the number box has no door either.
    /// A XAML file is live when its <c>x:Class</c> is; a C# file is live when
    /// any type it declares is constructed or is a static class; a VB file is
    /// always live, because the VB application is the entry point and reaches
    /// its own forms by default instance, which no sweep can see (#482).</para>
    /// <para>Inheritance counts: a type is constructed when any descendant is.</para>
    /// </remarks>
    private sealed class ConstructionIndex
    {
        private readonly List<SourceFile> _files;
        private readonly Dictionary<string, List<SourceFile>> _typeFiles;
        private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _memo = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);

        public ConstructionIndex(List<SourceFile> files, Dictionary<string, List<SourceFile>> typeFiles, Dictionary<string, string> parents)
        {
            _files = files;
            _typeFiles = typeFiles;
            foreach (var (child, parent) in parents)
            {
                if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = new List<string>();
                list.Add(child);
            }
        }

        public bool IsConstructed(string owner)
        {
            if (_memo.TryGetValue(owner, out var known)) return known;
            if (!_inProgress.Add(owner)) return false; // cycle: nothing outside the cycle builds it
            var result = Compute(owner);
            _inProgress.Remove(owner);
            _memo[owner] = result;
            return result;
        }

        private bool Compute(string owner)
        {
            var own = _typeFiles.TryGetValue(owner, out var list) ? new HashSet<SourceFile>(list) : new HashSet<SourceFile>();
            var internalNew = false;
            var externalStaticCall = false;
            var staticCall = new Regex($@"(?<![\w.])(?:[\w]+\.)*{Regex.Escape(owner)}\s*\.\s*[A-Z]\w*\s*\(", RegexOptions.CultureInvariant);

            foreach (var file in _files)
            {
                if (file.Code.IndexOf(owner, StringComparison.Ordinal) < 0) continue;
                var isOwn = own.Contains(file);

                bool constructs = file.Language switch
                {
                    Language.CSharp => CsConstruction.Matches(file.Code).Any(m => m.Groups["type"].Value == owner)
                                       || CsTargetTypedNew.Matches(file.Code).Any(m => m.Groups["type"].Value == owner),
                    Language.VisualBasic => VbConstruction.Matches(file.Code).Any(m => m.Groups["type"].Value == owner),
                    Language.Xaml => XamlElement.Matches(file.Code).Any(m => m.Groups["type"].Value == owner),
                    _ => false,
                };

                if (constructs && !isOwn && FileIsLive(file)) return true;
                if (constructs && isOwn) internalNew = true;
                if (!isOwn && file.Language != Language.Xaml && staticCall.IsMatch(file.Code) && FileIsLive(file)) externalStaticCall = true;
            }
            if (internalNew && externalStaticCall) return true;

            if (_children.TryGetValue(owner, out var kids))
                foreach (var kid in kids)
                    if (IsConstructed(kid)) return true;
            return false;
        }

        private bool FileIsLive(SourceFile file)
        {
            switch (file.Language)
            {
                case Language.VisualBasic:
                    return true;
                case Language.Xaml:
                {
                    var m = Regex.Match(file.Code, @"x:Class\s*=\s*""(?<cls>[\w.]+)""");
                    if (!m.Success) return true; // a resource dictionary or template; cannot say, so do not hide
                    var cls = m.Groups["cls"].Value;
                    cls = cls.Contains('.') ? cls[(cls.LastIndexOf('.') + 1)..] : cls;
                    return IsConstructed(cls);
                }
                default:
                {
                    if (file.DeclaredTypes.Count == 0) return true;
                    if (Regex.IsMatch(file.Code, @"\bstatic\s+(?:partial\s+)?class\b")) return true;
                    foreach (var t in file.DeclaredTypes)
                        if (IsConstructed(t)) return true;
                    return false;
                }
            }
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The product corpus: every file git tracks, plus untracked files that
    /// are not ignored. Never a filesystem walk - see the class remarks.
    /// </summary>
    private static IEnumerable<string> CorpusPaths(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in GitList(root, "ls-files -z"))
            if (seen.Add(rel)) yield return rel;
        foreach (var rel in GitList(root, "ls-files -z --others --exclude-standard"))
            if (seen.Add(rel)) yield return rel;
    }

    private static IEnumerable<string> GitList(string root, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string listing;
        try
        {
            using var git = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            listing = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            if (git.ExitCode != 0) throw new InvalidOperationException("git " + arguments + " exited " + git.ExitCode);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The delegate scan could not ask git which files belong to the tree under \"" + root + "\" (git "
                + arguments + "). It refuses to fall back to a filesystem walk: that walk reads gitignored trees, and "
                + "a verdict that depends on what is lying around the checkout is worse than none because it is read "
                + "as a verdict. Underlying: " + ex.Message, ex);
        }
        return listing.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                      .Where(rel => File.Exists(Path.Combine(root, rel)));
    }

    private static bool IsGenerated(string relative)
    {
        var name = Path.GetFileName(relative);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".Designer.vb", StringComparison.OrdinalIgnoreCase)
               || relative.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               || relative.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVendor(string relative)
    {
        var rel = relative.Replace('\\', '/');
        var cut = rel.IndexOf('/');
        var first = cut < 0 ? rel : rel[..cut];
        return VendorRoots.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Tests are neither callsites nor assigners. A test that
    /// invokes a hook to exercise it is not an operator path, and a test that
    /// assigns one to drive a dialog is not wiring.</summary>
    private static bool IsTest(string relative)
    {
        var rel = relative.Replace('\\', '/');
        return rel.Contains(".Tests/", StringComparison.OrdinalIgnoreCase)
               || Path.GetFileName(rel).EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Blank comments and string literals to spaces, preserving every offset
    /// and newline, so a name inside prose or a label never reads as code and
    /// a <c>//</c> inside a URL string never truncates the line.
    /// </summary>
    private static string Blank(string text, Language language)
    {
        var chars = text.ToCharArray();
        int i = 0, n = chars.Length;

        void BlankRange(int from, int to)
        {
            for (var k = from; k < to && k < n; k++)
                if (chars[k] != '\n' && chars[k] != '\r') chars[k] = ' ';
        }

        if (language == Language.Xaml)
        {
            while (i < n)
            {
                var start = text.IndexOf("<!--", i, StringComparison.Ordinal);
                if (start < 0) break;
                var end = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
                end = end < 0 ? n : end + 3;
                BlankRange(start, end);
                i = end;
            }
            return new string(chars);
        }

        var vb = language == Language.VisualBasic;
        while (i < n)
        {
            var c = chars[i];
            if (vb)
            {
                if (c == '\'')
                {
                    var end = text.IndexOf('\n', i); end = end < 0 ? n : end;
                    BlankRange(i, end); i = end; continue;
                }
                if (c == '"')
                {
                    var j = i + 1;
                    while (j < n)
                    {
                        if (chars[j] == '"') { if (j + 1 < n && chars[j + 1] == '"') { j += 2; continue; } break; }
                        if (chars[j] == '\n') break;
                        j++;
                    }
                    BlankRange(i + 1, j); i = Math.Min(j + 1, n); continue;
                }
                i++; continue;
            }

            // C#
            if (c == '/' && i + 1 < n && chars[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i); end = end < 0 ? n : end;
                BlankRange(i, end); i = end; continue;
            }
            if (c == '/' && i + 1 < n && chars[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); end = end < 0 ? n : end + 2;
                BlankRange(i, end); i = end; continue;
            }
            if (c == '"')
            {
                var verbatim = i > 0 && (chars[i - 1] == '@' || (chars[i - 1] == '$' && i > 1 && chars[i - 2] == '@'));
                var j = i + 1;
                while (j < n)
                {
                    if (verbatim)
                    {
                        if (chars[j] == '"') { if (j + 1 < n && chars[j + 1] == '"') { j += 2; continue; } break; }
                    }
                    else
                    {
                        if (chars[j] == '\\') { j += 2; continue; }
                        if (chars[j] == '"' || chars[j] == '\n') break;
                    }
                    j++;
                }
                BlankRange(i + 1, j); i = Math.Min(j + 1, n); continue;
            }
            if (c == '\'')
            {
                // char literal: 'x' or '\n' or 'A'
                var j = i + 1;
                if (j < n && chars[j] == '\\') j += 2;
                while (j < n && chars[j] != '\'' && chars[j] != '\n' && j - i < 10) j++;
                if (j < n && chars[j] == '\'') { BlankRange(i + 1, j); i = j + 1; continue; }
            }
            i++;
        }
        return new string(chars);
    }
}

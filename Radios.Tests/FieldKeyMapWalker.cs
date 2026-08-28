using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Radios.Tests
{
    /// <summary>
    /// Walks one field handler's SYNTAX and collects the keys it claims.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four shapes that make a textual scan wrong here, all of them real:
    /// </para>
    /// <para>
    /// 1. <b>A modifier test nested inside a character test.</b>
    /// <c>case Key.Left: case Key.Right:</c> whose body then branches on
    /// <c>Keyboard.Modifiers == ModifierKeys.Alt</c> versus <c>Shift</c>. The
    /// walker reads the chain and emits Alt+Left, Alt+Right, Shift+Left,
    /// Shift+Right — four bindings a grep for "Key.Left" would call one.
    /// </para>
    /// <para>
    /// 2. <b>A modifier carried in a local.</b> <c>bool unmodified =
    /// Keyboard.Modifiers == ModifierKeys.None;</c>, then
    /// <c>ch &gt;= 'A' &amp;&amp; ch &lt;= 'H' &amp;&amp; unmodified</c>.
    /// Locals are resolved back to their initializers.
    /// </para>
    /// <para>
    /// 3. <b>A parameter that differs per caller.</b> One body serves RIT and
    /// XIT, and its <c>'='</c> branch is guarded by <c>isRIT</c>. Walking it
    /// once and attributing the result to both fields would invent a binding
    /// on XIT that does not exist, so boolean arguments are bound at the call
    /// and constant-folded.
    /// </para>
    /// <para>
    /// 4. <b>A mode the operator is not usually in.</b> Quick-type
    /// accumulation claims every letter while <c>_inQuickType</c> holds. That
    /// is a real binding and a false answer to "which letters are free", so a
    /// branch guarded by a private mode field is recorded as STATE-GATED: it
    /// satisfies a declared row, and it is reported beside the free list
    /// rather than spending from it.
    /// </para>
    /// </remarks>
    internal sealed class HandlerWalker
    {
        private readonly Dictionary<string, MethodDeclarationSyntax> _methods;
        private readonly List<string> _notes;
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);

        internal List<FieldKeyMapScan.Claim> Claims { get; } = new();

        internal HandlerWalker(Dictionary<string, MethodDeclarationSyntax> methods, List<string> notes)
        {
            _methods = methods;
            _notes = notes;
        }

        // ────────────────────────────────────────────────────────────────

        private sealed class Context
        {
            internal string Method = "";
            internal Dictionary<string, bool> BoundBools = new(StringComparer.Ordinal);
            internal Dictionary<string, ExpressionSyntax> LocalBools = new(StringComparer.Ordinal);
            internal string EventParameter = "";
        }

        /// <summary>One disjunct: the keys it names and what guards them.</summary>
        private sealed class Clause
        {
            internal List<string> Tokens = new();
            internal List<string> NamedKeys = new();
            internal string Mods = "";
            internal bool Dead;
            internal bool Gated;

            /// <summary>The private mode fields that gate this clause, named.</summary>
            internal string Gate = "";

            internal bool ModsUnresolved;

            /// <summary>
            /// The clause tested <c>Keyboard.Modifiers == ModifierKeys.None</c>,
            /// which is a POSITIVE statement that the binding is unmodified —
            /// not the same as saying nothing about modifiers at all.
            /// </summary>
            internal bool ExplicitlyPlain;
        }

        // ────────────────────────────────────────────────────────────────

        internal void Walk(MethodDeclarationSyntax method, Dictionary<string, bool> bound)
        {
            string name = method.Identifier.ValueText;
            if (!_visiting.Add(name)) return;

            var ctx = new Context
            {
                Method = name,
                BoundBools = bound,
                LocalBools = LocalBools(method),
                EventParameter = EventParameterName(method),
            };

            foreach (var statement in BodyStatements(method))
                VisitStatement(statement, ctx, keyConstrained: false, gate: "");

            _visiting.Remove(name);
        }

        private static IEnumerable<StatementSyntax> BodyStatements(MethodDeclarationSyntax method)
        {
            if (method.Body != null) return method.Body.Statements;
            if (method.ExpressionBody != null)
                return new StatementSyntax[] { SyntaxFactory.ExpressionStatement(method.ExpressionBody.Expression) };
            return Array.Empty<StatementSyntax>();
        }

        private static string EventParameterName(MethodDeclarationSyntax method)
        {
            var p = method.ParameterList.Parameters.FirstOrDefault(
                x => x.Type != null && x.Type.ToString().EndsWith("KeyEventArgs", StringComparison.Ordinal));
            return p?.Identifier.ValueText ?? "";
        }

        private static Dictionary<string, ExpressionSyntax> LocalBools(MethodDeclarationSyntax method)
        {
            var map = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
            foreach (var decl in method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
            {
                if (decl.Declaration.Type.ToString() is not ("bool" or "var")) continue;
                foreach (var v in decl.Declaration.Variables)
                {
                    var init = v.Initializer?.Value;
                    if (init == null) continue;
                    if (!init.ToString().Contains("Keyboard.Modifiers", StringComparison.Ordinal)) continue;
                    map[v.Identifier.ValueText] = init;
                }
            }
            return map;
        }

        // ────────────────────────────────────────────────────────────────
        //  Statements
        // ────────────────────────────────────────────────────────────────

        private void VisitStatement(StatementSyntax statement, Context ctx,
            bool keyConstrained, string gate)
        {
            switch (statement)
            {
                case BlockSyntax block:
                    foreach (var s in block.Statements) VisitStatement(s, ctx, keyConstrained, gate);
                    return;

                case IfStatementSyntax ifStatement:
                    VisitIf(ifStatement, ctx, keyConstrained, gate);
                    return;

                case SwitchStatementSyntax switchStatement:
                    VisitSwitch(switchStatement, ctx, keyConstrained, gate);
                    return;

                case ExpressionStatementSyntax expression:
                    TryFollow(expression.Expression, ctx, keyConstrained, gate);
                    return;

                default:
                    foreach (var child in statement.ChildNodes().OfType<StatementSyntax>())
                        VisitStatement(child, ctx, keyConstrained, gate);
                    return;
            }
        }

        private void VisitIf(IfStatementSyntax node, Context ctx, bool keyConstrained, string gate)
        {
            // `if (HandleQuickTypeKey(key, ch, e)) return;` — the helper IS the
            // handler for those keys, so the call in the condition is followed.
            TryFollow(node.Condition, ctx, keyConstrained, gate);

            var clauses = Decompose(node.Condition, ctx);
            bool namesKeys = clauses.Any(c => c.Tokens.Count > 0);
            bool bodyReadsModifiers = ReferencesModifiers(node.Statement, ctx);

            foreach (var clause in clauses.Where(c => !c.Dead && c.Tokens.Count > 0))
            {
                bool unresolved = clause.ModsUnresolved
                    || (clause.Mods.Length == 0 && bodyReadsModifiers);
                foreach (string token in clause.Tokens)
                    Emit(token, clause.Mods, Join(gate, clause.Gate), unresolved, ctx.Method);
            }

            // A branch reached only through a private mode field carries that
            // gating down into everything nested inside it.
            string innerGate = gate;
            if (clauses.Count > 0 && clauses.All(c => c.Gated || c.Dead))
            {
                foreach (var clause in clauses.Where(c => !c.Dead))
                    innerGate = Join(innerGate, clause.Gate);
            }

            VisitStatement(node.Statement, ctx, keyConstrained || namesKeys, innerGate);
            if (node.Else != null)
                VisitStatement(node.Else.Statement, ctx, keyConstrained, gate);
        }

        private static readonly string[] GateSeparator = { " and " };

        /// <summary>Merge two gate descriptions without repeating a name.</summary>
        private static string Join(string a, string b)
        {
            if (a.Length == 0) return b;
            if (b.Length == 0) return a;
            var parts = new SortedSet<string>(
                a.Split(GateSeparator, StringSplitOptions.RemoveEmptyEntries)
                 .Concat(b.Split(GateSeparator, StringSplitOptions.RemoveEmptyEntries)),
                StringComparer.Ordinal);
            return string.Join(" and ", parts);
        }

        private void VisitSwitch(SwitchStatementSyntax node, Context ctx,
            bool keyConstrained, string gate)
        {
            foreach (var section in node.Sections)
            {
                var tokens = new List<string>();
                foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
                {
                    string? token = LabelToken(label.Value);
                    if (token != null) tokens.Add(token);
                }

                if (tokens.Count > 0)
                {
                    var variants = ModifierChain(section.Statements, ctx);
                    if (variants != null)
                    {
                        foreach (string token in tokens)
                            foreach (string mods in variants)
                                Emit(token, mods, gate, false, ctx.Method);
                    }
                    else
                    {
                        bool unresolved = section.Statements.Any(s => ReferencesModifiers(s, ctx));
                        foreach (string token in tokens)
                            Emit(token, "", gate, unresolved, ctx.Method);
                    }
                }

                foreach (var s in section.Statements)
                    VisitStatement(s, ctx, keyConstrained || tokens.Count > 0, gate);
            }
        }

        private string? LabelToken(ExpressionSyntax value) => value switch
        {
            LiteralExpressionSyntax lit when lit.Token.Value is char c => CharToken(c),
            MemberAccessExpressionSyntax member when IsKeyEnum(member)
                => NamedKeyToken(member.Name.Identifier.ValueText),
            _ => null,
        };

        private static bool IsKeyEnum(MemberAccessExpressionSyntax member)
            => member.Expression.ToString().EndsWith("Key", StringComparison.Ordinal);

        // ────────────────────────────────────────────────────────────────
        //  Following a call into another handler method
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Follow a call only when nothing about the KEY has been decided yet.
        /// <c>AdjustFreq</c> delegates to <c>AdjustSplit</c> from inside
        /// <c>ch == 'S'</c>; following that would credit the Frequency field
        /// with the whole Split map, which it does not have.
        /// </summary>
        private void TryFollow(ExpressionSyntax expression, Context ctx,
            bool keyConstrained, string gate)
        {
            if (keyConstrained) return;
            if (ctx.EventParameter.Length == 0) return;

            foreach (var invocation in expression.DescendantNodesAndSelf()
                         .OfType<InvocationExpressionSyntax>())
            {
                string? name = invocation.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax m when m.Expression is ThisExpressionSyntax
                        => m.Name.Identifier.ValueText,
                    _ => null,
                };
                if (name == null) continue;
                if (!_methods.TryGetValue(name, out var target)) continue;

                bool carriesEvent = invocation.ArgumentList.Arguments.Any(
                    a => a.Expression is IdentifierNameSyntax id
                         && string.Equals(id.Identifier.ValueText, ctx.EventParameter, StringComparison.Ordinal));
                if (!carriesEvent) continue;

                var bound = BindBooleans(target, invocation, ctx);
                var nested = new HandlerWalker(_methods, _notes);
                foreach (string v in _visiting) nested._visiting.Add(v);
                nested.Walk(target, bound);

                foreach (var claim in nested.Claims)
                {
                    string merged = Join(gate, claim.Gate);
                    Claims.Add(new FieldKeyMapScan.Claim
                    {
                        Binding = claim.Binding,
                        ModsUnresolved = claim.ModsUnresolved,
                        StateGated = claim.StateGated || merged.Length > 0,
                        Gate = merged,
                        Method = claim.Method,
                    });
                }
            }
        }

        private static Dictionary<string, bool> BindBooleans(MethodDeclarationSyntax target,
            InvocationExpressionSyntax invocation, Context ctx)
        {
            var bound = new Dictionary<string, bool>(StringComparer.Ordinal);
            var parameters = target.ParameterList.Parameters;
            var args = invocation.ArgumentList.Arguments;

            for (int i = 0; i < parameters.Count && i < args.Count; i++)
            {
                if (parameters[i].Type?.ToString() != "bool") continue;
                var expression = args[i].Expression;
                if (expression.IsKind(SyntaxKind.TrueLiteralExpression)) bound[parameters[i].Identifier.ValueText] = true;
                else if (expression.IsKind(SyntaxKind.FalseLiteralExpression)) bound[parameters[i].Identifier.ValueText] = false;
                else if (expression is IdentifierNameSyntax id
                         && ctx.BoundBools.TryGetValue(id.Identifier.ValueText, out bool value))
                {
                    bound[parameters[i].Identifier.ValueText] = value;
                }
            }
            return bound;
        }

        // ────────────────────────────────────────────────────────────────
        //  Conditions → clauses (disjunctive normal form, then read)
        // ────────────────────────────────────────────────────────────────

        private List<Clause> Decompose(ExpressionSyntax condition, Context ctx)
            => Dnf(condition).Select(conjuncts => ReadClause(conjuncts, ctx, 0)).ToList();

        private static List<List<ExpressionSyntax>> Dnf(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);

            if (expression is BinaryExpressionSyntax binary)
            {
                if (binary.IsKind(SyntaxKind.LogicalOrExpression))
                {
                    var list = Dnf(binary.Left);
                    list.AddRange(Dnf(binary.Right));
                    return list;
                }
                if (binary.IsKind(SyntaxKind.LogicalAndExpression))
                {
                    var result = new List<List<ExpressionSyntax>>();
                    foreach (var left in Dnf(binary.Left))
                        foreach (var right in Dnf(binary.Right))
                            result.Add(left.Concat(right).ToList());
                    return result;
                }
            }
            return new List<List<ExpressionSyntax>> { new() { expression } };
        }

        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax p) expression = p.Expression;
            return expression;
        }

        private Clause ReadClause(List<ExpressionSyntax> conjuncts, Context ctx, int depth)
        {
            var clause = new Clause();
            var chars = new List<char>();
            char? low = null, high = null;
            var mods = new List<string>();

            foreach (var raw in conjuncts)
            {
                var conjunct = Unwrap(raw);

                if (conjunct.ToString().Contains("Keyboard.Modifiers", StringComparison.Ordinal)
                    || NamesLocalModifier(conjunct, ctx))
                {
                    // handled below by the specific shapes; nothing to gate on
                }

                foreach (string gateField in PrivateFields(conjunct))
                {
                    clause.Gated = true;
                    clause.Gate = Join(clause.Gate, gateField);
                }

                switch (conjunct)
                {
                    case PrefixUnaryExpressionSyntax not when not.IsKind(SyntaxKind.LogicalNotExpression):
                    {
                        var inner = Unwrap(not.Operand);
                        if (inner is IdentifierNameSyntax id
                            && ctx.BoundBools.TryGetValue(id.Identifier.ValueText, out bool value)
                            && value)
                        {
                            clause.Dead = true;
                        }
                        break;
                    }

                    case IdentifierNameSyntax identifier:
                    {
                        string name = identifier.Identifier.ValueText;
                        if (ctx.BoundBools.TryGetValue(name, out bool value))
                        {
                            if (!value) clause.Dead = true;
                        }
                        else if (depth < 4 && ctx.LocalBools.TryGetValue(name, out var initializer))
                        {
                            var expanded = ReadClause(Dnf(initializer).FirstOrDefault() ?? new(), ctx, depth + 1);
                            if (expanded.Dead) clause.Dead = true;
                            if (expanded.Gated)
                            {
                                clause.Gated = true;
                                clause.Gate = Join(clause.Gate, expanded.Gate);
                            }
                            if (expanded.ModsUnresolved) clause.ModsUnresolved = true;
                            if (expanded.Mods.Length > 0) mods.AddRange(expanded.Mods.Split('+'));
                            else if (expanded.ExplicitlyPlain) mods.Add("");
                            chars.AddRange(expanded.Tokens
                                .Where(t => t.Length == 1)
                                .Select(t => t[0]));
                        }
                        break;
                    }

                    case BinaryExpressionSyntax binary:
                        ReadComparison(binary, ctx, clause, chars, ref low, ref high, mods);
                        break;
                }
            }

            if (chars.Count > 0)
            {
                clause.Tokens.AddRange(chars.Select(CharToken).Distinct(StringComparer.Ordinal));
            }
            else if (low.HasValue && high.HasValue && low.Value <= high.Value)
            {
                for (char c = low.Value; c <= high.Value; c++) clause.Tokens.Add(CharToken(c));
            }
            clause.Tokens.AddRange(clause.NamedKeys);
            clause.Tokens = clause.Tokens.Distinct(StringComparer.Ordinal).ToList();

            clause.Mods = FieldKeyMapScan.NormaliseMods(mods.Where(m => m.Length > 0));
            return clause;
        }

        private void ReadComparison(BinaryExpressionSyntax binary, Context ctx, Clause clause,
            List<char> chars, ref char? low, ref char? high, List<string> mods)
        {
            var left = Unwrap(binary.Left);
            var right = Unwrap(binary.Right);

            bool isEquals = binary.IsKind(SyntaxKind.EqualsExpression);
            bool isNotEquals = binary.IsKind(SyntaxKind.NotEqualsExpression);

            // `(Keyboard.Modifiers & ModifierKeys.Shift) != 0`
            if (isNotEquals && left is BinaryExpressionSyntax bitAnd
                && bitAnd.IsKind(SyntaxKind.BitwiseAndExpression)
                && bitAnd.ToString().Contains("Keyboard.Modifiers", StringComparison.Ordinal))
            {
                string? modifier = ModifierName(bitAnd.Left) ?? ModifierName(bitAnd.Right);
                if (modifier != null && modifier != "None") mods.Add(modifier);
                else clause.ModsUnresolved = true;
                return;
            }

            if (isEquals || isNotEquals)
            {
                bool modifierComparison =
                    left.ToString().Contains("Keyboard.Modifiers", StringComparison.Ordinal)
                    || right.ToString().Contains("Keyboard.Modifiers", StringComparison.Ordinal);

                if (modifierComparison)
                {
                    string? modifier = ModifierName(left) ?? ModifierName(right);
                    if (!isEquals || modifier == null) clause.ModsUnresolved = true;
                    else if (modifier == "None") clause.ExplicitlyPlain = true;
                    else mods.AddRange(modifier.Split('|').Select(m => m.Trim()));
                    return;
                }

                if (!isEquals) return;

                if (CharLiteral(left) is char lc) { chars.Add(lc); return; }
                if (CharLiteral(right) is char rc) { chars.Add(rc); return; }

                string? key = KeyMember(left) ?? KeyMember(right);
                if (key != null) clause.NamedKeys.Add(NamedKeyToken(key));
                return;
            }

            if (binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression) && CharLiteral(right) is char lo)
                low = lo;
            else if (binary.IsKind(SyntaxKind.LessThanOrEqualExpression) && CharLiteral(right) is char hi)
                high = hi;
        }

        private static bool NamesLocalModifier(ExpressionSyntax expression, Context ctx)
            => expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(i => ctx.LocalBools.ContainsKey(i.Identifier.ValueText));

        /// <summary>
        /// The private fields a condition reads. A branch reachable only when
        /// one of these is set is a MODE, not a resting-state binding — the
        /// difference between "S is taken on this field" and "S is swallowed
        /// while you are part-way through typing a frequency".
        /// </summary>
        private static IEnumerable<string> PrivateFields(SyntaxNode node)
            => node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Select(i => i.Identifier.ValueText)
                .Where(n => n.StartsWith('_'))
                .Distinct(StringComparer.Ordinal);

        private static char? CharLiteral(ExpressionSyntax expression)
            => Unwrap(expression) is LiteralExpressionSyntax lit && lit.Token.Value is char c ? c : null;

        private static string? KeyMember(ExpressionSyntax expression)
            => Unwrap(expression) is MemberAccessExpressionSyntax member && IsKeyEnum(member)
                ? member.Name.Identifier.ValueText
                : null;

        private static string? ModifierName(ExpressionSyntax expression)
        {
            var node = Unwrap(expression);
            if (node is MemberAccessExpressionSyntax member
                && member.Expression.ToString().EndsWith("ModifierKeys", StringComparison.Ordinal))
            {
                return member.Name.Identifier.ValueText;
            }
            if (node is BinaryExpressionSyntax or2 && or2.IsKind(SyntaxKind.BitwiseOrExpression))
            {
                string? l = ModifierName(or2.Left);
                string? r = ModifierName(or2.Right);
                if (l != null && r != null) return l + "|" + r;
            }
            return null;
        }

        // ────────────────────────────────────────────────────────────────
        //  Modifier chains hung off a switch section
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// When a case body's whole job is an if/else chain on
        /// <c>Keyboard.Modifiers</c>, the section binds one key per modifier —
        /// the shape that carries Alt+Left, Alt+Right, Shift+Left and
        /// Shift+Right in the Modern frequency field. Returns null when the
        /// body is anything else.
        /// </summary>
        private List<string>? ModifierChain(IEnumerable<StatementSyntax> statements, Context ctx)
        {
            var flat = new List<StatementSyntax>();
            foreach (var s in statements)
            {
                if (s is BlockSyntax block) flat.AddRange(block.Statements);
                else flat.Add(s);
            }

            var executable = flat.Where(s => s is not LocalDeclarationStatementSyntax
                                          && s is not BreakStatementSyntax
                                          && s is not EmptyStatementSyntax).ToList();
            if (executable.Count != 1 || executable[0] is not IfStatementSyntax chain) return null;

            var variants = new List<string>();
            IfStatementSyntax? current = chain;
            while (current != null)
            {
                if (!ReferencesModifiers(current.Condition, ctx)) return null;
                var clauses = Decompose(current.Condition, ctx);
                if (clauses.Any(c => c.Tokens.Count > 0)) return null;
                foreach (var clause in clauses.Where(c => !c.Dead))
                    variants.Add(clause.Mods);

                var next = current.Else?.Statement;
                if (next is IfStatementSyntax nested) { current = nested; continue; }
                if (next != null) variants.Add("");
                current = null;
            }
            return variants.Count == 0 ? null : variants.Distinct(StringComparer.Ordinal).ToList();
        }

        private static bool ReferencesModifiers(SyntaxNode node, Context ctx)
        {
            if (node.ToString().Contains("Keyboard.Modifiers", StringComparison.Ordinal)) return true;
            return node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(i => ctx.LocalBools.ContainsKey(i.Identifier.ValueText));
        }

        // ────────────────────────────────────────────────────────────────
        //  Tokens
        // ────────────────────────────────────────────────────────────────

        private void Emit(string token, string mods, string gate, bool modsUnresolved, string method)
        {
            Claims.Add(new FieldKeyMapScan.Claim
            {
                Binding = new FieldKeyMapScan.Binding(mods, token),
                ModsUnresolved = modsUnresolved,
                StateGated = gate.Length > 0,
                Gate = gate,
                Method = method,
            });
        }

        internal static string CharToken(char c)
        {
            if (c == ' ') return "Space";
            if (c >= 'a' && c <= 'z') return char.ToUpperInvariant(c).ToString();
            return c.ToString();
        }

        /// <summary>
        /// A <c>Key</c> enum member under the name the inventory writes. Key
        /// NAMES only — nothing here says which field a key belongs to.
        /// </summary>
        private string NamedKeyToken(string member)
        {
            switch (member)
            {
                case "Space": return "Space";
                case "Up": case "Down": case "Left": case "Right":
                case "Home": case "End": case "Escape": case "Tab": case "Insert":
                    return member;
                case "PageUp": case "Prior": return "Page Up";
                case "PageDown": case "Next": return "Page Down";
                case "Enter": case "Return": return "Enter";
                case "Back": return "Backspace";
                case "Delete": return "Delete";
                case "OemComma": return ",";
                case "OemPeriod": case "Decimal": return ".";
                case "OemPlus": return "=";
                case "Add": return "+";
                case "OemMinus": case "Subtract": return "-";
                case "OemQuestion": return "?";
                default:
                    if (member.Length == 1 && member[0] >= 'A' && member[0] <= 'Z') return member;
                    if (member.Length == 2 && member[0] == 'D' && char.IsDigit(member[1]))
                        return member[1].ToString();
                    if (member.StartsWith("NumPad", StringComparison.Ordinal) && member.Length == 7)
                        return member[6].ToString();
                    _notes.Add("no token name for Key." + member + " — it is bound and this scan cannot name it");
                    return "Key." + member;
            }
        }
    }
}

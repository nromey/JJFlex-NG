using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.KeyOwnership
{
    /// <summary>
    /// Where a command may be invoked FROM — the interaction-context axis.
    /// Independent of <see cref="KeyScope"/>, which says in which MODE.
    /// </summary>
    public enum InvocationReach
    {
        /// <summary>Only the surface whose nearest context is the declared one.</summary>
        OwnSurface,
        /// <summary>The declared context and everything inside it — declare once at "home", reach every Home field.</summary>
        ContextAndDescendants,
        /// <summary>Any participating surface. Whether it also BEATS local shortcuts is <see cref="GestureDeclaration.Reserved"/>.</summary>
        AcrossApplication,
    }

    /// <summary>How a declaration ranks against another that a surface could also reach.</summary>
    public enum DeclarationPrecedence
    {
        /// <summary>Nearest context wins; an across-application declaration is the last fallback.</summary>
        Normal,
        /// <summary>A registry binding explicitly permitted to override a field's own binding in this focus context.</summary>
        OverridesLocal,
        /// <summary>
        /// Owns its gesture across its whole context, and a more specific
        /// declaration of the same gesture inside it is a VALIDATION ERROR —
        /// not a quiet winner, and not a quiet loser. This is the ruled Home
        /// tuning declaration.
        /// </summary>
        Authoritative,
    }

    /// <summary>What a command needs before it can run. Availability is never discovered by calling the handler.</summary>
    [Flags]
    public enum CommandRequirements
    {
        None = 0,
        Radio = 1,
        ActiveSlice = 2,
        /// <summary>The slice the gesture names must exist.</summary>
        NamedSlice = 4,
    }

    /// <summary>Whether a command may run while a modal dialog is up.</summary>
    public enum ModalPolicy
    {
        /// <summary>Refused inside a modal boundary. The DEFAULT: nothing acts behind a dialog by accident.</summary>
        RefusedInModal = 0,
        /// <summary>Safe inside a modal — a read-only status query, a layer, a slice jump that moves no focus.</summary>
        AllowedInModal,
    }

    /// <summary>How the action's target is derived.</summary>
    public enum TargetRule
    {
        Application,
        ActiveSlice,
        /// <summary>The slice whose letter is the key pressed (<see cref="KeyFamily.SliceLetter"/>).</summary>
        SliceNamedByKey,
        /// <summary>The layer's currently selected target.</summary>
        LayerCurrentTarget,
        /// <summary>The layer target this declaration selects (<see cref="GestureDeclaration.SelectsLayerTargetId"/>).</summary>
        LayerTargetSelected,
    }

    /// <summary>Where a tuning command's step comes from.</summary>
    public enum StepSource
    {
        None,
        TuningCoarse,
        TuningFine,
    }

    public enum DeclarationKind
    {
        Command,
        /// <summary>A gesture that used to act here and now answers with where its action went. Performs nothing.</summary>
        Retired,
        /// <summary>Inside a layer: this key is not the layer's, and continues to the next owner in the SAME origin context.</summary>
        LayerPassThrough,
    }

    /// <summary>
    /// One gesture, declared once, as data: what it is, where it may be
    /// invoked from, what it needs, and exactly which modifiers it takes.
    /// Recognition reads these; nothing here can execute.
    /// </summary>
    public sealed record GestureDeclaration
    {
        /// <summary>Stable command identity. Field and layer commands have no registry number, so this is a name.</summary>
        public string CommandId { get; init; } = "";

        /// <summary>The registry command where there is one. Existing numeric values are never reassigned.</summary>
        public CommandValues? RegistryCommand { get; init; }

        public ChordPattern Chord { get; init; }

        public DeclarationKind Kind { get; init; } = DeclarationKind.Command;

        /// <summary>The MODE axis. Composed with, never replaced by, the context axis below.</summary>
        public KeyScope Scope { get; init; } = KeyScope.Global;

        /// <summary>The interaction context it is declared at; null only for <see cref="InvocationReach.AcrossApplication"/>.</summary>
        public string? ContextId { get; init; }

        public InvocationReach Reach { get; init; } = InvocationReach.OwnSurface;

        /// <summary>
        /// An across-application gesture that also reserves its exact chord
        /// against local shortcuts and mnemonics. A property of the BINDING,
        /// not of <see cref="KeyScope.Global"/>: working in every mode is not
        /// the same claim as beating a dialog's own keys.
        /// </summary>
        public bool Reserved { get; init; }

        /// <summary>An approved transfer out of a native menu (the JJ key). Requires <see cref="Reserved"/>.</summary>
        public bool MayLeaveMenu { get; init; }

        /// <summary>The layer kinds this belongs to; <see cref="KeyLayerKinds.None"/> means it is not a layer key.</summary>
        public KeyLayerKinds LayerKinds { get; init; } = KeyLayerKinds.None;

        /// <summary>One layer by id, or null for EVERY layer of <see cref="LayerKinds"/> — the universal tier.</summary>
        public string? LayerId { get; init; }

        public DeclarationPrecedence Precedence { get; init; } = DeclarationPrecedence.Normal;

        public CommandRequirements Requires { get; init; } = CommandRequirements.None;

        public ModalPolicy Modal { get; init; } = ModalPolicy.RefusedInModal;

        public bool Repeats { get; init; }

        public TargetRule Target { get; init; } = TargetRule.Application;

        public int Direction { get; init; }

        public StepSource Step { get; init; } = StepSource.None;

        public bool Fine { get; init; }

        /// <summary>For a layer arrow: which axis this gesture is, checked against the selected target's <see cref="ValueLayerAxes"/>.</summary>
        public ValueLayerAxes Axis { get; init; } = ValueLayerAxes.None;

        public string? SelectsLayerTargetId { get; init; }

        public LayerEffect LayerEffect { get; init; } = LayerEffect.None;

        public string? OpensLayerId { get; init; }

        /// <summary>Required when <see cref="Kind"/> is <see cref="DeclarationKind.Retired"/>.</summary>
        public RelocationNotice? Relocation { get; init; }
    }

    /// <summary>One adjustable target of a value layer, as far as ownership needs to know it.</summary>
    public sealed record LayerTargetDeclaration
    {
        public string LayerId { get; init; } = "";
        /// <summary>Matches <see cref="ValueTarget.Id"/>.</summary>
        public string TargetId { get; init; } = "";
        /// <summary>Matches <see cref="ValueTarget.PerSlice"/>: the value belongs to the active slice.</summary>
        public bool PerSlice { get; init; }
        /// <summary>Matches <see cref="ValueTarget.Axes"/>.</summary>
        public ValueLayerAxes Axes { get; init; } = ValueLayerAxes.UpDown;
    }

    public enum DeclarationConflictKind
    {
        /// <summary>Two declarations of equal standing claim one gesture in one context and an overlapping mode.</summary>
        EqualPriorityClaim,
        /// <summary>A more specific declaration re-claims a gesture an authoritative declaration owns across that context.</summary>
        ShadowsAuthoritative,
        /// <summary>A layer re-declares a gesture the universal tier owns in every layer.</summary>
        ShadowsUniversalTier,
        /// <summary>A local declaration claims a chord that a reserved across-application declaration already wins everywhere.</summary>
        ShadowedByReservation,
        /// <summary>The declaration contradicts itself.</summary>
        Malformed,
    }

    public sealed record DeclarationConflict(
        DeclarationConflictKind Kind,
        GestureDeclaration First,
        GestureDeclaration? Second,
        string Detail);

    /// <summary>
    /// A VALIDATED set of declarations. The arbiter accepts nothing else, so
    /// there is no route by which two equal claims reach a decision and the
    /// earlier registration quietly wins.
    /// </summary>
    public sealed class GestureDeclarationSet
    {
        public IReadOnlyList<GestureDeclaration> Declarations { get; }
        public IReadOnlyList<LayerTargetDeclaration> LayerTargets { get; }

        private GestureDeclarationSet(IReadOnlyList<GestureDeclaration> d, IReadOnlyList<LayerTargetDeclaration> t)
        {
            Declarations = d;
            LayerTargets = t;
        }

        /// <summary>
        /// Validates and, only if clean, builds the set.
        /// </summary>
        /// <param name="contextParents">
        /// Each known context's parent (null for a root). With it, a field that
        /// re-claims an authoritative Home gesture is refused HERE; a context
        /// not listed is still handled correctly at decision time, where the
        /// authoritative declaration wins.
        /// </param>
        public static bool TryCreate(
            IEnumerable<GestureDeclaration> declarations,
            IEnumerable<LayerTargetDeclaration>? layerTargets,
            IReadOnlyDictionary<string, string?>? contextParents,
            out GestureDeclarationSet? set,
            out IReadOnlyList<DeclarationConflict> conflicts)
        {
            var d = declarations.ToList();
            var t = (layerTargets ?? Enumerable.Empty<LayerTargetDeclaration>()).ToList();
            conflicts = Validate(d, t, contextParents);
            set = conflicts.Count == 0 ? new GestureDeclarationSet(d, t) : null;
            return set != null;
        }

        /// <summary>As <see cref="TryCreate"/>, throwing with every conflict named.</summary>
        public static GestureDeclarationSet Create(
            IEnumerable<GestureDeclaration> declarations,
            IEnumerable<LayerTargetDeclaration>? layerTargets = null,
            IReadOnlyDictionary<string, string?>? contextParents = null)
        {
            if (TryCreate(declarations, layerTargets, contextParents, out var set, out var conflicts))
                return set!;
            throw new InvalidOperationException(
                "Gesture declarations conflict: " + string.Join("; ", conflicts.Select(c => c.Kind + " — " + c.Detail)));
        }

        public static IReadOnlyList<DeclarationConflict> Validate(
            IReadOnlyList<GestureDeclaration> declarations,
            IReadOnlyList<LayerTargetDeclaration> layerTargets,
            IReadOnlyDictionary<string, string?>? contextParents)
        {
            var found = new List<DeclarationConflict>();

            foreach (var d in declarations)
            {
                string? wrong = Malformation(d, layerTargets);
                if (wrong != null)
                    found.Add(new DeclarationConflict(DeclarationConflictKind.Malformed, d, null, d.CommandId + ": " + wrong));
            }

            for (int i = 0; i < declarations.Count; i++)
            {
                for (int j = i + 1; j < declarations.Count; j++)
                {
                    var a = declarations[i];
                    var b = declarations[j];
                    if (!a.Chord.Overlaps(b.Chord)) continue;
                    if (!ScopesCollide(a.Scope, b.Scope)) continue;

                    bool aLayer = a.LayerKinds != KeyLayerKinds.None;
                    bool bLayer = b.LayerKinds != KeyLayerKinds.None;
                    if (aLayer != bLayer) continue;   // a layer key and a non-layer key never compete: the layer is a different owner

                    DeclarationConflictKind? kind = aLayer ? LayerConflict(a, b) : SurfaceConflict(a, b, contextParents);
                    if (kind != null)
                        found.Add(new DeclarationConflict(kind.Value, a, b,
                            a.Chord + " is claimed by both " + a.CommandId + " and " + b.CommandId));
                }
            }
            return found;
        }

        private static string? Malformation(GestureDeclaration d, IReadOnlyList<LayerTargetDeclaration> layerTargets)
        {
            if (string.IsNullOrEmpty(d.CommandId)) return "no command identity";
            if (d.Chord.Family == KeyFamily.None && d.Chord.Key == GestureKey.None) return "no key";
            if (d.Kind == DeclarationKind.Retired && d.Relocation == null) return "retired without saying where the action went";
            if (d.Reach == InvocationReach.AcrossApplication && d.ContextId != null) return "across-application with a context";
            if (d.Reach != InvocationReach.AcrossApplication && d.ContextId == null && d.LayerKinds == KeyLayerKinds.None) return "no context";
            if (d.Reserved && d.Reach != InvocationReach.AcrossApplication) return "reserved but not across-application";
            if (d.MayLeaveMenu && !d.Reserved) return "may leave a menu but is not reserved";
            if (d.Target == TargetRule.SliceNamedByKey && d.Chord.Family != KeyFamily.SliceLetter) return "names a slice by key without a slice-letter chord";
            if (d.Target == TargetRule.LayerTargetSelected)
            {
                if (d.SelectsLayerTargetId == null || d.LayerId == null) return "selects a layer target without naming it";
                if (!layerTargets.Any(t => t.LayerId == d.LayerId && t.TargetId == d.SelectsLayerTargetId))
                    return "selects layer target " + d.SelectsLayerTargetId + ", which layer " + d.LayerId + " does not declare";
            }
            if (d.Target == TargetRule.LayerCurrentTarget && d.LayerKinds == KeyLayerKinds.None) return "adjusts a layer value outside any layer";
            if (d.Step != StepSource.None && d.Direction == 0) return "a step with no direction";
            return null;
        }

        private static DeclarationConflictKind? LayerConflict(GestureDeclaration a, GestureDeclaration b)
        {
            if ((a.LayerKinds & b.LayerKinds) == KeyLayerKinds.None) return null;
            if (a.LayerId != null && b.LayerId != null)
                return a.LayerId == b.LayerId ? DeclarationConflictKind.EqualPriorityClaim : null;
            if (a.LayerId == null && b.LayerId == null) return DeclarationConflictKind.EqualPriorityClaim;
            return DeclarationConflictKind.ShadowsUniversalTier;
        }

        private static DeclarationConflictKind? SurfaceConflict(
            GestureDeclaration a, GestureDeclaration b, IReadOnlyDictionary<string, string?>? parents)
        {
            bool aWide = a.Reach == InvocationReach.AcrossApplication;
            bool bWide = b.Reach == InvocationReach.AcrossApplication;

            if (aWide && bWide)
                return a.Reserved == b.Reserved && a.Precedence == b.Precedence
                    ? DeclarationConflictKind.EqualPriorityClaim
                    : a.Reserved != b.Reserved ? DeclarationConflictKind.ShadowedByReservation : null;

            if (aWide != bWide)
            {
                var wide = aWide ? a : b;
                return wide.Reserved ? DeclarationConflictKind.ShadowedByReservation : null;
            }

            if (a.ContextId == b.ContextId)
                return a.Precedence == b.Precedence ? DeclarationConflictKind.EqualPriorityClaim
                    : (a.Precedence == DeclarationPrecedence.Authoritative || b.Precedence == DeclarationPrecedence.Authoritative)
                        ? DeclarationConflictKind.ShadowsAuthoritative : null;

            if (parents != null)
            {
                if (a.Precedence == DeclarationPrecedence.Authoritative && Reaches(a, b.ContextId!, parents))
                    return DeclarationConflictKind.ShadowsAuthoritative;
                if (b.Precedence == DeclarationPrecedence.Authoritative && Reaches(b, a.ContextId!, parents))
                    return DeclarationConflictKind.ShadowsAuthoritative;
            }
            return null;
        }

        /// <summary>True when <paramref name="outer"/>'s reach includes the context <paramref name="inner"/>.</summary>
        private static bool Reaches(GestureDeclaration outer, string inner, IReadOnlyDictionary<string, string?> parents)
        {
            if (outer.Reach != InvocationReach.ContextAndDescendants) return false;
            string? walk = inner;
            for (int guard = 0; walk != null && guard < 64; guard++)
            {
                if (walk == outer.ContextId) return true;
                walk = parents.TryGetValue(walk, out var p) ? p : null;
            }
            return false;
        }

        /// <summary>Whether a scope is live in a mode. The same rule <c>KeyCommands</c> applies to its registry today.</summary>
        public static bool ScopeMatchesMode(KeyScope scope, OperatingMode mode) => scope switch
        {
            KeyScope.Global => true,
            KeyScope.Radio => mode == OperatingMode.Classic || mode == OperatingMode.Modern,
            KeyScope.Classic => mode == OperatingMode.Classic,
            KeyScope.Modern => mode == OperatingMode.Modern,
            KeyScope.Logging => mode == OperatingMode.Logging,
            _ => false,
        };

        /// <summary>Whether two scopes can be live in the same mode.</summary>
        public static bool ScopesCollide(KeyScope a, KeyScope b)
        {
            foreach (OperatingMode m in Enum.GetValues(typeof(OperatingMode)))
                if (ScopeMatchesMode(a, m) && ScopeMatchesMode(b, m)) return true;
            return false;
        }
    }
}

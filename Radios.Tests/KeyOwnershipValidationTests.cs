using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Radios.KeyOwnership;
using Xunit;
using static Radios.Tests.KeyOwn;

namespace Radios.Tests
{
    /// <summary>
    /// The declaration set refuses to exist with two equal claims in it — never
    /// first-registration-wins — and the key-ownership namespace stays plain
    /// data: no UI framework type, no radio library type.
    /// </summary>
    public class KeyOwnershipValidationTests
    {
        private static GestureDeclaration HomeKey(string id, GestureKey key, string context = "home",
            InvocationReach reach = InvocationReach.ContextAndDescendants, KeyScope scope = KeyScope.Radio) =>
            new GestureDeclaration
            {
                CommandId = id,
                Chord = new ChordPattern(key),
                Scope = scope,
                ContextId = context,
                Reach = reach,
            };

        [Fact]
        public void The_ruled_Home_map_is_itself_free_of_conflicts()
        {
            Assert.True(GestureDeclarationSet.TryCreate(
                RuledHomeKeyMap.Declarations, RuledHomeKeyMap.LayerTargets, RuledHomeKeyMap.ContextParents,
                out var set, out var conflicts));
            Assert.NotNull(set);
            Assert.Empty(conflicts);
        }

        [Fact]
        public void Two_equal_priority_claims_on_one_gesture_and_context_fail_validation()
        {
            var planted = new[] { HomeKey("test.first", GestureKey.G), HomeKey("test.second", GestureKey.G) };

            Assert.False(GestureDeclarationSet.TryCreate(planted, null, null, out var set, out var conflicts));
            Assert.Null(set);   // there is no set to arbitrate over, so neither registration can win
            var conflict = Assert.Single(conflicts);
            Assert.Equal(DeclarationConflictKind.EqualPriorityClaim, conflict.Kind);
            Assert.Equal("test.first", conflict.First.CommandId);
            Assert.Equal("test.second", conflict.Second!.CommandId);

            Assert.Throws<InvalidOperationException>(() => GestureDeclarationSet.Create(planted));

            // The order of registration changes nothing.
            Assert.False(GestureDeclarationSet.TryCreate(planted.Reverse(), null, null, out _, out _));

            // Positive control: the same two on different keys are fine.
            Assert.True(GestureDeclarationSet.TryCreate(
                new[] { HomeKey("test.first", GestureKey.G), HomeKey("test.second", GestureKey.K) }, null, null, out _, out _));
        }

        [Theory]
        [InlineData(KeyScope.Classic, KeyScope.Modern, false)]    // never live together
        [InlineData(KeyScope.Radio, KeyScope.Logging, false)]
        [InlineData(KeyScope.Global, KeyScope.Classic, true)]
        [InlineData(KeyScope.Radio, KeyScope.Modern, true)]
        [InlineData(KeyScope.Global, KeyScope.Logging, true)]
        public void The_mode_axis_still_decides_whether_two_claims_can_meet(KeyScope a, KeyScope b, bool conflict)
        {
            var pair = new[] { HomeKey("test.a", GestureKey.G, scope: a), HomeKey("test.b", GestureKey.G, scope: b) };
            Assert.Equal(!conflict, GestureDeclarationSet.TryCreate(pair, null, null, out _, out _));
        }

        [Theory]
        [InlineData(KeyScope.Global, OperatingMode.Classic, true)]
        [InlineData(KeyScope.Global, OperatingMode.Logging, true)]
        [InlineData(KeyScope.Radio, OperatingMode.Classic, true)]
        [InlineData(KeyScope.Radio, OperatingMode.Modern, true)]
        [InlineData(KeyScope.Radio, OperatingMode.Logging, false)]
        [InlineData(KeyScope.Classic, OperatingMode.Modern, false)]
        [InlineData(KeyScope.Modern, OperatingMode.Modern, true)]
        [InlineData(KeyScope.Logging, OperatingMode.Logging, true)]
        [InlineData(KeyScope.Logging, OperatingMode.Modern, false)]
        public void KeyScope_means_what_it_has_always_meant(KeyScope scope, OperatingMode mode, bool live)
        {
            Assert.Equal(live, GestureDeclarationSet.ScopeMatchesMode(scope, mode));
        }

        [Theory]
        [InlineData("home.slice-operations", "test.slice-gain-down", GestureKey.Down)]   // gain wants its arrow back
        [InlineData("home.slice", "test.previous-slice", GestureKey.Up)]                 // selection wants its arrow back
        [InlineData("home.frequency", "test.jump-to-frequency", GestureKey.PageDown)]    // the old PageDown focus jump
        public void A_field_that_re_claims_a_gesture_Home_owns_is_refused_at_validation(string field, string id, GestureKey key)
        {
            var declarations = new List<GestureDeclaration>(RuledHomeKeyMap.Declarations)
            {
                HomeKey(id, key, field, InvocationReach.OwnSurface),
            };
            Assert.False(GestureDeclarationSet.TryCreate(declarations, RuledHomeKeyMap.LayerTargets,
                RuledHomeKeyMap.ContextParents, out _, out var conflicts));
            Assert.Contains(conflicts, c => c.Kind == DeclarationConflictKind.ShadowsAuthoritative
                && (c.First.CommandId == id || c.Second!.CommandId == id));
        }

        [Fact]
        public void A_field_validation_has_never_heard_of_still_cannot_take_the_arrow_from_tuning()
        {
            // The context is missing from the parent map, so validation cannot
            // see the shadowing. The decision must still come out as tuning.
            var declarations = new List<GestureDeclaration>(RuledHomeKeyMap.Declarations)
            {
                HomeKey("test.rogue-gain-down", GestureKey.Down, "home.rogue", InvocationReach.OwnSurface),
            };
            var set = GestureDeclarationSet.Create(declarations, RuledHomeKeyMap.LayerTargets, RuledHomeKeyMap.ContextParents);

            var fake = new FakeActions();
            var action = Assert.IsType<ExecuteAction>(fake.Press(OnHome("home.rogue", Bare(GestureKey.Down)), set));
            Assert.Equal("home.tune-down", action.CommandId);
            Assert.Empty(fake.OtherActions);
        }

        [Fact]
        public void A_nearer_declaration_beats_a_wider_ordinary_one_and_a_permitted_override_beats_both()
        {
            var wide = HomeKey("test.home-wide", GestureKey.G);
            var field = HomeKey("test.field", GestureKey.G, "home.slice", InvocationReach.OwnSurface);
            var set = GestureDeclarationSet.Create(new[] { wide, field });

            Assert.Equal("test.field", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(OnHome("home.slice", Bare(GestureKey.G)), set)).CommandId);
            Assert.Equal("test.home-wide", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(OnHome("home.frequency", Bare(GestureKey.G)), set)).CommandId);

            // Registration order is not what decided that.
            var reversed = GestureDeclarationSet.Create(new[] { field, wide });
            Assert.Equal("test.field", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(OnHome("home.slice", Bare(GestureKey.G)), reversed)).CommandId);

            var registry = new GestureDeclaration
            {
                CommandId = "test.registry-override",
                Chord = new ChordPattern(GestureKey.G),
                Reach = InvocationReach.AcrossApplication,
                Precedence = DeclarationPrecedence.OverridesLocal,
                Modal = ModalPolicy.AllowedInModal,
            };
            var withOverride = GestureDeclarationSet.Create(new[] { wide, field, registry });
            Assert.Equal("test.registry-override",
                Assert.IsType<ExecuteAction>(KeyArbiter.Decide(OnHome("home.slice", Bare(GestureKey.G)), withOverride)).CommandId);
        }

        [Fact]
        public void A_layer_may_not_invent_its_own_slice_selection_over_the_universal_tier()
        {
            var declarations = new List<GestureDeclaration>(RuledHomeKeyMap.Declarations)
            {
                new GestureDeclaration
                {
                    CommandId = "test.audio-steals-shift-c",
                    Chord = new ChordPattern(GestureKey.C, Shift: ShiftRequirement.Left),
                    LayerKinds = KeyLayerKinds.ValueLayer,
                    LayerId = "audio",
                },
            };
            Assert.False(GestureDeclarationSet.TryCreate(declarations, RuledHomeKeyMap.LayerTargets,
                RuledHomeKeyMap.ContextParents, out _, out var conflicts));
            Assert.Contains(conflicts, c => c.Kind == DeclarationConflictKind.ShadowsUniversalTier);
        }

        [Fact]
        public void A_dialog_shortcut_on_a_reserved_chord_is_reported_not_silently_shadowed()
        {
            var reserved = new GestureDeclaration
            {
                CommandId = "test.speak-tx-status",
                Chord = new ChordPattern(GestureKey.S, Alt: true, Shift: ShiftRequirement.Either),
                Reach = InvocationReach.AcrossApplication,
                Reserved = true,
            };
            var dialogSave = new GestureDeclaration
            {
                CommandId = "test.save-preset",
                Chord = new ChordPattern(GestureKey.S, Alt: true, Shift: ShiftRequirement.Left),
                ContextId = "dialog.preset",
                Reach = InvocationReach.ContextAndDescendants,
            };
            Assert.False(GestureDeclarationSet.TryCreate(new[] { reserved, dialogSave }, null, null, out _, out var conflicts));
            Assert.Equal(DeclarationConflictKind.ShadowedByReservation, Assert.Single(conflicts).Kind);
        }

        [Fact]
        public void A_layer_key_and_a_Home_key_on_the_same_gesture_do_not_conflict_because_the_layer_is_a_different_owner()
        {
            // Bare Down is tuning on Home and value-decrease in a value layer,
            // and the ruled map — already validated above — holds both.
            Assert.Contains(RuledHomeKeyMap.Declarations, d => d.LayerKinds == KeyLayerKinds.None && d.Chord.Key == GestureKey.Down && !d.Chord.Alt);
            Assert.Contains(RuledHomeKeyMap.Declarations, d => d.LayerKinds != KeyLayerKinds.None && d.Chord.Key == GestureKey.Down);
        }

        public static IEnumerable<object[]> MalformedDeclarations()
        {
            yield return new object[] { HomeKey("", GestureKey.G) };
            yield return new object[] { HomeKey("test.no-key", GestureKey.None) };
            yield return new object[] { HomeKey("test.retired-to-nowhere", GestureKey.G) with { Kind = DeclarationKind.Retired } };
            yield return new object[] { HomeKey("test.reserved-but-local", GestureKey.G) with { Reserved = true } };
            yield return new object[] { HomeKey("test.step-without-direction", GestureKey.G) with { Step = StepSource.TuningCoarse } };
            yield return new object[] { HomeKey("test.slice-by-key-without-letters", GestureKey.G) with { Target = TargetRule.SliceNamedByKey } };
            yield return new object[] { HomeKey("test.layer-value-outside-a-layer", GestureKey.G) with { Target = TargetRule.LayerCurrentTarget } };
            yield return new object[]
            {
                new GestureDeclaration
                {
                    CommandId = "test.selects-an-undeclared-target",
                    Chord = new ChordPattern(GestureKey.G),
                    LayerKinds = KeyLayerKinds.ValueLayer,
                    LayerId = "audio",
                    Target = TargetRule.LayerTargetSelected,
                    SelectsLayerTargetId = "no-such-target",
                },
            };
        }

        [Theory]
        [MemberData(nameof(MalformedDeclarations))]
        public void A_declaration_that_contradicts_itself_is_refused(GestureDeclaration bad)
        {
            Assert.False(GestureDeclarationSet.TryCreate(new[] { bad }, RuledHomeKeyMap.LayerTargets, null, out _, out var conflicts));
            Assert.Contains(conflicts, c => c.Kind == DeclarationConflictKind.Malformed);
        }

        [Fact]
        public void An_Either_Shift_pattern_overlaps_a_sided_one_and_two_different_sides_do_not()
        {
            var either = new ChordPattern(GestureKey.Left, Shift: ShiftRequirement.Either);
            var left = new ChordPattern(GestureKey.Left, Shift: ShiftRequirement.Left);
            var right = new ChordPattern(GestureKey.Left, Shift: ShiftRequirement.Right);
            var none = new ChordPattern(GestureKey.Left);

            Assert.True(either.Overlaps(left));
            Assert.False(left.Overlaps(right));
            Assert.False(none.Overlaps(either));
            Assert.True(ChordPattern.ForFamily(KeyFamily.SliceLetter, shift: ShiftRequirement.Either)
                .Overlaps(new ChordPattern(GestureKey.H, Shift: ShiftRequirement.Right)));
            Assert.False(ChordPattern.ForFamily(KeyFamily.SliceLetter, shift: ShiftRequirement.Either)
                .Overlaps(new ChordPattern(GestureKey.I, Shift: ShiftRequirement.Right)));
        }

        [Fact]
        public void The_ruled_map_allocates_no_layer_letter_beyond_the_audio_opener_already_ruled()
        {
            var openers = RuledHomeKeyMap.Declarations.Where(d => d.LayerEffect == LayerEffect.OpenLayer).ToList();
            var only = Assert.Single(openers);
            Assert.Equal(GestureKey.A, only.Chord.Key);
            Assert.Equal("audio", only.OpensLayerId);
        }

        // ── Key identity and purity ─────────────────────────────────────

        [Fact]
        public void GestureKey_values_are_the_virtual_key_codes_so_an_adapter_converts_with_a_cast()
        {
            Assert.Equal(0x26, (int)GestureKey.Up);
            Assert.Equal(0x28, (int)GestureKey.Down);
            Assert.Equal(0x21, (int)GestureKey.PageUp);
            Assert.Equal(0x22, (int)GestureKey.PageDown);
            Assert.Equal(0x41, (int)GestureKey.A);
            Assert.Equal(0x48, (int)GestureKey.H);
            Assert.Equal(0x5A, (int)GestureKey.Z);
            Assert.Equal(0x39, (int)GestureKey.D9);
            Assert.Equal(0x7B, (int)GestureKey.F12);
            Assert.Equal(0xBF, (int)GestureKey.Slash);
            Assert.Equal((int)System.Windows.Forms.Keys.Oem2, (int)GestureKey.Slash);
            Assert.Equal((int)System.Windows.Forms.Keys.J, (int)GestureKey.J);
        }

        private static IEnumerable<string> ForbiddenTypesReachableFrom(IEnumerable<Type> types)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            bool Forbidden(Type t)
            {
                if (t.IsByRef || t.IsArray) t = t.GetElementType()!;
                if (t.IsGenericType && t.GetGenericArguments().Any(Forbidden)) return true;
                string ns = t.Namespace ?? "";
                return ns == "System.Windows" || ns.StartsWith("System.Windows.", StringComparison.Ordinal)
                    || ns.StartsWith("Flex", StringComparison.Ordinal) || ns.StartsWith("JJFlexWpf", StringComparison.Ordinal);
            }
            foreach (var type in types)
            {
                foreach (var f in type.GetFields(all)) if (Forbidden(f.FieldType)) yield return type.Name + "." + f.Name;
                foreach (var p in type.GetProperties(all)) if (Forbidden(p.PropertyType)) yield return type.Name + "." + p.Name;
                foreach (var m in type.GetMethods(all))
                {
                    if (Forbidden(m.ReturnType)) yield return type.Name + "." + m.Name;
                    foreach (var prm in m.GetParameters()) if (Forbidden(prm.ParameterType)) yield return type.Name + "." + m.Name;
                }
            }
        }

        [Fact]
        public void No_type_in_the_key_ownership_namespace_touches_a_UI_framework_or_the_radio_library()
        {
            var ours = typeof(KeyArbiter).Assembly.GetTypes()
                .Where(t => t.Namespace == "Radios.KeyOwnership").ToList();
            Assert.True(ours.Count >= 20, "the scan found the namespace");

            // Positive control: the scan DOES see a UI framework type where one
            // exists. ValueTarget holds System.Windows.Forms.Keys.
            Assert.NotEmpty(ForbiddenTypesReachableFrom(new[] { typeof(ValueTarget) }));

            Assert.Empty(ForbiddenTypesReachableFrom(ours));
        }
    }
}

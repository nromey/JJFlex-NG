using System.Collections.Generic;

namespace Radios.KeyOwnership
{
    /// <summary>
    /// The RULED Home key grammar, written as declarations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOTHING LOADS THIS YET. The running application still takes every key
    /// from <c>KeyCommands</c> and the field handlers; this set neither
    /// replaces, feeds nor alters that table. It exists so the grammar can be
    /// stated once and tested before anything is wired to it.
    /// </para>
    /// <para>
    /// What it states, and where each part was ruled:
    /// </para>
    /// <para>
    /// Bare Up and Down on the Home command surface TUNE — declared once at
    /// Home scope and inherited by every Home field, including fields that do
    /// not exist yet. Gain and slice selection both gave up the bare arrows
    /// (#517, ruled 2026-09-20).
    /// </para>
    /// <para>
    /// Inside a layer the arrows act on that layer's value or target. In the
    /// audio layer V is slice volume, O is PC output and P is pan — three
    /// different things (#514; the identities match the audio layer's own
    /// target ids in the source).
    /// </para>
    /// <para>
    /// Shift plus a slice letter is the slice jump INSIDE the JJ key's tier,
    /// and inside every layer (#515, tier two). It is not declared outside a
    /// layer, so a bare Shift+A in an editor is an uppercase A.
    /// </para>
    /// <para>
    /// PageUp and PageDown on Home are retired: they answer with where pan
    /// went, and perform no pan and no focus jump (#517, ruled 2026-09-21).
    /// </para>
    /// <para>
    /// Alt+Up and Alt+Down stay what they are today, band up and band down.
    /// They are here so that the map demonstrably leaves them alone.
    /// </para>
    /// <para>
    /// No layer letter is allocated here. JJ key A for audio is an existing,
    /// ruled opener (#514, #515); tuning and slice layers have no letter.
    /// </para>
    /// </remarks>
    public static class RuledHomeKeyMap
    {
        public const string HomeContext = "home";
        public const string FrequencyField = "home.frequency";
        public const string SliceField = "home.slice";
        public const string SliceOperationsField = "home.slice-operations";

        public const string JJKeyLayer = "jjkey";
        public const string AudioLayer = "audio";

        public const string SliceVolumeTarget = "slice-volume";
        public const string PcOutputTarget = "pc-output";
        public const string PanTarget = "pan";
        public const string HeadphoneTarget = "headphone";

        public const string TuneUp = "home.tune-up";
        public const string TuneDown = "home.tune-down";
        public const string PanRetired = "home.pan-retired";
        public const string BandUp = "band.up";
        public const string BandDown = "band.down";
        public const string ArmJJKey = "jjkey.arm";
        public const string SliceJump = "slice.jump";
        public const string OpenAudioLayer = "jjkey.open-audio-layer";
        public const string LayerHelpList = "layer.help-list";
        public const string LayerHelpExplorer = "layer.help-explorer";
        public const string LayerSelectTarget = "layer.select-target";
        public const string LayerValueIncrease = "layer.value-increase";
        public const string LayerValueDecrease = "layer.value-decrease";
        public const string LayerConfirm = "layer.confirm";
        public const string LayerCancel = "layer.cancel";

        public const string PanRelocationId = "pan-moved-to-audio-layer";

        /// <summary>Parents of the Home contexts known today. A field missing from here still inherits Home's keys.</summary>
        public static IReadOnlyDictionary<string, string?> ContextParents { get; } = new Dictionary<string, string?>
        {
            [HomeContext] = null,
            [FrequencyField] = HomeContext,
            [SliceField] = HomeContext,
            [SliceOperationsField] = HomeContext,
        };

        public static IReadOnlyList<LayerTargetDeclaration> LayerTargets { get; } = new[]
        {
            new LayerTargetDeclaration { LayerId = AudioLayer, TargetId = SliceVolumeTarget, PerSlice = true, Axes = ValueLayerAxes.UpDown },
            new LayerTargetDeclaration { LayerId = AudioLayer, TargetId = PcOutputTarget, PerSlice = false, Axes = ValueLayerAxes.UpDown },
            new LayerTargetDeclaration { LayerId = AudioLayer, TargetId = PanTarget, PerSlice = true, Axes = ValueLayerAxes.Both },
            new LayerTargetDeclaration { LayerId = AudioLayer, TargetId = HeadphoneTarget, PerSlice = false, Axes = ValueLayerAxes.UpDown },
        };

        public static IReadOnlyList<GestureDeclaration> Declarations { get; } = Build();

        public static GestureDeclarationSet CreateSet() =>
            GestureDeclarationSet.Create(Declarations, LayerTargets, ContextParents);

        private static List<GestureDeclaration> Build()
        {
            const KeyLayerKinds anyLayer = KeyLayerKinds.JJKeyArmed | KeyLayerKinds.ValueLayer;
            var list = new List<GestureDeclaration>();

            // ── Home: tuning, once ──────────────────────────────────────
            list.Add(Tune(TuneUp, GestureKey.Up, +1));
            list.Add(Tune(TuneDown, GestureKey.Down, -1));

            // ── Home: the freed paging keys say where pan went ──────────
            foreach (var key in new[] { GestureKey.PageUp, GestureKey.PageDown })
                list.Add(new GestureDeclaration
                {
                    CommandId = PanRetired,
                    Chord = new ChordPattern(key),
                    Kind = DeclarationKind.Retired,
                    Scope = KeyScope.Radio,
                    ContextId = HomeContext,
                    Reach = InvocationReach.ContextAndDescendants,
                    Precedence = DeclarationPrecedence.Authoritative,
                    Relocation = new RelocationNotice { Id = PanRelocationId, LayerId = AudioLayer, LayerTargetId = PanTarget },
                });

            // ── Home: band change keeps Alt+arrows, exactly as today ────
            list.Add(Band(BandUp, GestureKey.Up, CommandValues.BandUp));
            list.Add(Band(BandDown, GestureKey.Down, CommandValues.BandDown));

            // ── The JJ key: reserved everywhere, and may leave a menu ───
            list.Add(new GestureDeclaration
            {
                CommandId = ArmJJKey,
                Chord = new ChordPattern(GestureKey.J, Control: true),
                Reach = InvocationReach.AcrossApplication,
                Reserved = true,
                MayLeaveMenu = true,
                Modal = ModalPolicy.AllowedInModal,
                LayerEffect = LayerEffect.ArmJJKey,
            });

            // ── Tier two, universal: Shift+letter jumps to that slice ───
            list.Add(new GestureDeclaration
            {
                CommandId = SliceJump,
                Chord = ChordPattern.ForFamily(KeyFamily.SliceLetter, shift: ShiftRequirement.Either),
                LayerKinds = anyLayer,
                Requires = CommandRequirements.Radio | CommandRequirements.NamedSlice,
                Modal = ModalPolicy.AllowedInModal,
                Target = TargetRule.SliceNamedByKey,
            });

            // ── Every layer: H lists, question mark explores ────────────
            list.Add(InLayer(LayerHelpList, new ChordPattern(GestureKey.H), anyLayer, null));
            list.Add(InLayer(LayerHelpExplorer, new ChordPattern(GestureKey.Slash), anyLayer, null));
            list.Add(InLayer(LayerHelpExplorer, new ChordPattern(GestureKey.Slash, Shift: ShiftRequirement.Either), anyLayer, null));
            list.Add(InLayer(LayerCancel, new ChordPattern(GestureKey.Escape), anyLayer, null));
            list.Add(InLayer(LayerConfirm, new ChordPattern(GestureKey.Enter), KeyLayerKinds.ValueLayer, null));

            // ── Tier one: JJ key, A opens the audio layer ───────────────
            list.Add(InLayer(OpenAudioLayer, new ChordPattern(GestureKey.A), KeyLayerKinds.JJKeyArmed, JJKeyLayer) with
            {
                LayerEffect = LayerEffect.OpenLayer,
                OpensLayerId = AudioLayer,
            });

            // ── Every value layer: the arrows move the chosen target ────
            list.Add(Adjust(LayerValueIncrease, GestureKey.Up, +1, ValueLayerAxes.UpDown));
            list.Add(Adjust(LayerValueDecrease, GestureKey.Down, -1, ValueLayerAxes.UpDown));
            list.Add(Adjust(LayerValueIncrease, GestureKey.Right, +1, ValueLayerAxes.LeftRight));
            list.Add(Adjust(LayerValueDecrease, GestureKey.Left, -1, ValueLayerAxes.LeftRight));

            // ── The audio layer's targets ───────────────────────────────
            list.Add(Select(new ChordPattern(GestureKey.V), SliceVolumeTarget));
            list.Add(Select(new ChordPattern(GestureKey.O), PcOutputTarget));
            list.Add(Select(new ChordPattern(GestureKey.P), PanTarget));
            list.Add(Select(new ChordPattern(GestureKey.H, Control: true), HeadphoneTarget));

            return list;
        }

        private static GestureDeclaration Tune(string id, GestureKey key, int direction) => new GestureDeclaration
        {
            CommandId = id,
            Chord = new ChordPattern(key),
            Scope = KeyScope.Radio,
            ContextId = HomeContext,
            Reach = InvocationReach.ContextAndDescendants,
            Precedence = DeclarationPrecedence.Authoritative,
            Requires = CommandRequirements.Radio | CommandRequirements.ActiveSlice,
            Repeats = true,
            Target = TargetRule.ActiveSlice,
            Direction = direction,
            Step = StepSource.TuningCoarse,
        };

        private static GestureDeclaration Band(string id, GestureKey key, CommandValues command) => new GestureDeclaration
        {
            CommandId = id,
            RegistryCommand = command,
            Chord = new ChordPattern(key, Alt: true),
            Scope = KeyScope.Radio,
            ContextId = HomeContext,
            Reach = InvocationReach.ContextAndDescendants,
            Requires = CommandRequirements.Radio,
        };

        private static GestureDeclaration InLayer(string id, ChordPattern chord, KeyLayerKinds kinds, string? layerId) => new GestureDeclaration
        {
            CommandId = id,
            Chord = chord,
            LayerKinds = kinds,
            LayerId = layerId,
            Modal = ModalPolicy.AllowedInModal,
        };

        private static GestureDeclaration Adjust(string id, GestureKey key, int direction, ValueLayerAxes axis) =>
            InLayer(id, new ChordPattern(key), KeyLayerKinds.ValueLayer, null) with
            {
                Target = TargetRule.LayerCurrentTarget,
                Direction = direction,
                Axis = axis,
                Repeats = true,
            };

        private static GestureDeclaration Select(ChordPattern chord, string targetId) =>
            InLayer(LayerSelectTarget, chord, KeyLayerKinds.ValueLayer, AudioLayer) with
            {
                Target = TargetRule.LayerTargetSelected,
                SelectsLayerTargetId = targetId,
                LayerEffect = LayerEffect.SelectTarget,
            };
    }
}

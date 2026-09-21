using System;
using System.Collections.Generic;

namespace Radios.KeyOwnership
{
    /// <summary>Where the keystroke is actually going.</summary>
    public enum InputDestination
    {
        /// <summary>One of this application's participating windows.</summary>
        Participating,
        /// <summary>Another application, or a window of ours that does not take part. Not ours to decide.</summary>
        OutsideApplication,
        /// <summary>
        /// Mid-transition: the host could not say. The arbiter refuses
        /// application actions rather than guessing at a remembered target.
        /// </summary>
        Unknown,
    }

    /// <summary>What kind of top-level window is active.</summary>
    public enum WindowKind
    {
        HomeShell,
        ModalDialog,
        ModelessDialog,
        OwnedPopup,
    }

    /// <summary>
    /// The SEMANTIC role of the focused surface — what a key means there, not
    /// what control class it is. The frequency display is a TextBox and a
    /// command surface; a callsign box is a TextBox and an editor. Only an
    /// adapter that knows the surface may say <see cref="CommandSurface"/>.
    /// </summary>
    public enum SurfaceRole
    {
        /// <summary>
        /// Nobody has classified this surface. It is the DEFAULT value on
        /// purpose, and it means local/native input: an unknown control never
        /// receives radio commands.
        /// </summary>
        Unknown = 0,
        /// <summary>Keys are commands here (the Home fields).</summary>
        CommandSurface,
        /// <summary>Keys are text here. Typing, selection, clipboard and caret movement stay local.</summary>
        TextEditor,
        /// <summary>A list, button, combo, checkbox, tree: it owns its own navigation and activation.</summary>
        StandardControl,
        /// <summary>Hosted content we do not control (a browser, a native child). Everything stays local.</summary>
        ForeignEmbedded,
    }

    /// <summary>
    /// The application's operating mode — the axis <see cref="KeyScope"/>
    /// already describes. Interaction context is a second, independent axis.
    /// </summary>
    public enum OperatingMode
    {
        Classic = 0,
        Modern = 1,
        Logging = 2,
    }

    /// <summary>What the native menu is doing, as observed by the shell.</summary>
    public enum NativeMenuState
    {
        None = 0,
        /// <summary>A committed menu-entry gesture has been seen; activation is posted but not yet confirmed.</summary>
        EntryPending,
        /// <summary>The menu bar is selected and no popup is open yet — the interval a popup-open flag misses.</summary>
        BarSelected,
        PopupOpen,
        NestedPopupOpen,
        SystemMenu,
        ContextMenu,
    }

    /// <summary>The shell's report of native-menu ownership, with enough to tell a live report from a stale one.</summary>
    public sealed record NativeMenuObservation
    {
        public NativeMenuState State { get; init; } = NativeMenuState.None;

        /// <summary>The window whose menu this is.</summary>
        public string OwnerWindowId { get; init; } = "";

        /// <summary>
        /// The snapshot's <see cref="InputSnapshot.ContextGeneration"/> at the
        /// moment this was last confirmed. A report from an older generation
        /// has not been reconciled with whatever changed since.
        /// </summary>
        public long ObservedAtGeneration { get; init; }

        public static NativeMenuObservation NoMenu { get; } = new NativeMenuObservation();
    }

    /// <summary>The active top-level window.</summary>
    public sealed record WindowContext
    {
        public string Id { get; init; } = "";
        public WindowKind Kind { get; init; } = WindowKind.HomeShell;
        /// <summary>For an owned popup or child dialog, the window that owns it.</summary>
        public string? OwnerWindowId { get; init; }
    }

    /// <summary>The focused surface, described semantically.</summary>
    public sealed record FocusedSurface
    {
        /// <summary>The window this surface lives in. A surface outside the ACTIVE window has no claim.</summary>
        public string WindowId { get; init; } = "";

        public SurfaceRole Role { get; init; } = SurfaceRole.Unknown;

        /// <summary>
        /// Interaction contexts from the NEAREST outward, e.g.
        /// <c>["home.slice-operations", "home"]</c>. A declaration made at
        /// "home" reaches every surface whose path contains "home" — which is
        /// how a field nobody anticipated inherits Home's keys. The Home rescue
        /// root, with no fields constructed, is simply <c>["home"]</c>.
        /// </summary>
        public IReadOnlyList<string> ContextPath { get; init; } = Array.Empty<string>();

        /// <summary>A combo drop-down or similar is open on this control; Escape closes it before the dialog.</summary>
        public bool HasOpenPopup { get; init; }

        /// <summary>A multi-line editor: Enter is text, not the dialog's default action.</summary>
        public bool AcceptsReturn { get; init; }
    }

    /// <summary>Which kind of explicit keyboard layer holds the keys.</summary>
    [Flags]
    public enum KeyLayerKinds
    {
        None = 0,
        /// <summary>The JJ key has been pressed and the next key is its command.</summary>
        JJKeyArmed = 1,
        /// <summary>A persistent value layer (audio, filter) — <see cref="ValueSubLayer"/>.</summary>
        ValueLayer = 2,
        /// <summary>Help-armed: owns ONLY its documented help and cancel keys; everything else continues below.</summary>
        HelpArmed = 4,
        /// <summary>A layer's own help list is up as a child capture; its navigation suspends the parent layer.</summary>
        LayerHelpList = 8,
    }

    /// <summary>The explicit layer state, as plain data.</summary>
    public sealed record KeyLayerState
    {
        public KeyLayerKinds Kind { get; init; } = KeyLayerKinds.None;

        /// <summary>The layer's identity — <see cref="ValueSubLayerDefinition.Id"/> for a value layer.</summary>
        public string LayerId { get; init; } = "";

        /// <summary>The selected target's <see cref="ValueTarget.Id"/>, or null when none is chosen yet.</summary>
        public string? CurrentTargetId { get; init; }

        /// <summary>The window the layer was opened from. A layer never follows the operator into an unrelated window.</summary>
        public string OriginWindowId { get; init; } = "";

        /// <summary>The radio session the layer's targets were bound under.</summary>
        public string? RadioSessionId { get; init; }

        /// <summary>False once the target's slice, radio or window has gone.</summary>
        public bool TargetValid { get; init; } = true;

        /// <summary>Suspended (a menu or the help child owns the keys); a suspended layer claims nothing.</summary>
        public bool Suspended { get; init; }
    }

    /// <summary>The radio facts availability checks need. No FlexLib type appears here.</summary>
    public sealed record RadioFacts
    {
        public bool Connected { get; init; }
        public string? SessionId { get; init; }
        /// <summary>The active slice's letter, or null when there is none.</summary>
        public char? ActiveSlice { get; init; }
        /// <summary>The letters of the slices that exist right now.</summary>
        public IReadOnlyList<char> Slices { get; init; } = Array.Empty<char>();

        public static RadioFacts Disconnected { get; } = new RadioFacts();
    }

    /// <summary>The tuning state a tune command's step is read from. Field identity is never part of it.</summary>
    public sealed record TuningContext
    {
        /// <summary>Modern tuning mode's coarse step, in hertz.</summary>
        public long CoarseStepHz { get; init; }
        /// <summary>Modern tuning mode's fine step, in hertz.</summary>
        public long FineStepHz { get; init; }
        /// <summary>Classic tuning mode's selected-digit multiplier, in hertz.</summary>
        public long ClassicDigitHz { get; init; }
    }

    /// <summary>An operation this application started on a key-down and that a key-up must end (push-to-talk).</summary>
    public sealed record OwnedOperation
    {
        public string OperationId { get; init; } = "";
        /// <summary>The key whose release ends it. Modifiers are deliberately absent: they may have changed.</summary>
        public GestureKey Key { get; init; }
    }

    /// <summary>
    /// Everything the arbiter may know about one keystroke, as plain data.
    /// No WPF type, no window handle, no FlexLib, no speech: a host adapter
    /// builds this, and a test builds it with object initialisers alone.
    /// </summary>
    public sealed record InputSnapshot
    {
        /// <summary>The EFFECTIVE key (already unwrapped from WPF's <c>Key.System</c>) and its exact modifiers.</summary>
        public KeyChord Chord { get; init; }

        public KeyPhase Phase { get; init; } = KeyPhase.Down;

        /// <summary>
        /// The press is producing text through a composition owner — AltGr, a
        /// dead key, an IME. It never matches a command, however much its
        /// Ctrl+Alt bits look like one.
        /// </summary>
        public bool IsTextComposition { get; init; }

        public InputDestination Destination { get; init; } = InputDestination.Participating;

        public WindowContext ActiveWindow { get; init; } = new WindowContext();

        /// <summary>The window holding the active modal boundary, or null when nothing is modal.</summary>
        public string? ModalBoundaryWindowId { get; init; }

        /// <summary>The focused surface, or null when the active window has no focused child.</summary>
        public FocusedSurface? Surface { get; init; }

        public NativeMenuObservation Menu { get; init; } = NativeMenuObservation.NoMenu;

        /// <summary>The explicit layer, or null.</summary>
        public KeyLayerState? Layer { get; init; }

        public OperatingMode Mode { get; init; } = OperatingMode.Modern;

        public RadioFacts Radio { get; init; } = RadioFacts.Disconnected;

        public TuningContext Tuning { get; init; } = new TuningContext();

        public IReadOnlyList<OwnedOperation> OwnedOperations { get; init; } = Array.Empty<OwnedOperation>();

        /// <summary>This application holds an active OR PENDING transmit operation, by its own bookkeeping.</summary>
        public bool LocalTransmitActiveOrPending { get; init; }

        /// <summary>The radio reports transmit. May lag; the local flag above is consulted as well, never instead.</summary>
        public bool RadioReportsTransmit { get; init; }

        /// <summary>Command identities that are declared but have no executor wired (a null delegate).</summary>
        public IReadOnlyCollection<string> CommandsWithoutExecutor { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Bumped by the coordinator whenever the active window, the modal
        /// boundary, the radio session, the active slice or menu ownership
        /// changes. NOT bumped by layer entry or by choosing a layer target —
        /// those are the layer's own state, carried in <see cref="Layer"/>.
        /// </summary>
        public long ContextGeneration { get; init; }
    }
}

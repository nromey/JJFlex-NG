namespace Radios.KeyOwnership
{
    /// <summary>
    /// Why a recognised command will not run. Distinct VALUES, never strings:
    /// each must be speakable differently, and "you are not connected" and
    /// "close this dialog first" are not the same sentence.
    /// </summary>
    public enum RefusalReason
    {
        /// <summary>The command needs a radio and none is connected.</summary>
        NoRadio,
        /// <summary>A modal dialog is up and the command would act behind it.</summary>
        ModalRestriction,
        /// <summary>The slice the command names, or the active slice it needs, does not exist. No other slice is substituted.</summary>
        MissingSlice,
        /// <summary>The target the command was aimed at has changed or gone since it was chosen.</summary>
        StaleTarget,
        /// <summary>The command is declared and bound but nothing is wired to perform it.</summary>
        NoExecutor,
        /// <summary>The gesture used to do something here and that thing has moved; the refusal says where.</summary>
        Relocated,
        /// <summary>The host could not establish who owns input just now. Nothing is changed and nothing is replayed.</summary>
        ContextInTransition,
    }

    /// <summary>Who receives a key the application hands on untouched.</summary>
    public enum KeyOwnerKind
    {
        /// <summary>Windows, or another application.</summary>
        OutsideApplication,
        /// <summary>The native menu's own navigation.</summary>
        NativeMenu,
        /// <summary>A text editor: typing, selection, clipboard, caret.</summary>
        TextEditor,
        /// <summary>A standard control's own navigation or activation.</summary>
        StandardControl,
        /// <summary>The window's default Enter, Escape and focus traversal.</summary>
        WindowDefault,
        /// <summary>A surface nobody classified, or foreign embedded content. Local input, never radio commands.</summary>
        LocalUnclassified,
        /// <summary>The lifetime owner of an operation this application started (a key-up ending push-to-talk).</summary>
        OperationOwner,
        /// <summary>A layer's help list, holding the arrows while it is up.</summary>
        LayerHelpList,
    }

    /// <summary>What kind of thing an action is aimed at.</summary>
    public enum ActionTargetKind
    {
        /// <summary>The application itself; no radio object.</summary>
        Application,
        /// <summary>The slice that was active WHEN THE DECISION WAS MADE, named by letter so it cannot drift.</summary>
        ActiveSlice,
        /// <summary>A slice named by the gesture itself (the universal jump).</summary>
        NamedSlice,
        /// <summary>The value a layer target holds.</summary>
        LayerValue,
    }

    /// <summary>
    /// The intended target of an action, pinned at decision time. An action
    /// queued behind a menu dismissal is re-checked against this, never
    /// re-aimed at whatever is current when it finally runs.
    /// </summary>
    public sealed record ActionTarget
    {
        public ActionTargetKind Kind { get; init; } = ActionTargetKind.Application;
        public char? SliceLetter { get; init; }
        public string? LayerId { get; init; }
        public string? LayerTargetId { get; init; }
        public string? RadioSessionId { get; init; }
        public string WindowId { get; init; } = "";
        public string? ModalBoundaryWindowId { get; init; }
        public long Generation { get; init; }
    }

    /// <summary>When an action may be carried out.</summary>
    public enum ActionDelivery
    {
        Immediate,
        /// <summary>
        /// The key was pressed inside a native menu. Consume the event, ask the
        /// menu to close, and run the action ONCE after dismissal is confirmed —
        /// after <see cref="KeyArbiter.Revalidate"/> agrees. If dismissal fails,
        /// report that and run nothing.
        /// </summary>
        AfterMenuDismissal,
    }

    /// <summary>What an action does to the layer state, so the coordinator need not infer it from a command name.</summary>
    public enum LayerEffect
    {
        None,
        /// <summary>Arms the JJ key.</summary>
        ArmJJKey,
        /// <summary>Opens the layer named in <see cref="ExecuteAction.OpensLayerId"/>.</summary>
        OpenLayer,
        /// <summary>Selects the layer target named in the action's target.</summary>
        SelectTarget,
        /// <summary>The one-shot JJ key is spent by this action.</summary>
        Disarm,
    }

    /// <summary>What must happen to an explicit layer as a side condition of this decision.</summary>
    public enum LayerDisposition
    {
        Keep,
        /// <summary>
        /// Drop every keyboard layer WITHOUT writing its entry values back to
        /// the radio. A safety stop, or a layer found stranded in a window it
        /// was not opened from.
        /// </summary>
        DropWithoutRestore,
    }

    /// <summary>Which sort of place an unbound key was pressed in, so the answer can name it.</summary>
    public enum UnboundContextKind
    {
        CommandSurface,
        Layer,
        /// <summary>The active window has no focused control; the answer offers that window's recovery route.</summary>
        NoFocusedControl,
    }

    /// <summary>
    /// The arbiter's answer. Exactly one of five shapes, and nothing else:
    /// <see cref="ExecuteAction"/>, <see cref="HandToOwner"/>,
    /// <see cref="Refuse"/>, <see cref="Unbound"/>, <see cref="NoCommandEvent"/>.
    /// A decision performs nothing; a host adapter delivers it.
    /// </summary>
    public abstract record KeyDecision
    {
        /// <summary>Which step of the normative order (1 to 8) produced this. Diagnostic, and pinned by tests.</summary>
        public int DecidedAtStep { get; init; }

        public LayerDisposition Layers { get; init; } = LayerDisposition.Keep;

        /// <summary>A native-menu report was present but could not be owning input, and was set aside.</summary>
        public bool StaleMenuReportIgnored { get; init; }
    }

    /// <summary>Execute ONE identified action.</summary>
    public sealed record ExecuteAction : KeyDecision
    {
        public string CommandId { get; init; } = "";
        /// <summary>The registry command, where the action is one. Its numeric value is unchanged.</summary>
        public CommandValues? RegistryCommand { get; init; }
        public ActionTarget Target { get; init; } = new ActionTarget();
        /// <summary>Whether OS auto-repeat re-runs the action.</summary>
        public bool Repeats { get; init; }
        /// <summary>-1, 0 or +1 for a value or tuning action.</summary>
        public int Direction { get; init; }
        /// <summary>For a tuning action, the signed step in hertz from the snapshot's tuning state.</summary>
        public long? SignedStepHz { get; init; }
        /// <summary>A fine (small-step) adjustment.</summary>
        public bool Fine { get; init; }
        public ActionDelivery Delivery { get; init; } = ActionDelivery.Immediate;
        public LayerEffect LayerEffect { get; init; } = LayerEffect.None;
        public string? OpensLayerId { get; init; }
        /// <summary>The key reached this action after a layer confirmed and passed it through; it stays in the origin context.</summary>
        public bool ContinuedBelowLayer { get; init; }
    }

    /// <summary>Hand the key, exactly once, to ONE identified native or control owner. Not "unhandled".</summary>
    public sealed record HandToOwner : KeyDecision
    {
        public KeyOwnerKind Owner { get; init; }
        /// <summary>The operation, window or surface context that owns it, where one is known.</summary>
        public string? OwnerId { get; init; }
        /// <summary>
        /// A modifier chord the engine cannot see a claim for. If the native
        /// route also leaves it unhandled, the adapter reports it as unbound —
        /// the engine cannot know a dialog's mnemonics, so it does not guess.
        /// </summary>
        public bool ReportIfUnhandled { get; init; }
        public bool ContinuedBelowLayer { get; init; }
    }

    /// <summary>Where a retired gesture's action went.</summary>
    public sealed record RelocationNotice
    {
        /// <summary>Stable identity of the notice; the sentence itself lives in the lexicon, not here.</summary>
        public string Id { get; init; } = "";
        public string? LayerId { get; init; }
        public string? LayerTargetId { get; init; }
    }

    /// <summary>A recognised command that will not run, and why. It is consumed; nothing lower gets the key.</summary>
    public sealed record Refuse : KeyDecision
    {
        public RefusalReason Reason { get; init; }
        public string CommandId { get; init; } = "";
        public ActionTarget? Target { get; init; }
        public RelocationNotice? Relocation { get; init; }
    }

    /// <summary>A command key with no meaning here. Gets contextual feedback; never a lower-priority action.</summary>
    public sealed record Unbound : KeyDecision
    {
        public UnboundContextKind ContextKind { get; init; }
        /// <summary>The nearest context, layer or window the answer should name.</summary>
        public string ContextId { get; init; } = "";
        /// <summary>The key is one of the layer's arrows, but the selected target does not move on that axis.</summary>
        public bool WrongAxis { get; init; }
    }

    /// <summary>Not a command at all: a bare modifier, an unowned key-up. No speech, no "unbound".</summary>
    public sealed record NoCommandEvent : KeyDecision
    {
        /// <summary>Swallow it (an auto-repeat of a command that does not repeat) rather than let it through.</summary>
        public bool Consume { get; init; }
    }
}

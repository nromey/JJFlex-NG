using JJFlexWpf.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Invariant 7. The tree walk can tell you whether a dialog's content is real;
/// it cannot tell you whether anything ever opens the dialog. A hook that is
/// declared, invoked from a menu handler and never assigned produces a command
/// that does nothing and a window that is never created - nothing to walk, and
/// the only evidence is at the callsites.
/// </summary>
/// <remarks>
/// <para>
/// <b>Task #591, and the shape it settled on.</b> The first version of this test
/// had one assertion and one hundred findings nobody had seen, because a
/// project <c>DeskGuard</c> refuses on a live desk is a project nobody runs.
/// When it was finally read, its own proof of over-reporting turned out to be a
/// live defect (#700) and its own named instance (#483's radio pane) turned out
/// to be invisible to it. Both are fixed in <see cref="DelegateSurfaceScan"/>;
/// what this file adds is the thing that lets the test be GREEN without being a
/// lowered bar.
/// </para>
/// <para>
/// <b>Four buckets, three of them handled here, one computed.</b> A dead hook on
/// a surface an operator can open is a defect and fails the build unless it is
/// on <see cref="AwaitingDecision"/> with the register number that owns it, or
/// on <see cref="DeliberatelyUnassigned"/> with the documented intention. A dead
/// hook on a type nothing constructs is the #482 class - the dialog has no door,
/// so its hooks are moot until somebody rules on the dialog - and the scan
/// computes that set itself; <see cref="UnconstructedAwaitingDecision"/> is the
/// acknowledgement list it is checked against in BOTH directions, so a dialog
/// that later gains a door has its hooks judged on the day. Name collisions are
/// no longer a bucket: the scan attributes evidence to a receiver, so a call to
/// somebody else's <c>SetName</c> is simply not evidence.
/// </para>
/// <para>
/// <b>Why every list is checked both ways.</b> The pattern is
/// <c>Radios.Tests.EarconCallerCoverageTests</c>: an entry that no longer
/// corresponds to a real finding must fail too, so the list cannot rot into a
/// permanent amnesty, and the next reader never learns that a live hook is
/// "awaiting decision". A table nobody verifies is a description-drift defect
/// within two sprints.
/// </para>
/// <para>
/// <b>Why the lists are rules about kinds of hook rather than a roster of
/// names.</b> Another track is adding delegate-shaped hooks while this one is
/// written, and the scan reads whatever is in the tree. A hook this file has
/// never seen lands in exactly one place: assigned, and silent; dead on a
/// constructed type, and red, which is this test doing its job; or dead on an
/// unconstructed type, and red with the instruction to add the TYPE to the
/// register. None of those is a false alarm, and none depends on this file
/// having heard of the hook.
/// </para>
/// </remarks>
public sealed class DelegateSurfaceTests
{
    private readonly ITestOutputHelper _output;

    public DelegateSurfaceTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------
    // The exception lists. Keys are Owner.Name. Every entry carries its reason,
    // because a bare name on an exclusion list is indistinguishable from an
    // oversight six months later.
    // ------------------------------------------------------------------

    /// <summary>
    /// Hooks that are unassigned BY DOCUMENTED DESIGN. "I could not find a
    /// caller" does not qualify; the declaring code must say the null case is
    /// intended. Expect this list to stay very short.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyUnassigned = new(StringComparer.Ordinal)
    {
        ["ValueLayerCues.Rail"] =
            "Its own doc comment: 'Optional; a layer whose rails are audible by number alone leaves " +
            "it null.' (Radios/ValueSubLayer.cs). No layer in the tree sets it today.",

        ["ValueSubLayerDefinition.DescribeEntry"] =
            "Single-value layers' entry sentence. The layer tests it for null and falls through: " +
            "DescribeLayerEntry first, then this, else nothing is said on entry " +
            "(ValueSubLayer.Enter). Optional by construction. OBSERVATION for the key-layer owner: " +
            "no single-value layer in the tree sets either describer, so entry to such a layer " +
            "is announced only by its earcon.",

        ["ValueSubLayerDefinition.DescribeHelp"] =
            "Same shape as DescribeEntry: DescribeLayerHelp first, then this, else silence " +
            "(ValueSubLayer.SpeakHelp). Optional by construction; nothing sets it.",

        ["ValueSubLayerDefinition.DescribeRestored"] =
            "Its own doc comment: 'Null falls back to DescribeClosed.' The cancel path reads " +
            "DescribeLayerRestored, then this, then DescribeClosed, which is required.",

        ["RadioComboBox.BoxIndexFunction"] =
            "The index-based alternative to the value-based pair. RadioComboBox.UpdateDisplay " +
            "uses BoxIndexFunction when set and otherwise selects by item equality; MainWindow " +
            "wires ModeControl and TXTuneControl with UpdateDisplayFunction/UpdateRigFunction " +
            "(the value pair), so the index pair is the road not taken, by the control's own " +
            "either/or.",

        ["RadioComboBox.UpdateRigByIndexFunction"] =
            "The other half of the index pair: OnSelectionChanged prefers it when set and " +
            "otherwise calls UpdateRigFunction, which is what MainWindow wires.",

        ["DirectPassthroughSink.SinkWriter"] =
            "Its own doc comment: 'Downstream sample writer. Null = drop. Phase 2 wires this to " +
            "the existing PortAudio path.' Hole-punch session plumbing in SmartLinkServices; " +
            "the null case is designed and traced. FLAG: 'Phase 2' names pending work, so " +
            "this entry is a documented null today and a reminder, not a verdict that the " +
            "work is done.",
    };

    /// <summary>
    /// Dead hooks on surfaces an operator CAN reach, where the fix needs a
    /// ruling this test cannot make. An entry is a defect with a register
    /// number, not an excuse; it comes off the day the hook is wired, and the
    /// reverse check below makes sure it does.
    /// </summary>
    private static readonly Dictionary<string, string> AwaitingDecision = new(StringComparer.Ordinal)
    {
        ["FreqOutHandlers.SaveStepSizes"] =
            "#346. Declared, invoked from ApplyStepSizes and MainWindow.ApplySettingsChanges, " +
            "assigned by nothing, and PersonalData.CoarseTuneStep/FineTuneStep are read and " +
            "written by nothing - so a step size the operator picks lives for the session and " +
            "is gone at exit. Left unwired ON PURPOSE pending Noel's ruling on the save " +
            "policy (a ladder key fires many times a second under repeat, and " +
            "PersonalData.Write is a synchronous whole-record save) and on whether a step " +
            "changed mid-QSO should become the permanent default. Operator consequence is " +
            "real; the decision is not this test's to make.",

        ["MessageDialog.CheckSuppressed"] =
            "#700's neighbour. The 'don't show again' feature was built and never adopted: no " +
            "caller sets PersistenceTag and no caller sets ShowDontShowAgain, so the box is " +
            "never shown and the hook's guard (PersistenceTag != null) is never true. Nothing " +
            "promises the feature to an operator. Decide: adopt it (needs a suppressed-messages " +
            "store under RadioConfig.AppDataRoot and a first caller) or delete the feature.",

        ["MessageDialog.SaveSuppressed"] =
            "The other half of CheckSuppressed, same status, same decision.",
    };

    /// <summary>
    /// Types that declare dead hooks and that NOTHING in product code
    /// constructs - the #482 class. Each entry says what the dialog would do
    /// if it were wired, so the person deciding its fate has the fact in front
    /// of them. The set is COMPUTED by the scan; this list is the
    /// acknowledgement, checked in both directions: a type here that gains a
    /// construction site must come off (and its hooks are then judged as
    /// reachable), and a new unconstructed type with dead hooks must be added.
    /// </summary>
    private static readonly Dictionary<string, string> UnconstructedAwaitingDecision = new(StringComparer.Ordinal)
    {
        ["ATUMemoriesDialog"] =
            "ATU Memories: switch the tuner's memories on or off and clear them. Reached, if " +
            "wired, through FlexBase.ShowATUMemoriesDialog from the ATU Memories command and " +
            "menu item (#700 neighbour, #110).",
        ["CMDLineDialog"] =
            "Command Line: type a raw command to the radio and see the reply. #482: dead with " +
            "no menu item at all - silent rather than honest.",
        ["ClusterDialog"] =
            "DX Cluster: connect to a telnet cluster, list spots, send commands, tune to a " +
            "selected spot, and optionally beep or track. The WinForms cluster client was the " +
            "original.",
        ["DefineCommandsDialog"] =
            "Define Keys: edit key assignments and CW message text. Superseded in practice by " +
            "KeysDialog and the Key Explorer, which are live.",
        ["ExportDialog"] =
            "Export Log: write the log to a file in a chosen format. #482: an honest dead end - " +
            "both halves dead and AddNotImplemented on both menu bars.",
        ["FiltersDspControl"] =
            "The Sprint 8 filter and DSP panel: ESC, TNF, RX and TX equaliser buttons, filter " +
            "widen, narrow and shift. #482 traces its orphaning to the deletion of the WinForms " +
            "filter forms; its hooks are the only operator route to TNF.",
        ["FindLogEntryDialog"] =
            "Find Log Entries: search the log by field and jump to a result.",
        ["ImportDialog"] =
            "Import Log: read entries from a file. #482: honest dead end, AddNotImplemented on " +
            "both menu bars.",
        ["LOTWMergeDialog"] =
            "LOTW Merge: validate a Logbook of The World file and merge confirmations into the " +
            "log. #482: honest dead end, AddNotImplemented on both menu bars.",
        ["ListerDialog"] =
            "Selection: a generic list editor - add, update, delete, choose. A building block; " +
            "nothing hosts it.",
        ["LogCharacteristicsDialog"] =
            "Log Characteristics: configure a log file (read its header, set its options).",
        ["LogEntryDialog"] =
            "Log Entry Form: the WPF replacement for the full LogEntry.vb form - new entry, " +
            "seek first and last, next and previous record, field navigation, session start and " +
            "end. The live logging surface is LogEntryControl in the logging layout, so this " +
            "is a superseded replacement; its GetFieldText, NewEntry and SetFieldText were the " +
            "three pure name collisions in the old scan.",
        ["LogTemplateDialog"] =
            "Log Entry template: define which fields a log entry carries, validate and save.",
        ["ManageGroupsDialog"] =
            "Manage Memory Groups: add, rename and delete memory groups.",
        ["MemoryGroupDialog"] =
            "Memory Groups: read-only browser of groups and their members.",
        ["MemoryScanDialog"] =
            "Memory Scan: choose memory groups and start a scan through them.",
        ["MenusDialog"] =
            "Rig menus, inherited from the Icom and Kenwood support removed in Phase 0. " +
            "RETIRED by design (#591: KeyCommands marks ShowMenus Retired and globals.vb " +
            "speaks 'menus not available'). Vestigial, not the #109 defect it resembles.",
        ["PersonalInfoCallbacks"] =
            "The callbacks record for PersonalInfoDialog: callsign duplicate check, callbook " +
            "credential and hostname validation, QRZ logbook key validation, open log " +
            "settings. PersonalInfoDialog itself has no construction site, so the record has " +
            "none either; the VB PersonalInfo form is what the operator reaches.",
        ["RadioInfoBox"] =
            "A read-only field box. Hosted only by FiltersDspControl.xaml, which nothing " +
            "constructs, so it has no door by transitivity.",
        ["RadioNumberBox"] =
            "A numeric field box with rig read and write hooks. Hosted only by " +
            "FiltersDspControl.xaml; no door by transitivity.",
        ["ReverseBeaconDialog"] =
            "Reverse Beacon: look a callsign up on the Reverse Beacon Network and list where it " +
            "was heard.",
        ["ScanDialog"] =
            "Scan Configuration: define, save, replace and remove linear scans and start one.",
    };

    // ------------------------------------------------------------------

    /// <summary>
    /// Positive controls FIRST, and for the right claim. A scan that resolves
    /// no receivers reports every hook as dead in the same words as a scan
    /// that found every defect, and a scan that resolves every receiver to
    /// everything reports a clean tree. Each control pins one attribution
    /// shape this codebase actually uses, on a hook known to be wired that way.
    /// </summary>
    [Fact]
    public void The_scan_sees_each_shape_of_wiring_it_claims_to_see()
    {
        var surfaces = DelegateSurfaceScan.Surfaces;
        _output.WriteLine($"{surfaces.Count} delegate-shaped hooks scanned across {DelegateSurfaceScan.CorpusFileCount} product files.");

        Assert.True(surfaces.Count >= 200,
            $"Only {surfaces.Count} hooks were parsed; there are hundreds. The declaration pattern has stopped matching and nothing below proves anything.");

        // Receiver declared in ANOTHER file and resolved through the global
        // symbol table: `Friend WithEvents RigControl As FlexBase` in globals.vb,
        // assigned as `RigControl.ShowTXControlsDialog = ...` in MainWindow and
        // invoked as `RigControl.ShowTXControlsDialog?.Invoke()` in globals.vb.
        // This is the #109 hook, wired in Sprint 33; if the scan cannot see
        // that wiring, #109 would read as still open.
        var tx = Find(surfaces, "FlexBase.ShowTXControlsDialog");
        Assert.True(tx.Assigned, "FlexBase.ShowTXControlsDialog is assigned in MainWindow.OnRadioStarted through RigControl, and the scan did not see it: " + Describe(tx));
        Assert.True(tx.Invoked, "FlexBase.ShowTXControlsDialog is invoked from globals.vb through RigControl, and the scan did not see it: " + Describe(tx));

        // Receiver is a local: `var dialog = new Dialogs.MemoriesDialog();`
        // then `dialog.GetSortedMemories = ...` in the same method.
        var sorted = Find(surfaces, "MemoriesDialog.GetSortedMemories");
        Assert.True(sorted.Assigned, "MemoriesDialog.GetSortedMemories is assigned on a local in MainWindow.ShowMemoriesDialog: " + Describe(sorted));
        Assert.True(sorted.Invoked, "MemoriesDialog.GetSortedMemories is invoked by its own dialog: " + Describe(sorted));

        // C# object initialiser: `new Dialogs.EscDialog { GetEscEnabled = () => ... }`.
        var esc = Find(surfaces, "EscDialog.GetEscEnabled");
        Assert.True(esc.Assigned, "EscDialog.GetEscEnabled is assigned in an object initialiser in NativeMenuBar.ShowEscDialog: " + Describe(esc));

        // C# object initialiser of a type declared in ANOTHER project:
        // `Cues = new Radios.ValueLayerCues { Entered = ... }` in KeyCommands.cs.
        // This is the layer-table shape another track is adding more of.
        var entered = Find(surfaces, "ValueLayerCues.Entered");
        Assert.True(entered.Assigned, "ValueLayerCues.Entered is assigned from a layer table initialiser in KeyCommands.cs: " + Describe(entered));
        Assert.True(entered.Invoked, "ValueLayerCues.Entered is invoked by ValueSubLayer: " + Describe(entered));

        // VB `With` initialiser: `New JJFlexWpf.KeyCommandContext With { .GetRigControl = Function() RigControl, ...`.
        var ctx = Find(surfaces, "KeyCommandContext.GetRigControl");
        Assert.True(ctx.Assigned, "KeyCommandContext.GetRigControl is assigned in a VB With-initialiser in globals.vb: " + Describe(ctx));

        // XAML-constructed owner: RadioPaneControl is an element in MainWindow.xaml.
        var pane = Find(surfaces, "RadioPaneControl.GetRXFrequency");
        Assert.True(pane.OwnerConstructed, "RadioPaneControl is constructed by MainWindow.xaml and the scan says nothing constructs it: " + Describe(pane));

        // And the other direction: a dialog nothing constructs must read that way.
        var lister = Find(surfaces, "ListerDialog.GetItems");
        Assert.False(lister.OwnerConstructed, "ListerDialog has no construction site in product code (#482) and the scan says it has one: " + Describe(lister));

        // Teardown is not wiring. RadioPaneControl.Cleanup() sets every hook to
        // null; that must not read as an assignment.
        Assert.DoesNotContain(pane.AssignedFrom, s => s.Contains("RadioPaneControl.xaml.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// The one that fails the build. A hook on a reachable surface that is
    /// called and never assigned, with no register entry owning it.
    /// </summary>
    [Fact]
    public void No_reachable_surface_is_called_through_a_delegate_nothing_ever_assigns()
    {
        var dead = DelegateSurfaceScan.Surfaces.Where(s => s.IsReachableDeadSurface).ToList();
        _output.WriteLine($"{dead.Count} dead hook(s) on reachable surfaces, {dead.Count(d => IsListed(d))} of them on a register list.");

        var unexplained = dead.Where(d => !IsListed(d)).ToList();
        if (unexplained.Count == 0) return;

        var message = new System.Text.StringBuilder();
        message.AppendLine($"{unexplained.Count} hook(s) are called from real code on a surface an operator can open, and nothing ever assigns them:");
        foreach (var surface in unexplained)
        {
            message.AppendLine($"  - {surface.Key} declared at {surface.DeclaredIn}");
            foreach (var callsite in surface.InvokedFrom)
                message.AppendLine($"      called from {callsite}");
            foreach (var mention in surface.NameOnlyMentions)
                message.AppendLine($"      (not counted: {mention})");
        }
        message.AppendLine("A control that looks interactive and does nothing. A sighted operator gets a weak cue; a blind operator gets silence, which is indistinguishable from a command that worked quietly (#109, #700). Wire it where the owner is constructed, or add it to AwaitingDecision with the register number that owns the decision.");
        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// The #482 class, computed and acknowledged. Both directions.
    /// </summary>
    [Fact]
    public void Every_unconstructed_type_with_dead_hooks_is_on_the_register_and_nothing_on_it_has_gained_a_door()
    {
        var surfaces = DelegateSurfaceScan.Surfaces;
        var orphaned = surfaces.Where(s => s.IsOrphanedDeadSurface)
                               .GroupBy(s => s.Owner, StringComparer.Ordinal)
                               .OrderBy(g => g.Key, StringComparer.Ordinal)
                               .ToList();

        _output.WriteLine($"{orphaned.Count} type(s) declare dead hooks and are constructed by nothing in product code:");
        foreach (var g in orphaned)
            _output.WriteLine($"  - {g.Key}: {string.Join(", ", g.Select(s => s.Name))}");

        var missing = orphaned.Select(g => g.Key).Where(t => !UnconstructedAwaitingDecision.ContainsKey(t)).ToList();
        var stale = UnconstructedAwaitingDecision.Keys.Where(t => !orphaned.Any(g => g.Key == t)).ToList();

        Assert.True(missing.Count == 0,
            "These types declare hooks that are called and never assigned, and NOTHING constructs the type, so no operator can reach the surface at all:\r\n  "
            + string.Join("\r\n  ", missing)
            + "\r\nThat is the #482 class, not the #109 class: the decision is about the dialog, not the hook. Add the type to UnconstructedAwaitingDecision with what it would do if wired, so the person who rules on it has the fact in front of them.");

        Assert.True(stale.Count == 0,
            "These types are listed as unconstructed and something now constructs them, or they no longer have dead hooks:\r\n  "
            + string.Join("\r\n  ", stale)
            + "\r\nTake them off the list. A dialog that gained a door has its hooks judged as reachable from today, which is the point of checking this both ways.");
    }

    /// <summary>
    /// The lists, checked the other way. An entry that is no longer a dead
    /// hook on a reachable surface has either been wired (good: take it off)
    /// or renamed (the entry explains nothing: fix it).
    /// </summary>
    [Fact]
    public void Nothing_on_the_exception_lists_has_quietly_been_wired_or_renamed()
    {
        var byKey = DelegateSurfaceScan.Surfaces.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var stale = new List<string>();
        var missing = new List<string>();

        foreach (var list in new[] { DeliberatelyUnassigned, AwaitingDecision })
        {
            foreach (var entry in list)
            {
                if (!byKey.TryGetValue(entry.Key, out var surface)) { missing.Add(entry.Key); continue; }
                if (!surface.IsReachableDeadSurface)
                    stale.Add(entry.Key + "  (" + (surface.Assigned ? "now assigned from " + string.Join(", ", surface.AssignedFrom) : !surface.Invoked ? "no longer invoked" : "owner no longer constructed") + ")");
            }
        }

        Assert.True(stale.Count == 0,
            "These hooks are listed as dead and the scan no longer finds them dead on a reachable surface:\r\n  " + string.Join("\r\n  ", stale)
            + "\r\nTake them off the list. Leaving them teaches the next reader that a live hook is awaiting a decision, which is the description drift this file exists to catch.");

        Assert.True(missing.Count == 0,
            "These keys are on the exception lists and no hook of that name is declared on that type any more:\r\n  " + string.Join("\r\n  ", missing)
            + "\r\nThe hook was renamed or deleted; update the list to match.");
    }

    /// <summary>
    /// The weaker cousin, reported but not failed: declared and never used at
    /// all. Often a hook that is waiting for its consumer rather than a defect,
    /// which is exactly the distinction a grep cannot make. Since #591 this also
    /// holds the former name collisions - a hook whose every "call" was to
    /// somebody else's member of the same name.
    /// </summary>
    [Fact]
    public void Unused_hooks_are_listed_for_triage()
    {
        var idle = DelegateSurfaceScan.Surfaces.Where(s => !s.Invoked && !s.Assigned).ToList();
        _output.WriteLine($"{idle.Count} hook(s) declared but neither assigned nor called with a resolvable receiver:");
        foreach (var surface in idle)
        {
            _output.WriteLine($"  - {surface.Key} at {surface.DeclaredIn}");
            foreach (var mention in surface.NameOnlyMentions)
                _output.WriteLine($"      (not counted: {mention})");
        }
    }

    /// <summary>The whole inventory, one line per hook, for triage and for the
    /// report. Never fails.</summary>
    [Fact]
    public void Full_inventory_for_triage()
    {
        foreach (var s in DelegateSurfaceScan.Surfaces)
        {
            var state = s.IsReachableDeadSurface ? "DEAD-REACHABLE"
                      : s.IsOrphanedDeadSurface ? "DEAD-ORPHANED"
                      : s.Assigned && s.Invoked ? "live"
                      : s.Assigned ? "assigned-not-invoked"
                      : "idle";
            _output.WriteLine($"{state}\t{s.Key}\t{s.DeclaredIn}\tassigned:[{string.Join("; ", s.AssignedFrom)}]\tinvoked:[{string.Join("; ", s.InvokedFrom)}]\tnot-counted:[{string.Join("; ", s.NameOnlyMentions)}]");
        }
    }

    // ------------------------------------------------------------------

    private static bool IsListed(DelegateSurface s)
        => DeliberatelyUnassigned.ContainsKey(s.Key) || AwaitingDecision.ContainsKey(s.Key);

    private static DelegateSurface Find(IReadOnlyList<DelegateSurface> surfaces, string key)
    {
        var s = surfaces.FirstOrDefault(x => x.Key == key);
        Assert.True(s != null, $"No hook {key} was parsed. Either it was renamed or the declaration pattern no longer matches its shape; either way this control proves nothing until it is fixed.");
        return s!;
    }

    private static string Describe(DelegateSurface s)
        => $"assigned from [{string.Join("; ", s.AssignedFrom)}], invoked from [{string.Join("; ", s.InvokedFrom)}], not counted [{string.Join("; ", s.NameOnlyMentions)}]";
}

using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Radios.Facts;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// E-series on the REAL dialog: the rows and detail the operator reaches, the
/// review button's token, the empty-state wording actually rendered, and the
/// disconnected entry points.
/// </summary>
/// <remarks>
/// <para>
/// <b>NOT RUN BY SPRINT 45 TRACK M2.</b> These construct and realise the
/// production window, so they need a desk run Noel schedules, under the
/// existing guard (<see cref="DeskGuard"/>). They were written and compiled; no
/// result is claimed for them.
/// </para>
/// <para>
/// They read what the control EXPOSES — automation peers, the text box's
/// value, the list item names — never the view model, because the claim they
/// test is reachability. A populated model behind an empty automation tree is
/// the failure these exist to catch.
/// </para>
/// </remarks>
public sealed class UndeliveredDetailsSurfaceTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A store with one synthetic owner. The message key is a real, text-only lexicon key.</summary>
    private sealed class SurfaceStore
    {
        public SurfaceStore()
        {
            Registry = FactAuthorityRegistry.Create();
            var contract = new ConditionContract("surface.synthetic", 1, new[] { "note" }, new[] { "facts.window.title" },
                DeliveryPriority.Ordinary, new[] { new EvidenceField("temperature", FactValueKind.Decimal, required: false) });
            FactOwner owner = Registry.DeclareOwner("synthetic surface owner", contract);
            Session = Registry.OpenSession("SERIAL-SURFACE");
            Publisher = Registry.Register(owner, Session, contract, new ConditionKey("c")).Publisher!;
        }

        public FactAuthorityRegistry Registry { get; }
        public FactSession Session { get; }
        public SlotPublisher Publisher { get; }
        public FactStore Store => Registry.Store;

        public PublicationResult Open(decimal temperature)
        {
            CapturedFactEvent ev = Publisher.Capture(FactObservation.Of(("temperature", FactValue.Of(temperature))), T0).Event!;
            return Publisher.Open(ev, "note", "facts.window.title",
                new[] { new MaterialDeclaration("temperature", FactValue.Of(temperature)) });
        }

        public void Correct(PublicationResult opened, decimal temperature)
        {
            CapturedFactEvent ev = Publisher.Capture(FactObservation.Of(("temperature", FactValue.Of(temperature))), T0).Event!;
            Publisher.Update(opened.Handle!, ev,
                FactTransition.Correction(new[] { new MaterialDeclaration("temperature", FactValue.Of(temperature)) }),
                Store.Find(opened.Handle!.Id)!.Revision);
        }
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement =>
        (T)window.FindName(name) ?? throw new InvalidOperationException(name + " not found");

    /// <summary>The list item names the automation tree exposes.</summary>
    private static List<string> ExposedRowNames(Window window)
    {
        var list = Control<ListBox>(window, "Items");
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(list);
        return (peer.GetChildren() ?? new List<AutomationPeer>()).Select(p => p.GetName()).ToList();
    }

    /// <summary>Select a row the way a client does, through its selection pattern.</summary>
    private static void SelectExposedRow(Window window, Func<string, bool> match)
    {
        var list = Control<ListBox>(window, "Items");
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(list);
        AutomationPeer row = peer.GetChildren().First(p => match(p.GetName()));
        ((ISelectionItemProvider)row.GetPattern(PatternInterface.SelectionItem)).Select();
        UiThread.Drain();
    }

    /// <summary>The detail text as a client reads it, through the text box's value pattern.</summary>
    private static string ExposedDetail(Window window)
    {
        var box = Control<TextBox>(window, "DetailText");
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(box);
        return ((IValueProvider)peer.GetPattern(PatternInterface.Value)).Value;
    }

    private static void Press(Window window, string button)
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(Control<Button>(window, button));
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        UiThread.Drain();
    }

    [Fact]
    public void E1_CorruptHistoryAndPressureAreRealSelectableRows()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-surface-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "facts-broken.json"), "{ this is not json");
        var surface = new SurfaceStore();
        using (var journal = new FactJournal(surface.Store, dir))
        {
            journal.TakeLease();
            journal.LoadHistory();
        }

        var result = UiThread.RunWithTimeout(() =>
        {
            var dialog = new UndeliveredDetailsDialog(surface.Store);
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            List<string> rows = ExposedRowNames(dialog);
            SelectExposedRow(dialog, n => n.Length > 0);
            return (realized.LoadedFired, rows, detail: ExposedDetail(dialog));
        }, TimeSpan.FromSeconds(30));

        Assert.True(result.LoadedFired, "Loaded never fired; nothing here is evidence.");
        Assert.NotEmpty(result.rows);
        Assert.Contains("facts-broken.json", result.detail, StringComparison.Ordinal);

        // POSITIVE CONTROL: an ordinary fact through the same route.
        var ok = new SurfaceStore();
        ok.Open(70m);
        var okRows = UiThread.RunWithTimeout(() =>
        {
            var dialog = new UndeliveredDetailsDialog(ok.Store);
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            return ExposedRowNames(dialog);
        }, TimeSpan.FromSeconds(30));
        Assert.Single(okRows);
    }

    [Fact]
    public void E2_ReviewButtonUsesTheInstalledDetail()
    {
        var surface = new SurfaceStore();
        PublicationResult opened = surface.Open(70m);

        var outcome = UiThread.RunWithTimeout(() =>
        {
            var dialog = new UndeliveredDetailsDialog(surface.Store);
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            var box = Control<TextBox>(dialog, "DetailText");
            box.Focus();
            box.CaretIndex = 5;
            string before = ExposedDetail(dialog);

            // Revision 2 arrives while revision 1 is being read.
            surface.Correct(opened, 72m);
            UiThread.Drain();
            string during = ExposedDetail(dialog);
            int caret = box.CaretIndex;
            bool refreshOffered = Control<Button>(dialog, "RefreshButton").IsEnabled;

            Press(dialog, "ReviewedButton");
            FactSnapshot afterOld = surface.Store.Find(opened.Handle!.Id)!;

            // Explicit refresh, then review the new detail.
            Press(dialog, "RefreshButton");
            string refreshed = ExposedDetail(dialog);
            Press(dialog, "ReviewedButton");
            FactSnapshot afterNew = surface.Store.Find(opened.Handle!.Id)!;

            return (before, during, caret, refreshOffered, oldPending: afterOld.IsPending, refreshed, newPending: afterNew.IsPending);
        }, TimeSpan.FromSeconds(30));

        Assert.Equal(outcome.before, outcome.during);          // the text under the reader did not move
        Assert.Equal(5, outcome.caret);
        Assert.True(outcome.refreshOffered);
        Assert.True(outcome.oldPending);                       // revision 2 stays owed
        Assert.Contains("72", outcome.refreshed, StringComparison.Ordinal);
        Assert.False(outcome.newPending);                      // positive control
    }

    [Fact]
    public void E3_DisconnectedDispatchReachesTheListThroughCommandFinderAndStatus()
    {
        // The real Command Finder entry, with no rig at all: the handler opens
        // the modal window, and a queued inspection reads its automation tree
        // and closes it from inside the nested loop.
        var seen = UiThread.RunWithTimeout(() =>
        {
            var commands = new KeyCommands(new KeyCommandContext());
            KeyTableEntry entry = commands.KeyTable.Single(e => e.KeyDef.Id == CommandValues.ShowUndeliveredDetails);
            Assert.Equal(KeyScope.Global, entry.Scope);

            string? detail = null;
            List<string>? rows = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                UndeliveredDetailsDialog? open = OpenWindows().OfType<UndeliveredDetailsDialog>().FirstOrDefault();
                if (open == null) return;
                rows = ExposedRowNames(open);
                detail = ExposedDetail(open);
                open.Close();
            }));
            entry.Handler!();
            return (rows, detail);
        }, TimeSpan.FromSeconds(30));

        Assert.NotNull(seen.rows);
        Assert.False(string.IsNullOrWhiteSpace(seen.detail));

        // And through Status, whose readout returns early with no radio.
        var status = UiThread.RunWithTimeout(() =>
        {
            var dialog = new StatusDialog();
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            string summary = Control<TextBlock>(dialog, "PendingSummary").Text;
            bool opened = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                UndeliveredDetailsDialog? open = OpenWindows().OfType<UndeliveredDetailsDialog>().FirstOrDefault();
                opened = open != null;
                open?.Close();
            }));
            Press(dialog, "UndeliveredButton");
            return (summary, opened);
        }, TimeSpan.FromSeconds(30));

        Assert.False(string.IsNullOrWhiteSpace(status.summary));
        Assert.True(status.opened);
    }

    [Fact]
    public void E4_EmptyStateRendersTheRoleThePredicatesChose()
    {
        // No journal: the window must render the UNVERIFIED role, not the
        // delivered sentence, because nothing can be said about saved history.
        var detached = new SurfaceStore();
        string rendered = UiThread.RunWithTimeout(() =>
        {
            var dialog = new UndeliveredDetailsDialog(detached.Store);
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            return ExposedDetail(dialog);
        }, TimeSpan.FromSeconds(30));

        FactListSnapshot projection = new FactListPresenter(detached.Store).OpenView().Snapshot(FactView.Pending);
        Assert.Equal(FactListView.EmptyStateText(projection), rendered);
        Assert.Contains("facts.window.nothing_pending_unverified", FactListPresenter.EmptyStateRoles(projection));
        Assert.NotEqual(Lexicon.Get("facts.window.nothing_pending"), rendered);
    }

    [Fact]
    public void E5_OpeningFocusAndBackgroundUpdatesNeverReviewAndCloseUnsubscribes()
    {
        var surface = new SurfaceStore();
        PublicationResult opened = surface.Open(70m);
        int before = HandlerCount(surface.Store);

        UiThread.RunWithTimeout(() =>
        {
            var dialog = new UndeliveredDetailsDialog(surface.Store);
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            Control<TextBox>(dialog, "DetailText").Focus();
            surface.Registry.Quiet.Observe("ctrl");
            surface.Correct(opened, 71m);
            UiThread.Drain();
            Press(dialog, "RefreshButton");
            Press(dialog, "ReadButton");
            return 0;
        }, TimeSpan.FromSeconds(30));

        Assert.Empty(surface.Store.Find(opened.Handle!.Id)!.Reviewed);
        Assert.Equal(before, HandlerCount(surface.Store));      // no leaked subscription
    }

    private static IEnumerable<Window> OpenWindows() =>
        PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
                          .Select(s => s.RootVisual).OfType<Window>();

    private static int HandlerCount(FactStore store)
    {
        FieldInfo? field = typeof(FactStore).GetField("ProjectionChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        return (field?.GetValue(store) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}

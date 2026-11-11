#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #398, repair one (Codex B2): the radio picker's Network identity card
/// follows the HIGHLIGHTED row when a sighting or the availability reconcile
/// changes that row in place, without the highlight moving. Before the repair
/// the card was refreshed only when the selection changed, and the list's
/// refresh deliberately keeps the same row objects and raises no
/// SelectionChanged, so a row selected before its SmartLink reply kept saying
/// SmartLink does not list it, and a later list with new ports left the old
/// ones on the card.
/// </summary>
/// <remarks>
/// <para><b>Desk safety.</b> Every test runs on a private STA thread, with the
/// dialog built as an uninitialized object (no window, no handle) and its list
/// and card as unhosted controls with no presentation source, asserted. The
/// dialog's selection handler is never wired, so the only way the card can
/// follow is the refresh under test.</para>
/// <para><b>What only the harness can confirm.</b> These drive the dialog's
/// real <c>OnRadioFound</c> and <c>RefreshRadiosList</c>, but a focus that
/// leaves the card is WPF's to deliver: the card test stands a reader inside
/// through the card's seam and calls the method the focus handler calls. That
/// the framework raises the handler when focus really leaves the text box is
/// for a UIA run.</para>
/// </remarks>
public sealed class PickerIdentityCardFollowsTests
{
    private const string Serial = "1234-5678-6300-0398";
    private const string Other = "1234-5678-8600-0398";
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// A roster row highlighted before its SmartLink reply; the reply arrives
    /// and updates the row in place; a later list changes its ports; then the
    /// radio leaves the list. The card says each in turn, with the highlight
    /// never moving. Before anything has asked SmartLink, and after the radio
    /// leaves the list while no session is live, it denies nothing (repair
    /// two); only once a live session's list has answered without the radio
    /// does it say SmartLink does not list it.
    /// </summary>
    [Fact]
    public void The_card_follows_the_highlighted_row_when_its_sighting_changes_in_place()
    {
        OnStaThread(() =>
        {
            Lexicon.Load(Lexicon.Partitions);
            var session = new CurrentSession();
            (bool lan, bool wan, FlexBase.RigData? sighting) availability = (false, false, null);
            var (dialog, list, card) = NewPicker(serial => availability);

            var row = new RadioListItem { Serial = Serial, Name = "Shack", ModelName = "FLEX-6300", RigData = null! };
            Rows(dialog).Add(row);
            list.Items.Add(row);
            list.SelectedItem = row;

            // Before any reply, with discovery still settling: the row says
            // "checking", and the card must not say SmartLink does not list
            // the radio (repair two, the integration pass's F1). Until then
            // this step asserted that denial.
            Invoke(dialog, "RefreshRadiosList", false);
            Assert.Equal(Lexicon.Get("connect.row.checking"), row.WhereText);
            Assert.EndsWith(Lexicon.Get("connect.identity.not_asked"), card.IdentityText.Text);
            Assert.DoesNotContain(Lexicon.Get("connect.identity.not_listed"), card.IdentityText.Text);

            // The SmartLink reply: the row is updated in place, the
            // highlight does not move.
            var first = Sighting(session, 21443, 21444);
            availability = (false, true, first);
            Invoke(dialog, "OnRadioFound", Found(first));
            Assert.Same(row, list.SelectedItem);
            Assert.Same(first, row.WanSighting);
            Assert.True(row.WanAvailable);
            Assert.Contains("external TCP 21443, external UDP 21444", card.IdentityText.Text);
            Assert.DoesNotContain(Lexicon.Get("connect.identity.not_listed"), card.IdentityText.Text);

            // A later list with new ports: same row, same text on the row, so
            // the list's refresh takes its unchanged branch.
            var second = Sighting(session, 31443, 31444);
            availability = (false, true, second);
            Invoke(dialog, "OnRadioFound", Found(second));
            Assert.Same(row, list.SelectedItem);
            Assert.Same(second, row.WanSighting);
            Assert.Contains("external TCP 31443, external UDP 31444", card.IdentityText.Text);
            Assert.DoesNotContain("21443", card.IdentityText.Text);

            // The radio leaves the list: the reconcile clears the row's
            // SmartLink flag and keeps its last sighting. Discovery has
            // settled, but no SmartLink session is live, so nothing has asked:
            // the card denies nothing and shows no old numbers (repair two).
            Put(dialog, "_localSettled", true);
            var coordinatorBefore = Radios.SmartLink.SmartLinkServices.Coordinator;
            try
            {
                Radios.SmartLink.SmartLinkServices.Override(
                    new Radios.SmartLink.SmartLinkSessionCoordinator(_ => session));
                availability = (false, false, null);
                Invoke(dialog, "RefreshRadiosList", false);
                Assert.False(row.WanAvailable);
                Assert.Same(second, row.WanSighting);
                Assert.False(row.AnySmartLinkSessionLive);
                Assert.EndsWith(Lexicon.Get("connect.identity.not_asked"), card.IdentityText.Text);
                Assert.DoesNotContain("31443", card.IdentityText.Text);

                // A session is live and its list answered without the radio:
                // now the card says it is not listed, not the old numbers.
                var asked = new Radios.SmartLink.SmartLinkSessionCoordinator(_ => session);
                asked.GetOrCreateSession(session.AccountId);
                Radios.SmartLink.SmartLinkServices.Override(asked);
                Invoke(dialog, "RefreshRadiosList", false);
                Assert.True(row.AnySmartLinkSessionLive);
                Assert.EndsWith(Lexicon.Get("connect.identity.not_listed"), card.IdentityText.Text);
                Assert.DoesNotContain("31443", card.IdentityText.Text);
            }
            finally
            {
                Radios.SmartLink.SmartLinkServices.Override(coordinatorBefore);
            }

            // A rebuild: another radio arrives, so the list is rebuilt, in the
            // same refresh that the reconcile puts the highlighted radio back
            // on SmartLink. The rebuild drops and restores the selection, and
            // the dialog's own selection handler (wired here exactly as its
            // constructor wires it) refreshes the card; WPF raises the event.
            int selectionChanges = 0;
            list.SelectionChanged += (_, _) => { selectionChanges++; Invoke(dialog, "RefreshIdentityCard"); };
            availability = (false, true, second);
            Invoke(dialog, "OnRadioFound", new RadioListItem
            {
                Serial = Other, Name = "Bench", ModelName = "FLEX-8600",
                RigData = null!, LanAvailable = true,
            });
            Assert.Equal(2, list.Items.Count);
            Assert.Same(row, list.SelectedItem);
            Assert.True(selectionChanges >= 2, "the rebuild did not drop and restore the selection, so the card was refreshed by nothing");
            Assert.True(row.WanAvailable);
            Assert.Contains("external TCP 31443, external UDP 31444", card.IdentityText.Text);
            Assert.DoesNotContain(Lexicon.Get("connect.identity.not_listed"), card.IdentityText.Text);
        });
    }

    /// <summary>
    /// The focused-reader guard holds a rewrite back while the reader is inside
    /// the card, and the held-back text is shown when they leave. Before the
    /// repair it was dropped, and the picker, which refreshes on events rather
    /// than a timer, never showed it.
    /// </summary>
    [Fact]
    public void A_rewrite_held_back_while_the_reader_is_inside_the_card_is_made_when_they_leave()
    {
        OnStaThread(() =>
        {
            var card = new NetworkIdentityCard();
            Assert.Null(PresentationSource.FromVisual(card));
            bool inside = false;
            card.ReaderInside = () => inside;

            card.ShowLines(new[] { "first" });
            Assert.Equal("first", card.IdentityText.Text);

            inside = true;
            card.ShowLines(new[] { "second" });
            Assert.Equal("first", card.IdentityText.Text);   // the reading position is not moved

            card.ReaderLeft();                                // still inside: nothing is owed yet to pay
            Assert.Equal("first", card.IdentityText.Text);

            inside = false;
            card.ReaderLeft();
            Assert.Equal("second", card.IdentityText.Text);

            // Nothing owed: leaving again rewrites nothing.
            card.IdentityText.Text = "untouched";
            card.ReaderLeft();
            Assert.Equal("untouched", card.IdentityText.Text);
        });
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static (RigSelectorDialog dialog, ListBox list, NetworkIdentityCard card) NewPicker(
        Func<string, (bool lan, bool wan, FlexBase.RigData? wanSighting)> availability)
    {
        var dialog = (RigSelectorDialog)RuntimeHelpers.GetUninitializedObject(typeof(RigSelectorDialog));
        var list = new ListBox();
        var card = new NetworkIdentityCard();
        Assert.Null(PresentationSource.FromVisual(list));
        Assert.Null(PresentationSource.FromVisual(card));

        // The dispatcher this thread already has (the controls above made it),
        // so the dialog's own Dispatcher.Invoke runs inline here.
        var dispatcherField = typeof(DispatcherObject).GetField("_dispatcher", Private);
        Assert.True(dispatcherField != null, "DispatcherObject._dispatcher is not where this test sets it.");
        dispatcherField!.SetValue(dialog, Dispatcher.CurrentDispatcher);

        Put(dialog, "RadiosBox", list);
        Put(dialog, "IdentityCard", card);
        Put(dialog, "PathCombo", new ComboBox());
        Put(dialog, "_radiosLock", new object());
        Put(dialog, "_radiosList", new List<RadioListItem>());
        Put(dialog, "_sightingsRecorded", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Serial, Other });
        Put(dialog, "_arrivalsAnnounced", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Put(dialog, "_autoSelectAnnounced", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Put(dialog, "_learnedPaths", new Dictionary<string, ConnectPathKind?>(StringComparer.OrdinalIgnoreCase) { [Serial] = null, [Other] = null });
        Put(dialog, "_pathAffordanceKey", "");
        Put(dialog, "_callbacks", new RigSelectorCallbacks
        {
            StartLocalDiscovery = () => { },
            StartRemoteDiscovery = _ => { },
            RegisterRadioFound = _ => { },
            UnregisterRadioFound = () => { },
            SaveAutoConnectSettings = (_, _, _, _, _) => { },
            SaveGlobalAutoConnect = _ => { },
            CheckOtherAutoConnect = _ => (false, ""),
            ScreenReaderSpeak = (_, _) => { },
            GetRadioAvailability = availability,
        });
        return (dialog, list, card);
    }

    /// <summary>A SmartLink sighting whose list is current, carrying the list's ports.</summary>
    private static FlexBase.RigData Sighting(CurrentSession session, int tcp, int udp)
    {
        var rd = (FlexBase.RigData)Activator.CreateInstance(typeof(FlexBase.RigData), nonPublic: true)!;
        rd.Name = "Shack";
        rd.ModelName = "FLEX-6300";
        rd.Serial = Serial;
        SetProperty(rd, nameof(FlexBase.RigData.Remote), true);
        SetProperty(rd, nameof(FlexBase.RigData.WanAvailable), true);
        SetProperty(rd, nameof(FlexBase.RigData.Advertised), new SmartLinkAdvertised(tcp, udp, true, false));
        var provenanceType = typeof(FlexBase).GetNestedType("WanListProvenance", BindingFlags.NonPublic);
        Assert.True(provenanceType != null, "FlexBase.WanListProvenance is not where this test builds it.");
        var provenance = Activator.CreateInstance(provenanceType!, session, session.SessionId, 1L, 1L)!;
        SetProperty(rd, "FromWanList", provenance);
        Assert.True(rd.StillCurrent(), "Control: the test's sighting does not read as current, so the picker would not take it as live.");
        return rd;
    }

    private static RadioListItem Found(FlexBase.RigData sighting) => new()
    {
        Serial = sighting.Serial,
        Name = sighting.Name,
        ModelName = sighting.ModelName,
        RigData = sighting,
        LanAvailable = sighting.LanAvailable,
        WanAvailable = sighting.WanAvailable,
    };

    private static List<RadioListItem> Rows(RigSelectorDialog dialog)
        => (List<RadioListItem>)typeof(RigSelectorDialog).GetField("_radiosList", Private)!.GetValue(dialog)!;

    private static void Invoke(RigSelectorDialog dialog, string method, params object[] args)
    {
        var m = typeof(RigSelectorDialog).GetMethod(method, Private);
        Assert.True(m != null, "RigSelectorDialog." + method + " is not where this test calls it.");
        m!.Invoke(dialog, args);
    }

    private static void Put(object target, string field, object value)
    {
        var f = target.GetType().GetField(field, Private);
        Assert.True(f != null, target.GetType().Name + "." + field + " is not where this test sets it.");
        f!.SetValue(target, value);
    }

    private static void SetProperty(object target, string property, object value)
    {
        var p = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(p != null, target.GetType().Name + "." + property + " is not where this test sets it.");
        p!.SetValue(target, value);
    }

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    /// <summary>
    /// A connected session whose list on connection 1 is current: only what
    /// <see cref="FlexBase.RigData.StillCurrent"/> asks is real; the rest refuses.
    /// </summary>
    private sealed class CurrentSession : Radios.SmartLink.IWanSessionOwner
    {
        public string SessionId => "s-picker-398";
        public string AccountId => "picker-398@example.com";
        public bool IsConnected => true;
        public Radios.SmartLink.SessionStatus Status => Radios.SmartLink.SessionStatus.Connected;
        public Exception? LastError => null;
        public int ReconnectAttemptCount => 0;
        public IReadOnlyList<Flex.Smoothlake.FlexLib.Radio> AvailableRadios
            => Array.Empty<Flex.Smoothlake.FlexLib.Radio>();
        public DateTime? LastRadioListUtc => DateTime.UtcNow;
        public Radios.SmartLink.SessionRadioListSnapshot RadioListSnapshot => new(
            Array.Empty<Flex.Smoothlake.FlexLib.Radio>(), DateTime.UtcNow,
            SessionConnected: true, ArrivedOnTheLiveConnection: true, ConnectionGeneration: 1, SessionId: SessionId)
        { ListRevision = 1 };
        public bool ListIsCurrent(long connectionGeneration) => connectionGeneration == 1;
#pragma warning disable CS0067 // events the test never raises
        public event EventHandler? ListCurrencyMayHaveChanged;
        public event EventHandler<Radios.SmartLink.SessionStatus>? StatusChanged;
        public event EventHandler<Radios.SmartLink.SignalThresholdEventArgs>? SignalThresholdCrossed;
        public event EventHandler<Radios.SmartLink.WanRadioListReceivedEventArgs>? RadioListReceived;
        public event EventHandler<Radios.SmartLink.NetworkDiagnosticReport>? NetworkReportReady;
#pragma warning restore CS0067
        public Radios.SmartLink.ISessionAudioSink AudioSink => throw new NotSupportedException();
        public void Connect() => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public void ReRegister(string programName, string platform, string jwt) => throw new NotSupportedException();
        public void EnableAutoRegistration(Func<bool, string?> jwtProvider, string programName, string platform = "Win10")
            => throw new NotSupportedException();
        public bool TryClaimRegistration() => throw new NotSupportedException();
        public System.Threading.Tasks.Task<string?> ConnectToRadio(string serial, int holePunchPort = 0,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Reset() => throw new NotSupportedException();
        public System.Threading.Tasks.Task<Radios.SmartLink.NetworkDiagnosticReport> RunNetworkDiagnosticAsync(string radioSerial,
            bool forceRefresh = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Radios.SmartLink.NetworkDiagnosticReport? GetLastNetworkReport(string radioSerial) => null;
        public Radios.SmartLink.NetworkDiagnosticReport? MostRecentNetworkReport => null;
        public void Dispose() { }
    }
}

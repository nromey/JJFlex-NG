using System.ComponentModel;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A radio list row tells its bindings when to re-read its text (#1008), so
/// the printed text and the accessible name, both bound to
/// <see cref="RadioListItem.DisplayText"/>, follow the row.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> A plain data object only, on an MTA thread (asserted
/// first), where WPF refuses to build any element. No dialog, no window.
/// </remarks>
public sealed class RadioListItemTextTests
{
    [Fact]
    public void A_row_raises_its_text_changed_and_the_text_is_the_rows_current_state()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        var row = new RadioListItem { Serial = "4925-1213-8600-6245", Name = "Bench", ModelName = "FLEX-8600" };
        var raised = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        string before = row.DisplayText;
        row.LanAvailable = true;
        row.NotifyDisplayTextChanged();

        Assert.Equal(new[] { nameof(RadioListItem.DisplayText) }, raised);
        Assert.Equal(row.ToString(), row.DisplayText);   // the accessible fallback reads the same text
        Assert.NotEqual(before, row.DisplayText);        // control: the change is one the text shows
    }

    // ---- #405 J10: the account a connect would use ----

    private const string Chosen = "chosen-elsewhere@example.com";   // not saved on this computer
    private const string LastSeen = "last-seen@example.com";         // saved here
    private const string Selector = "working-with@example.com";

    /// <summary>
    /// The rule: the connect's own answer when it names one, otherwise the
    /// choice, then the observation, as before.
    /// </summary>
    [Fact]
    public void The_bound_account_is_the_one_a_connect_would_use_when_the_plan_names_one()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(LastSeen, RadioListItem.AccountInPlay(LastSeen, Chosen, LastSeen));
        Assert.Equal(Chosen, RadioListItem.AccountInPlay("", Chosen, LastSeen));
        Assert.Equal(LastSeen, RadioListItem.AccountInPlay(null, "", LastSeen));
        Assert.Equal("", RadioListItem.AccountInPlay("", "", ""));
        Assert.True(RadioListItem.BoundElsewhere(LastSeen, Selector));
        Assert.False(RadioListItem.BoundElsewhere(LastSeen, LastSeen.ToUpperInvariant()));
        Assert.False(RadioListItem.BoundElsewhere("", Selector));
    }

    /// <summary>
    /// Ruled by Noel 2026-11-09 20:52 (J10): the chosen account is not saved
    /// here and the last-seen one is, so the row names the saved last-seen
    /// account, and Enter, which looks up <see cref="RadioListItem.BoundAccount"/>,
    /// switches to it. The control: with no account from the plan the row
    /// names the chosen one, as today.
    /// </summary>
    [Fact]
    public void A_row_whose_chosen_account_is_not_saved_names_the_saved_last_seen_one()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        var row = new RadioListItem
        {
            Serial = "0000-0000-6300-0001", Name = "Shack", ModelName = "FLEX-6300",
            PreferredAccount = Chosen, LastSeenViaAccount = LastSeen, ConnectAccount = LastSeen,
            DiscoverySettled = true,   // past "checking": a row nothing can see now
        };
        row.ForeignAccount = RadioListItem.BoundElsewhere(row.BoundAccount, Selector);
        Assert.True(row.ForeignAccount);
        Assert.Equal(LastSeen, row.BoundAccount);
        Assert.Contains(LastSeen, row.DisplayText);
        Assert.DoesNotContain(Chosen, row.DisplayText);

        row.ConnectAccount = "";                         // the plan names none: today's words
        row.NotifyDisplayTextChanged();
        Assert.Equal(Chosen, row.BoundAccount);
        Assert.Contains(Chosen, row.DisplayText);
    }

    /// <summary>
    /// The roster paint asks the connect's answer per radio BEFORE it takes
    /// the list lock (ConnectAccountFor reads disk and the held sessions), and
    /// derives the foreign flag from the same rule as BoundAccount; choosing
    /// an account asks it again.
    /// </summary>
    [Fact]
    public void The_paint_asks_the_connects_answer_outside_the_list_lock()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs"))
            .Replace("\r\n", "\n");
        int paint = src.IndexOf("private void PaintRoster(", System.StringComparison.Ordinal);
        int ask = src.IndexOf("connectBySerial[k.Serial] = Radios.FlexBase.RadioListAccountFor(k.Serial, out bool ruledOut);", paint, System.StringComparison.Ordinal);
        int theLock = src.IndexOf("lock (_radiosLock)", paint, System.StringComparison.Ordinal);
        Assert.True(paint > 0 && ask > paint && theLock > ask, "RadioListAccountFor must be asked before the list lock");
        Assert.Contains("RadioListItem.AccountInPlay(connect, k.PreferredAccount, k.LastSeenViaAccount), accountEmail);", src, System.StringComparison.Ordinal);
        Assert.Contains("radio.ConnectAccount = Radios.FlexBase.RadioListAccountFor(radio.Serial, out bool ruledOut);", src, System.StringComparison.Ordinal);
        // #405 repair two: the remembered-account state is stamped beside the
        // account, in the paint (new and existing rows) and at a changed choice.
        Assert.Contains("existing.EverySavedListRuledItOut = ruledOutEverywhere;", src, System.StringComparison.Ordinal);
        Assert.Contains("EverySavedListRuledItOut = ruledOutEverywhere,", src, System.StringComparison.Ordinal);
        Assert.Contains("radio.EverySavedListRuledItOut = ruledOut;", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("FlexBase.ConnectAccountFor(", src, System.StringComparison.Ordinal);
        Assert.Contains("GetAccountByEmail(radio.BoundAccount)", src, System.StringComparison.Ordinal);
    }

    // ---- #405 repair two (Codex, 2026-11-10): no registration claim from history ----

    /// <summary>
    /// The remembered-account wording follows the state, and only the state:
    /// the same row with <see cref="RadioListItem.EverySavedListRuledItOut"/>
    /// false keeps "registered to" (the control that the branch is the one
    /// deciding), and a CHOSEN saved account keeps "preferred account", which
    /// claims only the choice, so Enter keeps its usual sentence there.
    /// </summary>
    [Fact]
    public void Only_a_remembered_account_is_worded_as_last_known_and_a_chosen_one_keeps_its_words()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        var row = new RadioListItem
        {
            Serial = "0000-0000-6300-0406", Name = "Shack", ModelName = "FLEX-6300",
            PreferredAccount = Chosen, LastSeenViaAccount = LastSeen, ConnectAccount = LastSeen,
            EverySavedListRuledItOut = true, DiscoverySettled = true,
        };
        row.ForeignAccount = RadioListItem.BoundElsewhere(row.BoundAccount, Selector);
        Assert.True(row.NamedFromHistoryOnly);
        Assert.Equal(Lexicon.Get("connect.row.offline_last_known_for",
            ("lastSeenViaAccount", LastSeen), ("age", ""), ("refreshing", "")), row.WhereText);

        row.EverySavedListRuledItOut = false;            // control: another state
        Assert.False(row.NamedFromHistoryOnly);
        Assert.Equal(Lexicon.Get("connect.row.offline_registered_to",
            ("lastSeenViaAccount", LastSeen), ("age", "")), row.WhereText);

        // A chosen account that IS saved, in the same all-answered state.
        var chosen = new RadioListItem
        {
            Serial = "0000-0000-6300-0407", Name = "Shack", ModelName = "FLEX-6300",
            PreferredAccount = LastSeen, LastSeenViaAccount = "", ConnectAccount = LastSeen,
            EverySavedListRuledItOut = true, DiscoverySettled = true,
        };
        chosen.ForeignAccount = RadioListItem.BoundElsewhere(chosen.BoundAccount, Selector);
        Assert.False(chosen.NamedFromHistoryOnly);
        Assert.Equal(Lexicon.Get("connect.row.offline_preferred_account",
            ("preferredAccount", LastSeen), ("age", "")), chosen.WhereText);
    }

    // ---- #405 repair one (Codex B1): every saved list answered, the radio absent ----

    /// <summary>
    /// Codex B1, 2026-11-09: the radio's chosen account is not saved here,
    /// its last-seen one is, and every saved account's list has arrived on
    /// its live connection without the radio (it is switched off). The plan
    /// says no saved account lists it and names none, and the row used to
    /// read that empty as "neither account saved", name the unsaved choice,
    /// and have Enter say the radio was registered to it.
    /// </summary>
    /// <remarks>
    /// <para>Planner to row to Enter, with nothing injected between: the
    /// radio's record is real (in the run's throwaway settings tree), the held
    /// sessions sit in a real coordinator and their lists answer without the
    /// radio, the account manager holds the two saved accounts in memory only,
    /// the row is stamped from the public answer exactly as the roster paint
    /// stamps it, and Enter is the dialog's own private <c>DoConnect</c>, on a
    /// dialog made with no constructor (so no window exists), stopped at the
    /// account switch by the session callback.</para>
    /// <para><b>Desk safety:</b> an MTA thread (asserted), where WPF refuses
    /// to build any element; speech is silenced for the run (asserted).</para>
    /// </remarks>
    [Fact]
    public void Once_every_saved_list_has_answered_without_the_radio_Enter_switches_to_the_saved_last_seen_account()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(JJFlexWpf.Tests.Infrastructure.QuietRun.Silenced, "Refusing: " + JJFlexWpf.Tests.Infrastructure.QuietRun.Describe());
        Assert.True(JJFlexWpf.Tests.Infrastructure.TestSettingsRoot.Isolated, "Refusing: " + JJFlexWpf.Tests.Infrastructure.TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);

        const string serial = "0000-0000-6300-0405";
        const string name = "Shack";
        Assert.True(RadioConfig.Update(serial, c =>
        {
            c.PreferredAccount = Chosen;
            c.LastSeenViaAccount = LastSeen;
            c.Ownership = RadioOwnership.Mine;   // answered, so Enter asks nothing first
        }));

        var manager = FlexBase.SharedAccountManager;
        var accountsField = typeof(SmartLinkAccountManager).GetField("_accounts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        object? accountsBefore = accountsField.GetValue(manager);
        accountsField.SetValue(manager, new System.Collections.Generic.List<SmartLinkAccount>
        {
            new() { Email = LastSeen, FriendlyName = LastSeen },
            new() { Email = Selector, FriendlyName = Selector },
        });

        var coordinatorBefore = Radios.SmartLink.SmartLinkServices.Coordinator;
        var coordinator = new Radios.SmartLink.SmartLinkSessionCoordinator(a => new AnsweredWithoutTheRadio(a));
        Radios.SmartLink.SmartLinkServices.Override(coordinator);
        coordinator.GetOrCreateSession(LastSeen);
        coordinator.GetOrCreateSession(Selector);
        try
        {
            // The planner's verdict: no saved account lists it; the connect uses none.
            Assert.Equal("", FlexBase.ConnectAccountFor(serial));
            // The answer the roster paint stamps: the saved last-seen account,
            // and that it is only remembered (#405 repair two).
            string account = FlexBase.RadioListAccountFor(serial, out bool ruledOut);
            Assert.Equal(LastSeen, account);
            Assert.True(ruledOut);

            var row = new RadioListItem
            {
                Serial = serial, Name = name, ModelName = "FLEX-6300",
                PreferredAccount = Chosen, LastSeenViaAccount = LastSeen, ConnectAccount = account,
                EverySavedListRuledItOut = ruledOut,
                DiscoverySettled = true,
            };
            row.ForeignAccount = RadioListItem.BoundElsewhere(
                RadioListItem.AccountInPlay(account, row.PreferredAccount, row.LastSeenViaAccount), Selector);
            Assert.True(row.ForeignAccount);
            Assert.Equal(LastSeen, row.BoundAccount);
            Assert.DoesNotContain(Chosen, row.DisplayText);

            // #405 repair two (Codex, 2026-11-10): the row names the account
            // as where the radio was last known, never "registered to" it.
            Assert.True(row.NamedFromHistoryOnly);
            Assert.Equal(Lexicon.Get("connect.row.offline_last_known_for",
                ("lastSeenViaAccount", LastSeen), ("age", ""), ("refreshing", "")), row.WhereText);
            Assert.DoesNotContain("registered to", row.DisplayText);

            // Enter.
            var said = new System.Collections.Generic.List<string>();
            string? switchedTo = null;
            var dialog = (RigSelectorDialog)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(RigSelectorDialog));
            SetField(dialog, "_accountSwitchTried", new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase));
            SetField(dialog, "_ownershipAsked", new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase));
            SetField(dialog, "_callbacks", new RigSelectorCallbacks
            {
                StartLocalDiscovery = () => { },
                StartRemoteDiscovery = _ => { },
                RegisterRadioFound = _ => { },
                UnregisterRadioFound = () => { },
                SaveAutoConnectSettings = (_, _, _, _, _) => { },
                SaveGlobalAutoConnect = _ => { },
                CheckOtherAutoConnect = _ => (false, ""),
                ScreenReaderSpeak = (text, _) => said.Add(text),
                SetSessionAccount = email => { switchedTo = email; throw new StoppedAtTheSwitch(); },
            });
            var doConnect = typeof(RigSelectorDialog).GetMethod("DoConnect",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(RadioListItem), typeof(ConnectPathKind?) }, null)!;
            var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => doConnect.Invoke(dialog, new object?[] { row, null }));
            Assert.IsType<StoppedAtTheSwitch>(thrown.InnerException);

            Assert.Equal(LastSeen, switchedTo);
            // Enter says the radio was last seen under it, not that it "uses" it.
            Assert.Equal(new[] { Lexicon.Get("connect.selector.switching_to_last_seen_account", ("radioName", name), ("email", LastSeen)) }, said);
            Assert.DoesNotContain(" uses account ", said[0]);
        }
        finally
        {
            Radios.SmartLink.SmartLinkServices.Override(coordinatorBefore);
            accountsField.SetValue(manager, accountsBefore);
            RadioConfig.Update(serial, c => { c.PreferredAccount = ""; c.LastSeenViaAccount = ""; });
        }
    }

    private static void SetField(object target, string field, object value)
        => target.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(target, value);

    private sealed class StoppedAtTheSwitch : System.Exception { }

    /// <summary>
    /// A held session that is connected and whose list has arrived on that
    /// connection, without the radio. Only what the account picture reads is
    /// real; the rest refuses.
    /// </summary>
    private sealed class AnsweredWithoutTheRadio : Radios.SmartLink.IWanSessionOwner
    {
        public AnsweredWithoutTheRadio(string account) { AccountId = account; SessionId = "s-" + account; }
        public string SessionId { get; }
        public string AccountId { get; }
        public bool IsConnected => true;
        public Radios.SmartLink.SessionStatus Status => Radios.SmartLink.SessionStatus.Connected;
        public System.Exception? LastError => null;
        public int ReconnectAttemptCount => 0;
        public System.Collections.Generic.IReadOnlyList<Flex.Smoothlake.FlexLib.Radio> AvailableRadios
            => System.Array.Empty<Flex.Smoothlake.FlexLib.Radio>();
        public System.DateTime? LastRadioListUtc => System.DateTime.UtcNow;
        public Radios.SmartLink.SessionRadioListSnapshot RadioListSnapshot => new(
            System.Array.Empty<Flex.Smoothlake.FlexLib.Radio>(), System.DateTime.UtcNow,
            SessionConnected: true, ArrivedOnTheLiveConnection: true, ConnectionGeneration: 1, SessionId: SessionId)
        { ListRevision = 1 };
        public bool ListIsCurrent(long connectionGeneration) => connectionGeneration == 1;
#pragma warning disable CS0067 // events the picture never raises
        public event System.EventHandler? ListCurrencyMayHaveChanged;
        public event System.EventHandler<Radios.SmartLink.SessionStatus>? StatusChanged;
        public event System.EventHandler<Radios.SmartLink.SignalThresholdEventArgs>? SignalThresholdCrossed;
        public event System.EventHandler<Radios.SmartLink.WanRadioListReceivedEventArgs>? RadioListReceived;
        public event System.EventHandler<Radios.SmartLink.NetworkDiagnosticReport>? NetworkReportReady;
#pragma warning restore CS0067
        public Radios.SmartLink.ISessionAudioSink AudioSink => throw new System.NotSupportedException();
        public void Connect() => throw new System.NotSupportedException();
        public void Disconnect() => throw new System.NotSupportedException();
        public void ReRegister(string programName, string platform, string jwt) => throw new System.NotSupportedException();
        public void EnableAutoRegistration(System.Func<bool, string?> jwtProvider, string programName, string platform = "Win10")
            => throw new System.NotSupportedException();
        public bool TryClaimRegistration() => throw new System.NotSupportedException();
        public System.Threading.Tasks.Task<string?> ConnectToRadio(string serial, int holePunchPort = 0,
            System.Threading.CancellationToken cancellationToken = default) => throw new System.NotSupportedException();
        public void Reset() => throw new System.NotSupportedException();
        public System.Threading.Tasks.Task<Radios.SmartLink.NetworkDiagnosticReport> RunNetworkDiagnosticAsync(string radioSerial,
            bool forceRefresh = false, System.TimeSpan? timeout = null, System.Threading.CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();
        public Radios.SmartLink.NetworkDiagnosticReport? GetLastNetworkReport(string radioSerial) => null;
        public Radios.SmartLink.NetworkDiagnosticReport? MostRecentNetworkReport => null;
        public void Dispose() { }
    }
}

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

    // ---- #405 repair three (Codex, 2026-11-10): the row follows the lists as they arrive ----

    /// <summary>
    /// Codex, verify three: the picker opened before the saved accounts'
    /// first lists arrived, so the paint asked the plan while a list was
    /// still to come and stamped "registered to" the saved last-seen account.
    /// Every saved list then answered without the radio, and the refresh the
    /// presence watcher runs re-read the sessions but never asked the plan
    /// again: the row kept "registered to" and Enter kept "uses account".
    /// </summary>
    /// <remarks>
    /// <para>Cold paint, then the empty lists arriving, then the real refresh,
    /// then the row and Enter, all through the dialog's own private methods:
    /// <c>PaintRoster</c> reads the radio from its real record (in the run's
    /// throwaway settings tree), <c>RefreshRadiosList</c> is the method
    /// <c>WatchPresenceSettleAsync</c> calls on every tick (the last assertion
    /// pins that), and Enter is <c>DoConnect</c>, stopped at the account
    /// switch. The earlier test builds its row only after every list has
    /// arrived, which is why it could not see this; it stays.</para>
    /// <para><b>Desk safety:</b> the dialog is made with no constructor, so
    /// no window exists. The paint and the refresh run on a private STA
    /// thread against an unhosted ListBox with no presentation source
    /// (asserted); Enter runs on this MTA thread (asserted), where WPF refuses
    /// to build any element. Speech is silenced for the run (asserted).</para>
    /// </remarks>
    [Fact]
    public void A_row_painted_before_the_lists_arrive_follows_them_and_Enter_says_last_seen()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(JJFlexWpf.Tests.Infrastructure.QuietRun.Silenced, "Refusing: " + JJFlexWpf.Tests.Infrastructure.QuietRun.Describe());
        Assert.True(JJFlexWpf.Tests.Infrastructure.TestSettingsRoot.Isolated, "Refusing: " + JJFlexWpf.Tests.Infrastructure.TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);

        const string serial = "0000-0000-6300-0408";
        const string name = "Cold Shack";
        Assert.True(RadioConfig.Update(serial, c =>
        {
            c.Nickname = name;
            c.Model = "FLEX-6300";
            c.PreferredAccount = Chosen;          // not saved here
            c.LastSeenViaAccount = LastSeen;      // saved here
            c.Ownership = RadioOwnership.Mine;    // answered, so Enter asks nothing first
            c.HiddenFromList = false;
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

        // Both saved accounts signed in, neither list arrived yet: the cold open.
        var sessions = new System.Collections.Generic.List<AnsweredWithoutTheRadio>();
        var coordinatorBefore = Radios.SmartLink.SmartLinkServices.Coordinator;
        var coordinator = new Radios.SmartLink.SmartLinkSessionCoordinator(a =>
        {
            var s = new AnsweredWithoutTheRadio(a) { Arrived = false };
            sessions.Add(s);
            return s;
        });
        Radios.SmartLink.SmartLinkServices.Override(coordinator);
        coordinator.GetOrCreateSession(LastSeen);
        coordinator.GetOrCreateSession(Selector);
        Assert.Equal(2, sessions.Count);

        var said = new System.Collections.Generic.List<string>();
        string? switchedTo = null;
        var dialog = (RigSelectorDialog)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(RigSelectorDialog));
        SetField(dialog, "_radiosList", new System.Collections.Generic.List<RadioListItem>());
        SetField(dialog, "_radiosLock", new object());
        SetField(dialog, "_learnedPaths", new System.Collections.Generic.Dictionary<string, ConnectPathKind?>(System.StringComparer.OrdinalIgnoreCase));
        SetField(dialog, "_autoSelectAnnounced", new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase));
        SetField(dialog, "_accountSwitchTried", new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase));
        SetField(dialog, "_ownershipAsked", new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase));
        SetField(dialog, "_localSettled", true);   // discovery settled: the row is past "checking"
        SetField(dialog, "_callbacks", new RigSelectorCallbacks
        {
            StartLocalDiscovery = () => { },
            StartRemoteDiscovery = _ => { },
            RegisterRadioFound = _ => { },
            UnregisterRadioFound = () => { },
            SaveAutoConnectSettings = (_, _, _, _, _) => { },
            SaveGlobalAutoConnect = _ => { },
            CheckOtherAutoConnect = _ => (false, ""),
            CurrentSmartLinkEmail = Selector,
            ScreenReaderSpeak = (text, _) => said.Add(text),
            SetSessionAccount = email => { switchedTo = email; throw new StoppedAtTheSwitch(); },
        });
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var paint = typeof(RigSelectorDialog).GetMethod("PaintRoster", flags, null, new[] { typeof(string) }, null)!;
        var refresh = typeof(RigSelectorDialog).GetMethod("RefreshRadiosList", flags, null, new[] { typeof(bool) }, null)!;

        RadioListItem? row = null;
        string coldText = "", answeredText = "";
        bool coldHistory = true, answeredHistory = false, shown = false;
        try
        {
            OnPrivateSta(() =>
            {
                var box = new System.Windows.Controls.ListBox();
                SetField(dialog, "RadiosBox", box);

                // The cold paint: the plan has a list still to come.
                paint.Invoke(dialog, new object[] { Selector });
                lock (GetField<object>(dialog, "_radiosLock"))
                    row = GetField<System.Collections.Generic.List<RadioListItem>>(dialog, "_radiosList")
                        .Single(r => r.Serial == serial);
                coldHistory = row.NamedFromHistoryOnly;
                coldText = row.WhereText;

                // Every saved list arrives, without the radio; then the refresh
                // the presence watcher runs.
                foreach (var s in sessions) s.Arrived = true;
                refresh.Invoke(dialog, new object[] { false });
                answeredHistory = row.NamedFromHistoryOnly;
                answeredText = row.WhereText;
                shown = box.Items.Contains(row);

                Assert.Null(System.Windows.PresentationSource.FromVisual(box));   // never hosted
                SetField(dialog, "RadiosBox", null!);
            });

            Assert.NotNull(row);
            string age = string.IsNullOrEmpty(row!.LastSeenText) ? ""
                : Lexicon.Get("connect.row.age_suffix", ("lastSeenText", row.LastSeenText));

            // The cold paint: both accounts signed in, neither list arrived.
            // Until #1059 this pinned "not heard from, registered to" here,
            // which asserted an absence nothing had answered. The row now
            // reads the card's answered-list fact and says it is checking.
            Assert.False(coldHistory);
            Assert.Equal(Lexicon.Get("connect.row.checking"), coldText);
            Assert.DoesNotContain("not heard from", coldText);

            // After the lists and the refresh: the same account, now only where
            // the radio was last known, never "registered to".
            Assert.True(shown);
            Assert.Equal(LastSeen, row.BoundAccount);
            Assert.True(row.EverySavedListRuledItOut);
            Assert.True(answeredHistory);
            Assert.True(row.ForeignAccount);
            Assert.Equal(Lexicon.Get("connect.row.offline_last_known_for",
                ("lastSeenViaAccount", LastSeen), ("age", age), ("refreshing", "")), answeredText);
            Assert.DoesNotContain("registered to", row.DisplayText);
            Assert.DoesNotContain(Chosen, row.DisplayText);

            // Enter, on the row the refresh left.
            var doConnect = typeof(RigSelectorDialog).GetMethod("DoConnect", flags,
                null, new[] { typeof(RadioListItem), typeof(ConnectPathKind?) }, null)!;
            var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => doConnect.Invoke(dialog, new object?[] { row, null }));
            Assert.IsType<StoppedAtTheSwitch>(thrown.InnerException);
            Assert.Equal(LastSeen, switchedTo);
            Assert.Equal(new[] { Lexicon.Get("connect.selector.switching_to_last_seen_account", ("radioName", name), ("email", LastSeen)) }, said);
            Assert.DoesNotContain(" uses account ", said[0]);
        }
        finally
        {
            Radios.SmartLink.SmartLinkServices.Override(coordinatorBefore);
            accountsField.SetValue(manager, accountsBefore);
            RadioConfig.Update(serial, c => { c.PreferredAccount = ""; c.LastSeenViaAccount = ""; c.HiddenFromList = true; });
        }

        // The refresh tested is the one the presence watcher runs on every tick.
        string src = ReadSelectorSource();
        int watch = src.IndexOf("private async System.Threading.Tasks.Task WatchPresenceSettleAsync()", System.StringComparison.Ordinal);
        int loop = src.IndexOf("while (PresenceStillSettling())", watch, System.StringComparison.Ordinal);
        int call = src.IndexOf("RefreshRadiosList();", loop, System.StringComparison.Ordinal);
        int end = src.IndexOf("_presenceSettleDeadlineUtc = null;", loop, System.StringComparison.Ordinal);
        Assert.True(watch > 0 && loop > watch && call > loop && call < end,
            "WatchPresenceSettleAsync must call RefreshRadiosList inside its settle loop");
        // ...and that refresh asks the plan outside the list lock, before it stamps.
        int refreshAt = src.IndexOf("private void RefreshRadiosList(bool reorder = false)", System.StringComparison.Ordinal);
        // The held lists are read ONCE (#1059), before the plan is asked, and
        // the same reading feeds the replan.
        int read = src.IndexOf("var heldLists = ReadHeldLists();", refreshAt, System.StringComparison.Ordinal);
        int ask = src.IndexOf("var replanned = ReplanRowAccountsIfTheListsMoved(heldLists);", refreshAt, System.StringComparison.Ordinal);
        int stamp = src.IndexOf("r.EverySavedListRuledItOut = plan.RuledOut;", refreshAt, System.StringComparison.Ordinal);
        int stampLock = src.LastIndexOf("lock (_radiosLock)", stamp, System.StringComparison.Ordinal);
        Assert.True(refreshAt > 0 && read > refreshAt && ask > read && stampLock > ask && stamp > stampLock,
            "RefreshRadiosList must read the held lists once and ask the plan before the lock it stamps the rows under");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(
            src.Substring(refreshAt, stamp - refreshAt), @"ReadHeldLists\(\)").Count);
        int replanAt = src.IndexOf("private Dictionary<string, (string Account, bool RuledOut)> ReplanRowAccountsIfTheListsMoved(HeldLists lists)", System.StringComparison.Ordinal);
        int replanEnd = src.IndexOf("internal static List<object>? ListsKey(HeldLists lists)", replanAt, System.StringComparison.Ordinal);
        string replan = src.Substring(replanAt, replanEnd - replanAt);
        Assert.DoesNotContain("AllSessions", replan, System.StringComparison.Ordinal);   // no second reader
        int check = replan.IndexOf("if (!ListsMovedSince(now, _rowAccountsAskedAt))", System.StringComparison.Ordinal);
        int askInReplan = replan.IndexOf("Radios.FlexBase.RadioListAccountFor(serial, out bool ruledOut);", System.StringComparison.Ordinal);
        int lockAt = replan.IndexOf("lock (_radiosLock)", System.StringComparison.Ordinal);
        int lockEnd = replan.IndexOf("foreach (var serial in serials)", System.StringComparison.Ordinal);
        Assert.True(check > 0 && lockAt > check, "the change check answers before any row is collected or any record read");
        Assert.True(askInReplan > lockEnd && lockEnd > 0, "the plan is asked after the list lock is released");
    }

    // ── #405 repair one: Radio Associations follows the lists while open ──

    /// <summary>
    /// #405 repair one (Codex B1). Radio Associations opens in the all-negative
    /// state, so its row says the saved account is not listed right now; then
    /// that same account's list names the radio while the window stays open.
    /// The row must lose the marking, through the real resolver and the radio
    /// list's change check, without a reopen. A quiet event (nothing moved)
    /// asks nothing, so no record is read.
    /// </summary>
    /// <remarks>
    /// <b>Desk safety:</b> the dialog is made with no constructor; its list is
    /// an unhosted ListBox on a private STA thread (presentation source asserted
    /// null). The asks run on the thread pool and land back through that
    /// thread's dispatcher, which this test pumps. Speech silenced, settings
    /// isolated (asserted).
    /// </remarks>
    [Fact]
    public void Radio_associations_drops_not_listed_when_the_same_account_lists_the_radio_again()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(JJFlexWpf.Tests.Infrastructure.QuietRun.Silenced, "Refusing: " + JJFlexWpf.Tests.Infrastructure.QuietRun.Describe());
        Assert.True(JJFlexWpf.Tests.Infrastructure.TestSettingsRoot.Isolated, "Refusing: " + JJFlexWpf.Tests.Infrastructure.TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);

        const string serial = "0000-0000-6300-1405";
        Assert.True(RadioConfig.Update(serial, c =>
        {
            c.Nickname = "Back Again";
            c.Model = "FLEX-6300";
            c.PreferredAccount = Chosen;          // not saved here
            c.LastSeenViaAccount = LastSeen;      // saved here
            c.HiddenFromList = false;
        }));

        var manager = FlexBase.SharedAccountManager;
        var accountsField = typeof(SmartLinkAccountManager).GetField("_accounts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        object? accountsBefore = accountsField.GetValue(manager);
        accountsField.SetValue(manager, new System.Collections.Generic.List<SmartLinkAccount>
        {
            new() { Email = LastSeen, FriendlyName = LastSeen },
        });
        var sessions = new System.Collections.Generic.List<AnsweredWithoutTheRadio>();
        var coordinatorBefore = Radios.SmartLink.SmartLinkServices.Coordinator;
        var coordinator = new Radios.SmartLink.SmartLinkSessionCoordinator(a =>
        {
            var s = new AnsweredWithoutTheRadio(a);   // connected, list arrived, radio absent
            sessions.Add(s);
            return s;
        });
        Radios.SmartLink.SmartLinkServices.Override(coordinator);
        coordinator.GetOrCreateSession(LastSeen);

        string notListed = Lexicon.Get("settings.associations.not_listed_now");
        string opened = "", followed = "", quiet = "";
        try
        {
            OnPrivateSta(() =>
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext());
                var dialog = (RadioAssociationsDialog)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(RadioAssociationsDialog));
                var box = new System.Windows.Controls.ListBox();
                SetField(dialog, "RadioList", box);
                typeof(RadioAssociationsDialog).GetMethod("ReloadRadios",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(dialog, new object?[] { null });
                var row = box.Items.OfType<RadioAssociationsDialog.RadioRow>().Single(r => r.Entry.Serial == serial);
                opened = row.DisplayText;
                int changed = 0;
                row.PropertyChanged += (_, e) => { if (e.PropertyName == "DisplayText") changed++; };

                void Follow()
                {
                    var task = dialog.FollowTheListsAsync();
                    var deadline = System.DateTime.UtcNow.AddSeconds(10);
                    while (!task.IsCompleted && System.DateTime.UtcNow < deadline)
                    {
                        var frame = new System.Windows.Threading.DispatcherFrame();
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Background,
                            new System.Action(() => frame.Continue = false));
                        System.Windows.Threading.Dispatcher.PushFrame(frame);
                        System.Threading.Thread.Sleep(10);
                    }
                    Assert.True(task.IsCompleted, "the follow did not finish");
                    task.GetAwaiter().GetResult();
                }

                // The same account's list now names the radio.
                sessions[0].Listed = new[]
                {
                    Flex.Smoothlake.FlexLib.Radio.CreateFromIp("FLEX-6300", serial, "Back Again",
                        System.Net.IPAddress.Loopback, "4.2.20.0"),
                };
                Follow();
                followed = row.DisplayText;
                Assert.Equal(1, dialog.ListAskPasses);

                // Nothing moved: no ask, so no record is read on a quiet event.
                Follow();
                quiet = row.DisplayText;
                Assert.Equal(1, dialog.ListAskPasses);          // nothing moved: the rows were not asked
                Assert.Equal(1, changed);                       // the binding was told once, for the real change

                Assert.Null(System.Windows.PresentationSource.FromVisual(box));   // never hosted
            });

            Assert.Contains(LastSeen, opened);
            Assert.EndsWith(notListed, opened);                 // the all-negative state, at load
            Assert.Contains(LastSeen, followed);
            Assert.DoesNotContain(notListed, followed);         // back on the same account: no marking
            Assert.Equal(followed, quiet);                      // a quiet event asked nothing
        }
        finally
        {
            Radios.SmartLink.SmartLinkServices.Override(coordinatorBefore);
            accountsField.SetValue(manager, accountsBefore);
            RadioConfig.Update(serial, c => { c.PreferredAccount = ""; c.LastSeenViaAccount = ""; c.HiddenFromList = true; });
        }

        // The window listens to the two notifications the radio list listens
        // to, and stops when it closes.
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(ReadSelectorPath()))!, "Dialogs", "RadioAssociationsDialog.xaml.cs"));
        Assert.Contains("_listSource.SessionRadioListReceived += OnListsMayHaveMoved;", src, System.StringComparison.Ordinal);
        Assert.Contains("_listSource.SessionListCurrencyMayHaveChanged += OnListsMayHaveMoved;", src, System.StringComparison.Ordinal);
        Assert.Contains("_listSource.SessionRadioListReceived -= OnListsMayHaveMoved;", src, System.StringComparison.Ordinal);
    }

    private static string ReadSelectorPath()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, "JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs");
    }

    // ── #1059: one reader of the held lists, each caller its own safe answer ──

    private static Radios.SmartLink.SessionRadioListSnapshot Snapshot(bool connected, bool arrived, long revision = 1) =>
        new(System.Array.Empty<Flex.Smoothlake.FlexLib.Radio>(), arrived ? new System.DateTime(2026, 11, 11, 12, 0, 0, System.DateTimeKind.Utc) : null,
            SessionConnected: connected, ArrivedOnTheLiveConnection: connected && arrived,
            ConnectionGeneration: arrived ? 1 : -1, SessionId: "s") { ListRevision = revision };

    /// <summary>
    /// The one reading's views (#1059): signed in is not answered, a session
    /// with no account counts nowhere, and an unread table answers empty for
    /// the rows and the card (nothing heard, nothing answered) but has no key
    /// for the replan, which reads as moved (re-ask every row).
    /// </summary>
    [Fact]
    public void The_held_lists_reading_gives_each_caller_its_own_safe_answer()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var lists = new RigSelectorDialog.HeldLists(new[]
        {
            ("answered@example.com", Snapshot(connected: true, arrived: true)),
            ("waiting@example.com", Snapshot(connected: true, arrived: false)),
            ("gone@example.com", Snapshot(connected: false, arrived: true)),
            ("", Snapshot(connected: true, arrived: true)),
        }, Read: true);
        Assert.Equal(new[] { "answered@example.com", "waiting@example.com" }, lists.LiveAccounts().OrderBy(a => a));
        Assert.Equal(new[] { "answered@example.com" }, lists.AnsweredAccounts());
        Assert.Equal(3, lists.NamedLists().Count);
        Assert.NotNull(RigSelectorDialog.ListsKey(lists));

        var unread = RigSelectorDialog.HeldLists.Unread;
        Assert.Empty(unread.LiveAccounts());
        Assert.Empty(unread.AnsweredAccounts());
        Assert.Empty(unread.NamedLists());
        Assert.Null(RigSelectorDialog.ListsKey(unread));
        Assert.True(RigSelectorDialog.ListsMovedSince(null, RigSelectorDialog.ListsKey(lists)));
    }

    /// <summary>
    /// The replan's change check, run (#405 repair three, kept through #1059):
    /// the rows are asked again only when a list moved; an unchanged reading
    /// asks nothing (no record read on a quiet tick); a new list revision asks
    /// again; and an unread table asks every time. Removing the check turns
    /// the second assertion red.
    /// </summary>
    [Fact]
    public void The_replan_asks_again_only_when_the_lists_moved_and_every_time_when_unread()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var dialog = (RigSelectorDialog)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(RigSelectorDialog));
        SetField(dialog, "_radiosLock", new object());
        SetField(dialog, "_radiosList", new System.Collections.Generic.List<RadioListItem>
        {
            new() { Serial = "0000-0000-1059-0001", AccountAskedOfThePlan = true },
        });
        var replan = typeof(RigSelectorDialog).GetMethod("ReplanRowAccountsIfTheListsMoved",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        int Asked(RigSelectorDialog.HeldLists lists) =>
            ((System.Collections.IDictionary)replan.Invoke(dialog, new object[] { lists })!).Count;

        var before = new RigSelectorDialog.HeldLists(new[] { ("a@example.com", Snapshot(true, false, revision: 0)) }, true);
        var same = new RigSelectorDialog.HeldLists(new[] { ("a@example.com", Snapshot(true, false, revision: 0)) }, true);
        var arrived = new RigSelectorDialog.HeldLists(new[] { ("a@example.com", Snapshot(true, true, revision: 1)) }, true);

        Assert.Equal(1, Asked(before));                          // the first ask
        Assert.Equal(0, Asked(same));                            // unchanged: nothing asked
        Assert.Equal(1, Asked(arrived));                         // the list landed: asked again
        Assert.Equal(0, Asked(arrived));
        Assert.Equal(1, Asked(RigSelectorDialog.HeldLists.Unread));   // unread: asked every time
        Assert.Equal(1, Asked(RigSelectorDialog.HeldLists.Unread));
    }

    /// <summary>
    /// #1059: a row whose bound account is signed in but whose lists have not
    /// answered says "checking", never "not heard from"; once SmartLink has
    /// answered without the radio it says "not heard from" as before. And the
    /// settle loop waits for answers, not sign-ins.
    /// </summary>
    [Fact]
    public void A_row_signed_in_but_not_yet_answered_says_checking_and_the_settle_loop_waits_for_the_answer()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        var row = new RadioListItem
        {
            Serial = "0000-0000-1059-0002", ModelName = "FLEX-6300", Name = "Pending",
            DiscoverySettled = true, ForeignAccount = true, ConnectAccount = LastSeen,
            BoundAccountHasLiveSession = true, BoundAccountListPending = true,
        };
        Assert.Equal(Lexicon.Get("connect.row.checking"), row.WhereText);

        row.BoundAccountListPending = false;                    // answered without it
        row.SmartLinkAnsweredWithoutIt = true;
        Assert.Contains("not heard from", row.WhereText);

        string src = ReadSelectorSource();
        int loop = src.IndexOf("while (PresenceStillSettling())", System.StringComparison.Ordinal);
        int end = src.IndexOf("_presenceSettleDeadlineUtc = null;", loop, System.StringComparison.Ordinal);
        string body = src.Substring(loop, end - loop);
        Assert.Contains("var answered = ReadHeldLists().AnsweredAccounts();", body, System.StringComparison.Ordinal);
        Assert.Contains("&& !answered.Contains(a));", body, System.StringComparison.Ordinal);
        // ...and the row's flag is stamped from the card's own fact.
        Assert.Contains("&& !r.SmartLinkAnsweredWithoutIt;", src, System.StringComparison.Ordinal);
    }

    private static string ReadSelectorSource()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs"))
            .Replace("\r\n", "\n");
    }

    private static T GetField<T>(object target, string field)
        => (T)target.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(target)!;

    /// <summary>
    /// Runs <paramref name="body"/> on a private STA thread, for the unhosted
    /// ListBox the refresh writes to. Nothing on that thread is ever shown.
    /// </summary>
    private static void OnPrivateSta(System.Action body)
    {
        Assert.Equal(System.Threading.ApartmentState.MTA, System.Threading.Thread.CurrentThread.GetApartmentState());
        System.Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (System.Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(System.TimeSpan.FromSeconds(30)), "The radio list test did not finish.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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

        /// <summary>
        /// Whether the first list has arrived on the live connection. True by
        /// default; the cold-open test starts it false (signed in, no list
        /// yet) and sets it when the list lands (#405 repair three).
        /// </summary>
        public bool Arrived { get; set; } = true;

        private static readonly System.Collections.Generic.IReadOnlyList<Flex.Smoothlake.FlexLib.Radio> NoRadios
            = System.Array.Empty<Flex.Smoothlake.FlexLib.Radio>();
        private static readonly System.DateTime ListLanded = new(2026, 11, 10, 12, 0, 0, System.DateTimeKind.Utc);

        public bool IsConnected => true;
        public Radios.SmartLink.SessionStatus Status => Radios.SmartLink.SessionStatus.Connected;
        public System.Exception? LastError => null;
        public int ReconnectAttemptCount => 0;
        /// <summary>
        /// The radios this session's list names. None by default; the
        /// Associations follow test sets it to make the radio come back
        /// (#405 repair one), which is a newer list, so a new revision.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<Flex.Smoothlake.FlexLib.Radio> Listed { get; set; } = NoRadios;
        public System.Collections.Generic.IReadOnlyList<Flex.Smoothlake.FlexLib.Radio> AvailableRadios => Listed;
        public System.DateTime? LastRadioListUtc => Arrived ? ListLanded : null;
        // The same snapshot for the same state, as a real session's is (it
        // hands back its held fields), so the window's "have the lists moved"
        // comparison sees a move only when the list lands.
        public Radios.SmartLink.SessionRadioListSnapshot RadioListSnapshot => Arrived
            ? new(Listed, ListLanded, SessionConnected: true, ArrivedOnTheLiveConnection: true,
                ConnectionGeneration: 1, SessionId: SessionId) { ListRevision = Listed.Count == 0 ? 1 : 2 }
            : new(NoRadios, null, SessionConnected: true, ArrivedOnTheLiveConnection: false,
                ConnectionGeneration: -1, SessionId: SessionId) { ListRevision = 0 };
        public bool ListIsCurrent(long connectionGeneration) => Arrived && connectionGeneration == 1;
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

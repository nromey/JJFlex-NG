#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// SmartLink registration goes through the RADIO's own account, by the
    /// same routing resolver the connect uses (#713, #798), and falls back to
    /// the saved default only when nothing names one, which is the usual case
    /// for a radio being registered for the first time.
    /// </summary>
    /// <remarks>
    /// The confirm step that names the account before anything is sent is the
    /// consent, and it is kept: these tests pin which account it is GIVEN.
    /// Nothing here registers anything; no radio is involved.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RegistrationAccountTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(RegistrationAccountTests));
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        private const string Default = "operator@example.com";
        private const string Owner = "owner@example.com";

        private static readonly SmartLinkAccount A = new() { Email = Default, FriendlyName = "Operator" };
        private static readonly SmartLinkAccount B = new() { Email = Owner, FriendlyName = "Owner" };
        private static readonly IReadOnlyList<SmartLinkAccount> Saved = new[] { A, B };

        private static readonly FieldInfo CurrentAccount =
            typeof(FlexBase).GetField("_currentAccount", BindingFlags.NonPublic | BindingFlags.Instance)!;

        public RegistrationAccountTests()
        {
            Assert.True(CurrentAccount != null, "FlexBase._currentAccount is not where this test sets it.");
            // The connection's account is the default, as after a picker pass.
            CurrentAccount.SetValue(_rig, A);
        }

        public void Dispose()
        {
            _rig.Dispose();
            _scope.Dispose();
        }

        private static int _next = Random.Shared.Next(1000, 9000);

        // Digits only: a radio id is four groups of digits, and the store
        // refuses anything else.
        private static string NewSerial() =>
            "1111-" + System.Threading.Interlocked.Increment(ref _next).ToString("D4") + "-6300-0001";

        [Fact]
        public void A_radio_whose_preferred_account_is_another_registers_to_that_account()
        {
            string serial = NewSerial();
            Assert.True(KnownRadioRoster.SetPreferredAccount(serial, Owner));

            var account = _rig.ResolveRegistrationAccount(serial, Saved);

            Assert.Same(B, account);
            // Routing only: the connection's own account is untouched.
            Assert.Same(A, CurrentAccount.GetValue(_rig));
        }

        /// <summary>
        /// Integration finding 5: registration walks the same tiers, in the
        /// same order, filtered by "saved", as the connect's plan. A radio
        /// whose chosen account is no longer saved, and that was last seen on
        /// a saved account other than the default, connects as that account;
        /// registration used to take only the top tier, find it unsaved, and
        /// drop to the default, so one radio resolved to two accounts.
        /// </summary>
        [Fact]
        public void A_chosen_account_nobody_saved_falls_to_the_last_seen_account_as_the_connect_does()
        {
            string serial = NewSerial();
            Assert.True(KnownRadioRoster.SetPreferredAccount(serial, "gone@example.com"));
            Assert.True(KnownRadioRoster.RecordSighting(serial, "Owner6300", "FLEX-6300", isRemote: true, accountEmail: Owner));

            Assert.Same(B, _rig.ResolveRegistrationAccount(serial, Saved));
            // And the connect's plan names the same account for the same radio.
            var picture = FlexBase.TakeAccountPictureFor(serial, saved: new[] { Default, Owner });
            Assert.Equal(Owner, FlexBase.PlanSmartLinkSignIn(picture).Account);
        }

        [Fact]
        public void A_radio_last_seen_on_another_account_registers_to_that_account()
        {
            string serial = NewSerial();
            Assert.True(KnownRadioRoster.RecordSighting(serial, "Owner6300", "FLEX-6300", isRemote: true, accountEmail: Owner));

            Assert.Same(B, _rig.ResolveRegistrationAccount(serial, Saved));
        }

        /// <summary>
        /// The normal case: a radio being registered for the first time is on
        /// no list and names no account. It still gets the saved account, which
        /// the confirm step then names. Never a refusal.
        /// </summary>
        [Fact]
        public void A_radio_with_no_account_recorded_still_gets_the_saved_account()
        {
            Assert.Same(A, _rig.ResolveRegistrationAccount(NewSerial(), Saved));
        }

        [Fact]
        public void A_radio_that_names_an_account_nobody_saved_falls_back_to_the_saved_account()
        {
            string serial = NewSerial();
            Assert.True(KnownRadioRoster.SetPreferredAccount(serial, "stranger@example.com"));

            Assert.Same(A, _rig.ResolveRegistrationAccount(serial, Saved));
        }

        private static readonly FieldInfo TheRadio =
            typeof(FlexBase).GetField("theRadio", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo IsConnectedField =
            typeof(FlexBase).GetField("_IsConnected", BindingFlags.NonPublic | BindingFlags.Instance)!;

        /// <summary>A rig on a local radio, as Radio Setup sees it when Register is pressed. No transport leaves the process.</summary>
        private Radios.Tests.StationConnect.VendorRadioFixture OnALocalRadio(string serial)
        {
            Assert.True(TheRadio != null && IsConnectedField != null, "FlexBase's radio fields are not where this test sets them.");
            var vendor = new Radios.Tests.StationConnect.VendorRadioFixture(serial);
            TheRadio.SetValue(_rig, vendor.Radio);
            IsConnectedField.SetValue(_rig, true);
            return vendor;
        }

        /// <summary>
        /// Repair round 1, Codex's first #713 blocker. The confirmation names
        /// the account from ONE preflight; a status refresh runs another
        /// preflight while the confirmation is open (Radio Setup's
        /// RefreshSetupStatuses does), and its answer must not become the
        /// account the registration is sent under.
        /// </summary>
        [Fact]
        public void A_status_refresh_after_the_confirmation_cannot_change_the_account_it_is_sent_under()
        {
            string serial = NewSerial();
            OnALocalRadio(serial);

            var confirmed = _rig.PreflightSmartLinkRegistration();      // what the confirm step names
            Assert.True(confirmed.CanProceed, confirmed.BlockReason);
            Assert.Equal(Default, confirmed.AccountEmail);

            CurrentAccount.SetValue(_rig, B);                          // the resolution changes meanwhile
            var refreshed = _rig.PreflightSmartLinkRegistration();      // the status refresh
            Assert.Equal(Owner, refreshed.AccountEmail);

            // What the send uses is the CONFIRMED check's account, on this
            // radio. Red at eab81fbd, where the send read a rig-wide field the
            // refresh had just overwritten with the owner's account.
            var sentUnder = FlexBase.AccountConfirmedFor(confirmed, serial);
            Assert.Same(A, sentUnder);
            Assert.Same(A, confirmed.Account);
            Assert.Same(B, FlexBase.AccountConfirmedFor(refreshed, serial));
        }

        /// <summary>
        /// The retry, and any send, asks the check again against the radio now
        /// connected: a check made for another radio is refused, never sent.
        /// </summary>
        [Fact]
        public void A_confirmed_check_is_refused_on_any_other_radio()
        {
            string serial = NewSerial();
            OnALocalRadio(serial);
            var confirmed = _rig.PreflightSmartLinkRegistration();
            Assert.Same(A, FlexBase.AccountConfirmedFor(confirmed, serial));

            Assert.Null(FlexBase.AccountConfirmedFor(confirmed, NewSerial()));
            Assert.Null(FlexBase.AccountConfirmedFor(confirmed, ""));
            Assert.Null(FlexBase.AccountConfirmedFor(null!, serial));
            Assert.Null(FlexBase.AccountConfirmedFor(new FlexBase.RegistrationCheck { CanProceed = false }, serial));
        }

        /// <summary>
        /// Codex's #713 B2: the radio the send checked is the radio it may
        /// command. After the token, a different radio object in its place, or
        /// a disconnect, stops the send.
        /// </summary>
        [Fact]
        public void The_send_stops_if_the_radio_it_checked_is_no_longer_the_connected_one()
        {
            var first = OnALocalRadio(NewSerial());
            Assert.True(_rig.StillTheRegistrationTarget(first.Radio));

            var second = OnALocalRadio(NewSerial());
            Assert.False(_rig.StillTheRegistrationTarget(first.Radio));
            Assert.True(_rig.StillTheRegistrationTarget(second.Radio));

            IsConnectedField.SetValue(_rig, false);
            Assert.False(_rig.StillTheRegistrationTarget(second.Radio));
        }

        /// <summary>
        /// The wiring: both registration doors ask the resolver, neither asks
        /// the saved default directly, and the account the preflight resolved
        /// is the one the confirm step names and the command is sent under.
        /// </summary>
        [Fact]
        public void Both_registration_doors_ask_the_radios_own_account()
        {
            string src = FlexBasePartials.JoinedText();
            string preflight = Body(src, "public RegistrationCheck PreflightSmartLinkRegistration()");
            string query = Body(src, "public Task<SmartLinkRegistrationEvidence.Finding> QuerySmartLinkRegistrationAsync()");
            string send = Body(src, "private bool SendRegistrationCommand(");

            Assert.Contains("ResolveRegistrationAccount(theRadio.Serial)", preflight, StringComparison.Ordinal);
            Assert.DoesNotContain("TryLoadSavedAccount()", preflight, StringComparison.Ordinal);
            Assert.Contains("check.AccountEmail = account.Email;", preflight, StringComparison.Ordinal);
            Assert.Contains("check.Account = account;", preflight, StringComparison.Ordinal);
            Assert.DoesNotContain("_registrationAccount", src, StringComparison.Ordinal);

            Assert.Contains("ResolveRegistrationAccount(serial)", query, StringComparison.Ordinal);
            Assert.DoesNotContain("TryLoadSavedAccount()", query, StringComparison.Ordinal);

            Assert.Contains("AccountConfirmedFor(confirmed, r.Serial)", send, StringComparison.Ordinal);
            // ONE read of the radio for the whole send, asked again after the
            // token, right before the command (repair round 2).
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(send, @"\btheRadio\b").Count);
            Assert.Contains("var r = theRadio;", send, StringComparison.Ordinal);
            int recheck = send.IndexOf("if (!StillTheRegistrationTarget(r))", StringComparison.Ordinal);
            int command = send.IndexOf("r.WanRegisterRadio(jwt)", StringComparison.Ordinal);
            int token = send.IndexOf("GetJwtFromSavedAccount(account)", StringComparison.Ordinal);
            Assert.True(token > 0 && recheck > token && command > recheck,
                "The radio must be asked again after the token and before the command.");
            Assert.Contains("GetJwtFromSavedAccount(account)", send, StringComparison.Ordinal);
            Assert.Contains("SendRegistrationCommand(register, confirmed, onStateChange, attempt: 2)", send, StringComparison.Ordinal);
            Assert.DoesNotContain("_currentAccount", send, StringComparison.Ordinal);

            // And Radio Setup sends the check its confirmation named.
            string setup = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs"));
            Assert.Contains("StartRegistration(register: true, check);", setup, StringComparison.Ordinal);
            Assert.Contains("StartRegistration(register: false, check);", setup, StringComparison.Ordinal);
            Assert.Contains("_rig.BeginSmartLinkRegistration(confirmed, OnState)", setup, StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Could not find the repository root above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string Body(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is not where this test reads it.");
            int open = text.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            Assert.Fail("The body of " + signature + " never closes.");
            return "";
        }
    }
}

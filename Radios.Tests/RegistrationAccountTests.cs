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
            Assert.Contains("_registrationAccount = account;", preflight, StringComparison.Ordinal);

            Assert.Contains("ResolveRegistrationAccount(serial)", query, StringComparison.Ordinal);
            Assert.DoesNotContain("TryLoadSavedAccount()", query, StringComparison.Ordinal);

            Assert.Contains("_registrationAccount ?? _currentAccount", send, StringComparison.Ordinal);
            Assert.Contains("GetJwtFromSavedAccount(account)", send, StringComparison.Ordinal);
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

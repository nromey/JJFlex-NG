#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;

namespace Radios.SmartLink
{
    /// <summary>
    /// Sprint 35 Track K — #259: SmartLink presence, push not poll.
    ///
    /// <para>
    /// LAN presence is continuous (a discovery packet a second); SmartLink
    /// presence was being thrown away. The server pushes an updated radio list
    /// for as long as a registered session lives — the 2026-08-25 capture shows
    /// four pushes over 145 seconds on one session — but we held ONE session at
    /// a time and dropped it on every refresh, so with three saved accounts two
    /// thirds of the roster was always a guess ("offline, last seen six days
    /// ago" meant only "we stopped asking six days ago").
    /// </para>
    ///
    /// <para>
    /// This service holds one <see cref="IWanSessionOwner"/> per saved account
    /// that can sign in silently, for the life of the app once remote operation
    /// has been engaged. Each session keeps ITSELF registered
    /// (<see cref="IWanSessionOwner.EnableAutoRegistration"/>), reconnects with
    /// backoff on drops, and refreshes its account's token just-in-time — so
    /// rows update from pushes exactly as LAN rows update from discovery
    /// packets, for every account at once.
    /// </para>
    ///
    /// <para><b>Constraints honoured:</b>
    /// silent (#85 — background sessions never take the foreground and never
    /// raise a sign-in form; the JWT provider returns null instead), never
    /// reordering (#254 — this layer only feeds the same RadioFound/RadioRemoved
    /// events LAN discovery feeds; row order is the dialog's discipline), and
    /// tokens kept valid by refreshing on every (re)registration rather than
    /// trusting a stored expiry.
    /// </para>
    ///
    /// <para><b>The cost, honestly:</b> N saved accounts means N persistent TLS
    /// connections to smartlink.flexradio.com. At the typical two or three this
    /// is negligible — SmartSDR holds one all day; we hold one per account.
    /// Accounts that cannot sign in silently (no refresh token, cleared
    /// sign-in) are skipped rather than churned, so the count is bounded by
    /// accounts the operator has actually signed in on this machine. Nothing
    /// connects before the operator's first remote action of the session —
    /// a LAN-only operator's app never dials out.
    /// </para>
    ///
    /// <para><b>Where the switch-on point lives, and why that is the whole of
    /// #382.</b> This machinery was correct from the day it shipped and was
    /// reachable from exactly one place: the end of a completed remote pass.
    /// So a cold roster could not be live until the operator had already
    /// connected somewhere remote — the one moment the roster existed to help
    /// with. Opening the radio chooser with an account-bound row in it is now
    /// also a switch-on point, because standing in front of a list of remote
    /// radios deciding which to use IS a remote action. The guarantee above is
    /// untouched: the condition tests the ROSTER, so a LAN-only operator has
    /// nothing to trigger it.
    /// </para>
    /// </summary>
    public static class SmartLinkPresenceService
    {
        private static readonly System.Threading.Lock _gate = new();
        private static bool _engaged;
        private static string _programName = "";

        /// <summary>Accounts we are currently holding a session for. Rebuilt on
        /// every ensure pass — see <see cref="HeldAccounts"/>.</summary>
        private static readonly HashSet<string> _heldAccounts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Produces a JWT for an account silently, or null when it would take
        /// UI. Args: the account, and whether to force a token refresh (the
        /// registration-invalid recovery path). Wired by FlexBase to its
        /// silent-JWT core so there is exactly ONE token recipe in the app.
        /// </summary>
        public static Func<SmartLinkAccount, bool, string?>? SilentJwtHook { get; set; }

        /// <summary>
        /// Source of the CURRENT saved accounts — resolved on every ensure
        /// pass, never cached, so accounts added or reset mid-session are
        /// honoured. Wired by FlexBase to the shared account manager.
        /// </summary>
        public static Func<IReadOnlyList<SmartLinkAccount>>? AccountsHook { get; set; }

        /// <summary>True once presence sessions have been engaged this run.</summary>
        public static bool Engaged
        {
            get { lock (_gate) return _engaged; }
        }

        /// <summary>
        /// The accounts this service is holding a session for right now — the
        /// ones we have actually asked about.
        ///
        /// <para><b>This is deliberately NOT "the accounts with a live
        /// session".</b> That question is already answered by the coordinator's
        /// session table, and the two answers differ in exactly the window that
        /// matters: between engaging and the TLS session coming up, an account
        /// is held but not live. A consumer that can only see "live or not" has
        /// to render that window as an absence — which is how a row would flash
        /// "not signed in" on its way to a verdict, asserting the very thing
        /// #340 removed, one state earlier.</para>
        ///
        /// <para>An account with no silent sign-in material is NEVER added, so
        /// a row bound to a cleared sign-in still reads "not signed in"
        /// immediately, which is true and is the one case the operator can act
        /// on.</para>
        /// </summary>
        public static IReadOnlyCollection<string> HeldAccounts
        {
            get { lock (_gate) return _heldAccounts.ToArray(); }
        }

        /// <summary>True when a session is being held for this account.</summary>
        public static bool IsHolding(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            lock (_gate) return _heldAccounts.Contains(email!);
        }

        /// <summary>
        /// Hold a session open for every saved account that can sign in
        /// silently. Idempotent and cheap to re-call: existing sessions are
        /// left exactly as they are, new accounts get new sessions, and
        /// accounts that lost their tokens are skipped (their session, if any,
        /// keeps its own retry cadence). Never activates a session — the
        /// active pointer belongs to the connect flow.
        /// </summary>
        public static void EnsureHeldSessions(string programName)
        {
            var accountsHook = AccountsHook;
            var jwtHook = SilentJwtHook;
            if (accountsHook == null || jwtHook == null)
            {
                Tracing.TraceLine("SmartLinkPresence: hooks not wired — presence not engaged", TraceLevel.Warning);
                return;
            }

            lock (_gate)
            {
                _engaged = true;
                if (!string.IsNullOrWhiteSpace(programName)) _programName = programName;
            }

            IReadOnlyList<SmartLinkAccount> accounts;
            try
            {
                accounts = accountsHook() ?? Array.Empty<SmartLinkAccount>();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SmartLinkPresence: accounts hook threw: {ex.Message}", TraceLevel.Error);
                return;
            }

            // Built fresh and swapped in at the end of the pass, never mutated
            // in place. An account DELETED from the manager never reaches the
            // skip branch below, so an incremental set would keep reporting it
            // as held forever — and a row bound to it would say we are signing
            // in to an address that no longer exists. Rebuilding makes the set
            // exactly what this pass found, which is the only thing it claims.
            var nowHeld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int held = 0, skipped = 0;
            foreach (var account in accounts)
            {
                if (account == null || string.IsNullOrWhiteSpace(account.Email)) continue;

                // An account with no refresh token and no id_token cannot sign
                // in silently; a session for it would fail its registration
                // every 30 seconds forever. Skip it — the next interactive
                // sign-in re-runs this pass and picks it up.
                if (string.IsNullOrEmpty(account.RefreshToken) && string.IsNullOrEmpty(account.IdToken))
                {
                    // Left out of nowHeld, so a sign-in cleared since the last
                    // pass stops being reported as held — a row bound to it must
                    // say "not signed in", which is true and is the one state
                    // here the operator can act on.
                    skipped++;
                    Tracing.TraceLine($"SmartLinkPresence: skipping {account.Email} — no silent sign-in material", TraceLevel.Info);
                    continue;
                }

                try
                {
                    var session = SmartLinkServices.Coordinator.GetOrCreateSession(account.Email);

                    // Capture the EMAIL, not the account instance: sign-in
                    // flows replace the instance in the shared manager, and a
                    // captured stale instance would keep offering dead tokens.
                    string email = account.Email;
                    session.EnableAutoRegistration(
                        forceRefresh =>
                        {
                            var current = ResolveAccount(email);
                            if (current == null)
                            {
                                Tracing.TraceLine($"SmartLinkPresence: account {email} no longer saved — cannot register", TraceLevel.Warning);
                                return null;
                            }
                            return SilentJwtHook?.Invoke(current, forceRefresh);
                        },
                        CurrentProgramName());

                    session.Connect();
                    nowHeld.Add(account.Email);
                    held++;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"SmartLinkPresence: could not hold session for {account.Email}: {ex.Message}", TraceLevel.Error);
                }
            }

            lock (_gate)
            {
                _heldAccounts.Clear();
                foreach (var e in nowHeld) _heldAccounts.Add(e);
            }

            Tracing.TraceLine(
                $"SmartLinkPresence: holding {held} session(s), one per silently-signable account" +
                (skipped > 0 ? $"; {skipped} account(s) skipped pending sign-in" : ""),
                TraceLevel.Info);
        }

        private static string CurrentProgramName()
        {
            lock (_gate) return _programName;
        }

        private static SmartLinkAccount? ResolveAccount(string email)
        {
            try
            {
                var accounts = AccountsHook?.Invoke();
                return accounts?.FirstOrDefault(a =>
                    a != null && string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SmartLinkPresence: account resolve threw: {ex.Message}", TraceLevel.Warning);
                return null;
            }
        }
    }
}

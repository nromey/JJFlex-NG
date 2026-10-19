#define CWMonitor
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, SmartLink registration, and what the radio and SmartLink actually report about reachability.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        #region SmartLink registration

        /// <summary>
        /// Result of validating whether this radio can be registered to a SmartLink
        /// account right now.
        /// </summary>
        public sealed class RegistrationCheck
        {
            public bool CanProceed { get; set; }
            public string BlockReason { get; set; } = string.Empty;
            public List<string> Warnings { get; } = new List<string>();
            /// <summary>Account the radio would be registered to.</summary>
            public string AccountEmail { get; set; } = string.Empty;
        }

        /// <summary>
        /// The radio's SmartLink ownership handshake state, in plain language.
        ///
        /// "WaitingForPTT" is the one that matters: the radio is asking for proof
        /// that a human is standing at it, and it will sit there until someone keys
        /// the mic or the attempt times out. That requirement is Flex's, not ours,
        /// and it is the reason a radio must be registered before it ships anywhere.
        /// </summary>
        public string RegistrationStateText
        {
            get
            {
                if (theRadio == null) return "No radio connected.";
                try
                {
                    return theRadio.WanOwnerHandshakeStatus switch
                    {
                        Radio.WanRadioRegistrationState.Undefined =>
                            "Not started. JJ Flex cannot tell from here whether this radio is already registered — if it shows up in your SmartLink radio list, it is.",
                        Radio.WanRadioRegistrationState.WaitingOnSmartLinkConnection =>
                            "The radio is contacting SmartLink.",
                        Radio.WanRadioRegistrationState.WaitingForPTT =>
                            "The radio is waiting for you to key the microphone or the CW key. Do that now, at the radio.",
                        Radio.WanRadioRegistrationState.WaitingOnServerConfirmation =>
                            "Keyed. Waiting for SmartLink to confirm.",
                        Radio.WanRadioRegistrationState.RegisterSuccess =>
                            "Registered. This radio is now tied to your SmartLink account, so you can connect to it when you're away from home.",
                        Radio.WanRadioRegistrationState.UnregisterSuccess =>
                            "Unregistered. This radio is no longer tied to a SmartLink account.",
                        Radio.WanRadioRegistrationState.FailedPTT =>
                            "Failed — the radio did not see the microphone or key pressed in time. Try again and key it as soon as you are asked.",
                        Radio.WanRadioRegistrationState.FailedServerConnection =>
                            "Failed — the radio could not reach SmartLink. Check that the radio has a working internet connection.",
                        Radio.WanRadioRegistrationState.FailedServerConfirmation =>
                            "Failed — SmartLink did not confirm the registration.",
                        Radio.WanRadioRegistrationState.FailedNotLicensed =>
                            "Failed — this radio is not licensed for SmartLink.",
                        Radio.WanRadioRegistrationState.FailedUnknown =>
                            "Failed, with no reason given. See the trace file.",
                        _ => "Unknown.",
                    };
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"RegistrationStateText: {ex.Message}", TraceLevel.Error);
                    return "Unknown.";
                }
            }
        }

        /// <summary>True once the handshake has reported success this session.</summary>
        public bool RegistrationSucceeded
        {
            get
            {
                try
                {
                    return theRadio != null
                        && theRadio.WanOwnerHandshakeStatus == Radio.WanRadioRegistrationState.RegisterSuccess;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Check whether registration can be attempted, without attempting it.
        ///
        /// Registration is sent on the radio's own command channel and the radio
        /// then reaches out to SmartLink itself, so it needs a live local
        /// connection plus a SmartLink account to register *to*. Doing it over
        /// SmartLink would be circular — you cannot connect that way until the
        /// radio is registered.
        /// </summary>
        public RegistrationCheck PreflightSmartLinkRegistration()
        {
            var check = new RegistrationCheck();

            if (theRadio == null || !IsConnected)
            {
                check.BlockReason = "No radio is connected.";
                return check;
            }

            if (theRadio.IsWan)
            {
                check.BlockReason =
                    "You are connected over SmartLink, which means this radio is already registered. " +
                    "Registration has to be done from the same local network as the radio.";
                return check;
            }

            if (!TryLoadSavedAccount())
            {
                // Two different problems, two different fixes. With several
                // accounts saved and none chosen, "sign in first" is false
                // advice — the operator is signed in, possibly twice; what is
                // missing is a decision, and re-signing-in does not make one.
                check.BlockReason = AccountManager.Accounts.Count == 0
                    ? "No SmartLink account is signed in. Sign in to SmartLink first — the radio is registered to an account, " +
                      "and JJ Flex needs to know which one."
                    : "Several SmartLink accounts are saved and none is set as the default. Open Manage SmartLink Accounts " +
                      "and choose a default account — the radio is registered to an account, and JJ Flex needs to know " +
                      "which one to check against.";
                return check;
            }

            check.AccountEmail = _currentAccount.Email;

            var others = OtherConnectedStations;
            if (others.Count > 0)
                check.Warnings.Add("Other stations are connected to this radio: " + string.Join(", ", others));
            // Only clients the radio has confirmed are named above; the ones a
            // list or a broadcast reported are named here instead, as possibly
            // affected, and never in both (#634, Track L7).
            var companyCaveat = UnconfirmedCompanyCaveat;
            if (companyCaveat != null) check.Warnings.Add(companyCaveat);

            check.Warnings.Add(
                "The radio will ask you to key the microphone or the CW key to prove someone is standing at it. " +
                "That check is required by FlexRadio and cannot be skipped or done remotely — which is why a radio " +
                "must be registered before it is shipped anywhere.");

            string jacks = PhysicalKeyingGuidance();
            if (jacks != null)
                check.Warnings.Add(jacks);

            check.CanProceed = true;
            return check;
        }

        /// <summary>
        /// Tactile, model-specific directions for plugging in a microphone or
        /// CW key well enough to key the radio — for the registration
        /// proof-of-presence and for first PTT generally. "Key the mic" is an
        /// instruction that assumes eyes; this is the app doing the looking.
        ///
        /// FLEX-8400/8600 facts verified against the FlexRadio FLEX-8000
        /// Hardware Reference Manual v1.0 (11/2024), sections 1.4, 19.4-19.5,
        /// 20.6, including the rear-panel photographs: standard models have a
        /// bare front panel (power button only); MIC, KEY, PHONES and PTT are
        /// all rear-panel; the MIC jack carries NO PTT — the FHM-3's PTT
        /// button only works with its second, RCA plug in the PTT jack.
        ///
        /// Returns null for models we have not physically verified (6000
        /// series, Aurora): wrong tactile directions are worse than none.
        /// Aurora is "based on" the 8400/8600 but its panel has not been
        /// confirmed — do not fold it in without checking its manual.
        /// </summary>
        private string PhysicalKeyingGuidance()
        {
            string model = RadioModel ?? string.Empty;
            if (!model.Contains("8400") && !model.Contains("8600"))
                return null;

            return
                $"Where to plug in on a {model}: every jack is on the rear panel — the front has only the power button. " +
                "Facing the back, find the VGA-style accessory connector with its two screw posts, right of center; it is " +
                "the easiest landmark to identify by touch. Just left of it is a square of four identical small jacks in " +
                "two columns of two. The microphone jack is the bottom jack of the column nearest the accessory " +
                "connector, and the CW key jack is directly above it. " +
                "The hand mic that came with the radio has two plugs, and both matter: the small plug goes into that " +
                "microphone jack, and the RCA plug goes into the push-to-talk jack — the top-left RCA in the block of " +
                "eight RCA jacks just right of the accessory connector. Without the RCA plug, the mic's PTT button " +
                "does nothing at all. " +
                "A CW key or paddle plugged into the key jack also works for this, and needs only its one plug.";
        }

        /// <summary>Answer to "can this operator reach the connected radio through SmartLink?"</summary>
        public enum SmartLinkRegistrationQuery
        {
            /// <summary>Cannot be determined — accounts exist but none is signed in, or SmartLink unreachable.</summary>
            Unknown,
            /// <summary>
            /// The radio is registered and reachable NOW: we arrived over
            /// SmartLink, or the serial is in a CURRENT account list — one the
            /// server pushed during this query, or the latest list held by a
            /// session that is connected now, pushed on its live connection. A
            /// radio the broker will route to
            /// is a radio registered to that account at that moment.
            ///
            /// <para>Present tense on purpose. Every caller reads it as a claim
            /// about now, and a listing only proves the moment it was sent — a
            /// radio can be unregistered, and a new registration replaces an
            /// old account's (#619). A list held by a session that has since
            /// disconnected never produces this, and neither does one a
            /// reconnected session carried over from its previous
            /// connection.</para>
            /// </summary>
            Registered,
            /// <summary>
            /// A SmartLink server answered and this radio was not in the list.
            /// <b>That is not a finding of non-registration</b>, and the name
            /// says so deliberately — it was <c>NotRegistered</c> until
            /// 2026-09-23, and one word did real damage.
            ///
            /// <para>The list is what the broker can route to right now. A
            /// radio leaves it when it is powered off, when its own path to the
            /// internet is down, when SmartLink is switched off at the radio,
            /// and when it was never registered. The application says the
            /// honest version of this on the Remote path already — "the remote
            /// radio may be turned off" — and said the dishonest version here,
            /// from the same push, 660 ms apart (#352).</para>
            /// </summary>
            NotInAccountList,
            /// <summary>
            /// No SmartLink account has ever been saved on this computer — the
            /// virgin-radio case. Distinct from Unknown so a caller can suggest
            /// setting SmartLink up instead of staying silent: silence is right
            /// when the server is unreachable, wrong when the user has simply
            /// never been told SmartLink exists.
            /// </summary>
            NoAccount,
        }

        /// <summary>
        /// Find out whether this operator can reach the connected radio through
        /// SmartLink — and, when the answer is no, say honestly how good that
        /// "no" is.
        ///
        /// <para>The radio itself cannot answer this — probed 2026-08-03: there
        /// is no wan-status query command, the discovery packet carries nothing,
        /// and FlexLib's WanRadioAuthenticated is dead vendor code that nothing
        /// assigns. The only thing there is to read is the SmartLink server's
        /// per-account list of radios it can route to, which is also how
        /// SmartSDR knows. <b>That list is not a registration roster</b>, so
        /// this returns <see cref="SmartLinkRegistrationQuery.NotInAccountList"/>
        /// and never a finding of non-registration; see
        /// <see cref="SmartLinkRegistrationEvidence"/> for the whole
        /// argument.</para>
        ///
        /// <para><b>A held list may answer yes only if it is current; it may
        /// never answer no.</b> A serial in the latest list of a session that
        /// is connected now, pushed on the connection that is live now,
        /// answers Registered without touching the network. A serial in a
        /// list held by a session that has disconnected is history — the radio
        /// was listed then, which says nothing certain about now — and so is
        /// one a reconnected session carried over from its previous connection
        /// before the new one sent its own. Both are set aside and the query
        /// goes and asks, exactly as it does for a serial missing from a held
        /// list (#619). An
        /// absence is reported only from a list the server pushed during this
        /// call. Until 2026-09-23 none of this held, and a cached empty list
        /// produced a permanent wrong answer in under a millisecond
        /// (#352).</para>
        ///
        /// <para><b>Every account we hold, not just the signed-in one</b>
        /// (#352). SmartLinkPresence keeps one live session per silently
        /// signable account and each carries its own AvailableRadios, so
        /// consulting all of them costs nothing and turns "not registered to
        /// the account you happen to be using" into "registered, under this
        /// other account of yours".</para>
        ///
        /// <para>Network-bound: may take several seconds when the session has to
        /// be established. Callers should treat it as background work, and treat
        /// Unknown as "say nothing" — a suggestion built on a guess is worse
        /// than no suggestion.</para>
        /// </summary>
        public Task<SmartLinkRegistrationEvidence.Finding> QuerySmartLinkRegistrationAsync()
        {
            var serial = theRadio?.Serial;
            if (string.IsNullOrEmpty(serial) || !IsConnected)
            {
                Tracing.TraceLine("QuerySmartLinkRegistration: no radio connected", TraceLevel.Info);
                return Task.FromResult(SmartLinkRegistrationEvidence.Finding.Unknown());
            }

            // Connected over SmartLink is proof of registration by itself.
            if (theRadio.IsWan)
                return Task.FromResult(SmartLinkRegistrationEvidence.Judge(
                    serial, arrivedOverSmartLink: true, anySavedAccount: true,
                    anAccountIsInHand: true, listsInHand: null));

            // A local connection never loads the account on its own — fall back
            // to the saved one so the query can give a real answer instead of
            // an Unknown that silences the advisory.
            TryLoadSavedAccount();
            var account = _currentAccount;
            if (account == null)
            {
                bool anySaved = SmartLinkAccountManager.AnySavedAccounts();
                Tracing.TraceLine($"QuerySmartLinkRegistration: no current account, savedAccounts={anySaved}", TraceLevel.Info);
                return Task.FromResult(SmartLinkRegistrationEvidence.Judge(
                    serial, arrivedOverSmartLink: false, anySavedAccount: anySaved,
                    anAccountIsInHand: false, listsInHand: null));
            }

            // Positive evidence already in hand — the latest list of a session
            // that is connected now, pushed on its live connection, CONTAINS
            // this serial. Answer without touching the network. A disconnected
            // session's list is history, and so is one a reconnected session
            // carried over from its previous connection; the judge sets both
            // aside. Absence in any held list is not an observation. All three
            // fall through to the ask below. Only held
            // lists are passed here, never this instance's captured push,
            // because that capture belongs to whichever ConnectToSmartLink ran
            // last and not to this call.
            var fromCache = SmartLinkRegistrationEvidence.Judge(
                serial, arrivedOverSmartLink: false, anySavedAccount: true,
                anAccountIsInHand: true, listsInHand: HeldAccountLists());
            if (fromCache.Verdict == SmartLinkRegistrationQuery.Registered)
            {
                Tracing.TraceLine(
                    $"QuerySmartLinkRegistration: {serial} is in a list already held"
                    + (fromCache.ListedUnderAccount.Length > 0 ? $" for {fromCache.ListedUnderAccount}" : "")
                    + " — registered, no round trip needed",
                    TraceLevel.Info);
                return Task.FromResult(fromCache);
            }

            return Task.Run(() =>
            {
                try
                {
                    // Silent only: this runs unprompted at connect time (and from
                    // Radio Setup's status refresh). If the token cannot be
                    // refreshed without a login page, the answer is Unknown —
                    // never interrupt the user with auth UI they did not ask for.
                    // markUsed: false — this is background bookkeeping, not the
                    // operator using the account, and it must not perturb the
                    // LastUsed ordering shown in the account manager.
                    string jwt = GetJwtFromSavedAccount(account, allowInteractiveLogin: false, markUsed: false);
                    if (string.IsNullOrEmpty(jwt))
                    {
                        Tracing.TraceLine("QuerySmartLinkRegistration: no JWT available silently", TraceLevel.Info);
                        return SmartLinkRegistrationEvidence.Finding.Unknown();
                    }

                    return AskSmartLinkAboutSerial(serial, account.Email, jwt);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"QuerySmartLinkRegistration: {ex.Message}", TraceLevel.Error);
                    return SmartLinkRegistrationEvidence.Finding.Unknown();
                }
            });
        }

        /// <summary>
        /// Run the SmartLink connect flow for <paramref name="askedAccount"/>
        /// and judge <paramref name="serial"/> against what came back — the
        /// network half of <see cref="QuerySmartLinkRegistrationAsync"/>, split
        /// out so the suite can drive the real connect flow, replay and all,
        /// against a mock server (#619).
        /// </summary>
        /// <remarks>
        /// <para><b>Whether a server answered is read from where the list came
        /// from, not from whether the connect flow succeeded.</b> The flow can
        /// return Success on three kinds of list: one the server pushed during
        /// this call, a held copy it replayed through the intake, and a list it
        /// rebuilt from <c>myRadioList</c>, which is this client's own
        /// bookkeeping. Only the first is the server speaking. Track L told
        /// them apart with the <c>wanListReceived</c> latch, and the replay
        /// sets that latch — so a replayed list read as a live answer and could
        /// produce the negative verdict from held state, which is exactly what
        /// the design forbids. The push is now captured at the one entry point
        /// that only a server push reaches, and the replay cannot reach it.</para>
        /// </remarks>
        internal SmartLinkRegistrationEvidence.Finding AskSmartLinkAboutSerial(
            string serial, string askedAccount, string jwt)
        {
            var result = ConnectToSmartLink(jwt);

            var lists = new List<SmartLinkRegistrationEvidence.AccountList>(HeldAccountLists());
            SmartLinkRegistrationEvidence.AccountList? pushed;
            lock (_wanIntakeLock) { pushed = _serverListThisCall; }
            // The capture belongs to the account the connect flow waited on. A
            // picker switch between the push and this read would otherwise
            // answer one account's question with another's list — the #342
            // shape in a place the #342 guard cannot see.
            if (pushed is { } p
                && string.Equals(p.Account, askedAccount, StringComparison.OrdinalIgnoreCase))
                lists.Add(p);

            var finding = SmartLinkRegistrationEvidence.Judge(
                serial, arrivedOverSmartLink: false, anySavedAccount: true,
                anAccountIsInHand: true, listsInHand: lists);

            Tracing.TraceLine(
                $"QuerySmartLinkRegistration: {serial} -> {finding.Verdict}"
                + $" (connect result={result}, server list this call={(pushed.HasValue ? "yes" : "no")}"
                + $", live={finding.FromALiveServerAnswer}, accounts consulted={finding.AccountsConsulted})",
                TraceLevel.Info);
            return finding;
        }

        /// <summary>
        /// The list each held SmartLink session is carrying, labelled by
        /// whether it arrived on a connection that is live now — see
        /// <see cref="SmartLinkRegistrationEvidence.HeldLists"/>.
        /// </summary>
        /// <remarks>
        /// Every entry here is positive evidence only. Absence from the union
        /// of these lists is never a conclusion — see
        /// <see cref="SmartLinkRegistrationEvidence.Judge"/>. A sweep that fails
        /// leaves the lists already gathered; it can only ever remove positive
        /// evidence, never manufacture an absence.
        /// </remarks>
        private static IReadOnlyCollection<SmartLinkRegistrationEvidence.AccountList> HeldAccountLists()
        {
            try
            {
                return SmartLinkRegistrationEvidence.HeldLists(
                    Radios.SmartLink.SmartLinkServices.Coordinator.AllSessions);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"HeldAccountLists: held-session sweep failed: {ex.Message}", TraceLevel.Warning);
                return Array.Empty<SmartLinkRegistrationEvidence.AccountList>();
            }
        }

        /// <summary>
        /// Register this radio to the signed-in SmartLink account.
        ///
        /// Progress arrives through the radio's WanOwnerHandshakeStatus property, so
        /// the caller supplies a callback that fires on every state change with the
        /// text from <see cref="RegistrationStateText"/>. The subscription is dropped
        /// once the handshake reaches any terminal state.
        /// </summary>
        /// <returns>False if the command could not be sent at all.</returns>
        public bool BeginSmartLinkRegistration(Action<string, bool> onStateChange)
            => SendRegistrationCommand(register: true, onStateChange);

        /// <summary>
        /// Remove this radio's SmartLink registration.
        ///
        /// Dangerous in a way that is not obvious: re-registering requires physically
        /// keying the radio, so unregistering a radio you cannot reach strands it —
        /// it can never be reached over SmartLink again without someone travelling to
        /// it. Callers must warn about this in the strongest terms they have.
        /// </summary>
        public bool BeginSmartLinkUnregistration(Action<string, bool> onStateChange)
            => SendRegistrationCommand(register: false, onStateChange);

        private bool SendRegistrationCommand(bool register, Action<string, bool> onStateChange, int attempt = 1)
        {
            if (theRadio == null || !IsConnected || _currentAccount == null) return false;
            // Registration binds the radio to an ACCOUNT and persists in the
            // radio. Both directions — register and unregister — change what
            // the radio keeps, so both sit inside the hold (audit 2.9).
            if (GuardRefuses("settings.guard.action.registration")) return false;

            try
            {
                var r = theRadio;

                string jwt = GetJwtFromSavedAccount(_currentAccount);
                if (string.IsNullOrEmpty(jwt))
                {
                    Tracing.TraceLine("SendRegistrationCommand: no JWT available", TraceLevel.Error);
                    return false;
                }

                System.ComponentModel.PropertyChangedEventHandler handler = null;
                handler = (s, e) =>
                {
                    if (e.PropertyName != "WanOwnerHandshakeStatus") return;

                    var state = r.WanOwnerHandshakeStatus;
                    bool terminal =
                        state == Radio.WanRadioRegistrationState.RegisterSuccess
                        || state == Radio.WanRadioRegistrationState.UnregisterSuccess
                        || state == Radio.WanRadioRegistrationState.FailedPTT
                        || state == Radio.WanRadioRegistrationState.FailedServerConnection
                        || state == Radio.WanRadioRegistrationState.FailedServerConfirmation
                        || state == Radio.WanRadioRegistrationState.FailedNotLicensed
                        || state == Radio.WanRadioRegistrationState.FailedUnknown;

                    Tracing.TraceLine($"SendRegistrationCommand: state={state} terminal={terminal} attempt={attempt}", TraceLevel.Info);

                    // The SmartLink server itself is flaky: live run 2026-08-04,
                    // its first registration attempt was refused and the second
                    // (identical) one worked. Retry ONCE on the two server-side
                    // failures only — FailedPTT and FailedNotLicensed are radio-
                    // or account-definitive and repeating them cannot succeed.
                    bool serverSideFailure =
                        state == Radio.WanRadioRegistrationState.FailedServerConnection
                        || state == Radio.WanRadioRegistrationState.FailedServerConfirmation;
                    if (terminal && serverSideFailure && attempt == 1)
                    {
                        r.PropertyChanged -= handler;
                        Tracing.TraceLine($"SendRegistrationCommand: server-side failure ({state}), retrying once", TraceLevel.Warning);
                        onStateChange?.Invoke("The SmartLink server refused the first attempt. Trying again.", false);
                        Task.Run(() =>
                        {
                            Thread.Sleep(2000);
                            // Re-entering fetches a fresh JWT — SmartLink id_tokens
                            // live 60 seconds, so the first one may already be dead.
                            if (!SendRegistrationCommand(register, onStateChange, attempt: 2))
                                onStateChange?.Invoke("The retry could not be sent. See the trace file for details.", true);
                        });
                        return;
                    }

                    onStateChange?.Invoke(RegistrationStateText, terminal);

                    if (terminal) r.PropertyChanged -= handler;
                };

                r.PropertyChanged += handler;

                if (register) r.WanRegisterRadio(jwt);
                else r.WanUnregisterRadio(jwt);

                Tracing.TraceLine($"SendRegistrationCommand: sent {(register ? "register" : "unregister")} for account {_currentAccount.Email}", TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SendRegistrationCommand: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        #endregion

        #region SmartLink reachability — what the radio and SmartLink actually report

        /// <summary>
        /// True when SmartLink has told us this radio needs a UDP hole-punch,
        /// meaning neither a forwarded port nor UPnP gave it a way in.
        ///
        /// This is the flag that decides whether a hole-punch port gets chosen at
        /// connect time — not the user's tier preference. See
        /// <c>sendRemoteConnect</c> and the note there about
        /// <c>NegotiatedHolePunchPort</c> being a value the client supplies.
        /// </summary>
        public bool RadioRequiresHolePunch
        {
            get { try { return theRadio != null && theRadio.RequiresHolePunch; } catch { return false; } }
        }

        /// <summary>True when the radio reports a forwarded port is in play.</summary>
        public bool RadioPortForwardActive
        {
            get { try { return theRadio != null && theRadio.IsPortForwardOn; } catch { return false; } }
        }

        /// <summary>The public TCP port SmartLink says the radio is reachable on, or 0.</summary>
        public int RadioPublicTlsPort
        {
            get { try { return theRadio?.PublicTlsPort ?? 0; } catch { return 0; } }
        }

        /// <summary>The public UDP port SmartLink says the radio is reachable on, or 0.</summary>
        public int RadioPublicUdpPort
        {
            get { try { return theRadio?.PublicUdpPort ?? 0; } catch { return 0; } }
        }

        /// <summary>True when the current connection is going through SmartLink.</summary>
        public bool IsWanConnection
        {
            get { try { return theRadio != null && theRadio.IsWan; } catch { return false; } }
        }

        /// <summary>
        /// Serial of the radio most recently connected to, kept after the
        /// connection ends. The network diagnostic needs a serial and a SmartLink
        /// session but not a live radio, so this is what lets it run in the state
        /// it exists for — when the connect failed.
        /// </summary>
        public string SelectedRadioSerial { get; private set; } = string.Empty;

        /// <summary>
        /// The hole-punch port chosen for the most recent remote connect, or 0 if
        /// hole-punch was not used. Recorded so the Network tab can show what
        /// actually happened rather than what was configured — the two differ
        /// whenever the radio did not ask for a hole-punch.
        /// </summary>
        public int LastHolePunchPort { get; private set; }

        // QB Track C: ConfiguredHolePunchPort (=> _currentAccount?.ConfiguredListenPort)
        // was removed here. It read the account listen-port field under its PUNCH
        // meaning; that meaning now lives per-radio (RadioConfig.FixedHolePunchPort)
        // and the account field keeps only the forwarded-port meaning. Its one
        // consumer, the Network tab's account-level punch-port editor, is gone too.

        // ── QB Track D — connect-failure truth ─────────────────────────
        //
        // Why this exists: Don's traces read fwdTcp=False for hours while
        // humans guessed at router rules. The evidence was in hand and the
        // user heard "connection failed". These members hold the composed
        // story of the most recent failed connect so every caller can speak
        // the reason itself instead of a shrug.

        /// <summary>
        /// Classified evidence for the most recent failed connect attempt,
        /// or null when the last attempt succeeded (or none was made).
        /// Reset at the top of every <see cref="Connect(string, bool)"/>.
        /// </summary>
        public ConnectFailureReport? LastConnectFailureReport { get; private set; }

        /// <summary>
        /// The speakable form of <see cref="LastConnectFailureReport"/> —
        /// summary sentence(s) plus the verbatim router rule when the
        /// evidence points at the router. Null when there is nothing to say.
        ///
        /// MERGE SEAM (Track C → Track D, 2026-08-07): Track C's branch
        /// carries a string property of this same name, set only on its
        /// ForwardOnly pre-attempt fail-fast and cleared at Connect() /
        /// sendRemoteConnect() entry. At merge, THIS computed property owns
        /// the name; C's assignment sites become
        /// RecordConnectFailure(new ConnectFailureReport {
        ///   Class = ConnectFailureClass.PreflightRefused,
        ///   SpokenSummary = &lt;C's message text&gt; }).
        /// The Connect()-entry reset already exists here; add the same
        /// reset at sendRemoteConnect() entry if C's contract requires it.
        /// </summary>
        public string? LastConnectFailureAdvice => LastConnectFailureReport?.ComposeSpeech();

        /// <summary>
        /// Record a composed failure report and trace it. Internal seam so
        /// every failure site files its evidence the same way.
        /// </summary>
        internal void RecordConnectFailure(ConnectFailureReport report)
        {
            LastConnectFailureReport = report;
            report.Trace();
        }

        /// <summary>
        /// Cached SmartLink test_connection report for a radio, if one has
        /// been collected this app run. Cache read only — never triggers a
        /// probe, so it is safe to consult from any state including a live
        /// hole-punched session.
        /// </summary>
        public Radios.SmartLink.NetworkDiagnosticReport? LastNetworkReportFor(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return null;
            try
            {
                return Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession?.GetLastNetworkReport(serial);
            }
            catch { return null; }
        }

        /// <summary>
        /// The radio's last-known LAN address from the connection cache, or
        /// null if this machine has never seen it on the LAN. This is what
        /// lets the router-rule text name a real address for a radio we can
        /// currently only reach (or fail to reach) over SmartLink.
        /// </summary>
        public string? CachedLanIpFor(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return null;
            try
            {
                string? lanIp = GetRadioConnectionCache().Lookup(serial)?.LanIp;
                return string.IsNullOrWhiteSpace(lanIp) ? null : lanIp;
            }
            catch { return null; }
        }

        /// <summary>
        /// The exact router rule this radio needs, built entirely from
        /// radio-reported values: advertised external ports, cached LAN IP,
        /// fixed internal TCP 4994 / UDP 4993. Null when the radio has not
        /// advertised external ports (e.g. pure hole-punch site). Public so
        /// any settings / guidance surface can offer the same verbatim text.
        /// </summary>
        public string? BuildRouterRuleText()
        {
            var r = theRadio;
            if (r == null) return null;
            return RouterRuleAdvisor.BuildRouterRuleText(
                r.PublicTlsPort, r.IsWan ? r.PublicUdpPort : 0, CachedLanIpFor(r.Serial));
        }

        /// <summary>
        /// Compose the failure story for a failed REMOTE connect, from the
        /// evidence already in hand plus (when safe) evidence one probe away:
        ///
        /// 1. The SmartLink test_connection report — reachability ground
        ///    truth measured from OUTSIDE the user's network. Cached copy is
        ///    used when present; a fresh probe is fetched only on the
        ///    forwarded path. NEVER auto-run on a hole-punch radio — the
        ///    radio-side probe is at minimum useless there and was implicated
        ///    in killing punched sessions (the f842e93f gate's reasoning).
        /// 2. A client-side TCP classification of the radio's public port —
        ///    refused (router answered, nothing behind the rule) versus
        ///    timed out (packets never arrived) are different diseases with
        ///    different medicine, and both used to be "open failed".
        /// 3. Radio-reported flags (forwarding on, ports, punch required).
        /// </summary>
        private ConnectFailureReport BuildRemoteConnectFailureReport(Radio r, bool remoteHandshakeFailed)
        {
            var detail = new List<string>();
            bool punch = false;
            try { punch = r.RequiresHolePunch; } catch { }
            int tlsPort = 0, udpPort = 0;
            try { tlsPort = r.PublicTlsPort; udpPort = r.PublicUdpPort; } catch { }
            detail.Add($"radio-reported path: {(punch ? "hole punch" : "forwarded ports")}, TCP {tlsPort}, UDP {udpPort}, forwarding flag {(RadioPortForwardActive ? "on" : "off")}");

            string? ruleText = BuildRouterRuleText();

            // Evidence source 1 — SmartLink's own outside-in probe.
            Radios.SmartLink.NetworkDiagnosticReport? probe = LastNetworkReportFor(r.Serial);
            if (probe == null && !punch)
            {
                // Forwarded path with no cached report: fetch one now. The
                // connect already failed, so there is no live session to
                // endanger, and eight seconds of waiting buys the difference
                // between evidence and a shrug. Punch path: never — see the
                // method doc.
                try
                {
                    var session = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;
                    if (session != null && session.IsConnected)
                    {
                        Tracing.TraceLine("BuildRemoteConnectFailureReport: fetching fresh test_connection (forwarded path, no cached report)", TraceLevel.Info);
                        var task = session.RunNetworkDiagnosticAsync(r.Serial, forceRefresh: false, timeout: TimeSpan.FromSeconds(8));
                        if (task.Wait(9000)) probe = task.Result;
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"BuildRemoteConnectFailureReport: probe fetch failed: {ex.Message}", TraceLevel.Warning);
                }
            }
            if (probe != null && probe.ProbeCompleted)
            {
                detail.Add($"SmartLink outside-in test ({probe.TimestampUtc:HH:mm:ss} UTC): forwarded TCP {YesNo(probe.ManualForwardTcpReachable)}, forwarded UDP {YesNo(probe.ManualForwardUdpReachable)}, UPnP TCP {YesNo(probe.UpnpTcpReachable)}, UPnP UDP {YesNo(probe.UpnpUdpReachable)}, NAT hole-punch support {YesNo(probe.NatSupportsHolePunch)}");
            }
            else if (probe != null)
            {
                detail.Add($"SmartLink outside-in test did not complete: {probe.ErrorDetail}");
            }

            // Evidence source 2 — our own TCP classification of the public
            // port. Forwarded path only; a punch radio has no listening
            // public TCP port to classify.
            TcpProbeResult? tcp = null;
            if (!punch && tlsPort > 0 && r.IP != null)
            {
                tcp = TcpReachabilityProbe.Classify(r.IP, tlsPort);
                detail.Add($"client-side TCP check of {r.IP}:{tlsPort}: {tcp.Outcome} after {tcp.ElapsedMs}ms ({tcp.Detail})");
            }

            // Compose the spoken story, most specific evidence first.
            ConnectFailureClass cls;
            string spoken;

            if (punch)
            {
                cls = ConnectFailureClass.TransportFailed;
                spoken = "Could not open a hole-punched connection to the radio. "
                    + "The radio has no forwarded ports, so the connection depends on both routers allowing the punch, and some networks never will.";
                if (probe?.ProbeCompleted == true && probe.NatSupportsHolePunch == false)
                {
                    spoken += " SmartLink's own network test reports this network does not support hole punch — a forwarded port on the radio's router is the reliable path.";
                }
                else
                {
                    spoken += " Trying again sometimes wins the timing; a forwarded port on the radio's router is the reliable fix.";
                }
                // A rule is still worth stating on the punch path when the
                // radio advertises nothing: the fix IS creating the rule.
                // But with no advertised external ports there are no honest
                // numbers to speak, so ruleText stays null unless the radio
                // provided them.
            }
            else if (tcp != null && tcp.Outcome == TcpProbeOutcome.Refused)
            {
                cls = ConnectFailureClass.TransportRefused;
                spoken = $"The radio's router answered on port {tlsPort} and refused the connection after {tcp.ElapsedMs} milliseconds — "
                    + "the port forward reaches the router, but nothing is listening behind the rule. "
                    + "The rule may point at the wrong LAN address, or the radio may be off.";
            }
            else if (tcp != null && (tcp.Outcome == TcpProbeOutcome.TimedOut || tcp.Outcome == TcpProbeOutcome.Unreachable))
            {
                cls = ConnectFailureClass.TransportTimedOut;
                spoken = $"Connection attempts to the radio's public port {tlsPort} got no answer at all — the packets never arrived. "
                    + "That usually means the router rule is missing, a firewall is dropping them, or the radio's public address has changed.";
                if (probe?.ProbeCompleted == true && probe.ManualForwardTcpReachable == false)
                {
                    spoken = $"SmartLink tested the radio's forwarded TCP port from the internet and could not reach it, and this computer's own check got no answer either. "
                        + "The router rule is the likely problem.";
                }
            }
            else if (probe?.ProbeCompleted == true && probe.ManualForwardTcpReachable == false && RadioPortForwardActive)
            {
                cls = ConnectFailureClass.TransportFailed;
                spoken = "The radio reports its forwarded TCP port is not reachable from the internet — SmartLink tested it from outside. Check the router rule.";
            }
            else if (remoteHandshakeFailed)
            {
                cls = ConnectFailureClass.RemoteHandshakeFailed;
                spoken = "SmartLink accepted the request, but the radio never reported ready to connect. The radio may be busy, restarting, or just dropped off the network. Trying again usually works.";
                ruleText = null; // no evidence pointing at the router — don't send anyone there
            }
            else if (tcp != null && tcp.Outcome == TcpProbeOutcome.Connected)
            {
                cls = ConnectFailureClass.TransportFailed;
                spoken = $"The radio's public port {tlsPort} answers from here, so the router rule looks right — the failure happened after the port, most likely in the secure handshake with the radio. Trying again usually works.";
                ruleText = null; // the rule is demonstrably fine
            }
            else
            {
                cls = ConnectFailureClass.TransportFailed;
                spoken = "Could not connect to the radio over SmartLink.";
                if (probe?.ProbeCompleted == true
                    && (probe.ManualForwardTcpReachable == true || probe.UpnpTcpReachable == true))
                {
                    spoken += " SmartLink's network test says the radio's port is reachable from the internet, so this looks like a passing problem — trying again usually works.";
                    ruleText = null;
                }
            }

            return new ConnectFailureReport
            {
                Class = cls,
                SpokenSummary = spoken,
                RouterRuleText = ruleText,
                DetailLines = detail,
                ProbeReport = probe,
                TcpProbe = tcp,
            };
        }

        private static string YesNo(bool? v) => v switch { true => "yes", false => "no", null => "unknown" };

        #endregion
    }
}

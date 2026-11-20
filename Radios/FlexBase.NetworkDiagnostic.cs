#nullable enable

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.SmartLink;

namespace Radios
{
    /// <summary>
    /// FlexBase, the network diagnostic (SmartLink connection tiers, Track C):
    /// which radio it describes, which account's session may test it, and the
    /// three parts the Network tab and Radio Setup step 6 show.
    /// </summary>
    /// <remarks>
    /// <para><b>One radio, one brokering account.</b> Until this, the test ran
    /// through the ACTIVE session and every reader took the newest report for
    /// ANY radio (<c>MostRecentNetworkReport</c>), so with three accounts or
    /// MultiFlex one radio's diagnostic could explain another's. Every read
    /// and every probe here goes through <see cref="NetworkDiagnosticPairingFor"/>
    /// and that radio's serial, and never falls back to the active session.</para>
    /// <para>A separate file so the hub's connect regions carry only the calls.</para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>
        /// The radio the diagnostic describes: the connected radio, else the
        /// one last selected (which is what the picker and Radio Setup name).
        /// Empty when neither is known.
        /// </summary>
        public string DiagnosticRadioSerial
        {
            get
            {
                try { return theRadio?.Serial ?? SelectedRadioSerial ?? ""; }
                catch { return SelectedRadioSerial ?? ""; }
            }
        }

        /// <summary>
        /// The account that brokers <paramref name="serial"/> for the
        /// diagnostic, from the facts Track A's brokering lookup reads, in its
        /// order, WITHOUT its fall back to the active session; and whether that
        /// account's session is signed in. Reads only; signs nothing in.
        /// </summary>
        public DiagnosticPairing NetworkDiagnosticPairingFor(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return DiagnosticPairing.None;
            try
            {
                var planned = _connectPlanAccount;
                string? plannedAccount = !string.IsNullOrEmpty(planned.Account)
                    && string.Equals(planned.Serial, serial, StringComparison.OrdinalIgnoreCase)
                    ? planned.Account : null;
                return NetworkDiagnosticPairing.Resolve(
                    plannedAccount,
                    GetWanAccountForSerial(serial),
                    ListingSessionAccountFor(serial),
                    SmartLinkConnectRecords.For(serial)?.BrokeringAccount,
                    account => SessionIfSignedIn(account) != null);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"NetworkDiagnosticPairingFor({serial}): {ex.Message}", TraceLevel.Warning);
                return DiagnosticPairing.None;
            }
        }

        /// <summary>The account of the session whose SmartLink list put this radio in the bank, or null.</summary>
        private static string? ListingSessionAccountFor(string serial)
        {
            lock (_wanRadiosLock)
            {
                return _wanRadiosBySerial.TryGetValue(serial, out var entry)
                    ? entry.From?.Session?.AccountId
                    : null;
            }
        }

        private static IWanSessionOwner? SessionIfSignedIn(string account)
        {
            var session = SmartLinkServices.Coordinator.GetSessionForAccount(account);
            return session != null && session.IsConnected ? session : null;
        }

        /// <summary>The session of a paired account, or null when the pairing is not paired.</summary>
        private static IWanSessionOwner? PairedSession(DiagnosticPairing pairing) =>
            pairing.IsPaired ? SessionIfSignedIn(pairing.Account) : null;

        /// <summary>
        /// The newest network test report for <paramref name="serial"/> from
        /// its own brokering account's session, or null. Never another radio's
        /// report and never another account's.
        /// </summary>
        public NetworkDiagnosticReport? NetworkReportFor(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return null;
            try { return PairedSession(NetworkDiagnosticPairingFor(serial))?.GetLastNetworkReport(serial); }
            catch { return null; }
        }

        /// <summary>
        /// Everything the diagnostic shows for <paramref name="serial"/>,
        /// gathered. Reads only: the bank, the accounts file as loaded, the
        /// radio's profile, the connect record and the paired session's cache.
        /// </summary>
        public NetworkDiagnosticInputs NetworkDiagnosticInputsFor(string serial)
        {
            serial ??= "";
            var pairing = NetworkDiagnosticPairingFor(serial);
            var record = SmartLinkConnectRecords.For(serial);

            Radio? listed = null;
            try { listed = string.IsNullOrWhiteSpace(serial) ? null : findWanEntry(serial, out _); }
            catch { listed = null; }

            // The tier and chosen flag of the account that brokers the radio,
            // as a connect would read them (AccountWhoseTierGoverns): an
            // account not saved here counts as Tier 1, never chosen.
            SmartLinkAccount? account = pairing.Kind == DiagnosticPairingKind.NoBrokeringAccount
                ? null
                : AccountWhoseTierGoverns(pairing.Account, AccountManager.Accounts, _currentAccount);

            RadioConnectionPreference choice = RadioConnectionPreference.Auto;
            try { if (!string.IsNullOrWhiteSpace(serial)) choice = RadioConfig.LoadForRadio(serial).ConnectionPreference; }
            catch (Exception ex) { Tracing.TraceLine($"NetworkDiagnosticInputsFor({serial}): profile: {ex.Message}", TraceLevel.Warning); }

            RadioPortReport? ports = listed != null ? RadioPortReport.FromRadio(listed) : record?.Report;

            return new NetworkDiagnosticInputs(
                serial,
                string.IsNullOrWhiteSpace(serial) ? "" : RadioNameForSpeech(serial),
                pairing,
                NetworkReportFor(serial),
                listed?.IsPortForwardOn,
                account?.ConnectionMode ?? SmartLinkConnectionMode.ManualPortForwardOnly,
                account?.TierChosen ?? false,
                choice,
                ports,
                PortsFromLastConnect: listed == null && record != null,
                record);
        }

        /// <summary>The diagnostic's three parts for <paramref name="serial"/>.</summary>
        public NetworkDiagnosticSections NetworkDiagnosticFor(string serial) =>
            NetworkDiagnosticComposer.Compose(NetworkDiagnosticInputsFor(serial));

        /// <summary>
        /// What the spoken status overlay for <paramref name="session"/> may
        /// explain itself with: the radio's own last test, only when this
        /// session's account brokers that radio (else null); the tier of THIS
        /// session's account (never the current account by habit); and the
        /// radio's forwarding flag. The tiers work, Track C.
        /// </summary>
        public (NetworkDiagnosticReport? Report, SmartLinkConnectionMode Tier, bool? RadioForwardingOn) SessionOverlayInputs(
            IWanSessionOwner? session)
        {
            try
            {
                string serial = DiagnosticRadioSerial;
                var report = SessionOverlayPairing.ReportFor(session?.AccountId, serial,
                    NetworkDiagnosticPairingFor(serial), s => session?.GetLastNetworkReport(s));
                var tier = AccountWhoseTierGoverns(session?.AccountId, AccountManager.Accounts, _currentAccount)?.ConnectionMode
                    ?? SmartLinkConnectionMode.ManualPortForwardOnly;
                bool? forwarding = report == null ? null : findWanEntry(serial, out _)?.IsPortForwardOn;
                return (report, tier, forwarding);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SessionOverlayInputs: {ex.Message}", TraceLevel.Warning);
                return (null, SmartLinkConnectionMode.ManualPortForwardOnly, null);
            }
        }

        /// <summary>
        /// The help key the Network tab's Help button opens for
        /// <paramref name="serial"/>, from that radio's brokering account: its
        /// session's status, the radio's own last test, that account's tier,
        /// and the radio's forwarding flag. Only keys the help already builds
        /// (<see cref="SessionStatusMessages.HelpDocFor(SessionStatus, NetworkDiagnosticReport?, SmartLinkConnectionMode, bool?)"/>).
        /// </summary>
        public string? NetworkHelpKeyFor(string serial)
        {
            try
            {
                var pairing = NetworkDiagnosticPairingFor(serial);
                bool known = pairing.Kind != DiagnosticPairingKind.NoBrokeringAccount;
                var session = known ? SmartLinkServices.Coordinator.GetSessionForAccount(pairing.Account) : null;
                var tier = known
                    ? AccountWhoseTierGoverns(pairing.Account, AccountManager.Accounts, _currentAccount)?.ConnectionMode
                        ?? SmartLinkConnectionMode.ManualPortForwardOnly
                    : SmartLinkConnectionMode.ManualPortForwardOnly;
                bool? forwarding = string.IsNullOrWhiteSpace(serial) ? null : findWanEntry(serial, out _)?.IsPortForwardOn;
                return SessionStatusMessages.HelpDocFor(
                    session?.Status ?? SessionStatus.Disconnected,
                    session?.GetLastNetworkReport(serial),
                    tier,
                    forwarding);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"NetworkHelpKeyFor({serial}): {ex.Message}", TraceLevel.Warning);
                return SessionStatusMessages.HelpKeyNetworkDiagnostics;
            }
        }

        /// <summary>What happened when the network test was asked for.</summary>
        /// <param name="Gate">The gate's answer; anything but Run sent nothing.</param>
        /// <param name="Serial">The radio asked about.</param>
        /// <param name="Pairing">Its brokering account; anything but Paired sent nothing.</param>
        /// <param name="Report">The test's report, when one was sent.</param>
        public sealed record NetworkTestRun(NetworkTestGateAnswer Gate, string Serial, DiagnosticPairing Pairing,
            NetworkDiagnosticReport? Report);

        /// <summary>
        /// The Network tab's Test network and Radio Setup step 6's check: test
        /// the radio the diagnostic describes, through its own brokering
        /// account's session, if the gate allows. The punched-session warning
        /// is the caller's, before this. Never signs in; never uses another
        /// account's session.
        /// </summary>
        public async Task<NetworkTestRun> RunNetworkTestAsync(bool forceRefresh = true)
        {
            string serial = DiagnosticRadioSerial;
            bool connected = IsConnected && theRadio != null;
            var gate = NetworkTestGate.Decide(connected, serial);
            if (gate != NetworkTestGateAnswer.Run)
            {
                Tracing.TraceLine($"RunNetworkTestAsync: not sent, gate={gate} serial={serial} connected={connected}", TraceLevel.Info);
                return new NetworkTestRun(gate, serial, DiagnosticPairing.None, null);
            }

            var pairing = NetworkDiagnosticPairingFor(serial);
            var session = PairedSession(pairing);
            if (session == null)
            {
                var said = pairing.IsPaired
                    ? pairing with { Kind = DiagnosticPairingKind.AccountNotSignedIn }
                    : pairing;
                Tracing.TraceLine(
                    $"RunNetworkTestAsync: not sent, serial={serial} pairing={said.Kind} account={said.Account} ({said.Source}); no other account's session is used and nothing signs in",
                    TraceLevel.Info);
                return new NetworkTestRun(gate, serial, said, null);
            }

            Tracing.TraceLine(
                $"RunNetworkTestAsync: probing serial={serial} through {pairing.Account} ({pairing.Source}), connected={connected}",
                TraceLevel.Info);
            var report = await session.RunNetworkDiagnosticAsync(serial, forceRefresh).ConfigureAwait(false);
            return new NetworkTestRun(gate, serial, pairing, report);
        }
    }
}

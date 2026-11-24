#nullable enable

using System;

namespace Radios.SmartLink
{
    /// <summary>How the diagnostic found the session that may test a radio.</summary>
    public enum DiagnosticPairingKind
    {
        /// <summary>The brokering account is known and its session is signed in.</summary>
        Paired = 0,
        /// <summary>Nothing names the account that brokers this radio. The test is not run through any other.</summary>
        NoBrokeringAccount = 1,
        /// <summary>The brokering account is known but its session is not signed in. The diagnostic never signs in.</summary>
        AccountNotSignedIn = 2,
    }

    /// <summary>
    /// Which account brokers a radio for the diagnostic, where that answer came
    /// from (for the trace), and whether that account's session is signed in.
    /// </summary>
    public sealed record DiagnosticPairing(DiagnosticPairingKind Kind, string Account, string Source)
    {
        public static readonly DiagnosticPairing None = new(DiagnosticPairingKind.NoBrokeringAccount, "", "none");
        public bool IsPaired => Kind == DiagnosticPairingKind.Paired;
    }

    /// <summary>
    /// The network test and its report belong to the radio's OWN brokering
    /// account (the tiers plan, section 5; settled for Track C): three accounts
    /// and MultiFlex must not cross-explain. Until this, the test ran through
    /// the ACTIVE session and Copy, Save, Radio Setup, Help and the spoken
    /// status overlay read the newest report for ANY radio.
    /// </summary>
    /// <remarks>
    /// <para>The account comes from the same facts Track A's brokering lookup
    /// (#798) reads, in its order: the account this rig's own connect plan
    /// named for the radio; the account the SmartLink bank attributes the radio
    /// to; the account of the session whose list described it; and the account
    /// the last connect to it was brokered through. <b>What it never does is
    /// the connect lookup's last step, falling back to whatever session is
    /// active</b>: when none of those names an account, the answer is
    /// <see cref="DiagnosticPairingKind.NoBrokeringAccount"/> and the test is
    /// not run. When the named account is not signed in, the answer says so;
    /// the diagnostic never starts a sign-in.</para>
    /// </remarks>
    public static class NetworkDiagnosticPairing
    {
        public static DiagnosticPairing Resolve(string? plannedAccount, string? bankOwner,
            string? listingSessionAccount, string? lastConnectAccount, Func<string, bool> accountSignedIn)
        {
            if (accountSignedIn == null) throw new ArgumentNullException(nameof(accountSignedIn));
            (string? account, string source) =
                !string.IsNullOrWhiteSpace(plannedAccount) ? (plannedAccount, "this rig's connect plan")
                : !string.IsNullOrWhiteSpace(bankOwner) ? (bankOwner, "the account whose list owns it")
                : !string.IsNullOrWhiteSpace(listingSessionAccount) ? (listingSessionAccount, "the session whose list described it")
                : !string.IsNullOrWhiteSpace(lastConnectAccount) ? (lastConnectAccount, "the last connect to it")
                : (null, "none");
            if (account == null) return DiagnosticPairing.None;
            return new DiagnosticPairing(
                accountSignedIn(account) ? DiagnosticPairingKind.Paired : DiagnosticPairingKind.AccountNotSignedIn,
                account, source);
        }
    }

    /// <summary>
    /// The spoken status overlay explains one SmartLink session's trouble.
    /// It may use a radio's network test only when that session's account is
    /// the one that brokers the radio, and then only that radio's own test
    /// (the tiers work, Track C). Until this it used the session's newest
    /// test of ANY radio.
    /// </summary>
    public static class SessionOverlayPairing
    {
        /// <param name="sessionAccount">The account of the session whose status changed.</param>
        /// <param name="serial">The radio the diagnostic describes (connected, else selected).</param>
        /// <param name="pairing">That radio's brokering account, signed in or not (a session that is reconnecting is not).</param>
        /// <param name="lastReportOnThatSession">The session's cached test for a serial.</param>
        public static NetworkDiagnosticReport? ReportFor(string? sessionAccount, string? serial,
            DiagnosticPairing pairing, Func<string, NetworkDiagnosticReport?> lastReportOnThatSession)
        {
            if (string.IsNullOrWhiteSpace(sessionAccount) || string.IsNullOrWhiteSpace(serial) || pairing == null)
                return null;
            if (pairing.Kind == DiagnosticPairingKind.NoBrokeringAccount
                || !string.Equals(pairing.Account, sessionAccount, StringComparison.OrdinalIgnoreCase))
                return null;
            return lastReportOnThatSession?.Invoke(serial!);
        }
    }

    /// <summary>What the network test's gate says before anything is sent.</summary>
    public enum NetworkTestGateAnswer
    {
        /// <summary>Go on to the punched-session warning and the pairing.</summary>
        Run = 0,
        /// <summary>No radio of ours is connected, and testing without one is not offered yet.</summary>
        NeedsAConnectedRadio = 1,
        /// <summary>No radio is connected or selected, so there is nothing to test.</summary>
        NoRadio = 2,
    }

    /// <summary>
    /// The network test's gate, shared by the Network tab's Test network and
    /// Radio Setup's step 6 check, and enforced again where the probe is sent.
    /// </summary>
    public static class NetworkTestGate
    {
        /// <summary>
        /// <b>OFF, deliberately (settled for Track C, 2026-11-19).</b> Testing a
        /// radio with none of ours connected is BUILT but not offered. SmartLink's
        /// test is radio-wide: the radio probes its own ports, and on a
        /// hole-punched session that probe has dropped the live connection in
        /// every field test (2026-08-05). With a radio of ours connected, the
        /// punched-session warning covers OUR session; with none connected,
        /// nothing warns before probing a radio another operator may be on, and
        /// this machine's accounts reach radios other people use. The tiers plan
        /// (section 5) makes establishing that effect a precondition of offering
        /// the test more widely. Turning this on is Noel's ruling, not a refactor.
        /// </summary>
        public const bool OfferedWithoutAConnectedRadio = false;

        public static NetworkTestGateAnswer Decide(bool radioConnected, string? serial) =>
            Decide(radioConnected, serial, OfferedWithoutAConnectedRadio);

        /// <summary>As <see cref="Decide(bool, string?)"/>, with the switch given (tests, and the day it is ruled on).</summary>
        public static NetworkTestGateAnswer Decide(bool radioConnected, string? serial, bool offeredWithoutAConnectedRadio)
        {
            if (string.IsNullOrWhiteSpace(serial)) return NetworkTestGateAnswer.NoRadio;
            if (!radioConnected && !offeredWithoutAConnectedRadio) return NetworkTestGateAnswer.NeedsAConnectedRadio;
            return NetworkTestGateAnswer.Run;
        }
    }

    /// <summary>
    /// Follows the connect records for one radio for a window that shows them.
    /// <see cref="SmartLinkConnectRecords.Changed"/> is raised on the storing
    /// thread and its callbacks can arrive out of order (Codex's re-check of
    /// Track A repair round 1), so the callback's record is used only to ask
    /// "is this my radio?". What is shown is re-read from
    /// <see cref="SmartLinkConnectRecords.For"/> when the posted refresh RUNS,
    /// on the window's thread, never taken from the callback.
    /// </summary>
    public sealed class ConnectRecordFollower : IDisposable
    {
        private readonly Func<string?> _watchedSerial;
        private readonly Action<Action> _post;
        private readonly Action<SmartLinkConnectRecord?> _show;
        private bool _disposed;

        /// <param name="watchedSerial">The radio the window describes now, asked at each callback.</param>
        /// <param name="post">Runs an action on the window's thread (a dispatcher's BeginInvoke).</param>
        /// <param name="show">Shows the radio's newest record, read when the posted action runs.</param>
        /// <param name="subscribe">False only for a test that drives <see cref="OnChanged"/> itself.</param>
        public ConnectRecordFollower(Func<string?> watchedSerial, Action<Action> post,
            Action<SmartLinkConnectRecord?> show, bool subscribe = true)
        {
            _watchedSerial = watchedSerial ?? throw new ArgumentNullException(nameof(watchedSerial));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _show = show ?? throw new ArgumentNullException(nameof(show));
            if (subscribe) SmartLinkConnectRecords.Changed += OnChanged;
        }

        /// <summary>The <see cref="SmartLinkConnectRecords.Changed"/> handler.</summary>
        public void OnChanged(SmartLinkConnectRecord changed)
        {
            if (_disposed || changed == null) return;
            string? serial = _watchedSerial();
            if (string.IsNullOrWhiteSpace(serial)
                || !string.Equals(changed.Serial, serial, StringComparison.OrdinalIgnoreCase))
                return;
            _post(() =>
            {
                if (_disposed) return;
                _show(SmartLinkConnectRecords.For(serial!));
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SmartLinkConnectRecords.Changed -= OnChanged;
        }
    }
}

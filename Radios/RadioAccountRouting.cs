using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>Which tier of <see cref="RadioAccountRouting"/> answered.</summary>
    public enum AccountRouteSource
    {
        /// <summary>No tier answered: nothing on record says which account reaches the radio.</summary>
        None,

        /// <summary>A SmartLink session lists the radio on its connection that is up now; the account whose list delivered it.</summary>
        LiveAttribution,

        /// <summary>The operator's deliberate, sticky choice for this radio (<see cref="RadioConfig.PreferredAccount"/>).</summary>
        Preferred,

        /// <summary>The account that last listed this radio, an observation (<see cref="RadioConfig.LastSeenViaAccount"/>).</summary>
        LastSeen,
    }

    /// <summary>
    /// The answer to "which SmartLink account reaches this radio?", and which
    /// tier gave it. <see cref="IsEmpty"/> is a real answer, not a failure:
    /// it means no account is known to reach the radio.
    /// </summary>
    public readonly record struct AccountRoute(string Account, AccountRouteSource Source)
    {
        /// <summary>No account is known to reach the radio.</summary>
        public static AccountRoute None => new AccountRoute("", AccountRouteSource.None);

        /// <summary>True when no tier answered.</summary>
        public bool IsEmpty => string.IsNullOrWhiteSpace(Account);

        public override string ToString() => IsEmpty ? "none" : $"{Account} ({Source})";
    }

    /// <summary>
    /// ONE resolver for the SmartLink account a radio is reached through, so
    /// connect and registration cannot give two answers to "whose radio is
    /// this" again. It is the ROUTING half of the two-resolver design: it
    /// answers which account to go through, and it is never a permission to
    /// write anything to a radio. Consent lives elsewhere
    /// (<see cref="RadioConfig.MayCreateRadioSideState"/>, the per-radio
    /// ownership answer), and an identity answer must not be reused as a
    /// permission answer.
    /// </summary>
    /// <remarks>
    /// <para><b>The order.</b> Live attribution first (the account whose
    /// session's CURRENT list names the serial; never the WAN bank, which can
    /// lag), then the operator's <see cref="RadioConfig.PreferredAccount"/>,
    /// then the observed <see cref="RadioConfig.LastSeenViaAccount"/>, then
    /// empty.</para>
    /// <para><b>This is the route as recorded, not the account signed in
    /// as.</b> A tier may name an account nobody saved. The account actually
    /// used, by the connect's plan and by registration alike, is the first
    /// SAVED account in this same order
    /// (<c>FlexBase.AccountPicture.FirstSaved</c>; integration pass, finding
    /// 5).</para>
    /// <para><b>Resolved from the RADIO, never from a mutable default.</b>
    /// The saved default account is deliberately not a tier: which account
    /// happens to be the default says nothing about whose radio this is. A
    /// caller that gets an empty answer falls back to its own historical path
    /// (for connect and registration, the saved default account) and says so
    /// in its trace; that fallback is a "nothing better is known", not a
    /// gate.</para>
    /// </remarks>
    public static class RadioAccountRouting
    {
        /// <summary>
        /// The tier order, as a pure function so it can be pinned without a
        /// settings tree or a SmartLink session.
        /// </summary>
        public static AccountRoute Resolve(string? liveAccount, string? preferredAccount, string? lastSeenAccount)
        {
            if (!string.IsNullOrWhiteSpace(liveAccount))
                return new AccountRoute(liveAccount!.Trim(), AccountRouteSource.LiveAttribution);
            if (!string.IsNullOrWhiteSpace(preferredAccount))
                return new AccountRoute(preferredAccount!.Trim(), AccountRouteSource.Preferred);
            if (!string.IsNullOrWhiteSpace(lastSeenAccount))
                return new AccountRoute(lastSeenAccount!.Trim(), AccountRouteSource.LastSeen);
            return AccountRoute.None;
        }

        /// <summary>
        /// The account that reaches <paramref name="serial"/>, read now: the
        /// WAN bank's attribution, then the radio's own per-radio record.
        /// Reads only; never writes a settings file.
        /// </summary>
        public static AccountRoute ForSerial(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return AccountRoute.None;

            // One reading of every held session's list and the radio's own
            // record (repair round 2). Live means a session whose list that
            // answers NOW names the serial: neither the WAN bank's attribution,
            // which can lag a newer list on the same connection, nor a
            // dropped session's history counts.
            AccountRoute route;
            try { route = FlexBase.TakeAccountPictureFor(serial).Route; }
            catch (Exception ex)
            {
                Tracing.TraceLine($"RadioAccountRouting.ForSerial({serial}): unreadable: {ex.Message}", TraceLevel.Warning);
                route = AccountRoute.None;
            }
            Tracing.TraceLine($"RadioAccountRouting.ForSerial({serial}): {route}", TraceLevel.Info);
            return route;
        }
    }
}

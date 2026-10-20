namespace JJFlexWpf;

/// <summary>
/// Which restore a radio's arrival owes Home. The decision half of
/// <see cref="MainWindow.ExitRescueMode"/>, pulled out so it can be tested
/// without constructing a window (#410).
/// </summary>
/// <remarks>
/// <para><b>Two things can take Home's operating surface away, and both owe
/// the same way back.</b> The rescue page collapses six panels after three
/// minutes with no radio; <c>RestoreNoRadioShell</c> collapses Fields, Meters
/// and Panadapter on EVERY disconnect, rescue or not, and starts that
/// three-minute countdown. Until #410 only the first had a way back: a radio
/// arriving inside the grace period found rescue mode never entered, took
/// <see cref="ExitRescueMode"/>'s early return, and nothing re-ran
/// <c>ApplyUIMode</c> — so the frequency and meter fields were gone from the
/// tab order after the one action an operator would try to fix it with.</para>
/// <para><b>The restore itself is not decided here.</b> Both routes go through
/// <c>ApplyUIMode</c>, which stays the single source of the connected layout;
/// this only says whether a restore is owed and which page is being retired.</para>
/// </remarks>
internal static class HomeLayoutRestore
{
    internal enum Route
    {
        /// <summary>
        /// Nothing collapsed the surface — the first connect of a session,
        /// which runs before the rescue page could exist. Startup applied the
        /// layout already; re-applying it would only rebuild the menu bar.
        /// </summary>
        None,

        /// <summary>The rescue page is up: retire it, then restore.</summary>
        FromRescuePage,

        /// <summary>
        /// A disconnect collapsed the surface and the radio came back inside
        /// the grace period, before the rescue page engaged. Restore.
        /// </summary>
        FromDisconnectShell,
    }

    /// <param name="rescueMode">The rescue page is showing.</param>
    /// <param name="disconnectShellUp">
    /// <c>RestoreNoRadioShell</c> has collapsed the surface and no restore has
    /// run since.
    /// </param>
    internal static Route Decide(bool rescueMode, bool disconnectShellUp)
    {
        if (rescueMode) return Route.FromRescuePage;
        return disconnectShellUp ? Route.FromDisconnectShell : Route.None;
    }
}

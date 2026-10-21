using Xunit;
using Route = JJFlexWpf.HomeLayoutRestore.Route;

namespace JJFlexWpf.Tests;

/// <summary>
/// Which restore a radio's arrival owes Home (#410). Pure decision: NO WINDOW,
/// no WPF object of any kind is constructed here, so this class is desk-safe.
/// </summary>
/// <remarks>
/// <para><b>What this cannot see.</b> It proves the decision, not the layout.
/// That <c>ExitRescueMode</c> acts on the decision by running
/// <c>ApplyUIMode</c>, and that <c>ApplyUIMode</c> really puts the fields
/// panel back in the tab order on a live radio, is a window and a radio and
/// is the bench half: disconnect, reconnect inside three minutes, Tab through
/// Home.</para>
/// </remarks>
public sealed class HomeLayoutRestoreTests
{
    [Fact]
    public void A_reconnect_inside_the_grace_period_owes_a_restore()
    {
        // The #410 case: a disconnect collapsed Fields, Meters and Panadapter,
        // the countdown was still running, rescue never engaged.
        Assert.Equal(Route.FromDisconnectShell,
            HomeLayoutRestore.Decide(rescueMode: false, disconnectShellUp: true));
    }

    [Fact]
    public void A_reconnect_after_the_rescue_page_engaged_still_retires_the_page()
    {
        // Unchanged. The rescue page collapses everything the disconnect shell
        // did and more, so when both are true the rescue route is the one owed.
        Assert.Equal(Route.FromRescuePage,
            HomeLayoutRestore.Decide(rescueMode: true, disconnectShellUp: true));
        Assert.Equal(Route.FromRescuePage,
            HomeLayoutRestore.Decide(rescueMode: true, disconnectShellUp: false));
    }

    [Fact]
    public void The_first_connect_of_a_session_owes_nothing()
    {
        // Startup applied the layout before the radio opened; nothing has
        // collapsed it. Re-applying here would only rebuild the menu bar
        // mid-connect for no reason.
        Assert.Equal(Route.None,
            HomeLayoutRestore.Decide(rescueMode: false, disconnectShellUp: false));
    }
}

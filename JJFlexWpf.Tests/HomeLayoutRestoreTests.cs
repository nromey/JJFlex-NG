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

    // ---- the meters panel (repair round one) ----
    // Pure state, no panel: what MainWindow does with the answer (sets
    // MetersPanel visible inside RestoreConnectedHomeLayout, on both routes) is
    // the bench half — open meters with Ctrl+M, drop the radio, reconnect.

    [Fact]
    public void A_meters_panel_open_at_the_disconnect_is_reopened_by_the_restore()
    {
        var m = new HomeLayoutRestore.MetersPanelMemory();
        m.Collapsing(metersVisibleNow: true);
        Assert.True(m.Restoring());
        Assert.False(m.Restoring()); // owed once, not forever
    }

    [Fact]
    public void A_meters_panel_that_was_closed_stays_closed()
    {
        var m = new HomeLayoutRestore.MetersPanelMemory();
        m.Collapsing(metersVisibleNow: false);
        Assert.False(m.Restoring());
    }

    [Fact]
    public void The_rescue_page_collapsing_after_the_disconnect_does_not_forget_the_panel_was_open()
    {
        // Disconnect shell, then three minutes later the rescue page: the
        // second collapse sees the panel already collapsed.
        var m = new HomeLayoutRestore.MetersPanelMemory();
        m.Collapsing(metersVisibleNow: true);
        m.Collapsing(metersVisibleNow: false);
        Assert.True(m.Restoring());
    }

    [Fact]
    public void A_Ctrl_M_press_while_disconnected_is_the_operators_newer_word()
    {
        var m = new HomeLayoutRestore.MetersPanelMemory();
        m.Collapsing(metersVisibleNow: true);
        m.OperatorToggled();
        Assert.False(m.Restoring());
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

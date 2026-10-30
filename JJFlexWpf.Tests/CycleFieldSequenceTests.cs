using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Input;
using JJFlexWpf.Controls;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The cycle field's sequence options (#835, fix round 2026-10-28): Check your
/// audio's method field is an escalation, so Down goes to the next step, Up
/// back, Home and End jump, nothing wraps, and either end reports a boundary
/// (ruled by Noel 20:32 and 21:20). Every other cycle field keeps its old keys.
/// </summary>
/// <remarks>
/// <b>Windowless:</b> one <see cref="CycleFieldControl"/>, a UserControl never
/// put in a window, on an STA thread. Keys are driven through
/// <c>HandleKey</c>, the method the PreviewKeyDown handler calls. <b>What this
/// cannot see:</b> the boundary sound itself, and what NVDA says.
/// </remarks>
public sealed class CycleFieldSequenceTests
{
    private static readonly string[] Steps = { "one", "two", "three" };

    [Fact]
    public void TheDefaultsAreTheOldKeysUpForwardDownBackAndWrapping()
    {
        Run(() =>
        {
            var c = Field(0);
            c.HandleKey(Key.Down);
            Assert.Equal(2, c.SelectedIndex);           // Down went back, and wrapped to the last
            c.HandleKey(Key.Up);
            Assert.Equal(0, c.SelectedIndex);           // Up went forward, and wrapped to the first
            Assert.False(c.HandleKey(Key.Home));        // Home and End are not the field's
            Assert.False(c.HandleKey(Key.End));
        });
    }

    [Fact]
    public void AsASequenceDownIsNextUpIsBackAndNeitherWraps()
    {
        Run(() =>
        {
            var hits = new List<bool>();
            var c = Sequence(0, hits);
            c.HandleKey(Key.Down);
            Assert.Equal(1, c.SelectedIndex);
            c.HandleKey(Key.Down);
            Assert.Equal(2, c.SelectedIndex);
            c.HandleKey(Key.Down);                      // at the last: stays, boundary at the last end
            Assert.Equal(2, c.SelectedIndex);
            Assert.Equal(new[] { false }, hits);

            c.HandleKey(Key.Up);
            c.HandleKey(Key.Up);
            Assert.Equal(0, c.SelectedIndex);
            c.HandleKey(Key.Up);                        // at the first: stays, boundary at the first end
            Assert.Equal(0, c.SelectedIndex);
            Assert.Equal(new[] { false, true }, hits);
        });
    }

    [Fact]
    public void AsASequenceHomeAndEndJumpAndAtTheirEndReportTheBoundary()
    {
        Run(() =>
        {
            var hits = new List<bool>();
            int changes = 0;
            var c = Sequence(1, hits);
            c.SelectionChanged += (s, i) => changes++;
            Assert.True(c.HandleKey(Key.End));
            Assert.Equal(2, c.SelectedIndex);
            Assert.True(c.HandleKey(Key.Home));
            Assert.Equal(0, c.SelectedIndex);
            Assert.Equal(2, changes);                   // each jump is a real selection change
            c.HandleKey(Key.Home);
            Assert.Equal(new[] { true }, hits);
            Assert.Equal(2, changes);                   // and the boundary is not one
        });
    }

    [Fact]
    public void WithBoundaryOnArrivalTheEndIsHeardOnTheWayInAndAgainOnEachPressAtIt()
    {
        // Ruled by Noel 2026-10-29 01:17: "It should play when you get to the
        // top most and bottom most items. Pressing up or down again plays the
        // boundary noise only."
        Run(() =>
        {
            var hits = new List<bool>();
            var c = Sequence(0, hits);
            c.BoundaryOnArrival = true;
            c.HandleKey(Key.Down);                      // to the middle: nothing
            Assert.Empty(hits);
            c.HandleKey(Key.Down);                      // arriving at the last
            Assert.Equal(2, c.SelectedIndex);
            Assert.Equal(new[] { false }, hits);
            c.HandleKey(Key.Down);                      // pressing again at the last: the sound alone
            Assert.Equal(2, c.SelectedIndex);
            Assert.Equal(new[] { false, false }, hits);
            c.HandleKey(Key.Up);                        // back to the middle: nothing new
            Assert.Equal(2, hits.Count);
            c.HandleKey(Key.Up);                        // arriving at the first
            Assert.Equal(new[] { false, false, true }, hits);
            c.HandleKey(Key.End);                       // End arrives at the last
            Assert.Equal(new[] { false, false, true, false }, hits);
            c.HandleKey(Key.Home);                      // Home arrives at the first
            Assert.Equal(new[] { false, false, true, false, true }, hits);
        });
    }

    [Fact]
    public void WithoutBoundaryOnArrivalArrivingAtAnEndIsSilentAsBefore()
    {
        Run(() =>
        {
            var hits = new List<bool>();
            var c = Sequence(1, hits);
            c.HandleKey(Key.Down);
            c.HandleKey(Key.Home);
            Assert.Empty(hits);                         // arrivals only; no press at an end
        });
    }

    /// <summary>
    /// #853, measured 2026-10-29: as a Spinner, JAWS read every arrow one value
    /// behind (it spoke its cached value at the keystroke and never the change);
    /// as a combo box it speaks the new value once. NVDA reads both correctly.
    /// So the field must stay a combo box with its value on the Value pattern.
    /// </summary>
    [Fact]
    public void TheFieldIsAComboBoxCarryingItsOptionAsTheValue()
    {
        Run(() =>
        {
            var c = Field(1);
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(c);
            Assert.Equal(System.Windows.Automation.Peers.AutomationControlType.ComboBox, peer.GetAutomationControlType());
            Assert.Equal("Method", peer.GetName());
            var value = (System.Windows.Automation.Provider.IValueProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Value);
            Assert.Equal("two", value.Value);
            c.HandleKey(Key.Up);
            Assert.Equal("three", value.Value);         // the pattern reads the option now, not a copy
        });
    }

    private static CycleFieldControl Field(int index)
    {
        var c = new CycleFieldControl();
        c.Setup("Method", Steps, index);
        return c;
    }

    private static CycleFieldControl Sequence(int index, List<bool> hits)
    {
        var c = Field(index);
        c.Wraps = false;
        c.DownMovesForward = true;
        c.HomeEndJump = true;
        c.AtBoundary = atFirst => hits.Add(atFirst);
        return c;
    }

    private static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E repair round 2, B1: a slice jump and Escape, through the
/// REAL seam where Codex found the round 1 repair still wrong. A real
/// <see cref="FlexBase"/> over a vendor <see cref="Radio"/> with two vendor
/// <see cref="Slice"/> objects, the real <c>RXVFO</c> setter, the real command
/// queue drained by hand, <see cref="FlexNoiseRadio"/> and the real
/// <see cref="NoiseController"/>. No socket: the radio's command channel is a
/// capture that refuses to connect.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam.</b> Setting <c>RXVFO</c> changes the number at once but only
/// QUEUES <c>Slice.Active</c>; every read goes through the radio's active slice,
/// which stays the old one until the queue runs and the radio says so.
/// <c>LayerSliceJump</c> tells the controller the new number at once. Round 1
/// read the Escape picture right then, under the new number, so it held slice
/// A's values labelled as B's, and an edit on B followed by Escape wrote A's
/// level onto B (Codex: B ends at 0, expected 42). Round 1's own tests switched
/// the fake radio's values before telling the controller, so they could not
/// see this.
/// </para>
/// <para>
/// <b>Desk safety.</b> No window and no WPF control (the fixture asserts an
/// MTA thread); nothing audible (<see cref="QuietRun"/>); settings isolated
/// (<see cref="TestSettingsRoot"/>); no socket, and no command leaves the
/// process: the capture records them.
/// </para>
/// </remarks>
public sealed class NoiseSliceJumpSeamTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public NoiseSliceJumpSeamTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    /// <summary>The radio's command channel, captured: connected as far as FlexLib can tell, and never to anything.</summary>
    private sealed class WireCapture : CommandCommunicationBase
    {
        public List<string> Commands { get; } = new();
        public WireCapture() => PublishConnected(BeginConnection());
        public override System.Net.IPAddress? LocalIp { get; set; }
        public override bool Connect(System.Net.IPAddress ip, int port, int source, out CommandConnection? connection) =>
            throw new InvalidOperationException("No connection is made in this test.");
        public override void Disconnect() { }
        public override void Write(string value) => Commands.Add(value[(value.IndexOf('|') + 1)..].Trim());
    }

    private static void Set(object o, string field, object? value) => o.GetType().GetField(field, Hidden)!.SetValue(o, value);
    private static object Get(object o, string field) => o.GetType().GetField(field, Hidden)!.GetValue(o)!;

    /// <summary>
    /// Slice A (Noise Blanker level 0, active) and slice B (level 42), a
    /// jump from A to B, an edit on B, Escape. Returns B's and A's levels.
    /// </summary>
    /// <param name="notifyAtJump">Tell the controller the moment the number changes, as LayerSliceJump does (the case Codex found).</param>
    /// <param name="pollAfterActivation">Let the surfaces' poll report the slice again once the radio has activated it, before the edit.</param>
    private static (int B, int A, List<string> Wire) JumpEditEscape(bool notifyAtJump, bool pollAfterActivation)
    {
        using var f = new NoiseFixture();
        using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        var radio = (Radio)Activator.CreateInstance(typeof(Radio), Hidden, null, new object[] { false }, null)!;
        var wire = new WireCapture();
        Set(radio, "_commandCommunication", wire);
        Slice NewSlice(int index, int level, bool active)
        {
            var s = (Slice)Activator.CreateInstance(typeof(Slice), Hidden, null, new object[] { radio }, null)!;
            Set(s, "_index", index); Set(s, "_nb_level", level); Set(s, "_active", active);
            radio.SliceList.Add(s);
            return s;
        }
        var a = NewSlice(0, 0, active: true);
        var b = NewSlice(1, 42, active: false);
        try
        {
            Set(rig, "theRadio", radio);
            var mine = (List<Slice>)Get(rig, "mySlices");
            mine.Add(a); mine.Add(b);
            Set(rig, "_RXVFO", 0);
            var queue = Get(rig, "q");
            queue.GetType().GetProperty("MainLoop")!.SetValue(queue, true);
            void Drain()
            {
                while ((int)queue.GetType().GetProperty("Count")!.GetValue(queue)! > 0)
                {
                    var item = queue.GetType().GetMethod("Dequeue")!.Invoke(queue, null)!;
                    ((Delegate)item.GetType().GetField("Item")!.GetValue(item)!).DynamicInvoke();
                }
            }

            var adapter = new FlexNoiseRadio(rig);
            var noise = new NoiseController(f.Catalog, () => adapter, () => f.Pipeline, () => f.Config, () => { },
                f.Spoken, f.Held, f.Clock, f.Cost);
            noise.NoteActiveSlice(0);
            var snap = noise.TakeSnapshot();

            rig.RXVFO = 1;                                   // the jump: the number now, the activation queued
            Assert.Same(a, radio.ActiveSlice);                // every read still reaches A
            if (notifyAtJump) noise.NoteActiveSlice(rig.RXVFO);
            Drain();                                          // the queued activation runs...
            Set(a, "_active", false); Set(b, "_active", true);   // ...and the radio's status makes B active
            Assert.Same(b, radio.ActiveSlice);
            if (!notifyAtJump || pollAfterActivation) noise.NoteActiveSlice(rig.RXVFO);

            noise.Adjust(noise.Levers("radio_nb", false)[0], +1, "test");
            Drain();
            Assert.Equal(45, b.NBLevel);
            wire.Commands.Clear();
            noise.RestoreSnapshot(snap, "test");
            Drain();
            return (b.NBLevel, a.NBLevel, wire.Commands.ToList());
        }
        finally
        {
            Set(rig, "theRadio", null);
        }
    }

    [Theory]
    [InlineData(true, true)]     // Codex's sequence: told at the jump, polled after activation
    [InlineData(true, false)]    // told at the jump, and the edit comes before any poll
    public void A_jump_reported_before_the_slice_activates_still_puts_back_the_new_slices_own_value(bool notifyAtJump, bool poll)
    {
        var (bLevel, aLevel, wire) = JumpEditEscape(notifyAtJump, poll);
        Assert.Equal(42, bLevel);                        // round 1 left 0 here: A's value written onto B
        Assert.Equal(0, aLevel);
        Assert.Equal(new[] { "slice set 1 nb_level=42" }, wire);
    }

    [Fact]
    public void Positive_control_reported_after_the_slice_activates_puts_back_the_new_slices_own_value()
    {
        // Codex's control: the same sequence with the report after the
        // activation. It controls the capture ORDER itself: if this went red
        // the test above would be measuring something else.
        var (bLevel, aLevel, wire) = JumpEditEscape(notifyAtJump: false, pollAfterActivation: true);
        Assert.Equal(42, bLevel);
        Assert.Equal(0, aLevel);
        Assert.Equal(new[] { "slice set 1 nb_level=42" }, wire);
    }
}

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
/// Sprint 49 Track E repair rounds 2 and 3, B1: a slice jump and Escape,
/// through the REAL seam where Codex found the earlier repairs still wrong. A
/// real <see cref="FlexBase"/> over a vendor <see cref="Radio"/> with two vendor
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
/// <c>LayerSliceJump</c> tells the controller the new number at once.
/// </para>
/// <para>
/// <b>Round 2</b> keyed the Escape picture on the slice the radio actually
/// reads. <b>Round 3</b> covers what that left: the controller's write
/// shadows (a value it just wrote, believed for two seconds over the radio's
/// echo) were keyed by engine only, so a level written on A answered for B
/// after the jump, and the slice was reconciled only before a WRITE, after a
/// step had already been computed from A's value. The poll that reports the
/// slice runs only while Home's field panel is shown, so with it hidden (or in
/// Logging mode) the window is the whole two seconds. The rows below are
/// Codex's, from its round 2 re-check, with the 500 ms pause on a hand-driven
/// clock.
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
    /// Slice A (Noise Blanker level 0, active) and slice B (level 42) on a real
    /// FlexBase, with the noise controller over FlexNoiseRadio, its Escape
    /// picture taken on A as the layer's entry takes it, and a clock the test
    /// moves by hand.
    /// </summary>
    private sealed class Bench : IDisposable
    {
        public readonly NoiseFixture F = new();
        public readonly FlexBase Rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        public readonly Radio Radio;
        public readonly Slice A, B;
        public readonly WireCapture Wire = new();
        public readonly NoiseController Noise;
        public NoiseSnapshot Snap;
        public long Now = 1_000_000;
        private readonly object _queue;

        public Bench()
        {
            Radio = (Radio)Activator.CreateInstance(typeof(Radio), Hidden, null, new object[] { false }, null)!;
            Set(Radio, "_commandCommunication", Wire);
            Slice NewSlice(int index, int level, bool active)
            {
                var s = (Slice)Activator.CreateInstance(typeof(Slice), Hidden, null, new object[] { Radio }, null)!;
                Set(s, "_index", index); Set(s, "_nb_level", level); Set(s, "_active", active);
                Radio.SliceList.Add(s);
                return s;
            }
            A = NewSlice(0, 0, active: true);
            B = NewSlice(1, 42, active: false);
            Set(Rig, "theRadio", Radio);
            var mine = (List<Slice>)Get(Rig, "mySlices");
            mine.Add(A); mine.Add(B);
            Set(Rig, "_RXVFO", 0);
            _queue = Get(Rig, "q");
            _queue.GetType().GetProperty("MainLoop")!.SetValue(_queue, true);
            var adapter = new FlexNoiseRadio(Rig);
            Noise = new NoiseController(F.Catalog, () => adapter, () => F.Pipeline, () => F.Config, () => { },
                F.Spoken, F.Held, F.Clock, F.Cost, clock: () => Now);
            Noise.NoteActiveSlice(0);
            Snap = Noise.TakeSnapshot();
        }

        /// <summary>Run the queued commands as the main loop would.</summary>
        public void Drain()
        {
            while ((int)_queue.GetType().GetProperty("Count")!.GetValue(_queue)! > 0)
            {
                var item = _queue.GetType().GetMethod("Dequeue")!.Invoke(_queue, null)!;
                ((Delegate)item.GetType().GetField("Item")!.GetValue(item)!).DynamicInvoke();
            }
        }

        /// <summary>Shift+B: the number at once, told to the controller as LayerSliceJump tells it, the activation queued.</summary>
        public void Jump()
        {
            Rig.RXVFO = 1;
            Assert.Same(A, Radio.ActiveSlice);           // every read still reaches A
            Noise.NoteActiveSlice(Rig.RXVFO);
        }

        /// <summary>The queued activation runs and the radio's status makes B the active slice.</summary>
        public void Activate()
        {
            Drain();
            Set(A, "_active", false); Set(B, "_active", true);
            Assert.Same(B, Radio.ActiveSlice);
        }

        public NoiseLever Level => Noise.Levers("radio_nb", false)[0];
        public void Adjust() { Noise.Adjust(Level, +1, "test"); Drain(); }
        public void Escape() { Noise.RestoreSnapshot(Snap, "test"); Drain(); }

        /// <summary>Close and reopen the noise layer, as Enter then JJ key N do.</summary>
        public void Reopen() { Noise.EndSnapshot(); Noise.BeginSession(); Snap = Noise.TakeSnapshot(); }

        /// <summary>Codex's starting state: a level edit on A (0 to 1), then the jump and B's activation complete, then 500 ms.</summary>
        public void RecentEditOnAThenJump(bool poll)
        {
            Adjust();
            Assert.Equal(1, A.NBLevel);
            Jump();
            Activate();
            Now += 500;                                    // inside A's two-second shadow
            if (poll) Noise.NoteActiveSlice(Rig.RXVFO);    // what the field panel's poll would have said
        }

        public void Dispose()
        {
            Set(Rig, "theRadio", null);
            Rig.Dispose();
            F.Dispose();
        }
    }

    // ── Round 2: the picture is of the slice the radio reads ──

    [Theory]
    [InlineData(true, true)]     // Codex's sequence: told at the jump, polled after activation
    [InlineData(true, false)]    // told at the jump, and the edit comes before any poll
    [InlineData(false, true)]    // positive control: told only after the activation
    public void A_jump_and_an_edit_on_the_new_slice_then_Escape_puts_back_the_new_slices_own_value(bool notifyAtJump, bool poll)
    {
        using var b = new Bench();
        b.Rig.RXVFO = 1;
        if (notifyAtJump) b.Noise.NoteActiveSlice(b.Rig.RXVFO);
        b.Activate();
        if (!notifyAtJump || poll) b.Noise.NoteActiveSlice(b.Rig.RXVFO);
        b.Adjust();
        Assert.Equal(45, b.B.NBLevel);
        b.Wire.Commands.Clear();
        b.Escape();
        Assert.Equal(42, b.B.NBLevel);                    // round 1 left 0 here: A's value written onto B
        Assert.Equal(0, b.A.NBLevel);
        Assert.Equal(new[] { "slice set 1 nb_level=42" }, b.Wire.Commands);
    }

    // ── Round 3: a recent edit on A never answers for B ──

    [Theory]
    [InlineData(false)]          // Codex's row: no poll (Home's field panel hidden)
    [InlineData(true)]           // positive control: the poll supplied
    public void After_a_recent_edit_on_A_Tab_on_B_speaks_Bs_own_level(bool poll)
    {
        using var b = new Bench();
        b.RecentEditOnAThenJump(poll);
        b.Noise.NextLever(+1);
        Assert.Equal("Level, 42.", b.F.Spoken.Last);     // round 2 said "Level, 1.": A's shadow
        Assert.Equal(42, b.B.NBLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void After_a_recent_edit_on_A_one_step_on_B_steps_from_Bs_own_level(bool poll)
    {
        using var b = new Bench();
        b.RecentEditOnAThenJump(poll);
        b.Wire.Commands.Clear();
        b.Adjust();
        Assert.Equal(45, b.B.NBLevel);                    // round 2 wrote 2: one step from A's 1
        Assert.Equal(1, b.A.NBLevel);
        Assert.Equal(new[] { "slice set 1 nb_level=45" }, b.Wire.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void After_a_recent_edit_on_A_closing_and_reopening_the_layer_on_B_then_Escape_puts_back_Bs_own_level(bool poll)
    {
        using var b = new Bench();
        b.RecentEditOnAThenJump(poll);
        b.Reopen();
        b.Noise.Toggle("radio_nb", "test");               // B, the layer's engine key, as Codex pressed it
        b.Drain();
        b.Adjust();
        Assert.Equal(45, b.B.NBLevel);
        b.Wire.Commands.Clear();
        b.Escape();
        Assert.Equal(42, b.B.NBLevel);                    // round 2 left 1: A's shadowed level, adopted under B's identity
        Assert.Equal(1, b.A.NBLevel);                     // A untouched
        Assert.Contains("slice set 1 nb_level=42", b.Wire.Commands);
        Assert.DoesNotContain(b.Wire.Commands, c => c.StartsWith("slice set 0 ", StringComparison.Ordinal));
    }
}

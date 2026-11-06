#nullable enable
using System;
using System.Reflection;
using System.Threading.Tasks;
using JJPortaudio;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round four (part 6): from a reference recording's end until
/// the radio is receiving, the transmitter carries silence and never the
/// microphone (Noel, 2026-11-05 01:37 and 01:38). After a reference only.
/// </summary>
/// <remarks>
/// <para>
/// The first half drives <see cref="TxFilePlayer"/> exactly as the audio
/// thread does, one 10 ms frame at a time, with "microphone" buffers pinned at
/// a value nothing else produces, so a single sample of microphone on the
/// encoder is unmistakable. The second half drives the real
/// <see cref="FlexBase"/> on the vendor radio fixture: the arm, the Mox-fall
/// disarm, the radio's interlock and its connection.
/// </para>
/// <para>
/// Which holder had the key does not change the silence, only the sentence
/// and the unkey route; the per-holder end of the window's path is
/// <c>JJFlexWpf.Tests.ReferenceEndSilenceTests</c>.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ReferenceEndSilenceTests : IDisposable
{
    private const int Rate = 48000;
    private const int Frames = 480;          // one 10 ms Opus frame at 48 kHz
    private const int Floats = Frames * 2;   // interleaved stereo
    private const float Mic = 0.9f;

    private readonly RadioConfigStaticsScope _scope = new(nameof(ReferenceEndSilenceTests));
    public void Dispose() => _scope.Dispose();

    private static float[] MicBuffer()
    {
        var buf = new float[Floats];
        Array.Fill(buf, Mic);
        return buf;
    }

    private static float[] Content(int frames)
    {
        var c = new float[frames];
        Array.Fill(c, 0.4f);
        return c;
    }

    // The player's private states, read only as positive controls: a schedule
    // that says "inside the final fade" proves it got there.
    private const int StFileFadeOut = 4;
    private const int StEndSilence = 6;

    private static int State(TxFilePlayer player) =>
        (int)typeof(TxFilePlayer).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;

    /// <summary>
    /// Feed the player frames until its pass has run off the end, then on
    /// through the final 10 ms fade: a reference is then in its final held
    /// silence, anything else back on the microphone. Returns the frames fed.
    /// </summary>
    private static int RunToTheEnd(TxFilePlayer player)
    {
        for (int i = 0; i < 400; i++)
        {
            player.Process(MicBuffer(), Floats, Rate);
            if (player.ReachedEnd && !player.Engaged)
            {
                // One 10 ms frame is the whole fade; two leave no doubt.
                player.Process(MicBuffer(), Floats, Rate);
                player.Process(MicBuffer(), Floats, Rate);
                return i;
            }
        }
        throw new InvalidOperationException("the pass never ran off its end");
    }

    /// <summary>
    /// The schedule Codex found (round four check, B1): feed 2.5 ms buffers
    /// until the file runs off its end in one of them. The pass is then inside
    /// its final 10 ms fade, not yet in its final held silence, because the
    /// fade is four such buffers long.
    /// </summary>
    private static void RunIntoTheFinalFade(TxFilePlayer player)
    {
        for (int i = 0; i < 2000 && !player.ReachedEnd; i++)
            player.Process(SmallMicBuffer(), SmallFloats, Rate);
        Assert.True(player.ReachedEnd, "positive control: the pass ran off its end");
        Assert.True(State(player) == StFileFadeOut, "positive control: the schedule is inside the final fade");
    }

    private const int SmallFloats = 240;     // 120 frames, 2.5 ms: a partial buffer

    private static float[] SmallMicBuffer()
    {
        var buf = new float[SmallFloats];
        Array.Fill(buf, Mic);
        return buf;
    }

    /// <summary>The audio thread stalls past the player's 100 ms stream-gap threshold, then the next buffer.</summary>
    private static float[] NextBufferAfterAStall(TxFilePlayer player)
    {
        System.Threading.Thread.Sleep(150);
        var next = SmallMicBuffer();
        player.Process(next, SmallFloats, Rate);
        return next;
    }

    private static bool AllZero(float[] buf)
    {
        foreach (float s in buf) if (s != 0f) return false;
        return true;
    }

    /// <summary>True when no sample of the microphone reached the encoder in <paramref name="frames"/> frames.</summary>
    private static bool MicNeverReaches(TxFilePlayer player, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            var buf = MicBuffer();
            player.Process(buf, Floats, Rate);
            foreach (float s in buf) if (s != 0f) return false;
        }
        return true;
    }

    private static bool MicIsBack(TxFilePlayer player)
    {
        for (int i = 0; i < 5; i++) player.Process(MicBuffer(), Floats, Rate);   // the 10 ms ramp
        var after = MicBuffer();
        player.Process(after, Floats, Rate);
        foreach (float s in after) if (Math.Abs(s - Mic) > 1e-5) return false;
        return true;
    }

    // ── The player ──────────────────────────────────────────────────────────

    [Fact]
    public void A_reference_pass_past_its_end_sends_silence_and_never_the_microphone_until_stopped()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate / 5), Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        RunToTheEnd(player);

        Assert.True(player.ReachedEnd);
        Assert.True(player.HoldingEndSilence);
        // What polls for the end still sees it, and what supplies buffers keeps supplying them.
        Assert.False(player.Engaged);
        Assert.False(player.Idle);
        Assert.True(MicNeverReaches(player, 300), "three seconds after the end, the microphone reached the encoder");

        player.Stop();
        Assert.False(player.HoldingEndSilence);
        Assert.True(MicIsBack(player), "after Stop the microphone comes back, untouched");
    }

    [Fact]
    public void A_stream_gap_does_not_let_the_microphone_back_into_held_silence()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate / 5), Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        RunToTheEnd(player);
        System.Threading.Thread.Sleep(150);   // longer than the player's 100 ms stream-gap threshold
        Assert.True(MicNeverReaches(player, 20), "a stall in the audio thread put the microphone on the air");
        Assert.True(player.HoldingEndSilence);
    }

    [Fact]
    public void Loading_new_content_or_starting_again_never_lets_the_microphone_through()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate / 5), Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        RunToTheEnd(player);

        // Starting again from held silence goes straight to the file. Every
        // sample, not only the exact microphone value: the file fades in from
        // zero and never passes its own 0.4, a microphone ramp would start at
        // 0.9 (strengthened in round four repair, 6e-3).
        player.StartHoldingSilenceAtEnd();
        var first = MicBuffer();
        player.Process(first, Floats, Rate);
        Assert.All(first, s => Assert.InRange(s, 0f, 0.4f + 1e-5f));
        Assert.True(player.Engaged);

        // A load (a send replacing it) is a Stop first: the hold is let go.
        RunToTheEnd(player);
        Assert.True(player.HoldingEndSilence);
        player.Load(Content(Rate / 5), Rate, "send");
        Assert.False(player.HoldingEndSilence);
        Assert.True(MicIsBack(player));
    }

    [Fact]
    public void A_stop_during_the_ramp_at_the_end_wins_over_the_hold()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate / 5), Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        // Feed until the file has run out but its 10 ms fade-out has not finished.
        for (int i = 0; i < 400 && !player.ReachedEnd; i++)
        {
            var buf = new float[Floats / 4];          // quarter frames, so the ramp spans calls
            Array.Fill(buf, Mic);
            player.Process(buf, buf.Length, Rate);
        }
        Assert.True(player.ReachedEnd);
        Assert.True(State(player) == StFileFadeOut, "positive control: inside the final fade");
        // Round four repair (6e-1): the final fade is part of the held end, so
        // this read false until then. It is the one definition every reader asks.
        Assert.True(player.HoldingEndSilence, "the final fade of a reference is held silence");
        player.Stop();
        Assert.False(player.HoldingEndSilence);
        Assert.True(MicIsBack(player), "a disarm in the last ramp must not be lost to the hold");
        Assert.False(player.HoldingEndSilence);
    }

    // ── Round four repair (part 6e-1): the final fade is inside the silence ──

    /// <summary>
    /// B1, the cold-gap half: a reference runs off its end in a partial buffer,
    /// the audio thread stalls 150 ms inside the final 10 ms fade, and the next
    /// buffer comes. Until this repair the gap branch read the fade as "not
    /// held" and handed the next buffer, the microphone, straight through.
    /// </summary>
    [Fact]
    public void A_stream_gap_inside_the_final_fade_does_not_let_the_microphone_back()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate / 5), Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        RunIntoTheFinalFade(player);

        var next = NextBufferAfterAStall(player);
        Assert.True(AllZero(next), "a stall in the final fade put the microphone on the air");
        Assert.True(player.HoldingEndSilence, "the stall let the hold go");
        Assert.False(player.Idle);
        Assert.True(MicNeverReaches(player, 20));
        Assert.True(State(player) == StEndSilence, "the fade finished into held silence");

        player.Stop();
        Assert.True(MicIsBack(player));
    }

    /// <summary>
    /// The control for the definition's scope: a recorded send or Play from
    /// computer (plain <c>Start</c>) still hands the microphone back through its
    /// own final fade, and a stall there still lets it straight back; so does a
    /// reference disarmed before its end. Only a reference's END is held.
    /// </summary>
    [Fact]
    public void A_send_or_a_disarmed_reference_still_gives_the_microphone_back_through_its_own_fade()
    {
        var send = new TxFilePlayer();
        send.Load(Content(Rate / 5), Rate, "send");
        send.Start();
        RunIntoTheFinalFade(send);
        Assert.False(send.HoldingEndSilence, "a send's final fade is not a reference's held end");
        var next = NextBufferAfterAStall(send);
        Assert.True(send.Idle);
        Assert.All(next, s => Assert.Equal(Mic, s, 5));

        var disarmed = new TxFilePlayer();
        disarmed.Load(Content(Rate / 5), Rate, "reference");
        disarmed.StartHoldingSilenceAtEnd();
        for (int i = 0; i < 10; i++) disarmed.Process(SmallMicBuffer(), SmallFloats, Rate);
        Assert.True(disarmed.Engaged, "positive control: mid-pass");
        disarmed.Stop();
        Assert.True(State(disarmed) == StFileFadeOut, "positive control: in the disarm's own fade");
        Assert.False(disarmed.HoldingEndSilence, "a disarm's fade is not the held end");
        next = NextBufferAfterAStall(disarmed);
        Assert.True(disarmed.Idle);
        Assert.All(next, s => Assert.Equal(Mic, s, 5));
    }

    /// <summary>
    /// Starting a pass again inside the final fade goes straight to the file,
    /// as it does from held silence: the microphone is already off the encoder,
    /// and ramping it out would put it back on for 10 ms. Until this repair the
    /// start read the fade as the microphone's and began with it at full level.
    /// </summary>
    [Fact]
    public void Starting_again_inside_the_final_fade_never_puts_the_microphone_on_the_encoder()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate / 5), Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        RunIntoTheFinalFade(player);
        player.Start();   // the Fixer's voice stage, which has no refusal of its own
        var first = SmallMicBuffer();
        player.Process(first, SmallFloats, Rate);
        // The file fades in from zero and never passes its own 0.4; any
        // microphone would start at 0.9.
        Assert.All(first, s => Assert.InRange(s, 0f, 0.4f + 1e-5f));
        Assert.True(player.Engaged);
        player.Stop();
    }

    /// <summary>
    /// A recorded send and Play from computer start with plain <c>Start</c>, as
    /// does the Fixer's voice stage: they end exactly as before, the microphone
    /// handed back by the player itself (also
    /// <c>TxInjectionTests.PlaybackEndsAndHandsTheMicrophoneBack</c>). And a
    /// plain pass after a reference's does not inherit its hold.
    /// </summary>
    [Fact]
    public void A_send_or_Play_from_computer_still_hands_the_microphone_back_at_its_end()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate / 5), Rate, "send");
        player.Start();
        RunToTheEnd(player);
        Assert.False(player.HoldingEndSilence);
        Assert.True(player.Idle);
        Assert.True(MicIsBack(player));

        player.Load(Content(Rate / 5), Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        RunToTheEnd(player);
        player.Stop();
        player.Load(Content(Rate / 5), Rate, "send after a reference");
        player.Start();
        RunToTheEnd(player);
        Assert.False(player.HoldingEndSilence);
        Assert.True(MicIsBack(player), "a send after a reference holds nothing");
    }

    /// <summary>The test tone is a different source and its end is untouched; pinned here so a change to it is seen.</summary>
    [Fact]
    public void The_test_tone_still_hands_the_microphone_back_when_released()
    {
        var tone = new TxToneGenerator();
        tone.Start();
        tone.Process(MicBuffer(), Floats, Rate);
        tone.Stop();
        for (int i = 0; i < 5; i++) tone.Process(MicBuffer(), Floats, Rate);
        var after = MicBuffer();
        tone.Process(after, Floats, Rate);
        Assert.All(after, s => Assert.Equal(Mic, s, 5));
    }

    // ── The radio ───────────────────────────────────────────────────────────

    private static void Set(FlexBase rig, string name, object value) =>
        typeof(FlexBase).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig, value);

    private static void NoteRadioMox(FlexBase rig, bool mox) =>
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig, new object[] { mox });

    private static void Drain(FlexBase rig)
    {
        while (rig.q.Count > 0)
            if (rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
    }

    private static async Task<bool> Within(Func<bool> condition, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        return condition();
    }

    private static void PlantNewConnection(RigOnVendorRadio f) =>
        typeof(Flex.Smoothlake.FlexLib.Radio).GetField("_commandCommunication", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(f.Vendor.Radio, new FakeCommandTransport());

    /// <summary>
    /// A real arm through <c>TxReferenceStartAsync</c>, keyed, played to its
    /// end: into its final held silence, or, with <paramref name="intoTheFade"/>,
    /// only into the final fade (round four repair, B1).
    /// </summary>
    private static async Task<RigOnVendorRadio> ReferenceRunToItsEnd(string serial, bool intoTheFade = false)
    {
        var f = new RigOnVendorRadio(serial);
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit vox_enable=1");
        f.Rig.TxFilePlayer.Load(Content(Rate / 5), Rate, "reference");
        f.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "transmit set vox_enable=0") f.Vendor.ReplyOk(f.Vendor.LastSequence);
        };
        var arm = f.Rig.TxReferenceStartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!arm.IsCompleted && DateTime.UtcNow < deadline) { Drain(f.Rig); await Task.Delay(1); }
        Assert.True(await arm, "positive control: the reference armed");
        // Keyed: our key and the radio's interlock both say transmit.
        NoteRadioMox(f.Rig, true);
        f.Vendor.Status("interlock state=TRANSMITTING source=SW");
        Assert.True(f.Rig.RadioInterlockReportsTransmit, "positive control: the fixture's interlock reached the radio");
        if (intoTheFade)
        {
            RunIntoTheFinalFade(f.Rig.TxFilePlayer);
            return f;
        }
        RunToTheEnd(f.Rig.TxFilePlayer);
        Assert.True(f.Rig.TxReferenceEndSilenceHeld, "a reference armed by TxReferenceStartAsync holds silence at its end");
        return f;
    }

    /// <summary>
    /// B1, the Mox half, through the real <c>noteRadioMox</c>: our Mox cache
    /// falls while the pass is inside its final fade. Until this repair the
    /// guard there read the fade as "the pass is over", disarmed, let the
    /// reference lease go and handed the microphone back with the radio's
    /// interlock still transmitting.
    /// </summary>
    [Fact]
    public async Task Our_Mox_falling_inside_the_final_fade_leaves_the_silence_held()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-fade-mox", intoTheFade: true);
        NoteRadioMox(f.Rig, false);
        Assert.True(f.Rig.RadioInterlockReportsTransmit, "positive control: the radio is still transmitting");
        Assert.True(f.Rig.TxReferenceArmed, "the Mox-fall disarm let the reference lease go before the radio received");
        Assert.True(f.Rig.TxReferenceEndSilenceHeld, "the Mox-fall disarm let the silence go before the radio received");
        Assert.True(AllZero(NextBufferAfterAStall(f.Rig.TxFilePlayer)), "the microphone reached the encoder");
        Assert.True(MicNeverReaches(f.Rig.TxFilePlayer, 30));

        // The window's poll then asks for the wait, as it does for every end.
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        Assert.False(await Within(() => how != null, 200), "the radio is still transmitting");
        f.Vendor.Status("interlock state=READY");
        Assert.True(await Within(() => how == ReferenceSilenceEnd.Received, 2_000));
        Assert.False(f.Rig.TxReferenceArmed);
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
    }

    /// <summary>
    /// The same definition, from the wait's side: the window sees the end while
    /// the pass is still in its final fade and the radio is already receiving.
    /// Until this repair the wait read the fade as "already let go", did
    /// nothing, and the fade then finished into held silence that nothing was
    /// left to end: the microphone stayed off the encoder and the reference
    /// lease kept VOX paused until the window closed.
    /// </summary>
    [Fact]
    public async Task The_radio_already_receiving_when_the_end_is_seen_in_the_fade_still_gets_the_microphone_back()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-fade-already", intoTheFade: true);
        // Receive reached without our Mox edge (a hardware line let go at the end).
        Set(f.Rig, "_Transmit", false);
        f.Vendor.Status("interlock state=READY");
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        Assert.Equal(ReferenceSilenceEnd.Received, how);
        Assert.False(f.Rig.TxReferenceEndSilenceHeld);
        Assert.False(f.Rig.TxReferenceArmed, "the reference lease, and VOX's pause, were left held");
        Assert.True(MicIsBack(f.Rig.TxFilePlayer), "the microphone never came back");
    }

    /// <summary>
    /// The normal way out, and the trap in it: our Mox cache falls the moment
    /// the app asks for an unkey, while the radio is still transmitting. The
    /// silence holds through that, and ends only when the radio reports receive.
    /// </summary>
    [Fact]
    public async Task The_silence_outlasts_our_Mox_falling_and_ends_when_the_radio_reports_receive()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-receive");
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);

        NoteRadioMox(f.Rig, false);                       // the unkey request: Mox cache false
        Assert.True(f.Rig.TxReferenceEndSilenceHeld, "the Mox-fall disarm let the microphone back before the radio received");
        Assert.True(MicNeverReaches(f.Rig.TxFilePlayer, 30));
        f.Vendor.Status("interlock state=UNKEY_REQUESTED source=SW");
        Assert.False(await Within(() => how != null, 200), "on its way out of transmit is still transmit");
        Assert.True(MicNeverReaches(f.Rig.TxFilePlayer, 30));

        f.Vendor.Status("interlock state=READY");
        Assert.True(await Within(() => how != null, 2_000), "the radio reports receive: the silence ends");
        Assert.Equal(ReferenceSilenceEnd.Received, how);
        Assert.False(f.Rig.TxReferenceEndSilenceHeld);
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
    }

    /// <summary>The other half of "receiving": the interlock reads ready while our own key is still down.</summary>
    [Fact]
    public async Task A_ready_interlock_under_our_own_key_is_not_receive()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-both");
        f.Vendor.Status("interlock state=READY");
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        Assert.False(await Within(() => how != null, 300));
        Assert.True(MicNeverReaches(f.Rig.TxFilePlayer, 30));
        Set(f.Rig, "_Transmit", false);
        Assert.True(await Within(() => how == ReferenceSilenceEnd.Received, 2_000));
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
    }

    /// <summary>
    /// A hardware push to talk held past the end: our key is not down, the
    /// radio's interlock is, and the silence lasts as long as the operator holds
    /// it (Noel, 01:37: "send silence until the operator lets go").
    /// </summary>
    [Fact]
    public async Task Under_a_hardware_line_the_silence_lasts_until_the_operator_lets_go()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-hardware");
        // Our key is not down: only the radio's interlock says transmit, as
        // under a hardware line (strengthened in round four repair, 6e-3).
        Set(f.Rig, "_Transmit", false);
        f.Vendor.Status("interlock state=TRANSMITTING source=MIC");
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        Assert.False(await Within(() => how != null, 400));
        Assert.True(MicNeverReaches(f.Rig.TxFilePlayer, 100), "the hand mic is held and the microphone reached the encoder");
        NoteRadioMox(f.Rig, false);
        f.Vendor.Status("interlock state=READY");
        Assert.True(await Within(() => how == ReferenceSilenceEnd.Received, 2_000));
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
    }

    [Fact]
    public async Task A_replaced_connection_ends_the_silence_and_gives_the_microphone_back()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-reconnect");
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        Assert.False(await Within(() => how != null, 200));
        PlantNewConnection(f);
        Assert.True(await Within(() => how != null, 2_000), "the connection the silence began on is gone");
        Assert.Equal(ReferenceSilenceEnd.ConnectionGone, how);
        Assert.False(f.Rig.TxReferenceEndSilenceHeld);
    }

    /// <summary>
    /// A disarm, a closing window, a departing radio and the hard kill all call
    /// <c>TxFileStop</c>: the silence ends there and then, and the wait, when it
    /// ends, finds nothing to do.
    /// </summary>
    /// <remarks>
    /// The microphone back while the radio still transmits is today's disarm
    /// and close behaviour. Under a hardware push to talk still held it waits
    /// on Noel's ruling (Codex's round four check, B2), so this pins what is,
    /// not what is safe.
    /// </remarks>
    [Fact]
    public async Task A_disarm_ends_the_silence_at_once_and_the_wait_then_does_nothing()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-disarm");
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        f.Rig.TxFileStop();
        Assert.False(f.Rig.TxReferenceEndSilenceHeld);
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
        NoteRadioMox(f.Rig, false);
        f.Vendor.Status("interlock state=READY");
        Assert.True(await Within(() => how != null, 2_000));
        Assert.Equal(ReferenceSilenceEnd.AlreadyReleased, how);
    }

    /// <summary>
    /// The disconnect's VOX release (<c>DisconnectVoxPauses</c>, which both
    /// disconnect paths call) ends the silence and the lease. Named for what it
    /// drives in round four repair (6e-3): not the whole disconnect.
    /// </summary>
    [Fact]
    public async Task The_disconnects_VOX_release_ends_the_silence()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-disconnect");
        typeof(FlexBase).GetMethod("DisconnectVoxPauses", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Rig, null);
        Assert.False(f.Rig.TxReferenceEndSilenceHeld);
        Assert.False(f.Rig.TxReferenceArmed);
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
    }

    /// <summary>
    /// Two waits for ONE pass: only the newest ends its silence. Renamed in
    /// round four repair (6e-3): it said "a newer pass", which it never made.
    /// An older wait against a newer pass is
    /// <see cref="An_older_wait_held_up_across_a_newer_pass_cannot_end_that_pass_silence"/>.
    /// </summary>
    [Fact]
    public async Task Of_two_waits_for_one_pass_only_the_newest_ends_the_silence()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-generation");
        ReferenceSilenceEnd? older = null, newer = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => older = h);
        f.Rig.TxReferenceEndSilenceUntilReceive(h => newer = h);
        NoteRadioMox(f.Rig, false);
        f.Vendor.Status("interlock state=READY");
        Assert.True(await Within(() => older != null && newer != null, 2_000));
        Assert.Equal(ReferenceSilenceEnd.AlreadyReleased, older);
        Assert.Equal(ReferenceSilenceEnd.Received, newer);
    }

    /// <summary>A recorded send keys through <c>TxFileStart</c> (or the VOX start): at its end the microphone is back, no hold.</summary>
    [Fact]
    public void A_send_through_the_rig_holds_no_silence_at_its_end()
    {
        using var f = new RigOnVendorRadio("ref-silence-send");
        f.Rig.TxFilePlayer.Load(Content(Rate / 5), Rate, "send");
        f.Rig.TxFileStart();
        RunToTheEnd(f.Rig.TxFilePlayer);
        Assert.False(f.Rig.TxReferenceEndSilenceHeld);
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));

        f.Rig.TxFilePlayer.Load(Content(Rate / 5), Rate, "vox send");
        f.Rig.TxFileStartForVox();
        RunToTheEnd(f.Rig.TxFilePlayer);
        Assert.False(f.Rig.TxReferenceEndSilenceHeld);
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
        f.Rig.TxFileStop();
    }

    /// <summary>Nothing transmitting when the end is seen: the microphone is back at once, on this thread.</summary>
    [Fact]
    public async Task Already_receiving_the_microphone_is_back_at_once()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-already");
        NoteRadioMox(f.Rig, false);
        f.Vendor.Status("interlock state=READY");
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        Assert.Equal(ReferenceSilenceEnd.Received, how);
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
    }

    // ── Round four repair (part 6e-2): a re-key never replays a finished pass ──

    /// <summary>True when some sample of the buffer is the reference itself (its content is 0.4).</summary>
    private static bool CarriesTheReference(float[] buf)
    {
        foreach (float s in buf) if (Math.Abs(s - 0.4f) < 1e-4f) return true;
        return false;
    }

    /// <summary>Frames of audio, each checked: nothing but exact silence.</summary>
    private static bool OnlySilence(TxFilePlayer player, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            var buf = MicBuffer();
            player.Process(buf, Floats, Rate);
            if (!AllZero(buf)) return false;
        }
        return true;
    }

    /// <summary>
    /// A re-key while the silence is held, the JAWS synthetic re-key's shape:
    /// our key down again (the app's own transmit setter, which asks the player
    /// to restart an armed reference), the radio's Mox rising (which asks it
    /// too), the transmit stream restarting cold. The transmitter carries
    /// silence: not the reference again, and not the microphone. Codex's
    /// round four check raised it; it did not reproduce before this repair,
    /// because both restart requests are refused past the end. Inside the
    /// final fade as well as after it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_re_key_during_the_silence_carries_silence_not_the_reference_or_the_microphone(bool intoTheFade)
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-rekey-" + intoTheFade, intoTheFade);
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        NoteRadioMox(f.Rig, false);                          // the app lets go after the tail
        f.Vendor.Status("interlock state=UNKEY_REQUESTED source=SW");

        f.Rig.Transmit = true;                               // the re-key, through our own key
        Drain(f.Rig);
        NoteRadioMox(f.Rig, true);
        f.Vendor.Status("interlock state=TRANSMITTING source=SW");
        var first = NextBufferAfterAStall(f.Rig.TxFilePlayer);
        Assert.True(AllZero(first), "the re-key put the reference or the microphone on the air");
        Assert.True(OnlySilence(f.Rig.TxFilePlayer, 100), "the re-key carried something other than silence");
        Assert.True(f.Rig.TxFilePlayer.ReachedEnd, "the finished pass started again");
        Assert.True(f.Rig.TxReferenceEndSilenceHeld);
        Assert.Null(how);

        NoteRadioMox(f.Rig, false);
        f.Vendor.Status("interlock state=READY");
        Assert.True(await Within(() => how == ReferenceSilenceEnd.Received, 2_000));
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
    }

    /// <summary>
    /// The race the re-key leaves open: a restart asked for in the pass's last
    /// buffer, its test passed on the UI thread while the audio thread ran off
    /// the end. Created here as the state that race leaves, the request planted
    /// past the end, because one thread cannot interleave the two. Until this
    /// repair the player honoured it and played the reference again over its
    /// own held silence. A restart asked for mid-pass still restarts.
    /// </summary>
    [Fact]
    public void A_restart_asked_for_as_the_pass_ran_off_its_end_does_not_replay_it()
    {
        var restartFlag = typeof(TxFilePlayer).GetField("_restartOnNextBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Control: mid-pass, a restart is honoured.
        var midPass = new TxFilePlayer();
        midPass.Load(Content(Rate / 5), Rate, "reference");
        midPass.StartHoldingSilenceAtEnd();
        for (int i = 0; i < 10; i++) midPass.Process(MicBuffer(), Floats, Rate);
        int before = midPass.PlayedFrames;
        midPass.RestartOnNextBuffer();
        midPass.Process(MicBuffer(), Floats, Rate);
        Assert.True(midPass.PlayedFrames < before, "positive control: a restart mid-pass starts the pass again");

        foreach (bool intoTheFade in new[] { false, true })
        {
            var player = new TxFilePlayer();
            player.Load(Content(Rate / 5), Rate, "reference");
            player.StartHoldingSilenceAtEnd();
            if (intoTheFade) RunIntoTheFinalFade(player); else RunToTheEnd(player);
            restartFlag.SetValue(player, true);
            for (int i = 0; i < 100; i++)
            {
                var buf = MicBuffer();
                player.Process(buf, Floats, Rate);
                Assert.False(CarriesTheReference(buf), "the finished pass was played again (into the fade: " + intoTheFade + ")");
                Assert.True(AllZero(buf), "the held silence carried something (into the fade: " + intoTheFade + ")");
            }
            Assert.True(player.ReachedEnd);
            Assert.True(player.HoldingEndSilence);
        }
    }

    /// <summary>
    /// After the radio receives, with no new arm, the reference is over: a
    /// re-key is an ordinary transmission on the microphone, and the lease that
    /// would have restarted the reference is gone.
    /// </summary>
    [Fact]
    public async Task A_re_key_after_receive_with_no_new_arm_is_ordinary_transmit_on_the_microphone()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-after-receive");
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        NoteRadioMox(f.Rig, false);
        f.Vendor.Status("interlock state=READY");
        Assert.True(await Within(() => how == ReferenceSilenceEnd.Received, 2_000));
        Assert.False(f.Rig.TxReferenceArmed, "positive control: the reference stood down at receive");

        f.Rig.Transmit = true;
        Drain(f.Rig);
        NoteRadioMox(f.Rig, true);
        f.Vendor.Status("interlock state=TRANSMITTING source=SW");
        var first = NextBufferAfterAStall(f.Rig.TxFilePlayer);
        Assert.False(CarriesTheReference(first), "the reference played again with no arm");
        Assert.True(f.Rig.TxFilePlayer.Idle);
        Assert.True(MicIsBack(f.Rig.TxFilePlayer), "an ordinary transmission carries the microphone");
    }

    /// <summary>A fresh arm after receive still plays the reference: the repair refuses only a finished pass, never a new one.</summary>
    [Fact]
    public async Task A_fresh_arm_after_receive_plays_the_reference_again()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-fresh-arm");
        ReferenceSilenceEnd? how = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => how = h);
        NoteRadioMox(f.Rig, false);
        f.Vendor.Status("interlock state=READY");
        Assert.True(await Within(() => how == ReferenceSilenceEnd.Received, 2_000));

        Assert.True(await ArmAgain(f), "a fresh arm after receive was refused");
        NoteRadioMox(f.Rig, true);
        f.Vendor.Status("interlock state=TRANSMITTING source=SW");
        bool heard = false;
        for (int i = 0; i < 50 && !heard; i++)
        {
            var buf = MicBuffer();
            f.Rig.TxFilePlayer.Process(buf, Floats, Rate);
            heard = CarriesTheReference(buf);
        }
        Assert.True(heard, "the fresh arm never played the reference");
        RunToTheEnd(f.Rig.TxFilePlayer);
        Assert.True(f.Rig.TxReferenceEndSilenceHeld, "the second pass holds its own end");
    }

    // ── Round four repair (part 6e-3): an older wait against a newer pass ──

    /// <summary>
    /// Codex's schedule (round four check, lifecycle audit): the radio's wait
    /// for an older pass decides (receive seen, or its connection replaced by a
    /// reconnect on the same FlexBase) and is then held up before it takes the
    /// gate. Meanwhile the window disarms, arms again, and the newer pass plays
    /// to its end, before the window's poll has asked for that pass's own wait.
    /// The older wait then resumes. Holding the gate on this thread is what
    /// holds it up, exactly where a stall would. Until this repair it read the
    /// newer pass's silence as its own and stopped it, the radio transmitting.
    /// </summary>
    [Theory]
    [InlineData(false)]   // the older wait saw receive
    [InlineData(true)]    // the older wait saw its connection replaced: a reconnect
    public async Task An_older_wait_held_up_across_a_newer_pass_cannot_end_that_pass_silence(bool reconnect)
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-older-" + reconnect);
        var player = f.Rig.TxFilePlayer;
        ReferenceSilenceEnd? older = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => older = h);
        object gate = typeof(FlexBase).GetField("_voxPauseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Rig)!;

        System.Threading.Monitor.Enter(gate);
        try
        {
            // No await in here: the gate is held by this thread.
            NoteRadioMox(f.Rig, false);
            if (reconnect)
            {
                PlantNewConnection(f);
                System.Threading.Thread.Sleep(200);           // the older wait sees its connection gone, and waits at the gate
                typeof(FlexBase).GetMethod("DisconnectVoxPauses", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(f.Rig, null);                      // what the disconnect does before the reconnect
            }
            else
            {
                f.Vendor.Status("interlock state=READY");
                System.Threading.Thread.Sleep(200);           // the older wait sees receive, and waits at the gate
                f.Rig.TxFileStop();                            // the window's DisarmReference
            }
            Assert.True(older == null, "positive control: the older wait is held up at the gate");

            // The fresh arm, the key, and the newer pass played to its end.
            f.Vendor.Status("transmit vox_enable=0");
            var arm = f.Rig.TxReferenceStartAsync();
            Assert.True(arm.IsCompleted && arm.Result, "positive control: the fresh arm went through");
            NoteRadioMox(f.Rig, true);
            f.Vendor.Status("interlock state=TRANSMITTING source=SW");
            RunToTheEnd(player);
            Assert.True(f.Rig.TxReferenceEndSilenceHeld, "positive control: the newer pass holds its end");
        }
        finally { System.Threading.Monitor.Exit(gate); }

        Assert.True(await Within(() => older != null, 2_000), "the older wait never ended");
        Assert.Equal(ReferenceSilenceEnd.AlreadyReleased, older);
        Assert.True(f.Rig.RadioInterlockReportsTransmit, "positive control: the radio is transmitting the newer pass");
        Assert.True(f.Rig.TxReferenceEndSilenceHeld, "an older wait ended the newer pass's silence while the radio transmits");
        Assert.True(OnlySilence(player, 30), "the microphone reached the encoder");

        // The newer pass's own wait ends it, at receive.
        ReferenceSilenceEnd? newer = null;
        f.Rig.TxReferenceEndSilenceUntilReceive(h => newer = h);
        NoteRadioMox(f.Rig, false);
        f.Vendor.Status("interlock state=READY");
        Assert.True(await Within(() => newer == ReferenceSilenceEnd.Received, 2_000));
        Assert.True(MicIsBack(player));
    }

    /// <summary>Arm the reference again through <c>TxReferenceStartAsync</c>, as the fixture's first arm did.</summary>
    private static async Task<bool> ArmAgain(RigOnVendorRadio f)
    {
        var arm = f.Rig.TxReferenceStartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!arm.IsCompleted && DateTime.UtcNow < deadline) { Drain(f.Rig); await Task.Delay(1); }
        return arm.IsCompleted && await arm;
    }
}

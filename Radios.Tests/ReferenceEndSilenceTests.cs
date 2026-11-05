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

    /// <summary>Feed the player frames until its pass has run off the end. Returns the frames fed.</summary>
    private static int RunToTheEnd(TxFilePlayer player)
    {
        for (int i = 0; i < 400; i++)
        {
            player.Process(MicBuffer(), Floats, Rate);
            if (player.ReachedEnd && !player.Engaged && (player.HoldingEndSilence || player.Idle)) return i;
        }
        throw new InvalidOperationException("the pass never ran off its end");
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

        // Starting again from held silence goes straight to the file.
        player.StartHoldingSilenceAtEnd();
        var first = MicBuffer();
        player.Process(first, Floats, Rate);
        foreach (float s in first) Assert.NotEqual(Mic, s);
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
        Assert.False(player.HoldingEndSilence);
        player.Stop();
        Assert.True(MicIsBack(player), "a disarm in the last ramp must not be lost to the hold");
        Assert.False(player.HoldingEndSilence);
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

    /// <summary>A real arm through <c>TxReferenceStartAsync</c>, keyed, played to its end.</summary>
    private static async Task<RigOnVendorRadio> ReferenceRunToItsEnd(string serial)
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
        RunToTheEnd(f.Rig.TxFilePlayer);
        Assert.True(f.Rig.TxReferenceEndSilenceHeld, "a reference armed by TxReferenceStartAsync holds silence at its end");
        return f;
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

    [Fact]
    public async Task A_disconnect_ends_the_silence()
    {
        using var f = await ReferenceRunToItsEnd("ref-silence-disconnect");
        typeof(FlexBase).GetMethod("DisconnectVoxPauses", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Rig, null);
        Assert.False(f.Rig.TxReferenceEndSilenceHeld);
        Assert.False(f.Rig.TxReferenceArmed);
        Assert.True(MicIsBack(f.Rig.TxFilePlayer));
    }

    /// <summary>An older wait still running cannot end a newer pass's silence.</summary>
    [Fact]
    public async Task Only_the_newest_wait_ends_the_silence()
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
}

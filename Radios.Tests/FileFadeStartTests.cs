#nullable enable
using System;
using System.Linq;
using System.Reflection;
using JJPortaudio;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// A start made while the transmit file player is fading a file out (a send's
/// natural end, or a disarm's Stop) goes straight to the new file: the
/// microphone is already off the encoder, so there is nothing of it to fade.
/// Until this repair the start read the fade as the microphone's and handed
/// the encoder the microphone at full level for 10 ms. A held reference's end
/// already did this (round four repair, part 6e); these are the other fades.
/// </summary>
/// <remarks>
/// Driven exactly as the audio thread drives the player, with "microphone"
/// buffers at 0.9, a value the file (0.4) never reaches, so a single sample
/// of microphone on the encoder is unmistakable.
/// </remarks>
public sealed class FileFadeStartTests
{
    private const int Rate = 48000;
    private const float Mic = 0.9f;
    private const float FileLevel = 0.4f;
    private const int SmallFloats = 240;     // 120 frames, 2.5 ms: the fade is four of these

    private const int StMicFadeOut = 1;
    private const int StFileFadeIn = 2;
    private const int StFileFadeOut = 4;
    private const int StMicFadeIn = 5;

    private static int State(TxFilePlayer player) =>
        (int)typeof(TxFilePlayer).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;

    private static float[] MicBuffer()
    {
        var buf = new float[SmallFloats];
        Array.Fill(buf, Mic);
        return buf;
    }

    private static float[] Content(int frames)
    {
        var c = new float[frames];
        Array.Fill(c, FileLevel);
        return c;
    }

    /// <summary>The highest sample the encoder is handed over the next <paramref name="buffers"/> buffers.</summary>
    private static float LoudestOver(TxFilePlayer player, int buffers)
    {
        float max = 0f;
        for (int b = 0; b < buffers; b++)
        {
            var buf = MicBuffer();
            player.Process(buf, SmallFloats, Rate);
            foreach (float s in buf) max = Math.Max(max, Math.Abs(s));
        }
        return max;
    }

    /// <summary>A send (plain Start) run until its file runs off its end: the player is then in the final fade.</summary>
    private static TxFilePlayer SendInItsFinalFade()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate / 10), Rate, "send");
        player.Start();
        for (int i = 0; i < 2000 && !player.ReachedEnd; i++)
            player.Process(MicBuffer(), SmallFloats, Rate);
        Assert.True(player.ReachedEnd, "positive control: the send ran off its end");
        Assert.True(State(player) == StFileFadeOut, "positive control: the schedule is inside the send's final fade");
        Assert.False(player.HoldingEndSilence, "positive control: a send holds no silence, so the old rule did not cover it");
        return player;
    }

    [Fact]
    public void A_start_inside_a_sends_final_fade_never_puts_the_microphone_on_the_encoder()
    {
        var player = SendInItsFinalFade();

        player.Start();   // the Fixer's voice stage, starting as the send ends

        Assert.Equal(StFileFadeIn, State(player));
        Assert.InRange(LoudestOver(player, 12), 0f, FileLevel + 1e-5f);
        Assert.True(player.Engaged);
        player.Stop();
    }

    [Fact]
    public void A_start_inside_a_disarms_fade_never_puts_the_microphone_on_the_encoder()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate), Rate, "send");
        player.Process(MicBuffer(), SmallFloats, Rate);   // the microphone flowing: not a cold start
        player.Start();
        // The microphone's 10 ms ramp out is four buffers; the fifth is the
        // file's own ramp in. A disarm from there fades the file out over
        // the rest of the ramp. (From full level the fade lasts one frame:
        // Stop leaves the ramp counter where the ramp in finished it, which
        // is reported separately, not changed here.)
        for (int i = 0; i < 5; i++) player.Process(MicBuffer(), SmallFloats, Rate);
        Assert.True(State(player) == StFileFadeIn, "positive control: the file is ramping in");

        player.Stop();   // the disarm: the file fades out
        player.Process(MicBuffer(), SmallFloats, Rate);
        Assert.True(State(player) == StFileFadeOut, "positive control: inside the disarm's fade");

        player.Start();

        Assert.Equal(StFileFadeIn, State(player));
        Assert.InRange(LoudestOver(player, 12), 0f, FileLevel + 1e-5f);
        player.Stop();
    }

    /// <summary>
    /// The control that makes the two above mean something: from the
    /// microphone (no pass running), a start DOES ramp the microphone out,
    /// so the encoder carries it, at up to its full level, for the first
    /// 10 ms. The tests above would see 0.9 if a start from a fade did it.
    /// </summary>
    [Fact]
    public void A_start_from_the_microphone_still_fades_the_microphone_out_first()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate / 10), Rate, "send");
        player.Process(MicBuffer(), SmallFloats, Rate);   // idle: the microphone passes

        player.Start();

        Assert.Equal(StMicFadeOut, State(player));
        var first = MicBuffer();
        player.Process(first, SmallFloats, Rate);
        Assert.Equal(Mic, first[0], 4);   // the ramp begins at the microphone's full level
        Assert.True(first[^1] < Mic);
        player.Stop();
    }

    /// <summary>
    /// A start while the microphone is ramping back in after a send fades it
    /// out from the level it had reached. Until then it began the ramp out at
    /// full level, a step up from wherever the ramp back had got to.
    /// </summary>
    [Fact]
    public void A_start_while_the_microphone_ramps_back_fades_it_out_from_the_level_it_reached()
    {
        var player = SendInItsFinalFade();
        for (int i = 0; i < 4 && State(player) != StMicFadeIn; i++)
            player.Process(MicBuffer(), SmallFloats, Rate);   // the file's fade runs out
        Assert.True(State(player) == StMicFadeIn, "positive control: the microphone is ramping back");
        var rampBack = MicBuffer();
        player.Process(rampBack, SmallFloats, Rate);   // a quarter of the way back
        float reached = rampBack[^1];
        Assert.InRange(reached, 0.05f, Mic * 0.5f);   // positive control: partly back, well short of full

        player.Start();

        Assert.Equal(StMicFadeOut, State(player));
        // The ramp out starts one ramp step above where the buffer above
        // ended (the counter had moved on by one frame), and only goes down.
        var next = MicBuffer();
        player.Process(next, SmallFloats, Rate);
        Assert.InRange(next[0], reached, reached + 0.003f);
        Assert.InRange(next.Max(), 0f, reached + 0.003f);
        player.Stop();
    }

    /// <summary>A send still ends as it did: the microphone comes back once its fade is over.</summary>
    [Fact]
    public void A_send_left_alone_still_hands_the_microphone_back()
    {
        var player = SendInItsFinalFade();
        for (int i = 0; i < 12; i++) player.Process(MicBuffer(), SmallFloats, Rate);   // the fade, then the microphone's ramp
        Assert.True(player.Idle);
        var after = MicBuffer();
        player.Process(after, SmallFloats, Rate);
        Assert.All(after, s => Assert.Equal(Mic, s, 5));
    }
}

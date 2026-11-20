#nullable enable
using System;
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
    private const int StFile = 3;
    private const int StFileFadeOut = 4;

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
        // The microphone's 10 ms ramp out is four buffers; the fifth and
        // sixth are the file's own ramp in, to about half level. A disarm
        // from there fades the file out from that level, over half a ramp,
        // so one buffer later it is still inside the fade. (Five buffers
        // until batch 17: the disarm then faded from the wrong level, and
        // from the right one a quarter-level fade ends inside one buffer.)
        for (int i = 0; i < 6; i++) player.Process(MicBuffer(), SmallFloats, Rate);
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

    // ---- a disarm fades the file out from the level it had (#1128) ----

    private const int FadeFrames = Rate / 100;   // 10 ms

    private static int BufferEndState(TxFilePlayer player) =>
        (int)typeof(TxFilePlayer).GetField("_bufferEndState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;

    /// <summary>The left channel of the next <paramref name="buffers"/> buffers, one value per frame.</summary>
    private static float[] FramesOver(TxFilePlayer player, int buffers)
    {
        var frames = new float[buffers * SmallFloats / 2];
        for (int b = 0; b < buffers; b++)
        {
            var buf = MicBuffer();
            player.Process(buf, SmallFloats, Rate);
            for (int f = 0; f < SmallFloats / 2; f++) frames[b * SmallFloats / 2 + f] = buf[f * 2];
        }
        return frames;
    }

    /// <summary>A send with the microphone flowing, run until its file plays at full level.</summary>
    private static TxFilePlayer FileAtLevel()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate), Rate, "send");
        player.Process(MicBuffer(), SmallFloats, Rate);   // the microphone flowing: not a cold start
        player.Start();
        for (int i = 0; i < 40 && State(player) != StFile; i++)
            player.Process(MicBuffer(), SmallFloats, Rate);
        Assert.True(State(player) == StFile, "positive control: the file plays at level");
        var at = MicBuffer();
        player.Process(at, SmallFloats, Rate);
        Assert.Equal(FileLevel, at[^1], 4);   // positive control: full level as the disarm comes
        return player;
    }

    /// <summary>Index of the first frame that is louder than the one before it: where the fade ends and the microphone comes back.</summary>
    private static int EndOfFall(float[] frames)
    {
        for (int i = 1; i < frames.Length; i++)
            if (frames[i] > frames[i - 1] + 1e-6f) return i;
        return frames.Length;
    }

    /// <summary>
    /// A disarm while the file plays at full level fades it out over 10 ms,
    /// from full level, then hands the microphone back from silence. Until
    /// batch 17 the fade carried on from the counter the ramp in had left at
    /// 1, so the very first frame after the disarm was silent: the file was
    /// cut in one frame, a click on the air.
    /// </summary>
    [Fact]
    public void A_disarm_at_full_level_fades_the_file_out_over_ten_milliseconds()
    {
        var player = FileAtLevel();

        player.Stop();
        Assert.False(player.Engaged, "the disarm is seen at once by callers");
        var frames = FramesOver(player, 6);

        Assert.InRange(frames[0], FileLevel - 0.002f, FileLevel + 1e-5f);
        int end = EndOfFall(frames);
        Assert.InRange(end, FadeFrames - 2, FadeFrames + 2);
        for (int i = 0; i < end; i++)
            Assert.InRange(frames[i], 0f, FileLevel + 1e-5f);   // no microphone sample inside the fade
        Assert.InRange(frames[end - 1], 0f, 0.002f);           // the fade reaches silence
        Assert.True(frames[^1] > frames[end], "then the microphone ramps back in");
    }

    /// <summary>
    /// A disarm in the file's ramp in fades it out from the level it had
    /// reached, not from one minus it. Until batch 17 a disarm a quarter of
    /// the way up jumped to three quarters.
    /// </summary>
    [Fact]
    public void A_disarm_during_the_ramp_in_fades_out_from_the_level_reached()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate), Rate, "send");
        player.Process(MicBuffer(), SmallFloats, Rate);
        player.Start();
        for (int i = 0; i < 4 && State(player) != StFileFadeIn; i++)
            player.Process(MicBuffer(), SmallFloats, Rate);
        Assert.True(State(player) == StFileFadeIn, "positive control: the file is ramping in");
        var last = MicBuffer();
        player.Process(last, SmallFloats, Rate);
        Assert.True(State(player) == StFileFadeIn, "positive control: still ramping in");
        float reached = last[^1];
        Assert.InRange(reached, 0.02f, FileLevel * 0.6f);   // positive control: part way up

        player.Stop();
        var frames = FramesOver(player, 4);

        Assert.InRange(frames[0], reached - 0.002f, reached + 0.002f);
        int end = EndOfFall(frames);
        Assert.InRange(frames[end - 1], 0f, 0.002f);
        for (int i = 0; i < end; i++)
            Assert.InRange(frames[i], 0f, reached + 0.002f);
    }

    /// <summary>
    /// A disarm while the microphone is still ramping out, before any of the
    /// file has been sent, plays none of the file: the microphone ramps back
    /// in from where it had got to. Until batch 17 a short ramp of the file
    /// went out first.
    /// </summary>
    [Fact]
    public void A_disarm_before_the_file_starts_sends_none_of_it()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate), Rate, "send");
        player.Process(MicBuffer(), SmallFloats, Rate);
        player.Start();
        var ramp = MicBuffer();
        player.Process(ramp, SmallFloats, Rate);
        Assert.True(State(player) == StMicFadeOut, "positive control: the microphone is ramping out");
        float micLevel = ramp[^1];
        Assert.InRange(micLevel, Mic * 0.5f, Mic * 0.9f);   // positive control: part way down

        player.Stop();
        var frames = FramesOver(player, 4);

        Assert.Equal(0, player.PlayedFrames);   // not one frame of the file
        Assert.InRange(frames[0], micLevel - 0.003f, micLevel + 0.003f);
        for (int i = 1; i < frames.Length; i++)
            Assert.True(frames[i] >= frames[i - 1] - 1e-6f, "the microphone only rises");
        Assert.Equal(Mic, frames[^1], 4);
        Assert.True(player.Idle);
    }

    /// <summary>
    /// The race the request closes: a disarm landing in the buffer where the
    /// file's ramp in completes, which then writes the file at level over the
    /// disarm. The audio thread's overwrite is played here by hand (a stop
    /// cannot land mid-buffer on one thread). Before batch 17 the file then
    /// played on to its end.
    /// </summary>
    [Fact]
    public void A_disarm_overwritten_by_the_ramp_in_completing_still_fades_the_file_out()
    {
        var player = new TxFilePlayer();
        player.Load(Content(Rate), Rate, "send");
        player.Process(MicBuffer(), SmallFloats, Rate);
        player.Start();
        for (int i = 0; i < 40 && State(player) != StFile; i++)
            player.Process(MicBuffer(), SmallFloats, Rate);
        Assert.True(BufferEndState(player) == StFile, "positive control: the buffer ended with the file at level");

        player.Stop();
        Assert.True(State(player) == StFileFadeOut, "positive control: the disarm was written");
        // What the audio thread's `_state = StFile`, as its ramp in completes, does to it.
        typeof(TxFilePlayer).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, StFile);

        var frames = FramesOver(player, 12);

        Assert.InRange(frames[0], FileLevel - 0.002f, FileLevel + 1e-5f);
        Assert.True(player.Idle, "the file faded out and the microphone came back");
        Assert.Equal(Mic, frames[^1], 4);
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

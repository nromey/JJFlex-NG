using System;
using System.IO;
using JJPortaudio;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The transmit injection contract (Sprint 33 Track I).
/// </summary>
/// <remarks>
/// <para>
/// These assert the properties the whole reference-audio idea rests on, and
/// they are worth having as tests rather than as careful reading because every
/// one of them is invisible on inspection and audible only on the air. The
/// injection point runs on the PortAudio callback thread inside a live
/// transmission; by the time a mistake here is noticeable, it has been
/// transmitted.
/// </para>
/// <para>
/// The one that matters most is REPLACEMENT, NEVER MIXING. A reference file
/// summed with a live microphone is not a reference file, and the failure is
/// silent: the measurement still produces a number, the number is just about
/// something else. Mixing at the wrong gain would look exactly like a quiet
/// room.
/// </para>
/// </remarks>
public class TxInjectionTests
{
    private const int Rate = 48000;
    private const int Frames = 480;          // one 10 ms Opus frame at 48 kHz
    private const int Floats = Frames * 2;   // interleaved stereo

    /// <summary>A buffer of "microphone" audio pinned at a value we can spot.</summary>
    private static float[] MicBuffer(float value)
    {
        var buf = new float[Floats];
        for (int i = 0; i < buf.Length; i++) buf[i] = value;
        return buf;
    }

    /// <summary>Content that is entirely one value, so mixing is unmistakable.</summary>
    private static float[] Content(float value, int frames)
    {
        var c = new float[frames];
        for (int i = 0; i < c.Length; i++) c[i] = value;
        return c;
    }

    [Fact]
    public void IdlePlayerLeavesTheMicrophoneAlone()
    {
        var player = new TxFilePlayer();
        player.Load(Content(0.5f, Rate), Rate, "test");

        var buf = MicBuffer(0.25f);
        player.Process(buf, Floats, Rate);

        Assert.All(buf, s => Assert.Equal(0.25f, s, 5));
        Assert.False(player.Engaged);
    }

    [Fact]
    public void EngagedPlayerReplacesTheMicrophoneRatherThanMixingWithIt()
    {
        var player = new TxFilePlayer();
        // Content is digital silence. If the player MIXED, the microphone's
        // 1.0 would survive at some level; only replacement can reach zero.
        player.Load(Content(0f, Rate), Rate, "silence");
        player.Start();

        // First call is a cold start (no previous Process), so it goes
        // straight to fading the content in — no microphone passes at all.
        var buf = MicBuffer(1.0f);
        player.Process(buf, Floats, Rate);
        // Ramp is 10 ms, exactly this frame, so the tail is fully replaced.
        Assert.Equal(0f, buf[Floats - 1], 5);
        Assert.Equal(0f, buf[Floats - 2], 5);

        // And once at level, every sample is content.
        buf = MicBuffer(1.0f);
        player.Process(buf, Floats, Rate);
        Assert.All(buf, s => Assert.Equal(0f, s, 5));
    }

    [Fact]
    public void ContentGoesToBothChannelsIdentically()
    {
        var player = new TxFilePlayer();
        var ramp = new float[Rate];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = (i % 100) / 100f;
        player.Load(ramp, Rate, "ramp");
        player.Start();

        var buf = MicBuffer(0f);
        player.Process(buf, Floats, Rate);   // fade-in frame
        buf = MicBuffer(0f);
        player.Process(buf, Floats, Rate);   // at level

        for (int i = 0; i < Floats; i += 2)
            Assert.Equal(buf[i], buf[i + 1], 6);
    }

    [Fact]
    public void PlaybackEndsAndHandsTheMicrophoneBack()
    {
        var player = new TxFilePlayer();
        // A fifth of a second of content: long enough to clear the ramps,
        // short enough to run out inside a handful of frames.
        player.Load(Content(0.4f, Rate / 5), Rate, "short");
        player.Start();

        // Run well past the end of the content.
        for (int i = 0; i < 60; i++)
        {
            var buf = MicBuffer(0.9f);
            player.Process(buf, Floats, Rate);
        }

        Assert.True(player.ReachedEnd);
        Assert.False(player.Engaged);

        // The microphone is genuinely back, untouched.
        var after = MicBuffer(0.9f);
        player.Process(after, Floats, Rate);
        Assert.All(after, s => Assert.Equal(0.9f, s, 5));
    }

    [Fact]
    public void StoppingRestoresTheMicrophone()
    {
        var player = new TxFilePlayer();
        player.Load(Content(0.4f, Rate), Rate, "long");
        player.Start();

        var buf = MicBuffer(0.9f);
        player.Process(buf, Floats, Rate);
        Assert.True(player.Engaged);

        player.Stop();
        // Fade the content out, then the microphone back in.
        for (int i = 0; i < 5; i++)
        {
            buf = MicBuffer(0.9f);
            player.Process(buf, Floats, Rate);
        }

        Assert.False(player.Engaged);
        var after = MicBuffer(0.9f);
        player.Process(after, Floats, Rate);
        Assert.All(after, s => Assert.Equal(0.9f, s, 5));
    }

    [Fact]
    public void AStreamGapRestartsTheRecordingFromTheBeginning()
    {
        // The transmit stream stops at unkey and starts again at the next
        // key-down. A pass that resumed mid-word would not be a repeatable
        // stimulus, which is the only reason a known file exists at all.
        var player = new TxFilePlayer();
        var counting = new float[Rate];
        for (int i = 0; i < counting.Length; i++) counting[i] = i / (float)counting.Length;
        player.Load(counting, Rate, "counting");
        player.Start();

        for (int i = 0; i < 4; i++)
            player.Process(MicBuffer(0f), Floats, Rate);
        Assert.True(player.PlayedSeconds > 0.02);

        // Simulate the stream having been stopped for longer than the gap
        // threshold, then restarted.
        System.Threading.Thread.Sleep(150);
        var buf = MicBuffer(0f);
        player.Process(buf, Floats, Rate);

        Assert.True(player.PlayedSeconds <= Frames / (double)Rate + 0.0005,
            $"expected the file to restart, but {player.PlayedSeconds:F4} s had played");
    }

    [Fact]
    public void AVoiceFileDoesNotBypassConditioningButAToneDoes()
    {
        // The whole point of sending a known VOICE is to measure the chain a
        // voice really travels. A tone is a calibrated reference and must
        // arrive at the encoder untouched. Getting these the wrong way round
        // produces measurements of a chain nobody uses.
        Assert.False(new TxFilePlayer().BypassesConditioning);
        Assert.True(new TxToneGenerator().BypassesConditioning);
    }

    [Fact]
    public void TheMuxLetsTheHighestPrioritySourceOwnTheBuffer()
    {
        var tone = new TxToneGenerator();
        var file = new TxFilePlayer();
        var mux = new TxInputSourceMux(tone, file);

        file.Load(Content(0f, Rate), Rate, "silence");

        // Nothing engaged: the microphone passes.
        var buf = MicBuffer(0.3f);
        mux.Process(buf, Floats, Rate);
        Assert.False(mux.Engaged);
        Assert.All(buf, s => Assert.Equal(0.3f, s, 5));

        // File only.
        file.Start();
        mux.Process(MicBuffer(1f), Floats, Rate);
        Assert.True(mux.Engaged);
        Assert.Same(file, mux.Active);
        Assert.False(mux.BypassesConditioning);

        // Tone as well: the calibrated source is listed first and wins, both
        // in what Active reports AND in what actually lands in the buffer.
        tone.LevelDb = -6;
        tone.Start();
        for (int i = 0; i < 4; i++) mux.Process(MicBuffer(0f), Floats, Rate);

        Assert.Same(tone, mux.Active);
        Assert.True(mux.BypassesConditioning);

        var final = MicBuffer(0f);
        mux.Process(final, Floats, Rate);
        // The file is digital silence and the tone is not, so any non-zero
        // sample proves the tone survived the file's pass over the buffer.
        bool sawTone = false;
        for (int i = 0; i < Floats; i++)
            if (Math.Abs(final[i]) > 0.001f) { sawTone = true; break; }
        Assert.True(sawTone, "the lower-priority source overwrote the winner");
    }

    [Fact]
    public void TheMuxKeepsCallingIdleSourcesSoTheirReleaseRampsFinish()
    {
        // An idle source still has to see the buffer: it stamps its stream-gap
        // clock and runs its ramp back to the microphone. Skipping idle
        // sources would hard-cut the mic on every release — the exact click
        // the ramps exist to prevent.
        var file = new TxFilePlayer();
        var mux = new TxInputSourceMux(file);
        file.Load(Content(0.5f, Rate), Rate, "content");
        file.Start();

        mux.Process(MicBuffer(0f), Floats, Rate);
        file.Stop();

        bool sawPartialMic = false;
        for (int i = 0; i < 5; i++)
        {
            var buf = MicBuffer(1f);
            mux.Process(buf, Floats, Rate);
            for (int j = 0; j < Floats; j++)
            {
                if (buf[j] > 0.001f && buf[j] < 0.999f) { sawPartialMic = true; break; }
            }
            if (sawPartialMic) break;
        }

        Assert.True(sawPartialMic,
            "the microphone came back with no ramp — that is an audible click");
    }

    // ── The handover contract (#208) ──────────────────────────────────────
    //
    // Idle became necessary the day a source stopped being fed by the capture
    // stream. While every frame came from the microphone, nothing had to ask:
    // the stream ran whether a source was engaged or not, so a release ramp
    // always got its buffers for free. A self-clocked source is started and
    // stopped AROUND the source it carries, so something has to know when the
    // handover back to the microphone is safe.

    [Fact]
    public void IdleIsNotSimplyTheOppositeOfEngaged()
    {
        // THE test, and the whole reason the member exists. Between "release
        // requested" and "release finished" a source is neither engaged nor
        // idle: it has stopped muting the microphone but is still ramping its
        // own signal down. Treating not-Engaged as safe-to-swap cuts the tone
        // dead mid-ramp, which is the click the ramps were written to prevent.
        var tone = new TxToneGenerator();
        Assert.True(tone.Idle);
        Assert.False(tone.Engaged);

        tone.Start();
        tone.Process(MicBuffer(0f), Floats, Rate);   // cold start -> tone fading in
        tone.Process(MicBuffer(0f), Floats, Rate);   // -> tone at level
        Assert.True(tone.Engaged);
        Assert.False(tone.Idle);

        tone.Stop();
        Assert.False(tone.Engaged);                  // the microphone is un-muted...
        Assert.False(tone.Idle);                     // ...but the tone is still sounding
    }

    [Fact]
    public void IdleBecomesTrueOnlyAfterTheReleaseRampHasFinished()
    {
        // The other half: it must eventually say yes, or the self-clocked
        // source would run forever and the microphone would never come back.
        var tone = new TxToneGenerator();
        tone.Start();
        tone.Process(MicBuffer(0f), Floats, Rate);
        tone.Process(MicBuffer(0f), Floats, Rate);
        tone.Stop();

        // Each ramp state is one 10 ms buffer at this rate; a handful of
        // buffers is comfortably enough to walk out of both of them.
        for (int i = 0; i < 5 && !tone.Idle; i++) tone.Process(MicBuffer(0f), Floats, Rate);

        Assert.True(tone.Idle,
            "the tone never reported idle, so the microphone would never get the slot back");
    }

    [Fact]
    public void AMuxIsIdleOnlyWhenEveryOneOfItsSourcesIs()
    {
        // Asking the mux is asking on behalf of all of them. A mux that
        // answered from the ENGAGED source alone would report idle while a
        // just-released source was still ramping — and whoever is supplying
        // the buffers would stop mid-ramp, which is the fault this whole
        // member exists to prevent.
        var tone = new TxToneGenerator();
        var file = new TxFilePlayer();
        var mux = new TxInputSourceMux(tone, file);

        Assert.True(mux.Idle);

        tone.Start();
        tone.Process(MicBuffer(0f), Floats, Rate);
        Assert.False(mux.Idle);

        tone.Stop();
        Assert.False(tone.Engaged);
        Assert.False(mux.Idle);      // still ramping, and the mux must say so

        for (int i = 0; i < 5 && !tone.Idle; i++) mux.Process(MicBuffer(0f), Floats, Rate);
        Assert.True(mux.Idle);
    }
}

/// <summary>
/// The recorder's file format (Sprint 33 Track I).
/// </summary>
public class WavWriterTests
{
    [Fact]
    public void WritesAWavAnythingCanOpen()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "jjflex-wavwriter-test-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var w = new WavWriter(path, 48000, 1))
            {
                var block = new float[4800];
                for (int i = 0; i < block.Length; i++) block[i] = 0.5f;
                w.Write(block, block.Length);
                Assert.Equal(4800, w.Frames);
                Assert.Equal(0.1, w.Seconds, 3);
            }

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + 4800 * 2, bytes.Length);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
            Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));

            Assert.Equal(1, BitConverter.ToInt16(bytes, 20));        // PCM
            Assert.Equal(1, BitConverter.ToInt16(bytes, 22));        // mono
            Assert.Equal(48000, BitConverter.ToInt32(bytes, 24));    // rate
            Assert.Equal(16, BitConverter.ToInt16(bytes, 34));       // bit depth
            Assert.Equal(4800u * 2, BitConverter.ToUInt32(bytes, 40)); // data size

            // The samples themselves, not just the header.
            short first = BitConverter.ToInt16(bytes, 44);
            Assert.InRange(first, 16000, 16600);   // 0.5 * 32767
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void ClampsRatherThanWrappingOnOverload()
    {
        // A sample past full scale that wrapped would flip polarity and click
        // viciously — in a file whose entire job is to be trustworthy.
        string path = Path.Combine(Path.GetTempPath(),
            "jjflex-wavwriter-clip-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var w = new WavWriter(path, 48000, 1))
            {
                w.Write(new[] { 2.0f, -2.0f, float.NaN, 1.0f }, 4);
            }

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(32767, BitConverter.ToInt16(bytes, 44));
            Assert.Equal(-32767, BitConverter.ToInt16(bytes, 46));
            Assert.Equal(0, BitConverter.ToInt16(bytes, 48));
            Assert.Equal(32767, BitConverter.ToInt16(bytes, 50));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

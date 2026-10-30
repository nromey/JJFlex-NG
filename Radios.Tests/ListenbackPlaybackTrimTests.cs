using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// An off-air take PLAYS, and says its length, from just before the first
/// keyed mark (#835, fix round 2026-10-28). Track C's countdown runs while the
/// take sets up and the capture starts during setup, so every take now opens
/// with seconds of receiver hiss before the key. heard.wav keeps every sample
/// on the IQ clock, because the analysis and the alignment read it by index;
/// the playback is a second file that starts at the key, less a quarter second.
/// </summary>
/// <remarks>
/// A synthetic take through the real <see cref="IqTakeWriter"/> and
/// <see cref="SsbTakeRenderer"/>, written to a temp folder. What it cannot see:
/// the real lag between the radio's key report and its audio in the IQ.
/// </remarks>
public sealed class ListenbackPlaybackTrimTests
{
    private const int Rate = 24000;

    [Fact]
    public void Three_seconds_of_setup_hiss_before_the_key_are_not_played_or_counted()
    {
        string directory = Take(keyAtSecond: 3, seconds: 5);
        try
        {
            var render = SsbTakeRenderer.Render(directory);
            // heard.wav is untouched: every pair, on the IQ clock.
            Assert.Equal(5 * Rate, render.Samples);
            Assert.Equal(5 * Rate, TakeAudio.ReadMono(render.Path).Samples.Length);

            long from = 3 * Rate - (long)Math.Round(Rate * SsbTakeRenderer.PlaybackLeadSeconds);
            var play = TakeAudio.ReadMono(render.PlaybackPath);
            Assert.NotEqual(render.Path, render.PlaybackPath);
            Assert.Equal(5 * Rate - from, play.Samples.Length);
            Assert.Equal((5 * Rate - from) / (double)Rate, render.PlaybackSeconds, 6);
            // The playback is the same samples, starting there.
            var heard = TakeAudio.ReadMono(render.Path).Samples;
            Assert.Equal(heard.Skip((int)from).Take(1000), play.Samples.Take(1000));
            Assert.Contains("playback-from-pair=" + from, File.ReadAllText(Path.Combine(directory, "render.txt")));
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [Fact]
    public void A_take_that_never_keyed_plays_whole()
    {
        string directory = Take(keyAtSecond: null, seconds: 2);
        try
        {
            var render = SsbTakeRenderer.Render(directory);
            Assert.Equal(2.0, render.PlaybackSeconds, 6);
            Assert.Equal(2 * Rate, TakeAudio.ReadMono(render.PlaybackPath).Samples.Length);
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [Fact]
    public void The_playback_start_never_goes_before_the_take()
    {
        Assert.Equal(0, SsbTakeRenderer.PlaybackStart(new[] { new TakeSpan(100, 2000) }, Rate));
        Assert.Equal(0, SsbTakeRenderer.PlaybackStart(Array.Empty<TakeSpan>(), Rate));
        Assert.Equal(Rate - Rate / 4, SsbTakeRenderer.PlaybackStart(new[] { new TakeSpan(Rate, 2 * Rate) }, Rate));
    }

    private static string Take(int? keyAtSecond, int seconds)
    {
        string directory = Path.Combine(Path.GetTempPath(), "jjflex-trim-" + Guid.NewGuid().ToString("N"));
        long ticks = Stopwatch.GetTimestamp();
        var facts = new Dictionary<string, string> { ["mode"] = "USB", ["tx-filter-low"] = "100", ["tx-filter-high"] = "3000",
            ["pan-centre-mhz"] = "14.2", ["tx-frequency-mhz"] = "14.2" };
        using var writer = new IqTakeWriter(directory, Rate, facts) { Active = true, EchoedRate = Rate };
        var random = new Random(7);
        for (int second = 0; second < seconds; ++second)
        {
            if (second == keyAtSecond)
                writer.Header.Event("keying", new { Source = "radio-TransmitChange", Keyed = true, AtPair = (long)second * Rate });
            var iq = new float[Rate * 2];
            for (int i = 0; i < iq.Length; ++i) iq[i] = random.Next(-30, 31) * 16f / 32768;
            writer.Accept(new(new(1, (byte)second, 0x02e3, 1, 2, (uint)(100 + second), 0, ticks + second * Stopwatch.Frequency), iq, 0, 0));
        }
        writer.Finish("operator-stop");
        return directory;
    }
}

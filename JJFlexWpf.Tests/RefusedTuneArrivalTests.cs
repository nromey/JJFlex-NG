using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A tune the radio was not given is never announced as an arrival (#790's
/// first bullet, #1023's 60 m leftover). The band jump, band navigation
/// (which jumps) and the 60 m channel stop say where they landed, move the
/// band memory and ask for a mode ONLY when the tune was admitted; refused,
/// they say "Unable to tune this frequency", the typed entry's refusal.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> Pure decisions and the source only, on an MTA thread,
/// asserted in every test. The admission itself is pinned in Radios.Tests
/// (<c>TuningReceiptTests</c>); these pin that the three paths ask it.
/// </remarks>
public sealed class RefusedTuneArrivalTests
{
    public RefusedTuneArrivalTests() => Lexicon.Load(Lexicon.Partitions);

    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    [Fact]
    public void A_refused_jump_says_the_typed_entrys_refusal_and_nothing_about_a_band()
    {
        AssertWindowless();
        var said = FreqOutHandlers.TypedTuneSpeech(FlexBase.TuneAnswer.Refused, VerbosityLevel.Terse);
        Assert.True(said.Beep);
        Assert.False(said.Arrived);
        Assert.Equal(Lexicon.Get("settings.tuning.unable_to_tune", VerbosityLevel.Terse), said.Sentence);
    }

    /// <summary>
    /// #1043 F3: a jump or a 60 m stop refused on a locked slice, or while the
    /// radio is transmitting, names that cause; every other cause keeps the
    /// typed entry's refusal word for word.
    /// </summary>
    [Fact]
    public void A_jump_refused_on_a_locked_slice_or_while_transmitting_names_that_cause()
    {
        AssertWindowless();
        var locked = FreqOutHandlers.RefusedTuneSpeech(FlexBase.TuneRefusal.SliceLocked, VerbosityLevel.Terse);
        Assert.True(locked.Beep);
        Assert.False(locked.Arrived);
        Assert.Equal(Lexicon.Get("settings.tuning.refused_slice_locked"), locked.Sentence);
        var keyed = FreqOutHandlers.RefusedTuneSpeech(FlexBase.TuneRefusal.Transmitting, VerbosityLevel.Terse);
        Assert.True(keyed.Beep);
        Assert.False(keyed.Arrived);
        Assert.Equal(Lexicon.Get("settings.tuning.refused_transmitting"), keyed.Sentence);
        Assert.NotEqual(locked.Sentence, keyed.Sentence);
        foreach (var other in new[] { FlexBase.TuneRefusal.Other, FlexBase.TuneRefusal.None })
            Assert.Equal(FreqOutHandlers.TypedTuneSpeech(FlexBase.TuneAnswer.Refused, VerbosityLevel.Terse),
                FreqOutHandlers.RefusedTuneSpeech(other, VerbosityLevel.Terse));
    }

    [Fact]
    public void The_refusal_reads_the_cause_the_radio_recorded_at_the_set()
    {
        AssertWindowless();
        string body = Method(Source("JJFlexWpf", "FreqOutHandlers.cs"), "internal void SayTuneRefused(");
        Assert.Contains("Rig?.TuneRefusedBecause", body, StringComparison.Ordinal);
        Assert.Contains("SayTypedTuneSpeech(RefusedTuneSpeech(why,", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_band_jump_asks_the_admission_before_anything_it_says_or_saves()
    {
        AssertWindowless();
        string body = Method(Source("JJFlexWpf", "FreqOutHandlers.cs"), "public void BandJump(");
        int tune = body.IndexOf("if (!TuneTo(targetFreq))", StringComparison.Ordinal);
        Assert.True(tune > 0, "the band jump tunes through TuneTo, which reads the admission");
        Assert.DoesNotContain("SetRXFrequency(targetFreq);", body, StringComparison.Ordinal);
        string refused = body.Substring(tune, body.IndexOf("return;", tune, StringComparison.Ordinal) - tune);
        Assert.Contains("SayTuneRefused(", refused, StringComparison.Ordinal);
        foreach (string after in new[] { "_lastBand = targetBand;", "BandJumpOutcome(", "BandBoundaryBeep()", "SaveBandMemory();" })
            Assert.True(body.IndexOf(after, StringComparison.Ordinal) > tune, after + " comes only after an admitted tune");
        // The current frequency saved BEFORE the jump stays: it is true.
        Assert.True(body.IndexOf("SaveCurrentToBandMemory();", StringComparison.Ordinal) < tune);
    }

    [Fact]
    public void Band_navigation_jumps_through_the_same_door()
    {
        AssertWindowless();
        string body = Method(Source("JJFlexWpf", "FreqOutHandlers.cs"), "public void BandNavigate(");
        Assert.Contains("BandJump(FlexBands[nextIndex]);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRXFrequency", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_60_meter_stop_moves_no_rotation_mode_or_memory_and_names_no_channel()
    {
        AssertWindowless();
        string body = Method(Source("JJFlexWpf", "MainWindow.xaml.cs"), "public void SixtyMeterChannelNavigate(");
        int write = body.IndexOf("RigControl.Frequency = stop.FrequencyHz;", StringComparison.Ordinal);
        int ask = body.IndexOf("if (RigControl.TuneAdmittedHere == false)", StringComparison.Ordinal);
        Assert.True(write > 0 && ask > write, "the admission is read right after the write");
        string refused = body.Substring(ask, body.IndexOf("return;", ask, StringComparison.Ordinal) - ask);
        Assert.Contains("SayTuneRefused(", refused, StringComparison.Ordinal);
        foreach (string after in new[] { "_sixtyMeterChannelIndex = stop.Index;", "FollowChannelStop(",
                                         "RigControl.Mode = stop.ChannelMode", "stop.Arrival", "SixtyMeterModeDelivery.Deliver(" })
            Assert.True(body.IndexOf(after, StringComparison.Ordinal) > ask, after + " comes only after an admitted stop");
    }

    [Fact]
    public void A_single_refused_step_stays_silent()
    {
        AssertWindowless();
        // As a step refused on a locked slice always has: TuneTo answers false
        // and the step returns without a word.
        string src = Source("JJFlexWpf", "FreqOutHandlers.cs");
        Assert.Contains("if (!TuneTo(newFreq)) return;", src, StringComparison.Ordinal);
        string tuneTo = Method(src, "private bool TuneTo(");
        Assert.Contains("return Rig?.TuneAdmittedHere != false;", tuneTo, StringComparison.Ordinal);
        Assert.DoesNotContain("Speak", tuneTo, StringComparison.Ordinal);
    }

    private static string Method(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature + " has moved");
        int open = src.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
        }
        throw new Xunit.Sdk.XunitException(signature + " does not close");
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}

using System;
using System.IO;
using NAudio.Wave;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A voice message's load does not measure loudness before the radio keys
/// (#718, step one; Noel, 2026-10-14: "That should just start streaming
/// instantaneously"), and everything that reports a level still measures.
/// </summary>
/// <remarks>
/// <b>Windowless.</b> A temporary WAV file, an unconnected <see cref="FlexBase"/>
/// whose transmit player takes samples into memory, and the loaders; no window
/// and no audio device. Each test asserts an MTA thread.
/// </remarks>
public sealed class SendLoadSkipsMeasureTests : IDisposable
{
    private readonly string _wav = Path.Combine(Path.GetTempPath(), "jjflex-718-" + Guid.NewGuid().ToString("N") + ".wav");
    private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

    public SendLoadSkipsMeasureTests()
    {
        // Two seconds of a 440 Hz tone at half scale: loud enough that a
        // measured load reports a real peak and loudness.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        using var writer = new WaveFileWriter(_wav, format);
        var samples = new float[48000 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / 48000.0);
        writer.WriteSamples(samples, 0, samples.Length);
    }

    public void Dispose()
    {
        try { File.Delete(_wav); } catch { }
        _rig.Dispose();
    }

    [Fact]
    public void The_send_path_loads_without_measuring()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(TxAudioFile.TryLoadPieceInto(_rig, _wav, 0, out var loaded, out string trouble), trouble);
        Assert.False(loaded!.Measured, "the send path measured loudness before keying");
        Assert.DoesNotContain("dBFS", loaded.Describe());
        Assert.DoesNotContain("LUFS", loaded.Describe());
        Assert.Equal(2.0, loaded.Seconds, 2);
    }

    [Fact]
    public void Everything_that_reports_a_level_still_measures()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // The reference audio's loader.
        Assert.True(TxAudioFile.TryLoad(_wav, 48000, out var reference, out string trouble), trouble);
        Assert.True(reference!.Measured);
        Assert.InRange(reference.PeakDb, -7f, -5f);   // half scale is about -6 dBFS
        Assert.Contains("LUFS", reference.Describe());

        // The Fix tool's, which transmits a reference and reports its level.
        Assert.True(TxAudioFile.TryLoadInto(_rig, _wav, out var fix, out trouble), trouble);
        Assert.True(fix!.Measured);

        // And the public piece loader keeps its old meaning.
        Assert.True(TxAudioFile.TryLoadPiece(_wav, 48000, 0, out var piece, out trouble), trouble);
        Assert.True(piece!.Measured);
    }
}

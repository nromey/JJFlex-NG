using System;
using System.IO;
using NAudio.Wave;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A send goes to the air in pieces, and nothing is shortened (#835, round 2).
/// </summary>
/// <remarks>
/// <para>
/// Until round 2 the loader quietly cut any file at fifteen minutes
/// (<see cref="TxAudioFile.MaxSeconds"/>): a twenty-minute recorded QSO went
/// out as fifteen, and nothing said so. With a send pausing at ten minutes so
/// the operator can identify (Noel, 2026-10-27 20:48 and 20:50), a recording
/// must go out whole, in pieces, each loaded from where the last paused. These
/// tests write a WAV whose every second carries its own level, so a sample
/// says which second of the file it came from.
/// </para>
/// <para>
/// <b>Windowless.</b> Nothing here constructs a WPF element or opens an audio
/// device: the loader decodes a file into an array. Each test asserts it runs
/// on an MTA thread, where WPF refuses to build any window
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>). It speaks
/// nothing either: the one refusal sentence comes back as a string.
/// </para>
/// </remarks>
public sealed class SendPieceLoaderTests : IDisposable
{
    private const int Rate = 8000;   // a low rate keeps a sixteen-minute file small
    private readonly string _dir;

    public SendPieceLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jjflex-send-pieces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The level every sample of second <paramref name="second"/> carries.</summary>
    private static float LevelOf(int second) => ((second % 50) + 1) / 64f;

    private string WriteRecording(string name, double seconds)
    {
        string path = Path.Combine(_dir, name + ".wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 1));
        long frames = (long)Math.Round(seconds * Rate);
        var block = new float[Rate];
        for (long done = 0; done < frames; )
        {
            int second = (int)(done / Rate);
            int n = (int)Math.Min(block.Length, frames - done);
            for (int i = 0; i < n; i++) block[i] = LevelOf(second);
            writer.WriteSamples(block, 0, n);
            done += n;
        }
        return path;
    }

    private static int SecondAt(float[] mono, int index)
    {
        // Invert LevelOf modulo 50: the level names the second within its fifty.
        return (int)Math.Round(mono[index] * 64f) - 1;
    }

    [Fact]
    public void ASixteenMinuteRecordingIsNeverShortened_ItsFirstPieceSaysMoreFollows()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string path = WriteRecording("sixteen", 960);

        Assert.True(TxAudioFile.TryLoadPiece(path, Rate, 0, out var piece, out string trouble), trouble);

        Assert.Equal(TxAudioFile.MaxSeconds, piece!.Seconds, 3);
        Assert.False(piece.ReachesEnd);
        Assert.Equal(960.0, piece.TotalSeconds, 1);
        Assert.Equal(960.0, piece.LengthSeconds, 1);
    }

    [Fact]
    public void TheWholeFileLoaderRefusesALongRecordingInASentenceRatherThanCuttingIt()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string path = WriteRecording("sixteen", 960);

        Assert.False(TxAudioFile.TryLoad(path, Rate, out var loaded, out string trouble));
        Assert.Null(loaded);
        Assert.Contains("sixteen", trouble, StringComparison.Ordinal);
        Assert.Contains("15 minutes", trouble, StringComparison.Ordinal);
    }

    [Fact]
    public void APieceCarriesOnFromTheHeldPlace_TheVerySecondItPausedAt()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string path = WriteRecording("sixteen", 960);

        Assert.True(TxAudioFile.TryLoadPiece(path, Rate, VoiceMessageSend.PauseAfterSeconds, out var piece,
            out string trouble), trouble);

        Assert.Equal(600.0, piece!.FromSeconds);
        Assert.Equal(360.0, piece.Seconds, 3);
        Assert.True(piece.ReachesEnd);
        Assert.Equal(960.0, piece.LengthSeconds, 3);
        // Its first sample is second 600's; five seconds in is second 605's.
        Assert.Equal(600 % 50, SecondAt(piece.Mono, 10));
        Assert.Equal(605 % 50, SecondAt(piece.Mono, 5 * Rate + 10));
    }

    [Fact]
    public void TwoPiecesSplitAtTheTenMinutePauseAreTheWholeRecording_NothingLostNothingTwice()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string path = WriteRecording("twenty", 1200);

        Assert.True(TxAudioFile.TryLoadPiece(path, Rate, 0, out var first, out string t1), t1);
        Assert.True(TxAudioFile.TryLoadPiece(path, Rate, 600, out var second, out string t2), t2);
        Assert.True(TxAudioFile.TryLoadPiece(path, Rate, 0 + 600 + 600, out var tail, out _) == false
                    || tail!.Seconds < 0.01,
            "a piece from the very end has nothing in it");

        // The first piece goes out until the pause at ten minutes; the second
        // carries on from there and is the rest.
        Assert.False(first!.ReachesEnd);
        Assert.True(second!.ReachesEnd);
        Assert.Equal(600.0, second.Seconds, 3);
        for (int s = 0; s < 600; s += 37)
            Assert.Equal(s % 50, SecondAt(first.Mono, s * Rate + Rate / 2));
        for (int s = 0; s < 600; s += 37)
            Assert.Equal((600 + s) % 50, SecondAt(second.Mono, s * Rate + Rate / 2));
        // The seam: the last sample before the pause and the first after it.
        Assert.Equal(599 % 50, SecondAt(first.Mono, 600 * Rate - 1));
        Assert.Equal(600 % 50, SecondAt(second.Mono, 0));
    }

    [Fact]
    public void ARecordingOfExactlyFifteenMinutesLoadsWhole_OneSampleMoreDoesNot()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string exact = WriteRecording("exact", TxAudioFile.MaxSeconds);
        string over = WriteRecording("over", TxAudioFile.MaxSeconds + 0.5);

        Assert.True(TxAudioFile.TryLoadPiece(exact, Rate, 0, out var whole, out string t1), t1);
        Assert.True(whole!.ReachesEnd);
        Assert.True(TxAudioFile.TryLoad(exact, Rate, out _, out string t2), t2);

        Assert.True(TxAudioFile.TryLoadPiece(over, Rate, 0, out var cut, out string t3), t3);
        Assert.False(cut!.ReachesEnd);
    }

    [Fact]
    public void APieceIsAlwaysLongerThanTheTenMinutesBeforeTheSendPauses()
    {
        // A piece that is not the file's end must never run out before the
        // pause, or the send would report "sent" with the rest unsent.
        Assert.True(TxAudioFile.MaxSeconds > VoiceMessageSend.PauseAfterSeconds + VoiceMessageSend.PauseEndMarginSeconds);
    }

    [Fact]
    public void TheLengthIsReadFromTheHeaderWithoutDecoding()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string path = WriteRecording("short", 200);
        Assert.True(TxAudioFile.TryMeasureSeconds(path, out double seconds));
        Assert.Equal(200.0, seconds, 2);
        Assert.False(TxAudioFile.TryMeasureSeconds(Path.Combine(_dir, "absent.wav"), out _));
    }
}

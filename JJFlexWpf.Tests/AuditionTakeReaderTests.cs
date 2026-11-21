using System;
using System.IO;
using System.Threading;
using JJFlexWpf.Dialogs;
using JJPortaudio;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The audition's own reader for its take (#262, Sprint 49 Track Q phase B
/// repair 2, R3): the file is read into memory a piece at a time with the
/// cancel checked between pieces, closed, then decoded by the reference audio's
/// recipe. So a cancelled worker lets go of the take file promptly, and the
/// samples are the ones the reference loader would have produced.
/// </summary>
/// <remarks>Windowless: asserts <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>; temporary files only; nothing played.</remarks>
public sealed class AuditionTakeReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jjflex-audition-reader-" + Guid.NewGuid().ToString("N"));

    public AuditionTakeReaderTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        CheckYourAudioDialog.AfterTakePieceReadForTest = null;
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Wav(int rate, double seconds)
    {
        string path = Path.Combine(_folder, "take-" + rate + ".wav");
        var samples = ChainAuditionTwinTests.Take(seconds);
        using var w = new WavWriter(path, rate, 1);
        w.Write(samples, samples.Length);
        w.Finish();
        return path;
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void The_take_decodes_to_exactly_what_the_reference_loader_gives(int rate)
    {
        string path = Wav(rate, 4);
        var mine = CheckYourAudioDialog.LoadAuditionTake(path, CancellationToken.None);
        Assert.True(TxAudioFile.TryLoad(path, 48000, out var reference, out string trouble), trouble);
        Assert.NotNull(mine);
        Assert.Equal(48000, mine!.Value.Rate);
        Assert.Equal(reference!.Mono, mine.Value.Mono);
    }

    [Fact]
    public void A_cancel_between_pieces_stops_the_read_and_lets_go_of_the_file()
    {
        string path = Wav(48000, 8);   // about 768 KB: several pieces
        Assert.Equal(File.ReadAllBytes(path), CheckYourAudioDialog.ReadTakeCancellably(path, CancellationToken.None));   // positive control

        using var cts = new CancellationTokenSource();
        int pieces = 0;
        bool heldDuringRead = false;
        CheckYourAudioDialog.AfterTakePieceReadForTest = () =>
        {
            if (++pieces != 1) return;
            // Mid-read the file is open: an exclusive open fails.
            try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { heldDuringRead = true; }
            cts.Cancel();
        };
        Assert.ThrowsAny<OperationCanceledException>(() => CheckYourAudioDialog.ReadTakeCancellably(path, cts.Token));
        Assert.True(heldDuringRead);
        Assert.Equal(1, pieces);
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }   // let go
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}

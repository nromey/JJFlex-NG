using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The recordings dialog's preview transport and its trimmed Save As (#787,
/// Tape Deck track 4): pause, resume, a seek while paused, a start part-way
/// in, and a range saved to the exact sample.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless, proved rather than assumed.</b> Every test asserts it runs on
/// an MTA thread, where WPF throws on constructing any element, so nothing
/// here can put a window on the operator's desktop
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>), and that the
/// harness has silenced output and bound a throwaway settings root
/// (<see cref="QuietRun"/>, <see cref="TestSettingsRoot"/>). The player is fed
/// a stand-in mixer, so no audio device is opened either.
/// </para>
/// <para>
/// What these cannot see: how far behind the speakers the mixer reads on a
/// real device. The heard position subtracts the output's measured latency;
/// with no device open that is the player's 200 ms default.
/// </para>
/// </remarks>
public sealed class RecordedQsoTransportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-qso-transport-" + Guid.NewGuid().ToString("N"));

    public RecordedQsoTransportTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        RecordingPlayer.StopWithoutSpeaking();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Wav(double seconds, int rate = 44100)
    {
        string path = Path.Combine(_dir, "take-" + Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
        var samples = Enumerable.Repeat(0.5f, (int)(seconds * rate)).ToArray();
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static int ReadOnce(ISampleProvider input, float[] buffer) => input.Read(buffer.AsSpan());

    [Fact]
    public void A_preview_starts_part_way_in_pauses_to_silence_moves_while_paused_and_resumes()
    {
        ISampleProvider? input = null;
        string path = Wav(3);
        Assert.True(RecordingPlayer.PlayWithMixer(path, "A test recording.", null, false,
            p => { input = p; return true; }, startSeconds: 1.0));
        Assert.NotNull(input);
        var buffer = new float[4410 * 2]; // 0.1 s of the mixer's stereo

        Assert.Equal(buffer.Length, ReadOnce(input!, buffer));
        Assert.Contains(buffer, s => s != 0);
        // Playing, the heard position trails the mixer by the output latency,
        // and never reads earlier than where the preview started.
        Assert.Equal(1.0, RecordingPlayer.PositionSeconds!.Value, 2);

        Assert.True(RecordingPlayer.Pause());
        Assert.True(RecordingPlayer.IsPaused);
        Assert.True(RecordingPlayer.IsPlaying, "paused is still playing: the preview is not over");
        double pausedAt = RecordingPlayer.PositionSeconds!.Value;
        Assert.InRange(pausedAt, 1.09, 1.2);
        Array.Fill(buffer, 1f);
        // A full read of silence: a short one would end the file for the mixer.
        Assert.Equal(buffer.Length, ReadOnce(input!, buffer));
        Assert.All(buffer, s => Assert.Equal(0f, s));
        Assert.Equal(pausedAt, RecordingPlayer.PositionSeconds!.Value, 3);

        Assert.True(RecordingPlayer.Seek(2.5));
        Assert.Equal(2.5, RecordingPlayer.PositionSeconds!.Value, 3);
        Assert.True(RecordingPlayer.Resume());
        Assert.False(RecordingPlayer.IsPaused);
        Assert.False(RecordingPlayer.Seek(0.5)); // moving through sounding audio waits for a later round
        Assert.Equal(buffer.Length, ReadOnce(input!, buffer));
        Assert.Contains(buffer, s => s != 0);
    }

    [Fact]
    public void Nothing_to_pause_resume_or_move_when_nothing_is_playing()
    {
        RecordingPlayer.StopWithoutSpeaking();
        Assert.False(RecordingPlayer.Pause());
        Assert.False(RecordingPlayer.Resume());
        Assert.False(RecordingPlayer.Seek(1));
        Assert.Null(RecordingPlayer.PositionSeconds);
    }

    [Fact]
    public void A_preview_that_plays_out_ends_like_any_other_playback()
    {
        ISampleProvider? input = null;
        string path = Wav(0.2);
        Assert.True(RecordingPlayer.PlayWithMixer(path, "A test recording.", null, false,
            p => { input = p; return true; }, startSeconds: 0.1));
        var buffer = new float[44100 * 2];
        Assert.True(ReadOnce(input!, buffer) < buffer.Length);
        Assert.True(SpinWait.SpinUntil(() => !RecordingPlayer.IsPlaying, TimeSpan.FromSeconds(2)));
    }

    // ── Save As of a trim ───────────────────────────────────────────────

    private static readonly object ResolverGate = new();
    private static bool _resolverSet;

    private static void FlacLibraryReady()
    {
        lock (ResolverGate)
        {
            if (_resolverSet) return;
            NativeLibrary.SetDllImportResolver(typeof(RecordedQsoFlacDecoder).Assembly, (name, _, _) =>
            {
                if (!name.Equals("libFLAC.dll", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                string arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";
                string path = Path.Combine(AppContext.BaseDirectory, "runtimes", arch, "native", "libFLAC.dll");
                if (!File.Exists(path))
                    path = Path.GetFullPath(Path.Combine(RepoPaths.RequireRoot(), "runtimes", arch, "native", "libFLAC.dll"));
                return NativeLibrary.Load(path);
            });
            _resolverSet = true;
        }
    }

    private string Flac(int rate, int frames)
    {
        string path = Path.Combine(_dir, "QSO-2026-10-24-153012.flac");
        var samples = new float[frames];
        for (int i = 0; i < frames; i++) samples[i] = (float)(0.3 * Math.Sin(i * (0.01 + i * 1e-6)));
        using (var writer = new RecordedQsoFlac(path, rate, 1))
            for (int i = 0; i < frames; i += 240)
                writer.Write(samples.AsSpan(i, Math.Min(240, frames - i)).ToArray());
        return path;
    }

    private static short[] Samples(string wav)
    {
        using var reader = new WaveFileReader(wav);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        var bytes = new byte[reader.Length];
        int n = reader.Read(bytes, 0, bytes.Length);
        var result = new short[n / 2];
        Buffer.BlockCopy(bytes, 0, result, 0, n);
        return result;
    }

    [Fact]
    public void Save_As_writes_exactly_the_trimmed_frames_and_leaves_the_original_alone()
    {
        FlacLibraryReady();
        string source = Flac(24000, 24000 * 2);
        byte[] before = File.ReadAllBytes(source);
        string whole = Path.Combine(_dir, "whole.wav");
        string part = Path.Combine(_dir, "part.wav");
        RecordedQsoExport.Convert(source, whole, RecordedQsoExport.Format.Wav, 1);
        RecordedQsoExport.Convert(source, part, RecordedQsoExport.Format.Wav, 1, 0, 6000, 30000);

        short[] all = Samples(whole);
        short[] trimmed = Samples(part);
        Assert.Equal(48000, all.Length);
        Assert.Equal(24000, trimmed.Length);
        Assert.Equal(all.Skip(6000).Take(24000), trimmed);
        Assert.Equal(before, File.ReadAllBytes(source));

        // To the end, from a start: the null end.
        string tail = Path.Combine(_dir, "tail.wav");
        RecordedQsoExport.Convert(source, tail, RecordedQsoExport.Format.Wav, 1, 0, 40000, null);
        Assert.Equal(all.Skip(40000), Samples(tail));
    }

    [Fact]
    public void A_range_that_ends_before_it_starts_is_refused_and_writes_nothing()
    {
        FlacLibraryReady();
        string source = Flac(24000, 24000);
        string out1 = Path.Combine(_dir, "bad.wav");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecordedQsoExport.Convert(source, out1, RecordedQsoExport.Format.Wav, 1, 0, 2000, 2000));
        Assert.False(File.Exists(out1));
    }
}

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
        // The pause fades the recording out rather than cutting it (retest
        // 2026-10-25, the crossfade): the first read after it starts loud and
        // ends silent; the next is silence. Both are full reads, because a
        // short one would end the file for the mixer. This asserted instant
        // silence until then, which was the cut Noel heard.
        Assert.Equal(buffer.Length, ReadOnce(input!, buffer));
        Assert.True(Math.Abs(buffer[0]) > 0.3f, "the fade starts near full level");
        Assert.Equal(0f, buffer[^1]);
        Array.Fill(buffer, 1f);
        Assert.Equal(buffer.Length, ReadOnce(input!, buffer));
        Assert.All(buffer, s => Assert.Equal(0f, s));
        Assert.Equal(pausedAt, RecordingPlayer.PositionSeconds!.Value, 3);

        Assert.True(RecordingPlayer.Seek(2.5));
        Assert.Equal(2.5, RecordingPlayer.PositionSeconds!.Value, 3);
        Assert.True(RecordingPlayer.Resume());
        Assert.False(RecordingPlayer.IsPaused);
        Assert.Equal(buffer.Length, ReadOnce(input!, buffer));
        Assert.Contains(buffer, s => s != 0);

        // Moving while it sounds (bench 2026-10-25). This line asserted the
        // REFUSAL until then, which pinned the defect Noel heard: every
        // Right Arrow announced a new position and the audio never jumped.
        // Back from 2.6 to 0.5: three more reads put the mixer at 0.8 and the
        // heard position, 200 ms behind it, at 0.6. Had the seek been refused
        // it would read past 2.6.
        Assert.True(RecordingPlayer.Seek(0.5));
        Assert.Equal(0.5, RecordingPlayer.PositionSeconds!.Value, 3);
        for (int i = 0; i < 3; i++) Assert.Equal(buffer.Length, ReadOnce(input!, buffer));
        Assert.InRange(RecordingPlayer.PositionSeconds!.Value, 0.55, 0.65);
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

    // Bench 2026-10-25: a recording played back over the band at full
    // level ("Right now it plays on top"). The radio's audio on this
    // computer now follows the operator's choice for exactly the life of
    // a playback, and comes back on the one path every playback ends by.
    [Fact]
    public void The_radio_audio_is_lowered_for_the_life_of_a_playback_and_comes_back_when_it_ends()
    {
        var before = RxPlaybackLevel.Level;
        try
        {
            RxPlaybackLevel.Level = PlaybackRadioLevel.Lowered12;
            Assert.Equal(1f, RxPlaybackLevel.TargetGain);

            Assert.True(RecordingPlayer.PlayWithMixer(Wav(1), "A test recording.", null, false, _ => true));
            Assert.True(RxPlaybackLevel.Active);
            Assert.Equal(0.251f, RxPlaybackLevel.TargetGain, 3);

            // Paused, the band comes back; resumed, it drops again (retest
            // 2026-10-25: "pausing still should probably bring audio back up").
            Assert.True(RecordingPlayer.Pause());
            Assert.Equal(1f, RxPlaybackLevel.TargetGain);
            Assert.True(RecordingPlayer.Resume());
            Assert.Equal(0.251f, RxPlaybackLevel.TargetGain, 3);

            RecordingPlayer.StopWithoutSpeaking();
            Assert.False(RxPlaybackLevel.Active);
            Assert.Equal(1f, RxPlaybackLevel.TargetGain);

            // A playback that never reaches a mixer ends at once, and the
            // radio is not left lowered behind it.
            Assert.False(RecordingPlayer.PlayWithMixer(Wav(1), "A test recording.", null, false, _ => false));
            Assert.False(RxPlaybackLevel.Active);
        }
        finally { RxPlaybackLevel.Level = before; }
    }

    [Fact]
    public void Each_playback_level_is_its_drop_in_dB_muted_is_zero_and_an_unknown_name_is_the_default()
    {
        Assert.Equal(1f, RxPlaybackLevel.GainFor(PlaybackRadioLevel.Unchanged));
        Assert.Equal(0.501f, RxPlaybackLevel.GainFor(PlaybackRadioLevel.Lowered6), 3);
        Assert.Equal(0.251f, RxPlaybackLevel.GainFor(PlaybackRadioLevel.Lowered12), 3);
        Assert.Equal(0.126f, RxPlaybackLevel.GainFor(PlaybackRadioLevel.Lowered18), 3);
        Assert.Equal(0.063f, RxPlaybackLevel.GainFor(PlaybackRadioLevel.Lowered24), 3);
        Assert.Equal(0f, RxPlaybackLevel.GainFor(PlaybackRadioLevel.Muted));
        Assert.Equal(6, RxPlaybackLevel.Choices.Length);
        Assert.Equal(PlaybackRadioLevel.Lowered12, RxPlaybackLevel.DefaultLevel);
        Assert.Equal(PlaybackRadioLevel.Muted, RxPlaybackLevel.ParseLevel("Muted"));
        Assert.Equal(PlaybackRadioLevel.Lowered12, RxPlaybackLevel.ParseLevel("Lowered99"));
        Assert.Equal(PlaybackRadioLevel.Lowered12, RxPlaybackLevel.ParseLevel(null));
        foreach (var level in RxPlaybackLevel.Choices)
            Assert.False(string.IsNullOrEmpty(RxPlaybackLevel.Label(level)));
    }

    [Fact]
    public void The_playback_level_glides_down_and_back_rather_than_stepping()
    {
        // 48 kHz mono, a 12 dB level. Down: 60 ms crosses 12 dB, so one
        // 480-frame buffer (10 ms) goes 2 dB, to 0.794. Never a step.
        var buffer = Enumerable.Repeat(1f, 480).ToArray();
        float gain = RxAudioPipeline.GlideGain(buffer, buffer.Length, 1, 48000, 1f, 0.251f, 12f);
        Assert.InRange(gain, 0.78f, 0.81f);
        Assert.True(buffer[0] > buffer[^1]);
        Assert.Equal(gain, buffer[^1], 4);

        // Back up: 500 ms crosses 12 dB, IN DECIBELS (retest 2026-10-25: the
        // straight-line return was heard as a blip). Half the time is half the
        // decibels, -6 dB, 0.501, where a straight amplitude ramp would be at
        // 0.626.
        var back = Enumerable.Repeat(1f, 12000).ToArray();
        gain = RxAudioPipeline.GlideGain(back, back.Length, 1, 48000, 0.251f, 1f, 12f);
        Assert.InRange(gain, 0.49f, 0.51f);
        for (int i = 1; i < back.Length; i++) Assert.True(back[i] >= back[i - 1]);

        // Muted arrives at exactly zero, and at full gain with nothing asked
        // the audio passes untouched.
        var mute = Enumerable.Repeat(1f, 4800).ToArray();
        Assert.Equal(0f, RxAudioPipeline.GlideGain(mute, mute.Length, 1, 48000, 1f, 0f, 60f));
        Assert.Equal(0f, mute[^1]);
        var untouched = Enumerable.Repeat(0.5f, 480).ToArray();
        Assert.Equal(1f, RxAudioPipeline.GlideGain(untouched, untouched.Length, 1, 48000, 1f, 1f, 12f));
        Assert.All(untouched, v => Assert.Equal(0.5f, v));
    }

    private static readonly object OpusGate = new();
    private static bool _opusSet;

    private static void OpusLibraryReady()
    {
        lock (OpusGate)
        {
            if (_opusSet) return;
            NativeLibrary.SetDllImportResolver(typeof(POpusCodec.OpusDecoder).Assembly, (name, _, _) =>
            {
                if (!name.Contains("opus", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                string arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";
                string path = Path.Combine(AppContext.BaseDirectory, "runtimes", arch, "native", "libopus.dll");
                if (!File.Exists(path))
                    path = Path.GetFullPath(Path.Combine(RepoPaths.RequireRoot(), "runtimes", arch, "native", "libopus.dll"));
                return NativeLibrary.Load(path);
            });
            _opusSet = true;
        }
    }

    // Bench 2026-10-25: the trimmed Opus file Save As wrote would not play
    // ("The byte stream type is not supported") and its row said "length
    // unknown", because Windows has no Ogg Opus decoder. Through Save As's
    // own encoder and back through the player's opener: the right length,
    // real audio, and a seek that lands where it was asked.
    [Fact]
    public void An_Opus_file_Save_As_wrote_opens_with_its_length_plays_audio_and_seeks()
    {
        FlacLibraryReady();
        OpusLibraryReady();
        string source = Flac(24000, 24000 * 2);
        string opus = Path.Combine(_dir, "saved.opus");
        RecordedQsoExport.Convert(source, opus, RecordedQsoExport.Format.Opus, 1);

        var info = RecordedQsoMedia.ReadInfo(opus);
        Assert.Equal(48000, info.Rate);
        Assert.Equal(1, info.Channels);
        Assert.InRange(info.Frames, 95900, 96100); // two seconds at 48 kHz

        using (var reader = RecordedQsoMedia.Open(opus))
        {
            Assert.InRange(reader.TotalTime.TotalSeconds, 1.99, 2.01);
            var samples = reader.ToSampleProvider();
            var buffer = new float[48000];
            int read = samples.Read(buffer.AsSpan());
            Assert.Equal(buffer.Length, read);
            Assert.Contains(buffer, v => Math.Abs(v) > 0.05f);

            reader.Position = reader.WaveFormat.AverageBytesPerSecond; // one second in
            Assert.Equal(1.0, reader.CurrentTime.TotalSeconds, 3);
            int total = 0;
            while ((read = samples.Read(buffer.AsSpan())) > 0) total += read;
            Assert.InRange(total, 47900, 48100); // the second second, and no more
        }

        // Through the player too: it is what Enter calls.
        Assert.True(RecordingPlayer.PlayWithMixer(opus, "A test recording.", null, false, _ => true));
        RecordingPlayer.StopWithoutSpeaking();
    }
}

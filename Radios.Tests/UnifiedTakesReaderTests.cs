#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes part 1: the reader dispatches on format then kind, the
/// catalog lists every take whatever its kind, and every existing IQ caller
/// states the kind it wants. Created state throughout: takes written by the
/// real <see cref="AudioTakeWriter"/>, read by the real reader.
/// </summary>
public sealed class UnifiedTakesReaderTests
{
    [Theory]
    [InlineData(TakeKind.RadioRecording)]
    [InlineData(TakeKind.LoopedBack)]
    public void A_written_playback_take_reads_back_clean_and_playable_with_no_iq(string kind)
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string take = UnifiedTakeFixtures.AudioTake(root, kind);
            var inspection = TakeReader.Inspect(take);
            Assert.Equal("clean", inspection.State);
            Assert.Equal(kind, inspection.Kind);
            Assert.True(inspection.IsAudio);
            Assert.False(inspection.HasIq);
            Assert.True(inspection.CanPlay);
            Assert.True(inspection.CanMeasure);
            // The IQ road's own answer stays false, so no IQ-only reader takes it.
            Assert.False(inspection.Playable);
            Assert.False(File.Exists(Path.Combine(take, "rf.iq")));
            Assert.Equal("closed", inspection.Fields[^1].Key);
            Assert.Single(AudioTakeTimeline.PlaybackSpans(inspection, long.MaxValue));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_tampered_capture_is_corrupt_by_its_checksum()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string take = UnifiedTakeFixtures.AudioTake(root);
            string wav = Path.Combine(take, "heard.wav");
            var bytes = File.ReadAllBytes(wav);
            bytes[^1] ^= 0x55;
            File.WriteAllBytes(wav, bytes);
            var inspection = TakeReader.Inspect(take);
            Assert.Equal("corrupt", inspection.State);
            Assert.Equal("checksum", inspection.Reason);
            Assert.False(inspection.CanPlay);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_capture_a_crash_left_unclosed_is_listed_incomplete_and_unplayable_and_never_repaired()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string take = UnifiedTakeFixtures.UnclosedAudioTake(root);
            string checksumBefore = TakeHeader.Checksum(Path.Combine(take, "heard.wav"));
            var inspection = TakeReader.Inspect(take);
            Assert.Equal("incomplete", inspection.State);
            Assert.Equal("unclosed-audio-unreadable", inspection.Reason);
            Assert.False(inspection.CanPlay);
            Assert.Equal(checksumBefore, TakeHeader.Checksum(Path.Combine(take, "heard.wav")));
            Assert.Contains(TakeCatalog.List(root), e => e.Directory == take && e.Kind == TakeKind.RadioRecording);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Invalid_format_and_kind_combinations_fail_explicitly()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            // An IQ header claiming an audio kind.
            string iqClaimingAudio = UnifiedTakeFixtures.IqHeader(root, "iq-audio", TakeKind.RadioRecording);
            Assert.Equal(("corrupt", "kind"), (TakeReader.Inspect(iqClaimingAudio).State, TakeReader.Inspect(iqClaimingAudio).Reason));
            // An audio header claiming an IQ kind.
            string audio = UnifiedTakeFixtures.AudioTake(root, facts: new Dictionary<string, string?>());
            string audioClaimingIq = Path.Combine(root, "audio-iq");
            Directory.CreateDirectory(audioClaimingIq);
            File.Copy(Path.Combine(audio, "heard.wav"), Path.Combine(audioClaimingIq, "heard.wav"));
            File.WriteAllLines(Path.Combine(audioClaimingIq, "take.txt"), File.ReadAllLines(Path.Combine(audio, "take.txt"))
                .Select(l => l == "kind=" + TakeKind.RadioRecording ? "kind=" + TakeKind.OffAir : l));
            var inspection = TakeReader.Inspect(audioClaimingIq);
            Assert.Equal("corrupt", inspection.State);
            Assert.Equal("kind", inspection.Reason);
            // An unknown format is refused exactly as it always was.
            string unknown = Path.Combine(root, "unknown");
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "take.txt"), "format=something-else\nclosed=clean\n");
            Assert.Equal(("corrupt", "format"), (TakeReader.Inspect(unknown).State, TakeReader.Inspect(unknown).Reason));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_missing_rf_iq_never_makes_an_iq_take_an_audio_take()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string iq = UnifiedTakeFixtures.IqHeader(root, "iq-no-file", TakeKind.OffAir);
            var inspection = TakeReader.Inspect(iq);
            Assert.Equal("corrupt", inspection.State);
            Assert.Equal(TakeKind.OffAir, inspection.Kind);
            Assert.False(inspection.IsAudio);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_playback_span_outside_the_file_is_corrupt()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string take = UnifiedTakeFixtures.AudioTake(root,
                during: w => w.NoteEvent(AudioTakeFields.PlaybackSpan, new AudioSpanRecord(0, 99_999_999)));
            var inspection = TakeReader.Inspect(take);
            Assert.Equal(("corrupt", "span-range"), (inspection.State, inspection.Reason));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void The_catalog_lists_every_kind_and_damaged_folders_newest_first_and_skips_the_registry()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string offAir = UnifiedTakeFixtures.IqHeader(root, "20261101-100000-000-00000001", TakeKind.OffAir);
            string diagnostic = UnifiedTakeFixtures.IqHeader(root, "20261101-110000-000-00000002", TakeKind.Diagnostic);
            string legacy = UnifiedTakeFixtures.IqHeader(root, "20261101-090000-000-00000003", null);
            string recording = UnifiedTakeFixtures.AudioTake(root, TakeKind.RadioRecording, name: "20261101-120000-000-00000004");
            string looped = UnifiedTakeFixtures.AudioTake(root, TakeKind.LoopedBack, name: "20261101-130000-000-00000005");
            string damaged = Path.Combine(root, "20261101-140000-000-00000006");
            Directory.CreateDirectory(damaged);
            File.WriteAllText(Path.Combine(damaged, "take.txt"), "this line has no equals sign\n");
            Directory.CreateDirectory(Path.Combine(root, TakeCatalog.TestsFolder, "anything"));

            var list = TakeCatalog.List(root);
            Assert.Equal(new[] { damaged, looped, recording, diagnostic, offAir, legacy }, list.Select(e => e.Directory));
            Assert.Equal(new[] { TakeKind.Damaged, TakeKind.LoopedBack, TakeKind.RadioRecording, TakeKind.Diagnostic,
                TakeKind.OffAir, TakeKind.UnknownLegacy }, list.Select(e => e.Kind));
            Assert.False(list[0].HeaderReadable);
            Assert.DoesNotContain(list, e => e.Name.StartsWith(".", StringComparison.Ordinal));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void The_off_air_and_diagnostic_callers_never_receive_another_kind_however_new_it_is()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string offAir = UnifiedTakeFixtures.IqHeader(root, "a-off-air", TakeKind.OffAir);
            string diagnostic = UnifiedTakeFixtures.IqHeader(root, "b-diagnostic", TakeKind.Diagnostic);
            string legacy = UnifiedTakeFixtures.IqHeader(root, "c-legacy", null);
            string recording = UnifiedTakeFixtures.AudioTake(root);
            var stamp = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(Path.Combine(offAir, "take.txt"), stamp.AddMinutes(-40));
            File.SetLastWriteTimeUtc(Path.Combine(diagnostic, "take.txt"), stamp.AddMinutes(-30));
            File.SetLastWriteTimeUtc(Path.Combine(legacy, "take.txt"), stamp.AddMinutes(-20));
            File.SetLastWriteTimeUtc(Path.Combine(recording, "take.txt"), stamp.AddMinutes(-10));

            Assert.Equal(offAir, TakeCatalog.Newest(root, new[] { TakeKind.OffAir }));
            Assert.Equal(diagnostic, TakeCatalog.Newest(root, new[] { TakeKind.Diagnostic }));
            Assert.Equal(recording, TakeCatalog.Newest(root, new[] { TakeKind.RadioRecording }));
            Assert.Null(TakeCatalog.Newest(root, new[] { TakeKind.OffAir }, except: offAir));
            // Positive control: the same scan with every kind does find the newest folder.
            Assert.Equal(recording, TakeCatalog.Newest(root, new[] { TakeKind.OffAir, TakeKind.Diagnostic,
                TakeKind.UnknownLegacy, TakeKind.RadioRecording }));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void The_iq_renderer_refuses_an_audio_take_and_leaves_its_capture_untouched()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string take = UnifiedTakeFixtures.AudioTake(root);
            string before = TakeHeader.Checksum(Path.Combine(take, "heard.wav"));
            var refusal = Assert.Throws<InvalidDataException>(() => SsbTakeRenderer.Render(take));
            Assert.Equal("audio-take-has-no-iq", refusal.Message);
            Assert.Equal("audio-take-has-no-iq", Assert.Throws<InvalidDataException>(() => TakeAnalysis.Run(take)).Message);
            Assert.Equal(before, TakeHeader.Checksum(Path.Combine(take, "heard.wav")));
            Assert.False(File.Exists(Path.Combine(take, "render.txt")));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Seconds_reads_samples_over_rate_on_the_audio_road()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string take = UnifiedTakeFixtures.AudioTake(root, seconds: 2);
            // A quarter second of lead, two seconds of playback, a quarter of tail.
            Assert.Equal(2.5, TakeFacts.Seconds(TakeReader.Inspect(take)), 6);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void The_writer_never_writes_after_closed()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string directory = AudioTakeWriter.NewDirectory(root);
            var writer = new AudioTakeWriter(directory, TakeKind.RadioRecording, UnifiedTakeFixtures.BaseFacts()
                .Where(f => f.Value != null).Select(f => new KeyValuePair<string, string>(f.Key, f.Value!)));
            var samples = UnifiedTakeFixtures.Speechlike(3, 4800);
            writer.BeginPlayback();
            writer.Write(samples, samples.Length);
            Assert.True(writer.Close(clean: true, "playback-ended"));
            long length = new FileInfo(Path.Combine(directory, "take.txt")).Length;
            writer.Write(samples, samples.Length);
            writer.Note("late", "fact");
            writer.Gap(100, "late");
            writer.EndPlayback();
            Assert.True(writer.Close(clean: false, "again"));
            writer.Dispose();
            Assert.Equal(length, new FileInfo(Path.Combine(directory, "take.txt")).Length);
            Assert.Equal("clean", TakeReader.Inspect(directory).State);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using JJPortaudio;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using POpusCodec;
using POpusCodec.Enums;
using Radios;

namespace JJFlexWpf;

/// <summary>Save As conversions for a finished Recorded QSO. Track 4 owns the dialog.</summary>
public static class RecordedQsoExport
{
    public enum Format { Flac, Wav, Mp3, Opus, M4a }

    /// <summary>
    /// Convert a FLAC recording to the selected ordinary audio file. Call off the UI thread
    /// for long conversations. Mono averages the left and right slice channels.
    /// </summary>
    public static void Convert(string source, string destination, Format format, int channels,
        int bitrate = 0) => Convert(source, destination, format, channels, bitrate, 0, null);

    /// <summary>
    /// Convert part of a FLAC recording (#787, Tape Deck track 4's trim): the
    /// sample frames from <paramref name="startFrame"/> up to, not including,
    /// <paramref name="endFrame"/>, or to the end of the file when it is null.
    /// Frames are the source's own, so a trim is exact to the sample. The
    /// original is never touched, and an existing destination survives a
    /// failure: the work is done in a temporary sibling and moved over it only
    /// at the end.
    /// </summary>
    public static void Convert(string source, string destination, Format format, int channels,
        int bitrate, long startFrame, long? endFrame)
    {
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (startFrame < 0) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (endFrame is long end && end <= startFrame) throw new ArgumentOutOfRangeException(nameof(endFrame));
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a different file for Save As.", nameof(destination));
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,
            ".qso-export-" + Guid.NewGuid().ToString("N") + Path.GetExtension(destination));
        using var reader = new RecordedQsoFlacWaveStream(source);
        if (startFrame > 0) reader.Position = startFrame * reader.WaveFormat.BlockAlign;
        ISampleProvider samples = reader.ToSampleProvider();
        if (endFrame is long last)
            samples = new FrameLimitSampleProvider(samples, last - startFrame);
        if (samples.WaveFormat.Channels == 2 && channels == 1)
            samples = new StereoToMonoSampleProvider(samples);
        else if (samples.WaveFormat.Channels == 1 && channels == 2)
            samples = new MonoToStereoSampleProvider(samples);

        try
        {
            switch (format)
            {
                case Format.Flac:
                    using (var writer = new RecordedQsoFlac(temporary, samples.WaveFormat.SampleRate, channels))
                        CopySamples(samples, channels, writer.Write);
                    break;
                case Format.Wav:
                    using (var writer = new WavWriter(temporary, samples.WaveFormat.SampleRate, channels))
                        CopySamples(samples, channels, data => writer.Write(data, data.Length));
                    break;
                case Format.Mp3:
                    EncodeMp3(samples, temporary, channels, bitrate == 0 ? 64000 : bitrate);
                    break;
                case Format.Opus:
                    EncodeOpus(samples, temporary, channels, bitrate == 0 ? 32000 : bitrate);
                    break;
                case Format.M4a:
                    EncodeM4a(samples, temporary, channels, bitrate == 0 ? 32000 : bitrate);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(format));
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Ends a source after a number of sample frames: the trim's end. Reads
    /// never split a frame, so the channels stay in step.
    /// </summary>
    private sealed class FrameLimitSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private long _remaining;

        internal FrameLimitSampleProvider(ISampleProvider source, long frames)
        {
            _source = source;
            _remaining = frames * source.WaveFormat.Channels;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            if (_remaining <= 0) return 0;
            int want = (int)Math.Min(buffer.Length, _remaining);
            int read = _source.Read(buffer.Slice(0, want));
            _remaining -= read;
            return read;
        }
    }

    /// <summary>
    /// True when Windows can make an MP3 here. Save As asks before it offers
    /// the choice (#787): a Windows N edition has no Media Foundation until the
    /// Media Feature Pack is added. Asks the same question the encoder will,
    /// at the rate and channel counts our recordings use.
    /// </summary>
    public static bool Mp3Available()
    {
        try
        {
            MediaFoundationApi.Startup();
            foreach (int rate in new[] { 24000, 48000 })
                foreach (int ch in new[] { 1, 2 })
                    if (MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_MP3,
                            new WaveFormat(rate, 16, ch), 64000) != null)
                        return true;
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or COMException or PlatformNotSupportedException
                                       or EntryPointNotFoundException or TypeInitializationException)
        {
            return false;
        }
    }

    private static void CopySamples(ISampleProvider source, int channels, Action<float[]> write)
    {
        float[] buffer = new float[4096 * channels];
        int n;
        while ((n = source.Read(buffer.AsSpan())) > 0)
        {
            if (n % channels != 0) throw new InvalidDataException("An audio frame ended partway through a sample.");
            float[] block = new float[n];
            Array.Copy(buffer, block, n);
            write(block);
        }
    }

    private static void EncodeMp3(ISampleProvider source, string destination, int channels, int bitrate)
    {
        try
        {
            MediaFoundationApi.Startup();
            var subtype = AudioSubtypes.MFAudioFormat_MP3;
            WaveFormat waveFormat = new(source.WaveFormat.SampleRate, 16, channels);
            var type = MediaFoundationEncoder.SelectMediaType(subtype, waveFormat, bitrate);
            if (type == null && source.WaveFormat.SampleRate != 24000)
            {
                source = new WdlResamplingSampleProvider(source, 24000);
                waveFormat = new WaveFormat(24000, 16, channels);
                type = MediaFoundationEncoder.SelectMediaType(subtype, waveFormat, bitrate);
            }
            if (type == null)
                throw new NotSupportedException("Windows does not offer this MP3 bitrate and channel choice.");
            using var encoder = new MediaFoundationEncoder(type);
            encoder.Encode(destination, source.ToWaveProvider16());
        }
        catch (DllNotFoundException ex)
        {
            throw new NotSupportedException("MP3 Save As needs Windows Media Foundation. On a Windows N edition, install the Media Feature Pack.", ex);
        }
    }

    private static void EncodeOpus(ISampleProvider source, string destination, int channels, int bitrate)
    {
        if (source.WaveFormat.SampleRate != 48000)
            source = new WdlResamplingSampleProvider(source, 48000);
        using var encoder = new OpusEncoder(SamplingRate.Sampling48000,
            channels == 1 ? Channels.Mono : Channels.Stereo, OpusApplicationType.Audio);
        encoder.Bitrate = bitrate;
        using var ogg = new RecordedQsoOggWriter(destination, channels, encoder.Lookahead);
        int frameFloats = encoder.FrameSizePerChannel * channels;
        float[] frame = new float[frameFloats];
        long inputFrames = 0;
        while (true)
        {
            int read = 0;
            while (read < frameFloats)
            {
                int n = source.Read(frame.AsSpan(read, frameFloats - read));
                if (n == 0) break;
                read += n;
            }
            if (read == 0) break;
            inputFrames += read / channels;
            Array.Clear(frame, read, frameFloats - read);
            ogg.AddAudioPacket(encoder.Encode(frame), encoder.FrameSizePerChannel);
            if (read < frameFloats) break;
        }
        // An extra packet covers the encoder lookahead; the final Ogg granule trims
        // the padding so playback duration equals the input duration.
        Array.Clear(frame);
        ogg.AddAudioPacket(encoder.Encode(frame), encoder.FrameSizePerChannel);
        ogg.Finish(inputFrames + encoder.Lookahead);
    }

    /// <summary>Returns true only when Windows actually enumerates an HE-AAC mono type.</summary>
    public static bool HeAacM4aAvailable()
    {
        try
        {
            MediaFoundationApi.Startup();
            foreach (var type in MediaFoundationEncoder.GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_AAC))
                if (type.ChannelCount == 1 && type.AverageBytesPerSecond * 8 == 32000 &&
                    type.SampleRate > 0 && IsHeAac(type))
                    return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or COMException or PlatformNotSupportedException)
        { return false; }
        return false;
    }

    private static bool IsHeAac(NAudio.MediaFoundation.MediaType type)
    {
        int profile = type.TryGetUInt32(MediaFoundationAttributes.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, -1);
        return profile is 0x2c or 0x2e or 0x2f or 0x30 or 0x31 or 0x32 or 0x33;
    }

    private static void EncodeM4a(ISampleProvider source, string destination, int channels, int bitrate)
    {
        if (channels != 1) throw new NotSupportedException("HE-AAC M4A Save As currently needs mono audio.");
        try
        {
            MediaFoundationApi.Startup();
            var types = MediaFoundationEncoder.GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_AAC);
            var type = Array.Find(types, t => t.SampleRate == source.WaveFormat.SampleRate &&
                t.ChannelCount == 1 && t.AverageBytesPerSecond * 8 == bitrate && IsHeAac(t));
            type ??= Array.Find(types, t => t.SampleRate == 32000 && t.ChannelCount == 1 &&
                t.AverageBytesPerSecond * 8 == bitrate && IsHeAac(t));
            type ??= Array.Find(types, t => t.SampleRate > 0 && t.ChannelCount == 1 &&
                t.AverageBytesPerSecond * 8 == bitrate && IsHeAac(t));
            if (type == null)
                throw new NotSupportedException("Windows does not offer HE-AAC M4A at this bitrate. No file was changed.");
            if (type.SampleRate != source.WaveFormat.SampleRate)
                source = new WdlResamplingSampleProvider(source, type.SampleRate);
            using var encoder = new MediaFoundationEncoder(type);
            encoder.Encode(destination, source.ToWaveProvider16());
        }
        catch (DllNotFoundException ex)
        {
            throw new NotSupportedException("M4A Save As needs Windows Media Foundation. On a Windows N edition, install the Media Feature Pack.", ex);
        }
    }
}

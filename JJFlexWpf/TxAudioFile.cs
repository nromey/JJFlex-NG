using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using JJPortaudio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JJFlexWpf;

/// <summary>
/// Reads an audio file into the shape the transmit injection point needs, and
/// measures it (Sprint 33 Track I).
/// </summary>
/// <remarks>
/// <para>
/// The player in <see cref="TxFilePlayer"/> deliberately knows nothing about
/// file formats — it lives in the System-only audio assembly so the numerical
/// harness can link it. Decoding, mixing down and resampling happen here,
/// once, off the audio thread, using the same NAudio chain
/// <see cref="EarconPlayer"/> already uses for its sounds.
/// </para>
/// <para>
/// RESAMPLE ON LOAD, NOT ON PLAY. The player can interpolate if it is handed
/// content at the wrong rate, but that is a fallback with an audible cost, and
/// a reference file whose sound depends on which rate the operator's sound
/// card negotiated is not a reference file. Doing it here, with a proper
/// resampler, means playback is a plain array read every time.
/// </para>
/// <para>
/// MONO, because the transmit stream carries a mono microphone duplicated to
/// both channels, and a reference should arrive the same way anything else
/// does. A stereo file is mixed down rather than refused: an operator who
/// recorded their reference on a stereo interface should not have to find that
/// out from an error message.
/// </para>
/// <para>
/// It also measures what it loaded — peak and integrated loudness, through the
/// same <see cref="LufsMeter"/> the transmit path and the microphone check
/// use. Level is part of what makes a reference a reference, so a surface that
/// offers a file should be able to say how loud it is before transmitting it.
/// </para>
/// </remarks>
public static class TxAudioFile
{
    /// <summary>A decoded file, ready to hand to the player.</summary>
    public sealed class Loaded
    {
        /// <summary>Mono samples in −1..1 at <see cref="SampleRate"/>.</summary>
        public float[] Mono { get; init; } = Array.Empty<float>();

        /// <summary>The rate the samples are at — the rate that was asked for.</summary>
        public int SampleRate { get; init; }

        /// <summary>A short name for speech: the file name without its extension.</summary>
        public string Name { get; init; } = "";

        /// <summary>The file it came from.</summary>
        public string Path { get; init; } = "";

        /// <summary>How long it is.</summary>
        public double Seconds { get; init; }

        /// <summary>Loudest sample, dBFS. <see cref="MicProbe.SilenceDb"/> if silent.</summary>
        public float PeakDb { get; init; }

        /// <summary>
        /// Gated loudness of the whole file, LUFS, BS.1770 — the same measure
        /// the microphone check and the transmit meter report, so one voice
        /// never gets two vocabularies depending on which stage measured it.
        /// </summary>
        public float IntegratedLufs { get; init; }

        /// <summary>
        /// One spoken line describing what this file is and how loud it is.
        /// </summary>
        public string Describe()
        {
            var parts = new List<string>
            {
                string.IsNullOrEmpty(Name) ? "unnamed" : Name,
                RecordingStore.DescribeLength(Seconds),
            };
            if (PeakDb > MicProbe.SilenceDb)
                parts.Add("peak " + PeakDb.ToString("F1") + " dBFS");
            if (IntegratedLufs > LufsMeter.Floor)
                parts.Add("loudness " + IntegratedLufs.ToString("F1") + " LUFS");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// The longest file this will load, in seconds. A reference take or a
    /// station message is a minute or two; anything at this length is somebody
    /// pointing us at an album, and decoding it whole into memory helps nobody.
    /// </summary>
    public const int MaxSeconds = 900;

    /// <summary>
    /// Decode <paramref name="path"/> to mono at <paramref name="targetRate"/>.
    /// </summary>
    /// <param name="trouble">
    /// Empty on success; otherwise why it could not be loaded, written to be
    /// spoken as-is.
    /// </param>
    public static bool TryLoad(string path, int targetRate, out Loaded? loaded, out string trouble)
    {
        loaded = null;
        trouble = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            trouble = "No file was given.";
            return false;
        }
        if (!File.Exists(path))
        {
            trouble = System.IO.Path.GetFileName(path) + " could not be found.";
            return false;
        }
        if (targetRate <= 0) targetRate = 48000;

        try
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider source = reader;

            // Mix down first, then resample: half as many samples through the
            // resampler, and the result is identical.
            if (source.WaveFormat.Channels > 1)
                source = source.ToMono();

            if (source.WaveFormat.SampleRate != targetRate)
                source = new WdlResamplingSampleProvider(source, targetRate);

            int cap = MaxSeconds * targetRate;
            var samples = new List<float>(Math.Min(cap, targetRate * 60));
            var block = new float[targetRate]; // one second at a time
            int read;
            while ((read = source.Read(block)) > 0)
            {
                for (int i = 0; i < read && samples.Count < cap; i++) samples.Add(block[i]);
                if (samples.Count >= cap) break;
            }

            if (samples.Count == 0)
            {
                trouble = System.IO.Path.GetFileName(path) + " has no audio in it.";
                return false;
            }

            float[] mono = samples.ToArray();
            Measure(mono, targetRate, out float peakDb, out float lufs);

            loaded = new Loaded
            {
                Mono = mono,
                SampleRate = targetRate,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Path = path,
                Seconds = (double)mono.Length / targetRate,
                PeakDb = peakDb,
                IntegratedLufs = lufs,
            };
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TxAudioFile.TryLoad failed for {path}: {ex.Message}");
            trouble = System.IO.Path.GetFileName(path) + " could not be read: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Load a file and hand it to a rig's transmit player, ready to send.
    /// </summary>
    /// <remarks>
    /// The target rate is the rate the transmit stream will actually run at,
    /// so the player never has to interpolate.
    /// </remarks>
    public static bool TryLoadInto(Radios.FlexBase rig, string path,
        out Loaded? loaded, out string trouble)
    {
        loaded = null;
        trouble = "";
        if (rig == null)
        {
            trouble = "No radio is connected.";
            return false;
        }

        int rate = (int)Radios.FlexBase.OpusTxSampleRateSetting;
        if (!TryLoad(path, rate, out loaded, out trouble)) return false;

        rig.TxFilePlayer.Load(loaded!.Mono, loaded.SampleRate, loaded.Name);
        return true;
    }

    /// <summary>
    /// Peak and integrated loudness of mono content, measured the way every
    /// other level in this application is measured.
    /// </summary>
    private static void Measure(float[] mono, int sampleRate, out float peakDb, out float lufs)
    {
        float peak = 0f;
        for (int i = 0; i < mono.Length; i++)
        {
            float a = mono[i] < 0f ? -mono[i] : mono[i];
            if (a > peak) peak = a;
        }
        peakDb = MicProbe.ToDb(peak);

        // The meter takes interleaved stereo, and a mono microphone reaches it
        // duplicated onto both channels — so duplicate here too, or the figure
        // would not be comparable with the one the microphone check reports
        // about the same voice.
        var meter = new LufsMeter();
        const int chunkFrames = 4800;
        var scratch = new float[chunkFrames * 2];
        int pos = 0;
        while (pos < mono.Length)
        {
            int frames = Math.Min(chunkFrames, mono.Length - pos);
            int j = 0;
            for (int i = 0; i < frames; i++)
            {
                float s = mono[pos + i];
                scratch[j++] = s;
                scratch[j++] = s;
            }
            meter.Process(scratch, frames * 2, (uint)sampleRate);
            pos += frames;
        }
        lufs = meter.IntegratedLufs;
    }
}

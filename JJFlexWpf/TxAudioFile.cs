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

        /// <summary>How long it is: the samples actually loaded, which for a piece is the piece.</summary>
        public double Seconds { get; init; }

        /// <summary>
        /// Where in the file these samples start, in seconds (#835): zero for a
        /// whole file or a first piece, the held place for a send carrying on.
        /// </summary>
        public double FromSeconds { get; init; }

        /// <summary>The file's own length as its header gives it, in seconds; zero when the file does not say.</summary>
        public double TotalSeconds { get; init; }

        /// <summary>
        /// True when the samples run to the end of the file; false when the
        /// piece stopped at <see cref="MaxSeconds"/> with more of the file
        /// after it. Never silently false: a piece that is not the end says so
        /// here, and a whole-file load that would be refused instead.
        /// </summary>
        public bool ReachesEnd { get; init; } = true;

        /// <summary>
        /// The whole recording's length as best known: exact when this piece
        /// reaches the end, otherwise the header's figure (or, for a file whose
        /// header does not say, at least everything up to this piece's end).
        /// </summary>
        public double LengthSeconds => ReachesEnd
            ? FromSeconds + Seconds
            : Math.Max(TotalSeconds, FromSeconds + Seconds);

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
    /// The most of a file decoded into memory at once, in seconds. A reference
    /// take or a station message is a minute or two, and fits whole; a
    /// recorded QSO can run far longer, and goes to the air in pieces of at
    /// most this, each loaded from where the last one paused (#835).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is cut silently any more</b> (#835, round 2). Until then a
    /// longer file was quietly shortened to this length, which was harmless
    /// while a send keyed for its whole length anyway; with a send pausing at
    /// ten minutes so the operator can identify, a recording longer than this
    /// must go out whole, in pieces, or not at all. So <see cref="TryLoadPiece"/>
    /// says whether its piece reaches the file's end, and the whole-file
    /// <see cref="TryLoad"/> refuses in a sentence rather than shortening.
    /// </para>
    /// <para>
    /// It must stay longer than a send's ten minutes and its end margin
    /// (<see cref="Radios.VoiceMessageSend.PauseAfterSeconds"/>), so a piece
    /// that is not the file's last can never run out before the pause; a test
    /// holds that.
    /// </para>
    /// </remarks>
    public const int MaxSeconds = 900;

    /// <summary>
    /// The file's length from its header, in seconds, without decoding it.
    /// False, with zero, when the file cannot be opened or does not say.
    /// </summary>
    public static bool TryMeasureSeconds(string path, out double seconds)
    {
        seconds = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            using WaveStream reader = OpenReader(path);
            seconds = reader.TotalTime.TotalSeconds;
            if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
            return seconds > 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TxAudioFile.TryMeasureSeconds failed for {path}: {ex.Message}");
            seconds = 0;
            return false;
        }
    }

    /// <summary>
    /// Decode the whole of <paramref name="path"/> to mono at
    /// <paramref name="targetRate"/>. A file longer than
    /// <see cref="MaxSeconds"/> is refused with a sentence saying why, never
    /// shortened: this is the loader for the reference audio, which plays a
    /// file whole.
    /// </summary>
    /// <param name="trouble">
    /// Empty on success; otherwise why it could not be loaded, written to be
    /// spoken as-is.
    /// </param>
    public static bool TryLoad(string path, int targetRate, out Loaded? loaded, out string trouble)
    {
        loaded = null;
        if (TryMeasureSeconds(path, out double measured) && Math.Round(measured) > MaxSeconds)
        {
            trouble = TooLong(path);
            return false;
        }
        if (!TryLoadPiece(path, targetRate, 0, out loaded, out trouble)) return false;
        if (loaded!.ReachesEnd) return true;
        loaded = null;
        trouble = TooLong(path);
        return false;
    }

    // DRAFT PROSE (#835): awaiting Noel
    private static string TooLong(string path) => Radios.Lexicon.Get("audio.send.file_too_long",
        ("file", System.IO.Path.GetFileNameWithoutExtension(path)),
        ("length", RecordingStore.DescribeLength(MaxSeconds)));

    /// <summary>
    /// Decode one piece of <paramref name="path"/>: from
    /// <paramref name="fromSeconds"/> into the file, for at most
    /// <see cref="MaxSeconds"/>, to mono at <paramref name="targetRate"/>
    /// (#835). <see cref="Loaded.ReachesEnd"/> says whether the piece runs to
    /// the file's end.
    /// </summary>
    /// <param name="trouble">
    /// Empty on success; otherwise why it could not be loaded, written to be
    /// spoken as-is.
    /// </param>
    public static bool TryLoadPiece(string path, int targetRate, double fromSeconds, out Loaded? loaded,
        out string trouble)
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
        if (!double.IsFinite(fromSeconds) || fromSeconds < 0) fromSeconds = 0;

        try
        {
            using WaveStream reader = OpenReader(path);
            double total = 0;
            try { total = reader.TotalTime.TotalSeconds; }
            catch (Exception ex) { Trace.WriteLine($"TxAudioFile: no length for {path}: {ex.Message}"); }
            if (!double.IsFinite(total) || total < 0) total = 0;

            if (fromSeconds > 0) SkipTo(reader, fromSeconds);
            ISampleProvider source = reader as ISampleProvider ?? reader.ToSampleProvider();

            // Mix down first, then resample: half as many samples through the
            // resampler, and the result is identical.
            if (source.WaveFormat.Channels > 1)
                source = source.ToMono();

            if (source.WaveFormat.SampleRate != targetRate)
                source = new WdlResamplingSampleProvider(source, targetRate);

            int cap = MaxSeconds * targetRate;
            // Sized from the header when it says, so a long piece is not built
            // by doubling a list several times over.
            long expected = total > fromSeconds ? (long)Math.Ceiling((total - fromSeconds) * targetRate) + targetRate
                                                : (long)targetRate * 60;
            var samples = new List<float>((int)Math.Min(cap, Math.Max(targetRate, expected)));
            var block = new float[targetRate]; // one second at a time
            int read;
            bool more = false;
            while ((read = source.Read(block)) > 0)
            {
                int i = 0;
                for (; i < read && samples.Count < cap; i++) samples.Add(block[i]);
                if (samples.Count >= cap)
                {
                    more = i < read;
                    break;
                }
            }
            if (samples.Count >= cap && !more)
            {
                // Exactly at the cap: one more read says whether the file
                // goes on. A piece that is not the end must say so.
                var probe = new float[256];
                more = source.Read(probe) > 0;
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
                FromSeconds = fromSeconds,
                TotalSeconds = total,
                ReachesEnd = !more,
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
    /// Open a file for decoding. A computer recording is FLAC, and Save As in
    /// Recorded QSOs can write Opus; both go through the decoders
    /// <see cref="RecordingPlayer"/> already plays them with
    /// (<see cref="RecordedQsoFlacWaveStream"/>, <see cref="RecordedQsoMedia"/>),
    /// so a recording that plays on the speakers is the same recording that
    /// goes to the air (#835, Tape Deck phase 2). Everything else — the WAV
    /// files voice messages and the reference audio use — opens exactly as it
    /// always has.
    /// </summary>
    private static WaveStream OpenReader(string path)
    {
        string type = System.IO.Path.GetExtension(path);
        if (type.Equals(".flac", StringComparison.OrdinalIgnoreCase)) return new RecordedQsoFlacWaveStream(path);
        if (type.Equals(".opus", StringComparison.OrdinalIgnoreCase)) return RecordedQsoMedia.Open(path);
        return new AudioFileReader(path);
    }

    /// <summary>
    /// Move a freshly opened reader to <paramref name="seconds"/> into the
    /// file, on a whole sample frame. Seeks when the reader can; otherwise, or
    /// when the seek is refused (a FLAC file whose header does not give its
    /// length cannot seek), reads and discards up to the place, which costs a
    /// decode and gets the same answer.
    /// </summary>
    private static void SkipTo(WaveStream reader, double seconds)
    {
        WaveFormat format = reader.WaveFormat;
        int block = Math.Max(1, format.BlockAlign);
        long bytes = (long)Math.Floor(seconds * format.SampleRate) * block;
        if (reader.CanSeek)
        {
            try
            {
                long length = reader.Length;
                if (length > 0 && bytes > length) bytes = length - length % block;
                reader.Position = bytes;
                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"TxAudioFile: seek refused ({ex.Message}); reading up to the place instead");
                try { reader.Position = 0; } catch { /* a reader that cannot seek back is still at the start */ }
            }
        }
        var discard = new byte[64 * 1024 - (64 * 1024) % block];
        long left = bytes;
        while (left > 0)
        {
            int n = reader.Read(discard, 0, (int)Math.Min(discard.Length, left));
            if (n <= 0) break;
            left -= n;
        }
    }

    /// <summary>
    /// Load a whole file and hand it to a rig's transmit player, ready to
    /// send. A file longer than <see cref="MaxSeconds"/> is refused, never
    /// shortened (<see cref="TryLoad"/>).
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
    /// Load one piece of a send, from <paramref name="fromSeconds"/>, and hand
    /// it to the rig's transmit player (#835). The send pauses at ten minutes
    /// of the piece, so a piece that is not the file's end never runs out
    /// before the pause, and the next piece is loaded from the held place.
    /// </summary>
    public static bool TryLoadPieceInto(Radios.FlexBase rig, string path, double fromSeconds,
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
        if (!TryLoadPiece(path, rate, fromSeconds, out loaded, out trouble)) return false;

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

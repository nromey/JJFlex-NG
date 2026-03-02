using System;
using System.IO;
using System.Media;
using System.Diagnostics;

namespace JJFlexWpf
{
    /// <summary>
    /// Synthesized beep tones for PTT warnings and UI earcons.
    /// Generates PCM WAV in memory — no PortAudio conflict with remote audio stream.
    /// </summary>
    public static class EarconPlayer
    {
        /// <summary>
        /// Play a warning beep at the given frequency and duration.
        /// </summary>
        /// <param name="frequencyHz">Tone frequency (e.g. 800 for warning, 1200 for urgent)</param>
        /// <param name="durationMs">Duration in milliseconds</param>
        public static void Beep(int frequencyHz = 800, int durationMs = 150)
        {
            try
            {
                using var stream = GenerateTone(frequencyHz, durationMs);
                using var player = new SoundPlayer(stream);
                player.Play(); // async, non-blocking
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.Beep failed: {ex.Message}");
                // Fallback to Console.Beep (blocking but always works)
                try { Console.Beep(frequencyHz, durationMs); }
                catch { /* swallow — no audio output available */ }
            }
        }

        /// <summary>
        /// Warning1 beep — moderate urgency (800 Hz, 150ms).
        /// </summary>
        public static void Warning1Beep() => Beep(800, 150);

        /// <summary>
        /// Warning2 beep — higher urgency (1000 Hz, 200ms).
        /// </summary>
        public static void Warning2Beep() => Beep(1000, 200);

        /// <summary>
        /// OhCrap beep — critical urgency (1200 Hz, 250ms).
        /// </summary>
        public static void OhCrapBeep() => Beep(1200, 250);

        /// <summary>
        /// TX start tone — two discrete tones: 400Hz then 800Hz.
        /// </summary>
        public static void TxStartTone()
        {
            try
            {
                using var stream = GenerateTwoTone(400, 50, 800, 50, 20);
                using var player = new SoundPlayer(stream);
                player.Play();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.TxStartTone failed: {ex.Message}");
            }
        }

        /// <summary>
        /// TX stop tone — two discrete tones: 800Hz then 400Hz.
        /// </summary>
        public static void TxStopTone()
        {
            try
            {
                using var stream = GenerateTwoTone(800, 50, 400, 50, 20);
                using var player = new SoundPlayer(stream);
                player.Play();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.TxStopTone failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Hard kill tone — two rapid descending beeps.
        /// </summary>
        public static void HardKillTone()
        {
            Beep(1000, 100);
            Beep(600, 200);
        }

        /// <summary>
        /// Play a frequency sweep (chirp) from startHz to endHz over durationMs.
        /// </summary>
        public static void Chirp(int startHz, int endHz, int durationMs)
        {
            try
            {
                using var stream = GenerateChirp(startHz, endHz, durationMs);
                using var player = new SoundPlayer(stream);
                player.Play();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.Chirp failed: {ex.Message}");
                try { Console.Beep((startHz + endHz) / 2, durationMs); }
                catch { }
            }
        }

        /// <summary>
        /// Confirmation tone — short two-tone click (low then high).
        /// Used after Ctrl+F frequency entry, ValueFieldControl Enter-to-set, etc.
        /// </summary>
        public static void ConfirmTone()
        {
            try
            {
                using var stream = GenerateTwoTone(500, 50, 700, 50, 20);
                using var player = new SoundPlayer(stream);
                player.Play();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.ConfirmTone failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Band boundary beep — distinctive double-beep when crossing band edges.
        /// 600 Hz, 50ms, pause, 600 Hz, 50ms. Clearly different from PTT warning tones.
        /// </summary>
        public static void BandBoundaryBeep()
        {
            Beep(600, 50);
            System.Threading.Tasks.Task.Delay(30).Wait();
            Beep(600, 50);
        }

        /// <summary>
        /// Generate a PCM WAV stream with a sine wave tone.
        /// 16-bit mono, 44100 Hz sample rate.
        /// </summary>
        private static MemoryStream GenerateTone(int frequencyHz, int durationMs)
        {
            const int sampleRate = 44100;
            const short bitsPerSample = 16;
            const short channels = 1;
            int samples = sampleRate * durationMs / 1000;
            int dataSize = samples * (bitsPerSample / 8) * channels;

            var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // WAV header
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataSize); // file size - 8
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt chunk
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16); // chunk size
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * (bitsPerSample / 8)); // byte rate
            writer.Write((short)(channels * (bitsPerSample / 8))); // block align
            writer.Write(bitsPerSample);

            // data chunk
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);

            // Sine wave with fade-in/fade-out envelope to avoid clicks
            int fadeLength = Math.Min(samples / 10, sampleRate / 100); // 10ms or 10% of duration
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / sampleRate;
                double sample = Math.Sin(2 * Math.PI * frequencyHz * t);

                // Envelope: fade in/out
                double envelope = 1.0;
                if (i < fadeLength)
                    envelope = (double)i / fadeLength;
                else if (i > samples - fadeLength)
                    envelope = (double)(samples - i) / fadeLength;

                short pcm = (short)(sample * envelope * 20000); // ~60% volume
                writer.Write(pcm);
            }

            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        /// <summary>
        /// Generate a PCM WAV stream with a linear frequency sweep.
        /// 16-bit mono, 44100 Hz sample rate.
        /// </summary>
        private static MemoryStream GenerateChirp(int startHz, int endHz, int durationMs)
        {
            const int sampleRate = 44100;
            const short bitsPerSample = 16;
            const short channels = 1;
            int samples = sampleRate * durationMs / 1000;
            int dataSize = samples * (bitsPerSample / 8) * channels;

            var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // WAV header
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt chunk
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * (bitsPerSample / 8));
            writer.Write((short)(channels * (bitsPerSample / 8)));
            writer.Write(bitsPerSample);

            // data chunk
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);

            // Linear frequency sweep with fade envelope
            int fadeLength = Math.Min(samples / 10, sampleRate / 100);
            double phase = 0.0;
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / samples; // 0..1 progress
                double freq = startHz + (endHz - startHz) * t;
                phase += 2 * Math.PI * freq / sampleRate;
                double sample = Math.Sin(phase);

                double envelope = 1.0;
                if (i < fadeLength)
                    envelope = (double)i / fadeLength;
                else if (i > samples - fadeLength)
                    envelope = (double)(samples - i) / fadeLength;

                short pcm = (short)(sample * envelope * 20000);
                writer.Write(pcm);
            }

            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        /// <summary>
        /// Generate a two-tone WAV: tone1 for dur1ms, gap of gapMs silence, tone2 for dur2ms.
        /// Used for confirmation tones and PTT chirps.
        /// </summary>
        private static MemoryStream GenerateTwoTone(int freq1Hz, int dur1Ms, int freq2Hz, int dur2Ms, int gapMs)
        {
            const int sampleRate = 44100;
            const short bitsPerSample = 16;
            const short channels = 1;

            int samples1 = sampleRate * dur1Ms / 1000;
            int gapSamples = sampleRate * gapMs / 1000;
            int samples2 = sampleRate * dur2Ms / 1000;
            int totalSamples = samples1 + gapSamples + samples2;
            int dataSize = totalSamples * (bitsPerSample / 8) * channels;

            var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // WAV header
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt chunk
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * (bitsPerSample / 8));
            writer.Write((short)(channels * (bitsPerSample / 8)));
            writer.Write(bitsPerSample);

            // data chunk
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);

            int fadeLen = sampleRate / 200; // 5ms fade

            // Tone 1
            for (int i = 0; i < samples1; i++)
            {
                double t = (double)i / sampleRate;
                double sample = Math.Sin(2 * Math.PI * freq1Hz * t);
                double env = 1.0;
                if (i < fadeLen) env = (double)i / fadeLen;
                else if (i > samples1 - fadeLen) env = (double)(samples1 - i) / fadeLen;
                writer.Write((short)(sample * env * 16000));
            }

            // Gap (silence)
            for (int i = 0; i < gapSamples; i++)
                writer.Write((short)0);

            // Tone 2
            for (int i = 0; i < samples2; i++)
            {
                double t = (double)i / sampleRate;
                double sample = Math.Sin(2 * Math.PI * freq2Hz * t);
                double env = 1.0;
                if (i < fadeLen) env = (double)i / fadeLen;
                else if (i > samples2 - fadeLen) env = (double)(samples2 - i) / fadeLen;
                writer.Write((short)(sample * env * 16000));
            }

            writer.Flush();
            stream.Position = 0;
            return stream;
        }
    }
}

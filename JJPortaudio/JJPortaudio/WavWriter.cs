using System;
using System.IO;
using System.Text;

namespace JJPortaudio
{
    /// <summary>
    /// Writes captured float samples to an ordinary 16-bit PCM WAV file
    /// (Sprint 33 Track I).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-rolled rather than NAudio's <c>WaveFileWriter</c> on purpose: this
    /// assembly is deliberately System-only so the numerical harness can link
    /// it (see <see cref="TxAudioConditioner"/>), and a RIFF header is
    /// forty-four bytes. Taking a dependency to avoid writing forty-four bytes
    /// would be the wrong trade.
    /// </para>
    /// <para>
    /// SIXTEEN-BIT PCM, not float32, and that is a deliberate choice about who
    /// the file is for. A recording an operator makes is theirs: they will want
    /// to play it in whatever plays audio on their machine, send it to a friend
    /// who says their audio sounds bad, or attach it to a support conversation.
    /// Plain 16-bit PCM opens everywhere; float32 WAV does not, and the ~96 dB
    /// it gives up sits far below the noise floor of any microphone that has
    /// ever been pointed at a ham.
    /// </para>
    /// <para>
    /// Samples arrive as floats in −1..1 and are clamped before conversion.
    /// Clamping rather than wrapping matters: a sample that overshoots would
    /// otherwise wrap to full-scale opposite polarity, which is a vicious click
    /// in a file whose whole job is to be trustworthy.
    /// </para>
    /// <para>
    /// The header is written with placeholder lengths up front and patched on
    /// <see cref="Dispose"/>, so a recording interrupted by a crash still
    /// leaves a file — malformed in its length fields, but with the audio
    /// present and recoverable. <see cref="Finish"/> does the same patching
    /// explicitly for callers that want the file complete without disposing.
    /// </para>
    /// <para>
    /// Not thread-safe. One capture thread owns one writer.
    /// </para>
    /// </remarks>
    public sealed class WavWriter : IDisposable
    {
        private const int HeaderBytes = 44;

        private FileStream _stream;
        private byte[] _scratch;
        private long _dataBytes;
        private bool _finished;

        /// <summary>Sample rate written into the header.</summary>
        public int SampleRate { get; }

        /// <summary>Channel count written into the header.</summary>
        public int Channels { get; }

        /// <summary>The file being written.</summary>
        public string Path { get; }

        /// <summary>Frames written so far.</summary>
        public long Frames => Channels > 0 ? _dataBytes / (2L * Channels) : 0;

        /// <summary>Seconds written so far.</summary>
        public double Seconds => SampleRate > 0 ? (double)Frames / SampleRate : 0.0;

        /// <summary>
        /// Create the file and reserve its header. Any existing file at this
        /// path is replaced.
        /// </summary>
        public WavWriter(string path, int sampleRate, int channels)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path", nameof(path));
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            Path = path;
            SampleRate = sampleRate;
            Channels = channels;

            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            // Reserve the header; the length fields are patched on Finish.
            _stream.Write(new byte[HeaderBytes], 0, HeaderBytes);
        }

        /// <summary>
        /// Append one block of interleaved float samples.
        /// </summary>
        /// <param name="buffer">interleaved samples in −1..1</param>
        /// <param name="count">how many floats of <paramref name="buffer"/> to take</param>
        public void Write(float[] buffer, int count)
        {
            if (_finished || _stream == null || buffer == null || count <= 0) return;
            if (count > buffer.Length) count = buffer.Length;

            int needed = count * 2;
            if (_scratch == null || _scratch.Length < needed) _scratch = new byte[needed];

            int j = 0;
            for (int i = 0; i < count; i++)
            {
                float s = buffer[i];
                if (s > 1f) s = 1f;
                else if (s < -1f) s = -1f;
                else if (float.IsNaN(s)) s = 0f;
                // 32767 rather than 32768: scaling by 32768 lets a sample of
                // exactly 1.0 land one past the top of the range and wrap.
                short v = (short)(s * 32767f);
                _scratch[j++] = (byte)(v & 0xFF);
                _scratch[j++] = (byte)((v >> 8) & 0xFF);
            }

            _stream.Write(_scratch, 0, needed);
            _dataBytes += needed;
        }

        /// <summary>
        /// Patch the header with the real lengths and flush. Safe to call
        /// twice; <see cref="Dispose"/> calls it.
        /// </summary>
        public void Finish()
        {
            if (_finished || _stream == null) return;
            _finished = true;

            _stream.Flush();
            _stream.Seek(0, SeekOrigin.Begin);

            int byteRate = SampleRate * Channels * 2;
            short blockAlign = (short)(Channels * 2);

            using (var w = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write((uint)(36 + _dataBytes));
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16u);                    // PCM fmt chunk size
                w.Write((short)1);               // WAVE_FORMAT_PCM
                w.Write((short)Channels);
                w.Write((uint)SampleRate);
                w.Write((uint)byteRate);
                w.Write(blockAlign);
                w.Write((short)16);              // bits per sample
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write((uint)_dataBytes);
            }

            _stream.Flush();
        }

        /// <summary>Patch the header and close the file.</summary>
        public void Dispose()
        {
            try { Finish(); }
            catch (Exception) { /* a partial file is still better than none */ }
            _stream?.Dispose();
            _stream = null;
        }
    }
}

using System;
using System.Diagnostics;
using NAudio.Wave;

namespace JJFlexWpf
{
    /// <summary>
    /// Local playback for the TX conditioning monitor tap (Track I): plays
    /// the processed output, the residual (what the chain removed), or the
    /// split view of both, on the PC's default output device while the
    /// operator transmits.
    ///
    /// This is a diagnostic monitor, not sidetone — a bit over a hundred
    /// milliseconds of latency is fine, dropouts under load are fine
    /// (DiscardOnBufferOverflow), and it deliberately uses its own small
    /// NAudio output rather than a second PortAudio stream so it cannot
    /// perturb the radio audio engine it is monitoring.
    ///
    /// Thread model: Push arrives on the PortAudio input-callback thread;
    /// Stop and Volume arrive from the UI. A single small lock guards the
    /// device lifecycle; the steady-state Push path holds it only long
    /// enough to hand bytes to the buffered provider.
    /// </summary>
    public sealed class TxMonitorPlayer : IDisposable
    {
        private readonly object _lock = new object();
        private WaveOutEvent _waveOut;
        private BufferedWaveProvider _provider;
        private byte[] _byteBuf = Array.Empty<byte>();
        private int _sampleRate;
        private volatile float _volume = 1.0f;
        private volatile bool _disposed;

        /// <summary>Playback volume 0..1. Applied to the output device.</summary>
        public float Volume
        {
            get { return _volume; }
            set
            {
                float v = Math.Clamp(value, 0f, 1f);
                _volume = v;
                lock (_lock)
                {
                    if (_waveOut != null) _waveOut.Volume = v;
                }
            }
        }

        /// <summary>
        /// Feed one buffer of interleaved stereo float samples. Called on the
        /// audio callback thread; starts the output device lazily on first
        /// use and rebuilds it if the stream's sample rate changes.
        /// </summary>
        public void Push(float[] buffer, int count, int sampleRate)
        {
            if (_disposed || count <= 0) return;
            if (sampleRate <= 0) sampleRate = 48000;

            lock (_lock)
            {
                if (_disposed) return;
                if (_waveOut == null || _sampleRate != sampleRate)
                {
                    StartLocked(sampleRate);
                    if (_waveOut == null) return; // device refused; stay silent
                }

                int bytes = count * sizeof(float);
                if (_byteBuf.Length < bytes) _byteBuf = new byte[bytes];
                Buffer.BlockCopy(buffer, 0, _byteBuf, 0, bytes);
                _provider.AddSamples(_byteBuf, 0, bytes);
            }
        }

        /// <summary>
        /// Open the output device ahead of the first Push, from a UI thread.
        /// Without this the device would open lazily inside the PortAudio
        /// input callback — a few milliseconds of stall in the one place that
        /// must never stall. The player still rebuilds itself if the stream
        /// turns out to run at a different rate.
        /// </summary>
        public void Prewarm(int sampleRate = 48000)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_waveOut == null || _sampleRate != sampleRate)
                    StartLocked(sampleRate);
            }
        }

        /// <summary>Stop playback and release the output device. Push after
        /// Stop simply starts it again — Stop is "monitor off", not disposal.</summary>
        public void Stop()
        {
            lock (_lock)
            {
                StopLocked();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                StopLocked();
            }
        }

        private void StartLocked(int sampleRate)
        {
            StopLocked();
            try
            {
                _provider = new BufferedWaveProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2))
                {
                    // A couple of seconds of headroom; if the UI stalls the
                    // reader we drop rather than block the audio callback.
                    BufferDuration = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true
                };
                var waveOut = new WaveOutEvent { DesiredLatency = 150 };
                waveOut.Init(_provider);
                waveOut.Volume = _volume;
                waveOut.Play();
                _waveOut = waveOut;
                _sampleRate = sampleRate;
            }
            catch (Exception ex)
            {
                // No output device, exclusive-mode clash, whatever — the
                // monitor is an aid, never a reason to disturb the TX path.
                Trace.WriteLine("TxMonitorPlayer: output open failed: " + ex.Message);
                _waveOut = null;
                _provider = null;
                _sampleRate = 0;
            }
        }

        private void StopLocked()
        {
            try
            {
                _waveOut?.Stop();
                _waveOut?.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxMonitorPlayer: stop failed: " + ex.Message);
            }
            _waveOut = null;
            _provider = null;
            _sampleRate = 0;
        }
    }
}

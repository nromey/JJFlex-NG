using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;
using NAudio.Wave;

namespace JJFlexWpf
{
    /// <summary>
    /// ISampleProvider that performs spectral subtraction noise reduction.
    /// Two modes: Sampling (captures noise profile) and Active (subtracts noise).
    /// Uses FftSharp for FFT/IFFT processing with overlap-add reconstruction.
    /// Sprint 25 Phase 11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each channel is processed on its own (#732, Sprint 48 Track G).</b>
    /// This used to read channel 0, subtract, and write the result into every
    /// channel, which replaced a right-panned slice with the left channel's
    /// audio. Every channel now has its own frame accumulator, overlap-add
    /// ring and noise profile; the arithmetic per channel is unchanged.
    /// </para>
    /// <para>
    /// <b>Profiles.</b> A profile captured live is captured per channel, so
    /// each channel subtracts the noise it actually carries. The saved file
    /// still holds ONE spectrum (the format is unchanged): it is the per-bin
    /// maximum across the channels, because a channel that is silent says
    /// nothing about the band, and with the channels identical (one slice,
    /// centred) that is exactly the channel-0 spectrum it always saved. A
    /// loaded profile is applied to every channel.
    /// </para>
    /// </remarks>
    public class SpectralSubtractionProvider : ISampleProvider
    {
        private readonly ISampleProvider? _source;
        private readonly WaveFormat _waveFormat;

        // FFT parameters
        private const int FftSize = 1024;
        private const int HopSize = FftSize / 2; // 50% overlap
        // FftSharp 2.2 replaced the static FftSharp.Window.Hanning(size) helper
        // with window classes: new FftSharp.Windows.Hanning().Create(size).
        // Deliberately the SYMMETRIC Hanning, not the HanningPeriodic that 2.2
        // also offers - periodic is arguably the better choice for overlap-add
        // STFT, but it is a different window and would change how this sounds.
        // That is a DSP decision, not a dependency upgrade.
        private readonly double[] _window;
        private readonly double[] _fftBuffer;

        private const int OutputBufferSize = FftSize * 4; // ring buffer

        /// <summary>
        /// Everything that used to be one set of fields for channel 0, now one
        /// per channel: input accumulation, the overlap-add output ring, and
        /// the noise profile and its capture.
        /// </summary>
        private sealed class ChannelState
        {
            public readonly float[] InputAccum = new float[FftSize];
            public int InputAccumCount;
            public readonly float[] OutputBuffer = new float[OutputBufferSize];
            public int OutputReadPos;
            public int OutputWritePos;
            public double[]? NoiseProfile;
            public readonly List<double[]> SamplingFrames = new();
            public int SampleFramesCaptured;
        }

        private ChannelState[] _channels;

        private bool _sampling;
        private int _sampleFrameTarget;
        private int _sampleFramesCaptured;

        /// <summary>Whether spectral subtraction is active (subtracting noise).</summary>
        public bool Enabled { get; set; }

        /// <summary>Subtraction aggressiveness 0.0-1.0. Higher = more noise removed, more artifacts.</summary>
        public float Strength { get; set; } = 0.7f;

        /// <summary>Spectral floor to prevent musical noise artifacts. 0.0-1.0 of original magnitude.</summary>
        public float SpectralFloor { get; set; } = 0.02f;

        /// <summary>True while actively sampling noise.</summary>
        public bool IsSampling => _sampling;

        /// <summary>Sampling progress 0.0-1.0.</summary>
        public float SamplingProgress => _sampleFrameTarget > 0
            ? Math.Min(1f, (float)_sampleFramesCaptured / _sampleFrameTarget)
            : 0f;

        /// <summary>True if a noise profile has been captured and is ready for subtraction.</summary>
        public bool HasProfile => _channels[0].NoiseProfile != null;

        /// <summary>Name of the currently loaded profile.</summary>
        public string ProfileName { get; set; } = "";

        public WaveFormat WaveFormat => _waveFormat;

        /// <summary>
        /// ISampleProvider constructor — wraps an existing audio source for NAudio chain use.
        /// </summary>
        public SpectralSubtractionProvider(ISampleProvider source)
        {
            _source = source;
            _waveFormat = source.WaveFormat;
            _window = new FftSharp.Windows.Hanning().Create(FftSize);
            _fftBuffer = new double[FftSize];
            _channels = NewChannels(_waveFormat.Channels);
        }

        /// <summary>
        /// Standalone constructor for live audio pipeline use (no ISampleProvider source).
        /// Call ProcessInPlace() to process buffers directly.
        /// </summary>
        public SpectralSubtractionProvider(int sampleRate = 48000, int channels = 2)
        {
            _source = null;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _window = new FftSharp.Windows.Hanning().Create(FftSize);
            _fftBuffer = new double[FftSize];
            _channels = NewChannels(channels);
        }

        private static ChannelState[] NewChannels(int count)
        {
            var states = new ChannelState[Math.Max(1, count)];
            for (int i = 0; i < states.Length; i++) states[i] = new ChannelState();
            return states;
        }

        /// <summary>
        /// The stream's channel count changed: rebuild the per-channel state.
        /// The profile carries over as a single spectrum applied to every
        /// channel, the same rule a loaded profile follows, and a capture in
        /// progress is cancelled because its frames no longer line up.
        /// </summary>
        private void EnsureChannels(int channels)
        {
            if (channels < 1 || _channels.Length == channels) return;
            var profile = ProfileForSaving();
            if (_sampling) CancelSampling();
            _channels = NewChannels(channels);
            foreach (var c in _channels) c.NoiseProfile = profile;
        }

        /// <summary>
        /// Start sampling noise. Call this when the band is quiet (no signals).
        /// </summary>
        /// <param name="durationSeconds">How long to sample (1-5 seconds).
        /// Default 3 per the ratified capture-window decision
        /// (project_sub_capture_window_decision) — was 2 until the DSP
        /// controls track, 2026-08-11.</param>
        public void StartSampling(int durationSeconds = 3)
        {
            durationSeconds = Math.Clamp(durationSeconds, 1, 5);
            int framesPerSecond = WaveFormat.SampleRate / HopSize;
            _sampleFrameTarget = framesPerSecond * durationSeconds;
            _sampleFramesCaptured = 0;
            foreach (var c in _channels)
            {
                c.SamplingFrames.Clear();
                c.SampleFramesCaptured = 0;
            }
            _sampling = true;
        }

        /// <summary>
        /// Cancel an in-progress sampling session.
        /// </summary>
        public void CancelSampling()
        {
            _sampling = false;
            foreach (var c in _channels) c.SamplingFrames.Clear();
        }

        // NAudio 3.0: ISampleProvider.Read takes a Span<float>. offset/count
        // are re-declared here so the body's index arithmetic is unchanged -
        // buffer[offset + n] indexes a Span exactly as it did an array.
        public int Read(Span<float> buffer)
        {
            int offset = 0;
            int count = buffer.Length;
            int totalRead = _source?.Read(buffer.Slice(offset, count)) ?? 0;
            if (totalRead == 0) return 0;

            int channels = WaveFormat.Channels;
            EnsureChannels(channels);

            if (_sampling)
            {
                AccumulateForSampling(buffer, offset, totalRead, channels);
                return totalRead;
            }

            if (!Enabled || !HasProfile)
                return totalRead;

            return ProcessSubtraction(buffer, offset, totalRead, channels);
        }

        /// <summary>
        /// Process audio buffer in-place without ISampleProvider chain.
        /// Used by the live RX audio pipeline. Handles both sampling and
        /// active subtraction modes.
        /// </summary>
        public void ProcessInPlace(float[] buffer, int offset, int count, int channels)
        {
            if (count == 0) return;
            EnsureChannels(channels);

            if (_sampling)
            {
                AccumulateForSampling(buffer, offset, count, channels);
                return;
            }

            if (!Enabled || !HasProfile) return;
            ProcessSubtraction(buffer, offset, count, channels);
        }

        private void AccumulateForSampling(Span<float> buffer, int offset, int count, int channels)
        {
            for (int ch = 0; ch < channels; ch++)
                AccumulateChannel(_channels[ch], buffer, offset + ch, offset + count, channels);

            // Every channel sees the same number of samples, so they reach the
            // target on the same frame; channel 0 stands for all of them.
            _sampleFramesCaptured = _channels[0].SampleFramesCaptured;
            if (_sampleFramesCaptured >= _sampleFrameTarget)
                FinalizeSampling();
        }

        private void AccumulateChannel(ChannelState st, Span<float> buffer, int first, int end, int channels)
        {
            if (st.SampleFramesCaptured >= _sampleFrameTarget) return;

            for (int i = first; i < end; i += channels)
            {
                st.InputAccum[st.InputAccumCount++] = buffer[i];

                if (st.InputAccumCount >= FftSize)
                {
                    // We have a full frame — FFT it and store the magnitude
                    for (int j = 0; j < FftSize; j++)
                        _fftBuffer[j] = st.InputAccum[j] * _window[j];

                    var spectrum = FftSharp.FFT.Forward(_fftBuffer);
                    var magnitudes = new double[spectrum.Length];
                    for (int k = 0; k < spectrum.Length; k++)
                        magnitudes[k] = spectrum[k].Magnitude;
                    st.SamplingFrames.Add(magnitudes);
                    st.SampleFramesCaptured++;

                    // Shift by HopSize for overlap
                    Array.Copy(st.InputAccum, HopSize, st.InputAccum, 0, FftSize - HopSize);
                    st.InputAccumCount = FftSize - HopSize;

                    if (st.SampleFramesCaptured >= _sampleFrameTarget)
                        return;
                }
            }
        }

        private void FinalizeSampling()
        {
            _sampling = false;

            foreach (var st in _channels)
            {
                if (st.SamplingFrames.Count == 0) continue;

                int spectrumSize = st.SamplingFrames[0].Length;
                var profile = new double[spectrumSize];

                // Average all captured frames to build the noise profile
                foreach (var frame in st.SamplingFrames)
                {
                    for (int i = 0; i < spectrumSize && i < frame.Length; i++)
                        profile[i] += frame[i];
                }
                for (int i = 0; i < spectrumSize; i++)
                    profile[i] /= st.SamplingFrames.Count;

                st.NoiseProfile = profile;
                st.SamplingFrames.Clear();
            }

            Trace.WriteLine($"SpectralSubtraction: noise profile captured ({_sampleFramesCaptured} frames, {_channels.Length} channel(s))");
        }

        private int ProcessSubtraction(Span<float> buffer, int offset, int totalRead, int channels)
        {
            for (int ch = 0; ch < channels; ch++)
                SubtractChannel(_channels[ch], buffer, offset + ch, offset + totalRead, channels);
            return totalRead;
        }

        private void SubtractChannel(ChannelState st, Span<float> buffer, int first, int end, int channels)
        {
            // Feed samples into the accumulation buffer
            for (int i = first; i < end; i += channels)
            {
                st.InputAccum[st.InputAccumCount++] = buffer[i];

                if (st.InputAccumCount >= FftSize)
                {
                    ProcessOneFrame(st);

                    // Shift by HopSize for overlap
                    Array.Copy(st.InputAccum, HopSize, st.InputAccum, 0, FftSize - HopSize);
                    st.InputAccumCount = FftSize - HopSize;
                }
            }

            // Read processed samples from the output ring buffer back into the
            // caller's buffer. Where the ring has nothing yet (the first frame
            // of a stream), the sample is left as it came in, as it always was.
            for (int i = first; i < end; i += channels)
            {
                if (st.OutputReadPos != st.OutputWritePos)
                {
                    buffer[i] = st.OutputBuffer[st.OutputReadPos];
                    st.OutputReadPos = (st.OutputReadPos + 1) % OutputBufferSize;
                }
            }
        }

        private void ProcessOneFrame(ChannelState st)
        {
            var noiseProfile = st.NoiseProfile;
            if (noiseProfile == null) return;

            // Apply window and copy to FFT buffer
            for (int i = 0; i < FftSize; i++)
                _fftBuffer[i] = st.InputAccum[i] * _window[i];

            // Forward FFT (in-place, returns complex pairs)
            System.Numerics.Complex[] spectrum = FftSharp.FFT.Forward(_fftBuffer);

            // Subtract noise profile from magnitudes, preserving phase
            for (int i = 0; i < spectrum.Length && i < noiseProfile.Length; i++)
            {
                double mag = spectrum[i].Magnitude;
                double phase = spectrum[i].Phase;

                // Subtract noise with strength scaling
                double cleanMag = mag - noiseProfile[i] * Strength;

                // Spectral floor prevents musical noise
                double floor = mag * SpectralFloor;
                if (cleanMag < floor) cleanMag = floor;

                spectrum[i] = System.Numerics.Complex.FromPolarCoordinates(cleanMag, phase);
            }

            // Inverse FFT (modifies spectrum in-place)
            FftSharp.FFT.Inverse(spectrum);
            for (int i = 0; i < FftSize && i < spectrum.Length; i++)
                _fftBuffer[i] = spectrum[i].Real;

            // Overlap-add into output buffer
            for (int i = 0; i < FftSize; i++)
            {
                int pos = (st.OutputWritePos + i) % OutputBufferSize;
                st.OutputBuffer[pos] += (float)(_fftBuffer[i] * _window[i]);
            }

            // Advance write position by HopSize (the non-overlapping portion is now final)
            // Clear the next HopSize samples in the ring buffer for the next overlap-add
            for (int i = 0; i < HopSize; i++)
            {
                int clearPos = (st.OutputWritePos + FftSize + i) % OutputBufferSize;
                st.OutputBuffer[clearPos] = 0;
            }
            st.OutputWritePos = (st.OutputWritePos + HopSize) % OutputBufferSize;
        }

        /// <summary>
        /// The one spectrum a saved file holds: per bin, the loudest channel's
        /// noise. Identical to channel 0 whenever the channels are identical.
        /// </summary>
        private double[]? ProfileForSaving()
        {
            double[]? merged = null;
            foreach (var st in _channels)
            {
                var p = st.NoiseProfile;
                if (p == null) continue;
                if (merged == null) { merged = (double[])p.Clone(); continue; }
                for (int i = 0; i < merged.Length && i < p.Length; i++)
                    if (p[i] > merged[i]) merged[i] = p[i];
            }
            return merged;
        }

        /// <summary>
        /// Load a noise profile from a saved file.
        /// </summary>
        public bool LoadProfile(string filePath)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(NoiseProfileData));
                using var stream = File.OpenRead(filePath);
                var data = (NoiseProfileData?)serializer.Deserialize(stream);
                if (data?.Magnitudes != null)
                {
                    foreach (var st in _channels) st.NoiseProfile = data.Magnitudes;
                    ProfileName = data.Name ?? Path.GetFileNameWithoutExtension(filePath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"SpectralSubtraction.LoadProfile failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Save the current noise profile to a file.
        /// </summary>
        public bool SaveProfile(string filePath, string name, string band = "", string antenna = "")
        {
            var profile = ProfileForSaving();
            if (profile == null) return false;

            try
            {
                var data = new NoiseProfileData
                {
                    Name = name,
                    Band = band,
                    Antenna = antenna,
                    CapturedUtc = DateTime.UtcNow,
                    SampleRate = WaveFormat.SampleRate,
                    FftSize = FftSize,
                    Magnitudes = profile
                };

                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? "");
                var serializer = new XmlSerializer(typeof(NoiseProfileData));
                using var stream = File.Create(filePath);
                serializer.Serialize(stream, data);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"SpectralSubtraction.SaveProfile failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>Clear the current noise profile.</summary>
        public void ClearProfile()
        {
            foreach (var st in _channels) st.NoiseProfile = null;
            ProfileName = "";
        }
    }

    /// <summary>
    /// Serializable noise profile data for save/load/share.
    /// </summary>
    [XmlRoot("NoiseProfile")]
    public class NoiseProfileData
    {
        public string Name { get; set; } = "";
        public string Band { get; set; } = "";
        public string Antenna { get; set; } = "";
        public DateTime CapturedUtc { get; set; }
        public int SampleRate { get; set; }
        public int FftSize { get; set; }
        public double[]? Magnitudes { get; set; }
    }
}

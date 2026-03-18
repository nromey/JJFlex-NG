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
    public class SpectralSubtractionProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        // FFT parameters
        private const int FftSize = 1024;
        private const int HopSize = FftSize / 2; // 50% overlap
        private readonly double[] _window;
        private readonly double[] _fftBuffer;
        private readonly double[] _overlapBuffer;

        // Noise profile (averaged magnitude spectrum)
        private double[]? _noiseProfile;
        private readonly List<double[]> _samplingFrames = new();
        private bool _sampling;
        private int _sampleFrameTarget;
        private int _sampleFramesCaptured;

        // Input accumulation buffer for building full FFT frames
        private readonly float[] _inputAccum;
        private int _inputAccumCount;

        // Output overlap-add buffer
        private readonly float[] _outputBuffer;
        private int _outputReadPos;
        private int _outputWritePos;
        private const int OutputBufferSize = FftSize * 4; // ring buffer

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
        public bool HasProfile => _noiseProfile != null;

        /// <summary>Name of the currently loaded profile.</summary>
        public string ProfileName { get; set; } = "";

        public WaveFormat WaveFormat => _source.WaveFormat;

        public SpectralSubtractionProvider(ISampleProvider source)
        {
            _source = source;
            _window = FftSharp.Window.Hanning(FftSize);
            _fftBuffer = new double[FftSize];
            _overlapBuffer = new double[FftSize];
            _inputAccum = new float[FftSize];
            _inputAccumCount = 0;
            _outputBuffer = new float[OutputBufferSize];
            _outputReadPos = 0;
            _outputWritePos = 0;
        }

        /// <summary>
        /// Start sampling noise. Call this when the band is quiet (no signals).
        /// </summary>
        /// <param name="durationSeconds">How long to sample (1-5 seconds).</param>
        public void StartSampling(int durationSeconds = 2)
        {
            durationSeconds = Math.Clamp(durationSeconds, 1, 5);
            int framesPerSecond = WaveFormat.SampleRate / HopSize;
            _sampleFrameTarget = framesPerSecond * durationSeconds;
            _sampleFramesCaptured = 0;
            _samplingFrames.Clear();
            _sampling = true;
        }

        /// <summary>
        /// Cancel an in-progress sampling session.
        /// </summary>
        public void CancelSampling()
        {
            _sampling = false;
            _samplingFrames.Clear();
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int totalRead = _source.Read(buffer, offset, count);
            if (totalRead == 0) return 0;

            int channels = WaveFormat.Channels;

            if (_sampling)
            {
                // Accumulate samples for noise profiling (don't modify audio)
                AccumulateForSampling(buffer, offset, totalRead, channels);
                return totalRead;
            }

            if (!Enabled || _noiseProfile == null)
                return totalRead;

            // Active subtraction mode
            return ProcessSubtraction(buffer, offset, totalRead, channels);
        }

        private void AccumulateForSampling(float[] buffer, int offset, int count, int channels)
        {
            for (int i = offset; i < offset + count; i += channels)
            {
                _inputAccum[_inputAccumCount++] = buffer[i];

                if (_inputAccumCount >= FftSize)
                {
                    // We have a full frame — FFT it and store the magnitude
                    for (int j = 0; j < FftSize; j++)
                        _fftBuffer[j] = _inputAccum[j] * _window[j];

                    var spectrum = FftSharp.FFT.Forward(_fftBuffer);
                    var magnitudes = new double[spectrum.Length];
                    for (int k = 0; k < spectrum.Length; k++)
                        magnitudes[k] = spectrum[k].Magnitude;
                    _samplingFrames.Add(magnitudes);
                    _sampleFramesCaptured++;

                    // Shift by HopSize for overlap
                    Array.Copy(_inputAccum, HopSize, _inputAccum, 0, FftSize - HopSize);
                    _inputAccumCount = FftSize - HopSize;

                    if (_sampleFramesCaptured >= _sampleFrameTarget)
                    {
                        FinalizeSampling();
                        return;
                    }
                }
            }
        }

        private void FinalizeSampling()
        {
            _sampling = false;

            if (_samplingFrames.Count == 0) return;

            int spectrumSize = _samplingFrames[0].Length;
            _noiseProfile = new double[spectrumSize];

            // Average all captured frames to build the noise profile
            foreach (var frame in _samplingFrames)
            {
                for (int i = 0; i < spectrumSize && i < frame.Length; i++)
                    _noiseProfile[i] += frame[i];
            }
            for (int i = 0; i < spectrumSize; i++)
                _noiseProfile[i] /= _samplingFrames.Count;

            _samplingFrames.Clear();
            Trace.WriteLine($"SpectralSubtraction: noise profile captured ({_sampleFramesCaptured} frames)");
        }

        private int ProcessSubtraction(float[] buffer, int offset, int totalRead, int channels)
        {
            // Feed samples into the accumulation buffer
            for (int i = offset; i < offset + totalRead; i += channels)
            {
                _inputAccum[_inputAccumCount++] = buffer[i];

                if (_inputAccumCount >= FftSize)
                {
                    ProcessOneFrame();

                    // Shift by HopSize for overlap
                    Array.Copy(_inputAccum, HopSize, _inputAccum, 0, FftSize - HopSize);
                    _inputAccumCount = FftSize - HopSize;
                }
            }

            // Read processed samples from the output ring buffer back into the caller's buffer
            int samplesWritten = 0;
            for (int i = offset; i < offset + totalRead; i += channels)
            {
                if (_outputReadPos != _outputWritePos)
                {
                    float processed = _outputBuffer[_outputReadPos];
                    _outputReadPos = (_outputReadPos + 1) % OutputBufferSize;
                    buffer[i] = processed;
                    // Copy to other channels
                    for (int ch = 1; ch < channels && i + ch < offset + totalRead; ch++)
                        buffer[i + ch] = processed;
                    samplesWritten++;
                }
            }

            return totalRead;
        }

        private void ProcessOneFrame()
        {
            if (_noiseProfile == null) return;

            // Apply window and copy to FFT buffer
            for (int i = 0; i < FftSize; i++)
                _fftBuffer[i] = _inputAccum[i] * _window[i];

            // Forward FFT (in-place, returns complex pairs)
            System.Numerics.Complex[] spectrum = FftSharp.FFT.Forward(_fftBuffer);

            // Subtract noise profile from magnitudes, preserving phase
            for (int i = 0; i < spectrum.Length && i < _noiseProfile.Length; i++)
            {
                double mag = spectrum[i].Magnitude;
                double phase = spectrum[i].Phase;

                // Subtract noise with strength scaling
                double cleanMag = mag - _noiseProfile[i] * Strength;

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
                int pos = (_outputWritePos + i) % OutputBufferSize;
                _outputBuffer[pos] += (float)(_fftBuffer[i] * _window[i]);
            }

            // Advance write position by HopSize (the non-overlapping portion is now final)
            // Clear the next HopSize samples in the ring buffer for the next overlap-add
            for (int i = 0; i < HopSize; i++)
            {
                int clearPos = (_outputWritePos + FftSize + i) % OutputBufferSize;
                _outputBuffer[clearPos] = 0;
            }
            _outputWritePos = (_outputWritePos + HopSize) % OutputBufferSize;
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
                    _noiseProfile = data.Magnitudes;
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
            if (_noiseProfile == null) return false;

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
                    Magnitudes = _noiseProfile
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
            _noiseProfile = null;
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

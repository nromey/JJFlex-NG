using System;
using System.Collections.Generic;
using System.Threading;

namespace JJFlexWpf
{
    /// <summary>480-sample adapter around the existing mono subtraction arithmetic.</summary>
    public sealed class SpectralCaptureEngine : IDenoiseEngine
    {
        private SpectralSubtractionProvider _provider = new(48000, 1) { Enabled = true };
        private readonly float[] _frame = new float[480];
        private float _subtraction = 0.7f, _floor = 0.02f;
        public string Id => "spectral_capture";
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public int LatencySamples => _provider.HasProfile ? 992 : 0;
        public bool IsAvailable => true;
        public static IReadOnlyList<EngineSettingDescriptor> SettingDescriptors { get; } = new[] {
            new EngineSettingDescriptor("subtraction_amount", "audio.denoise.subtraction_amount", EngineSettingKind.Number,
                new EngineSettingValue(0.7), 0, 1, 0.01),
            new EngineSettingDescriptor("spectral_floor", "audio.denoise.spectral_floor", EngineSettingKind.Number,
                new EngineSettingValue(0.02), 0, 0.2, 0.001)
        };
        public IReadOnlyList<EngineSettingDescriptor> Settings => SettingDescriptors;
        public EngineSettingValue GetSetting(string name) => name switch {
            "subtraction_amount" => new EngineSettingValue(Volatile.Read(ref _subtraction)),
            "spectral_floor" => new EngineSettingValue(Volatile.Read(ref _floor)),
            _ => throw new ArgumentException("Unknown setting", nameof(name))
        };
        public bool TrySetSetting(string name, EngineSettingValue value)
        {
            EngineSettingDescriptor? descriptor = null;
            for (int i = 0; i < Settings.Count; i++)
                if (Settings[i].Name == name) { descriptor = Settings[i]; break; }
            if (descriptor == null || !descriptor.Accepts(value)) return false;
            if (name == "subtraction_amount") Volatile.Write(ref _subtraction, (float)value.Number);
            else Volatile.Write(ref _floor, (float)value.Number);
            return true;
        }
        internal void SetProfile(double[]? profile) => _provider.SetChannelProfile(0, profile);
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (input.Length != 480 || output.Length != 480) throw new ArgumentException("Expected 480 samples");
            _provider.Strength = Volatile.Read(ref _subtraction);
            _provider.SpectralFloor = Volatile.Read(ref _floor);
            input.CopyTo(_frame);
            _provider.ProcessInPlace(_frame, 0, 480, 1);
            _frame.CopyTo(output);
            return true;
        }
        public void Reset()
        {
            var profile = _provider.GetChannelProfile(0);
            _provider = new(48000, 1) { Enabled = true };
            _provider.SetChannelProfile(0, profile);
        }
        public void Dispose() { }
    }
}

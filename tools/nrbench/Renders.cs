using JJFlexWpf;
using Radios;
using System.Diagnostics;
using System.Globalization;

namespace NrBench;
partial class BenchApp
{
    // Mirrors RxAudioPipeline.Process and PcDenoiseSlot.ParametersFor/ApplyParameters:
    // spectral before neural, RNNoise strength .8, other defaults fully wet.
    sealed class Stage
    {
        public DenoiseEngineSettings Settings { get; set; } = new() { Direction = DenoiseDirection.Receive };
        public string Variant { get; set; } = "";
        public string ProfileSource { get; set; } = "";
        public double[][]? Profile { get; set; }
        public Func<IDenoiseEngine>? TestFactory { get; set; }
        public Stage Copy() => new() { Settings = new DenoiseEngineSettings {
            EngineId = Settings.EngineId, Direction = Settings.Direction, Strength = Settings.Strength,
            Values = Settings.Values.Select(v => new DenoiseNamedValue { Name=v.Name, Number=v.Number, Choice=v.Choice }).ToList()
        }, Variant=Variant, ProfileSource=ProfileSource, Profile=Profile, TestFactory=TestFactory };
        public string Words => Settings.EngineId switch {
            "deepfilternet3" => $"DeepFilterNet3 limit {(Value("attenuation_limit_db") == 100 ? "unlimited" : Value("attenuation_limit_db").ToString("0.###", CultureInfo.InvariantCulture) + " dB")}",
            "rnnoise" => $"{(Variant == "Historical" ? "rnnoise-before-sprint49 (old input level, aligned blend)" : "rnnoise (corrected input level, aligned blend)")} {Settings.Strength * 100:0} percent",
            "spectral_capture" => $"capture subtract {Value("subtraction_amount"):0.##} floor {Value("spectral_floor"):0.###}, {ProfileSource}",
            _ => Settings.EngineId
        };
        public double Value(string name) => Settings.Values.Single(x => x.Name == name).Number;
    }
    sealed class Render
    {
        public int Number;
        public string Group = "";
        public List<Stage> Stages = new();
        public bool Experiment, Residual, NearestComparison, SpeechFallback;
        public string SourceWords = "";
        public string? Refusal;
        public string? FileName;
        // Audio lives only while this render and its immediate residual are finished.
        public float[]? Audio;
        public Action? Prepare;
        public Render? ResidualRender;
        public bool CaptureQuiet, CaptureSpeech;
        public double[][]? QuietProfile, SpeechProfile;
        public string? QuietProfileRefusal, SpeechProfileRefusal;
        public int Latency, MeasuredDelay;
        public double Speech, Noise, GainDb, RequestedGainDb, Change;
        public List<string> Notes = new();
        public int Compare;
        public string Words => (Residual ? "residual of " : "") + (Experiment ? "experiment " : "") +
            (SourceWords.Length > 0 ? SourceWords : Stages.Count == 0 ? "unprocessed" : string.Join(" then ", Stages.Select(x => x.Words)));
    }
    static Stage Neural(double limit) => new() { Settings = new() { EngineId="deepfilternet3", Direction=DenoiseDirection.Receive,
        Strength=1, Values=new() { new() { Name="attenuation_limit_db", Number=limit }, new() { Name="post_filter_beta", Number=0 } } } };
    static Stage Rn(float strength=.8f, string scaling="Corrected") => new() { Variant=scaling,
        Settings = new() { EngineId="rnnoise", Direction=DenoiseDirection.Receive, Strength=strength } };
    static Stage CaptureStage(double[][]? profile, string source, double amount=.7) => new() {
        Profile=profile, ProfileSource=source, Settings=new() { EngineId="spectral_capture", Direction=DenoiseDirection.Receive, Strength=1,
            Values=new() { new() { Name="subtraction_amount", Number=amount }, new() { Name="spectral_floor", Number=.02 } } } };
    static IDenoiseEngine MakeEngine(Stage stage, string library) => stage.TestFactory?.Invoke() ?? stage.Settings.EngineId switch {
        "deepfilternet3" => new DeepFilterNet3Engine(libraryPath:library),
        "rnnoise" => new RnnoiseEngine(Enum.Parse<RnnoiseScaling>(stage.Variant)),
        "spectral_capture" => new SpectralCaptureEngine(),
        _ => throw new InvalidOperationException("Unknown engine " + stage.Settings.EngineId)
    };
    static void ApplySettings(Stage stage, IDenoiseEngine engine, int channel)
    {
        if (stage.Settings.Direction != DenoiseDirection.Receive || !float.IsFinite(stage.Settings.Strength) || stage.Settings.Strength < 0 || stage.Settings.Strength > 1)
            throw new InvalidOperationException("Invalid receive strength or direction");
        if(engine is RnnoiseEngine rn && rn.Scaling.ToString()!=stage.Variant)throw new InvalidOperationException("RNNoise scaling readback mismatch");
        if (engine.Id != stage.Settings.EngineId) throw new InvalidOperationException("Engine id mismatch");
        if (!engine.IsAvailable) throw new InvalidOperationException(engine.Id + " unavailable: " + engine.UnavailabilityDetail);
        if (engine.FrameLength != Frame || engine.SampleRate != Rate) throw new InvalidOperationException("Unsupported engine frame or rate");
        if (stage.Settings.Values.Select(x=>x.Name).Distinct().Count() != stage.Settings.Values.Count)
            throw new InvalidOperationException("Duplicate named setting");
        foreach (var descriptor in engine.Settings)
            if (!stage.Settings.Values.Any(x => x.Name == descriptor.Name)) stage.Settings.Values.Add(new() {
                Name=descriptor.Name, Number=descriptor.Default.Number, Choice=descriptor.Default.Choice });
        foreach (var value in stage.Settings.Values)
        {
            if (!engine.TrySetSetting(value.Name, new(value.Number, value.Choice)))
                throw new InvalidOperationException($"{engine.Id} refused setting {value.Name}={value.Choice ?? value.Number.ToString(CultureInfo.InvariantCulture)}");
            var actual = engine.GetSetting(value.Name);
            if (actual.Choice != value.Choice || Math.Abs(actual.Number-value.Number) > 1e-6)
                throw new InvalidOperationException($"{engine.Id} setting readback mismatch: {value.Name}");
            value.Number=actual.Number; value.Choice=actual.Choice;
        }
        if (engine is SpectralCaptureEngine spectral)
        {
            // The latency readback is the completion guard, including an absent profile.
            spectral.SetProfile(stage.Profile != null && channel < stage.Profile.Length ? stage.Profile[channel] : null);
            if (Mutation!="ripple-profile" && spectral.LatencySamples != 992)
                throw new InvalidOperationException("Capture profile did not engage 992-sample latency: " + stage.ProfileSource);
        }
    }
    sealed class Chain : IDisposable
    {
        readonly List<NoiseReductionProvider> providers = new();
        public int Latency => providers.Sum(x => x.LatencySamples);
        public Chain(List<Stage> stages, int channels, string library)
        {
            try
            {
                foreach (Stage stage in stages)
                {
                    // Validate and set profiles inside the factory, before any frame or latency read.
                    int ch=0;
                    var provider=new NoiseReductionProvider(Rate, channels, () => {
                        var engine=MakeEngine(stage,library);
                        try { ApplySettings(stage,engine,ch++); return engine; }
                        catch { engine.Dispose(); throw; }
                    }) { Enabled=true, Strength=stage.Settings.Strength };
                    providers.Add(provider);
                    if (provider.Bank == null || !provider.Bank.IsAvailable)
                        throw new InvalidOperationException("Could not initialise " + stage.Words + "; missing library, invalid setting or profile. See validation below.");
                }
            }
            catch { Dispose(); throw; }
        }
        public void Process(float[] buffer, int offset, int count, int channels)
        {
            if (count != Frame*channels) throw new InvalidOperationException("Bench packets must be whole 480-sample frames");
            foreach(var p in providers) p.ProcessInPlace(buffer,offset,count,channels);
        }
        public void CheckHealth()
        {
            foreach(var p in providers)
            {
                var bank=p.Bank!;
                if (!bank.IsAvailable || bank.FallbackFrames != 0 || bank.LateFrames != 0 || bank.CarryLatencySamples != 0)
                    throw new InvalidOperationException($"{p.EngineId}: unavailable or failed frames: fallback {bank.FallbackFrames}, late {bank.LateFrames}, carry {bank.CarryLatencySamples}");
            }
        }
        public void Dispose() { foreach(var p in providers) p.Dispose(); }
    }
    static float[] ProcessChain(float[] input, int channels, List<Stage> stages, string library, out int latency, List<double>? times=null)
    {
        // Validate outside the provider too: it intentionally catches init exceptions, whereas
        // the bench must retain the exact refused setting in its refusal text.
        foreach(var stage in stages)
            for(int ch=0;ch<channels;ch++) { using var e=MakeEngine(stage,library); ApplySettings(stage,e,ch); }
        using var chain=new Chain(stages,channels,library);
        latency=chain.Latency;
        int packet=Frame*channels;
        // Whole packets plus tail flush preserve all input and delayed output. Never feed a short final packet.
        int count=((input.Length + channels*latency + packet-1)/packet)*packet;
        var output=new float[count]; input.CopyTo(output,0);
        for(int p=0;p<output.Length;p+=packet)
        {
            long t=Stopwatch.GetTimestamp(); chain.Process(output,p,packet,channels);
            times?.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        chain.CheckHealth();
        if(chain.Latency != latency) throw new InvalidOperationException("Latency changed during render");
        if(output.Any(x=>!float.IsFinite(x))) throw new InvalidOperationException("Non-finite engine output");
        return output;
    }
}

using JJFlexWpf;
using Radios;

namespace NrBench;
// Sprint 49 bench CW: the WDSP helper's engines (NNR with each of its four models,
// NR2) and NR4 (libspecbleach, in process). The helper runs as the app runs it: the
// app's own WdspHost and WdspHelperEngine, compiled in by link, over the real pipe,
// with the one bench-only difference the engine already offers, the offline
// per-frame wait, so a faster-than-real-time caller gets exactly the live delay and
// never runs ahead of the helper.
partial class BenchApp
{
    /// <summary>The helper the bench drives; set by the helper option, else found by <see cref="LocateBenchHelper"/>.</summary>
    static string? HelperPath;
    static WdspHost? benchHost;
    static readonly object hostGate = new();

    /// <summary>The four NNR model slots the helper offers at WDSP 2.11, in WDSP's own slot order.</summary>
    static readonly string[] NnrModels = { "standard", "premium", "standard_cw", "premium_cw" };

    /// <summary>
    /// The delays the helper documents (its README, "What the helper offers"), before the
    /// pipeline's k frames. The self-check holds the engine's reported delay to these, so a
    /// model that silently did not take shows up on premium_cw, the slot whose delay differs.
    /// </summary>
    static int DocumentedAlgorithmicDelay(string engineId, string? model) => engineId switch
    {
        "nnr" => model == "premium_cw" ? 2070 : 2454,
        "nr2" => 4064,
        "nr4" => 512,
        _ => throw new InvalidOperationException("No documented delay for " + engineId),
    };

    static bool IsHelperEngine(string id) => id is "nnr" or "nr2";
    static bool IsNewEngine(string id) => id is "nnr" or "nr2" or "nr4";

    static string LocateBenchHelper()
    {
        if (Mutation == "helper-missing") return @"C:\nonexistent\" + WdspHelperEngine.HelperFileName;
        if (HelperPath != null) return HelperPath;
        string beside = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", WdspHelperEngine.HelperFileName);
        if (File.Exists(beside)) return beside;
        // Track A's local build, the one Sprint 49 renders with. Not in this repository: the helper is GPL.
        return @"C:\dev\jjflex-wdsp-helper\build\x64-Release\" + WdspHelperEngine.HelperFileName;
    }

    /// <summary>
    /// One host per path for the bench's life. Timings are the app's except where an offline
    /// caller differs: a longer construction wait and stall allowance, because the bench can
    /// spend seconds between renders measuring while the helper is idle, and the first
    /// handshake plans FFTW patiently.
    /// </summary>
    static WdspHost BenchHost()
    {
        lock (hostGate)
        {
            string path = LocateBenchHelper();
            if (benchHost != null && string.Equals(benchHost.ResolvePath(), path, StringComparison.OrdinalIgnoreCase)) return benchHost;
            benchHost?.Dispose();
            benchHost = new WdspHost(path, new WdspHostPolicy
            {
                HandshakeDeadline = TimeSpan.FromSeconds(15),
                ConstructionWait = TimeSpan.FromSeconds(20),
                Stall = TimeSpan.FromSeconds(10),
                OfflineFrameWait = TimeSpan.FromSeconds(10),
            });
            return benchHost;
        }
    }

    static void DisposeBenchHost()
    {
        lock (hostGate) { benchHost?.Dispose(); benchHost = null; }
    }

    static IReadOnlyDictionary<string, EngineSettingValue> Starting(Stage stage) =>
        stage.Settings.Values.ToDictionary(v => v.Name, v => new EngineSettingValue(v.Number, v.Choice));

    static IDenoiseEngine MakeHelperEngine(Stage stage) =>
        new WdspHelperEngine(BenchHost(), stage.Settings.EngineId, DenoiseDirection.Receive, Starting(stage),
            WdspHelperEngine.DefaultPipelineDepth, offlineWait: true);

    /// <summary>NNR with one model. Every other setting stays at the helper's default (mask floor -25 dB).</summary>
    static Stage Nnr(string model) => new() { Settings = new() { EngineId = "nnr", Direction = DenoiseDirection.Receive, Strength = 1,
        // nnr-same-model: the bench's own wiring broken, every model built as standard while the
        // self-check still asks for four; the distinct-models control must go red.
        Values = new() { new() { Name = "model", Choice = Mutation == "nnr-same-model" ? "standard" : model } } } };

    /// <summary>NR2 at WDSP's receive defaults, as the helper offers them.</summary>
    static Stage Nr2() => new() { Settings = new() { EngineId = "nr2", Direction = DenoiseDirection.Receive, Strength = 1 } };

    /// <summary>NR4 (libspecbleach) at the app's defaults: 20 dB reduction, low-latency mode.</summary>
    static Stage Nr4() => new() { Settings = new() { EngineId = "nr4", Direction = DenoiseDirection.Receive, Strength = 1 } };

    static string? ChoiceOf(Stage stage, string name) => stage.Settings.Values.FirstOrDefault(v => v.Name == name)?.Choice;

    /// <summary>
    /// The frames a helper engine answers false for at the start, by design: k frames of round
    /// trip, then the frames the helper's own delay discards. Anything beyond is a real late frame.
    /// </summary>
    static long StartupLateFrames(IDenoiseEngine engine)
    {
        if (engine is not WdspHelperEngine helper || engine.LatencySamples == 0) return 0;
        int algorithmic = helper.LatencySamples - helper.PipelineDepth * Frame;
        return helper.PipelineDepth + (algorithmic + Frame - 1) / Frame;
    }
}

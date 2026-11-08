using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E, E5: failed starts, the generation-0 result, and the
/// truth of Noel's S15 sentence. Windowless; the GaveUp test starts the stub
/// helper the Track B classes use, and leaves no process behind.
/// </summary>
public sealed class NoiseSlotResultTests
{
    public NoiseSlotResultTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static RxAudioPipeline PipelineWhoseDefaultCannotBuild()
    {
        var rows = new[]
        {
            new DenoiseEngineEntry("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.rnnoise", new[] { DenoiseDirection.Receive },
                _ => throw new InvalidOperationException("rnnoise.dll is the wrong architecture"), () => Unavailability.InitFailed),
            new DenoiseEngineEntry("spectral_capture", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.spectral_capture", new[] { DenoiseDirection.Receive },
                _ => new NoiseFixture.Engine("spectral_capture", SpectralCaptureEngine.SettingDescriptors, new Dictionary<string, EngineSettingValue>(), 0),
                () => Unavailability.None, SettingDescriptors: SpectralCaptureEngine.SettingDescriptors),
        };
        return new RxAudioPipeline(catalog: new DenoiseEngineCatalog(rows));
    }

    private static void Feed(RxAudioPipeline p, int buffers)
    {
        for (int i = 0; i < buffers; i++) { p.Process(new float[960]); Thread.Sleep(1); }
    }

    [Fact]
    public void A_default_that_cannot_build_says_nothing_while_the_slot_was_never_used_and_once_after_it_is_turned_on()
    {
        using var p = PipelineWhoseDefaultCannotBuild();
        var results = new ConcurrentQueue<DenoiseSelectionResult>();
        p.SelectionResult += (_, r) => results.Enqueue(r);
        Feed(p, 30);
        Thread.Sleep(100); // results are published on the thread pool
        Assert.Empty(results);

        p.NeuralSlot.Enabled = true;
        var time = Stopwatch.StartNew();
        while (results.IsEmpty && time.ElapsedMilliseconds < 3000) Feed(p, 1);
        Feed(p, 30);
        Thread.Sleep(100);
        var only = Assert.Single(results);
        Assert.False(only.Processing);
        Assert.True(only.SlotStopped);
        Assert.Equal("rnnoise", only.EngineId);
    }

    [Fact]
    public void The_controller_speaks_Noels_failed_start_sentence_verbatim_once_per_result()
    {
        using var f = new NoiseFixture();
        f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural, "nnr", false, "gave up") { SlotStopped = true });
        Assert.Equal("Noise reduction could not start. Unprocessed audio will continue playing until you attempt to enable noise reduction again.", f.Spoken.Last);
        Assert.Equal(SpeechSubject.NoiseHelperState, f.Spoken.Said[^1].Subject);
        Assert.Equal(VerbosityLevel.Critical, f.Spoken.Said[^1].Level);
        f.Spoken.Verbosity = VerbosityLevel.Terse;
        f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural, "nnr", false, "gave up") { SlotStopped = true });
        Assert.Equal("Noise reduction start failed, now playing unprocessed audio.", f.Spoken.Last);
        // A success says nothing more: the switch sentence already did.
        int count = f.Spoken.Said.Count;
        f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural, "nnr", true));
        Assert.Equal(count, f.Spoken.Said.Count);
    }

    [Fact]
    public void The_sentence_stays_true_because_a_new_selection_moves_GaveUp_back_to_Idle_and_really_retries()
    {
        // "Unprocessed audio will continue playing until you attempt to enable
        // noise reduction again" is true only while turning an engine on again
        // really starts the helper again. That is WdspHost.Attach moving
        // GaveUp to Idle; this pins it, so the day it changes the sentence's
        // premise fails here rather than in the operator's ear.
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--exit-early", "2" } : Array.Empty<string>(),
            HelperTestRig.Fast with { ConstructionWait = TimeSpan.FromSeconds(5) });
        var first = rig.Engine("nnr");
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.GaveUp, "the helper to give up");
        Assert.False(first.IsAvailable);
        Assert.Single(rig.Host.LaunchedProcessIds);

        var again = rig.Engine("nnr"); // a new selection, with no Retry
        Assert.True(again.IsAvailable, again.UnavailabilityDetail);
        Assert.NotEqual(WdspHelperState.GaveUp, rig.Host.State);
        Assert.Equal(2, rig.Host.LaunchedProcessIds.Count);
    }
}

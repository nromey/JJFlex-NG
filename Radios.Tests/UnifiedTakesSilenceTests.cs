#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round two, D2's silence: a saved playback that mutes another
/// slice, captures, ends and puts the slice back says NOTHING. Measured through
/// the speech transcript, with render off, so nothing can reach the screen
/// reader on the desk, and with a positive control that the transcript would
/// have seen speech.
/// </summary>
[Collection(SpeechOutputStaticsCollection.Name)]
public sealed class UnifiedTakesSilenceTests : IDisposable
{
    private readonly string _path;
    private readonly string _root = UnifiedTakeFixtures.NewRoot();
    private readonly VerbosityLevel _verbosityBefore;

    public UnifiedTakesSilenceTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-takes-silence-tests");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
        ScreenReaderOutput.ResetTransientSpeechStateForTest();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
    }

    public void Dispose()
    {
        OutputChannelRecorder.Configure(render: false, record: false);
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        try { File.Delete(_path); } catch { }
        for (int attempt = 0; ; attempt++)
        {
            try { UnifiedTakeFixtures.Remove(_root); break; }
            catch (IOException) when (attempt < 50) { System.Threading.Thread.Sleep(50); }
        }
    }

    private static void AssertTheDeskIsSafe()
    {
        Assert.False(OutputChannelRecorder.RenderEnabled, "Refusing: render is on, so speech could reach the screen reader");
        string backend = ScreenReaderOutput.BackendName;
        Assert.True(backend == "diverted" || backend == "none",
            "Refusing: the live speech backend is '" + backend + "', which could reach the screen reader");
    }

    private string[] Spoken()
    {
        using var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("event").GetString() == "speech")
            .Select(e => e.GetProperty("text").GetString() ?? "")
            .ToArray();
    }

    [Fact]
    public async Task Saving_on_a_muted_slice_a_capture_an_exit_and_the_slice_restored_says_nothing()
    {
        AssertTheDeskIsSafe();
        // Positive control: the transcript sees speech when there is speech.
        ScreenReaderOutput.Speak("positive control for the take silence test", VerbosityLevel.Critical);
        Assert.Contains("positive control for the take silence test", Spoken());
        int before = Spoken().Length;

        using var c = UnifiedTakesMuteTests.ThreeSlices();
        var run = PlaybackTakeRun.Admit(c.F.Rig, TakeKind.RadioRecording, SliceRecorderTarget.TransmitSlice, new TakeSeriesContext());
        run.NoteKeyed();
        var sink = new UnifiedTakesRunTests.Sink();
        var armed = run.ArmAsync(_root, sink.Start, muteOthers: true);
        Assert.True(await c.Pump(armed));
        Assert.True(c.HeldMute[2]);
        sink.Plays(1);
        Assert.True(await c.Pump(run.Finished));
        Assert.True(await c.Pump(run.Mutes.Restored));
        Assert.False(c.HeldMute[2]);
        await Task.Delay(200);

        var said = Spoken().Skip(before).ToArray();
        Assert.True(said.Length == 0, "spoken: " + string.Join(" | ", said));
    }
}

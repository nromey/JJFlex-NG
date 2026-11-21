using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using JJPortaudio;
using NAudio.Wave;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The audition's window while a take is processed off its thread (#262,
/// Sprint 49 Track Q phase B, items 4 and 9): an old generation's verdict is
/// never shown or said; the current one is; choosing another method does not
/// cancel the render, and the result waits for the method to come back; the
/// play buttons and Keep stay hidden while it processes and Process again
/// stays where the focus is.
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> Every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>, then works on a
/// private STA thread with an uninitialized dialog (its constructor never runs)
/// and unhosted buttons. What the window asks to say is read from the output
/// transcript, never a reader; the take is a real
/// <see cref="ChainAuditionTake"/> in a temporary folder with a fake recorder and
/// a fake processor that blocks until released.
/// </remarks>
public sealed class AuditionAnalysisWindowTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jjflex-audition-window-" + Guid.NewGuid().ToString("N"));
    private readonly string _transcript = Path.Combine(Path.GetTempPath(), "jjflex-audition-window-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        OutputChannelRecorder.Configure(render: false, record: false);
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { File.Delete(_transcript); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void An_old_generations_verdict_is_never_shown_or_said_and_the_current_one_is()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced);
        Assert.True(TestSettingsRoot.Isolated);
        string said = Transcribed(() => Run(() =>
        {
            var dialog = Dialog(AudioCheckListenMethods.Audition);
            var (take, chain) = Take();
            Put(dialog, "_audition", take);
            chain.Release.Set();
            var first = take.AnalyseInBackground(Recorded(take))!.Result;
            var second = take.ProcessAgain()!.Result;          // a newer generation
            var box = (TextBox)Get(dialog, "_micReadingBox")!;
            Invoke(dialog, "ShowAuditionReading", "before");

            Invoke(dialog, "OnAuditionAnalysed", first);           // stale
            Assert.Equal("before", box.Text);

            Invoke(dialog, "OnAuditionAnalysed", second);          // current: positive control
            Assert.Equal(ChainAudition.Verdict(second.Result!), box.Text);
        }));
        // The verdicts of the two analyses are the same words, so count them:
        // only the current one was said.
        Assert.Equal(1, Count(said, "WORDING PLACEHOLDER: speech heard at"));
    }

    [Fact]
    public void A_cancelled_outcome_is_never_shown_or_said()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string said = Transcribed(() => Run(() =>
        {
            var dialog = Dialog(AudioCheckListenMethods.Audition);
            var (take, chain) = Take();
            Put(dialog, "_audition", take);
            var run = take.AnalyseInBackground(Recorded(take))!;
            Assert.True(chain.Entered.Wait(10000));
            take.Discard();
            var outcome = run.Result;
            Assert.True(outcome.Cancelled);
            var box = (TextBox)Get(dialog, "_micReadingBox")!;
            Invoke(dialog, "ShowAuditionReading", "before");
            Invoke(dialog, "OnAuditionAnalysed", outcome);
            Assert.Equal("before", box.Text);
        }));
        Assert.Equal(0, Count(said, "WORDING PLACEHOLDER: speech heard at"));
    }

    [Fact]
    public void Choosing_another_method_does_not_cancel_and_the_verdict_waits_for_the_method_to_come_back()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string said = Transcribed(() => Run(() =>
        {
            var dialog = Dialog(AudioCheckListenMethods.Audition);
            var (take, chain) = Take();
            Put(dialog, "_audition", take);
            var run = take.AnalyseInBackground(Recorded(take))!;
            Assert.True(chain.Entered.Wait(10000));

            Select(dialog, AudioCheckListenMethods.ComputerMic);
            Invoke(dialog, "ApplyAuditionLayout", AudioCheckListenMethods.ComputerMic);
            Assert.True(take.IsAnalysing, "choosing another method cancelled the render");

            chain.Release.Set();
            var outcome = run.Result;
            Assert.False(outcome.Cancelled);
            Invoke(dialog, "OnAuditionAnalysed", outcome);
            // Not said while another method is shown; held for the reading box.
            Select(dialog, AudioCheckListenMethods.Audition);
            Invoke(dialog, "UpdateMicReading");
            Assert.Equal(ChainAudition.Verdict(outcome.Result!), ((TextBox)Get(dialog, "_micReadingBox")!).Text);
        }));
        Assert.Equal(0, Count(said, "WORDING PLACEHOLDER: speech heard at"));
    }

    [Fact]
    public void While_it_processes_the_play_buttons_and_Keep_are_hidden_and_Process_again_stays()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Run(() =>
        {
            var dialog = Dialog(AudioCheckListenMethods.Audition);
            var (take, chain) = Take();
            Put(dialog, "_audition", take);
            chain.Release.Set();
            take.AnalyseInBackground(Recorded(take))!.Wait(10000);
            Invoke(dialog, "SyncAuditionButtons");
            Assert.Equal(new[] { "cleaned", "raw", "removed", "keep", "again" }, Visible(dialog));   // positive control

            chain.Release.Reset();
            chain.Entered.Reset();
            var again = take.ProcessAgain()!;
            Assert.True(chain.Entered.Wait(10000));
            Invoke(dialog, "SyncAuditionButtons");
            Assert.Equal(new[] { "again" }, Visible(dialog));
            chain.Release.Set();
            again.Wait(10000);
            Invoke(dialog, "SyncAuditionButtons");
            Assert.Equal(new[] { "cleaned", "raw", "removed", "keep", "again" }, Visible(dialog));
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static int Count(string text, string what)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
        return n;
    }

    private string Transcribed(Action body)
    {
        var oldVerbosity = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _transcript);
        try { body(); }
        finally
        {
            OutputChannelRecorder.Configure(render: false, record: false);
            ScreenReaderOutput.CurrentVerbosity = oldVerbosity;
        }
        if (!File.Exists(_transcript)) return "";
        using var stream = new FileStream(_transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    private sealed class Blocking : IChainAuditionProcessor
    {
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public ChainProcessing Process(float[] raw, int rate, CancellationToken cancel, Action<double>? progress)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(20), cancel);
            return ChainProcessing.Rendered(raw.Select(s => s * 0.5f).ToArray(), 0, gateEnabled: false, thresholdDb: null);
        }
    }

    private (ChainAuditionTake Take, Blocking Chain) Take()
    {
        var chain = new Blocking();
        return (new ChainAuditionTake(_folder, new FakeRecorder(), chain, ReadMono), chain);
    }

    private static string Recorded(ChainAuditionTake take)
    {
        Assert.True(take.Start(DateTime.Now));
        var recorder = (FakeRecorder)typeof(ChainAuditionTake).GetField("_recorder", Private)!.GetValue(take)!;
        recorder.Stop();
        return recorder.Path;
    }

    private static readonly string[] ButtonFields =
        { "_auditionCleanedButton", "_auditionRawButton", "_auditionRemovedButton", "_auditionKeepButton", "_auditionProcessAgainButton" };
    private static readonly string[] ButtonNames = { "cleaned", "raw", "removed", "keep", "again" };

    private static string[] Visible(CheckYourAudioDialog dialog) =>
        ButtonFields.Select((f, i) => (Button: (Button)Get(dialog, f)!, Name: ButtonNames[i]))
            .Where(b => b.Button.Visibility == Visibility.Visible).Select(b => b.Name).ToArray();

    private static CheckYourAudioDialog Dialog(AudioCheckListenMethods shown)
    {
        var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        var offered = new List<AudioCheckListenMethods>(AudioCheckMethods.Offered(true, false, "", true));
        var control = new CycleFieldControl();
        control.Setup("Method", CheckYourAudioDialog.MethodNames(offered), offered.IndexOf(shown));
        Put(dialog, "_offered", offered);
        Put(dialog, "_listenMethodControl", control);
        Put(dialog, "_micReadingBox", new TextBox());
        Put(dialog, "_computerMicReading", "");
        Put(dialog, "_auditionReading", "");
        var make = typeof(CheckYourAudioDialog).GetMethod("MakeAuditionButton", BindingFlags.Static | BindingFlags.NonPublic)!;
        string[] keys = { "audio.audition.play.cleaned", "audio.audition.play.raw", "audio.audition.play.removed", "audio.audition.keep", "audio.audition.process_again" };
        for (int i = 0; i < ButtonFields.Length; i++)
            Put(dialog, ButtonFields[i], make.Invoke(null, new object[] { keys[i], "audio.audition.keep_help", new Action(() => { }) }));
        return dialog;
    }

    private static void Select(CheckYourAudioDialog dialog, AudioCheckListenMethods method)
    {
        var offered = (List<AudioCheckListenMethods>)Get(dialog, "_offered")!;
        var control = (CycleFieldControl)Get(dialog, "_listenMethodControl")!;
        control.SuppressEvents = true;
        control.SelectedIndex = offered.IndexOf(method);
        control.SuppressEvents = false;
    }

    private static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Put(object target, string field, object? value) => Field(target, field).SetValue(target, value);
    private static object? Get(object target, string field) => Field(target, field).GetValue(target);

    private static FieldInfo Field(object target, string name)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
            if (t.GetField(name, Private) is { } f) return f;
        throw new MissingFieldException(target.GetType().Name, name);
    }

    private static object? Invoke(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method, Private)!.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>Speech-like bursts over quiet noise, eight seconds.</summary>
    private static float[] SyntheticTake() => ChainAuditionTwinTests.Take(8);

    private sealed class FakeRecorder : IAuditionRecorder
    {
        public string Path = "";
        private bool _running;
        public bool IsRunning => _running;
        public bool IsRecordingTo(string path) => _running && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);
        public bool Start(string path, bool holdFileStart) { Path = path; _running = true; return true; }
        public void BeginFileAt(string path, long? stopwatchTimestamp) { }
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            var samples = SyntheticTake();
            using var w = new WavWriter(Path, Rate, 1);
            w.Write(samples, samples.Length);
            w.Finish();
        }
    }

    private static (float[] Mono, int Rate)? ReadMono(string path)
    {
        if (!File.Exists(path)) return null;
        using var reader = new WaveFileReader(path);
        var provider = reader.ToSampleProvider();
        var all = new float[(int)reader.SampleCount];
        int at = 0, got;
        while (at < all.Length && (got = provider.Read(all.AsSpan(at))) > 0) at += got;
        return (all, reader.WaveFormat.SampleRate);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using JJPortaudio;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The audition's buttons in Check your audio (#262, Sprint 49 Track Q phase
/// A): which appear, in what order, and when, and that the reading box carries
/// the audition's own words.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> Every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/> on its own
/// thread, then works on a private STA thread with an uninitialized dialog
/// (its constructor never runs) and unhosted buttons that are never put in a
/// window. The take is a real <see cref="ChainAuditionTake"/> in a temporary
/// folder, recorded by a fake that writes a synthetic take, so no microphone is
/// opened, nothing is played, and the operator's settings folder is never
/// touched.
/// </para>
/// <para>
/// <b>What this cannot see:</b> the tab order as NVDA walks it, what is spoken,
/// and the buttons' effect when pressed. Those are the harness walk's.
/// </para>
/// </remarks>
public sealed class CheckYourAudioAuditionButtonsTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jjflex-audition-ui-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void PhaseAOffersPlayRawAndKeepOnlyWhileTheAuditionIsShown()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Run(() =>
        {
            var dialog = Dialog(AudioCheckListenMethods.Audition);
            var take = ReadyTake(ChainNotConnectedYet.Instance);
            Put(dialog, "_audition", take);
            Invoke(dialog, "SyncAuditionButtons");

            Assert.Equal(new[] { "raw", "keep" }, Visible(dialog));

            // Another method shown: none of them.
            Select(dialog, AudioCheckListenMethods.ComputerMic);
            Invoke(dialog, "SyncAuditionButtons");
            Assert.Empty(Visible(dialog));
        });
    }

    [Fact]
    public void WithAChainTheyComeCleanedFirstThenRawThenWhatWasRemovedThenKeep()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Run(() =>
        {
            var dialog = Dialog(AudioCheckListenMethods.Audition);
            Put(dialog, "_audition", ReadyTake(new HalvingChain()));
            Invoke(dialog, "SyncAuditionButtons");
            Assert.Equal(new[] { "cleaned", "raw", "removed", "keep" }, Visible(dialog));
            // The tab order is the build order, and that is PlayOrder then Keep.
            var order = (Button?[])Invoke(dialog, "AuditionButtonsInOrder")!;
            Assert.Equal(new[] { "cleaned", "raw", "removed", "keep" }, order.Select(Name(dialog)).ToArray());
        });
    }

    [Fact]
    public void NothingIsShownWhileRecordingAndKeepGoesOnceTheTakeIsKept()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Run(() =>
        {
            var dialog = Dialog(AudioCheckListenMethods.Audition);
            var recorder = new FakeRecorder();
            var take = new ChainAuditionTake(_folder, recorder, new HalvingChain(), ReadMono);
            recorder.Saved = p => take.Saved(p, out _);
            Put(dialog, "_audition", take);

            Assert.True(take.Start(DateTime.Now));
            Invoke(dialog, "SyncAuditionButtons");
            Assert.Empty(Visible(dialog));
            Assert.True((bool)Get(dialog, "AuditionRecording", property: true)!);

            take.StopRecording();
            Invoke(dialog, "SyncAuditionButtons");
            Assert.Contains("keep", Visible(dialog));

            Assert.True(take.Keep(Path.Combine(_folder, "kept", "take.wav")));
            Invoke(dialog, "SyncAuditionButtons");
            Assert.Equal(new[] { "cleaned", "raw", "removed" }, Visible(dialog));
        });
    }

    [Fact]
    public void TheReadingBoxCarriesTheAuditionsOwnWordsWhileItIsShown()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Run(() =>
        {
            var dialog = Dialog(AudioCheckListenMethods.Audition);
            var box = (TextBox)Get(dialog, "_micReadingBox")!;
            Invoke(dialog, "UpdateMicReading");
            Assert.Equal(Lexicon.Get("audio.audition.idle"), box.Text);

            Invoke(dialog, "ShowAuditionReading", "a verdict");
            Assert.Equal("a verdict", box.Text);

            // Not while another method is shown: that method's reading is its own.
            Select(dialog, AudioCheckListenMethods.ComputerMic);
            Invoke(dialog, "UpdateMicReading");
            Assert.NotEqual("a verdict", box.Text);
        });
    }

    [Fact]
    public void WithNoRadioTheAuditionsAboutLineIsItsOwnAndNotNoRadioConnected()
    {
        // Track Q2: the audition is offered with no radio (Noel 2026-11-19
        // 12:23: "this auditions on PC noise reduction and transmit, no radio
        // required"), so its line must not say a radio is missing. The dialog
        // here has no radio: its constructor never ran, so _rig is null.
        NoiseReductionPerChannelTests.AssertWindowless();
        Run(() =>
        {
            var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
            var offered = new List<AudioCheckListenMethods>(AudioCheckMethods.Offered(false, false, "no-radio", false));
            var control = new CycleFieldControl();
            control.Setup("Method", CheckYourAudioDialog.MethodNames(offered), offered.IndexOf(AudioCheckListenMethods.Audition));
            Put(dialog, "_offered", offered);
            Put(dialog, "_listenMethodControl", control);
            var status = new TextBox();
            Put(dialog, "_methodStatus", status);
            Put(dialog, "_offAirStatus", "");
            Assert.Null(Get(dialog, "_rig"));

            Invoke(dialog, "UpdateMethodStatus");
            Assert.Equal(AudioCheckMethods.About(AudioCheckListenMethods.Audition), status.Text);
            Assert.NotEqual(Lexicon.Get("audio.no_radio_connected") + ".", status.Text);
        });
    }

    [Fact]
    public void TheMethodIsNamedFromTheLexiconAndCarriesItsOwnHelp()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(Lexicon.Get("audio.check.method.audition"), CheckYourAudioDialog.MethodName(AudioCheckListenMethods.Audition));
        Assert.NotEqual("audio.check.method.audition", CheckYourAudioDialog.MethodName(AudioCheckListenMethods.Audition));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static readonly string[] ButtonFields =
        { "_auditionCleanedButton", "_auditionRawButton", "_auditionRemovedButton", "_auditionKeepButton" };
    private static readonly string[] ButtonNames = { "cleaned", "raw", "removed", "keep" };

    private static Func<Button?, string> Name(CheckYourAudioDialog dialog) => b =>
    {
        for (int i = 0; i < ButtonFields.Length; i++)
            if (ReferenceEquals(Get(dialog, ButtonFields[i]), b)) return ButtonNames[i];
        return "?";
    };

    private static string[] Visible(CheckYourAudioDialog dialog)
    {
        var order = (Button?[])Invoke(dialog, "AuditionButtonsInOrder")!;
        return order.Where(b => b!.Visibility == Visibility.Visible).Select(Name(dialog)).ToArray();
    }

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
        string[] keys = { "audio.audition.play.cleaned", "audio.audition.play.raw", "audio.audition.play.removed", "audio.audition.keep" };
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

    private ChainAuditionTake ReadyTake(IChainAuditionProcessor processor)
    {
        var recorder = new FakeRecorder();
        var take = new ChainAuditionTake(_folder, recorder, processor, ReadMono);
        recorder.Saved = p => take.Saved(p, out _);
        Assert.True(take.Start(DateTime.Now));
        take.StopRecording();
        Assert.Equal(AuditionTakeState.Ready, take.State);
        return take;
    }

    /// <summary>Speech-like bursts over quiet noise, eight seconds.</summary>
    private static float[] SyntheticTake()
    {
        int n = 8 * Rate;
        var rng = new Random(262);
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double s = (rng.NextDouble() * 2 - 1) * 0.0055;
            double phase = (t - 0.5) % 1.5;
            if (t >= 0.5 && phase < 0.6) s += 0.12 * (Math.Sin(2 * Math.PI * 140 * t) + 0.5 * Math.Sin(2 * Math.PI * 280 * t));
            x[i] = (float)s;
        }
        return x;
    }

    private sealed class HalvingChain : IChainAuditionProcessor
    {
        public ChainProcessing Process(float[] raw, int rate) =>
            ChainProcessing.Rendered(raw.Select(s => s * 0.5f).ToArray(), 0, gateEnabled: false, thresholdDb: null);
    }

    private sealed class FakeRecorder : IAuditionRecorder
    {
        private string _path = "";
        private bool _running;
        public Action<string>? Saved;
        public bool IsRunning => _running;
        public bool IsRecordingTo(string path) => _running && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
        public bool Start(string path) { _path = path; _running = true; return true; }
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            var samples = SyntheticTake();
            using (var w = new WavWriter(_path, Rate, 1)) { w.Write(samples, samples.Length); w.Finish(); }
            Saved?.Invoke(_path);
        }
    }

    private static (float[] Mono, int Rate)? ReadMono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int at = 12, rate = Rate;
        while (at + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int size = BitConverter.ToInt32(bytes, at + 4);
            if (id == "fmt ") rate = BitConverter.ToInt32(bytes, at + 12);
            if (id == "data")
            {
                var mono = new float[size / 2];
                for (int i = 0; i < mono.Length; i++) mono[i] = BitConverter.ToInt16(bytes, at + 8 + 2 * i) / 32768f;
                return (mono, rate);
            }
            at += 8 + size;
        }
        return null;
    }

    private static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    private static void Put(object target, string name, object? value) =>
        typeof(CheckYourAudioDialog).GetField(name, Private)!.SetValue(target, value);

    private static object? Get(object target, string name, bool property = false) => property
        ? typeof(CheckYourAudioDialog).GetProperty(name, Private)!.GetValue(target)
        : typeof(CheckYourAudioDialog).GetField(name, Private)!.GetValue(target);

    private static object? Invoke(object target, string name, params object[] args) =>
        typeof(CheckYourAudioDialog).GetMethod(name, Private)!.Invoke(target, args.Length == 0 ? null : args);
}

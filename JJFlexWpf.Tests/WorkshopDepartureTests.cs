using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Radios.Listenback;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The Audio Workshop after Radio, Disconnect: the shell tells it the radio
/// departed, and it stops reaching that radio. Its radio sections go
/// unavailable, its meter poll stops, Mic Gain neither writes nor confirms, and
/// a bench diagnostic take already running finishes exactly as it did before,
/// on the radio it began on. The same shape as Check your audio's
/// (<see cref="CheckWindowCurrentRadioTests"/>).
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> The source tests read files on an MTA thread, asserted.
/// The created-state tests need real WPF controls, so they run on a private STA
/// thread: the Workshop is an uninitialized object whose constructor (a Window)
/// never runs, and its controls are never put in a window, asserted by their
/// having no presentation source. Speech is recorded, never rendered
/// (<c>QuietRun</c>). The running take is an uninitialized
/// <see cref="ListenbackRun"/>: nothing captures, nothing keys.
/// </remarks>
public sealed class WorkshopDepartureTests
{
    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    // ---- created state: the real departure, the real arrow, the real tick ----

    [Fact]
    public void After_a_Disconnect_the_radio_sections_are_unavailable_the_poll_stopped_and_Mic_Gain_confirms_nothing()
    {
        RunOnSta(parts =>
        {
            var (dialog, gain, meterTimer, bench, rig) = parts;

            // Connected, as SetRig leaves it: available, polling, and the arrow
            // on Mic Gain writes and confirms. The positive control.
            Assert.True(gain.MicGain.IsEnabled);
            Assert.True(bench.IsEnabled);
            Assert.True(meterTimer.IsEnabled);
            Arrow(gain, from: 37);
            Assert.Contains(GainConfirmation(38), ScreenReaderOutput.RecentMessages);

            // Radio > Disconnect: CloseTheRadio disposes the radio, then tells the Workshop.
            rig.Dispose();
            Assert.True(dialog.RigDeparting(rig));

            // Straight back to the open Workshop: nothing to reach.
            Assert.False(gain.MicGain.IsEnabled, "Mic Gain is unavailable once its radio has departed");
            Assert.False(bench.IsEnabled, "with no bench work running, the bench section goes with the radio");
            Assert.False(meterTimer.IsEnabled, "the meter poll stops with the radio");
            Assert.Null(LiveRig(dialog));
            Arrow(gain, from: 38);
            Assert.DoesNotContain(GainConfirmation(39), ScreenReaderOutput.RecentMessages);

            // The attachment itself is kept: only the take's finish reads it.
            Assert.Same(rig, Get(dialog, "_rig"));

            // A second notice, or one for a radio this window does not hold, does nothing.
            Assert.False(dialog.RigDeparting(rig));
            using var other = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            Assert.False(dialog.RigDeparting(other));
        });
    }

    [Fact]
    public void A_meter_tick_already_queued_at_the_Disconnect_reads_nothing()
    {
        RunOnSta(parts =>
        {
            var (dialog, _, _, _, rig) = parts;
            var tick = typeof(AudioWorkshopDialog).GetMethod("MeterTimer_Tick", Private)!;

            // Positive control: connected, the tick goes past its radio guard
            // and reaches the category tabs, which this uninitialized window
            // does not have. Reaching them IS the proof the guard let it through.
            var reached = Assert.Throws<System.Reflection.TargetInvocationException>(() => tick.Invoke(dialog, new object?[] { null, EventArgs.Empty }));
            Assert.IsType<NullReferenceException>(reached.InnerException);

            rig.Dispose();
            Assert.True(dialog.RigDeparting(rig));

            // Departed: the same tick stops at the guard and reads nothing.
            tick.Invoke(dialog, new object?[] { null, EventArgs.Empty });
        });
    }

    [Fact]
    public void A_bench_take_running_at_the_Disconnect_keeps_its_section_and_its_radio_until_it_ends()
    {
        RunOnSta(parts =>
        {
            var (dialog, gain, _, bench, rig) = parts;
            var run = (ListenbackRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ListenbackRun));
            Put(dialog, "_benchRun", run);
            Put(dialog, "_benchRunRig", rig);

            rig.Dispose();
            Assert.True(dialog.RigDeparting(rig));

            // The radio controls go; the bench section stays, so the take's
            // Stop button and result box still work.
            Assert.False(gain.MicGain.IsEnabled);
            Assert.True(bench.IsEnabled, "a running take keeps its Stop button and result box");
            // And the take is untouched: still running, still on its radio,
            // which is still the window's attachment.
            Assert.Same(run, Get(dialog, "_benchRun"));
            Assert.Same(rig, Get(dialog, "_benchRunRig"));
            Assert.Same(rig, Get(dialog, "_rig"));

            // The take ends (FinishDiagnosticAsync's finally clears it, then
            // asks for availability again): the section goes with the radio.
            Put(dialog, "_benchRun", null);
            Put(dialog, "_benchRunRig", null);
            Invoke(dialog, "UpdateRadioControlAvailability");
            Assert.False(bench.IsEnabled, "once the take has ended, the bench section goes with the radio");
        });
    }

    [Fact]
    public void The_next_attachment_is_live_whatever_the_departed_radio_was()
    {
        RunOnSta(parts =>
        {
            var (dialog, _, _, _, rig) = parts;
            Assert.True(dialog.RigDeparting(rig));
            Assert.Null(LiveRig(dialog));
            using var next = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            // The mark names the departed radio, not "departed" in general.
            Put(dialog, "_rig", next);
            Assert.Same(next, LiveRig(dialog));
        });
    }

    // ---- the wiring and the sorting, from source ----

    [Fact]
    public void The_shell_tells_the_Workshop_at_every_dispose_after_Check_your_audio()
    {
        AssertWindowless();
        string vb = Source("globals.vb");
        string close = Between(vb, "Friend Sub CloseTheRadio()", "End Sub");
        int dispose = close.IndexOf("RigControl.Dispose()", StringComparison.Ordinal);
        int check = close.IndexOf("JJFlexWpf.Dialogs.CheckYourAudioDialog.NotifyRigDeparting(RigControl)", StringComparison.Ordinal);
        int workshop = close.IndexOf("JJFlexWpf.Dialogs.AudioWorkshopDialog.NotifyRigDeparting(RigControl)", StringComparison.Ordinal);
        int letGo = close.IndexOf("RigControl = Nothing", StringComparison.Ordinal);
        Assert.True(dispose > 0 && check > dispose && workshop > check && letGo > workshop,
            "CloseTheRadio tells the Workshop after the dispose, before it lets go of the radio");
        // Never NotifyRigGone: its SetRig(null) finishes a running bench take at the disconnect.
        Assert.DoesNotContain("AudioWorkshopDialog.NotifyRigGone", close, StringComparison.Ordinal);

        string release = Between(vb, "Private Sub ReleaseRigControl(why As String)", "End Sub");
        dispose = release.IndexOf("old.Dispose()", StringComparison.Ordinal);
        workshop = release.IndexOf("JJFlexWpf.Dialogs.AudioWorkshopDialog.NotifyRigDeparting(old)", StringComparison.Ordinal);
        letGo = release.IndexOf("RigControl = Nothing", StringComparison.Ordinal);
        Assert.True(dispose > 0 && workshop > dispose && letGo > workshop, "ReleaseRigControl tells the Workshop at the dispose");
        Assert.DoesNotContain("AudioWorkshopDialog.NotifyRigGone", release, StringComparison.Ordinal);
    }

    [Fact]
    public void The_departure_ends_nothing_and_every_attachment_clears_it()
    {
        AssertWindowless();
        string src = Source("JJFlexWpf", "Dialogs", "AudioWorkshopDialog.xaml.cs");
        Assert.Contains("_gain = new TransmitGainControls(Dispatcher, () => LiveRig,", src, StringComparison.Ordinal);
        string departing = Between(src, "internal bool RigDeparting(FlexBase rig)", "\n    }");
        foreach (string ends in new[] { "SetRig(", "BenchToolsRigChanging", "FinishDiagnosticAsync", "_benchCancellation", "_benchRun", "_benchWatch", "CloseBenchTools" })
            Assert.DoesNotContain(ends, departing, StringComparison.Ordinal);
        string setRig = Between(src, "public void SetRig(FlexBase? rig)", "BindMeterInventory(rig);");
        int assign = setRig.IndexOf("_rig = rig;", StringComparison.Ordinal);
        int clear = setRig.IndexOf("_departedRig = null;", StringComparison.Ordinal);
        int avail = setRig.IndexOf("UpdateRadioControlAvailability();", StringComparison.Ordinal);
        Assert.True(assign > 0 && clear > assign && avail > clear, "SetRig clears the departure mark before availability");

        // Every way bench work ends asks for availability again, so the section
        // a departed radio's take kept goes away when that work does.
        string bench = Source("JJFlexWpf", "Dialogs", "AudioWorkshopDialog.BenchTools.cs");
        foreach (string method in new[] { "private async Task ToggleDiagnosticAsync()", "private async Task FinishDiagnosticAsync(string reason)",
                     "private async Task PreflightAsync()", "private async Task PlayDetunedAsync(int detune)", "private async Task RestoreBenchAsync()" })
        {
            string body = Between(bench, method, "\n    }\n");
            string last = body.Substring(body.LastIndexOf("finally", StringComparison.Ordinal));
            Assert.Contains("UpdateRadioControlAvailability();", last, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The sorting, held for the whole class rather than the reads checked
    /// today: outside the bench take's own machinery, no Workshop partial reads
    /// the attachment. Every radio write and every read that describes the
    /// radio asks <c>LiveRig</c>, so a control added later cannot quietly
    /// write to a radio that is gone. The bench take's file keeps the
    /// attachment only where the take finishes on the radio it began on.
    /// </summary>
    [Fact]
    public void No_Workshop_partial_reads_the_departed_radio_outside_the_bench_takes_finish()
    {
        AssertWindowless();
        var root = new DirectoryInfo(Path.Combine(SolutionDir(), "JJFlexWpf", "Dialogs"));
        var partials = root.GetFiles("AudioWorkshopDialog.*.cs")
            .Where(f => f.Name != "AudioWorkshopDialog.xaml.cs"
                        && f.Name != "AudioWorkshopDialog.BenchTools.cs"
                        // Sprint 49's file, left alone on purpose: its one read
                        // is the PC cleanup line's text, in a PC-side section.
                        && f.Name != "AudioWorkshopDialog.TxCleanup.cs")
            .ToList();
        Assert.True(partials.Count >= 9, "expected the Workshop's partials; found " + partials.Count);
        // Positive control: the pattern finds the attachment where it must be.
        Assert.Matches(new Regex(@"\b_rig\b"), File.ReadAllText(Path.Combine(root.FullName, "AudioWorkshopDialog.xaml.cs")));
        foreach (var f in partials)
            Assert.DoesNotMatch(new Regex(@"\b_rig\b"), File.ReadAllText(f.FullName));

        // In the bench file, only the take's own finish and its comparisons.
        string bench = File.ReadAllText(Path.Combine(root.FullName, "AudioWorkshopDialog.BenchTools.cs"));
        foreach (Match m in Regex.Matches(bench, @"^.*\b_rig\b.*$", RegexOptions.Multiline))
        {
            string line = m.Value;
            bool allowed = line.Contains("ReferenceEquals(rig, _rig)") || line.Contains("ReferenceEquals(runRig, _rig)")
                || line.Contains("ReferenceEquals(_rig, _departedRig)") || line.Contains("_benchRecoveryReader ?? _rig?.CaptureListenbackRecoveryReader()");
            Assert.True(allowed, "a bench tools line reads the attachment outside the take's finish: " + line.Trim());
        }
    }

    // ---- helpers ----

    private const System.Reflection.BindingFlags Private =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private static FlexBase? LiveRig(AudioWorkshopDialog dialog) =>
        (FlexBase?)typeof(AudioWorkshopDialog).GetProperty("LiveRig", Private)!.GetValue(dialog);

    private static object? Get(object target, string name) =>
        typeof(AudioWorkshopDialog).GetField(name, Private)!.GetValue(target);

    private static void Put(object target, string name, object? value) =>
        typeof(AudioWorkshopDialog).GetField(name, Private)!.SetValue(target, value);

    private static void Invoke(object target, string name) =>
        typeof(AudioWorkshopDialog).GetMethod(name, Private, Type.EmptyTypes)!.Invoke(target, null);

    private static string GainConfirmation(int value) => Lexicon.Get("audio.tx.mic_gain", ("value", value));

    /// <summary>One press of the arrow on Mic Gain: the control's own adjustment path, which raises ValueChanged.</summary>
    private static void Arrow(TransmitGainControls gain, int from)
    {
        gain.MicGain.Value = from;   // the polling path: no event
        typeof(JJFlexWpf.Controls.ValueFieldControl).GetMethod("AdjustValue", Private)!.Invoke(gain.MicGain, new object[] { 1 });
        Assert.Equal(from + 1, gain.MicGain.Value);
    }

    /// <summary>
    /// The Workshop's parts as SetRig leaves them on a connected radio, built on
    /// a private STA thread with no presentation source: a radio section
    /// holding Mic Gain (a real <see cref="TransmitGainControls"/> whose radio is
    /// the window's own <c>LiveRig</c>, as the constructor wires it), the bench
    /// tools' radio section, and the meter poll timer.
    /// </summary>
    private static void RunOnSta(Action<(AudioWorkshopDialog dialog, TransmitGainControls gain,
        System.Windows.Threading.DispatcherTimer meterTimer, System.Windows.Controls.GroupBox bench, FlexBase rig)> body)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            var verbosity = ScreenReaderOutput.CurrentVerbosity;
            TransmitGainControls? gain = null;
            System.Windows.Threading.DispatcherTimer? meterTimer = null;
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
                Lexicon.Load(Lexicon.Partitions);
                ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
                var dialog = (AudioWorkshopDialog)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AudioWorkshopDialog));
                var sections = new DialogSections();
                gain = new TransmitGainControls(System.Windows.Threading.Dispatcher.CurrentDispatcher,
                    () => LiveRig(dialog), () => false, () => null, host: "a test");
                sections.Open(new System.Windows.Controls.StackPanel(), "Levels");
                sections.AddRadio(new System.Windows.Controls.StackPanel(), gain.MicGain);
                var bench = sections.OpenRadio(new System.Windows.Controls.StackPanel(), "Bench tools");
                meterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
                Put(dialog, "_sections", sections);
                Put(dialog, "_gain", gain);
                Put(dialog, "_meterTimer", meterTimer);
                Put(dialog, "_benchSection", bench);
                Put(dialog, "_rig", rig);
                Invoke(dialog, "UpdateRadioControlAvailability");
                meterTimer.Start();
                Assert.Null(System.Windows.PresentationSource.FromVisual(gain.MicGain));
                Assert.Null(System.Windows.PresentationSource.FromVisual(bench));
                body((dialog, gain, meterTimer, bench, rig));
            }
            catch (System.Reflection.TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
            finally
            {
                meterTimer?.Stop();
                gain?.Dispose();
                rig.Dispose();
                ScreenReaderOutput.CurrentVerbosity = verbosity;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    private static string Between(string text, string start, string end)
    {
        int at = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + start);
        int stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        Assert.True(stop > at, "no end after: " + start);
        return text.Substring(at, stop - at);
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { SolutionDir() }.Concat(parts).ToArray())).Replace("\r\n", "\n");
}

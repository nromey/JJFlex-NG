using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Radios.Listenback;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Check your audio after Radio, Disconnect (#1043 F1): the method line and the
/// mic reading say what the power-off path says ("No radio connected."), not
/// the departed radio's method or meter, except that an Off the air take still
/// finishing on that radio keeps its own status line and its measurements.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> The window is an uninitialized object whose constructor
/// (a Window) never runs; its method line, reading box and method field are
/// real controls on a private STA thread, never put in a window, asserted by
/// their having no presentation source. The running take is an uninitialized
/// <see cref="ListenbackRun"/>: nothing captures, nothing keys. Speech is
/// recorded, never rendered (<c>QuietRun</c>). The source pin runs on an MTA
/// thread, asserted.
/// </remarks>
public sealed class DepartedCheckLinesTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string NoRadio => Lexicon.Get("audio.no_radio_connected") + ".";
    private const string MicNoRadio = "Mic audio: no radio connected";

    [Fact]
    public void After_a_Disconnect_the_method_line_and_the_mic_reading_say_no_radio_is_connected()
    {
        Run(w =>
        {
            // A take finished while the radio was live: its result and its
            // measurements are on screen, as today (the positive control).
            Put(w.Dialog, "_offAirStatus", "An earlier take's result.");
            Put(w.Dialog, "_offAirReading", "An earlier take's measurements.");
            Invoke(w.Dialog, "UpdateMethodStatus");
            Invoke(w.Dialog, "UpdateMicReading");
            Assert.Equal("An earlier take's result.", w.Method.Text);
            Assert.StartsWith("Mic audio", w.Reading.Text);
            Assert.NotEqual(MicNoRadio, w.Reading.Text.Split(Environment.NewLine)[0]);

            // Radio > Disconnect: the shell tells the window, which refreshes both lines.
            w.Rig.Dispose();
            Assert.True(w.Dialog.RigDeparting(w.Rig));
            Assert.Equal(NoRadio, w.Method.Text);
            Assert.Equal(MicNoRadio, w.Reading.Text);

            // And another method chosen says the same, never its own description.
            w.Field.SelectedIndex = w.Offered.IndexOf(AudioCheckListenMethods.Monitor);
            Invoke(w.Dialog, "UpdateMethodStatus");
            Assert.Equal(NoRadio, w.Method.Text);
        });
    }

    [Fact]
    public void An_off_air_take_still_finishing_keeps_its_status_line_and_its_measurements()
    {
        Run(w =>
        {
            var run = (ListenbackRun)RuntimeHelpers.GetUninitializedObject(typeof(ListenbackRun));
            Put(w.Dialog, "_iqRun", run);
            Put(w.Dialog, "_iqRunRig", w.Rig);
            Put(w.Dialog, "_offAirStatus", "Recording the take.");
            w.Rig.Dispose();
            Assert.True(w.Dialog.RigDeparting(w.Rig));

            // Still running on the departed radio: its line stays.
            Assert.Equal("Recording the take.", w.Method.Text);

            // It finishes after the departure: its result takes the line and
            // stays there once the run is cleared, as do its measurements.
            Invoke(w.Dialog, "IqStatus", "The take's result.", false, null, false, null);
            Put(w.Dialog, "_iqRun", null);
            Put(w.Dialog, "_iqRunRig", null);
            Put(w.Dialog, "_offAirReading", "This take's measurements.");
            Put(w.Dialog, "_offAirReadingAfterDeparture", true);   // as UpdateOffAirReadingAsync sets it (pinned below)
            Invoke(w.Dialog, "UpdateMethodStatus");
            Invoke(w.Dialog, "UpdateMicReading");
            Assert.Equal("The take's result.", w.Method.Text);
            Assert.Equal(MicNoRadio + Environment.NewLine + "This take's measurements.", w.Reading.Text);
        });
    }

    [Fact]
    public void A_finishing_take_marks_its_lines_where_they_are_written()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string offAir = Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.OffAir.cs");
        string status = Between(offAir, "private void IqStatus(", "UpdateMethodStatus();");
        Assert.Contains("_offAirStatusAfterDeparture = RigHasDeparted;", status, StringComparison.Ordinal);
        string reading = Between(offAir, "private async Task UpdateOffAirReadingAsync(", "UpdateMicReading();");
        Assert.Contains("_offAirReadingAfterDeparture = RigHasDeparted;", reading, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private sealed record Parts(CheckYourAudioDialog Dialog, TextBox Method, TextBox Reading, CycleFieldControl Field,
        List<AudioCheckListenMethods> Offered, FlexBase Rig);

    /// <summary>The window as SetRig leaves it on a connected radio, Off the air chosen.</summary>
    private static void Run(Action<Parts> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            System.Windows.Threading.DispatcherTimer? poll = null;
            try
            {
                Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
                Lexicon.Load(Lexicon.Partitions);
                var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
                var offered = new List<AudioCheckListenMethods>(AudioCheckMethods.DisplayOrder);
                var field = new CycleFieldControl();
                field.Setup("Method", CheckYourAudioDialog.MethodNames(offered), offered.IndexOf(AudioCheckListenMethods.OffAir));
                var method = new TextBox();
                var reading = new TextBox();
                poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
                Put(dialog, "_offered", offered);
                Put(dialog, "_listenMethodControl", field);
                Put(dialog, "_methodStatus", method);
                Put(dialog, "_micReadingBox", reading);
                Put(dialog, "_offAirStatus", "");
                Put(dialog, "_offAirReading", "");
                Put(dialog, "_computerMicReading", "");
                Put(dialog, "_sections", new DialogSections());
                Put(dialog, "_pollTimer", poll);
                Put(dialog, "_rig", rig);
                Assert.Equal(AudioCheckListenMethods.OffAir, offered[field.SelectedIndex]);
                Assert.Null(System.Windows.PresentationSource.FromVisual(method));
                Assert.Null(System.Windows.PresentationSource.FromVisual(reading));
                Assert.Null(System.Windows.PresentationSource.FromVisual(field));
                body(new Parts(dialog, method, reading, field, offered, rig));
            }
            catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
            finally
            {
                poll?.Stop();
                rig.Dispose();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    private static void Put(object target, string name, object? value) =>
        typeof(CheckYourAudioDialog).GetField(name, Private)!.SetValue(target, value);

    private static void Invoke(object target, string name, params object?[] args) =>
        typeof(CheckYourAudioDialog).GetMethod(name, Private)!.Invoke(target, args.Length == 0 ? null : args);

    private static string Between(string text, string start, string end)
    {
        int at = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + start);
        int stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        Assert.True(stop > at, "no end after: " + start);
        return text.Substring(at, stop - at);
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}

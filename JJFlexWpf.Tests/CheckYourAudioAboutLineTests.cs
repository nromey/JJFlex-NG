using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The "About this method" line in Check your audio (#835, fix round
/// 2026-10-28): one short sentence (ruled 21:20), carried in its accessible
/// name so NVDA speaks it on focus (bench step 45), and with no radio the
/// computer-microphone method's sentence rather than "no radio" (ruled 20:34).
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless,</b> the same way as <c>CheckYourAudioCtrlF1Tests</c>: the
/// dialog is an uninitialized object whose constructor never runs, and the only
/// elements built are a <see cref="CycleFieldControl"/> and a
/// <see cref="TextBox"/>, never put in a window. STA because WPF elements
/// require it.
/// </para>
/// <para>
/// <b>What this cannot see:</b> what NVDA actually says. The accessible name is
/// what NVDA speaks on focus for an edit; the bench proves it with NVDA's log.
/// </para>
/// </remarks>
public sealed class CheckYourAudioAboutLineTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void WithNoRadioTheLineShowsTheComputerMicrophoneSentenceAndCarriesItInItsName()
    {
        Run(() =>
        {
            var (dialog, status) = Dialog(new List<AudioCheckListenMethods> { AudioCheckListenMethods.ComputerMic });
            Invoke(dialog, "UpdateMethodStatus");
            string about = AudioCheckMethods.About(AudioCheckListenMethods.ComputerMic);
            Assert.Equal(about, status.Text);
            Assert.Equal(Lexicon.Get("audio.check.method_status_label") + ": " + about, AutomationProperties.GetName(status));
        });
    }

    [Fact]
    public void ARadioMethodWithNoRadioSaysSoInTheLineAndItsName()
    {
        Run(() =>
        {
            var (dialog, status) = Dialog(new List<AudioCheckListenMethods>(AudioCheckMethods.DisplayOrder));
            ((CycleFieldControl)Get(dialog, "_listenMethodControl")!).SelectedIndex = 2; // Monitor
            Invoke(dialog, "UpdateMethodStatus");
            string none = Lexicon.Get("audio.no_radio_connected") + ".";
            Assert.Equal(none, status.Text);
            Assert.EndsWith(": " + none, AutomationProperties.GetName(status));
        });
    }

    [Fact]
    public void TheLineIsTheShortSentenceNotTheDescription()
    {
        Run(() =>
        {
            var (dialog, status) = Dialog(new List<AudioCheckListenMethods> { AudioCheckListenMethods.ComputerMic });
            Invoke(dialog, "UpdateMethodStatus");
            Assert.DoesNotContain(AudioCheckMethods.Description(AudioCheckListenMethods.ComputerMic), status.Text,
                StringComparison.Ordinal);
        });
    }

    private static (CheckYourAudioDialog Dialog, TextBox Status) Dialog(List<AudioCheckListenMethods> offered)
    {
        var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        var control = new CycleFieldControl();
        control.Setup("Method", CheckYourAudioDialog.MethodNames(offered), 0);
        var status = new TextBox();
        Put(dialog, "_offered", offered);
        Put(dialog, "_listenMethodControl", control);
        Put(dialog, "_methodStatus", status);
        Put(dialog, "_offAirStatus", "");
        return (dialog, status);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    private static void Put(object target, string name, object? value) =>
        typeof(CheckYourAudioDialog).GetField(name, Private)!.SetValue(target, value);

    private static object? Get(object target, string name) =>
        typeof(CheckYourAudioDialog).GetField(name, Private)!.GetValue(target);

    private static void Invoke(object target, string name, params object[] args) =>
        typeof(CheckYourAudioDialog).GetMethod(name, Private)!.Invoke(target, args.Length == 0 ? null : args);
}

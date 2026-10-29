using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using JJPortaudio;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Created states around Check your audio's computer-microphone check (#835,
/// Codex round 1 on the 2026-10-28 fix round): a disconnect while it listens,
/// and the Fix tool's one hand-off decision from either window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless,</b> the same way as <c>CheckYourAudioAboutLineTests</c>: the
/// dialog is an uninitialized object whose constructor never runs, and the only
/// elements built are a <see cref="CycleFieldControl"/> and a <see cref="TextBox"/>,
/// never put in a window. The "listening" probe is a real <see cref="MicProbe"/>
/// that was never started, so no audio device is opened; stopping one is the
/// same call the window makes. STA because WPF elements require it.
/// </para>
/// <para>
/// <b>What this cannot see:</b> a real microphone held open, the Fix tool's
/// window, or speech.
/// </para>
/// </remarks>
public sealed class CheckYourAudioComputerMicStateTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ADisconnectWhileTheComputerCheckListensLeavesOnlyThatMethodOfferedAndKeepsListening()
    {
        Run(() =>
        {
            // Connected: every radio method was on offer, and the computer check is listening.
            var dialog = Dialog(new List<AudioCheckListenMethods>(AudioCheckMethods.DisplayOrder));
            var probe = new MicProbe();
            Put(dialog, "_computerMicProbe", probe);

            // The radio goes: SetRig(null) left _rig null and asked for the offer again.
            Invoke(dialog, "RefreshOfferedMethods");

            var offered = (List<AudioCheckListenMethods>)Get(dialog, "_offered")!;
            Assert.Equal(new[] { AudioCheckListenMethods.ComputerMic }, offered);
            var control = (CycleFieldControl)Get(dialog, "_listenMethodControl")!;
            Assert.Equal(1, control.Options.Length);
            Assert.Equal(0, control.SelectedIndex);
            // And it is still listening: a disconnect is not the operator stopping it.
            Assert.Same(probe, Get(dialog, "_computerMicProbe"));
            probe.Dispose();
        });
    }

    [Theory]
    // check busy, take running, Workshop bench take, computer check listening
    [InlineData(false, false, false, false, CheckYourAudioDialog.FixToolHandoff.Open)]
    [InlineData(false, false, false, true, CheckYourAudioDialog.FixToolHandoff.StopComputerMicThenOpen)]
    [InlineData(true, false, false, false, CheckYourAudioDialog.FixToolHandoff.RefuseCheckRunning)]
    [InlineData(false, true, false, false, CheckYourAudioDialog.FixToolHandoff.RefuseCheckRunning)]
    [InlineData(false, false, true, false, CheckYourAudioDialog.FixToolHandoff.RefuseCheckRunning)]
    [InlineData(false, false, true, true, CheckYourAudioDialog.FixToolHandoff.RefuseCheckRunning)]
    public void BothFixToolDoorsShareOneDecision(bool check, bool take, bool bench, bool computerMic,
        object expected)   // object: the enum is internal to JJFlexWpf
    {
        Assert.Equal(expected, (object)CheckYourAudioDialog.DecideFixToolHandoff(check, take, bench, computerMic));
    }

    [Fact]
    public void TheHandOffStopsTheComputerCheckWhicheverWindowAsks()
    {
        Run(() =>
        {
            var dialog = Dialog(new List<AudioCheckListenMethods> { AudioCheckListenMethods.ComputerMic });
            Put(dialog, "_computerMicProbe", new MicProbe());
            var instance = typeof(CheckYourAudioDialog).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            var before = instance.GetValue(null);
            instance.SetValue(null, dialog);
            try
            {
                // The Workshop's door asks the same question as the window's own.
                Assert.True(CheckYourAudioDialog.HandOffToFixTool("the Audio Workshop's Diagnostics page"));
                Assert.Null(Get(dialog, "_computerMicProbe"));
            }
            finally { instance.SetValue(null, before); }
        });
    }

    [Fact]
    public void WithNoCheckWindowOpenTheHandOffSimplyOpens()
    {
        var instance = typeof(CheckYourAudioDialog).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var before = instance.GetValue(null);
        instance.SetValue(null, null);
        try { Assert.True(CheckYourAudioDialog.HandOffToFixTool("a test")); }
        finally { instance.SetValue(null, before); }
    }

    private static CheckYourAudioDialog Dialog(List<AudioCheckListenMethods> offered)
    {
        var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        var control = new CycleFieldControl();
        control.Setup("Method", CheckYourAudioDialog.MethodNames(offered), 0);
        Put(dialog, "_offered", offered);
        Put(dialog, "_listenMethodControl", control);
        Put(dialog, "_methodStatus", new TextBox());
        Put(dialog, "_offAirStatus", "");
        Put(dialog, "_computerMicReading", "");
        Put(dialog, "_computerMicPrivacyWhy", "");
        Put(dialog, "_checkStarts", new AudioCheckStartGate());
        return dialog;
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

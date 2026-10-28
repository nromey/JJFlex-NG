using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Automation;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Ctrl+F1 on Check your audio's method follows the selection (#835, Noel
/// 2026-10-28 00:15): change the method, press Ctrl+F1, hear the new one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> The dialog is an uninitialized object
/// (<c>RuntimeHelpers.GetUninitializedObject</c>, as in
/// <c>RecorderRoundEightTests</c>): its constructor never runs, so no window is
/// built, shown or sourced. The only element constructed is the method's
/// <c>CycleFieldControl</c>, a UserControl that is never put in a window. The
/// thread is STA because WPF elements require it.
/// </para>
/// <para>
/// The answer is read through <see cref="JJFlexHelp.FindExplanation"/>, the
/// same walk the Ctrl+F1 handler speaks from. <b>What this cannot see:</b> the
/// operator's arrow press itself. The cycle's SelectionChanged handler is
/// wired in <c>BuildCheckDialog</c>, which needs the window; it calls
/// <c>ApplyMethodLayout</c>, which this test calls directly, and the silent
/// path (<c>SetMethodSilently</c>, used when the offered methods change) is
/// called as itself. What NVDA speaks is the bench's.
/// </para>
/// </remarks>
public sealed class CheckYourAudioCtrlF1Tests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void CtrlF1OnTheMethodFollowsTheSelection()
    {
        Run(() =>
        {
            var (dialog, control) = Dialog();
            foreach (var method in AudioCheckMethods.DisplayOrder)
            {
                Invoke(dialog, "SetMethodSilently", method);
                Assert.Equal(AudioCheckMethods.ContextHelp(method), JJFlexHelp.FindExplanation(control));
            }
        });
    }

    [Fact]
    public void CtrlF1FollowsAnArrowPressThroughTheMethodLayout()
    {
        Run(() =>
        {
            var (dialog, control) = Dialog();
            var offered = new List<AudioCheckListenMethods>(AudioCheckMethods.DisplayOrder);
            for (int i = offered.Count - 1; i >= 0; i--)
            {
                control.SelectedIndex = i;
                Invoke(dialog, "ApplyMethodLayout");
                Assert.Equal(AudioCheckMethods.ContextHelp(offered[i]), JJFlexHelp.FindExplanation(control));
            }
        });
    }

    [Fact]
    public void TheDescriptionIsNotReadOnFocus()
    {
        // NVDA reads UIA HelpText on every focus change (#91). The method's
        // description must reach Ctrl+F1 only, so the control's HelpText stays
        // the short hint it always carried.
        Run(() =>
        {
            var (dialog, control) = Dialog();
            foreach (var method in AudioCheckMethods.DisplayOrder)
            {
                Invoke(dialog, "SetMethodSilently", method);
                string hint = AutomationProperties.GetHelpText(control) ?? "";
                Assert.DoesNotContain(AudioCheckMethods.Description(method), hint, StringComparison.Ordinal);
                Assert.DoesNotContain(Lexicon.Get("audio.check.method_help"), hint, StringComparison.Ordinal);
                // Positive control: the description IS on the control, on the
                // on-demand channel, so the assertions above are not passing
                // because nothing was set anywhere.
                Assert.Contains(AudioCheckMethods.Description(method), JJFlexHelp.GetText(control) ?? "",
                    StringComparison.Ordinal);
            }
        });
    }

    private static (CheckYourAudioDialog Dialog, CycleFieldControl Control) Dialog()
    {
        var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        var offered = new List<AudioCheckListenMethods>(AudioCheckMethods.DisplayOrder);
        var control = new CycleFieldControl();
        control.Setup("Method", CheckYourAudioDialog.MethodNames(offered), 0);
        Put(dialog, "_offered", offered);
        Put(dialog, "_listenMethodControl", control);
        Put(dialog, "_offAirStatus", "");
        return (dialog, control);
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

    private static void Invoke(object target, string name, params object[] args) =>
        typeof(CheckYourAudioDialog).GetMethod(name, Private)!.Invoke(target, args.Length == 0 ? null : args);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

// LEXICON_SCANNER_EXEMPT: the pin below quotes a source line.

/// <summary>
/// Ctrl+Enter in Check your audio: a press stops what is really playing, and
/// otherwise starts the check. A Record yourself take that had played to its
/// end stayed on the window's list, so the next press "stopped" it, started
/// nothing and said nothing (found reading the code for #859's report of
/// Ctrl+Enter doing nothing on the watts field).
/// </summary>
/// <remarks>
/// Windowless: an MTA thread and an UNINITIALIZED dialog, with an unconnected
/// radio, on which no slice is playing.
/// </remarks>
public sealed class CheckCtrlEnterTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public CheckCtrlEnterTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
    }

    private static CheckYourAudioDialog Dialog(FlexBase rig, HashSet<SliceRecorderTarget> targets)
    {
        var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        typeof(CheckYourAudioDialog).GetField("_rig", Private)!.SetValue(dialog, rig);
        typeof(CheckYourAudioDialog).GetField("_checkPlayTargets", Private)!.SetValue(dialog, targets);
        return dialog;
    }

    private static bool Stop(CheckYourAudioDialog dialog) =>
        (bool)typeof(CheckYourAudioDialog).GetMethod("StopCheckPlayback", Private)!.Invoke(dialog, null)!;

    [Fact]
    public void A_take_that_finished_does_not_swallow_the_next_press()
    {
        var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        try
        {
            var targets = new HashSet<SliceRecorderTarget> { SliceRecorderTarget.Letter('A') };
            Assert.False(Stop(Dialog(rig, targets)));   // false lets Ctrl+Enter go on to start the check
            Assert.Empty(targets);
        }
        finally { try { rig.Dispose(); } catch { } }
    }

    [Fact]
    public void A_take_still_playing_takes_the_first_press_by_design()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"));
        int method = src.IndexOf("private bool StopCheckPlayback()", StringComparison.Ordinal);
        int asks = src.IndexOf("if (!_rig.IsSlicePlaying(target)) continue;", method, StringComparison.Ordinal);
        int stops = src.IndexOf("_rig.StopSlicePlayAsync(target)", asks, StringComparison.Ordinal);
        int counts = src.IndexOf("stopped = true;", stops, StringComparison.Ordinal);
        // In the same loop: the tone's own "stopped" further down is not this.
        Assert.True(asks > method && stops > asks && counts > stops && counts - stops < 120);
    }
}

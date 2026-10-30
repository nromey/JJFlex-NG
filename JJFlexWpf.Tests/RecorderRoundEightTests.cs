using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using Radios;
using System.Windows.Controls;
using Xunit;

namespace JJFlexWpf.Tests;

public sealed class RecorderRoundEightTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void PendingVoxReplyKeepsBothWorkshopArmChecksTickedThroughTheirOwnPoll()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                // Check your audio since #835, which took the two arms with the
                // rest of the Hear Yourself page.
                var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
                Put(dialog, "_rig", rig);
                var reference = new CheckBox { IsChecked = true };
                var tone = new CheckBox { IsChecked = true };
                Put(dialog, "_refArmCheck", reference);
                Put(dialog, "_toneCheck", tone);
                Put(dialog, "_injectionArmPending", true);

                Invoke(dialog, "SyncReferenceUi");
                Invoke(dialog, "SyncToneArmUi");
                Assert.True(reference.IsChecked == true);
                Assert.True(tone.IsChecked == true);

                // Positive control: without the pending state, the same polls
                // notice that neither engine is engaged and clear both boxes.
                Put(dialog, "_injectionArmPending", false);
                Invoke(dialog, "SyncReferenceUi");
                Invoke(dialog, "SyncToneArmUi");
                Assert.False(reference.IsChecked == true);
                Assert.False(tone.IsChecked == true);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The poll thread did not finish.");
        if (failure != null) throw failure;
    }

    [Fact]
    public void UnkeyedReferenceRemainsArmedWhenTheWorkshopPolls()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                rig.TxFilePlayer.Load(new float[48000 * 4], 48000, "reference");
                rig.TxFilePlayer.Start();
                var owner = new VoxPauseOwner(_ => false, (_, _, _) => true);
                var lease = owner.Acquire(FlexBase.VoxSetting.SimpleVox);
                typeof(FlexBase).GetField("_referenceVoxLease", Private)!.SetValue(rig, lease);
                var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
                var reference = new CheckBox { IsChecked = true };
                Put(dialog, "_rig", rig);
                Put(dialog, "_refArmCheck", reference);
                Put(dialog, "_refPassRunning", true);

                var mox = typeof(FlexBase).GetMethod("noteRadioMox", Private)!;
                mox.Invoke(rig, new object[] { true });
                mox.Invoke(rig, new object[] { false });
                Invoke(dialog, "SyncReferenceUi");

                Assert.True(reference.IsChecked == true);
                Assert.True(rig.TxFilePlaying);
                Assert.True(rig.TxReferenceArmed);
                rig.TxFileStop();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The poll thread did not finish.");
        if (failure != null) throw failure;
    }

    [Theory]
    [InlineData("ReferenceAudio", "TxReferenceStartAsync")]
    [InlineData("TestTone", "TxToneStartAsync")]
    public void PendingStateSpansTheRadioVoxReply(string part, string call)
    {
        string source = File.ReadAllText(Path.Combine(SourceRoot(),
            "JJFlexWpf", "Dialogs", "CheckYourAudioDialog." + part + ".cs"));
        Assert.Contains("_injectionArmPending = true;\n        try { started = await rig." + call + "(); }",
            source.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("finally { _injectionArmPending = false; }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructedRecordedFieldHoldsTheLongestPlayLabel()
    {
        var field = new FrequencyDisplay.DisplayField("Recorded", 52, "", "");
        Assert.True(field.Length >= "Play 120 second recording".Length);
        Assert.Equal(20, new FrequencyDisplay.DisplayField("Other", 52, "", "").Length);
    }

    // WorkshopRecorderLabelsNameTheSliceTheyActOn pinned the labels of the
    // Workshop's Record and Play active-slice buttons. Those buttons were
    // removed in #835 (ruled 13:57): the recorder has its own doors, and their
    // one unique job, the Loopback Check's listening slice, belongs to the
    // Loopback method now. Radios.Tests.CheckYourAudioWiringTests pins that
    // nothing else names the listening slice.

    private static void Put(object target, string name, object? value) =>
        typeof(CheckYourAudioDialog).GetField(name, Private)!.SetValue(target, value);

    private static void Invoke(object target, string name) =>
        typeof(CheckYourAudioDialog).GetMethod(name, Private)!.Invoke(target, null);

    private static string SourceRoot()
    {
        for (DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "JJFlexWpf", "Dialogs",
                "CheckYourAudioDialog.ReferenceAudio.cs"))) return d.FullName;
        throw new DirectoryNotFoundException("The recorder source tree was not found.");
    }
}

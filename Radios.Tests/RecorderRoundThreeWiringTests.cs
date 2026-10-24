using System.IO;
using Xunit;

namespace Radios.Tests;

// Wiring checks are not a substitute for pressing the controls under a screen reader.
public sealed class RecorderRoundThreeWiringTests
{
    private static string Source(string path) => IntegrationPassTree.Read(
        Path.Combine(IntegrationPassTree.Root, path));

    [Fact]
    public void Both_Home_modes_expose_the_record_field_and_route_its_press()
    {
        string main = Source("JJFlexWpf/MainWindow.xaml.cs");
        Assert.Equal(2, main.Split("DisplayField(\"Record\"").Length - 1);
        Assert.Contains("FreqOut.Write(\"Record\"", main);
        Assert.Equal(2, main.Split("_freqOutHandlers.AdjustRecord(field, e)").Length - 1);
        Assert.Contains("RecorderControls.ToggleSliceRecordAsync(_window.RigControl, SliceRecorderTarget.ActiveSlice)",
            Source("JJFlexWpf/FreqOutHandlers.cs"));
    }

    [Fact]
    public void The_outer_window_routes_the_Home_pane_ring_from_both_surfaces()
    {
        string main = Source("JJFlexWpf/MainWindow.xaml.cs");
        Assert.Contains("FieldsPanel.FocusNextHomePane", main);
        string fields = Source("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs");
        Assert.Contains("ReturnFocusToFreqOut?.Invoke()", fields);
        Assert.Contains("HomePaneNavigation.Next", fields);
    }

    [Fact]
    public void The_computer_options_are_bound_to_the_installed_engine()
    {
        string fields = Source("JJFlexWpf/Controls/ScreenFieldsPanel.Recorder.cs");
        Assert.Contains("engine.RecordWhatYouHeard =", fields);
        Assert.Contains("engine.Stereo =", fields);
        Assert.Contains("!recorder.IsRecording", fields);
    }

    [Fact]
    public void Both_Audio_Check_replay_doors_use_the_shared_slice_play_gate()
    {
        string check = Source("JJFlexWpf/Dialogs/AudioWorkshopDialog.AudioCheck.cs");
        Assert.Contains("rig.StartSlicePlayAsync(RadioTakeTarget)", check);
        Assert.Contains("rig.StartSlicePlayAsync(_recorderTarget)", check);
        Assert.DoesNotContain("SlicePlayOn = true", check);
    }

    [Fact]
    public void On_air_slice_play_consults_the_cut_hold_before_admission()
    {
        string controller = Source("JJFlexWpf/PttSafetyController.cs");
        Assert.Contains("!RefuseIfHeldAfterCut(\"slice playback\")", controller);
        string main = Source("JJFlexWpf/MainWindow.xaml.cs");
        Assert.Contains("_pttController?.AllowsRecorderPlayback(recorderRig) == true", main);
    }
}

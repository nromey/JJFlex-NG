using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Tier 1 must be silent, and this is the standing assertion that says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Task #233.</b> The suppression itself lives in <see cref="QuietRun"/>. It
/// is a module initializer, which means it either ran before anything else or it
/// did not run at all, and nothing downstream would notice the difference — the
/// dialogs would construct perfectly and the operator would simply hear them.
/// </para>
/// <para>
/// These tests need no dispatcher, no window and no desktop, so they run even
/// when <see cref="DeskGuard"/> refuses the rest of the tier. That is
/// deliberate: the run where the guard refuses is exactly the run somebody is
/// investigating a noise problem in.
/// </para>
/// </remarks>
public sealed class QuietRunTests
{
    [Fact]
    public void ThisRunMakesNoSound()
    {
        Assert.True(QuietRun.Silenced,
            "Tier 1 constructs real application dialogs, and dialog construction " +
            "drives earcons and speech. Rendering is supposed to be off for the " +
            "whole test process. It is not. " + (QuietRun.Failure ?? "No reason was recorded."));

        Assert.False(OutputChannelRecorder.RenderEnabled,
            "OutputChannelRecorder.RenderEnabled is true, so ScreenReaderOutput and " +
            "EarconPlayer will open audio devices and this run can be heard.");
    }

    [Fact]
    public void TheRunReportSaysWhetherItCouldBeHeard()
    {
        // An operator who heard something during a run has to be able to find
        // out afterwards whether the run believed it was silent. A description
        // that says nothing is the same as no description.
        string described = QuietRun.Describe();

        Assert.False(string.IsNullOrWhiteSpace(described));
        Assert.Contains("audio", described, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheReportNamesTheSoundThisCannotSwitchOff()
    {
        // A suppression that overstates its reach is the instrument this whole
        // track exists to remove. MessageBox.Show with any MessageBoxImage
        // sounds through user32 before a line of our code runs, and
        // ModalWatchdog exists precisely because at least one dialog raises one
        // while it is being CONSTRUCTED — which is the one thing this tier does
        // to every dialog in the app. The watchdog closes that box after it has
        // already been heard.
        //
        // So the caveat is stated on the SUCCESS path. An operator who heard a
        // ding during an otherwise correct run deserves a report that accounts
        // for it rather than one that contradicts him.
        Assert.Contains("message-box", QuietRun.Describe(), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT suppressed", QuietRun.UnsuppressedSounds, System.StringComparison.Ordinal);
    }
}

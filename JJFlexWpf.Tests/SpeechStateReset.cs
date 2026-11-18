using System.Reflection;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The one way a JJFlexWpf test drops the speech arbiter's transient state
/// (#1095). Lives beside <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>,
/// at the project root, on purpose: <c>Infrastructure/</c> belongs to the
/// harness lane, and this is a test-hygiene helper, not part of the harness.
/// </summary>
/// <remarks>
/// <para>
/// The arbiter is process-wide, and a value an operator moves (a gain, a
/// volume, a value field) is HELD a few hundred milliseconds so a sweep is said
/// once, at its end. A test that presses an arrow on a value field and returns
/// at once leaves that value waiting, and it is said into whichever test records
/// speech next: "Mic Gain 39" turned up in <c>PlayAfterStopTests</c> (#1058) and
/// then in <c>TuningChordYieldTests</c> (#1139), from two classes that press the
/// arrow on a real Mic Gain field. <see cref="ScreenReaderOutput.Silence"/> does
/// not withdraw a held value; this does.
/// </para>
/// <para>
/// <b>A test that makes speech it does not wait for calls this in its
/// teardown</b>, so the next class starts clean whatever it is. A test that
/// records speech may call it in its setup too. Three classes each had their
/// own reflection copy of this until #1095.
/// </para>
/// <para>
/// The reset is internal to Radios and this project is not among its friends,
/// so it is reached by name, and a rename is a red test that says so rather
/// than a silent no-op.
/// </para>
/// </remarks>
internal static class SpeechStateReset
{
    /// <summary>Drop everything the arbiter is holding or waiting to deliver, unheard.</summary>
    internal static void Now()
    {
        var reset = typeof(ScreenReaderOutput).GetMethod("ResetTransientSpeechStateForTest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(reset != null, "ScreenReaderOutput.ResetTransientSpeechStateForTest is missing");
        reset!.Invoke(null, null);
    }
}

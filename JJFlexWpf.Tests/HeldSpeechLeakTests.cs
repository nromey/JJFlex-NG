using System;
using System.Linq;
using System.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A test that presses the arrow on a real value field leaves nothing behind
/// for the next test that listens (#1139, the class of #1058). The value field
/// says its new value as a HELD sentence, released a few hundred milliseconds
/// later so a sweep is said once; the two classes that press Mic Gain from 38 to
/// 39 returned before it was released, and "Mic Gain 39" was said into
/// whichever class recorded speech next.
/// </summary>
/// <remarks>
/// xUnit does not order classes, so the order is made here, inside one test:
/// the producer's own test runs, then this test listens as the next class would.
/// The producers build their parts on a private STA thread with no presentation
/// source, and this body runs on the test's MTA thread, so no window is made.
/// Each producer drops the held value in its teardown (<see cref="SpeechStateReset"/>),
/// which is the fix this pins; without it, both of these go red.
/// </remarks>
public sealed class HeldSpeechLeakTests
{
    /// <summary>Longer than a held value can wait: the 300 ms settle plus the 1,200 ms ceiling of the anti-clip gap, with room.</summary>
    private const int ListenMs = 2500;

    [Fact]
    public void The_check_window_tests_Mic_Gain_press_is_not_heard_by_the_next_listener() =>
        ProducerThenListener(() =>
        {
            using var producer = new CheckWindowCurrentRadioTests();
            producer.A_Disconnect_then_a_direct_return_finds_the_radio_controls_unavailable_the_poll_stopped_and_no_gain_confirmed();
        });

    [Fact]
    public void The_workshop_tests_Mic_Gain_press_is_not_heard_by_the_next_listener() =>
        ProducerThenListener(() =>
            new WorkshopDepartureTests().After_a_Disconnect_the_radio_sections_are_unavailable_the_poll_stopped_and_Mic_Gain_confirms_nothing());

    private static void ProducerThenListener(Action producer)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Lexicon.Load(Lexicon.Partitions);
        SpeechStateReset.Now();
        var verbosity = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;   // a listener hears everything
        try
        {
            producer();

            // The next class starts here, and listens.
            string marker = "next listener " + Guid.NewGuid().ToString("N");
            ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical);
            Assert.Contains(marker, ScreenReaderOutput.RecentMessages);   // the listener can hear
            Thread.Sleep(ListenMs);

            var heard = ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker).ToList();
            Assert.True(!heard.Any(m => m.StartsWith("Mic Gain", StringComparison.Ordinal)),
                "a producer's held value reached the next listener: " + string.Join(" | ", heard));
        }
        finally
        {
            ScreenReaderOutput.CurrentVerbosity = verbosity;
            SpeechStateReset.Now();
        }
    }

    // ── One reset, not a copy per class (#1095) ─────────────────────────

    /// <summary>Files, other than the shared helper, that reach the reset themselves.</summary>
    internal static string[] OwnCopiesOfTheReset(params (string File, string Text)[] files) =>
        files.Where(f => !f.File.EndsWith("SpeechStateReset.cs", StringComparison.OrdinalIgnoreCase)
                      && !f.File.EndsWith(nameof(HeldSpeechLeakTests) + ".cs", StringComparison.OrdinalIgnoreCase)
                      && f.Text.Contains("\"ResetTransientSpeechStateForTest\"", StringComparison.Ordinal))
             .Select(f => System.IO.Path.GetFileName(f.File)).ToArray();

    [Fact]
    public void Only_the_shared_helper_reaches_the_reset()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string tests = System.IO.Path.Combine(dir!.FullName, "JJFlexWpf.Tests");
        var files = System.IO.Directory.GetFiles(tests, "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.Contains(System.IO.Path.DirectorySeparatorChar + "obj" + System.IO.Path.DirectorySeparatorChar)
                     && !f.Contains(System.IO.Path.DirectorySeparatorChar + "bin" + System.IO.Path.DirectorySeparatorChar))
            .Select(f => (f, System.IO.File.ReadAllText(f))).ToArray();
        Assert.True(files.Length > 50, "the test sources were not found");
        Assert.Empty(OwnCopiesOfTheReset(files));

        // The control: a class with its own copy is caught.
        Assert.Single(OwnCopiesOfTheReset(("Planted.cs",
            "var reset = typeof(ScreenReaderOutput).GetMethod(\"ResetTransientSpeechStateForTest\", flags);")));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// After an update is handed off, the app closes through the shell's own
/// close, and an exit the operator cancels is said (#1204, junk box batch 16).
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread, and a <see cref="MainWindow"/> built as
/// an uninitialized object, as <see cref="TuneKeyCarrierTests"/> builds it.
/// The posted half, <see cref="MainWindow.CloseShellForInstalledUpdate"/>, is
/// called directly; the post itself needs a dispatcher this object does not
/// have, and the source test in Radios.Tests (<c>UpdateCloseTests</c>) pins
/// that the callers post it. The shell close is a stand-in that runs the real
/// <see cref="MainWindow.RequestShutdown"/>, as the shell's FormClosing does.
/// </remarks>
public sealed class UpdateExitCancelledTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private readonly VerbosityLevel _verbosityWas;

    public UpdateExitCancelledTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Lexicon.Load(Lexicon.Partitions);
        _verbosityWas = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
    }

    public void Dispose() => ScreenReaderOutput.CurrentVerbosity = _verbosityWas;

    private static MainWindow Window() =>
        (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));

    private static List<string> Said(Action act)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        act();
        return ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker).Reverse().ToList();
    }

    private static string Cancelled => Lexicon.Get("connect.update.exit_cancelled");

    [Fact]
    public void An_exit_the_operator_cancels_says_the_update_did_not_install()
    {
        var main = Window();
        int closes = 0;
        main.AppExitCallback = () => false;   // the operator answered an exit question by staying
        main.CloseShellCallback = () => { closes++; main.RequestShutdown(); };

        var said = Said(main.CloseShellForInstalledUpdate);

        Assert.Equal(1, closes);
        Assert.Contains(Cancelled, said);
    }

    [Fact]
    public void An_exit_that_goes_ahead_says_nothing_more()
    {
        var main = Window();
        int closes = 0;
        // The exit went ahead: RequestShutdown's own mark, set as it sets it.
        main.CloseShellCallback = () =>
        {
            closes++;
            typeof(MainWindow).GetField("_isClosing", Any)!.SetValue(main, true);
        };

        var said = Said(main.CloseShellForInstalledUpdate);

        Assert.Equal(1, closes);
        Assert.DoesNotContain(Cancelled, said);
    }

    [Fact]
    public void The_cancelled_sentence_is_a_real_entry()
    {
        // Positive control for the two above: the key resolves to text, so
        // "not said" cannot pass merely because the key is missing.
        Assert.False(string.IsNullOrWhiteSpace(Cancelled));
        Assert.NotEqual("connect.update.exit_cancelled", Cancelled);
    }
}

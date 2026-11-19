using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using JJFlexUpdater;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// An in-app update, in the right order (#1204, junk box batch 16, repair 1):
/// download, apply Settings' edits (Settings route), ask the exit questions,
/// and only on go launch the helper or installer and close the app.
/// </summary>
/// <remarks>
/// <para>
/// Windowless, proved: an MTA thread, and a <see cref="MainWindow"/> built as
/// an uninitialized object, as <see cref="TuneKeyCarrierTests"/> builds it.
/// The posted half, <see cref="MainWindow.InstallAndClose"/>, is called
/// directly with a stand-in launch, so no process is ever started. The shell
/// close is a stand-in that runs the real <see cref="MainWindow.RequestShutdown"/>,
/// as the shell's FormClosing does, and the exit is a stand-in for
/// <c>ExitApplication</c>'s question step: it takes the one-shot mark, and
/// asks the questions only when the mark was not set. The VB side's use of
/// the mark is pinned in source by Radios.Tests' <c>UpdateCloseTests</c>.
/// </para>
/// <para>
/// The download is the update dialog's <c>PrepareAsync</c>, which needs a
/// network; that it launches nothing is pinned in source there too. Here a
/// downloaded update is represented by the launch it carries.
/// </para>
/// <para>
/// The class name is from the first build, kept so the merge filter that
/// names it still covers it.
/// </para>
/// </remarks>
public sealed class UpdateExitCancelledTests : IDisposable
{
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

    private static string Stayed => Lexicon.Get("connect.update.exit_cancelled");
    private static string NeedsFixing => Lexicon.Get("connect.update.settings_need_fixing");
    private static string Ready => Lexicon.Get("connect.update.helper_handoff");
    private static string InstallerRunning => Lexicon.Get("connect.update.installer_running");

    /// <summary>
    /// A shell wired as the app wires it: the questions callback, the exit
    /// (which asks the same questions unless the mark says they were
    /// answered), and the shell close through the real RequestShutdown.
    /// Every step is written to <paramref name="log"/>.
    /// </summary>
    private static MainWindow Wired(List<string> log, bool answer)
    {
        var main = Window();
        main.ExitQuestionsCallback = () => { log.Add("ask"); return answer; };
        main.AppExitCallback = () =>
        {
            if (main.TakeExitQuestionsAnswered())
            {
                log.Add("exit, questions already answered");
                return true;
            }
            log.Add("ask");
            return answer;
        };
        main.CloseShellCallback = () => { log.Add("close"); main.RequestShutdown(); };
        return main;
    }

    private static Func<UpdateExecutionResult> Launch(List<string> log,
        UpdateExecutionMode mode = UpdateExecutionMode.HelperHandoff) => () =>
    {
        log.Add("launch");
        return mode == UpdateExecutionMode.HelperHandoff
            ? UpdateExecutionResult.HelperHandoff("staging")
            : UpdateExecutionResult.FullBundle("setup.exe");
    };

    [Fact]
    public void The_order_is_download_apply_ask_launch_close()
    {
        var log = new List<string>();
        var main = Wired(log, answer: true);

        log.Add("download");   // the dialog's PrepareAsync has returned
        bool handedOn = SettingsDialog.HandOffDownloadedUpdate(
            applyPendingEdits: () => { log.Add("apply"); return true; },
            markAndClose: () => { log.Add("settings closed"); main.InstallAndClose(Launch(log)); },
            sayNotInstalled: () => log.Add("said not installed"));

        Assert.True(handedOn);
        Assert.Equal(new[] { "download", "apply", "settings closed", "ask", "launch", "close",
                             "exit, questions already answered" }, log);
    }

    [Fact]
    public void Staying_launches_nothing_and_says_the_update_was_not_installed()
    {
        var log = new List<string>();
        var main = Wired(log, answer: false);

        var said = Said(() => main.InstallAndClose(Launch(log)));

        Assert.Equal(new[] { "ask" }, log);
        Assert.Contains(Stayed, said);
        Assert.DoesNotContain(Ready, said);
    }

    [Fact]
    public void Going_launches_once_asks_once_and_closes_once()
    {
        var log = new List<string>();
        var main = Wired(log, answer: true);

        var said = Said(() => main.InstallAndClose(Launch(log)));

        Assert.Equal(1, log.Count(s => s == "ask"));
        Assert.Equal(1, log.Count(s => s == "launch"));
        Assert.Equal(1, log.Count(s => s == "close"));
        Assert.Contains(Ready, said);
        Assert.DoesNotContain(Stayed, said);
    }

    [Fact]
    public void A_failed_Settings_apply_launches_nothing_and_says_so()
    {
        var log = new List<string>();
        var main = Wired(log, answer: true);

        bool handedOn = SettingsDialog.HandOffDownloadedUpdate(
            applyPendingEdits: () => { log.Add("apply"); return false; },
            markAndClose: () => { log.Add("settings closed"); main.InstallAndClose(Launch(log)); },
            sayNotInstalled: () => log.Add("said not installed"));

        Assert.False(handedOn);
        Assert.Equal(new[] { "apply", "said not installed" }, log);
    }

    [Fact]
    public void The_one_shot_mark_serves_one_exit_and_is_gone_after_it()
    {
        var log = new List<string>();
        var main = Wired(log, answer: true);
        bool secondTake = true;
        var exit = main.AppExitCallback!;
        main.AppExitCallback = () =>
        {
            bool go = exit();
            secondTake = main.TakeExitQuestionsAnswered();   // already consumed
            return go;
        };

        main.InstallAndClose(Launch(log));

        Assert.Contains("exit, questions already answered", log);
        Assert.False(secondTake);
        Assert.False(main.TakeExitQuestionsAnswered());
    }

    [Fact]
    public void The_mark_is_gone_even_when_the_close_never_reaches_the_exit()
    {
        var log = new List<string>();
        var main = Wired(log, answer: true);
        main.CloseShellCallback = () => throw new InvalidOperationException("the shell is gone");

        main.InstallAndClose(Launch(log));

        Assert.Equal(new[] { "ask", "launch" }, log);
        Assert.False(main.TakeExitQuestionsAnswered());
    }

    [Fact]
    public void A_launch_that_fails_closes_nothing_and_says_the_update_failed()
    {
        var log = new List<string>();
        var main = Wired(log, answer: true);

        var said = Said(() => main.InstallAndClose(() =>
        {
            log.Add("launch");
            throw new JJFlexUpdater.Staging.HelperLaunchException("helper.exe", "it would not start");
        }));

        Assert.Equal(new[] { "ask", "launch" }, log);
        Assert.Contains(Lexicon.Get("connect.update.failed_speech", ("message", "it would not start")), said);
        Assert.DoesNotContain(Ready, said);
        Assert.False(main.TakeExitQuestionsAnswered());
    }

    [Fact]
    public void The_installer_route_says_its_own_sentence_after_go()
    {
        var log = new List<string>();
        var main = Wired(log, answer: true);

        var said = Said(() => main.InstallAndClose(Launch(log, UpdateExecutionMode.FullBundleInstaller)));

        Assert.Contains(InstallerRunning, said);
        Assert.DoesNotContain(Ready, said);
    }

    [Fact]
    public void The_sentences_are_real_entries()
    {
        // Positive control for the tests above: each key resolves to text, so
        // "not said" cannot pass merely because a key is missing.
        foreach (var (key, text) in new[] {
                     ("connect.update.exit_cancelled", Stayed),
                     ("connect.update.settings_need_fixing", NeedsFixing),
                     ("connect.update.helper_handoff", Ready),
                     ("connect.update.installer_running", InstallerRunning) })
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(key, text);
        }
    }
}

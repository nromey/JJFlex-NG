using System.Windows.Forms;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #693 repair round (Codex's blocker of 2026-10-25): a JJ key layer waiting
/// in the window does not outlive focus leaving for another program, and an
/// in-window near miss is deliberately not handed to the system-wide hook.
/// Driven through the real <see cref="KeyCommands.DoCommand(Keys, bool)"/>,
/// the real leader switch and the real near-miss lookup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The system-wide decider's near-miss test starts
/// from <c>SetHelpArmed(true, ...)</c>, so it can only see a layer the hook was
/// TOLD about. The path Codex traced starts inside the window: Ctrl+J, K, a
/// move to a logger, then Alt+K there and, later, a key back here. This drives
/// that path from its first key, on O and Alt+O since #835 bound Ctrl+K (Noel,
/// 2026-10-27 20:49): a bare K now names Ctrl+K, the VOX send box, rather than
/// Alt+K. O is still a letter whose only chord is on the Alt tier.
/// </para>
/// <para>
/// <b>Desk safety</b>, as <see cref="ValueLayerRoutingTests"/> establishes it:
/// no window is constructed (<see cref="KeyCommands"/> is not a
/// DependencyObject, the setDefault constructor installs no global handler,
/// the context's main window is null), and H and the slash key are never
/// pressed, because they open a surface. <see cref="QuietRun"/> diverts every
/// earcon and utterance, and the first assertion refuses to go on if it did
/// not take. No rig at all: Alt+O says what is still running, read from the
/// same register the test reads, which is the observable here.
/// </para>
/// </remarks>
public sealed class LeaderFocusLossTests : IDisposable
{
    private readonly VerbosityLevel _verbosityBefore;

    public LeaderFocusLossTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
    }

    public void Dispose() => ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;

    private static KeyCommands NewCommands()
    {
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        return new KeyCommands(new KeyCommandContext(), setDefault: true);
    }

    /// <summary>
    /// Speak a marker first, so a key that said NOTHING cannot pass on the
    /// strength of a sentence an earlier key left in the history.
    /// </summary>
    private static string? SpokenAfter(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        press();
        string? first = ScreenReaderOutput.RecentMessages.FirstOrDefault();
        return first == marker ? null : first;
    }

    private static void NearMissOnO(KeyCommands commands)
    {
        Assert.True(commands.DoCommand(Keys.J | Keys.Control, fromDialog: false));
        Assert.True(commands.LeaderWaitingForSecondKey);
        commands.DoCommand(Keys.O, fromDialog: false);
        Assert.True(commands.LeaderHelpArmed, "Ctrl+J then O is a near miss; the layer must wait for Alt+O");
        Assert.Equal(Keys.O | Keys.Alt, commands.LeaderNearMissChordHonoured);
    }

    /// <summary>What Alt+O says, read from the same register it reads.</summary>
    private static string RunningSentence() => RunningCostRegister.DescribeForSpeech();

    [Fact]
    public void Positive_control_while_still_in_the_window_the_named_chord_runs()
    {
        // Without this, the tests below could pass because Alt+O never
        // reaches its sentence at all, rather than because the layer let go.
        var commands = NewCommands();
        NearMissOnO(commands);

        string? said = SpokenAfter(() => Assert.True(commands.DoCommand(Keys.O | Keys.Alt, fromDialog: false)));
        Assert.Equal(RunningSentence(), said);
        Assert.False(commands.LeaderHelpArmed);
    }

    [Fact]
    public void An_in_window_near_miss_is_not_handed_to_the_system_wide_hook()
    {
        // The deliberate half of the choice: a slip made in this window must
        // not have the hook claim Alt+O (or H, or Escape) from the logger the
        // operator moves to. The promise "from another program too" is made
        // for a near miss reached through the system-wide JJ key, which
        // relays itself.
        var commands = NewCommands();
        Assert.False(SystemWideKeys.HookIsWaiting, "precondition: nothing has armed the hook");
        NearMissOnO(commands);
        Assert.False(SystemWideKeys.HookIsWaiting,
            "an in-window near miss reached the hook, so Alt+O typed in another program would be swallowed");
    }

    [Fact]
    public void After_focus_leaves_for_another_program_the_near_miss_no_longer_claims_its_chord()
    {
        var commands = NewCommands();
        NearMissOnO(commands);

        // The shell's WM_ACTIVATEAPP: another program took the foreground.
        commands.ReleaseOneShotWaits("test: another program took the foreground");

        Assert.False(commands.LeaderHelpArmed);
        Assert.Equal(Keys.None, commands.LeaderNearMissChordHonoured);

        // Back in the window, Alt+O is an ordinary key again: the running
        // report does not come from a layer the operator left minutes ago.
        string? said = SpokenAfter(() => commands.DoCommand(Keys.O | Keys.Alt, fromDialog: false));
        Assert.NotEqual(RunningSentence(), said);
    }

    [Fact]
    public void After_focus_leaves_Escape_back_in_the_window_is_not_a_layer_cancel()
    {
        var commands = NewCommands();
        NearMissOnO(commands);
        commands.ReleaseOneShotWaits("test: another program took the foreground");

        string? said = SpokenAfter(() => commands.DoCommand(Keys.Escape, fromDialog: false));
        Assert.NotEqual(Lexicon.Get("leader.cancelled"), said);
    }

    [Fact]
    public void An_unknown_key_arm_and_a_bare_JJ_key_let_go_too()
    {
        // The class, not the instance: the unknown-key arm (#303) and the bare
        // Ctrl+J wait are the same one-shot state, and had the same weakness.
        var commands = NewCommands();
        Assert.True(commands.DoCommand(Keys.J | Keys.Control, fromDialog: false));
        commands.DoCommand(Keys.Z | Keys.Control, fromDialog: false);
        Assert.True(commands.LeaderHelpArmed, "an unknown key arms the layer for H, slash and Escape");
        Assert.Equal(Keys.None, commands.LeaderNearMissChordHonoured);
        commands.ReleaseOneShotWaits("test");
        Assert.False(commands.LeaderHelpArmed);

        Assert.True(commands.DoCommand(Keys.J | Keys.Control, fromDialog: false));
        Assert.True(commands.LeaderWaitingForSecondKey);
        commands.ReleaseOneShotWaits("test");
        Assert.False(commands.LeaderWaitingForSecondKey);
    }

    [Fact]
    public void Releasing_with_nothing_waiting_changes_nothing()
    {
        var commands = NewCommands();
        commands.ReleaseOneShotWaits("test");
        Assert.False(commands.LeaderWaitingForSecondKey);
        Assert.False(commands.LeaderHelpArmed);
    }
}

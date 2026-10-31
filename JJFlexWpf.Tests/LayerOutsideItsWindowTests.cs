using System.Windows.Forms;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #846: a value layer belongs to the window it opened in. A dialog of ours
/// opening over a layer closes it keeping its changes, so an Escape in the
/// dialog cannot put anything back on the radio. Driven through the real
/// <see cref="KeyCommands"/> and the real audio layer, the way
/// <see cref="ValueLayerRoutingTests"/> does.
/// </summary>
/// <remarks>
/// <b>Desk safety:</b> no window is constructed. A window here is a plain
/// object handed to the window probe and to
/// <see cref="KeyCommands.LetGoOfLayerOutsideItsWindow"/>, and the class runs
/// on a thread that could not build one (<c>AssertWindowless</c>). The wiring
/// of the focus event is pinned from source by
/// <c>Radios.Tests.LayerWindowWiringTests</c>; the press itself goes to the
/// harness.
/// </remarks>
public sealed class LayerOutsideItsWindowTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly VerbosityLevel _verbosityBefore;

    public LayerOutsideItsWindowTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
    }

    public void Dispose()
    {
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _rig.Dispose();
    }

    private KeyCommands CommandsWithAudioLayerOpenIn(object? window)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        var context = new KeyCommandContext { GetRigControl = () => _rig, GetPower = () => true };
        var commands = new KeyCommands(context, setDefault: true) { LayerWindowProbe = () => window };
        commands.EnterAudioLayer(onPan: false);
        Assert.True(commands.LiveValueLayer != null, "the audio layer did not open");
        return commands;
    }

    private static string? SpokenAfter(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        press();
        string? first = ScreenReaderOutput.RecentMessages.FirstOrDefault();
        return first == marker ? null : first;
    }

    [Fact]
    public void A_dialog_taking_the_keyboard_closes_a_layer_left_open_on_Home()
    {
        var commands = CommandsWithAudioLayerOpenIn(window: null);
        var dialog = new object();

        Assert.True(commands.LetGoOfLayerOutsideItsWindow(dialog, windowIsHelpSurface: false, "test"));

        Assert.Null(commands.LiveValueLayer);
    }

    [Fact]
    public void Escape_in_that_dialog_is_not_a_layer_cancel()
    {
        var commands = CommandsWithAudioLayerOpenIn(window: null);
        commands.LetGoOfLayerOutsideItsWindow(new object(), windowIsHelpSurface: false, "test");

        bool taken = commands.DoCommand(Keys.Escape, fromDialog: true);

        Assert.False(taken, "Escape was consumed, so it never reached the dialog");
    }

    [Fact]
    public void The_layer_closes_without_a_sentence_for_the_window_change_to_cut()
    {
        var commands = CommandsWithAudioLayerOpenIn(window: null);

        string? said = SpokenAfter(() =>
            commands.LetGoOfLayerOutsideItsWindow(new object(), windowIsHelpSurface: false, "test"));

        Assert.Null(said);
    }

    [Fact]
    public void Positive_control_a_layer_opened_in_a_dialog_stays_with_that_dialog()
    {
        // The Audio Workshop promise: a layer opened IN a dialog works there.
        var workshop = new object();
        var commands = CommandsWithAudioLayerOpenIn(workshop);

        Assert.False(commands.LetGoOfLayerOutsideItsWindow(workshop, windowIsHelpSurface: false, "test"));

        Assert.NotNull(commands.LiveValueLayer);
    }

    [Fact]
    public void A_second_dialog_over_that_one_closes_it()
    {
        var commands = CommandsWithAudioLayerOpenIn(new object());

        Assert.True(commands.LetGoOfLayerOutsideItsWindow(new object(), windowIsHelpSurface: false, "test"));

        Assert.Null(commands.LiveValueLayer);
    }

    [Fact]
    public void A_key_on_Home_closes_a_layer_whose_dialog_has_gone_and_acts_as_a_Home_key()
    {
        var commands = CommandsWithAudioLayerOpenIn(new object());

        // Z, not Escape: the layer would close itself on Escape either way.
        commands.DoCommand(Keys.Z, fromDialog: false);

        Assert.Null(commands.LiveValueLayer);
    }

    [Fact]
    public void A_key_on_Home_leaves_a_layer_that_opened_on_Home_alone()
    {
        var commands = CommandsWithAudioLayerOpenIn(window: null);

        // Z is no key of this layer, so it is refused and the layer stays.
        commands.DoCommand(Keys.Z, fromDialog: false);

        Assert.NotNull(commands.LiveValueLayer);
    }

    [Fact]
    public void A_help_surface_opened_from_the_layer_leaves_it_open()
    {
        var commands = CommandsWithAudioLayerOpenIn(window: null);

        Assert.False(commands.LetGoOfLayerOutsideItsWindow(new object(), windowIsHelpSurface: true, "test"));

        Assert.NotNull(commands.LiveValueLayer);
    }

    [Fact]
    public void With_no_layer_open_nothing_happens()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var commands = new KeyCommands(new KeyCommandContext(), setDefault: true);

        Assert.False(commands.LetGoOfLayerOutsideItsWindow(new object(), windowIsHelpSurface: false, "test"));
    }
}

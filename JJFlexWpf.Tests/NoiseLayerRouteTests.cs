using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E repair round 1, B2: on Home the bracket keys reached the
/// receive filter before the noise layer was asked, because
/// <c>MainWindow_PreviewKeyDown</c>'s step 2 runs ahead of the registry
/// dispatch where a layer is first asked. Driven here through the REAL
/// <see cref="KeyCommands"/>, the real noise layer over the noise fixture, and
/// the real audio layer, the way <see cref="ValueLayerRoutingTests"/> drives
/// the host.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is proved, and what is not.</b> The main window's handler is an
/// instance method on a control that needs a window, so it cannot run on the
/// live desk. What runs here is everything it now calls: the first-refusal
/// route it asks before moving the filter (<see cref="KeyCommands.OfferKeyToLiveLayerFirst"/>,
/// which the static it calls forwards to), with the real layers behind it.
/// That the handler asks it, and asks it first, is pinned from source. A key
/// pressed on a real build is the harness's.
/// </para>
/// <para>
/// <b>Desk safety.</b> No window: <see cref="KeyCommands"/> is not a
/// DependencyObject and the setDefault constructor installs no global
/// handler; the context's main window is null; the noise controller is the
/// fixture's, over a fake radio and fake engines. Nothing audible:
/// <see cref="QuietRun"/> silences render for the assembly, and every test
/// refuses to go on if that did not take. No radio: the rig is an unconnected
/// <see cref="FlexBase"/>, and no Shift+letter slice jump is pressed.
/// </para>
/// </remarks>
public sealed class NoiseLayerRouteTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly NoiseFixture _noise;
    private readonly VerbosityLevel _verbosityBefore;

    public NoiseLayerRouteTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _noise = new NoiseFixture();
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
    }

    public void Dispose()
    {
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _noise.Dispose();
        _rig.Dispose();
    }

    private KeyCommands Commands()
    {
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        return new KeyCommands(new KeyCommandContext
        {
            GetRigControl = () => _rig,
            GetPower = () => true,
            GetNoise = () => _noise.Noise,
        }, setDefault: true);
    }

    private static readonly Keys[] HomeFilterBrackets =
    {
        Keys.OemOpenBrackets, Keys.OemCloseBrackets,
        Keys.OemOpenBrackets | Keys.Shift, Keys.OemCloseBrackets | Keys.Shift,
        Keys.OemOpenBrackets | Keys.Control, Keys.OemCloseBrackets | Keys.Control,
    };

    [Fact]
    public void Home_offers_a_bracket_to_the_noise_layer_first_and_the_layer_steps_a_favourite_with_it()
    {
        var commands = Commands();
        commands.EnterNoiseLayer();
        Assert.NotNull(commands.LiveValueLayer);
        Assert.Null(_noise.Noise.Current);

        Assert.True(commands.OfferKeyToLiveLayerFirst(Keys.OemCloseBrackets));
        Assert.NotNull(commands.LiveValueLayer);
        string first = Assert.IsType<string>(_noise.Noise.Current);   // the brackets stepped onto an engine
        Assert.True(_noise.Noise.IsOn(first));
        Assert.True(commands.OfferKeyToLiveLayerFirst(Keys.OemCloseBrackets));
        Assert.NotEqual(first, _noise.Noise.Current);
        Assert.True(commands.OfferKeyToLiveLayerFirst(Keys.OemOpenBrackets));
        Assert.Equal(first, _noise.Noise.Current);
        Assert.NotNull(commands.LiveValueLayer);
    }

    [Fact]
    public void A_bracket_form_the_noise_layer_does_not_bind_goes_back_to_Home_untouched()
    {
        var commands = Commands();
        commands.EnterNoiseLayer();
        _noise.Spoken.Clear();
        foreach (var k in new[] { Keys.OemOpenBrackets | Keys.Shift, Keys.OemCloseBrackets | Keys.Control })
        {
            Assert.False(commands.OfferKeyToLiveLayerFirst(k));
            Assert.NotNull(commands.LiveValueLayer);
        }
        Assert.Empty(_noise.Spoken.Said);
        Assert.Null(_noise.Noise.Current);
    }

    [Fact]
    public void A_layer_that_binds_no_bracket_hands_every_filter_key_back_with_the_layer_as_it_was()
    {
        // The audio layer's brackets are the host's filter keys (#778b), and
        // stay so: first refusal returns each one without refusing it, so
        // Home moves the filter with the layer still open, exactly as before.
        var commands = Commands();
        commands.EnterAudioLayer(onPan: false);
        var layer = commands.LiveValueLayer;
        Assert.NotNull(layer);
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        foreach (var k in HomeFilterBrackets)
        {
            Assert.False(commands.OfferKeyToLiveLayerFirst(k));
            Assert.Same(layer, commands.LiveValueLayer);
        }
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
    }

    [Fact]
    public void With_no_layer_open_nothing_is_asked_and_the_brackets_are_Homes()
    {
        var commands = Commands();
        foreach (var k in HomeFilterBrackets) Assert.False(commands.OfferKeyToLiveLayerFirst(k));
        Assert.False(KeyCommands.LiveLayerTakesKeyFirst(Keys.OemCloseBrackets));   // no routing owner here at all
    }

    [Fact]
    public void Homes_step_two_asks_a_live_layer_before_it_moves_the_filter()
    {
        // A source pin, said as one: the handler needs a window.
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        int home = main.IndexOf("private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)", StringComparison.Ordinal);
        int step4 = main.IndexOf("DoCommandHandler(winFormsKey)", home, StringComparison.Ordinal);
        Assert.True(home >= 0 && step4 > home);
        int bracket = main.IndexOf("if (rawKey == Key.OemOpenBrackets || rawKey == Key.OemCloseBrackets)", home, StringComparison.Ordinal);
        int ask = main.IndexOf("KeyCommands.LiveLayerTakesKeyFirst(WpfKeyConverter.ToWinFormsKeys(e))", home, StringComparison.Ordinal);
        int filter = main.IndexOf("_freqOutHandlers.HandleFilterHotkey(e);", home, StringComparison.Ordinal);
        Assert.True(bracket > home && ask > bracket && filter > ask && filter < step4,
            "the bracket branch must ask a live layer before HandleFilterHotkey, and both before the registry dispatch");
        string between = main.Substring(ask, filter - ask);
        Assert.Contains("e.Handled = true;", between);
        Assert.Contains("return;", between);

        string commands = Source("JJFlexWpf", "KeyCommands.cs");
        Assert.Contains("_globalRoutingOwner?.OfferKeyToLiveLayerFirst(k) == true;", commands);
    }

    private static string Source(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path))).Replace("\r\n", "\n");
    }
}

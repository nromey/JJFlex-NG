using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The direct mode keys refuse a mode the radio has not listed and say so,
/// rather than writing it (the 8600 lists no DSB; a DSB written anyway was
/// answered with another mode while the app named the one it asked for).
/// Until this class, the refusal had only a text pin, and disabling it stayed
/// green. Driven through the production entry, <c>MainWindow.SetMode</c>.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> No window is constructed: the MainWindow is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/> (no constructor, no
/// XAML, no HWND), and every test first asserts it runs on an MTA thread, where
/// WPF refuses to build any element. The rig is an unconnected
/// <see cref="FlexBase"/>, so nothing reaches a radio; it has reported no mode
/// list, so the known modes are FlexLib's documented set, and a mode outside
/// that set is the refused case. <see cref="QuietRun"/> has render off, so
/// nothing is audible; the spoken history is read, not heard.
/// </remarks>
public sealed class SetModeRefusalTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly FlexBase _rig;
    private readonly MainWindow _main;

    public SetModeRefusalTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        _main.RigControl = _rig;
        var power = typeof(MainWindow).GetField("_radioPowerOn", Any);
        Assert.True(power != null, "production field MainWindow._radioPowerOn is missing");
        power!.SetValue(_main, true);
    }

    public void Dispose() => _rig.Dispose();

    private static string SpokenAfter(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        press();
        var recent = ScreenReaderOutput.RecentMessages;
        Assert.NotEqual(marker, recent.FirstOrDefault());
        return recent.First();
    }

    [Fact]
    public void A_mode_the_radio_has_not_listed_is_refused_and_said()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.DoesNotContain("RTTY", ModeCycle.KnownModes(_rig.RadioModeList));

        string said = SpokenAfter(() => _main.SetMode("RTTY"));

        Assert.Equal(Lexicon.Get("settings.mode.not_on_radio", ("mode", ModeWords.Spoken("RTTY"))), said);
    }

    [Fact]
    public void Control_a_listed_mode_is_not_refused()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.Contains("CW", ModeCycle.KnownModes(_rig.RadioModeList));

        string said = SpokenAfter(() => _main.SetMode("CW"));

        Assert.NotEqual(Lexicon.Get("settings.mode.not_on_radio", ("mode", ModeWords.Spoken("CW"))), said);
    }
}

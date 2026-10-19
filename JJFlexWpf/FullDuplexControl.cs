using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf;

internal static class FullDuplexControl
{
    internal static async Task ToggleAsync(FlexBase? rig)
    {
        string key = rig == null ? "audio.no_radio_connected" : await rig.ToggleFullDuplexAsync();
        // Menu activation can return before the native menu has relinquished
        // focus. Deliver from the arriving Home context after that transition.
        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new System.Action(() => Speak(key)));
        else Speak(key);
    }
    private static void Speak(string key) => ScreenReaderOutput.Speak(Lexicon.Get(key), VerbosityLevel.Critical, interrupt: true);
}

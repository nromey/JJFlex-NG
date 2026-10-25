using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf;

internal static class FullDuplexControl
{
    internal static async Task ToggleAsync(FlexBase? rig)
    {
        string key;
        if (rig == null) key = "audio.no_radio_connected";
        else
        {
            var request = rig.ToggleFullDuplexAsync();
            if (!request.IsCompleted) Speak("audio.full_duplex.setting");
            key = await request;
        }
        // Menu activation can return before the native menu has relinquished
        // focus. Deliver from the arriving Home context after that transition.
        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new System.Action(() =>
            {
                Speak(key);
                if (key is "audio.full_duplex.on" or "audio.full_duplex.off") rig?.BeginFullDuplexLanCheck();
            }));
        else
        {
            Speak(key);
            if (key is "audio.full_duplex.on" or "audio.full_duplex.off") rig?.BeginFullDuplexLanCheck();
        }
    }
    private static void Speak(string key) => ScreenReaderOutput.Speak(Lexicon.Get(key), VerbosityLevel.Critical, interrupt: true);
}

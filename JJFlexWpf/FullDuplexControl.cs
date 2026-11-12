using System.Threading.Tasks;
using System.Windows.Threading;
using Radios;
using Radios.Speech;

namespace JJFlexWpf;

internal static class FullDuplexControl
{
    internal static async Task ToggleAsync(FlexBase? rig)
    {
        // The caller's thread is the UI thread (the Transmit menu, the JJ key
        // chord, the Home field's button). Its dispatcher is taken HERE, before
        // the await: Application.Current is always null in this WinForms-hosted
        // app, so asking it never waited for anything and the sentence was
        // spoken at once, before the return to Home could cut it. Never creates
        // a dispatcher; null only on a thread with none, and then it is said at once.
        Dispatcher? dispatcher = HostedDispatcher.OfThisThread();
        string key;
        if (rig == null) key = "audio.no_radio_connected";
        else
        {
            var request = rig.ToggleFullDuplexAsync();
            if (!request.IsCompleted) Speak("audio.full_duplex.setting");
            key = await request;
            // Null: the change-nothing hold refused it and has said so. A
            // second sentence here, 2 ms later and interrupting, cut it (#486).
            if (key == null) return;
        }
        // Menu activation can return before the native menu has relinquished
        // focus. Deliver from the arriving Home context after that transition,
        // QUEUED behind whatever Home says on arrival rather than cutting it or
        // being cut by it (the a7f1d31f pattern).
        if (dispatcher != null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new System.Action(() => Deliver(rig, key)));
        else Deliver(rig, key);
    }

    /// <summary>The intent the outcome is spoken with: queued, never cutting the arriving window's own speech.</summary>
    internal const SpeechIntent OutcomeIntent = SpeechIntent.Queue;

    private static void Deliver(FlexBase? rig, string key)
    {
        ScreenReaderOutput.Speak(Lexicon.Get(key), OutcomeIntent, VerbosityLevel.Critical,
            subject: SpeechSubject.FullDuplexOutcome);
        if (key is "audio.full_duplex.on" or "audio.full_duplex.off") rig?.BeginFullDuplexLanCheck();
    }

    private static void Speak(string key) => ScreenReaderOutput.Speak(Lexicon.Get(key), VerbosityLevel.Critical, interrupt: true);
}

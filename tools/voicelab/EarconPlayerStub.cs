namespace JJFlexWpf
{
    /// <summary>
    /// Voicelab stand-in for the app's EarconPlayer: the synthesis source
    /// only needs the mixer sample-rate constant. Keeps the tool free of the
    /// WPF/audio-device machinery the real EarconPlayer drags in.
    /// </summary>
    internal static class EarconPlayer
    {
        internal const int MixerSampleRate = 44100;
    }
}

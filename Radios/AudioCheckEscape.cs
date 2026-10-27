namespace Radios
{
    /// <summary>What one Escape press does in Check your audio.</summary>
    public enum AudioCheckEscapeAction
    {
        /// <summary>Nothing is starting or running: the dialog's ordinary close.</summary>
        Close,

        /// <summary>A check is still asking the radio before it keys: cancel its right to key, and stay.</summary>
        CancelPendingStart,

        /// <summary>A start already cancelled is only rolling back: close.</summary>
        CloseCancelledStart,

        /// <summary>Something is transmitting: unkey, and stay.</summary>
        UnkeyAndStay,

        /// <summary>Nothing is transmitting but the check has not finished: end what remains, and close.</summary>
        EndAndClose,
    }

    /// <summary>
    /// The decision behind Escape in Check your audio (#835): "while a check is
    /// transmitting, the first press unkeys and stays, the second closes;
    /// Escape never leaves you transmitting" (Noel).
    /// </summary>
    public static class AudioCheckEscape
    {
        /// <param name="startPending">A check is starting (the start gate is busy).</param>
        /// <param name="startCancelled">That start has already been cancelled.</param>
        /// <param name="sessionActive">A check session is alive, in any phase.</param>
        /// <param name="stillTransmitting">The PTT controller or the radio says it is transmitting.</param>
        public static AudioCheckEscapeAction Decide(bool startPending, bool startCancelled,
            bool sessionActive, bool stillTransmitting)
        {
            if (startPending)
                return startCancelled ? AudioCheckEscapeAction.CloseCancelledStart : AudioCheckEscapeAction.CancelPendingStart;
            // Escape never leaves you transmitting: while anything still
            // transmits, every press unkeys and stays.
            if (stillTransmitting) return AudioCheckEscapeAction.UnkeyAndStay;
            // Unkeyed, but the check is still in its post-unkey grace, its
            // recorder startup or the Loopback playback: this is the second
            // press, and it closes.
            if (sessionActive) return AudioCheckEscapeAction.EndAndClose;
            return AudioCheckEscapeAction.Close;
        }
    }
}

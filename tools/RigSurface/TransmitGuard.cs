using System;
using System.Globalization;

namespace JJFlex.RigSurface
{
    /// <summary>What a command would do to the transmitter.</summary>
    public enum CommandEffect
    {
        /// <summary>Cannot key the radio.</summary>
        Silent,

        /// <summary>Keys the transmitter. Requires consent and a power ceiling.</summary>
        Keys,

        /// <summary>
        /// Runs the antenna tuner, which both keys the transmitter AND costs
        /// relay operations. Rationed separately and more tightly.
        /// </summary>
        KeysAndWearsRelays,
    }

    /// <summary>
    /// Decides, structurally rather than by substring search, whether a command
    /// would put RF out of the radio.
    ///
    /// <para>Substring matching is not good enough here and the reason is
    /// concrete: <c>slice tune 0 14.250</c> merely retunes a receiver and is
    /// completely safe, while <c>transmit set tune=1</c> keys a carrier. Both
    /// contain "tune". A guard that greps for the word either blocks routine
    /// retuning or waves the carrier through, and the second mistake is the one
    /// that happens next to a hot dummy load.</para>
    /// </summary>
    public static class TransmitGuard
    {
        public static CommandEffect Classify(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return CommandEffect.Silent;

            string[] tokens = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string verb = tokens[0].ToLowerInvariant();
            string sub = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : string.Empty;

            switch (verb)
            {
                // "xmit 1" is the bare key-down. "xmit 0" is the way OUT, and it
                // must never be classified as dangerous.
                //
                // This is not a nicety. The unkey path runs in a finally block
                // and behind a watchdog, and if the guard refused it the radio
                // would stay keyed while the tool reported an error about
                // refusing to key it. A guard that can trap you in transmit is
                // worse than no guard at all.
                case "xmit":
                    return tokens.Length > 1 && !IsTruthy(tokens[1])
                        ? CommandEffect.Silent
                        : CommandEffect.Keys;

                // "atu start" transmits a tuning carrier AND throws relays.
                // "atu bypass" and "atu set memories_enabled=" do neither.
                case "atu":
                    return sub is "start" or "tune"
                        ? CommandEffect.KeysAndWearsRelays
                        : CommandEffect.Silent;

                // CWX sends actual CW. Every form of it keys.
                case "cwx":
                    return CommandEffect.Keys;

                // The DVK plays a recorded voice message into the transmitter.
                case "dvk":
                    return sub is "play" or "send" ? CommandEffect.Keys : CommandEffect.Silent;

                case "transmit":
                    return ClassifyTransmit(tokens);

                default:
                    return CommandEffect.Silent;
            }
        }

        private static CommandEffect ClassifyTransmit(string[] tokens)
        {
            // "transmit tune 1" and "transmit set tune=1" both key a carrier.
            // "transmit set tunepower=10" does not — it only says how hard the
            // carrier would be if someone later asked for one.
            for (int i = 1; i < tokens.Length; i++)
            {
                string token = tokens[i].ToLowerInvariant();

                if (string.Equals(token, "tune", StringComparison.Ordinal)
                    && i + 1 < tokens.Length
                    && IsTruthy(tokens[i + 1]))
                {
                    return CommandEffect.Keys;
                }

                int eq = token.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) continue;

                string key = token[..eq];
                string value = token[(eq + 1)..];

                if (key is "mox" or "tune" && IsTruthy(value)) return CommandEffect.Keys;

                // Setting either of these to zero is an UNKEY, which is always
                // permitted — a guard that blocks the way out of transmit is
                // worse than no guard.
            }

            return CommandEffect.Silent;
        }

        private static bool IsTruthy(string value)
        {
            if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n != 0;
        }
    }

    /// <summary>
    /// Thrown when something asks the wire to key the radio without consent.
    /// </summary>
    public sealed class TransmitRefusedException : InvalidOperationException
    {
        public TransmitRefusedException(string message) : base(message) { }

        public TransmitRefusedException() : base("This command would key the radio and no consent was presented.") { }

        public TransmitRefusedException(string message, Exception innerException) : base(message, innerException) { }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JJFlex.RigSurface
{
    /// <summary>Three states, because "we could not tell" is not "no".</summary>
    public enum TransmitState
    {
        /// <summary>The radio says it is not transmitting.</summary>
        NotTransmitting,

        /// <summary>The radio says it is transmitting, or is about to be.</summary>
        Transmitting,

        /// <summary>
        /// We do not know. No interlock status has arrived, or the value was one
        /// this tool does not recognise.
        /// <para>This is treated exactly like Transmitting by every guard. An
        /// unreadable condition must never be counted as a safe one — the same
        /// principle the chain analyzer is built on, applied to the one question
        /// where being wrong puts RF into a disconnected antenna socket.</para>
        /// </summary>
        Unknown,
    }

    /// <summary>One client connected to the radio.</summary>
    public sealed record ConnectedClient(
        string Handle,
        string? Program,
        string? Station,
        bool IsUs,
        bool HoldsLocalPtt)
    {
        public string Describe()
        {
            string who = string.IsNullOrEmpty(Program) ? "an unnamed program" : Program;
            string where = string.IsNullOrEmpty(Station) ? "" : $" at station {Station}";
            string us = IsUs ? " (this harness)" : "";
            string ptt = HoldsLocalPtt ? ", holding local PTT" : "";
            return $"{Handle}: {who}{where}{us}{ptt}";
        }
    }

    /// <summary>Refusal raised by a guard. Carries a reason fit to read aloud.</summary>
    public sealed class HarnessRefusedException : InvalidOperationException
    {
        public HarnessRefusedException(string message) : base(message) { }

        public HarnessRefusedException() : base("The harness refused to proceed.") { }

        public HarnessRefusedException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// The checks that run before the harness is allowed to touch anything.
    ///
    /// <para>None of these are optional and none of them are one-time. The
    /// transmit check in particular runs before every single assertion, because
    /// state can change underneath a long run: Noel can pick up the microphone,
    /// a VOX threshold can trip, a stuck foot switch can key the radio. The
    /// check costs a dictionary lookup against a model the radio is already
    /// pushing at us. Omitting it costs a transmission into a disconnected
    /// antenna socket.</para>
    /// </summary>
    public static class Guards
    {
        /// <summary>
        /// How long the whole status stream may be silent before a mutation is
        /// preceded by a round trip that proves the link is still there.
        /// </summary>
        public static TimeSpan StaleAfter { get; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Reads whether the radio is transmitting.
        ///
        /// <para><b>There is no MOX status key on the wire.</b> This surprised
        /// us and it is worth stating plainly: the radio never reports a field
        /// called mox. The vendor library synthesises its Mox property purely
        /// from the interlock state, and any observer that waits for a mox key
        /// will wait forever and conclude, silently and permanently, that the
        /// radio is never transmitting.</para>
        /// </summary>
        public static TransmitState ReadTransmitState(RigWire wire)
        {
            ArgumentNullException.ThrowIfNull(wire);

            string? state = wire.State.Get(RigField.Interlock("state"));
            if (string.IsNullOrEmpty(state)) return TransmitState.Unknown;

            return state.ToUpperInvariant() switch
            {
                // Keyed, or committed to keying.
                "TRANSMITTING" or "PTT_REQUESTED" or "UNKEY_REQUESTED" => TransmitState.Transmitting,

                // A stuck PTT input means something is holding the transmitter
                // down whether or not the radio has got round to saying so.
                // Treated as transmitting on purpose.
                "STUCK_INPUT" => TransmitState.Transmitting,

                "RECEIVE" or "READY" or "NOT_READY" => TransmitState.NotTransmitting,

                // A fault or timeout has dropped the transmitter, but the radio
                // is not in a normal receive state either. Not safe to assume.
                "TX_FAULT" or "TIMEOUT" => TransmitState.Unknown,

                _ => TransmitState.Unknown,
            };
        }

        /// <summary>
        /// Which client holds the transmitter, or null if none does. Lets the
        /// harness distinguish "the application is transmitting" from "somebody
        /// else is", which are very different situations.
        /// </summary>
        public static string? TransmittingClientHandle(RigWire wire)
        {
            ArgumentNullException.ThrowIfNull(wire);
            string? handle = wire.State.Get(RigField.Interlock("tx_client_handle"));
            return string.IsNullOrEmpty(handle) || handle is "0x00000000" or "0"
                ? null
                : RigWire.NormaliseHandle(handle);
        }

        /// <summary>
        /// Refuses unless the radio is definitely not transmitting. Call this
        /// before every assertion and before every write.
        /// </summary>
        public static void RequireNotTransmitting(RigWire wire)
        {
            ArgumentNullException.ThrowIfNull(wire);

            // If the radio has gone quiet, prove the link before trusting a
            // cached answer. A dead socket looks exactly like a calm radio.
            if (DateTimeOffset.UtcNow - wire.State.LastStatusAt > StaleAfter)
            {
                try
                {
                    wire.Send("version", TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException ex)
                {
                    throw new HarnessRefusedException(
                        "Refusing to touch the radio: the command channel has stopped answering, " +
                        "so nothing this tool believes about the radio's state can be trusted.", ex);
                }
            }

            TransmitState state = ReadTransmitState(wire);
            if (state == TransmitState.NotTransmitting) return;

            string? who = TransmittingClientHandle(wire);
            string whose = who is null ? "" : $" Transmitter held by client {who}.";
            string reported = wire.State.Get(RigField.Interlock("state")) ?? "nothing at all";

            throw new HarnessRefusedException(
                state == TransmitState.Transmitting
                    ? $"Refusing to proceed: the radio is transmitting. Interlock reports {reported}.{whose}"
                    : $"Refusing to proceed: cannot tell whether the radio is transmitting. " +
                      $"Interlock reports {reported}. An unreadable answer is treated as a transmitting one.");
        }

        /// <summary>Every client the radio currently reports as connected.</summary>
        public static IReadOnlyList<ConnectedClient> Census(RigWire wire)
        {
            ArgumentNullException.ThrowIfNull(wire);

            var clients = new List<ConnectedClient>();
            foreach (RigObject obj in wire.State.GetObjects(RigTarget.Client))
            {
                if (obj.Fields.TryGetValue("connected", out string? connected)
                    && string.Equals(connected, "0", StringComparison.Ordinal))
                {
                    continue;
                }

                string handle = RigWire.NormaliseHandle(obj.OwnerHandle ?? string.Empty);
                obj.Fields.TryGetValue("program", out string? program);
                obj.Fields.TryGetValue("station", out string? station);
                obj.Fields.TryGetValue("local_ptt", out string? ptt);

                clients.Add(new ConnectedClient(
                    handle,
                    program,
                    station,
                    IsUs: string.Equals(handle, wire.ClientHandle, StringComparison.OrdinalIgnoreCase),
                    HoldsLocalPtt: string.Equals(ptt, "1", StringComparison.Ordinal)));
            }
            return clients;
        }

        /// <summary>Clients that are not us.</summary>
        public static IReadOnlyList<ConnectedClient> OtherOperators(RigWire wire)
            => Census(wire).Where(c => !c.IsUs).ToList();

        /// <summary>
        /// Refuses if anyone else is connected.
        ///
        /// <para>This is the guard for the STANDALONE exercising mode, where the
        /// harness changes station state on its own connection. Transmit is a
        /// genuine mutual exclusion on a Flex, but the rest of the surface is
        /// merely shared — and reconfiguring another operator's radio while they
        /// work is not made acceptable by the radio permitting it.</para>
        ///
        /// <para>It is deliberately NOT the guard for observe mode. In the
        /// composed arrangement the application's client being connected is the
        /// entire point, and there this method must not be called.</para>
        /// </summary>
        public static void RequireSoleOperator(RigWire wire)
        {
            IReadOnlyList<ConnectedClient> others = OtherOperators(wire);
            if (others.Count == 0) return;

            string list = string.Join("; ", others.Select(c => c.Describe()));
            throw new HarnessRefusedException(
                $"Refusing to run the exercising harness: {others.Count} other client(s) are connected — {list}. " +
                "This mode changes station state, and doing that under somebody else's hands is not acceptable. " +
                "Disconnect them, or use observe mode, which changes nothing.");
        }

        /// <summary>
        /// Finds the application's client, for the composed mode. Matches on the
        /// program name the client registered with.
        /// </summary>
        public static ConnectedClient? FindClientByProgram(RigWire wire, string programContains)
        {
            ArgumentNullException.ThrowIfNull(programContains);
            return Census(wire).FirstOrDefault(c =>
                !c.IsUs
                && c.Program is not null
                && c.Program.Contains(programContains, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Is this object ours to write? Station-global objects have no owner and
        /// the answer turns on the mode; client-owned objects belong to whoever
        /// their client handle names.
        /// </summary>
        public static bool IsOurs(RigWire wire, RigObject obj)
        {
            ArgumentNullException.ThrowIfNull(wire);
            ArgumentNullException.ThrowIfNull(obj);

            if (obj.OwnerHandle is null) return true; // station-global, no owner
            return string.Equals(
                RigWire.NormaliseHandle(obj.OwnerHandle),
                wire.ClientHandle,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A plain-language census, for the top of a run report. Bullets, not a
        /// table — everything this project prints gets read aloud.
        /// </summary>
        public static string DescribeCensus(RigWire wire)
        {
            IReadOnlyList<ConnectedClient> clients = Census(wire);
            if (clients.Count == 0)
            {
                return "The radio reports no connected clients, which should be impossible while we are talking to it. "
                     + "Treat every client-owned reading below as unattributed.";
            }

            var lines = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture, $"{clients.Count} client(s) connected:"),
            };
            lines.AddRange(clients.Select(c => "  - " + c.Describe()));
            return string.Join(Environment.NewLine, lines);
        }
    }
}

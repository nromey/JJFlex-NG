#define CWMonitor
using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the RequireOperatorPresence primitive.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        #region Sprint 28 Phase 6 — RequireOperatorPresence primitive

        /// <summary>
        /// Strictness level for the <see cref="RequireOperatorPresence"/> authorization
        /// primitive. Callers pick the level appropriate to their operation's blast
        /// radius — port-forward changes use Passive, firmware-class operations use
        /// ActiveChallenge.
        /// </summary>
        public enum PresenceLevel
        {
            /// <summary>
            /// Check <see cref="Flex.Smoothlake.FlexLib.GUIClient.IsLocalPtt"/> on the
            /// current client. Passes when this client is the radio's primary operator
            /// right now (the client the radio routes physical mic/key PTT events to).
            /// Remote SmartLink clients that haven't been assigned IsLocalPtt fail this
            /// check, which is the intended gate for operations like SmartLink port
            /// config that change radio-persistent state.
            /// </summary>
            Passive,

            /// <summary>
            /// Deliberately stubbed. First caller will be firmware upload work (see
            /// memory <c>project_8600_unbox_firmware_trigger.md</c>) — the PTT-listen-
            /// with-timeout logic is easier to get right when a real caller drives the
            /// design. Using this level before implementation lands throws
            /// NotImplementedException intentionally.
            /// </summary>
            ActiveChallenge
        }

        /// <summary>
        /// Sprint 28 Phase 6 — authorization gate for destructive operations that
        /// change radio-persistent state. Use from call sites that want to prevent
        /// non-primary-operator clients from committing changes (port-forward config,
        /// future firmware upload, future factory reset, etc.).
        ///
        /// When the check passes, <paramref name="onConfirmed"/> is invoked. When it
        /// fails, <paramref name="onDenied"/> is invoked (if supplied) AND the denial
        /// is announced via <see cref="Radios.ScreenReaderOutput"/> so the user hears
        /// why their action didn't go through.
        ///
        /// Threat-proportional authorization: pick the <paramref name="level"/> that
        /// matches the consequence of your operation. See <see cref="PresenceLevel"/>
        /// for per-level semantics. Callers self-document their strictness choice at
        /// the call site rather than burying it in an internal lookup table.
        /// </summary>
        /// <param name="level">How strict the presence check should be.</param>
        /// <param name="reason">Short phrase describing what the user is trying to do.
        /// Embedded in the denial announcement (e.g., "change SmartLink port settings"
        /// -> "Cannot change SmartLink port settings &mdash; you must be the primary
        /// operator at the radio").</param>
        /// <param name="onConfirmed">Invoked synchronously when the check passes.</param>
        /// <param name="onDenied">Optional callback invoked after the denial speech.
        /// Leave null if the call site has nothing to do on denial beyond the
        /// announcement.</param>
        public void RequireOperatorPresence(PresenceLevel level, string reason, Action onConfirmed, Action onDenied = null)
        {
            switch (level)
            {
                case PresenceLevel.Passive:
                    if (IsCurrentClientLocalPtt())
                    {
                        onConfirmed?.Invoke();
                    }
                    else
                    {
                        string msg = Lexicon.Get("connect.presence.denied", ("reason", reason));
                        Radios.ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical, interrupt: true);
                        Tracing.TraceLine($"RequireOperatorPresence denied (Passive): {reason}", TraceLevel.Info);
                        onDenied?.Invoke();
                    }
                    break;

                case PresenceLevel.ActiveChallenge:
                    throw new NotImplementedException(
                        "PresenceLevel.ActiveChallenge is reserved for firmware-class " +
                        "operations. Implementation lands alongside firmware upload work " +
                        "(see memory project_8600_unbox_firmware_trigger.md). Do not use " +
                        "before that feature ships — it will not behave correctly.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level,
                        "Unknown PresenceLevel value.");
            }
        }

        /// <summary>
        /// Authorization gate for SmartLink port-settings changes (Noel's
        /// 2026-08-06 ownership decision). Two independent ways to pass:
        /// presence (primary operator at the radio, same as before), or the
        /// owner-declared per-radio waiver <see cref="RadioConfig.AllowRemotePortChanges"/>.
        /// The trust model behind the waiver: a valid SmartLink token for the
        /// radio's account IS the owner's grant — JJ Flex cannot distinguish
        /// the owner from someone the owner handed credentials to, and
        /// pretending otherwise just locks remote-base owners out of their own
        /// rigs. The waiver is per-radio, default off, and set by whoever runs
        /// this copy of JJ Flex. Firmware-class operations do NOT use this
        /// gate — they get PresenceLevel.ActiveChallenge when it ships, which
        /// must honor <see cref="RadioConfig.AllowRemoteFirmwareUpdates"/>.
        /// </summary>
        public void RequirePortSettingsAuthority(string reason, Action onConfirmed, Action onDenied = null)
        {
            if (IsCurrentClientLocalPtt())
            {
                Tracing.TraceLine($"RequirePortSettingsAuthority: presence pass ({reason})", TraceLevel.Info);
                onConfirmed?.Invoke();
                return;
            }

            string serial = theRadio?.Serial ?? string.Empty;
            if (serial.Length > 0 && RadioConfig.LoadForRadio(serial).AllowRemotePortChanges)
            {
                Tracing.TraceLine(
                    $"RequirePortSettingsAuthority: remote waiver pass for {serial} ({reason})",
                    TraceLevel.Info);
                onConfirmed?.Invoke();
                return;
            }

            string msg = Lexicon.Get("connect.presence.port_denied", ("reason", reason));
            Radios.ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical, interrupt: true);
            Tracing.TraceLine(
                $"RequirePortSettingsAuthority denied: {reason} serial={serial}", TraceLevel.Info);
            onDenied?.Invoke();
        }

        /// <summary>
        /// Sprint 28 Phase 6 — returns true when this connection's GUI client holds
        /// <see cref="Flex.Smoothlake.FlexLib.GUIClient.IsLocalPtt"/>. That flag is
        /// the radio's own attestation of "this is the primary operator client,"
        /// unfakable by remote SmartLink sessions.
        /// </summary>
        public bool IsCurrentClientLocalPtt()
        {
            try
            {
                var client = TheGuiClient;
                if (client == null) return false;

                // A record with no client_id that FlexLib did not stamp as
                // ours was rebuilt from a discovery packet, which carries no
                // local-PTT fact — its false is fabricated. Answer from the
                // last AUTHORITATIVE observation instead, so the presence
                // gate keeps telling the truth through the radio's
                // remove/re-add dance (symptom 5: the gate denied a local
                // operator because it read the impostor record).
                if (string.IsNullOrEmpty(client.ClientID) && !client.IsThisClient
                    && _lastAuthoritativeLocalPtt.HasValue)
                {
                    Tracing.TraceLine(
                        $"IsCurrentClientLocalPtt: record for handle {client.ClientHandle} carries no identity; answering from last authoritative observation ({_lastAuthoritativeLocalPtt.Value})",
                        TraceLevel.Info);
                    return _lastAuthoritativeLocalPtt.Value;
                }

                return client.IsLocalPtt;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"IsCurrentClientLocalPtt failed: {ex.Message}", TraceLevel.Warning);
                return false;
            }
        }

        #endregion
    }
}

using System;
using System.Globalization;
using System.Text.Json;

namespace Radios.Fixer
{
    /// <summary>
    /// One message from the page to the host, parsed and validated — or
    /// refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page is the newest, largest and most-changed surface in the Fixer
    /// Tool, and it only ever opens when something is already wrong. So the
    /// host parses what it sends rather than trusting it: an unknown message
    /// kind, a missing field, or something absurdly large is REFUSED and
    /// traced, never guessed at and never silently dropped.
    /// </para>
    /// <para>
    /// <b>The important property is what this type CANNOT hold.</b> There is no
    /// field for "the load is declared", none for "this stage transmits", none
    /// for "the rig is idle". Those are safety facts, and safety facts live in
    /// <see cref="FixerTransmitGate"/> or are read from the radio. A page that
    /// starts sending them will find the host has nowhere to put them — which
    /// is a stronger guarantee than a comment asking nobody to trust them,
    /// because it survives someone refactoring in good faith.
    /// </para>
    /// <para>
    /// The same trick as <c>TxToneLadder.ModePlan</c>, which carries no filter
    /// information so that a caller cannot read the cuts of the mode it is
    /// about to leave.
    /// </para>
    /// </remarks>
    public readonly struct FixerPageMessage
    {
        /// <summary>
        /// The largest raw message the host will look at. A legitimate message
        /// is a few dozen bytes; anything approaching this is a page fault, not
        /// an operator.
        /// </summary>
        public const int MaxRawBytes = 8192;

        /// <summary>
        /// The longest load declaration kept. Operators write "50 ohm dummy
        /// load on ANT1", not an essay. Truncated rather than refused, because
        /// refusing an over-long answer would block the one thing that has to
        /// be answered before anything can transmit.
        /// </summary>
        public const int MaxDeclarationChars = 200;

        /// <summary>What the page is asking for.</summary>
        public enum Kind
        {
            /// <summary>Nothing usable. See <see cref="Problem"/>.</summary>
            Unusable = 0,
            /// <summary>The page has rendered and is ready to be driven.</summary>
            Ready,
            /// <summary>The operator said what the antenna socket is connected to.</summary>
            DeclareLoad,
            /// <summary>Run a stage.</summary>
            RunStage,
            /// <summary>Run a stage again — deliberate, and distinct from RunStage.</summary>
            RunStageAgain,
            /// <summary>Skip a stage, giving a reason.</summary>
            SkipStage,
            /// <summary>Apply one of the fixes the page offered.</summary>
            ApplyFix,
            /// <summary>Stop — Escape, or the Stop control.</summary>
            Stop,
            /// <summary>Put the report on the clipboard as plain text.</summary>
            CopyReport,
            /// <summary>Open a help topic.</summary>
            OpenHelp,
            /// <summary>Open the audio device picker, which the host owns.</summary>
            OpenDevicePicker,
        }

        /// <summary>Why a message was unusable. Traced, never shown raw to the operator.</summary>
        public enum Fault
        {
            None = 0,
            /// <summary>Not a string, or empty.</summary>
            Empty,
            /// <summary>Bigger than <see cref="MaxRawBytes"/>.</summary>
            TooLarge,
            /// <summary>Not JSON, or not a JSON object.</summary>
            NotJson,
            /// <summary>No "kind", or one the host does not know.</summary>
            UnknownKind,
            /// <summary>A field this kind requires was missing or blank.</summary>
            MissingField,
        }

        public Kind What { get; }
        public Fault Problem { get; }

        /// <summary>The run the page believes it is in. Checked, never trusted.</summary>
        public string RunId { get; }

        /// <summary>Which stage, for the kinds that name one.</summary>
        public string StageId { get; }

        /// <summary>
        /// Free text: the load declaration, the skip choice id, the fix id, or
        /// the help topic, depending on <see cref="What"/>. One field rather
        /// than four, because every one of them is a short opaque string the
        /// host hands straight on to something that knows what it means.
        /// </summary>
        public string Value { get; }

        /// <summary>True when this message can be acted on.</summary>
        public bool Usable => What != Kind.Unusable;

        private FixerPageMessage(Kind what, Fault problem, string runId,
                                 string stageId, string value)
        {
            What = what;
            Problem = problem;
            RunId = runId ?? "";
            StageId = stageId ?? "";
            Value = value ?? "";
        }

        private static FixerPageMessage Bad(Fault why)
            => new FixerPageMessage(Kind.Unusable, why, "", "", "");

        /// <summary>
        /// Parse a raw message from the page. Never throws: a surface whose job
        /// is to diagnose a broken radio must not itself fall over on a
        /// malformed string.
        /// </summary>
        public static FixerPageMessage Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Bad(Fault.Empty);
            if (raw.Length > MaxRawBytes) return Bad(Fault.TooLarge);

            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch (JsonException) { return Bad(Fault.NotJson); }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Bad(Fault.NotJson);

                string kind = Text(root, "kind");
                if (kind.Length == 0) return Bad(Fault.UnknownKind);

                string run = Text(root, "run");
                string stage = Text(root, "stage");

                switch (kind)
                {
                    case "ready":
                        return new FixerPageMessage(Kind.Ready, Fault.None, run, "", "");

                    case "declare-load":
                    {
                        string what = Text(root, "what");
                        if (what.Length == 0) return Bad(Fault.MissingField);
                        if (what.Length > MaxDeclarationChars)
                            what = what.Substring(0, MaxDeclarationChars);
                        return new FixerPageMessage(Kind.DeclareLoad, Fault.None, run, "", what);
                    }

                    case "run-stage":
                    {
                        if (stage.Length == 0) return Bad(Fault.MissingField);
                        // "again" is the operator's deliberate repeat, and it
                        // becomes a DIFFERENT kind rather than a flag — so a
                        // handler cannot treat the two as one by forgetting to
                        // read a boolean.
                        bool again = Flag(root, "again");
                        return new FixerPageMessage(
                            again ? Kind.RunStageAgain : Kind.RunStage,
                            Fault.None, run, stage, "");
                    }

                    case "skip-stage":
                    {
                        string choice = Text(root, "choice");
                        if (stage.Length == 0 || choice.Length == 0) return Bad(Fault.MissingField);
                        return new FixerPageMessage(Kind.SkipStage, Fault.None, run, stage, choice);
                    }

                    case "apply-fix":
                    {
                        string fix = Text(root, "fix");
                        if (fix.Length == 0) return Bad(Fault.MissingField);
                        return new FixerPageMessage(Kind.ApplyFix, Fault.None, run, stage, fix);
                    }

                    case "stop":
                    {
                        // The source is recorded but never gates anything: every
                        // route to Stop is equally authoritative, and Escape may
                        // be swallowed in browse mode, so the button is a
                        // primary route rather than a fallback.
                        string source = Text(root, "source");
                        return new FixerPageMessage(Kind.Stop, Fault.None, run, "", source);
                    }

                    case "copy-report":
                        return new FixerPageMessage(Kind.CopyReport, Fault.None, run, "", "");

                    case "open-help":
                    {
                        string topic = Text(root, "topic");
                        if (topic.Length == 0) return Bad(Fault.MissingField);
                        return new FixerPageMessage(Kind.OpenHelp, Fault.None, run, stage, topic);
                    }

                    case "open-device-picker":
                        return new FixerPageMessage(Kind.OpenDevicePicker, Fault.None, run, "", "");

                    default:
                        return Bad(Fault.UnknownKind);
                }
            }
        }

        /// <summary>
        /// What to write to the trace when a message could not be used. Never
        /// shown to the operator: they did not send it and cannot act on it.
        /// </summary>
        public string FaultDescription()
        {
            switch (Problem)
            {
                case Fault.Empty: return "an empty message";
                case Fault.TooLarge:
                    return "a message larger than "
                        + MaxRawBytes.ToString(CultureInfo.InvariantCulture) + " bytes";
                case Fault.NotJson: return "a message that was not a JSON object";
                case Fault.UnknownKind: return "a message of an unknown kind";
                case Fault.MissingField: return "a message missing a field it needs";
                default: return "";
            }
        }

        private static string Text(JsonElement o, string name)
            => o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "").Trim()
                : "";

        private static bool Flag(JsonElement o, string name)
            => o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
    }
}

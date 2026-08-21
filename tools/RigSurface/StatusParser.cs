using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JJFlex.RigSurface
{
    /// <summary>One object's worth of status, lifted off one wire line.</summary>
    internal sealed class ParsedStatus
    {
        public RigTarget Target { get; init; }

        public int Index { get; init; } = RigField.NoIndex;

        /// <summary>For client and stream status, the handle token that names it.</summary>
        public string? Handle { get; init; }

        public bool Removed { get; init; }

        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Turns FlexRadio status lines into <see cref="ParsedStatus"/> records.
    ///
    /// <para>The wire shapes, as the vendor parser reads them:</para>
    /// <list type="bullet">
    ///   <item><description><c>S0|radio slices=4 lineout_gain=50 ...</c></description></item>
    ///   <item><description><c>S0|transmit rfpower=100 mic_level=35 ...</c></description></item>
    ///   <item><description><c>S0|interlock state=READY tx_allowed=1 ...</c></description></item>
    ///   <item><description><c>S0|atu status=TUNE_NOT_STARTED memories_enabled=1</c></description></item>
    ///   <item><description><c>S0|slice 3 mode=USB RF_frequency=14.250000 client_handle=0x1234ABCD</c></description></item>
    ///   <item><description><c>S0|client 0x1234ABCD connected client_id=... program=SmartSDR station=Shack</c></description></item>
    ///   <item><description><c>S0|display pan 0x40000000 band=20 rfgain=0 pre=+8dB</c></description></item>
    ///   <item><description><c>S0|meter 1.src=SLC#1.num=0#1.nam=LEVEL#1.unit=dBm#</c></description></item>
    /// </list>
    ///
    /// <para>Three details in there are easy to get wrong, and each one produces
    /// a tool that is confidently incomplete rather than obviously broken. Meter
    /// status is delimited by hash characters, not spaces. Slices are removed by
    /// reporting <c>in_use=0</c> rather than by any "removed" token. And values
    /// carry embedded spaces as U+007F.</para>
    ///
    /// <para>Anything not modelled explicitly is still kept, under
    /// <see cref="RigTarget.Unknown"/> with the object name folded into the key,
    /// so an unmodelled object shows up as unmodelled rather than disappearing.
    /// Silently dropping status is how an observer starts lying.</para>
    /// </summary>
    internal static class StatusParser
    {
        /// <summary>U+007F, DEL. How the radio encodes a space inside a value.</summary>
        private const char EmbeddedSpace = (char)0x7F;

        /// <summary>
        /// Parses the body of an S-line (everything after "S&lt;handle&gt;|").
        /// One line can describe several objects, which is why this returns a
        /// list — meter status routinely packs many meters into one line.
        /// </summary>
        public static List<ParsedStatus> Parse(string body)
        {
            var results = new List<ParsedStatus>();
            if (string.IsNullOrWhiteSpace(body)) return results;

            string[] tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return results;

            switch (tokens[0])
            {
                case "radio":
                    results.Add(Simple(RigTarget.Radio, tokens, 1));
                    break;

                case "transmit":
                    results.Add(Simple(RigTarget.Transmit, tokens, 1));
                    break;

                case "interlock":
                    results.Add(Simple(RigTarget.Interlock, tokens, 1));
                    break;

                case "atu":
                    results.Add(Simple(RigTarget.Atu, tokens, 1));
                    break;

                case "gps":
                    results.Add(Simple(RigTarget.Gps, tokens, 1));
                    break;

                case "waveform":
                    results.Add(Simple(RigTarget.Waveform, tokens, 1));
                    break;

                case "slice":
                    results.Add(SliceStatus(tokens));
                    break;

                case "xvtr":
                    results.Add(Indexed(RigTarget.Xvtr, tokens));
                    break;

                case "tnf":
                    results.Add(Indexed(RigTarget.Tnf, tokens));
                    break;

                case "client":
                    results.Add(Client(tokens));
                    break;

                case "meter":
                    results.AddRange(Meters(tokens));
                    break;

                case "display":
                    ParsedStatus? display = Display(tokens);
                    if (display is not null) results.Add(display);
                    break;

                case "amplifier":
                    results.Add(HandleKeyed(RigTarget.Amplifier, tokens));
                    break;

                case "eq":
                    ParsedStatus? eq = Eq(tokens);
                    if (eq is not null) results.Add(eq);
                    break;

                default:
                    results.Add(Unmodelled(tokens[0], tokens));
                    break;
            }

            return results;
        }

        private static ParsedStatus Simple(RigTarget target, string[] tokens, int from)
        {
            var status = new ParsedStatus { Target = target };
            AddFields(status, tokens, from);
            return status;
        }

        private static ParsedStatus Indexed(RigTarget target, string[] tokens)
        {
            (int index, int from) = ReadIndex(tokens);
            var status = new ParsedStatus
            {
                Target = target,
                Index = index,
                Removed = Contains(tokens, from, "removed"),
            };
            AddFields(status, tokens, from);
            return status;
        }

        /// <summary>
        /// Slices are special in one respect: the radio does not send a
        /// "removed" token for them. A released slice is announced as
        /// <c>in_use=0</c>, and a tool that waits for the word "removed" will
        /// carry a dead slice in its model forever.
        /// </summary>
        private static ParsedStatus SliceStatus(string[] tokens)
        {
            (int index, int from) = ReadIndex(tokens);

            bool gone = Contains(tokens, from, "removed");
            for (int i = from; i < tokens.Length && !gone; i++)
            {
                if (string.Equals(tokens[i], "in_use=0", StringComparison.Ordinal)) gone = true;
            }

            var status = new ParsedStatus { Target = RigTarget.Slice, Index = index, Removed = gone };
            AddFields(status, tokens, from);
            return status;
        }

        private static (int Index, int From) ReadIndex(string[] tokens)
        {
            if (tokens.Length > 1
                && int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return (parsed, 2);
            }
            return (RigField.NoIndex, 1);
        }

        private static ParsedStatus Client(string[] tokens)
        {
            string? handle = tokens.Length > 1 ? tokens[1] : null;
            bool disconnected = Contains(tokens, 2, "disconnected");

            var status = new ParsedStatus
            {
                Target = RigTarget.Client,
                Index = HandleToIndex(handle),
                Handle = handle,
                Removed = disconnected,
            };

            // "connected" and "disconnected" are bare tokens, not key=value.
            // Recording them as a field lets the model answer "is this client
            // live?" without depending on object-removal semantics.
            if (Contains(tokens, 2, "connected")) status.Fields["connected"] = "1";
            if (disconnected) status.Fields["connected"] = "0";

            AddFields(status, tokens, 2);
            return status;
        }

        private static ParsedStatus HandleKeyed(RigTarget target, string[] tokens)
        {
            string? handle = tokens.Length > 1 ? tokens[1] : null;
            var status = new ParsedStatus
            {
                Target = target,
                Index = HandleToIndex(handle),
                Handle = handle,
                Removed = Contains(tokens, 2, "removed"),
            };
            AddFields(status, tokens, 2);
            return status;
        }

        private static ParsedStatus? Display(string[] tokens)
        {
            // "display pan 0x40000000 band=20 rfgain=0 ..." — this is where RF
            // gain, preamp and band actually live. They are per-panadapter,
            // which is to say per-SCU, not per-slice and not station-wide.
            if (tokens.Length < 3) return null;

            string handle = tokens[2];
            var status = new ParsedStatus
            {
                Target = RigTarget.Display,
                Index = HandleToIndex(handle),
                Handle = handle,
                Removed = Contains(tokens, 3, "removed"),
            };
            status.Fields["display_kind"] = tokens[1];
            AddFields(status, tokens, 3);
            return status;
        }

        private static ParsedStatus? Eq(string[] tokens)
        {
            // "eq rxsc mode=1 63Hz=0 ..." — the sub-token names which equaliser.
            if (tokens.Length < 2) return null;
            int index = tokens[1].StartsWith("tx", StringComparison.Ordinal) ? 1 : 0;
            var status = new ParsedStatus { Target = RigTarget.Eq, Index = index };
            status.Fields["eq_kind"] = tokens[1];
            AddFields(status, tokens, 2);
            return status;
        }

        private static IEnumerable<ParsedStatus> Meters(string[] tokens)
        {
            // Meter status is the one topic that is NOT space delimited between
            // records. The payload is hash separated:
            //   meter 1.src=SLC#1.num=0#1.nam=LEVEL#1.low=-150.0#1.unit=dBm#
            // Splitting on spaces like every other topic yields one record and
            // silently loses the rest.
            //
            // Note also that only meter DESCRIPTORS arrive here. The readings
            // themselves come over UDP as VITA-49, so a TCP-only observer knows
            // what meters exist and what they mean but never what they say.
            var records = new List<string>();
            for (int i = 1; i < tokens.Length; i++)
            {
                records.AddRange(tokens[i].Split('#', StringSplitOptions.RemoveEmptyEntries));
            }

            var byIndex = new Dictionary<int, ParsedStatus>();
            foreach (string record in records)
            {
                int eq = record.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) continue;

                string left = record[..eq];
                string value = DecodeValue(record[(eq + 1)..]);

                int dot = left.IndexOf('.', StringComparison.Ordinal);
                if (dot <= 0) continue;

                if (!int.TryParse(left[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out int meterNum))
                {
                    continue;
                }

                if (!byIndex.TryGetValue(meterNum, out ParsedStatus? status))
                {
                    status = new ParsedStatus { Target = RigTarget.Meter, Index = meterNum };
                    byIndex[meterNum] = status;
                }
                status.Fields[left[(dot + 1)..]] = value;
            }
            return byIndex.Values;
        }

        private static ParsedStatus Unmodelled(string objectName, string[] tokens)
        {
            var status = new ParsedStatus { Target = RigTarget.Unknown };
            for (int i = 1; i < tokens.Length; i++)
            {
                string token = tokens[i];
                int eq = token.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) continue;
                status.Fields[objectName + "." + token[..eq]] = DecodeValue(token[(eq + 1)..]);
            }
            return status;
        }

        private static void AddFields(ParsedStatus status, string[] tokens, int from)
        {
            for (int i = from; i < tokens.Length; i++)
            {
                string token = tokens[i];
                int eq = token.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) continue;
                status.Fields[token[..eq]] = DecodeValue(token[(eq + 1)..]);
            }
        }

        private static bool Contains(string[] tokens, int from, string bare)
        {
            for (int i = from; i < tokens.Length; i++)
            {
                if (string.Equals(tokens[i], bare, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static int HandleToIndex(string? handle)
        {
            if (string.IsNullOrEmpty(handle)) return RigField.NoIndex;
            string hex = handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? handle[2..] : handle;
            return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
                ? unchecked((int)value)
                : RigField.NoIndex;
        }

        /// <summary>
        /// Values cannot contain a raw space, because the wire is space
        /// delimited. Anything the radio needs to embed arrives escaped.
        ///
        /// <para>The form that matters is DEL, U+007F: station names and profile
        /// names carry their spaces that way in both directions, and the vendor
        /// library does the same substitution when it sends one. A tool that
        /// does not decode it reports a station called "MyShack" run together
        /// and looks broken in a confusing place.</para>
        /// </summary>
        internal static string DecodeValue(string raw)
        {
            if (raw.Length == 0) return raw;

            if (raw.IndexOf(EmbeddedSpace) >= 0)
            {
                raw = raw.Replace(EmbeddedSpace, ' ');
            }

            if (raw.IndexOf('\\') < 0 && raw.IndexOf('%') < 0)
            {
                return raw;
            }

            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '\\' && i + 3 < raw.Length && raw[i + 1] == 'u' && raw[i + 2] == '{')
                {
                    int close = raw.IndexOf('}', i + 3);
                    if (close > 0
                        && int.TryParse(raw[(i + 3)..close], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                    {
                        sb.Append((char)code);
                        i = close;
                        continue;
                    }
                }

                if (raw[i] == '%' && i + 2 < raw.Length
                    && int.TryParse(raw.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int pct))
                {
                    sb.Append((char)pct);
                    i += 2;
                    continue;
                }

                sb.Append(raw[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The inverse, for values we send. Applied to anything the harness
        /// writes back during restore, so a station or profile name containing a
        /// space survives the round trip instead of being truncated at the space
        /// by the radio's own tokenizer.
        /// </summary>
        internal static string EncodeValue(string value)
            => value.IndexOf(' ') >= 0 ? value.Replace(' ', EmbeddedSpace) : value;
    }
}

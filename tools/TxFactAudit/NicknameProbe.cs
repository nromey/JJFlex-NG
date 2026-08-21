using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using JJFlex.RigSurface;

namespace JJFlex.TxFactAudit
{
    /// <summary>
    /// Finds out what the radio actually does with a nickname.
    ///
    /// <para><b>Why this needed measuring.</b> <c>Radio.Nickname</c> sanitises
    /// the string and sends <c>radio name &lt;value&gt;</c> with no length check
    /// anywhere in FlexLib or in JJ Flexible, and the command is space
    /// delimited with no quoting. So there are two ways an operator's chosen
    /// name can be quietly not what they typed — too long, or containing a
    /// space — and one failure mode of this input is already known to report
    /// success: an empty value is accepted and ignored, and there is no command
    /// that clears the field.</para>
    ///
    /// <para><b>Every read is from a connection that did not write.</b> The
    /// radio broadcasts a status change to every client EXCEPT the one that
    /// caused it, so reading a value back on the writing connection returns our
    /// own model, unchanged since before the write, and looks exactly like
    /// confirmation. This probe therefore opens the observer first and asserts
    /// only through it.</para>
    ///
    /// <para><b>It always puts the name back.</b> This is Noel's station and
    /// the nickname is the only thing other clients see of it — a headless 8600
    /// has no front display, so discovery is the whole of its visible effect.
    /// The restore is verified from a third, fresh connection rather than
    /// assumed.</para>
    /// </summary>
    internal static class NicknameProbe
    {
        /// <summary>What the radio substitutes for a space in status output.
        /// The status stream is space delimited, so the radio encodes an
        /// embedded space as DEL rather than breaking its own framing. Nothing
        /// does the reverse on the way in, which is the question below.</summary>
        private const char SpaceInStatus = '';

        internal static int Run(string[] args, Func<string[], RigWire> open, Func<string[], string, string?> option)
        {
            string restoreTo = option(args, "--restore-to") ?? "K5NER";
            bool apply = args.Any(a => string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase));

            Console.WriteLine("What the radio does with a nickname: how long, and what about a space.");
            Console.WriteLine();
            Console.WriteLine("Two open questions, both answered by setting a value and reading it back from a");
            Console.WriteLine("connection that did not write it. Whatever happens, the name is put back to "
                              + restoreTo + ".");
            Console.WriteLine();

            if (!apply)
            {
                Console.WriteLine("Plan only — this WRITES STATION STATE. Re-run with --apply.");
                return 0;
            }

            using RigWire writer = open(args);
            Console.WriteLine();
            Console.WriteLine("Opening a second connection to read back through.");
            using RigWire observer = open(args);

            string? original = observer.State.Get(RigField.Radio("nickname"));
            Console.WriteLine();
            Console.WriteLine("The radio's nickname before anything is written: "
                              + Describe(original));

            var findings = new List<string>();
            try
            {
                findings.AddRange(ProbeLength(writer, observer));
                findings.AddRange(ProbeSpace(writer, observer));
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("Putting the name back.");
                Set(writer, restoreTo);

                // A THIRD connection for the restore check. The observer has
                // been listening the whole time and its model is almost
                // certainly right, but "almost certainly" is not the standard
                // for leaving somebody's station renamed.
                Thread.Sleep(500);
                using RigWire check = open(args);
                string? after = check.State.Get(RigField.Radio("nickname"));
                Console.WriteLine("A fresh connection now reads: " + Describe(after));
                Console.WriteLine(string.Equals(after, restoreTo, StringComparison.Ordinal)
                    ? "Restored, and confirmed from a connection that saw none of the writes."
                    : "NOT RESTORED. The radio reads " + Describe(after) + " and should read "
                      + restoreTo + ". Say so plainly rather than letting it pass.");
            }

            Console.WriteLine();
            Console.WriteLine("WHAT WE LEARNED");
            Console.WriteLine();
            foreach (string line in findings) Console.WriteLine("  - " + line);
            return 0;
        }

        /// <summary>
        /// How long a nickname the radio will keep.
        ///
        /// <para>Each candidate is a different string, not the same string at
        /// different lengths, so the three possible outcomes stay
        /// distinguishable: kept whole, truncated to N, or rejected outright —
        /// and rejection shows up as the PREVIOUS value still being there,
        /// which is the empty-value failure mode repeating itself and would
        /// otherwise read as "no truncation".</para>
        /// </summary>
        private static IEnumerable<string> ProbeLength(RigWire writer, RigWire observer)
        {
            var results = new List<string>();
            int longestKept = 0;
            int shortestRefused = int.MaxValue;
            string? lastAccepted = observer.State.Get(RigField.Radio("nickname"));
            string? refusalCode = null;

            // Every length from 8 up. The interesting boundary turned out to be
            // in single digits, so stepping in eights would have found only that
            // it was "somewhere below sixteen".
            foreach (int length in new[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 32 })
            {
                string candidate = Ruler(length);
                WireReply reply = Set(writer, candidate);
                string? seen = Settle(observer, RigField.Radio("nickname"), candidate);

                if (seen == candidate)
                {
                    longestKept = Math.Max(longestKept, length);
                    lastAccepted = candidate;
                    Console.WriteLine($"  {length} characters: kept whole.");
                    continue;
                }

                // THE TRAP, and this probe fell into it on its first run: the
                // ruler strings are prefixes of one another, so the value left
                // over from the last SUCCESSFUL write is always a prefix of the
                // one that just failed. Testing "is it a prefix" therefore reads
                // a refusal as a truncation, every time, and reports a
                // confident wrong limit. Compare against what we last got the
                // radio to accept, not against the shape of the string.
                if (seen == lastAccepted)
                {
                    shortestRefused = Math.Min(shortestRefused, length);
                    if (refusalCode is null && !reply.Ok) refusalCode = reply.Code;
                    Console.WriteLine($"  {length} characters: REFUSED. The name is still "
                                      + Describe(seen) + $", and the radio answered {reply.Code}.");
                    continue;
                }

                if (seen is not null && seen.Length > 0
                    && candidate.StartsWith(seen, StringComparison.Ordinal))
                {
                    shortestRefused = Math.Min(shortestRefused, length);
                    results.Add($"A {length}-character name came back {seen.Length} characters long and is a "
                              + "genuine truncation, not a leftover: the radio cut it.");
                    Console.WriteLine($"  {length} characters: TRUNCATED to {seen.Length}.");
                    lastAccepted = seen;
                    continue;
                }

                shortestRefused = Math.Min(shortestRefused, length);
                Console.WriteLine($"  {length} characters: unexpected — radio reads {Describe(seen)}.");
            }

            if (longestKept > 0 && shortestRefused != int.MaxValue)
            {
                results.Add($"The longest nickname the radio accepted was {longestKept} characters; "
                          + $"{shortestRefused} was refused. So the limit is {longestKept}.");
            }
            else if (longestKept > 0)
            {
                results.Add($"Every length tried up to {longestKept} characters was accepted.");
            }

            if (refusalCode is not null)
            {
                results.Add($"An over-long nickname is REFUSED with error {refusalCode} — the radio does not "
                          + "truncate it and does not silently ignore it, it says no on the wire. But nothing "
                          + "listens: Radio.Nickname sends the command and never inspects the reply, so JJ "
                          + "Flexible reports a rename that did not happen and leaves the old name in place.");
            }

            return results;
        }

        /// <summary>
        /// What happens to a space, which the command's own framing has no way
        /// to carry.
        /// </summary>
        private static IEnumerable<string> ProbeSpace(RigWire writer, RigWire observer)
        {
            var results = new List<string>();
            const string withSpace = "ALPHA BRAVO";

            Set(writer, withSpace);
            string? seen = Settle(observer, RigField.Radio("nickname"), withSpace);

            Console.WriteLine();
            Console.WriteLine("  A name with a space in it: the radio reads " + Describe(seen) + ".");

            if (seen is null)
            {
                results.Add("A nickname containing a space left the radio reporting nothing at all.");
            }
            else if (seen == withSpace)
            {
                results.Add("A space survives a nickname intact, and comes back as a space. "
                          + "The command's space delimiting does not bite here.");
            }
            else if (seen == withSpace.Replace(' ', SpaceInStatus))
            {
                results.Add("A space survives, and the radio reports it back encoded as U+007F so its own "
                          + "space-delimited status framing still parses. Anything displaying the nickname "
                          + "has to decode that or an operator sees a control character in their radio's name.");
            }
            else if (seen == "ALPHA")
            {
                results.Add("A nickname containing a space is TRUNCATED AT THE SPACE. 'ALPHA BRAVO' becomes "
                          + "'ALPHA'. The set command is space delimited with no quoting, so everything after "
                          + "the first space is parsed as further arguments and dropped — and the radio "
                          + "reports success. An operator who names their radio 'Shack 8600' gets 'Shack'.");
            }
            else if (seen == withSpace.Replace(" ", ""))
            {
                // MEASURED on the bench 8600, 2026-08-20. Neither truncation nor
                // survival: the radio takes the remaining space-delimited tokens
                // and joins them.
                results.Add("A nickname containing a space has the space SILENTLY REMOVED. 'ALPHA BRAVO' is "
                          + "stored as 'ALPHABRAVO' — the radio takes the space-delimited tokens after "
                          + "'radio name' and joins them, because the command has no quoting and no escape. "
                          + "It reports success, and nothing anywhere warns the operator. Someone naming "
                          + "their radio 'Shack 8600' gets 'Shack8600' and finds out from the radio list.");
            }
            else
            {
                results.Add($"A nickname containing a space came back as {Describe(seen)}, which is none of "
                          + "the outcomes anticipated. Worth a second look.");
            }

            return results;
        }

        /// <summary>
        /// A string of a known length whose every position is readable, so a
        /// truncated answer says exactly where it was cut. Deliberately no
        /// spaces and no punctuation: this probe is measuring length, and one
        /// question at a time.
        /// </summary>
        private static string Ruler(int length)
        {
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++) sb.Append((char)('0' + (i % 10)));
            return sb.ToString();
        }

        private static WireReply Set(RigWire writer, string value)
        {
            return writer.Send("radio name " + value);
        }

        /// <summary>
        /// Give the radio time to broadcast, then report whatever the observer
        /// ended up with — the value we hoped for, something else, or nothing.
        /// </summary>
        private static string? Settle(RigWire observer, RigField field, string expected)
        {
            observer.WaitForValue(field, expected, TimeSpan.FromSeconds(2));
            // Even when the value never matches, the delta may still be in
            // flight; let the stream go quiet before believing the answer.
            observer.Settle(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));
            return observer.State.Get(field);
        }

        /// <summary>
        /// A value as prose, with any control characters named rather than
        /// printed. A U+007F sent to a terminal — or to a screen reader — is
        /// not a thing anyone can read, and it is exactly what this probe
        /// expects to find.
        /// </summary>
        private static string Describe(string? value)
        {
            if (value is null) return "nothing at all";
            if (value.Length == 0) return "an empty string";

            var sb = new StringBuilder();
            foreach (char c in value)
            {
                if (c == SpaceInStatus) sb.Append("<U+007F>");
                else if (char.IsControl(c)) sb.Append("<U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('>');
                else sb.Append(c);
            }
            return $"\"{sb}\" ({value.Length} characters)";
        }
    }
}

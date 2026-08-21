using System;
using System.Linq;

namespace JJFlex.RigSurface
{
    /// <summary>
    /// Sends one command and prints what the radio said.
    ///
    /// <para>Added after the first hardware run, where it was needed and
    /// missing. The nickname write reported OK, the radio never sent a status
    /// delta for it, the harness therefore believed the write had not taken,
    /// and restore consequently skipped a field that HAD in fact changed. Undoing
    /// that by hand required a way to send one command, and there wasn't one.</para>
    ///
    /// <para>It goes through the same transmit guard as everything else, so it
    /// cannot key the radio however it is invoked.</para>
    /// </summary>
    internal static class Send
    {
        public static int Run(string[] args)
        {
            string[] words = args
                .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                .ToArray();

            string? host = Program.Option(args, "--host");
            if (host is not null) words = words.Where(w => w != host).ToArray();

            if (words.Length == 0)
            {
                Console.Error.WriteLine("send needs a command, for example: send radio name MyRadio");
                return 2;
            }

            string command = string.Join(' ', words);

            CommandEffect effect = TransmitGuard.Classify(command);
            if (effect != CommandEffect.Silent)
            {
                Console.Error.WriteLine(
                    $"Refused '{command}': it would key the radio ({effect}). " +
                    "This command cannot transmit, whatever it is asked to send.");
                return 3;
            }

            using RigWire wire = Program.Open(args);
            Guards.RequireNotTransmitting(wire);

            WireReply reply = wire.Send(command);
            Console.WriteLine();
            Console.WriteLine(reply.ToString());
            Console.WriteLine();
            Console.WriteLine("Note that an OK reply means the radio ACCEPTED the command, not that any status");
            Console.WriteLine("changed. Several fields are never echoed back. Confirm with 'observe' on a fresh");
            Console.WriteLine("connection, which re-reads the full state rather than waiting for a delta.");
            return reply.Ok ? 0 : 1;
        }
    }
}

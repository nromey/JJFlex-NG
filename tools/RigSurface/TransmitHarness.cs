using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace JJFlex.RigSurface
{
    /// <summary>
    /// The transmit harness. Built now, deliberately, while nothing is hot.
    ///
    /// <para><b>The dummy load is not here yet.</b> The Palstar DL-2000 is on
    /// order. This exists so that when it arrives the tests are written against
    /// a harness that already exists and has been read calmly, rather than
    /// composed in a hurry next to a load that is warming up. Only the one-watt
    /// smoke test is runnable before then, and only sparingly.</para>
    ///
    /// <para><b>A dummy load cannot meaningfully test the antenna tuner.</b>
    /// State this to anyone who expects otherwise. Into a matched fifty ohms the
    /// tuner finds a match immediately, so all that gets exercised is the
    /// command path — did we ask, did it answer, did the status move through
    /// TUNE_IN_PROGRESS to a result. Real tuning behaviour needs a real
    /// mismatch, which means a real antenna, and there is no substitute.</para>
    ///
    /// <para><b>The tuner is rationed by relay wear, not by RF.</b> It will tune
    /// with nothing connected at all — Noel has done it. The cost is mechanical:
    /// physical relays with a finite number of operations, spent whether or not
    /// any power went anywhere. So the budget is a counter enforced in code, not
    /// a comment asking the author to be careful.</para>
    /// </summary>
    internal static class TransmitHarness
    {
        /// <summary>
        /// Watts. The only power sanctioned before the dummy load arrives, and
        /// only for short single keyings.
        /// </summary>
        public const int SmokeTestWatts = 1;

        public static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("transmit needs a subcommand: plan, smoke, or atu.");
                return 2;
            }

            return args[0].ToLowerInvariant() switch
            {
                "plan" => Plan(args),
                "smoke" => Smoke(args),
                "atu" => Atu(args),
                _ => Bad(args[0]),
            };
        }

        private static int Bad(string sub)
        {
            Console.Error.WriteLine($"Unknown transmit subcommand '{sub}'.");
            return 2;
        }

        // ---------------------------------------------------------------- //

        private static TransmitPlan SmokePlan() => new()
        {
            Purpose = "One short low-power keying, to prove that the transmit path keys, that the radio reports "
                    + "TRANSMITTING while it does, and that it returns to receive afterwards. No antenna is connected.",
            PowerCeilingWatts = SmokeTestWatts,
            MaxSingleKeyDownSeconds = 1.0,
            TotalKeyDownBudgetSeconds = 2.0,
            CoolingRatio = 10.0,
            AtuTuneBudget = 0,
            EstimatedDuration = TimeSpan.FromSeconds(20),
        };

        private static TransmitPlan AtuPlan(int cycles, int watts) => new()
        {
            Purpose = "Exercise the antenna tuner's COMMAND PATH. Note what this does and does not show: into a "
                    + "matched load the tuner finds a match immediately, so this proves the command is issued and "
                    + "the status moves, and proves nothing about tuning behaviour. Each cycle costs relay "
                    + "operations that do not come back.",
            PowerCeilingWatts = watts,
            MaxSingleKeyDownSeconds = 8.0,
            TotalKeyDownBudgetSeconds = 8.0 * cycles,
            CoolingRatio = 6.0,
            AtuTuneBudget = cycles,
            EstimatedDuration = TimeSpan.FromSeconds(cycles * 60),
        };

        private static int Plan(string[] args)
        {
            Console.WriteLine("The smoke test, which is runnable now:");
            Console.WriteLine();
            Console.WriteLine(SmokePlan().Describe());
            Console.WriteLine();
            Console.WriteLine("An antenna tuner run, which is NOT runnable yet:");
            Console.WriteLine();
            Console.WriteLine(AtuPlan(cycles: 3, watts: 10).Describe());
            Console.WriteLine();
            Console.WriteLine("Standing facts about this bench, so nobody has to rediscover them:");
            Console.WriteLine();
            Console.WriteLine("  - No antenna is connected. The dummy load is on order and is not here.");
            Console.WriteLine("  - One watt is acceptable for a smoke test, used sparingly. Nothing keys");
            Console.WriteLine("    repeatedly at any power until the load arrives.");
            Console.WriteLine("  - The load, when it does arrive, handles 400 watts continuously and 2 kilowatts");
            Console.WriteLine("    for a minute. An iterative harness keys many times, so the budget is tracked");
            Console.WriteLine("    in code rather than trusted to whoever wrote the test.");
            Console.WriteLine("  - The tuner is rationed by RELAY WEAR and not by RF. It tunes happily with");
            Console.WriteLine("    nothing connected, and every cycle spends relay operations permanently.");
            Console.WriteLine("  - A dummy load cannot meaningfully test the tuner. Into a matched fifty ohms it");
            Console.WriteLine("    matches instantly. Only the command path is exercised. Real tuning behaviour");
            Console.WriteLine("    needs a real mismatch, which means a real antenna.");
            Console.WriteLine();
            Console.WriteLine("Every run asks for typed consent first, enforces its own power ceiling by reading");
            Console.WriteLine("the power back FROM THE RADIO rather than trusting that it was set, and restores");
            Console.WriteLine("every setting it touched including when it fails partway.");
            return 0;
        }

        // ---------------------------------------------------------------- //

        private static int Smoke(string[] args)
        {
            using RigWire wire = Program.Open(args);

            Console.WriteLine();
            Console.WriteLine(Guards.DescribeCensus(wire));

            Guards.RequireSoleOperator(wire);
            Guards.RequireNotTransmitting(wire);

            TransmitConsent? consent = TransmitConsent.Grant(SmokePlan(), AskOperator, GrantedBy());
            if (consent is null)
            {
                Console.WriteLine("Not authorised. Nothing was transmitted.");
                return 0;
            }

            using var scope = RigStateScope.Capture(wire);
            using var watchdog = new KeyWatchdog(wire, consent);

            try
            {
                if (!SetPowerFromBelow(wire, consent, SmokeTestWatts)) return 1;

                Console.WriteLine();
                Console.WriteLine("Keying for one second.");
                KeyResult result = KeyDown(wire, consent, watchdog, TimeSpan.FromSeconds(1));

                Console.WriteLine();
                Console.WriteLine(result.Describe());
                Console.WriteLine();
                Console.WriteLine(consent.Summarise());
                return result.Keyed && result.Unkeyed ? 0 : 1;
            }
            finally
            {
                consent.Revoke();
            }
        }

        private static int Atu(string[] args)
        {
            Console.WriteLine("The antenna tuner run is PARKED until the dummy load arrives.");
            Console.WriteLine();
            Console.WriteLine("The harness below is complete and the budget is enforced, but running it now would");
            Console.WriteLine("key the radio repeatedly with nothing connected, and it would tell us nothing that");
            Console.WriteLine("a matched load will not tell us better. Relay operations are permanent; there is no");
            Console.WriteLine("reason to spend them on a run whose result is known in advance.");
            Console.WriteLine();
            Console.WriteLine(AtuPlan(cycles: 3, watts: 10).Describe());
            Console.WriteLine();
            Console.WriteLine("When the load is here, this becomes runnable by removing exactly this refusal.");
            Console.WriteLine("It is written this way rather than left unwritten on purpose: the code gets read");
            Console.WriteLine("calmly now instead of being composed in a hurry beside something hot.");
            return 0;
        }

        // ---------------------------------------------------------------- //
        // Power
        // ---------------------------------------------------------------- //

        /// <summary>
        /// Brings power to the ceiling FROM BELOW, and verifies the result by
        /// reading it back from the radio.
        ///
        /// <para>Reading it back is not belt and braces, it is the point. The
        /// application's own first-run setup writes RFPower = 100 unconditionally
        /// when it finds no saved profile, so "we set it to one watt" and "the
        /// radio is at one watt" are genuinely different claims. This function
        /// only ever makes the second one, and it refuses rather than keying if
        /// the radio disagrees.</para>
        /// </summary>
        private static bool SetPowerFromBelow(RigWire wire, TransmitConsent consent, int watts)
        {
            var field = RigField.Transmit("rfpower");
            int ceiling = consent.ClampPower(watts);

            string? reported = wire.State.Get(field);
            Console.WriteLine($"The radio reports transmit power as {reported ?? "(nothing)"}.");

            // Zero first, so that if anything below fails the radio is left at
            // the bottom of its range rather than wherever it happened to be.
            WireReply floor = wire.Send(OwnershipTable.SetCommand(field, "0")!);
            if (!floor.Ok)
            {
                Console.Error.WriteLine($"Could not set power to zero: {floor.Code} {floor.Message}. Refusing to key.");
                return false;
            }
            wire.WaitForValue(field, "0", TimeSpan.FromSeconds(2));

            WireReply set = wire.Send(OwnershipTable.SetCommand(field, ceiling.ToString(CultureInfo.InvariantCulture))!);
            if (!set.Ok)
            {
                Console.Error.WriteLine($"Could not set power to {ceiling}: {set.Code} {set.Message}. Refusing to key.");
                return false;
            }

            bool confirmed = wire.WaitForValue(field, ceiling.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(3));
            string? now = wire.State.Get(field);

            if (!confirmed)
            {
                Console.Error.WriteLine(
                    $"Asked for {ceiling} watts and the radio reports {now ?? "nothing at all"}. " +
                    "REFUSING TO KEY. A power setting that cannot be confirmed from the radio is not a power setting.");
                return false;
            }

            if (!int.TryParse(now, NumberStyles.Integer, CultureInfo.InvariantCulture, out int actual)
                || actual > ceiling)
            {
                Console.Error.WriteLine(
                    $"The radio reports {now} against a ceiling of {ceiling}. REFUSING TO KEY.");
                return false;
            }

            Console.WriteLine($"The radio confirms {actual} watts, at or below the ceiling of {ceiling}.");
            return true;
        }

        // ---------------------------------------------------------------- //
        // Keying
        // ---------------------------------------------------------------- //

        private sealed record KeyResult(bool Keyed, bool Unkeyed, TimeSpan Held, string Detail)
        {
            public string Describe()
            {
                if (!Keyed)
                {
                    return "The radio never reported TRANSMITTING. It did not key. " + Detail;
                }
                if (!Unkeyed)
                {
                    return "THE RADIO KEYED AND DID NOT REPORT RETURNING TO RECEIVE. Check it by hand. " + Detail;
                }
                return string.Create(CultureInfo.InvariantCulture,
                    $"Keyed for {Held.TotalSeconds:F2} seconds and returned to receive. {Detail}").TrimEnd();
            }
        }

        /// <summary>
        /// One bounded, paired keying. There is deliberately no way to key
        /// without also unkeying: the raw commands are not exposed to callers,
        /// and the unkey runs in a finally with a watchdog behind it.
        /// </summary>
        private static KeyResult KeyDown(RigWire wire, TransmitConsent consent, KeyWatchdog watchdog, TimeSpan requested)
        {
            Guards.RequireNotTransmitting(wire);

            TimeSpan hold = consent.ClampKeyDown(requested);
            if (hold <= TimeSpan.Zero)
            {
                return new KeyResult(false, true, TimeSpan.Zero, "The key-down budget is spent.");
            }

            var start = DateTime.UtcNow;
            bool keyed = false;

            try
            {
                watchdog.Arm(hold + TimeSpan.FromSeconds(2));
                WireReply key = wire.SendKeying("xmit 1", consent);
                if (!key.Ok)
                {
                    return new KeyResult(false, true, TimeSpan.Zero,
                        $"The radio refused 'xmit 1': {key.Code} {key.Message}.");
                }

                keyed = wire.WaitFor(RigField.Interlock("state"),
                    v => Guards.ReadTransmitStateFrom(v) == TransmitState.Transmitting,
                    TimeSpan.FromSeconds(2));

                DateTime until = start + hold;
                while (DateTime.UtcNow < until) Thread.Sleep(20);

                return new KeyResult(keyed, true, DateTime.UtcNow - start,
                    keyed ? "" : "The command was accepted but the interlock never reported TRANSMITTING.");
            }
            finally
            {
                TimeSpan held = DateTime.UtcNow - start;
                Unkey(wire, consent);
                consent.RecordKeyDown(held);
                watchdog.Disarm();

                bool receiving = wire.WaitFor(RigField.Interlock("state"),
                    v => Guards.ReadTransmitStateFrom(v) == TransmitState.NotTransmitting,
                    TimeSpan.FromSeconds(3));

                if (!receiving)
                {
                    Console.Error.WriteLine(
                        "THE RADIO HAS NOT REPORTED RETURNING TO RECEIVE. Interlock says " +
                        (wire.State.Get(RigField.Interlock("state")) ?? "nothing") + ". Check the radio by hand.");
                }
                else if (keyed)
                {
                    Console.WriteLine("The radio reports receive again.");
                }
            }
        }

        /// <summary>
        /// Unkeys. Never throws, is safe to call when not keyed, and is safe to
        /// call twice — every path out of a keying goes through it.
        /// </summary>
        internal static void Unkey(RigWire wire, TransmitConsent? consent)
        {
            try
            {
                // "xmit 0" is classified as silent by the guard, precisely so
                // that the way OUT of transmit is never blocked by a budget, a
                // revoked consent, or anything else.
                wire.Send("xmit 0", TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Console.Error.WriteLine("The unkey command failed: " + ex.Message + ". CHECK THE RADIO BY HAND.");
            }
        }

        /// <summary>
        /// Unkeys the radio if a keying outlives its allowance, and on process
        /// exit or Ctrl+C.
        ///
        /// <para>The failure this exists for is not hypothetical. If the process
        /// dies between key and unkey, the radio stays keyed with nobody
        /// watching, and the only thing that ends it is somebody noticing. A
        /// finally block does not run when the process is killed; this does what
        /// it can about the cases that are catchable.</para>
        /// </summary>
        private sealed class KeyWatchdog : IDisposable
        {
            private readonly RigWire _wire;
            private readonly TransmitConsent _consent;
            private readonly Timer _timer;
            private readonly ConsoleCancelEventHandler _cancel;
            private readonly EventHandler _exit;

            public KeyWatchdog(RigWire wire, TransmitConsent consent)
            {
                _wire = wire;
                _consent = consent;
                _timer = new Timer(_ => Fire("the keying outlived its allowance"), null, Timeout.Infinite, Timeout.Infinite);

                _cancel = (_, e) =>
                {
                    e.Cancel = true;
                    Fire("the run was interrupted");
                };
                _exit = (_, _) => Fire("the process is exiting");

                Console.CancelKeyPress += _cancel;
                AppDomain.CurrentDomain.ProcessExit += _exit;
            }

            public void Arm(TimeSpan within) => _timer.Change(within, Timeout.InfiniteTimeSpan);

            public void Disarm() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

            private void Fire(string why)
            {
                Console.Error.WriteLine($"Watchdog: unkeying because {why}.");
                Unkey(_wire, _consent);
            }

            public void Dispose()
            {
                Console.CancelKeyPress -= _cancel;
                AppDomain.CurrentDomain.ProcessExit -= _exit;
                _timer.Dispose();
                Unkey(_wire, _consent);
            }
        }

        // ---------------------------------------------------------------- //

        private static string GrantedBy() =>
            Environment.UserName + " at " + Environment.MachineName;

        /// <summary>
        /// Reads the plan aloud and waits for the confirmation phrase.
        ///
        /// <para>Deliberately a blocking console read with no default and no
        /// timeout. There is no way to consent by pressing return, and no way to
        /// consent by not answering.</para>
        /// </summary>
        private static string? AskOperator(string prompt)
        {
            Console.WriteLine();
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine(prompt);
            Console.WriteLine("--------------------------------------------------");
            Console.Write("> ");
            return Console.ReadLine();
        }
    }
}

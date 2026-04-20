#nullable enable

using System;
using System.Linq;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.SmartLink;

namespace SmartLinkSessionHarness
{
    /// <summary>
    /// Interactive console harness for exercising the Sprint 26 Phase 1
    /// SmartLink session stack end-to-end against a real SmartLink backend.
    ///
    /// <para>
    /// Not bundled in the installer. Used during Phase 1 to prove the session
    /// owner + adapter + coordinator work against real SmartLink before Phase 2
    /// migration touches production paths in FlexBase. Also useful after
    /// Sprint 26 ships as a diagnostic tool for field issues that need to be
    /// reproduced without the JJ Flex UI in the way.
    /// </para>
    ///
    /// <para>
    /// <b>Auth:</b> you need a valid SmartLink JWT to register the application.
    /// Easiest source: sign in via JJ Flex normally, then retrieve the token
    /// from the trace file or crash reporter. Pass it via the interactive
    /// <c>token &lt;jwt&gt;</c> command or the <c>--token &lt;jwt&gt;</c>
    /// command-line argument.
    /// </para>
    ///
    /// <para>
    /// <b>Commands:</b>
    /// <list type="bullet">
    /// <item><c>token &lt;jwt&gt;</c> — set the SmartLink JWT for registration</item>
    /// <item><c>connect</c> — bring up the session (Connect)</item>
    /// <item><c>disconnect</c> — explicit user-initiated disconnect</item>
    /// <item><c>status</c> — print Status, IsConnected, LastError, ReconnectAttemptCount</item>
    /// <item><c>drop</c> — force IsConnected=false on the underlying WanServer (simulates a silent drop)</item>
    /// <item><c>list</c> — print AvailableRadios</item>
    /// <item><c>reset</c> — full Reset()</item>
    /// <item><c>shutdown</c> / <c>exit</c> — dispose the session + exit</item>
    /// <item><c>trace on</c>/<c>trace off</c> — toggle verbose tracing to the console</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class Program
    {
        // Harness application identity. Picked to make it obvious in SmartLink backend
        // logs that traffic is from the harness, not the main app.
        private const string ProgramName = "JJFlexHarness";
        private const string Platform = "Windows";

        private static int Main(string[] args)
        {
            Console.WriteLine("SmartLinkSessionHarness — Sprint 26 Phase 1 integration REPL");
            Console.WriteLine("Type 'help' for commands.");
            Console.WriteLine();

            // Optional: --token from CLI so you can reuse shell history without pasting.
            string? initialToken = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--token" && i + 1 < args.Length)
                {
                    initialToken = args[i + 1];
                }
            }

            EnableConsoleTracing();

            var coordinator = new SmartLinkSessionCoordinator(
                accountId =>
                {
                    var sessionId = Guid.NewGuid().ToString().Substring(0, 8);
                    var adapter = new WanServerAdapter(tracePrefix: $"[session={sessionId}]");
                    var sink = new DirectPassthroughSink();
                    return new WanSessionOwner(sessionId, accountId, adapter, sink);
                });

            // For the harness, one account only. Account id is just a handle;
            // the real-world relevance is that the coordinator keys sessions
            // by account.
            var session = coordinator.EnsureSessionForAccount("harness-account");

            session.StatusChanged += (_, status) =>
            {
                Console.WriteLine($"-> status changed: {status}");
            };

            string? token = initialToken;
            bool running = true;

            while (running)
            {
                Console.Write("> ");
                var line = Console.ReadLine();
                if (line == null) break;
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var parts = line.Split(' ', 2);
                var cmd = parts[0].ToLowerInvariant();
                var rest = parts.Length > 1 ? parts[1].Trim() : "";

                try
                {
                    switch (cmd)
                    {
                        case "help":
                            PrintHelp();
                            break;
                        case "token":
                            if (string.IsNullOrEmpty(rest))
                            {
                                Console.WriteLine("usage: token <jwt>");
                                break;
                            }
                            token = rest;
                            Console.WriteLine("Token set. Next connect will send register message after session is up.");
                            break;
                        case "connect":
                            session.Connect();
                            // If a token is already set, send register once we observe Connected.
                            // For the harness we do a simple retry-poll for up to ~5s.
                            if (token != null)
                            {
                                if (WaitForConnectedBrief(session))
                                {
                                    session.ReRegister(ProgramName, Platform, token);
                                    Console.WriteLine("Register message sent.");
                                }
                                else
                                {
                                    Console.WriteLine("Still not Connected after 5s; skipping register. Try 'register' once status=Connected.");
                                }
                            }
                            break;
                        case "register":
                            if (token == null)
                            {
                                Console.WriteLine("No token set. 'token <jwt>' first.");
                                break;
                            }
                            session.ReRegister(ProgramName, Platform, token);
                            Console.WriteLine("Register message sent.");
                            break;
                        case "disconnect":
                            session.Disconnect();
                            break;
                        case "status":
                            Console.WriteLine($"SessionId:     {session.SessionId}");
                            Console.WriteLine($"AccountId:     {session.AccountId}");
                            Console.WriteLine($"Status:        {session.Status}");
                            Console.WriteLine($"IsConnected:   {session.IsConnected}");
                            Console.WriteLine($"ReconnectAttempts: {session.ReconnectAttemptCount}");
                            Console.WriteLine($"LastError:     {session.LastError?.Message ?? "(none)"}");
                            break;
                        case "drop":
                            Console.WriteLine("NOTE: 'drop' can't be simulated against the real WanServer from the harness.");
                            Console.WriteLine("      To test drop behavior, disconnect the network (airplane mode / unplug)");
                            Console.WriteLine("      and observe that Status transitions through Reconnecting.");
                            break;
                        case "list":
                            var radios = session.AvailableRadios;
                            Console.WriteLine($"AvailableRadios: {radios.Count}");
                            foreach (var r in radios)
                            {
                                Console.WriteLine($"  - {r.Nickname} / {r.Model} / serial={r.Serial}");
                            }
                            break;
                        case "reset":
                            session.Reset();
                            Console.WriteLine("Reset issued.");
                            break;
                        case "trace":
                            if (rest.Equals("on", StringComparison.OrdinalIgnoreCase))
                            {
                                Tracing.On = true;
                                Console.WriteLine("Tracing on.");
                            }
                            else if (rest.Equals("off", StringComparison.OrdinalIgnoreCase))
                            {
                                Tracing.On = false;
                                Console.WriteLine("Tracing off.");
                            }
                            else
                            {
                                Console.WriteLine($"Tracing is currently {(Tracing.On ? "on" : "off")}. Use 'trace on' or 'trace off'.");
                            }
                            break;
                        case "shutdown":
                        case "exit":
                        case "quit":
                            running = false;
                            break;
                        default:
                            Console.WriteLine($"unknown command: {cmd}. 'help' for list.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"!! {ex.GetType().Name}: {ex.Message}");
                }
            }

            Console.WriteLine("Shutting down session...");
            coordinator.Dispose();
            Console.WriteLine("Done.");
            return 0;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Commands:");
            Console.WriteLine("  token <jwt>       — set SmartLink JWT for application registration");
            Console.WriteLine("  connect           — start session (Connect)");
            Console.WriteLine("  register          — re-send register message (requires token set)");
            Console.WriteLine("  disconnect        — explicit Disconnect");
            Console.WriteLine("  status            — print session state");
            Console.WriteLine("  list              — print AvailableRadios");
            Console.WriteLine("  drop              — (advisory) pull the network to simulate a drop");
            Console.WriteLine("  reset             — full Reset() cycle");
            Console.WriteLine("  trace on|off      — toggle verbose tracing to the console");
            Console.WriteLine("  shutdown / exit   — dispose session + exit");
        }

        private static bool WaitForConnectedBrief(IWanSessionOwner session)
        {
            for (int i = 0; i < 50; i++)
            {
                if (session.IsConnected) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private static void EnableConsoleTracing()
        {
            // Wire JJTrace to emit to the console so harness users see adapter /
            // owner traces as they happen. Off by default — 'trace on' enables.
            Tracing.On = false;
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        }
    }
}

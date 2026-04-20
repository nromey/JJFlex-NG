# SmartLink Session Harness

A console REPL for exercising JJ Flex's SmartLink session stack end-to-end without the main app UI in the way. Ships with Debug builds; excluded from Release installers.

## What it's for

When a SmartLink connection misbehaves — silent drops, reconnect stalls, "I connected fine yesterday but today it won't" — the diagnostic question is almost always "is it the session layer or the UI layer?" This harness talks straight to the Sprint 26 session subsystem: `WanServerAdapter` → `WanSessionOwner` → `SmartLinkSessionCoordinator`. No WPF, no FlexBase, no field routing. If the harness connects cleanly but the main app doesn't, the problem is higher up the stack. If the harness also fails, it's a session-layer or network problem and you have direct trace visibility.

Three workflows it's built for:

1. **Field-issue reproduction.** A tester reports a SmartLink symptom. Noel runs the harness locally against the tester's account (with a JWT pulled from their trace file or crash report) to see if the symptom repros at the session layer.

2. **Pre-release smoke.** Before cutting a 4.1.x release that touched session code, run the harness through connect → drop (airplane mode) → reconnect → disconnect → shutdown. If that sequence is clean, the main app's SmartLink behavior is likely clean too.

3. **Network troubleshooting.** When Sprint 27's NetworkTest wires in, the harness gains commands to run a SendTestConnection and dump the UPnP / port-forward / NAT-hole-punch results — without needing a radio actually connected. Useful for "is your router set up correctly?" conversations.

## Where it lives

- **Source:** `tools/SmartLinkSessionHarness/` in the repo (permanent; never deleted).
- **Debug build output:** `tools/SmartLinkSessionHarness/bin/x64/Debug/net10.0-windows/SmartLinkSessionHarness.exe`.
- **Debug zip (for testers):** copied into `tools/harness/` inside the Debug zip that `build-debug.bat --publish` distributes. Testers get it automatically when they pull the debug build.
- **Release installer:** intentionally NOT included. End users don't get a REPL.

## Getting a SmartLink JWT

The harness needs a JWT to register with SmartLink. Three ways to get one:

1. **From a JJ Flex trace file.** Launch JJ Flex, sign in to SmartLink normally. Trace file at `%AppData%\JJFlexRadio\JJFlexRadioTrace.txt` contains a line like `ConnectToSmartLink: SendRegisterApplicationMessageToServer: JJFlexRadio Win10 jwt=eyJhbGc...` — copy everything after `jwt=` up to the next space.

2. **From a crash report.** If a tester sent you a crash zip, the trace inside will have the same line.

3. **Sign in with the harness itself** (future — not yet implemented). For now, use the trace-extraction path.

**⚠️ JWT security:** JWTs are bearer tokens. Don't paste them into chat / screenshots / public pastebins. Treat like a temporary password. They expire; Auth0 typically rotates them after ~24h.

## Running it

Build:

```
dotnet build tools/SmartLinkSessionHarness/SmartLinkSessionHarness.csproj -c Debug
```

Run (two forms):

```
# Interactive — paste token at the prompt
tools\SmartLinkSessionHarness\bin\x64\Debug\net10.0-windows\SmartLinkSessionHarness.exe

# Token from command-line (saves shell history has the JWT — see security note)
tools\SmartLinkSessionHarness\bin\x64\Debug\net10.0-windows\SmartLinkSessionHarness.exe --token eyJhbGc...
```

## Commands

The prompt is `> ` and reads stdin line-by-line. Commands are case-insensitive.

- **`help`** — list all commands.
- **`token <jwt>`** — set the SmartLink JWT used for registration. Must be set before `connect` is useful.
- **`connect`** — call `session.Connect()` on the coordinator's active session. If a token is set, automatically sends the register message once the session reports Connected. Status transitions stream to the console as they happen.
- **`register`** — re-send the register message (after `token` is set). Useful if registration was skipped or needs to re-fire after a token refresh.
- **`disconnect`** — explicit `session.Disconnect()`. Session enters Disconnected state and stays there.
- **`status`** — print SessionId, AccountId, Status (enum), IsConnected (bool), ReconnectAttemptCount, and LastError message.
- **`list`** — print `session.AvailableRadios` — the radios SmartLink has brokered for this account. Populated after a successful register.
- **`reset`** — `session.Reset()` — clears state and restarts the connect flow. Useful if a session got into an odd state you want to flush.
- **`drop`** — (advisory) there's no programmatic way to force a real-WanServer drop from the harness. To test drop behavior, pull the network (airplane mode / unplug / kill Tailscale) and watch Status transition through Reconnecting.
- **`trace on` / `trace off`** — toggle JJTrace's console output. When on, every `WanServerAdapter.*` call traces as it happens (prefixed with `[session=...]`). When off, only status messages print. Default: off.
- **`shutdown` / `exit` / `quit`** — dispose the coordinator cleanly and exit.

## What you'll see

Normal happy path:

```
> token eyJhbGc...
Token set. Next connect will send register message after session is up.
> connect
-> status changed: Connecting
-> status changed: Connected
Register message sent.
> list
AvailableRadios: 1
  - 6300 inshack / FLEX-6300 / serial=1624-5555-6300-0042
> disconnect
-> status changed: Disconnected
> exit
Shutting down session...
Done.
```

Disconnected-mid-session (after Connect, pull the network):

```
> status
...
Status:            Reconnecting
ReconnectAttempts: 3
LastError:         The SSL connection could not be established...
```

Auth rejection:

```
> connect
-> status changed: Connecting
-> status changed: AuthorizationExpired
Still not Connected after 5s; skipping register. Try 'register' once status=Connected.
```

## When to use (and when not to)

**Use when:**
- Reproducing a SmartLink-specific issue without UI variables.
- Pre-release session-layer smoke test.
- Asking "is the session alive at all?" before debugging higher-level symptoms.
- Capturing a clean per-call trace for a bug report.

**Don't use when:**
- The symptom is UI-shaped (announcements, menu behavior, braille display). The harness has no UI — it can't reproduce those.
- You need to test radio-side features (slice ops, DSP, tuning). The harness only exercises the SmartLink session; radio connect is brokered but radio state is not manipulated.
- A non-technical tester is the only available operator. The REPL is developer-oriented; expecting a ham with no terminal experience to run it is friction that doesn't pay off.

## Architectural mapping

For anyone reading this while debugging:

- `Program.cs:Main` — entrypoint + REPL loop.
- `coordinator` variable — instance of `SmartLinkSessionCoordinator`. Real production type, same as what JJ Flex's main app uses.
- `session` variable — result of `coordinator.EnsureSessionForAccount("harness-account")`. Real `WanSessionOwner` behind a real `WanServerAdapter` wrapping a real FlexLib `WanServer`.
- `session.StatusChanged` subscription — lets the REPL print state transitions as they happen on the monitor thread.

This is the same object graph the main app uses — there's no test double or mock. When the harness says "Connected", the SSL handshake actually happened against `smartlink.flexradio.com:443`. When it says "Reconnecting", the monitor thread's backoff loop is actually running. Confidence that the harness reflects production behavior is complete.

## See also

- `docs/planning/agile/sprint26-ragchew-keepalive-kerchunk.md` — Sprint 26 plan Phase 1 describes the session architecture the harness exercises.
- `docs/planning/hole-punch-lifeline-ragchew.md` — Living Decisions Log for the multi-sprint networking arc, including R1 (reentrant-lock choice) and R2/R3/R4 scope calls.
- `Radios/SmartLink/` — the production subsystem the harness talks to.

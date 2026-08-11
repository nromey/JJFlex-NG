# R4 trace analysis — Outcome B confirmed

**Date analyzed:** 2026-05-02
**Trace source:** Don's run on 2026-05-01 06:34 AM, archived at `round4/JJFlexRadioTrace.txt`
**Bottom line:** The .NET 10 `ReceiveAsync(token)` hypothesis (Outcome A) is **falsified**. The bug is upstream of the kernel UDP buffer. Pivot from "fix the receive loop" to "diff socket-options between FlexLib 4.1.5 and 4.2.18 Discovery.cs."

## What R4 measured

R4's diagnostic build added a 5-second synchronous receive drain on the bound discovery socket BEFORE the async receive loop starts. This was specifically to disambiguate:
- **Outcome A**: SyncDrain receives radio packets → bug is in async ReceiveAsync(token), trivial fix
- **Outcome B**: SyncDrain sees nothing → bug is upstream of kernel UDP delivery, harder fix

## What the trace shows

**SelfTest passed cleanly (all three probes received):**

```
Discovery.SelfTest: SENT probe 'loopback' -> 127.0.0.1:4992
Discovery.SelfTest: RECEIVED probe 'loopback' (len=16 from=127.0.0.1:59428)
Discovery.SelfTest: SENT probe 'nic-self-Ethernet 3' -> 192.168.1.21:4992
Discovery.SelfTest: RECEIVED probe 'nic-self-Ethernet 3' (len=16 from=192.168.1.21:59429)
Discovery.SelfTest: SENT probe 'limited-broadcast' -> 255.255.255.255:4992
Discovery.SelfTest: RECEIVED probe 'limited-broadcast' (len=16 from=192.168.1.21:59430)
Discovery.SelfTest: complete
```

The bound socket can receive UDP from loopback, NIC-self, and limited-broadcast paths. Socket is healthy.

**SyncDrain saw zero packets across all three retry cycles:**

```
Discovery.SyncDrain: starting 5000ms synchronous receive test on bound socket
Discovery.SyncDrain: complete -- received 0 total (0 from non-local sources, 0 from local sources)
```

15 cumulative seconds of synchronous receive time. Zero packets from any source.

The async loop also receives nothing in the subsequent 10-second windows. Pattern is identical across cycle 1 (0-16s), cycle 2 (24.7-39.9s), and cycle 3 (39.9-64.3s).

## Why this rules out Outcome A

Outcome A would have shown: SyncDrain successfully receives radio broadcasts, but the async loop does not. That would have implicated `ReceiveAsync(token)` specifically.

R4 shows SyncDrain ALSO seeing nothing. Sync-vs-async isn't the variable. Both receive mechanisms see zero externally-originated packets despite the socket being able to receive self-originated packets.

## Why the bug isn't where we previously suspected

The decisive grep already established that `Discovery.cs` is the ONLY file in FlexLib 4.2.18 that binds port 4992. So the "API.Init silently binds 4992 internally" hypothesis is also dead — there's no competing socket.

Yet:
- Don's 4.1.5 build receives the radio's broadcasts on the same machine, same NIC, same network, same firmware. Confirmed multiple times.
- 4.2.18 sees external broadcasts hit zero, while self-originated broadcasts (SelfTest) round-trip fine.

This narrows the bug to: **how Discovery.cs in 4.2.18 sets up the socket differently from 4.1.5 in a way that affects external broadcast delivery but not local-loopback delivery.**

## Most likely culprits to investigate next

The next investigative step is a source-level diff of `Discovery.cs` between FlexLib 4.1.5 and 4.2.18, focused on socket-option-affecting calls. Specific candidates:

1. **`UdpClient` constructor overload change.** Different overloads set different default socket options. The constructor used in 4.2.18 may default to a state that filters external broadcasts.
2. **Multicast group join.** If 4.2.18 joins a multicast group (`UdpClient.JoinMulticastGroup`), it may put the socket in multicast-only mode for external delivery while still allowing self-loopback.
3. **`MulticastLoopback` setting.** If 4.2.18 sets `MulticastLoopback = true` and we previously had it false (or vice versa), behavior differs subtly between local and external paths.
4. **`Client.SetSocketOption(IP_MULTICAST_IF, ...)`.** Binding the socket to a specific multicast interface can suppress non-multicast broadcast delivery.
5. **`Client.SetSocketOption(IP_PKTINFO, true)`.** Enabling per-packet info can change the WFP delivery path on Windows.
6. **`SO_REUSEADDR` / `ExclusiveAddressUse` change.** Different combinations can route external broadcasts to different bound sockets even when only one binds the port.
7. **Implicit / explicit `EnableBroadcast = true` change.** If 4.1.5 set this explicitly and 4.2.18 relies on a default that's different on .NET 10, we'd see the symptom.

## Source diff result (autonomous, completed 2026-05-02)

Diffed `Discovery.cs` between FlexLib 4.0.1 (main repo) and 4.2.18 (worktree HEAD). **Result: socket setup is byte-identical.** Both versions:

```csharp
udp = new UdpClient();
udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
udp.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
```

No `JoinMulticastGroup`, no `MulticastLoopback`, no `IP_MULTICAST_IF`, no `IP_PKTINFO`, no `ExclusiveAddressUse`, no explicit `EnableBroadcast`, no buffer sizing — neither version touches any of these. The socket setup hypothesis is **falsified**.

The only Discovery.cs differences between versions are:
1. `Stop()` adds `udp?.Close()` after `_loopCts.Cancel()` in 4.2.18 (cleanup improvement)
2. Receive loop uses `ReceiveAsync(token)` under `#if NET6_0_OR_GREATER` (already falsified by R4)
3. More exception types caught in receive loop (robustness)
4. Minor `Debug.WriteLine` formatting changes

None of these touch the bind path or the socket options.

**Conclusion: the bug is NOT in Discovery.cs at all.** Pivot the investigation upstream.

## Pivoted next steps

1. **Diff API.cs / API.Init between 4.0.1 and 4.2.18.** The earlier grep claimed these are byte-identical — verify that claim. API.Init runs at app startup right before Discovery.Start; if it does anything new in 4.2.18 (e.g., sends a probe packet to the radio that puts it into a state where it stops broadcasting), that would explain everything we're seeing.

2. **Diff HAAPI.cs between versions.** High-availability API path may have changed; possibly affects radio-state or network behavior at startup.

3. **Audit new files in 4.2.18** for startup side effects: `VitaPacketBase`, `HighPriorityTaskScheduler`, `MmcssPipelineScheduler`, `Filter`, `NAVTEX`, `Waveform`. Per earlier grep none touch UDP/4992, but verify they don't run startup code that affects the network stack or radio state.

4. **If all source-diff investigations come up empty:** Wireshark on Don's machine to confirm packets ARE reaching the NIC at all. If they reach the NIC but not the JJFlex socket → kernel-level WFP / AV interceptor issue. If they don't reach the NIC → upstream network state somehow caused by 4.2.18 binary.

5. **Parallel: 8600 unbox + bringup on Noel's machine.** Per `project_8600_unbox_firmware_trigger.md` — if the 8600 with new firmware works on 4.2.18, the bug is older-firmware-specific (Don's 6300 firmware version becomes the variable). Different fix shape entirely (firmware compatibility shim vs receive-mechanism fix).

## R5 build shape (conditional on the diff finding something)

If the diff identifies a candidate option, R5 is:
- Tiny diagnostic build that REVERTS the suspected option in our wrapper, plus a one-line trace marker confirming the new option state.
- Send to Don. Quick yes/no: does the radio show up in `myRadioList` now?
- If yes → strip diagnostics, commit the actual fix to `track/flexlib-42`, update MIGRATION.md, ship clean R6 to Don for soak.
- If no → the diff didn't catch it. Pivot to Wireshark on Don's machine, kernel-level investigation, or 8600 parallel test.

If the diff finds NOTHING relevant (4.2.18 socket setup is byte-identical to 4.1.5), the bug is .NET-10-runtime-specific and Wireshark / 8600 parallel becomes the only path forward.

## What's been done in this session

- Don's R4 trace archived from `for-claude/` to `round4/JJFlexRadioTrace.txt`.
- This analysis written.
- Memory entry `project_flexlib_4218_discovery_investigation.md` updated to reflect Outcome B and the new investigative direction.
- Source-diff investigation NOT yet started — will be the next autonomous step unless Noel directs otherwise.

## Reference

Archived rounds in this folder:
- R1 / R2 / R3 / R4 zips and their respective Don traces in `round2/`, `round3-firewall-test/`, `round4-r3-test/` (test instructions), `round4/` (this analysis's evidence)
- R1 evidence at top level: `JJFlexRadioTrace.txt`, `JJFlexRadioTraceOld.txt`, `NOTES-diagnostic.txt`

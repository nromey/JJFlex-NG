# API.cs + HAAPI.cs source diff — FlexLib 4.0.1 vs 4.2.18

**Date:** 2026-05-03 evening
**Investigator:** Claude (main session, end-of-day autonomous diff job)
**Trigger:** R4 + Discovery.cs source diff falsified the receive-mechanism hypothesis. Per `memory/project_flexlib_4218_discovery_investigation.md`, next investigative step was to inspect API.cs + HAAPI.cs for startup-side-effects that could affect Discovery.cs's broadcast reception.

## TL;DR

**No smoking gun in either file.** API.cs is byte-identical between 4.0.1 and 4.2.18 (`diff` exit code 0). HAAPI.cs more than doubled in size (311 → 656 lines) but the entire delta is amplifier/metering subsystem work — none of it touches sockets, UDP, network stack, or anything that could affect Discovery.cs. HAAPI is also instantiated per-radio AFTER discovery completes, so it cannot even run during discovery's listening window.

**Recommended next step:** SmartSDR ILSpy decompile per `memory/project_smartsdr_decompile_authorization.md`. The remaining FlexLib 4.2.18 internal source-diff candidates (`HighPriorityTaskScheduler.cs`, `MmcssPipelineScheduler.cs`, the `Filter`/`NAVTEX`/`Waveform` files, `VitaPacketBase.cs`) are cheaper to inspect than a full SmartSDR decompile, so doing one more lap on those *before* ILSpy is reasonable — but the SmartSDR decompile is the load-bearing move once internal-source-diff is exhausted.

## API.cs comparison

`diff "FlexLib_API/FlexLib/API.cs"` between 4.0.1 (main repo) and 4.2.18 (worktree HEAD) returned exit code 0 — **byte-identical**. The prior grep claim in the investigation memory is now confirmed by character-level comparison. No further analysis needed for this file.

## HAAPI.cs comparison

### Structural verdict

HAAPI.cs in 4.2.18 is essentially a rewrite of the High-Availability API surface — the amplifier control + metering layer used by 6700/8000-series radios with high-power TX amps. It is NOT a network-stack module. The constructor wires up state via `_radio` and `_meterList`; it never binds a socket, never sends a UDP packet, never touches `Socket`, `UdpClient`, `IPEndPoint`, or related types.

**Crucially: HAAPI is instantiated downstream of discovery**, not at app startup. The Radio constructor creates a `HAAPI` instance, and Radio objects are created from VITA-49 discovery packets. By the time a HAAPI exists, discovery has already succeeded for that radio. HAAPI cannot affect whether discovery ever happens.

### Changes in 4.2.18

**Removed (4.0.1 had, 4.2.18 doesn't):**
- `_transmit_slice` field + `_radio.PropertyChanged` subscription in constructor + `_transmit_slice_PropertyChanged` handler. The 4.0.1 HAAPI actively listened for TransmitSlice changes to drive `AmpIsSelected` (TXAnt switching between "ANT1" and "MOD"). 4.2.18 dropped this whole subsystem.
- `AmpIsSelected` property and the TXAnt-driven antenna-switching logic.
- `AmplifierFaultEventHandler(string reason)` event signature — replaced (see Added).

**Added (4.2.18 has, 4.0.1 didn't):**
- `HaapiFaultEventHandler(string noun, string reason)` — broader fault event with noun classification.
- `HaapiWarningEventHandler(string noun, string reason)` + `HaapiWarningClearedEventHandler(string noun)` — entire new warning state machine.
- `SendHaapiCommand(string)` — direct command-sending path that wraps `_radio.SendCommand`.
- `HaapiChangeMode(AmplifierMode)` + `HandleHaapiModeReply` — new mode-change pattern using `ReplyHandler`.
- `ParseWarningStatus` — new parser branch (called from `ParseStatus` for "warning" messages).
- Restructured `ParseFaultStatus` — different protocol format (`type=detection source=combiner state=FAULTED` key-value parsing) vs 4.0.1's `noun=val reason=...` parser.
- **Entire metering subsystem (~250 lines)** — new `Metering` region containing:
  - `_meterList` field (List<Meter>)
  - `FindMeterByIndex` / `FindMeterByName` lookup APIs
  - `AddMeter` / `RemoveMeter` with subscription wiring for 10 specific meter names (`lpf_fwd_pwr`, `lpf_swr`, `hv_sply_out_volt`, `hv_sply_out_current`, `hv_sply_temp`, `pa_0_temp`, `pa_1_temp`, `drv_temp`, `comb_bal_load_temp`, `comb_hpf_load_tmp`)
  - 10+ private `*_DataReady` callback handlers
  - 10+ public events (`HaapiFwdPwrDataReady`, `HaapiVswrDataReady`, etc.) and corresponding `On...` raisers
  - `MeterDataReadyEventHandler` delegate

**Behavioral changes in shared code:**
- `ParseAmplifierStatus`: 4.2.18 added `|| float.IsNaN(temp)` guards on the `frequency` and `module_gain` parses (defensive against NaN injection from radio).
- Constructor: 4.0.1 subscribes to `_radio.PropertyChanged`; 4.2.18 does not. Constructor now just sets `_radio` and `_meterList = new List<Meter>()`.

### Network-stack relevance — none

Searched the 4.2.18 HAAPI.cs for every network primitive that could interact with Discovery.cs:
- No `using System.Net` or `using System.Net.Sockets`.
- No `UdpClient`, `Socket`, `IPEndPoint`, `IPAddress`, `EnableBroadcast`, `JoinMulticastGroup`, `SetSocketOption`, `Bind`.
- No threads spawned, no `Task.Run`, no timers, no `ThreadPool` interaction.
- No interaction with the OS-level networking stack (no WMI, no `NetworkInterface`, no firewall APIs).
- No static constructors with side effects.
- No field initializers that allocate sockets or anything network-relevant.

The metering subsystem subscribes to `Meter.DataReady` events but `Meter` is a per-radio object that exists only after discovery + connection succeed. Cannot run during discovery.

## Startup side-effect candidates (ranked) — none in scope

Both files are ruled out:

1. **API.cs** — byte-identical, no possible behavior delta.
2. **HAAPI.cs** — large delta but entirely in amplifier/metering domain, no network-stack touch, instantiated per-radio post-discovery so cannot affect discovery timing or socket state.

## Verdict

**Bug is not in API.cs or HAAPI.cs.** Both can be removed from the suspect ranking in `memory/project_flexlib_4218_discovery_investigation.md`. The remaining suspects in the memory's "ranked suspects" section all stand:

1. **Other FlexLib 4.2.18 startup files** that haven't been individually inspected — `HighPriorityTaskScheduler.cs`, `MmcssPipelineScheduler.cs`, `VitaPacketBase.cs`, plus `Filter` / `NAVTEX` / `Waveform` files. The earlier grep ruled these out for UDP/socket touches but didn't verify whether any have static constructors, app-domain-load side effects, or task-scheduler replacements that affect UDP receive timing. **Should be inspected before going to SmartSDR ILSpy.**
2. **.NET 10 runtime networking-stack change** — would need Wireshark to confirm.
3. **SmartSDR ILSpy decompile** — definitive comparison against a known-working reference implementation. Authorized per `memory/project_smartsdr_decompile_authorization.md`. Highest cost but highest signal.

## Recommended sequence for tomorrow's session

1. **30-min pass** through `HighPriorityTaskScheduler.cs`, `MmcssPipelineScheduler.cs`, `VitaPacketBase.cs` in 4.2.18. Look for static constructors, app-startup side effects, anything that replaces the default task scheduler globally. These three are the highest-suspicion remaining internal candidates.
2. **If those are clean:** install SmartSDR Plus on Noel's machine + ILSpy decompile of its discovery code path. Diff against FlexLib 4.2.18's Discovery.cs + the surrounding startup code we've now inspected. The SmartSDR install also serves the firmware-update Phase D investigation per the cross-reference in the decompile-authorization memory.
3. **In parallel with either above:** if Noel unboxes the 8600 with new firmware, run R4 build against it. If 8600 with new firmware is discovered fine, the bug is older-firmware-specific and the install-strategy concern in `project_firmware_install_dependency_strategy.md` becomes load-bearing for 4.2.0.

## Files inspected

- `C:\dev\jjflex-ng\FlexLib_API\FlexLib\API.cs` — 336 lines, FlexLib 4.0.1
- `C:\dev\jjflex-flexlib-42\FlexLib_API\FlexLib\API.cs` — 336 lines, FlexLib 4.2.18 — byte-identical
- `C:\dev\jjflex-ng\FlexLib_API\FlexLib\HAAPI.cs` — 311 lines, FlexLib 4.0.1
- `C:\dev\jjflex-flexlib-42\FlexLib_API\FlexLib\HAAPI.cs` — 656 lines, FlexLib 4.2.18 — full read, no network-stack touches

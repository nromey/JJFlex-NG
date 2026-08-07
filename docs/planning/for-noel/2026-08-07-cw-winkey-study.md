# WinKey protocol study — K1EL WinKeyer USB / WK3 chip

Research memo for the CW rewrite design round. Fired from `docs/planning/active/research-queue.md` (agent research, 2026-08-07). Companion memos: `aethersdr-cw-review.md`, `cw-decode-survey.md`. Feeds `project_cw_keying_design.md`.

Date: 2026-08-07. Sources: WK3.1 IC Interface & Operation Manual Rev 1.3 (hamcrafters2.com, full protocol reference), K1EL/Hamcrafters product pages, N1MM Logger+ docs, FlexRadio Community threads (WKFlex, RKI, Maestro CW latency), plus JJFlex-NG source (`FlexLib_API/FlexLib/CWX.cs`, `Radio.cs` CW methods). Full source list at the end.

## Executive summary

- WinKey is the de facto hardware CW-keying standard in ham software. The K1EL WK3/WK3.1 chip buffers ASCII from the host over a simple serial link and generates crystal-accurate Morse in hardware, immune to Windows timing jitter. Nearly every logger speaks the protocol, and FlexRadio's own SmartSDR CAT ships a WinKeyer *emulation* port — the protocol is a lingua franca, not just a device.
- The critical protocol fact for us: **paddle activity is never reported as element-level (dit/dah up/down) events over serial.** The host sees a BREAKIN status bit and, optionally, one ASCII character echoed *after* each character completes. Element timing lives entirely inside the chip and its hardware KEY output pin.
- That shapes the integration answer: WinKey can drive a remote Flex **per character** through its serial protocol (paddle echo → CWX, the proven WKFlex model), or **per element** only by hardware-sensing its KEY output pin on a second serial line (the RKI/xKEY model, forwarding via FlexLib's timestamped `CWKey()` network stream). Both give the operator zero-latency *local hardware sidetone* — the single feature that makes remote paddle CW workable at all, and doubly so for a blind op who has no waterfall and lives by the sidetone.
- Hardware is alive and well in 2026: WKUSB Rev C ($144 assembled — pot, pushbuttons, battery standalone, sidetone speaker), WKmini ($64, bare-bones), WK3.1 ICs for homebrew, Kanga UK for EU/UK, plus a large installed base and open-source protocol emulators (K3NG). No availability risk.
- Verdict: **complementary, not redundant** to the planned keyboard iambic/straight/bug engine. Same radio-side output primitives (CWX character path, NetCW element path), different front ends. Build the radio-side CW output abstraction once; hang the keyboard engine, WinKey host support, and (later) a WinKey emulation server for loggers off of it.

## What WinKey is

WinKey is a single-chip Morse keyer (currently WinKeyer 3.1) from K1EL Systems / Steve Elliott. The design premise, straight from the datasheet: multithreaded Windows cannot generate accurately timed Morse, so the PC sends ASCII and configuration over serial, and the chip translates to Morse and keys the transmitter directly. A paddle input lets the operator break in and send by hand at any time; a speed-pot input gives instant tactile speed control. The chip also runs standalone (no PC), with its own stored messages and paddle-command configuration.

The WKUSB product wraps the chip with a USB interface (CH340 virtual COM), speed pot, four message pushbuttons, sidetone speaker, AAA-battery standalone power, and two optically isolated KEY/PTT output pairs for driving two rigs.

## The serial protocol

### Transport and session

- 1200 baud, 8 data bits, 2 stop bits, no parity, no flow control. Always powers up at 1200; an admin command switches to 9600, reverting on close/reset. Byte cost at 1200 baud: 11 bits ≈ 9.2 ms per byte.
- Power-up state is standalone mode. Host mode begins with Admin Host Open (`0x00 0x02`); the chip answers with its firmware revision byte, and the host must wait for it before sending anything else. Admin Host Close (`0x00 0x03`) returns the unit to standalone (so the same paddle keeps working when the app exits — a nice resilience property). Physical disconnect also drops it back to standalone automatically.
- After open, hosts normally push a 15-byte Load Defaults block (`0x0F`) to sync every parameter in one shot: mode register, WPM, sidetone frequency, weight, PTT lead-in and tail, pot min/range, X2 mode, key compensation, Farnsworth WPM, paddle switchpoint, dit/dah ratio, pin config, X1 mode.
- Admin sub-commands also select WK1/WK2/WK3 compatibility personas (progressively richer status reporting and mode registers), dump/load the 256-byte EEPROM, trigger stored messages, read supply voltage, and set sidetone volume.

### Command classes

Three kinds of host-to-keyer traffic:

- **Immediate commands** (`0x01`–`0x17`) bypass the buffer and take effect at once: set WPM speed (`0x02`, 5–99; zero means "follow the speed pot"), weighting, PTT lead/tail, speed-pot min/range, pause, get pot value, backspace the buffer, pin configuration (which KEY/PTT ports are live, sidetone enable, ultimatic priority, paddle hang time), clear buffer, key-immediate for tune (`0x0B`, with a hard-coded 100-second watchdog), HSCW, Farnsworth, the main mode register (`0x0E`), first-element extension, keying compensation, paddle switchpoint, software paddle (`0x14`), status request (`0x15`), buffer-pointer manipulation (`0x16`, used for on-the-fly callsign correction), and dit/dah ratio.
- **Buffered commands** (`0x18`–`0x1F`) queue in-line with text and fire in position: PTT on/off, timed key-down, wait N seconds, merge-two-letters into a prosign, buffered speed change (with cancel), buffered HSCW, NOP. This is how loggers embed mid-message speed bumps ("5NN" faster than the exchange) and custom prosigns.
- **Data bytes** (ASCII `0x20` and up) go into a 160-character FIFO and become Morse. Punctuation maps to prosigns (`+` is AR, `=` is BT, `>` is SK, etc.); the `|` character inserts a half-dit pad.

The mode register (`0x0E`) is where the keying personality lives: keyer mode (iambic B, iambic A, ultimatic, bug — bug mode doubles as straight-key pass-through on the dah input), paddle swap, autospace, contest word spacing, paddle watchdog, and the two echo enables described next.

### Keyer-to-host traffic — what the host can actually know

The chip talks back in a loosely coupled, unsolicited style. The datasheet is explicit that the host must run a receive loop classifying each byte by its top bits, never blocking on a response (worst-case response latency to a status/pot request: 200 ms):

- **Status byte** (`0b11xxxxxx`): WAIT (internal timed event), KEYDOWN (tune), BUSY (sending), **BREAKIN** (paddle break-in active), XOFF (buffer over two-thirds full). In WK2/WK3 persona, a variant carries **pushbutton press/release states** for the four buttons.
- **Speed pot byte** (`0b10xxxxxx`): 6-bit value, sent unsolicited whenever the pot moves, windowed to a host-configured min/range across 32 steps.
- **Echo bytes** (plain ASCII): with Serial Echoback on, each character sourced from the host is echoed *after* it is fully sent — this is how loggers track sending progress in real time. With **Paddle Echoback** on, characters the operator sends *by paddle* are also echoed, again only after the character completes. The host tells the two apart by whether BREAKIN was set when the echo arrived.

**What is not reported: element-level paddle events.** No dit/dah/key-up/key-down transitions ever cross the serial link. Paddle input takes priority in hardware — it interrupts serial sending and clears the buffer — but the host learns of it only via BREAKIN and completed-character echoes. Any design that needs true element timing must tap the chip's hardware KEY output pin, not the protocol.

The reverse direction exists, amusingly: Software Paddle (`0x14`) lets the host assert virtual paddle lines (up/dit/dah/both), imagined for keyboard-as-paddle. The datasheet itself warns that keyboard-to-serial-to-chip latency makes this "a challenge above 20 WPM" — a useful vendor-confirmed data point for our own keyboard-engine latency budget, and a strong argument for keeping our keyboard engine's element generation host-side rather than proxying through a WinKey.

## How loggers integrate it

- N1MM Logger+, DXLab (WinWarbler), Win-Test, Logger32, N3FJP, HRD/DM780, TR4W and essentially every contest/logging package support WinKey natively. In N1MM you pick the COM port, check "CW/Other," and check the WinKey box — the docs specifically say *not* to configure DTR/RTS by hand; the WinKey checkbox handles the line states. The logger feeds function-key macros as ASCII; the keyer times the Morse; serial echo drives the logger's "sent so far" display; the pot can drive the logger's speed or vice versa.
- **FlexRadio's SmartSDR CAT ships a "Winkeyer" port type** — a software emulation of this exact protocol. Loggers think they are talking to a WKUSB; CAT translates to radio commands (the CWX path). That FlexRadio chose WinKey emulation as *the* way to let loggers key their radios is the strongest possible endorsement of the protocol's lingua-franca status — and precedent for JJFlex doing the same.
- **WKFlex** (Max N5NHJ and Dave N1BIT) closes the remote loop the other way: it tunnels a *physical* local WinKeyer into SmartSDR CAT's virtual WinKeyer port, so a remote op sends with real paddles, hears the WKUSB's local hardware sidetone with zero latency, and the radio keys per character. Reported wrinkle: at higher speeds users need a "speed offset" (running the WinKeyer faster than the radio's CWX speed) to keep character echoes arriving ahead of the radio's sending and avoid inter-letter gaps. WKmini can't do this trick well since it has no pot. This is the exact character-granularity ceiling predicted by the protocol analysis above.
- **RemoteKeyerInterface** (RKI, NQ6N) and **xKEY** (DL3LSM, macOS) take the element route: the local keyer's KEY output (or a bare straight key) is wired to a serial-port control line on the PC, and the app forwards key-down/key-up transitions to the Flex over the radio API, with sidetone generated locally (by the keyer or the PC). RKI's docs call the WKUSB "the optimal choice" as the local keyer precisely because of its built-in sidetone and PTT. Maestro uses the same API path; community measurements put keying RTT around 72 ms typical, rising to 130–140 ms under load — usable with local sidetone, punishing without it.

## Hardware availability in 2026

- **WKUSB Rev C** — current, $144, fully assembled and tested only (kits discontinued). Speed pot, four message pushbuttons, internal 3×AAA battery for standalone, onboard sidetone speaker, two optically isolated KEY/PTT pairs, CH340 USB-serial. This is the model that matters for us: the pot, buttons, speaker, and standalone mode are the accessibility payload.
- **WKmini** — $64 ($75 with cables), same WK3.1 IC in a tiny case; no pot, no battery/standalone, no pushbuttons, no sidetone speaker; speed comes from the host. Fine as a logger dongle; much weaker for the blind-op use cases below.
- **WK3.1 IC** sold separately (DIP and SMT) for homebrew and third-party products; a serial (non-USB) board kit also exists.
- K1EL cannot currently sell direct to the EU/UK; **Kanga UK** is the authorized distributor there. The used market is deep (two decades of WK1/WK2/WK3 units), and the open-source **K3NG Arduino keyer emulates the WinKey protocol**, so the protocol outlives any single piece of hardware. Bottom line: zero availability risk; recommending "get a WKUSB" to a tester is a same-week proposition.

## Value for a blind, screen-reader-first operator

This is where WinKey stops being a contester's convenience and becomes an accessibility device:

- **Local hardware sidetone with zero latency.** A blind op has no waterfall and no TX meter glance — the sidetone *is* the TX feedback. Over SmartLink, radio-generated sidetone returns through network audio 100–300 ms late, which makes paddle sending physically impossible. A WKUSB's speaker closes the loop at the operator's fingers regardless of network state. This is the killer feature and the reason WKFlex/RKI exist.
- **A physical speed knob.** Instant, tactile, eyes-free speed control with unsolicited pot reports the app can announce ("22 WPM") and mirror into CWX speed. No dialog, no spin control, no focus dance. This aligns exactly with the friction-tax principle.
- **Physical message buttons.** Four pushbuttons whose press/release states are reported to the host in WK2/WK3 persona — JJFlex could bind them to CWX macros or app actions, giving a blind contester hardware F-keys. Standalone message playback also works with the PC off.
- **Standalone survivability.** If the PC, JJFlex, or the screen reader crashes mid-QSO, the keyer drops back to standalone mode and the paddle keeps working (battery-powered, even). For a local rig that means the QSO survives; the psychological value of "the key never dies with the software" is real.
- **No UI of its own to be inaccessible.** WinKey is a protocol, not an app. Every knob it exposes is set over serial, so JJFlex can own 100% of the configuration surface in its own accessible dialogs. The op never needs K1EL's WK3tools utility (whose accessibility we haven't assessed and don't need to).
- **Echo satisfies the no-silent-keystrokes rule for the paddle.** Paddle echoback means JJFlex can announce or log what the operator *actually sent* by hand — screen-reader confirmation of outgoing CW, something even sighted SmartSDR users don't get.

## Integration feasibility for JJFlex

### .NET serial handling

Trivially feasible. `System.IO.Ports.SerialPort` (Microsoft's package on `net10.0-windows`) at 1200-8-N-2 with a `DataReceived` handler and a one-byte classifier state machine per the datasheet's own pseudo-code. JJFlex already has exactly this pattern in-tree: `C:\dev\JJFlex-NG\JJFlexControl\Serial.cs` (FlexControl knob: `SerialPort`, `BaudRate`, `DataReceived`) and `C:\dev\JJFlex-NG\JJW2WattMeter\Serial.cs`. The protocol's loosely coupled design (never wait for a reply; classify unsolicited bytes) maps cleanly onto an event-driven reader. Byte budget is comfortable: ~9.2 ms/byte at 1200 baud, ~18 ms for a two-byte command, and the optional 9600-baud mode cuts that by 8x if it ever matters. CH340 driver installation on the op's PC is the only friction point worth documenting.

### The FlexLib side (verified against our vendored FlexLib 4.1.5)

- **Character path:** `CWX` (`FlexLib_API/FlexLib/CWX.cs`) — `Send(string)`, `Send(string, block)` with queued/sent callbacks, `Insert`, `Erase`, `ClearBuffer`, twelve stored macros, `Speed` 5–100 WPM, `Delay` (break-in delay 0–2000 ms), `QskEnabled`, and per-character `CharSent` progress events. The radio times the Morse; network latency only delays the start.
- **Element path:** `Radio.CWKey(bool state, string timestamp, ...)` and `Radio.CWPTT(...)` (`Radio.cs` ~line 9643–9722) ride the dedicated network CW stream (`_netCWStream`), sending each transition with a timestamp and sequence index — and FlexLib fires each command four times over ~15 ms for UDP loss-resilience. The radio jitter-buffers against the timestamps. This is the Maestro path. `Radio.CWKeyImmediate(bool)` is the simple non-timestamped variant (tune-style).

### Three viable architectures, in ascending ambition

- **Architecture 1 — WinKey host, character bridge (the WKFlex model, minus the middleman).** JJFlex opens the WKUSB, enables paddle echoback, and forwards each echoed character to `CWX.Send()`; pot bytes sync `CWX.Speed` (and get announced); BREAKIN triggers `CWX.ClearBuffer()` so the paddle interrupts buffered sends remotely, mirroring the chip's own paddle-priority semantics. Op hears the WKUSB's local sidetone. Known ceiling: one-character latency and possible inter-letter gaps at speed (mitigate with the WKFlex speed-offset trick — run the WinKey a few WPM above CWX). Small, self-contained, and immediately valuable to Don-style SmartLink operators who own WinKeyers. This is the recommended first build.
- **Architecture 2 — element bridge (the RKI/xKEY model).** Sense the WKUSB's KEY output on a serial control line (CTS/DSR via a trivial cable) and forward transitions through `CWKey()` with timestamps. True element fidelity, QSK-capable on a LAN, degrades gracefully over WAN because sidetone is still local. Notably, this path doesn't require the WinKey *protocol* at all — any keyer or straight key works — but the WKUSB remains the recommended hardware because it supplies the iambic engine and sidetone. The NetCW plumbing built here is the same plumbing the future keyboard engine's Flex output needs, so this work is shared, not throwaway. Watch item: `SerialPort.PinChanged` latency/jitter on Windows needs a measurement pass; a tight read thread may beat the event API.
- **Architecture 3 — WinKey emulation server (JJFlex as the virtual WKUSB).** JJFlex exposes a WinKey-protocol port on one end of a virtual COM pair (com0com et al.) and translates to CWX, exactly as SmartSDR CAT does. Every WinKey-speaking logger — N1MM first among them — can then key the Flex *through JJFlex* with no SmartSDR running. For blind contesters pairing N1MM with JJFlex, this is the single highest-leverage integration on the list. It reuses Architecture 1's protocol tables from the other side. Sequence it after the CW engine round settles.

### Latency summary

- WinKey serial hop: single-digit to low-double-digit milliseconds — negligible.
- Character bridge end-to-end: one character time (e.g., ~300–500 ms for a mid-length character at 25 WPM) plus network; masked entirely by local sidetone, felt only as turnaround lag.
- Element bridge: network RTT bound (~72–140 ms reported on the Maestro path); fine for LAN QSK ambitions, WAN-usable with local sidetone.
- Neither path is gated by .NET performance; the constraints are physics (WAN RTT) and protocol shape (character granularity), not our stack.

## Complementary or redundant to the planned keyboard keying engine?

**Complementary.** The overlap is only apparent:

- The planned engine (keyboard/gamepad/touchscreen iambic, straight, bug — per `project_cw_keying_design.md`) is the **zero-hardware** input path, and its element generation must live host-side anyway (WinKey's own Software Paddle command is vendor-documented as failing above ~20 WPM, which independently validates that decision).
- WinKey support is the **hams-already-own-this** input path, contributing three things software cannot: real paddles with a real iambic feel, a hardware speed pot, and zero-latency hardware sidetone that survives app death.
- Both terminate in the same two radio-side primitives — CWX for character-granular sending, the NetCW `CWKey()` stream for element-granular sending. Design consequence: **build one radio-side CW output abstraction with two granularities, and treat keyboard engine, WinKey host, and WinKey emulation as front ends.** No code in the keyboard engine should know whether a WinKey exists, and vice versa.
- Do *not* adopt the WinKey protocol as the internal engine interface — its character-only reporting is exactly the constraint our element-capable engine must not inherit. It is an integration target, not an architecture template.
- Per the standing design decision, a *local* Flex still prefers a paddle plugged into the radio's own key jack; WinKey's sweet spot is remote operation (Don over SmartLink) and logger integration — which is precisely where JJFlex's users live.

## Open questions and risks

- Port exclusivity: one app owns the physical WinKeyer at a time. If an op runs N1MM and JJFlex together, who opens the hardware? (Emulation mode dissolves this: N1MM talks to JJFlex's virtual WinKey, JJFlex owns the hardware.)
- WKmini owners get no pot — JJFlex UI must be the speed source there (already true of CWX today, so no new work, just a capability note).
- CWX prosign/character-set mapping vs WinKey's table (`+` AR, `=` BT, etc.) deserves a test pass so echoed paddle characters round-trip correctly.
- The WKFlex speed-offset behavior suggests our character bridge should expose a tunable WinKey-vs-CWX speed delta rather than hard-locking the two.
- `SerialPort.PinChanged` timing fidelity on Windows (Architecture 2) needs empirical measurement before we promise QSK numbers.
- CH340 driver installation is a small onboarding friction for Rev C hardware; document it in help.

## Sources

- WK3.1 IC Interface & Operation Manual Rev 1.3 (K1EL/Hamcrafters): https://www.hamcrafters2.com/files/WK3_Datasheet_v1.3.pdf
- WKUSB product page (Rev C, $144, features, EU/UK note): https://www.hamcrafters2.com/WKUSBX.html
- WKmini product page ($64/$75, feature deltas): https://hamcrafters2.com/WKminiRevB.html
- WKUSB User Manual v1.4: https://www.hamcrafters2.com/files/WKUSB_Manual_v1.4.pdf
- N1MM Logger+ interfacing docs (WinKey checkbox, DTR/RTS guidance): https://n1mmwp.hamdocs.com/getting-started/interfacing-basics/ and https://n1mmwp.hamdocs.com/setup/the-configurer/
- K1EL's own N1MM+WKUSB setup note: https://www.hamcrafters2.com/files/N1MM_help.pdf
- WKFlex — CW remote with SmartSDR and Winkeyer (N5NHJ/N1BIT, FlexRadio Community): https://community.flexradio.com/discussion/8024988/wkflex-cw-remote-with-smartsdr-and-winkeyer/p1
- RemoteKeyerInterface (NQ6N): https://groups.io/g/RemoteKeyerInterface
- xKEY remote keying for Flex (DL3LSM): https://dl3lsm.blogspot.com/2020/04/introducing-xkey-remote-keying.html
- Maestro CW keying latency thread (72 to 130–140 ms RTT): https://community.flexradio.com/discussion/7914038/remote-latency-rtt-change-with-maestro-cw-keying
- SmartSDR CAT WinKeyer emulation discussions: https://community.flexradio.com/discussion/6969549/winkeyer-emulation
- Kanga UK (EU/UK distribution): https://www.kanga-products.co.uk/k1el-keyer-ics
- JJFlex-NG source verified: `FlexLib_API/FlexLib/CWX.cs`; `FlexLib_API/FlexLib/Radio.cs` (`CWKey`, `CWPTT`, `CWKeyImmediate`, ~lines 9643–9727); `JJFlexControl/Serial.cs`

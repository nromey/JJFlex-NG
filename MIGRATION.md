# FlexLib upgrade/Migration notes

This app carries a small, non-breaking shim to enforce TLS 1.2+ without editing vendor FlexLib sources. When you drop in a newer FlexLib, reapply only the items below.

## What to reapply after upgrading FlexLib
1. **Keep the TLS wrapper**: Copy `FlexLib_API/FlexLib/SslClientTls12.cs` into the new FlexLib `FlexLib` folder (adjust the path if the vendor layout changes). This wrapper enforces TLS 1.2/1.3 and mirrors the stock `SslClient` API.
2. **Point TLS command transport to the wrapper**: In `FlexLib/FlexLib/TlsCommandCommunication.cs`, replace the `SslClient` field/constructor with `SslClientTls12` (the only code edit inside the vendor tree).
3. **Prefer TLS when available**: In `FlexLib/FlexLib/Radio.cs`, keep the small change that chooses `TlsCommandCommunication` whenever `IsWan` is true **or** `PublicTlsPort > 0`. This avoids plaintext when the radio exposes a TLS port.
4. **App-wide TLS floor**: Ensure `ApplicationEvents.vb` is included in the project. It sets `ServicePointManager.SecurityProtocol = Tls12 Or Tls13` at startup so any `HttpWebRequest`/HTTPS calls use modern TLS.
5. **Discovery.Receive race fix (added 2026-04-15)**: In `FlexLib_API/FlexLib/Discovery.cs`, keep the patched `Receive()` method that (a) captures a local `UdpClient` reference at entry, (b) null-guards on entry, (c) catches `ObjectDisposedException` and `SocketException` around `ReceiveAsync`, and (d) only nulls the static `udp` field if it still points at the captured local. Stock FlexLib NREs at line 75 when `Discovery.Start()` fires a second time before a prior `Receive` task has fully terminated (trigger: `apiInit(force=true)` via `LocalRadios()`). Our patch also adds `Debug.WriteLine` traces at task start/exit for future race visibility. See `flexlib-discovery-nre-report.txt` at repo root for the full write-up (reportable upstream to Flex). Remove this patch if/when a FlexLib release ships its own fix.
6. **Firmware short-read patch — 4.1.x ONLY, obsolete in 4.2.x**: 4.1.x's `Private_SendUpdateFile` in `Radio.cs` called `stream.Read(buffer, 0, length)` once and ignored the return value; a short read would stream a partially-zero firmware image. Our patch (commit `7c93c7f8`) looped until full. **4.2.x rewrote firmware upload as `public async Task SendUpdateFile` using `fileStream.CopyToAsync(tcpStream)` — no buffer, no unchecked read — so the patch is structurally unnecessary there.** If you ever downgrade back to a 4.1.x tree, re-apply it. Note the 4.2.x signature change (`void` → `async Task`): `FlexBase.BeginFirmwareUpdate` attaches an `OnlyOnFaulted` continuation to trace transfer errors; completion is still verified via discovery polling (`WatchFirmwareUpdateAsync`), never via the task. (The same unchecked-read pattern still exists in 4.2.x for waveform/turf/database import — not on the firmware path, left as vendor-stock.)
7. **Do NOT carry diagnostic patches forward**: the R5 diagnostic (`Vita/MmcssPipelineScheduler.cs` redirecting `Instance` to `TaskScheduler.Default`) was a silent-discovery experiment that disables MMCSS real-time audio scheduling. It must be vendor-stock in anything shipped. The R6 discovery-cascade + instrumentation in `Discovery.cs` IS kept (Debug.WriteLine compiles out of Release).
8. **VitaSocket UDP resilience patch (added 2026-08-05)**: In `FlexLib_API/Vita/VitaSocket.cs`, keep four JJFlex patches (all comment-marked): (a) the `SIO_UDP_CONNRESET` ioctl in the base constructor — suppresses Windows' behavior of throwing `SocketException(ConnectionReset)` on the next Send/Receive after an ICMP port-unreachable echo; (b) send methods (`SendUdp`/`SendUdpAsync`) log-and-continue instead of `Dispose()` on exception, and early-return when `_radioEndpoint` is null; (c) `ReceiveLoop` treats `ConnectionReset` as a non-event and only disposes after 50 consecutive receive failures; (d) the static `TraceSink` hook. Vendor 4.2.x stock code called `Dispose()` from ALL of these catch sites, so a single ICMP bounce during the WAN hole-punch race silently killed the entire UDP data plane (no audio, no meters, `PersistenceLoaded` never set, `start_call` timeout ~34-54s — the 2026-08-05 field-test signature). Companion edits: `FlexLib/Radio.cs` `UdpRegistrationLoop` traces loop start + first success through `VitaSocket.TraceSink` (JJFlex-patch-marked); `Radios/FlexBase.cs` constructor wires `TraceSink` to `Tracing.TraceLine`. Reportable upstream to Flex. Remove (a)-(c) if a FlexLib release ships its own fix; keep the TraceSink wiring regardless.

## Upgrade procedure that worked for 4.2.18 → 4.2.20 (2026-08-03)

Rather than a fresh vendor copy + manual patch reapply, use git for a 3-way merge per changed file:
1. Branch off `main`; `git checkout track/flexlib-42 -- FlexLib_API/` to pull the already-patched 4.2.18 tree (never merge `main` INTO that branch — main carries the revert and silently deletes the drop).
2. For each vendor-changed file we also patched (find them by diffing pristine drops **with line endings normalized** — repo objects are LF, vendor zips are CRLF): `git merge-file ours base theirs` with base = pristine old vendor (commit `92385f88` for 4.2.18), ours = patched tree, theirs = new vendor file. 4.2.18→4.2.20 changed only `Radio.cs` (clean merge, zero conflicts) and `RXAudioStream.cs` (never patched, blind copy).
3. Re-check items 1–7 above, restore `MmcssPipelineScheduler.cs` to vendor-stock, build, and verify `Radio.cs` line counts: 4.1.5 = 14,471; 4.2.18 patched = 15,212; 4.2.20 merged = 15,268 (`wc -l`, CRLF or LF both count the same).

Tip: After an upgrade, run a build and verify a remote connect; the wrapper logs the negotiated protocol so you can confirm TLS 1.2+.

## About older FlexLib v3
Legacy v3 folders have been removed to keep the repo/installer lean. The app builds against `FlexLib_API`.

## Installer size
The installer is larger than older JJ versions because the repo now includes multiple FlexLib versions, extra dependencies, and more binaries. Cleaning out unused legacy folders (e.g., old FlexLib versions) will reduce package size, but keep the current v4 FlexLib and required runtime files.

## Current modernization status (Nov 2025)
- **Native deps refreshed:** Opus 1.5.2 (libopus.dll) and PortAudio v19.7.0 (portaudio.dll) built for x86/x64 and staged under `bin/Release`, `bin/Release/x86`, `bin/Release/x64`.
- **Wrappers updated:** `P-Opus-master` and `PortAudioSharp-src-0.19.3` converted to SDK-style projects targeting `net48;net8.0-windows` with x86/x64 platforms.
- **Main app migration in progress:** `JJFlexRadio.vbproj` converted to SDK-style `net48;net8.0-windows`, WinForms, x86/x64. Post-build installer runs only for `Release|x86|net48` to preserve legacy packaging while we modernize.
- **Known warnings:** net8 builds of FlexLib/UiWpfFramework emit reference-unification warnings (WindowsDesktop ref packs) but build completes.
- **Radios library trimmed to Flex-only:** `AllRadios.RigTable` now exposes Flex entries only (`FlexRadio : FlexBase`), discovery uses FlexLib `API.RadioAdded` directly in AllRadios (no more Kenwood/Icom/Elecraft stubs). Build passes with the flex-only surface.
- **Rig selector:** Flex-only entries; Remote/Login (SmartLink) re-enabled and call the existing FlexBase SmartLink flow. If SmartLink needs further hardening, disable again and note here.

## Next steps
- Update installer/packaging to include both x86 and x64 payloads (arch-detect or dual installers).
- Ensure net8 builds copy the correct arch-specific native DLLs into output/publish.
- Consider dropping net48 targeting once net8 is verified end-to-end.
- Re-introduce SmartLink/manual entry UI on top of FlexBase if remote WAN access is required.

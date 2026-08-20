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
8. **Version-stamp the new drop (added 2026-08-16)**: In `FlexLib_API/FlexLib/FlexLib.csproj`, update `<Version>` and `<InformationalVersion>` to the new vendor version (e.g. `4.2.20.41343`). The About page, crash reports, and debug bundles all report FlexLib's version by querying the built DLL at runtime — the stamp is what makes that query honest. The csproj sat at `0.0.0.0` from the SDK-style conversion until 2026-08-16, so every FlexLib.dll built in between claims no version at all. If the stamp is ever forgotten, the About page will loudly report the missing stamp rather than a version.
9. **VitaSocket UDP resilience patch (added 2026-08-05)**: In `FlexLib_API/Vita/VitaSocket.cs`, keep four JJFlex patches (all comment-marked): (a) the `SIO_UDP_CONNRESET` ioctl in the base constructor — suppresses Windows' behavior of throwing `SocketException(ConnectionReset)` on the next Send/Receive after an ICMP port-unreachable echo; (b) send methods (`SendUdp`/`SendUdpAsync`) log-and-continue instead of `Dispose()` on exception, and early-return when `_radioEndpoint` is null; (c) `ReceiveLoop` treats `ConnectionReset` as a non-event and only disposes after 50 consecutive receive failures; (d) the static `TraceSink` hook. Vendor 4.2.x stock code called `Dispose()` from ALL of these catch sites, so a single ICMP bounce during the WAN hole-punch race silently killed the entire UDP data plane (no audio, no meters, `PersistenceLoaded` never set, `start_call` timeout ~34-54s — the 2026-08-05 field-test signature). Companion edits: `FlexLib/Radio.cs` `UdpRegistrationLoop` traces loop start + first success through `VitaSocket.TraceSink` (JJFlex-patch-marked); `Radios/FlexBase.cs` constructor wires `TraceSink` to `Tracing.TraceLine`. Reportable upstream to Flex. Remove (a)-(c) if a FlexLib release ships its own fix; keep the TraceSink wiring regardless.

10. **Package version bumps inside the vendor tree (added 2026-08-17)**: the
    dependency-currency pass raised four NuGet references that live in
    `FlexLib_API` csproj files, so a fresh vendor drop will revert them to
    whatever Flex shipped. Re-apply: `Newtonsoft.Json` 13.0.3 → **13.0.4** and
    `AsyncAwaitBestPractices` 9.0.0 → **10.0.0** in `FlexLib/FlexLib.csproj`;
    `QRCoder` and `QRCoder.Xaml` 1.6.0 → **1.8.0** in
    `UiWpfFramework/UiWpfFramework.csproj`; `RestSharp` 112.1.0 → **114.0.0** in
    `Util/Util.csproj`. RestSharp 114's breaking changes do not reach us — its
    only consumer is `Util/BigCommerce.cs`, FlexRadio's store API, which
    implements no `IAuthenticator` and touches no redirect options. **Left
    deliberately unpatched:** the six `NU1510` warnings from
    `System.Collections.Immutable`, `System.ValueTuple`, `System.Text.Json` and
    `System.Runtime.CompilerServices.Unsafe` references in `FlexLib`,
    `UiWpfFramework` and `Util`. The net10.0 shared framework supplies all four,
    so the references should be *deleted* rather than bumped — but that is a
    structural edit to vendor files for warning hygiene alone, and the trade
    wants a decision rather than a drive-by. The equivalent references in our
    own projects were removed on 2026-08-17.

11. **Public accessor for the meter list (applied 2026-08-19, Sprint 32 Track
    A)**: in `FlexLib_API/FlexLib/Radio.cs`, next to `FindMeterByName`, keep the
    comment-marked `public ImmutableList<Meter> GetMeters()` — `lock (_meters)`,
    `return _meters.ToImmutableList()`. `Radio` keeps its meter inventory in
    `private List<Meter> _meters`, guarded throughout by `lock (_meters)`, and
    every accessor around it is public (`FindMeterByName`,
    `FindMetersByAmplifier`, `FindMetersByTuner`) — but there was no way to
    enumerate it, so "what meters does this radio actually have?" was
    unanswerable through the supported API. That is not academic: on 2026-08-16
    a FLEX-8600 reported **102 meters** against the eight the Live Meters tab
    hardcoded, and anything that lets an operator choose which meters to watch
    has to ask the radio rather than carry a list. The patch mirrors
    `FindMetersByAmplifier` exactly — same lock, same `ImmutableList<Meter>`
    return — so it is stylistically vendor-native and cannot hand out a list
    that mutates underneath a caller. It is **purely additive**: no existing
    vendor line changes, so a 3-way merge on the next upgrade cannot conflict
    with it, but it WILL need re-adding if a merge takes `Radio.cs` wholesale,
    which is the whole reason this list exists. `Radios/FlexBase.cs` and
    `Radios/MeterInventory.cs` both depend on it, so a lost reapply is a build
    break rather than a silent degradation — deliberately. **Reportable
    upstream to Flex:** an enumerator for a list whose every other accessor is
    public is an obvious gap, and they may simply add it. History: reviewed and
    written down on 2026-08-16 under a heading reading "Not yet applied",
    because that session needed only a trace and got it by reflection; applied
    on 2026-08-19 when the meter inventory service needed a supported route.
    The reflection in `FlexBase.traceMeterInventory` was deleted in the same
    commit — two ways to reach one private field is how one of them rots
    unnoticed.

12. **Explicit compression on the remote TX audio stream (applied 2026-08-20,
    Sprint 33 Track G)**: two comment-marked patches, both additive.

    In `FlexLib_API/FlexLib/Radio.cs`, keep
    `RequestRemoteAudioTXStream(bool isCompressed)` — sending
    `stream create type=remote_audio_tx compression=opus` or `compression=none`
    — and the `[Obsolete]` attribute on the parameterless original. Vendor stock
    deprecated the parameterless RX request in favour of
    `RequestRXRemoteAudioStream(bool isCompressed)` and never did the same for
    TX, so a client encoding Opus for transmit had no supported way to say so
    and sent Opus into a stream whose compression it never declared. The wire
    format accepts the argument on both directions — FlexLib's own protocol
    comment on the `stream` status parser reads
    `type=<remote_audio_rx|remote_audio_tx> compression=<none|opus>` — and
    `TXRemoteAudioStream.StatusUpdate` already parses the `compression=` key the
    radio answers with. Only the request omitted it. The overload mirrors the RX
    shape deliberately, `[Obsolete]` wording included, so the file carries one
    idiom rather than two.

    In `FlexLib_API/FlexLib/TXRemoteAudioStream.cs`, keep the
    `CompressionSetting` and `LastStatusLine` properties and the one-line
    assignments that fill them. `IsCompressed` is a bool and therefore cannot
    tell `compression=none` apart from a status line carrying no compression key
    at all; both leave it false. Keeping the radio's unparsed answer is what
    makes "what does this radio do with a stream that does not declare
    compression?" an observation rather than an inference. `Radios/FlexBase.cs`
    logs both in `opusInputStreamAddedHandler` and raises an error-level line if
    the radio opened the stream as anything other than opus, because every
    packet we send is Opus and a mismatch would be silent transmit with no other
    symptom.

    **This is hygiene, not a bug fix, and the distinction matters if you are
    reading this while chasing dead transmit audio.** A FLEX-8600 answers a
    create sent *without* the argument with `compression=OPUS`, and shipping
    SmartSDR sends the same bare command — both wire-observed on 2026-08-10.
    Declaring compression is not expected to change behaviour on that radio. The
    value is in not depending on an undocumented radio-side default, and in the
    next client author not falling into the same trap. **Reportable upstream to
    Flex:** an API that offers explicit compression on RX and silently defaults
    it on TX is an asymmetry worth closing. Worth reporting in the same breath:
    `RXRemoteAudioStream` compares the compression value case-sensitively
    against `"OPUS"` while `TXRemoteAudioStream` lowercases first, so the two
    directions disagree about what a valid answer looks like. Nothing inside
    FlexLib consumes either `IsCompressed`, and `FlexBase` force-sets the RX one
    to true, which is why that has never bitten.

## Upgrade procedure that worked for 4.2.18 → 4.2.20 (2026-08-03)

Rather than a fresh vendor copy + manual patch reapply, use git for a 3-way merge per changed file:
1. Branch off `main`; `git checkout track/flexlib-42 -- FlexLib_API/` to pull the already-patched 4.2.18 tree (never merge `main` INTO that branch — main carries the revert and silently deletes the drop).
2. For each vendor-changed file we also patched (find them by diffing pristine drops **with line endings normalized** — repo objects are LF, vendor zips are CRLF): `git merge-file ours base theirs` with base = pristine old vendor (commit `92385f88` for 4.2.18), ours = patched tree, theirs = new vendor file. 4.2.18→4.2.20 changed only `Radio.cs` (clean merge, zero conflicts) and `RXAudioStream.cs` (never patched, blind copy).
3. Re-check items 1–9 above (the version stamp and the VitaSocket patch included — the old "1–7" here predated both), restore `MmcssPipelineScheduler.cs` to vendor-stock, build, and verify `Radio.cs` line counts: 4.1.5 = 14,471; 4.2.18 patched = 15,212; 4.2.20 merged = 15,268 (`wc -l`, CRLF or LF both count the same).

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

# FlexLib upgrade/Migration notes

This app carries a small, non-breaking shim to enforce TLS 1.2+ without editing vendor FlexLib sources. When you drop in a newer FlexLib, reapply only the items below.

## What to reapply after upgrading FlexLib
1. **Keep the TLS wrapper**: Copy `FlexLib_API/FlexLib/SslClientTls12.cs` into the new FlexLib `FlexLib` folder (adjust the path if the vendor layout changes). This wrapper enforces TLS 1.2/1.3 and mirrors the stock `SslClient` API.
2. **Point TLS command transport to the wrapper**: In `FlexLib/FlexLib/TlsCommandCommunication.cs`, replace the `SslClient` field/constructor with `SslClientTls12` (the only code edit inside the vendor tree).
3. **Prefer TLS when available**: In `FlexLib/FlexLib/Radio.cs`, keep the small change that chooses `TlsCommandCommunication` whenever `IsWan` is true **or** `PublicTlsPort > 0`. This avoids plaintext when the radio exposes a TLS port.
4. **App-wide TLS floor**: Ensure `ApplicationEvents.vb` is included in the project. It sets `ServicePointManager.SecurityProtocol = Tls12 Or Tls13` at startup so any `HttpWebRequest`/HTTPS calls use modern TLS.

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

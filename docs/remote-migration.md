## Remote / SmartLink Modernization Plan

This document captures the current state and the work needed to get Remote/SmartLink discovery and connect working again on the modernized JJFlexRadio codebase.

### Current state
- TLS/Auth0: HTTPS enforced; TLS 1.2+ forced via `ServicePointManager` in `AuthForm`. The Auth dialog now shows the full authorize URL and no longer triggers the “plaintext http” warning.
- UI: Remote button opens the auth window; radio list shows “unknown/unknown” and Connect is a no-op.
- Radios build currently uses stub radio types (to keep the solution compiling). We attempted to reintroduce real rig implementations from the old JJRadio tree, but missing dependencies blocked the build.
- Build locking: Close any running JJFlexRadio instance before building; otherwise `Radios.dll` is locked.

### What’s missing (to restore Remote)
- Audio/Opus stream layer (from JJRadio/FlexLib v2.4.9):
  - `OpusStream`, `AudioStream`, `TXAudioStream`
  - Flex audio/opus event and command wiring (`Radio.SendCommand`, `Add/Remove/Request AudioStream/TXAudioStream/OpusStream`, `On*StreamAdded`, etc.)
- Rig/handler classes:
  - `KenwoodIhandler`, `IcomIhandler`
  - Filters/forms: `K3Filters`, `TS590Filters`, `TS2000Filters`, `ic9100filters`, etc.
  - `UserEnteredRemoteRigInfo`
- Rig table entries: `Flex6300`, `Kenwood*`, `Icom*`, `ElecraftK3`, etc. need real implementations wired instead of stubs.
- API gaps: FlexLib 4.x lacks many of the audio methods present in 2.4.9. We need shims or to import the older audio layer alongside (or instead of) current FlexLib APIs.

### Plan options
1) Shim with FlexLib 4.x (preferred if feasible):
   - Add adapter methods around FlexLib 4.x to mimic the old `Radio` audio APIs (`SendCommand`, add/remove/request audio/opus/TX streams, event delegates).
   - Port `OpusStream/AudioStream/TXAudioStream` and adjust calls to the shimmed API surface.
2) Import older audio layer:
   - Bring over the v2.4.9 audio/event classes and, if necessary, a parallel copy of the older FlexLib audio event interfaces.
   - Reconcile type clashes (namespaces/assembly) with the current FlexLib 4.x.

### Proposed steps (grain by grain)
1) Close running app to avoid file locks on `bin\x86\Debug\net48\Radios.dll`.
2) Add real rig files back into `Radios` (already copied from `JJRadio/Radios` but currently excluded due to missing deps).
3) Port/inline missing handler/filter classes:
   - `KenwoodIhandler.cs`, `IcomIhandler.cs` (already copied)
   - Filters/forms: `K3Filters`, `TS590Filters`, `TS2000Filters`, `ic9100filters` (copy from `JJRadio/Radios` and include in csproj).
4) Port/inline audio layer:
   - `OpusStream.cs`, `AudioStream.cs`, `TXAudioStream.cs` (copied from `JJRadio/FlexLib_API_v2.4.9/FlexLib`; need to adjust API calls).
   - Add shim methods to match old `Radio` APIs or bring in old `Radio` audio events; key missing members:
     - `SendCommand`, `AddAudioStream/RemoveAudioStream`, `RequestAudioStream`, `AddTXAudioStream/RemoveTXAudioStream`, `RequestTXAudioStream`, `AddOpusStream/RemoveOpusStream`, `On*StreamAdded` events, `AudioStreamAdded/TXAudioStreamAdded/OpusStreamAdded` event types.
5) Resolve remaining missing types:
   - `UserEnteredRemoteRigInfo` (form/class from `JJRadio/Radios`), `Flex6300` rig type, `RemoteRxOn/RemoteTxOn` semantics.
6) Wire rig table:
   - Replace stub entries in `AllRadios.RigTable` with real types (Generic, GenericBinary, Kenwood TS590/TS590SG/TS2000, ElecraftK3, Icom9100, Flex6300, etc.).
7) Build/test Remote:
   - Verify Auth0 still ok; Remote should discover real rigs (non-unknown names) and Connect should act.
8) Clean up warnings:
   - Many hide/override warnings from `FlexBase` inheriting `AllRadios`—can be reduced by adding `override`/`new` where appropriate after functionality is confirmed.

### Build/runtime notes
- Close JJFlexRadio before rebuilding (Radios.dll lock).
- Warnings about DotNetZip and missing rulesets are known; ignore for now.
- Current runnable EXE (with stubs): `bin\x86\Debug\net48\JJFlexRadio 4.0.2.exe` (from repo root).

### Auth/TLS
- Auth URL is HTTPS; TLS 1.2+ enforced in `AuthForm.cs`.
- Auth form shows a yellow banner with the full authorize URL for visibility and logs the URI via JJTrace.

### File locations to watch
- Stubs (to remove once real rigs are wired): `Radios/StubRadios.cs`
- Real rig files copied from `JJRadio/Radios`: `Generic.cs`, `Kenwood*.cs`, `Icom*.cs`, `Elecraft*.cs`, `Flex.cs`
- Handlers: `kenwoodIHandler.cs`, `IcomIhandler.cs`
- Audio: `OpusStream.cs`, `AudioStream.cs`, `TXAudioStream.cs` (ported from `JJRadio/FlexLib_API_v2.4.9/FlexLib`)
- Filters/forms to import: `K3Filters*`, `TS590Filters*`, `TS2000Filters*`, `ic9100filters*`
- Shim targets: adjust FlexLib 4.x `Radio` API gaps or introduce legacy audio event interfaces.

### Next agent guidance
- Decide shim vs. legacy audio import; start with shimming FlexLib 4.x if minimal changes suffice.
- Bring in missing filter/handler forms and include them in `Radios.csproj`.
- Add missing `UserEnteredRemoteRigInfo` and `Flex6300` rig type if not present.
- Rebuild after closing the app; fix compile errors incrementally, starting with missing types/APIs.
- Update this document as you make progress or change the plan.

### Work started (in progress)
- Pruned non-Flex rigs/filters from the build; reverted to stub layer so the app builds/runs (Flex-only scope).
- Current build succeeds (warnings only) with stubs active: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x86`.
- Remaining work is to replace stubs with real Flex-only Remote/SmartLink implementation using FlexLib 4.x.

### Next steps (Flex-only path)
1) Remove stubs and wire real Flex Remote using FlexLib 4.x (SmartLink discovery/connect).
2) Add minimal shims only if needed for Flex Remote (avoid reintroducing non-Flex rigs).
3) Rebuild (ensure app is closed to avoid Radios.dll lock), then test Remote/Connect.
4) Update this doc with any new shims or API notes encountered.

### FlexLib 4.x audio/remote notes (for wiring)
- FlexLib 4.0.1 `Radio` already exposes Remote audio streams:
  - TX: `CreateOpusStream()` (returns `TXRemoteAudioStream`), `RequestRemoteAudioTXStream()`, `AddTXRemoteAudioStream(...)`, `RemoveTXRemoteAudioStream(...)`, events `TXRemoteAudioStreamAdded/Removed`.
  - RX: `RequestRXRemoteAudioStream(bool isCompressed)`, `AddRXRemoteAudioStream(...)`, `RemoveRXRemoteAudioStream(...)`, events `RXRemoteAudioStreamAdded/Removed`.
- These can replace the legacy JJRadio `OpusStream/AudioStream/TXAudioStream` layer; prefer using the built-in FlexLib Remote audio streams rather than porting the old audio layer.
- Plan: adapt the Flex Remote implementation to call the FlexLib 4.x `Request...` APIs and subscribe to the existing RemoteAudio stream events, removing the need for the legacy audio types/shims.

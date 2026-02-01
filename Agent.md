# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current sprint:** Sprint 1 - SmartLink Saved Accounts

## 2) Technical Foundation
- Solution: `JJFlexRadio.sln`
- Languages: VB.NET (main app) + C# (libraries)
- Framework: `net8.0-windows` (.NET 8)
- Platforms: x64 (primary), x86 (legacy)
- FlexLib v4: `FlexLib_API/`

## 3) Completed Work

### .NET 8 Migration (All Phases Complete)
- Phase 0: Removed non-Flex radio support (Icom, Kenwood, Generic)
- Phase 0.5: Added FLEX-8400/8400M and Aurora AU-510/510M to RigTable
- Phase 1-2: SDK-style conversion for all C# projects
- Phase 3: All projects target `net8.0-windows`
- Phase 4: Architecture-aware native DLL loading (`NativeLoader.vb`)
- Phase 5: WebView2 for Auth0 (`AuthFormWebView2.cs`)
- Phase 6: Dual x86/x64 installers with architecture suffix
- Phase 7: Cleanup (removed conditional compilation)

### Advanced DSP Features (Complete)
UI controls and FlexBase properties implemented for:
- Neural Noise Reduction (RNN)
- Spectral Noise Reduction (NRS)
- Legacy Noise Reduction (NRL)
- Noise Reduction Filter (NRF)
- FFT Auto-Notch (ANFT)
- Legacy Auto-Notch (ANFL)

## 4) Current Sprint: SmartLink Saved Accounts

**Goal:** Enable users to save SmartLink credentials and reconnect without re-authenticating each session.

**Tasks:**
1. Create `SmartLinkAccountManager.cs` with DPAPI encryption
2. Extract refresh token from Auth0 response
3. Update `setupRemote()` to use saved accounts
4. Build `SmartLinkAccountSelector` UI dialog
5. Implement token refresh mechanism
6. Wire up invalid token handler

**Planning docs:** `docs/planning/agile/Sprint-01-SmartLink-Auth.md`

## 5) Build Commands

```batch
# 64-bit build (recommended)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64

# 32-bit build
dotnet build JJFlexRadio.sln -c Release -p:Platform=x86

# Debug build
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64
```

## 6) Key Files

### Authentication (SmartLink)
- `Radios/AuthFormWebView2.cs` - WebView2-based Auth0 login
- `Radios/FlexBase.cs` - `setupRemote()` method (lines 456-537)
- `FlexLib_API/FlexLib/WanServer.cs` - SmartLink protocol

### DSP/Filters
- `Radios/Flex6300Filters.cs` - DSP controls UI
- `Radios/FlexBase.cs` - Radio abstraction layer

### Security
- `FlexLib_API/FlexLib/SslClientTls12.cs` - TLS 1.2+ wrapper
- `FlexLib_API/Util/StringCipher.cs` - Rijndael encryption (available)

## 7) GitHub Actions

- **CI:** `.github/workflows/windows-build.yml` - Build on push/PR
- **Release:** `.github/workflows/release.yml` - Tag-triggered release

Create releases:
```batch
git tag -a v4.1.X -m "Release 4.1.X - description"
git push origin main --tags
```

## 8) Documentation

| File | Description |
|------|-------------|
| `CLAUDE.md` | Build commands, project structure, coding patterns |
| `docs/TODO.md` | Feature backlog |
| `docs/remote-migration.md` | SmartLink modernization status |
| `docs/CHANGELOG.md` | Version history |
| `docs/planning/agile/` | Sprint planning documents |

---

*Updated: Sprint 1 kickoff - SmartLink Saved Accounts*

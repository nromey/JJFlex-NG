                        # FlexLib API v4.0.1 - Missing Features in JJFlexRadio

## Overview
This document identifies advanced features available in FlexLib API v4.0.1 that are not currently exposed in JJFlexRadio. These features were added in SmartSDR v4.0+ and represent significant improvements to noise reduction, diversity reception, and signal processing.

## 1. Diversity Reception (QSB Mitigation)

**Status:** ❌ Not Implemented  
**Availability:** FLEX-6600, 6700, 8600, 6700R only (requires 2 SCUs)

### FlexLib API Properties:
- `Slice.DiversityOn` (bool) - Enable/disable diversity mode
- `Slice.DiversityChild` (bool) - Indicates if slice is diversity child
- `Slice.DiversityIndex` (int) - Paired diversity slice index
- `Slice.DiversitySlicePartner` (Slice) - Reference to partner slice
- `Radio.DiversityIsAllowed` (bool) - Check if radio supports diversity

### Current Status:
- README.htm states: "At present, JJFlexRadio doesn't support diversity reception."
- No properties exposed in FlexBase.cs
- No UI controls in Flex6300Filters.cs

### What It Does:
Diversity reception uses two receive antennas simultaneously to mitigate QSB (fading) caused by signal polarization changes. The radio combines the signals from both slices to provide better reception under poor propagation conditions.

---

## 2. Advanced Noise Reduction Algorithms

### 2.1 Neural Noise Reduction (RNN) - AI-Based

**Status:** ⚠️ FlexBase implemented; UI pending  
**Type:** Recurrent Neural Network noise reduction

#### FlexLib API Properties:
- `Slice.RNNOn` (bool) - Enable/disable AI noise reduction

#### Current Status:
- FlexBase: `NeuralNoiseReduction` added (maps to `Slice.RNNOn`)
- UI: No control yet; add toggle in Filters panel

#### What It Does:
Uses artificial intelligence (recurrent neural network) to identify and remove noise while preserving speech/signal quality. This is the newest and most advanced noise reduction algorithm.

---

### 2.2 Noise Reduction with Filter (NRF) - "Better NR"

**Status:** ⚠️ FlexBase implemented; UI pending

#### FlexLib API Properties:
- `Slice.NRFOn` (bool) - Enable/disable NRF
- `Slice.NRFLevel` (int, 0-100) - NRF strength level

#### Current Status:
- FlexBase: `NoiseReductionFilter` + `NoiseReductionFilterLevel` added
- UI: No controls yet; add toggle + level in Filters panel

#### What It Does:
Enhanced noise reduction that combines filtering techniques for better performance than the original NR algorithm.

---

### 2.3 LMS Legacy Noise Reduction (NRL)

**Status:** ⚠️ FlexBase implemented; UI pending  
**Type:** Least Mean Squares algorithm (legacy)

#### FlexLib API Properties:
- `Slice.NRLOn` (bool) - Enable/disable LMS noise reduction
- `Slice.NRL_Level` (int, 0-100) - LMS NR level

#### Current Status:
- FlexBase: `NoiseReductionLegacy` + `NoiseReductionLegacyLevel` added
- UI: No controls yet; add toggle + level in Filters panel

#### What It Does:
The older LMS (Least Mean Squares) noise reduction algorithm, kept for compatibility. Some users prefer it for certain signal types.

---

### 2.4 Spectral Subtraction Noise Reduction (NRS)

**Status:** ⚠️ FlexBase implemented; UI pending  
**Type:** Speex-based spectral subtraction

#### FlexLib API Properties:
- `Slice.NRSOn` (bool) - Enable/disable spectral subtraction NR
- `Slice.NRSLevel` (int, 0-100) - Spectral subtraction level

#### Current Status:
- FlexBase: `SpectralNoiseReduction` + `SpectralNoiseReductionLevel` added
- UI: No controls yet; add toggle + level in Filters panel

#### What It Does:
Uses spectral subtraction techniques (Speex library) to remove noise from the signal.

---

### 2.5 FFT-Based Automatic Notch Filter (ANFT)

**Status:** ⚠️ FlexBase implemented; UI pending

#### FlexLib API Properties:
- `Slice.ANFTOn` (bool) - Enable/disable FFT-based auto-notch

#### Current Status:
- FlexBase: `AutoNotchFFT` added (maps to `Slice.ANFTOn`)
- UI: No controls yet; add toggle in Filters panel

#### What It Does:
FFT-based automatic notch filter for removing narrow-band interference. More advanced than the legacy LMS-based ANF.

---

### 2.6 LMS Legacy Auto-Notch Filter (ANFL)

**Status:** ⚠️ FlexBase implemented; UI pending

#### FlexLib API Properties:
- `Slice.ANFLOn` (bool) - Enable/disable legacy LMS auto-notch
- `Slice.ANFL_Level` (int, 0-100) - Legacy ANF level

#### Current Status:
- FlexBase: `AutoNotchLegacy` + `AutoNotchLegacyLevel` added (maps to `Slice.ANFLOn`/`ANFL_Level`)
- UI: No controls yet; add toggle + level in Filters panel
- Note: Existing `ANF` controls remain for original algorithm

#### What It Does:
The legacy LMS-based automatic notch filter, kept for compatibility.

---

## 3. Currently Implemented Noise Reduction

### ✅ Basic Noise Reduction (NR)
- **FlexBase Property:** `NoiseReduction` (OffOnValues)
- **FlexBase Property:** `NoiseReductionLevel` (0-100)
- **FlexLib:** `Slice.NROn`, `Slice.NRLevel`
- **UI Control:** Available in Flex6300Filters.cs

### ✅ Auto-Notch Filter (ANF) - Original
- **FlexBase Property:** `ANF` (OffOnValues)
- **FlexBase Property:** `AutoNotchLevel` (0-100)
- **FlexLib:** `Slice.ANFOn`, `Slice.ANFLevel`
- **UI Control:** Available in Flex6300Filters.cs

### ✅ Audio Peak Filter (APF)
- **FlexBase Property:** `APF` (OffOnValues)
- **FlexBase Property:** `APFLevel` (0-100)
- **FlexLib:** `Slice.APFOn`, `Slice.APFLevel`
- **UI Control:** Available in Flex6300Filters.cs

### ✅ Noise Blanker (NB)
- **FlexBase Property:** `NoiseBlanker` (OffOnValues)
- **FlexBase Property:** `NoiseBlankerLevel` (0-100)
- **FlexLib:** `Slice.NBOn`, `Slice.NBLevel`
- **UI Control:** Available in Flex6300Filters.cs

### ✅ Wideband Noise Blanker (WNB)
- **FlexBase Property:** `WidebandNoiseBlanker` (OffOnValues)
- **FlexBase Property:** `WidebandNoiseBlankerLevel` (0-100)
- **FlexLib:** `Slice.WNBOn`, `Slice.WNBLevel`
- **UI Control:** Available in Flex6300Filters.cs

---

## Implementation Priority Recommendations

### High Priority (Most Impactful)

1. **Neural Noise Reduction (RNN)** - The AI-based solution is state-of-the-art
2. **Diversity Reception** - Critical for QSB mitigation (but needs 2-SCU radio to test)
3. **Noise Reduction with Filter (NRF)** - "Better NR" algorithm

### Medium Priority

4. **Spectral Subtraction (NRS)** - Alternative NR approach
5. **FFT-Based Auto-Notch (ANFT)** - Better than original ANF

### Lower Priority (Legacy/Compatibility)

6. **LMS Legacy NR (NRL)** - For users who prefer the old algorithm
7. **LMS Legacy ANF (ANFL)** - For users who prefer the old algorithm

---

## Implementation Notes

### UI Considerations

The Flex6300Filters UI would need additional controls for:
- Radio buttons or dropdown to select NR algorithm type (Original, NRL, NRS, NRF, RNN)
- Checkboxes for the new auto-notch options (ANFT, ANFL)
- Diversity controls (if radio supports it)

### Testing Requirements

- **Diversity:** Requires a FLEX-6600, 6700, 8600, or 6700R to test
- **Neural NR:** Available on all radios with SmartSDR v4.0+
- **Other algorithms:** Should be testable on any Flex radio with v4.0+ firmware

### Menu Structure Suggestion

Consider adding a "DSP Algorithms" or "Advanced Noise Reduction" submenu to allow users to:
1. Choose their preferred NR algorithm
2. Enable/disable diversity (if supported)
3. Select auto-notch filter type (Original/FFT/Legacy)

---

## 4. MultiFlex Multi-Client Support

### Overview

MultiFlex allows multiple GUI clients (users) to connect to the same FlexRadio simultaneously. In version 3+, this enables scenarios like:
- **You and a friend** both controlling the same radio
- **Multiple programs** running on different computers
- **Shared radio resources** (slices, panadapters)

### Multi-Client Tracking APIs

**Radio.GuiClients** (List<GUIClient>) - All connected clients
- Each `GUIClient` provides:
  - `ClientHandle` (uint) - Unique client ID
  - `ClientID` (string) - Client identifier
  - `Station` (string) - Station name (e.g., "W1ABC")
  - `Program` (string) - Software name (e.g., "SmartSDR", "JJFlex")
  - `IsThisClient` (bool) - True if this is your client
  - `IsAvailable` (bool) - Client is responsive
  - `IsLocalPtt` (bool) - Has local PTT access

**Radio.GuiClientStations** (string) - Comma-separated list of station names
**Radio.GuiClientIPs** (string) - IP addresses of connected clients
**Radio.GuiClientHosts** (string) - Hostnames of connected clients

### Slice/Resource Management

**Radio.AvailableSlices** (int) - Remaining slices not in use by other clients
**Radio.MaxSlices** (int) - Total slice capacity of the radio
**Radio.SliceList** (List<Slice>) - All slices (yours and others')
**Radio.MaxPanadapters** (int) - Total panadapter capacity

### Implementation Considerations

When implementing features like **diversity reception**, you must check if resources are available:

```csharp
// Check if diversity is possible with current resource usage
bool canUseDiversity = 
    theRadio.DiversityIsAllowed &&  // Hardware supports it
    (theRadio.FeatureLicense?.LicenseFeatDivEsc?.FeatureEnabled == true) &&  // Licensed
    (theRadio.RXAntList != null && theRadio.RXAntList.Length >= 2) &&  // Antennas available
    theRadio.AvailableSlices >= 2;  // Need 2 slices (parent + child)

// If friend is using 6 out of 8 slices, you may not have enough for diversity
```

**Best Practice**: Display connected clients and resource usage in the UI so users understand why certain features may be unavailable.
@@### Disabling MultiFlex (Exclusive Access)
@@
@@**Radio.MultiFlexEnabled** (bool) - Get or set whether MultiFlex is enabled
@@- **Set to `false`** to disable MultiFlex and prevent other users from connecting
@@- **Set to `true`** to allow multiple simultaneous connections
@@- Command: `radio set mf_enable=0/1`
@@- **Minimum Version**: SmartSDR v3.0+ (check `Radio.MinimumMajorVersionForMultiFlex`)
@@
@@```csharp
@@// Disable MultiFlex to get exclusive access to the radio
@@theRadio.MultiFlexEnabled = false;  // Prevents other clients from connecting
@@
@@// Re-enable MultiFlex when done
@@theRadio.MultiFlexEnabled = true;  // Allow others to connect again
@@```
@@
@@**Use Case**: If you need all the radio's resources (e.g., all 8 slices on an 8600) and don't want your friend connecting while you're operating, disable MultiFlex. This gives you **exclusive access** to the transceiver.
@@
@@**Forcibly Disconnect Clients**:
@@```csharp
@@// Disconnect another client by their handle
@@theRadio.DisconnectClientByHandle(client.ClientHandle.ToString());
@@
@@// Or disconnect yourself
@@theRadio.Disconnect();
@@```
@@

---

## 5. License/Entitlement System Integration

### Overview

FlexRadio uses a sophisticated licensing system to gate premium features. The FlexLib API v4.0.1 provides `Radio.FeatureLicense` which exposes:

### Subscription Status
- `IsSmartSDRPlus` (bool) - Has active SmartSDR+ subscription
- `IsSmartSDRPlusEA` (bool) - Has early access subscription
- `SsdrPlusExpiration` (DateTime) - Subscription expiration
- `SsdrEarlyAccessExpiration` (DateTime) - EA expiration

### Feature-Specific Licenses

Each feature has a `Feature` record with:
- `FeatureName` (string) - Feature identifier
- `FeatureEnabled` (bool) - Whether feature is available
- `FeatureStatusReason` (enum) - Why enabled/disabled
  - `LicenseFile` - Purchased via license file
  - `Plus` - Available via SmartSDR+ subscription
  - `Ea` - Available via Early Access subscription
  - `BuiltIn` - Built into radio
  - `Unknown` - Status unknown
- `FeatureGatedMessage` (string) - User-friendly message if disabled

### Available Feature Properties

| Feature Property | Radio Command | Description |
|-----------------|---------------|-------------|
| `LicenseFeatNoiseReduction` | "noise_reduction" | Advanced NR algorithms (RNN, NRF, NRS, etc.) |
| `LicenseFeatDivEsc` | "div_esc" | Diversity & Echo Suppression/Cancellation |
| `LicenseFeatDVK` | "digital_voice_keyer" | Digital Voice Keyer |
| `LicenseFeatAutotune` | "auto_tune" | Automatic antenna tuning |
| `LicenseFeatMultiflex` | "multiflex" | Multiple simultaneous users |
| `LicenseFeatNoiseFloor` | "noise_floor" | Noise floor display |
| `LicenseFeatSmartlink` | "smartlink" | Remote operation |
| `LicenseFeatWFP` | "wfp" | Wide Frequency Panadapter |
| `LicenseFeatWideBandwidth` | "wide_bandwidth" | Wide bandwidth operation |
| `LicenseFeatAlpha` | "alpha" | Alpha/experimental features |

### Implementation Strategy

#### 1. Check License Before Exposing UI
```csharp
// Example: Check if noise reduction is licensed
if (theRadio.FeatureLicense?.LicenseFeatNoiseReduction?.FeatureEnabled == true)
{
    // Enable RNN, NRF, NRS controls
}
else
{
    // Gray out or hide controls
    // Show tooltip: theRadio.FeatureLicense.LicenseFeatNoiseReduction.FeatureGatedMessage
}
```

#### 2. Check Diversity Support
```csharp
// Must check FOUR conditions for diversity:
// 1. Radio hardware capability (2 SCUs)
// 2. Feature is licensed
// 3. At least 2 antennas are available
// 4. Available slices (MultiFlex awareness - other users may be connected)
bool diversityAvailable = 
    theRadio.DiversityIsAllowed &&  // Radio has 2 SCUs (6600, 6700, 8600, 6700R)
    (theRadio.FeatureLicense?.LicenseFeatDivEsc?.FeatureEnabled == true) &&  // Feature is licensed
    (theRadio.RXAntList != null && theRadio.RXAntList.Length >= 2) &&  // At least 2 antennas available
    theRadio.AvailableSlices >= 2;  // Need 2 slices for diversity parent+child (check for other connected users)

if (diversityAvailable)
{
    // Enable diversity controls
    // User can select which two antennas to use (e.g., ANT1 + ANT2, or ANT1 + RX_A)
}
else if (!theRadio.DiversityIsAllowed)
{
    // Hide completely - hardware doesn't support it
}
else if (theRadio.FeatureLicense?.LicenseFeatDivEsc?.FeatureEnabled != true)
{
    // Gray out with license message
}
else if (theRadio.RXAntList?.Length < 2)
{
    // Gray out with message: "Connect at least 2 antennas to use diversity"
}
```

**Antenna List Properties:**
- `Radio.RXAntList` (string[]) - Array of available RX antennas (e.g., ["ANT1", "ANT2", "RX_A", "RX_B", "XVTR"])
- `Slice.RXAntList` (string[]) - Same as Radio.RXAntList, per-slice view
- `Slice.TXAntList` (string[]) - Available TX antennas
- `Slice.RXAnt` (string) - Current RX antenna selection (e.g., "ANT1")
- `Slice.TXAnt` (string) - Current TX antenna selection

#### 3. Handle Radio Model Differences

Some features depend on hardware capabilities, not just licenses:
- **Diversity:** Requires 2 SCUs (6600, 6700, 8600, 6700R)
- **Neural NR:** May require newer firmware
- **DVK:** Likely requires SmartSDR integration

Use `theRadio.Model` to check radio type and `theRadio.DiversityIsAllowed` for hardware capability.

#### 4. Future-Proof for New Features

FlexRadio adds new features monthly. The license system is extensible:
- Monitor `FeatureLicense` PropertyChanged events
- Parse unknown feature names gracefully
- Log new features for investigation
- Allow users to access newly-licensed features without app updates

#### 5. UI/UX Best Practices

**Menu Items:**
- ✅ **Enabled & Active** - Checked menu item, feature is on
- ✅ **Enabled & Inactive** - Unchecked menu item, feature is off
- 🔒 **Not Licensed** - Grayed out with tooltip explaining how to purchase
- ❌ **Not Supported** - Hidden completely (hardware limitation)

**Tooltips for Locked Features:**
- Use `Feature.FeatureGatedMessage` property
- Examples:
  - "Subscribe to SmartSDR+ to use this feature!"
  - "Purchase this feature!"
  - "Subscribe to SmartSDR+ Early Access to use this feature!"

**Dynamic Updates:**
- Listen for license PropertyChanged events
- Update UI when user purchases/subscribes
- Handle license expiration gracefully

### Testing Notes

1. **Without License:** Controls should be grayed/hidden with helpful messages
2. **With License:** All features available
3. **Expired Subscription:** Features should become unavailable (graceful degradation)
4. **Hardware Limitations:** Distinguish between "need license" vs "radio doesn't support"

### Model Support Matrix

| Radio Model | SCUs | Max Slices | Diversity | Neural NR | Notes |
|------------|------|------------|-----------|-----------|-------|
| FLEX-6300 | 1 | 2 | ❌ | ✅ | Entry level |
| FLEX-6400(M) | 1 | 2 | ❌ | ✅ | M version is HF-only |
| FLEX-6500 | 1 | 4 | ❌ | ✅ | |
| FLEX-6600(M) | 2 | 4 | ✅ | ✅ | First 2-SCU model |
| FLEX-6700 | 2 | 8 | ✅ | ✅ | Flagship |
| FLEX-6700R | 2 | 8 | ✅ | ✅ | Rack mount |
| FLEX-8600(M) | 2 | 8 | ✅ | ✅ | 8000 series |

---

## Supported Radios and Detection

JJFlex should not hard-code a single model (e.g., 6300). Use dynamic detection from the radio at runtime:

- Supported families: FLEX-6300, 6400(M), 6500, 6600(M), 6700, 6700R, 8400, 8600(M), Aurora series (A520/A520M) upcoming
- Detect via `theRadio.Model` (e.g., "FLEX-8600M") and capabilities:
    - Diversity capability: `theRadio.DiversityIsAllowed`
    - Slice capacity: `theRadio.MaxSlices`
    - Panadapter capacity: `theRadio.MaxPanadapters`
    - Antennas: `theRadio.RXAntList`, per-slice `Slice.RXAnt`

Guidelines:
- Avoid model-specific UI types like `Flex6300Filters.cs` controlling availability; gate features by capability flags and license.
- When a feature depends on hardware (e.g., diversity), compute availability from the capability + license + resources (slices, antennas).
- For future radios (Aurora series) rely on discovery values for `Model`, `MaxSlices`, etc.; do not filter out unknown models unless explicitly unsupported.

## Questions for User

1. ✅ **License Awareness:** We'll check `Radio.FeatureLicense` before exposing features
2. ✅ **Radio Model:** We'll use `Radio.DiversityIsAllowed` to check hardware capability
3. Do you have access to a 2-SCU radio (6600/6700/8600) to test diversity?
4. Would you prefer a single "Noise Reduction" control that switches algorithms, or separate controls for each?
5. Should legacy algorithms (NRL, ANFL) be exposed or hidden by default?
6. For unlicensed features, prefer: (a) Gray out with tooltip, or (b) Hide completely?

---

**Generated:** December 2, 2025  
**FlexLib API Version:** v4.0.1  
**JJFlexRadio Branch:** fix/install-bat-nsis  
**License System:** Fully integrated with Radio.FeatureLicense API

---

## Secure Zip Library Replacement

Current usage includes DotNetZip (`Ionic.Zip`) in several files (e.g., `ImportSetup.vb`, `ExportSetup.vb`, `DebugInfo.vb`, `Radios original/FlexDB.cs`) and a `PackageReference` to `DotNetZip` in `FlexLib_API/FlexLib/FlexLib.csproj`. DotNetZip has known vulnerabilities (Zip Slip path traversal; CVE-2018-1002205). SmartSDR 4.0.1 also addressed zip-related security issues.

Recommended remediation:
- Replace DotNetZip with the built-in `System.IO.Compression` (`ZipArchive`, `ZipFile`).
- Implement safe extraction to prevent path traversal.
- Remove `Imports Ionic.Zip` / `using Ionic.Zip` and the DotNetZip package reference.

Safe extraction pattern:
```csharp
using System;
using System.IO;
using System.IO.Compression;

static void ExtractZipSecure(string zipPath, string destinationDir)
{
    using (ZipArchive zip = ZipFile.OpenRead(zipPath))
    {
        string destRoot = Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories

            string fullPath = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!fullPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Unsafe entry (Zip Slip) — skip or log
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            entry.ExtractToFile(fullPath, overwrite: true);
        }
    }
}
```

Migration notes:
- Replace DotNetZip constructs (e.g., `ZipFile.Read`, `ZipFile.AddFile`, `ZipFile.Save`) with `ZipArchive` and `ZipFile` APIs.
- If password-protected zips are required, consider alternatives cautiously; otherwise prefer `System.IO.Compression` for security and maintenance.


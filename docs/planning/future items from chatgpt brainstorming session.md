# JJ FLEX -- FUTURE ARCHITECTURE & FEATURE ROADMAP

Generated: 2026-02-18 04:13 UTC

------------------------------------------------------------------------

## OVERVIEW

This document consolidates strategic ideas for expanding JJ Flex into a
radio activity platform with digital voice, activity discovery, and net
participation support, while maintaining architectural discipline and
avoiding scope explosion.

------------------------------------------------------------------------

## EPIC 1: FreeDV2 / RADEV2 Integration

### Vision

Provide FreeDV2 as a first-class mode using RADEV2, with support for: -
External SSH-managed node (Raspberry Pi) - Future native in-radio
support - User preference for backend selection

### Architecture

**Capability:** FreeDV2\
**Backends:** - NativeRadio - ExternalNode (SSH Managed)

**Runtime selection logic:** 1. If user preference = Prefer Node → use
Node\
2. Else if radio reports native support → use Native\
3. Else if node available → use Node\
4. Else → Mode unavailable

### Node Manager (Tools → FreeDV2 Node Manager)

**States:** - Not Installed - Installed but Offline - Running

**Capabilities:** - SSH key-based configuration - Install RADEV2 node
(AppImage) - systemd service management - Start / Stop - Version check +
monthly update - Log viewer

### Mode Behavior

-   Show FreeDV2 as:
    -   Not Configured
    -   Node Offline
    -   Available
-   Auto-disable DAX when activating
-   Ensure slice exists
-   Announce activation state

------------------------------------------------------------------------

## EPIC 2: Unified Activity Engine

### Objective

Normalize digital and DX activity sources into a single model.

### Data Model: ActivitySpot

-   Callsign
-   Frequency
-   Mode
-   Grid (optional)
-   Signal report (optional)
-   Timestamp
-   Source (DXCluster, PSKReporter, etc.)

### Provider Interface

`IActivityProvider` - GetSpots(filter) - Optional streaming support

### Initial Providers

-   Telnet DX Cluster
-   PSKReporter (FreeDV2 + FT8 ready)

### Activity Panel Features

-   Accessible list
-   Mode filter
-   Band filter
-   One-action Tune to Spot
-   Auto-set mode + frequency

------------------------------------------------------------------------

## EPIC 3: HF Activity Net Integration

### Objective

Simplify participation in structured nets (e.g., HF Activity).

### Features

-   Display band schedule
-   Tune to current slot
-   Next band navigation
-   Optional "Check In Online" dialog
-   Accessible structured layout

------------------------------------------------------------------------

## EPIC 4: NetLogger Integration Strategy

### Objective

Enhance net participation without duplicating logging software.

### Phase 1

-   Research NetLogger XML API
-   Pull Active Nets
-   Pull Check-ins

### Phase 2

-   Provide Tune-to-Net workflow
-   Optional "Log to NetLogger" integration

### Chat / AIM Consideration

-   Investigate whether AIM messages are exposed via API
-   If not exposed:
    -   Avoid reverse engineering
    -   Consider collaborating with maintainer
    -   Do not split net chat into parallel system without adoption

------------------------------------------------------------------------

## AIM Accessibility Research Requirements

### Problems Identified

-   Cannot focus AIM window reliably
-   No automatic reading of new messages
-   Cannot review history using arrow keys

### Accessibility Requirements

-   Keyboard-focusable message history control
-   Up/Down navigation through message list
-   Page navigation
-   Proper UIA exposure
-   Optional auto-announce of new messages
-   No owner-drawn custom controls without UIA support

### Acceptance Criteria

-   Fully keyboard operable
-   Screen reader reads each message
-   No focus traps
-   New messages accessible without stealing focus

------------------------------------------------------------------------

## Scope Boundaries

### JJ Flex SHOULD:

-   Control radio
-   Manage digital nodes
-   Provide activity discovery
-   Assist net participation
-   Integrate with existing logging tools

### JJ Flex SHOULD NOT:

-   Become a full logging suite
-   Replace NetLogger entirely
-   Duplicate award tracking systems
-   Implement unsupported reverse-engineered APIs

------------------------------------------------------------------------

## Strategic Outcome

JJ Flex evolves into:

-   A digital voice command center
-   An activity-aware radio client
-   A structured net participation assistant
-   A future-ready platform for FT8 and additional digital modes
-   A tool that remains accessibility-first and architecturally clean

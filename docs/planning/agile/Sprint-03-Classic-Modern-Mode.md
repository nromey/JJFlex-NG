# Sprint 3: Classic/Modern Mode Foundation

**Status:** Complete
**Version:** 4.1.11 (no version bump — infrastructure only, no user-visible release)

## Goal

Add UI mode infrastructure so JJFlexRadio supports two modes:
- **Classic** — preserves everything as-is (zero changes for existing users)
- **Modern** — new menu structure with stub items, ready for future feature sprints

This sprint builds the **foundation only**. Modern menu items are stubs (announce "coming soon")
except where they can delegate to existing handlers. FreqOut and StatusBox stay in both modes.

## What Shipped

### UIMode Enum & Persistence
- `UIMode` enum (Classic=0, Modern=1, Logging=2) in `globals.vb`
- `UIModeSetting` integer field in `PersonalData.personal_v1` (backward-compatible)
- `CurrentUIMode` property with validation (unknown values fall back to Classic)
- `ActiveUIMode` convenience property in globals.vb
- `LastNonLogMode` property for future Logging mode toggle
- New operators default to Modern; existing operators default to Classic
- One-time upgrade prompt on first launch after upgrade

### Mode Switching Infrastructure
- `ApplyUIMode()` shows/hides menus based on mode
- `ShowClassicUI()` / `ShowModernUI()` toggle menu visibility
- Called from: Form1_Load, operatorChanged, toggle handler

### Modern Menu Skeleton
- `BuildModernMenus()` creates menus programmatically (not Designer)
- Menu structure: Radio, Slice (with DSP/Antenna/FM/etc. submenus), Filter, Audio, Tools
- Existing handler delegation: Connect, Operators, Profiles, Stations, Exit, Band Plans, etc.
- Stub items announce "coming soon" via screen reader
- Full accessibility: AccessibleName, AccessibleRole, AttachMenuAccessibilityHandlers

### Mode Toggle
- **Ctrl+Shift+M** global hotkey (ProcessCmdKey override)
- Classic: "Switch to Modern UI" in Actions menu
- Modern: "Switch to Classic UI" in Tools menu
- Instant toggle with speech confirmation, no restart required

## Files Modified
- `globals.vb` — UIMode enum, ActiveUIMode, LastNonLogMode
- `PersonalData.vb` — UIModeSetting, UIModeDismissed, CurrentUIMode, copy constructor
- `PersonalInfo.vb` — New operator defaults to Modern
- `Form1.vb` — BuildModernMenus, ApplyUIMode, ToggleUIMode, ProcessCmdKey, upgrade prompt

## Design Decisions
- Modern menus built in code (not Designer) to keep Classic menus untouched
- ProcessCmdKey used for Ctrl+Shift+M (global, independent of KeyCommands)
- Logging mode (UIMode=2) falls back to Classic until future sprint
- FreqOut/StatusBox stay visible in both modes (Modern replacements ship later)

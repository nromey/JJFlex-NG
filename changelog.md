# Changelog

# Changelog

## 4.1.7
- Added a Feature Availability tab to the Radio Info dialog plus an Actions menu shortcut to show feature gating reasons (licenses, hardware, and mode constraints) without adding tab stops to main screens.
- Added a "Refresh Licenses" button to request an on-demand license status update from the radio.
- Added an Actions menu entry for audio device setup and a prompt when enabling PC audio if input/output devices are not configured.

## 4.1.6
- Added global crash reporting: unhandled errors now write a text report and mini-dump, zipped as `JJFlexError-YYYYMMDD-HHMMSS.zip` under `%APPDATA%\JJFlexRadio\Errors` with a user-facing notice.

## 4.1.5
- Only surface subscribed Flex features on the main JJFlexRadio screen and related menus (ESC, diversity, NR/ANF/filters, etc.).
- Consolidated noise mitigation controls into an Actions → Filters submenu for easier discovery; items hide/disable when the radio lacks the capability.
- Added CW Autotune support (SmartSDR 4.0+) with proper license gating in menus and UI.
- Added an optional daily trace logging mode: when enabled from Operations, runs create timestamped `JJFlexRadioTraceYYYYMMDDHHMMSS.txt` files and automatically archive prior days; when disabled, the legacy `JJFlexRadioTrace.txt`/`JJFlexRadioTraceOld.txt` behavior remains.

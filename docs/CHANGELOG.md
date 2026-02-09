# Changelog

All notable changes to this project will be documented in this file.

## Unreleased — QRZ/HamQTH Callbook Lookup & Logging Improvements

Logging Mode keeps getting smarter. The headline feature this round is automatic callbook lookups — tab out of the call sign field and JJFlexRadio reaches out to QRZ.com or HamQTH to fill in the station's name, QTH, state, and grid square. Your screen reader announces what it found ("QRZ: Name, QTH, State, Grid") so you know exactly what happened without having to Tab through fields to check.

- **Callbook auto-fill**: Supports both QRZ.com (XML API, requires subscription) and HamQTH.com (free). Configure which service to use in your operator profile under the new Callbook Lookup section. Credentials are stored per-operator. Auto-fill only touches empty fields — anything you've typed or that came from the previous-contact lookup stays put.
- **Credential migration**: If you had HamQTH credentials from the old system, they automatically migrate to the new callbook settings on first load. No re-entering passwords.
- **Full Log Form access (Ctrl+Alt+L)**: Need to enter a field that LogPanel doesn't have — rig, antenna, or some obscure ADIF tag? Press Ctrl+Alt+L to pop open JJ's full LogEntry form as a modal dialog. Also available from the Log menu as "Full Log Form." When you close it, the Recent QSOs grid refreshes to catch anything you logged in there. If you have an unsaved entry in LogPanel, it asks whether to save before opening.
- **Classic/Modern log hotkeys removed**: The 11 hotkeys that used to open the old LogEntry form from Classic and Modern modes (Alt+C, Ctrl+N, etc.) are now disabled. All logging goes through Logging Mode. This was a clean break — if you're logging, you should be in Logging Mode.
- **Reset Confirmations**: New menu item under Log in Logging Mode. If you checked "Don't ask me again" on the Escape-clear confirmation during a pileup and now want the safety net back, this restores it.
- **Modern menu stub fix (BUG-001)**: The "coming soon" placeholder items in Modern mode menus were silent in JAWS — you'd press Enter and hear nothing. Fixed by disabling the items (grayed out) and putting "coming soon" right in the accessible name. Both JAWS and NVDA now announce the placeholder state without needing to click.

## 4.1.12.0 - Logging Mode

This is the one I've been building toward. JJFlexRadio now has a dedicated Logging Mode — press Ctrl+Shift+L from anywhere in the app and you're in a clean, focused QSO entry screen. No menus full of radio controls you don't need mid-QSO, no hunting for the right form. Just you, your log, and the radio.

- **Quick-entry panel**: Call, RST Sent/Rcvd, Name, QTH, State, Grid, Comments — all in a tight Tab-order layout. Freq, mode, band, and UTC time auto-fill from the radio when you start a QSO. Press Enter to log, and the fields reset for the next one. It's the workflow I always wanted.
- **Radio pane**: A slim status strip on the left showing frequency, mode, band, and tune step. F6 toggles focus over to it, and your screen reader announces "Radio pane" so you know where you are. Arrow keys tune the radio (Up/Down by step, Shift for coarse, Left/Right to change step size). Ctrl+F pops the manual frequency dialog. You don't have to leave Logging Mode just to nudge the VFO. Tab stays in the log entry fields — you can't accidentally wander into the radio pane by tabbing.
- **Recent QSOs grid**: The bottom half of the screen shows your last 20 QSOs in a DataGridView — Time, Call, Mode, Freq, RST Sent, RST Rcvd, Name. JAWS and NVDA navigate it natively with arrow keys (row/column announcements, the whole deal). It auto-updates when you log a new contact.
- **Previous contact lookup**: Tab out of the Call field and JJFlexRadio instantly checks your entire log. If you've worked them before, you'll hear something like "W1AW — 3 previous contacts, last on 2026-01-15, 20m CW" from your screen reader. Name and QTH auto-fill from the previous contact too. You can Tab to the info field to re-read it if you missed the announcement.
- **Dup checking**: If you've already worked a station (matching your dup type — call only, call+band, etc.), you get a beep and a screen reader warning when you Tab out of the call sign field. It's warn-only, not blocking — you can still save the contact if you want. The dup dictionary loads from the log file at startup, so even after a restart it remembers who you've worked.
- **Field hotkeys**: Alt+C (Call), Alt+T (RST Sent), Alt+R (RST Received), Alt+N (Name), Alt+Q (QTH), Alt+S (State), Alt+G (Grid), Alt+E (Comments). Ctrl+N clears the form, Ctrl+W saves. All mnemonics make sense — T for senT, R for Received.
- **F6 pane switching**: Standard Windows F6/Shift+F6 toggles focus between the Radio pane and the Log entry pane. Feels natural.
- **Mode round-trip**: Ctrl+Shift+L drops you into Logging Mode, and pressing it again takes you right back to Classic or Modern — whichever you were using. Your field values survive the round-trip too.
- **Close protection**: Try to close the app with an unsaved entry and you'll get a save/discard/cancel dialog. If fields are missing, the dialog tells you what's needed before you click Yes.
- **Escape to clear**: Press Escape to clear the form. First time it asks for confirmation with a "Don't ask me again" checkbox for pileup mode.
- **Log Characteristics in Logging Mode**: Ctrl+Shift+N opens Log Characteristics without file conflicts. Previously this would crash with "Unable to process log file" because the log file was locked. Fixed by using shared file access in the log I/O layer.
- **SKCC WES form retired**: The old contest-specific SKCC WES log form is removed from the log type registry. Logging Mode replaces it with a general-purpose approach.
- **Screen reader audit**: Every control has proper AccessibleName/AccessibleRole. Mode transitions, tune step changes, previous contact lookups — all announced. Nothing happens silently. JAWS 2026 tested; NVDA has a minor difference with the Radio pane landmark (UIA processing).
- **Build system**: Improved `build-installers.bat` — clean slate approach that nukes output folders and old setup files before building. Both x64 and x86 installers generate reliably now.
- **Version bump**: 4.1.12.

If you've been opening JJFlexRadio just to log a few CW QSOs and wished the UI would get out of the way, this is that update.

## 4.1.11.0 - Seamless Auto-Connect & Audio Fix

This one's about making JJFlexRadio feel like it knows you. Set up auto-connect once, and the app just connects to your radio when it starts - no clicking through selection screens, no hunting for your saved account. And we finally fixed the "why is this so quiet?" WAN audio problem.

- **Seamless auto-connect**: Pick your radio, right-click, "Set as Auto-Connect," and you're done. Next time you launch JJFlexRadio, it connects automatically. Works for both local radios and SmartLink remotes.
- **Friendly offline handling**: If your auto-connect radio isn't available (maybe Don finally turned his off), you get a proper dialog with choices: try again, pick a different radio, or disable auto-connect. No cryptic errors, no stuck screens.
- **Single radio rule**: Only one radio can be the auto-connect target. Set it on Radio B? It clears on Radio A. No ambiguity about which radio will connect.
- **Settings confirmation**: Before saving auto-connect, you see exactly what's being saved - radio name, low bandwidth preference, the works. No "wait, what did I just enable?" moments.
- **Fixed WAN audio volume**: If your laptop speakers sounded anemic through SmartLink, you're not imagining things. The Opus decode path was bypassing all gain stages and outputting at ~16% of full scale. We added an OutputGain stage that boosts it to proper levels. Default is 4x (comfortable listening), adjustable in a future update.
- **Help page works again**: The .NET 8 migration broke the Help menu because Microsoft changed a default. Fixed with one line.
- **Fresh native DLLs**: Rebuilt Opus 1.6.1 and PortAudio 19.7.0 from source with proper SIMD optimizations for both x64 and x86. The old x86 Opus DLL was suspiciously large (1.28MB vs 366KB now).
- **Screen reader everywhere**: Connection states are all announced - connecting, connected, offline, disconnected. Nothing happens silently.

If you've been frustrated by clicking through RigSelector every single launch, or by turning your laptop volume to 100% just to hear the radio, this is your update.

## 4.1.10.0 - SmartLink Saved Accounts

Finally! This one's been on my list forever. You can now save your SmartLink login and stop typing credentials every single time you want to connect to a remote radio. I know, I know - it should have been there from day one.

- **Saved SmartLink accounts**: After logging in, JJFlexRadio asks if you want to save the account. Give it a friendly name like "Don's 6600" or "Club Station" and next time you click Remote, just pick it from the list. No more hunting for passwords or waiting for two-factor codes while your DX window closes.
- **Secure storage**: Your tokens are encrypted using Windows DPAPI - fancy talk for "tied to your Windows login." If someone copies the file to another machine, it's useless to them. No plaintext passwords, ever.
- **Automatic refresh**: When your session expires (they do, eventually), JJFlexRadio quietly tries to refresh it. If that works, you won't even notice. If it fails, you'll need to log in again, but your saved accounts stick around.
- **Account housekeeping**: You can rename or delete saved accounts from the selector. Made a typo in the name? Fixed in two clicks.
- **PKCE under the hood**: I upgraded the auth flow from the old "implicit" method to Authorization Code + PKCE. If that means nothing to you, don't worry - it just means the login is more secure and actually allows the "remember me" feature to work properly. The old way literally couldn't do refresh tokens. Who knew?

When you click Remote and have saved accounts, you'll see the account picker. Want to log in fresh? Just hit "New Login."

## 4.1.9.1 - WebView2 & Screen Reader Fixes

This patch squashes some bugs that made SmartLink login a real pain, especially if you use a screen reader.

- **WebView2 no longer freezes everything**: The login window was locking up for several seconds while Edge initialized in the background. Now it loads asynchronously - you'll see "Authenticating with SmartLink..." while it warms up, and your screen reader keeps working. Much better.
- **The 100ms focus bug is dead**: NVDA users were getting stuck in limbo because we were yanking focus around too aggressively. The login page now waits until it's actually ready before announcing "Login page ready." Patience is a virtue, even for code.
- **Tolk instead of CrossSpeak**: Swapped out the screen reader library for better NVDA, JAWS, and SuperNova support. Same announcements, better compatibility.
- **No more "access denied"**: Moved WebView2's cache folder to your AppData so it stops complaining when JJFlexRadio runs from Program Files.

If SmartLink was hanging or your screen reader went mysteriously silent during login, give this version a try.

## 4.1.9.0 - The .NET 8 Migration Release
This is a big one. I finally ripped the band-aid off and migrated the entire codebase from .NET Framework 4.8 to .NET 8.0. Here's what changed:

- **Architecture**: JJFlexRadio now builds for both x64 (64-bit) and x86 (32-bit). The installer names include the architecture suffix so you know which one you're grabbing: `Setup JJFlexRadio_4.1.9_x64.exe` or `_x86.exe`.
- **SDK-style projects**: All C# projects converted to the modern SDK-style format. Cleaner, faster builds, better tooling support.
- **WebView2 for Auth0**: Replaced the ancient WebBrowser control with Microsoft Edge WebView2 for SmartLink authentication. This means modern TLS, better compatibility, and no more IE quirks.
- **TLS 1.3 support**: Now negotiates TLS 1.3 where available, with TLS 1.2 fallback. Removed all the conditional compilation gymnastics for .NET 4.8.
- **Native DLL loading**: Added `NativeLoader.vb` that automatically loads the correct architecture-specific native DLLs (Opus, PortAudio) at startup.
- **GitHub Releases**: Set up automated release workflow. When I push a version tag like `v4.1.9`, GitHub Actions builds both architectures and publishes installers to the Releases page.
- **Housekeeping**: Removed legacy radio support (Icom, Kenwood, Generic) since this is JJ*Flex*Radio after all. Also added FLEX-8400 and Aurora AU-510 to the rig table.

The migration was a multi-phase effort documented in our sprint archives. It involved converting all projects to SDK-style, updating target frameworks, handling native DLL loading, and migrating WebView2 for auth.

## 4.1.8.0
- Added Feature Availability tab in the Radio Info dialog with per-feature status (Diversity, ESC, CW Autotune, NR/ANF variants) and a refresh licenses button. Now, the "radio info" dialog, button renames from rig to radio, has two tabs, the first tells you everything about your radio i.e. serial etc. The Feature availability tab is your window on the world of features that you have access to via your license. If Flex releases a new feature that changes radio operations/adds new goodies and you aren't a subscriber, the system will tell you what features you have and which ones are disabled due to your license. It does not tell you how long you have left on your sdr plus license or what type of license you have, SmartSDR can theoretically do that.
- Added an item under the actions menu which will open the radio info dialog while focussing on and in the feature availability tab.
- cleared the place up a bit from an accessibility perspective. Menus no long have & symbols sprinkled throughout the menu. Though ampersands are cool in their own right, they made menus hard to read with a screen reader. Also added support to tell you if a menu item is checked, unchecked, or unavailable. You'd think that would be easy and straightforward, but no.
- NR/ANF now list individual algorithms. Now, you will know that, for instance, RNN is only available on 8000 series radios. Now you will get a list of all of the supported noise mitigation options and if your radio is not cool for school anymore. Algorithms include Basic NR/ANF, RNN, NRF, NRS, NRL, ANFT, ANFL. See the Flex manual or  www.flexradio.com for more details.
- Now, if you have 1 SFU, you will not see diversity reception or E.S.C.
- Improved audio device handling: if you'd like to change your audio device, select the actions menu and then select "audio device setup ..." If no audio device was selected at setup, JJ Flex will ask you to select a sound device. This should fix reported errors that would occur if no audio device was selected.
- Tweaked labeling/accessibility for the Radio Info entry points.
## 4.1.7.0
I did a cleanup pass here but never shipped it. Think of this as a scratchpad release that I used to squash bugs and keep momentum.
## 4.1.60: Error reporting implemented
- I wired up crash reporting so you no longer need a debug build to send useful crash info. A crash now generates a dump + stack trace that I can actually use to fix things.

## 4.1.5, the subscription aware and wishful thinking update
- I hid controls your radio isn’t licensed for so we don’t tease features you can’t use.
- I tightened up the codebase by removing more JJ Radio leftovers to make things less confusing to maintain.
- Subscribed features now show up both in menus and on the main Flex filters page, so the UI reflects what you actually own.
- I added a Noise Control submenu under Actions (with an eye toward adding shortcuts later).
- Diversity/ESC now disappear when the radio can’t support them, with a clear “not supported” message so it’s obvious why.
- CW Autotune landed in Actions for CW mode; it finds the strongest CW signal using your configured sidetone.
- I added “Daily log trace” because it’s nerdy and useful: it auto-creates daily traces and archives the previous day.

## 4.1.0
- I pulled in FlexLib 4.1.3 so we stay current with upstream bug fixes and API changes.
- Continued the slow, careful work of wiring up the v4 noise/mitigation features.
- Added an ESC dialog (Enhanced Signal Clarity) for radios with enough SCUs.
- Started thinking about subscription-aware UI so we can align with SmartSDR+ gating.
## [4.0.5] - 2025-12-03
- I added the advanced NR/ANF controls (RNN, NRF/NRS/NRL, ANFT/ANFL) and made their gating license-aware with clear tooltips.
- I introduced a `FlexBase.DiversityReady` helper to wrap all the “can we do diversity?” checks in one place (license, antennas, slices, etc.).
- I removed DotNetZip in favor of `System.IO.Compression` and added safe extraction to close a known zip-slip risk.
- I expanded the radio registry so each Flex model maps to `FlexRadio` and we use capability checks instead of hardcoding behavior.
- I bumped version metadata to 4.0.5 to keep installers and release notes aligned.

## [4.0.4] - 2025-xx-xx
- Refactor: Continued migration to FlexLib 4.0 APIs
- Auth: Iterations on GUI auth/SmartLink page behavior

## [4.0.3] - 2025-xx-xx
- Refactor: Initial FlexLib 4.0 adoption across core radio paths
- UI: Stability fixes in Filters and Pan controls

## [4.0.2] - 2025-xx-xx
- Fix: SmartLink connection reliability improvements
- Build: Solution cleanup, reference alignment

## [4.0.1] - 2025-xx-xx
- Base: Start of 4.x line, compatibility with SmartSDR 4.0
- Infra: Project file updates and initial docs for missing features

## Upcoming
- **Audio Controls** - Audio Boost menu and hotkey for adjusting PC Audio gain, Local Flex Audio volume control, and possibly audio device hot-swap. See `docs/TODO.md` for the full feature backlog.
- **QRZ XML Lookup** - Auto-fill Name, QTH, State, Grid from QRZ on callsign tab-out. Secure credential storage via DPAPI.
- Diversity status announcements for screen reader users
- Meter announcements (ALC, SWR, signal strength) on demand
- Configurable "Recent QSOs" grid size (currently 20, will be a setting)


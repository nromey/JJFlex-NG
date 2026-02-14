# Changelog

All notable changes to this project will be documented in this file.

## 4.1.114.0 — Sprint 7: Modern Menu, Logging Polish, Bug Fixes (Don's Birthday Release 🎂)

Sprint 7 wrapped up the Modern menu build-out and put Logging Mode through its paces. Released on 2/14 — Valentine's Day and Don's birthday. Happy birthday Don! Five tracks merged to main, testing found 8 bugs, 5 fixed in this release — the other 3 deferred to Sprints 8-9 when we move to pure WPF.

### Modern Menu DSP & Filters (Track A)

- **DSP toggle speech**: All Modern menu DSP items (NR, NB, ANF, Neural NR, etc.) now speak their actual toggled state — "Noise Reduction on" / "Noise Reduction off". Previously they'd always say "on" because FlexLib's async property pattern returns stale values. Fixed with local variable pattern across all toggles.
- **Filter controls**: Narrow, Widen, and Shift filter menu items now speak the resulting filter width or shift value after each adjustment. Same async pattern fix.
- **"Coming soon" stubs**: Modern menu placeholders now speak "coming in a future update" when clicked, instead of silently doing nothing. Items stay enabled so screen readers can discover them.
- **CW hotkey feedback**: Ctrl+1 through Ctrl+7 CW message slots and F12 CW Stop now give feedback when no CW messages are configured.

### WPF Migration (Track B)

- **LogPanel → WPF**: The Logging Mode entry panel is now a WPF UserControl hosted via ElementHost. Tab order, field navigation, and screen reader announcements all work through the WPF control. The Recent QSOs grid is a proper WPF DataGrid with row-level screen reader support.
- **Station Lookup → WPF**: Station Lookup dialog rebuilt as a WPF Window with AutomationProperties on all controls.
- **Focus management**: Keyboard focus properly clears when switching between WPF and WinForms contexts — no more stuck focus or phantom key interception.

### Station Lookup Enhancements (Track C)

- **Log Contact button**: Look up a callsign in Station Lookup, click "Log Contact", and you drop straight into Logging Mode with call, name, QTH, state, and grid pre-filled. Dup check and previous contact lookup fire automatically for the pre-filled call.
- **Distance and bearing**: Station Lookup calculates great-circle distance and bearing from your operator grid square to the looked-up station's grid. Uses Haversine formula with Maidenhead grid conversion.

### Speak Status (Track D)

- **Speak Status hotkey**: Quick summary of current radio state — frequency, mode, band, and active slice — spoken to the screen reader on demand.
- **Status Dialog**: Planned as a full accessible WPF dialog with organized sections, but disabled for this release due to accessibility issues (BUG-020). Will be rebuilt properly in Sprint 8+.

### Configurable QSO Grid (Track E)

- **Adjustable grid size**: Set your preferred Recent QSOs count (5–100, default 20) in operator settings. The Logging Mode grid respects your choice instead of hardcoding 20.

### Bug Fixes

- **APF mode guard (BUG-017)**: Audio Peak Filter is a CW-only feature, but the toggle happily said "on" in SSB and other modes without actually doing anything. Now it tells you straight: "Audio Peak Filter is only available in CW mode." No more phantom toggles.
- **Dup count speech cleanup (BUG-018)**: The dup check and previous-contact lookup were both speaking at once with different numbers — "6 contacts" from one, "2 duplicates" from the other. Turns out they measure different things (total QSOs vs. matches on current band/mode), but hearing both was confusing. Silenced the dup speech for now; the "Previously worked, N contacts" announcement is the primary feedback, and the exclamation beep still alerts you to dups. Full unification comes in Sprint 8 with pure WPF.
- **Log Contact silence (BUG-019)**: Clicking "Log Contact" in Station Lookup used to stutter through a garbled speech attempt. You clicked "Log Contact" — you know you're entering Logging Mode. Now it enters silently and the screen reader naturally reads the pre-filled call sign field. Clean.
- **Status Dialog disabled (BUG-020)**: The Status Dialog was completely inaccessible — no tab stops, no close button, appeared off-screen. Rather than ship a broken dialog, Ctrl+Alt+S now speaks "Status Dialog coming in a future update. Use Speak Status for a quick summary." Will rebuild properly in pure WPF.
- **QSO grid count (BUG-021)**: The Recent QSOs grid was hardcoded to show 20 rows regardless of your operator setting. Now it respects whatever count you've configured.
- **QSO grid row announcements (BUG-022)**: Arrow through QSO grid rows and your screen reader said "JJFlexWPF.RecentQsoRow" instead of the callsign. Added a `ToString()` override so up/down arrow announces the callsign (e.g., "W1AW"). Left/right cell navigation works but NVDA double-reads cell values — that's a known WPF-in-WinForms interop quirk that'll be fixed with custom AutomationPeers in Sprint 8.

### Known Issues

- **"Unknown" on logging mode entry (R1)**: Screen reader briefly says "unknown" when entering Logging Mode. This is a WPF-in-WinForms ElementHost interop limitation — the WPF control's automation tree momentarily confuses the SR. Will resolve naturally with Sprint 8's full WPF conversion.
- **NVDA DataGrid cell double-read**: Left/right arrow in the QSO grid announces cell values twice in NVDA (e.g., "SSB SSB mode"). JAWS handles it correctly. Fixable with custom AutomationPeers once we're pure WPF.
- **Focus-on-launch**: App doesn't grab focus when launched from Explorer — you stay on the Explorer window. Will address post-Sprint 9.

### Deferred to Sprint 8-9 (WPF Conversion)

- **BUG-016**: Logging Mode exit focus sometimes lands on wrong control
- **BUG-020**: Full Status Dialog rebuild as accessible WPF dialog
- **BUG-023**: Connect-while-connected flow is messy

## 4.1.13.0 — Sprint 6: Callbook Fallback, QRZ Logbook, Hotkeys v2

This release packs two full feature tracks plus a major hotkey overhaul. The callbook system got a safety net (QRZ goes down? HamQTH picks up automatically), QRZ Logbook uploads your QSOs in real time, and every hotkey in the app now works reliably in every mode. That last part sounds like it should have always been true — turns out the menu system was quietly eating our Alt-key shortcuts. Not anymore.

### Callbook Fallback (Track A)

- **Callbook auto-fill**: Supports both QRZ.com (XML API, requires subscription) and HamQTH.com (free). Configure which service to use in your operator profile under the new Callbook Lookup section. Credentials are stored per-operator. Auto-fill only touches empty fields — anything you've typed or that came from the previous-contact lookup stays put.
- **QRZ → HamQTH auto-fallback (BUG-007)**: If QRZ login fails three times in a row, JJFlexRadio silently switches to the built-in HamQTH account so lookups keep working. You get a one-time notification explaining the fallback. No more silent lookup failures when your QRZ subscription lapses.
- **HamQTH built-in account for LogPanel (BUG-008)**: If you select HamQTH as your callbook but don't have personal credentials, LogPanel now falls back to the built-in "JJRadio" HamQTH account automatically.
- **Credential migration**: If you had HamQTH credentials from the old system, they automatically migrate to the new callbook settings on first load.
- **Credential validation on save**: Operator profile tests your credentials when you click Update. Clear error messages for QRZ subscription issues and HamQTH login failures.
- **Secure credential storage**: Callbook passwords encrypted using Windows DPAPI.

### QRZ Logbook Upload (Track B)

- **Real-time QRZ Logbook**: Log a QSO in Logging Mode and it automatically uploads to your QRZ.com logbook. Enable in operator settings with your QRZ API key.
- **Validate button**: Test your QRZ Logbook API key right from settings — shows your QRZ log stats (total QSOs, etc.) to confirm everything's connected.
- **Circuit breaker**: If QRZ's server has problems, uploads pause after 5 consecutive errors and resume automatically later. Your local log is always saved regardless.
- **Graceful degradation**: Invalid API key? QRZ down? Your QSO still saves locally with no errors. QRZ issues are logged silently — you'll see them in the trace if you look, but they won't interrupt your operating.

### Hotkeys v2 (Track C)

- **Scope-aware hotkeys**: Every hotkey now belongs to a scope — Global (works everywhere), Radio (Classic + Modern only), or Logging (Logging Mode only). The same physical key can do different things depending on your mode: Alt+C is CW Zero Beat in Radio mode but jumps to the Call Sign field in Logging mode.
- **Central key dispatcher (BUG-010)**: Rewrote the keyboard routing so ALL hotkeys go through the scope-aware registry BEFORE the menu system sees them. This fixed F6 not switching panes in Logging Mode, Alt+C/Alt+S opening menus instead of executing commands, F1 not working in Logging, and Ctrl+/ (Command Finder) being intermittent.
- **Command Finder (Ctrl+/)**: Type a few characters and it searches all available commands by name, keywords, and synonyms. Shows the current hotkey binding next to each match. Select one and press Enter to execute it immediately. The result list updates as you type and announces the count to your screen reader. Scope-aware — Radio commands don't clutter the list when you're in Logging Mode.
- **Tabbed Hotkey Editor**: Three tabs — Global, Radio, Logging. Select a command, press the new key you want, done. Conflict detection auto-clears the old binding (VS Code style) so you can never save duplicate keys (BUG-012).
- **CW message migration**: F5–F11 CW messages automatically migrated to Ctrl+1–7 (one-time, transparent). F12 still stops CW. The old F-keys are freed up for future features.

### Other Fixes

- **"Coming soon" stubs speak (BUG-011)**: Modern mode placeholder menu items now include "coming soon" directly in the menu text so all screen readers announce it. Previously only AccessibleName had the suffix, which JAWS and NVDA ignored on disabled items.
- **Hotkey XML corruption (BUG-014)**: Combined Keys values (e.g., Ctrl+C) were getting decomposed by XmlSerializer into unparseable flag names, silently losing your bindings on restart. Hotkeys are now stored as integers with a contamination guard — if a loaded key is Keys.None but the built-in default has a real binding, the default is preserved.
- **Full Log Form access (Ctrl+Alt+L)**: Pop open JJ's full LogEntry form as a modal from Logging Mode.
- **Station Lookup upgraded (Ctrl+L)**: Uses your configured callbook service with DX country announcements.
- **Natural screen reader announcements**: Callbook results spoken as values — "Robert, College Station, Texas" — not field names.
- **UTC timestamp fix (BUG-005)**: Each QSO now gets a fresh timestamp (was stuck at first QSO's time).
- **Callbook announcement queueing (BUG-006)**: Callbook results queue after field announcements during fast tabbing.
- **Modern menu accessibility (BUG-009)**: All Modern mode menus and submenus now have proper AccessibleName.
- **Ctrl+Shift+M in Logging Mode**: Previously ignored the toggle. Now exits Logging Mode first, then switches Classic/Modern as expected.
- **F6 pane switch cleanup**: Removed a duplicate Speak call and ensured the key is marked as handled before the routine runs, preventing edge cases where F6 could leak to the MenuStrip.

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

## Upcoming (Sprint 7+)
- **WPF migration**: LogPanel and Station Lookup converting from WinForms to WPF for native UIA support — fixes DataGridView row-0 indexing, automatic AutomationProperties, better JAWS/NVDA reliability
- **Station Lookup enhancements**: "Log Contact" button (pre-fills Logging Mode from lookup), distance and bearing calculation from operator grid to station grid
- **Plain-English Status**: Speak Status hotkey for concise spoken radio summary, Status Dialog for full accessible display
- **Configurable QSO grid size**: Choose how many recent QSOs appear in Logging Mode (currently hardcoded at 20)
- **Slice Menu**: Full slice operating model in Modern mode (Sprint 8)
- **QSO Grid**: Filtering, paging, and row-1 indexing (Sprint 8)
- See `docs/planning/vision/JJFlex-TODO.md` for the full feature backlog


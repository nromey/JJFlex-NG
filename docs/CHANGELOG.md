# Changelog

All notable changes to this project will be documented in this file.

## 4.1.114.0 — Don (WA2IWC)'s Birthday Release

Released on 2/14 — Valentine's Day and Don's birthday. Happy birthday Don!

This release wraps up the Modern menu build-out, polishes Logging Mode, and adds some features I've been wanting for a while. I also spent quality time actually *using* the app with a screen reader and found a bunch of things that needed fixing. Eight bugs total — five squashed in this release, three deferred to the next round.

### What's New

- **Modern menu DSP controls actually talk back now**: All the DSP toggles in the Modern menu — NR, NB, ANF, Neural NR, the whole gang — now tell you whether they're on or off when you toggle them. Before, they'd cheerfully say "on" every single time, even when you were turning them off. That's fixed. Same goes for the filter controls (Narrow, Widen, Shift) — they now announce the resulting filter width or shift value. Keep in mind, that as we remodel the living room and the kitchen, that while in reconstruction mode, you'll have to use the tools menu, and select available features if you want to know what features your radio supports. Upcoming filters will become sadly unavailable if your radio is not subscribed to that particular feature or mode. You'll also notice, later, that you'll be able to hear what hotkeys to press to activate your menu item as you arrow through the place. Be patient with us, this reconstruction ain't the most fun thing in the world, but it will definitely allow us to really shine when it comes to accessibility, and, at the same time, not confuse you with ... confusing announcements, keystrokes, and speaking kerfuffles along the way. I hate to tell you that it's gonna be great before it's great, but ... really ... truly ... IT'S GONNA BE GREAT, REALLY GREAT, MAN!
- **"Coming soon" items speak up**: Those placeholder items in the Modern menu that didn't do anything? They now tell you "coming in a future update" when you click them. At least now you know *why* nothing happened.
- **CW message feedback**: Ctrl+1 through Ctrl+7 play your stored CW messages on the air with the built-in keyer. F12 stops CW. If you haven't set up any CW messages yet, these hotkeys now tell you that instead of doing nothing.
- **Log Contact button in Station Lookup**: Look up a callsign (Ctrl+L), click "Log Contact," and boom — you're in Logging Mode with the call, name, QTH, state, and grid already filled in. Dup check and previous contact lookup fire automatically. It's the workflow: hear a station, look them up, log the contact — three steps, no retyping.
- **Distance and bearing in Station Lookup**: After a lookup, you'll see how far away the station is and what direction to point your beam (or wish you had a beam). Calculated from your operator grid square to theirs. For now, access your operator settings within the operator menu, and enter your gridsquare to be able to look this snazzy info up. Trust us, it'll get way easier soon!
- **Speak Status hotkey**: Get a quick spoken summary of your radio's current state — frequency, mode, band, active slice — without navigating anywhere.
- **Status Dialog (disabled for now)**: I built a full status dialog, but honestly it was a mess — couldn't tab through it, no close button, window appeared somewhere in outer space. Rather than ship something broken, Ctrl+Alt+S now just tells you it's coming in a future update and suggests using Speak Status instead. I'll rebuild it properly in a future release. Remember that construction I told you about earlier, construction's just ... messy. At least I told you so you don't open a door and fall out into space, then down, down, down .... splat.
- **Configurable QSO grid size**: Go to your operator settings and set how many recent QSOs you want to see in Logging Mode — anywhere from 5 to 100 (default is still 20). The grid now actually respects this setting instead of ignoring it.
- **Audio Peak Filter mode guard**: APF is a CW-only feature, but JJFlexRadio used to happily toggle it in SSB mode without doing anything. Now if you try it outside CW, you get a clear message: "Audio Peak Filter is only available in CW mode."

### Fixes

- **QSO grid rows announce callsigns**: Arrow through the Recent QSOs grid and your screen reader now says the callsign (e.g., "W1AW") instead of some cryptic internal type name. Left/right arrow to navigate individual cells within a row still works. NVDA has a quirk where it reads cell values twice — working on that for the next release.
- **Dup count no longer argues with itself**: When you entered a duplicate callsign, two different things were speaking at once — "6 contacts" from one source, "2 duplicates" from another. Turns out they were counting different things (total QSOs vs. matches on the current band and mode), but hearing both was just confusing. Cleaned it up so you hear one clear "Previously worked, N contacts" announcement, plus the warning beep.
- **Log Contact doesn't stutter anymore**: Clicking "Log Contact" used to produce a garbled speech attempt as multiple announcements tripped over each other. You clicked "Log Contact" — you know you're going to Logging Mode. Now it enters quietly and your screen reader just reads the pre-filled call sign field. Much cleaner, much cooler I think.

### Known Quirks

- **"Unknown" on logging mode entry**: Your screen reader might briefly say "unknown" when entering Logging Mode. This is a side effect of mixing two different UI technologies together and will go away in the next major release when I finish converting the app to a single technology.
- **NVDA double-reads in QSO grid**: If you arrow left/right through QSO grid cells in NVDA, it reads cell values twice (e.g., "SSB SSB mode"). JAWS handles it fine, so if you wanna see where we're going soon, run JAWS and you'll hear a spreadsheet-like experience where you'll hear rows and columns read, an informational nirvana, and a sneak peek into future grids we've got planned for ya. Will fix this properly in the next release.
- **App doesn't grab focus on launch**: If you double-click the exe in Explorer, the app starts but focus stays on the Explorer window. Annoying — on my list to fix.

## 4.1.13.0 — Callbook Fallback, QRZ Logbook, Hotkeys v2

This release packs two full feature sets plus a major hotkey overhaul. The callbook system got a safety net (QRZ goes down? HamQTH picks up automatically), QRZ Logbook uploads your QSOs in real time, and every hotkey in the app now works reliably in every mode. That last part sounds like it should have always been true — turns out the menu system was quietly eating our Alt-key shortcuts. Not anymore.

### Callbook Fallback

- **Callbook auto-fill**: Supports both QRZ.com (XML API, requires subscription) and HamQTH.com (free). Configure which service to use in your operator profile under the new Callbook Lookup section. Credentials are stored per-operator. Auto-fill only touches empty fields — anything you've typed or that came from the previous-contact lookup stays put.
- **QRZ to HamQTH auto-fallback**: If QRZ login fails three times in a row, JJFlexRadio silently switches to the built-in HamQTH account so lookups keep working. You get a one-time notification explaining the fallback. No more silent lookup failures when your QRZ subscription lapses.
- **HamQTH built-in account**: If you select HamQTH as your callbook but don't have personal credentials, JJFlexRadio falls back to its built-in HamQTH account automatically.
- **Credential migration**: If you had HamQTH credentials from the old system, they automatically migrate to the new callbook settings on first load.
- **Credential validation on save**: Your operator profile tests your credentials when you click Update. Clear error messages for QRZ subscription issues and HamQTH login failures are all yours if you need them, and you know you need them, right?
- **Secure credential storage**: Callbook passwords are now encrypted using Windows DPAPI — a nifty Microsoft feature that ties encryption to your Windows login, useless if someone copies the file, but if you're logged in, we autodecrypt the file.

### QRZ Logbook Upload

- **Real-time QRZ Logbook**: Log a QSO in Logging Mode and it automatically uploads to your QRZ.com logbook. Enable in operator settings with your QRZ API key. To find your key, login to Qrz.com, click your callsign, this may require that you use your virtual mouse to double click it, then select your logbook. Select settings. Find the first table on the page. That table contains the API for your logbook. If you have multiple logbooks, each of them has their own API, make sure you use the API for the logbook you want JJFlexRadio to have access to. Enter the listed API into JJFlexRadio and click validate.
- **Validate button**: Test your QRZ Logbook API key right from settings — shows your QRZ log stats (total QSOs, etc.) to confirm everything's connected.
- **Circuit breaker**: If QRZ's server has problems, uploads pause after 5 consecutive errors and resume automatically later. Your local log is always saved regardless.
- **Graceful degradation**: Invalid API key? QRZ down? Your QSO still saves locally with no errors. QRZ issues are logged silently — you'll see them in the trace if you look, but they won't interrupt your operating.

### Hotkeys v2

- **Scope-aware hotkeys**: Every hotkey now belongs to a scope — Global (works everywhere), Radio (Classic + Modern only), or Logging (Logging Mode only). The same physical key can do different things depending on your mode: Alt+C is CW Zero Beat in Radio mode but jumps to the Call Sign field in Logging mode.
- **Keyboard routing rewrite**: ALL hotkeys now go through the scope-aware registry BEFORE the menu system sees them. This fixed F6 not switching panes in Logging Mode, Alt+C/Alt+S opening menus instead of executing commands, F1 not working in Logging, and Ctrl+/ (Command Finder) being intermittent.
- **Command Finder (Ctrl+/)**: This cool little search utility can be the unsung hero while you're operating in a contest and want to know what key to press when, when you want to do what in JJFlexRadio. Type a few characters and it searches all available commands by name, keywords, and synonyms. Shows the current hotkey binding next to each match. Select one and press Enter to execute it immediately. The result list updates as you type and announces the count to your screen reader. Only shows commands relevant to your current mode.
- **Tabbed Hotkey Editor**: Three tabs — Global, Radio, Logging. Select a command, press the new key you want, done. Conflict detection auto-clears the old binding so you can never save duplicate keys.
- **CW message migration**: F5-F11 CW messages automatically migrated to Ctrl+1-7 (one-time, transparent). F12 still stops CW. The old F-keys are freed up for future features.

### Other Fixes

- **"Coming soon" stubs speak**: Modern mode placeholder menu items now include "coming soon" directly in the menu text so all screen readers announce it. We thought they did before, but nope.
- **Hotkey corruption on restart**: Your key bindings were getting corrupted when saved, silently reverting to defaults. Fixed — your custom hotkeys now survive restarts.
- **Full Log Form access (Ctrl+Alt+L)**: Pop open JJ's full LogEntry form as a modal from Logging Mode if you need or want to access or edit all aspects of your log.
- **Station Lookup upgraded (Ctrl+L)**: Uses your configured callbook service with DX country announcements to "look" up data about a station. Simply enter the station's call "What country is he from?" and whammo, get contact data you can use at your fingertips.
- **Natural screen reader announcements**: Callbook results spoken as values — "Santa, North Pole" — not field names.
- **UTC timestamp fix**: Each QSO now gets a fresh timestamp (was stuck at first QSO's time).
- **Callbook announcement queueing**: Callbook results queue after field announcements during fast tabbing, so things don't talk over each other.
- **Modern menu accessibility**: All Modern mode menus and submenus now have proper screen reader labels.
- **Ctrl+Shift+M in Logging Mode**: Previously ignored the toggle. Now exits Logging Mode first, then switches Classic/Modern as expected.

## 4.1.12.0 — Logging Mode

This is the one I've been building toward. JJFlexRadio now has a dedicated Logging Mode — press Ctrl+Shift+L from anywhere in the app and you're in a clean, focused QSO entry screen. No menus full of radio controls you don't need mid-QSO, no hunting for the right form. Just you, your log, and the radio.

- **Quick-entry panel**: Call, RST Sent/Rcvd, Name, QTH, State, Grid, Comments — all in a tight Tab-order layout. Freq, mode, band, and UTC time auto-fill from the radio when you start a QSO. Press Enter to log, and the fields reset for the next one. It's the workflow I always wanted.
- **Radio pane**: A slim status strip on the left showing frequency, mode, band, and tune step. F6 toggles focus over to it, and your screen reader announces "Radio pane" so you know where you are. Arrow keys tune the radio (Up/Down by step, Shift for coarse, Left/Right to change step size). Ctrl+F pops the manual frequency dialog. You don't have to leave Logging Mode just to nudge the VFO. Tab stays in the log entry fields — you can't accidentally wander into the radio pane by tabbing.
- **Recent QSOs grid**: The bottom half of the screen shows your recent QSOs — Time, Call, Mode, Freq, RST Sent, RST Rcvd, Name. JAWS and NVDA navigate it natively with arrow keys (row/column announcements, the whole deal). It auto-updates when you log a new contact.
- **Previous contact lookup**: Tab out of the Call field and JJFlexRadio instantly checks your entire log. If you've worked them before, you'll hear something like "W1AW — 3 previous contacts, last on 2026-01-15, 20m CW" from your screen reader. Name and QTH auto-fill from the previous contact too. You can Tab to the info field to re-read it if you missed the announcement.
- **Dup checking**: If you've already worked a station (matching your dup type — call only, call+band, etc.), you get a beep and a screen reader warning when you Tab out of the call sign field. It's warn-only, not blocking — you can still save the contact if you want. The dup dictionary loads from the log file at startup, so even after a restart it remembers who you've worked.
- **Field hotkeys**: Alt+C (Call), Alt+T (RST Sent), Alt+R (RST Received), Alt+N (Name), Alt+Q (QTH), Alt+S (State), Alt+G (Grid), Alt+E (Comments). Ctrl+N clears the form, Ctrl+W saves. All mnemonics make sense — T for senT, R for Received.
- **F6 pane switching**: Standard Windows F6/Shift+F6 toggles focus between the Radio pane and the Log entry pane. Feels natural.
- **Mode round-trip**: Ctrl+Shift+L drops you into Logging Mode, and pressing it again takes you right back to Classic or Modern — whichever you were using. Your field values survive the round-trip too.
- **Close protection**: Try to close the app with an unsaved entry and you'll get a save/discard/cancel dialog. If fields are missing, the dialog tells you what's needed before you click Yes.
- **Escape to clear**: Press Escape to clear the form. First time it asks for confirmation with a "Don't ask me again" checkbox for pileup mode.
- **Log Characteristics in Logging Mode**: Ctrl+Shift+N opens Log Characteristics without file conflicts. Previously this would crash because the log file was locked. Log characteristics allow you to edit characteristics of your log and create a new log file if you need to do that.
- **SKCC WES form retired**: The old contest-specific SKCC WES log form is removed. Logging Mode replaces it with a general-purpose approach. We're looking into a contest creator/configurator that you might just see somewhere down the road.
- **Screen reader audit**: Every control has proper labels and roles. Mode transitions, tune step changes, previous contact lookups — all announced. Nothing happens silently.

If you've been opening JJFlexRadio just to log a few CW QSOs and wished the UI would get out of the way, this is that update.

## 4.1.11.0 — Classic/Modern Mode, Auto-Connect & Audio Fix

This one's about giving you choices. JJFlexRadio now has two UI modes — Classic for the "if it ain't broke" crowd, and Modern for those of us who want a cleaner, more accessible interface that's built for screen readers from the ground up. Plus, auto-connect means the app just connects to your radio when it starts, and we finally fixed the "why is this so quiet?" WAN audio problem.

### Classic vs. Modern Mode

This is a big one for how JJFlexRadio feels day to day. You now have two ways to use the app:

- **Classic mode**: Everything you're used to. Same menus, same layout, same muscle memory. If you've been using JJFlexRadio for years and you like it the way it is, Classic is your friend. Nothing changes, nothing moves, nothing breaks. Don loves Classic mode, and honestly, we respect that.
- **Modern mode**: A brand new menu structure designed from scratch with screen reader accessibility as priority number one. Menus are organized by what you're actually doing — Radio, Slice, Filter, Audio, Tools — instead of the legacy layout. Every item has proper screen reader labels, checked/unchecked states, and clear announcements. It's where all the new features land first.
- **Ctrl+Shift+M** toggles between Classic and Modern instantly. No restart needed, no settings to dig through. Try Modern, don't like it? One keystroke back to Classic. Your preference is saved per operator, so if Don wants Classic and you want Modern on the same install, everybody's happy.
- **New installs default to Modern**, but you'll get a one-time prompt asking which you prefer. Existing users stay on Classic until they decide to switch.

Think of it like this: Classic is the cozy old shack with the tubes glowing in the corner. Modern is the new shack with the flat screens and the ergonomic chair. Same radio, same bands, same fun — just a different way to get to the controls. And you can walk between the two shacks anytime you want.

### Auto-Connect & Audio

- **Seamless auto-connect**: Pick your radio, right-click, "Set as Auto-Connect," and you're done. Next time you launch JJFlexRadio, it connects automatically. Works for both local radios and SmartLink remotes.
- **Friendly offline handling**: If your auto-connect radio isn't available (maybe Don finally turned his off), you get a proper dialog with choices: try again, pick a different radio, or disable auto-connect. No cryptic errors, no stuck screens.
- **Single radio rule**: Only one radio can be the auto-connect target. Set it on Radio B? It clears on Radio A. No ambiguity about which radio will connect.
- **Settings confirmation**: Before saving auto-connect, you see exactly what's being saved — radio name, low bandwidth preference, the works. No "wait, what did I just enable?" moments.
- **Fixed WAN audio volume**: If your laptop speakers sounded anemic through SmartLink, you're not imagining things. The remote audio path was outputting at about 16% of full scale. We added gain staging that boosts it to proper levels. Default is 4x (comfortable listening), adjustable in a future update.
- **Help page works again**: The .NET 8 migration broke the Help menu. Fixed with one line of smart code wizardry, thank you Claude.
- **Fresh native DLLs**: Rebuilt Opus and PortAudio from source with proper optimizations for both 64-bit and 32-bit architectures in mind.
- **Screen reader everywhere**: Connection states are all announced — connecting, connected, offline, disconnected. Nothing happens silently.

If you've been frustrated by clicking through the radio selector every single launch, or by turning your laptop volume to 100% just to hear the radio, this is your update.

## 4.1.10.0 — SmartLink Saved Accounts

Finally! This one's been on my list forever. You can now save your SmartLink login and stop typing credentials every ... single ... time you want to connect to a remote radio. I know, I know — it should have been there from day one.

- **Saved SmartLink accounts**: After logging in, JJFlexRadio asks if you want to save the account. Give it a friendly name like "Main ISS Flex Radio" (I can dream, right?), or "Club Station" and next time you click Remote, just pick it from the list. No more hunting for passwords or waiting for two-factor codes while your DX window closes.
- **Secure storage**: Your login tokens, little pieces of data that tell Flex Systems that you are in fact you, are encrypted using Windows DPAPI — tied to your Windows login. If someone copies the file to another machine, it's useless to them. No plaintext passwords, ever, not anymore that is.
- **Automatic refresh**: When your session expires (they do, eventually), JJFlexRadio quietly tries to refresh it. If that works, you won't even notice. If it fails, you'll need to log in again, but your saved accounts stick around.
- **Account housekeeping**: You can rename or delete saved accounts from the selector. Made a typo in the name? Fixed in two clicks.
- **Improved auth security**: I upgraded the auth flow to a more modern method that's more secure and actually allows the "remember me" feature to work properly. The old way literally couldn't do refresh tokens. Who knew?

When you click Remote and have saved accounts, you'll see the account picker. Want to log in fresh? Just hit "New Login."

## 4.1.9.1 — WebView2 & Screen Reader Fixes

This patch squashes some bugs that made SmartLink login a real pain, especially if you use a screen reader.

- **Login window no longer freezes everything**: The login window was locking up for several seconds while the browser initialized in the background. Now it loads asynchronously — you'll see "Authenticating with SmartLink..." while it warms up, and your screen reader keeps working. Much better.
- **The focus bug is dead**: NVDA users were getting stuck in limbo because we were yanking focus around too aggressively. The login page now waits until it's actually ready before announcing "Login page ready." Patience is a virtue, even for code.
- **Better screen reader support**: Swapped out the screen reader library for better NVDA, JAWS, and SuperNova support. Same announcements, better compatibility.
- **No more "access denied"**: Moved the browser cache folder to your AppData so it stops complaining when JJFlexRadio runs from Program Files.

If SmartLink was hanging or your screen reader went mysteriously silent during login, give this version a try.

## 4.1.9.0 — The .NET 8 Migration Release

This is a big one. I finally ripped the band-aid off and migrated the entire codebase from the old .NET Framework to the modern .NET 8. Here's what changed:

- **64-bit and 32-bit support**: JJFlexRadio now builds for both x64 (64-bit) and x86 (32-bit). The installer names include the architecture suffix so you know which one you're grabbing.
- **Modern auth for SmartLink**: Replaced the ancient Internet Explorer-based login with Microsoft Edge for SmartLink authentication. Modern security, better compatibility, no more IE quirks.
- **TLS 1.3 support**: Now negotiates TLS 1.3 where available, with TLS 1.2 fallback. Your connections are as secure as they can be.
- **Smart native DLL loading**: Automatically loads the correct 64-bit or 32-bit audio libraries at startup. No more manual file shuffling.
- **Housekeeping**: Removed legacy radio support (Icom, Kenwood, Generic) since this is JJ*Flex*Radio after all. Also added FLEX-8400 and Aurora AU-510 to the rig table.

## 4.1.8.0 — Feature Availability & Accessibility

- **Feature Availability tab**: The Radio Info dialog now has a second tab that shows exactly which features your radio and license support — Diversity, ESC, CW Autotune, NR/ANF variants. If Flex releases a new feature and you aren't a subscriber, you'll see which features are available and which aren't. No guessing.
- **Quick access from Actions menu**: One click opens Radio Info straight to the Feature Availability tab.
- **Menu accessibility cleanup**: Menus no longer have & symbols sprinkled throughout, which were confusing screen readers. Also added support to tell you if a menu item is checked, unchecked, or unavailable. You'd think that would be easy and straightforward, but no.
- **NR/ANF algorithm breakdown**: Now lists individual algorithms (Basic NR/ANF, RNN, NRF, NRS, NRL, ANFT, ANFL) and tells you which ones your radio supports. RNN is 8000 series only — now you'll know why it's not showing up on your 6300.
- **Single-SCU radio awareness**: If your radio has one SCU, you won't see Diversity Reception or ESC controls cluttering up the interface.
- **Audio device setup**: Actions menu now has "Audio Device Setup..." for changing your sound device. Also fixes errors that occurred when no audio device was selected.

## 4.1.7.0

I did a cleanup pass here but never shipped it. Think of this as a scratchpad release that I used to squash bugs and keep momentum.

## 4.1.6.0 — Error Reporting

- Wired up crash reporting so you no longer need a debug build to send useful crash info. A crash now generates a dump and stack trace that I can actually use to fix things.

## 4.1.5.0 — Subscription Awareness

- Hidden controls your radio isn't licensed for so we don't tease features you can't use.
- Tightened up the codebase by removing more legacy leftovers to make things less confusing to maintain.
- Subscribed features now show up both in menus and on the main Flex filters page, so the UI reflects what you actually own.
- Added a Noise Control submenu under Actions (with an eye toward adding shortcuts later).
- Diversity/ESC now disappear when the radio can't support them, with a clear "not supported" message so it's obvious why.
- CW Autotune landed in Actions for CW mode — it finds the strongest CW signal using your configured sidetone.
- Added "Daily log trace" because it's nerdy and useful: it auto-creates daily traces and archives the previous day.

## 4.1.0.0

- Pulled in FlexLib 4.1.3 so we stay current with upstream bug fixes and API changes.
- Continued wiring up noise/mitigation features.
- Added an ESC dialog (Enhanced Signal Clarity) for radios with enough SCUs.
- Started building subscription-aware UI to align with SmartSDR+ feature gating.

## 4.0.5.0

- Added the advanced NR/ANF controls (RNN, NRF/NRS/NRL, ANFT/ANFL) and made their availability license-aware with clear tooltips.
- Added a helper to wrap all the "can we do diversity?" checks in one place (license, antennas, slices, etc.).
- Replaced DotNetZip with built-in compression to close a known security issue.
- Expanded the radio registry so each Flex model uses capability checks instead of hardcoded behavior.

## 4.0.4.0
- Continued migration to FlexLib 4.0 APIs.
- Auth/SmartLink page improvements.

## 4.0.3.0
- Initial FlexLib 4.0 adoption across core radio paths.
- Stability fixes in Filters and Pan controls.

## 4.0.2.0
- SmartLink connection reliability improvements.
- Solution cleanup.

## 4.0.1.0
- Start of the 4.x line, compatibility with SmartSDR 4.0.
- Initial docs for missing features.

# Changelog

All notable changes to this project will be documented in this file. The opinions and coolness of developers, radio amateurs, and operatives is long, but you might like it. Read at your leisure for all changes for each release.

## 4.1.15.1: stop the presses, we got us a breaker breaker emergency

The last version forgot how to connect to locally connected radios. In other words, we concentrated on connecting to remote radios so much that we forgot how to access radios you have sitting right next to you. Never fear, it was an easy fix. In short, we kicked it in the pants, and made sure that the application was OK with not displaying SmartLink Logon when it isn't necessary. Sorry about all that.

## 4.1.15.0: The More than just rearranging deck chairs on the Titanic version

This one's big. Twelve sprints of work since the Birthday Release — that means we slammed a ton of features into twelve marathon coding sessions for the fun and enjoyment of amateur radio operators worldwide. This version has not been published as a beta version. Soon friends, soon!

The headline: we rebuilt the entire application from the ground up. The old interface is gone. In its place is a modern, accessible app that was designed from day one so your screen reader can tell you everything that's happening. New features include a complete PTT safety system, band jumping, license-aware tuning, an updated settings dialog, a ScreenFields panel for hands-on radio control that is efficiently organized so that you don't have to tab 72 times to get to what you want or use the menus, filter presets and nudging--something that just isn't done yet by others, a profile reporter, and a complete audio overhaul. We also made the SmartLink connection ... connect reliably and quickly. As always, let me know via k5ner@eml.cc if you have questions, comments, or reports related to this slug of fun.

### The Big One: Complete UI Rebuild

We rebuilt the entire Ke5AL, Jim-created JJFlexRadio interface from scratch. The old technology that powered the app's screens and controls has been replaced with a modern framework that gives us much better control over what your screen reader hears and how you navigate. It means only supporting Windows 10 and greater, but the accessibility changes are real, and the framework that is used continues to be updated by Microsoft--worth a limited JJFlex, cause who's using Windows 7 alone, these days. If you are, let us know--I'll give you some free software counselling.
Here's what all this rigmarole means for you:

- **Menus that actually work with screen readers**: We tried three different menu systems before we found one that doesn't confuse JAWS and NVDA. The menus now behave like proper Windows menus — your screen reader says "Radio menu" not "Radio collapsed expanded collapsed." Arrow through items, hear their names and hotkeys, press Enter to activate. Classic, Modern, and Logging modes each get their own menu set tailored to what you're doing.
- **Everything speaks**: Connection states, mode changes, frequency updates, error messages — nothing happens silently anymore. If JJFlexRadio does something, your screen reader knows about it. Error dialogs that used to appear behind other windows now show up front and center, and your screen reader announces them before the dialog even opens. If you use a braille display, you'll get those messages as well. We plan to implement a verbosity system so that Jj Flex won't spam you if you don't need spamming/screen reader speech.
- **Radio Selector rebuilt**: The dialog where you pick your radio has been completely rebuilt. It's faster, more accessible, and runs on the main thread so your screen reader doesn't lag. The first radio in the list is auto-selected so you can just hit Enter if you only have one rig. We also call it the "radio selector", we know you have a rig but ... the plan is to implement support for other transceivers and receivers. We therefore thought that radio would fit our upcoming plans for JJ Flex.
- **SmartLink authentication improved**: If you're using JJ Flex to connect to your radio or a friend's radio remotely, Flex Systems use Smartlink to make it happen. When you log into your account, the system sends you tokens which become keys to your radio and remote connections.  These tokens expire, and that created all kinds of havoc when we changed the way that we securely connect to your radio.  Expired login tokens are detected before they cause errors. The old phantom authentication windows that would pop up randomly are gone, replaced by useful screen reader speech that gives you details on what is happening. The station name timeout was bumped to 45 seconds so slower internet connections don't get cut off mid-handshake--this radio operator lost lots of days of sleep agonizing over that but Claude just kept trying new things and wondering why I was insanely tracking why connecting to a radio either halfway around the world or across the country just doesn't happen. The long and short of it is that it just works now, every time, and if problems crop up later, we've got lots of tools that are silently implemented so that we can track down the ghost in the wires.
- The screen reader smoothly moves to logging mode when you need it. We had all kinds of craziness which happened that we had to fix, mainly your screen reader said "unknown" over and over again. Trust me, be glad you didn't get to experience that.
- **NVDA and JAWS double-reads in QSO grid fixed**: Arrowing left/right through QSO grid cells in logging mode no longer double speak. It was super annoying and we crushed the stupidity. 

### ScreenFields Panel

This is a brand new way to interact with your radio's settings. Based on Jim's well-known and well-loved screen fields menu, it's now available in modern and classic mode, mainly because, in its current form, we found it to be super useful. Instead of hunting through menus to find a noise reduction toggle or a power level slider which was how you found screen fields in previous versions, you now have a panel of expandable categories right on the main screen. Tab past the VFO frequency panel and you'll find:

- **Five categories**: Noise Reduction & DSP, Audio, Receiver, Transmission, and Antenna are available in the screen fields user interface. Each one can be skipped by tabbing, and you can then expand the category that you need by pressing space or enter. Collapse it to keep your UI clean, or leave it there, it's all up to you.
- **Keyboard hotkeys to jump straight there**: Ctrl+Shift+N (Noise Reduction & DSP), Ctrl+Shift+U (Audio), Ctrl+Shift+R (Receiver), Ctrl+Shift+T (Transmission), and Ctrl+Shift+A (Antenna) jump you directly to a screen fields category. If the panel is hidden, it shows up. If the category is collapsed, it expands. If it's already open, it collapses as you hit the hotkey. One hotkey does the right thing no matter where you start.
- **Real controls, not menu items**: Checkboxes for toggles (Neural NR, Noise Blanker, VOX), value sliders for levels (Volume, NR Level, TX Power), cycle controls for modes (AGC: Off → Slow → Medium → Fast). Arrow keys adjust values, Space toggles checkboxes, and your screen reader announces every change.
- **Page Up and Page Down jump by 10x**: TX Power goes up by 10 watts per press instead of 1. Enter any numeric field, type a number, press Enter to set it directly. Home and End jump to min and max.
- **Smart focus management**: Expanding a category moves focus to the first control inside it. Collapsing returns focus to the frequency display. Escape from any field takes you back to the frequency display.
- **Hotkeys at a glance**: Ctrl+Shift+N (DSP), Ctrl+Shift+U (Audio), Ctrl+Shift+R (Receiver), Ctrl+Shift+T (Transmission), Ctrl+Shift+A (Antenna).

### Panadapter Braille Display

If you use a refreshable braille display, you can now "see" the band activity. The panadapter data is rendered as a text representation on your braille display — signal peaks show up as characters you can feel under your fingers. PageUp and PageDown jump between frequency segments, and the current segment's low and high frequencies are labeled so you know where you are on the band.

### PTT Safety System

- **Spacebar won't accidentally transmit anymore**: PTT now requires Ctrl+Space to hold-to-talk and Shift+Space to lock. Plain spacebar in the frequency field does nothing — no more accidental transmissions. You'll hear a chirp when you key up and a lower tone when you go back to receive, so you always know your TX state even without headphones. Since our last public release which required you to tab to transmit via the boring transmit button. Now, you can transmit from anywhere you are in the application.
- **"Am I transmitting?" hotkey**: Hit Alt+Shift+S anytime and your screen reader tells you whether you're transmitting, what mode (hold or locked), and how much time you have left before the safety timeout kicks in. See notes further down detailing the safety features built into our humble JJ Flex PTT button. On a braille display, the status bar shows "Transmitting" or "TX Locked" so you can glance down anytime.
- **TX health monitor**: If you lock TX and your mic is silent for more than 5 seconds, you'll hear "Check microphone." If your ALC is pegging, you'll hear "Microphone level too high." Think of it like Zoom telling you you're muted — but for ham radio. If you've got zero ALC and JJ Flex is set, the system will unkey if no signal is detected.
- **PTT speech can be muted**: In Settings → PTT, uncheck "Announce transmit/receive" and you'll only hear the chirp tones, not the spoken "Transmitting" / "Receiving." Great when you're on a hot mic and don't want your screen reader going out over the air.
- **Dummy Load Mode**: Toggle under Transmission — sets RF Power and Tune Power to zero so you can safely key up into a virtual dummy load. The timeout safety now works correctly in Dummy Load Mode as well (it used to cut you off after 60 seconds because it thought nobody was talking).
- **Speak Status now includes TX detail**: Ctrl+Shift+S tells you your PTT mode and time remaining when you're transmitting.

### Tuning & Band Navigation

- **Tuning in Modern mode**: Tuning your radio is now completely keyboard-driven. Up and Down arrow keys tune by your current step size. Press C to toggle between Coarse mode (1 kHz, 2 kHz, 5 kHz steps) and Fine mode (5 Hz, 10 Hz, 100 Hz steps). PageUp and PageDown cycle through the available steps within whichever mode you're in. Press Shift+S to hear your current mode and step size, and F to hear your current frequency. Your step sizes are saved per operator so they're waiting for you next time. It's like having a tuning knob under your fingertips — except it talks to you.
- **Band jumping with F-keys**: F3 through F9 jump you straight to 160m, 80m, 40m, 20m, 15m, 10m, and 6m. Shift+F3 through Shift+F6 get you to the WARC bands (60m, 30m, 17m, 12m). Alt+Up and Alt+Down step through bands sequentially. The radio remembers where you were on each band for each mode — jump from 40m CW to 80m and it puts you right back at your last CW frequency on 80. Stay tuned for channel hopping in WARC on the 30 m band for U.S. based amateurs.
- **License-aware tuning**: Tell the app your license class (Extra, Advanced, General, or Technician) in the new Settings dialog, and it'll beep and tell you when you tune across a band edge or into a portion you're not licensed for. Turn on TX Lockout and it won't let you transmit outside your authorized segment — even checks your filter width so a wide SSB signal near a band edge gets caught. Because nobody wants an FCC letter or an OO.
- **Choose how you hear frequencies**: Check out the new setting in Tools → Settings → Tuning to pick how frequencies are spoken. Pick from raw Hz ("14.225.000"), kilohertz ("14,225 kilohertz"), or megahertz ("14.225 megahertz"). You hold the power, choose what sounds natural to you, we won't judge.
- **Slices show as A, B, C, D**: Everywhere — the frequency display, the slice menu, spoken announcements — slices now show as letters like the radio itself does, not confusing numbers like "0" and "1."
- **Slice management from the keyboard**: Press period to create a new slice, comma to release one. Digit keys jump to a slice by number.
- **Step multiplier**: Type +5 or -5 in the frequency field to multiply your tune step — so +5 at 1 kHz step gives you 5 kHz per click. Handy for scanning across a band quickly.
- **S-meter readout**: Hit Alt+S to read your current signal strength. Quick and easy — no needless navigation, just hit the hotkey.
- **Reverse Beacon lookup**: Ctrl+Alt+R opens a Reverse Beacon Network lookup for your callsign right in the browser. See who's hearing you.

### Filters

- **Filter edge adjustment mode**: Previously, you could only shift filters up and down the band, "Squeeze" and "stretch" filter edges. Now, you can adjust edges individually, something that most boring ol transceivers can do. Here's how it works. Double-tap the bracket keys to enter edge-adjust mode, a left bracket double click selects the right edge and a double click of the right bracket grabs your right filter boundary. Use `[` and `]` to nudge just the low or high filter edge depending on which edge you grabbed when you grabbed it. You'll hear a tone on each adjustment and your screen reader will tell you the new width. If you forgot that you grabbed a filter you greedy person you, never fear. Escape will exit your grabby filter attempt. If you stop making adjustments to your filter, it will drop it like it's hot. Great for carving out interference on a crowded band, or being a grabby person should you like doing that.
- **Wide-open filter presets**: JJ Flex's filter preset now gives you a "wide open" mode which can give you some pretty incredible sounding receive audio on your end. Wide open equates to 4 kHz on SSB, 2 kHz for CW, 12 kHz for AM, and 16 kHz for FM. Sometimes you just want to hear everything your receiver can hear and that's OK. Have everything then!
- **Filter presets fixed for all modes**: Alt+[ and Alt+] presets now work correctly on LSB and digital lower sideband modes. Before, they were applying wrong filter values that could mute your audio. Sorry about that one.

### Settings & Tools

- **Settings dialog**: Finally! A proper tabbed settings dialog (under Tools → Settings) with tabs for PTT configuration, tuning preferences, license class, and audio. No more hunting through menus to change your timeout or step sizes. Check the other menu settings and let us know if you think we're missing any settings that should belong here.
- **Command Finder**: Press Ctrl+/ and you can search every command in the app — try "band", "transmit", "filter", "power", whatever you're looking for. It shows the hotkey next to each match, and you can press Enter to execute a command right from the search results. It defaults to showing commands for your current mode, with a "Show All" checkbox if you want to see everything.
- **F1 context help**: Press F1 in the frequency field, slice field, or other controls and you'll hear a quick tip about what that field does and how to use it. F1 anywhere else opens Command Finder. We here at JJ Flex HQ know that we have a penchant for adding lots of hotkeys, now you'll be able to peruse the list of keys you can use to change things on your transceiver wherever you're focussed.
- **Menu checkmarks**: Toggle items like Mute, ATU, VOX, and Squelch now show checkmarks when they're on. Your screen reader will say "checked" or "not checked" so you always know the current state of things. This is a normal, or should be a normal feature we shouldn't be amazed at, but give us a break, we totally changed the architecture of the application so let us be excited for things that you might find mundane. To get this to work right, we had to change a bunch of crap, enjoy mundane.
- **Profile Report**: Ever wonder what's actually different between your "Contest" and "Ragchew" profiles? Now you can find out. Tools → Profile Report loads each of your profiles one at a time, captures every setting, and shows you a side-by-side comparison. It also lists every meter the radio makes available, which is handy for troubleshooting. Don't worry — your original profile is restored when it's done.
- **Connection Tester**: Having trouble connecting to your radio? The radio selector dialog now has a built-in connection tester — pick a radio, hit Test, and it runs diagnostic cycles that measure connection timing, identify failures, and report the actual reason something went wrong. These test results can help us here at Jj Flex HQ (I'm an HQ of one intrepid ham, don't judge me). This is a great feature which can help us to help you with your SmartLink connection issues or verifying your setup before you get on the air.
- **Export and Import in the Modern menu**: Export Profiles and Import Profiles are now in the Tools menu if you're using the Modern layout. Before, you had to switch to Classic to find them. Please note: you can only import and export radios that exist on your local network. If you're using a friend's remote, or if you sent your radio to a friend's place so it can use their 25 acre antenna farm, you best ask either friend to log in and send you your profile file. Once JJ Flex HQ has a local network accessible radio, we hope to analyze how profiles are stored so that we can read them properly.

### Fixes

- **Neural NR license gating**: If your radio doesn't have the NR license, the menu tells you instead of showing a toggle that does nothing.
- **Connection Tester reports actual failure reasons**: Instead of always saying "station name timeout," you get the real reason. Sure, this may not **really** help you find a root cause, but if you're experiencing connection failure, this will help me get to the bottom of your issues, even if you forgot to turn on your radio before connecting.
- **SmartLink connection race fixed**: There was a nasty bug where connecting to a remote radio via SmartLink could hang if authentication and radio discovery happened in the wrong order. That's fixed — Connect is now decoupled from the Radio Selector so they can't step on each other and force condition bad underscore business.
- **Export over SmartLink won't silently fail anymore**: Profile export uses a trick where the radio connects back to your computer, which obviously can't work through the internet. Before, it would just... say "complete" and give you nothing. Now it tells you upfront that you need a LAN connection. Also found and fixed a cat-walked-on-keyboard bug in the error handler that was swallowing all export errors — yeah, really, a string of c's in a case statement. Classic Jim. Note to self: ask Jim's family if he had a cat
- **Menu speech cleanup**: Opening menus with Alt+R or Alt+T no longer causes stuttered or garbled speech. The timing between menu close and screen reader announcements is better tuned now.
- **Confirmation feedback on entry**: When you type a frequency with Ctrl+F or enter a number in a value field, you now hear a short confirmation clunk and spoken feedback. No more wondering "did it take?". Let us know if this clunk is too subtle, we can spice it up **a lot** if required.

### Under the Hood

- Complete UI rebuild: the entire application's UI was converted from the old WinForms technology to modern Windows Presentation Foundation (WPF) over the course of four sprints. About 8,000 lines of dead code were deleted in the process.
- Menus rebuilt three times — WPF Menu, WinForms MenuStrip, and finally native Win32 HMENU — because the first two confused screen readers. Third time's the charm. The rumors are true, when we moved to WPF, we had a situation where we had no menu bar or no actual UI. Those were rough times, for real bro.
- Complete audio overhaul: all tones and earcons now use NAudio with a persistent mixer, replacing the old SoundPlayer approach. Tones can overlap, sounds are embedded as resources, and there are synth fallbacks for everything. We will be adding earcons for enabling and disabling features soon. We'll also let you turn off earcons, because it can get annoying.
- Config files now use callsign-first naming (e.g., `K5NER_Noel_Romey_pttConfig.xml`) for easier identification and debug-ability, is debug-ability a word, or did I just make stuff up. Remind me to tell you all about fartsnoodles, a word I invented to relate unintentional testing of edge conditions while debugging / testing applications.
- Profile Reporter can now load-snapshot-restore profiles and enumerate all radio meters.
- Codebase continues to shrink — down from Jim's original 303,689 lines to our current size which is less than half of the original codebase.

## 4.1.14.0 — Don (WA2IWC)'s Birthday Release

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
- **Modern mode**: A brand new menu structure designed from scratch with screen reader accessibility as priority number one. Menus are organized by what you're actually doing — Radio, Slice, Filter, Audio, Tools — instead of the legacy layout. Every item has proper screen reader labels, checked/unchecked states, and clear announcements. It's where all the new features land first. We're not saying that Classic mode misses out on all new features and enhancements, but you're more likely to see Modern mode get the quality of life features that you'll wonder to yourself, why didn't we do it like this from day one?
- **Ctrl+Shift+M** toggles between Classic and Modern instantly. No restart needed, no settings to dig through. Try Modern, don't like it? One keystroke back to Classic. Your preference is saved per operator, so if Don wants Classic and you want Modern on the same install, everybody's happy. Want to tune JJ style for a while? Be our guest. You can also switch directly to classic mode by pressing ctrl+shift+c.
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

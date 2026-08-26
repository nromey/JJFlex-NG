# JJ Flexible Radio Access Changelog {#top}
Authored by Noel Romey with the assistance of ... my man ... Clauidd. Thanks buddy, you're a machine but we work well together, keep it up as you destroy the planet.

All notable changes to this project are  documented in this file. The opinions and cool factor  of developers, radio amateurs,  operatives,  and pets  should be taken with a huge grain of salt, a slab really. Other assistance for portions of JJ Flexible courtesy of Peanut Butter, my Abysinian buddy. This change log is long, but you might like it, because this is definitely not you're Grandpa's typoical change log. Read at your own risk.  

## Jump to a Version {#versions}

- [Unreleased / Foundation Phase — staging for 4.2.0](#unreleased)
- [4.1.16 — The Name Change Edition](#v4-1-16)
- [4.1.15.1 — Stop the Presses (local-radio hotfix)](#v4-1-15-1)
- [4.1.15.0 — More Than Just Rearranging Deck Chairs on the Titanic](#v4-1-15-0)
- [4.1.14.0 — Don (WA2IWC)'s Birthday Release](#v4-1-14-0)
- [4.1.13.0 — Callbook Fallback, QRZ Logbook, Hotkeys v2](#v4-1-13-0)
- [4.1.12.0 — Logging Mode](#v4-1-12-0)
- [4.1.11.0 — Classic/Modern Mode, Auto-Connect & Audio Fix](#v4-1-11-0)
- [4.1.10.0 — SmartLink Saved Accounts](#v4-1-10-0)
- [4.1.9.1 — WebView2 & Screen Reader Fixes](#v4-1-9-1)
- [4.1.9.0 — The .NET 8 Migration Release](#v4-1-9-0)
- [4.1.8.0 — Feature Availability & Accessibility](#v4-1-8-0)
- [4.1.7.0 — Cleanup pass (never shipped)](#v4-1-7-0)
- [4.1.6.0 — Error Reporting](#v4-1-6-0)
- [4.1.5.0 — Subscription Awareness](#v4-1-5-0)
- [4.1.0.0 — FlexLib 4.1.3 adoption](#v4-1-0-0)
- [4.0.5.0 — Advanced NR/ANF controls](#v4-0-5-0)
- [4.0.4.0 — FlexLib 4.0 migration, continued](#v4-0-4-0)
- [4.0.3.0 — Initial FlexLib 4.0 adoption](#v4-0-3-0)
- [4.0.2.0 — SmartLink reliability](#v4-0-2-0)
- [4.0.1.0 — Start of the 4.x line](#v4-0-1-0)

Jim Shaffer's changelog for the 1.x, 2.x, and 3.x versions lives in a separate archival file — see [CHANGELOG-legacy.md](CHANGELOG-legacy.md) for that history.

## Unreleased / Foundation Phase — staging for 4.2.0 {#unreleased}

> **Note:** 4.1.17 was officially skipped on 2026-05-09 in favor of going directly to 4.2.0 with the FlexLib 4.2.18 upgrade. This section accumulates user-facing work landing on main as foundation-phase content; it will fold into 4.2.0's release notes when that release cuts. Per `memory/project_flexlib_4218_merge_sequencing.md` and the 2026-05-09 confirmation.



Moving sucks. Full stop. But isn't it awesome when you realize you've stopped using plastic silverware and paper plates, you've mastered your stove and oven, your pictures are up, your favorite chair is in a favorite place, and — well, this new scary house you just moved into, the one you probably own and still get lost in, starts feeling like home. That's what this release is all about. So make yourself at home and enjoy this slug of mighty fine updates that'll have you thinking "this feels right, it feels like home." Cheesy, yes, but it's helped to define JJ Flexible's newly named "Home."

This release is about the space where you actually spend your time in the app — formerly announced as "the VFO frequency-and-slice area," which was a mouthful I know. It's got a real name now and it speaks for itself when you land on it. It also does more work per keystroke than it ever had a right to. We also fixed some flaky wiring and janky plumbing around connecting your Flex to the network and allowing inbound connections to your radio. This work has been cooking since the last release, and if you've ever had problems establishing a remote connection to your radio from outside your network, these changes will affect you. We also added a safety check that makes sure you don't accidentally change settings on somebody else's radio when you're not the owner. In short, if you're not at your radio, you won't be able to make serious network-related changes that affect the Flex and SmartLink. These and more as you follow this loopy coax down the line.

### Foundation Phase Headlines {#unreleased-headlines}

- [The main screen is now called Home](#home-intro), and it tells you so. From anywhere in the application, press F2 to "go home." Your screen reader will say "Home" plus whichever field you're on — slice, frequency, S-meter, wherever. You may hear more or less speech depending on your speech verbosity setting. No more navigating a "Frequency Display" region whose name never quite told you where you were.
- [Your radio's panels are described in words now](#know-your-radio), jack by jack, with tactile landmarks instead of "see figure 25-4." There's a page per model under Help, and any dialog that asks you to go touch the radio has a "Where are the jacks on my radio?" button that opens the right one.
- **[Slice changes can stick now](#slice-changes-stick).** Release a slice, close the radio, come back tomorrow, and there it is again — that was every morning for me, and it turns out the layout lives in the radio, not in this app. Slice changes now tell you they're temporary, "Save Station Setup to Radio" is right there on the Slice menu where the question actually occurs to you, and there's an optional offer at disconnect. It never saves on its own, and it flatly refuses while another operator is on your radio.
- [Squelch lives in Home now](#squelch-in-home). Arrow right past the S-meter and you'll find it. Press Q from anywhere in Home to toggle it — same muscle memory as M for mute, R for RIT, X for XIT.
- [The sounds are yours to shape now](#sounds-you-shape). Your CW notifications can follow the radio's own sidetone pitch, or keep the frequency you set. They can be something richer than a pure sine, which matters a lot if band noise keeps burying them. `Ctrl+J` then `E` re-sends recent CW you missed, stepping further back each press. And if you preferred the plain alert sounds to the rebuilt ones, there's a setting that gives them back.
- [Escape](#escape-does-its-job) now closes screen field categories. Press Escape once inside an open field group to close it. Press Escape twice quickly (about half a second between presses) to collapse all open field categories at once. Once the categories are collapsed, you'll be back at Home — because there's no place like home ... there's no place like home ... what? Sorry, fell asleep there for a second, no idea why. This suggestion came from Don, who got really tired of tabbing through large fields just to close them. You've done it right if you hear two tones — can't miss 'em.
- **The [key map across Home](#universal-keys-from-home) makes sense now.** R toggles RIT from anywhere you press it. X toggles XIT. The `=` key does the old transceive thing (which freed X to do what most hams expect). Pan moves to Page Up / Home / Page Down instead of letter keys, so if your finger slips you don't pan to Patagonia.
- **You can finally change your keys — for real this time.** Tools then Hotkey Editor opens the new Keys surface: every command in the app with its current key, its scope, and its default, arranged by scope, alphabetically, or by function group — plus a read-only view of all the built-in field keys. Pick a command, press Change Key, press the key you want — it works immediately, no restart. If the key already belongs to something else, JJ Flexible names the collision out loud and asks before taking it. There's unbind, per-key reset to default, reset-all with a confirmation, and an Export Key List button that writes your whole key map to a file you can read or share. The old Key Assignments window — the one where Update looked like it saved and never actually did — is gone. Help then Key Assignments opens the same surface for browsing, one menu item instead of three.
- **Press ? on any Home field and it speaks that field's keys.** "Keys on Frequency: Up Down coarse tune..." — the field you're on first, then the keys that work everywhere in Home. Same wording as the per-field help dialog and the Command Finder, because they all read from one table now, so they can never drift apart again.
- **The universal Home keys are truly universal now.** M, V, R, X, Q, and = were advertised to work from any Home field, but the S meter, squelch, and slice fields didn't get the memo. Now they have it. And jumping to a slice that doesn't exist speaks "not created" instead of silently doing nothing.
- **A handful of keys now do what the reference always claimed.** Ctrl+Alt+[ and Ctrl+Alt+] really nudge the TX filter's high edge (the receive filter's squeeze handler had been eating them). Home on the Slice field really pans center. Ctrl+Shift+F in the logging pane really searches your log. The keyboard reference itself got a top-to-bottom scrub against what the app actually does — the JJ key table alone had five wrong rows.
- **Heads-up on two keys that moved to honesty:** Memory scan and Speak frequency were listed with default keys (Ctrl+Shift+M and Ctrl+Shift+F) that never actually reached them — those chords have always belonged to switching tuning mode and the frequency readout toggle, and they still do. Both commands now show as unbound, and you can give them any free key you like in the Hotkey Editor.
- **The diagnostic log stops flooding itself.** The log used to write your radio's meter readings — mic level, SWR, power, S-meter — many times a second, every second, transmitting or not. One morning's log weighed in at sixteen megabytes before lunch, and all that chatter was drowning out the lines that actually explain a problem. Those readings now live behind their own switch: "Record the meter stream" on the Diagnostics tab, off unless you turn it on. Turn it on for a bench session — where those numbers are exactly what you want — and each meter writes one tidy line per second carrying its lowest, highest, and latest reading, so a one-instant peak still gets caught. Detailed logs and captures are several times smaller across the board, which means the log can finally hold a useful stretch of history instead of the last few minutes of meter chatter.
- **JJ Flexible now tells you what it left running.** Some switches stay where you put them. Turn the meter stream on for a bench session, get pulled into a pileup, and it's still on next Tuesday — quietly filling your disk while you have no earthly way of knowing. A sighted operator gets a little recording light in the corner of the screen for exactly this situation. We don't, and I got tired of finding out the expensive way. So there's a new answer: press `Ctrl+J` then `O` — O for "what's on" — and JJ Flexible reads back everything it currently has running and what each one has cost you so far. "Meter stream recording, 218,000 meter lines into the log, and it will still be on the next time you start." If something grows past a sensible size, it speaks up on its own — once, at the moment it crosses the line, not every few minutes until you learn to tune it out. And if you close the app with recording still going, it tells you before it goes, and offers to switch it off on your way out. Your everyday log and your meter tones don't set any of this off; the log is on for everyone and the tones you can hear perfectly well. It's the silent ones that get a word.
- **[Ctrl+S is more responsive now](#smeter-answers-every-press).** Working the key to follow a signal used to get you silence — each press pushed the answer further away, and an unchanged reading said nothing at all. Now every press answers, including the last one. Thanks to Don for catching it.
- **[Networking settings](#network-tab-configurability) you can actually configure on your own.** Manual port forwarding, UPnP, and hole-punch all live in the Network tab now. Bye-bye, SmartSDR, for that task at least. We also included a diagnostic probe with a copy-and-pasteable report you can send me when something breaks.
- **[Safety check on port forwarding](#port-forwarding-safety-check).** If you're connected to somebody else's radio via SmartLink and try to change the port settings, JJ Flexible politely stops you instead of overwriting their radio's config.
- **[Each radio now remembers its own connection settings](#radios-tab)** in the new Radios tab under Settings — Tools then Configure Radio jumps straight there. One radio can use a forwarded port while another hole-punches, and it's all editable whether you're connected or not. Oh, and you can finally rename your radio from JJ Flexible instead of borrowing SmartSDR for it.
- **OK and Apply now behave like every other Windows program, on every settings screen.** OK applies your changes and closes. Apply applies them and keeps the dialog open. Cancel discards what you haven't applied. The per-screen one-off buttons — "Save profile," "Apply to connected radio" — are gone, and with them the trap where you typed something, pressed OK, and the app quietly threw it away. If you changed it, OK keeps it. Full stop.
- **The radio name you type actually sticks now.** Renaming a radio and finding it back under its old name later was exactly that trap — the name box needed a separate button that was easy to miss, and OK discarded the edit without a word. Name it on the Radios tab, press OK or Apply, and it's saved: pushed to the radio itself when you're connected to it, and held with a plain-words heads-up when you're not.
- **Settings that have to wait now say so out loud.** Some things can't take effect until you're connected — a name for a radio that's off, or its remote power setting. Press OK and JJ Flexible lists what's waiting in a small dialog you can arrow through and re-read at your own pace, instead of letting you assume it all happened.
- **REM ON is reachable exactly when you need it.** The remote power jack setting used to require a live connection — which is precisely when a remote radio being *off* made it unreachable. It now lives in each radio's settings on the Radios tab: set it any time, radio present or not, and it's applied the next time you connect. One honest note from the hardware department: the REM ON jack only does something if it's actually wired to a relay.
- **You can now tell JJ Flexible "I can't get to this radio."** A per-radio setting for rigs that live somewhere you can't walk to — a remote site, the far end of a SmartLink link, a friend's shack across town. Checking it offers to set up the safety net such a radio needs — REM ON, the remote administration allowances — with every setting named and explained before anything changes, and a receipt afterwards that you can re-read. There's a "don't show this explanation again" for the snowbirds who flip it twice a year, but the receipt always shows: nothing changes on your radio behind your back.
- **The router instructions now tell the whole truth.** The port you pick for SmartLink forwarding is the *outside* port — the one the internet sees. The radio itself always listens at its local address on port 4994 for TCP and 4993 for UDP, and your router's two forwarding rules have to connect the outside port to those. The app used to say "forward the port to the radio" as if inside and outside matched; they don't, and one of our own debugging afternoons went down that exact hole. Every screen that mentions forwarding now spells out both rules, with your radio's actual address filled in when it's connected.
- **[SmartLink sign-in is a real dialog now](#native-signin)** — email box, password box, Sign In button, Forgot Password button. No web page, no browser, no fighting a login form that won't talk to your screen reader. And signing in this way sticks: JJ Flexible quietly renews it in the background from then on.
- **[Enter connects, like it always said it would](#selector-enter-connects).** The radio selector has told you "press Enter to connect" forever — and now Enter actually does it. Pick a radio in the list, press Enter, you're connecting. Bonus: a stray keypress right after your remote radios appear can no longer accidentally restart the whole remote search.
- **[Remote can start itself now](#remote-first-startup).** If you only ever operate remote, there's a new checkbox on your SmartLink account: "Start Remote automatically at startup." Check it once, and from then on the radio selector goes looking for your remote radios the moment it opens — no more pressing Remote every single session. Off by default, per account, and local radios still show up alongside.
- **[Use an account without changing your default](#use-account-now).** The account manager grew a "Use Now" button. Borrowing a friend's SmartLink account for one evening? Use Now switches to it for this session only — your default account is untouched and everything is back to normal next time you start the app.
- **[The radio selector remembers every radio you've ever used](#known-radios-roster).** Open it and your radios are already there — including the ones that are switched off — each row saying whether it's online right now and, if not, when it was last seen and where. Mark the ones you care about as favorites and they sort to the top. No more staring at an empty box while the app goes looking.
- **Every radio now remembers how you like to reach it.** The Connection path control beside the list works for every radio now, not just the rare one that shows up both locally and on SmartLink — Automatic, Local network first, or SmartLink first, saved with the radio itself. If your radio lives at a friend's house, tell it "SmartLink first" once and Connect goes straight there every time, instead of knocking on your own empty network first and making you wait out the failure. The same choice is in the radio's right-click menu under Default Connection Path.
- **Connect tries the other door before giving up.** If your radio doesn't answer the way it usually does — the network hiccuped, the radio moved, whatever — JJ Flexible now tries the other path and tells you it's doing it, instead of stopping at the first no. Nothing happens in secret: every switch is announced, so you always know whether you're on your own network or going through SmartLink. Radios you take places — Field Day, a club shack, a new house — just keep connecting.
- **One press of Enter does the whole job now.** Connecting to a SmartLink radio used to take two Enters — the first one signed in, and then just... stopped, waiting for you to press it again. Now Enter signs in, finds your radio, and connects, narrating as it goes. This one's been biting Don every single session.
- **Need to force a path? It's in the right-click menu.** Connect Locally and Connect over SmartLink are on every radio's context menu (Applications key or Shift+F10). These mean business: Connect over SmartLink will never quietly fall back to your local network — if the remote path is broken, you'll hear exactly that, which is precisely what you want when you're testing whether the remote path works.
- **Heads-up: the Remote button is gone, and Alt+R with it.** Connect now looks for your SmartLink radios by itself whenever a radio's connection path calls for it, so the button had one job left — showing the remote list — and that moved to the radio list's right-click menu as Show Remote Radios, which becomes Refresh Remote List once they're listed. If Alt+R was in your fingers: Shift+F10, then R. The refresh still reconnects to SmartLink on purpose — the server only hands out the radio list once per connection, so asking twice on the same line gets you yesterday's answer.
- **The name you give a radio actually sticks now.** Name a radio in Settings and that's what you'll hear in the radio list from then on — even while the radio is online broadcasting its own idea of its name, and even after it's been seen a hundred times. Before this, your chosen name quietly lost to the radio's built-in name the moment the radio showed up, and worse, got overwritten on disk. Your name wins now. You typed it on purpose.
- **[The account button says what it actually does](#account-button-truth).** No accounts saved? It says "Sign in to SmartLink." One? "SmartLink Account." Two or more? "Switch Account" — and it tells you which one you're using. There's a line under the list saying the same thing, and every SmartLink connect announces the account it's about to use.
- **Busy radio, clean exit.** Connect to a radio whose slices are all taken — somebody's already operating on every one of them — and JJ Flexible now tells you who has them and backs out on its own. It used to announce the problem and then sit at "Connecting" indefinitely, quietly waiting for you to dismiss an error window you had no way of knowing existed.
- **You stay recognized at your own radio.** Mid-connection, a Flex sometimes drops and re-adds the list entry for your own connection with the identity fields blanked — and JJ Flexible would take that at face value: suddenly "another client connected" out of nowhere, and worse, the app could decide you were no longer the operator at the radio and refuse to let you change network settings you were sitting right in front of. It keeps its own memory of who you are now and doesn't fall for it.
- **Auto-connect stopped taking no for an answer.** If your auto-connect radio isn't where it was last time — it was local and now it's remote, or the other way around — startup now tries the other path automatically, saying so, instead of failing every morning until you reconfigured it.
- **Your default account is actually in charge now.** With more than one SmartLink account saved, JJ Flexible used to quietly favor whichever account it had touched most recently — and "touched" included its own background housekeeping, so the wrong account could win forever no matter what you set as default. That's how a few of you saw a registration reminder naming somebody else's account on your own radio. One rule everywhere now: your default (or the account you picked with Use Now) is the account in play, full stop. And when several accounts are saved with no default chosen, JJ Flexible asks you to pick one instead of guessing — it will never quietly sign in as someone else again. The account manager also lists your default first now instead of whoever was used last.
- **The radio list knows whose radio is whose.** An offline radio that belongs to a different SmartLink account now says so right on its row — "registered to don@example.com, last seen 2 days ago" — instead of reading like an anonymous remote radio. Press Enter on it and JJ Flexible switches to that account and refreshes the list for you, announced first, instead of grinding through a half-minute search on an account that could never find it. And every SmartLink connect now speaks which account it's using before it starts — for every radio, every time, so there are no surprises about whose account is on the air. The offline rows also say "last seen" exactly once now, which they did not always manage before.
- **A radio can have its own account.** If you share a club rig — two operators, two SmartLink accounts, one radio — you can now tell JJ Flexible which account that radio should always use: press the Applications key (or Shift+F10) on the radio and pick Preferred Account. There's also an Associations button in the SmartLink account manager that shows every radio this computer knows and which account each one uses, and lets you change the binding from the account side. Radios without a preference keep working exactly as before — automatic, no setup needed.
- **The list tells you when it's loaded — and F2 repeats it.** When your local radios have had a chance to appear you'll hear "Local loaded" (still listening for more — local radios keep trickling in the whole time the picker is open), and every successful remote pass says "Remote loaded." Missed it, or your screen reader was mid-sentence? Press F2 in the selector any time and it speaks the whole picture: local, remote, and how many radios are online right now.
- **Shift+Tab wraps around in every dialog now.** Tabbing backward from the first control used to stop dead — now it wraps to the last control, in the radio selector and every other dialog. The bottom of the selector (the network identity card and the account line) is one Shift+Tab from the radio list instead of a dozen Tabs the long way round.
- **Shift+F10 opens the radio menu now.** On a keyboard without an Applications key, Shift+F10 is the only way to reach the radio list's context menu — and it was opening a useless system menu instead. Fixed; both keys open the same menu, anchored to your radio.
- **The identity card and the Status readout read like text now.** Both used to announce as lists — "item 1 of 12" — and arrowing through them pretended you were selecting something. They're plain readable text now: arrow by line, by word, by character, select what you want and copy it. Your reading position still survives the automatic refresh, so the readout won't yank you back to the top mid-sentence.
- [CW send and receive boxes](#cw-text-boxes) go away if you can't use them. Unless you're in CW mode, the receive and send text boxes aren't visible and they're not in the tab order. Switch to CW with Alt+C and they come right back.
- **Ctrl+Tab works for [panel navigation](#panel-navigation) again.** Sorry about that. Ctrl+Tab moves you through the major category fields, and Ctrl+Shift+Tab reverses direction. The popup menu that used to live on Ctrl+Tab is disabled pending a proper redesign — it's going to come back as an actual accessible toolbar that also looks spiffy. Stay tuned on that one.
- **The [Connecting screen now lets you out](#connecting-cancellation).** Press Escape or click the X close button and JJ Flexible cancels the connection attempt right away — no more waiting for a timeout, no more force-quitting the app. The screen also tells you what phase it's in as it works, so you know whether SmartLink is slow, the slice is still being acquired, or something else is up. If a connect takes more than a minute, you get a "keep waiting or cancel" prompt with the actual reason; at five minutes JJ Flexible cancels for you so the screen never sits there spinning forever.
- **[Tuning unity](#tuning-unity).** Up and Down arrow tune by your coarse step. Shift+Up and Shift+Down tune by your fine step. There is no mode any more — no `C` key to flip back and forth, no Page Up/Down to cycle a step list. One coarse value, one fine value, set in Settings. Don was the one who said it: "you can tune much more easily if you don't have to mode switch." He's right.
- **[Audio levels live in the Audio expander now](#audio-expander-volume).** Volume, headphone level, and line out level all live under Ctrl+Shift+U. The old `Alt+Page Up`, `Alt+Shift+Page Up`, and `Shift+Page Up` shortcuts (and their `Page Down` counterparts) are retired. Hotkeys earn their slot when one keystroke equals one action; values belong with their fields.
- **[RIT and XIT now have a scale-adjust mode](#rit-xit-scale-adjust).** Press a number 1, 2, 3, or 4 while focus is on the RIT or XIT field, and you're in scale-adjust mode at 1 Hz, 10 Hz, 100 Hz, or 1 kHz. Up and Down then walk the offset by that scale until you press 0, Escape, or navigate away. Don asked for this one — chasing a drifting correspondent without field-hopping through decade fields is the workflow this exists for.
- **[The program file has a new name](#exe-rename).** JJ Flexible Radio Access now installs as `jjflexible.exe` rather than `JJFlexRadio.exe`. Your Start Menu and desktop shortcuts are updated for you and keep working, and your settings, radio profiles, CW messages, and SmartLink accounts do not move an inch. The one thing worth a look: a taskbar pin or a shortcut you made yourself points at the old file and needs re-pinning once.
- **[The installer brings everything it needs](#self-contained).** Run the new Setup on a fresh Windows machine and JJ Flexible just installs. No more "you need .NET 10, click here to download Microsoft's runtime first." The download is about 55 MB instead of 10 — bigger, but it's one trip instead of three, and that "go install this other thing" detour was an accessibility wall for screen reader users on new machines. Done.
- **[The Trace Archive Browser is here](#trace-archive-browser).** Every connection your app makes already gets quietly archived in the background — every successful connect, every flaky retry, every killed session. There is now a place inside JJ Flexible to actually look at them. Open the Tracing dialog (Help menu, then Tracing) and you'll see a new "Archive Browser" tab next to the existing tracing controls. Filter by date, by outcome, by radio. Pop open a trace in your text viewer. Copy the file path so you can attach it to an email. Bundle a handful of traces into a single zip to send to me. The list speaks itself as you arrow through it, so you can hear "AS retry then success on Don 6300, one minute twenty-three seconds" without leaving the row.
- **[Your radio's volume knobs are visible now](#radio-outputs-visible).** Settings, Audio tab has a Radio Outputs group: headphone level, line out level, and the three output mutes, all of which apply to the radio as you change them. On a radio without a front panel, those software levels are the only volume control that exists, and until now JJ Flex could nudge them but never show you where they stood. There's a "Why is my radio silent?" button too, which walks the likely causes and tells you the first thing it finds. Starting with the one that catches everybody: a Flex makes no audio at all — headphone jack included — until something connects to it.
- **[One place to choose your sound devices](#audio-devices-dialog).** The old device picker was two dialogs in a row, both with a list labelled "device list," and on a fresh install it could ambush you mid-connect from the background. It's one dialog now — radio audio out, microphone in, alert and CW device, meter device — reachable from the Audio menu, from Settings, or from the Command Finder. It announces what's currently chosen, marks your system default in words, and if a device you picked gets unplugged it falls back and says so instead of going quiet.
- **[Is this thing on? Now you can find out without going on the air](#microphone-check).** The Audio Devices dialog has a Microphone check in it. Pick your microphone, press Start microphone check, talk, and JJ Flexible tells you what it hears — in the same plain words the Audio Workshop uses, with the number beside them. No transmitting, no radio, no SmartSDR, no Sound Recorder. And it tells three different silences apart: nothing reaching your microphone, nothing coming out of Windows at all, and Windows privacy settings blocking us — with a button that takes you straight to the page that fixes the last one.
- **[One device, one line in the list](#one-device-one-line).** Windows offers most sound hardware three or four times over, once for each of its sound systems, so a single USB interface used to fill the picker with identical-looking copies — and one of those copies is usually the wrong one to pick. Each piece of hardware is one choice now, and [you're the one who picks which audio system it comes through](#pick-your-audio-system). Devices that are USB, Bluetooth, or HDMI say so. Entries that are really a loopback of what your computer is playing say that too, loudly, because choosing one as your transmit microphone puts your own received audio on the air. And a device you saved that isn't plugged in stays in the list marked "Not connected" instead of quietly disappearing.
- **[You pick the audio system now, and the list got shorter for it](#pick-your-audio-system).** Windows hands the same sound card to programs through several different driver models, and JJ Flexible used to pick one for you without saying so. There's a plain "Audio system" choice at the top of the Audio Devices dialog now — WASAPI by default, with MME there for anything WASAPI won't open. Picking it up front means the duplicate copies of your hardware simply aren't in the list to begin with, so the picker is *smaller* than it was before this control existed. That's not the usual direction, and I'm quietly delighted about it.
- **[Mono microphones work](#mono-microphones-work).** If your only microphone has one channel — which is most USB headsets — JJ Flexible used to list it and then refuse it, which is a polite way of saying you couldn't use the app. Your voice goes to the radio on both channels now. No workaround, no second interface, no panning tricks.
- **[Transmit audio quality is yours to set](#transmit-audio-quality).** Full quality is still the default and still what you want. But when a remote link is having a bad night and your audio keeps breaking up, there are lower settings that ask less of the connection. Duller audio that gets through beats better audio that doesn't.
- **[CW notifications moved in with their speaker](#cw-with-alert-device).** The switch and the device it plays through are on the same tab now, next to each other, instead of one tab apart. Defaults unchanged — CW notifications are still off until you turn them on.
- **[JJ Flex now updates itself](#in-app-updates).** Tools menu has a new "Check for Updates" item. Settings has a new "Updates" tab where you pick your channel — Stable, Beta, or Nightly — and decide whether you want JJ Flex to check on its own. By default it checks at startup and every couple of hours while it's running, but you're in charge of all of that. When an update lands, you get a dialog that tells you what's new and how big the download is, and one keystroke does the install. No more hunting for installers on the website.
- **[Hear yourself before anyone else does](#audio-check).** The Audio Workshop grew an Audio Check: one button keys the radio and your own transmit audio comes back in your headphones while you talk. Record a take, unkey, and it plays right back so you can hear what a mic adjustment actually did. If your setup ritual has always been "how do I sound?" into a quiet band, this is that friend, on demand, with nobody rolling their eyes. And by default the check now makes **no RF at all** — dummy load, zero watts — because every meter it reads tells the same truth with the power at zero.
- **[The volume you actually wanted is finally on the menu](#honest-audio-hub).** If you listen over PC audio, the loudness of radio sound coming out of your computer was never adjustable — it was welded to one value deep in the code. It's yours now: PC Output Volume, 0 to 24 dB, on the Audio menu, in Home's audio group, and on the new volume gesture. And every control that moves the radio's own jacks now says "On-Radio" right in its name, so "Headphone Level" can never again talk you into turning a knob on a radio three states away.
- **[Every volume, one gesture: Ctrl+J, then V](#honest-audio-hub).** Volume mode. Pick a target with one letter — H for on-radio headphone, P for PC output, M for mic level, L for on-radio line out, C for compander level, S for the speech processor's mode — then ride Up and Down. Every press speaks the new value, you can hop between targets without leaving the mode, and Escape ends it. The compander and speech processor toggles joined the JJ layer too, and there's a new mic-audio field in Home's audio group that tells you "just right" or "coming in hot" when you arrow to it.
- **[A built-in test tone, so you can test transmit audio without a microphone](#test-tone).** The Audio Workshop's TX Audio tab has a Test Tone section now: arm it and a clean, steady tone takes your microphone's place while you transmit — your mic is fully muted, so no room noise rides along. It starts at 440 hertz, but the pitch is yours to change and JJ Flexible remembers your choice: hearing varies, and a test tone you can't hear is no test at all. Pick from 440 hertz, a 700 hertz CW-style tone, the classic 1 kilohertz test tone, or type in any frequency you like. There's a level control in dBFS, and a "hear the tone while it transmits" switch so you can confirm by ear — or keep your shack quiet, your call. One honest guardrail: if you park the tone outside your transmit filter, nothing goes out — so JJ Flexible warns you out loud the moment you set it there, again when you arm it, and at every key-down, instead of letting you "test" silence. And whenever the tone is armed, every transmission announces it's sending the tone instead of your voice — you'll never make a contact by accident with a sine wave.
- **Ghost copies of JJ Flexible are done haunting your shack.** Closing the app really closes it now. Before this fix, a sound device that wouldn't let go could leave an invisible copy of JJ Flexible running after the window disappeared — and if you opened the app again, the two copies would quietly wrestle over your settings file, which is why settings sometimes wouldn't stick until you hunted the ghost down in Task Manager. The audio shutdown now waits a polite few seconds and then leaves anyway, and even a truly wedged sound driver can't keep the app alive against your will.
- **Global keys are finally global.** The keys that claim to work everywhere — F1, the status keys, stop CW, the Command Finder, verbosity, repeat-last-message, the whole JJ layer — used to go quietly dead the moment a dialog had focus. Worse, one of them got hijacked: Alt+Shift+S inside the Audio Workshop saved a preset instead of speaking your transmit status, because the Save button had quietly claimed the keystroke. Every one of them now reaches its real command from inside any dialog, and a dialog's own keys still come first, so nothing a dialog needs was taken away.
- **Push to talk works from Home's field groups now.** Ctrl+Space used to go dead the moment focus entered the expanders — which is exactly where you are while riding Mic Level. Keying, the Shift+Space transmit lock, and Escape-to-unkey all work from the field groups too. And Escape keeps its manners: transmitting, it unkeys first; not transmitting, it collapses the group like it always has.
- **Mic check, one two: Ctrl+J, then K.** One chord speaks how your mic audio is doing — "just right, peak minus 12 dBFS" — and nothing else in front of it. While you're transmitting it follows the last second and a half, so you can ride the mic gain and hear each change land; while receiving it reports your last transmission. It works inside the Audio Workshop too, which is exactly where you'll want it.
- **The test tone rides the JJ layer: Ctrl+J, then G.** Arm and disarm the workshop's test tone from anywhere, no dialog required, using your saved pitch and level. Same honesty as the workshop: it refuses to arm when the tone can't reach the transmitter and says why, and it warns you out loud if your tone sits outside the transmit filter where nothing would go out.
- **Speak Transmit Status now leads with what you don't know.** While you're transmitting, Alt+Shift+S speaks your mic-audio verdict first and the transmit details after — you just keyed the radio, you know you're transmitting; what you want is how you sound. While receiving it reads exactly as before.
- **You choose how mic-audio checks read** — plain English, decibels, or both — under Settings, Notifications, "Mic audio readout." Both is the default, which is exactly what you've been hearing all along. That choice now reaches the read-only mic fields in Home and the Audio Workshop as well, which only seems fair given those fields exist to be read out loud.
- **Your mic reading carries both numbers now: "just right, peak minus 12 dBFS, loudness minus 19 LUFS."** They are not two ways of saying the same thing. The peak tells you how close your loudest instant came to the ceiling — your headroom. The loudness tells you how loud you actually sound to whoever's copying you. They disagree sometimes, and that's exactly why you get both: peaky speech reads fine on peak while you sound small on the air, and a comfortable loudness can hide the odd consonant slamming into the ceiling and getting its top sliced off. There's a Help page called "Why your audio has two numbers" that lays it out properly. Slightly embarrassing footnote: the app had been measuring that loudness figure on every transmission for a while now and had never once mentioned it to anybody.
- **A loud room gets mentioned now — and no, turning yourself up is not the fix.** Here's the wrinkle. That loudness measurement deliberately ignores the gaps between your words, which is normally exactly right: your pauses shouldn't count against how loud you sound. But a fan, an air conditioner, or traffic through a window doesn't leave any gaps to ignore — it runs continuously, so it stops being background and becomes part of your level. The number can cheerfully tell you you're sitting pretty while your voice is buried behind your own room. So JJ Flexible watches the quiet stretches as well as the loud ones now, and when your voice isn't standing far enough clear of your room you'll hear one extra line: "Steady background noise, about 14 dB under your voice. Turning up would raise the room too." It's an observation, not a scolding, and it never replaces your level verdict — your level can be perfectly good and your room still be loud. Those are two separate facts and you get told both. A quiet shack never hears about this at all, which is the whole idea: the only thing worse than a meter that misses a problem is one that invents one every time you key up.
- **Holding the push-to-talk key now measures the transmission you just made.** Ask how your audio was after a held transmission and you used to get numbers from some earlier locked transmission — possibly minutes earlier, possibly at a completely different mic gain. Both ways of keying start a fresh measurement now.
- **PC audio comes back the way you left it.** Whether radio audio plays through your computer used to be forgotten the moment you disconnected — if your radio is only reachable remotely, that meant switching it on again every single session, forever. Each radio has a memory now: by default the next connect brings PC audio back the way you left it, and Settings, Audio has a per-radio choice — as I left it, always on for this radio, or always off. Always on is the remote operator's friend: even if a bad night ends with the switch off, the next connect turns it back on. And whatever happens at connect gets said out loud — no switch is ever flipped silently.
- **The Audio Workshop's check cluster sits together now.** During an audio check your hands live in exactly three places — Mic Gain, the live reading, and the Stop button — and the reading used to sit a half-dozen Shift+Tabs from the knob you were riding. The order is now Start, reading, Mic Gain: start a check, focus lands on Mic Gain, one Shift+Tab reads your level, one more reaches Stop.
- **The Live Meters tab finally has somewhere to stand.** Confession: those eight readings — S-meter, forward power, SWR, and friends — were plain text your Tab key sailed right past, so the tab talked at you when values changed but you could never walk up to a meter and ask it anything. Every reading is a real field now: Tab to it, read it at your own pace with your screen reader's review commands, and press F6 to hop between the Receiver, Transmit, and Hardware groups. And since any meter is now yours to read whenever you like, the meters have stopped announcing every twitch on their own — a busy band's S-meter was narrating twice a second and talking over everything you actually cared about. They also come clean when there's nothing behind them: disconnect the radio and every reading says "no radio connected" instead of freezing on numbers that are no longer true.
- **Audio presets are a real, complete feature now.** Confession time: the workshop's Save Preset button had been announcing "Preset saved" while quietly dropping your preset on the floor, and Load kept insisting there was nothing to load — not even the three presets that ship in the box. Both genuinely work now, and with the plumbing fixed the preset story got its missing chapters. **Import** joins Export on the toolbar (`Alt+I`), so the preset file a friend sends you can finally get *into* the app, not just out of it — it lands in your saved list and leaves the radio alone until you deliberately load it, because a file showing up is not permission to retune your transmitter. A file that can't be read gets called unreadable, honestly, instead of turning into a mysterious blank preset. And **Delete** exists at last, right inside the Load picker where your presets are listed: press `Delete` on the one you're done with, confirm, gone — the radio untouched. The Audio Presets help page was rewritten to describe what presets actually capture — your transmit audio chain, mic through processing through TX filter and monitor — instead of a receive-side system that never existed.
- **Microphone profiles are here — built around the mic, not the radio.** Your headset has a name now. The Audio Workshop's new Microphone Profiles section saves everything one microphone needs under that name: the computer half (which Windows device it is, its input level, its boost) and, per radio, the radio half. Here's the clever part: your Flex already keeps its own mic profiles on the radio, shared with every program that connects to it — so instead of copying those settings and letting two copies drift apart, your microphone profile simply remembers *which* of the radio's profiles to load. Apply "headset" and both halves land in one press. Carry the same profile to a second radio and save it there too — the two radios' setups live side by side under the one mic's name. And on a radio you've never set it up on, applying it adjusts your computer and touches nothing of theirs — the polite-guest rule, built right into the shape of the thing. A referenced profile the radio doesn't have gets said out loud, plainly; JJ Flexible never guesses at a substitute and never creates profiles on a radio unless you explicitly ask it to.
- **Audio presets carry your whole transmit chain now — EQ included.** Saved and exported presets were quietly missing the transmit equalizer; now it rides along with everything else. Presets also remember which input they were tuned on — mic jack, balanced, PC — because a preset dialed in on one input is a different animal on another, and loading one on the wrong input now says so instead of leaving you wondering why your award-winning ragchew sound went sideways. Older preset files that never captured the EQ stay safe: loading one leaves your radio's EQ alone rather than zeroing it. Exported files also carry a version stamp now, so a preset made by a future JJ Flexible can be recognized instead of half-read in silence.
- **A corrupt preset file gets announced, not swallowed.** If your saved presets file ever becomes unreadable — a bad shutdown, a disk hiccup — JJ Flexible used to quietly show you the three built-ins as if your saved tuning never existed, which is settings loss with nobody told. Now it says so out loud, and the unreadable file is kept right where it was, renamed, so nothing overwrites it and its contents can still be rescued. The same honesty covers your filter presets and the new microphone profiles.
- **Your audio settings file has one home now.** Behind the scenes, the file holding your audio preferences was being written to two different folders by different parts of the app — which is the kind of arrangement where a setting can mysteriously "not stick" depending on which copy got read. It lives in exactly one place now, your existing settings walk themselves over automatically, and the old location keeps working for one more release so nothing is lost in the move.
- **There is a warning sound now, and it sounds like a warning.** Every alert sound JJ Flexible made was a short beep or two — pleasant, tidy, and completely interchangeable if you weren't already expecting one. So a genuine "something is wrong here" landed on your ears sounding exactly like a checkbox agreeing with you. There's a proper alarm now: one long tone, three quarters of a second, with harmonics stacked on it so it has a real voice instead of a polite little beep. You will not confuse it with anything else in the app, which is the entire point — when you hear it, the sentence right behind it is the part that matters. It has its own switch in Settings, Notifications, under Alert Sounds, alongside the five that were already there. Turning it off is your call, but it's the one I'd leave on: every other sound in that list is the app answering a key you just pressed, and these are the ones that speak up when you didn't ask.
- **Connecting to a radio that would have transmitted silence now says so — loudly.** Here's a quiet little disaster: your Flex keeps microphone profiles on the radio itself, and if none of them is selected, audio from your computer will not be transmitted through your radio. Nothing warns you. Nothing sounds broken. You key up, you have a lovely one-sided conversation, and nobody hears a word of it. JJ Flexible now checks this the moment you connect. On a radio you've marked as yours it just fixes it — loads a profile and tells you it did — because on your own radio, asking you to approve a repair to a thing you never broke is just friction. On anybody else's radio it changes nothing and tells you what it found, because a guest doesn't rearrange the furniture. Either way you hear about it before you key up rather than afterwards, and if it's the warning, it comes in behind that new alarm. Nothing you did caused it, and receive was never affected.
- **A dropped audio buffer leaves a paper trail now.** Your computer's sound system reports every buffer of audio it drops — that's what a periodic click often is — and JJ Flexible used to receive that report and throw it away, which made clicking complaints nearly impossible to pin down. The trace file now notes the first time a drop happens, counts the rest quietly, and writes the totals when audio stops. If you've been hearing clicks, a trace you send me can finally show whether the sound system was dropping audio or the problem lives somewhere else.
- **The Command Finder knows the audio rooms now.** Search `Ctrl+/` for "volume," "levels," "boost," or "headphone" and the PC Audio Levels and On-Radio Levels dialogs come up with the menu path to each; search "preset," "profile," or "audio check" and the Audio Workshop's own keys — Ctrl+S, Ctrl+O, Ctrl+Enter — are in the list too.
- **The Command Finder has help of its own now.** Press F1 while the finder is open and you get the page about the finder itself — searching, categories, running what you found — instead of being dropped on the keyboard reference. Help then Keyboard Reference still goes exactly where it always did.
- **The tracing window's button tells your screen reader the truth.** Its Start/Stop button announced "Start or stop tracing" no matter which one it was about to do, so you could not tell whether pressing it would begin a trace or end one. It now says "Start tracing" or "Stop tracing" to match what it will actually do. A proper new home for diagnostics is designed and on the way; this makes the old door honest in the meantime.
- **Device names with a trademark symbol read cleanly now.** Windows says "Intel® Smart Sound Technology"; JJ Flexible used to garble that into "IntelÂ®," and your screen reader would dutifully voice the stray character every time you arrowed past it in the device list. Fixed at the source — device names now read exactly as Windows spells them.
- **PC Spectral NR finally has its missing half: noise capture, Ctrl+J then Q.** Q for quiet. Find a calm spot on the band, press it, and JJ Flexible grabs three seconds of your noise floor and starts subtracting exactly that noise from everything you hear. The capture talks the whole way through — it announces the start, counts the seconds out loud, and finishes with "noise profile captured" — because a capture you can't hear happening is a capture you can't trust. Press Q again mid-capture to cancel, and it says so. Until now the app would cheerfully announce "no noise profile loaded" while offering no way, anywhere, to capture one; that dead end is gone, and the announcement itself now names the key that fixes it.
- **The PC noise reduction knobs finally reach the panel.** Turn on PC Neural NR or PC Spectral NR in the DSP field group and the controls appear right underneath: strength for both engines, the artifact-guard floor for spectral, and a Voice Modes Only switch that keeps the speech-trained neural engine out of your CW. Every adjustment speaks its new value. And since running both engines at once wants gentler settings than either alone, the new Noise Profiles dialog has an Apply Recommended Levels button — one press sets the right values for whatever you have switched on.
- **Noise profiles are real things you can keep.** Every capture saves itself and comes back on your next connect. The new Noise Profiles dialog — Slice menu, DSP, PC Noise Reduction — lets you save profiles under names of your own; they remember the band and antenna they came from, so the list reads back "20m, ANT1, captured August 11," which is how you'll actually remember them. Load one, clear it, set the capture length (1 to 5 seconds), or open the profiles folder and handle the files like any others — a profile is one small file, so sending your 40 meter profile to a friend with the same grow-light neighbor is just sending a file.
- **Your PC noise reduction settings stick now.** On/off, strengths, floor, voice-only, and the loaded profile all survive an app restart. They used to quietly reset to defaults every single session.
- **"Spectral NR" and "PC Spectral NR" no longer dare you to guess which is which.** The radio's own DSP toggles now say On-Radio — On-Radio Neural NR, On-Radio Spectral NR — in the panel, the menus, and speech, the same vocabulary as On-Radio Headphone Level. One word tells you which side of the wire a feature lives on.
- **[Press D, get D](#slice-identity).** Slice letters are honest now, even when another station's slices share your radio. Jumping to slice D lands on D, a mode change lands on the slice you're actually hearing, and Release All Extra Slices keeps the slice you're on instead of dumping you back to A.
- **[The transmit slice shows its face](#transmit-slice).** Home has a new Transmit slice field that always shows which slice keys the radio, with the keys to set it, move it, or clear it entirely — cleared means nothing keys up until you say so. The Slice menu grew a matching Transmit Slice submenu.
- **[Transmit power finally has a front door](#power-dialog)** — Radio menu, Transmit, Power. Transmit and tune power in one dialog, applied as you adjust, and on a transverter port it switches to real dBm with decimals. The rest of the transmit chain — mic gain, compander, speech processor, monitor, TX filter — now has menu paths too.
- **[A failed connect tells you why](#connect-failure-honesty).** No more bare "Connection failed." You hear what the evidence says — the router refused, the packets vanished, the sign-in was rejected, the radio never showed up in your account — and when the router rule is the problem, JJ Flexible reads you the exact rule your router needs, built from what the radio itself reports.
- **[Reboot and firmware live on the Radio menu now](#radio-maintenance).** Two new entries at the bottom of the Radio menu: Reboot Radio, with a confirmation so a stray Enter can't power-cycle your rig, and Update Radio Firmware, which takes you straight to the updater.
- **[The Feature Availability window opens now](#feature-availability).** Tools, Feature Availability tells you feature by feature — diversity, the noise reduction family, auto notch, CW autotune — whether it's on, off, unlicensed, or unavailable, and the reason why. The same window lets you set your radio's callsign, name, and front panel display.
- **[The About page tells the truth now, all of it](#honest-about-page).** Help, About reports every moving part from the running program itself — the app's full build number, FlexLib, the Opus codec, PortAudio by its build revision, the .NET runtime, and exactly where your trace file lives, so "where are your logs" is a glance instead of a conversation. One button copies the whole report for a support email, and it all works with no radio connected — which is when you need it.
- **[Sub-watt transmit power reads as a real number](#honest-power-readout)** instead of rounding down to 0 watts, which is what the app also says when you aren't transmitting at all. If you drive a transverter or run QRP, the instrument was blind to your entire operating range. Over-S9 signal readings were also being announced six times too high in several places, and are now plain decibels.
- **[The GPS page leads with lock](#gps-leads-with-lock)** — the fact that actually decides whether your radio is disciplined — and it always announces when the reference locks, which it previously could miss entirely. It also carries the clock correction figure in parts per billion now.
- **[If the radio takes your microphone away, you hear about it](#mic-selection-assert).** Something changing the radio's transmit input behind your back — a profile load will do it — used to mean transmitting silence with no warning at all.
- **[The meters panel got torn down and rebuilt](#meters-rebuilt).** You can now hear any meter your radio publishes instead of the same eight forever, the panel is one tidy screen instead of a tab-through pile, and a test tone actually stops.
- **JJ Flexible can record your voice now, and it tells you every single time.** There's a Record button in the Audio Workshop's new Reference Audio section. Press it and it says recording has started. Press it again and it says what it saved and how long it is. While it's running it pipes up every half minute, so a recorder you walked away from is never quietly listening to your shack. Your recordings live in a folder on your computer — never on the radio, which matters, because a recording stored on the rig doesn't come with you when you're operating somebody else's. There's an Open recordings folder button for playing them, renaming them, or mailing one to a friend, and any WAV file you drop in there shows up in the list with no importing.
- **You can send a known recording instead of your microphone.** Same section: pick a recording, tick "Send the reference instead of my microphone," and key up the way you always do. It goes out through exactly the same processing your voice goes through, and your microphone comes back the moment it finishes. Nothing transmits on its own — you're still the one keying the radio. Here's why you'd bother: it's the only way to actually answer "did that change help?" Turn a knob, send the same audio again, compare. When it's you doing the talking it's a slightly different you every time, and two different takes aren't a comparison, they're two anecdotes.
- **A reference recording ships in the box.** It's the identical audio on every station running JJ Flexible, so a reading you take means the same thing as a reading I take. On it: a slate, a level tone, a steady passage long enough for the loudness meters to settle, the phonetic alphabet, counting, the same sentence said quietly then normally then loudly, a run of plosives and sibilants to hear what your processing does to them, and a few seconds of silence for the noise floor. The script it reads sits right beside it as a plain text file, so you can find out what you're about to transmit without transmitting it first. Read that same script into the recorder and you've got a reference in your own voice, on your own microphone, in your own room — the honest one for your station — that still lines up with everyone else's. And it never says a callsign and never calls CQ, deliberately: if it ever hits the air by accident, anyone listening hears it announce itself as a test recording, twice.

### Any meter your radio has, and a panel you can get around {#meters-rebuilt}

The meters panel — the one that turns your radio's readings into tones you can hear while you operate — has been rebuilt from the ground up. Here's what changed and why you'll notice.

- **Every meter your radio reports is available now, not eight of them.** The old panel offered a fixed list: S-meter, ALC, mic, power, SWR, compression, voltage, PA temp. That was never the radio's list — an 8600 publishes over a hundred meters, and we were throwing away the other ninety-something before you ever got a say. Pick from the short list of the ones most people want, or tick "Show every meter this radio reports" and go browsing. They're grouped by where they come from: the radio itself, each slice, and any amplifier or tuner you have hooked up.
- **Your existing meter tones survived the move.** This one mattered to me. The old panel stored your choice as a position in that fixed list, and the new list is a completely different shape — so a careless job here would have quietly repointed everybody's tones at the wrong meter, with nothing to tell you. Your saved meters get translated to the radio's own names on first run, and everything you'd tuned by ear — voice, volume, pan, pitch range, when it sounds — comes through untouched.
- **One meter at a time, chosen from a list.** The panel used to stack a full set of controls for every meter you had, so getting to the fourth one meant tabbing past three. Now there's a single "Meter" list at the top: pick the one you want, and the controls below it are that meter's controls. Press Delete on the list to remove a meter — it asks first, because there's no undo and you may have spent a while getting it to sound right.
- **A test tone stops.** Press Test, hear two seconds, done. It used to run forever if meter tones were on, which — since the only way into the panel also switched them on — meant essentially always. Sorry about that one.
- **Ctrl+M opens the panel and nothing else.** It used to open the panel *and* switch your meter tones on or off in the same keystroke, which is two jobs on one key and made both of them awkward. If you just wanted to look at your settings, you started a noise. Now Ctrl+M is the panel, and Ctrl+J then T is the tones — same as it's always been in the JJ layer, and it's in the Meter Tones menu too.
- **Pan is a slider instead of three positions.** Left, centre, right was fine when three meters was a lot. It isn't any more, so pan runs smoothly from full left to full right, and it tells you where you are in words rather than making you decode a number. Worth saying out loud: never let pan be the only difference between two meters. If you listen in mono, or hear better on one side, it isn't there at all — that's what the voices are for.
- **The meters section makes a sound when it opens and closes,** like every other section on Home. It was the only one that didn't.
- **Your S-meter is no longer described as a meter your radio doesn't have.** With a radio connected, the Source box read "LEVEL (not reported by this radio)" for the S-meter — on a radio that was reporting it several times a second. The tone was right the whole time; only the words were wrong, and the words are the half you can read. The cause was slices: an S-meter set to follow whichever slice you're listening to could never be matched against a list that only knew about slice 0, slice 1 and so on.
- **"Active slice" is a choice you can actually pick now.** Following whichever slice you're listening to is what your S-meter has always done and what every default is set to — but it wasn't in the list, so re-picking your source quietly pinned you to one receiver instead. It's there now, listed ahead of the numbered slices, because it's what most people want. The numbered ones are still there if you'd rather watch one particular slice.
- **A meter on a slice that isn't running says exactly that,** instead of claiming your radio has never heard of it. Two different problems that used to share one message, which sent you looking for the wrong fix.
- **Meters are named in a few words instead of a sentence.** Picking a source used to name the meter after the whole browsing line — "Slice 0: LEVEL — S-Meter Level" — which is a mouthful when it gets read out once per meter every time you arrow through your list. Now it's "LEVEL on the active slice." The long version still lives in the Source list, where the extra description is what helps you tell two unfamiliar meters apart.
- **Pressing Test twice gives you two full previews.** The second press used to get cut off after a fraction of a second, because the first press's two-second timer was still running and stopped everything when it went off.
- **The meters panel keeps working after the window is rebuilt.** A leftover from the rebuild meant the panel could stop noticing newly added meters — the original bug all over again, in the one form that would have been hardest to pin down.

[Return to version headlines](#unreleased-headlines)

### Ctrl+S is more responsive now {#smeter-answers-every-press}

Ctrl+S reads the S-meter. Press it a few times in a row and it used to go quiet — each fresh press told the app to hold off a little longer before speaking, so working the key while you rode a signal got you silence until you gave up, with your screen reader's own "control s" echo as the only proof anything was happening. And if the signal hadn't moved since your last press, a second press said nothing at all, on the theory that you'd already heard it. Wrong theory for a meter: you pressed the key because you want to know *now*, and "still S9" is an answer, not a repeat.

Now the first press answers right away, same as ever, and if you keep pressing you get a fresh reading about once a second for as long as you keep asking — with the reading from your final press always landing. Thanks to Don for catching this one.

[Return to version headlines](#unreleased-headlines)

### Slice changes can stick now {#slice-changes-stick}

Here's a thing that made me feel like I was losing my mind. Release Slice D, close the radio, come back tomorrow — and there's Slice D, sitting right where it always was, four slices parked on 14.1 USB like nothing happened. Do it again. Same result. The release worked. The app said it worked. It *did* work. And every morning the radio handed me back exactly what I'd spent the previous evening getting rid of.

The slices were never the problem. Creating them worked, releasing them worked. What nobody had told me — and I own the radio — is that the layout lives in the *radio*, in something called a global profile, and unless something writes that profile, the radio restores its stored setup the moment you connect again. Everything I did was correct and temporary, and nothing ever said so.

So it says so now, and more to the point, it tells you what to do about it.

- **Slice changes announce that they're temporary.** Add or release a slice and you'll hear "this will not survive disconnect unless you save the profile" right after the change itself. Short, once, and only when *you* changed something — the slices the radio hands you at connect aren't provisional and don't get the reminder.
- **"Save Station Setup to Radio" is on the Slice menu,** in the Selection submenu, which is the actual answer to "okay, so how do I keep it?" It sits with New Slice and Release Active Slice, because that's where you are when the question occurs to you — not four steps away on a menu you had no reason to open. It tells you which profile it's about to write and asks you to confirm before anything happens.
- **The confirmation names the profile,** and that's deliberate: there was previously no way at all to find out which global profile your radio had loaded. Now the one moment you need to know is the one moment you're told.
- **It's honest that it saves everything.** A global profile is the whole station — every slice, its frequency, its mode. If you came in thinking about one slice, the prompt says so before you commit, not afterward.
- **JJ Flexible will not save while somebody else is on your radio.** This is the one I care most about. A global profile belongs to the *radio*, and everyone who connects shares it. Saving while a second operator is connected would quietly store their slices as your station setup, so it refuses, and it tells you why instead of just going quiet.
- **There's a switch if you want to be asked automatically.** Notifications tab in Settings: "Offer to save my setup to the radio when I disconnect." It's off unless you turn it on. Every other radio you've ever owned saves your settings when you switch it off, and I get why you'd want that — but a Flex isn't off when you disconnect, and somebody else may still be using it. So this only ever *asks*. It never saves on its own, it stays quiet when you changed nothing, and it won't raise the subject at all on a radio you haven't marked as yours.

There's also a quieter fix in here. If you'd set up a default global profile that didn't exist on the radio yet, JJ Flexible would create it for you as it shut down — handy, and it still does — except it would happily do that while another operator was connected, baking their slices into your profile forever. It doesn't do that any more.

[Return to version headlines](#unreleased-headlines)

### The honest audio hub {#honest-audio-hub}

There are two completely different kinds of "volume" in a remote rig, and until now the Audio menu didn't tell you which was which. Headphone Level and Line Out Level move the jacks on the radio itself — useful if you're sitting next to it, silent if you're listening over PC audio from across town. Meanwhile the volume a PC-audio operator actually wants — how loud the radio plays through the computer — wasn't on the menu at all. One of our testers spent a very confusing session turning "Headphone Level" up and down and hearing nothing change, because nothing he could hear was changing. That's on us, and it's fixed.

Here's the new shape:

- The Audio menu has a "PC Audio Levels" item that opens a small dialog holding this computer's side of the wire: PC Output Volume (in dB — 12 is the loudness you've been living with all along, and you can now go quieter or a lot louder) and Mic Level. The dialog stays open while you ride a level with Up and Down — nudge it five times without reopening anything — every press speaks the new value, changes apply the moment you make them, your PC volume is remembered between sessions, and Escape closes it when you're happy. We tried these as menu items first, and a menu turned out to be a lousy place to ride a value: it slams shut after every single nudge. Now the menu is the door and the dialog is the room.
- "On-Radio Levels" right below it opens the same kind of dialog for the radio's own jacks — headphone and line out volume, plus mutes for the headphone jack, line out, and front speaker — all plainly labeled "On-Radio" so you always know which side of the wire you're adjusting. Two dialogs on purpose, not one: they're two different things on two sides of the wire, and keeping them apart is the whole point. Both doors are always on the menu: plenty of people run PC audio and the shack speakers at once.
- The old flat Audio Gain and Pan up/down menu items are gone — they were duplicates from the old days, and the "multiple audio up and down things" clutter went with them. Your slice's Volume and Pan still live as arrow fields in Home's audio group (`Ctrl+Shift+U`), where riding them has always felt right.
- Home's audio group mirrors all of it: PC Output Volume, Mic Level, the on-radio levels and mutes, and a read-only mic-audio field. Arrow to that last one and it speaks the same verdict the Audio Workshop gives — "just right," "coming in hot," or "turn it up," with the peak — live while you're transmitting, or from your last transmission after you unkey.
- Volume mode, `Ctrl+J` then `V`, is the fast lane for all of it — the full walkthrough lives in the keyboard reference under "Volume mode." While we were in the JJ layer, the compander picked up `Ctrl+J, C` and the speech processor `Ctrl+J, Shift+P`, so the whole "how I sound" chain rides one gesture family.
- The JJ layer's own help (`Ctrl+J, H`) now reads the complete, current command list — it had quietly fallen out of date and was skipping six commands — and it finishes by telling you where the other help lives: F1 for the control you're on, `Ctrl+/` to search everything.
- If you crank PC Output Volume all the way up on a strong signal, the audio now politely flattens at maximum instead of turning into digital hash. Your ears may still object to 24 dB of boost; the math no longer will.
- **The Earcon Scratchpad leaves your radio alone.** Opening it used to mute your slice and unmute it again on the way out. That made sense when the scratchpad just played a sound so you could check it existed. It stopped making sense once the scratchpad grew Hold, scale walk and the harmonic series and became the place you go to decide whether an alert is loud enough to hear — because the thing it has to be loud enough to hear *over* is the band, and muting took the band away. Judge it against real noise now. Your own mute is still one keystroke away, M for this slice and Shift+M for all of them, and it stays wherever you put it.

[Return to version headlines](#unreleased-headlines)

### You Pick the Audio System Now, and the List Got Shorter for It {#pick-your-audio-system}

Here's a thing about Windows that nobody should have to know: your sound card isn't offered to programs once. It's offered several times over, once for each driver model Windows supports — MME, DirectSound, WASAPI, and a low-level one called kernel streaming. Same hardware, four front doors.

JJ Flexible used to deal with that by folding the copies together and picking a door for you. Which sounds tidy, and mostly was, except for one thing: it often landed on MME, and MME quietly converts sample rates on the way through. That means MME will cheerfully tell us your microphone is running at 48 kHz whether it is or not. Every rate test I ever ran came back clean. Of course it did — I was asking the one component in the chain that has a professional interest in telling me everything's fine.

So there's an **Audio system** choice at the top of the Audio Devices dialog now. WASAPI is the default, because WASAPI tells the truth: it reports the rate your hardware is actually running at, and it says no to a device that can't do what the radio needs instead of papering over it. MME is right there if you want it, and sometimes you will — it's the forgiving one, and a device WASAPI turns down will usually work under it. The dialog says all of this in plain words, both ways round, because both choices are legitimate and which one is right depends on your gear.

The part I like: **adding this control made the dialog simpler.** Once you've picked an audio system, there are no duplicate copies of your hardware left to fold, so the folding rule is just gone. Fewer rows, fewer rules, and the reason each device is in the list is now something you decided instead of something I guessed.

And a bonus that falls straight out of it — when you're on WASAPI, any device Windows is running at a rate the radio can't use says so right in its row, with both fixes named: set it to 48000 Hz in Windows Sound settings, or switch to MME and let it convert. That's a whole category of "my transmit audio is weird and I can't tell why" that you can now see from inside the app.

Your existing setup is safe. If you've got a device saved that lives on a different audio system, it stays selected and stays working, and the dialog tells you which system it's on rather than pretending it vanished.

[Return to version headlines](#unreleased-headlines)

### Mono Microphones Work {#mono-microphones-work}

This one bothered me more the longer I looked at it.

A great many USB headset microphones have exactly one channel. JJ Flexible would list yours, mark it "not usable yet," and refuse to save it. If that headset was the only microphone you owned, the honest summary is that you couldn't use the app. The workaround — gang two inputs on an audio interface and pan them both to centre — requires owning an audio interface, which is not much of a workaround for someone whose whole audio setup is a headset.

Fixed. A mono microphone opens as mono and your voice goes to the radio on both channels. A mono speaker gets both channels mixed together, same idea in reverse. The row still tells you it's mono, because you should know what's happening to your audio, but it's a description now instead of a rejection.

While I was in there I also collapsed the two different messages that used to describe this. The list said "mono, not usable yet" and the refusal said "it needs a stereo device" — one limitation, two vocabularies, and neither of them explained anything. Turns out the right way to unify them was to delete the refusal.

[Return to version headlines](#unreleased-headlines)

### Transmit Audio Quality Is Yours to Set {#transmit-audio-quality}

Under the microphone list there's a **Transmit audio quality** setting. Full quality is the default, it's what's been running all along, and it's what you want on a normal day.

The lower settings encode your voice at a lower sample rate. It uses less of your connection and it sounds duller — that's the trade, plainly. It's there for the bad night, when a remote link keeps breaking your transmit audio into gravel and you'd rather sound like a telephone and be understood.

Two honest caveats, because I'd rather you hear them from me than discover them. Your sound card has the last word: if it can't run at the rate you asked for, JJ Flexible opens at one it can and encodes to match, rather than sending the radio something it can't follow. And since MME converts rates and WASAPI doesn't, the lower settings are most likely to actually take hold while you're on MME. The change applies from your next connection, not to one already running.

[Return to version headlines](#unreleased-headlines)

### The About page stops guessing {#honest-about-page}

When something breaks, the first question is always "what exactly are you running?" — and until now there was no good way to answer it. The About dialog's System tab now asks the running program instead of repeating what somebody once typed: every library reports its own version, straight from the code that's actually loaded. The app's full build number, FlexLib, the Opus audio codec, PortAudio, the .NET runtime, the WebView2 runtime, your Windows version — all live, none of it typed in by hand.

A few touches worth knowing about:

- PortAudio is reported by its build revision rather than its version number, because PortAudio's version number literally never changes — a build from 2021 and a build from last week both claim the same "V19.7.0". The revision is the part that tells the truth, so it comes first.
- The Support section answers what a support conversation always starts with: where the program lives on disk, whether this install carries its own .NET runtime, and — the big one — exactly where your trace file is. "Where are your logs" is now a glance.
- The copy button copies everything — all four tabs in one go — so nobody has to read version strings aloud over the air. It's labeled "Copy Everything" now, and it means it.
- The page is organized under real headings, so your screen reader's H key jumps section to section in browse mode, and all the text is selectable.
- No WebView2 runtime on your machine? The same facts appear as plain selectable text. You lose the formatting, never the information.
- Crash reports and debug bundles now carry this exact same information, assembled by the same code — so what About shows you is guaranteed to match what a crash report says was running. No more two versions of the truth.
- Escape closes the dialog even while you're reading inside the page content.

[Return to version headlines](#unreleased-headlines)

### The power readout tells the truth now {#honest-power-readout}

Here's one that had been hiding in plain sight since forever, and I only caught it because I sat down at the 8600 with the power set to zero and keyed up to see what would happen.

The radio made RF. Not much — around a tenth of a watt, three times in a row, real signal genuinely leaving the radio. And JJ Flexible said **0 watts**, which is exactly, character for character, what it says when you aren't transmitting at all.

The cause was embarrassingly simple: the power readout only ever dealt in whole watts, so anything under half a watt got rounded down to nothing. If you run a hundred watts you'd never notice in a lifetime. If you drive a transverter, or you're a QRP operator, or you're one of those people who enjoys seeing how far a fraction of a watt will get you — that's not an edge case, that's *your entire operating range*, and the instrument was blind to all of it.

Forward power now carries decimals when it needs them. A tenth of a watt reads as a tenth of a watt. Everywhere it appears: the power field on Home while you're transmitting, `Ctrl+S` (which used to say "Power 0" and didn't even name a unit — now it says "Power 0.05 watts"), the multi-slice status readout, the braille display, the spoken meter summary, and the Audio Workshop's Live Meters tab, which now shows you dBm and watts side by side so you don't have to convert in your head while you're trying to read an instrument. Press Space on the power field mid-transmit and it says "Power" and the wattage, instead of reading you an S-meter that isn't there.

And a smaller one found in the same neighborhood: strong signals over S9 were being announced *six times too high* in a few places (ten times too high on `Ctrl+S`). If you heard "S9 plus 24" on a signal that felt like S9 plus 4 — you were right and the app was wrong. It reports plain decibels over S9 now.

[Return to version headlines](#unreleased-headlines)

### The GPS page leads with the fact that matters {#gps-leads-with-lock}

If you have the GPS-disciplined oscillator option, the question you're actually asking when you open Tools then GPS and Reference is "is my radio locked to it?" Everything else — satellites, grid square, altitude — is supporting detail. But the page's live announcements were leading with the GPS fix text, which can say something perfectly cheerful while the reference hasn't locked yet.

Worse: the moment the reference *did* lock, you often weren't told. The dialog only announced when the GPS status text or the reference selection changed — lock itself wasn't on the list of things worth mentioning. So the single event you sat there waiting for could slip by in silence.

Lock leads now, in the summary, in the reference section, and in the live announcements — and a lock change is always announced. The page also picked up the radio's clock correction figure, in parts per billion, sitting right next to the lock state, because together those two are the whole answer to "is my reference any good."

[Return to version headlines](#unreleased-headlines)

### When the radio takes your microphone away {#mic-selection-assert}

When you turn on PC audio, JJ Flexible tells the radio "transmit audio comes from the computer now." It said that once, at startup, and then never checked again — so if something else changed the radio's mind afterward (loading a profile will do it), your transmit audio quietly stopped reaching the transmitter. You'd key up, hear your own monitor, see nothing wrong, and put out silence.

JJ Flexible now watches that setting while you're transmitting with computer audio. If the radio switches its transmit input behind your back, you're told out loud which input it went to, and it's set back. If *you* change it — picking the analog mic in the Audio Workshop, say — that's your call and nothing nags you about it. The app only speaks up for changes it didn't make.

[Return to version headlines](#unreleased-headlines)

### Home's got a real name now {#home-intro}

In the past, the place you landed on when you pressed F2 in JJ Flexible was called "Frequency Display" or "Frequency and VFO Display" in accessibility announcements. Neither name really told you what you were looking at, and neither was easy to say. This familiar place is now called "JJ Flexible Home." If you hear "home" while you're using the application, you're in the place your hand goes when you want to operate the rig. Home on a knobs-and-buttons radio is where your main control cluster lives — the dials and switches you touch every time you sit down at the shack. JJ Flexible's Home is the same idea, expressed as a row of accessible fields.

When focus lands there, you hear "Home" plus whichever sub-field you landed on. The exact wording scales with your [speech verbosity setting](#speech-verbosity). On Terse, it's "Home, slice" (or whichever field). On Chatty, it's "JJ Flexible Home, slice, 14.225.000" — the full story including the current frequency for context.

[Return to version headlines](#unreleased-headlines)

### Universal Keys from Home {#universal-keys-from-home}

Single-letter keys that toggle radio features on and off now operate the same way throughout your JJ Flexible Home. You don't have to navigate to a specific field to perform a specific function. In the old days, if you pressed M while focus was on the frequency tuning group, nothing would happen. Now, you can mute the active slice from anywhere in Home. We hope these changes remove some confusion from your life and add some efficiency to your operating workflow. Affected keys are as follows:

- **M** — toggle mute on the active slice
- **V** — cycle to the next slice
- **R** — toggle RIT
- **X** — toggle XIT
- **Q** — toggle squelch
- **=** — make the current slice transceive (both RX and TX on this slice)

[Return to version headlines](#unreleased-headlines)

### Escape Does Its Job {#escape-does-its-job}

Pressing Escape inside an open field group (DSP, Audio, Receiver, Transmission, Antenna) now actually closes that group and puts focus on its header. You can re-open it with Space. This was supposed to be the behavior all along; it finally works the way it reads.

Press Escape twice quickly and everything collapses at once — all open groups close, focus returns to Home, and you hear a distinctive two-tone descending sound confirming "you backed out of everything." If you've ever had a Windows Explorer moment where you wanted to just get away from the thing you were in, this is that key, applied to JJ Flexible's structure.

[Return to version headlines](#unreleased-headlines)

### Three New Sound Cues {#three-new-sound-cues}

JJ Flexible now has three distinct sound cues that fire when opening and closing field groups:

- **Expand** — when a group opens, you'll hear an ascending chirp with some sandy texture mixed in. The rising pitch means you've successfully opened a major field category. As before, you can tab into it and adjust settings. If you press a hotkey to open a category directly, you'll hear this same sound. We designed the chirp to be heard over actual radio noise, and we'll continue to tune the sounds and how they're played as more people use the software.
- **Collapse** — this tone sounds the same as the expand sound, but it drops in pitch instead of rising. The falling pitch means you've closed the category.
- **Collapse all open fields (the "gavel")** — when you double-tap the Escape key, all open category fields close and you return to Home. Listen for two distinct tones descending in pitch, confirming that everything closed. These tones are meant to feel like finality, like the thing you pressed actually did something significant. If you're having trouble activating this feature, read about [how quick is quick](#quick).

The noise texture on the chirps is designed to cut through radio static better than a pure tone would. Your ear picks out the distinctive "shhhwee" shape even when 40 or 80 meters is crashing with thunderstorm QRN.

[Return to version headlines](#unreleased-headlines)

### The sounds are yours to shape now {#sounds-you-shape}

I've been rebuilding the app's sounds for a while and mostly telling you what they'd become. This time you get the knobs. All of these live under **Settings**, then **Audio**, in the **Alerts and CW Notifications** section, and every one of them plays you a sample as you arrow through the choices — because reading the word "Sawtooth" tells you approximately nothing about what a sawtooth sounds like.

**Your CW notifications can follow the radio's sidetone.** There's a CW pitch setting with exactly two answers: use the frequency you set here, or follow whatever sidetone pitch the radio is set to. Nothing clever in between — I nearly built something that offset the notification pitch automatically to keep it out of the way, and it turns out you're a better judge of that than I am. If you pick "follow the radio" and no radio is connected, you get your configured frequency and the app doesn't make a fuss about it.

**And a CW tone shape, because a pure sine is the easiest thing on earth to bury.** Sine is what the notifications have always been. Now there's also Square, Sawtooth, Reed, Hollow and Bell, which stack harmonics on the tone to different degrees. If your CW notifications are hard to pick out of band noise — or worse, hard to tell apart from actual received CW — this is the setting to reach for before the volume. Turning a buried sine up just gives you a louder buried sine. Changing what the tone is *made of* moves it somewhere the noise isn't. They're all set to the same loudness on purpose, so when you compare them you're comparing character and not level.

**Missed a CW message? Echo it.** Press `Ctrl+J` then `E` and the app re-sends the last thing it keyed at you — the slice census, "SL A USB", whatever it was. Press it again and you step back another message, through the last ten, wrapping round at the oldest. It's the CW twin of `Ctrl+F4` for speech, and the two lists are separate so that running with speech off doesn't leave you pressing a key with nothing to say. The prosigns stay out of it: AS, BT and SK are punctuation, and "closing" arriving out of the blue helps nobody. And the "press again to go further back" window is measured from when the CW *finishes*, not from when you pressed — at 10 words per minute "SL A USB" takes nearly nine seconds to send, and a timer that started at the keypress would have given up before you'd finished listening.

**The old alert sounds are available again.** There's an Alert tone set setting with two choices: Rich, the rebuilt sounds with their shaped attacks and harmonics and the transmit-warning family that escalates three different ways; and Simple, the plain tones they replaced. Same pitches, same rhythms, same loudness — just simpler sounds. Arrow between them and you'll hear a sample of each. One honest warning if you pick Simple: the three transmit warnings go back to being pure tones that differ only in pitch, which is exactly what they were and exactly why I changed them. If you never had trouble telling the first from the last, you'll be fine.

I called these two Rich and Simple on purpose, and it took me a couple of tries to get there. My first instinct was Modern and Classic — until I remembered the app already has a Classic and a Modern tuning mode, which have absolutely nothing to do with how anything sounds. Two different Classic/Modern switches meaning two unrelated things is the kind of thing that's obvious when you built it and baffling when you're just trying to find a setting. Rich and Simple say what you'll actually hear.

**Your sign-off gets to finish now, whatever speed you key at.** When you close the app it sends you off properly — "73 SK ee", or "73 de JJF SK ee" if you're running 25 words per minute or better. The app gave that a flat five seconds to finish, and five seconds turns out to be the wrong answer at both ends of the dial. Slow down to 10 or 15 and the short version doesn't fit. Speed up to 25 and the message gets *longer*, because that's where the "de JJF" joins in, so it doesn't fit either. Between about 16 and 24, and again above 32, everything was fine — which is exactly why I never caught it, because I key at 20. The app now works out how long your farewell actually takes at your speed and waits for that instead of guessing. If your 73 has ever ended a syllable early, that's what it was.

[Return to version headlines](#unreleased-headlines)

### You get to decide how quick quick really is {#quick}

There's a new setting in **Settings > Accessibility** called Double-Tap Tolerance. It has four choices — Quick (250 ms), Normal (500 ms), Relaxed (750 ms), and Leisurely (1000 ms) — and it controls how sensitive JJ Flexible is when detecting a double tap. If Quick is selected, you have 250 milliseconds between presses to register a double tap. Select Leisurely and you have a full second. A longer tolerance may help folks with dexterity or fine motor control challenges to successfully double-tap when that was impossible before. On the other hand, if you're quick on the draw, by all means set it fast. The power is in your hands now.

This setting affects two behaviors in JJ Flexible today. First, double-tapping your left or right bracket enters filter-edge adjust mode. Second, double-tapping [Escape](#escape-does-its-job) closes all open category fields in the field list. Any future double-tap features will respect the same setting, so set it once and forget about it — we've got you.

[Return to version headlines](#unreleased-headlines)

### Squelch in Home {#squelch-in-home}

We've tried to keep JJ Flexible's behavior mirroring the older JJ Flex patterns Jim Shaffer set up. Squelch used to live only in the Receiver field group within Screen Fields — you had to expand it to toggle squelch or adjust the squelch level. Now both the squelch toggle and its level value live in Home as two adjacent fields, right after the S-meter. To reach squelch from Home, right-arrow until you hear the S-meter — squelch will be the next option. Use Spacebar or the letter Q to toggle squelch, and press up or down arrow to increase or decrease the level. When squelch is off, the squelch level field disappears from Home to keep navigation tight.

Remember, you don't need to be on the field that says "squelch" in Home to toggle it. Press Q from any Home field to toggle squelch without arrowing — it's the same universal pattern as the other single-letter toggles. JJ Flexible stores your last squelch level, so when you toggle squelch back on, it returns to whatever you set previously.

[Return to version headlines](#unreleased-headlines)

### Network Tab Configurability {#network-tab-configurability}

JJ Flexible now gives you full control over the three SmartLink networking tiers needed to conquer difficult network topologies. If you're behind network address translation — most of us are — SmartLink can bust through. That's awesome, but until now screen reader users had to wrestle with SmartSDR's inaccessible GUI to make the radio-side changes SmartLink needs. The Network tab in Settings now does the following:

- **Manual port forwarding** is the sovereign option — you set the port, you know what's happening. Use this if you want to configure your router to open specific ports yourself. The port-forward setting you change from JJ Flexible tells your radio which external port to advertise for UDP and TCP connections. You also have to use your router's admin pages to forward the matching ports. You **can** pick any external port you like — that lets you put multiple radios on the same network and still use SmartLink. Set up two port-forward rules on your router: rule 1 forwards your chosen external port over TCP to port 4994 on the radio. Rule 2 forwards your chosen external port over UDP to port 4993 on the radio. Sound complicated? It can be. If you're not comfortable with this process, you now have two more options, and JJ Flexible will try them both.
- **UPnP** is the convenience option. Turn UPnP on and JJ Flexible asks your router to set up port mapping automatically. Some operators are uncomfortable with UPnP because it can be an issue for security-conscious network setups — your choice, your network.
- **Hole-punch / extended reach** is the last-resort option for hard-to-reach or hard-to-configure networks. If UPnP and manual port forwards both fail, JJ Flexible will try to bust a hole (legally, I promise) through your NAT so that SmartLink can reach your radio.
- **Network diagnostic probe** is an option that asks Flex's SmartLink servers to test your network and its ability to reach your radio from the great beyond. This is a very useful tool because it can give you a concrete sense of how to configure SmartLink properly. The probe tells you in plain English what's working and what isn't. If the two automatic options fail, you now have the information you need to set up manual port forwarding so the Flex Systems servers can reach your configured external port.
- **Copy and save the network diagnostic report.** If something's broken, you can copy the data directly to an email, or save the report to a file and attach it to a support request to me. The report includes your radio firmware, the network test results, and other radio settings and statistics that may help me diagnose your issue — or encourage you to contact Flex directly.

JJ Flexible tries port forwarding first (if it's set on the radio), so SmartLink can operate through secure networks and networks with restrictive policies. Select UPnP or hole-punch if the tests show those are viable options for you. Either way, this is a huge step forward for screen reader users who want to independently configure their radio and its network settings.

[Return to version headlines](#unreleased-headlines)

### Every Radio Gets Its Own Settings — and Yes, You Can Finally Name It {#radios-tab}

Settings has a new Radios tab, and the Tools menu has a matching "Configure Radio" entry that jumps straight to it. Pick a radio — every radio you've ever connected to is remembered by serial number, or type a serial in directly — and the settings you save there belong to that radio and no other.

Here's why this matters. How you reach a radio is a fact about where the radio lives, not about your account. My radio needs a hole punch busted through the router; Don's radio has a proper forwarded port waiting. Until now those two preferences fought over one account-level setting — and worse, most of the controls grayed out unless you were connected. Think about that: the moment you can't connect is exactly the moment you need to change how you connect. The Radios tab works with no radio connected at all. Choose automatic (follow what the radio reports about itself — the right answer for almost everyone), forwarded ports only, or hole-punch always; pin a fixed hole-punch port if your router rewards consistency; save. The next connection to that radio does what you said.

And the name. You can finally name your radio from JJ Flexible. The name lives on the radio itself — discovery, SmartLink, and every other client show it — so saving while connected to the radio renames it for real, and JJ Flexible says so. Saving while not connected updates the name shown in your list and tells you plainly that the radio keeps its old name until you save while connected to it. No more leaving your radio introducing itself by its model number because renaming it meant a trip through somebody else's software.

One more thing lives on this tab: remote administration, for radios that live somewhere you can never be. Normally, changes that persist on the radio — port settings, firmware — require being at the radio, and that's the right default. But if your radio sits at a friend's place three states away, being "at the radio" is exactly what you can't do. Two switches, per radio, both off unless you say otherwise: allow port changes from a remote connection, and allow firmware updates without someone at the radio. When you turn one on, JJ Flexible tells you exactly what you're accepting — port mistakes are recoverable from afar, interrupted firmware updates are not — and then respects your call. Your radio, your risk tolerance, your decision.

[Return to version headlines](#unreleased-headlines)

### Signing In Without the Web Page {#native-signin}

This one goes out to Don, who spent an afternoon locked out of his own radio by a login web page that wouldn't cooperate. SmartLink sign-in is now an ordinary dialog: an email box, a password box, a Sign In button. Type, Enter, connected. Your screen reader treats it like any other dialog because it *is* any other dialog.

There's a Forgot Password button right there too — it emails you a reset link, no hunting through websites. And if your account uses two-factor sign-in, or you just prefer the old way, Use Browser Instead brings back the web page.

Here's the part you'll notice over time: signing in through the new dialog *sticks*. The old web-page sign-ins had a dirty secret — they could never actually renew themselves, and the only thing keeping you signed in was a browser cookie quietly aging in the background. When Don's cookie finally expired, so did his patience. Sign-ins made through the new dialog renew themselves properly, in the background, indefinitely. You'll be asked for your password once after updating, and then the nagging should be over.

[Return to version headlines](#unreleased-headlines)

### Enter Connects Now. For Real This Time {#selector-enter-connects}

Confession time. When the radio selector found exactly one radio, it would announce "selected, press Enter to connect" — and then Enter did absolutely nothing, because nobody ever taught the radio list what Enter means. Worse, if Windows had quietly parked your focus on the Remote button instead of the list (which it loved to do right after a remote search finished), that same Enter press would restart the entire remote search. You'd hear the list flicker and rebuild itself and wonder what on earth just happened. I wondered too, and the trace file ratted us out.

So: the radio list now honors Enter. Arrow to a radio, press Enter, you're connecting — exactly what the announcement has promised all along. And when a remote search finishes, focus lands directly on the first radio in the list, so what your screen reader announces and what Enter acts on are the same thing. If a stray press does land on Remote right after a search finished, JJ Flexible just says your remote radios are already listed and puts you back on the list instead of starting over.

[Return to version headlines](#unreleased-headlines)

### Remote, Before You Ask {#remote-first-startup}

If your radio lives somewhere else — a friend's shack, a remote site, the other end of a SmartLink — then every session starts the same way: open JJ Flexible, wait, press Remote, wait again. That first press is pure ritual. You were always going to press it.

Now the app can press it for you. In Manage SmartLink Accounts, each account has a new checkbox: "Start Remote automatically at startup." Check it, and whenever that account is the one in use, the radio selector starts hunting for your remote radios the moment it opens. You'll hear "Starting remote radios for your account" so it's never a mystery. Local radios still show up alongside like always — this adds, it never takes away. It's off by default, it's per account, and unchecking it puts the ritual back if you miss it.

[Return to version headlines](#unreleased-headlines)

### Borrow an Account Without Losing Your Own {#use-account-now}

The account manager always had Set Default — good for "this is my account, use it from now on." But sometimes you want "use this one just for tonight." Maybe you're helping a friend check their station, maybe you're the club member who connects to three different radios under three different accounts. Changing your default and remembering to change it back was the only way, and "remembering to change it back" is doing a lot of heavy lifting in that sentence.

There's now a Use Now button right below Set Default. It switches to the selected account for the rest of this session — and that's all it does. Close the app, start it again, and you're back on your default like nothing happened. One more thing got fixed along the way: when the account picker appears during a remote connect and you press Set Default there, it now actually saves that account as your default, so the picker stops asking you every time.

[Return to version headlines](#unreleased-headlines)

### Your Radios Are Already in the List {#known-radios-roster}

The radio selector used to be an empty room that slowly filled up. You'd open it, hear "no radios found yet," wait a beat, and then things would start appearing — assuming they were switched on. If your radio happened to be off, or the SmartLink hadn't been asked yet, the app behaved as if you'd never owned a radio in your life.

It knows better now. The selector opens with every radio this copy of JJ Flexible has ever met, and each row tells you where it stands: "6300inshack, FLEX-6300, remote via SmartLink" if it's there right now, or "6300inshack, FLEX-6300, offline, last seen remote via SmartLink, last seen 3 days ago" if it isn't. Rows go live as discovery finds them, and a radio that switches off doesn't vanish from the list any more — it just goes quiet and says so.

Press Enter on an offline radio and you won't get a dead end. If it was last seen through SmartLink, JJ Flexible starts looking for it right then and tells you so. If it was on your local network, it says the honest thing: it's not here, it may be powered off.

There's a favorites list, too. Bring up the context menu on any radio — the Applications key, or Shift+F10 — and choose Add to Favorites. Favorites sort to the top of the list and stay there between sessions. If you have one radio you use ninety percent of the time, this is that.

One more small honesty fix while I was in there: the "no radios found" message used to tell you to click a SmartLink button. There has never been a SmartLink button. It says Remote now, which is the button that's actually there.

[Return to version headlines](#unreleased-headlines)

### One Radio, Two Doors {#dual-homed-radios}

Here's something that was quietly true and quietly invisible. If your Flex sits on your own network and you've also registered it with SmartLink, it's reachable two different ways. JJ Flexible knew this perfectly well and never mentioned it — local discovery shouted first, won the row, and the SmartLink identity was thrown in the bin. You'd see "local" and that was the end of the conversation.

Now the row says "local network and SmartLink," and there's a Connection path control next to the list — Alt+P gets you there. Two choices: Local network, or Remote via SmartLink. Local stays selected by default, because local really is the better path: lower latency, no round trip to Flex's servers, nothing to go wrong at your router. But you can choose SmartLink deliberately, and when you do, that's genuinely the path it takes. It won't quietly connect locally behind your back and let you think otherwise.

Why would you want the slower path on purpose? Three reasons, and they're all good ones. It teaches you that both paths exist, which matters the day you're travelling and only one of them works. It lets you test your remote setup from your own chair instead of finding out it's broken when you're four states away. And frankly, it's just the truthful way to describe a radio that has two front doors.

If the radio only answers one way, the control still tells you which — it just doesn't let you change something that has no alternative.

[Return to version headlines](#unreleased-headlines)

### The Account Button Stops Guessing {#account-button-truth}

That button in the radio selector said "Switch Account" no matter what. Never signed in? Switch Account. Exactly one account saved, nothing to switch to? Switch Account. It also announced "Account updated, press Remote to connect" every single time you pressed it, whether or not you'd changed anything — including when you'd opened the account manager, looked around, and pressed Escape.

It behaves now. With nothing saved it says "Sign in to SmartLink." With one account it says "SmartLink Account" and names the account. With two or more it says "Switch Account" and tells you which one you're currently using. It only claims something changed when something changed; otherwise it says what's still true, which beats both lying and saying nothing.

There's also a line under the radio list showing the SmartLink account in play, and it's a real stop on the Tab key rather than decoration you can't reach. And when you press Remote, JJ Flexible now says which account it's signing in as before it goes — "Connecting to SmartLink as yourname@example.com." If you've ever had two accounts and stared at a radio list wondering whose radios these were, that one's for you.

Switching accounts got faster in the bargain. The moment you pick a different account, the selector paints the radios that account had last time — clearly labelled as last known, and clearly labelled as refreshing — while it fetches the current list in the background. When the real list lands you'll hear "radio list updated." You can read and think while it works instead of waiting at an empty screen. It will never connect you to a remembered radio without a live look first; the remembered list is there so you have something to read, not something to trust.

[Return to version headlines](#unreleased-headlines)

### The Port Forwarding Safety Check {#port-forwarding-safety-check}

When you press Apply in **Settings > Network**, JJ Flexible now checks whether you're the primary operator of the radio before letting you make the change. If you're connected locally to your own radio, the radio considers you the primary because JJ Flexible can detect that a microphone is connected and that you're authorized to make the change. During the change process, you'll see a confirmation dialog asking "are you sure?" The default answer is No, so that you don't accidentally store a wrong setting. Select Yes and the setting is applied. If you're connected remotely via SmartLink to someone else's radio, JJ Flexible politely refuses: "Cannot change SmartLink port settings. You must be the primary operator of the radio."

This behavior catches two different kinds of accidental changes that could otherwise occur. The first is changing a setting when you're not the radio's owner. The second is the inevitable fat-finger moment where you didn't mean to save to firmware at all — the confirmation dialog catches that too. In other words, if I'm connected to my dog Hawke's Flex Radio via SmartLink, but I forget that I'm on his radio, JJ Flexible won't let me apply a network change to his firmware. This two-layered approach is necessary, especially for a setting that persists on the radio and affects every future connection. If I could set someone else's port forwarding without knowing how their router is configured, I'd inadvertently break their ability to accept remote connections — whether for a friend or for my dog.

There is one more tie that binds us to Flex Systems' SmartSDR. For now, you must connect your Flex using SmartSDR to upgrade radio firmware. JJ Flexible will soon support direct firmware upload, and we'll use an even stricter version of this same check to ensure that you're physically at your radio before uploading firmware. SmartSDR requires a quick press of PTT or your code key to confirm you're at the radio, and that's likely how we'll support the feature too. Then ... freedom! Accessibility freedom!!

[Return to version headlines](#unreleased-headlines)

### The CW Text Boxes Know When They're Wanted {#cw-text-boxes}

If your radio's mode is set to any CW variant (CW, CWL, or CWU), JJ Flexible shows you a received text box — which, in a future JJ Flexible version, will display decoded CW — and a send box that lets you send CW by typing. Make sure VOX or full break-in is selected if you want to send CW remotely. Right now, that's the only way to send CW remotely with your Flex. Switching your Flex to voice modes like USB or LSB, or any digital mode, hides both of these boxes. They're not just hidden visually — they're also removed from the tab order. In short, they're ... gone, like really gone.

If you switch back to CW, those boxes return. This may seem like a small quality-of-life fix, but if you've been wondering why those boxes lived in your tab order during a phone QSO, the answer is: they didn't have to. Claude and I are absent-minded. I can speak for myself, and I simply forgot to disable them for modes that don't use them.

[Return to version headlines](#unreleased-headlines)

### More Stable Remote Sessions {#remote-stability}

We worked on network connectivity and stability. Hopefully, you'll never know we did, but the work was necessary to make sure you stay connected to your local or remote radio. If you've had SmartLink sessions fall over mid-QSO before, you should see fewer of those dropouts, and the system should stop telling you "connection is slow" as often. We'll keep tuning network behavior, so if you have issues, let us know.

A few more from this department, found the fun way — by things going wrong on my own radios at one in the morning:

- **Crash fix: the network-drop crash loop is gone.** If your network changed underneath a live session — a VPN switching on, Wi-Fi handing off, a cable getting bumped — JJ Flexible could crash, and then crash again on every twitch of the dying connection until you killed it. That whole family of crashes is fixed. A dropped connection now just means a dropped connection.
- **Crash reports no longer hoard your disk.** Each crash report includes a memory snapshot that can run a few hundred megabytes, and until now they piled up forever. Old reports now clean themselves up after thirty days, the folder keeps itself under a sensible size, and each report is stored once instead of twice. If you've been crash-free you had nothing to clean anyway — lucky you.
- **A firmware update that dies mid-send now says so.** If the radio closes the connection while the firmware file is still uploading, JJ Flexible tells you right then — "the radio closed the connection, the update was not applied" — instead of cheerfully waiting for a restart that isn't coming. The watcher still reports what version the radio comes back on, so you always hear the end of the story.
- **SmartLink registration tries twice before complaining.** The SmartLink server sometimes refuses a perfectly good registration on the first attempt and accepts the identical one a moment later. JJ Flexible now quietly retries once before telling you something went wrong, and tells you when it's doing so.

[Return to version headlines](#unreleased-headlines)

### Ctrl+Tab Reclaimed for Panel Navigation {#panel-navigation}

Ctrl+Tab used to pop up an "action toolbar" menu. It's disabled now, and Ctrl+Tab / Ctrl+Shift+Tab are back to doing what they do best in a tabbed interface: moving between open field groups. The action toolbar is coming back in a future release as an actual toolbar, not a menu — a persistent accessible UI surface you can navigate, not a popup that interrupted your tab nav every time you reached for it. I'm hoping the toolbar looks good too.

[Return to version headlines](#unreleased-headlines)

### The Connecting Screen Is No Longer a Trap {#connecting-cancellation}

You used to be able to open the Connect dialog, watch it say "Connecting..." for a long time, and have absolutely no way to back out short of forcing the app closed from Task Manager. Escape did nothing. The X close button did nothing. Alt+F4 did nothing. If a slice didn't free up or SmartLink hung mid-handshake, you were stuck. This was — being generous here — a bad time, particularly for screen reader users for whom "force-quit the application" means losing your place and starting over. I lived this for over three minutes one night at 1:30 AM, and that was the moment this fix moved to the top of the list.

The Connecting screen now does what it should always have done. Press Escape and JJ Flexible cancels the connection attempt right away. Click the X close button, same thing. Either way, your screen reader hears "Connection attempt cancelled" and you land back where you started.

While the connection is in progress, the screen tells you which phase it's in. You'll hear "Connected to [your radio]. Waiting for slice..." then "Slice acquired. Setting up..." Each phase change rings a small counting tone — one note for the first phase, two for the second, three for the third — so you can hear the connection make progress even when you're not glued to the screen reader. Fast LAN connects (where each phase finishes in under half a second) skip the announcements entirely so the common case stays quiet.

If a connect takes longer than a minute total, JJ Flexible asks: "Connection slow — [what's going on]. Keep waiting, or cancel?" The "what's going on" part pulls real diagnostic information from what's been happening behind the scenes. Maybe SmartLink hasn't sent your station name back yet. Maybe the slice is waiting on a remote re-add. Maybe the connection dropped and is being retried. You get the actual reason, not generic "slow" boilerplate. Choose Keep waiting and you get another minute. Choose Cancel and you're out.

Five minutes is the hard ceiling. If the connection still hasn't completed after five minutes total, JJ Flexible auto-cancels for you and says "Connection attempt timed out — cancelled." This is the safety net for the case where you walked away or got pulled into something else — your radio doesn't sit there pretending it's about to connect indefinitely.

You can silence the phase announcements and counting tones if you find them busy. **Settings > Notifications > Speak connection progress** turns them off. Critical events — connect successful, connect failed, cancel, timeout — still speak regardless. The toggle is on by default for new users so you have some confidence that the app is doing something while a connect is underway.

One more thing in the same neighborhood: if you've ever heard the 73 prosign play twice when you closed JJ Flexible while connected, that was a small bug where the disconnect handler and the app-shutdown handler each played it. Disconnect now claims the prosign for the session and shutdown steps aside, so you hear 73 once — the way Jim originally intended.

[Return to version headlines](#unreleased-headlines)

### Tuning Unity — Up coarse, Shift+Up fine, no more mode switching {#tuning-unity}

There used to be a `C` key in Modern tuning that flipped you between coarse and fine. You'd press Up and tune by 1 kHz. Press C, press Up, and now you'd tune by 100 Hz. Press Page Up to climb the step list to 5 kHz. Press C again to drop back to fine. It worked, but it was modal — silent modal, the worst kind, where you forget which one you're in until something doesn't tune the way you expected.

That's all gone. Up and Down are coarse. Shift+Up and Shift+Down are fine. One value per direction, configured in Settings → Tuning. The defaults are 5 kHz coarse and 100 Hz fine, which match what most operators set the old C-toggle to anyway.

Some collateral cleanup that came with this:

- The `C` toggle no longer exists in Modern tuning's Frequency field. Pressing C just types the letter C if you were typing a frequency.
- Page Up and Page Down on the Frequency field used to cycle through the step list. The list is gone (one value per coarse and one per fine), so the keys do nothing on the Frequency field — they still pan in the Slice field, where they always have.
- Shift+S still announces your step sizes — it just announces both now ("coarse 5 kilohertz, fine 100 hertz") since there's no "current" mode to single out.
- The Tuning menu under Slice has slimmed down: Toggle Coarse/Fine and the Step Size Larger/Smaller pair are gone. Speak Current Step is still there.
- Settings → Tuning still has Coarse and Fine pickers — just one value each, not a list. The "Edit Tuning Steps" button retired with the list.

[Return to version headlines](#unreleased-headlines)

### Audio levels live in the Audio expander, not on a hotkey {#audio-expander-volume}

Six audio-volume hotkey pairs got retired:

- `Alt+Page Up` / `Alt+Page Down` — was main audio gain
- `Alt+Shift+Page Up` / `Alt+Shift+Page Down` — was headphones volume
- `Shift+Page Up` / `Shift+Page Down` — was line out volume

All three of those values now live in the Audio expander. Press `Ctrl+Shift+U` to open it, then arrow to Volume, Headphone Level, or Line Out Level. Up and Down nudge the value, Shift+Up and Shift+Down move in finer steps, Page Up and Page Down jump by ten, Home and End snap to minimum and maximum.

The reasoning is the same one that powered tuning unity: hotkeys are for toggles, where one key reliably does one thing. Values belong with their fields, where the standard arrow conventions apply consistently. Audio levels especially aren't real-time controls — you're not flicking the volume mid-QSO; you set it once and forget it.

If you don't have a physical volume control on your speakers and you've been relying on the hotkey to adjust speaker volume on the fly, the new path is `Ctrl+Shift+U` then arrow to Volume. We're keeping an eye on whether that's a workable substitute. If it isn't for your setup, let us know — we have a fallback design ready.

The slots themselves stay reserved in the keymap so a future feature can claim them deliberately, instead of someone stumbling into them and being surprised that the keys do nothing.

[Return to version headlines](#unreleased-headlines)

### Your Radio's Volume Knobs Are Visible Now {#radio-outputs-visible}

Here's an embarrassing story that turned into a feature. I plugged headphones
into my 8600, heard nothing, and went hunting for a problem. There wasn't one —
or rather, there were two, and neither was findable from inside JJ Flex.

The first one is the big one, and it catches everybody once: **a Flex makes no
audio at all until something connects to it.** Not at the headphone jack, not
at line out, not at the front speaker. Power it on, plug in headphones, and you
get silence — by design. It's a server sitting there waiting for a client. If
you're coming from a conventional rig where the receiver is wired to the
speaker, this is genuinely surprising, and there was nowhere in the app that
said so. Now there is: it's the first thing on the new Audio Troubleshooting
help page, and it's in Getting Started too.

The second one is that on a radio with no front panel — every non-M model —
the software levels aren't one volume control among several. They're the only
volume control that exists. JJ Flex could nudge them from a menu but could
never show you where they stood. If they were at zero, the radio was silent and
nothing told you why.

So Settings, Audio tab now opens with a **Radio Outputs** group:

- **Headphone level** and **Line out level**, 0 to 100, arrow up and down.
  These take effect on the radio as you change them, not when you press OK —
  you're setting these by listening, and a level that doesn't apply until you
  close and reopen the dialog is useless for that.
- **Mute the headphone output**, **Mute the line out output**, and **Mute the
  front panel speaker**, each one saying its new state as you toggle it.

There's also a **Why is my radio silent?** button that walks the causes in
order and tells you the first thing it finds — not connected, output muted,
level at zero, radio audio not coming through your computer. It's the ladder I
should have had that evening.

[Return to version headlines](#unreleased-headlines)

### One Place to Choose Your Sound Devices {#audio-devices-dialog}

The old way to pick which sound devices carried your radio audio was two
dialogs in a row. The first asked for an input device, the second for an
output, and nothing announced that a second one was coming. Both had a list
labelled "device list" — the same words both times — with the window title
doing all the work of saying which one you were actually choosing. Cancel the
first and it marched you into the second anyway.

Worse: on a brand new install, that pair of dialogs could appear *during your
first connect*, thrown up from the background by the audio machinery, sometimes
landing behind the main window where your screen reader couldn't follow. This
gated all audio on a fresh install, and choosing by ear was, honestly, not
really possible.

It's one dialog now, and it covers everything: the device your radio's receive
audio plays through, the microphone sent to the radio, the device your alerts
and CW notifications use, and the meter tone device. Open it from the Audio
menu (**Audio Devices**), from Settings on the Audio tab, or from the Command
Finder. Each list says what it's for, the current choice is announced when it
opens, and the system default is marked in words rather than just being first
in the list.

A few things it now does that it never did:

- **It tells you when a device you chose is gone.** Unplug the interface your
  radio audio was going to, and JJ Flex falls back to your Windows default and
  says so out loud. It doesn't go quiet on you, and it doesn't block the
  connect. Your original choice stays saved, so plugging the device back in
  picks it right back up.
- **Moving a USB headset to a different port no longer confuses it.** Your
  saved devices are remembered by name, not by their position in a list that
  reshuffles every time hardware comes and goes. That mattered more than it
  sounds: the old way, a device sliding into a vacated slot could quietly
  become your transmit microphone. And a driver update that changes how many
  channels your device reports no longer loses your choice either — the name
  is what counts. If a saved device truly can't be found, JJ Flex says so;
  it never quietly swaps in whatever took its place.
- **It has a Refresh button**, because the device list is a snapshot. Plug
  something in while the dialog is open and Refresh will find it.
- **Your laptop's built-in microphone shows up now.** A lot of built-in mics —
  especially the microphone arrays in newer laptops — report themselves as
  four-channel devices, and the picker used to quietly leave anything that
  wasn't exactly stereo off the list. That could make a laptop's only real
  microphone unselectable, which looked for all the world like JJ Flex couldn't
  see your hardware. Multi-channel devices are listed now and just work — JJ
  Flex uses them in stereo, and the dialog tells you when it's doing that.
- **Mono devices are in the list too, flagged honestly.** They started out
  listed-but-refusable, which was already better than being invisible. Later in
  this same release they stopped being refusable at all —
  [mono microphones work now](#mono-microphones-work).

And a small one: **turning PC audio on now tells you the truth.** If it can't
start, you hear that it didn't, instead of hearing "PC audio on" while nothing
plays.

[Return to version headlines](#unreleased-headlines)

### Is This Thing On? {#microphone-check}

Here is a question that should never have needed a transmitter to answer: is my
microphone working?

Until now the only way to find out inside JJ Flexible was to key up and watch
the mic meter. Think about what that actually asks of you. You go on the air —
real antenna, real band, possibly from three states away over SmartLink — to
settle a question that has nothing whatsoever to do with your radio. And if the
answer turns out to be "no, it's muted," congratulations, you just transmitted
silence at somebody.

So the Audio Devices dialog has a **Microphone check** in it now, sitting right
under the microphone list. Pick your microphone, press **Start microphone
check** (or Alt+M), and talk. That's it. Nothing transmits. The radio isn't
involved and doesn't even need to be connected. You don't need SmartSDR open,
you don't need Windows Sound Recorder, you don't need a friend on frequency.

The verdict lives in a read-only box you can Tab straight to, so your screen
reader's read-current-control command is the "say my level" button — no new
hotkey to learn. It updates about twice a second while the check runs, and it
uses exactly the same words the Audio Workshop uses — *turn it up*, *just
right*, *coming in hot* — with the number in dBFS right beside them. One
vocabulary everywhere, so the same voice never gets two different opinions
depending on which window you asked in.

Now the part I'm actually proud of. Silence is not one thing, it's three, and
they need three different fixes:

- **Only the noise floor.** JJ Flexible can hear your interface but nothing is
  arriving at it — a very low number that never budges when you talk. Your
  microphone isn't plugged into the input you think it is, or the gain knob is
  down, or your condenser mic is waiting for phantom power that isn't switched
  on. JJ Flexible says so in those words.
- **Nothing at all.** Not quiet — literally, mathematically nothing. Every
  sample zero. A working microphone always has a little hiss on it, so this
  means Windows is handing us silence rather than audio, and that's a mute
  somewhere, not a microphone problem.
- **Windows is blocking us.** Which brings me to the good bit.

**JJ Flexible now handles Windows microphone privacy the way Zoom does.**
Windows has a switch buried in privacy settings called "Let desktop apps access
your microphone," and when it's off, your microphone doesn't fail — it just
delivers perfect, convincing silence. Every meter reads zero. Every cable looks
fine. I have watched people take an entire shack apart over that switch.

If it's off, JJ Flexible now says so by name, tells you which switch it is, and
offers a button that opens the Windows privacy page directly. One press, one
toggle, come back, run the check again. If it's a policy your workplace set,
you get told that too, because sending you to a page that can't help you is its
own kind of rude. And if nothing is blocked, that button never appears at all —
I'm not going to nag you about a setting that's already correct.

The check hangs on to your microphone only while it's running. Stop it, switch
devices, refresh the list, or close the dialog, and it lets go immediately.

[Return to version headlines](#unreleased-headlines)

### One Device, One Line in the List {#one-device-one-line}

Windows has a slightly embarrassing habit: it offers the same sound hardware to
programs three or four separate times, once for each of the sound systems it
has accumulated over thirty years. Your interface isn't listed once. It's
listed as many times as Windows has ways of talking to it.

Which meant that until now, plugging in a nice new USB interface filled the
device picker with a small crowd of identical-looking choices, and exactly one
of them was the one you wanted. Choosing by ear, that's not a decision, that's
a coin flip. On my own machine the microphone list had **48 entries** for what
any human being would call four devices.

One piece of hardware is one line now. That same list is down to 22 lines, and
the ones that remain are things you'd recognise.

The first version of this fix folded the copies together and took the most
modern route to your device on my judgement rather than yours. It worked, and
it had a flaw I didn't spot until later in this same release: folding means
*something* has to choose which copy wins, and that something was me, silently.
[You pick the audio system now](#pick-your-audio-system) — which turned out to
make this list shorter still, and honest about why each row is in it.

While I was in there, a few more honesty upgrades to that list:

- **Devices say how they're attached** when Windows tells us plainly — USB,
  Bluetooth, HDMI. When it doesn't, JJ Flexible says nothing rather than
  guessing, because a label that's confidently wrong is worse than no label.
  (I did try to work out built-in versus a jack on the back. Windows knows, but
  it won't say — on my machine it described a USB audio interface and a piece
  of pure software with the identical answer. So I'm not going to pretend.)
- **Entries that are really a loopback are called out.** Some entries in the
  microphone list aren't microphones at all — they're whatever your computer is
  currently playing, handed back to you as a recording source. They look
  completely legitimate. Pick one as your transmit microphone and you'll put
  your own received audio on the air, which is a fun way to make a pileup very
  cross with you. They now say what they are.
- **The Sound Mapper entries admit what they do.** "Microsoft Sound Mapper" and
  "Primary Sound Capture Driver" aren't devices, they're pointers at whatever
  Windows is currently set to. The list says so.
- **A saved device that isn't plugged in stays visible.** It sits at the top of
  the list marked "Not connected" instead of silently vanishing. Leave it
  selected and JJ Flexible keeps it saved for when you plug it back in; pick
  something else and it switches. Either way you chose, rather than finding out
  later.

And if you want the old everything-everywhere view, there's a **Show every
sound endpoint** checkbox at the bottom of the dialog. That view lists every
copy, names the sound system after each one, and includes the low-level kernel
entries that are normally hidden. It's there for when you're chasing something
strange. It resets itself the next time you start JJ Flexible, because a
diagnostic you forgot you turned on is just a confusing dialog.

One deliberate piece of restraint: if your audio already works, pressing OK
does not quietly move you onto the new preferred route. A configuration that
works keeps working until you choose otherwise.

[Return to version headlines](#unreleased-headlines)

### CW Notifications Moved In With Their Speaker {#cw-with-alert-device}

CW notifications play through your alert device. They always have. But the
switch that turned them on lived on the Notifications tab and the device they
used lived on the Audio tab, which meant setting up prosign notifications
involved two tabs and a leap of faith about whether they were related.

They're together now, on the Audio tab, under **Alerts and CW Notifications** —
the device, then the enable checkbox, then sidetone and speed. The Notifications
tab keeps a line pointing at the new spot, because a setting that moves should
say where it went rather than just vanishing.

Nothing changed about the defaults. CW notifications are still off unless you
turn them on — not everybody does CW, and I'm not going to start beeping
prosigns at people who don't.

[Return to version headlines](#unreleased-headlines)

### RIT and XIT Scale-Adjust Mode {#rit-xit-scale-adjust}

When you're chasing a slow drift in someone else's signal, RIT (or XIT) is the tool. The old way to tune the offset was to navigate the cursor through the 1, 10, 100, and 1000 Hz columns of the field, then press Up or Down at the column you wanted. That works fine when you've got time. It does not work fine in a pile-up while everyone's calling and the DX is climbing.

Now there's a quick path. With focus on the RIT or XIT field, press 1, 2, 3, or 4. You enter scale-adjust mode at 1 Hz, 10 Hz, 100 Hz, or 1 kHz respectively. Your screen reader announces the mode ("RIT adjust, 100 Hz"). A rising mode-enter tone confirms you're in. Now Up and Down walk the offset by that scale. Press another digit 1–4 to switch scale without leaving the mode. Press 0, Escape, or navigate to a different field to exit; you'll hear a descending mode-exit tone on the way out. Pressing R (or X) to toggle RIT (or XIT) off also exits — useful muscle memory for the bail.

The decade-position cursor approach still works when you're not in scale-adjust mode. Digits 5 through 9 still type at the cursor like before. The new mode is purely additive — Don's fast path, sitting alongside the legacy path.

This is the third home of a pattern we've been quietly building toward: the sticky-but-announced modal. Filter-edge grab uses it (double-tap a bracket key on the filter to grab one edge for adjustment). RIT and XIT scale-adjust is the same idea applied to offset tuning. The plumbing is shared now, so future features that want this feel — a deliberate entry, an announced state, a focus-bound exit — can use it without re-rolling.

[Return to version headlines](#unreleased-headlines)

### The Program File Has a New Name {#exe-rename}

The application you launch is now called `jjflexible.exe`. It used to be `JJFlexRadio.exe`. That's the whole change on disk, and for almost everybody it's invisible — the installer swaps your Start Menu and desktop shortcuts over to the new file and cleans the old one out, so you launch JJ Flexible the same way you always have and land in the same place.

Nothing you care about moves. Your settings folder is still `JJFlexRadio` in your user profile, your per-radio configurations are still keyed to your radio's serial number, your saved SmartLink accounts, your CW messages, your logs, and your key definitions are all untouched. Upgrading over an existing install keeps every one of them. I want to be plain about that, because "they renamed the program" is the kind of sentence that makes a person back up their whole shack PC before updating. You don't have to.

The one thing that doesn't fix itself: a shortcut you built by hand, or a taskbar pin. Those point straight at the old file name, and they'll stop working. Unpin and re-pin from the Start Menu once and you're set for good.

Why do it at all? Because code signing is coming, and Windows builds its trust in an application against a specific signed file. The first time you download a newly signed program, Windows SmartScreen tends to be suspicious of it, and that suspicion only fades as the signed file accumulates a history. If I renamed the program *after* signing it, that history would reset and every one of you would get the scary blue "Windows protected your PC" screen all over again. Better to take the name I intend to keep and start the clock once. It also finally lines the file up with what the application has actually been called for a while now.

[Return to version headlines](#unreleased-headlines)

### The Installer Brings Everything It Needs {#self-contained}

When you run the new Setup on a fresh Windows machine, JJ Flexible just installs. No more "this app needs Microsoft .NET 10 — please install it first," no chasing a separate download from Microsoft's website, no extra UAC prompt for a runtime installer that may or may not read well in your screen reader. Hit Setup, accept the install location, you're in. JJ Flexible carries its own copy of the runtime now.

The trade is download size. The new installer is about 55 MB instead of about 10 MB, because the runtime is bundled inside. You only download it once per release, so the cost is a single concentrated hit instead of a multi-step scavenger hunt the first time. If you already have .NET 10 installed for another app, no conflict — JJ Flexible runs on its own bundled copy and minds its own business.

Why this mattered enough to do: the "you need a separate runtime" experience for someone using a screen reader on a brand new shack PC was a wall. Microsoft's runtime download page wasn't always clean to navigate; the runtime installer occasionally threw a UAC prompt that read poorly under JAWS; and after all that work the original Setup might still demand a different prerequisite. Installing JJ Flexible should be one step. Now it is.

[Return to version headlines](#unreleased-headlines)

### The Trace Archive Browser {#trace-archive-browser}

Every time you connect JJ Flexible to a rig, the app keeps a quiet record of what happened. Successful connect? Filed. Slow retry that eventually worked? Filed with that detail attached. Stuck connect that you finally killed by closing the window? When you next launch the app, last session's trace gets filed too, marked as killed so you can tell it apart from clean exits. Old archives age out automatically after thirty days so the folder doesn't grow forever. All of this has been running in the background for a while now.

What was missing was a place to actually look at any of it. That place is here. Open the Help menu, choose Tracing, and you'll find a new tab next to the existing tracing controls labeled "Archive Browser."

The browser shows you every archived session as a row in a list. The columns are date, how long the session ran, what the outcome was (success, AS retry then success, killed, that sort of thing), which radio you were connecting to, and how big the compressed trace file is on disk. Arrow up and down through the rows and your screen reader speaks each one as a chord — outcome, target, duration. The amount of detail scales with your speech verbosity setting, so on Terse you hear the essentials and on Chatty you also hear how many key events were tagged in that session.

Above the list is a row of filters. There's a date-from and date-to picker (both optional — leave the checkbox unchecked to ignore that bound), an outcome dropdown that defaults to Any, and a free-text search that matches connection target name, IP, callsign, and outcome reason. As you change a filter, your screen reader politely announces "twelve of forty-seven shown" so you know it took effect without having to navigate over to look. The search box debounces — it waits for you to stop typing before it actually filters, so you don't get a flood of announcements per keystroke.

Below the list is a detail panel that fills in when you select a row. It shows the outcome reason, the connection target, any key events the session tagged ("AS retry attempt 2 remote", "slice in use", etc.), the app version that produced the trace, the verbosity level it was running at, and the full file path on disk.

There are four action buttons:

- **View Trace** opens the selected trace in your default text viewer (Notepad on most systems). It pulls the file out of the compressed archive into a temp folder first so the viewer can read it. Press Enter on a row to do the same thing.
- **Copy Path** copies the full disk path of the selected trace to your clipboard, useful if you want to paste it into an email or attach it to a bug report. Ctrl+C on a row also works.
- **Export Selected** lets you pick several rows (Shift-click or Ctrl+A for select all) and bundles them into a single zip file at a location you choose. Convenient for sending me a stack of related traces.
- **Delete Selected** removes the selected traces from disk and from the archive index. It asks first. The Delete key on a row works too.

At the bottom of the tab there's a footer line showing how much space the archive is using total and how many entries it holds, plus a "Prune Now" button. Auto-prune runs in the background at thirty days, but if you want to prune sooner — say, you're trying to free up a bit of disk — you can change the retention number to anything from 1 to 365 days and click Prune Now. It asks for confirmation, then announces how many entries it removed.

The tracing tab on the left is exactly the same as it was — start a user trace, pick a level, set a file name. Nothing changes there. The browser just gives you a window into what the background trace persistence has been quietly building up for you.

[Return to version headlines](#unreleased-headlines)

### JJ Flex Updates Itself Now {#in-app-updates}

This one's been a long time coming. Up to now, getting a new version of JJ Flexible meant I'd email or message you, you'd go find an installer, you'd run it, and I'd cross my fingers that it worked. That works for a small group of testers, but it's not what users deserve, and it definitely isn't what people who use a screen reader to install software deserve. Hunting for a download link in a browser, getting past SmartScreen, finding the saved file — every step is a friction tax, and the people who most need the latest fixes are the people who pay the most tax. So now JJ Flexible takes care of all of it.

**Settings, Updates tab.** A brand-new home for everything update-related. You pick your channel — Stable, Beta, or Nightly — and decide whether JJ Flex should check on its own. The defaults are friendly: yes, check at launch; yes, check every couple of hours while you're running. If you'd rather drive yourself, flip both toggles off and use the manual button. The tab also shows when the last check ran, so you always know.

**The three channels.** Stable is what most people will want — it's the public release line, the version that's been through the most testing. Beta is a step earlier, where milestone previews land for folks who like to try things slightly ahead of the rest. Nightly is the bleeding edge — every overnight build that compiles cleanly. Nightly is great if you're a tester or you want a fix the same day it lands, but it can also surprise you with brand-new bugs that haven't been caught yet. The first time you switch to Nightly, JJ Flex makes sure you understand that — a confirmation dialog explains the deal so you know what you're opting into.

**Tools menu, Check for Updates.** One-click manual trigger. Same flow as the Settings tab's "Check now" button, but accessible without opening Settings. If you're connected to a radio when you click it, JJ Flex asks first ("hey, applying an update will close the app — check anyway?") so an accidental click during a QSO doesn't kick you out.

**Updates know how big they are.** When an update is available, the dialog tells you both the delta size (how much you'll actually download) and what the full installer would have been. Most updates between nightlies are 5 to 15 megabytes instead of the 100-plus megabytes a full installer would be, because the .NET runtime almost never changes between nightly builds. We only fetch the files that changed. The dialog will say something like "12 MB delta, 92% smaller than the full installer." You see exactly what you're committing to before you say yes.

**Skip a version, change your mind later.** Don't want a particular update? Hit Skip This Version and JJ Flex won't pester you about it again. If you change your mind, the Settings tab can still show it to you. Cancel just closes the dialog and we'll ask again at the next check.

**No silent phone-home.** This is important enough to spell out: JJ Flex only contacts the update server when you've allowed it to. The auto-check toggles are right there in your face, and even with auto-check on, the dialog asks before downloading anything. If something goes wrong fetching the manifest, JJ Flex stays quiet on the launch path — no nag for a transient network blip — but the manual Check for Updates command will tell you what happened so you can troubleshoot.

**Failsafe path.** Most updates use the small delta route, but if for any reason the delta path fails (a partial download, a hash mismatch, anything), JJ Flex automatically falls back to fetching the full installer and running it the old way. You don't see this — it just happens. Either way, you end up on the new version.

[Return to version headlines](#unreleased-headlines)

### Know Your Radio: where the jacks actually are {#know-your-radio}

Here's a thing nobody tells you when a new radio lands on your desk: there are a *lot* of ports back there. The manual in the box will happily explain all of them, right after it shows you a photograph. Which is a great help if you can see the photograph.

So there's a new set of help pages, one per radio, that describe your radio's panels the way you'd actually want them described. Not "see figure 25-4." Instead: find the VGA-style connector with the two screw posts, because it's the easiest thing back there to identify by touch, and now everything else can be described relative to it. Just left of it is a square of four identical little jacks in two columns of two. The microphone jack is the bottom one of the column nearest the accessory connector, and the key jack is directly above it. That kind of thing. Landmarks first, then a full inventory of every jack on both panels.

You'll find these under Help, in a new "Know Your Radio" section, with a page each for the FLEX-6300, the 6400 and 6600, the 6500, the 6700, and the 8400 and 8600 — the M models included, since their jacks live in the same places even though their front panels are covered in knobs.

Better still, you usually won't have to go looking. Any dialog that asks you to go put your hands on the radio — registering with SmartLink, sending firmware — now carries a button reading "Where are the jacks on my radio?" It opens the page for the radio you're actually connected to, sits on top of the dialog you were in the middle of, and drops you right back where you were when you close it. Alt+J gets you there.

**The one that bites people.** Where the hand mic plugs in, and whether that one plug is enough, is *not* the same across the line. On the 6300, 6500 and 6700, the mic goes into a round eight-pin connector on the front, and that single plug carries push-to-talk along with it. On the 6400, 6600 and the whole 8000 series, the mic goes into a small jack on the back and it carries **no** PTT at all — the hand mic has a second RCA plug that has to go into the separate PTT jack, or that PTT button does exactly nothing. That's not a broken radio and it's not a broken mic. The jack simply has no PTT pin. This is the single most useful fact in the whole set of pages, which is why it's on the first one.

**Everything here was checked against the pictures.** Each page was written from FlexRadio's own hardware reference manual for that radio, and then checked against the panel photographs in it — because reading a manual's text without looking at its figures gets jack positions wrong, and confidently wrong directions are worse than none. That's also why the Aurora radios aren't in the list yet. They're built on the 8400 and 8600, but "built on" isn't the same as verified, so rather than a stub page that might send you to the wrong end of the radio, the button just doesn't appear. When their panels get checked against a manual, the page shows up and the button starts offering it.

The FLEX-6500 page says so where it applies, too: FlexRadio documents the 6500 and 6700 rear panels together but only ever photographs the 6700, so that page tells you the connector list is solid and the exact left-to-right positions are a good guide rather than a guarantee. You deserve to know which is which.

[Return to version headlines](#unreleased-headlines)

### Hear Yourself Before Anyone Else Does {#audio-check}

Every ham has done the "how's my audio?" dance — find a friend, key up, fiddle with a setting, key up again, apologize for the third time. The Audio Workshop now has an Audio Check that replaces the friend. Start a check (there's a button in the workshop, `Ctrl+Enter` from anywhere inside it, or ask the Command Finder to "check my transmit audio") and the radio keys up while your own transmit audio plays back to you live in phone modes. Talk, adjust, listen. Press Escape once and transmit stops immediately; press it again and the workshop closes — Escape always gets you out, in that order, on purpose.

Here's the part I'm proudest of: **by default, the check puts no RF on the air at all.** It rides the dummy load mode — zero watts, transmit and tune power both parked at nothing — and every meter the check reads works exactly the same, because they all measure your audio before it ever reaches the power amplifier. I proved this at the radio: a test tone read within a decibel of the same value at zero watts as it ever did with power up. So the check that measures your audio no longer keys a carrier onto whatever frequency you happen to be sitting on — which matters a lot once test tones are one checkbox away. When you *do* want to confirm that RF actually leaves the radio — a different question, and a good one — flip "Transmit power during checks" to Low power and set the wattage yourself, right down to 1 watt. Your choice is remembered per radio. And the announcement tells you which kind of check you're getting, in words: "Audio check, dummy load, no RF" versus "Audio check, transmitting at 1 watt." No more hearing "transmitting at zero watts" and wondering what broke.

The workshop also grew a live mic reading you can actually sit on. It's a read-only text field right after the Start button: land the workshop, Tab once, and there's your level — "just right, peak minus 11 dBFS" — updating continuously while you transmit. Because it's a real field, your screen reader's own read-current-control command re-reads it as often as you like, with no special hotkey to remember and no speech firing when you didn't ask. When the workshop opens, focus is already on Start Audio Check: if you're set up, Enter starts the test — zero navigation for the common case. And once a check is running, focus lands on Mic Gain with the reading one Shift+Tab back and Stop one more, so the whole adjust-and-listen loop lives in three neighboring stops.

While we were at it, the preset buttons answer to the keys your fingers already know: `Ctrl+S` saves a preset, `Ctrl+O` loads one. That also quietly fixed a real bug — Save Preset used to grab `Alt+Shift+S` inside the workshop, which is the app-wide "speak my transmit status" key, so the one place you most wanted to ask about your audio was the one place you couldn't.

A word about safety, because this feature can key your radio: it never does so silently. Every key-down announces itself — the mode, the frequency, the power when there is any — and every key-up announces too, so you always know whether you're on the air. The check also times itself out — it will not leave you transmitting because you got pulled away.

There's a recorder in there too. Record a take, unkey, and it plays back on its own about a second later. Take one, tweak the compander, take two, compare — your radio holds about two minutes of audio, which is more "testing, testing, one two three" than anyone needs. What you hear is your full processed transmit audio, the same chain the other station gets.

On radios with transverter ports there's also a Loopback Check that sends genuine RF from one port to the other, so you can hear your signal after it's been transmitted and received for real. Honest label on this one: it's a rough listen — good for "is my audio present and shaped right," not a faithful off-air recording. A receiver in the same box, inches from its own transmitter, is drinking from a firehose. For ground truth, a cheap SDR on a real antenna still wins, and the help says so.

And the key that opens the workshop, Ctrl+Shift+W, now reliably opens the workshop. An old saved key assignment could silently steal that chord for switching S-meter units — if your workshop key has ever "changed the units" instead, that's what happened, and it's fixed.

[Return to version headlines](#unreleased-headlines)

### Press D, Get D {#slice-identity}

Here's a bug with a long reach. JJ Flexible tracked your slices by their position in a list, while the radio names them by letter. Alone on your radio those two always agree. Add MultiFlex — another station holding slices on the same radio — or churn slices for a while, and they could drift apart. The symptoms were maddening precisely because they were rare: jump to slice D and land somewhere else, change mode from the menu and watch it land on a slice you weren't listening to.

The letter is the identity now. When you say D, JJ Flexible finds the slice the radio calls D — not the fourth thing in a list that may or may not start where you think. Announcements always speak the radio's true letter, so what you hear, what you press, and what the radio does are finally the same conversation.

Related fix from the same digging: Release All Extra Slices keeps the slice you're on. If you're on B and release the extras, you stay on B — it no longer walks you back to slice A as a parting gift.

[Return to version headlines](#unreleased-headlines)

### The Transmit Slice Shows Its Face {#transmit-slice}

Which slice keys your radio when you press PTT? The radio always knew; now you do too, without pressing anything. Home has a Transmit slice field, sitting just past VOX, showing the transmit slice's letter — or a dash, spoken as "none," when no slice will key the radio at all.

On the field: Space sets transmit to the slice you're on, Up and Down move it between slices, a letter A through H sets it directly, and Delete or Backspace clears it. Cleared is a real state, not an error — "Transmit slice cleared. No slice will key the radio." Think of it as a soft transmit lockout: nothing keys up until you assign transmit again. Press ? on the field and it speaks all of this, like every other Home field.

The Slice menu has a matching Transmit Slice submenu, so the same choices are two keystrokes away even when you're nowhere near Home. Before this, moving transmit lived entirely on a hidden T keypress in the Slice field — still there, still works, but no longer a secret.

[Return to version headlines](#unreleased-headlines)

### Transmit Power Finally Has a Front Door {#power-dialog}

Transmit power had no menu path at all — none — and the field for it lived deep in the Screen Fields panel. Now: Radio menu, Transmit, Power (Alt+R, T, P walks straight there; it's also under Slice, Transmission). The dialog holds transmit power and tune power together, each change applies to the radio as you make it, and Escape closes when you're done. No OK button to hunt for, because there's nothing to confirm — you set power by result, not by form.

The transverter part is the quiet star. Select a transverter port as your TX antenna and the power controls switch from watts to dBm, with two decimal places, matching how transverter drive actually works — and JJ Flexible tells you about the change at the moment you select the port. Typed entry takes a minus sign and a decimal point, because minus ten point five is a number you genuinely need there.

While the door was open, the whole transmit chain got menu paths: mic gain, mic boost, mic bias, compander, speech processor, TX monitor, and the TX filter, plus TX antenna selection right next to Power — where you can hear how the two relate. The Command Finder knows all of them by name.

[Return to version headlines](#unreleased-headlines)

### When a Connect Fails, You Hear Why {#connect-failure-honesty}

This one comes from a hard week of real debugging. A remote radio wouldn't connect; the app said "Connection failed" and nothing else — while the whole time it was holding evidence that told the real story. Never again. A failed connect now speaks its evidence:

- **Refused and timed out are different problems, and now different sentences.** A refusal means your router answered and nothing was behind the rule — check the port forward. A timeout means the packets never arrived at all — firewall, ISP, wrong address. You hear which one happened.
- **The router rule, read aloud, verbatim.** When the evidence points at your router, JJ Flexible builds the exact rule from what the radio itself advertises — external ports, the radio's address, the fixed internal ports — and speaks it. Nothing for you to reconstruct, nothing typed from anyone's memory.
- **Sign-in problems say "sign-in."** Only an actual rejected sign-in brings up the login form. A network hiccup or a server problem now says your sign-in is fine, instead of marching you through a pointless password ceremony.
- **The misleading "no RX antenna" message is gone** for failures that were never about antennas. If the radio's setup data never arrived, that's a connection problem, and it says so.
- **Auto-connect failures explain themselves too.** The morning dialog no longer just says your radio "is not available" — it includes the same reasoned report, so you know whether to check the radio, the router, or your account before you've had coffee.

Two related additions in the same spirit. The Status dialog — and the radio selector — now carry a network identity card: who the radio is (model, serial, firmware) and exactly how this computer reaches it, including forwarded-port status and the most recent reachability test, one arrow-readable line at a time. And the "Test network" button now warns you first when you're connected through a hole punch, because that particular test can knock down the very connection you're testing — your call, made with the facts.

[Return to version headlines](#unreleased-headlines)

### Reboot and Firmware Moved Onto the Radio Menu {#radio-maintenance}

Rebooting your radio used to mean a walk to the power button or a dig through Settings. The Radio menu now ends with a small maintenance section: Reboot Radio and Update Radio Firmware. Reboot asks you to confirm before it does anything — a menu you can arrow through should never be one stray Enter away from power-cycling your rig mid-QSO. Update Radio Firmware drops you at the firmware updater in Radio Setup, already on the right tab.

[Return to version headlines](#unreleased-headlines)

### The Feature Availability Window Actually Opens {#feature-availability}

Confession: Tools, Feature Availability has been on the menu for a long time, and choosing it did absolutely nothing. The window existed; the door was never connected. It opens now, and it's worth the visit — every gated feature on your radio listed with its true state and the reason: diversity ("unavailable - model lacks diversity support" or "need two RX antennas"), the whole noise reduction family, auto notch, CW autotune. "Unsubscribed" tells you it's a license thing; "not available in FM mode" tells you it's a mode thing; "select a slice" tells you it's a you thing. There's a Refresh License button for right after you've changed your subscription.

The same window's General tab is useful in its own right: your radio's model, firmware, serial, and address, plus three things you can change — the radio's callsign, its name (the one every client and SmartLink shows), and what an M-model's front panel displays.

[Return to version headlines](#unreleased-headlines)

### Push-to-Talk Holds Under JAWS {#ptt-holds-under-jaws}

Here's an ugly one we went looking for before anybody had to report it. JAWS handles a held-down key differently than NVDA does: instead of passing the hold through, it can feed the application a rapid stream of press-and-release taps. If that happens on Ctrl+Space, the radio would key and unkey about four times a second while you're holding the key and talking — you'd have no idea, and the station on the other end would hear your voice chopped to bits.

Push to talk now recognizes that tap stream for what it physically is — one finger, holding one key — and keeps the transmitter keyed until you actually let go. The detection is automatic and cautious: if your screen reader delivers held keys normally (NVDA does), absolutely nothing changes, not even a millisecond. If it doesn't, the very first transmission of a session may still hiccup once while the app figures out what's going on; every hold after that is solid. The one cost on an affected setup is that unkeying trails your finger by about a third of a second — a far better deal than transmitting your QSO through a picket fence.

[Return to version headlines](#unreleased-headlines)

### The JJ Layer Helps You Recover from a Near Miss {#jj-near-miss}

The JJ layer (Ctrl+J, then a key) puts different commands on the same letter depending on the modifier — A is Auto Notch while Ctrl+A is PC audio, D is tuning speech debounce while Ctrl+D starts a diagnostic capture. Which means sooner or later your fingers press Ctrl+G when you meant plain G, and until now the answer was "Unknown command. Press H for help." — a dead end that sends you off to listen to the whole command list for the letter you nearly pressed.

Now, when the chord you pressed is empty but the same letter does something at another modifier level, you're told exactly that: "Ctrl+G is not a command. G: Arm or disarm the TX test tone." One press of Ctrl+J and the right letter, and you're back on track. The layer teaches you its own map at the exact moment you need it.

[Return to version headlines](#unreleased-headlines)

### Your Key Map Heals Itself After an Old Build {#key-map-heals}

If you've been running a build from before mid-August, your saved key map was written in an older internal numbering, and loading it into a current build could silently attach some of your keys to the wrong commands — everything looks fine, the file loads, and then a key you've trusted for months quietly does something else. That's about as nasty as a bug gets for an operator who lives on the keyboard.

The app now spots that damaged numbering by evidence, per key, the moment the file loads — and repairs it. Any binding you personally customized is never touched by the automatic repair: those are left exactly as you set them, because only you know what you meant by them. If your map was all stock bindings, the whole thing heals silently on first launch and you'll never know anything happened. Which is the point.

[Return to version headlines](#unreleased-headlines)

### Under the kitchen sink: stuff that might interest you but probably not {#under-the-kitchen-sink}

- The new sound cues are tuned to cut through real radio noise. Background audio processing favors the earcon frequencies during a chirp, so you can still hear the cue when the band is crashing.
- A shared safety check now protects destructive operations. Today's port-forward apply uses it, and future features like firmware upload will share the same guard. One place to tighten if we ever need to, not twelve.
- Your per-operator accessibility preferences now persist across app restarts, so whatever you set on one session is waiting for you on the next.
- Home and the Screen Fields panel are cleaner siblings now. Home is where you operate minute-to-minute; the panel is where you reach for deeper settings. Less stepping on each other's toes.
- Value fields that go below zero now take a typed minus sign — RF gain at minus 8 is a couple of keystrokes, not an arrow march down from zero. Fields that never go negative tell you so instead of silently ignoring the key.
- Every successful connect now lands with the signature double-beep — local, remote, or auto-connect. A fast home-network connect used to complete in total silence, which read as "did anything happen?"
- The headphone and line out volume keys speak the level as they change it, and the line out keys now work whether or not PC audio is running.
- The SmartLink account manager has a "Start Fresh" button that clears every saved sign-in and walks you straight into a clean login — the reset that used to mean deleting a settings file by hand with the app closed.
- A handful of menu items that were quietly dead are now alive: Station Lookup, Operators, Connected Stations, Local PTT On, and Band Plans. The few that really aren't built yet now say "not yet implemented" instead of falsely blaming your radio connection.
- The "Gather debug info" bundle travels light now. It used to pack your entire trace archive — up to thirty days of sessions — into every bundle; it now brings the five most recent sessions, which is what a bug hunt actually reads. Your full archive stays right where it was on disk.
- The debug bundle also stopped hauling the whole program around — and learned to check your install while it's at it. It used to include a complete copy of the program folder, and once the installer started carrying its own .NET, that meant roughly 190 megabytes of files that are identical on every machine. The save crawled, and the result blew past the size limit of basically every email and upload form. The bundle now carries a fingerprint list of every file in your install instead, checked against the list of what your release actually shipped. Everything matches? The completion message tells you "Install verified clean." Something's damaged, missing, or doesn't belong? A plain-text report in the bundle names it, file by file — so a corrupted install diagnoses itself now instead of hiding in a mountain of binaries. And the save finishes in a blink.
- **Check runs save themselves now — so that Test ID finally means something.** Every run of the transmit checks (Tools, then Fix) writes itself to disk as it happens, stage by stage, not when you close the window. Fair's fair: the report has been leading with a test ID all along, and one of you asked the obvious question — "do these things get saved somewhere?" The honest answer used to be no; the ID named a run that evaporated when the window closed. Now the ID names a saved report. Close the checks mid-run, have the computer crash, whatever — everything measured up to that moment is already on disk under that ID, ready to quote in an email next week or set beside a fresh run to prove a change actually helped.
- **Saved check runs have a home: Tools, Fix, then Saved check runs.** Every saved run is listed there, newest first, each line leading with its test ID. Open one and the full report reads exactly like the live page, with a copy button. Export it as a web page or plain text to attach to an email — and if you want a PDF or a Word document, any converter that eats web pages will happily oblige. Delete the ones you're done with; the newest two hundred keep themselves. A run that stopped part-way says so in the list, and opening it leads with what's changed on your radio since it stopped — "Tune power changed from 10 watts to 100 watts" — naming the exact stage to run again, so you never trust a measurement your own settings have quietly outgrown.
- **The checks switch on the diagnostic recording for you — and tell you they did.** A sighted operator gets a little recording light; you get a sentence, which is better anyway. When a check run starts, the detailed diagnostic capture starts with it and announces itself, and when the run ends it stops, gets saved, and the saved run remembers exactly which recording goes with it. If you already had a capture running for your own reasons, the checks keep their hands off it entirely — it was yours, it stays yours, still running.

[Return to version headlines](#unreleased-headlines)

### Thanks, Don and Justin {#thanks-don-and-justin}

Don (WA2IWC) — on infrastructure — and Justin (AI5OS) — on 8000-series checking — have been pounding on the daily builds and finding things that only show up when real users try real things. A lot of what shipped in this release came from their testing, suggestions, and questions. The earcon tuning especially went through multiple rounds until the sounds were actually audible against radio audio, rather than just theoretically correct in a quiet room. Thanks to all the testers.

[Return to top](#top) · [Jump to versions](#versions)

---

## 4.1.16: The Name Change Edition {#v4-1-16}

I've renamed the app to JJ Flexible Radio Access. The name reflects where we're headed — flexible radio control that puts accessibility first. Your settings, profiles, and everything else are exactly where you left them. No migration needed.

### Headlines (skim here, details below)

- **Neural Noise Reduction and Spectral Subtraction on every Flex radio — fuh free, man.** PC-side NR engines are now wired into the RX audio chain for every Flex, including the 6300 that's never had fancy DSP hardware. No license, no hardware, no catch. It's free. Because it's free. Fuh free.
- **CW prosigns speak alongside your radio.** AS/BT for connect, mode names in Morse, 73 SK for the farewell. A second channel in the language CW operators are already wired for.
- **Braille display status line.** Live radio state — frequency, mode, S-meter, SWR, power — on your refreshable braille display. First in ham software that we know of.
- **SmartLink multi-account + port forwarding controls.** Save multiple SmartLink accounts and switch without re-login, plus a new Network tab for tough-NAT manual port forwarding. Things most SmartLink software doesn't let you do.
- **Running on .NET 10 LTS.** Fresh platform, four years of runway, performance and accessibility-API improvements under the hood.

### You Decide How Much I Talk {#speech-verbosity}

New verbosity system! Press Ctrl+Shift+V to cycle between three levels: Terse (just the essentials), Normal (what you're used to), and Verbose (everything, all the time). Your choice is saved per operator, so it's waiting for you next session. If you've ever wished the app would just shut up and let you operate, Terse mode is your new best friend. If you want every last detail, Verbose has you covered.

### Slices Got Smarter

- Removing a slice no longer confuses the app about which slice you're on. The VFO stays locked to the right slice even after you remove one.
- The slice selector now shows you only the slices you actually own by default — cleaner list when you're solo, no more seeing phantom slices from other MultiFlex stations. (We're working on better visibility into other clients' slices for the next release — when another operator is on the same radio, you should be able to see what they've got going on.) Stay tuned for better usability coming down the pike.
- Slice operations (add, remove, lock TX) are more reliable and give you better feedback about what just happened.

### Status Dialog: Rebuilt from Scratch

Press Ctrl+Alt+S and you'll get a complete status dialog which will give you the skinny on operating conditions, frequency, and other important operating data that your Flex (cause that's the only radio we support now) advertises.  It refreshes live — frequency, mode, signal strength, TX state, all updating in real time, just use your up and down arrows to navigate through the info.  The status data is organized by category so you can jump to what you care about within the accessible list of status. We think this is way better than the old "speak everything at once" approach, though you can still get that any time with Ctrl+Shift+S if you want it.

### Audio Overhaul

- The Audio tab in Settings now combines what used to be separate audio and meter tone controls into one unified tab. Less hunting, more adjusting.
- Under the hood, the audio architecture now properly supports dual, and multi-channel audio for radios that have it.

### About Dialog Upgrade

The About dialog now uses a modern web browser engine instead of the ancient one it was running on. It loads faster, renders properly, and won't randomly fail on systems with strict security policies. We will be using this dialog to deliver update notifications and notification settings as well as including links to our web page and support options all on the internal about page.

### 60-Meter Band Smarts

You know what I hate about 60 meters? The channels, before I got my Flex, I was terrified to get onto 60, mainly because I was terrified that I'd be transmitting out of band. Fear no more Mr. NER, fear no more. Jump to a 60-meter channel and JJ Flexible automatically switches you to USB — because that's what the FCC requires on those channelized frequencies in the U.S. If you land on the digital segment, it'll set CW mode. No more accidentally transmitting in the wrong mode on 60 meters. You'll hear a quick announcement telling you what mode was set and which channel or segment you're on. We will use the same approach for custom band segments that are available in other countries. This will have me and good ol Claude delving through pages and pages of radio regulations from lands near and far, but we're up for it, because we know that JJ Flexible will be a worldwide success once it lands in operator shacks worldwide.

### Audio Workshop Fix

The Audio Workshop dialog's Tab key navigation was broken — you'd get stuck tabbing in circles inside each tab instead of being able to move between controls normally, definitely no bueno.  Tab now moves through controls the way you'd expect a normally behaving application to act.

### DSP Level Minimums

DSP controls like radio based noise reduction level and noise blanker level now enforce sensible minimums for the flex-controlled algorithm you choose. You won't accidentally set NR to zero and wonder why it's not doing anything — the minimum is set to a level where the feature actually works.

### SmartLink Multi-Account Support

Big one here. You can now save multiple SmartLink accounts and switch between them. If you use your buddy's radio via SmartLink and also have your own, you don't have to log in and out anymore. Each account gets its own saved session — switch accounts, hit Remote, and you're on the other radio. No re-entering passwords after the first login. The Switch Account button on the Radio Selector makes it easy, and Set Default in Manage SmartLink Accounts lets you pick which account Remote uses by default. Not even Flex does this stuff, so we're living on the edge here by giving you this capability, but that's when JJ Flexible shines, right at its honed technological edge.

### See Who Else Is On the Radio (MultiFlex Client Management)

When you connect to a radio that has multiple clients on it — MultiFlex, in Flex terminology — you can now see who's there. Radio menu → MultiFlex Clients opens a dialog listing every connected person, their station name, and which slices they own. Your own client is marked "(This client)" so you can tell yourself apart. Primary clients can kick guests from the same dialog if you need your radio back — selecting a client and pressing Disconnect will do the trick. Note: basic MultiFlex across two remote SmartLink clients still has some rough edges we're fixing in the next release — if you see odd slice visibility or missed connect/disconnect announcements, that's known and on the fix list.

### SmartLink Port Forwarding Controls (for Tough Networks)

SmartLink normally handles your port forwarding automatically through UPnP, but sometimes that path just doesn't work — you're behind restrictive NAT, your router doesn't support UPnP properly, or you've turned UPnP off for security reasons. Until now, your only option was wrestling with an inaccessible radio control tool to set it up manually. No longer. The new Network tab in Settings lets you configure port forwarding directly within JJ Flexible: specify the port, point SmartLink at it, done. For the security-minded operator or anyone on a network where UPnP just won't cooperate, you'll herald this as the best thing since sliced bread — a real game-changer, because it's the first step toward the complete radio-control-and-firmware-update pathway we're building right into JJ Flexible. The next version rounds this out with full UPnP support, automatic hole-punch, and help docs that explain the whole setup in plain, accessible English. For now, if you need guidance on which ports to forward, check Flex Radio's SmartLink setup documentation and take note of what they say you need to open.

### Action Toolbar

Press Ctrl+Tab anywhere and you get a quick popup with your most-used TX actions: ATU Tune, Tune Carrier, Transmit, and Speak Status. Arrow to what you want, hit Enter, done. Faster than hunting through menus when you need to hit that tuner right now.

### Manual Tuner Carrier: Ctrl+Shift+T

If you're using an external antenna tuner that needs a live TX carrier to tune against, press **Ctrl+Shift+T** to drop carrier at your configured tune power. Press again to kill it. The app speaks "Tune carrier on" and "Tune carrier off" each time you toggle, so you always know the TX state without looking. No more tabbing over to a Transmit button or digging through menus when you just need to dial in an external tuner. This one's a dedicated hotkey in addition to its Ctrl+Tab palette entry, for the operators who reach for it often.

### Every Button Tells You Its Shortcut

Tabbing through any dialog now tells you the keyboard shortcut for every button. NVDA says "Connect, Alt+N" and JAWS says "Connect, N" — both screen readers get the right info without being redundant. This covers every single dialog in the app, not just the main ones.

### Menu Toggles Say On or Off

DSP toggles in the menu (NR, NB, ANF, meter tones, etc.) now say "Legacy NR: On" or "Legacy NR: Off" instead of relying on the checkmark glyph that some screen readers don't announce. You always know the state.

### Earcon Mute

Ctrl+J then Shift+T mutes all alert sounds (earcons, beeps, confirmation tones) without touching meter tones. Handy when you want the meter tones for tuning but don't need the app dinging at you. Your preference is saved.

### When Dialogs Close, You Know Where You're Operating

Close any of JJ Flexible's dialogs and you'll hear a quick status announcement — "Listening on 14.175, USB, 20 meter band, slice A" — so your screen reader doesn't just say "pane" and leave you wondering, "where's the pain, man?"

### Frequency Entry Sounds (and a Secret or Two)

Type a frequency and you'll hear a click beep on each digit — straightforward confirmation that each keypress registered. Hit Enter to commit and a ding plays to tell you it took. The click is the default, visible right there in Settings → Audio → Frequency Entry Sound, and you can switch it off anytime if you'd rather type in silence. And if you poke around the app in the right way, you might stumble across a couple of alternative sound modes tucked away as little rewards for the curious — but that's all we'll say about those.

### Ctrl+F: A Dedicated Frequency Entry Dialog

New hotkey — Ctrl+F from anywhere opens a focused frequency entry dialog. Type the frequency you want, hit Enter, you're there. Escape cancels without changing anything. The dialog honors all the same typing sounds as the main frequency field, and it gives you cleaner feedback on band edges: if you try to tune somewhere you're not licensed or outside any amateur band, the dialog tells you before it commits. Better than quick-type in the main field when you want a distraction-free "just go here" moment.

### Braille Display Status Line

If you have a braille display, the app now pushes a compact status line to it when you're on the main frequency field — or "home," as we call it, the spot where you'll spend most of your time operating. S-meter, SWR, power, mode — packed into however many cells your display supports. When your screen reader has something to say, the status line yields so you never miss an important message; it pops back into view a moment later once the screen reader's done. Configure your cell count and which fields to show under Settings → Audio tab.

### Your App Speaks CW With You

New this release — if you're a CW operator, the app punctuates major moments with proper Morse prosigns over your computer speaker. Think of it as your radio talking back in the same language it uses on the air.

- **AS** (wait, standing by) plays alongside the "Connecting to [radio]" speech when you start a connection.
- **BT** (break, ready to receive) plays when the radio is up and ready for you.
- **CW for the mode name** plays in parallel with the mode announcement when you change modes — so switching to CW gives you both "CW" via speech and dah-di-dah-dit dah-dah in Morse. Same for the other modes — each gets its Morse letters played alongside the speech.
- **73 SK** plays when you close the app — or **73 de JJF SK** if your speed is 25 WPM or above. A proper ham farewell.

This isn't replacing speech — both channels play together, so you don't lose anything. You just get a second layer of feedback in the language CW operators are wired for. Turn it on in Settings → Audio → CW Notifications. You can adjust CW speed (WPM), sidetone frequency, and which prosigns you want to hear. Your preferences are saved per operator. Please note: purists may notice code jitter ... phrases that might run together, we're working on that for our next big release. The CW sidetone generation moved into its own dedicated engine this release, so the timing is cleaner and the tones are click-free. More polish coming.

### Noise Reduction: More Options, For Every Radio

- **The full on-radio NR trio is now exposed on 8000-series and Aurora radios** with their adjustable level settings wired up: Neural NR (Ctrl+J, R), Spectral NR (Ctrl+J, S), and NR Filter / NRF (Ctrl+J, Shift+N). All three run on the radio's own DSP hardware — zero PC cycles — and are license-gated per your Flex subscription. If your radio or license doesn't include them, the menu entries stay hidden rather than taunting you with things you can't use.
- **and ... the Big one — the big cahuna! And yes, I'm sorry I hid this in a bullet hundreds of words after teasing the feature in our headlines, I'm a mean old amateur radio operator, what can I say? PC-side noise reduction is now live on every Flex radio**, including all 6000 series radios which don't have fancy DSP hardware and beefy processors on the radio. Two freely available, open source DSP noise processing engines are wired into the RX audio chain: a neural RNNoise engine and a spectral subtraction engine. These run on your computer, so they work whether your radio has built-in DSP or not. Toggle them from the DSP menu. No separate hardware, no license required — just more noise reduction available on more radios when you use JJ Flexible. Note: you must have PC audio turned on to enjoy this feature. Also, expect these features to become much more customizable in our next public release.

### More Mode Hotkeys: AM, FM, DIGU, DIGL

Direct mode-change hotkeys were added for most popular modes in this release. You've had Alt+U (USB), Alt+L (LSB), and Alt+C (CW) for a while. Now you also get:

- **Alt+A** = AM
- **Alt+F** = FM
- **Alt+D** = DIGU
- **Alt+Shift+D** = DIGL
- **alt+m** = cycle to the next mode in the Flex Radio mode stack
- **alt+shift+m** = cycle to the previous mode  in your current Flex Radio mode stack.

To make room on the keyboard, a couple of menu access keys and one hotkey had to move:

- The Audio menu now lives on  **Alt+O** (it was Alt+A).
- the Filter menu  now resides on **Alt+E** (it was Alt+F).
- Activate DX Cluster functions is now **Alt+Shift+X** previously, it was Alt+D.

The Slice → Mode submenu shows every hotkey next to its mode, so you can open the menu, arrow through, and learn the bindings without memorizing them. SAM, NFM, and DFM don't get direct hotkeys since though amateur operators use them, the ones that do probably won't mind cycling through the mode stack or using the menu to select their favorite mode. I'm partial to SAM myself, don't judge.

### Your Title Bar Now Tells You the Essentials

Press **Insert+T** anytime and your screen reader will read the title bar, which now carries live radio status — active slice letter, frequency, mode. It offers you a Quick glance option, available without even tabbing anywhere. Cool eh? Thought so! This stealthy title bar updates in real time as the radio changes state, so Insert+T always tells you where you are right now, not where you were a poll cycle age--old news.

### Smarter Band Edge Speech

We included a couple of  band-edge announcement improvements that we think you might learn to love:

- Tuning across a band edge now announces which slice you're on: "Slice A, out of band" instead of just "out of band" — so if you have multiple slices open, you know which one tripped the warning.
- First-tune after connect no longer fires a false-positive out-of-band warning while the radio is still handshaking its initial frequency.
- Band-edge wrap behavior cleaned up so you can tune past an edge and back without spurious extra announcements.

### Customizable Filter Presets

The filter preset dialog can now be edited. Open it, tweak the widths for each mode, save. Your presets are stored per operator and loaded automatically. If you've got a preferred SSB wide that's different from our defaults, or a CW narrow you swear by for contest filtering, dial them in and forget about it, it's that easy friends!

### Customizable Tuning Step Presets

While we're talking presets, we had the same idea for tuning steps, no deja vou here. Normally, you can move through your step-size presets by press C (coarse/fine toggle). PageUp/PageDown in Modern mode are now editable per operator. If you want 5 Hz, 25 Hz, 100 Hz on CW instead of our defaults, make that happen and save it for posterity.

### Panadapter Visibility Toggle

We added a new toggle in Settings → Notifications: "Show panadapter." Hide the visual panadapter if you don't use it and want JJ Flexible's window and tab chain to be more clean. Show it if a sighted helper is looking over your shoulder. Your choice is saved.

### Settings Improvements

- We added volume controls in Settings (master, alert, and meter volume). These settings are now accessible value controls instead of sliders. Press Up/Down to adjust by the configured step (5 by default), Shift+Up or Shift+Down for fine-grain 1-unit nudges, PageUp or PageDown for 10-unit jumps, and Home or End to snap to the minimum or maximum. Your screen reader announces the new value after every change.
- The DSP state now updates immediately when you change modes — no more waiting for the poll cycle to notice that switching from USB to CW changed your NR settings.

### Quick Fixes

- The Ctrl+Alt+S hotkey conflict between Start Scan and Status Dialog is now resolved. Status Dialog keeps the hotkey; Start Scan is menu-only.
- Slice cycling (Up/Down in the VFO field) no longer wraps around — it stops at the first and last slice and tells you when you've reached the bottom of your list of slices.
- The Status Dialog holds your place when it refreshes instead of jumping back to the top. This little change made the dialog usable rather than a dialog where, like an oasis in the desert, you'd never get to the end of the list.
- The Slice Operations field now says "Slice A Operations: Volume 60" instead of the cryptic "Slice Audio 60."
- Modern tuning mode no longer forces position-sensitive navigation in the frequency field.
- **SWR after tune now gets announced** — after either using the internal Flex Radio ATU or an external tuner, when the ATU finishes tuning, or you turn off the manual tuning signal, the app waits for the SWR reading to settle and then speaks it. Previously it would read a stale or idle value. One of our star testers, Don (WA2IWC) asked for this one and he was right — it's a huge help knowing what the actual match came out to.
- **Crash fix: Callouts NRE** — the app could crash when the radio fired a Callouts event in certain edge cases (Don hit this three times). Root cause was an inaccessible shadow field; fixed.
- **Band-edge boundary lag in Ctrl+F** — typing a frequency and hitting Enter could let you commit just past a band edge before the boundary check caught it. Now the check fires before commit.
- **CW prosign cancellation race** — if you triggered two CW prosigns in quick succession (for example, mode-change-then-disconnect), the second could cut off the first. A FIFO queue in the new CW engine handles this cleanly now.

### Under the Hood

- **Running on .NET 10 LTS.** This release migrated from .NET 8 to .NET 10, the latest long-term-support release from Microsoft. Why'd we do it? Simple. You can now expect stability for years to come, better performance, better and more mature native accessibility support, and the groundwork for modern features we'll build on later.
- The entire keyboard command system was rebuilt in C#. You won't notice any difference in how hotkeys work — that's the point. But it means we can add new keyboard features much more quickly and more reliably going forward.
- Access key announcements are now greatly improved — your screen reader now tells you the keyboard shortcut for controls that have one.
- Build system fixes ensure both 64-bit and 32-bit versions compile cleanly.
- Per-account WebView2 browser profiles keep SmartLink sessions isolated — the cookie jar fix that makes multi-account work. If you want details, find me, this author loves talking about cookies and cookie jars.
- Connection state changes now trigger menu rebuilds so you never get stuck on "Disconnect" when the connection failed.
- The ModeChanged event fires immediately so DSP controls update without waiting for the poll timer.
- **CW prosign engine rewrite** with proper envelope shaping for click-free tones and a FIFO queue to prevent cancellation races. Builds the foundation for CW practice mode + on-air keying coming in a future release.
- **The Discovery race condition is now fixed** — FlexLib's local-network radio discovery could throw a null-reference exception on the second Start/Stop cycle. Rare in practice but it would crash the discovery thread when it hit.
- **New build-number versioning** — our installer filenames now include a 4-part version (e.g. `Setup JJFlex_4.1.16.42_x64.exe`). The fourth component auto-increments per commit, so every build you see is uniquely identified. This will make bug reports way easier to triangulate to a specific build and configuration, and it'll make our upcoming automatic crash reporter that much more robust.
- RX antenna list wait bumped from 5s to 20s to handle slow SmartLink handshakes without falsely reporting "no antennas" upon connection to the radio.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.15.1: stop the presses, we got us a breaker breaker emergency {#v4-1-15-1}

The last version forgot how to connect to locally connected radios. In other words, we concentrated on connecting to remote radios so much that we forgot how to access radios you have sitting right next to you. Never fear, it was an easy fix. In short, we kicked it in the pants, and made sure that the application was OK with not displaying SmartLink Logon when it isn't necessary. Sorry about all that.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.15.0: The More than just rearranging deck chairs on the Titanic version {#v4-1-15-0}

This one's big. Twelve sprints of work since the Birthday Release — that means we slammed a ton of features into twelve marathon coding sessions for the fun and enjoyment of amateur radio operators worldwide. This version has not been published as a beta version. Soon friends, soon!

The headline: we rebuilt the entire application from the ground up. The old interface is gone. In its place is a modern, accessible app that was designed from day one so your screen reader can tell you everything that's happening. New features include a complete PTT safety system, band jumping, license-aware tuning, an updated settings dialog, a ScreenFields panel for hands-on radio control that is efficiently organized so that you don't have to tab 72 times to get to what you want or use the menus, filter presets and nudging--something that just isn't done yet by others, a profile reporter, and a complete audio overhaul. We also made the SmartLink connection ... connect reliably and quickly. As always, let me know via k5ner@eml.cc if you have questions, comments, or reports related to this slug of fun.

### The Big One: Complete UI Rebuild

We rebuilt the entire Ke5AL, Jim-created JJFlexRadio interface from scratch. The old technology that powered the app's screens and controls has been replaced with a modern framework that gives us much better control over what your screen reader hears and how you navigate. It means only supporting Windows 10 and greater, but the accessibility changes are real, and the framework that is used continues to be updated by Microsoft--worth a limited JJFlex, cause who's using Windows 7 alone, these days. If you are, let us know--I'll give you some free software counselling.
Here's what all this rigmarole means for you:

- **Menus that actually work with screen readers**: We tried three different menu systems before we found one that doesn't confuse JAWS and NVDA. The menus now behave like proper Windows menus — your screen reader says "Radio menu" not "Radio collapsed expanded collapsed." Arrow through items, hear their names and hotkeys, press Enter to activate. Classic, Modern, and Logging modes each get their own menu set tailored to what you're doing.
- **Everything speaks**: Connection states, mode changes, frequency updates, error messages — nothing happens silently anymore. If JJ Flexible does something, your screen reader knows about it. Error dialogs that used to appear behind other windows now show up front and center, and your screen reader announces them before the dialog even opens. If you use a braille display, you'll get those messages as well. We plan to implement a verbosity system so that JJ Flexible won't spam you if you don't need spamming/screen reader speech.
- **Radio Selector rebuilt**: The dialog where you pick your radio has been completely rebuilt. It's faster, more accessible, and runs on the main thread so your screen reader doesn't lag. The first radio in the list is auto-selected so you can just hit Enter if you only have one rig. We also call it the "radio selector", we know you have a rig but ... the plan is to implement support for other transceivers and receivers. We therefore thought that radio would fit our upcoming plans for JJ Flexible.
- **SmartLink authentication improved**: If you're using JJ Flexible to connect to your radio or a friend's radio remotely, Flex Systems use Smartlink to make it happen. When you log into your account, the system sends you tokens which become keys to your radio and remote connections.  These tokens expire, and that created all kinds of havoc when we changed the way that we securely connect to your radio.  Expired login tokens are detected before they cause errors. The old phantom authentication windows that would pop up randomly are gone, replaced by useful screen reader speech that gives you details on what is happening. The station name timeout was bumped to 45 seconds so slower internet connections don't get cut off mid-handshake--this radio operator lost lots of days of sleep agonizing over that but Claude just kept trying new things and wondering why I was insanely tracking why connecting to a radio either halfway around the world or across the country just doesn't happen. The long and short of it is that it just works now, every time, and if problems crop up later, we've got lots of tools that are silently implemented so that we can track down the ghost in the wires.
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
- **"Am I transmitting?" hotkey**: Hit Alt+Shift+S anytime and your screen reader tells you whether you're transmitting, what mode (hold or locked), and how much time you have left before the safety timeout kicks in. See notes further down detailing the safety features built into our humble JJ Flexible PTT button. On a braille display, the status bar shows "Transmitting" or "TX Locked" so you can glance down anytime.
- **TX health monitor**: If you lock TX and your mic is silent for more than 5 seconds, you'll hear "Check microphone." If your ALC is pegging, you'll hear "Microphone level too high." Think of it like Zoom telling you you're muted — but for ham radio. If you've got zero ALC and JJ Flexible is set, the system will unkey if no signal is detected.
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
- **Wide-open filter presets**: JJ Flexible's filter preset now gives you a "wide open" mode which can give you some pretty incredible sounding receive audio on your end. Wide open equates to 4 kHz on SSB, 2 kHz for CW, 12 kHz for AM, and 16 kHz for FM. Sometimes you just want to hear everything your receiver can hear and that's OK. Have everything then!
- **Filter presets fixed for all modes**: Alt+[ and Alt+] presets now work correctly on LSB and digital lower sideband modes. Before, they were applying wrong filter values that could mute your audio. Sorry about that one.

### Settings & Tools

- **Settings dialog**: Finally! A proper tabbed settings dialog (under Tools → Settings) with tabs for PTT configuration, tuning preferences, license class, and audio. No more hunting through menus to change your timeout or step sizes. Check the other menu settings and let us know if you think we're missing any settings that should belong here.
- **Command Finder**: Press Ctrl+/ and you can search every command in the app — try "band", "transmit", "filter", "power", whatever you're looking for. It shows the hotkey next to each match, and you can press Enter to execute a command right from the search results. It defaults to showing commands for your current mode, with a "Show All" checkbox if you want to see everything.
- **F1 context help**: Press F1 in the frequency field, slice field, or other controls and you'll hear a quick tip about what that field does and how to use it. F1 anywhere else opens Command Finder. We here at JJ Flexible HQ know that we have a penchant for adding lots of hotkeys, now you'll be able to peruse the list of keys you can use to change things on your transceiver wherever you're focussed.
- **Menu checkmarks**: Toggle items like Mute, ATU, VOX, and Squelch now show checkmarks when they're on. Your screen reader will say "checked" or "not checked" so you always know the current state of things. This is a normal, or should be a normal feature we shouldn't be amazed at, but give us a break, we totally changed the architecture of the application so let us be excited for things that you might find mundane. To get this to work right, we had to change a bunch of crap, enjoy mundane.
- **Profile Report**: Ever wonder what's actually different between your "Contest" and "Ragchew" profiles? Now you can find out. Tools → Profile Report loads each of your profiles one at a time, captures every setting, and shows you a side-by-side comparison. It also lists every meter the radio makes available, which is handy for troubleshooting. Don't worry — your original profile is restored when it's done.
- **Connection Tester**: Having trouble connecting to your radio? The radio selector dialog now has a built-in connection tester — pick a radio, hit Test, and it runs diagnostic cycles that measure connection timing, identify failures, and report the actual reason something went wrong. These test results can help us here at JJ Flexible HQ (I'm an HQ of one intrepid ham, don't judge me). This is a great feature which can help us to help you with your SmartLink connection issues or verifying your setup before you get on the air.
- **Export and Import in the Modern menu**: Export Profiles and Import Profiles are now in the Tools menu if you're using the Modern layout. Before, you had to switch to Classic to find them. Please note: you can only import and export radios that exist on your local network. If you're using a friend's remote, or if you sent your radio to a friend's place so it can use their 25 acre antenna farm, you best ask either friend to log in and send you your profile file. Once JJ Flexible HQ has a local network accessible radio, we hope to analyze how profiles are stored so that we can read them properly.

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

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.14.0 — Don (WA2IWC)'s Birthday Release {#v4-1-14-0}

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
- **Audio Peak Filter mode guard**: APF is a CW-only feature, but JJ Flexible used to happily toggle it in SSB mode without doing anything. Now if you try it outside CW, you get a clear message: "Audio Peak Filter is only available in CW mode."

### Fixes

- **QSO grid rows announce callsigns**: Arrow through the Recent QSOs grid and your screen reader now says the callsign (e.g., "W1AW") instead of some cryptic internal type name. Left/right arrow to navigate individual cells within a row still works. NVDA has a quirk where it reads cell values twice — working on that for the next release.
- **Dup count no longer argues with itself**: When you entered a duplicate callsign, two different things were speaking at once — "6 contacts" from one source, "2 duplicates" from another. Turns out they were counting different things (total QSOs vs. matches on the current band and mode), but hearing both was just confusing. Cleaned it up so you hear one clear "Previously worked, N contacts" announcement, plus the warning beep.
- **Log Contact doesn't stutter anymore**: Clicking "Log Contact" used to produce a garbled speech attempt as multiple announcements tripped over each other. You clicked "Log Contact" — you know you're going to Logging Mode. Now it enters quietly and your screen reader just reads the pre-filled call sign field. Much cleaner, much cooler I think.

### Known Quirks

- **"Unknown" on logging mode entry**: Your screen reader might briefly say "unknown" when entering Logging Mode. This is a side effect of mixing two different UI technologies together and will go away in the next major release when I finish converting the app to a single technology.
- **NVDA double-reads in QSO grid**: If you arrow left/right through QSO grid cells in NVDA, it reads cell values twice (e.g., "SSB SSB mode"). JAWS handles it fine, so if you wanna see where we're going soon, run JAWS and you'll hear a spreadsheet-like experience where you'll hear rows and columns read, an informational nirvana, and a sneak peek into future grids we've got planned for ya. Will fix this properly in the next release.
- **App doesn't grab focus on launch**: If you double-click the exe in Explorer, the app starts but focus stays on the Explorer window. Annoying — on my list to fix.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.13.0 — Callbook Fallback, QRZ Logbook, Hotkeys v2 {#v4-1-13-0}

This release packs two full feature sets plus a major hotkey overhaul. The callbook system got a safety net (QRZ goes down? HamQTH picks up automatically), QRZ Logbook uploads your QSOs in real time, and every hotkey in the app now works reliably in every mode. That last part sounds like it should have always been true — turns out the menu system was quietly eating our Alt-key shortcuts. Not anymore.

### Callbook Fallback

- **Callbook auto-fill**: Supports both QRZ.com (XML API, requires subscription) and HamQTH.com (free). Configure which service to use in your operator profile under the new Callbook Lookup section. Credentials are stored per-operator. Auto-fill only touches empty fields — anything you've typed or that came from the previous-contact lookup stays put.
- **QRZ to HamQTH auto-fallback**: If QRZ login fails three times in a row, JJ Flexible silently switches to the built-in HamQTH account so lookups keep working. You get a one-time notification explaining the fallback. No more silent lookup failures when your QRZ subscription lapses.
- **HamQTH built-in account**: If you select HamQTH as your callbook but don't have personal credentials, JJ Flexible falls back to its built-in HamQTH account automatically.
- **Credential migration**: If you had HamQTH credentials from the old system, they automatically migrate to the new callbook settings on first load.
- **Credential validation on save**: Your operator profile tests your credentials when you click Update. Clear error messages for QRZ subscription issues and HamQTH login failures are all yours if you need them, and you know you need them, right?
- **Secure credential storage**: Callbook passwords are now encrypted using Windows DPAPI — a nifty Microsoft feature that ties encryption to your Windows login, useless if someone copies the file, but if you're logged in, we autodecrypt the file.

### QRZ Logbook Upload

- **Real-time QRZ Logbook**: Log a QSO in Logging Mode and it automatically uploads to your QRZ.com logbook. Enable in operator settings with your QRZ API key. To find your key, login to Qrz.com, click your callsign, this may require that you use your virtual mouse to double click it, then select your logbook. Select settings. Find the first table on the page. That table contains the API for your logbook. If you have multiple logbooks, each of them has their own API, make sure you use the API for the logbook you want JJ Flexible to have access to. Enter the listed API into JJ Flexible and click validate.
- **Validate button**: Test your QRZ Logbook API key right from settings — shows your QRZ log stats (total QSOs, etc.) to confirm everything's connected.
- **Circuit breaker**: If QRZ's server has problems, uploads pause after 5 consecutive errors and resume automatically later. Your local log is always saved regardless.
- **Graceful degradation**: Invalid API key? QRZ down? Your QSO still saves locally with no errors. QRZ issues are logged silently — you'll see them in the trace if you look, but they won't interrupt your operating.

### Hotkeys v2

- **Scope-aware hotkeys**: Every hotkey now belongs to a scope — Global (works everywhere), Radio (Classic + Modern only), or Logging (Logging Mode only). The same physical key can do different things depending on your mode: Alt+C is CW Zero Beat in Radio mode but jumps to the Call Sign field in Logging mode.
- **Keyboard routing rewrite**: ALL hotkeys now go through the scope-aware registry BEFORE the menu system sees them. This fixed F6 not switching panes in Logging Mode, Alt+C/Alt+S opening menus instead of executing commands, F1 not working in Logging, and Ctrl+/ (Command Finder) being intermittent.
- **Command Finder (Ctrl+/)**: This cool little search utility can be the unsung hero while you're operating in a contest and want to know what key to press when, when you want to do what in JJ Flexible. Type a few characters and it searches all available commands by name, keywords, and synonyms. Shows the current hotkey binding next to each match. Select one and press Enter to execute it immediately. The result list updates as you type and announces the count to your screen reader. Only shows commands relevant to your current mode.
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

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.12.0 — Logging Mode {#v4-1-12-0}

This is the one I've been building toward. JJ Flexible now has a dedicated Logging Mode — press Ctrl+Shift+L from anywhere in the app and you're in a clean, focused QSO entry screen. No menus full of radio controls you don't need mid-QSO, no hunting for the right form. Just you, your log, and the radio.

- **Quick-entry panel**: Call, RST Sent/Rcvd, Name, QTH, State, Grid, Comments — all in a tight Tab-order layout. Freq, mode, band, and UTC time auto-fill from the radio when you start a QSO. Press Enter to log, and the fields reset for the next one. It's the workflow I always wanted.
- **Radio pane**: A slim status strip on the left showing frequency, mode, band, and tune step. F6 toggles focus over to it, and your screen reader announces "Radio pane" so you know where you are. Arrow keys tune the radio (Up/Down by step, Shift for coarse, Left/Right to change step size). Ctrl+F pops the manual frequency dialog. You don't have to leave Logging Mode just to nudge the VFO. Tab stays in the log entry fields — you can't accidentally wander into the radio pane by tabbing.
- **Recent QSOs grid**: The bottom half of the screen shows your recent QSOs — Time, Call, Mode, Freq, RST Sent, RST Rcvd, Name. JAWS and NVDA navigate it natively with arrow keys (row/column announcements, the whole deal). It auto-updates when you log a new contact.
- **Previous contact lookup**: Tab out of the Call field and JJ Flexible instantly checks your entire log. If you've worked them before, you'll hear something like "W1AW — 3 previous contacts, last on 2026-01-15, 20m CW" from your screen reader. Name and QTH auto-fill from the previous contact too. You can Tab to the info field to re-read it if you missed the announcement.
- **Dup checking**: If you've already worked a station (matching your dup type — call only, call+band, etc.), you get a beep and a screen reader warning when you Tab out of the call sign field. It's warn-only, not blocking — you can still save the contact if you want. The dup dictionary loads from the log file at startup, so even after a restart it remembers who you've worked.
- **Field hotkeys**: Alt+C (Call), Alt+T (RST Sent), Alt+R (RST Received), Alt+N (Name), Alt+Q (QTH), Alt+S (State), Alt+G (Grid), Alt+E (Comments). Ctrl+N clears the form, Ctrl+W saves. All mnemonics make sense — T for senT, R for Received.
- **F6 pane switching**: Standard Windows F6/Shift+F6 toggles focus between the Radio pane and the Log entry pane. Feels natural.
- **Mode round-trip**: Ctrl+Shift+L drops you into Logging Mode, and pressing it again takes you right back to Classic or Modern — whichever you were using. Your field values survive the round-trip too.
- **Close protection**: Try to close the app with an unsaved entry and you'll get a save/discard/cancel dialog. If fields are missing, the dialog tells you what's needed before you click Yes.
- **Escape to clear**: Press Escape to clear the form. First time it asks for confirmation with a "Don't ask me again" checkbox for pileup mode.
- **Log Characteristics in Logging Mode**: Ctrl+Shift+N opens Log Characteristics without file conflicts. Previously this would crash because the log file was locked. Log characteristics allow you to edit characteristics of your log and create a new log file if you need to do that.
- **SKCC WES form retired**: The old contest-specific SKCC WES log form is removed. Logging Mode replaces it with a general-purpose approach. We're looking into a contest creator/configurator that you might just see somewhere down the road.
- **Screen reader audit**: Every control has proper labels and roles. Mode transitions, tune step changes, previous contact lookups — all announced. Nothing happens silently.

If you've been opening JJ Flexible just to log a few CW QSOs and wished the UI would get out of the way, this is that update.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.11.0 — Classic/Modern Mode, Auto-Connect & Audio Fix {#v4-1-11-0}

This one's about giving you choices. JJ Flexible now has two UI modes — Classic for the "if it ain't broke" crowd, and Modern for those of us who want a cleaner, more accessible interface that's built for screen readers from the ground up. Plus, auto-connect means the app just connects to your radio when it starts, and we finally fixed the "why is this so quiet?" WAN audio problem.

### Classic vs. Modern Mode

This is a big one for how JJ Flexible feels day to day. You now have two ways to use the app:

- **Classic mode**: Everything you're used to. Same menus, same layout, same muscle memory. If you've been using JJ Flexible for years and you like it the way it is, Classic is your friend. Nothing changes, nothing moves, nothing breaks. Don loves Classic mode, and honestly, we respect that.
- **Modern mode**: A brand new menu structure designed from scratch with screen reader accessibility as priority number one. Menus are organized by what you're actually doing — Radio, Slice, Filter, Audio, Tools — instead of the legacy layout. Every item has proper screen reader labels, checked/unchecked states, and clear announcements. It's where all the new features land first. We're not saying that Classic mode misses out on all new features and enhancements, but you're more likely to see Modern mode get the quality of life features that you'll wonder to yourself, why didn't we do it like this from day one?
- **Ctrl+Shift+M** toggles between Classic and Modern instantly. No restart needed, no settings to dig through. Try Modern, don't like it? One keystroke back to Classic. Your preference is saved per operator, so if Don wants Classic and you want Modern on the same install, everybody's happy. Want to tune JJ style for a while? Be our guest. You can also switch directly to classic mode by pressing ctrl+shift+c.
- **New installs default to Modern**, but you'll get a one-time prompt asking which you prefer. Existing users stay on Classic until they decide to switch.

Think of it like this: Classic is the cozy old shack with the tubes glowing in the corner. Modern is the new shack with the flat screens and the ergonomic chair. Same radio, same bands, same fun — just a different way to get to the controls. And you can walk between the two shacks anytime you want.

### Auto-Connect & Audio

- **Seamless auto-connect**: Pick your radio, right-click, "Set as Auto-Connect," and you're done. Next time you launch JJ Flexible, it connects automatically. Works for both local radios and SmartLink remotes.
- **Friendly offline handling**: If your auto-connect radio isn't available (maybe Don finally turned his off), you get a proper dialog with choices: try again, pick a different radio, or disable auto-connect. No cryptic errors, no stuck screens.
- **Single radio rule**: Only one radio can be the auto-connect target. Set it on Radio B? It clears on Radio A. No ambiguity about which radio will connect.
- **Settings confirmation**: Before saving auto-connect, you see exactly what's being saved — radio name, low bandwidth preference, the works. No "wait, what did I just enable?" moments.
- **Fixed WAN audio volume**: If your laptop speakers sounded anemic through SmartLink, you're not imagining things. The remote audio path was outputting at about 16% of full scale. We added gain staging that boosts it to proper levels. Default is 4x (comfortable listening), adjustable in a future update.
- **Help page works again**: The .NET 8 migration broke the Help menu. Fixed with one line of smart code wizardry, thank you Claude.
- **Fresh native DLLs**: Rebuilt Opus and PortAudio from source with proper optimizations for both 64-bit and 32-bit architectures in mind.
- **Screen reader everywhere**: Connection states are all announced — connecting, connected, offline, disconnected. Nothing happens silently.

If you've been frustrated by clicking through the radio selector every single launch, or by turning your laptop volume to 100% just to hear the radio, this is your update.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.10.0 — SmartLink Saved Accounts {#v4-1-10-0}

Finally! This one's been on my list forever. You can now save your SmartLink login and stop typing credentials every ... single ... time you want to connect to a remote radio. I know, I know — it should have been there from day one.

- **Saved SmartLink accounts**: After logging in, JJ Flexible asks if you want to save the account. Give it a friendly name like "Main ISS Flex Radio" (I can dream, right?), or "Club Station" and next time you click Remote, just pick it from the list. No more hunting for passwords or waiting for two-factor codes while your DX window closes.
- **Secure storage**: Your login tokens, little pieces of data that tell Flex Systems that you are in fact you, are encrypted using Windows DPAPI — tied to your Windows login. If someone copies the file to another machine, it's useless to them. No plaintext passwords, ever, not anymore that is.
- **Automatic refresh**: When your session expires (they do, eventually), JJ Flexible quietly tries to refresh it. If that works, you won't even notice. If it fails, you'll need to log in again, but your saved accounts stick around.
- **Account housekeeping**: You can rename or delete saved accounts from the selector. Made a typo in the name? Fixed in two clicks.
- **Improved auth security**: I upgraded the auth flow to a more modern method that's more secure and actually allows the "remember me" feature to work properly. The old way literally couldn't do refresh tokens. Who knew?

When you click Remote and have saved accounts, you'll see the account picker. Want to log in fresh? Just hit "New Login."

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.9.1 — WebView2 & Screen Reader Fixes {#v4-1-9-1}

This patch squashes some bugs that made SmartLink login a real pain, especially if you use a screen reader.

- **Login window no longer freezes everything**: The login window was locking up for several seconds while the browser initialized in the background. Now it loads asynchronously — you'll see "Authenticating with SmartLink..." while it warms up, and your screen reader keeps working. Much better.
- **The focus bug is dead**: NVDA users were getting stuck in limbo because we were yanking focus around too aggressively. The login page now waits until it's actually ready before announcing "Login page ready." Patience is a virtue, even for code.
- **Better screen reader support**: Swapped out the screen reader library for better NVDA, JAWS, and SuperNova support. Same announcements, better compatibility.
- **No more "access denied"**: Moved the browser cache folder to your AppData so it stops complaining when JJ Flexible runs from Program Files.

If SmartLink was hanging or your screen reader went mysteriously silent during login, give this version a try.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.9.0 — The .NET 8 Migration Release {#v4-1-9-0}

This is a big one. I finally ripped the band-aid off and migrated the entire codebase from the old .NET Framework to the modern .NET 8. Here's what changed:

- **64-bit and 32-bit support**: JJ Flexible now builds for both x64 (64-bit) and x86 (32-bit). The installer names include the architecture suffix so you know which one you're grabbing.
- **Modern auth for SmartLink**: Replaced the ancient Internet Explorer-based login with Microsoft Edge for SmartLink authentication. Modern security, better compatibility, no more IE quirks.
- **TLS 1.3 support**: Now negotiates TLS 1.3 where available, with TLS 1.2 fallback. Your connections are as secure as they can be.
- **Smart native DLL loading**: Automatically loads the correct 64-bit or 32-bit audio libraries at startup. No more manual file shuffling.
- **Housekeeping**: Removed legacy radio support (Icom, Kenwood, Generic) since this is JJ*Flex*Radio after all. Also added FLEX-8400 and Aurora AU-510 to the rig table.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.8.0 — Feature Availability & Accessibility {#v4-1-8-0}

- **Feature Availability tab**: The Radio Info dialog now has a second tab that shows exactly which features your radio and license support — Diversity, ESC, CW Autotune, NR/ANF variants. If Flex releases a new feature and you aren't a subscriber, you'll see which features are available and which aren't. No guessing.
- **Quick access from Actions menu**: One click opens Radio Info straight to the Feature Availability tab.
- **Menu accessibility cleanup**: Menus no longer have & symbols sprinkled throughout, which were confusing screen readers. Also added support to tell you if a menu item is checked, unchecked, or unavailable. You'd think that would be easy and straightforward, but no.
- **NR/ANF algorithm breakdown**: Now lists individual algorithms (Basic NR/ANF, RNN, NRF, NRS, NRL, ANFT, ANFL) and tells you which ones your radio supports. RNN is 8000 series only — now you'll know why it's not showing up on your 6300.
- **Single-SCU radio awareness**: If your radio has one SCU, you won't see Diversity Reception or ESC controls cluttering up the interface.
- **Audio device setup**: Actions menu now has "Audio Device Setup..." for changing your sound device. Also fixes errors that occurred when no audio device was selected.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.7.0 {#v4-1-7-0}

I did a cleanup pass here but never shipped it. Think of this as a scratchpad release that I used to squash bugs and keep momentum.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.6.0 — Error Reporting {#v4-1-6-0}

- Wired up crash reporting so you no longer need a debug build to send useful crash info. A crash now generates a dump and stack trace that I can actually use to fix things.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.5.0 — Subscription Awareness {#v4-1-5-0}

- Hidden controls your radio isn't licensed for so we don't tease features you can't use.
- Tightened up the codebase by removing more legacy leftovers to make things less confusing to maintain.
- Subscribed features now show up both in menus and on the main Flex filters page, so the UI reflects what you actually own.
- Added a Noise Control submenu under Actions (with an eye toward adding shortcuts later).
- Diversity/ESC now disappear when the radio can't support them, with a clear "not supported" message so it's obvious why.
- CW Autotune landed in Actions for CW mode — it finds the strongest CW signal using your configured sidetone.
- Added "Daily log trace" because it's nerdy and useful: it auto-creates daily traces and archives the previous day.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.0.0 {#v4-1-0-0}

- Pulled in FlexLib 4.1.3 so we stay current with upstream bug fixes and API changes.
- Continued wiring up noise/mitigation features.
- Added an ESC dialog (Enhanced Signal Clarity) for radios with enough SCUs.
- Started building subscription-aware UI to align with SmartSDR+ feature gating.

[Return to top](#top) · [Jump to versions](#versions)

## 4.0.5.0 {#v4-0-5-0}

- Added the advanced NR/ANF controls (RNN, NRF/NRS/NRL, ANFT/ANFL) and made their availability license-aware with clear tooltips.
- Added a helper to wrap all the "can we do diversity?" checks in one place (license, antennas, slices, etc.).
- Replaced DotNetZip with built-in compression to close a known security issue.
- Expanded the radio registry so each Flex model uses capability checks instead of hardcoded behavior.

[Return to top](#top) · [Jump to versions](#versions)

## 4.0.4.0 {#v4-0-4-0}
- Continued migration to FlexLib 4.0 APIs.
- Auth/SmartLink page improvements.

[Return to top](#top) · [Jump to versions](#versions)

## 4.0.3.0 {#v4-0-3-0}
- Initial FlexLib 4.0 adoption across core radio paths.
- Stability fixes in Filters and Pan controls.

[Return to top](#top) · [Jump to versions](#versions)

## 4.0.2.0 {#v4-0-2-0}
- SmartLink connection reliability improvements.
- Solution cleanup.

[Return to top](#top) · [Jump to versions](#versions)

## 4.0.1.0 {#v4-0-1-0}
- Start of the 4.x line, compatibility with SmartSDR 4.0.

[Return to top](#top) · [Jump to versions](#versions)
- Initial docs for missing features.

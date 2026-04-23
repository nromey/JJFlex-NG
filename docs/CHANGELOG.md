# JJ Flexible Radio Access Changelog {#top}
Authored by Noel Romey with the assistance of ... my man ... Clauidd. Thanks buddy, you're a machine but we work well together, keep it up as you destroy the planet.

All notable changes to this project are  documented in this file. The opinions and cool factor  of developers, radio amateurs,  operatives,  and pets  should be taken with a huge grain of salt, a slab really. Other assistance for portions of JJ Flexible Radio courtesy of Peanut Butter, my Abysinian buddy. This change log is long, but you might like it, because this is definitely not you're Grandpa's typoical change log. Read at your own risk.  

## Jump to a Version {#versions}

- [4.1.17 — The Make Yourself at Home Edition](#v4-1-17)
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

## 4.1.17: The Make Yourself at Home Edition {#v4-1-17}

Moving sucks. Full stop. But isn't it awesome when you realize you've stopped using plastic silverware and paper plates, you've mastered your stove and oven, your pictures are up, your favorite chair is in a favorite place, and — well, this new scary house you just moved into, the one you probably own and still get lost in, starts feeling like home. That's what this release is all about. So make yourself at home and enjoy this slug of mighty fine updates that'll have you thinking "this feels right, it feels like home." Cheesy, yes, but it's helped to define JJ Flexible Radio's newly named "Home."

This release is about the space where you actually spend your time in the app — formerly announced as "the VFO frequency-and-slice area," which was a mouthful I know. It's got a real name now and it speaks for itself when you land on it. It also does more work per keystroke than it ever had a right to. We also fixed some flaky wiring and janky plumbing around connecting your Flex to the network and allowing inbound connections to your radio. This work has been cooking since the last release, and if you've ever had problems establishing a remote connection to your radio from outside your network, these changes will affect you. We also added a safety check that makes sure you don't accidentally change settings on somebody else's radio when you're not the owner. In short, if you're not at your radio, you won't be able to make serious network-related changes that affect the Flex and SmartLink. These and more as you follow this loopy coax down the line.

### Version 4.1.17 Headlines {#v4-1-17-headlines}

- [The main screen is now called Home](#home-intro), and it tells you so. From anywhere in the application, press F2 to "go home." Your screen reader will say "Home" plus whichever field you're on — slice, frequency, S-meter, wherever. You may hear more or less speech depending on your speech verbosity setting. No more navigating a "Frequency Display" region whose name never quite told you where you were.
- [Squelch lives in Home now](#squelch-in-home). Arrow right past the S-meter and you'll find it. Press Q from anywhere in Home to toggle it — same muscle memory as M for mute, R for RIT, X for XIT.
- [Escape](#escape-does-its-job) now closes screen field categories. Press Escape once inside an open field group to close it. Press Escape twice quickly (about half a second between presses) to collapse all open field categories at once. Once the categories are collapsed, you'll be back at Home — because there's no place like home ... there's no place like home ... what? Sorry, fell asleep there for a second, no idea why. This suggestion came from Don, who got really tired of tabbing through large fields just to close them. You've done it right if you hear two tones — can't miss 'em.
- **The [key map across Home](#universal-keys-from-home) makes sense now.** R toggles RIT from anywhere you press it. X toggles XIT. The `=` key does the old transceive thing (which freed X to do what most hams expect). Pan moves to Page Up / Home / Page Down instead of letter keys, so if your finger slips you don't pan to Patagonia.
- **[Networking settings](#network-tab-configurability) you can actually configure on your own.** Manual port forwarding, UPnP, and hole-punch all live in the Network tab now. Bye-bye, SmartSDR, for that task at least. We also included a diagnostic probe with a copy-and-pasteable report you can send me when something breaks.
- **[Safety check on port forwarding](#port-forwarding-safety-check).** If you're connected to somebody else's radio via SmartLink and try to change the port settings, JJ Flexible politely stops you instead of overwriting their radio's config.
- [CW send and receive boxes](#cw-text-boxes) go away if you can't use them. Unless you're in CW mode, the receive and send text boxes aren't visible and they're not in the tab order. Switch to CW with Alt+C and they come right back.
- **Ctrl+Tab works for [panel navigation](#panel-navigation) again.** Sorry about that. Ctrl+Tab moves you through the major category fields, and Ctrl+Shift+Tab reverses direction. The popup menu that used to live on Ctrl+Tab is disabled pending a proper redesign — it's going to come back as an actual accessible toolbar that also looks spiffy. Stay tuned on that one.

### Home's got a real name now {#home-intro}

In the past, the place you landed on when you pressed F2 in JJ Flexible Radio was called "Frequency Display" or "Frequency and VFO Display" in accessibility announcements. Neither name really told you what you were looking at, and neither was easy to say. This familiar place is now called "JJ Flexible Radio Home." If you hear "home" while you're using the application, you're in the place your hand goes when you want to operate the rig. Home on a knobs-and-buttons radio is where your main control cluster lives — the dials and switches you touch every time you sit down at the shack. JJ Flexible's Home is the same idea, expressed as a row of accessible fields.

When focus lands there, you hear "Home" plus whichever sub-field you landed on. The exact wording scales with your [speech verbosity setting](#speech-verbosity). On Terse, it's "Home, slice" (or whichever field). On Chatty, it's "JJ Flexible Home, slice, 14.225.000" — the full story including the current frequency for context.

[Return to version headlines](#v4-1-17-headlines)

### Universal Keys from Home {#universal-keys-from-home}

Single-letter keys that toggle radio features on and off now operate the same way throughout your JJ Flexible Home. You don't have to navigate to a specific field to perform a specific function. In the old days, if you pressed M while focus was on the frequency tuning group, nothing would happen. Now, you can mute the active slice from anywhere in Home. We hope these changes remove some confusion from your life and add some efficiency to your operating workflow. Affected keys are as follows:

- **M** — toggle mute on the active slice
- **V** — cycle to the next slice
- **R** — toggle RIT
- **X** — toggle XIT
- **Q** — toggle squelch
- **=** — make the current slice transceive (both RX and TX on this slice)

[Return to version headlines](#v4-1-17-headlines)

### Escape Does Its Job {#escape-does-its-job}

Pressing Escape inside an open field group (DSP, Audio, Receiver, Transmission, Antenna) now actually closes that group and puts focus on its header. You can re-open it with Space. This was supposed to be the behavior all along; it finally works the way it reads.

Press Escape twice quickly and everything collapses at once — all open groups close, focus returns to Home, and you hear a distinctive two-tone descending sound confirming "you backed out of everything." If you've ever had a Windows Explorer moment where you wanted to just get away from the thing you were in, this is that key, applied to JJ Flexible's structure.

[Return to version headlines](#v4-1-17-headlines)

### Three New Sound Cues {#three-new-sound-cues}

JJ Flexible now has three distinct sound cues that fire when opening and closing field groups:

- **Expand** — when a group opens, you'll hear an ascending chirp with some sandy texture mixed in. The rising pitch means you've successfully opened a major field category. As before, you can tab into it and adjust settings. If you press a hotkey to open a category directly, you'll hear this same sound. We designed the chirp to be heard over actual radio noise, and we'll continue to tune the sounds and how they're played as more people use the software.
- **Collapse** — this tone sounds the same as the expand sound, but it drops in pitch instead of rising. The falling pitch means you've closed the category.
- **Collapse all open fields (the "gavel")** — when you double-tap the Escape key, all open category fields close and you return to Home. Listen for two distinct tones descending in pitch, confirming that everything closed. These tones are meant to feel like finality, like the thing you pressed actually did something significant. If you're having trouble activating this feature, read about [how quick is quick](#quick).

The noise texture on the chirps is designed to cut through radio static better than a pure tone would. Your ear picks out the distinctive "shhhwee" shape even when 40 or 80 meters is crashing with thunderstorm QRN.

[Return to version headlines](#v4-1-17-headlines)

### You get to decide how quick quick really is {#quick}

There's a new setting in **Settings > Accessibility** called Double-Tap Tolerance. It has four choices — Quick (250 ms), Normal (500 ms), Relaxed (750 ms), and Leisurely (1000 ms) — and it controls how sensitive JJ Flexible is when detecting a double tap. If Quick is selected, you have 250 milliseconds between presses to register a double tap. Select Leisurely and you have a full second. A longer tolerance may help folks with dexterity or fine motor control challenges to successfully double-tap when that was impossible before. On the other hand, if you're quick on the draw, by all means set it fast. The power is in your hands now.

This setting affects two behaviors in JJ Flexible Radio today. First, double-tapping your left or right bracket enters filter-edge adjust mode. Second, double-tapping [Escape](#escape-does-its-job) closes all open category fields in the field list. Any future double-tap features will respect the same setting, so set it once and forget about it — we've got you.

[Return to version headlines](#v4-1-17-headlines)

### Squelch in Home {#squelch-in-home}

We've tried to keep JJ Flexible Radio's behavior mirroring the older JJ Flex patterns Jim Shaffer set up. Squelch used to live only in the Receiver field group within Screen Fields — you had to expand it to toggle squelch or adjust the squelch level. Now both the squelch toggle and its level value live in Home as two adjacent fields, right after the S-meter. To reach squelch from Home, right-arrow until you hear the S-meter — squelch will be the next option. Use Spacebar or the letter Q to toggle squelch, and press up or down arrow to increase or decrease the level. When squelch is off, the squelch level field disappears from Home to keep navigation tight.

Remember, you don't need to be on the field that says "squelch" in Home to toggle it. Press Q from any Home field to toggle squelch without arrowing — it's the same universal pattern as the other single-letter toggles. JJ Flexible stores your last squelch level, so when you toggle squelch back on, it returns to whatever you set previously.

[Return to version headlines](#v4-1-17-headlines)

### Network Tab Configurability {#network-tab-configurability}

JJ Flexible now gives you full control over the three SmartLink networking tiers needed to conquer difficult network topologies. If you're behind network address translation — most of us are — SmartLink can bust through. That's awesome, but until now screen reader users had to wrestle with SmartSDR's inaccessible GUI to make the radio-side changes SmartLink needs. The Network tab in Settings now does the following:

- **Manual port forwarding** is the sovereign option — you set the port, you know what's happening. Use this if you want to configure your router to open specific ports yourself. The port-forward setting you change from JJ Flexible tells your radio which external port to advertise for UDP and TCP connections. You also have to use your router's admin pages to forward the matching ports. You **can** pick any external port you like — that lets you put multiple radios on the same network and still use SmartLink. Set up two port-forward rules on your router: rule 1 forwards your chosen external port over TCP to port 4994 on the radio. Rule 2 forwards your chosen external port over UDP to port 4993 on the radio. Sound complicated? It can be. If you're not comfortable with this process, you now have two more options, and JJ Flexible will try them both.
- **UPnP** is the convenience option. Turn UPnP on and JJ Flexible asks your router to set up port mapping automatically. Some operators are uncomfortable with UPnP because it can be an issue for security-conscious network setups — your choice, your network.
- **Hole-punch / extended reach** is the last-resort option for hard-to-reach or hard-to-configure networks. If UPnP and manual port forwards both fail, JJ Flexible will try to bust a hole (legally, I promise) through your NAT so that SmartLink can reach your radio.
- **Network diagnostic probe** is an option that asks Flex's SmartLink servers to test your network and its ability to reach your radio from the great beyond. This is a very useful tool because it can give you a concrete sense of how to configure SmartLink properly. The probe tells you in plain English what's working and what isn't. If the two automatic options fail, you now have the information you need to set up manual port forwarding so the Flex Systems servers can reach your configured external port.
- **Copy and save the network diagnostic report.** If something's broken, you can copy the data directly to an email, or save the report to a file and attach it to a support request to me. The report includes your radio firmware, the network test results, and other radio settings and statistics that may help me diagnose your issue — or encourage you to contact Flex directly.

JJ Flexible tries port forwarding first (if it's set on the radio), so SmartLink can operate through secure networks and networks with restrictive policies. Select UPnP or hole-punch if the tests show those are viable options for you. Either way, this is a huge step forward for screen reader users who want to independently configure their radio and its network settings.

[Return to version headlines](#v4-1-17-headlines)

### The Port Forwarding Safety Check {#port-forwarding-safety-check}

When you press Apply in **Settings > Network**, JJ Flexible now checks whether you're the primary operator of the radio before letting you make the change. If you're connected locally to your own radio, the radio considers you the primary because JJ Flexible can detect that a microphone is connected and that you're authorized to make the change. During the change process, you'll see a confirmation dialog asking "are you sure?" The default answer is No, so that you don't accidentally store a wrong setting. Select Yes and the setting is applied. If you're connected remotely via SmartLink to someone else's radio, JJ Flexible politely refuses: "Cannot change SmartLink port settings. You must be the primary operator of the radio."

This behavior catches two different kinds of accidental changes that could otherwise occur. The first is changing a setting when you're not the radio's owner. The second is the inevitable fat-finger moment where you didn't mean to save to firmware at all — the confirmation dialog catches that too. In other words, if I'm connected to my dog Hawke's Flex Radio via SmartLink, but I forget that I'm on his radio, JJ Flexible won't let me apply a network change to his firmware. This two-layered approach is necessary, especially for a setting that persists on the radio and affects every future connection. If I could set someone else's port forwarding without knowing how their router is configured, I'd inadvertently break their ability to accept remote connections — whether for a friend or for my dog.

There is one more tie that binds us to Flex Systems' SmartSDR. For now, you must connect your Flex using SmartSDR to upgrade radio firmware. JJ Flexible will soon support direct firmware upload, and we'll use an even stricter version of this same check to ensure that you're physically at your radio before uploading firmware. SmartSDR requires a quick press of PTT or your code key to confirm you're at the radio, and that's likely how we'll support the feature too. Then ... freedom! Accessibility freedom!!

[Return to version headlines](#v4-1-17-headlines)

### The CW Text Boxes Know When They're Wanted {#cw-text-boxes}

If your radio's mode is set to any CW variant (CW, CWL, or CWU), JJ Flexible shows you a received text box — which, in a future JJ Flexible version, will display decoded CW — and a send box that lets you send CW by typing. Make sure VOX or full break-in is selected if you want to send CW remotely. Right now, that's the only way to send CW remotely with your Flex. Switching your Flex to voice modes like USB or LSB, or any digital mode, hides both of these boxes. They're not just hidden visually — they're also removed from the tab order. In short, they're ... gone, like really gone.

If you switch back to CW, those boxes return. This may seem like a small quality-of-life fix, but if you've been wondering why those boxes lived in your tab order during a phone QSO, the answer is: they didn't have to. Claude and I are absent-minded. I can speak for myself, and I simply forgot to disable them for modes that don't use them.

[Return to version headlines](#v4-1-17-headlines)

### More Stable Remote Sessions {#remote-stability}

We worked on network connectivity and stability. Hopefully, you'll never know we did, but the work was necessary to make sure you stay connected to your local or remote radio. If you've had SmartLink sessions fall over mid-QSO before, you should see fewer of those dropouts, and the system should stop telling you "connection is slow" as often. We'll keep tuning network behavior, so if you have issues, let us know.

[Return to version headlines](#v4-1-17-headlines)

### Ctrl+Tab Reclaimed for Panel Navigation {#panel-navigation}

Ctrl+Tab used to pop up an "action toolbar" menu. It's disabled now, and Ctrl+Tab / Ctrl+Shift+Tab are back to doing what they do best in a tabbed interface: moving between open field groups. The action toolbar is coming back in a future release as an actual toolbar, not a menu — a persistent accessible UI surface you can navigate, not a popup that interrupted your tab nav every time you reached for it. I'm hoping the toolbar looks good too.

[Return to version headlines](#v4-1-17-headlines)

### Under the kitchen sink: stuff that might interest you but probably not {#under-the-kitchen-sink}

- The new sound cues are tuned to cut through real radio noise. Background audio processing favors the earcon frequencies during a chirp, so you can still hear the cue when the band is crashing.
- A shared safety check now protects destructive operations. Today's port-forward apply uses it, and future features like firmware upload will share the same guard. One place to tighten if we ever need to, not twelve.
- Your per-operator accessibility preferences now persist across app restarts, so whatever you set on one session is waiting for you on the next.
- Home and the Screen Fields panel are cleaner siblings now. Home is where you operate minute-to-minute; the panel is where you reach for deeper settings. Less stepping on each other's toes.

[Return to version headlines](#v4-1-17-headlines)

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

You know what I hate about 60 meters? The channels, before I got my Flex, I was terrified to get onto 60, mainly because I was terrified that I'd be transmitting out of band. Fear no more Mr. NER, fear no more. Jump to a 60-meter channel and JJ Flexible Radio automatically switches you to USB — because that's what the FCC requires on those channelized frequencies in the U.S. If you land on the digital segment, it'll set CW mode. No more accidentally transmitting in the wrong mode on 60 meters. You'll hear a quick announcement telling you what mode was set and which channel or segment you're on. We will use the same approach for custom band segments that are available in other countries. This will have me and good ol Claude delving through pages and pages of radio regulations from lands near and far, but we're up for it, because we know that JJ Flexible will be a worldwide success once it lands in operator shacks worldwide.

### Audio Workshop Fix

The Audio Workshop dialog's Tab key navigation was broken — you'd get stuck tabbing in circles inside each tab instead of being able to move between controls normally, definitely no bueno.  Tab now moves through controls the way you'd expect a normally behaving application to act.

### DSP Level Minimums

DSP controls like radio based noise reduction level and noise blanker level now enforce sensible minimums for the flex-controlled algorithm you choose. You won't accidentally set NR to zero and wonder why it's not doing anything — the minimum is set to a level where the feature actually works.

### SmartLink Multi-Account Support

Big one here. You can now save multiple SmartLink accounts and switch between them. If you use your buddy's radio via SmartLink and also have your own, you don't have to log in and out anymore. Each account gets its own saved session — switch accounts, hit Remote, and you're on the other radio. No re-entering passwords after the first login. The Switch Account button on the Radio Selector makes it easy, and Set Default in Manage SmartLink Accounts lets you pick which account Remote uses by default. Not even Flex does this stuff, so we're living on the edge here by giving you this capability, but that's when JJ Flexible shines, right at its honed technological edge.

### See Who Else Is On the Radio (MultiFlex Client Management)

When you connect to a radio that has multiple clients on it — MultiFlex, in Flex terminology — you can now see who's there. Radio menu → MultiFlex Clients opens a dialog listing every connected person, their station name, and which slices they own. Your own client is marked "(This client)" so you can tell yourself apart. Primary clients can kick guests from the same dialog if you need your radio back — selecting a client and pressing Disconnect will do the trick. Note: basic MultiFlex across two remote SmartLink clients still has some rough edges we're fixing in the next release — if you see odd slice visibility or missed connect/disconnect announcements, that's known and on the fix list.

### SmartLink Port Forwarding Controls (for Tough Networks)

SmartLink normally handles your port forwarding automatically through UPnP, but sometimes that path just doesn't work — you're behind restrictive NAT, your router doesn't support UPnP properly, or you've turned UPnP off for security reasons. Until now, your only option was wrestling with an inaccessible radio control tool to set it up manually. No longer. The new Network tab in Settings lets you configure port forwarding directly within JJ Flexible: specify the port, point SmartLink at it, done. For the security-minded operator or anyone on a network where UPnP just won't cooperate, you'll herald this as the best thing since sliced bread — a real game-changer, because it's the first step toward the complete radio-control-and-firmware-update pathway we're building right into JJ Flexible Radio. The next version rounds this out with full UPnP support, automatic hole-punch, and help docs that explain the whole setup in plain, accessible English. For now, if you need guidance on which ports to forward, check Flex Radio's SmartLink setup documentation and take note of what they say you need to open.

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

Close any of JJ Flexible Radio's dialogs and you'll hear a quick status announcement — "Listening on 14.175, USB, 20 meter band, slice A" — so your screen reader doesn't just say "pane" and leave you wondering, "where's the pain, man?"

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
- **and ... the Big one — the big cahuna! And yes, I'm sorry I hid this in a bullet hundreds of words after teasing the feature in our headlines, I'm a mean old amateur radio operator, what can I say? PC-side noise reduction is now live on every Flex radio**, including all 6000 series radios which don't have fancy DSP hardware and beefy processors on the radio. Two freely available, open source DSP noise processing engines are wired into the RX audio chain: a neural RNNoise engine and a spectral subtraction engine. These run on your computer, so they work whether your radio has built-in DSP or not. Toggle them from the DSP menu. No separate hardware, no license required — just more noise reduction available on more radios when you use JJ Flexible Radio. Note: you must have PC audio turned on to enjoy this feature. Also, expect these features to become much more customizable in our next public release.

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
- **Everything speaks**: Connection states, mode changes, frequency updates, error messages — nothing happens silently anymore. If JJFlexRadio does something, your screen reader knows about it. Error dialogs that used to appear behind other windows now show up front and center, and your screen reader announces them before the dialog even opens. If you use a braille display, you'll get those messages as well. We plan to implement a verbosity system so that JJ Flexible won't spam you if you don't need spamming/screen reader speech.
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
- **Audio Peak Filter mode guard**: APF is a CW-only feature, but JJFlexRadio used to happily toggle it in SSB mode without doing anything. Now if you try it outside CW, you get a clear message: "Audio Peak Filter is only available in CW mode."

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

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.12.0 — Logging Mode {#v4-1-12-0}

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

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.11.0 — Classic/Modern Mode, Auto-Connect & Audio Fix {#v4-1-11-0}

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

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.10.0 — SmartLink Saved Accounts {#v4-1-10-0}

Finally! This one's been on my list forever. You can now save your SmartLink login and stop typing credentials every ... single ... time you want to connect to a remote radio. I know, I know — it should have been there from day one.

- **Saved SmartLink accounts**: After logging in, JJFlexRadio asks if you want to save the account. Give it a friendly name like "Main ISS Flex Radio" (I can dream, right?), or "Club Station" and next time you click Remote, just pick it from the list. No more hunting for passwords or waiting for two-factor codes while your DX window closes.
- **Secure storage**: Your login tokens, little pieces of data that tell Flex Systems that you are in fact you, are encrypted using Windows DPAPI — tied to your Windows login. If someone copies the file to another machine, it's useless to them. No plaintext passwords, ever, not anymore that is.
- **Automatic refresh**: When your session expires (they do, eventually), JJFlexRadio quietly tries to refresh it. If that works, you won't even notice. If it fails, you'll need to log in again, but your saved accounts stick around.
- **Account housekeeping**: You can rename or delete saved accounts from the selector. Made a typo in the name? Fixed in two clicks.
- **Improved auth security**: I upgraded the auth flow to a more modern method that's more secure and actually allows the "remember me" feature to work properly. The old way literally couldn't do refresh tokens. Who knew?

When you click Remote and have saved accounts, you'll see the account picker. Want to log in fresh? Just hit "New Login."

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.9.1 — WebView2 & Screen Reader Fixes {#v4-1-9-1}

This patch squashes some bugs that made SmartLink login a real pain, especially if you use a screen reader.

- **Login window no longer freezes everything**: The login window was locking up for several seconds while the browser initialized in the background. Now it loads asynchronously — you'll see "Authenticating with SmartLink..." while it warms up, and your screen reader keeps working. Much better.
- **The focus bug is dead**: NVDA users were getting stuck in limbo because we were yanking focus around too aggressively. The login page now waits until it's actually ready before announcing "Login page ready." Patience is a virtue, even for code.
- **Better screen reader support**: Swapped out the screen reader library for better NVDA, JAWS, and SuperNova support. Same announcements, better compatibility.
- **No more "access denied"**: Moved the browser cache folder to your AppData so it stops complaining when JJFlexRadio runs from Program Files.

If SmartLink was hanging or your screen reader went mysteriously silent during login, give this version a try.

[Return to top](#top) · [Jump to versions](#versions)

## 4.1.9.0 — The .NET 8 Migration Release {#v4-1-9-0}

This is a big one. I finally ripped the band-aid off and migrated the entire codebase from the old .NET Framework to the modern .NET 8. Here's what changed:

- **64-bit and 32-bit support**: JJFlexRadio now builds for both x64 (64-bit) and x86 (32-bit). The installer names include the architecture suffix so you know which one you're grabbing.
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

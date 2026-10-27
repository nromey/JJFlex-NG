> The help's outline: the one file the help build reads to know which pages exist, where each lives, and what the Contents and the Index show.
>
> How to read and edit it:
> - A heading line is one Contents entry. Its level is its depth in the Contents, and its text is the name the Contents shows.
> - The line straight under a heading names that entry's page, written Folder/Name.md, or just Name.md for a page at the top of md/. The build checks it exactly: the file must exist at that path, with that case.
> - A heading with no page line is a folder entry: it groups the entries under it and opens no page.
> - A page line that starts with "Planned: " names a page that is in the outline but not written yet. The build leaves a planned page out of the Contents, the Index and the search, and lists every planned page when it finishes. When you write the page, delete the "Planned: " and keep the rest of the line. The build fails if a planned page exists, so the mark cannot outlive the page it describes.
> - A folder entry whose entries are all planned is left out of the Contents too, until one of them is written.
> - A line starting with a greater-than sign is a note. The build ignores notes. They say what each page covers and where its words come from: MOVED is an existing page in its new place, SPLIT is a piece of a page that will be divided, NEW is a page nobody has written yet.
> - Blank lines are ignored. Any other line fails the build, naming its line number.
>
> Rules the build enforces on the pages themselves: every file under md/ is named here; file names are unique across all folders, ignoring case; there is one level of folders; links between pages use the bare page name, like [Listen Back](ListenBack.md), and the build finds the folder.
>
> WhatsNew.md is not in git. The build copies docs/CHANGELOG.md to md/WhatsNew.md before anything else runs.

# Welcome
Welcome.md
> MOVED. Yours to write.

# What's New
WhatsNew.md
> Generated from the changelog at build time. Opens with a note that it is history; tester names out; stale entries annotated.

# Getting Started
GettingStarted/GettingStarted.md
> MOVED. Installation and first connection; you write the introduction.

## Screen Reader Setup
GettingStarted/ScreenReaderSetup.md
> MOVED. Setting up NVDA, JAWS or Narrator for the app.

## Operating Modes
GettingStarted/OperatingModes.md
> MOVED. Modern tuning, Classic tuning, Logging mode, and switching between them.

# Know Your Radio
KnowYourRadio/KnowYourRadio.md
> MOVED. The panel guides. In the app they open only from three confirmations on the Settings Radio Setup tab.

## FLEX-6300
KnowYourRadio/Flex6300.md
> MOVED. Also embedded in the app.

## FLEX-6400 and FLEX-6600
KnowYourRadio/Flex6400And6600.md
> MOVED. Also embedded in the app.

## FLEX-6500
KnowYourRadio/Flex6500.md
> MOVED. Also embedded in the app.

## FLEX-6700
KnowYourRadio/Flex6700.md
> MOVED. Also embedded in the app.

## FLEX-8400 and FLEX-8600
KnowYourRadio/Flex8400And8600.md
> MOVED. Also embedded in the app.

## Aurora AU-510 and AU-520
Planned: KnowYourRadio/Aurora510And520.md
> NEW, added 2026-10-26 at your request. Written only once its panel layout is checked against FlexRadio's own documentation (#832); the in-app guide gets it at the same time.

# Keyboard Reference
KeyboardReference/KeyboardReference.md
> SPLIT, the hub. How the keys are organised, which keys work where, and a link to every page below.

## Keys That Work Anywhere in JJ Flexible
Planned: KeyboardReference/AppWideKeys.md
> SPLIT. Keys that work anywhere in the app, and the view controls. Not called global hotkeys: to Noel that term means keys that work outside the app.

## Using JJ Flexible From Any Application
Planned: KeyboardReference/FromAnyApplication.md
> SPLIT. The keys that reach JJ Flexible while another program has focus, and the Settings System-wide keys tab. Its index keywords include "global hotkeys".

## Band and Mode Change Keys
Planned: KeyboardReference/BandAndModeKeys.md
> SPLIT. Band jumping and mode switching.

## Radio Control Keys
Planned: KeyboardReference/RadioControlKeys.md
> SPLIT. Transmit filter sculpting, push to talk and receive filter adjustment.

## JJ Flexible Home Expander Keys
Planned: KeyboardReference/HomeFieldKeys.md
> SPLIT. Every Home field's expander keys and the category openers. The S Meter and Offset fields get their missing sections (#466).

## Keys Used when Adjusting Audio
Planned: KeyboardReference/AudioKeys.md
> SPLIT. Audio controls, the Audio Workshop's keys (two sections today, merged into one), transmit tests, the Home recorder and Recorded QSOs.

## Keys used from Within Dialogs
Planned: KeyboardReference/DialogKeys.md
> SPLIT. The radio selector, moving between categories, the Audio Devices dialog and the trace archive browser.

## Messages, Scanning and Spotting Keys
Planned: KeyboardReference/MessagesScanningAndSpotting.md
> SPLIT. CW message keys, scan keys and spotting. The resume-scan and DX cluster keys do nothing today (#829), and the page says so.

## Logging Mode Keys
Planned: KeyboardReference/LoggingModeKeys.md
> SPLIT. Moved text with corrections only, if you agree it is not a new logging page.

## Commands With No Default Key
Planned: KeyboardReference/CommandsWithNoDefaultKey.md
> SPLIT. Commands you reach from the Command Finder or give a key yourself.

## The JJ Key
KeyboardReference/JJKey.md
> MOVED from leader-key. How the JJ key works and how every layer behaves (arrows adjust, H lists keys, Enter keeps, Escape reverts). The keys themselves are on the pages below, divided by layer, which together are the one copy of the chord list.

### JJ Key Top Level
Planned: KeyboardReference/JJKeyTopLevel.md
> SPLIT. Every key on the top layer: the letters that open each layer, the slice jumps, the switches and the actions.

### Audio Layer
Planned: KeyboardReference/JJKeyAudioLayer.md
> SPLIT. The audio layer's keys, and the doors into it (A, V, Alt+P).

### Filter Layer
Planned: KeyboardReference/JJKeyFilterLayer.md
> SPLIT. The filter layer's keys.

### Noise Reduction Layer
Planned: KeyboardReference/JJKeyNoiseLayer.md
> SPLIT. The noise layer's switches. Each new layer gets its own page here as it is built.

## Command Finder
KeyboardReference/CommandFinder.md
> MOVED. Finding any command by name, including the ones with no key.

## Hotkey Editor and Key Assignments
Planned: KeyboardReference/HotkeyEditor.md
> NEW. Reading, changing and exporting your key bindings.

## Escape and Collapse
KeyboardReference/EscapeAndCollapse.md
> MOVED. What Escape does in each place.

## The JJ Key Explorer
Planned: KeyboardReference/JJKeyExplorer.md
> NEW. Browsing every JJ key and layer as a tree.

# JJ Flexible Home
JJFlexibleHome/JJFlexibleHome.md
> MOVED. The main screen: purpose, its fields, categories and how to move around it.

## What Happens to the Home Layout When Your Radio Isn't Connected
JJFlexibleHome/HomeWithoutARadio.md
> MOVED. Radio Rescue and what Home offers before a radio connects.

## Tuning and Frequency
JJFlexibleHome/TuningAndFrequency.md
> MOVED. Tuning, entering a frequency, tuning steps from the frequency field.

## Band Navigation
JJFlexibleHome/BandNavigation.md
> MOVED. The Band menu and band keys. Has a table today (#532).

## 60 Meter Operation
JJFlexibleHome/SixtyMeters.md
> MOVED. The 60 meter channels.

## Mode Switching
JJFlexibleHome/ModeSwitching.md
> MOVED. Changing mode. Has a table today (#532).

## Slice Management
JJFlexibleHome/SliceManagement.md
> MOVED. Creating, releasing, choosing and muting slices.

## Squelch
JJFlexibleHome/Squelch.md
> MOVED. Squelch on Home and in the Receiver menu.

## Status Dialog
JJFlexibleHome/StatusDialog.md
> MOVED. Describes a dialog that does not exist today; rewrite (#476).

## Panadapter Visibility
JJFlexibleHome/PanadapterVisibility.md
> MOVED. Reached only from the Settings Notifications tab, Display group.

## Memories
Planned: JJFlexibleHome/Memories.md
> NEW (#828). The memories dialog, from Ctrl+J then Alt+M or the Command Finder: listing, tuning, adding, deleting and editing memories.

## Scanning
Planned: JJFlexibleHome/Scanning.md
> NEW (#828). Linear, saved and memory scans from the Command Finder, and Ctrl+Z to stop. Written after the bench check.

# Receiver and DSP

## Filters and DSP
Receiver/FiltersAndDsp.md
> MOVED. The Filter menu, the DSP submenu and the filter keys. A table and two moved chords (#532).

## Receiver Controls
Planned: Receiver/ReceiverControls.md
> NEW. AGC mode and threshold, RF gain and squelch level.

## Tracking Notch Filters
Planned: Receiver/TrackingNotchFilters.md
> NEW (#580). The tracking notch manager, from Ctrl+J then Alt+N or the DSP category, and Auto Notch beside it.

## Equalizers
Planned: Receiver/Equalizers.md
> NEW. The receive and transmit equalizers, from buttons in Home's DSP and Transmission categories.

## PC-Side Noise Reduction
Receiver/PcNoiseReduction.md
> MOVED. Noise reduction on the computer, noise profiles and capturing one.

## Antenna Switching
Receiver/AntennaSwitching.md
> MOVED. Receive and transmit antennas, diversity and Enhanced Signal Clarity.

## Feature Availability
Planned: Receiver/FeatureAvailability.md
> NEW. What your radio and licence allow, and why a control is unavailable rather than hidden.

## GPS and Reference
Planned: Receiver/GpsAndReference.md
> NEW. GNSS status and the reference oscillator.

# Transmit

## PTT and Transmission
Transmit/PttAndTransmission.md
> MOVED. Keying the radio, VOX, transmit status and the Transmit menu.

## Power and the Antenna Tuner
Planned: Transmit/PowerAndTuner.md
> SPLIT and NEW. The Power dialog, tune carrier, ATU tune, ATU mode and memories, dummy load mode.

## Transmit Controls
Planned: Transmit/TxControls.md
> NEW. The Transmit controls dialog, from the Command Finder only.

## TX Bandwidth Sculpting
Transmit/TxSculpting.md
> MOVED. Shaping the transmit filter. One moved chord (#532).

## CW Send and Receive Text Boxes
Transmit/CwTextBoxes.md
> MOVED. Sending and reading CW as text.

## CW Messages
Planned: Transmit/CwMessages.md
> NEW. Stored CW messages, managing them and the keys that send them.

# Audio Workshop
AudioWorkshop/AudioWorkshop.md
> The hub, yours to write. What the workshop is, opening it, its keys, its tabs, and where everyday volume lives.

## This Computer

### Microphone Setups
Planned: AudioWorkshop/MicrophoneSetups.md
> SPLIT. Setups versus the radio's mic profiles, Apply and Save.

### Transmit Cleanup
Planned: AudioWorkshop/TransmitCleanup.md
> NEW. PC Cleanup: transmit noise reduction, the noise gate and the residual monitor.

## Transmit Settings
Planned: AudioWorkshop/TransmitSettings.md
> SPLIT. Microphone source, boost and bias, compander, processor, transmit filter and monitor.

## Hear Yourself

### Limitations
Planned: AudioWorkshop/HearYourselfLimitations.md
> NEW. What limits hearing yourself, by radio model and number of SCUs, and what an SCU is.

### Audio Check
Planned: AudioWorkshop/AudioCheck.md
> SPLIT. Checking your transmit audio safely: listen method, power during checks, playing the last take.

### Listen Back
Planned: AudioWorkshop/ListenBack.md
> NEW. Tell me how my sound changed, and the prompt about radio settings a take changed that the radio never confirmed restoring.

### Test Tone
Planned: AudioWorkshop/TestTone.md
> SPLIT. Pitch, level, hearing it locally, arming, and the transmit filter warning.

### Reference Recording
Planned: AudioWorkshop/ReferenceRecording.md
> NEW. What a reference recording is, how you might use one, and making and sending one.

### Loopback Check
Planned: AudioWorkshop/LoopbackCheck.md
> SPLIT. Two-receiver radios only; what it proves and what it does not.

## Meters in the Workshop
Planned: AudioWorkshop/WorkshopMeters.md
> SPLIT. The Meters tab's eight readings and the meter inventory; links to Meter Tones.
> May split further: there is a lot to meters, and braille support will add more.

## Amplifier
Planned: AudioWorkshop/Amplifier.md
> NEW. What the radio reports about an external amplifier or tuner, and standby versus operate.

## Audio Diagnostics
Planned: AudioWorkshop/AudioDiagnostics.md
> NEW. The receive and transmit chain checks and the evidence to send with a report.

## Audio Presets
AudioWorkshop/AudioPresets.md
> MOVED. Saved audio presets. We'll need to explain the differences between radio profiles, audio settings, etc . May need a split because it's a confusing setting.

# Listening to your radio

## Audio Levels
Audio/AudioLevels.md
> MOVED. PC and on-radio levels.

## Why Your Audio Has Two Numbers
Audio/AudioTwoNumbers.md
> MOVED. The two level readings and what each one means.

## Audio Devices
Planned: Audio/AudioDevices.md
> SPLIT from Audio Troubleshooting. Choosing sound devices and the audio system.

## The Recorder
Planned: Audio/Recorder.md
> NEW. Recording and playing a slice, recording to a file and playing from the computer.

## Recorded QSOs
Audio/RecordedQsos.md
> MOVED. Finding and playing back recorded contacts.

## Braille Display
Planned: Audio/BrailleDisplay.md
> NEW. The braille status line and its toggle key. Plans for Braille.

# Sounds and Notifications

## Earcon Control
Sounds/EarconControl.md
> MOVED. Turning earcons on and off and setting their levels.

## Earcon Explorer
Sounds/EarconExplorer.md
> MOVED. Hearing every earcon; also a tab in the Audio Workshop.

## Meter Tones and the Meters Panel
Sounds/MeterTones.md
> MOVED. Meter tones, presets and the peak watcher. Has a table today (#532).

## CW Notifications
Sounds/CwNotifications.md
> MOVED. Hearing the app's messages in Morse.

## Frequency Entry Sounds
Sounds/FrequencyEntrySounds.md
> MOVED. The sounds while you type a frequency.

## Operator Alarms
Planned: Sounds/OperatorAlarms.md
> NEW. Setting an alarm, the alarm editor and the alarm journal.

## Speech and Verbosity
Planned: Sounds/SpeechAndVerbosity.md
> NEW. What each verbosity level announces; written after Codex confirms it from the code.

# Logging
Logging/Logging.md
> MOVED, corrections only. Quick Log and the log form, described as they behave today.

## Callbook Lookup
Logging/CallbookLookup.md
> MOVED, corrections only. Station lookup and logging the contact.

# Settings
Settings/Settings.md
> SPLIT from Settings and Profiles. Opening Settings, OK, Apply and Cancel, the tabs, and where your files live.

## Profiles
Planned: Settings/Profiles.md
> SPLIT and NEW. Global, transmit and mic profiles, export and import, and the profile reports.

## Operators
Planned: Settings/Operators.md
> NEW. The Radio menu's Operators list and each operator's details, which CW messages and the log rely on.

## PTT Tab
Planned: Settings/PttSettings.md
> NEW. The PTT tab.

## Tuning Tab
Settings/TuningSteps.md
> MOVED. Coarse and fine tuning steps.

### Filter Presets
Settings/FilterPresets.md
> MOVED. Editing filter presets from the Tuning tab.

## License Tab
Planned: Settings/License.md
> NEW. The License tab.

## Audio Tab
Planned: Settings/AudioSettings.md
> NEW. A short map of the Audio tab, pointing at the pages that describe each group.

## Radios Tab
Planned: Settings/RadioConfiguration.md
> SPLIT. Whose radio this is, signal strength units, SmartLink connection, reaching it and remote administration.

## Radio Setup Tab
Planned: Settings/RadioSetupChecklist.md
> NEW. The seven steps, from connecting to restarting the radio, and the panel guide buttons.

## Notifications Tab
Planned: Settings/Notifications.md
> NEW. A short map pointing at the Sounds pages, not a second description of them.

### Messages You Have Silenced
Settings/SilencedMessages.md
> MOVED. Reviewing and restoring messages you chose not to see again.

## Accessibility Tab
Planned: Settings/Accessibility.md
> NEW. The Accessibility tab as a whole.

### Double-Tap Tolerance
Settings/DoubleTapTolerance.md
> MOVED. How fast a double tap must be.

### When Another Program Takes Your Keyboard
Settings/FocusWatchdog.md
> MOVED. The focus watchdog and its off switch.

## Updates Tab
Planned: Settings/Updates.md
> NEW. Update channels, Check for Updates, and what an update does to your settings.

# Networking and SmartLink
> The Settings Network tab is covered by this folder.

## How the App Reaches a Remote Radio
Networking/NetworkingOverview.md
> MOVED. How the app reaches a remote radio, at a glance. No tier numbers anywhere in the networking pages.

## Manual Port Forwarding
Networking/ManualPortForwarding.md
> MOVED. Forwarding a port on your router yourself.

## UPnP
Networking/Upnp.md
> MOVED. Letting the router open the port.

## Network Diagnostics
Networking/NetworkDiagnostics.md
> MOVED. Running the network test and reading its results.

## SmartLink Accounts
Networking/SmartLinkAccounts.md
> MOVED. Signing in and managing accounts.

## Port Forwarding Safety
Networking/PortForwardOwnership.md
> MOVED. Which ports are ours and what we change on your router.

## SmartLink and Remote Operation
Networking/SmartLinkRemote.md
> MOVED. Operating a radio away from home.

## MultiFlex Clients and Connected Stations
Networking/MultiFlexClients.md
> MOVED. Who else is on the radio, and the Connected Stations list.

# Troubleshooting
> The Settings Diagnostics tab is covered by The Diagnostic Log.

## Connection Issues
Troubleshooting/ConnectionTroubleshooting.md
> MOVED. When a radio will not connect.

## Audio Problems
Troubleshooting/AudioTroubleshooting.md
> MOVED. The no-sound checklist, kept whole; devices and evidence moved out with pointers.

## JJ Flexible Fix
Planned: Troubleshooting/Fixer.md
> NEW. The transmit problems walk, its stages, pausing and resuming, and saved runs.

## Signal Captures
Planned: Troubleshooting/SignalCaptures.md
> NEW (#580). What a signal capture holds, starting one, and sending it.

## The Diagnostic Log
Troubleshooting/DiagnosticLog.md
> MOVED. Capturing a log, saved sessions, problem bundles, and sending me the evidence.

## Known Issues
Troubleshooting/KnownIssues.md
> MOVED. What is known not to work yet.

# About
AboutDialog.md
> MOVED. The About dialog.

## Acknowledgments
Planned: Acknowledgments.md
> NEW. Thanks to our testers. A name appears only once that person has said yes, so it starts with none.

> The outline ends here.
> Not documented because nothing reaches them yet. Each of these exists in the code, but an operator cannot reach it today, or reaching it does nothing:
> - The DX cluster. Alt+Shift+X is bound but only writes a trace line, and the cluster dialog is opened by nothing (#829).
> - Resuming a paused scan. Ctrl+Shift+F2 is bound, but nothing ever pauses a scan, so it cannot act (#829).
> - The Quick Actions popup. It was retired and no code for it remains; its history is in What's New. Whether its old page is deleted is open item 4 in the plan.
> - The newer dialogs for scanning, memory scanning, saved scans, scan names, memory groups and group management. The live surfaces are Jim's original forms, which the Scanning page covers.
> - The newer logging dialogs: the log entry form, finding an entry, log characteristics, log templates, import, export and LOTW merge. The live log surfaces are Jim's original forms, and the Logging page describes them as they behave. The Log menu's LOTW Merge item says it is not implemented, and the Navigate menu's four items say "coming soon".
> - The newer operator dialogs (the operators list, personal information and login name). Jim's original forms do this work, and the Operators page covers them.
> - The newer welcome dialog. First run shows Jim's original welcome form, which Getting Started covers.
> - The standalone system-wide keys dialog. The Settings tab holds the same panel.
> - The newer reverse beacon dialog. Ctrl+Alt+R opens Jim's form, which the spotting keys page mentions.
> - Seven Jim-era leftovers nobody opens: the command line, COM information, define commands, menus, pan list, watt meter configuration and About Program dialogs, plus a file picker.
> - A memory-type saved scan, which is switched off in the code.

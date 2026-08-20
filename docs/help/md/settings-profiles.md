# Settings and Profiles

JJ Flexible Radio Access has a Settings dialog that lets you customise the application to fit your operating style.

## Opening the Settings Dialog

Open the **Tools** menu and choose **Settings**. The dialog opens on a list of its categories — PTT, Tuning, License, Audio, Network and the rest — with whichever category you pick filling the rest of the window. Arrow through the list to move between them, or press **Ctrl+Tab** and **Ctrl+Shift+Tab** from anywhere in the dialog, which works even while you are sitting in a text box. Tab moves from the list into that category's own settings and never changes category by itself. There is more on this on the Keyboard Reference page, under "Moving between categories".

## OK, Apply, and Cancel

Every screen in Settings uses the same three buttons, and they always mean the same thing:

- **OK** applies your changes and closes the dialog.
- **Apply** applies your changes and leaves the dialog open, so you can keep working.
- **Cancel** closes without applying anything you have not already applied. (Cancel does not undo an Apply you already pressed — once applied, a change is real.)

There are no per-screen variants — no separate "save this" or "apply that" buttons to hunt for. If you changed it, OK or Apply keeps it.

Some settings describe a radio that is not connected right now — a radio name, or the REM ON power jack setting on the Radios tab. Those are saved immediately and applied the next time you connect, and JJ Flexible Radio Access tells you so in plain words when you press OK, in a small dialog you can re-read at your own pace. If it did not say anything is waiting, nothing is.

## Key Settings Tabs

- **PTT** — transmit timeout and warning timing.
- **Tuning** — tuning step defaults, band memory, and tuning speech debounce (see below).
- **License** — license class, country selection, and transmit rule enforcement (see below).
- **Audio** — the radio's own outputs, radio audio through this computer, alert sounds, meter tones, and the braille status line.
- **Network** — SmartLink port forwarding, connection tiers, and network diagnostics.
- **Radios** — per-radio settings remembered by serial number: how to reach each radio, its name, remote administration allowances, the REM ON power jack, and whether the radio is somewhere you can physically get to. Works with no radio connected — that is the point.
- **Radio Setup** — the ordered checklist for bringing a new radio up, especially one that will live somewhere you cannot walk to.
- **Notifications** — speech verbosity, alert sounds, and connection progress announcements.
- **Accessibility** — the Double-Tap Tolerance setting and other accessibility controls live here.
- **Updates** — update channel and automatic update checking.
- **Diagnostics** — the diagnostic log, detailed captures, saved sessions, problem report bundles, and disk space controls. The full story is on the Diagnostic Log help page; Tools, then Diagnostics, jumps straight to this tab.

## The Radios Tab — Reaching This Radio From Away

Each radio you own gets its own per-radio settings, remembered by serial number, and one of them settles a question the app used to keep asking: do you ever want to reach this radio over the internet?

The **"Reaching this radio from away"** setting has three answers, because "no" genuinely comes in two flavors:

- **Ask me when it comes up** — the starting state. On a local connection to an unregistered radio, JJ Flexible may mention SmartLink registration as an option.
- **I only use this radio here** — the peace-and-quiet answer. Choosing it silences every SmartLink registration prompt for this radio, permanently. Nothing about your local operating changes; registering is only how you reach a radio from somewhere else, and a radio that never leaves your house does not need it.
- **I want to reach it from away** — points you toward SmartLink registration and the Radio Setup checklist.

Your answer is saved per radio, works with no radio connected, and can be changed here any time it stops being true.

## Profiles

Profiles let you save and switch between different configurations — useful if
you operate from different locations, or switch between operating styles, such
as a contest profile with tighter filters and a ragchew profile with more
comfortable bandwidth.

The important thing to know first: **these profiles are stored in the radio, not
in this program.** There are three kinds — global, transmit and microphone — and
a global profile holds the whole station, which means every slice, its frequency
and its mode.

Because they live in the radio, they are shared with everyone who connects to
it. If two of you use the same rig, you are both using the same profiles. That
is worth remembering before you save one.

Open the profile list from the **Radio** menu, then **Profiles**. It shows the
profiles you have told JJ Flexible about, and also any the radio itself is
carrying that you have never adopted — those are marked "on radio". From here
you can select a profile to load it, add a name, update an entry, delete one, or
save the current station into a global profile.

### Saving your station without opening this dialog

Adding or releasing a slice does not change anything permanently: the radio puts
its stored setup back the next time you connect. The quickest way to keep a
change is **Save Station Setup to Radio** on the Slice menu, which saves into
whichever global profile the radio currently has loaded, and tells you which one
that is before it writes anything.

You can also have JJ Flexible ask you on the way out. Turn on **Offer to save my
setup to the radio when I disconnect** on the Notifications tab. It only ever
asks — it never saves by itself, it stays quiet when you have changed nothing or
when another operator is connected, and it does not raise the subject at all on
a radio you have not marked as yours on the Radios tab.

There is more about all of this on the Slice Management help page.

## The Tuning Tab

The **Tuning** tab controls how frequency announcements work while you are tuning:

- **Enable tuning debounce** — when this is checked, the application waits until you stop pressing arrow keys before speaking the frequency. Tuning debounce prevents you from hearing every intermediate step when you are tuning rapidly across a band.
- **Debounce delay (ms)** — how long the application waits after your last keystroke before speaking the final frequency. A shorter delay gives you faster feedback; a longer delay means more silence while you are still tuning. The default works well for most operators.

You can also toggle tuning debounce on the fly with the JJ key — press `Ctrl+J`, then `D`.

## The License Tab

The **License** tab controls country-specific transmit rules:

- **Country** — select your country (the default is US). Your choice determines which band plans and channelisation rules apply.
- **Enforce transmit rules** — when this is checked, the application restricts tuning and transmission to legal frequencies and channels for your country. For example, on 60 meters in the US this setting limits you to the 5 authorised channels plus the digital segment.

## Configuration Files

JJ Flexible Radio Access stores its configuration in your user profile folder at `%AppData%\JJFlexRadio\`. (That folder keeps the older `JJFlexRadio` name even though the program file is now `jjflexible.exe` — your settings stay put when you update.) If you ever need to start from scratch, you can rename or delete that folder while the application is closed, and JJ Flexible Radio Access will create fresh default settings the next time it launches.

**Warning:** Back up your configuration folder before making any destructive changes to it. Deleting the folder means losing your saved settings, your CW messages, and your profiles.

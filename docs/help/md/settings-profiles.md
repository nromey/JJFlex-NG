# Settings and Profiles

JJ Flexible Radio Access has a Settings dialog that lets you customise the application to fit your operating style.

## Opening the Settings Dialog

Open the **Tools** menu and choose **Settings**. The Settings dialog opens as a tabbed window, and you can tab through the tabs to find the section you want.

## Key Settings Tabs

- **Operating Mode** — switch between the Modern and Classic layouts.
- **Audio Routing** — configure your speaker, headphone, and line-out settings.
- **CW Messages** — set up the CW message macros that fire on `Ctrl+1` through `Ctrl+7`.
- **Callbook Service** — choose QRZ.com, HamQTH, or another callbook lookup service.
- **Earcon Volume** — adjust the volume of the application's alert sounds.
- **Tuning** — configure tuning speech debounce (see below).
- **License** — country selection and transmit rule enforcement (see below).
- **Accessibility** — the Double-Tap Tolerance setting and other accessibility controls live here.

## Profiles

Settings profiles let you save and switch between different configurations. Profiles are useful if you operate from different locations, or if you switch between different operating styles — for example, a contest profile with tighter filters and a ragchew profile with more comfortable bandwidth.

## The Tuning Tab

The **Tuning** tab controls how frequency announcements work while you are tuning:

- **Enable tuning debounce** — when this is checked, the application waits until you stop pressing arrow keys before speaking the frequency. Tuning debounce prevents you from hearing every intermediate step when you are tuning rapidly across a band.
- **Debounce delay (ms)** — how long the application waits after your last keystroke before speaking the final frequency. A shorter delay gives you faster feedback; a longer delay means more silence while you are still tuning. The default works well for most operators.

You can also toggle tuning debounce on the fly with the leader key — press `Ctrl+J`, then `D`.

## The License Tab

The **License** tab controls country-specific transmit rules:

- **Country** — select your country (the default is US). Your choice determines which band plans and channelisation rules apply.
- **Enforce transmit rules** — when this is checked, the application restricts tuning and transmission to legal frequencies and channels for your country. For example, on 60 meters in the US this setting limits you to the 5 authorised channels plus the digital segment.

## Configuration Files

JJ Flexible Radio Access stores its configuration in your user profile folder at `%AppData%\JJFlexRadio\`. (The folder name uses the internal binary name, not the product name.) If you ever need to start from scratch, you can rename or delete that folder while the application is closed, and JJ Flexible Radio Access will create fresh default settings the next time it launches.

**Warning:** Back up your configuration folder before making any destructive changes to it. Deleting the folder means losing your saved settings, your CW messages, and your profiles.

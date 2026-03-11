# Settings and Profiles

JJ Flexible Radio Access has settings to customize the application to fit your operating style.

## Opening Settings

Access settings from the Settings menu or the appropriate menu item.

## Key Settings

- **Operating Mode** — Switch between Modern and Classic layout
- **Audio Routing** — Configure speaker, headphone, and line out settings
- **CW Messages** — Set up your `Ctrl+1` through `Ctrl+7` CW message macros
- **Callbook Service** — Choose QRZ.com, HamQTH, or other lookup services
- **Earcon Volume** — Adjust the volume of UI sound effects
- **Tuning** — Configure tuning speech debounce (see below)
- **License** — Country selection and transmit rule enforcement (see below)

## Profiles

Settings profiles let you save and switch between different configurations. This is useful if you operate from different locations or switch between different operating styles (contesting vs. ragchewing, for example).

## Tuning Tab

The **Tuning** tab controls how frequency announcements work while you're tuning:

- **Enable tuning debounce** — When checked, the app waits until you stop pressing arrow keys before speaking the frequency. This avoids hearing every intermediate step when you're tuning rapidly.
- **Debounce delay (ms)** — How long the app waits after your last keystroke before speaking the final frequency. A shorter delay means faster feedback; a longer delay means more silence while tuning. The default works well for most operators.

You can also toggle debounce on the fly with the leader key: press `Ctrl+J` then `D`.

## License Tab

The **License** tab controls country-specific transmit rules:

- **Country** — Select your country (defaults to US). This determines which band plans and channelization rules apply.
- **Enforce transmit rules** — When checked, the app restricts tuning and transmission to legal frequencies and channels for your country. For example, on 60 meters in the US, this limits you to the 5 authorized channels plus the digital segment.

## Configuration Files

JJ Flexible Radio Access stores its configuration in your user profile folder at `%AppData%\JJFlexRadio\`. If you ever need to start fresh, you can rename or delete this folder (with the application closed) and the app will create new default settings on next launch.

**Warning:** Back up your configuration folder before making changes to it. Deleting it means losing your saved settings, CW messages, and profiles.

# Double-Tap Tolerance

Double-Tap Tolerance is a timing setting that controls how quickly you need to press a key twice for JJ Flexible Radio Access to recognise it as a double-tap. It currently affects two behaviours:

- Double-tapping `[` or `]` to enter filter-edge adjustment mode.
- Double-tapping `Escape` to collapse all open field groups and return you to the JJ Flexible Home.

Any future features that rely on a double-tap gesture will respect the same setting, so you only have to configure it once.

## Where to Set It

Open **Tools > Settings** and go to the **Accessibility** tab. You will find a radio-button group called **Double-Tap Tolerance** with four options:

- **Quick (250 ms)** — for fast typists. Close to the pre-Sprint 28 behaviour, if you were used to the older snappy feel.
- **Normal (500 ms)** — the recommended default. Balanced for most users.
- **Relaxed (750 ms)** — for a more deliberate input cadence.
- **Leisurely (1000 ms)** — the slowest option. Good if you prefer to verify each keystroke with speech before you press the next key.

Select the option that matches how you type. Your choice persists across application restarts.

## How to Tell If the Setting Is Right for You

If your double-taps are not being recognised — for example, you press `[` twice and nothing happens to switch you into filter-edge mode — your current tolerance is probably too tight. Move to a slower setting.

If you find that single Escape presses occasionally trigger the collapse-all behaviour when you did not intend them to, your current tolerance is probably too loose. Move to a tighter setting.

Most users land comfortably on Normal.

## Why Four Named Steps Instead of a Slider

Four discrete, named steps make the setting easier to navigate with a screen reader than a slider with fine-grained values would be. Each option announces with both its name and its exact millisecond count — for example, "Normal, 500 milliseconds, selected." You hear exactly what you are picking, and you do not have to arrow through dozens of possible values to find the right one.

## Related Topics

- Escape and Collapse
- Filter Edges
- Keyboard Reference

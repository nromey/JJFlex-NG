# Double-Tap Tolerance

Double-tap tolerance is a timing setting that controls how quickly you need to press a key twice for JJ Flex to recognize it as a double-tap. It affects two behaviors currently:

- Double-tapping `[` or `]` to enter filter-edge adjustment mode.
- Double-tapping `Escape` to collapse all open field groups and return to Home.

Any future features that use double-tap will respect the same setting, so you only configure it once.

## Where to Set It

Open Settings and go to the **Accessibility** tab. You'll find a radio-button group called **Double-tap tolerance** with four options:

- **Quick (250 ms)** — for fast typists. Close to the pre-Sprint 28 behavior if you were used to the older snappy feel.
- **Normal (500 ms)** — the recommended default. Balanced for most users.
- **Relaxed (750 ms)** — for a more deliberate input cadence.
- **Leisurely (1000 ms)** — slowest. Good if you prefer to verify each key press with speech before pressing the next key.

Select the one that matches how you type. Your choice persists across app restarts.

## How to Tell if It's Right for You

If your double-taps aren't being recognized — you press `[` twice and nothing changes to filter-edge mode — your current tolerance is too tight. Move to a slower setting.

If you find that single Escapes occasionally trigger collapse-all when you didn't mean them to, your current tolerance is too loose. Move to a tighter setting.

Most users land comfortably on Normal.

## Why Four Steps Instead of a Slider

Four discrete named steps make the setting easier to navigate with a screen reader than a slider with fine-grained values. Each option announces with both its name and its exact millisecond count: "Normal, 500 milliseconds, selected." You hear what you're picking; you don't have to arrow through dozens of possible values to find the right one.

## Related Topics

- Escape and Collapse
- Filter Edges
- Keyboard Reference

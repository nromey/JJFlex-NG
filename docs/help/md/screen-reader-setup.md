# Screen Reader Setup

JJFlexRadio is designed to work with NVDA and JAWS. Here's how to get the best experience.

## NVDA

NVDA works well with JJFlexRadio right out of the box. A few tips:

- Make sure you're running a recent version of NVDA (2023.1 or later recommended).
- JJFlexRadio announces important state changes automatically. If NVDA is too chatty, you can adjust verbosity in NVDA's speech settings.
- The ScreenFields panel uses standard accessible controls. Use Tab to move between fields and arrow keys to adjust values.

## JAWS

JAWS also works well. A few things to know:

- JAWS may need a moment to detect the application on first launch.
- If you're not hearing announcements from JJFlexRadio, make sure JAWS is set to read dynamic content changes.
- All menus are native Win32 menus, so JAWS navigates them naturally with the arrow keys and Enter.

## Common Tips for Both Screen Readers

- **Menus:** Press Alt to activate the menu bar. Arrow keys navigate, Enter selects.
- **ScreenFields:** Use `Ctrl+Shift+1` through `Ctrl+Shift+5` to jump directly to a ScreenFields category.
- **Command Finder:** Press `Ctrl+/` to search for any command by name. This is often faster than memorizing every hotkey.
- **Status announcements:** Press `Ctrl+Shift+S` to hear the current radio status spoken at any time.
- **Earcons:** JJFlexRadio uses short audio tones (earcons) to confirm actions like toggling DSP features. These play through your default audio device alongside screen reader speech.

## Troubleshooting

If your screen reader isn't reading JJFlexRadio controls:

1. Make sure JJFlexRadio is the focused application (Alt+Tab to it).
2. Try pressing `Ctrl+Shift+S` to force a status announcement. If you hear it, the screen reader connection is working.
3. Restart your screen reader if controls seem unresponsive.
4. Check that you're running the latest version of JJFlexRadio — accessibility improvements land in almost every release.

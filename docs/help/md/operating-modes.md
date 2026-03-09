# Operating Modes

JJFlexRadio has two main operating modes: Modern and Classic. You can switch between them in the Settings dialog.

## Modern Mode

Modern mode is the default. It's designed for keyboard-driven operation with audio feedback. Everything you need is accessible through hotkeys, the ScreenFields panel, and the Command Finder (`Ctrl+/`).

Modern mode is the recommended way to use JJFlexRadio, especially with a screen reader.

## Classic Mode

Classic mode provides a more traditional layout similar to SmartSDR. It includes visual controls that may be useful for sighted operators or those transitioning from SmartSDR.

Both Classic and Modern modes share the same Radio scope hotkeys for tuning, band jumping, mode switching, and audio controls. The difference is mainly in the visual layout and which on-screen controls are available.

## Logging Mode

When you move focus to the logging pane, you enter Logging mode. This activates the logging hotkeys (like `Alt+C` for Call, `Alt+T` for His RST, etc.) and deactivates the Radio scope keys that share the same physical keys.

For example, `Alt+C` switches to CW mode when you're in Radio scope, but jumps to the Call field when you're in Logging scope. JJFlexRadio handles this automatically based on where your focus is.

## Switching Modes

- To switch between Modern and Classic, go to Settings.
- To enter Logging mode, Tab into the logging pane. Tab out to return to Radio mode.
- Global hotkeys (like F1, Ctrl+/, and Ctrl+J leader keys) work in all modes.

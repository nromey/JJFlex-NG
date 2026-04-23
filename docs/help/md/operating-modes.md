# Operating Modes

JJ Flexible Radio Access has two main operating modes for interacting with the radio: Modern and Classic. You can switch between them in the Settings dialog.

## Modern Mode

Modern mode is the default operating mode. It is designed for keyboard-driven operation with audio feedback, and everything you need is reachable through hotkeys, the ScreenFields panel, and the Command Finder (`Ctrl+/`).

Modern mode is the recommended way to use JJ Flexible Radio Access, especially if you are working with a screen reader.

## Classic Mode

Classic mode provides a more traditional layout similar to SmartSDR. It includes visual controls that may be useful for sighted operators, or for operators who are transitioning from SmartSDR and want a familiar arrangement while they learn the application.

Both Classic mode and Modern mode share the same Radio-scope hotkeys for tuning, band jumping, mode switching, and audio controls. The main difference between them is the visual layout and which on-screen controls are available.

## Logging Mode

When you move focus into the logging pane, you enter Logging mode. Logging mode activates the logging hotkeys (such as `Alt+C` for Call, `Alt+T` for His RST, and so on) and it deactivates any Radio-scope keys that share the same physical keys.

For example, `Alt+C` switches the radio to the CW mode when you are in Radio scope, but the same `Alt+C` jumps to the Call field when you are in Logging scope. JJ Flexible Radio Access handles this switch automatically based on where your focus is currently sitting.

## Switching Between Modes

- To switch between Modern mode and Classic mode, go to Settings.
- To enter Logging mode, move focus into the logging pane (press `Tab` repeatedly until you hear the logging pane announce itself). Move focus back out to return to Radio mode.
- Global hotkeys (such as `F1` for help, `Ctrl+/` for the Command Finder, and the `Ctrl+J` leader key) work in every mode.

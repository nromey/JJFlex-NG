# Operating Modes

JJ Flexible Radio Access has two tuning modes: Modern tuning mode and Classic tuning mode. You can switch between them under **Tools > Settings**. Everything else in the application — the menus, the Home fields, the DSP features, the audio controls, the logging pane — is shared across both tuning modes. The name carries the word "tuning" because that is the only thing that changes when you switch.

## Modern Tuning Mode

Modern tuning mode is the default. It is designed for keyboard-driven operation with audio feedback, and everything you need is reachable through hotkeys, the ScreenFields panel, and the Command Finder (`Ctrl+/`).

Modern tuning mode is the recommended way to use JJ Flexible Radio Access, especially if you are working with a screen reader.

## Classic Tuning Mode

Classic tuning mode provides a more traditional tuning-knob-style layout similar to SmartSDR. It includes visual controls that may be useful for sighted operators, or for operators who are transitioning from SmartSDR and want a familiar arrangement while they learn the application.

Both Classic tuning mode and Modern tuning mode share the same Radio-scope hotkeys for tuning, band jumping, mode switching, and audio controls. The main difference between them is the visual layout and which on-screen controls are available.

## Logging Mode

When you move focus into the logging pane, you enter Logging mode. Logging mode activates the logging hotkeys (such as `Alt+C` for Call, `Alt+T` for His RST, and so on) and it deactivates any Radio-scope keys that share the same physical keys.

For example, `Alt+C` switches the radio to the CW mode when you are in Radio scope, but the same `Alt+C` jumps to the Call field when you are in Logging scope. JJ Flexible Radio Access handles this switch automatically based on where your focus is currently sitting.

Logging mode is orthogonal to Classic and Modern tuning modes — you can be in Logging mode on top of either tuning mode.

## Switching Between Modes

- To switch between Modern tuning mode and Classic tuning mode, go to **Tools > Settings**.
- To enter Logging mode, move focus into the logging pane (press `Tab` repeatedly until you hear the logging pane announce itself). Move focus back out to return to Radio mode.
- Global hotkeys (such as `F1` for help, `Ctrl+/` for the Command Finder, and the `Ctrl+J` leader key) work in every mode.

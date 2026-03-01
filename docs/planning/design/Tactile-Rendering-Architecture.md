# Tactile Rendering Architecture

## Purpose
Define a device-agnostic tactile rendering pipeline for JJ Flex supporting DotPad and future tactile devices.

Signal Data → Feature Extraction → Logical Frame Buffer → Layout Engine → Device Adapter → Hardware

Core Components:
- WaterfallBuffer
- TactileFrameBuffer (60x40 logical grid)
- LayoutEngine
- DeviceAdapter (DotPadAdapter, BrailleDisplayAdapter)

Update cadence: ~500ms tactile refresh (configurable)
Cursor state independent of scroll state.

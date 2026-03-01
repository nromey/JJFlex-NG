# Audio Pipeline Architecture

Radio Audio → Processing → Mixer → Output
Signal Data → Sonification → Mixer → Output
Audio Capture → Rolling Buffer → Replay

Includes:
- Live radio stream
- Peak sprite sonification
- Rolling recording buffer
- Say Again replay mode

Configurable mix levels and ducking.
Non-blocking architecture required.

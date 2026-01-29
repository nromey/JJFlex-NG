# x64 Native Libraries

This folder needs x64 versions of:
- `libopus.dll` - Build from https://opus-codec.org/ or download prebuilt
- `portaudio.dll` - Build from https://www.portaudio.com/ or download prebuilt

## Building from source

### Opus (libopus.dll)
```bash
git clone https://gitlab.xiph.org/xiph/opus.git
cd opus
mkdir build-x64 && cd build-x64
cmake .. -A x64 -DCMAKE_BUILD_TYPE=Release
cmake --build . --config Release
# Copy Release/opus.dll to this folder as libopus.dll
```

### PortAudio (portaudio.dll)
```bash
git clone https://github.com/PortAudio/portaudio.git
cd portaudio
mkdir build-x64 && cd build-x64
cmake .. -A x64 -DCMAKE_BUILD_TYPE=Release
cmake --build . --config Release
# Copy Release/portaudio.dll to this folder
```

## Prebuilt binaries
- Opus: https://opus-codec.org/downloads/
- PortAudio: Often bundled with audio software or available via vcpkg

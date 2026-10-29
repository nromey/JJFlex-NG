---
keywords:
  - DSP
  - receive filters
  - filter width
  - noise blanker
  - noise reduction
  - auto notch
  - audio peak filter
  - NR Filter
---

# Filters and DSP

Receive filters and digital signal processing (DSP) help separate a station from nearby signals and noise. In JJ Flexible Radio Access, Home's **Receiver** group shows the receive filter's edges and width. The **Noise Reduction and DSP** group holds the noise-processing controls. Use the filter layer to adjust the passband and the noise layer to switch processing on or off.

## Choosing a Receive Filter

A receive filter passes a range of frequencies and rejects signals outside that range. A narrower filter can help separate a CW station from a crowded band. Voice needs a wider passband to remain intelligible. The low and high edges set the limits; the difference between them is the filter width.

Home's **Receiver** group gives a read-only report of the current receive filter. To change it, press `Ctrl+J`, then `F` to open the filter layer. It starts with the receive filter of the selected slice.

- Hold `Left Shift` and press `Left` or `Right` to lower or raise the low edge.
- Hold `Right Shift` and press `Left` or `Right` to lower or raise the high edge.
- Press `Left` or `Right` without a modifier to slide the whole filter down or up while keeping its width. `Up` and `Down` also slide it up and down.
- Press `Ctrl+Up` to widen the filter or `Ctrl+Down` to narrow it about its centre.
- Press `S` to hear both edges and the width. Hold `Left Shift` with `S` to hear only the low edge, or `Right Shift` with `S` to hear only the high edge.
- Press `Home` or `End` to move the selected edge to its lowest or highest limit. Without a modifier, these keys move the whole filter; with `Ctrl`, they set the minimum or maximum width.
- Press `T` to work on the transmit (TX) filter, or `R` to return to the receive filter.
- Press `Enter` to keep the changes and close the layer. Press `Escape` to restore the filters changed during this visit and close the layer.

The adjustment step follows the filter width: narrow filters use smaller steps. The permitted edges depend on the operating mode. To hear the receive filter values without opening the layer, press `Ctrl+Alt+F`.

For the general layer controls, see [JJ Key Commands](JJKey.md). For saved filter settings, see [Filter Presets](FilterPresets.md). Transmit filtering is covered in [TX Audio Sculpting](TxSculpting.md).

## Choosing Noise Processing

Different tools address different kinds of interference. Use a blanker for sharp pulses, noise reduction for background noise, or an automatic notch for a steady carrier. Compare the result with the feature off: removing more noise is useful only while the wanted signal remains readable.

To use the switches described in the following sections, press `Ctrl+J`, then `N` to open the noise layer. The letter given for each feature is pressed inside that layer. Press `H` there for its command list. Press `Enter` to keep changes or `Escape` to restore the switches changed during this visit.

### Noise Reduction

The radio's standard noise reduction (NR) reduces background hiss on received signals. Press `N` in the noise layer to switch it on or off.

Home and the Slice DSP menu also offer **Legacy NR**, a separate radio control. Home's **NR Level** control appears when Legacy NR is on; adjust it to compare noise reduction with the clarity of the station.

### Noise Blanker

The noise blanker (NB) suppresses impulse noise such as power-line crackle, electric-fence pulses, and sharp clicks. Press `B` in the noise layer to switch it on or off. Home's **NB Level** control appears when the blanker is on.

### Wideband Noise Blanker

The wideband noise blanker (WNB) works across the whole passband. It offers another way to address pulse noise alongside the standard blanker. Press `W` in the noise layer to switch it on or off. Home's **WNB Level** control appears when it is on.

### On-Radio Neural Noise Reduction

Neural noise reduction uses machine learning to separate speech from noise. **On-Radio Neural NR** runs in the radio and requires supporting hardware, firmware and a suitable licence. Press `R` in the noise layer to switch it on or off.

### On-Radio Spectral Noise Reduction

Spectral noise reduction works on the frequency content of received audio to reduce steady background noise, such as hum. **On-Radio Spectral NR** runs in the radio and has its own hardware, firmware and licence requirements. Press `S` in the noise layer to switch it on or off.

### NR Filter

**NR Filter** is another on-radio noise-reduction option, available only with supporting hardware, firmware and a suitable licence. Press `Ctrl+N` in the noise layer to switch it on or off.

### Auto Notch Filter

The auto notch filter (ANF) finds and suppresses steady carriers and tones, such as a heterodyne over a voice contact. Press `A` in the noise layer to switch the automatic notch on or off. This controls **FFT Auto-Notch**, which uses a fast Fourier transform (FFT); the separate **Legacy Auto-Notch** option is available in Home and the Slice DSP menu.

### Audio Peak Filter

The audio peak filter (APF) emphasises the CW tone to help pick out a station. Press `P` in the noise layer to switch it on or off. This feature is for CW; in another mode, the layer reports "Audio Peak Filter is CW only" instead of enabling it.

### Noise Reduction on the Computer

The noise layer also offers PC neural noise reduction with `Ctrl+R` and PC spectral noise reduction with `Ctrl+S`. For setup, noise-profile capture and management, see [PC-Side Noise Reduction](PcNoiseReduction.md).

## Using Home or the Slice DSP Menu

Open Home's **Noise Reduction and DSP** group to read or change the processing controls. The on-radio switches apply to the selected slice. The PC noise-reduction controls apply to audio processed on this computer.

The **Slice** menu's **DSP** submenu offers the same feature families:

- **Noise Reduction** contains the on-radio neural and spectral switches, **NR Filter**, **Legacy NR**, and commands to raise or lower the legacy NR level.
- **Noise Blankers** contains the two blanker switches and their level adjustments.
- **Auto Notch** contains **FFT Auto-Notch** and **Legacy Auto-Notch**.
- **Audio Peak Filter** switches the CW peak filter on or off.
- **PC Noise Reduction** contains the PC switches, **Capture Noise Profile**, **Noise Profiles...**, and **Open Noise Profiles Folder**.

## When a Feature Is Unavailable

Some on-radio features need particular hardware, firmware or a licence. On-radio neural NR, spectral NR and NR Filter are also unavailable in CW and FM modes. The advanced noise-reduction menu items remain present when unavailable, with wording that marks their state. Select one to hear why it cannot be used. The corresponding noise-layer key also explains the refusal.

For a fuller account of the connected radio's capabilities, open the **Tools** menu and choose **Feature Availability**.

## Related Topics

- [JJ Key Commands](JJKey.md)
- [PC-Side Noise Reduction](PcNoiseReduction.md)
- [Filter Presets](FilterPresets.md)
- [TX Audio Sculpting](TxSculpting.md)
- [JJ Flexible Home](JJFlexibleHome.md)

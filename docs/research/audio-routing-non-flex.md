# Audio Routing for Non-Flex Radios

**Status:** Multi-radio Phase 6 deliverable
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Noel — input to architecture synthesis (Phase 9)
**Builds on:** Phase 4 (`IRadioBackend`); Phase 5 (per-radio config); Hamlib API survey section 5

---

## What this document is

Hamlib does CAT only. Audio for non-Flex radios is JJF's problem. Today's
JJFlex audio pipeline (NAudio + PortAudio + Opus) was built for FlexRadio's
network audio model — VITA-49 packets streamed over UDP, multiple audio streams
per slice, DAX virtual audio. None of that applies to a TS-2000 connected via
COM port and a SignaLink soundcard plugged into the radio's data jack.

This document proposes how JJ Flexible routes audio for non-Flex radios — what
the user sees in setup, what the architecture exposes, and how the existing
pipeline integrates the new audio sources.

---

## 1. The audio-physics primer

Each non-Flex radio class has different audio plumbing:

### 1.1 HF transceivers with USB audio CODEC (TS-590SG, IC-7300, FT-991A)

The radio enumerates as a USB audio device when plugged in. Windows sees:

- "USB Audio CODEC (TS-590SG)" or similar as a recording device — radio's RX audio
- "USB Audio CODEC (TS-590SG)" as a playback device — radio's TX audio

The audio is digital, native, no soundcard between PC and radio. Quality is
high (typically 48 kHz / 16-bit). This is the friendliest case.

### 1.2 HF transceivers with analog audio jacks (TS-590S non-G, older Yaesus)

External soundcard interface required:
- SignaLink USB
- RIGblaster
- DigiRig
- Tigertronics SLM-7

These are USB audio devices that bridge the radio's analog audio to the PC.
Configuration: the user picks which USB device represents "this radio's audio
in" and "this radio's audio out."

### 1.3 VHF/UHF FM mobiles (TM-V71A, FTM-400)

Same as analog HF but with one additional wrinkle: the data port uses
unsquelched audio for packet/APRS. The user routes data-port audio to a
soundcard for digital modes (packet, FT8 over FM) but speaker audio (the
listening output) goes through the radio's own speaker (or sometimes through
a Y-splitter to PC speakers + radio).

### 1.4 SDR-architected receivers (RFspace, future SDR-only radios)

Raw I/Q over USB or network. JJF would need DSP at the JJF side. Out of
scope for the first multi-radio sprint; reasonable to plan that
`IRadioBackend` exposes `IIqStream` for these radios, JJF DSP processes I/Q
into demodulated audio.

### 1.5 HTs

Mostly speaker-direct. CAT-side audio is rare. For HTs that have data jacks
(TH-D74), same model as HF analog — soundcard interface required.

---

## 2. The audio routing abstraction

Per Phase 4 section 10.1, audio is its own concern. The proposed interface:

```csharp
public interface IAudioRouter
{
    // Discovery
    IReadOnlyList<AudioDevice> EnumerateInputs();    // Windows recording devices
    IReadOnlyList<AudioDevice> EnumerateOutputs();   // Windows playback devices

    // Per-radio routing
    Task<AudioRouteResult> SetRadioInputAsync(string radioId, AudioDevice device);
    Task<AudioRouteResult> SetRadioOutputAsync(string radioId, AudioDevice device);
    Task<AudioRouteResult> SetRadioDataPortInputAsync(string radioId, AudioDevice device);  // optional
    Task<AudioRouteResult> SetRadioDataPortOutputAsync(string radioId, AudioDevice device); // optional

    AudioRoute GetRoute(string radioId);
    event EventHandler<AudioRouteChangedEventArgs> RouteChanged;

    // Master output
    AudioDevice MasterListenDevice { get; set; }     // user's main listening device (speakers/headset)
}

public sealed class AudioDevice
{
    public string Id { get; init; }                  // stable Windows endpoint id
    public string Name { get; init; }                // user-friendly name
    public AudioDeviceKind Kind { get; init; }       // Input | Output
    public AudioDeviceCategory Category { get; init; } // BuiltIn | Usb | Bluetooth | Other
    public bool IsDefault { get; init; }             // Windows default at enumeration time
    public string DriverInfo { get; init; }          // e.g. "USB Audio CODEC"
}

public sealed class AudioRoute
{
    public AudioDevice RxDevice { get; init; }       // radio audio coming in
    public AudioDevice TxDevice { get; init; }       // radio audio going out
    public AudioDevice DataPortRxDevice { get; init; }  // null when not used
    public AudioDevice DataPortTxDevice { get; init; }  // null when not used
}
```

### 2.1 Where audio routing lives in code

Not in `IRadioBackend`. Audio routing is orthogonal — the same TS-590SG might
be driven by a Hamlib backend with audio routed to a SignaLink, while another
TS-590SG with USB audio uses its built-in CODEC. The `IAudioRouter` is one
service used by orchestration code that ties radio backends to audio devices
based on per-radio config.

### 2.2 Where audio routing lives on disk

Per Phase 5 directory layout, per-radio audio config goes in
`%AppData%\JJFlexRadio\radios\<radio-id>\audio.json`:

```json
{
  "rxDevice": {
    "id": "{0.0.1.00000000}.{a1b2c3d4-...}",
    "name": "Microphone (USB Audio CODEC)",
    "category": "Usb"
  },
  "txDevice": {
    "id": "{0.0.0.00000000}.{e5f6...}",
    "name": "Speakers (USB Audio CODEC)",
    "category": "Usb"
  },
  "dataPortRxDevice": null,
  "dataPortTxDevice": null,
  "schemaVersion": 1
}
```

The `id` is the stable Windows endpoint ID; the `name` is for display and
helps reconnect by name if the ID changes (e.g., user replaces their
SignaLink with a new one).

---

## 3. The setup UX (must be accessible)

Per `feedback_accessibility_is_end_to_end.md`, audio device pick must be fully
accessible. No "drag this slider" or "find the right line in the mixer" —
straightforward dialog with screen-reader-friendly device list.

### 3.1 The "Configure Audio for This Radio" dialog

Triggered when:
- User adds a new non-Flex radio (after picking model, before connecting)
- User opens Settings → Radios → [radio nickname] → Audio
- An existing radio loses its previously-selected device (device unplugged)

Dialog content:

```
Audio for: TS-2000 Shack
Status: Select audio devices below before connecting

Receive (radio audio coming in to PC):
[ ▼ Microphone (USB Audio CODEC)  ]
   Test Receive: [ Listen for 5 seconds ]

Transmit (PC audio going out to radio):
[ ▼ Speakers (USB Audio CODEC)    ]
   Test Transmit: [ Send 1 kHz tone for 1 second ]

Optional data port (for packet, APRS, FT8):
   [ ☐ This radio uses a data port ]
   Data port receive: [ ▼ ... ]
   Data port transmit: [ ▼ ... ]

[ Save and Connect ]    [ Cancel ]
```

Screen-reader friendliness:

- The combo boxes are standard WinForms `ComboBox` (full UIA support)
- Each combo box has `AccessibleName` set to "Receive device for radio name"
- The "Test Receive" button starts a 5-second listening session that announces
  "Audio is reaching this device" if signal detected on the input, or "No
  audio detected" if silent
- The "Test Transmit" button (1-second 1 kHz tone) confirms audio is going
  out — useful for verifying the radio's data jack is wired to the PC's
  expected port
- All controls keyboard-accessible; no mouse required
- The dialog state is fully described in JJF's screen reader output

The "Test" buttons matter a lot for accessibility — sighted users rely on the
Windows mixer's signal-level meter to verify "audio is flowing." Blind users
need an audible "yes audio is flowing" or "no audio detected" feedback. JJF
provides this directly.

### 3.2 The "device unavailable" path

If a configured device disappears (USB unplugged), JJF detects on next radio
connect attempt and prompts:

> "The device you previously chose for receiving audio from TS-2000 Shack is
> not available. Would you like to choose a different device?"

User picks a new device from the dialog or cancels (radio remains
disconnected). The previous device choice is remembered; if it reappears
later, JJF prompts again to switch back.

Per flexibility principle: don't auto-switch silently when devices reappear;
give the user the choice.

### 3.3 Audio device discovery

Use Windows Core Audio API (`MMDeviceEnumerator`) for enumeration. Existing
JJF dependency on PortAudio is fine, but PortAudio's device list often
duplicates devices across host APIs (WASAPI / DirectSound / WDM-KS); the
Windows Core Audio enumeration is cleaner.

For each device, surface:
- Endpoint ID (stable across reboots if the device stays plugged in)
- Friendly name from Windows
- Whether it's the system default
- Category (built-in motherboard audio, USB, Bluetooth)

The `Category` lets us deprioritize Bluetooth devices (high latency,
unreliable for SSB / CW) — show them in the list but tag with "(Bluetooth —
high latency)."

---

## 4. Integration with the existing JJF audio pipeline

### 4.1 Today's pipeline (Flex)

```
FlexLib (VITA-49 UDP receive)
  → AudioStream (FlexLib's audio)
  → RxAudioPipeline (Sprint 25 Phase 20: NR + spectral sub)
  → PostDecodeProcessor delegates
  → NAudio output device (user's chosen listening device)
```

The pipeline is event-driven — each VITA-49 packet's payload flows through.

### 4.2 New pipeline for non-Flex radios

```
PortAudio / NAudio input device (radio's audio source)
  → SoundcardSource (new — wraps PortAudio's input stream)
  → RxAudioPipeline (same NR + spectral sub stage, reused)
  → PostDecodeProcessor delegates (same)
  → NAudio output device (same listening device)
```

The middle and end of the pipeline don't change. The source-side abstraction
gets a new implementation:

```csharp
public interface IAudioSource : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    void Start();
    void Stop();
    event EventHandler<AudioFrameEventArgs> FrameAvailable;
}

public class FlexNetworkAudioSource : IAudioSource { /* existing */ }
public class SoundcardAudioSource : IAudioSource    { /* new — wraps PortAudio capture */ }
```

The pipeline accepts an `IAudioSource`; orchestration code picks the right
implementation per radio backend. Flex radios get `FlexNetworkAudioSource`
(driven by FlexLibBackend's audio events). Non-Flex radios get
`SoundcardAudioSource` reading from the user's chosen input device.

### 4.3 NR / spectral sub on non-Flex audio

Sprint 25 Phase 20 gave Flex 6000-series radios PC-side neural noise
reduction (one of the unique-to-JJF capabilities listed in
`project_strategic_identity.md`). Reusing the same NR pipeline on non-Flex
radio audio is the natural expansion — once any radio's RX audio reaches
`RxAudioPipeline`, NR + spectral sub work on it regardless of source.

This is a real win: **Mark's TS-590 gets PC-side NR, the same as a 6300
does, even though the TS-590's hardware-side NR is much weaker than the
8000 series.** The friction-tax principle in action: Mark gets the same
quality JJF accessibility that Don gets.

### 4.4 TX path for non-Flex radios

```
JJF audio source (mic input chosen by user)
  → CW keyer text-to-tones (when sending CW from text)
  → TX processing (compression, EQ — TBD per radio capability)
  → NAudio playback to user's chosen TX device (radio's TX audio in)
```

PTT happens via CAT (Hamlib `rig_set_ptt`). The audio stream to the radio is
continuous when PTT is on; muted when PTT is off.

Latency considerations: the lag from user pressing PTT to audio reaching
radio matters for SSB. Target <100 ms total round-trip; achievable with
WASAPI shared mode at 10ms buffer. Document this for the test matrix.

### 4.5 Data port audio (optional second pair)

Some radios have a data port distinct from the front-mic / speaker path.
TM-V71A's data port carries unsquelched audio for packet. TS-2000 has a
"DATA" connector at the back. Users running JJF for digital modes route
data port to a different soundcard than the speaker.

The interface allows two independent device pairs per radio (regular
RX/TX + data port RX/TX). Most users won't use the data port path; it's
optional.

---

## 5. The "Flex 8600 as panadapter source for an IC-7300" vision

`project_strategic_identity.md` lists "cross-radio audio routing" as a
differentiator: use a Flex 8600 as a panadapter source for an IC-7300, route
SDR audio to non-SDR radios via sound cards within the audio pipeline.

This is forward-looking — the first multi-radio sprint shouldn't try to ship
this. But the architecture should not preclude it. Concretely:

- `IAudioRouter` exposes "wire device A to device B" beyond just per-radio
  routing
- The audio pipeline can have multiple sources active at once; each source
  has a "destination" (speaker / radio's TX input / both)
- Cross-radio routing UI (Sprint 30+ scope) lets the user say "FLEX-8600's
  RX audio → IC-7300's accessory port for use as panadapter audio reference"

For now, capture this as "the architecture must allow source-to-destination
routing beyond just radio-to-speaker." Don't ship it.

---

## 6. The user's master output device

Where does the user actually listen?

- One option: per-radio output device (each radio has its own listening
  device — radio A in headphones, radio B in speakers)
- Another option: master listening device (one device for all radios; mixer
  combines their audio streams)
- Hybrid: master device is the default, per-radio override is allowed

Recommendation: **master device with per-radio override.** Most operators
run one radio at a time and use one listening device. Multi-radio operators
will have the override. Per-radio config holds the override; user-scope
config holds the master default.

This integrates with `project_multi_radio_tabbed_architecture.md` (when JJF
gains tabbed multi-radio UI): the active tab's radio plays through the
listening device; background tabs are muted unless explicitly mixed in.

---

## 7. The "no Flex" path — JJF as a Hamlib-only client

Some users may be inheriting from JJ Radio's user base and have no Flex at
all. JJ Flexible should be fully usable for them. This means:

- The audio pipeline must work without FlexLib's network audio path
- Settings UI must guide users through soundcard config for their radio
- The "Add a Radio" workflow must work with zero Flex radios on the network
- No Flex-specific UI is rendered when no Flex radios are configured

This is the ring-vision "one client to rule them all" test — JJF
should serve a TS-590-only operator just as well as a Flex-only operator,
without making either feel like a second-class citizen.

The audio-routing UX above is the entire surface that exists for that
operator. Get it right, and they have parity.

---

## 8. Per-radio audio config — file shapes

Per Phase 5 directory layout, per-radio audio in
`%AppData%\JJFlexRadio\radios\<radio-id>\audio.json`. Schema:

```json
{
  "rxDevice": {
    "id": "{0.0.1.00000000}.{a1b2c3d4-c5e6-f7g8-h9i0-j1k2l3m4n5o6}",
    "name": "Microphone (USB Audio CODEC)",
    "category": "Usb"
  },
  "txDevice": {
    "id": "{0.0.0.00000000}.{a1b2c3d4-...}",
    "name": "Speakers (USB Audio CODEC)",
    "category": "Usb"
  },
  "dataPortRxDevice": null,
  "dataPortTxDevice": null,
  "txMode": "PttGated",
  "rxAudioToMaster": true,
  "rxAudioToOverrideDevice": null,
  "schemaVersion": 1
}
```

`txMode` options:
- `PttGated` — send audio only when PTT is on
- `Always` — send audio regardless (for "always-on" ALC monitoring)
- `Disabled` — never send audio (use radio's own mic, JJF only does CAT)

`rxAudioToMaster` / `rxAudioToOverrideDevice` — controls where THIS radio's
RX audio plays. Default true (master device); override per-radio if the user
wants a specific listening device for this radio.

---

## 9. Implementation sequencing within multi-radio sprints

Within the multi-radio implementation arc, audio is its own track. Estimated
sprint distribution:

**Sprint N (multi-radio kickoff)**: introduce `IRadioBackend` and refactor
FlexLib code behind it. No audio-pipeline changes. Existing Flex audio works
as-is.

**Sprint N+1**: introduce `HamlibBackend` (CAT only — no audio yet).
Connection works; users hear nothing because audio routing isn't built yet.
This is a "skeleton" sprint; non-Flex testers can confirm CAT works.

**Sprint N+2 (audio routing)**: introduce `IAudioRouter`, the audio device
discovery + setup dialog, and `SoundcardAudioSource`. Wire the JJF audio
pipeline to consume `SoundcardAudioSource` for non-Flex radios. Mark's TS-590
becomes "audibly working."

**Sprint N+3 (test + iterate)**: Doug's TM-V71A, Noel's TS-2000 testing.
Iterate on audio device UX based on real-tester feedback.

**Sprint N+4+ (polish + cross-radio routing)**: cross-radio routing UI,
master/override device handling polish, latency tuning per radio class.

This serializes to about 3-4 sprints AFTER the multi-radio CAT work lands.
The full multi-radio arc (CAT first, then audio, then polish) is plausibly
5-6 sprints — multi-quarter scope, not a single sprint.

---

## 10. Open design questions

1. **NAudio vs PortAudio for the new soundcard path.** JJF already uses both.
   PortAudio is wrapped in JJPortaudio; NAudio is used elsewhere. Which is
   the right driver for the soundcard input/output? Recommendation: prefer
   PortAudio for consistency with existing JJF code; fall back to NAudio if
   PortAudio's WASAPI host API has issues with specific devices. Both are
   .NET-friendly; the choice doesn't affect API surface above the driver.

2. **Latency budget per radio class.** Different operating modes have
   different acceptable latency. CW: <50 ms one-way. SSB: <100 ms. FM
   mobile: <200 ms acceptable. Per-radio config can hold a "latency
   preference" override; the audio pipeline picks WASAPI shared (low) vs
   exclusive (lower) modes accordingly.

3. **Audio level adjustment.** Some radios have hardware-side audio level
   controls; some don't. JJF's audio pipeline has its own gain stage.
   Should the user adjust radio-side or JJF-side levels? Recommendation:
   prefer radio-side (avoid double-AGC fighting); document this in the audio
   setup dialog ("Use the radio's AF gain to set listening level; this
   setting is for fine-tuning JJF's mix").

4. **CW key-side-tone for non-Flex radios.** Flex radios send CW via the
   radio's hardware keyer (CWX) and the radio handles sidetone. Non-Flex
   radios via Hamlib's `rig_send_morse` similarly. But for radios where
   `rig_send_morse` isn't supported, JJF could generate sidetone PC-side
   and route to the radio's mic input as audio CW. This is degenerate-but-
   possible; first sprint says "use radio's hardware keyer when available;
   capability flag `HardwareCwKeyer` gates."

5. **What about radios where audio comes through CAT (rare)?** Some VHF/UHF
   programming-cable setups carry audio over the CAT line. Hamlib doesn't
   abstract this. Treat as out of scope — those radios route through external
   soundcard interfaces in practice.

6. **Device hot-plug handling.** What if the user plugs in a USB device after
   JJF starts? Recommendation: re-enumerate on `WM_DEVICECHANGE` Windows
   message, raise `IAudioRouter.RouteChanged` if the new device matches a
   previously-saved-but-missing endpoint ID, prompt user to confirm.

7. **Bluetooth devices in the picker — show or hide?** Recommendation: show
   with "(Bluetooth — high latency)" tag. User decides if it's good enough.
   Don't hide options the user might want.

---

## 11. Forward references

This document feeds:

- **Phase 7 (TS-2000 conformance scope)** — audio routing is part of the
  conformance test
- **Phase 8 (Tester onboarding)** — audio setup is the most complex
  onboarding step for non-Flex testers
- **Phase 9 (Architecture synthesis)** — audio routing as a sibling concern
  to `IRadioBackend`

---

## Document Status

**Phase 6 of 10 complete.** `IAudioRouter` interface, accessible setup
dialog, integration with existing JJF audio pipeline, sequencing within
multi-radio arc. Mark's TS-590 gets the same JJF audio pipeline benefits
(NR + spectral sub) as Don's 6300 once this lands.

**Estimated read time:** 15-18 minutes for full review; 8 minutes for
sections 2, 3, and 4 alone.

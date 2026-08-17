# The voice type and meter model — published early for D3 and H

**Track D2, 2026-08-16.** This is the shape other tracks build against. The
synthesis behind it may still be moving; these types are the contract and are
stable from this commit forward. Everything lives in `JJFlexWpf`:

- `JJFlexWpf/MeterVoice.cs` — `MeterVoice`, `MeterVoiceLibrary`
- `JJFlexWpf/MeterModel.cs` — `MeterDefinition`, `MeterSourceRef`,
  `MeterRange`, `MeterSourceKind`, `MeterUnits`, `MeterActivation`,
  `LegacyMeterCatalog`

## The one-sentence versions

- **A voice is a named, serialisable parameter set** — partial amplitudes,
  brightness tilt, inharmonicity, tremolo/vibrato rate and depth, gate
  pattern, pitch alternation, attack/decay/sustain, filtered-noise mix. Not an
  enum, no switch behind it: the renderer plays whatever the numbers say.
- **A meter is a SOURCE plus a RANGE plus a VOICE**, with its own audibility,
  readability, volume, pan and pitch range. Two meters may share a source.
  The range is in the source's own units.
- **Grammar:** timbre identifies the meter, pitch carries its value, pan
  enhances but is never load-bearing.

## MeterVoice — what D3 and H need to know

**Identity is `Name`.** Meters reference voices by name via
`MeterDefinition.VoiceName`, resolved through `MeterVoiceLibrary.Resolve(name)`
(never null — falls back to `"Pure"`).

**Built-ins are code data, not config.** `MeterVoiceLibrary.BuiltIns` ships
the alphabet (Pure, Hollow, Reedy, Organ, Bell, Trill, Raspy, Thin, Square,
Breath, Ring, Two-Tone, Swell, Pulsing, Urgent). They are never persisted, so
they can improve between versions. User voices persist in
`AudioOutputConfig.UserVoices` and are loaded via
`MeterVoiceLibrary.SetUserVoices(...)` at config-apply time.

**User voices may not shadow built-in names.** `SaveUserVoice` suffixes a
colliding name (" 2") and returns the name actually stored. This is the
never-silently-rewrite-a-shared-vocabulary rule enforced at the API.

**The live-tweak model (D3's `T` mode) is already decided and modelled:**

- A live tweak NEVER edits the referenced voice. It clones it into
  `MeterDefinition.VoiceOverride` (use `voice.Clone()`), adjusts the clone
  live, and the audio follows immediately.
- On leaving tweak mode, one prompt: **keep as a copy** (call
  `MeterVoiceLibrary.SaveUserVoice(override)`, set `VoiceName` to the returned
  name, null the override), **replace the original** (only meaningful for user
  voices — update the user voice in place via `SaveUserVoice`; built-ins can
  never be replaced, offer keep-as-copy instead), or **discard** (null the
  override).
- The three live-tweak axes map to: `Brightness` (-1..+1 spectral tilt — the
  "does it cut through" lever), `TremoloRateHz`, `TremoloDepth`.

**Thread-safety contract:** scalar properties of a playing voice may be
mutated live (the renderer re-reads every buffer, float writes are atomic).
The `Partials` array must be REPLACED wholesale, never edited element-wise.

## What H needs — earcons as voices

H's toggle ding pair should be two voices (or one voice played at two
pitches — recommendation: one voice, rising pitch pair for on, falling for
off, so the two are obvious siblings by construction). For one-shot playback
use:

```csharp
float[] samples = VoicedToneSampleProvider.RenderMono(
    MeterVoiceLibrary.Resolve("Bell"), frequencyHz: 880, durationMs: 120,
    volume: 0.5f);
```

then hand the buffer to the EarconPlayer's cached-sound path. (RenderMono
lands with the synthesis commit; the signature is fixed as above.) Gated,
plucky voices (Bell, Urgent) make natural earcons; the envelope retriggers
per gate cycle so a 120 ms render of a plucky voice is one clean strike.

## MeterDefinition — what D3 needs to know

- `Id` — stable GUID string. Leader-layer numbering and any muscle-memory
  mapping keys on `Id`, never on list index.
- `Name` — operator-visible, freely editable ("SWR fine").
- `Source` — a `MeterSourceRef`: `Kind` (RadioReported / PcDerived /
  FrequencyDomain / Derived) + `Key` (+ `SecondaryKey` for derived deltas,
  `FrequencyHz`/`SpanHz` for probes, `SliceIndex` for per-slice sources).
  Until Track B's real meter-list accessor lands, only the eight legacy keys
  in `LegacyMeterCatalog` produce live values; everything else is
  representable but dormant — build the picker against the model, not against
  the eight.
- `Range` — `Low`/`High` **in the source's own units** plus `Units` and the
  source-stated `UnitsLabel`. `Range.Normalize(raw)` maps a raw value to
  0..1; `PitchForValue(raw)` gives the tone frequency. Narrowing the range is
  how the S5-to-S9+60 resolution trick works; two meters sharing a source
  with different ranges is the coarse/fine SWR pair.
- `VoiceName` + `VoiceOverride` — see above. `EffectiveVoice()` is what
  actually renders.
- `Enabled` (audible) and `Readable` are independent properties of the same
  meter — one list, not two systems.
- `Activation` — Always / ReceiveOnly / TransmitOnly; data, not hardcoded.
- Defaults: meters ship `Enabled = false` at full source range. Nothing
  sounds until asked; nothing is hidden by a narrowed scale nobody chose.

Rows should speak state from these fields: "SWR, enabled, Bell, centre" is
`Name`, `Enabled`, `VoiceName` (or "customised" when `VoiceOverride` is
set — say so, it is the only visible difference), pan bucket.

## Engine surface (post-rework)

`MeterToneEngine.Slots` remains the runtime list; each slot now wraps a
`MeterDefinition` (`slot.Definition`) plus a `VoicedToneSampleProvider`
(`slot.ToneProvider`, stereo, live `Pan`/`Frequency`/`Volume`/`Active`, and a
`Voice` reference you can swap live). Legacy bridges kept so existing callers
compile: `slot.Source` (legacy `MeterSource` enum), `slot.Waveform` (maps to
the equivalent voice via `MeterVoiceLibrary.FromLegacyWaveform`). Pan changes
now take effect live — under the old registration model they silently never
did.

`ApplyPreset` still exists and still replaces the working set (unchanged
semantics); presets are now built from definitions referencing built-in
voices. `LoadDefinitions(list)` / `ExportDefinitions()` round-trip the meter
list through `AudioOutputConfig.Meters`.

## Persistence (additive on AudioOutputConfig, coordinated with Track F)

- `AudioOutputConfig.UserVoices : List<MeterVoice>` — user-authored voices.
- `AudioOutputConfig.Meters : List<MeterDefinition>` — the one meter list.
  Empty list = never configured; the engine seeds from the preset. Both are
  plain XML-serialisable POCOs; a JSON sharing pack later is a serializer
  choice, not a remodel.
- The old `MeterSlotConfig`/`MeterSlots` property was written by NOTHING —
  verified by grep — so there is no data to migrate. Left in place untouched
  so F's restructuring meets no surprise.

## Perceptual honesty carried into the design

The built-in alphabet separates identities on at least two axes each
(spectrum, modulation rate, attack/pattern). The modulation-rate ladder is
0 / ~5 / ~18 / 28 / 65 Hz plus slow gating — every step ≥ 50% apart, far past
the JND. This is an alphabet of ~15, but the honest ceiling on SIMULTANEOUS
tracking is much lower than the ceiling on identification — D2's empirical
findings on that are reported separately (see the track report; the demo
renders live in the bench artifacts, built for ear verification).

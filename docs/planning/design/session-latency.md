# Session Latency — Measurement Service Design

**Status:** Design doc; implementation lands in Sprint 26 or 27 depending on sequencing of on-air CW and multi-radio work.
**Owner:** Noel / Claude
**Created:** 2026-04-15

---

## Problem

JJ Flex has **one shared latency signal** but **five independent features** that need it. Without a unified measurement service, each feature will roll its own probe, waste bandwidth, and disagree about whose definition of "latency" is canonical. We need one source of truth.

The five consumers:

1. **CW audio keyer** (on-air) — compensate the PTT-tail / VOX delay so the last element's audio clears the radio before unkey. See `cw-keying-design.md`.
2. **Multi-radio audio mixer** — per-stream delay alignment so three concurrent radios' audio arrives time-aligned at the operator's ears. Without this, spatial pan gets garbled and the operator can't fuse events across radios.
3. **Session health / reconnect logic** — RTT spike or jitter burst is the earliest signal that a connection is about to fail. Feeds preemptive reconnect and "your connection just got shaky" announcements.
4. **Status UI** — each session's tab/card shows live round-trip and jitter. Tab-through-sessions can announce "6300 inshack, round trip 45 ms, jitter 3." Useful info that users explicitly asked for.
5. **Auto-quality tuning** — RTT above threshold → suggest low-bandwidth mode; a storm of jitter → suggest fewer simultaneous streams or a wired connection.

## Scope

### In

- Per-session RTT + jitter measurement, probe cadence, rolling statistics.
- Public surface on `IWanSessionOwner` (Sprint 26 architecture) — `TimeSpan RoundTrip`, `TimeSpan Jitter`, `DateTime LastProbe`, plus change events.
- Submission to the relevant consumer subsystems (CW keyer, mixer, UI, health logic).
- Human-readable formatting for status announcements ("round trip 52 ms, jitter 3 ms").

### Out

- Network layer latency profiling (we're a radio app, not a diagnostic tool). We measure the radio-to-us round trip, nothing else.
- ISP or routing advice beyond "cable tends to beat Wi-Fi" (platform concern, not ours — see `feedback_dont_duplicate_platform_warnings.md`).

## Measurement mechanism

**Preferred: piggyback on existing keepalives.** FlexLib's `SslClient` has a ping thread (`PingThread`) that fires at a fixed interval. We intercept or shadow it to attach a timestamp and measure the reply time. Zero new traffic, minimum intrusion.

**Alternative: dedicated probe.** Send a small cheap command over the TLS control channel at a configured cadence, await the reply, time the round trip. Use if the existing keepalive turns out not to surface reply timing cleanly. Bandwidth is trivial (a few tens of bytes per sample).

**Cadence.** Default one probe per second. Fast enough for jitter detection within ~5 seconds of a spike; slow enough that traffic is negligible. Pause during active TX to avoid contention with user-driven commands.

**Statistics.** Rolling window of the last 20 samples. Expose:

- `RoundTrip` — **median** of the window. Median (not mean) so a single spike doesn't skew the displayed value.
- `Jitter` — **MAD (median absolute deviation)** of the window, times 1.4826 so it reads as a Gaussian-equivalent standard deviation. More robust than naive standard deviation in the presence of outliers.
- `LastProbe` — wall-clock time of the latest sample.
- `RecentSamples` — the raw window, available for diagnostic/debug logging but not exposed to ordinary UI.

**Outlier handling.** A probe that times out (e.g. 5× median) is recorded as an "outlier" and excluded from rolling stats but counted separately. An elevated outlier rate is itself a signal (reported to session health).

## API shape

```csharp
public interface ISessionLatencyProbe
{
    TimeSpan RoundTrip { get; }      // median over rolling window
    TimeSpan Jitter { get; }         // MAD × 1.4826 ≈ std-dev-equivalent
    DateTime LastProbe { get; }      // UTC
    int OutlierCount { get; }        // timeouts within the window

    event EventHandler<LatencyChangedEventArgs>? Changed;
}

public interface IWanSessionOwner
{
    // ... other Sprint 26 Phase 1 members ...
    ISessionLatencyProbe Latency { get; }
}
```

`Changed` fires when RTT or Jitter has moved by more than a configurable threshold (default ±20%) to avoid event spam. Consumers that want every tick (e.g. a diagnostic graph) read `RecentSamples` on a timer of their own.

## Consumers

### 1. CW audio keyer (on-air)

Reads `RoundTrip` at PTT-engage time. Uses it to:

- Extend the hold-time after the last CW element's audio stops, by `RoundTrip + safety_margin`, before releasing PTT. Safety margin ≈ 50 ms.
- Configure the radio's VOX/breakin tail to match the total delay budget so the radio doesn't drop TX between characters on fast CW.

Reads `Jitter` at PTT-engage time. If jitter is elevated (say > 30 ms), either warn the operator ("network timing variable — consider disabling break-in") or automatically lengthen the hold-time further.

### 2. Multi-radio audio mixer (Sprint 28+)

The session coordinator tracks `max(RoundTrip across all active sessions) = slowestRtt`. Each session's `ISessionAudioSink` is given `PlayoutDelay = slowestRtt - thisRtt` so every session's audio lands at the final mixer at the same wall-clock instant. Updates smoothly (500 ms ramp) when latencies shift to avoid audible glitches.

### 3. Session health / reconnect

`Changed` handler watches for:

- RTT > 2× baseline → warn ("connection slow — may degrade").
- Consecutive outliers (timeouts) → consider preemptive reconnect.
- Jitter > baseline × 3 → indicate instability in the session's status.

Signals are consumed by the Sprint 26 disconnect-diagnostic message dictionary (see `hole-punch-lifeline-ragchew.md`).

### 4. Status UI

Per-session card displays:

> 6300 inshack — round trip 52 ms, jitter 3 ms — connection stable

Screen-reader friendly. Numbers update on `Changed` events. Category ("stable" / "variable" / "degraded") derives from RTT + jitter + outlier count.

### 5. Auto-quality tuning

At session start, sample initial RTT before commiting to high-bandwidth Opus / full panadapter subscriptions. If RTT > 200 ms or initial jitter > 50 ms, default to low-bandwidth mode and note the reason. User can override.

## Implementation plan

Targets Sprint 26 Phase 1 (the `WanSessionOwner` refactor) as the natural home. A small `SessionLatencyProbe` class lives inside `WanSessionOwner`, is updated from the same monitor thread, and is exposed via the `Latency` property on `IWanSessionOwner`.

Probe-timing work: add a timestamp field to the outgoing keepalive and record the round trip when the reply is matched. If FlexLib's SslClient doesn't give us a clean hook, layer a lightweight "ping=<N>" command (FlexRadio's command channel is text-based; an unrecognized command returns an error message with the same N echoed, which is enough to measure timing).

Rolling stats: a fixed-size circular buffer + recompute median/MAD on each sample (cheap at N=20).

### Surfacing

- `IWanSessionOwner.Latency` — always available from Sprint 26 onward.
- Status line formatter: a helper in Radios or JJFlexWpf that turns `(RoundTrip, Jitter, OutlierCount)` into a human-readable string.
- Sprint 27 NetworkTest integration reuses the same probe for diagnostic output ("latency to this radio is 52 ms, your network seems healthy").

## Observations from the field

- **Wired Ethernet** beats Wi-Fi on *jitter* more than on *mean RTT*. Both add roughly the same path latency (the internet hop dominates); wired eliminates the Wi-Fi contention-induced spikes. Status UI should therefore lean on jitter, not just mean.
- **Typical SmartLink RTT** over residential broadband in continental US: 40–80 ms. Anything under 100 ms is fine for CW up to 30 WPM with appropriate hold-time; jitter under 20 ms keeps it feeling clean.
- **Cellular / hotel Wi-Fi** can easily add 200+ ms and spiky jitter. Users on those networks should expect low-bandwidth mode and tolerance for occasional glitches.
- **Symmetric NAT + hole-punching** adds rendezvous-server hops on top of base SmartLink path — assume higher latency than a direct port-forward setup. (Relevant to the future Tier 3 work in `hole-punch-lifeline-ragchew.md`.)

## Living decisions log

- **2026-04-15** — Median + MAD chosen over mean + stdev for robustness against single-sample spikes. Both are O(N log N) for N=20 which is free.
- **2026-04-15** — Rolling window size 20 at 1 Hz chosen as the balance between responsiveness (20 s to fully refresh the stats) and stability (single spikes don't dominate). Tunable if operator feedback says otherwise.
- **2026-04-15** — Per-session (not global) probe, because multi-radio demands per-stream alignment. A global "app latency" number has no well-defined meaning when three radios are connected.
- **2026-04-15** — Piggybacking on keepalives preferred; dedicated probe acceptable if keepalive reply timing can't be extracted cleanly. Either way, traffic overhead is negligible (<1 kbps).

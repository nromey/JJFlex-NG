# SmartLink Direct-Connect Feasibility — Cached WAN IP as Rung 1b

**Status:** source-reading investigation against FlexLib 4.2.18 at `C:\dev\jjflex-flexlib-42\FlexLib_API\`
**Date:** 2026-05-04
**Purpose:** Determine whether `Rung 1b — cached WAN IP` can connect direct to a Flex radio without re-walking the SmartLink Auth0 → server → broker chain on every attempt.

---

## 1. Verdict

**Constrained case, but more permissive than expected: the radio's TLS endpoint is self-authenticating in the cryptographic sense, but it expects a fresh per-attempt `wanConnectionHandle` token issued by the SmartLink server via `radio connect_ready`. The handle is single-use per connect-attempt, not a persistent session ticket.** Cached WAN IP cannot skip the SmartLink server query, but it CAN skip discovery entirely (no `radio list` parse, no UI radio chooser round-trip) and skip NAT-traversal coordination when the radio is reachable on a known forwarded TLS port. Practical speedup: small but real (~ a Discovery/UI round-trip's worth), nowhere near 5–10x.

**Caveat:** the `wan validate handle=` command is sent AFTER the TLS connect succeeds, not as part of the handshake. Code-reading alone cannot prove that the radio terminates the TLS connection if the handle is missing or stale — only firmware-side behavior would. Live testing would resolve this; see Open Questions §5.

---

## 2. Evidence

### 2.1 The TLS handshake to the radio carries no client credentials

`FlexLib_API/FlexLib/TlsCommandCommunication.cs:26`

```csharp
_tlsToRadio = new SslClientTls12(radioIp.ToString(), radioPort.ToString(),
    srcPort, startPingThread: false, validateCert: false);
```

`validateCert: false` means the client doesn't even validate the radio's cert (radios self-sign), and `SslClientTls12.cs:26-261` shows there is no client certificate, no Bearer header, no pre-shared key, no token in the TLS handshake. The handshake is one-way encrypted-channel-only. So the radio learns nothing about who the client is until the first plaintext command arrives over the encrypted channel.

### 2.2 The first authoritative command after connect is `wan validate handle=`

`FlexLib_API/FlexLib/Radio.cs:2196-2214`

```csharp
lock (_connectSyncObj)
{
    if (IsWan)
    {
        if (RequiresHolePunch)
            connected = _commandCommunication.Connect(_ip, NegotiatedHolePunchPort, NegotiatedHolePunchPort);
        else
            connected = _commandCommunication.Connect(_ip, PublicTlsPort);

        if (connected)
            SendCommand("wan validate handle=" + _wanConnectionHandle);
    }
    else
    {
        connected = _commandCommunication.Connect(_ip);
    }
    ...
}
```

The handle is the *only* WAN-specific identification sent to the radio after the TLS handshake. There is no other authentication command in the connect sequence. Whatever `wan validate handle=…` does on the radio side is the entire WAN auth check.

### 2.3 The handle is server-issued per connect-attempt, not derived locally

`FlexLib_API/FlexLib/WanServer.cs:499-509`

```csharp
public void SendConnectMessageToRadio(string radioSerial, int HolePunchPort = 0)
{
    ...
    string command = "application connect serial=" + radioSerial + " hole_punch_port=" + HolePunchPort;
    if (_sslClient != null) _sslClient.Write(command);
}
```

`FlexLib_API/FlexLib/WanServer.cs:394-409`

```csharp
private void ParseRadioConnectReadyMessage(string msg)
{
    ...
    keyValuePairs.TryGetValue("handle", out handle);
    keyValuePairs.TryGetValue("serial", out serial);
    OnWanRadioConnectReady(handle, serial);
}
```

The flow is: client sends `application connect serial=<X> hole_punch_port=<P>` to SmartLink → SmartLink replies asynchronously with `radio connect_ready handle=<H> serial=<X>` → JJ Flex stores `H` on `Radio.WANConnectionHandle` (`Radios/FlexBase.cs:2031`) → Radio.Connect() sends it to the radio after TLS connect.

The handle string is opaque to FlexLib — no parse, no derivation, no retry of an old value. The only producer is the SmartLink server. Without the `application connect` round-trip there is no fresh handle to send.

### 2.4 The radio also doesn't have a separate auth phase — the server "primes" it

There is no FlexLib code path to ask the radio "are you ready to accept TLS connections from me." The `application connect` server message is not just a directory lookup — the server-side response (`radio connect_ready`) implies the SmartLink backend has signaled the radio out-of-band that a client with handle `H` is about to connect. The radio almost certainly only accepts a `wan validate handle=H` command from the next TLS connection if it has been told by the server to expect that handle (otherwise any random TLS client could connect to a public-facing radio and provide arbitrary text).

This is inferred — the firmware source isn't visible — but it is the only design that makes sense given the protocol shape: client TLS is anonymous, the only auth payload is a handle, and that handle has to be opaque-to-client otherwise its security value is zero.

### 2.5 Discovery can be cached cleanly

`FlexLib_API/FlexLib/WanServer.cs:203-388` parses `radio list` messages from the server into `Radio` instances, populating `IP`, `PublicTlsPort`, `RequiresHolePunch`, `IsPortForwardOn`, and so on. JJ Flex consumes the resulting `Radio` via `WanRadioRadioListReceived`. None of these fields are session-keyed or time-sensitive on the *radio* side (the radio doesn't know what its public IP/port-forward state is reported as) — they're purely informational about how to reach the radio. So caching them is safe across sessions, modulo: ISP IP changes, radio reboots that re-register with a new UPnP port, or owner-side port-forward changes.

### 2.6 NAT hole-punch is a separate, optional, server-coordinated phase

`FlexLib_API/FlexLib/WanServer.cs:330-340` shows `requiresHolePunch` is set only when neither port-forward nor UPnP is detected. `Radio.cs:2200-2210` shows the connect path branches on `RequiresHolePunch`: if true, the client uses the `NegotiatedHolePunchPort` (set by SmartLink during the broker phase); if false, the client connects direct to `PublicTlsPort`. The hole-punch path requires SmartLink's coordination; the port-forward / UPnP path does not require coordination at the network layer — *but the connect handle is still required at the protocol layer*.

So for a radio with port-forward or UPnP, the network-level connection IS direct TCP/TLS with no SmartLink involvement *during the TCP/TLS handshake itself*. The SmartLink server's only role for these radios is to issue the per-attempt handle.

---

## 3. The connection sequence (from the client's perspective)

Annotated with which steps a cached WAN IP could skip.

| # | Step | Skippable with cache? | Notes |
|---|------|------------------------|-------|
| 1 | Auth0 → JWT | No (per-session) | Already cached in JJ Flex's account model; not per-attempt cost. |
| 2 | TCP+TLS to `smartlink.flexradio.com:443` | No | Once per session, persistent. |
| 3 | `application register name=… token=<JWT>` | No | Server-side state — the server must associate this client process with the account before it'll broker connects. |
| 4 | Server → `radio list … public_tls_port=… public_ip=… requires_hole_punch=…` | YES | Discovery cache can replace this step entirely, populating `Radio` fields locally from the cache. **This is the meaningful saving.** |
| 5 | Client → `application connect serial=<S> hole_punch_port=<P>` | NO | This generates the per-attempt `wanConnectionHandle`. No cache substitute. |
| 6 | Server → `radio connect_ready handle=<H> serial=<S>` | NO | Required precondition for step 8. |
| 7 | Client TCP+TLS to `radio_public_ip:public_tls_port` | YES (network-direct) | Already direct; no SmartLink mediation in the handshake path. |
| 8 | Client → `wan validate handle=<H>` over the TLS connection | NO | First load-bearing command on the radio side. |
| 9 | `client gui` / `sub …` / `GetVersions()` etc. | n/a | Per-attempt setup, not WAN-specific. |

**What "Rung 1b — cached WAN IP" actually saves:** step 4. That's it. The discovery list parse, plus the UI-level radio chooser round-trip if there is one. Steps 5/6 still have to happen because without the handle the radio almost certainly won't accept the TLS connection.

**What it does NOT save:** the SmartLink session itself (steps 1-3, 5, 6, 8) is still on the critical path.

---

## 4. Implementation implications

Given the constrained verdict, the proposed Rung 1b should be reshaped:

### 4.1 Don't bypass SmartLink — bypass discovery

The cache shouldn't try to skip the SmartLink server query. Instead, it should let the JJ Flex code skip the wait for `WanRadioRadioListReceived` and immediately call `WanSessionOwner.ConnectToRadio(serial)` once the SmartLink session is up. The radio object is reconstructed from cache (matching the fields `WanServer.ParseRadioListMessage` would populate), then pushed straight into the connect path.

Concrete shape:

1. After Auth0 + `application register`, fire `application connect` immediately for the cached serial, in parallel with whatever radio-list refresh would have run.
2. The cached `Radio` object pre-populates `IP`, `PublicTlsPort`, `RequiresHolePunch`, `IsPortForwardOn`, `Version`, etc.
3. When `WanRadioConnectReady(handle, serial)` arrives, the `Radio.WANConnectionHandle` is set and `Radio.Connect()` proceeds normally.
4. If the cached IP is wrong (ISP changed it, radio re-registered with new UPnP port) the TCP connect fails fast → fall through to the next rung (full discovery refresh). Detecting this only needs the standard TCP connect-timeout.

This is "skip discovery wait", not "skip SmartLink." It still produces a measurable speedup — discovery refresh can take 1-3 seconds depending on server health, and on a known-static SmartLink configuration it's pure overhead.

### 4.2 The "best case" (5-10x) is not reachable through the FlexLib protocol

The radio's anonymous TLS endpoint plus opaque handle design means there's no way to construct a valid `wan validate handle=…` value locally. Persistent session tickets aren't part of the protocol surface. The 5-10x best-case scenario would require either a firmware-side change (Flex offering a per-client signed token that survives sessions) or JJ Flex rolling its own auth on top of the radio (impossible — the radio's auth check is firmware-side, we can't replace it).

### 4.3 What JJ Flex calls

The existing call surface is sufficient. No new FlexLib APIs are needed; the wins come from how JJ Flex's session-owner code orchestrates them:

- Keep using `WanServer.SendConnectMessageToRadio(serial, holePunchPort)` and `WanRadioConnectReady` — these are mandatory.
- Reconstitute the `Radio` object from cache before the radio-list response arrives, so step 4 above is the start signal not the wait point.
- Validate cache freshness on a per-radio basis: cache age, last-known firmware version, last-known port. Stale-cache fallback: drop to full discovery (Rung 1a).

### 4.4 Where this lives in the rung chain

Rung 1b should be re-described as: "Cached SmartLink target — skip radio-list refresh wait." The TCP/TLS connect to the radio is direct in any case (steps 7+); the saving is in steps 4 and the surrounding UI roundtrips, not in steps 5-8.

---

## 5. Open questions

These cannot be resolved by code-reading alone.

### 5.1 Is the handle single-use, or is it valid for a handle-lifetime window?

Code shows the handle is generated per `application connect` and there's no obvious refresh mechanism. But whether the radio expires it after one validate, after a server-side TTL, or after a successful TCP disconnect — only firmware-side behavior tells us. Affects whether brief disconnect/reconnect can reuse the same handle.

### 5.2 Does the radio drop a TLS connection that doesn't send `wan validate handle=` within N seconds?

`Radio.Connect` sends the handle immediately after TCP connect (Radio.cs:2213), so the protocol assumes prompt validation. But there's no client-side timeout enforcement of the validate command. If the radio is permissive (e.g. accepts the TLS connection and just refuses commands until handle is sent), then the cached IP path can connect-then-validate in one step. If the radio terminates the TLS within 100ms of no validate arriving, the timing must be tighter.

### 5.3 What happens when SmartLink issues a fresh handle for a radio that has an existing TLS connection from the same client?

i.e. does the radio enforce one-handle-per-TLS-connection, or could a new handle invalidate prior connections? This affects whether reconnect-during-jitter is feasible (Rung 1b's main value-add, which is "reconnect after a transient blip without re-walking the full chain").

### 5.4 Does the radio's `wan validate` accept a stale handle from a prior session?

i.e. does the SmartLink server keep handles in some per-radio/per-client cache, or are they ephemeral? Code shows no persistence on the client side. Likely ephemeral on server-side too. If so, no shortcut exists.

### 5.5 Is `wan validate handle=…` actually enforced, or is it informational?

Worst-case-charitable: maybe the radio accepts any TLS connection from any IP and the handle is purely for telemetry / accounting. This would change the verdict to "best case." Highly unlikely (it would mean the radio is a public open TLS server with no auth), but worth ruling out via probe — connect with a malformed handle and observe behavior.

### 5.6 Resolution path

Live testing (with Don's 6300 and Justin's 8400 as cooperative test radios):

1. Capture a known-good handle from a normal connect.
2. Disconnect TLS cleanly, reconnect TLS to the same `PublicTlsPort` immediately, send the same handle. Does the radio accept?
3. Wait 60s, retry. Does the handle still work?
4. Connect TLS without sending any handle, send other commands. Does the radio respond, or refuse / drop?

Outcomes from these four probes pin down the constrained-case bounds and potentially upgrade the verdict if the radio is more permissive than the protocol shape suggests.

---

## 6. Recommendation

Lock the implementation shape to **constrained case, reframed as "skip discovery refresh."** Build Rung 1b around immediate `application connect` after `application register`, cache-driven `Radio` object reconstruction, and TCP-fail fallback to Rung 1a. Don't budget for the 5-10x scenario unless §5.5 probes flip the verdict. Add §5 probes to a future networking-investigation sprint to validate or expand the bounds.

# QSY Pileup Handshake — the roster and connect batch

**Status:** planned, not started. Written 2026-08-14 at Noel's instruction:
*"We need to plan all the fixes in batches / a batch rather than fixing stuff
from time to time."*

**Why a batch and not five fixes.** Five reported symptoms, and they are almost
certainly **three roots**. Patching them individually is how the sixth symptom
gets created — every one of them lives in the same 2,668 lines of roster,
discovery-cache and selector code, and two of them are already suspected to
share a cause.

---

## The symptoms, as reported

1. **Don's radio tries to connect locally every time when it should go straight
   to SmartLink.** Noel, 2026-08-14. This is the headline.
2. **A known-local radio that is unreachable never falls back to SmartLink.**
   Trace-proven on the laptop, 12:07 session: discovery drained **zero** packets,
   both connect attempts took the LOCAL branch, hung 20-30 seconds, then "rig's
   open failed" — and the trace contains **zero SmartLink activity at all**. No
   account load, no session, no radio list. Meanwhile the same radio showed
   `status=Available` on SmartLink from another machine.
3. **Connecting through the roster takes two Enters** — the first appears to run
   a refresh, the second actually connects.
4. **A radio name set in per-radio settings is invisible whenever the radio is
   discovered** (task #75). `RigSelectorDialog.PaintRoster` skips the roster
   entirely for any radio already in the discovered list, so the operator's
   chosen name only appears while the radio is offline.
5. **Presence check fails after the client remove/re-add dance**, and the
   authority gate then denies a local operator. Trace-proven on the 8600 over
   LAN: the gui client is added with a full clientId, `isLocalPtt=True`,
   `myClient=True`, then removed at ~1.4 s during station-name binding and
   re-added with an **empty clientId**, `myClient=False`, `isLocalPtt=False` —
   same ClientHandle (1384667612). `IsCurrentClientLocalPtt` reads the impostor
   record and `RequirePortSettingsAuthority` denies.
6. **Account switching friction.** The ms-02 stays signed into SmartLink as Don;
   switching to Noel's own account to reach the 8600 is clumsy under the new
   roster code.

---

## The connect flow Noel wants (2026-08-15) — this reframes the whole batch

Captured from Noel directly. **Read this before Root A below**, which was written
from the discovery side and is correct but enters the problem from the wrong end.

### First, a correction to this document's framing

This file previously said the roster is "what stands between Don and using his
radio." **That is wrong and Noel corrected it.** Don can connect. The defect is
that his radio *tries local first when he has selected remote*, which costs him
a failed attempt and a wait every single time. It is an efficiency and
correctness problem, not a blocker. Noel hit the mirror image himself: he had to
press **Remote** explicitly to get his normally-local radio to connect at all.

### The vision

> *"Right now there's a Remote button. I'm thinking that needs to go away, with a
> connect locally or a connect remotely button. Another option would be to have a
> Connect button which would connect based on preference (locally if selected,
> remotely if selected). Enter clicks Connect and connects based on selection.
> Right click would allow you to connect locally or remotely, an option for both,
> and something that allows you to select connect locally as default or connect
> remotely by default."*

Concretely:

- **The separate Remote button goes away.** Today `RigSelectorDialog.xaml` has
  both `ConnectButton` (line 69) and `RemoteButton` (line 91). Two buttons for
  one act, where the second exists only because the first cannot be trusted to
  pick the right path.
- **One Connect button that honours the radio's stored preference.** Enter
  activates it. The preference is per-radio and persistent.
- **The context menu carries the explicit verbs** — connect locally, connect
  remotely, and the ability to set which is the default for this radio.
  **This surface already exists**: `RigSelectorDialog.xaml:47-63` has a real
  ContextMenu on the radio list with Connect / Add to Favorites / Auto-Connect
  Settings / Preferred Account and a `ContextMenuOpening` handler. Shift+F10
  already reaches it. The connect verbs extend a live menu rather than
  introducing a new one.

### Why the current code cannot do this — the preference is erased three times

Sharper than Root A, and verified in code 2026-08-15:

1. **`IsRemote` never consults the preference off the dual-homed path.**
   `RigSelectorDialog.xaml.cs:100` derives
   `DualHomed ? PreferRemotePath : WanAvailable ? true : LanAvailable ? false : LastSeenRemote`.
   A radio seen on the LAN hits `LanAvailable ? false` and goes local
   unconditionally. The operator's stated choice is not an input to the
   expression.
2. **Lines 705 and 780 overwrite the stored choice**:
   `if (!row.DualHomed) row.PreferRemotePath = false;`
3. **Line 1031 ANDs it away again at the point of use**:
   `SelectedPreferRemotePath = radio.DualHomed && radio.PreferRemotePath;`

And because `WanAvailable` is never learned for a radio discovered locally,
`DualHomed` is false — so all three suppressions fire for exactly the radios
where the preference matters most. **This is a settings-are-intents violation of
the purest kind** (`memory/project_settings_are_intents_not_commands.md`): the
app does not merely ignore the operator's choice, it destroys it.

### The consequence that unifies this batch

**"Honour the stated preference" and "learn WAN availability" are the same fix.**
If a preference of *remote* is authoritative even when the app believes the radio
is LAN-only, then the app must open a SmartLink session in order to satisfy it —
which is precisely the behaviour absent from the trace that drained zero
SmartLink activity. Entering from the operator's side is cleaner than entering
from discovery's side, and it makes the fallback logic a consequence rather than
a special case.

### Decisions to settle in Phase 1, not now

- **What happens to `PathCombo`** (the "Remote via SmartLink" combo, referenced
  at line 1199). Under this model it either becomes the preference editor or
  becomes redundant. It must not survive as a third place the preference lives.
- **Do the explicit verbs also deserve buttons**, or is the context menu enough?
  Noel offered both shapes. Fewer buttons is fewer tab stops
  (`memory/feedback_speak_only_when_ui_does_not_convey.md`), which argues for
  Connect alone plus the menu — but that hides an escape hatch behind a chord,
  and the whole reason Noel got connected at all was reaching for an explicit
  Remote button.
- **What Connect announces** when preference and reality disagree. The
  no-silent-path-substitution rule below still governs: if preference says remote
  and the app falls back to local, or vice versa, it says so.
- **Default for a radio with no preference recorded yet.** Local-first is the
  existing behaviour and the safe one; make it explicit rather than emergent.

---

## The three suspected roots

**Root A — a radio is classified before its WAN availability is ever learned.**
`RadioListItem.IsRemote` is derived as `DualHomed ? PreferRemotePath : …`, and
the whole path-selection apparatus (`PreferRemotePath`, the "Remote via
SmartLink" combo, the dual-homed display strings) **only engages for rows that
are already known to be dual-homed**. But `WanAvailable` is never learned for a
radio the roster has decided is local, because nothing opens the per-radio
SmartLink session before classifying it. A local-only classification is therefore
self-fulfilling: it cannot be revised by evidence it never goes looking for.

Explains symptoms 1 and 2 completely, and plausibly 6.

**Root B — the roster and the discovered list are merged by exclusion, not by
union.** `PaintRoster` skips any roster entry whose serial is already in
`_radiosList`. So roster-held facts — the operator's chosen name, last-seen
data, preferred account, favourite status — are available only for radios that
are NOT currently present. Every roster fact silently loses to a discovery fact,
including facts discovery does not carry at all.

Explains symptom 4, and is a strong candidate for part of 3 and 6.

**Root C — "my client" is resolved by clientId, which the radio omits on
re-add.** ClientHandle survives the remove/re-add; clientId does not. Any
identity check keyed on clientId reads the impostor record.

Explains symptom 5. Independent of A and B, but in the same connect path and
worth fixing in the same pass.

## Sequence — investigation first, deliberately

**Do not start with fixes.** Five symptoms over three suspected roots is exactly
the case where a targeted patch makes the next bug. The first pass produces a
map, not a diff.

**Phase 1 — read the state machine and write it down.**
Cover `RigSelectorDialog.xaml.cs` (2,035 lines), `KnownRadioRoster.cs` (349),
`RadioConnectionCache.cs` (284). Produce a plain-prose description of: how a
radio gets into the list, what facts each source contributes, who wins when they
disagree, when a SmartLink session is opened and by whom, and exactly what
decides which branch a connect takes. **The deliverable is that description**, and
it goes in this file. If the three roots above turn out to be wrong, better to
learn it here than after three fixes.

**Phase 2 — confirm each root against a trace**, not against reasoning. Traces
already exist for symptoms 2 and 5; get one for 4 by naming a radio and watching
the merge.

**Phase 3 — fix, as one batch, one merge.** Likely shape:
- Learn WAN availability before classifying, or make classification revisable
  when a local attempt fails. **Noel's stated design intent: local first, then
  automatically try SmartLink.** A failed local connect should fall through, not
  stop.
- Merge roster and discovery by union with an explicit precedence rule, and
  write that rule down where the next reader will find it. The operator's chosen
  name should win over the hardware's — they typed it deliberately and recently.
- Resolve "my client" by ClientHandle.
- Fold the refresh into the first Enter.

**Phase 4 — verify at a radio.** LAN to the 8600, SmartLink to the 8600, and —
if his radio is up — Don's 6300, which is the only one that reproduces symptom 1
naturally.

## What must not regress

- **Local-first stays local-first.** The fix is a fallback, not a preference
  flip. A radio on the LAN must not start travelling through SmartLink because
  the code got more willing to try it.
- **The dual-homed row and its explicit "Remote via SmartLink" choice must
  survive.** That machinery is correct; it is just unreachable for rows that
  never learn they are dual-homed.
- **No silent path substitution.** If a connect falls back from local to
  SmartLink, say so. An operator who thinks they are on the LAN and is actually
  on SmartLink has been misled about latency, bandwidth and who else can see the
  radio.
- **Do not slow the common case.** A radio that is present on the LAN should
  connect as fast as it does today; the SmartLink probe must not become a
  precondition for the happy path.

## Related, deliberately NOT in this batch

- **Mono capture support** (engine opens two channels, cannot upmix from one).
  Small and well understood — `deviceParams` opens at native count, the input
  callback duplicates, and `endPtr` walks `BufferSize / 2` for mono. `MicProbe`
  already does exactly this duplication. Noel, 2026-08-14: *"We can do it but
  there's lots more that we need to fix first."*
- **#74 REM ON**, **#76 GPS lock and PPB**, **#73 DSP explanations** — pre-lock,
  but separate subsystems.
- **The transverter bench (#27)** — still the blocker for its own arc, and needs
  radio time rather than code.
- **Updater manifest 404** — infrastructure, not roster.

## Context that frames the whole batch

The 2026-08-14 A/B verdict: laptop → exit node → SmartLink → forwarded ports →
the 8600 keyed with PC audio and produced the monitor echo. **Client, SmartLink
and the Opus upstream transport are proven good end to end over WAN**, and
SmartSDR fails on Don's radio too. So the TX-audio fault is at Don's end and is a
support matter.

**Corrected 2026-08-15 by Noel.** This section previously concluded that the
roster is "the thing standing between Don and using his radio at all." It is not
— **Don can connect.** What the roster costs him is a failed local attempt and a
wait on every connect, because it ignores the remote preference he selected.
Worth fixing properly, and it is the batch that most improves his daily
experience — but it is an efficiency and correctness defect, not a blocker, and
this batch should not crowd out other work on the strength of a blocker that
does not exist. See the connect-flow section at the top of this file.

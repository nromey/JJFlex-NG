# Brief: the JJ Flexible Fixer Tool — engine and page

For an agent building the run engine and the web surface. Ruled by Noel
2026-08-25. The authority is task **#218**; this is the working brief.

---

## What you are building

A **framework**, not a page. A stage runner that takes a *stage set* as data and
renders it as a browsable web surface, plus **one stage set: transmit**.

Say that back before starting. The commonest way to get this wrong is to build
a five-stage transmit page, because a second domain then costs a rewrite instead
of a data file. Connection is coming; it is not in scope now.

**Scope is transmit only.** Do not build a connection stage set.

---

## Why it exists

An operator cannot transmit. They open it, and it finds out why — and **fixes
what can be fixed**. At the end they have a document that answers both their
question and FlexRadio's.

Two readers, one document:

- **The operator** reads it to know what to do. Plain, direct, and it is fine to
  say what we think is wrong. Supporting our own user is the job.
- **FlexRadio** reads it to decide whether anything is theirs. Observations,
  measurements, conditions — our interpretation clearly labelled as ours and
  placed **last**. See #217.

---

## The five stages

Ordered so the thing under test sits between two independent positive controls.

0. **Audio setup.** What is selected — host API, input, output, sample rate,
   channels — read from what is actually open, not from config. Those can
   differ, and where they do that *is* the finding.
1. **Microphone check.** Is audio arriving in this computer? No radio involved.
2. **Transmitter check.** Does the transmitter work? No audio involved.
3. **Injected transmit.** Tones and generated voice, microphone bypassed.
4. **Spoken transmit.** The operator's microphone in the path.

Stage 1 proves the microphone; stage 2 proves the transmitter. Stages 3 and 4
differ in exactly one thing. **Stage 1's result is a baseline the spoken stage
is read against**, not just a gate — a stage 4 failure means something quite
different depending on whether the microphone measured well minutes earlier.

Stages 0–2 do not transmit. Run them before anything keys.

---

## Interfaces that already exist — build on these, do not reimplement

Committed, tested, stable:

- `Radios.ChainChecks.TxTuneProbe` — stage 2's verdicts, `Result`,
  `AudioTestingHasStanding`, `EvidenceSection`.
- `Radios.ChainChecks.TxTuneProbeRunner.Run(rig, loadDeclared, cancel, port)` —
  keys the transmitter.
- `Radios.ChainChecks.TxToneLadder` — `DeriveRungs`, `PlanForMode`, verdicts.
- `Radios.ChainChecks.TxToneLadderScope.Enter(rig)` — mode switch, filter read,
  restore, as a `using`.
- `Radios.ChainChecks.ChainAnalyzer.EvidenceText(...)` — stage 1's static
  analysis.
- Microphone measurement already exists (#36).

**Anything that keys the transmitter is a delegate the host supplies.** Do not
call `TxTuneProbeRunner` or any transmit path directly from the engine or the
page. The host owns the transmit boundary.

**Do not build a device picker.** `AudioDevicesDialog` owns that, and #207 is
redesigning it. Stage 0 offers the *specific fixes it detected*; if the operator
wants the full picker, the host opens it.

---

## The run

- **One test ID per run**, generated at the start, stamped on every stage
  result, the trace, and the report. Short enough to read down a phone and type
  into an email; avoid characters that sound alike spoken.
- **Every stage result carries its own timestamp** as well as the run ID. Stages
  can be run out of order, so a report where stage 1 is forty minutes older
  than stage 4 **must say so** — the operator may have changed microphones.
- **A skipped stage is recorded as skipped, with the reason.** Never blank. A
  gap in a numbered set reads as an omission; "not run, and why" is evidence.
- **Re-running a stage replaces its result and says it was re-run.** A stale
  result under a stage the operator just re-attempted is drift in miniature.
- **The report states the order things were actually run in**, which is now
  different from the order they are listed in.

### Skip has two distinct reasons — do not collapse them

- *"I can't speak directly into my radio"* — the radio is remote. A PC
  microphone may still exist, so a comparison is still possible. **Narrows** the
  fault domain.
- *"I don't have access to a microphone"* — none at all. The comparison is
  impossible. **Leaves it open.**

Different reports. A skipped step is **not** a passed step and the output must
make that impossible to misread.

---

## Detect, and fix what can be fixed

Every finding is one of three, and the page says which:

1. **We can fix it** → a button, **at the point of detection**. MME selected
   when WASAPI is available; no microphone chosen; PC audio off; empty mic
   profile. Never "go to Settings" — an operator sent elsewhere loses their
   place. Never fix silently: offer, act on a press, say what changed.
2. **They can fix it, we can't** → say exactly what to do, one sentence, no
   jargon. Unplugged, muted in Windows, privacy setting, antenna port open.
3. **Nobody here can fix it** → say so and stop implying otherwise.

**Every fix applied is recorded in the report** — what was wrong, what it became,
when. The operator can undo it, later stages are read against a configuration
that *changed mid-run*, and FlexRadio must not be shown measurements taken under
a setup we quietly altered.

### MME specifically

MME does not merely sound worse — **it misreports the device**, returning
converted sample rates rather than what the hardware runs. A microphone
measurement through MME measures Windows' resampler. It is also PortAudio's
default nomination, so it is what an operator gets by doing nothing (#61).
Warn, explain in one sentence why, offer the fix.

---

## The surface

**Web content in a WebView2**, because browse mode gives single-letter
navigation that a WPF dialog cannot. `H` between stages, `B` between buttons.
Explanation text costs **zero tab stops** and stays fully readable — which is
the whole reason for this decision.

- **Tabs for running the stages.** A real ARIA tablist: arrow between tabs, Tab
  into the panel. Each stage has its own controls, result and expandable
  explanation. Prev/Next buttons for the guided path.
- **A final report section that is ONE continuous document** containing every
  stage in order, test ID at the top. Interactive part paged; evidence part
  continuous. Copy acts on one region, so it cannot silently copy a third.
- **Plain buttons, checkboxes and radio buttons.** Nothing custom. A bespoke
  widget must earn its place by doing something standard controls cannot;
  nothing here does.
- **An always-reachable Stop button.** Not a fallback — the primary way out.
- **A link to help**, inline, at the point of confusion.

### Voice

**Ask questions like a person; do not label fields.** *"Microphone: connected"*
could be a label, a heading or a statement and a screen reader gives no help
deciding. *"Do you have a microphone connected? Yes — Audient EVO8, on WASAPI"*
can only be read one way. In browse mode **the questions become the
navigation.**

Plain and human, not chatty and not clinical.

**All user-facing copy needs Noel's review before it ships.** Write it well;
expect it to change. This is the surface an operator meets when something has
already gone wrong.

### Speech: the page speaks for itself

**No Prism on this surface.** WebView2 exposes real UIA; a properly marked-up
page conveys itself. In browse mode the screen reader is reading *where the
operator is*, so app-driven speech fights them.

Stage names, results, verdicts, explanations, progress — **page only**.

One carve-out, and the host handles it: **Critical warnings** get an
`aria-live="assertive"` region *and* an app-side earcon, until testing proves
the live region fires reliably under both NVDA and JAWS.

### Structure to get right

- Real heading hierarchy, one level per nesting step, no skips.
- `<button>` elements, not clickable divs.
- Labels associated with controls; `aria-describedby` for explanations.
- Disclosure for long explanations — `<details>` or a proper disclosure pattern.
- Results in the DOM where their stage is, so browse order matches meaning.
- **Count the tab stops.** Read-only prose must not be one.

---

## The output

**Length is not the constraint; comprehension from the first screen is.** Lead
with what was found and what to do; measurements follow. A reader who stops
after one screen still has the answer; a reader who continues finds everything
needed to check it.

**Two forms.** The page renders HTML; the email to FlexRadio wants **plain
text**. Copy yields the plain-text form. Do not make the operator copy rendered
HTML into a mail client and hope.

**Encourage starting at the beginning.** Free navigation stays, but the default
path is stage 0 onward, and a partial run says plainly which stages were not
done and that the answer is weaker for it. Encourage, do not lock.

---

## What the host owns — not yours

- The WebView2 shell and window.
- The **Escape bridge**, and every transmit call.
- The earcon path and Critical warnings.
- Pressing the keys on a real build under both screen readers.

**Escape is asymmetric** and the host implements it: **keyed** → unkey
immediately, no confirmation, *then* ask whether to abandon; **unkeyed** → offer
to stop. The dangerous thing while transmitting is the delay, not the action.

---

## Definition of done

- The engine runs a stage set from data. Swapping in a different set requires no
  code change.
- Every rule above has a test that fails if the rule is broken. Skip semantics,
  test ID stamping, re-run replacement, out-of-order timestamps, the three-way
  fix taxonomy, both output forms.
- **No test asserts a frequency, a device name or a threshold that the code
  derives.** Assert the property, not the example. (See
  `TxToneLadderDerivationTests` for the pattern.)
- Heading hierarchy, tablist semantics and tab-stop count verified by inspecting
  the rendered markup — statically, in a test.
- The whole thing builds and the existing 563 tests still pass.

**Do not mark it done because it compiles.** Nothing here is verified until it
has been driven, and that part is the host's.

---

## The wire between page and host

Added 2026-08-25 after the brief was found to leave this open — which meant
both halves were about to invent their own names. The host parser at
`Radios/Fixer/FixerPageMessage.cs` is the authority; this section describes it.

### Page to host

`window.chrome.webview.postMessage(JSON.stringify(...))`, always a JSON object
with a `kind`:

- `{"kind":"ready"}`
- `{"kind":"declare-load","what":"50 ohm dummy load on ANT1"}` — `what` required
  and non-blank; over 200 characters is truncated rather than refused, because
  refusing would block the one answer that gates every transmit.
- `{"kind":"run-stage","run":"<runId>","stage":"<stageId>"}`
- `{"kind":"run-stage","run":"...","stage":"...","again":true}` — the deliberate
  repeat. Parses as a **different kind**, not a flag: a flag can be forgotten by
  a handler, a kind falls through the switch visibly. `again` must be a real
  JSON `true`; `"true"` and `1` read as false on purpose, so a page that thinks
  it asked for a repeat gets a refusal it can retry rather than an extra
  transmission.
- `{"kind":"skip-stage","run":"...","stage":"...","choice":"<skipChoiceId>"}`
- `{"kind":"apply-fix","run":"...","stage":"...","fix":"<fixId>"}`
- `{"kind":"stop","source":"escape"}` — `source` is recorded for the trace and
  gates nothing. Parses from the bare `{"kind":"stop"}`: a Stop that could be
  refused for a missing field is a Stop that fails exactly when the page is in
  the state you most want out of.
- `{"kind":"copy-report"}`
- `{"kind":"open-help","topic":"fixer/transmit/microphone-check"}`
- `{"kind":"open-device-picker"}`

### What the host refuses

Unknown kind, missing required field, non-object JSON, and anything over 8192
bytes — all refused and traced, never silently dropped. Parsing never throws: a
surface whose job is to diagnose a broken radio must not fall over on a bad
string.

### The field that does not exist

`FixerPageMessage` has **no** `loadDeclared`, `transmits`, `keyed`, `force` or
`power`. Those are safety facts; they live in `FixerTransmitGate` or are read
from the radio. A page that starts sending them finds the host has nowhere to
put them — a stronger guarantee than a comment asking nobody to trust them,
because it survives a good-faith refactor. Same device as `ModePlan` carrying no
filter information.

Message shape and permission are deliberately two layers. A message with no
`run` parses fine and the gate refuses it. Two places refusing the same thing
for different reasons is how one of them ends up wrong.

### Host to page

`window.jjflex.receive(<json string>)`, defined by the page. The page owns its
own state model; the host owns only the transport.

# Arc sequencing

**Split out of `research-queue.md` 2026-08-23**, where it was 200 lines inside a
file that had stopped being readable. This is Noel's ordering decision for the
major arcs and it is still live, which is exactly why it should not be buried in
a working dashboard.

---

## Arc sequencing decided 2026-08-11 (Noel)

**Order: finish the audio arc → backend reporting + infrastructure + signing →
CW rewrite.** The CW rewrite was previously "the next arc"; it is not. An
infrastructure phase goes in front of it.

**This does NOT supersede the audio work.** Noel was explicit — the
honest-TX-audio arc (`docs/planning/vision/honest-tx-audio.md`) finishes first.
This entry exists so the infrastructure phase is not forgotten, not to reorder
anything in flight.

**What the infrastructure phase covers:**

- **Storage and processing on R2 plus rarbox/roarbox** for crash reports, user
  feedback, and problem reports — the server side of the three report types
  that currently have designs but no destination. Existing design memory:
  `project_sprint29_crash_reporter_vision.md`,
  `project_user_initiated_feedback_session.md`,
  `project_crash_triage_bundle_flow.md`. Hosting precedent:
  `project_data_provider_hosting.md`.
- **The update pipeline** — `active/auto-update-research.md`,
  `project_sprint29_updater_vision.md`, `project_chained_updater_pattern.md`.
  Note the app-update manifest at `data.jjflexible.radio` still 404s.
- **Code signing** — `active/signing-track.md`,
  `project_microsoft_trusted_signing.md`.

**THE TRACE BROWSER IS THE CLIENT-SIDE HALF OF THAT PIPELINE — AND IT HAS NEVER
BEEN TESTED (Noel, 2026-08-11).** Sprint 29 Track H shipped a Trace Browser tab
in the TraceAdmin dialog (`ff7b3b16`, 2026-05-09, on main): date/outcome/text
filters, sortable list, detail panel, and **View / Export / Delete / Prune**
actions over the trace archive.

- **Noel does not recall ever testing it, and the evidence agrees:** the
  Sprint 29 test matrix already carries a written checklist for it
  (`agile/sprint29-test-matrix.md`, "Track H — Trace Archive Browser tab") with
  **every functional box still unchecked.** The test plan exists; nobody ran it.
  This is shipped, unverified code sitting in a release branch.
- **Architecturally it is further along than it looks.** A crash report, a
  feedback bundle and a problem report all need the same client-side act: *find
  the relevant trace, select it, package it, send it.* The browser already does
  find / filter / select / **Export**. What is missing is the transport (upload
  to R2) and the backend that receives and processes. **So the reporting work is
  mostly a transport-and-backend job with the selection UI already built** — and
  testing that UI is therefore a prerequisite for the phase, not a side errand.
- **Do this when the audio arc's radio-seat session happens**, since the browser
  needs real archived traces to browse and the audio testing generates them.

**AND IT IS NOT JUST THE BROWSER — SPRINT 29 WAS NEVER SYSTEMATICALLY TESTED
(Noel, 2026-08-11: "that was about the time I said I needed a JJ Flexible
break").** `agile/sprint29-test-matrix.md` holds **83 test items across 8
tracks and not one is ticked.**

- **The evidence survives a control check.** Unticked boxes would prove nothing
  if ticking were not the practice — so: `sprint25-test-matrix.md` is **55
  checked out of 55**, same checkbox format, worked through completely. The
  sprints between (23, 24, 26) and `4.1.17` / `pre-4.2-foundation-drop` use no
  checkbox syntax at all, so they are silent rather than negative. **Sprint 25
  proves the practice exists and gets finished when it runs; Sprint 29 is the
  one that uses the format and shows zero.** Honest limit: this proves the
  *matrix* was never worked through, not that zero testing happened — some
  tracks were likely exercised informally. Treat it as "no record of systematic
  verification."
- **THE REFRAME THAT MATTERS FOR THE INFRASTRUCTURE PHASE: five of Sprint 29's
  eight tracks ARE that phase, already built and shipped.** Track A (trace
  persistence, 9 items), Track D (**app-updater client**, delta-fetch + XZ, 12),
  Track H (**trace browser**, 16), Track M (**updater helper exe**, atomic file
  replacement, 10), Track N (**server-side manifest generation**, 12) — **59 of
  the 83 items.** So the phase Noel is queuing is substantially a
  **verify-and-deploy** job on existing code, not a from-scratch build. What is
  genuinely new is the R2/rarbox/roarbox storage and processing for the three
  report types, and the signing track.
- **This also explains the 404.** The app-update manifest at
  `data.jjflexible.radio` returns nothing because Track N built the *generator*
  and nothing was ever generated or deployed. That is a deployment gap, not a
  missing feature.
- **Track J (self-contained build pipeline, 9 items) is the sharpest one.**
  `CLAUDE.md` states fresh-VM verification is **mandatory before public
  release** — install on a Windows VM that has never had .NET 10 and confirm
  jjflexible.exe launches to Home without a runtime prompt. Those 9 items are
  unchecked, so the release-blocking test our own SOP calls mandatory has no
  record of ever running.
  - **The ms-02 cannot serve as that proof, and it is worth writing down before
    someone proposes it again (checked 2026-08-11).** JJFlex launching fine on
    the ms-02 is not evidence of self-containment: `dotnet --list-runtimes`
    there reports **Microsoft.NETCore.App 10.0.9 and
    Microsoft.WindowsDesktop.App 10.0.9**, installed alongside SDK 10.0.301. A
    build machine necessarily has the runtime — **the box you build on is the
    one box that can never validate self-containment.** Nor is .NET 10 bundled
    with Windows 11; Windows ships .NET *Framework* 4.8, a different and much
    older product, so "it must have come with the OS" does not explain it
    either.
  - **The configuration itself is verified good:** the x64 Release output does
    carry `coreclr.dll`, `clrjit.dll`, `hostfxr.dll` and `hostpolicy.dll`, so
    `<SelfContained>` is doing its job. What is missing is only the *proof on a
    machine without the runtime*.
  - **The Lenovo is disqualified too (Noel, 2026-08-11): it was the build
    machine in May, before the ms-02 existed.** So both available Windows boxes
    have the SDK and neither can serve as the control. Don's and Justin's
    machines are no better — pre-self-contained builds required a .NET 10
    Desktop Runtime install, so testers likely acquired it that way.
  - **USE WINDOWS SANDBOX, not a provisioned VM.** Checked on the ms-02
    2026-08-11: `HypervisorPresent: True`, edition **Windows 11 Pro**, so it is
    supported; the feature is simply not enabled yet
    (`WindowsSandbox.exe` absent). Enable once with elevation plus a reboot:
    `Enable-WindowsOptionalFeature -Online -FeatureName "Containers-DisposableClientVM"`.
    Why it beats a VM for this specific job:
    - **Disposable means the control cannot rot.** Every launch is a fresh
      Windows image, so it is structurally impossible for .NET 10 to be present
      unless something in the test installs it. A hand-built VM is clean once
      and drifts thereafter — exactly how a fresh-VM test quietly stops proving
      anything.
    - **No ISO, licence or disk provisioning** — it derives from the host image.
    - **It makes the SOP sustainable.** CLAUDE.md calls this mandatory before
      every public release; a five-minute repeatable check gets run, a
      provisioned-VM ritual does not.
    - **It is verifiable without sight or a screen reader inside the sandbox,
      which is the part that matters here.** A `.wsb` config takes
      `MappedFolders` plus a `LogonCommand`, so the run can be fully scripted:
      assert `dotnet --list-runtimes` is empty (proving the control is valid),
      run the installer silently, launch `jjflexible.exe`, wait, then assert the
      process is alive, a main window exists, and no dialog mentioning ".NET" or
      "runtime" appeared — writing a plain-text verdict to the mapped folder.
      **Noel reads the result file on the host with NVDA.** This is not a
      workaround for blindness; a written assertion is stronger evidence than
      anyone eyeballing a VM screen, and it satisfies
      `feedback_accessibility_is_end_to_end.md` rather than straining it.
    - Scope note: the publish-shape items (~180-190 MB, ~364 files, satellite
      dirs, runtime DLLs present) are **host-side** checks and need no sandbox
      at all. Only the launch-without-runtime assertion does.
  - **REFRAME — THE TEST IS NO LONGER ABOUT .NET, AND "IT LAUNCHED FINE" IS THE
    WRONG ASSERTION (2026-08-11).** Noel's reasoning that SmartSDR ran on Tony's
    runtime-free machine using the same technique is sound about the *technique*
    — and the .NET question is settled more directly than that anyway, by
    inspection: our own output carries `coreclr.dll`, `clrjit.dll`,
    `hostfxr.dll`, `hostpolicy.dll` and `vcruntime140_cor3.dll`. **What a fresh
    machine uniquely catches is everything else the app silently assumes is
    present — and the two leading candidates do not fail at launch:**
    - **WebView2 Evergreen Runtime.** The build bundles the managed wrappers and
      `WebView2Loader.dll`, but **the loader is a shim that loads a separately
      installed system runtime** (Microsoft Edge WebView2 Runtime); it is not
      the browser engine. Windows 11 preinstalls it, Windows 10 machines may
      not. **Failure surface is SmartLink/Auth0 login, not startup** — the app
      opens to Home perfectly and then cannot log in.
    - **The public VC++ redistributable for the native audio DLLs.**
      `portaudio.dll` and `libopus.dll` are bundled under
      `runtimes/win-x64/native/`, but they are MSVC-built natives that may bind
      to system `msvcp140.dll` / `vcruntime140.dll`. .NET's private
      `vcruntime140_cor3.dll` is a renamed copy those natives will not use.
      **Failure surface is audio not starting** — again, well after launch.
    - **So the sandbox script must exercise more than a launch:** open to Home,
      attempt a SmartLink login far enough to prove WebView2 instantiates, and
      start the audio engine far enough to prove both natives load. A
      launch-only check would return a green result on a machine where the two
      things remote operators depend on are both broken.
- **Remaining tracks:** F (tuning UX bundle, 13) and G (stuck-modal escape
  changelog, 2) — user-facing, not infrastructure, but equally unverified.
- **Suggested shape:** do not treat this as one 83-item slog. Split it — the
  five infrastructure tracks become the front half of the infrastructure phase
  (verify, then deploy, then build the report backend on top), while F and G
  ride along with whatever radio-seat session happens next.

**SPLIT BY WHAT ACTUALLY NEEDS A RADIO (Noel, 2026-08-11 — "mechanics of making
sure buttons click, CW announces right, etc. can easily be tested with this
radio rather than tying up Don's").** Read against the matrix, the radio burden
is far smaller than 83 items suggests:

- **NO RADIO AT ALL — 45 of 83 items.** Track D (updater client: on-demand
  check, SHA-256 tamper detection, channel selector), Track G (changelog prose
  review), Track J (build output size/file count/satellite dirs, fresh-VM
  install), Track M (helper waits for PID, per-file backup→download→rename,
  SHA-mismatch rollback), Track N (`manifest_gen.py` dry run, R2 upload,
  hash-match skip, XZ magic-byte check). These are file, network and build
  assertions. **A rig would not participate even if one were connected.**
- **NEEDS ARCHIVED TRACES, NOT A LIVE RADIO — Track H's 16.** Tab visibility,
  filters, sorting, detail panel, View/Export/Delete/Prune all operate on the
  archive. **The audio investigation has already generated a large trace corpus**,
  so the archive is likely populated enough to test against right now.
- **NEEDS A RADIO, AND THE BENCH 8600 FULLY SUFFICES — 22 items.** Track A's 9
  (connect/disconnect archiving, AS-retry marker, killed-session detection) and
  Track F's 13 (coarse/fine tuning steps, the retired `C` binding, the split
  Settings fields). **No antenna required** — none of it transmits.
- **NEEDS DON'S RADIO: ZERO ITEMS.** Nothing in Sprint 29's matrix requires his
  6300, which matters because his radio is a production station and never a test
  target (`memory/project_don_radio_lives_at_tonys.md`).

**Two consequences worth acting on:**

1. **Testing Track N IS the deployment.** N's items run `manifest_gen.py` and
   push to `data.jjflexible.radio` — so working through that track produces the
   manifest whose absence is the current 404, which in turn unblocks Track D's
   items (D needs a manifest to fetch). The update pipeline therefore has a
   natural test ORDER that is also a rollout order: **N → deploy → D → M → J's
   fresh-VM install as the end-to-end proof.** Do not test them in matrix order.
2. **D, M and N are largely scriptable, so background agents can run them.**
   Hash comparisons, XZ magic bytes (`FD 37 7A 58 5A`), helper.log assertions,
   PID-exit sequencing and upload skip-on-hash-match are all machine-checkable
   without a human or a screen reader. Track J's size and file-count assertions
   too — only its fresh-VM launch needs a person. **What genuinely needs Noel at
   the keyboard with NVDA is Track H's browser mechanics, Track A's labels, and
   Track F's tuning feel.**


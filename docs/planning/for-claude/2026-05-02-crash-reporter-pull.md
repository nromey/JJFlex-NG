---
type: full design memo — fresh review pass
review needed: confirm 04-26 design holds, weigh in on open UX details
priority: medium — Sprint 29 scope, parallel to updater work
source: pulled from memory/project_sprint29_crash_reporter_vision.md per your "Pull X" request
---

# Foundational crash reporter — full design pass (2026-05-02)

This memo captured your 04-26 thinking on replacing the manual-Dropbox-copy crash flow with a one-click upload to the JJ Flexible Data Provider. Sprint 29 scope, parallel to updater work, shares some infrastructure with it.

Use `**** ` for inline answers; `**** ACK` for sections that hold up.

---

## Note on overlap with updater memo

This crash reporter is also referenced as **Phase F of `project_sprint29_updater_vision.md`** (which I pulled separately as `2026-05-02-sprint29-updater-pull.md`). The two memos describe the same thing at different abstraction levels:

- **Crash reporter memo (this one):** implementation-level — what the dialog says, what files get sent, what the receiver endpoint looks like, where the WPF dispatcher fix goes.
- **Updater memo Phase F:** scope-level — "auto error / crash notification" as one of the 8 Sprint 29 phases.

**Recommendation:** keep this memo as canonical for crash-reporter design; mark updater Phase F as "see crash-reporter memo for details" so the two don't drift. Already flagged in the updater pull doc.

**** ACK: NER

---

## The problem this fixes

Today's `CrashReporter.SaveCrash()` writes three files to `%APPDATA%\JJFlexRadio\Errors\`:
- `JJFlexError-YYYYMMDD-HHMMSS.dmp` (minidump)
- `JJFlexError-YYYYMMDD-HHMMSS.txt` (stack trace + context)
- `JJFlexError-YYYYMMDD-HHMMSS.zip` (both bundled)

User is then expected to navigate to that folder, find the most recent .zip, copy it to Dropbox `crash\`. Multi-step friction for screen-reader users. AND the user often doesn't know a crash dump was generated.

Result: Noel only gets crash reports from most-engaged testers (Don, Justin), only when they remember + have time + can find the file.

Your framing: *"That data's gold if the report is generated correctly."* — captured 04-26.

**** ACK on problem framing?
**** ACK NER
---

## Sprint 29 target — one-click upload

After a crash, on next app start (or in-app post-recovery), surface a dialog:

> JJFlex crashed last time at YYYY-MM-DD HH:MM:SS.
> May we send the crash report to JJFlex Support?
>
> [Send] [Don't send] [Show what's in the report]

"Show what's in the report" path opens a viewer of the .txt content so user sees exactly what's being uploaded — protects no-phone-home transparency requirement.

When user clicks Send: HTTP POST the .zip to crash-receiver endpoint on JJ Flexible Data Provider. Endpoint stores file in per-date directory; no parsing, no extraction, no analytics. Noel reviews manually.

**Open UX details for your call:**
**** a pretty major change here, see your memories for daetails. Crash reporter and feedback are similar and use same submit path to rabox disk space.
**** User would be able to either submit feedback, same files included plus possibly a network diagnostic and upload of radio statistics / otehr diagnostics that may help. If submitting feedback user enters text explaining whatever feedback i.e. report on what is acting weird etc. Possibly ask questions / include boxes for optional questions. Collect user's computer info including OS. Display in a read-only box what info is submitted for transparency.
**** Crash report includes a box which allows user to explain what they were doing when crash happens (optional). We could include other questions which we could use to diagnose the crash further to include their email if provided. Again all the user really needs is to click save to report, cancel to not report. Do we make crash report happen automatically or explain in the dialog that the report will help fix the issue, whatever it is, also explain that though one click is required to send this in, info could help.
**** FOr feedback, also include optional contact info so I can reach back out. Email, Callsign etc. Want to make the dialog easy to fill out or just submit so users are more likely to submit information. Do we include a checkbox to include supporting files or no, or do we rqeuire the diagnostic pack. For feedback maybe allow pack to be disabled but default to enabled. Crash requires pack.


1. **Send button keyboard shortcut?** The dialog is critical — user might want a single keystroke to send/dismiss. Suggest `Alt+S` for Send, `Alt+D` for Don't send, `Alt+W` for "Show what's in the report" (W for "what").

   **** 

2. **Dialog timing.** On next app start, BEFORE main window loads (modal blocking)? Or AFTER main window loads (non-modal toast-style)? My lean: modal at start — user has fresh attention, can't accidentally dismiss while doing other things. But that adds startup friction.
****Modal at start if user hasn't responded when crash is realized. Do we just gather the crashes for a particular run and then on nbext app run, we submit them all? I'm leaning toward that.

   
3. **"Don't send" persistence.** If user clicks Don't send, does the report stay on disk for later review? Get deleted? Move to a "rejected" folder?

   **** I'd say it's queued in case it happens again and you have the ability to seend otehr reports when crash happens / if there are muiltiple crashes in a session and you have the ability when crash packages are detected to delete all or report. Do you have any other ideas to lesssen (friction tax principle)?

---

## Endpoint location

Per `project_jjflex_data_provider.md`:
- Data Provider is read-only as primary principle (URL-as-config, hosted on Cloudflare R2 per `project_data_provider_hosting.md`, JJF servers for dynamic).
- Crash-receiver IS a write endpoint — can NOT live on R2 (which is static-only).
- Goes on JJF dynamic-services tier (rarbox per current architecture).

**Recommended subdomain: `crashes.jjflexible.radio`** (mirrors `data.jjflexible.radio` pattern). Different security surface, different rate-limiting needs, different abuse profile.

URL-as-config still applies: receiver URL is in a config file, swappable via DNS or config edit.

**** ACK on `crashes.jjflexible.radio` subdomain (NNER)

**** Place receiver on rarbox (per current architecture) or roarbox (warm spare per this morning's discussion)? Lean: rarbox primary since it's already provisioned and low-volume; if reliability matters more, set up Cloudflare load-balancer pool with both. (RARBOX definitely as extra processing not necessary for reception.

---

## No-silent-phone-home alignment (LOCKED 04-26)

This pattern is COMPATIBLE with `project_no_silent_phone_home.md` because:
- Each upload is user-consented per crash event (per principle's narrow exception)
- "Show what's in the report" gives full transparency before send
- Default is "ask" — never auto-send
- Advanced setting could opt user into "always send without asking" but default must remain ask-each-time
**** Agree with that. Friction decrease could add the setting to auto send to be put in a settings page in case user wants to reverse decision in dialog. All options have defaults so that one click (send) can be selected.
Anti-pattern to avoid: silent background uploads, "send all" toggles in hidden settings menu, auto-include of files outside the crash report (logs, configs).

**** ACK (NER) See above comments.

**** Open question: should the "always send without asking" advanced setting exist at all? Argument for: power users / Don / Justin would opt in to reduce friction. Argument against: even the existence of that setting risks misuse / accidental enable. Lean: include it, hide deep in Settings → Diagnostics → "Advanced crash reporting" subsection so it requires intention to find.
**** Yes but don't automatically send, always show dialog so user can enter data to support crash and follow-up. You could enter user information so that contact info doesn't ahve to be entered each time, a checkbox for save contact info sends it to a crash reporting and feedback settings tab in settings which can be edited.
---

## Foundational improvements bundled in scope

Beyond upload UX, "foundational crash reporter" includes:

### 1. Better .txt content

Current report has stack trace; should also include:
- FlexLib version
- OS build
- Screen reader running (for accessibility-related crashes)
**** - Connected radio make,model, and serial if available
- Recent log tail (last 100 trace lines)

**** ACK but see full line from NER. ACK on these additions? Anything else worth bundling? Network diagnostic (extra NER)

### 2. WPF dispatcher exception handling

Currently missing (code-health observation from 2026-04-26 fartsnoodle scan). Without it, WPF dispatcher exceptions fall through to `AppDomain.CurrentDomain.UnhandledException` which crashes hard.

Adding `Application.Current.DispatcherUnhandledException` lets us decorate the crash with screen-reader-friendly speech ("an internal error occurred, please reopen") BEFORE the crash report fires.

**Status check:** memo says this is "Pre-Sprint-29 fix landing 2026-04-26 standalone." **Has this landed yet?** I can verify by grepping the codebase for `DispatcherUnhandledException` if useful.

**** Want me to verify status now, or skip and check during sprint kickoff?
**** Check now. Would be a good thing to report.
### 3. Crash dedupe

If same exception fires N times in M minutes (storm), report-prompt suppresses to "JJFlex crashed N times. Send a single report covering all of them?" instead of N consecutive prompts.

**Open: N and M values?** Lean: N=3, M=5 minutes. Aggressive enough to catch storms, gentle enough to not lose individual crashes.

**** Agree, I talked about this previously, it would have you report it on app start. That brings up a question, so we show the dialog right away or give it time to show something later. Do we even show a dialog, or show a minimal one, then detail at upon app start again?

### 4. Test-mode "fake crash" command

For verifying report-flow works without actually crashing. Hidden command in Command Finder (`Trigger crash for testing` or similar).

**** ACK on test command? Naming preference?
**** We could add this in ctrl+f code woord "whoopsie or something similar. Include it in command finder, and we might get stuff that is eroneous. If I really need someone to trigger it I can give them the word."
---

## Sprint 29 implementation order (rough estimate)

1. WPF dispatcher fix — half day (if not already landed; standalone work)
2. Crash receiver endpoint setup on rarbox — half day
3. Better .txt content (logging additions to CrashReporter.SaveCrash) — half day
4. One-click upload dialog + HTTP POST client — 1 day
5. "Show what's in the report" viewer — half day
6. Dedupe logic — half day
7. Test-mode trigger command — quarter day
8. Help text + accessibility polish — half day

**Total: ~4-5 days.** Fits comfortably in Sprint 29 alongside updater work without crowding.

**** ACK on order + estimate?
$**** AAck but you might want to add extra stuff including teh feedback submission and processing. Add a possible URL and if we need to change it we can change in code or in a JSON settings file (probably lean more toward change of code.)
---

## Build-now-ship-later question

Per `project_build_now_ship_later.md` — do you want me to spin up `track/crash-reporter` branch now so dev work can happen in parallel with the updater track? Or wait for Sprint 29 formal kickoff?

**** ACK this is very load bearing now.

---

## Confirm or revise

`**** ACK` — design holds, ready for sprint planning.
**** ACK as written in total (NER 05-03-2026)
`**** ` (specific changes) — apply changes; I'll update the memory entry.

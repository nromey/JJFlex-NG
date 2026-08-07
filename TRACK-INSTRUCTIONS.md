# QB Track F — Dialog & SmartLink account sweep (C2 revival)

**Recommended model: Fable.** The MessageBox sweep is judgment-per-site, and
the account flows carry design calls with auth-adjacent consequences.
Document judgment calls in a "Design decisions" section appended to this
file.

## Context

One of six parallel tracks in the 2026-08-07 queue-burn (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`). JJ Flex is a
screen-reader-first FlexRadio client. This track revives the old Track C2
dialog sweep, whose worktree was closed down 2026-08-07. The full original
ledger with all context is archived at
`C:\dev\JJFlex-NG\docs\planning\agile\archive\track-instructions\track-dialog-sweep-C2.md`
(main repo — read it; this file is the updated summary, that one holds the
deep context and the findings narrative).

**Status updates since C2 was written:** items 2 and 4 DONE and merged
(AdvisoryDialog pattern confirmed working by Noel); item 12 SHIPPED as "Use
Now" (confirmed live 2026-08-07); item 11 mostly mooted by the native
password login (WebView2 is now only the MFA fallback — the silent-validation
fix applies only there, lowest priority); item 16 moved to Track B; items
6, 7, 14, 15 moved to Track E (selector territory). Everything below is
yours.

## NVDA lessons — apply to EVERY dialog you touch (from Noel's live testing)

- Blank lines in a read-only WPF TextBox make NVDA re-read the neighboring
  line (degenerate UIA range). Put a single space on each blank line —
  `AdvisoryDialog.NormalizeLineBreaks` is the reference.
- Every IsDefault button says "carriage return" under NVDA unless you set
  explicit `AutomationProperties` AccessKey/AcceleratorKey. Set them on
  every IsDefault button in every dialog you touch.
- Verify by arrowing through EVERY line of every converted dialog —
  line-ending and blank-line bugs are invisible visually and inaudible
  under JAWS.
- Escape closes every dialog. Errors and failure reports never get
  suppress-keys. Every spoken string via ScreenReaderOutput at a deliberate
  VerbosityLevel.

## Work items

1. **The ~94-site `MessageBox.Show` sweep in JJFlexWpf.** Judgment per site,
   not mechanical: informational advisories → `AdvisoryDialog.Show` (action
   button when an obvious "take me there" exists; suppress key ONLY where
   permanently declining is legitimate — never on errors); trivial
   confirmations may stay simple but must be readable. The archived ledger
   has the full triage guidance. Commit in batches by area so merge review
   is sane.
2. **Item 3 — GPS status dialog arrowability.** `LiveStatusTextBox` — the
   one thing Noel named as broken in his 2026-08-04 test pass. Small and
   self-contained; do it first as your warm-up.
3. **Item 5 — radio rename field.** FlexLib `Radio.Nickname` setter works
   (sends `radio name <x>`, persists radio-side, flows back through
   discovery, works over SmartLink). Build: Radio Setup GroupBox (this is
   your ONLY touch in SettingsDialog.RadioSetup — Track C owns the other
   settings tabs), FlexBase setter wrapper, auto-connect display-name
   refresh. Urgency context: the unnamed 8600 row is what Noel mis-picked
   when aiming for Don's 6300.
4. **Item 5b — ConfirmActionDialog warnings must be readable.** The
   warnings list (keying guidance, do-not-power-off — the highest-stakes
   text in the whole flow) is currently un-navigable. Give it the
   AdvisoryDialog read-only reviewable-edit treatment (arrow through lines,
   caret navigation), not a ListBox NVDA cannot enter. Seen failing twice
   live, including during a real firmware update.
5. **Item 8 — native SmartLink signup + forgot password.** The hosted
   page's signup link half-works: creates the account, then fails the
   redirect and REPORTS failure. SmartSDR never uses the page — it posts
   `https://frtest.auth0.com/dbconnections/signup` and
   `.../dbconnections/change_password` natively (connection
   "Username-Password-Authentication"; client_id and error mapping in the
   archived ledger; reference implementation `smartlink-signup.ps1` in the
   2026-08-04 session scratchpad). Build an accessible JJFlexDialog form
   (email, password, repeat; SPEAK every validation error), reachable from
   the SmartLink account dialog; after signup route into the existing
   native login flow. Also test the hosted page's forgot-password link —
   if broken, the native form is the only working path. Update the
   SmartLink-setup advisory copy that currently oversells the page signup.
6. **Item 8a — propagate mid-session sign-in.** After a successful New
   Login while a radio is connected: load the new account into the live
   FlexBase (reuse whatever connect does), then re-run
   `SuggestRegistrationIfUnregisteredAsync` (reset the per-run
   suggested-serial guard). Today's only recourse is restarting the app.
   Verify: sign in mid-session → advisory fires → Radio Setup step 2 shows
   a live Register button without a restart.
7. **Item 13 — "not registered" advisory names the account.** State the
   account it checked ("…is not registered to dbreda@mail.com") and handle
   registered-elsewhere: if another saved account's cached list knows the
   radio, say switch accounts instead of suggesting registration.
8. **Item 17 — "see the message" sweep.** Any spoken string that refers the
   user to text they must go find is a bug — speak the reason itself.
   Start at `StaticIpControl.UseCurrentButton_Click`
   (JJFlexWpf\Controls\StaticIpControl.xaml.cs:170), then sweep for the
   pattern. Also: affordances that cannot work in the current state (e.g.
   "use current address" over SmartLink) announce as unavailable instead of
   failing-then-explaining.
9. **Startup speech ordering policy.** While the advisory chain is active,
   main-window bring-up speech (the welcome line, focus-driven slice
   speech) queues behind it the same way SpeakConnectStatus already does —
   an ordering policy, not whack-a-mole. Also check how Tab reached the
   main window behind a modal advisory (focus should not escape).

## Ownership boundaries (do not cross)

- Dialog family (AdvisoryDialog, ConfirmActionDialog, new signup forms) and
  the SmartLink account manager dialogs are yours. `RigSelectorDialog` is
  Track E's. SettingsDialog: only the RadioSetup rename GroupBox (item 3),
  nothing else.
- The MessageBox sweep will brush other tracks' files — convert the CALL
  SITES only; do not restructure surrounding logic in files another track
  owns (B: audio settings; C: network settings; D: connect failure paths).
  When in doubt, list the site in your report and skip it.
- No key bindings without flagging the orchestrator.

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh after every build.

## Commit style

Commit after each work item (sweep: per batch): `QB Track F: <what changed>`.
Push to `origin` (never `upstream`). Report completion to Noel when done.

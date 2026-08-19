# Three rulings the diagnostics track needs from you

**From:** Sprint 30 Track D (front door diagnostics), `sprint30/track-d`
**Date:** 2026-08-18
**Why you:** each of these is a judgement about what the app owes an operator,
not something the code can settle. All three are built and working right now;
each one has a decision baked in that I would rather you confirmed or overruled
than left to be discovered later.

Read this at whatever pace suits — nothing here blocks the merge. If you change
one, it is a small edit each time.

---

## Ruling one: when may the app delete a crash report you never answered about?

**What you asked for:** keep the most recent N, and never delete one that has
not been submitted or explicitly dismissed. That is exactly what shipped: three
newest kept unconditionally, and beyond that a report is only removed once you
have either sent it or answered "no" to the send prompt.

**The problem with that rule, taken literally.** If the send prompt never
appears — the app died before it could, or the prompt itself failed — the report
has no verdict and would be kept forever. A machine that crashes that way
repeatedly grows without bound, which is the exact 1.8 GB problem this work
exists to fix.

**What I did, and want you to confirm.** A report with no verdict survives
everything for **90 days**. After that it is removed, and the log says so by
name: `removed UNRESOLVED crash report JJFlexError-....zip (never sent and never
dismissed, and older than 90 days)`. Deleting evidence nobody acted on should
never happen quietly, so it does not.

**Your options, if 90 days is wrong:**

- **Longer.** 180 days, a year. Costs disk on a machine that is already having a
  bad time.
- **Never.** Fully honours what you asked for; accepts that a pathological
  machine can fill its disk with dumps. Defensible — the crash reporter's whole
  value is having the dump when support asks.
- **Shorter.** I would not: a crash you never saw a prompt for is precisely the
  one worth keeping.

There is also a manual control on the Diagnostics tab — "Delete crash reports I
have sent or dismissed" — which never touches an unanswered one, whatever the
automatic policy says.

---

## Ruling two: which failures earn a window that appears on its own?

The app now offers you the diagnostic log at the moment something fails. This is
the item where every mistake is silent: an offer that fires at the wrong moments
teaches you to dismiss it permanently, and an offer that fails to fire is worse
than none at all, because by then you believe there is a safety net.

**It offers on four things:**

- a setting you changed that did not reach disk (it is live now, gone next
  launch, and today's only message is "see the trace file for details")
- a connection to a named radio that failed
- audio that would not open, or stopped
- the reporting pipeline itself breaking — a problem report that would not
  build, a capture that would not start

**It stays silent on:** crashes (the crash reporter already prompts with a full
bundle and an upload choice — two windows deep at the worst possible moment is
not a kindness), "no radios found" (an ordinary state with an obvious next step),
login and token rejections (your own next action fixes them, and the log carries
your SmartLink email and JWT fragments, so offering to export raises the privacy
cost for no diagnostic gain), anything a retry already absorbed, corrupt preset
files (that message is already honest and actionable), and firmware download
failures (re-downloadable by definition).

**And regardless of kind it stays silent:** while you are transmitting, after
shutdown has begun, when the diagnostic log is off, after two offers in one
session, and forever after you answer "Not now" even once.

**What I want from you:** tell me if any of those four should not interrupt you,
or if something in the silent list should. The two-per-session cap and the
one-"Not now"-ends-it rule are also judgements — say if either feels wrong.

---

## Ruling three: delete the old tracing dialog now, or keep it one release?

The ratified design retires the WPF trace dialog outright. Nothing opens it any
more — Help → Tracing is gone, replaced by Tools → Diagnostics.

I **kept the file** rather than deleting it, corrected: it now reads the live
tracing state instead of assuming tracing is off, routes start and stop through
the session-aware path instead of flipping the switch behind the archive's back,
and defaults to the real log file instead of `Documents\JJRadioTrace.txt`.

**Why keep it:** this sprint merges five tracks, the Settings dialog is the
surface most likely to need backing out at merge time, and a corrected fallback
sitting in the tree is cheaper than rebuilding one under pressure. Same reasoning
as keeping `AuthForm` when WebView2 replaced it.

**Why you might disagree:** it is dead code, and dead code with a plausible name
is how somebody wires it back up by accident in six months.

Say the word and it goes. Otherwise it should be deleted once 4.1.17 has shipped
with the Diagnostics tab — that instruction is in the file's own header comment
so it does not get forgotten.

---

## One thing you should know that is not a ruling

`%AppData%\JJFlexRadio` on this machine currently holds **34 loose log text files,
35 MB**, and the automatic prune is working correctly. Those files are all from
today. The one-day plain-text window is what keeps them; a day with thirty
launches produces thirty files, every one of them already saved compressed in the
Traces folder.

So the earlier read that auto-pruning "misses the loose files" was not right —
it covers them, it just has a one-day horizon. Which is why the manual "Delete
loose log text files" button explicitly deletes files newer than that horizon
too. That is the control that was actually missing.

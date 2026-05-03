# for-claude — work queue from Noel to Claude

When something appears in this folder, **Claude needs to act on it.** Pair folder is `../for-noel/` (where Claude drops things for Noel).

See `../for-noel/README.md` for the full protocol and the four-asterisk comment convention.

## Two ways things land here

### Round-trip from for-noel

Noel finishes reviewing a doc that Claude drafted in `for-noel/`. Noel adds `**** ` annotations and **moves the file here**. Claude processes on next session, files the result to its permanent home, deletes the for-claude copy.

### Direct drop from Noel

Noel wants Claude to look at something on his own initiative — a Don email, a research paper link, a screenshot, a piece of code from another project, a one-liner question. He drops it directly here. No prior Claude memo required.

Lightweight forms that work:

- **A pointer file** — `2026-05-02-don-trace-from-yesterday.md` with one line: "Read this trace and tell me what you make of it: [path]."
- **A question file** — `2026-05-02-question-about-flexlib.md` with the question. Claude answers in chat and (if the answer is durable) saves it to memory or a permanent doc.
- **A bare artifact** — drop the trace / screenshot / pdf directly. Filename should be self-explanatory.

## Quick reference

- File appears here → Claude reads it on the next session and acts on Noel's `**** ` comments or the direct ask.
- After processing, Claude deletes the for-claude copy and the permanent version (if any) lands in its proper home.
- Git history retains the review trail when the folder is committed.

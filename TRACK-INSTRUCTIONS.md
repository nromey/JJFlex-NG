# Track G — The honest About page

**Worktree:** `C:\dev\jjflex-g` · **Branch:** `bsr/track-g` · **Model:** Sonnet

**Read first:** `docs/planning/active/barefoot-splatter-ragchew.md`, section
"Track G — The honest About page".

## Theme

When a tester reports a problem, we need to know exactly what they are running,
and there is currently no way for them to tell us.

## The work

**A WebView2 page**, with a plain fallback for machines lacking WebView2.

**Why WebView2 is not cosmetic: it gives BROWSE MODE.** This is the wall the
Audio Workshop hit — `AutomationProperties.HeadingLevel` does nothing in a WPF
dialog because a dialog runs in focus mode, where `H` types a letter. A web page
runs in browse mode: `H` jumps headings, arrows read continuously, selection and
copy behave normally. For a page whose entire job is "read this to your supporter
or paste it into a report," that is the difference between tabbing fields and
just reading.

**Query the libraries at RUNTIME. Never hardcode a version.**

An About page with `"Opus 1.5.2"` baked in is **worse than no page**, because it
lies with confidence and someone acts on it. **This project already made that
mistake** — CLAUDE.md claimed Opus 1.5.2 until it was caught, when the shipped
DLLs were 1.6.1 on both architectures. The documentation drifted on the exact
fact this page exists to report.

`opus_get_version_string()` and `Pa_GetVersionText()` cost nothing to call.

**PortAudio needs special handling.** It reports `"PortAudio V19.7.0-devel"`
whether built in 2021 or last week — upstream never bumped it, so the number is
meaningless. **Only the revision suffix distinguishes builds.** Lead with the
revision; **never show a bare 19.7.0**, which tells a supporter the opposite of
the truth. The pinned SHA lives in `build-native/portaudio-pinned-commit.txt`.

**Report:** FlexLib, Opus, PortAudio (revision-first), .NET runtime, the app's
own four-part build, OS version and architecture.

**Include the non-library facts support actually asks for:** whether the build is
self-contained, the executable path, and **the trace file location** — that last
one turns "where are your logs" from a five-minute exchange into a glance.

**Usable in a support conversation:** selectable text, a **copy-everything
button** so nobody reads hex aloud, and reachable **with no radio connected** —
support calls rarely happen while things are working.

**Entirely local.** No external fetch to render an About box. See
`memory/project_no_silent_phone_home.md`.

**Build it as a DATA STRUCTURE first, UI second.** The crash reporter and the
feedback bundle need this same content embedded. If the page assembles display
strings, the reporter duplicates the logic and the two disagree about what
version is running. This is the standing rule of the tranche: **if a thing will
be consumed by a second feature, model it as data from the first line.**

## Papercuts you own

Help-page and wording papercuts touching version or diagnostic reporting.

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.**
- Build: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Track G: <description>`.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Builds clean. The page opens with no radio, reports every component from a
runtime query, leads PortAudio with its revision, offers copy-everything, and its
content comes from a data structure a crash report could reuse. Verify the
browse-mode navigation actually works rather than assuming it does.

# AGENTS.md — instructions for Codex

This repository's working rules live in `CLAUDE.md`. **Claude Code leads this
project.** You work the tasks it assigns you, and you check its work when asked.
This file tells you where everything is, how to report back, and the rules you
must never miss. It is deliberately short: Codex stops reading instruction files
at 32 KiB, and `CLAUDE.md` alone is nearly three times that, so it is something
you READ, not something that is loaded for you.

## Who leads, and what that means for you

- **Claude Code owns** the task register, the memory tree, `CLAUDE.md`, this
  file, the end-of-day seal, and every merge into a shared branch.
- **You work only on what a brief assigns.** A brief is a file in the
  `for-codex` mailbox (below), or a direct instruction from Noel, the project
  owner, in your own session. Do not widen the scope. If you think the brief is
  wrong, do what it asks where that is safe and say why in your report.
- **This project is built for, and by, people who use screen readers.**
  Everything you write for a person is prose or bullets. Never tables,
  diagrams or ASCII art.

## Read these before any work, in this order

1. **`CLAUDE.md`**, in this folder, in full. It is written for Claude Code, so
   it names Claude's own tools (the Agent tool, Artifacts, skills, TaskCreate).
   Translate those to your own tools. **Its rules apply to you unchanged.**
2. **The memory core**:
   `C:\Users\nrome\.claude\projects\C--dev-JJFlex-NG\memory\MEMORY.md`.
   Claude has this loaded automatically; you must open it yourself. It points to
   topic index files in the same folder. Open the one that matches your task.
   **Before you state any fact about this project, search that folder**, for
   example `rg -il "profile" C:\Users\nrome\.claude\projects\C--dev-JJFlex-NG\memory`.
   The usual failure is not being unable to find a fact but not realising it was
   there to find.
3. **The task register**: `C:\Users\nrome\JJFlex-private\planning\active\tasks.md`
   (`C:\dev\jjf-private` is a junction to the same tree). Read the whole entry
   your brief names, not just its heading.
4. **Your brief.**

**If your sandbox cannot read the memory folder or the register, stop and say
so.** Do not carry on from guesses. That is a setup problem for Claude to fix,
and work done without that context is the work this arrangement exists to
prevent.

## Rules you must never break

These are quoted from `CLAUDE.md`, where each is explained. A test in this
repository fails if any bold phrase below stops appearing there, so the two
files cannot drift apart without somebody noticing.

<!-- hard-rules:begin -->
- **NEVER run `dotnet test` without naming a project.** At solution scope it
  builds every test project, and `JJFlexWpf.Tests` puts real windows on Noel's
  live desktop. Name the project, every time.
- **`JJFLEX_TIER1_DESK_FREE`** must never be set by you, in a command, a
  script or a file. It is a declaration by a human who has left the machine.
- **`JJFLEX_CONFIG_DIR`** must be set to an absolute temporary folder if a
  brief ever has you launch the application, so the operator's live settings
  are never touched.
- **PLANNING DOCUMENTS GO IN JJFlex-private**, never in this repository. This
  repository is public. Notes, reports, transcripts and anything naming a
  tester belong in `C:\Users\nrome\JJFlex-private`.
- **Stage specific files.** Never `git add -A` and never `git add .`.
- **NEVER `upstream`.** Push only to `origin`, and only when a brief says to.
  Never push JJFlex-private at all; it has no remote on purpose.
- **Do NOT ping testers.** Noel handles every conversation with them.
- **Do NOT bump the version.**
- **Do NOT auto-publish** anything: no Dropbox, no NAS release folders, no
  GitHub releases, no website.
- **NEVER tables** in anything a person reads.
- **Close running JJFlexRadio before building**, or the build fails on a locked
  DLL and can leave you testing stale binaries.
<!-- hard-rules:end -->

Also, from the memory tree rather than `CLAUDE.md`:

- **Never transmit or key a radio**, for any reason, unless Noel tells you to in
  your own session. Keying rules are in the memory tree; search it for
  `transmit` before going anywhere near the radio code paths that key.
- **Do not edit vendor code** under `FlexLib_API` unless your brief says to.

## Memory and the register: read them, never write them

Claude is the only writer for both. Do not create, edit or delete files in the
memory folder, and do not edit `tasks.md`. When you learn something that should
outlive your session, or a task's state changes, put it in your report under
**For the memory** or **For the register**. Claude files it and checks it
against what is already there.

## The mailbox

Both folders are under `C:\Users\nrome\JJFlex-private\planning\`. **A folder's
name says who acts next.**

- **`for-codex`** holds briefs for you, named `YYYY-MM-DD-slug.md`. When you
  finish one, move it into `for-codex\done`.
- **`for-claude`** is where your report goes, named
  `YYYY-MM-DD-codex-slug.md`. Put `codex` in the name so Claude can tell your
  reports apart from Noel's own notes in the same folder. The first line is
  `From: Codex (model name) — brief: the brief's file name`.

A report says, in this order: what you did; what you verified, and how; what
you could not verify; what you deliberately did not do, and why; then **For the
register** and **For the memory**. A report that finds nothing must still say
what it checked, because "no findings" from a check that looked at nothing reads
exactly like a clean result.

Reports go to JJFlex-private, never into this repository, because they may name
testers or private context.

## Git

- **Work only in the worktree or branch your brief names.** Never work in a
  worktree another agent is using, and never merge into a shared branch.
- **Your sandbox keeps `.git` read-only**, so every commit asks Noel for
  approval. That is intended; do not look for a way around it.
- Match the existing message style (`git log`), and end every commit with the
  trailer `Co-Authored-By: Codex (model name) <noreply@openai.com>` so the
  end-of-day seal can say which system did what.
- **Keep each file's existing line endings.** Git normalises them on commit, but
  a file rewritten wholesale still makes a working tree unreadable to review.

## Building and testing

- Build: `dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`
- Test: `dotnet test Radios.Tests/Radios.Tests.csproj -c Debug -p:Platform=x64`
- Run the window-constructing project only filtered, exactly as `CLAUDE.md`
  shows under its integration pass, and say which classes you ran.
- A build or test is not verified until you have checked it produced fresh
  output. `CLAUDE.md` explains why and how.
- If a restore needs the network and your sandbox has none, report it. Do not
  switch the sandbox off.

## When your brief is to check Claude's work

Read the commits or diff the brief names and test every claim against the code
itself, not against the commit message. Say plainly what is wrong. You are a
second pair of eyes from a different model, and that difference is the whole
point, so do not soften a finding because Claude sounded sure.

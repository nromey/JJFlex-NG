# The Diagnostic Log

When something goes wrong with radio software, the worst part is usually that it went wrong five minutes ago and left no trace. The diagnostic log is JJ Flexible's answer: a quiet record of what the app was doing, kept so that a problem which already happened can still be explained — without you having to reproduce it on demand while somebody watches.

Everything about it lives in one place: **Settings, then the Diagnostics tab**. Tools, then Diagnostics, jumps straight there. The first thing on that tab is a single sentence that tells you whether a log is being kept, at what detail, and whether a capture is running right now — so you never have to wonder.

## The standing log

By default, JJ Flexible keeps a diagnostic log all the time. At the Normal detail level it records connections, errors, and the actions you take, and honestly, you will never notice the cost — not in speed, and not meaningfully in disk space either, because old sessions clean themselves up (more on that below).

You can turn the standing log off with the "Keep a diagnostic log" checkbox. I'd suggest leaving it on: the whole value of the thing is that it was already running when something surprised you.

There are two detail levels:

- **Normal** — the recommended everyday setting. Connections, errors, your actions.
- **Detailed** — records nearly everything the app does. The files grow fast at this level, so use it when I ask you for a detailed log, or better yet, use a capture instead.

If you've used earlier versions of JJ Flexible, you may remember a Tracing dialog with five detail levels with names only a programmer could love. That dialog is retired. Two honest choices and a capture button replace it, and they cover everything the five levels did.

## Detailed capture — the "watch this" button

A capture is for when you can make the problem happen. Start a capture, reproduce the problem, stop the capture — and everything the app did in between is recorded at maximum detail and saved as its own named session, separate from the everyday log.

Three ways to run one, all equivalent:

- The "Start detailed capture" button on the Diagnostics tab. Press it again to stop.
- **Ctrl+J, then Ctrl+D** — the JJ key chord. Works from anywhere: any dialog, radio or no radio. Press it once to start, again to stop.
- If the app itself notices something fail, it may offer to start one for you.

Stopping a capture announces where it went, and an "Export this capture" button appears so you can save it as a single file right away. The standing log resumes on its own afterward — stopping a capture never leaves you unrecorded.

## Saved sessions

Every run of the app, and every capture, becomes a session in the **Saved Diagnostic Logs** window — the "Browse saved logs" button opens it. Each session says when it ran, how it ended, and whether it was a capture. From there you can read a session, export it as a file to send, or delete it.

Sessions tidy themselves: each one is compressed and kept for thirty days, then removed automatically. Yesterday's sessions stay readable as plain text for one day before being compressed.

When I ask you for "a problem report", the **"Save a problem report bundle"** button does the whole job: it gathers the recent sessions and a snapshot of your setup into one file, ready to attach to an email. Nothing is sent anywhere by itself — the bundle lands wherever you choose to save it, and sending it is entirely your move.

## Disk space

The Diagnostics tab's Disk space group tells you what the settings folder is holding — press "Measure now" to count it (it's a button rather than automatic because a large folder takes a moment, and a mysterious pause helps nobody). Two cleanup buttons live beside it: one deletes leftover loose log text files (the compressed sessions are untouched), and one deletes crash reports you've already sent or dismissed. A crash report you have never dealt with is never deleted automatically — its whole value is being there when support asks.

## Privacy, plainly

The diagnostic log stays on this computer. It can include your callsign, contacted callsigns, your SmartLink email, network addresses, and your radio's serial number. When you export a session or save a problem report, you are choosing to share whatever that session recorded — review it first if that matters to you. Nothing is ever sent automatically.

## Where the file actually is

The Diagnostics tab shows the live log's location, with a "Copy log file path" button (puts the full path on the clipboard) and an "Open log folder" button (opens it in File Explorer). You'll rarely need either — exporting from Saved Diagnostic Logs is the easier road — but the direct route is there when someone asks for the raw file.

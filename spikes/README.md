# Sprint 38 Track H spikes — JAWS braille line research

`jaws-line/` holds the hardware-test spike: a JAWS application script and key
map, scoped to jjflexible.exe, that prove line painting and cursor routing in
one sitting. What each key does, what a human must run, and every claim that
needs a real braille display are in the design document:
`C:\Users\nrome\JJFlex-private\planning\jaws-braille-line-design.md`.

`chm2txt.py` renders pages of the extracted FSDN reference as plain text.

`fsdn-extract/` is NOT committed: it is Freedom Scientific's copyrighted
developer reference, and this repository is public. Regenerate it locally:

    "C:\Program Files\7-Zip\7z.exe" x ^
      "C:\Users\nrome\JJFlex-private\jaws-dev-guides\fsdn\fsdn.chm" ^
      -ospikes\fsdn-extract

The spike passed tools/readercapture/verify/verify_jaws_script.py (name,
arity, constants, block balance against the installed JAWS 2026 catalogue).
It has NOT been compiled: scompile.exe cannot open a source file outside a
JAWS settings folder, so compiling is inseparable from installing into the
user settings tree — a human step by track boundary.

"""Print the Fixer's prose exactly as an operator meets it, to be READ.

Reads the RENDERED pages in C:\\temp\\fixer rather than pulling strings out of
source. That is the honest instrument: it is the assembled text, in order,
with nothing added by an extractor and nothing missed by one. Pulling literals
out of C# drags comments and code along with them, which is what the first
attempt at this did.

Noel, 2026-08-25: "just read the damn sentences."
"""
import io, re, sys, glob, html
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

for path in sorted(glob.glob(r'C:\temp\fixer\*.html')):
    raw = io.open(path, encoding='utf-8').read()
    raw = re.sub(r'(?is)<(script|style)\b.*?</\1>', ' ', raw)
    # Block tags become breaks so sentences do not run together.
    raw = re.sub(r'(?i)</(p|div|li|h[1-6]|button|section)>', '\n', raw)
    text = html.unescape(re.sub(r'<[^>]+>', ' ', raw))
    # Inline tags became spaces, so "</strong>." read as " ." and looked
    # like a real typo. It was not: the instrument introduced it. Close the
    # gap before punctuation so the reader does not invent defects.
    text = re.sub(r'\s+([.,;:!?])', r'\1', text)

    print("\n########## %s ##########" % path.split('\\')[-1])
    for line in text.splitlines():
        line = re.sub(r'\s+', ' ', line).strip()
        if len(line) < 12:
            continue
        for s in re.split(r'(?<=[.!?])\s+', line):
            s = s.strip()
            if len(s) >= 12:
                print("  " + s)

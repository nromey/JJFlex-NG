"""Check that every NVDA symbol the plugin depends on exists in the installed NVDA.

WHY THIS EXISTS
---------------
The plugin can only be exercised inside NVDA, and NVDA is the operator's live
screen reader. So the failure this file is built to prevent is the one that
would waste a whole debugging session: an add-on that loads, attaches nothing,
records nothing, and looks exactly like a reader that received nothing.

NVDA ships its modules as .pyc inside library.zip. The bytecode cannot be
unmarshalled by a different Python, but every identifier in a code object is
stored as plain text in the marshal blob, so a name that exists can be proven
to exist. That is enough for the question being asked: does
speech.extensions.pre_speech still exist in THIS NVDA, or did it get renamed?

POSITIVE CONTROL: the scanner is first pointed at a name that is definitely
present and a name that is definitely absent. If it cannot tell those two
apart, it reports itself broken and certifies nothing - because a scanner that
finds nothing looks exactly like an NVDA with nothing in it.

Usage:  python verify_nvda_symbols.py [path\\to\\NVDA]
"""

import os
import re
import sys
import zipfile

# module -> names the plugin genuinely uses from it
REQUIRED = {
    "speech/extensions.pyc": ["pre_speech", "speechCanceled"],
    "braille.pyc": ["pre_writeCells", "BrailleHandler", "handler", "message"],
    "globalPluginHandler.pyc": ["GlobalPlugin"],
    "scriptHandler.pyc": ["script"],
    "api.pyc": ["copyToClip", "getForegroundObject", "getFocusObject"],
    "core.pyc": ["callLater"],
    "ui.pyc": ["message"],
    "speech/__init__.pyc": ["speakText"],
    "buildVersion.pyc": ["version"],
}

# The control pair. One of these must be found and the other must not.
CONTROL_PRESENT = ("speech/speech.pyc", "speak")
CONTROL_ABSENT = ("speech/speech.pyc", "thisNameIsNotInNvdaAnywhereAtAll")

IDENT = re.compile(rb"[A-Za-z_][A-Za-z0-9_]{2,}")


def find_nvda(explicit=None):
    if explicit:
        return explicit
    for cand in (r"C:\Program Files\NVDA", r"C:\Program Files (x86)\NVDA",
                 os.path.expandvars(r"%LOCALAPPDATA%\Programs\NVDA")):
        if os.path.isfile(os.path.join(cand, "library.zip")):
            return cand
    return None


def names_in(zf, module):
    try:
        blob = zf.read(module)
    except KeyError:
        return None
    return set(m.decode("ascii", "replace") for m in IDENT.findall(blob))


def has_name(names, wanted):
    """Identifiers are stored back to back in the marshal blob, so a name can
    appear glued to the next one. Accept a prefix match, not just equality."""
    if names is None:
        return False
    if wanted in names:
        return True
    return any(n.startswith(wanted) for n in names)


def main(argv):
    nvda = find_nvda(argv[1] if len(argv) > 1 else None)
    if not nvda:
        print("SKIP: no NVDA installation found, so nothing can be verified. "
              "This is not a pass.")
        return 2
    lib = os.path.join(nvda, "library.zip")
    zf = zipfile.ZipFile(lib)
    version = ""
    libdir = os.path.join(nvda, "lib")
    if os.path.isdir(libdir):
        entries = [d for d in os.listdir(libdir) if re.match(r"^\d{4}\.", d)]
        if entries:
            version = sorted(entries)[-1]
    print("NVDA at %s%s" % (nvda, (", version " + version) if version else ""))

    mod, present = CONTROL_PRESENT
    if not has_name(names_in(zf, mod), present):
        print("POSITIVE CONTROL FAILED: the scanner cannot find %s in %s, which "
              "is certainly there. The scanner is broken; it certifies nothing."
              % (present, mod))
        return 3
    mod, absent = CONTROL_ABSENT
    if has_name(names_in(zf, mod), absent):
        print("POSITIVE CONTROL FAILED: the scanner claims to find %s, which "
              "cannot exist. It would call anything present." % absent)
        return 3
    print("Positive control passed: the scanner finds a name that is there and "
          "does not find one that is not.")

    missing = []
    for module, wanted in sorted(REQUIRED.items()):
        names = names_in(zf, module)
        if names is None:
            missing.append("module %s is not in library.zip at all" % module)
            continue
        for name in wanted:
            if not has_name(names, name):
                missing.append("%s does not define %s" % (module, name))

    if missing:
        print("\n%d missing symbol(s). The plugin will attach fewer hooks than "
              "it claims, so fix these before trusting a capture:" % len(missing))
        for m in missing:
            print("  " + m)
        return 1
    print("\nEvery symbol the plugin uses is present in this NVDA.")
    print("This proves the NAMES exist. It does not prove the plugin loads, "
          "that the extension points fire for speech arriving over the "
          "controller RPC, or that the gestures are free. Only running it in "
          "NVDA proves those.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

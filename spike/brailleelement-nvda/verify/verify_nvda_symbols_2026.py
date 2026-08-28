"""Prove every NVDA name the brailleElement spike touches exists in the
INSTALLED NVDA — the build the operator actually runs — without loading NVDA.

Same technique and the same honesty rules as
tools/readercapture/verify/verify_nvda_symbols.py: NVDA ships modules as .pyc
inside library.zip; identifiers in a code object are stored as plain text in
the marshal blob, so a name that exists can be proven to exist. That answers
the only question asked here: did the API surface the 2026-04-29 Track C
survey documented survive into this NVDA, or did something get renamed?

It does NOT prove behaviour, signatures, or that the add-on loads.

POSITIVE CONTROL: before certifying anything, the scanner must find a name
that is definitely present and fail to find one that cannot exist. A scanner
that finds nothing looks exactly like an NVDA with nothing in it.

Usage:  python verify_nvda_symbols_2026.py [path\\to\\NVDA]
"""

import os
import re
import sys
import zipfile

# module -> names the spike genuinely uses from it (see _session.py and
# __init__.py), plus the dispatch-chain names the design's routing story
# depends on even though our code never calls them directly.
REQUIRED = {
    "braille.pyc": [
        # painting
        "Region", "TextRegion", "rawText", "update", "brailleCells",
        "rawToBraillePos", "brailleToRawPos",
        "handler", "mainBuffer", "messageBuffer", "regions",
        "displayDimensions", "displaySize",
        "scrollForward", "scrollBack",
        # routing dispatch chain (design-load-bearing, not called by us)
        "routeTo", "windowPosToBufferPos", "bufferPosToRegionPos",
        "BrailleDisplayGesture", "routingIndex",
        # message path (the generic/Prism baseline lands here)
        "message", "_dismissMessage",
        # focus interplay dismiss() reasons about
        "handleGainFocus", "_doNewObject",
    ],
    "globalCommands.pyc": [
        "script_braille_routeTo",
        "script_braille_scrollForward",
        "script_braille_scrollBack",
    ],
    "louisHelper.pyc": ["translate"],
    "globalPluginHandler.pyc": ["GlobalPlugin"],
    "scriptHandler.pyc": ["script"],
    "api.pyc": ["getFocusObject"],
    "ui.pyc": ["message"],
}

CONTROL_PRESENT = ("braille.pyc", "handler")
CONTROL_ABSENT = ("braille.pyc", "thisNameIsNotInNvdaAnywhereAtAll")

IDENT = re.compile(rb"[A-Za-z_][A-Za-z0-9_]{2,}")


def find_nvda(explicit=None):
    if explicit:
        return explicit
    for cand in (
        r"C:\Program Files\NVDA",
        r"C:\Program Files (x86)\NVDA",
        os.path.expandvars(r"%LOCALAPPDATA%\Programs\NVDA"),
    ):
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
    """Identifiers sit back to back in the marshal blob, so a name can appear
    glued to the next one. Accept a prefix match, not just equality."""
    if names is None:
        return False
    if wanted in names:
        return True
    return any(n.startswith(wanted) for n in names)


def main(argv):
    nvda = find_nvda(argv[1] if len(argv) > 1 else None)
    if not nvda:
        print(
            "SKIP: no NVDA installation found, so nothing can be verified. "
            "This is not a pass."
        )
        return 2
    zip_path = os.path.join(nvda, "library.zip")
    zf = zipfile.ZipFile(zip_path)

    # Positive control first.
    ctrl = names_in(zf, CONTROL_PRESENT[0])
    if not has_name(ctrl, CONTROL_PRESENT[1]) or has_name(ctrl, CONTROL_ABSENT[1]):
        print(
            "BROKEN: the scanner cannot tell a present name from an absent "
            "one against {}. Certifying nothing.".format(zip_path)
        )
        return 3
    print("control: OK (present/absent pair distinguished)")

    missing = []
    for module, wanted in sorted(REQUIRED.items()):
        ns = names_in(zf, module)
        if ns is None:
            print("MISSING MODULE: {}".format(module))
            missing.append(module)
            continue
        for w in wanted:
            if not has_name(ns, w):
                print("MISSING: {} :: {}".format(module, w))
                missing.append("{}::{}".format(module, w))

    if missing:
        print(
            "FAIL: {} name(s) the spike depends on are absent from {}. "
            "The design must be re-checked against this NVDA before any "
            "hands-on test.".format(len(missing), nvda)
        )
        return 1
    print(
        "PASS: every NVDA name the spike touches exists in {}. This proves "
        "names, not behaviour - the hands-on test still decides.".format(nvda)
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

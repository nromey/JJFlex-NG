"""Run the spike's cell-to-element mapping logic OUTSIDE NVDA and assert the
properties the design claims — because the one machine with NVDA on it is the
operator's, and his reader is live.

A fake `braille` module is injected before importing _session. The fake's
Region.update() plays the role liblouis plays inside NVDA: it populates
brailleCells, rawToBraillePos and brailleToRawPos from rawText. Tests run it
two ways — identity (uncontracted) and a synthetic contraction where cells
and characters do NOT line up one-to-one — so the routing math is proven to
lean on the maps, never on character counts.

What this proves: the mapping, patch, separator, indicator and
dismiss-after-clobber logic. What it cannot prove: that NVDA loads the
add-on, paints the display, or delivers routing gestures. That is the
hands-on test in the README.

POSITIVE CONTROL: the harness first asserts a deliberate failure and must
catch it, so a screen full of passes is not an empty loop.
"""

import importlib.util
import os
import sys
import types
from collections import namedtuple

HERE = os.path.dirname(os.path.abspath(__file__))
SESSION_PATH = os.path.join(
    HERE, "..", "addon", "globalPlugins", "brailleElementSpike", "_session.py"
)

DisplayDimensions = namedtuple("DisplayDimensions", ("numRows", "numCols"))


# ---------------------------------------------------------------------------
# The fake braille module
# ---------------------------------------------------------------------------

def _identity_translate(rawText):
    """Uncontracted stand-in: one cell per character, cell value 0x01 for
    visible characters and 0x00 for spaces (a space really is an empty cell
    in braille)."""
    cells = [0x00 if ch == " " else 0x01 for ch in rawText]
    raw_to_braille = list(range(len(rawText)))
    braille_to_raw = list(range(len(rawText)))
    return cells, raw_to_braille, braille_to_raw


def _contracted_translate(rawText):
    """Synthetic contraction: every run of two non-space characters becomes
    ONE cell (last odd character gets its own), spaces stay their own cell.
    Deliberately not real braille — the point is only that cells and
    characters stop lining up one-to-one."""
    cells = []
    raw_to_braille = []
    braille_to_raw = []
    i = 0
    while i < len(rawText):
        if rawText[i] == " ":
            braille_to_raw.append(i)
            raw_to_braille.append(len(cells))
            cells.append(0x00)
            i += 1
            continue
        j = i
        while j < len(rawText) and j - i < 2 and rawText[j] != " ":
            j += 1
        cell_index = len(cells)
        braille_to_raw.append(i)
        for k in range(i, j):
            raw_to_braille.append(cell_index)
        cells.append(0x11)
        i = j
    return cells, raw_to_braille, braille_to_raw


class _FakeRegion:
    translator = staticmethod(_identity_translate)

    def __init__(self):
        self.rawText = ""
        self.brailleCells = []
        self.rawToBraillePos = []
        self.brailleToRawPos = []

    def update(self):
        cells, r2b, b2r = self.translator(self.rawText)
        self.brailleCells = cells
        self.rawToBraillePos = r2b
        self.brailleToRawPos = b2r


class _FakeBuffer:
    def __init__(self):
        self.regions = []
        self.update_calls = 0

    def update(self):
        self.update_calls += 1


class _FakeHandler:
    def __init__(self):
        self.mainBuffer = _FakeBuffer()
        self.displayDimensions = DisplayDimensions(numRows=1, numCols=40)
        self.displaySize = 40
        self.update_calls = 0
        self.scrolls = []

    def update(self):
        self.update_calls += 1

    def scrollForward(self):
        self.scrolls.append("forward")

    def scrollBack(self):
        self.scrolls.append("back")


def _install_fake_braille():
    fake = types.ModuleType("braille")
    fake.Region = _FakeRegion
    fake.handler = _FakeHandler()
    sys.modules["braille"] = fake
    return fake


def _load_session_module():
    spec = importlib.util.spec_from_file_location("_session_under_test", SESSION_PATH)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ---------------------------------------------------------------------------
# The tests
# ---------------------------------------------------------------------------

def main():
    failures = []

    def check(name, cond, detail=""):
        if cond:
            print("pass: " + name)
        else:
            print("FAIL: " + name + ("  [" + detail + "]" if detail else ""))
            failures.append(name)

    # Positive control: the harness must be able to fail.
    control_caught = False
    try:
        assert 1 == 2, "deliberate"
    except AssertionError:
        control_caught = True
    if not control_caught:
        print("BROKEN: the harness cannot register a failure. Certifying nothing.")
        return 3
    print("control: OK (deliberate failure was caught)")

    fake = _install_fake_braille()
    S = _load_session_module()

    def el(text, id, click=True):
        return S.DisplayElement(
            text=text, id=id, on_click=(_recorder if click else None)
        )

    clicks = []

    def _recorder(element_id, offset):
        clicks.append((element_id, offset))

    # --- uncontracted mapping ---------------------------------------------
    _FakeRegion.translator = staticmethod(_identity_translate)
    r = S._ElementRegion([el("Play", "play"), el("Stop", "stop"), el("Mute", "mute")])
    r.update()
    check("rawText composed with two-space separators", r.rawText == "Play  Stop  Mute")

    clicks.clear()
    r.routeTo(0)
    check("cell 0 -> play offset 0", clicks == [("play", 0)])
    clicks.clear()
    r.routeTo(3)
    check("cell 3 -> play offset 3", clicks == [("play", 3)])
    clicks.clear()
    r.routeTo(4)  # first separator cell
    r.routeTo(5)  # second separator cell
    check("separator cells are a no-op", clicks == [])
    clicks.clear()
    r.routeTo(6)
    check("cell 6 -> stop offset 0", clicks == [("stop", 0)])
    clicks.clear()
    r.routeTo(len(r.brailleToRawPos) - 1)
    check("last cell -> mute offset 3", clicks == [("mute", 3)])
    clicks.clear()
    r.routeTo(999)
    r.routeTo(-1)
    check("out-of-range cells are a no-op", clicks == [])

    # read-only element: click must not fire
    r2 = S._ElementRegion([el("SM7", "s_meter", click=False), el("Mute", "mute")])
    r2.update()
    clicks.clear()
    r2.routeTo(0)
    check("read-only element click is a no-op", clicks == [])
    r2.routeTo(5)
    check("clickable neighbour still fires", clicks == [("mute", 0)])

    # --- contracted mapping ------------------------------------------------
    _FakeRegion.translator = staticmethod(_contracted_translate)
    rc = S._ElementRegion([el("Play", "play"), el("Stop", "stop")])
    rc.update()
    # "Play  Stop": Play -> 2 cells, two spaces -> 2 cells, Stop -> 2 cells
    check("contracted cell count differs from char count",
          len(rc.brailleCells) == 6 and len(rc.rawText) == 10)
    clicks.clear()
    rc.routeTo(1)  # second cell of contracted "Play" = raw chars 2..3
    check("contracted cell 1 -> play (offset from the map, not from cells)",
          clicks == [("play", 2)], repr(clicks))
    clicks.clear()
    rc.routeTo(4)  # first cell of contracted "Stop"
    check("contracted cell 4 -> stop offset 0", clicks == [("stop", 0)], repr(clicks))
    clicks.clear()
    rc.routeTo(2)
    rc.routeTo(3)
    check("contracted separator cells are a no-op", clicks == [])

    # --- indicator ---------------------------------------------------------
    _FakeRegion.translator = staticmethod(_identity_translate)
    ri = S._ElementRegion(
        [el("Play", "play"), el("SM7", "s_meter", click=False)],
        indicate_clickable=False,
    )
    ri.update()
    check("indicator off: no dots 7+8 anywhere",
          all(c & S.CLICKABLE_INDICATOR_DOTS == 0 for c in ri.brailleCells))
    ri.indicate_clickable = True
    ri.update()
    play_cells = ri.brailleCells[0:4]
    sep_cells = ri.brailleCells[4:6]
    sm_cells = ri.brailleCells[6:]
    check("indicator on: dots 7+8 under every clickable cell",
          all(c & S.CLICKABLE_INDICATOR_DOTS == S.CLICKABLE_INDICATOR_DOTS
              for c in play_cells))
    check("indicator on: separators untouched",
          all(c & S.CLICKABLE_INDICATOR_DOTS == 0 for c in sep_cells))
    check("indicator on: read-only cells untouched",
          all(c & S.CLICKABLE_INDICATOR_DOTS == 0 for c in sm_cells))

    # --- session lifecycle -------------------------------------------------
    handler = fake.handler
    sess = S.BrailleElementSession()
    prior = _FakeRegion()
    handler.mainBuffer.regions = [prior]
    sess.open([el("Play", "play")])
    check("open replaces the main buffer with our region",
          len(handler.mainBuffer.regions) == 1
          and handler.mainBuffer.regions[0] is not prior)
    check("is_attached true after open", sess.is_attached)
    check("display_dimensions reads the handler", sess.display_dimensions == (40, 1))

    sess.patch("play", "Pause")
    check("patch rewrites the element text",
          handler.mainBuffer.regions[0].rawText == "Pause")
    sess.patch("no_such_id", "X")
    check("patch with unknown id is a no-op",
          handler.mainBuffer.regions[0].rawText == "Pause")

    sess.update([el("A", "a"), el("B", "b")])
    check("update replaces the element list",
          handler.mainBuffer.regions[0].rawText == "A  B")

    sess.dismiss()
    check("dismiss restores the prior regions when we still own the buffer",
          handler.mainBuffer.regions == [prior])
    check("is_attached false after dismiss", not sess.is_attached)

    # clobber case: NVDA repopulated the buffer while we were up
    sess2 = S.BrailleElementSession()
    sess2.open([el("Play", "play")])
    nvda_fresh = _FakeRegion()
    handler.mainBuffer.regions = [nvda_fresh]  # simulate handleGainFocus
    before = handler.update_calls
    sess2.dismiss()
    check("dismiss after clobber leaves NVDA's fresh content alone",
          handler.mainBuffer.regions == [nvda_fresh])
    check("dismiss after clobber does not even repaint",
          handler.update_calls == before)

    # double open guard
    sess3 = S.BrailleElementSession()
    sess3.open([el("A", "a")])
    raised = False
    try:
        sess3.open([el("B", "b")])
    except RuntimeError:
        raised = True
    check("second open without dismiss raises", raised)
    sess3.dismiss()

    # pan plumbs through
    sess4 = S.BrailleElementSession()
    sess4.open([el("A", "a")])
    handler.scrolls.clear()
    sess4.pan(S.PanDirection.FORWARD)
    sess4.pan(S.PanDirection.BACK)
    check("pan calls the handler's scroll entry points",
          handler.scrolls == ["forward", "back"])
    sess4.dismiss()

    print()
    if failures:
        print("FAIL: {} of the design's claims did not hold: {}".format(
            len(failures), ", ".join(failures)))
        return 1
    print("PASS: mapping, separators, contraction, indicator, patch, and "
          "dismiss-after-clobber all behave as the design claims. NVDA "
          "integration and hardware remain the human's test.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

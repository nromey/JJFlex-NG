"""Offline check of the braille trial add-on's pure logic (layout.py).

Runs with no screen reader: it imports layout.py by path, feeds it view strings
exactly as the trial program builds them, and checks the parsing, the one-line
layout, the width rule, the cursor, the cell-to-field mapping and the message
packing.

IT CARRIES ITS OWN POSITIVE CONTROL. Before checking anything real it runs one
check that MUST fail and one that MUST pass. If the harness cannot tell them
apart it refuses to certify anything: a checker that reports nothing looks
exactly like code with nothing wrong.

Usage:  python -I verify_trial_layout.py
"""

import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
LAYOUT_PATH = os.path.join(HERE, "..", "appModules", "jjbrailletrial", "layout.py")

# The trial program's first view string, character for character as
# TrialLine.BuildViewString produces it (tests/TrialLineTests.cs pins the same).
FIRST_VIEW = (
	"JJFV1\t1\t1\thome\t1\t0\t0\n"
	"1\tV\t14.225.000\n"
	"2\tC\tUSB\n"
	"3\tR\tS7\n"
	"4\tT\tVOX off\n"
	"5\tT\tTuning modern"
)


def load_layout():
	spec = importlib.util.spec_from_file_location("jjbt_layout", LAYOUT_PATH)
	mod = importlib.util.module_from_spec(spec)
	spec.loader.exec_module(mod)
	return mod


class Checker(object):
	def __init__(self):
		self.passed = 0
		self.failed = []

	def eq(self, name, actual, expected):
		if actual == expected:
			self.passed += 1
		else:
			self.failed.append("%s: expected %r, got %r" % (name, expected, actual))

	def raises(self, name, exc, fn):
		try:
			fn()
		except exc:
			self.passed += 1
			return
		except Exception as e:  # noqa: BLE001
			self.failed.append("%s: expected %s, got %r" % (name, exc.__name__, e))
			return
		self.failed.append("%s: expected %s, nothing was raised" % (name, exc.__name__))


def self_check():
	c = Checker()
	c.eq("control that must pass", 1 + 1, 2)
	c.eq("control that must fail", 1 + 1, 3)
	return c.passed == 1 and len(c.failed) == 1


def view_with(**changes):
	"""FIRST_VIEW with header parts or field texts replaced."""
	lines = FIRST_VIEW.split("\n")
	header = lines[0].split("\t")
	for key, index in (("revision", 1), ("layout", 2), ("view", 3), ("caret", 4), ("char", 6)):
		if key in changes:
			header[index] = str(changes[key])
	lines[0] = "\t".join(header)
	for ordinal, text in changes.get("texts", {}).items():
		parts = lines[ordinal].split("\t")
		parts[2] = text
		lines[ordinal] = "\t".join(parts)
	return "\n".join(lines)


def run(L):
	c = Checker()

	# Parsing.
	v = L.parse_view(FIRST_VIEW)
	c.eq("revision", v.revision, 1)
	c.eq("layout", v.layout, 1)
	c.eq("view", v.view, "home")
	c.eq("caret ordinal", v.caretOrdinal, 1)
	c.eq("caret character", v.caretChar, 0)
	c.eq("field count", len(v.fields), 5)
	c.eq("field 5", (v.fields[4].ordinal, v.fields[4].kind, v.fields[4].text), (5, "T", "Tuning modern"))

	six = "\t".join(FIRST_VIEW.split("\n")[0].split("\t")[:6]) + FIRST_VIEW[FIRST_VIEW.index("\n"):]
	c.eq("a six-part header parses, caret character unknown", L.parse_view(six).caretChar, None)

	c.raises("unknown version", L.ViewError, lambda: L.parse_view(FIRST_VIEW.replace("JJFV1", "JJFV2", 1)))
	c.raises("empty string", L.ViewError, lambda: L.parse_view(""))
	c.raises("short header", L.ViewError, lambda: L.parse_view("JJFV1\t1\t1\n1\tV\tx"))
	c.raises("unknown view", L.ViewError, lambda: L.parse_view(view_with(view="sideways")))
	c.raises("no fields", L.ViewError, lambda: L.parse_view(FIRST_VIEW.split("\n")[0]))
	c.raises("bad field line", L.ViewError, lambda: L.parse_view(FIRST_VIEW + "\n6\tT"))
	c.raises("duplicate field", L.ViewError, lambda: L.parse_view(FIRST_VIEW + "\n5\tT\tagain"))
	c.raises("non-number revision", L.ViewError, lambda: L.parse_view(view_with(revision="x")))

	# Layout, home view.
	mem = L.WidthMemory()
	line = L.lay_out(v, mem)
	c.eq("raw text", line.rawText, "14.225.000  USB  S7  VOX off  Tuning modern")
	c.eq("cursor on the first digit", line.cursorPos, 0)
	c.eq("slot starts", [s.start for s in line.slots], [0, 12, 17, 21, 30])

	# Cell to field.
	c.eq("first cell", line.route_at(0), (1, 0))
	c.eq("100 Hz digit", line.route_at(7), (1, 7))
	c.eq("last digit", line.route_at(9), (1, 9))
	c.eq("gap after frequency belongs to it, character not known", line.route_at(10), (1, None))
	c.eq("second gap cell", line.route_at(11), (1, None))
	c.eq("mode", line.route_at(13), (2, 1))
	c.eq("VOX first cell", line.route_at(21), (4, 0))
	c.eq("tuning last cell", line.route_at(42), (5, 12))
	c.eq("past the end", line.route_at(43), None)
	c.eq("negative", line.route_at(-1), None)

	# The width rule: VOX off -> on keeps its slot, so tuning does not move.
	on = L.parse_view(view_with(revision=2, texts={4: "VOX on"}))
	line2 = L.lay_out(on, mem)
	c.eq("VOX on padded to the width VOX off had", line2.rawText, "14.225.000  USB  S7  VOX on   Tuning modern")
	c.eq("tuning did not move", line2.slots[4].start, 30)
	c.eq("a press on VOX's padding routes to VOX, character not known", line2.route_at(27), (4, None))
	# And back again: still the same width.
	off = L.parse_view(view_with(revision=3, texts={4: "VOX off"}))
	c.eq("width kept after shrinking back", L.lay_out(off, mem).slots[4].start, 30)
	# A new layout number forgets the widths.
	newLayout = L.parse_view(view_with(revision=4, layout=2, texts={4: "VOX on"}))
	c.eq("new layout forgets widths", L.lay_out(newLayout, mem).slots[4].start, 29)

	# Cursor follows the caret, including the seventh header part.
	caretOn100 = L.parse_view(view_with(revision=5, char=7))
	c.eq("cursor on the 100 Hz digit", L.lay_out(caretOn100, L.WidthMemory()).cursorPos, 7)
	caretOnTuning = L.parse_view(view_with(revision=6, caret=5, char=3))
	c.eq("cursor in the tuning field", L.lay_out(caretOnTuning, L.WidthMemory()).cursorPos, 33)
	badChar = L.parse_view(view_with(revision=7, caret=2, char=99))
	c.eq("caret character outside the field falls back to its first cell", L.lay_out(badChar, L.WidthMemory()).cursorPos, 12)
	c.eq("caret_moved, same place", L.caret_moved(v, L.parse_view(view_with(revision=8))), False)
	c.eq("caret_moved, new place", L.caret_moved(v, caretOn100), True)

	# Field view: only the caret's field.
	fieldView = L.parse_view(view_with(revision=9, view="field", caret=2))
	fl = L.lay_out(fieldView, L.WidthMemory())
	c.eq("field view text", fl.rawText, "USB")
	c.eq("field view route", fl.route_at(2), (2, 2))
	c.eq("field view cursor", fl.cursorPos, 0)
	c.raises("off view shows NVDA's own line", L.ViewError,
		lambda: L.lay_out(L.parse_view(view_with(view="off")), L.WidthMemory()))

	# Message packing, matching BridgeProtocol.cs.
	c.eq("route, 100 Hz digit", L.encode_route(1, 1, 7), (1, 65536 + 8))
	c.eq("route, not known", L.encode_route(1, 4, None), (1, 4 * 65536))
	c.eq("hello, NVDA, 1 by 40", L.encode_hello(1, 40), (65536 + 1, 65536 + 40))
	c.eq("command, switch view", L.encode_command(L.COMMAND_SWITCH_VIEW), (1, 1))
	c.eq("message names", (L.HELLO_MESSAGE_NAME, L.ROUTE_MESSAGE_NAME, L.COMMAND_MESSAGE_NAME),
		("JJFlexBraille_Hello", "JJFlexBraille_Route", "JJFlexBraille_Command"))

	return c


def main():
	if not self_check():
		print("POSITIVE CONTROL FAILED: the checker cannot tell a pass from a failure. Certifying nothing.")
		return 3
	print("Positive control passed: the checker flags a deliberate failure and passes a true check.")
	L = load_layout()
	c = run(L)
	if c.failed:
		print("%d passed, %d FAILED:" % (c.passed, len(c.failed)))
		for f in c.failed:
			print("  " + f)
		return 1
	print("%d checks passed on %s." % (c.passed, os.path.normpath(LAYOUT_PATH)))
	print("This proves the add-on's parsing and mapping, not that NVDA loads it.")
	return 0


if __name__ == "__main__":
	sys.exit(main())

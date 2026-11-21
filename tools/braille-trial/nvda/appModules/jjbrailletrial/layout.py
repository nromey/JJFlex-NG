# layout.py - the braille trial add-on's pure logic.
#
# Parses the view string the trial program publishes on its carrier element
# (braille Home design section 4.3), lays the fields out on one line, and maps
# a position in that line back to a field and a character. Imports nothing from
# NVDA, so tools/braille-trial/nvda/verify/verify_trial_layout.py can run it
# with no screen reader.
#
# One line only: NVDA's own braille buffer pans a line longer than the display,
# which is what the trial's fourth step judges. The page rule of design section
# 3.3 is the real build's, not the trial's.

FORMAT_TAG = "JJFV1"

# Two blank cells between fields (design section 3.3). The gap belongs to the
# field on its left (section 3.5), so a press there routes to that field with
# the character "not known".
GAP = "  "

# Bridge constants. These must match tools/braille-trial/app/Model/BridgeProtocol.cs.
HELLO_MESSAGE_NAME = "JJFlexBraille_Hello"
ROUTE_MESSAGE_NAME = "JJFlexBraille_Route"
COMMAND_MESSAGE_NAME = "JJFlexBraille_Command"
PROTOCOL_VERSION = 1
READER_NVDA = 1
COMMAND_SWITCH_VIEW = 1

VIEWS = ("field", "home", "off")


class ViewError(ValueError):
	"""The view string could not be used. The add-on falls back to NVDA's own line."""


class Field(object):
	__slots__ = ("ordinal", "kind", "text")

	def __init__(self, ordinal, kind, text):
		self.ordinal = ordinal
		self.kind = kind
		self.text = text

	def __repr__(self):
		return "Field(%d, %r, %r)" % (self.ordinal, self.kind, self.text)


class View(object):
	"""One parsed view string."""

	__slots__ = ("revision", "layout", "view", "caretOrdinal", "markClickable", "caretChar", "fields", "raw")

	def __init__(self, revision, layout, view, caretOrdinal, markClickable, caretChar, fields, raw):
		self.revision = revision
		self.layout = layout
		self.view = view
		self.caretOrdinal = caretOrdinal
		self.markClickable = markClickable
		self.caretChar = caretChar  # None when the header has no seventh part
		self.fields = fields
		self.raw = raw

	def field(self, ordinal):
		for f in self.fields:
			if f.ordinal == ordinal:
				return f
		return None


def _int(text, what):
	try:
		return int(text)
	except (TypeError, ValueError):
		raise ViewError("%s is not a number: %r" % (what, text))


def parse_view(text):
	"""Parse a view string. Raises ViewError on anything it cannot use, including
	an unknown format version, so an old add-on meeting a newer program shows the
	reader's own line instead of guessing (design section 4.3)."""
	if not text:
		raise ViewError("the view string is empty")
	lines = text.split("\n")
	header = lines[0].split("\t")
	if header[0] != FORMAT_TAG:
		raise ViewError("unknown format %r" % header[0])
	if len(header) < 6:
		raise ViewError("the header has %d parts, at least 6 are needed" % len(header))
	revision = _int(header[1], "revision")
	layout = _int(header[2], "layout")
	view = header[3]
	if view not in VIEWS:
		raise ViewError("unknown view %r" % view)
	caretOrdinal = _int(header[4], "caret ordinal")
	markClickable = header[5] == "1"
	caretChar = None
	if len(header) >= 7 and header[6] != "":
		caretChar = _int(header[6], "caret character")
	fields = []
	seen = set()
	for line in lines[1:]:
		if line == "":
			continue
		parts = line.split("\t", 2)
		if len(parts) != 3:
			raise ViewError("a field line has %d parts, 3 are needed: %r" % (len(parts), line))
		ordinal = _int(parts[0], "field ordinal")
		if ordinal in seen:
			raise ViewError("field %d appears twice" % ordinal)
		seen.add(ordinal)
		fields.append(Field(ordinal, parts[1], parts[2]))
	if not fields:
		raise ViewError("the view has no fields")
	return View(revision, layout, view, caretOrdinal, markClickable, caretChar, fields, text)


class Slot(object):
	"""Where one field sits in the line: [start, start + width) holds its text,
	padded; [start + width, end) is the gap after it."""

	__slots__ = ("ordinal", "start", "textLength", "width", "end")

	def __init__(self, ordinal, start, textLength, width, end):
		self.ordinal = ordinal
		self.start = start
		self.textLength = textLength
		self.width = width
		self.end = end

	def __repr__(self):
		return "Slot(%d, start=%d, text=%d, width=%d, end=%d)" % (
			self.ordinal, self.start, self.textLength, self.width, self.end)


class Line(object):
	"""The laid-out line: its raw text, the slots, and the cursor."""

	__slots__ = ("rawText", "slots", "cursorPos", "layout", "revision")

	def __init__(self, rawText, slots, cursorPos, layout, revision):
		self.rawText = rawText
		self.slots = slots
		self.cursorPos = cursorPos
		self.layout = layout
		self.revision = revision

	def route_at(self, rawPos):
		"""(ordinal, character or None) for a position in rawText, or None past the end.
		A press on a field's padding or on the gap after it routes to that field
		with the character not known."""
		if rawPos is None or rawPos < 0:
			return None
		for slot in self.slots:
			if slot.start <= rawPos < slot.end:
				ch = rawPos - slot.start
				if ch < slot.textLength:
					return (slot.ordinal, ch)
				return (slot.ordinal, None)
		return None


class WidthMemory(object):
	"""Design section 3.3: each field keeps the widest it has ever been in this
	session, so a toggle going from 'off' to 'on' never moves what follows it.
	Forgotten when the layout number changes."""

	def __init__(self):
		self.layout = None
		self.widths = {}

	def width_for(self, layout, ordinal, textLength):
		if layout != self.layout:
			self.layout = layout
			self.widths = {}
		w = max(self.widths.get(ordinal, 0), textLength)
		self.widths[ordinal] = w
		return w


def lay_out(view, memory):
	"""Lay a parsed view out on one line. In the field view only the caret's
	field is shown; in the home view every field, in order. Raises ViewError
	for the 'off' view, which means 'show the reader's own line'."""
	if view.view == "off":
		raise ViewError("the program asked for its braille to be off")
	if view.view == "field":
		f = view.field(view.caretOrdinal)
		shown = [f] if f is not None else []
	else:
		shown = list(view.fields)
	if not shown:
		raise ViewError("no field to show (caret ordinal %d)" % view.caretOrdinal)
	parts = []
	slots = []
	pos = 0
	cursor = None
	for i, f in enumerate(shown):
		width = memory.width_for(view.layout, f.ordinal, len(f.text))
		padded = f.text + " " * (width - len(f.text))
		gap = GAP if i < len(shown) - 1 else ""
		slots.append(Slot(f.ordinal, pos, len(f.text), width, pos + width + len(gap)))
		if f.ordinal == view.caretOrdinal:
			ch = view.caretChar if view.caretChar is not None else 0
			if ch < 0 or ch >= len(f.text):
				ch = 0
			cursor = pos + ch
		parts.append(padded)
		parts.append(gap)
		pos += width + len(gap)
	return Line("".join(parts), slots, cursor, view.layout, view.revision)


def encode_hello(rows, columns, reader=READER_NVDA, version=PROTOCOL_VERSION):
	"""(wParam, lParam) for Hello. See BridgeProtocol.cs for the packing."""
	return (reader * 65536 + version, rows * 65536 + columns)


def encode_route(layout, ordinal, ch):
	"""(wParam, lParam) for Route. ch None means 'not known' (code 0)."""
	code = 0 if ch is None else ch + 1
	return (layout & 0xFFFFFFFF, (ordinal * 65536 + code) & 0xFFFFFFFF)


def encode_command(command, reader=READER_NVDA):
	return (command, reader)


def caret_moved(old, new):
	"""True when the program's caret is somewhere else in the new view."""
	if old is None:
		return True
	return (old.caretOrdinal, old.caretChar) != (new.caretOrdinal, new.caretChar)

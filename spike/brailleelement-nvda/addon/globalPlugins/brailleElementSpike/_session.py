# brailleElement primitive, NVDA side — Sprint 38 Track G spike.
#
# Descended from the 2026-04-29 Track C prototype (branch
# track/braille-research, docs/planning/track-c/prototype/), revised for the
# six locked v1 decisions in
# memory/project_braille_primitive_v1_decisions.md:
#
#   - opt-in cursor indicator (dots 7+8 OR'd into clickable cell ranges)
#     is IN v1, off by default;
#   - focus dismissal is BOTH event-driven and a 250 ms polling watchdog
#     (the watchdog lives in the plugin, not here — the session only offers
#     dismiss(); policy belongs to the consumer).
#
# And one fix over the prototype: dismiss() no longer blindly restores the
# region list it saved at open(). If focus changed while the session was up,
# NVDA's own handleGainFocus has already repopulated mainBuffer, and
# restoring a stale snapshot would clobber correct content with dead
# content. dismiss() now restores only if our region still owns the buffer,
# and otherwise leaves NVDA's current content alone.
#
# Verified without NVDA by verify/verify_region_logic.py (mapping math,
# separator no-op, patch, indicator dots) and
# verify/verify_nvda_symbols_2026.py (every NVDA name used here exists in
# the installed NVDA 2026.1). NOT yet run inside NVDA — that is a human
# test, spelled out in the spike README.

from dataclasses import dataclass
from enum import Enum
from typing import Callable, List, Optional, Tuple

import braille

# Dots 7 and 8 as liblouis cell bits. NVDA defines no DOT7/DOT8 constants
# (verified against 2026.1 — cursor shapes are raw config ints), so the
# literal is the honest spelling. 0x40 = dot 7, 0x80 = dot 8.
CLICKABLE_INDICATOR_DOTS = 0xC0


@dataclass
class DisplayElement:
	text: str
	id: str
	on_click: Optional[Callable[[str, int], None]] = None


class PanDirection(Enum):
	FORWARD = 1
	BACK = 2


class _ElementRegion(braille.Region):
	"""A Region owning an ordered list of named elements. Cursor-routing
	clicks come back through routeTo and are dispatched to the element
	whose rawText range contains the click."""

	SEPARATOR = "  "  # two spaces; visible gap on real displays

	def __init__(
		self,
		elements: List[DisplayElement],
		separator: str = SEPARATOR,
		indicate_clickable: bool = False,
	):
		super().__init__()
		self._sep = separator
		self.indicate_clickable = indicate_clickable
		self._set_elements(elements)

	def _set_elements(self, elements: List[DisplayElement]) -> None:
		self._elements = list(elements)
		# list[(start_in_rawText, end_in_rawText, element)]
		self._ranges: List[Tuple[int, int, DisplayElement]] = []
		parts: List[str] = []
		cursor = 0
		for i, el in enumerate(self._elements):
			start = cursor
			parts.append(el.text)
			cursor += len(el.text)
			self._ranges.append((start, cursor, el))
			if i < len(self._elements) - 1:
				parts.append(self._sep)
				cursor += len(self._sep)
		self.rawText = "".join(parts)

	def update(self) -> None:
		# Let NVDA translate rawText through liblouis. This populates
		# brailleCells and the position maps (rawToBraillePos,
		# brailleToRawPos) — contracted tables included, which is why we
		# never do our own translation or cell math from character counts.
		super().update()
		if not self.indicate_clickable:
			return
		# Opt-in cursor indicator (locked v1 decision #4): OR dots 7+8
		# into every cell that renders a clickable element, AFTER
		# translation, using the raw->braille map so contraction cannot
		# desynchronise text ranges from cell ranges.
		n_cells = len(self.brailleCells)
		for start, end, el in self._ranges:
			if el.on_click is None:
				continue
			for rawPos in range(start, min(end, len(self.rawToBraillePos))):
				cell = self.rawToBraillePos[rawPos]
				if 0 <= cell < n_cells:
					self.brailleCells[cell] |= CLICKABLE_INDICATOR_DOTS

	def routeTo(self, braillePos: int) -> None:
		# braillePos is the cell offset WITHIN THIS REGION (the buffer has
		# already translated display-window position through
		# windowPosToBufferPos and bufferPosToRegionPos before we are
		# called). Map cell -> rawText offset -> element.
		if not (0 <= braillePos < len(self.brailleToRawPos)):
			return
		rawPos = self.brailleToRawPos[braillePos]
		for start, end, el in self._ranges:
			if start <= rawPos < end:
				if el.on_click is not None:
					el.on_click(el.id, rawPos - start)
				return
		# Click landed on a separator: deliberate no-op (design §5.4).


class BrailleElementSession:
	"""Owns the braille surface between open() and dismiss(). The consumer
	decides WHEN to dismiss (focus policy is the consumer's job — see the
	plugin for the dual event+watchdog pattern)."""

	def __init__(self) -> None:
		self._region: Optional[_ElementRegion] = None
		self._saved_regions: Optional[list] = None

	@property
	def is_attached(self) -> bool:
		return self._region is not None

	@property
	def display_dimensions(self) -> Tuple[int, int]:
		dims = braille.handler.displayDimensions
		return (dims.numCols, dims.numRows)

	def open(
		self,
		elements: List[DisplayElement],
		indicate_clickable: bool = False,
	) -> None:
		if self._region is not None:
			raise RuntimeError("Session already open; dismiss first")
		self._region = _ElementRegion(
			elements, indicate_clickable=indicate_clickable
		)
		self._region.update()
		self._saved_regions = list(braille.handler.mainBuffer.regions)
		braille.handler.mainBuffer.regions = [self._region]
		braille.handler.mainBuffer.update()
		braille.handler.update()

	def update(self, elements: List[DisplayElement]) -> None:
		if self._region is None:
			raise RuntimeError("Session not open; call open() first")
		self._region._set_elements(elements)
		self._repaint()

	def patch(self, element_id: str, new_text: str) -> None:
		if self._region is None:
			raise RuntimeError("Session not open; call open() first")
		for el in self._region._elements:
			if el.id == element_id:
				el.text = new_text
				break
		else:
			return  # unknown id: no-op
		self._region._set_elements(self._region._elements)
		self._repaint()

	def set_indicator(self, on: bool) -> None:
		if self._region is None:
			return
		self._region.indicate_clickable = on
		self._repaint()

	def _repaint(self) -> None:
		self._region.update()
		braille.handler.mainBuffer.update()
		braille.handler.update()

	def dismiss(self) -> None:
		if self._region is None:
			return
		region = self._region
		saved = self._saved_regions
		self._region = None
		self._saved_regions = None
		buf = braille.handler.mainBuffer
		if region in buf.regions:
			# We still own the buffer: put back what was there, then
			# repaint. (If focus changed, NVDA already repopulated and we
			# fall to the else branch — restoring the stale snapshot
			# there would clobber correct content. Prototype bug, fixed.)
			buf.regions = saved if saved is not None else []
			buf.update()
			braille.handler.update()
		# else: NVDA's handleGainFocus already replaced our content;
		# nothing to restore, nothing to repaint.

	def pan(self, direction: PanDirection) -> None:
		if self._region is None:
			return
		if direction == PanDirection.FORWARD:
			braille.handler.scrollForward()
		else:
			braille.handler.scrollBack()

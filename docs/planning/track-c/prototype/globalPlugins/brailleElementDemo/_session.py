# BrailleElementSession + _ElementRegion — the cross-AT primitive (NVDA side).
# This is the prototype. Production version belongs in a standalone repo
# (see cross-at-primitive-design.md §8 question 2).

from dataclasses import dataclass
from enum import Enum
from typing import Callable, List, Optional

import braille


@dataclass
class DisplayElement:
	text: str
	id: str
	on_click: Optional[Callable[[str, int], None]] = None


class PanDirection(Enum):
	FORWARD = 1
	BACK = 2


class _ElementRegion(braille.Region):
	"""A custom Region that owns an ordered list of named elements and
	routes cursor-routing-key clicks back to the element's on_click."""

	SEPARATOR = "  "  # two spaces; visible spacing on most displays

	def __init__(self, elements: List[DisplayElement], separator: str = SEPARATOR):
		super().__init__()
		self._sep = separator
		self._set_elements(elements)

	def _set_elements(self, elements: List[DisplayElement]) -> None:
		self._elements = list(elements)
		self._ranges: List[tuple] = []  # list[(start_in_rawText, end_in_rawText, element)]
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

	def routeTo(self, braillePos: int) -> None:
		# Translate cell-position -> rawText offset -> element.
		if not (0 <= braillePos < len(self.brailleToRawPos)):
			return
		rawPos = self.brailleToRawPos[braillePos]
		for start, end, el in self._ranges:
			if start <= rawPos < end:
				if el.on_click is not None:
					el.on_click(el.id, rawPos - start)
				return
		# Click landed on a separator. Per design, no-op.


class BrailleElementSession:
	"""Owns the braille display surface for the duration of a session.
	Open(), update()/patch() during the session, dismiss() to release."""

	def __init__(self) -> None:
		self._region: Optional[_ElementRegion] = None
		self._saved_regions: Optional[list] = None

	@property
	def is_attached(self) -> bool:
		return self._region is not None

	@property
	def display_dimensions(self) -> tuple:
		dims = braille.handler.displayDimensions
		return (dims.numCols, dims.numRows)

	def open(self, elements: List[DisplayElement]) -> None:
		if self._region is not None:
			raise RuntimeError("Session already open; dismiss first")
		self._region = _ElementRegion(elements)
		self._region.update()
		# Save the prior mainBuffer regions so we can restore on dismiss.
		self._saved_regions = list(braille.handler.mainBuffer.regions)
		braille.handler.mainBuffer.regions = [self._region]
		braille.handler.mainBuffer.update()
		braille.handler.update()

	def update(self, elements: List[DisplayElement]) -> None:
		if self._region is None:
			raise RuntimeError("Session not open; call open() first")
		self._region._set_elements(elements)
		self._region.update()
		braille.handler.mainBuffer.update()
		braille.handler.update()

	def patch(self, element_id: str, new_text: str) -> None:
		if self._region is None:
			raise RuntimeError("Session not open; call open() first")
		# Find and mutate one element in place.
		mutated = False
		for _, _, el in self._region._ranges:
			if el.id == element_id:
				el.text = new_text
				mutated = True
				break
		if not mutated:
			return
		# Rebuild ranges/rawText from the (mutated) elements list.
		self._region._set_elements(list(self._region._elements))
		self._region.update()
		braille.handler.mainBuffer.update()
		braille.handler.update()

	def dismiss(self) -> None:
		if self._region is None:
			return
		# Restore the prior mainBuffer state.
		if self._saved_regions is not None:
			braille.handler.mainBuffer.regions = self._saved_regions
		else:
			braille.handler.mainBuffer.regions = []
		braille.handler.mainBuffer.update()
		braille.handler.update()
		self._region = None
		self._saved_regions = None

	def pan(self, direction: PanDirection) -> None:
		if self._region is None:
			return
		if direction == PanDirection.FORWARD:
			braille.handler.scrollForward()
		else:
			braille.handler.scrollBack()

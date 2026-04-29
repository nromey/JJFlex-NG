# brailleElementDemo — Track C primitive prototype
# Bound to NVDA+Shift+B: toggle a demo session showing "Play Stop Mute".
# Routing into any element speaks "Element clicked: <id>".

import globalPluginHandler
import scriptHandler
import ui

from ._session import BrailleElementSession, DisplayElement


def _on_click(element_id: str, cell_offset: int) -> None:
	# The demo just announces. Real consumers dispatch to their domain logic.
	ui.message("Element clicked: {}".format(element_id))


_DEMO_ELEMENTS = [
	DisplayElement(text="Play", id="play", on_click=_on_click),
	DisplayElement(text="Stop", id="stop", on_click=_on_click),
	DisplayElement(text="Mute", id="mute", on_click=_on_click),
]


class GlobalPlugin(globalPluginHandler.GlobalPlugin):
	scriptCategory = "Braille element demo"

	def __init__(self):
		super().__init__()
		self._session = None

	def terminate(self):
		# Always dismiss on plugin teardown so we don't leave a dangling region.
		if self._session is not None and self._session.is_attached:
			self._session.dismiss()
		self._session = None
		super().terminate()

	@scriptHandler.script(
		description="Toggle the braille element demo (Play, Stop, Mute)",
		category="Braille element demo",
		gesture="kb:NVDA+shift+b",
	)
	def script_toggleDemo(self, gesture):
		if self._session is None:
			self._session = BrailleElementSession()
		if self._session.is_attached:
			self._session.dismiss()
			ui.message("Braille element demo dismissed")
		else:
			self._session.open(_DEMO_ELEMENTS)
			ui.message("Braille element demo opened. Route to act.")

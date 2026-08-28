# brailleElementSpike — Sprint 38 Track G research spike.
#
# Answers, on real hardware, the milestone question in the operator's own
# words: "see if for example we can write a sample line and determine if
# cursor routing shows where you click."
#
# NVDA+Shift+L toggles a demo session that paints four elements:
#
#     14.250  USB  fartsnoodle  Mute
#
# A cursor-routing key over any element speaks "clicked <name>" plus the
# cell offset within the element. "clicked fartsnoodle" is verbatim the
# acceptance sentence from the 2026-05-06 Track C handoff.
#
# NVDA+Control+Shift+L toggles the opt-in clickable indicator (dots 7+8
# under clickable cells) while a session is open — locked v1 decision #4.
#
# Gesture note: the 2026-04-29 prototype used NVDA+Shift+B, which is a
# BUILT-IN binding in current NVDA (report battery status; verified against
# the installed 2026.1 bytecode). Global plugin scripts shadow built-ins, so
# keeping it would have silently stolen a working key. L is free of
# built-ins (scanned the same way) and mnemonic for "line". Rebindable
# under Input Gestures either way.
#
# Focus dismissal is BOTH paths, deliberately (locked v1 decision #5):
#   - event-driven: event_gainFocus dismisses when focus moves;
#   - a 250 ms polling watchdog dismisses if the focus object changed
#     without the event reaching us.
# Either alone would usually work; both together is the decision.

import api
import globalPluginHandler
import scriptHandler
import ui
import wx

import braille

from ._session import BrailleElementSession, DisplayElement

WATCHDOG_MS = 250  # locked v1 decision #5; do not "optimise" away


def _speak_click(element_id: str, cell_offset: int) -> None:
	# The demo announces; a real consumer dispatches to domain logic.
	ui.message("clicked {} (offset {})".format(element_id, cell_offset))


def _demo_elements():
	return [
		DisplayElement(text="14.250", id="frequency", on_click=_speak_click),
		DisplayElement(text="USB", id="mode", on_click=_speak_click),
		DisplayElement(text="fartsnoodle", id="fartsnoodle", on_click=_speak_click),
		DisplayElement(text="Mute", id="mute", on_click=_speak_click),
	]


class GlobalPlugin(globalPluginHandler.GlobalPlugin):
	scriptCategory = "JJ Flexible braille element spike"

	def __init__(self):
		super().__init__()
		self._session = BrailleElementSession()
		self._indicator = False
		self._focus_at_open = None
		self._watchdog = wx.PyTimer(self._watchdog_tick)

	def terminate(self):
		self._dismiss("plugin terminating")
		super().terminate()

	# --- dismissal, both paths -------------------------------------------

	def _dismiss(self, why: str) -> None:
		self._watchdog.Stop()
		self._focus_at_open = None
		if self._session.is_attached:
			self._session.dismiss()

	def event_gainFocus(self, obj, nextHandler):
		# Event path: any focus movement while the session is up ends it.
		if self._session.is_attached and obj is not self._focus_at_open:
			self._dismiss("focus event")
		nextHandler()

	def _watchdog_tick(self):
		# Polling path: catches focus changes whose events never reached
		# us. Deliberately redundant with event_gainFocus.
		if not self._session.is_attached:
			self._watchdog.Stop()
			return
		try:
			current = api.getFocusObject()
		except Exception:
			return
		if current is not self._focus_at_open:
			self._dismiss("watchdog")

	# --- gestures --------------------------------------------------------

	@scriptHandler.script(
		description="Toggle the braille element spike line (14.250, USB, fartsnoodle, Mute)",
		category="JJ Flexible braille element spike",
		gesture="kb:NVDA+shift+l",
	)
	def script_toggleSpike(self, gesture):
		if self._session.is_attached:
			self._dismiss("toggled off")
			ui.message("Braille element spike dismissed")
			return
		if braille.handler.displaySize == 0:
			ui.message(
				"No braille display cells available. Session opened anyway; "
				"use the braille viewer or connect a display."
			)
		self._focus_at_open = api.getFocusObject()
		self._session.open(_demo_elements(), indicate_clickable=self._indicator)
		self._watchdog.Start(WATCHDOG_MS)
		cols, rows = self._session.display_dimensions
		ui.message(
			"Braille element spike open, {} cells. Route to act.".format(cols * rows)
		)

	@scriptHandler.script(
		description="Toggle the clickable-cell indicator (dots 7 and 8) on the spike line",
		category="JJ Flexible braille element spike",
		gesture="kb:NVDA+control+shift+l",
	)
	def script_toggleIndicator(self, gesture):
		self._indicator = not self._indicator
		self._session.set_indicator(self._indicator)
		ui.message(
			"Clickable indicator {}".format("on" if self._indicator else "off")
		)

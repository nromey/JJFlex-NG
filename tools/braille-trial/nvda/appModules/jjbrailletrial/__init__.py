# jjbrailletrial - the braille trial's NVDA app module (register #347).
#
# THROWAWAY TRIAL CODE, loaded from NVDA's developer scratchpad only while the
# trial program jjbrailletrial.exe runs. It is not the shipped add-on.
#
# What it does, following the braille Home design sections 4.4 and 4.6:
# - Recognises the trial's Home control by its automation id, JJFlexHome, and
#   gives it an overlay class whose getBrailleRegions returns one custom region.
# - Reads the view string from the carrier element, JJFlexBrailleView, on a
#   background thread with its own UI Automation client, so NVDA's main thread
#   never waits on the program. Also listens for the carrier's name-change
#   event, and logs which of the two actually delivered, because the design
#   expects the event never to arrive (NVDA registers for property changes on
#   the focus and its ancestors only, and the carrier is neither).
# - Paints every field on one line; NVDA's own buffer pans it.
# - Keeps NVDA's cursor on the program's caret.
# - On a routing press, works out field and character and posts the Route
#   message to the program's window. Posts Hello when Home gains focus.
#
# Rules for code inside the operator's screen reader (design section 4.6):
# any error makes getBrailleRegions raise NotImplementedError so NVDA shows
# its normal line; nothing on the main thread waits on the program; nothing is
# monkey-patched. Everything it does is logged at info level with the prefix
# "jjbrailletrial:".

import ctypes
import ctypes.wintypes
import threading

import api
import appModuleHandler
import braille
import core
import queueHandler
from logHandler import log
from NVDAObjects.UIA import UIA
from scriptHandler import script

try:
	# NVDA 2026.3 and later: braille is a package and Region lives here.
	from braille.regions.base import Region
except ImportError:
	# Older NVDA: braille is one module.
	from braille import Region

from . import layout

PREFIX = "jjbrailletrial: "
HOME_AUTOMATION_ID = "JJFlexHome"
CARRIER_AUTOMATION_ID = "JJFlexBrailleView"
SCRIPT_CATEGORY = "JJ Flexible braille trial"

# How often the background thread reads the carrier, in seconds.
POLL_SECONDS = 0.1

# Our own user32 instance, so setting argtypes here never changes the
# function objects the rest of NVDA uses through ctypes.windll.user32.
_user32 = ctypes.WinDLL("user32", use_last_error=True)
_RegisterWindowMessage = _user32.RegisterWindowMessageW
_RegisterWindowMessage.argtypes = [ctypes.wintypes.LPCWSTR]
_RegisterWindowMessage.restype = ctypes.wintypes.UINT
_PostMessage = _user32.PostMessageW
_PostMessage.argtypes = [ctypes.wintypes.HWND, ctypes.wintypes.UINT, ctypes.wintypes.WPARAM, ctypes.wintypes.LPARAM]
_PostMessage.restype = ctypes.wintypes.BOOL

_messages = {}


def _message(name):
	msg = _messages.get(name)
	if not msg:
		msg = _RegisterWindowMessage(name)
		_messages[name] = msg
	return msg


def _post(hwnd, name, wParam, lParam):
	"""Post, never send: the reader never waits on the program."""
	msg = _message(name)
	if not msg or not hwnd:
		return False, 0
	ok = bool(_PostMessage(hwnd, msg, wParam, lParam))
	return ok, (0 if ok else ctypes.get_last_error())


def _displaySize():
	try:
		dims = braille.handler.displayDimensions
		return int(dims.numRows), int(dims.numCols)
	except Exception:
		try:
			return 1, int(braille.handler.displaySize)
		except Exception:
			return 0, 0


class CarrierWatcher(threading.Thread):
	"""Reads the carrier's Name off NVDA's main thread and hands each new value
	to the main thread through NVDA's event queue."""

	def __init__(self, hwnd, deliver):
		super().__init__(name="jjbrailletrial carrier watcher", daemon=True)
		self._hwnd = hwnd
		self._deliver = deliver
		self._stopEvent = threading.Event()
		self._wakeEvent = threading.Event()
		self.wakes = 0

	def stop(self):
		self._stopEvent.set()
		self._wakeEvent.set()

	def wake(self):
		"""Read now. Called when NVDA did deliver a name-change event for the carrier."""
		self.wakes += 1
		self._wakeEvent.set()

	def run(self):
		import comtypes
		from comtypes import CLSCTX_INPROC_SERVER, COMError, CoCreateInstance
		from comInterfaces import UIAutomationClient as UIA_

		comtypes.CoInitializeEx(comtypes.COINIT_MULTITHREADED)
		try:
			client = CoCreateInstance(
				UIA_.CUIAutomation._reg_clsid_,
				interface=UIA_.IUIAutomation,
				clsctx=CLSCTX_INPROC_SERVER,
			)
			try:
				# A stalled program must not hold this thread for long either.
				c2 = client.QueryInterface(UIA_.IUIAutomation2)
				c2.ConnectionTimeout = 1000
				c2.TransactionTimeout = 1000
			except Exception:
				pass
			# The carrier is in neither the control nor the content view, so the
			# search must run in the raw view: FindFirst on its own would apply
			# the control view and never find it.
			cache = client.CreateCacheRequest()
			cache.TreeFilter = client.RawViewCondition
			cache.AddProperty(UIA_.UIA_NamePropertyId)
			condition = client.CreatePropertyCondition(UIA_.UIA_AutomationIdPropertyId, CARRIER_AUTOMATION_ID)
			carrier = None
			last = None
			misses = 0
			while not self._stopEvent.is_set():
				woken = self._wakeEvent.is_set()
				self._wakeEvent.clear()
				try:
					if carrier is None:
						root = client.ElementFromHandle(self._hwnd)
						carrier = root.FindFirstBuildCache(UIA_.TreeScope_Descendants, condition, cache)
						if carrier is None:
							misses += 1
							if misses in (1, 10, 100):
								log.info(PREFIX + "carrier %s not found under window 0x%X (try %d)" % (
									CARRIER_AUTOMATION_ID, self._hwnd, misses))
						else:
							log.info(PREFIX + "carrier found under window 0x%X" % self._hwnd)
					if carrier is not None:
						text = carrier.CurrentName
						if text != last:
							last = text
							queueHandler.queueFunction(
								queueHandler.eventQueue, self._deliver, text, "event" if woken else "poll")
				except COMError as e:
					log.info(PREFIX + "reading the carrier failed (%s); will look for it again" % (e,))
					carrier = None
				except Exception:
					log.exception(PREFIX + "carrier watcher error")
					carrier = None
				self._wakeEvent.wait(POLL_SECONDS)
		except Exception:
			log.exception(PREFIX + "carrier watcher could not start; NVDA's own line stays")
		finally:
			try:
				comtypes.CoUninitialize()
			except Exception:
				pass


class TrialRegion(Region):
	"""The one braille region: every field on one line, cursor at the caret,
	routing presses posted to the program."""

	def __init__(self, obj, appMod):
		super().__init__()
		# BrailleHandler.handleUpdate finds a region by its obj.
		self.obj = obj
		self.pendingCaretUpdate = False
		self._appMod = appMod
		self.line = None

	def update(self):
		line = self._appMod.currentLine
		self.line = line
		if line is None:
			self.rawText = ""
			self.cursorPos = None
		else:
			self.rawText = line.rawText
			self.cursorPos = line.cursorPos
		super().update()

	def routeTo(self, braillePos):
		try:
			self._appMod.routeFromRegion(self, braillePos)
		except Exception:
			log.exception(PREFIX + "route failed")


class TrialHome(UIA):
	"""Overlay for the trial's Home control."""

	def event_gainFocus(self):
		try:
			self.appModule.homeGainedFocus(self)
		except Exception:
			log.exception(PREFIX + "focus handling failed")
		super().event_gainFocus()

	def event_loseFocus(self):
		try:
			self.appModule.homeLostFocus(self)
		except Exception:
			log.exception(PREFIX + "lose focus handling failed")
		super().event_loseFocus()

	def getBrailleRegions(self, review=False):
		if review:
			raise NotImplementedError
		try:
			if self.appModule.currentLine is None:
				raise NotImplementedError
			return [TrialRegion(self, self.appModule)]
		except NotImplementedError:
			raise
		except Exception:
			log.exception(PREFIX + "getBrailleRegions failed; NVDA's own line is shown")
			raise NotImplementedError

	@script(
		description="Braille trial: switch between the Home view and the field view",
		category=SCRIPT_CATEGORY,
	)
	def script_switchView(self, gesture):
		hwnd = self.windowHandle
		w, l = layout.encode_command(layout.COMMAND_SWITCH_VIEW)
		ok, err = _post(hwnd, layout.COMMAND_MESSAGE_NAME, w, l)
		log.info(PREFIX + "command: switch view posted to 0x%X, ok=%s error=%d" % (hwnd or 0, ok, err))


class AppModule(appModuleHandler.AppModule):

	def __init__(self, *args, **kwargs):
		super().__init__(*args, **kwargs)
		self.currentView = None
		self.currentLine = None
		self._memory = layout.WidthMemory()
		self._watcher = None
		self._hwnd = 0
		self._counts = {"poll": 0, "event": 0, "nameChangeEvents": 0}
		log.info(PREFIX + "app module loaded (Region from %s)" % Region.__module__)

	def terminate(self):
		self._stopWatcher()
		log.info(PREFIX + "app module unloaded; carrier updates by poll %d, after an event %d; name-change events %d" % (
			self._counts["poll"], self._counts["event"], self._counts["nameChangeEvents"]))
		super().terminate()

	def chooseNVDAObjectOverlayClasses(self, obj, clsList):
		if isinstance(obj, UIA):
			try:
				automationId = obj.UIAAutomationId
			except Exception:
				return
			if automationId == HOME_AUTOMATION_ID:
				clsList.insert(0, TrialHome)

	def event_nameChange(self, obj, nextHandler):
		# The carrier is neither control nor content and must never be spoken.
		# If NVDA does deliver its name changes, wake the watcher and stop here.
		if isinstance(obj, UIA):
			try:
				isCarrier = obj.UIAAutomationId == CARRIER_AUTOMATION_ID
			except Exception:
				isCarrier = False
			if isCarrier:
				self._counts["nameChangeEvents"] += 1
				n = self._counts["nameChangeEvents"]
				if n <= 3 or n % 50 == 0:
					log.info(PREFIX + "name-change event for the carrier arrived (%d so far)" % n)
				if self._watcher is not None:
					self._watcher.wake()
				return
		nextHandler()

	# -------------------------------------------------------------- focus

	def homeGainedFocus(self, obj):
		self._hwnd = obj.windowHandle or 0
		rows, cols = _displaySize()
		w, l = layout.encode_hello(rows, cols)
		ok, err = _post(self._hwnd, layout.HELLO_MESSAGE_NAME, w, l)
		log.info(PREFIX + "Home has focus; hello posted to window 0x%X, display %d row(s) of %d cells, ok=%s error=%d" % (
			self._hwnd, rows, cols, ok, err))
		self._startWatcher()

	def homeLostFocus(self, obj):
		self._stopWatcher()
		log.info(PREFIX + "Home lost focus; watcher stopped")

	def _startWatcher(self):
		self._stopWatcher()
		if not self._hwnd:
			log.info(PREFIX + "Home has no window handle; NVDA's own line stays")
			return
		self._watcher = CarrierWatcher(self._hwnd, self._onCarrierText)
		self._watcher.start()

	def _stopWatcher(self):
		if self._watcher is not None:
			self._watcher.stop()
			self._watcher = None

	# --------------------------------------------------------- the line

	def _onCarrierText(self, text, how):
		"""Main thread, via NVDA's event queue. Never waits on the program."""
		focus = api.getFocusObject()
		if not isinstance(focus, TrialHome):
			return
		old = self.currentView
		if old is not None and text == old.raw:
			return
		try:
			view = layout.parse_view(text)
			line = layout.lay_out(view, self._memory)
		except layout.ViewError as e:
			hadOurs = self.currentLine is not None
			self.currentView = None
			self.currentLine = None
			log.info(PREFIX + "view not usable (%s); NVDA's own line is shown" % e)
			if hadOurs:
				braille.handler.handleGainFocus(focus)
			return
		self._counts[how] += 1
		n = self._counts[how]
		if n <= 3 or n % 50 == 0:
			log.info(PREFIX + "carrier update by %s (%d so far), revision %d, %d characters: %r" % (
				how, n, view.revision, len(text), line.rawText))
		first = self.currentLine is None
		self.currentView = view
		self.currentLine = line
		if first:
			# Our region does not exist yet; rebuild the focus regions so it does.
			braille.handler.handleGainFocus(focus)
			log.info(PREFIX + "line painted: %r, cursor at %s" % (line.rawText, line.cursorPos))
			return
		braille.handler.handleUpdate(focus)
		if layout.caret_moved(old, view):
			core.callLater(30, self._followCaret)

	def _followCaret(self):
		"""If the program's caret moved off the part of the line on the display,
		pan to it. A value changing never pans; only a caret move does."""
		try:
			buf = braille.handler.mainBuffer
			region = buf.regions[-1] if buf.regions else None
			if not isinstance(region, TrialRegion) or region.brailleCursorPos is None:
				return
			if buf.cursorWindowPos is not None:
				return
			buf.scrollTo(region, region.brailleCursorPos)
			buf.updateDisplay()
			log.info(PREFIX + "panned to follow the caret")
		except Exception:
			log.exception(PREFIX + "follow caret failed")

	# ------------------------------------------------------------ routing

	def routeFromRegion(self, region, braillePos):
		line = region.line
		if line is None:
			log.info(PREFIX + "routing press at cell %d with no line; ignored" % braillePos)
			return
		try:
			rawPos = region.brailleToRawPos[braillePos]
		except (IndexError, TypeError):
			log.info(PREFIX + "routing press at cell %d is past the line; ignored" % braillePos)
			return
		target = line.route_at(rawPos)
		if target is None:
			log.info(PREFIX + "routing press at cell %d (character %d) is past the last field; ignored" % (braillePos, rawPos))
			return
		ordinal, ch = target
		w, l = layout.encode_route(line.layout, ordinal, ch)
		ok, err = _post(self._hwnd, layout.ROUTE_MESSAGE_NAME, w, l)
		log.info(PREFIX + "route: cell %d -> line character %d -> field %d, character %s, layout %d, revision %d; posted to 0x%X ok=%s error=%d" % (
			braillePos, rawPos, ordinal, "not known" if ch is None else ch, line.layout, line.revision,
			self._hwnd, ok, err))

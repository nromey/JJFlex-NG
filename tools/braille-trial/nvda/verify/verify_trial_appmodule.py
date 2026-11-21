"""Exercise the trial app module's glue code against stand-ins for NVDA.

verify_trial_layout.py proves the pure logic. This one loads the real
appModules/jjbrailletrial/__init__.py with small fake NVDA modules in place of
the real ones, and walks the path a session takes: Home gains focus (Hello is
posted), the carrier's text arrives (our region replaces NVDA's line), a value
changes (the region is updated in place, no new focus), a routing press lands
(Route is posted with the right numbers), an unusable view hands the line back
to NVDA, and a review-mode request is refused. It catches the mistakes a
syntax check cannot: a misspelt attribute, a wrong argument, a call into NVDA
that the real code does not make.

It does NOT prove NVDA loads the module or that NVDA calls it this way; the
fakes encode our reading of NVDA 2026.3.1's source. The carrier-reading thread
is not started (it needs UI Automation and a running program).

Positive control: the same walk is run against a deliberately broken copy of
the route encoder, and must fail.

Usage:  python -I verify_trial_appmodule.py
"""

import importlib.util
import os
import sys
import types

HERE = os.path.dirname(os.path.abspath(__file__))
PKG_DIR = os.path.join(HERE, "..", "appModules", "jjbrailletrial")

FIRST_VIEW = (
	"JJFV1\t1\t1\thome\t1\t0\t0\n"
	"1\tV\t14.225.000\n"
	"2\tC\tUSB\n"
	"3\tR\tS7\n"
	"4\tT\tVOX off\n"
	"5\tT\tTuning modern"
)


def install_fakes(record):
	def mod(name, **attrs):
		m = types.ModuleType(name)
		for k, v in attrs.items():
			setattr(m, k, v)
		sys.modules[name] = m
		return m

	class Region(object):
		def __init__(self):
			self.rawText = ""
			self.cursorPos = None
			self.brailleCells = []
			self.brailleToRawPos = []
			self.rawToBraillePos = []
			self.brailleCursorPos = None

		def update(self):
			# Uncontracted, one cell per character: enough to test the mapping.
			self.brailleCells = [ord(c) for c in self.rawText]
			self.brailleToRawPos = list(range(len(self.rawText)))
			self.rawToBraillePos = list(range(len(self.rawText)))
			self.brailleCursorPos = self.cursorPos

	class Dims(object):
		numRows = 1
		numCols = 40

	class Buffer(object):
		def __init__(self):
			self.regions = []
			self.cursorWindowPos = 0

	class Handler(object):
		displayDimensions = Dims()
		displaySize = 40

		def __init__(self):
			self.mainBuffer = Buffer()

		def handleGainFocus(self, obj):
			record.append(("handleGainFocus", obj))
			self.mainBuffer.regions = []
			try:
				regions = obj.getBrailleRegions(review=False)
			except NotImplementedError:
				record.append(("nvdaOwnLine",))
				return
			for r in regions:
				r.update()
				self.mainBuffer.regions.append(r)

		def handleUpdate(self, obj):
			record.append(("handleUpdate", obj))
			for r in self.mainBuffer.regions:
				if getattr(r, "obj", None) == obj:
					r.update()

	braille = mod("braille", handler=Handler())
	braille.__path__ = []
	regions = mod("braille.regions")
	regions.__path__ = []
	mod("braille.regions.base", Region=Region)
	braille.regions = regions

	class Log(object):
		def info(self, msg, *a):
			record.append(("log", msg))

		def exception(self, msg, *a):
			record.append(("logException", msg))
			raise AssertionError("the app module logged an exception: " + msg)

	mod("logHandler", log=Log())
	focus = {"obj": None}
	mod("api", getFocusObject=lambda: focus["obj"])

	class AppModule(object):
		def __init__(self, *a, **k):
			pass

		def terminate(self):
			pass

	mod("appModuleHandler", AppModule=AppModule)
	mod("core", callLater=lambda delay, fn, *a, **k: record.append(("callLater", delay)))
	mod("queueHandler", eventQueue=object(), queueFunction=lambda q, fn, *a, **k: fn(*a, **k))

	class UIA(object):
		UIAAutomationId = ""
		windowHandle = 0x1234

		def event_gainFocus(self):
			record.append(("super.event_gainFocus",))

		def event_loseFocus(self):
			pass

	nvdaObjects = mod("NVDAObjects")
	nvdaObjects.__path__ = []
	mod("NVDAObjects.UIA", UIA=UIA)

	def script(**kwargs):
		return lambda f: f

	mod("scriptHandler", script=script)
	return focus, UIA


def load_app_module(breakEncoder=False):
	pkgName = "appModules.jjbrailletrial"
	for name in list(sys.modules):
		if name.startswith("appModules"):
			del sys.modules[name]
	parent = types.ModuleType("appModules")
	parent.__path__ = [os.path.join(PKG_DIR, "..")]
	sys.modules["appModules"] = parent
	spec = importlib.util.spec_from_file_location(
		pkgName, os.path.join(PKG_DIR, "__init__.py"), submodule_search_locations=[PKG_DIR])
	m = importlib.util.module_from_spec(spec)
	sys.modules[pkgName] = m
	spec.loader.exec_module(m)
	if breakEncoder:
		m.layout.encode_route = lambda layoutNo, ordinal, ch: (layoutNo, ordinal * 65536)
	return m


def walk(breakEncoder=False):
	record = []
	focus, UIA = install_fakes(record)
	m = load_app_module(breakEncoder)
	posts = []
	m._post = lambda hwnd, name, w, l: (posts.append((hwnd, name, w, l)), (True, 0))[1]
	failures = []

	def check(name, actual, expected):
		if actual != expected:
			failures.append("%s: expected %r, got %r" % (name, expected, actual))

	app = m.AppModule(4321)
	app._startWatcher = lambda: record.append(("watcherStarted",))

	class HomeBase(UIA):
		UIAAutomationId = "JJFlexHome"

	clsList = [HomeBase]
	app.chooseNVDAObjectOverlayClasses(HomeBase(), clsList)
	check("overlay chosen for JJFlexHome", clsList[0], m.TrialHome)
	other = [UIA]
	app.chooseNVDAObjectOverlayClasses(UIA(), other)
	check("no overlay for anything else", other, [UIA])

	home = type("Home", (m.TrialHome, HomeBase), {})()
	home.appModule = app
	focus["obj"] = home

	# Focus arrives before any view: NVDA's own line, Hello posted.
	home.event_gainFocus()
	check("hello posted", posts[-1], (0x1234, "JJFlexBraille_Hello", 65536 + 1, 65536 + 40))
	try:
		home.getBrailleRegions(review=False)
		failures.append("getBrailleRegions with no view did not raise NotImplementedError")
	except NotImplementedError:
		pass

	# First carrier text: our region replaces NVDA's line.
	app._onCarrierText(FIRST_VIEW, "poll")
	buf = sys.modules["braille"].handler.mainBuffer
	check("one region of ours", [type(r).__name__ for r in buf.regions], ["TrialRegion"])
	region = buf.regions[0]
	check("painted line", region.rawText, "14.225.000  USB  S7  VOX off  Tuning modern")
	check("cursor at the caret", region.cursorPos, 0)

	# A value change updates in place: handleUpdate, not a new focus.
	record.clear()
	changed = FIRST_VIEW.replace("\t1\t1\thome", "\t2\t1\thome", 1).replace("14.225.000", "14.225.100")
	app._onCarrierText(changed, "poll")
	check("update in place", [r[0] for r in record if r[0].startswith("handle")], ["handleUpdate"])
	check("new frequency painted", region.rawText[:10], "14.225.100")
	check("no pan for a value change", [r for r in record if r[0] == "callLater"], [])

	# A caret move asks for the follow-caret check.
	record.clear()
	moved = changed.replace("\t2\t1\thome\t1\t0\t0", "\t3\t1\thome\t1\t0\t7", 1)
	app._onCarrierText(moved, "event")
	check("caret move schedules follow", [r for r in record if r[0] == "callLater"], [("callLater", 30)])
	check("cursor on the 100 Hz digit", region.cursorPos, 7)

	# Routing presses.
	region.routeTo(7)
	check("route on the 100 Hz digit", posts[-1], (0x1234, "JJFlexBraille_Route", 1, 65536 + 8))
	region.routeTo(31)
	check("route on the tuning toggle", posts[-1], (0x1234, "JJFlexBraille_Route", 1, 5 * 65536 + 2))
	region.routeTo(11)
	check("route on the gap after frequency", posts[-1], (0x1234, "JJFlexBraille_Route", 1, 65536))
	before = len(posts)
	region.routeTo(500)
	check("press past the line posts nothing", len(posts), before)

	# Review mode is never ours.
	try:
		home.getBrailleRegions(review=True)
		failures.append("review mode did not raise NotImplementedError")
	except NotImplementedError:
		pass

	# An unusable view hands the line back to NVDA.
	record.clear()
	app._onCarrierText("JJFV9\tgarbage", "poll")
	check("unusable view rebuilds with NVDA's own line", [r[0] for r in record if r[0] in ("handleGainFocus", "nvdaOwnLine")],
		["handleGainFocus", "nvdaOwnLine"])

	# Name-change events for the carrier are swallowed; others pass on.
	passed = []

	class Carrier(UIA):
		UIAAutomationId = "JJFlexBrailleView"

	app.event_nameChange(Carrier(), lambda: passed.append("carrier"))
	app.event_nameChange(UIA(), lambda: passed.append("other"))
	check("carrier name change not passed on", passed, ["other"])

	# The switch-view script posts Command.
	home.script_switchView(None)
	check("command posted", posts[-1], (0x1234, "JJFlexBraille_Command", 1, 1))
	return failures


def main():
	broken = walk(breakEncoder=True)
	if not any("route on the 100 Hz digit" in f for f in broken):
		print("POSITIVE CONTROL FAILED: a broken route encoder was not caught. Certifying nothing.")
		return 3
	print("Positive control passed: a broken route encoder is caught.")
	failures = walk()
	if failures:
		print("%d FAILED:" % len(failures))
		for f in failures:
			print("  " + f)
		return 1
	print("The app module's focus, paint, update, caret-follow, routing, fallback and command paths behave as intended against the stand-ins.")
	print("This does not prove NVDA loads it or calls it this way.")
	return 0


if __name__ == "__main__":
	sys.exit(main())

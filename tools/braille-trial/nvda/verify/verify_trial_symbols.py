"""Check that every NVDA name the trial app module relies on exists in the
NVDA installed on this machine.

NVDA ships its modules as .pyc inside library.zip. The bytecode cannot be
loaded by a different Python, but every identifier a code object uses is stored
as plain text in it, so a name can be proven present (the method of
tools/readercapture/verify/verify_nvda_symbols.py). It proves the names exist
in the right modules, not that the app module loads or behaves.

POSITIVE CONTROL: a name that is certainly present and one that cannot exist
are checked first; if the scanner cannot tell them apart it certifies nothing.

Usage:  python -I verify_trial_symbols.py [path\\to\\NVDA]
"""

import os
import sys
import zipfile

# module inside library.zip -> names the app module uses from it
REQUIRED = {
	# 2026.3 layout first; the app module falls back to braille.Region on older NVDA.
	"braille/regions/base.pyc": ["Region", "routeTo", "brailleToRawPos", "rawText", "cursorPos", "brailleCursorPos"],
	"braille/regions/focus.pyc": ["getFocusRegions", "getBrailleRegions", "NotImplementedError"],
	"braille/brailleHandler.pyc": ["handleUpdate", "handleGainFocus", "displayDimensions", "displaySize", "mainBuffer", "routeTo"],
	"braille/buffers.pyc": ["scrollTo", "_get_cursorWindowPos", "updateDisplay", "regions", "saveWindow", "restoreWindow"],
	"braille/display/__init__.pyc": ["numRows", "numCols"],
	"NVDAObjects/UIA/__init__.pyc": ["UIA", "UIAAutomationId", "event_gainFocus", "event_loseFocus"],
	"appModuleHandler.pyc": ["AppModule", "chooseNVDAObjectOverlayClasses", "terminate"],
	"queueHandler.pyc": ["queueFunction", "eventQueue"],
	"core.pyc": ["callLater"],
	"api.pyc": ["getFocusObject"],
	"scriptHandler.pyc": ["script"],
	"logHandler.pyc": ["log"],
	"comInterfaces/UIAutomationClient.pyc": [
		"CUIAutomation", "IUIAutomation", "IUIAutomation2",
		"UIA_NamePropertyId", "UIA_AutomationIdPropertyId", "TreeScope_Descendants",
	],
	# The COM methods live in the generated module the stub above re-exports.
	"comInterfaces/_944DE083_8FB8_45CF_BCB7_C477ACB2F897_0_1_0.pyc": [
		"FindFirstBuildCache", "CreateCacheRequest", "RawViewCondition", "ElementFromHandle",
		"CreatePropertyCondition", "CurrentName", "TreeFilter", "AddProperty",
		"ConnectionTimeout", "TransactionTimeout",
	],
	"comtypes/__init__.pyc": ["CoInitializeEx", "CoUninitialize", "COINIT_MULTITHREADED", "CLSCTX_INPROC_SERVER", "CoCreateInstance", "COMError"],
}

CONTROL_PRESENT = ("api.pyc", "getFocusObject")
CONTROL_ABSENT = ("api.pyc", "thisNameIsNotInNvdaAnywhereAtAll")

def find_nvda(explicit=None):
	if explicit:
		return explicit
	for cand in (r"C:\Program Files\NVDA", r"C:\Program Files (x86)\NVDA",
			os.path.expandvars(r"%LOCALAPPDATA%\Programs\NVDA")):
		if os.path.isfile(os.path.join(cand, "library.zip")):
			return cand
	return None


class _Names(object):
	"""Membership test against a module's bytecode. Marshal stores a short
	identifier as a type byte, a one-byte length and the characters, so a name
	is present when its length byte followed by its text is in the data. (A
	plain identifier regex merges a name with the next string's type byte.)"""

	def __init__(self, data):
		self._data = data

	def __contains__(self, name):
		raw = name.encode("ascii")
		return len(raw) < 256 and (bytes([len(raw)]) + raw) in self._data


def names_in(zf, module):
	try:
		data = zf.read(module)
	except KeyError:
		return None
	return _Names(data)


def main(argv):
	root = find_nvda(argv[1] if len(argv) > 1 else None)
	if not root:
		print("SKIP: no installed NVDA found. This is not a pass.")
		return 2
	zf = zipfile.ZipFile(os.path.join(root, "library.zip"))

	present = names_in(zf, CONTROL_PRESENT[0]) or _Names(b"")
	absent = names_in(zf, CONTROL_ABSENT[0]) or _Names(b"")
	if CONTROL_PRESENT[1] not in present or CONTROL_ABSENT[1] in absent:
		print("POSITIVE CONTROL FAILED: the scanner cannot tell a present name from an absent one. Certifying nothing.")
		return 3
	print("Positive control passed: %s found, %s not found." % (CONTROL_PRESENT[1], CONTROL_ABSENT[1]))

	problems = []
	checked = 0
	for module, wanted in REQUIRED.items():
		names = names_in(zf, module)
		if names is None:
			problems.append("module %s is not in this NVDA" % module)
			continue
		for name in wanted:
			checked += 1
			if name not in names:
				problems.append("%s: %s not found" % (module, name))
	if problems:
		print("%d problem(s) in %s:" % (len(problems), root))
		for p in problems:
			print("  " + p)
		return 1
	print("All %d names found in %s." % (checked, root))
	print("This proves the names exist, not that the app module loads.")
	return 0


if __name__ == "__main__":
	sys.exit(main(sys.argv))

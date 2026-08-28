"""jjfcapture - NVDA half of the JJ Flexible reader-side capture instrument.

WHAT THIS IS FOR
----------------
JJ Flexible's own trace records what the application SENT. It writes "Spoke"
whether or not anything was ever heard. This plugin records what NVDA RECEIVED
and what NVDA actually EMITTED, with timestamps, so the two records can be laid
side by side and the question "did the reader get it and drop it, or did it
never arrive?" can be answered by reading rather than by listening.

It is a debugging instrument for the developers and for testers who agree to run
it. It is NOT something an operator installs in order to use JJ Flexible.

WHAT IT HOOKS, AND WHY EACH ONE
-------------------------------
speech.extensions.pre_speech        every sequence NVDA accepted for speaking
speech.extensions.speechCanceled    NVDA threw queued speech away
braille.handler.message             a braille message NVDA was handed
braille.pre_writeCells              what actually reached the display

The pair that answers the open fault: an utterance that appears under
"speech received" and is followed immediately by "speech canceled" was
received and dropped. An utterance that appears in JJ Flexible's trace and
in NEITHER of those never arrived at all. Those need opposite fixes.

KEYS (all rebindable in NVDA's Input Gestures, under "JJ Flexible capture")
    NVDA+shift+j            copy the capture to the clipboard
    NVDA+control+shift+j    pause or resume capturing
    NVDA+alt+j              run the positive control
    NVDA+shift+k            plant a marker at this instant
"""

import globalPluginHandler
import scriptHandler
import ui
import api
import core

from ._record import (
    CaptureLog,
    CH_SPEECH,
    CH_BRAILLE,
    EV_RECEIVED,
    EV_EMITTED,
    EV_CANCELED,
)

SCRIPT_CATEGORY = "JJ Flexible capture"


def _nvda_version():
    try:
        import buildVersion
        return getattr(buildVersion, "version", "") or ""
    except Exception:
        return ""


def _sequence_text(sequence):
    """Plain text of a speech sequence, plus the non-text commands it carried."""
    words = []
    commands = []
    try:
        for item in sequence:
            if isinstance(item, str):
                words.append(item)
            else:
                commands.append(type(item).__name__)
    except Exception:
        return "", []
    return " ".join(w for w in words if w), commands


def _focus_app():
    try:
        obj = api.getForegroundObject()
        mod = getattr(obj, "appModule", None)
        return getattr(mod, "appName", None)
    except Exception:
        return None


def _sleeping():
    try:
        focus = api.getFocusObject()
        return bool(getattr(focus, "sleepMode", False))
    except Exception:
        return None


class GlobalPlugin(globalPluginHandler.GlobalPlugin):
    scriptCategory = SCRIPT_CATEGORY

    def __init__(self):
        super(GlobalPlugin, self).__init__()
        self.log = CaptureLog(reader="nvda", reader_version=_nvda_version())
        self._hooks = {}
        self._orig_braille_message = None
        self._attach()
        self.log.session_start(self._hooks)

    # ------------------------------------------------------------- attach

    def _attach(self):
        # Every hook is attached independently and its success recorded. A hook
        # that silently failed to attach is the likeliest cause of an empty
        # capture, so it is never allowed to fail quietly.
        try:
            from speech.extensions import pre_speech
            pre_speech.register(self._on_pre_speech)
            self._hooks["speech.pre_speech"] = True
        except Exception as exc:
            self._hooks["speech.pre_speech"] = repr(exc)

        try:
            from speech.extensions import speechCanceled
            speechCanceled.register(self._on_speech_canceled)
            self._hooks["speech.speechCanceled"] = True
        except Exception as exc:
            self._hooks["speech.speechCanceled"] = repr(exc)

        try:
            import braille
            braille.pre_writeCells.register(self._on_pre_write_cells)
            self._hooks["braille.pre_writeCells"] = True
        except Exception as exc:
            self._hooks["braille.pre_writeCells"] = repr(exc)

        # braille.handler.message() is where nvdaController_brailleMessage
        # lands, which is the door JJ Flexible's braille comes through. There
        # is no extension point on it, so it is wrapped and restored on
        # terminate. The wrapper never swallows the call.
        try:
            import braille
            handler = braille.handler
            self._orig_braille_message = handler.message

            def wrapped(text, *args, **kwargs):
                try:
                    self.log.add(CH_BRAILLE, EV_RECEIVED, text=text,
                                 via="handler.message", focus=_focus_app())
                except Exception:
                    pass
                return self._orig_braille_message(text, *args, **kwargs)

            handler.message = wrapped
            self._hooks["braille.handler.message"] = True
        except Exception as exc:
            self._orig_braille_message = None
            self._hooks["braille.handler.message"] = repr(exc)

    def terminate(self):
        try:
            from speech.extensions import pre_speech
            pre_speech.unregister(self._on_pre_speech)
        except Exception:
            pass
        try:
            from speech.extensions import speechCanceled
            speechCanceled.unregister(self._on_speech_canceled)
        except Exception:
            pass
        try:
            import braille
            braille.pre_writeCells.unregister(self._on_pre_write_cells)
        except Exception:
            pass
        if self._orig_braille_message is not None:
            try:
                import braille
                braille.handler.message = self._orig_braille_message
            except Exception:
                pass
            self._orig_braille_message = None
        try:
            self.log.close()
        except Exception:
            pass
        super(GlobalPlugin, self).terminate()

    # -------------------------------------------------------------- hooks

    def _on_pre_speech(self, speechSequence=None, **kwargs):
        try:
            text, commands = _sequence_text(speechSequence or [])
            priority = kwargs.get("priority")
            self.log.add(
                CH_SPEECH, EV_RECEIVED,
                text=text,
                commands=commands or None,
                priority=str(priority) if priority is not None else None,
                symbolLevel=str(kwargs.get("symbolLevel")) if kwargs.get("symbolLevel") is not None else None,
                focus=_focus_app(),
                sleeping=_sleeping(),
            )
        except Exception:
            pass

    def _on_speech_canceled(self, **kwargs):
        try:
            self.log.add(CH_SPEECH, EV_CANCELED, text="", focus=_focus_app())
        except Exception:
            pass

    def _on_pre_write_cells(self, cells=None, rawText=None, currentCellCount=None, **kwargs):
        try:
            self.log.add(
                CH_BRAILLE, EV_EMITTED,
                text=rawText if rawText is not None else "",
                cells=len(cells) if cells is not None else None,
                cellCount=currentCellCount,
            )
        except Exception:
            pass

    # ------------------------------------------------------------ scripts

    @scriptHandler.script(
        description="Copy the reader capture to the clipboard",
        category=SCRIPT_CATEGORY,
        gesture="kb:NVDA+shift+j",
    )
    def script_copyCapture(self, gesture):
        text = self.log.render()
        try:
            api.copyToClip(text)
        except Exception:
            ui.message("Could not copy the capture to the clipboard.")
            return
        if self.log.control_passed:
            ui.message("Capture copied. Positive control passed.")
        else:
            # Said out loud on purpose. The single most expensive mistake this
            # instrument could cause is someone trusting an unvalidated capture.
            ui.message("Capture copied. No positive control passed, so an empty "
                       "capture proves nothing.")

    @scriptHandler.script(
        description="Pause or resume the reader capture",
        category=SCRIPT_CATEGORY,
        gesture="kb:NVDA+control+shift+j",
    )
    def script_toggleCapture(self, gesture):
        on = not self.log.enabled
        self.log.set_enabled(on)
        ui.message("Reader capture on." if on else "Reader capture paused.")

    @scriptHandler.script(
        description="Plant a marker in the reader capture",
        category=SCRIPT_CATEGORY,
        gesture="kb:NVDA+shift+k",
    )
    def script_marker(self, gesture):
        self.log.marker("operator marker")
        ui.message("Marker.")

    @scriptHandler.script(
        description="Run the positive control: speak a known token and prove it was captured",
        category=SCRIPT_CATEGORY,
        gesture="kb:NVDA+alt+j",
    )
    def script_positiveControl(self, gesture):
        import random
        token = "jjfcap%04d" % random.randint(0, 9999)
        # scope="internal": this proves the hooks are attached and recording.
        # It does NOT prove that NVDA's external controller RPC is reachable,
        # because NVDA does not ship the controller client library. Say so in
        # the record rather than letting a reader over-read the result.
        self.log.control_begin(token, scope="internal", how="speech.speakText")
        try:
            import speech
            speech.speakText(token)
        except Exception as exc:
            self.log.error("positive control", repr(exc))
            ui.message("Positive control could not emit. The instrument is broken.")
            return
        core.callLater(1200, self._finishControl)

    def _finishControl(self):
        rec = self.log.control_check()
        if rec and rec.get("passed"):
            ui.message("Positive control passed. The capture is recording.")
        else:
            ui.message("Positive control FAILED. The capture is not recording. "
                       "Do not trust an empty result.")

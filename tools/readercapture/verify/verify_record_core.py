"""Exercise the capture core - the code that actually ships inside the NVDA plugin.

This runs `_record.py` itself, not a copy of it, against a scratch directory,
and asserts the properties the instrument's honesty depends on:

  * a capture with no control says so, in its first line
  * a capture whose control FAILED says so, and says not to trust it
  * a capture whose control passed names the token and the channel
  * a token planted before the control was armed does NOT count as a pass
  * what is written to disk reads back identically as JSON
  * the ring trims from the oldest end and the file keeps everything
  * hooks that failed to attach are reported, not swallowed

The harness carries its own positive control: it first proves the assertion
machinery can fail, so a run of all-passes is not just an empty loop.

Usage:  python verify_record_core.py
"""

import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "nvda", "globalPlugins", "jjfcapture"))

import _record  # noqa: E402


FAILURES = []


def check(label, condition, detail=""):
    if condition:
        print("  pass  %s" % label)
    else:
        print("  FAIL  %s %s" % (label, detail))
        FAILURES.append(label)
    return bool(condition)


def harness_control():
    """Prove the harness can report a failure before trusting its passes."""
    saved = list(FAILURES)
    del FAILURES[:]
    print("Harness positive control:")
    ok_true = check("a true assertion passes", True)
    check("a false assertion is recorded as a failure", False, "(expected, this line is the control)")
    caught = len(FAILURES) == 1
    del FAILURES[:]
    FAILURES.extend(saved)
    if not (ok_true and caught):
        print("HARNESS BROKEN: it cannot tell a pass from a failure. Nothing below means anything.")
        return False
    print("  control passed: the harness records failures.\n")
    return True


def main():
    if not harness_control():
        return 3

    tmp = tempfile.mkdtemp(prefix="jjfcap-verify-")
    try:
        # --- no control ---------------------------------------------------
        print("A capture with no positive control:")
        log = _record.CaptureLog("nvda", "2026.1", directory=tmp, ring=5)
        log.session_start({"speech.pre_speech": True,
                           "braille.pre_writeCells": "AttributeError('gone')"})
        log.add(_record.CH_SPEECH, _record.EV_RECEIVED, text="slice B 7.255 USB")
        header = "\n".join(log.header_lines())
        check("leads with the no-control warning",
              header.startswith("NO POSITIVE CONTROL RAN"))
        check("names the hook that did attach", "speech.pre_speech" in header)
        check("names the hook that did NOT attach",
              "Hooks NOT attached" in header and "braille.pre_writeCells" in header)
        rendered = log.render()
        check("the utterance is in the rendered text", "slice B 7.255 USB" in rendered)
        path = log.path
        log.close()

        # --- file round trip ----------------------------------------------
        print("What reached disk:")
        recs = _record.read_jsonl(path)
        check("every record was written", len(recs) == 2, "(got %d)" % len(recs))
        check("the utterance survived the round trip",
              any(r.get("text") == "slice B 7.255 USB" for r in recs))
        check("records carry a wall clock and a monotonic offset",
              all("t" in r and "mono" in r for r in recs))
        check("sequence numbers are strictly increasing",
              [r["seq"] for r in recs] == sorted(set(r["seq"] for r in recs)))

        # --- control passes -------------------------------------------------
        print("A capture whose control passed:")
        log = _record.CaptureLog("nvda", "2026.1", directory=tmp)
        log.session_start({"speech.pre_speech": True})
        log.control_begin("jjfcap1234", scope="internal", how="speech.speakText")
        log.add(_record.CH_SPEECH, _record.EV_RECEIVED, text="jjfcap1234")
        log.control_check()
        check("control is reported as passed", log.control_passed)
        header = "\n".join(log.header_lines())
        check("header leads with the pass", header.startswith("Positive control passed"))
        check("header names the token", "jjfcap1234" in header)
        check("header names the channel", "speech" in header.split("\n")[0])
        log.close()

        # --- control fails ---------------------------------------------------
        print("A capture whose control failed:")
        log = _record.CaptureLog("nvda", "2026.1", directory=tmp)
        log.session_start({"speech.pre_speech": True})
        log.control_begin("jjfcap9999", scope="internal", how="speech.speakText")
        log.add(_record.CH_SPEECH, _record.EV_RECEIVED, text="something else entirely")
        log.control_check()
        check("control is reported as failed", not log.control_passed)
        header = "\n".join(log.header_lines())
        check("header leads with the failure", header.startswith("POSITIVE CONTROL FAILED"))
        check("header tells the reader to draw no conclusions",
              "Draw no conclusions" in header)
        log.close()

        # --- a token that predates the arming must not count ------------------
        print("A token that arrived BEFORE the control was armed:")
        log = _record.CaptureLog("nvda", "2026.1", directory=tmp)
        log.add(_record.CH_SPEECH, _record.EV_RECEIVED, text="jjfcap4242")
        log.control_begin("jjfcap4242", scope="internal", how="speech.speakText")
        log.control_check()
        check("stale token does not count as a pass", not log.control_passed)
        log.close()

        # --- ring trimming ----------------------------------------------------
        print("Ring and file under load:")
        log = _record.CaptureLog("nvda", "2026.1", directory=tmp, ring=4)
        for i in range(20):
            log.add(_record.CH_SPEECH, _record.EV_RECEIVED, text="utterance %d" % i)
        kept = log.records()
        check("the ring holds only its size", len(kept) == 4, "(got %d)" % len(kept))
        check("the ring keeps the NEWEST records",
              kept[-1]["text"] == "utterance 19")
        check("the ring drops the OLDEST records",
              all(r["text"] != "utterance 0" for r in kept))
        p2 = log.path
        log.close()
        on_disk = _record.read_jsonl(p2)
        check("the file kept everything the ring dropped",
              sum(1 for r in on_disk if r.get("ch") == _record.CH_SPEECH) == 20,
              "(got %d)" % sum(1 for r in on_disk if r.get("ch") == _record.CH_SPEECH))

        # --- braille and speech share one timeline ----------------------------
        print("Speech and braille share one timeline:")
        log = _record.CaptureLog("jaws", "26.0.0", directory=tmp)
        a = log.add(_record.CH_BRAILLE, _record.EV_RECEIVED, text="7.255 USB")
        b = log.add(_record.CH_SPEECH, _record.EV_RECEIVED, text="7.255 USB")
        check("both channels are in the same record stream",
              a["seq"] < b["seq"] and a["mono"] <= b["mono"])
        check("the reader is named on every record",
              a["reader"] == "jaws" and b["reader"] == "jaws")
        log.close()

    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print()
    if FAILURES:
        print("%d check(s) failed: %s" % (len(FAILURES), ", ".join(FAILURES)))
        return 1
    print("All checks passed. This exercises the record core only. It says "
          "nothing about whether the reader hooks fire, which can only be "
          "learned by running the instrument inside a screen reader.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

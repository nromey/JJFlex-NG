"""Reader-agnostic capture core for the JJ Flexible reader-side instrument.

This module knows nothing about NVDA or JAWS. It owns the record format, the
JSON Lines writer, the in-memory ring, the positive-control bookkeeping and the
pasteable rendering. It is deliberately importable by a plain CPython so the
offline harness in ``tools/readercapture/verify/`` can exercise the same code
that runs inside the screen reader.

THE ONE RULE THIS FILE EXISTS TO ENFORCE
----------------------------------------
An instrument that records nothing is indistinguishable from a reader that
received nothing. So a capture is not evidence until a positive control has
passed inside the SAME session. ``render()`` refuses to present a capture as
meaningful unless it can name a control that passed, and says so in the first
line the reader hears.
"""

from __future__ import annotations

import datetime
import json
import os
import queue
import threading
import time

RECORD_FORMAT_VERSION = 1

# Channels
CH_SPEECH = "speech"
CH_BRAILLE = "braille"
CH_META = "meta"

# Events. "received" means the reader was handed it. "emitted" means the reader
# actually pushed it at the human (synth or display). The gap between those two
# on one utterance is the whole point of this instrument.
EV_RECEIVED = "received"
EV_EMITTED = "emitted"
EV_CANCELED = "canceled"
EV_SESSION = "session"
EV_MARKER = "marker"
EV_SELFTEST = "selftest"
EV_ERROR = "error"


def _now_wall():
    # Local time WITH offset, milliseconds. JJTrace stamps local time, and this
    # record has to line up against it by eye as well as by machine.
    dt = datetime.datetime.now().astimezone()
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + ("%03d" % (dt.microsecond // 1000)) + dt.strftime("%z")


class CaptureLog(object):
    """Append-only capture with a bounded in-memory ring and a JSONL file."""

    def __init__(self, reader, reader_version="", directory=None, ring=4000,
                 write_file=True, clock=None, monoclock=None):
        self.reader = reader
        self.reader_version = reader_version
        self.ring_size = int(ring)
        self._records = []
        self._lock = threading.RLock()
        self._seq = 0
        self._clock = clock or _now_wall
        self._monoclock = monoclock or (lambda: time.monotonic())
        self._t0 = self._monoclock()
        self._enabled = True

        # Positive-control state. Nothing here is optional decoration; render()
        # reads it and changes what it claims.
        self._control_token = None
        self._control_started_mono = None
        self._control_started_seq = 0
        self._control_result = None  # dict once resolved

        self.path = None
        self._q = None
        self._thread = None
        self._stop = None
        if write_file:
            self.path = self._open_file(directory)

    # ---------------------------------------------------------------- file

    def _default_dir(self):
        base = os.environ.get("LOCALAPPDATA") or os.environ.get("TEMP") or "."
        return os.path.join(base, "jjfcapture")

    def _open_file(self, directory):
        directory = directory or self._default_dir()
        try:
            os.makedirs(directory, exist_ok=True)
        except Exception:
            return None
        # Milliseconds and pid, not just seconds. Two sessions started inside
        # one second used to append to the same file, which silently merged two
        # captures into one and made the record lie about what happened.
        now = datetime.datetime.now()
        stamp = now.strftime("%Y%m%d-%H%M%S") + ("%03d" % (now.microsecond // 1000))
        path = os.path.join(directory, "jjfcapture-%s-%s-%d.jsonl"
                            % (self.reader, stamp, os.getpid()))
        self._q = queue.Queue()
        self._stop = threading.Event()
        self._thread = threading.Thread(
            target=self._writer_loop, args=(path,), name="jjfcapture-writer", daemon=True
        )
        self._thread.start()
        return path

    def _writer_loop(self, path):
        # File I/O never happens on the reader's speech thread. A stuttering
        # screen reader is a changed screen reader, and this instrument has to
        # observe without perturbing.
        try:
            fh = open(path, "a", encoding="utf-8", newline="\n")
        except Exception:
            return
        try:
            while True:
                try:
                    item = self._q.get(timeout=0.25)
                except queue.Empty:
                    if self._stop.is_set():
                        break
                    continue
                if item is None:
                    break
                try:
                    fh.write(item + "\n")
                    fh.flush()
                except Exception:
                    pass
        finally:
            try:
                fh.close()
            except Exception:
                pass

    # -------------------------------------------------------------- record

    def add(self, ch, ev, text=None, **fields):
        if not self._enabled and ev not in (EV_SESSION, EV_SELFTEST):
            return None
        with self._lock:
            self._seq += 1
            rec = {
                "v": RECORD_FORMAT_VERSION,
                "seq": self._seq,
                "t": self._clock(),
                "mono": int(round((self._monoclock() - self._t0) * 1000)),
                "reader": self.reader,
                "ch": ch,
                "ev": ev,
            }
            if text is not None:
                rec["text"] = text
            for k, v in fields.items():
                if v is not None:
                    rec[k] = v
            self._records.append(rec)
            if len(self._records) > self.ring_size:
                del self._records[: len(self._records) - self.ring_size]
        if self._q is not None:
            try:
                self._q.put_nowait(json.dumps(rec, ensure_ascii=False, default=repr))
            except Exception:
                pass
        return rec

    def session_start(self, hooks, extra=None):
        """Record which hooks actually attached.

        A hook that failed to attach is the single most likely reason a capture
        comes back empty, so it is recorded at the top of the file and repeated
        in the rendered header rather than left in a log nobody reads.
        """
        payload = {
            "reader_version": self.reader_version,
            "hooks": dict(hooks),
            "pid": os.getpid(),
            "format": RECORD_FORMAT_VERSION,
        }
        if extra:
            payload.update(extra)
        return self.add(CH_META, EV_SESSION, text="capture session started", **payload)

    def marker(self, label):
        """Operator-planted landmark: 'the thing I am about to test starts here'."""
        return self.add(CH_META, EV_MARKER, text=str(label))

    def error(self, where, detail):
        return self.add(CH_META, EV_ERROR, text=str(detail), where=str(where))

    # ----------------------------------------------------- positive control

    def control_begin(self, token, scope, how):
        """Arm the positive control.

        ``scope`` says honestly how much the control proves:
          "internal" - it proves the capture hooks are attached and recording.
          "ingress"  - it entered by the same external door the application uses,
                       so it also proves that door is open.
        """
        with self._lock:
            self._control_token = token
            self._control_started_mono = self._monoclock()
            self._control_started_seq = self._seq
            self._control_result = None
        return self.add(CH_META, EV_SELFTEST, text="positive control armed",
                        token=token, scope=scope, how=how, phase="begin")

    def control_check(self, window_seconds=3.0):
        """Resolve the armed control by looking for its token in the ring."""
        with self._lock:
            token = self._control_token
            started = self._control_started_mono
            if token is None:
                return None
            # Only records that arrived AFTER the control was armed count. A
            # token found in older traffic would be a coincidence dressed up as
            # a proof. Sequence, not milliseconds: two records inside one
            # millisecond used to let a stale token pass.
            arm_seq = self._control_started_seq
            seen = []
            for rec in self._records:
                if rec.get("ev") == EV_SELFTEST:
                    continue
                if rec.get("seq", 0) <= arm_seq:
                    continue
                text = rec.get("text") or ""
                if token in text:
                    seen.append(rec.get("ch"))
            elapsed = self._monoclock() - (started or self._monoclock())
            passed = bool(seen)
            self._control_result = {
                "token": token,
                "passed": passed,
                "channels": sorted(set(seen)),
                "elapsed_ms": int(round(elapsed * 1000)),
                "window_s": window_seconds,
            }
            result = dict(self._control_result)
        return self.add(CH_META, EV_SELFTEST, text="positive control resolved",
                        phase="result", **result)

    @property
    def control_passed(self):
        return bool(self._control_result and self._control_result.get("passed"))

    # ------------------------------------------------------------ control

    @property
    def enabled(self):
        return self._enabled

    def set_enabled(self, on):
        self._enabled = bool(on)
        return self.add(CH_META, EV_SESSION,
                        text="capture enabled" if self._enabled else "capture paused")

    def records(self):
        with self._lock:
            return list(self._records)

    def clear(self):
        with self._lock:
            self._records = []
            self._control_token = None
            self._control_result = None

    def close(self):
        if self._stop is not None:
            self._stop.set()
        if self._q is not None:
            try:
                self._q.put_nowait(None)
            except Exception:
                pass
        if self._thread is not None:
            try:
                self._thread.join(timeout=2.0)
            except Exception:
                pass

    # ------------------------------------------------------------- render

    def header_lines(self):
        """The first thing a reader hears. It leads with the honesty verdict."""
        lines = []
        res = self._control_result
        if res is None:
            lines.append(
                "NO POSITIVE CONTROL RAN IN THIS SESSION. An empty or short "
                "capture proves nothing: a broken instrument and a silent reader "
                "look identical here. Run the control, then capture again."
            )
        elif not res.get("passed"):
            lines.append(
                "POSITIVE CONTROL FAILED. Token %s was emitted and never came "
                "back through the hooks, so this instrument is not recording. "
                "Draw no conclusions from anything below."
                % res.get("token")
            )
        else:
            lines.append(
                "Positive control passed. Token %s came back on %s after %d ms, "
                "so the hooks below were live for this session."
                % (res.get("token"), ", ".join(res.get("channels") or ["nothing"]),
                   res.get("elapsed_ms", -1))
            )
        lines.append("Reader: %s %s" % (self.reader, self.reader_version or "(version unknown)"))
        lines.append("Record format: version %d, JSON Lines." % RECORD_FORMAT_VERSION)
        if self.path:
            lines.append("Full machine-readable capture: %s" % self.path)
        sess = None
        for rec in self._records:
            if rec.get("ev") == EV_SESSION and "hooks" in rec:
                sess = rec
                break
        if sess:
            hooks = sess.get("hooks") or {}
            attached = sorted(k for k, v in hooks.items() if v is True)
            missing = sorted(k for k, v in hooks.items() if v is not True)
            lines.append("Hooks attached: %s" % (", ".join(attached) if attached else "none"))
            if missing:
                lines.append("Hooks NOT attached: %s" % ", ".join(missing))
        return lines

    def render(self, limit=None):
        """Plain text for the clipboard. No tables, no columns, no art."""
        recs = self.records()
        if limit:
            recs = recs[-int(limit):]
        out = ["JJ Flexible reader capture"]
        out.extend(self.header_lines())
        out.append("Records below: %d." % len(recs))
        out.append("")
        for rec in recs:
            out.append(self.render_record(rec))
        return "\n".join(out)

    @staticmethod
    def render_record(rec):
        bits = ["%s  +%dms  %s %s" % (rec.get("t"), rec.get("mono", 0),
                                      rec.get("ch"), rec.get("ev"))]
        text = rec.get("text")
        if text is not None:
            bits.append("text: %s" % text)
        for key in ("priority", "reason", "cells", "cursor", "token", "scope",
                    "how", "passed", "channels", "where", "focus", "sleeping"):
            if key in rec:
                bits.append("%s: %s" % (key, rec[key]))
        return " | ".join(bits)


def read_jsonl(path):
    """Read a capture file back. Used by the offline harness and by anything
    that wants to diff a capture against the application's own trace."""
    out = []
    with open(path, "r", encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            out.append(json.loads(line))
    return out

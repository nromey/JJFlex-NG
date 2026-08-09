"""Minimal raw-wire client for a FlexRadio's TCP command channel (port 4992).

Bench tooling for the audio-workshop transverter verification (plan item 1b in
docs/planning/active/audio-workshop-plan.md).

SAFETY INVARIANT, DELIBERATE AND LOAD-BEARING: this module never transmits.
There is no `xmit` command anywhere in this file or its callers, and there must
never be one. Keying is the operator's job, by hand mic. Configuration is ours.

Wire protocol, as much of it as we need:
  send     C<seq>|<command>\n
  reply    R<seq>|<hexcode>|<message>\n
  status   S<handle>|<object> <key>=<value> ...
  message  M<code>|<text>
  banner   V<version> and H<handle> arrive unsolicited at connect.
"""

import socket
import threading
import time

DEFAULT_RADIO = "192.168.50.100"
DEFAULT_PORT = 4992


class FlexWire:
    def __init__(self, host=DEFAULT_RADIO, port=DEFAULT_PORT, timeout=10.0):
        self.host = host
        self.port = port
        self.timeout = timeout
        self._sock = None
        self._seq = 0
        self._replies = {}
        self._status = []
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._reader = None
        self.handle = None
        self.version = None

    # -- lifecycle ---------------------------------------------------------

    def connect(self):
        self._sock = socket.create_connection((self.host, self.port), self.timeout)
        self._sock.settimeout(0.5)
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()
        # The banner (V.../H...) arrives on its own; give it a moment.
        deadline = time.time() + 3.0
        while time.time() < deadline and self.handle is None:
            time.sleep(0.05)
        return self

    def close(self):
        self._stop.set()
        if self._sock:
            try:
                self._sock.close()
            except OSError:
                pass
        self._sock = None

    def __enter__(self):
        return self.connect()

    def __exit__(self, *_):
        self.close()

    # -- plumbing ----------------------------------------------------------

    def _read_loop(self):
        buf = b""
        while not self._stop.is_set():
            try:
                chunk = self._sock.recv(8192)
            except socket.timeout:
                continue
            except OSError:
                break
            if not chunk:
                break
            buf += chunk
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                self._handle_line(line.decode("utf-8", "replace").strip("\r"))

    def _handle_line(self, line):
        if not line:
            return
        tag, body = line[0], line[1:]
        if tag == "R":
            parts = body.split("|", 2)
            if len(parts) >= 2:
                seq = parts[0]
                code = parts[1]
                msg = parts[2] if len(parts) > 2 else ""
                with self._lock:
                    self._replies[seq] = (code, msg)
        elif tag in ("S", "M"):
            with self._lock:
                self._status.append(line)
        elif tag == "V":
            self.version = body
        elif tag == "H":
            self.handle = body

    # -- commands ----------------------------------------------------------

    def send(self, command, wait=True):
        """Send one command. Returns (code, message); code '0' means success."""
        self._seq += 1
        seq = str(self._seq)
        self._sock.sendall(f"C{seq}|{command}\n".encode())
        if not wait:
            return (None, None)
        deadline = time.time() + self.timeout
        while time.time() < deadline:
            with self._lock:
                if seq in self._replies:
                    return self._replies.pop(seq)
            time.sleep(0.02)
        raise TimeoutError(f"no reply to: {command}")

    def drain_status(self, seconds=2.0):
        """Collect status lines for a while, then return and clear them."""
        time.sleep(seconds)
        with self._lock:
            out = list(self._status)
            self._status.clear()
        return out

    def subscribe_all(self):
        for what in ("radio all", "slice all", "tx all", "xvtr all", "atu all"):
            try:
                self.send(f"sub {what}")
            except TimeoutError:
                pass


def ok(result, what):
    code, msg = result
    status = "OK" if code == "0" else f"ERROR {code}"
    detail = f" ({msg})" if msg else ""
    print(f"  {what}: {status}{detail}")
    return code == "0"

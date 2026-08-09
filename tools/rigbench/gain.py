"""Receiver RF gain stepper for the ears slice.

  python tools\\rigbench\\gain.py [slice_index]      # defaults to slice 1

The radio takes six discrete settings, 8 dB apart, spanning 40 dB:

    -8   0   8   16   24   32

  Up / Down     one step (8 dB)
  Home          bottom, -8
  End           top, +32
  Q or Escape   quit

Counting is easy here: there are only six positions, and the script says
where it is. Every change is logged to gain-log.txt next to this script.

Why this exists: FlexLib cannot set RF gain at all. Slice.cs:213 builds the
command as "slice set" + index (no space), emitting `slice set1 rfgain=24`,
which the radio silently discards. Present in the 4.2.20 API drop too, so it
is a vendor bug, not ours. Sent correctly the radio parses and validates it.

THIS SCRIPT NEVER TRANSMITS.
"""

import datetime
import msvcrt
import os
import sys

from flexwire import FlexWire, DEFAULT_RADIO

STEPS = [-8, 0, 8, 16, 24, 32]
LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "gain-log.txt")


def log(line):
    stamp = datetime.datetime.now().strftime("%H:%M:%S")
    with open(LOG_PATH, "a", encoding="utf-8") as fh:
        fh.write(f"{stamp}  {line}\n")


def read_gain(wire, index, subscribe=True):
    if subscribe:
        wire.send("sub slice all")
    for line in wire.drain_status(1.5):
        body = line.split("|", 1)[-1]
        if body.startswith(f"slice {index} "):
            for token in body.split():
                if token.startswith("rfgain="):
                    try:
                        return int(float(token.split("=", 1)[1]))
                    except ValueError:
                        return None
    return None


def main():
    args = [a for a in sys.argv[1:] if a.count(".") != 3]
    index = args[0] if args else "1"
    host = next((a for a in sys.argv[1:] if a.count(".") == 3), DEFAULT_RADIO)

    print(__doc__)
    print(f"Slice {index} on {host}\n")

    with FlexWire(host) as wire:
        current = read_gain(wire, index)
        if current is None:
            print(f"Could not read slice {index}'s RF gain. Is that slice in use?")
            return 1

        pos = min(range(len(STEPS)), key=lambda i: abs(STEPS[i] - current))
        print(f"START  rfgain {STEPS[pos]} dB   (position {pos + 1} of {len(STEPS)})")
        log(f"--- session start, slice {index}, rfgain {STEPS[pos]}")

        while True:
            ch = msvcrt.getch()
            if ch in (b"q", b"Q", b"\x1b", b"\x03"):
                break
            if ch not in (b"\x00", b"\xe0"):
                continue

            code = msvcrt.getch()
            if code == b"H":       # up
                new_pos = min(pos + 1, len(STEPS) - 1)
            elif code == b"P":     # down
                new_pos = max(pos - 1, 0)
            elif code == b"G":     # home
                new_pos = 0
            elif code == b"O":     # end
                new_pos = len(STEPS) - 1
            else:
                continue

            if new_pos == pos:
                edge = "bottom" if pos == 0 else "top"
                print(f"       at the {edge} already, {STEPS[pos]} dB")
                continue

            rc, rmsg = wire.send(f"slice set {index} rfgain={STEPS[new_pos]}")
            if rc != "0":
                print(f"       {STEPS[new_pos]} dB refused: {rmsg}")
                log(f"refused {STEPS[new_pos]}: {rmsg}")
                continue

            pos = new_pos
            line = f"rfgain {STEPS[pos]} dB   (position {pos + 1} of {len(STEPS)})"
            print(f"       {line}")
            log(line)

        log(f"--- session end, rfgain {STEPS[pos]}")
        print(f"\nLeft at {STEPS[pos]} dB")
        print(f"Log: {LOG_PATH}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

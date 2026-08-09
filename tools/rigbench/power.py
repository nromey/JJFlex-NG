"""Transverter drive stepper — the operator's knob for the loopback test.

  python tools\\rigbench\\power.py [xvtr_index] [ip]

Every keypress is a KNOWN increment from a KNOWN start, so you can sweep the
whole range by counting presses and narrating as you go, without reading the
screen or hearing the PC. Starts at the floor, -10.0 dBm (0.1 mW).

  Up / Down          1 dB      the main step; count these
  Page Up / Page Dn  5 dB      big jumps
  Right / Left       0.25 dB   fine trim once you find the edge
  Home               back to the floor, -10.0 dBm
  Q or Escape        quit (leaves the value where it is)

Every change is timestamped into power-log.txt next to this script, so the
sweep can be reconstructed afterward even if nobody was reading along.

THIS SCRIPT NEVER TRANSMITS. Keying is yours, by hand mic.
"""

import datetime
import msvcrt
import os
import sys

from flexwire import FlexWire, DEFAULT_RADIO

FLOOR_DBM = -10.0
# 8600 with an IF below 80 MHz: +15.0. (Xvtr.cs:177-194 — it is +10.0 only on
# 6400/6600 and +8.0 for an IF at or above 80 MHz.) The radio clamps too; this
# is so we never even ask for something it would silently trim.
CEILING_DBM = 15.0

LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "power-log.txt")

KEYS = {
    b"H": ("up", 1.0),
    b"P": ("down", -1.0),
    b"I": ("pgup", 5.0),
    b"Q": ("pgdn", -5.0),
    b"M": ("right", 0.25),
    b"K": ("left", -0.25),
}


def milliwatts(dbm):
    return 10.0 ** (dbm / 10.0)


def describe(dbm):
    mw = milliwatts(dbm)
    if mw < 1.0:
        return f"{dbm:+.2f} dBm  ({mw * 1000:.0f} microwatts)"
    return f"{dbm:+.2f} dBm  ({mw:.2f} mW)"


def log(line):
    stamp = datetime.datetime.now().strftime("%H:%M:%S")
    with open(LOG_PATH, "a", encoding="utf-8") as fh:
        fh.write(f"{stamp}  {line}\n")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    index = args[0] if args else "0"
    host = args[1] if len(args) > 1 else DEFAULT_RADIO

    print(__doc__)
    print(f"Transverter band {index} on {host}\n")

    with FlexWire(host) as wire:
        value = FLOOR_DBM
        code, msg = wire.send(f"xvtr set {index} max_power={value:.2f}")
        if code != "0":
            print(f"Could not set drive: ERROR {code} ({msg})")
            print("Is the band index right? setup_loopback.py printed it.")
            return 1

        log(f"--- session start, band {index}, {describe(value)}")
        print(f"START  {describe(value)}")
        print("Counting from here. Each Up is 1 dB.\n")

        while True:
            ch = msvcrt.getch()
            if ch in (b"q", b"Q", b"\x1b"):
                break
            if ch == b"\x03":  # Ctrl+C
                break

            delta = None
            if ch in (b"\x00", b"\xe0"):
                code2 = msvcrt.getch()
                if code2 == b"G":  # Home
                    delta = FLOOR_DBM - value
                elif code2 in KEYS:
                    delta = KEYS[code2][1]
            if delta is None:
                continue

            new = max(FLOOR_DBM, min(CEILING_DBM, round(value + delta, 2)))
            if new == value:
                edge = "floor" if new <= FLOOR_DBM else "ceiling"
                print(f"       at the {edge} already, {describe(value)}")
                continue

            rc, rmsg = wire.send(f"xvtr set {index} max_power={new:.2f}")
            if rc != "0":
                print(f"       ERROR {rc} ({rmsg}) — still {describe(value)}")
                log(f"ERROR {rc} setting {new:.2f}: {rmsg}")
                continue

            value = new
            line = describe(value)
            print(f"       {line}")
            log(line)
            if value > 10.0:
                print("       (above +10 dBm — hotter than the 6400/6600 ceiling)")

        log(f"--- session end, {describe(value)}")
        print(f"\nLeft at {describe(value)}")
        print(f"Log: {LOG_PATH}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

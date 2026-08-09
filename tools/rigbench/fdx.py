"""Full-duplex gate — set it AFTER connecting, because the global profile
load at connect clobbers it back to 0.

  python tools\\rigbench\\fdx.py        # report current state
  python tools\\rigbench\\fdx.py on     # open the gate
  python tools\\rigbench\\fdx.py off    # back to factory default

Without full duplex, keying mutes every receiver and the loopback is
impossible. JJFlex exposes this nowhere, which is why it lives here.

THIS SCRIPT NEVER TRANSMITS.
"""

import sys

from flexwire import FlexWire, DEFAULT_RADIO, ok


def read_state(wire):
    wire.send("sub radio all")
    for line in wire.drain_status(1.5):
        if "full_duplex_enabled=" in line:
            for field in line.split():
                if field.startswith("full_duplex_enabled="):
                    return field.split("=", 1)[1]
    return "unknown"


def main():
    args = [a.lower() for a in sys.argv[1:]]
    want = None
    if "on" in args:
        want = "1"
    elif "off" in args:
        want = "0"
    host = next((a for a in sys.argv[1:] if a.count(".") == 3), DEFAULT_RADIO)

    with FlexWire(host) as wire:
        before = read_state(wire)
        print(f"full_duplex_enabled is currently {before}"
              f"  ({'gate OPEN' if before == '1' else 'gate CLOSED'})")

        if want is None:
            print("\nPass 'on' or 'off' to change it.")
            return 0

        if before == want:
            print(f"Already {want}. Nothing to do.")
            return 0

        ok(wire.send(f"radio set full_duplex_enabled={want}"),
           f"full_duplex_enabled={want}")
        after = read_state(wire)
        print(f"now {after}  ({'gate OPEN' if after == '1' else 'gate CLOSED'})")
        if after != want:
            print("\nIt did not stick. Something is re-asserting it —")
            print("most likely a profile load. Re-run after the radio settles.")
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

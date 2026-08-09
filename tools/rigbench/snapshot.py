"""Read-only snapshot of the radio's current state. Changes nothing.

Usage:  python tools\\rigbench\\snapshot.py [ip]

Prints the radio, transmit, slice and transverter status the radio reports,
plus a filtered view of the handful of fields the transverter-loopback test
cares about, so we have a restore point before touching anything.
"""

import sys

from flexwire import FlexWire, DEFAULT_RADIO

INTERESTING = (
    "full_duplex_enabled",
    "mon=",
    "mox=",
    "rfpower",
    "tunepower",
    "ant=",
    "txant",
    "rxant",
    "freq=",
    "mode=",
)


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_RADIO
    print(f"Connecting to {host} (read-only)...")
    with FlexWire(host) as wire:
        print(f"  version {wire.version}, handle {wire.handle}")
        wire.subscribe_all()
        lines = wire.drain_status(3.0)

    print(f"\n--- {len(lines)} status lines ---\n")
    for line in lines:
        print(line)

    print("\n--- fields the loopback test touches ---\n")
    for line in lines:
        low = line.lower()
        if any(key in low for key in INTERESTING):
            print(line)


if __name__ == "__main__":
    main()

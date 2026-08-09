"""Set up (or tear down) the transverter-loopback test arrangement.

Plan item 1b, docs/planning/active/audio-workshop-plan.md.

  python tools\\rigbench\\setup_loopback.py            # set up
  python tools\\rigbench\\setup_loopback.py --teardown # put it back

This touches only the two things JJFlex does not expose:
  * radio full_duplex_enabled  (the gate — without it, keying mutes every RX)
  * an XVTR band definition    (the only fine drive control that exists, in dBm)

Slices, antennas and monitor stay the operator's job in JJFlex, so the audio
lands at the radio's headphone jack and the operator keeps control.

THIS SCRIPT NEVER TRANSMITS. There is no xmit command here by design.
"""

import sys

from flexwire import FlexWire, DEFAULT_RADIO, ok

# The classic 2m transverter mapping: tune the slice to 144.100 and the radio
# actually transmits 28.100 out the XVTR port. IF is deliberately below 80 MHz
# so the dBm ceiling is +15.0 rather than +8.0 (Xvtr.cs:177-194).
BAND_NAME = "TEST"
RF_FREQ = 144.100
IF_FREQ = 28.100
FLOOR_DBM = -10.0


def find_xvtr_indexes(wire):
    wire.send("sub xvtr all")
    found = {}
    for line in wire.drain_status(2.0):
        # S<handle>|xvtr <index> name=... rf_freq=... in_use=...
        parts = line.split("|", 1)
        if len(parts) < 2:
            continue
        body = parts[1].split()
        if len(body) >= 2 and body[0] == "xvtr":
            try:
                found[int(body[1])] = " ".join(body[2:])
            except ValueError:
                pass
    return found


def setup(host):
    with FlexWire(host) as wire:
        print(f"Connected to {host} (version {wire.version}, handle {wire.handle})")

        print("\nFull duplex (the gate):")
        ok(wire.send("radio set full_duplex_enabled=1"), "full_duplex_enabled=1")

        existing = find_xvtr_indexes(wire)
        if existing:
            print(f"\nExisting transverter bands: {sorted(existing)}")

        print("\nTransverter band:")
        code, msg = wire.send("xvtr create")
        if code != "0":
            print(f"  xvtr create: ERROR {code} ({msg})")
            return 1
        index = msg.strip()
        print(f"  xvtr create: OK (index {index})")

        ok(wire.send(f"xvtr set {index} name={BAND_NAME}"), f"name={BAND_NAME}")
        ok(wire.send(f"xvtr set {index} rf_freq={RF_FREQ:.6f}"), f"rf_freq={RF_FREQ}")
        ok(wire.send(f"xvtr set {index} if_freq={IF_FREQ:.6f}"), f"if_freq={IF_FREQ}")
        ok(wire.send(f"xvtr set {index} lo_error=0.000000"), "lo_error=0")
        ok(wire.send(f"xvtr set {index} rx_gain=0.00"), "rx_gain=0")
        ok(wire.send(f"xvtr set {index} rx_only=0"), "rx_only=0")
        ok(wire.send(f"xvtr set {index} max_power={FLOOR_DBM:.2f}"),
           f"max_power={FLOOR_DBM} dBm (0.1 mW)")

        print("\nAs the radio now reports it:")
        for idx, desc in sorted(find_xvtr_indexes(wire).items()):
            print(f"  xvtr {idx}: {desc}")

        print(f"""
Ready. In JJFlex:
  1. Connect to the 8600.
  2. Tune a slice to {RF_FREQ:.3f} MHz  (the radio transmits {IF_FREQ:.3f} out the XVTR port)
  3. TX antenna on that slice  = XVT A
  4. Second slice, same frequency and mode, RX antenna = XVT A
  5. TX monitor OFF  (it was already off in the snapshot)

Then run:  python tools\\rigbench\\power.py {index}
""")
    return 0


def teardown(host):
    with FlexWire(host) as wire:
        print(f"Connected to {host}")
        print("\nFull duplex back to factory default:")
        ok(wire.send("radio set full_duplex_enabled=0"), "full_duplex_enabled=0")

        print("\nRemoving test transverter bands:")
        removed = 0
        for idx, desc in sorted(find_xvtr_indexes(wire).items()):
            if BAND_NAME.lower() in desc.lower():
                ok(wire.send(f"xvtr remove {idx}"), f"xvtr remove {idx}")
                removed += 1
        if not removed:
            print(f"  none named {BAND_NAME} found")

        print("\nRemaining transverter bands:")
        remaining = find_xvtr_indexes(wire)
        if remaining:
            for idx, desc in sorted(remaining.items()):
                print(f"  xvtr {idx}: {desc}")
        else:
            print("  none")
    return 0


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    host = args[0] if args else DEFAULT_RADIO
    if "--teardown" in sys.argv:
        return teardown(host)
    return setup(host)


if __name__ == "__main__":
    sys.exit(main())

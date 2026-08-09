"""Read and drive slices directly over the wire, bypassing JJFlex.

  python tools\\rigbench\\slices.py                        # list slices
  python tools\\rigbench\\slices.py 0 mode=USB             # set one field
  python tools\\rigbench\\slices.py 0 mode=USB rxant=XVTA  # several at once
  python tools\\rigbench\\slices.py 0 tune 144.100         # retune
  python tools\\rigbench\\slices.py remove 3               # release a slice

Diagnostic value beyond convenience: FlexLib caches a slice's mode locally and
dedups against that cache (Slice.cs:292), so a command the radio rejects can
leave the app believing it succeeded and silently no-op every later attempt.
Driving the radio from here skips that cache entirely, which discriminates an
app-side problem from a radio-side one.

Fields worth knowing: mode, rxant, txant, tx (1 = this is the transmit slice),
active, audio_mute, rfgain.

THIS SCRIPT NEVER TRANSMITS.
"""

import sys

from flexwire import FlexWire, DEFAULT_RADIO

SHOW = ("RF_frequency", "mode", "rxant", "txant", "tx", "active", "in_use",
        "filter_lo", "filter_hi", "audio_mute", "client_handle")


def read_slices(wire, subscribe=True):
    if subscribe:
        wire.send("sub slice all")
    slices = {}
    for line in wire.drain_status(1.5):
        body = line.split("|", 1)[-1]
        if not body.startswith("slice "):
            continue
        parts = body.split()
        if len(parts) < 2:
            continue
        try:
            idx = int(parts[1])
        except ValueError:
            continue
        fields = slices.setdefault(idx, {})
        for token in parts[2:]:
            if "=" in token:
                k, v = token.split("=", 1)
                fields[k] = v
    return slices


def show(slices):
    if not slices:
        print("  no slices")
        return
    for idx in sorted(slices):
        f = slices[idx]
        bits = [f"slice {idx}"]
        for key in SHOW:
            if key in f:
                bits.append(f"{key}={f[key]}")
        print("  " + "  ".join(bits))


def main():
    argv = [a for a in sys.argv[1:] if a.count(".") != 3 or a.replace(".", "").isdigit()]
    host = next((a for a in sys.argv[1:]
                 if a.count(".") == 3 and not a.replace(".", "").isdigit()), DEFAULT_RADIO)

    with FlexWire(host) as wire:
        if not argv:
            print("Slices as the radio reports them:\n")
            show(read_slices(wire))
            return 0

        if argv[0] == "remove":
            for idx in argv[1:]:
                code, msg = wire.send(f"slice remove {idx}")
                print(f"  remove {idx}: {'OK' if code == '0' else f'ERROR {code} ({msg})'}")
            print("\nNow:")
            show(read_slices(wire))
            return 0

        index = argv[0]
        before = read_slices(wire).get(int(index), {})
        rc = 0

        rest = argv[1:]
        i = 0
        while i < len(rest):
            if rest[i] == "tune" and i + 1 < len(rest):
                freq = rest[i + 1]
                code, msg = wire.send(f"slice tune {index} {freq}")
                print(f"  tune {freq}: {'OK' if code == '0' else f'ERROR {code} ({msg})'}")
                if code != "0":
                    rc = 1
                i += 2
                continue

            arg = rest[i]
            i += 1
            if "=" not in arg:
                print(f"  skipping '{arg}' (expected key=value)")
                continue
            key, value = arg.split("=", 1)
            was = before.get(key, "(not reported)")
            code, msg = wire.send(f"slice set {index} {key}={value}")
            if code != "0":
                print(f"  {key}: ERROR {code} ({msg})  — the RADIO refused this")
                rc = 1
                continue
            after = read_slices(wire, subscribe=False).get(int(index), {}).get(key)
            if after is None:
                print(f"  {key}: sent OK, no status update seen (was {was})")
            else:
                verdict = "OK" if after == value else "REVERTED"
                print(f"  {key}: {was} -> {after}  [{verdict}]")
                if after != value:
                    rc = 1

        print("\nNow:")
        show(read_slices(wire))
        return rc


if __name__ == "__main__":
    sys.exit(main())

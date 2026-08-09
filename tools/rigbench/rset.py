"""Read or set radio-level settings over the wire.

  python tools\\rigbench\\rset.py                              # show the radio line
  python tools\\rigbench\\rset.py band_persistence_enabled     # read one field
  python tools\\rigbench\\rset.py band_persistence_enabled=0   # set it, then read it back

Sets are issued as `radio set <key>=<value>` and always verified by re-reading,
because several of these get re-asserted by profile loads and a write that
silently loses is worse than one that fails loudly.

THIS SCRIPT NEVER TRANSMITS. Commands that would key the radio are refused
below rather than merely avoided by convention.
"""

import sys

from flexwire import FlexWire, DEFAULT_RADIO

FORBIDDEN = ("xmit", "tune", "atu_start", "mox")


def merge(fields, lines):
    """Fold every `radio ...` status line into the field map.

    A second `sub radio all` does NOT make the radio re-send its full status —
    subscriptions emit once. After a set, the radio sends a delta line for the
    changed field, so verification means watching for that, not re-reading.
    """
    for line in lines:
        body = line.split("|", 1)[-1]
        if not body.startswith("radio "):
            continue
        for token in body.split()[1:]:
            if "=" in token:
                k, v = token.split("=", 1)
                fields[k] = v
    return fields


def radio_fields(wire, subscribe=True):
    if subscribe:
        wire.send("sub radio all")
    return merge({}, wire.drain_status(1.5))


def main():
    args = [a for a in sys.argv[1:] if a.count(".") != 3]
    host = next((a for a in sys.argv[1:] if a.count(".") == 3), DEFAULT_RADIO)

    with FlexWire(host) as wire:
        fields = radio_fields(wire)
        if not fields:
            print("No radio status came back.")
            return 1

        if not args:
            for k in sorted(fields):
                print(f"  {k} = {fields[k]}")
            return 0

        rc = 0
        for arg in args:
            if any(bad in arg.lower() for bad in FORBIDDEN):
                print(f"REFUSED: {arg} (this tool does not key the radio)")
                rc = 1
                continue

            if "=" not in arg:
                print(f"  {arg} = {fields.get(arg, '(not reported)')}")
                continue

            key, value = arg.split("=", 1)
            before = fields.get(key, "(not reported)")
            code, msg = wire.send(f"radio set {key}={value}")
            if code != "0":
                print(f"  {key}: ERROR {code} ({msg})")
                rc = 1
                continue

            merge(fields, wire.drain_status(1.5))
            after = fields.get(key, "(not reported)")
            verdict = "OK" if after == value else "DID NOT STICK"
            print(f"  {key}: {before} -> {after}  [{verdict}]")
            if after != value:
                print("    Something is re-asserting this. Usually a profile load.")
                rc = 1
        return rc


if __name__ == "__main__":
    sys.exit(main())

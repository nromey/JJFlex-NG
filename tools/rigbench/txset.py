"""Read or set transmit-section settings over the wire.

  python tools\rigbench\txset.py                  # show the transmit line
  python tools\rigbench\txset.py rfpower=1        # set drive
  python tools\rigbench\txset.py mon=0            # monitor off

THIS SCRIPT NEVER TRANSMITS. Commands that would key the radio are refused.
"""

import sys

from flexwire import FlexWire, DEFAULT_RADIO

FORBIDDEN = ("xmit", "tune=", "mox")
SHOW = ("freq", "rfpower", "tunepower", "tx_slice_mode", "tx_antenna",
        "sb_monitor", "mon_gain_sb", "mic_selection", "mic_level",
        "compander", "compander_level", "speech_processor_enable",
        "speech_processor_level", "inhibit")


def tx_fields(wire, subscribe=True):
    if subscribe:
        wire.send("sub tx all")
    fields = {}
    for line in wire.drain_status(1.5):
        body = line.split("|", 1)[-1]
        if not body.startswith("transmit "):
            continue
        for token in body.split()[1:]:
            if "=" in token:
                k, v = token.split("=", 1)
                fields[k] = v
    return fields


def main():
    args = [a for a in sys.argv[1:] if a.count(".") != 3]
    host = next((a for a in sys.argv[1:] if a.count(".") == 3), DEFAULT_RADIO)

    with FlexWire(host) as wire:
        fields = tx_fields(wire)
        if not args:
            for key in SHOW:
                if key in fields:
                    print(f"  {key} = {fields[key]}")
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
            was = fields.get(key, "(not reported)")
            code, msg = wire.send(f"transmit set {key}={value}")
            if code != "0":
                print(f"  {key}: ERROR {code} ({msg})")
                rc = 1
                continue
            after = tx_fields(wire, subscribe=False).get(key, "(no update seen)")
            print(f"  {key}: {was} -> {after}")
        return rc


if __name__ == "__main__":
    sys.exit(main())

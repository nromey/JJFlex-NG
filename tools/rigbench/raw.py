"""Send raw wire commands and print the radio's actual reply code.

  python tools\rigbench\raw.py "transmit set rfpower=1" "sub tx all"

Every argument is one command. Reply code 0 means the radio accepted it —
which is NOT the same as the value changing, so status lines are dumped after.

Commands that would key the radio are refused.
"""

import sys

from flexwire import FlexWire, DEFAULT_RADIO

FORBIDDEN = ("xmit", "mox")


def main():
    cmds = sys.argv[1:]
    if not cmds:
        print(__doc__)
        return 1

    with FlexWire(DEFAULT_RADIO) as wire:
        for cmd in cmds:
            if any(bad in cmd.lower() for bad in FORBIDDEN):
                print(f"REFUSED: {cmd}")
                continue
            code, msg = wire.send(cmd)
            print(f"  > {cmd}\n    reply code={code} msg={msg!r}")
        print("\n  status lines that followed:")
        for line in wire.drain_status(2.0):
            body = line.split("|", 1)[-1]
            if body.startswith(("transmit ", "radio slices=", "interlock ", "xvtr ")):
                print(f"    {body[:200]}")
    return 0
if __name__ == "__main__": sys.exit(main())

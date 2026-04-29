# TM-V71A CAT Control Project

This project implements computer control of the Kenwood TM-V71A dual-band mobile
transceiver via its serial CAT (Computer Aided Transceiver) port.

---

## Project Files

| File | Contents |
|------|----------|
| `TMV71A_CAT_PROTOCOL.md` | Complete CAT command reference — all 34 commands, full parameter tables, CTCSS/DCS tone tables, programming mode binary protocol, memory map, known bugs & workarounds |

---

## Protocol Research Sources

The Kenwood TM-V71A protocol was never officially published. All documentation is
community-reverse-engineered. These are the authoritative sources used:

### Primary Reference — LA3QMA Reverse Engineering Repository
**https://github.com/LA3QMA/TM-V71_TM-D710-Kenwood**

The most complete command reference available. Contains:
- Individual markdown files for every known command (`/commands/` directory)
- Reference tables for CTCSS, DCS, step sizes, modes, band codes, DTMF (`/tables/` directory)
- `MEMORYMAP.md` — full binary memory layout with channel record byte format
- `PROGRAMMING_MODE.md` — binary read/write protocol with hex examples
- `SERVICE_MODE.md` — diagnostic service mode entry/exit

Use this first when looking up any command parameter or table value.

### Hamlib Radio Control Library
**https://github.com/Hamlib/Hamlib**
**https://hamlib.org**

Working C implementation used by rigctl, fldigi, WSJT-X and many other programs.

Key source files:
- `rigs/kenwood/tmd710.c` — TM-V71A / TM-D710 backend (primary implementation)
- `rigs/kenwood/th.c` — TH handheld series base (used by TM-V71A)
- `rigs/kenwood/kenwood.c` — Core Kenwood protocol layer
- `rigs/kenwood/kenwood.h` — Constants and definitions

**Known fixed bug:** Issue #1767 — `kenwood_transaction()` stripped the `\r`
terminator, breaking all TM-V71A communication. Fixed in Hamlib 4.6.4+. Always use
4.6.4 or later.

Raw file URLs:
- https://raw.githubusercontent.com/Hamlib/Hamlib/master/rigs/kenwood/tmd710.c
- https://raw.githubusercontent.com/Hamlib/Hamlib/master/rigs/kenwood/th.c
- https://raw.githubusercontent.com/Hamlib/Hamlib/master/rigs/kenwood/kenwood.c

### larsks / tm-v71-tools (Python 3)
**https://github.com/larsks/tm-v71-tools**

Python 3.7+ CLI and library. Covers VFO tuning, band selection, squelch, and memory
channel read/write. Good clean Python to study for implementation reference.

Key files:
- `tm_v71/commands.py` — command definitions
- `tm_v71/api.py` — high-level API
- `tm_v71/schema.py` — parameter schemas / validation

### AG7GN / kenwood (Python + XML-RPC)
**https://github.com/AG7GN/kenwood**

Full Python GUI application with XML-RPC server for Fldigi/Hamlib integration.
Includes multiple PTT control methods (GPIO, DigiRig, CM108, CAT).

Key files:
- `cat710.py` — complete CAT command layer (best Python reference for all commands)
- `common710.py` — constants, frequency validation, band constraints
- `710.py` — main GUI + XML-RPC server

### CHIRP Radio Programming Software
**https://github.com/chirpradio/chirp**

Driver: `chirp/drivers/tmv71.py`

Used primarily for memory channel read/write via programming mode (binary protocol).
Good reference for channel record encoding/decoding.

### N3UJJ Command Reference PDF
**http://n3ujj.com/manuals/control_commands.pdf**

Scanned command reference PDF covering TM-V71 / TM-D710 commands. Useful for
cross-checking parameter ranges when other sources disagree.

### larsks Blog — TM-V71A Linux Integration
**https://blog.oddbit.com/post/2019-10-03-tm-v71a-linux-part-1/**

Practical write-up covering serial connection, flow control quirks, and real-world
CAT command usage under Linux. Good for troubleshooting connection issues.

### Digirig Forum — Serial Response Issues
**https://forum.digirig.net/t/kenwood-tm-v71a-serial-response/2225**

Documents the hardware flow control (CTS) requirement and the RTS→CTS loopback
workaround for adapters that don't assert CTS.

---

## Key Protocol Facts (Quick Reference)

- **Terminator:** `\r` (0x0D) — NOT semicolon, NOT `\r\n`
- **Flow control:** Hardware RTS/CTS required — radio won't respond without CTS
- **Baud rate:** 57,600 default (Menu #519)
- **Connector:** Mini-DIN 8-pin on rear of main unit
- **Command format:** 2-letter command + comma-separated params + `\r`
- **Programming mode entry:** `0M PROGRAM\r` → responds `0M\r`
- **TM-V71A ≠ TM-D710:** Most CAT commands are identical; GPS commands (GM, GP, GT)
  are TM-D710G only; MN and MS have different max name lengths

---

## Useful Search Terms for Further Research

When researching additional protocol details, these search terms tend to surface useful
results:

- `"TM-V71A" CAT protocol serial commands`
- `"TM-D710" PC control commands`  
- `kenwood tmv71 python serial`
- `site:github.com TM-V71 CAT`
- `hamlib tmd710.c`
- LA3QMA kenwood commands
- `"0M PROGRAM"` (programming mode entry string)

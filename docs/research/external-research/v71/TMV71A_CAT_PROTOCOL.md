# Kenwood TM-V71A CAT Control Protocol — Complete Reference

> Sources: LA3QMA reverse-engineering repo, Hamlib tmd710.c/th.c, larsks/tm-v71-tools,
> AG7GN/kenwood, CHIRP tmv71.py driver.
> The protocol was never officially published by Kenwood; this is community documentation.

---

## Serial Connection

| Parameter    | Value |
|--------------|-------|
| Connector    | Mini-DIN 8-pin (rear of main unit) |
| Baud rate    | 57,600 default — Menu #519 sets 9600 / 19200 / 38400 / 57600 |
| Format       | 8N1 |
| Flow control | **Hardware RTS/CTS required** |
| Terminator   | `\r` (0x0D carriage return — NOT semicolon) |

**Flow control workaround:** Without CTS asserted the radio accepts commands but never
responds. If your USB-serial adapter lacks hardware flow control, loop RTS→CTS by
shorting Mini-DIN8 pins 1 and 2 together on the cable.

---

## Response Codes

```
[COMMAND]   command accepted (CAT mode)
N           bad parameters
?           unrecognised command
0x06        success (programming mode only)
0x0F        error state (programming mode only)
```

---

## Command Reference

Commands are listed alphabetically. Parameters use comma separation; no spaces after
the command letter unless noted.

---

### AE — Serial Number / Model

Read-only. Returns the serial number and model string from the main unit. Querying the
display unit returns garbled output.

```
Get:  AE
      → serialnumber,model
```

---

### AS — Reverse (TX/RX Swap)

Swaps the transmit and receive frequencies on a band without changing the stored offset.

```
Get:  AS p1
Set:  AS p1,p2

p1  band    0=Band A  1=Band B
p2  mode    0=Normal  1=Reverse
```

---

### BC — Band / PTT Control

Selects which band side is under CAT control and which side keys up on PTT.

```
Get:  BC
Set:  BC p1,p2
      → p1,p2

p1  ctrl band   0=Band A  1=Band B
p2  PTT band    0=Band A  1=Band B
```

| Example | Effect |
|---------|--------|
| `BC 0,0` | Control A, PTT A |
| `BC 1,1` | Control B, PTT B |
| `BC 0,1` | Control A, PTT B (crossband) |

---

### BE — APRS Beacon

Triggers or toggles an APRS beacon depending on the current beacon mode. Does not
accept parameters.

```
Send:  BE

Behaviour by beacon mode:
  Manual      → transmits one beacon immediately
  PTT         → arms next PTT to also send beacon
  Auto        → toggles auto-beacon on/off
  SmartBeacon → toggles SmartBeacon on/off
```

---

### BL — Backlight

Controls display backlight colour and brightness. TM-V71A only has the colour field
(p1); the TM-D710 adds brightness (p2).

```
Get:  BL
Set:  BL p1[,p2]
      → p1[,p2]

p1  colour
      TM-D710:   0=yellow  1=green
      TM-D710G:  0=orange  1=green  2=yellow

p2  brightness (TM-D710G only)
      0=off  1–8 (1=dim … 8=brightest)
```

---

### BT — Burst Tone Frequency

Selects the tone frequency sent when `TT` is issued.

```
Get:  BT
Set:  BT p1
      → p1

p1   0=1000 Hz   1=1450 Hz   2=1750 Hz   3=2100 Hz
```

---

### BY — Busy / Squelch Status

Returns whether the squelch is open on a band. Read-only.

```
Get:  BY p1
      → p1,p2

p1  band    0=Band A  1=Band B
p2  status  0=squelch closed (no signal)  1=squelch open (busy)
```

---

### CC — Call Channel Configuration

Reads or writes all parameters of the Call Channel (the channel recalled by pressing
CALL on the front panel). Follows the same 15-field layout as `ME`.

```
Get:  CC p1
Set:  CC p1,p2,p3,p4,p5,p6,p7,p8,p9,p10,p11,p12,p13,p14,p15

p1   band (see Band table)
p2   RX frequency Hz, 10 digits, zero-padded
p3   RX step size (see Step table)
p4   shift direction (see Shift table)
p5   reverse  0=off  1=on
p6   tone encode  0=off  1=on
p7   CTCSS squelch  0=off  1=on
p8   DCS  0=off  1=on
p9   tone/CTCSS encode index (see CTCSS table)
p10  CTCSS squelch index (see CTCSS table)
p11  DCS code index (see DCS table)
p12  offset frequency Hz, 8 digits, zero-padded
p13  mode (see Mode table)
p14  TX frequency Hz, 10 digits (used for odd splits; 0 = use offset)
p15  TX step size (see Step table)
```

---

### CD — Channel / Frequency Mode Status

Reads or forces the display between frequency (VFO) mode and channel (memory) mode.
Duplicates part of `VM` but operates globally.

```
Get:  CD
Set:  CD p1
      → p1

p1   0=Frequency mode   1=Channel mode
```

---

### CS — Callsign

Reads or writes the station callsign stored in the radio (used for APRS, EchoLink, etc).

```
Get:  CS
Set:  CS <callsign>
      → callsign
```

---

### DL — Dual / Single Band Mode

Switches between showing both bands simultaneously or only one.

```
Get:  DL
Set:  DL p1
      → p1

p1   0=Dual band   1=Single band
```

---

### DM — DTMF Memory

Stores or recalls one of 10 DTMF code strings. Each string is exactly 16 characters;
pad shorter codes with spaces on the right.

```
Get:  DM p1
Set:  DM p1,p2
      → p1,p2

p1   channel  0–9
p2   DTMF string, 16 characters (pad with spaces if shorter)
     Valid characters: 0-9 A B C D * #
```

**Example:** Store "147.555" as channel 3 — `DM 3,147555          ` (padded to 16 chars)

---

### DT — DTMF Transmit

Sends a single DTMF tone or row/column frequency pair. **Radio must be transmitting (TX
active) when this command is sent.**

```
Set:  DT p1,p2

p1=0 (tone by digit):
  p2   0–9, A, B, C, D, E(=*), F(=#)

p1=1 (tone by row/column frequency):
  p2   1=697Hz  2=770Hz  3=852Hz  4=941Hz
       5=1209Hz 6=1336Hz 7=1447Hz 8=1633Hz
```

---

### DW — Step Down (Emulate Down Key)

Decrements the VFO or memory channel by one step. In VFO mode steps by the current
step size; in MR mode steps to the previous memory channel.

```
Set:  DW [n]
      (no response)

n   number of steps 01–99; omit or use 00 in MR mode for one step
```

---

### FO — VFO Frequency and Parameters

The primary frequency-setting command. Sets or reads all VFO parameters in one shot.

```
Get:  FO p1
Set:  FO p1,p2,p3,p4,p5,p6,p7,p8,p9,p10,p11,p12,p13
      → p1,p2,p3,p4,p5,p6,p7,p8,p9,p10,p11,p12,p13

p1   band               0=Band A  1=Band B
p2   RX frequency Hz    10 digits, zero-padded (e.g. 0145500000)
p3   RX step size       (see Step table)
p4   shift direction    (see Shift table)
p5   reverse            0=off  1=on
p6   tone encode        0=off  1=on
p7   CTCSS squelch      0=off  1=on
p8   DCS                0=off  1=on
p9   tone/CTCSS encode index  (see CTCSS table)
p10  CTCSS squelch index      (see CTCSS table)
p11  DCS code index           (see DCS table)
p12  offset Hz          8 digits zero-padded (e.g. 00600000 = 600 kHz)
p13  mode               0=FM  1=NFM  2=AM
```

**Split TX:** set p4=0, make p12 non-zero. For a totally independent TX frequency,
use the `ME` command's p14 field instead.

**Example — 145.500 MHz, +600 kHz, CTCSS 100.0 Hz encode:**
```
FO 0,0145500000,0,1,0,1,0,0,12,12,000,00600000,0
```

---

### FV — Firmware Version

Returns the firmware version string of the selected unit.

```
Get:  FV p1
      → firmware_string

p1   0=main unit   1=control panel   2=TNC
```

---

### GM — Radio / GPS Mode *(TM-D710G only)*

Switches between running both the radio and internal GPS, or GPS only.

```
Get:  GM
Set:  GM p1
      → p1

p1   0=Radio + internal GPS on
     1=Radio off, internal GPS on
```

---

### GP — Internal GPS *(TM-D710G only)*

Enables or disables the built-in GPS and selects whether GPS data is output on the
serial port.

```
Get:  GP
Set:  GP p1,p2
      → p1,p2

p1   0=GPS off   1=GPS on
p2   0=iGPS only (no serial output)   1=iGPS + serial data output
```

---

### GT — Internal GPS Mode Read *(TM-D710G only)*

Read-only shorthand for the GPS on/off state.

```
Get:  GT
      → p1

p1   N=GPS off   n/a=GPS on
```

---

### ID — Radio Identification

Returns the model identifier string. Use to verify the connection.

```
Get:  ID
      → ID TM-V71A
```

---

### LK — Key Lock

Locks or unlocks the front-panel keys.

```
Get:  LK
Set:  LK p1
      → p1

p1   0=unlocked   1=locked
```

---

### ME — Memory Channel Read / Write

Reads or writes all parameters for a numbered memory channel. This is the full channel
record including a separate TX frequency for odd splits.

```
Get:  ME p1
Set:  ME p1,p2,p3,p4,p5,p6,p7,p8,p9,p10,p11,p12,p13,p14,p15,p16
      → p1,p2,...,p16

p1   channel number  000–199 (normal)  200–219 (edge pairs)  220–221 (call)
p2   RX frequency Hz, 10 digits
p3   RX step size (see Step table)
p4   shift direction (see Shift table)
p5   reverse  0=off  1=on
p6   tone encode  0=off  1=on
p7   CTCSS squelch  0=off  1=on
p8   DCS  0=off  1=on
p9   tone/CTCSS encode index (see CTCSS table)
p10  CTCSS squelch index (see CTCSS table)
p11  DCS code index (see DCS table)
p12  offset Hz, 8 digits
p13  mode  0=FM  1=NFM  2=AM
p14  TX frequency Hz, 10 digits (independent split TX; 0 = derive from offset)
p15  unknown — set to 0
p16  lockout / scan skip  0=normal  1=skip
```

**Erase a channel** — send the channel number with a trailing comma only:
```
ME 042,
```

---

### MN — Memory Channel Name

Reads or writes the alphanumeric label for a memory channel.

```
Get:  MN p1
Set:  MN p1,p2
      → p1,p2

p1   channel  000–221
p2   name string
     TM-V71A: up to 6 characters, UPPERCASE only
     TM-D710:  up to 8 characters
```

---

### MR — Memory Channel Select

Switches a band to a specific memory channel. The band must already be in Memory mode
(`VM p1,1`).

```
Get:  MR p1
Set:  MR p1,p2
      → p1,p2

p1   band     0=Band A  1=Band B
p2   channel  000–221
```

---

### MS — Power-On Message

Reads or writes the text displayed at power-on.

```
Get:  MS
Set:  MS <text>
      → text

TM-V71A: up to 6 characters, UPPERCASE only
TM-D710:  up to 8 characters
```

---

### MU — Menu Configuration

Reads or writes all 42 radio menu parameters in a single command. Reading returns all
42 values; writing requires all 42 (read-modify-write pattern recommended).

```
Get:  MU
Set:  MU p1,p2,p3,...,p41
```

| p# | Menu item | Values |
|----|-----------|--------|
| p1 | Beep | 0=off 1=on |
| p2 | Beep volume | 0–7 |
| p3 | External speaker mode | 0 / 1 / 2 |
| p4 | Voice announce | 0=off 1=auto 2=manual |
| p5 | Language | 0=English 1=Japanese |
| p6 | Voice volume | 0–7 |
| p7 | Voice speed | 0–4 |
| p8 | Playback repeat | 0=off 1=on |
| p9 | Playback repeat interval | 00–60 (seconds) |
| p10 | Continuous recording | 0=off 1=on |
| p11 | VHF AIP (Advanced Intercept Point) | 0=off 1=on |
| p12 | UHF AIP | 0=off 1=on |
| p13 | S-meter squelch hang time | 0=off 1=125ms 2=250ms 3=500ms |
| p14 | Mute hang time | 0=off 1=125ms 2=250ms 3=500ms 4=750ms 5=1000ms |
| p15 | Beatshift | 0=off 1=on |
| p16 | Timeout timer (TOT) | 0=3min 1=5min 2=10min |
| p17 | Recall method | 0=All 1=Current |
| p18 | EchoLink speed | 0=fast 1=slow |
| p19 | DTMF hold | 0=off 1=on |
| p20 | DTMF speed | 0=fast 1=slow |
| p21 | DTMF pause | 0=100ms 1=250ms 2=500ms 3=750ms 4=1000ms 5=1500ms 6=2000ms |
| p22 | DTMF key lock | 0=off 1=on |
| p23 | Auto repeater offset | 0=off 1=on |
| p24 | 1750 Hz hold | 0=off 1=on |
| p25 | *(unknown)* | |
| p26 | Brightness level | 0=off 1=max |
| p27 | Auto brightness | 0=off 1=on |
| p28 | Backlight colour | 0=amber 1=green |
| p29 | PF1 key function | 0–22 (see Programmable Keys table) |
| p30 | PF2 key function | 0–22 |
| p31 | Mic PF1 key function | 0–22 |
| p32 | Mic PF2 key function | 0–22 |
| p33 | Mic PF3 key function | 0–22 |
| p34 | Mic PF4 key function | 0–22 |
| p35 | Mic key lock | 0=off 1=on |
| p36 | Scan resume | 0=time 1=carrier 2=seek |
| p37 | APO | 0=off 1=30min 2=60min 3=90min 4=120min 5=180min |
| p38 | External data band | 0=Band A 1=Band B 2=TX-A/RX-B 3=TX-B/RX-A |
| p39 | External data speed | 0=1200 bd 1=9600 bd |
| p40 | SQC output source | 0=off 1=BUSY 2=SQL 3=TX 4=BUSY\|TX 5=SQL\|TX |
| p41 | Auto PM store | 0=off 1=on |
| p42 | Display partition bar | 0=off 1=on |

*Note: MU uses p1–p41 in the Set form (41 params) but the table above lists p42 per
the LA3QMA docs. Verify count against your firmware version before writing.*

---

### PC — Transmit Power

Reads or sets the power level for a band.

```
Get:  PC p1
Set:  PC p1,p2
      → p1,p2

p1   band    0=Band A  1=Band B
p2   power   0=High    1=Medium    2=Low (EL on some models)
```

---

### PV — Programmable VFO Limits

Sets the tuning range limits for each frequency sub-band. Useful for restricting VFO
sweep to a specific portion of a band.

```
Get:  PV p1
Set:  PV p1,p2,p3
      → p1,p2,p3

p1  sub-band index
    0=Band A 118 MHz
    1=Band A 144 MHz
    2=Band A 220 MHz
    3=Band A 300 MHz
    4=Band A 430 MHz
    5=Band B 144 MHz
    6=Band B 220 MHz
    7=Band B 300 MHz
    8=Band B 430 MHz
    9=Band B 1200 MHz

p2  lower limit in MHz, 4 digits (e.g. 0144)
p3  upper limit in MHz, 4 digits (e.g. 0148)
```

---

### RT — Real-Time Clock *(TM-D710 / GPS models)*

Reads or sets the internal clock used for APRS time-stamping.

```
Get:  RT
Set:  RT p1,p2,p3,p4,p5,p6
      → p1,p2,p3,p4,p5,p6

p1  year  YYYY
p2  month  MM
p3  day    DD
p4  hour   HH
p5  minute MM
p6  second SS
```

---

### RX — Receive Mode

Drops the radio out of transmit. No parameters.

```
Send:  RX
```

---

### SR — Reset

Performs a radio reset. **Use with care — some reset types erase all memories.**

```
Send:  SR p1

p1   0=VFO reset (clears VFO settings only)
     1=Partial reset
     2=PM (Program Memory) reset
     3=Full reset (wipes everything)
```

*The display unit responds `N` to this command; send it to the main unit.*

---

### SQ — Squelch Level

Sets or reads the squelch threshold for a band.

```
Get:  SQ p1
Set:  SQ p1,p2
      → p1,p2

p1   band    0=Band A  1=Band B
p2   level   00–1F hexadecimal (0x00=open/off  0x1F=fully closed)
```

32 steps total. Mid-range is approximately `0x0F`.

**Examples:**
```
SQ 0,00   → Band A open squelch
SQ 0,0F   → Band A mid
SQ 0,1F   → Band A tight
SQ 1,08   → Band B light
```

---

### SS — S-Meter Squelch

Enables squelch based on received signal strength rather than noise threshold.

```
Get:  SS p1
Set:  SS p1,p2
      → p1,p2

p1   band    0=Band A  1=Band B
p2   mode    0=S-meter squelch off
             1=S-meter squelch on
             2=S-meter squelch level (query returns current level)
```

---

### TC — TNC Control

Enables or disables the built-in TNC. **Send three Ctrl-C characters (`^C^C^C`) before
issuing this command** to escape any active TNC session first.

```
Set:  TC p1

p1   0=TNC off   1=TNC on
```

---

### TN — TNC Mode

Selects the TNC operating mode and which band carries packet data.

```
Get:  TN
Set:  TN p1,p2
      → p1,p2

p1   mode    0=off   1=APRS   2=TNC (packet)

p2   data band
     0=Band A
     1=Band B
     2=TX on Band A, RX on Band B
     3=TX on Band B, RX on Band A
```

---

### TT — Transmit Tone Burst

Transmits the tone frequency selected by `BT`. Send once to start, once to stop. The
radio must be in TX mode or this keys up the PTT automatically.

```
Send:  TT    (start)
Send:  TT    (stop)
```

---

### TX — Transmit

Keys up the PTT on whichever band has PTT assigned (see `BC`). Audio comes from the
microphone jack, not from the DATA port.

```
Send:  TX
```

---

### TY — Radio Type / Region

Returns radio region and enabled feature flags. Read-only.

```
Get:  TY
      → p1,p2,p3,p4,p5

p1  region      M=EU   K=US (and variants)
p2  MARS/CAP TX expansion   0=no   1=enabled
p3  Max TX expansion        0=no   1=enabled
p4  Cross-band repeat       0=no   1=capable
p5  SkyCommand              0=no   1=capable
```

---

### UP — Step Up (Emulate Up Key)

Increments the VFO or memory channel by one step.

```
Set:  UP [n]
      (no response)

n   number of steps 01–99; omit or 00 for one step in MR mode
```

---

### VM — VFO / Memory Mode

Selects the operating mode for a band — VFO tuning, memory channel recall, call
channel, or weather channel.

```
Get:  VM p1
Set:  VM p1,p2
      → p1,p2

p1   band    0=Band A  1=Band B
p2   mode    0=VFO     1=Memory    2=Call channel    3=WX channel
```

---

## Reference Tables

### Band Codes

| Code | Meaning |
|------|---------|
| 0 | Band A |
| 1 | Band B |
| 2 | TX Band A, RX Band B |
| 3 | TX Band B, RX Band A |

---

### Step Size Codes

| Code | Step |
|------|------|
| 0 | 5 kHz |
| 1 | 6.25 kHz |
| 2 | 28.33 kHz |
| 3 | 10 kHz |
| 4 | 12.5 kHz |
| 5 | 15 kHz |
| 6 | 20 kHz |
| 7 | 25 kHz |
| 8 | 30 kHz |
| 9 | 50 kHz |
| A | 100 kHz |

---

### Shift / Duplex Codes

| Code | Meaning |
|------|---------|
| 0 | Simplex (or split via p14 TX freq) |
| 1 | + (positive offset) |
| 2 | − (negative offset) |

*Values above 2 are silently rounded down to 0 (simplex) on write.*

---

### Modulation Mode Codes

| Code | Mode |
|------|------|
| 0 | FM |
| 1 | NFM (narrow FM) |
| 2 | AM |

---

### CTCSS Tone Table (42 tones, index 00–41)

| Index | Hz | Index | Hz | Index | Hz |
|:-----:|---:|:-----:|---:|:-----:|---:|
| 00 | 67.0 | 14 | 107.2 | 28 | 173.8 |
| 01 | 69.3 | 15 | 110.9 | 29 | 179.9 |
| 02 | 71.9 | 16 | 114.8 | 30 | 186.2 |
| 03 | 74.4 | 17 | 118.8 | 31 | 192.8 |
| 04 | 77.0 | 18 | 123.0 | 32 | 203.5 |
| 05 | 79.7 | 19 | 127.3 | 33 | 206.5 |
| 06 | 82.5 | 20 | 131.8 | 34 | 210.7 |
| 07 | 85.4 | 21 | 136.5 | 35 | 218.1 |
| 08 | 88.5 | 22 | 141.3 | 36 | 225.7 |
| 09 | 91.5 | 23 | 146.2 | 37 | 229.1 |
| 10 | 94.8 | 24 | 151.4 | 38 | 233.6 |
| 11 | 97.4 | 25 | 156.7 | 39 | 241.8 |
| 12 | 100.0 | 26 | 162.2 | 40 | 250.3 |
| 13 | 103.5 | 27 | 167.9 | 41 | 254.1 |

*Note: The LA3QMA source has a typo listing index 09 twice (for both 91.5 and 127.3 Hz).
The correct value at index 09 is 91.5 Hz; index 19 is 127.3 Hz.*

---

### DCS Code Table (104 codes, index 000–103)

| Index | DCS | Index | DCS | Index | DCS | Index | DCS |
|:-----:|----:|:-----:|----:|:-----:|----:|:-----:|----:|
| 000 | 023 | 026 | 152 | 052 | 311 | 078 | 466 |
| 001 | 025 | 027 | 155 | 053 | 315 | 079 | 503 |
| 002 | 026 | 028 | 156 | 054 | 325 | 080 | 506 |
| 003 | 031 | 029 | 162 | 055 | 331 | 081 | 516 |
| 004 | 032 | 030 | 165 | 056 | 332 | 082 | 523 |
| 005 | 036 | 031 | 172 | 057 | 343 | 083 | 565 |
| 006 | 043 | 032 | 174 | 058 | 346 | 084 | 532 |
| 007 | 047 | 033 | 205 | 059 | 351 | 085 | 546 |
| 008 | 051 | 034 | 212 | 060 | 356 | 086 | 565 |
| 009 | 053 | 035 | 223 | 061 | 364 | 087 | 606 |
| 010 | 054 | 036 | 225 | 062 | 365 | 088 | 612 |
| 011 | 065 | 037 | 226 | 063 | 371 | 089 | 624 |
| 012 | 071 | 038 | 243 | 064 | 411 | 090 | 627 |
| 013 | 072 | 039 | 244 | 065 | 412 | 091 | 631 |
| 014 | 073 | 040 | 245 | 066 | 413 | 092 | 632 |
| 015 | 074 | 041 | 246 | 067 | 423 | 093 | 654 |
| 016 | 114 | 042 | 251 | 068 | 431 | 094 | 662 |
| 017 | 115 | 043 | 252 | 069 | 432 | 095 | 664 |
| 018 | 116 | 044 | 255 | 070 | 445 | 096 | 703 |
| 019 | 122 | 045 | 261 | 071 | 446 | 097 | 712 |
| 020 | 125 | 046 | 263 | 072 | 452 | 098 | 723 |
| 021 | 131 | 047 | 265 | 073 | 454 | 099 | 731 |
| 022 | 132 | 048 | 266 | 074 | 455 | 100 | 732 |
| 023 | 134 | 049 | 271 | 075 | 462 | 101 | 734 |
| 024 | 143 | 050 | 274 | 076 | 464 | 102 | 743 |
| 025 | 145 | 051 | 306 | 077 | 465 | 103 | 754 |

---

### DTMF Character Map

| p1,p2 | Character | | p1,p2 | Character |
|:-----:|:---------:|-|:-----:|:---------:|
| 0,0 | 0 | | 0,A | A |
| 0,1 | 1 | | 0,B | B |
| 0,2 | 2 | | 0,C | C |
| 0,3 | 3 | | 0,D | D |
| 0,4 | 4 | | 0,E | * |
| 0,5 | 5 | | 0,F | # |
| 0,6 | 6 | | 1,1 | 697 Hz |
| 0,7 | 7 | | 1,2 | 770 Hz |
| 0,8 | 8 | | 1,3 | 852 Hz |
| 0,9 | 9 | | 1,4 | 941 Hz |
| | | | 1,5 | 1209 Hz |
| | | | 1,6 | 1336 Hz |
| | | | 1,7 | 1447 Hz |
| | | | 1,8 | 1633 Hz |

---

### Programmable Key Function Codes (MU p29–p34)

| Code | Function | Panel PF1/2 | Mic PF1–4 |
|------|----------|:-----------:|:---------:|
| 00 | WX | ✓ | ✓ |
| 01 | Frequency Band | ✓ | ✓ |
| 02 | CTRL | ✓ | ✓ |
| 03 | Monitor | ✓ | ✓ |
| 04 | VGS | ✓ | ✓ |
| 05 | VOICE | ✓ | ✓ |
| 06 | Group Up | ✓ | ✓ |
| 07 | Menu | ✓ | ✓ |
| 08 | Mute | ✓ | ✓ |
| 09 | Shift | ✓ | ✓ |
| 0A | Dual | ✓ | ✓ |
| 0B | M>V | ✓ | ✓ |
| 0C | VFO | — | ✓ |
| 0D | MR | — | ✓ |
| 0E | CALL | — | ✓ |
| 0F | MHz | — | ✓ |
| 10 | Tone | — | ✓ |
| 11 | REV | — | ✓ |
| 12 | LOW | — | ✓ |
| 13 | LOCK | — | ✓ |
| 14 | A/B | — | ✓ |
| 15 | ENTER | — | ✓ |
| 16 | 1750 Hz | ✓ | ✓ |

---

### SQC Output Source Codes (MU p40)

| Code | SQC triggers on |
|------|-----------------|
| 0 | OFF (SQC pin always low) |
| 1 | BUSY (squelch open) |
| 2 | SQL (squelch threshold) |
| 3 | TX (transmitting) |
| 4 | BUSY or TX |
| 5 | SQL or TX |

---

### APO (Auto Power Off) Codes (MU p37)

| Code | Timeout |
|------|---------|
| 0 | Off |
| 1 | 30 min |
| 2 | 60 min |
| 3 | 90 min |
| 4 | 120 min |
| 5 | 180 min |

---

## Programming Mode (Binary Memory Access)

Use for bulk channel operations. Much faster than `ME` for loading many channels.

### Enter / Exit

```
Enter:
  >>> 0M PROGRAM\r          hex: 30 4D 20 50 52 4F 47 52 41 4D 0D
  <<< 0M\r

Exit:
  >>> E                     hex: 45
  <<< <status> \r \x00
```

Display shows `PROG MCP`. If no command follows quickly, display shows `PROG ERR`.
The radio still responds to R/W commands in the error state; status byte will have
additional bits set.

### Read Block

```
>>> R <addr_hi> <addr_lo> <length>
<<< W <addr_hi> <addr_lo> <length> <data bytes>
>>> 0x06     (acknowledge)
<<< <status>
```

### Write Block

```
>>> W <addr_hi> <addr_lo> <length> <data bytes>
<<< <status>   (0x06 = ok, 0x0F = error)
```

### Memory Map

| Address | Contents |
|---------|----------|
| 0x0010 | Crossband repeat (1=on) |
| 0x0011 | Wireless remote (1=on) |
| 0x0012 | Remote ID (wireless access code) |
| 0x0016 | Current PM channel (0–5) |
| 0x0017 | Key lock (0=off 1=on) |
| 0x0021 | PC port speed (0=9600 1=19200 2=38400 3=57600) |
| 0x0030–0x00CF | DTMF memory codes (16 bytes × 10 channels) |
| 0x00D0–0x00FF | DTMF memory names (8 bytes × 10 channels) |
| 0x0120–0x016F | EchoLink memory names (8 bytes × 10) |
| 0x0170 | Repeater ID (12 bytes) |
| 0x0190–0x01DF | EchoLink memory codes (8 bytes × 10) |
| 0x0200–0x0C00 | Programmable memory (512 bytes × 6 PM slots) |
| 0x0E00 | Channel flags (2 bytes × 1000 channels) |
| 0x1700–0x557F | Channel records (16 bytes × 1000 channels) |
| 0x5800–0x773F | Channel names (8 bytes × 1000 channels) |
| 0x77E0–0x782D | WX channel names |
| 0x7D00–0x7D9F | Group names (16 bytes × 8 groups) |
| 0x7DA0 | PM names (16 bytes × 5 PMs) |

### Channel Record Structure (16 bytes at 0x1700 + channel×16)

| Bytes | Contents |
|-------|----------|
| 0–3 | RX frequency × 1,000,000 |
| 4 | RX step size code |
| 5 | Modulation mode code |
| 6 | Bitfield: bits 4–6=tone/CTCSS/DCS, bit 3=reverse, bit 2=split, bits 0–1=shift |
| 7 | Tone/CTCSS encode index |
| 8 | CTCSS squelch index |
| 9 | DCS code index |
| 10–13 | TX offset or TX frequency × 1,000,000 |
| 14 | TX step size code |
| 15 | (padding) |

Tone bitfield in byte 6:
```
0b100 = tone encode    0b010 = CTCSS    0b001 = DCS    0b000 = none
```

### Channel Flag Structure (2 bytes at 0x0E00 + channel×2)

| Value | Meaning |
|-------|---------|
| `0xFF 0xFF` | Channel deleted / empty |
| byte 1 = `0x05` | VHF channel |
| byte 1 = `0x08` | UHF channel |
| byte 1 high bit set | Channel disabled (not deleted, just off) |
| byte 2 bit 0 | Lockout / scan skip |

---

## Known Issues & Workarounds

| Issue | Detail | Fix / Workaround |
|-------|--------|-----------------|
| **No CTS → silent radio** | Radio accepts commands but never responds without CTS | Short RTS→CTS on Mini-DIN8 pins 1–2 |
| **Hamlib `\r` stripping** | `kenwood_transaction()` stripped CR terminator (bug #1767) | Use Hamlib ≥ 4.6.4 |
| **Programming mode timeout** | Radio shows PROG ERR after ~5 s inactivity | Re-send `0M PROGRAM\r` |
| **No cross-tone** | Cannot use different RX/TX CTCSS codes on same channel | Use DCS, or source a radio that supports cross-tone |
| **freq field width** | Hamlib uses 10 digits; some forum code uses 11 | Pad to 10 digits with leading zeros |
| **ME erase needs trailing comma** | `ME 042` → error; `ME 042,` → erases channel | Always include the comma |
| **DT requires active TX** | DTMF send silently ignored if not transmitting | Issue `TX` first, then `DT`, then `RX` |
| **TC requires ^C^C^C first** | TNC session must be exited before TC will work | Send three 0x03 bytes before TC |
| **SR display unit returns N** | Reset command only works on main unit | Send SR to main unit serial port |
| **RFI lockup** | USB-serial adapters lock up during transmit | Add ferrite chokes to serial cable |
| **Programming mode ↔ firmware version mismatch** | Loading another radio's full memory dump can brick the radio | Never bulk-restore memories from a different firmware version |

---

## Quick-Reference Examples

```
# Confirm connection
ID

# Band A → 146.520 MHz simplex, no tones, mid squelch
BC 0,0
VM 0,0
FO 0,0146520000,0,0,0,0,0,0,00,00,000,00000000,0
SQ 0,0A

# Band A → 145.500 MHz, +600 kHz, CTCSS 100.0 Hz encode
FO 0,0145500000,0,1,0,1,0,0,12,12,000,00600000,0

# Band A → memory channel 42
VM 0,1
MR 0,042

# Write memory channel 050: 446.000 MHz, DCS 023 (index 000)
ME 050,0446000000,0,0,0,0,0,1,00,00,000,00000000,0,0446000000,0,0
MN 050,SIMP

# Read Band B squelch
SQ 1

# DCS on Band B (DCS code 023 = index 000)
FO 1,0446000000,0,0,0,0,0,1,00,00,000,00000000,0

# Lock front panel
LK 1

# Read firmware versions
FV 0
FV 1

# Transmit 1750 Hz burst (tone set to 1750 Hz first)
BT 2
TT

# Full radio reset (DESTRUCTIVE — erases all memories)
SR 3
```

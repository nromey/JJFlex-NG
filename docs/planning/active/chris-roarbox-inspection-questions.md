# Roarbox Inspection Questions for Chris

**To:** Chris Polk — `cspolk@outlook.com`
**From:** Noel
**Date:** 2026-05-06
**Purpose:** Pre-purchase verification before ordering CPU upgrade parts for roarbox (the 1U Dell R620 in your office). Goal is to bring it from 1× E5-2620 v2 (6c/12t) up to 2× E5-2697 v2 (24c/48t) — the official R620 ceiling — and slot a Ubiquiti switch in front of your gear so we can stop calling you whenever we need to open a port.

---

## Quick context for Chris

Hey Chris — while you have the R620 lid open, can you check four things for me? This determines exactly what parts I need to ship and whether the BIOS needs updating before the swap. None of these require any disassembly beyond what you've already done. Should take ~10 minutes total.

---

## The four questions

### 1. Airflow shroud cutouts

Look at the plastic airflow shroud over the CPUs (the molded piece that channels fan air across the heatsinks). Does it have **two cutouts** — one for each CPU heatsink — or just **one cutout** with the other side as solid plastic?

- **Two cutouts:** Great, no shroud purchase needed.
- **One cutout (other side is solid):** I'll add a dual-CPU shroud (Dell PN F84CR, ~$10-15) to the order so you don't have to scrounge one.

### 2. Chassis fan count

The front fan bank — the row of small fans behind the front bezel that pushes air through the chassis. Are all **6 fan slots populated and healthy**, or are any missing or dead?

- **All 6 present and spinning normally:** Done, no fan purchase needed. Adding a second CPU does not require additional fans (a 1-CPU and 2-CPU R620 ship with the same fan complement).
- **Any missing or visibly dead:** Tell me how many and I'll source replacement fans.

### 3. CPU2 socket dust cover + pin inspection

The empty CPU2 socket — is the small black plastic dust cover popped over the socket pins (protecting them while the socket sits unused), or is the socket bare?

- **Dust cover present:** Pins are protected. Excellent.
- **Bare socket, pins exposed:** Visually inspect with a flashlight and a magnifier (your phone's macro mode works well, take a photo). Are any pins **bent, missing, or visibly damaged**? Send the photo if so. Bent pins on the LGA 2011 socket are a board-replacement situation, and we'd want to know that BEFORE buying CPUs.

### 4. Current BIOS version

Easiest capture path:
- **Via SSH** (we already have access from Noel's machine): `sudo dmidecode -s bios-version`
- **Via iDRAC web UI:** Login → System → BIOS → Version
- **Via boot screen:** Press F2 at POST, the version is displayed top-left

I want to confirm the BIOS supports E5-2697 v2 chips before we install them. The fact that the current E5-2620 v2 is running tells us the BIOS is at least at v2-supporting era (revision 2.x or later) — but specific late-cycle SKUs sometimes need the latest revision. If BIOS is current, we proceed straight to the swap. If not, I'll send instructions to update first while the existing CPU is still in the box (BIOS update doesn't require a reboot; runs from the OS).

---

## One bonus question (separate from the chassis work)

While you're at it — can you check on your firewall whether **ports 80 and 443** are currently forwarded from roarbox's public IP to the box?

- **Yes, both are already forwarded:** Perfect, I can start standing up HTTPS services on roarbox immediately (the solar service is the first one queued up). I don't have to wait for the switch to arrive to ship something useful.
- **No, neither/either is forwarded:** Are you willing to open them? It's a small one-time firewall change on your end — and once the switch goes in later, your firewall drops out of roarbox's traffic path entirely so this becomes moot. If you'd rather wait until switch-day so it's a single change, that's also fine; I just won't ship anything public-facing until then.
- **You don't remember:** Easiest check is probably your firewall admin UI's port-forward / NAT table. Look for entries forwarding 80 or 443 to roarbox's private LAN address (`10.10.2.247`).

This is purely informational — won't change what parts I order, won't delay anything. It just tells me whether I can deploy services now vs after the switch lands.

---

## Reply format

Easiest if you just send back something like:

> 1. Shroud: dual cutouts (or: single cutout)
> 2. Fans: 6/6 present and healthy (or: N missing/dead)
> 3. Dust cover: present (or: bare; pins look fine / pins bent on socket — see photo)
> 4. BIOS version: 2.X.X
> 5. Ports 80/443: both forwarded / one forwarded / neither / can't easily tell

Once I have those answers I'll order the CPU pair, heatsink, paste, and shroud (if needed), and ship them to your office for install.

---

## What's NOT changing during the CPU swap

To clear up anything that might be unclear from the parts list:

- The chassis itself stays put — no swap, no re-rack.
- Existing OS, data, network config, configured users, etc., are untouched. CPU swap is hardware-only; Linux just notices more cores at next boot.
- Power supply and chassis fans don't need replacing — both already handle 2× 130W CPUs.
- No changes needed to your firewall or upstream gear during the CPU swap. The Ubiquiti switch installation is a separate, later step we can sequence after the CPU work is verified.

---

## About the Ubiquiti switch (recap of what we discussed)

Separate from the CPU swap, this is the second piece — dropping a small Ubiquiti 8-port 2.5G switch into your office to sit between the ONT and the rest of your gear. You and I already talked through the why, but since it'll be in writing here for both of us, quick recap so nothing gets lost:

### What the switch is for

ONT (10 Gbps fiber) → **new Ubiquiti 8-port 2.5G switch** → your firewall + roarbox + (future devices, when we want them) — each device hanging off its own port, each public-facing host with its own public IP.

### Why this is a good idea — for *you*

- **No more port-change tickets from me.** Right now, if I want to expose a new service on roarbox or change a port, I have to ping you and ask you to touch your firewall. After the switch goes in, that responsibility moves entirely to roarbox's host firewall (UFW), the same model we use on rarbox. You stop being my network helpdesk for roarbox traffic.
- **Your company's connectivity doesn't change at all.** Your office firewall stays exactly where it is, between the new switch and your company LAN. Nothing about how your other computers reach the internet, get DHCP, talk to printers, hit your VPN — none of that changes. Your firewall is still in charge of everything behind it.
- **Easy to mount.** Small unit, non-rack-mount, non-PoE — you mentioned screwing brackets to a piece of plywood, that works perfectly. Doesn't consume rack space.
- **It's the least I can do.** You're hosting roarbox for free. Giving you the switch is part of the deal — it's my equipment paying me back for your hospitality.

### Why this is a good idea — for me

- Roarbox keeps its dedicated public IP, sits on the flat shared segment alongside your firewall (each with its own public IP), and handles its own port policy via UFW.
- If I ever want to colocate another device with you in the future, it just plugs into a free port — no rewiring of your gear required.
- I get the same "lock down everything, open nothing without thinking" UFW posture we use on rarbox.

### Will you have to touch the R620 again for the switch part?

**No.** The switch install happens entirely outside the chassis — it's all physical cabling between ONT, switch, roarbox's network port, and your firewall's WAN port. The R620 itself stays sealed after the CPU swap. The work is "unplug existing patch cables from current location, plug into the new switch instead, plus one new cable for roarbox." Probably 15 minutes of cabling, no chassis work.

### Sequencing

- **Step 1 (now):** CPU swap. Self-contained, no network impact.
- **Step 2 (later, your call when):** Switch install. We sequence whenever's convenient for you. The CPU work and switch install are independent — neither blocks or depends on the other.

You don't need to do anything about the switch right now — just confirm you're still good with the plan when you reply with the four CPU inspection answers. I'll order the switch separately and ship it to you when we're ready for that phase.

---

Thanks Chris — let me know what you find on the four checks above, and yell if any of the switch plan changed since we last talked.

# Roarbox Upgrade Parts List

**Last updated:** 2026-05-07
**For:** Upgrading roarbox (Dell PowerEdge R620) from 1× E5-2620 v2 (6c/12t) to 2× E5-2697 v2 (24c/48t), populating memory to 128 GB at full DDR3-1600 speed to support JJF's compute workload (solar runs + RNN model training + agent triage), expanding the data RAID6 array from 4 disks to 6 disks (1.2 TB → 2.4 TB usable at same redundancy), and adding an 8-port 2.5G switch in front of Chris's office gear so roarbox port control lives on UFW (same model as rarbox).

---

## Current state of roarbox

| Aspect | Value |
|---|---|
| Chassis | Dell PowerEdge R620 (1U) |
| CPU populated | 1× Intel Xeon E5-2620 v2 in CPU1 socket; CPU2 socket empty |
| RAM | 16 GB on CPU1's bank (2× 8 GB DDR3-1600 ECC RDIMM); CPU2's 12 DIMM slots currently inert. Donor Ivy Bridge box has 8× 8 GB DDR3 sticks of unknown spec — match-check pending. |
| Disks | LD0 (sda): RAID1 of 2× 146 GB Toshiba MK1401GRRB 15K SAS → 136 GiB usable for OS. LD1 (sdb): RAID6 of 4× 600 GB SAS 10K (mixed: 2× Toshiba AL13SEB600 at ~31,678 power-on hours + 2× Seagate ST600MM0088 at 139 hours, recent replacements) → 1.199 TB usable. Slots 6 & 7 empty. |
| PERC | H710 Mini Mono (512 MB cache), 8-bay 2.5" SFF backplane. Online RAID6 capacity expansion (OCE) supported. |
| NIC | 4× onboard 1 Gbps; only `eno1` connected |
| Network | Dedicated public IP; behind Chris's office ISP block |

## Target state after upgrade

| Aspect | Value |
|---|---|
| CPU | 2× Intel Xeon E5-2697 v2 → **24 cores / 48 threads** at 2.7 / 3.5 GHz |
| RAM | 128 GB (8× 16 GB DDR3-1600 ECC RDIMM 2Rx4) at full DDR3-1600 speed in balanced 2 DPC × 4 channel config on CPU1; CPU2's 12 slots remain dormant until CPU2 install. Existing 2× 8 GB sticks + donor box's 8× 8 GB sticks (if compatible) become spares. |
| Disks | 6× 600 GB SAS 10K in RAID6 → **2.4 TB usable** at same 2-disk fault tolerance. OS RAID1 array (sda) untouched. |
| Network | ONT → Noel's 8-port 2.5G Ubiquiti switch → roarbox + Chris's firewall (each with own public IP) |
| Port control | UFW on roarbox; no Chris-side firewall touch needed for new ports |

---

## Server upgrade parts

### Required

| Item | Qty | Notes | Where to buy | Price (~2026-05-06) |
|---|---|---|---|---|
| **Intel Xeon E5-2697 v2** (stepping SR19H) | 2 | **Matched pair, same SKU.** Do NOT mix v1 / v2 or different v2 SKUs in the two sockets. Avoid engineering samples (ES). | eBay search: `E5-2697 v2 SR19H` | $60-120 each → **$120-240 pair** |
| **Dell R620 1U heatsink** | 1 | Dell PN **2DPDC** or **48VTC**. **R620-specific (1U low-profile)** — you cannot reuse R820 heatsinks; they're 2U-height and won't fit. | eBay search: `Dell R620 heatsink 2DPDC` | $10-20 |

(Thermal paste: Chris has plenty, no need to buy.)

### Optional future add-ons (NOT in this round)

| Item | Purpose | Cost |
|---|---|---|
| Intel I225-V or I226-V 2.5G PCIe NIC | Lets roarbox actually use the 2.5G switch link (current `eno1` is 1 Gbps) | $25-50 |

(Memory was previously listed here as a future add-on; promoted into this round — see Memory section below.)

---

## Memory

**Buying: 8× DDR3-1600 ECC RDIMM, 16 GB per stick, dual-rank (2Rx4), 1.5V → 128 GB total at full DDR3-1600 speed.**

Installs in CPU1's 8 active DIMM slots in balanced 2 DPC × 4 channel config. Full quad-channel bandwidth.

### Why 128 GB

Roarbox is the compute box; rarbox at Hetzner handles the I/O-bound work (JJ Flexible Data Provider, crash receiver, web stuff). Roarbox's workload:

- **Solar runs** — HF propagation modeling, K-index/SFI analysis, prediction.
- **RNN model training for ham radio noise reduction** — 32-64 GB peak resident during a serious training run; multiple parallel experiments compound that.
- **Claude triage agents on larger contexts** as those start running here.

128 GB sizes correctly for that mix. 64 GB would constrain RNN training. 256 GB requires LRDIMM (~3× per-GB cost) and isn't justified at current scale.

### SKU and sourcing

```
DDR3-1600 ECC RDIMM, 16 GB, dual-rank (2Rx4), 1.5V
```

Any of these (mix-and-match across the 8 sticks is fine):
- Samsung M393B2G70BH0-CK0
- Hynix HMT42GR7AFR4A-PB or HMT42GR7BFR4A-PB
- Micron MT36JSF2G72PZ-1G6E1
- Crucial CT204872BB160B

Sourcing:
- **ServerSupply** — established refurbisher, deep R620 inventory.
- **Memory4Less** — also reliable, Dell-specific listings.
- **eBay refurbisher listings with 99%+ feedback** — typically cheapest.

Avoid generic Amazon listings without explicit ECC RDIMM spec.

### Existing 8 GB sticks

The 2× 8 GB currently in roarbox + the 8× 8 GB in the donor Ivy Bridge box become **spares** if compatible (DDR3-1600 ECC RDIMM). Photograph one donor label and confirm before deciding whether to keep them or sell/donate. Doesn't gate the buy.

### Future memory growth (informational only — not in this round)

If RNN training pressure pushes past 128 GB later:

- **Cheaper path:** mirror this buy in CPU2's slots when CPU2 is active → 256 GB total at full speed, ~$80-160 additional.
- **More expensive path:** replace with 8× 32 GB LRDIMM → 256 GB on CPU1 alone, ~$240-480.

Don't pre-buy either; revisit when there's real workload pressure.

---

## Disks

**Buying: 2× Seagate ST600MM0088, 600 GB / 10K / 2.5" SFF SAS 12 Gbps, with Dell caddies → expand RAID6 from 4 disks to 6 disks.**

Slots 6 and 7 in the chassis are empty (confirmed by roarbox diagnostic 2026-05-07). After install + online capacity expansion (OCE), the data RAID6 array goes from 1.2 TB usable to 2.4 TB usable at the same redundancy posture (2-disk fault tolerance). OS RAID1 array is untouched.

### Why this SKU

The existing 4-disk array is mixed: 2× Toshiba AL13SEB600 (31,678 power-on hours, original) + 2× Seagate ST600MM0088 (139 hours, recent replacements). Buying 2 more matching Seagates keeps the array at 4× Seagate + 2× Toshiba — better wear distribution since the Toshibas are older. The Seagate model is already validated by the H710 in this exact chassis, and Dell-firmware (TT31 or later) is preferred over generic Seagate firmware for clean PERC reporting.

### SKU specifics

```
Seagate ST600MM0088
600 GB / 10K RPM / 2.5" SFF / SAS 12 Gbps / 512n
With Dell caddy (gen11/12 hot-swap tray)
```

Dell DPN stickers (any of these are the same drive): **0R95FV / 0K1JY9 / 033KFP**.

### Sourcing

| Source | Price/drive | Notes |
|---|---|---|
| eBay Top Rated refurbisher (with caddy) | $25-50 | Filter "Top Rated Plus" + "Seller refurbished". 30-90 day warranty typical. |
| ServerSupply.com | ~$75 | Dell-OEM refurb, formal warranty. |
| AllHDD.com | $40-60 | Refurb, listed warranty. |

Search keywords: `ST600MM0088 Dell caddy refurbished`. Top Rated Plus + Seller Refurbished filter. **Budget: $50-100 total** for 2 drives + caddies.

### Pre-install workflow (DEFERRED to OCE runbook)

Adding the disks isn't drop-in — RAID6 OCE on H710 is a multi-step ops procedure that lives in its own runbook (to be drafted when parts arrive). The full sequence:

1. **Verify PERC firmware ≥ A11** via iDRAC web UI → Storage → Controllers → PERC H710 Mini → Properties. Latest is **A12 (21.3.5-0002)**. If older than A11, flash to A12 from Dell's support site before triggering OCE — older firmware has known OCE-on-RAID6 issues.
2. **Install management tool** — either Broadcom `storcli64` (lightweight, single binary) or Dell OMSA on Debian (heavier, repo-based). The BIOS Ctrl+R utility cannot do RAID6 OCE; software path is required.
3. **Backup sdb** (the 1.2 TB volume) before reshape — OCE is supposed to be safe, but reshape windows are when arrays die. Don't skip.
4. **Insert the 2 new drives** into slots 6 and 7. PERC will see them as "Unconfigured Good."
5. **Trigger OCE** via storcli or OMSA: `storcli /c0/v1 start migrate type=raid6 option=add drives=32:6,32:7` (slot/enclosure IDs verified per actual storcli output before running).
6. **Reshape window: 12-36 hours.** Array stays online; I/O slow during. Plan for off-hours.
7. **Resize filesystem** after reshape: `resize2fs /dev/sdb` (the FS is ext2, no LVM in the way).

Until that runbook is drafted and exercised, **don't trigger OCE manually**. The BIOS Ctrl+R utility can't do it accidentally, so just inserting drives into slots 6/7 won't auto-expand — they'll sit as Unconfigured Good until explicitly added to the array.

---

## Network parts

### Required

| Item | Qty | Notes | Where to buy | Price |
|---|---|---|---|---|
| **Ubiquiti UniFi Switch Flex 2.5G 8** (USW-Flex-2.5G-8) | 1 | 8× 2.5GbE + 1× 10G SFP+ + 1× 10G RJ45 uplink, non-PoE, desktop/shelf form factor (no rack ears needed). Chris confirmed this fits his shelf and is an upgrade over current gear. Verify SKU on Ubiquiti store before ordering — UniFi lineup shifts. | `https://store.ui.com` → Switching → Flex line | $200-250 |
| Cat 6 patch cables | 4-6 short | 1-3 ft for switch-to-device runs | Amazon, Monoprice | $1-3 each |

---

## Total cost

| Item | Cost |
|---|---|
| 2× Intel Xeon E5-2697 v2 (matched pair, SR19H) | $70-100 |
| Dell R620 1U heatsink (PN 2DPDC or 48VTC) | $10-20 |
| 8× DDR3-1600 ECC RDIMM 16 GB 2Rx4 | $160-200 |
| 2× Seagate ST600MM0088 600 GB 10K SAS w/ Dell caddy | $50-100 |
| Ubiquiti UniFi Switch Flex 2.5G 8 (USW-Flex-2.5G-8) | $200-250 |
| Cat 6 patch cables (1-3 ft, qty 4-6) | $5-15 |
| **Total** | **~$495-685** |

Optional later (not in this round): 2.5G PCIe NIC for the switch link, +$25-50.

(Thermal paste and airflow shroud not in this buy — Chris already has paste and the dual-CPU shroud is in place. CPU + memory price ranges anchored on real eBay listings observed 2026-05-07, not earlier theoretical estimates.)

---

## What ships to Chris

1. Sealed matched pair of E5-2697 v2 CPUs (original packaging where possible — counterfeit risk on eBay is real; prefer sellers with high feedback)
2. Dell R620 heatsink (separately packed)
3. The CPU install runbook (drafted after parts arrive — covers diagonal-screw torque sequence, paste application, post-install BIOS verification)

(Chris already has paste and the dual-CPU shroud is in place — neither in the shipment.)

The Ubiquiti switch, the memory, and the SAS disks ship separately to roarbox's location (or wherever Chris wants to receive them). Memory installs are independent of the CPU swap. Disk install requires the OCE runbook (separate from the CPU runbook); don't trigger drive insertion + OCE in the same maintenance window as the CPU swap.

---

## Verification before install

Inspection results from Chris's 2026-05-07 hands-in pass + roarbox-Claude diagnostic:

1. ☑ Airflow shroud has dual CPU cutouts — no shroud swap needed
2. ☑ Chassis fan count in place
3. ☑ CPU2 socket dust cover present + no bent pins
4. ☑ PERC H710 Mini Mono confirmed; 8-bay backplane, slots 6 & 7 empty, OCE on RAID6 supported
5. ☐ BIOS version supports E5-2697 v2 — run `sudo dmidecode -s bios-version` on roarbox before scheduling CPU install. Almost certainly fine (E5-2620 v2 already runs there), but cheap insurance.
6. ☐ PERC H710 firmware ≥ A11 (target A12 = 21.3.5-0002) — check via iDRAC web UI → Storage → Controllers → PERC H710 Mini → Properties. Required before triggering RAID6 OCE; older firmware has known issues.

The donor box's 8× 8 GB DIMMs are independently checked for spare-fitness (photograph one label, confirm DDR3-1600 ECC RDIMM). Doesn't gate the install — just decides whether to keep them as spares or sell/donate.

---

## Why these specific choices

- **E5-2697 v2 over E5-2680 v2:** Cost gap is ~$70-140 for the pair; 4 more cores per socket; official R620 ceiling. Half-stepping doesn't save enough to be worth it.
- **R620-specific heatsink (don't reuse R820):** R820 heatsinks are 2U-height; they physically won't fit in a 1U chassis. Same socket, completely different cooling envelope.
- **Ubiquiti Flex 2.5G 8 (vs Pro Max or Lite line):** Lite line tops out at 1 Gbps — not enough headroom. Pro Max 8 has 2× 10G SFP+ (vs Flex's 1× SFP+ + 1× 10G RJ45) and costs ~$100 more. For Chris's office use case (single uplink to ISP gear, roarbox + a couple of office devices below), the Flex line's two-uplink-types config (one SFP+ for fiber, one RJ45 for copper) is actually more flexible than two SFP+ ports. Confirmed fit + Chris's preference.
- **2.5G switch when roarbox NIC is 1G:** Future-proofs the link. Roarbox auto-negotiates down to 1G with no functional issue. Adds an upgrade path that costs ~$30 in NIC if/when needed.
- **UFW for port control:** Same model as rarbox. Roarbox is autonomous on its dedicated public IP; Chris doesn't need to touch his firewall when you change exposed services.

# Roarbox Upgrade Parts List

**Last updated:** 2026-05-06
**For:** Upgrading roarbox (Dell PowerEdge R620) from 1× E5-2620 v2 (6c/12t) to 2× E5-2697 v2 (24c/48t), and adding an 8-port 2.5G switch in front of Chris's office gear so roarbox port control lives on UFW (same model as rarbox).

---

## Current state of roarbox

| Aspect | Value |
|---|---|
| Chassis | Dell PowerEdge R620 (1U) |
| CPU populated | 1× Intel Xeon E5-2620 v2 in CPU1 socket; CPU2 socket empty |
| RAM | 16 GB on CPU1's bank; CPU2's 12 DIMM slots currently inert |
| Disks | 1.1 TB unused PERC virtual disk (`sda`, ext2, unmounted) + 128 GB boot disk (`sdb`) |
| NIC | 4× onboard 1 Gbps; only `eno1` connected |
| Network | Dedicated public IP; behind Chris's office ISP block |

## Target state after upgrade

| Aspect | Value |
|---|---|
| CPU | 2× Intel Xeon E5-2697 v2 → **24 cores / 48 threads** at 2.7 / 3.5 GHz |
| RAM | Same for now; future-upgrade-ready (24 DIMM slots become accessible) |
| Network | ONT → Noel's 8-port 2.5G Ubiquiti switch → roarbox + Chris's firewall (each with own public IP) |
| Port control | UFW on roarbox; no Chris-side firewall touch needed for new ports |

---

## Server upgrade parts

### Required

| Item | Qty | Notes | Where to buy | Price (~2026-05-06) |
|---|---|---|---|---|
| **Intel Xeon E5-2697 v2** (stepping SR19H) | 2 | **Matched pair, same SKU.** Do NOT mix v1 / v2 or different v2 SKUs in the two sockets. Avoid engineering samples (ES). | eBay search: `E5-2697 v2 SR19H` | $60-120 each → **$120-240 pair** |
| **Dell R620 1U heatsink** | 1 | Dell PN **2DPDC** or **48VTC**. **R620-specific (1U low-profile)** — you cannot reuse R820 heatsinks; they're 2U-height and won't fit. | eBay search: `Dell R620 heatsink 2DPDC` | $10-20 |
| **Thermal paste** | 1 tube | Arctic MX-4 or equivalent. One small tube does ~10 chips. | Amazon, Newegg, Micro Center | $8-12 |

### Conditionally required

| Item | When needed | Dell PN | Cost |
|---|---|---|---|
| Dual-CPU airflow shroud | Only if Chris's inspection finds a single-CPU shroud installed | **F84CR** | $10-15 |

### Optional future add-ons (NOT in this round)

| Item | Purpose | Cost |
|---|---|---|
| Intel I225-V or I226-V 2.5G PCIe NIC | Lets roarbox actually use the 2.5G switch link (current `eno1` is 1 Gbps) | $25-50 |
| DDR3-1600 ECC RDIMMs (16/32 GB sticks) | Populate CPU2's newly-active DIMM slots | Varies by capacity |

---

## Network parts

### Required

| Item | Qty | Notes | Where to buy | Price |
|---|---|---|---|---|
| **Ubiquiti UniFi Switch Pro Max 8** | 1 | 8× 2.5GbE + 2× 10G SFP+, non-PoE, desktop/shelf form factor (no rack ears needed). Verify current SKU on Ubiquiti store before ordering — UniFi lineup shifts. | `https://store.ui.com` → Switching → Pro Max | $300-400 |
| Cat 6 patch cables | 4-6 short | 1-3 ft for switch-to-device runs | Amazon, Monoprice | $1-3 each |

---

## Total cost scenarios

| Scenario | Cost |
|---|---|
| Minimum (2× CPU + heatsink + paste; current shroud is dual-CPU) | **~$140-270** |
| With shroud swap (single-CPU shroud reported) | **~$155-285** |
| Server upgrade + Ubiquiti switch | **~$455-685** |
| Plus optional 2.5G NIC (later phase) | **~$485-735** |

---

## What ships to Chris

1. Sealed matched pair of E5-2697 v2 CPUs (in original packaging if possible — counterfeit risk on eBay is real, prefer sellers with high feedback)
2. Dell R620 heatsink (separately packed)
3. Tube of thermal paste
4. **Conditionally:** dual-CPU airflow shroud (only if his inspection requires it)
5. The install runbook (we'll draft after parts arrive — covers diagonal-screw torque sequence, paste application, post-install BIOS verification)

The Ubiquiti switch ships separately to roarbox's location (or wherever Chris wants to receive it) since it's a network install he'll do in his office, not part of the chassis work.

---

## Pre-purchase verification checklist

Before clicking "buy" on the CPU pair, get answers from Chris on the four inspection questions in `chris-roarbox-inspection-questions.md`:

1. ☐ Airflow shroud has dual or single CPU cutouts
2. ☐ Chassis fan count (should be 6)
3. ☐ CPU2 socket dust cover present + no bent pins
4. ☐ Current BIOS version (must support E5-2697 v2; the running E5-2620 v2 is a strong indicator BIOS is current enough, but verify)

If any of those come back unexpected, we adjust the order or sequence (e.g., BIOS update before chip install).

---

## Why these specific choices

- **E5-2697 v2 over E5-2680 v2:** Cost gap is ~$70-140 for the pair; 4 more cores per socket; official R620 ceiling. Half-stepping doesn't save enough to be worth it.
- **R620-specific heatsink (don't reuse R820):** R820 heatsinks are 2U-height; they physically won't fit in a 1U chassis. Same socket, completely different cooling envelope.
- **Ubiquiti Pro Max 8 (vs Lite line):** Lite line tops out at 1 Gbps. No Lite-tier 8-port 2.5G exists at time of writing.
- **2.5G switch when roarbox NIC is 1G:** Future-proofs the link. Roarbox auto-negotiates down to 1G with no functional issue. Adds an upgrade path that costs ~$30 in NIC if/when needed.
- **UFW for port control:** Same model as rarbox. Roarbox is autonomous on its dedicated public IP; Chris doesn't need to touch his firewall when you change exposed services.

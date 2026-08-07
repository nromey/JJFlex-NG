# rarbox WireGuard NAT lab — agent briefing (2026-08-07)

**Authorization:** Noel green-lit execution 2026-08-07 (queue Decisions
section); rarbox is in approve mode — expect at most one 1Password SSH
approval prompt that Noel clicks. The persistent UFW opening for the WG
port was pre-authorized 2026-08-06.

**MANDATORY first step: read
`C:\Users\nrome\.claude\projects\C--dev-JJFlex-NG\memory\project_rarbox_hardening.md`**
— account model, drop-in-config rule, UFW posture. Non-negotiable
constraints from it: never edit vendor config files (sshd_config,
jail.conf, sudoers, 50unattended-upgrades) — numbered drop-ins only; the
box is `ner@rarbox.macaw-jazz.ts.net` (tailnet-only SSH, key auth via the
1Password agent); NOPASSWD sudo is deliberate — do not "fix" it.

**SSH mechanics (learned this session):** use Windows OpenSSH from
PowerShell — Git Bash's ssh CANNOT reach the 1Password agent pipe and
fails with publickey denied. No BatchMode (it blocks the approval
prompt). For file transfers use `scp` of locally written files — never
long heredocs over ssh.

## Recon results (2026-08-07, read-only pass — trust these, don't re-derive)

- Debian 13.6, kernel 6.12.85+deb13-cloud-amd64, hostname rarbox.
- WAN interface: `eth0`, 178.156.204.128/32 (default via 172.31.1.1,
  DHCP). IPv6 present (2a01:4ff:f0:e09a::1/64).
- WireGuard NOT installed (no wg/wg-quick, no dpkg entry).
- nftables present; existing tables: ip/ip6 filter, inet f2b-table,
  ip/ip6 nat, ip/ip6 mangle (UFW + fail2ban managed — do not modify
  their tables).
- UFW active: default deny incoming, allow outgoing, **deny routed**.
  Allowed: 80/tcp, 443/tcp public; 22/tcp on tailscale0 only.
- `sysctl` not on ner's PATH — use `/usr/sbin/sysctl` or (better) a
  `/etc/sysctl.d/` drop-in. `net.ipv4.ip_forward` state unknown, assume 0.
- Disk 69G free on /; 7.7G RAM. Plenty.

## Design (ratified in the queue's Track/Decisions entries)

Goal: rarbox becomes a WireGuard endpoint whose NAT personality is
switchable, so JJFlex hole-punch + UDP source latch can be regression-
tested against full-cone / port-restricted / symmetric router
temperaments from the shack. Replaces the Tailscale-exit-node approach
(whose port-restricted masquerade can NEVER validate the latch). Doubles
as the first rehearsal for JJ Flexible Connect's relay tier.

1. `apt install wireguard` (+ `wireguard-tools` if split).
2. wg0: server 10.66.0.1/24, ListenPort 51820/udp. Generate server AND
   client keypairs on rarbox. Config at `/etc/wireguard/wg0.conf`
   (root:root 0600), `systemctl enable --now wg-quick@wg0`.
3. Client conf (full tunnel: AllowedIPs 0.0.0.0/0, DNS 1.1.1.1,
   Endpoint 178.156.204.128:51820, client 10.66.0.2/32): assemble on
   rarbox, scp BACK to the Windows machine at
   `C:\Users\nrome\JJFlex-private\natlab\rarbox-natlab-client.conf`.
   **Contains a private key — NEVER commit it to any repo; JJFlex-private
   only.**
4. IP forwarding: `/etc/sysctl.d/99-natlab-forward.conf` with
   `net.ipv4.ip_forward=1`, apply with `/usr/sbin/sysctl --system`.
5. UFW: `ufw allow 51820/udp comment 'natlab wireguard'` (public —
   pre-authorized); `ufw route allow in on wg0 out on eth0` (tunnel
   clients may reach the internet; UFW's default routed policy is deny,
   and established/related return traffic passes via its before-rules).
6. NAT personalities: a dedicated nft table `ip natlab` managed ONLY by
   a switch script at `/usr/local/sbin/natlab` (mode files under
   `/etc/natlab/`, atomic `nft -f` table replace):
   - `natlab port-restricted` — plain `masquerade` on oifname eth0 from
     10.66.0.0/24 (netfilter default = endpoint-dependent filtering; the
     personality that killed the latch test on exit nodes).
   - `natlab symmetric` — `masquerade random` (per-flow source-port
     randomization).
   - `natlab full-cone <port>` — masquerade PLUS prerouting
     `iif eth0 udp dport <port> dnat to 10.66.0.2` and a scoped
     `ufw route allow proto udp to 10.66.0.2 port <port>` so unsolicited
     inbound reaches the client (JJFlex fixed punch port, e.g. 40420).
     `natlab off` removes the table AND any scoped ufw route rule it
     added; `natlab status` prints the active table + relevant ufw rules.
7. Log every command + output verbatim in your report. Rollback plan:
   `natlab off`; `systemctl disable --now wg-quick@wg0`; `ufw delete
   allow 51820/udp`; remove the sysctl drop-in; `apt remove wireguard`.
   All additive — nothing existing is modified.

## Validation (report the recipe; Noel executes the client side later)

Noel-side: install the WireGuard Windows app, import the conf, connect,
`curl ifconfig.me` must return 178.156.204.128. Then per personality:
JJFlex remote connect to the 8600 over the tunnel, trace grep for
`source latch` (the latch fix `625bdbae` has never been validated —
port-restricted mode is the one that exercises it). Include this recipe
verbatim at the end of your report.

## Reporting

Write your full run log + state summary + validation recipe to
`C:\dev\JJFlex-NG\docs\planning\active\natlab-run-report.md` (no
secrets — key material stays out; the client conf path is referenced,
not quoted). Final agent text: 5-line summary.

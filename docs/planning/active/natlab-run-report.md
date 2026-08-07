# rarbox WireGuard NAT lab — run report (2026-08-07)

Executed by the NAT-lab agent of the 2026-08-07 queue-burn ensemble, per
`docs/planning/active/natlab-briefing.md`. All steps additive; no vendor
config files edited; UFW/fail2ban nft tables untouched. Hardening memory
(`project_rarbox_hardening.md`) read before any rarbox command.

No key material appears in this report. The client conf (contains a
private key) lives at
`C:\Users\nrome\JJFlex-private\natlab\rarbox-natlab-client.conf` and
nowhere else — the staged copy on rarbox was deleted after verified
transfer, so the client private key exists ONLY in that file. WireGuard
public keys appear below (they are not secrets and are needed to verify
peer identity).

## Outcome summary

- WireGuard 1.0.20210914-3 installed from Debian trixie main.
- `wg0` up: server 10.66.0.1/24, ListenPort 51820/udp, one peer
  (10.66.0.2/32). `wg-quick@wg0` enabled and active (survives reboot).
- Server public key: `Q9YjibuJJJ+2ql/c9czD14hgq3k5/As5/fpVfug+vFQ=`
- Client public key: `0ocjG2X/qBbrAFe+0veEvDo+3OuZYmYpbppqp3giNw0=`
- `/etc/wireguard/wg0.conf` root:root 0600; `/etc/wireguard` 0700.
- Client conf delivered to
  `C:\Users\nrome\JJFlex-private\natlab\rarbox-natlab-client.conf`
  (structure verified: [Interface]/[Peer], Address 10.66.0.2/32,
  DNS 1.1.1.1, AllowedIPs 0.0.0.0/0, Endpoint 178.156.204.128:51820).
  One addition beyond the briefing spec: `PersistentKeepalive = 25` —
  standard for a client behind home NAT, keeps the tunnel mapping alive;
  affects only the client-to-rarbox WG path, not the NAT personality
  under test.
- IPv4 forwarding: `/etc/sysctl.d/99-natlab-forward.conf` installed and
  applied. (Discrepancy vs recon, see below: forwarding was already 1.)
- UFW: `51820/udp ALLOW IN` (v4+v6, comment "natlab wireguard") and
  `route allow in on wg0 out on eth0` (v4+v6). Default routed policy
  remains deny; no other rules touched.
- NAT personalities: `/usr/local/sbin/natlab` (root 0755) + mode files
  in `/etc/natlab/` (`port-restricted.nft`, `symmetric.nft`,
  `full-cone.nft.in`, all root 0644). All modes exercised successfully,
  including scoped-ufw-rule cleanup on mode switch and on `off`.
- **Final resting state: `natlab port-restricted` active** — the
  personality that exercises the source-latch fix, and required for the
  out-of-box `curl ifconfig.me` validation to work. `nft list tables`
  shows `ip natlab` added alongside the untouched vendor tables.
- Staging files removed from `/tmp` on rarbox.

## Discrepancies vs the briefing

1. **`net.ipv4.ip_forward` was already 1**, not the assumed 0.
   Provenance: `/etc/sysctl.d/99-tailscale.conf` (line 1:
   `net.ipv4.ip_forward = 1`) — left over from the Tailscale
   exit-node experiment. The natlab drop-in was installed anyway, per
   design step 4: it keeps forwarding durable if the Tailscale
   exit-node config is ever retired. Same value, no conflict.
2. No other discrepancies. WireGuard was absent, UFW posture, nft
   table list, WAN interface `eth0`, and IP 178.156.204.128 all matched
   the recon block exactly.

## Design notes (implementation details the briefing left open)

- Atomic table replace uses the standard nft idiom inside one `nft -f`
  transaction: bare `table ip natlab` (create if missing), then
  `delete table ip natlab`, then the full new definition. No window
  where the table is absent.
- Chains: postrouting hook priority srcnat (100), policy accept,
  `oifname "eth0" ip saddr 10.66.0.0/24 masquerade` (port-restricted) /
  `masquerade random` (symmetric). Full-cone adds a prerouting hook
  priority dstnat (-100) with `iifname "eth0" udp dport <port> dnat to
  10.66.0.2`.
- Full-cone is templated (`full-cone.nft.in`, `@PORT@` placeholder,
  sed-substituted to a `/run` tempfile at switch time).
- State tracking: `/etc/natlab/state` holds the active mode (and port
  for full-cone) so the script can delete exactly the scoped ufw route
  rule it added. `off` removes state, table, and scoped rule.
- The script re-execs itself under sudo if run unprivileged, and only
  ever touches the `ip natlab` table plus its own comment-tagged ufw
  route rule.

## Command log (verbatim)

All commands run from PowerShell on Noel's Windows machine using Windows
OpenSSH (`C:\Windows\System32\OpenSSH\ssh.exe` / `scp.exe`) against
`ner@rarbox.macaw-jazz.ts.net`. Provisioning files were written locally
in the session scratchpad, LF-verified, and scp'd — no heredocs over
ssh.

### 1. Connectivity test

```
$ ssh ner@rarbox.macaw-jazz.ts.net "hostname && whoami && date -u"
rarbox
ner
Fri Aug  7 09:51:51 PM UTC 2026
```

### 2. Pre-flight state check

```
$ ssh ner@rarbox.macaw-jazz.ts.net "command -v wg wg-quick; dpkg -l wireguard 2>&1 | tail -2; ls -la /etc/wireguard 2>&1; ls -la /etc/natlab 2>&1; test -f /usr/local/sbin/natlab && echo 'natlab script EXISTS' || echo 'no natlab script'; cat /proc/sys/net/ipv4/ip_forward; sudo ufw status verbose; sudo nft list tables"
dpkg-query: no packages found matching wireguard
ls: cannot access '/etc/wireguard': No such file or directory
ls: cannot access '/etc/natlab': No such file or directory
no natlab script
1
Status: active
Logging: on (low)
Default: deny (incoming), allow (outgoing), deny (routed)
New profiles: skip

To                         Action      From
--                         ------      ----
80/tcp                     ALLOW IN    Anywhere                   # HTTP (nginx)
443/tcp                    ALLOW IN    Anywhere                   # HTTPS (nginx)
22/tcp on tailscale0       ALLOW IN    Anywhere                   # SSH via tailnet only
80/tcp (v6)                ALLOW IN    Anywhere (v6)              # HTTP (nginx)
443/tcp (v6)               ALLOW IN    Anywhere (v6)              # HTTPS (nginx)
22/tcp (v6) on tailscale0  ALLOW IN    Anywhere (v6)              # SSH via tailnet only

table ip filter
table ip6 filter
table inet f2b-table
table ip nat
table ip6 nat
table ip mangle
table ip6 mangle
```

### 3. ip_forward provenance check

```
$ ssh ner@rarbox.macaw-jazz.ts.net "grep -rn 'ip_forward' /etc/sysctl.conf /etc/sysctl.d/ 2>/dev/null; echo '---'; ls /etc/sysctl.d/"
/etc/sysctl.d/99-tailscale.conf:1:net.ipv4.ip_forward = 1
---
99-tailscale.conf
README.sysctl
```

### 4. Install WireGuard

```
$ ssh ner@rarbox.macaw-jazz.ts.net "sudo DEBIAN_FRONTEND=noninteractive apt-get update -qq && sudo DEBIAN_FRONTEND=noninteractive apt-get install -y wireguard 2>&1 | tail -20 && echo '=== VERSIONS ===' && wg --version && which wg wg-quick"
  wireguard-tools
The following NEW packages will be installed:
  wireguard wireguard-tools
0 upgraded, 2 newly installed, 0 to remove and 7 not upgraded.
Need to get 90.7 kB of archives.
After this operation, 341 kB of additional disk space will be used.
Get:1 http://deb.debian.org/debian trixie/main amd64 wireguard-tools amd64 1.0.20210914-3 [84.9 kB]
Get:2 http://deb.debian.org/debian trixie/main amd64 wireguard all 1.0.20210914-3 [5,804 B]
Fetched 90.7 kB in 0s (5,832 kB/s)
Selecting previously unselected package wireguard-tools.
(Reading database ... 71902 files and directories currently installed.)
Preparing to unpack .../wireguard-tools_1.0.20210914-3_amd64.deb ...
Unpacking wireguard-tools (1.0.20210914-3) ...
Selecting previously unselected package wireguard.
Preparing to unpack .../wireguard_1.0.20210914-3_all.deb ...
Unpacking wireguard (1.0.20210914-3) ...
Setting up wireguard-tools (1.0.20210914-3) ...
wg-quick.target is a disabled or a static unit, not starting it.
Setting up wireguard (1.0.20210914-3) ...
Processing triggers for man-db (2.13.1-1) ...
=== VERSIONS ===
wireguard-tools v1.0.20210914 - https://git.zx2c4.com/wireguard-tools/
/usr/bin/wg
/usr/bin/wg-quick
```

(dpkg "Reading database" progress spam collapsed to one line; otherwise
verbatim.)

### 5. scp provisioning files to /tmp

Six files written locally (LF endings verified clean before transfer):
`natlab-setup-wg.sh`, `99-natlab-forward.conf`, `natlab`,
`port-restricted.nft`, `symmetric.nft`, `full-cone.nft.in`.

```
$ scp natlab-setup-wg.sh 99-natlab-forward.conf natlab port-restricted.nft symmetric.nft full-cone.nft.in ner@rarbox.macaw-jazz.ts.net:/tmp/
$ ssh ner@rarbox.macaw-jazz.ts.net "ls -la /tmp/natlab* /tmp/99-natlab-forward.conf /tmp/port-restricted.nft /tmp/symmetric.nft /tmp/full-cone.nft.in"
-rw-rw-r-- 1 ner ner  235 Aug  7 16:53 /tmp/99-natlab-forward.conf
-rw-rw-r-- 1 ner ner  516 Aug  7 16:53 /tmp/full-cone.nft.in
-rw-rw-r-- 1 ner ner 3422 Aug  7 16:53 /tmp/natlab
-rw-rw-r-- 1 ner ner 1559 Aug  7 16:53 /tmp/natlab-setup-wg.sh
-rw-rw-r-- 1 ner ner  372 Aug  7 16:53 /tmp/port-restricted.nft
-rw-rw-r-- 1 ner ner  319 Aug  7 16:53 /tmp/symmetric.nft
```

### 6. Provision wg0 and enable the service

The setup script generates both keypairs on rarbox (umask 077), writes
`/etc/wireguard/wg0.conf` (root:root 0600) with the server private key
+ client public key, stages the client conf at
`/home/ner/rarbox-natlab-client.conf` (ner:ner 0600), refuses to run if
wg0.conf already exists, and prints only public keys.

```
$ ssh ner@rarbox.macaw-jazz.ts.net "sudo bash /tmp/natlab-setup-wg.sh && sudo systemctl enable --now wg-quick@wg0 2>&1 && echo '=== wg show ===' && sudo wg show && echo '=== ip addr wg0 ===' && ip addr show wg0"
server public key: Q9YjibuJJJ+2ql/c9czD14hgq3k5/As5/fpVfug+vFQ=
client public key: 0ocjG2X/qBbrAFe+0veEvDo+3OuZYmYpbppqp3giNw0=
wg0.conf written (root:root 0600); client conf staged at /home/ner/rarbox-natlab-client.conf (delete after transfer)
Created symlink '/etc/systemd/system/multi-user.target.wants/wg-quick@wg0.service' → '/usr/lib/systemd/system/wg-quick@.service'.
=== wg show ===
interface: wg0
  public key: Q9YjibuJJJ+2ql/c9czD14hgq3k5/As5/fpVfug+vFQ=
  private key: (hidden)
  listening port: 51820

peer: 0ocjG2X/qBbrAFe+0veEvDo+3OuZYmYpbppqp3giNw0=
  allowed ips: 10.66.0.2/32
=== ip addr wg0 ===
6: wg0: <POINTOPOINT,NOARP,UP,LOWER_UP> mtu 1420 qdisc noqueue state UNKNOWN group default qlen 1000
    link/none
    inet 10.66.0.1/24 scope global wg0
       valid_lft forever preferred_lft forever
```

### 7. Retrieve client conf, verify, delete staged copy

```
$ scp ner@rarbox.macaw-jazz.ts.net:/home/ner/rarbox-natlab-client.conf C:\Users\nrome\JJFlex-private\natlab\rarbox-natlab-client.conf
size: 323 bytes
has [Interface]: True
has [Peer]: True
has PrivateKey line: True
has Endpoint 178.156.204.128:51820: True
has AllowedIPs 0.0.0.0/0: True
has DNS 1.1.1.1: True
has Address 10.66.0.2/32: True
```

(Local verification done by substring checks — file content never
displayed.)

```
$ ssh ner@rarbox.macaw-jazz.ts.net "rm -f /home/ner/rarbox-natlab-client.conf && ls /home/ner/rarbox-natlab-client.conf 2>&1; ..."
ls: cannot access '/home/ner/rarbox-natlab-client.conf': No such file or directory
```

### 8. sysctl forwarding drop-in (same ssh call as the rm above)

```
$ ... sudo install -o root -g root -m 644 /tmp/99-natlab-forward.conf /etc/sysctl.d/99-natlab-forward.conf && sudo /usr/sbin/sysctl --system 2>&1 | grep -E 'natlab|ip_forward' && echo '=== runtime ===' && cat /proc/sys/net/ipv4/ip_forward
* Applying /etc/sysctl.d/99-natlab-forward.conf ...
net.ipv4.ip_forward = 1
net.ipv4.ip_forward = 1
=== runtime ===
1
```

### 9. UFW rules

```
$ ssh ner@rarbox.macaw-jazz.ts.net "sudo ufw allow 51820/udp comment 'natlab wireguard' && sudo ufw route allow in on wg0 out on eth0 && echo '=== ufw status verbose ===' && sudo ufw status verbose"
Rule added
Rule added (v6)
Rule added
Rule added (v6)
=== ufw status verbose ===
Status: active
Logging: on (low)
Default: deny (incoming), allow (outgoing), deny (routed)
New profiles: skip

To                         Action      From
--                         ------      ----
80/tcp                     ALLOW IN    Anywhere                   # HTTP (nginx)
443/tcp                    ALLOW IN    Anywhere                   # HTTPS (nginx)
22/tcp on tailscale0       ALLOW IN    Anywhere                   # SSH via tailnet only
51820/udp                  ALLOW IN    Anywhere                   # natlab wireguard
80/tcp (v6)                ALLOW IN    Anywhere (v6)              # HTTP (nginx)
443/tcp (v6)               ALLOW IN    Anywhere (v6)              # HTTPS (nginx)
22/tcp (v6) on tailscale0  ALLOW IN    Anywhere (v6)              # SSH via tailnet only
51820/udp (v6)             ALLOW IN    Anywhere (v6)              # natlab wireguard

Anywhere on eth0           ALLOW FWD   Anywhere on wg0
Anywhere (v6) on eth0      ALLOW FWD   Anywhere (v6) on wg0
```

### 10. Install natlab script + mode files

```
$ ssh ner@rarbox.macaw-jazz.ts.net "sudo install -d -o root -g root -m 755 /etc/natlab && sudo install -o root -g root -m 644 /tmp/port-restricted.nft /tmp/symmetric.nft /tmp/full-cone.nft.in /etc/natlab/ && sudo install -o root -g root -m 755 /tmp/natlab /usr/local/sbin/natlab && ls -la /etc/natlab/ /usr/local/sbin/natlab && sudo bash -n /usr/local/sbin/natlab && echo 'bash syntax OK'"
-rwxr-xr-x 1 root root 3422 Aug  7 16:57 /usr/local/sbin/natlab

/etc/natlab/:
total 20
drwxr-xr-x  2 root root 4096 Aug  7 16:57 .
drwxr-xr-x 83 root root 4096 Aug  7 16:57 ..
-rw-r--r--  1 root root  516 Aug  7 16:57 full-cone.nft.in
-rw-r--r--  1 root root  372 Aug  7 16:57 port-restricted.nft
-rw-r--r--  1 root root  319 Aug  7 16:57 symmetric.nft
bash syntax OK
```

### 11. Exercise all personalities

```
$ ssh ner@rarbox.macaw-jazz.ts.net "echo '=== 1. status (fresh) ==='; sudo natlab status; echo '=== 2. port-restricted ==='; sudo natlab port-restricted; sudo nft list table ip natlab; echo '=== 3. symmetric ==='; sudo natlab symmetric; sudo nft list table ip natlab"
=== 1. status (fresh) ===
state: off
--- nft table ip natlab ---
(no natlab table loaded)
--- ufw rules mentioning 10.66.0.2 ---
(none)
=== 2. port-restricted ===
natlab: port-restricted active (masquerade on eth0 for 10.66.0.0/24)
table ip natlab {
	chain postrouting {
		type nat hook postrouting priority srcnat; policy accept;
		oifname "eth0" ip saddr 10.66.0.0/24 masquerade
	}
}
=== 3. symmetric ===
natlab: symmetric active (masquerade on eth0 for 10.66.0.0/24, random source ports)
table ip natlab {
	chain postrouting {
		type nat hook postrouting priority srcnat; policy accept;
		oifname "eth0" ip saddr 10.66.0.0/24 masquerade random
	}
}
```

```
$ ssh ner@rarbox.macaw-jazz.ts.net "echo '=== 4. full-cone 40420 ==='; sudo natlab full-cone 40420; sudo nft list table ip natlab; echo '--- ufw after full-cone ---'; sudo ufw status | grep -F 10.66.0.2; echo '=== 5. switch full-cone -> port-restricted (ufw rule must vanish) ==='; sudo natlab port-restricted; sudo ufw status | grep -F 10.66.0.2 || echo 'ufw scoped rule correctly removed'; echo '=== 6. full-cone 40420 again, then off ==='; sudo natlab full-cone 40420; sudo natlab off; sudo natlab status"
=== 4. full-cone 40420 ===
natlab: full-cone active (masquerade + udp/40420 dnat to 10.66.0.2)
table ip natlab {
	chain prerouting {
		type nat hook prerouting priority dstnat; policy accept;
		iifname "eth0" udp dport 40420 dnat to 10.66.0.2
	}

	chain postrouting {
		type nat hook postrouting priority srcnat; policy accept;
		oifname "eth0" ip saddr 10.66.0.0/24 masquerade
	}
}
--- ufw after full-cone ---
10.66.0.2 40420/udp        ALLOW FWD   Anywhere                   # natlab full-cone
=== 5. switch full-cone -> port-restricted (ufw rule must vanish) ===
natlab: port-restricted active (masquerade on eth0 for 10.66.0.0/24)
ufw scoped rule correctly removed
=== 6. full-cone 40420 again, then off ===
natlab: full-cone active (masquerade + udp/40420 dnat to 10.66.0.2)
natlab: off (natlab table removed, scoped ufw rule removed)
state: off
--- nft table ip natlab ---
(no natlab table loaded)
--- ufw rules mentioning 10.66.0.2 ---
(none)
```

### 12. Final state (default personality set, /tmp cleaned)

```
$ ssh ner@rarbox.macaw-jazz.ts.net "sudo natlab port-restricted; rm -f /tmp/natlab-setup-wg.sh /tmp/99-natlab-forward.conf /tmp/natlab /tmp/port-restricted.nft /tmp/symmetric.nft /tmp/full-cone.nft.in; echo '=== FINAL STATE ==='; ..."
natlab: port-restricted active (masquerade on eth0 for 10.66.0.0/24)
=== FINAL STATE ===
--- wg ---
interface: wg0
  public key: Q9YjibuJJJ+2ql/c9czD14hgq3k5/As5/fpVfug+vFQ=
  private key: (hidden)
  listening port: 51820

peer: 0ocjG2X/qBbrAFe+0veEvDo+3OuZYmYpbppqp3giNw0=
  allowed ips: 10.66.0.2/32
--- service ---
enabled
active
--- forwarding ---
1
--- natlab ---
state: port-restricted
--- nft table ip natlab ---
table ip natlab {
	chain postrouting {
		type nat hook postrouting priority srcnat; policy accept;
		oifname "eth0" ip saddr 10.66.0.0/24 masquerade
	}
}
--- ufw rules mentioning 10.66.0.2 ---
(none)
--- nft tables (natlab added, vendor tables untouched) ---
table ip filter
table ip6 filter
table inet f2b-table
table ip nat
table ip6 nat
table ip mangle
table ip6 mangle
table ip natlab
--- ufw ---
Status: active

To                         Action      From
--                         ------      ----
80/tcp                     ALLOW       Anywhere                   # HTTP (nginx)
443/tcp                    ALLOW       Anywhere                   # HTTPS (nginx)
22/tcp on tailscale0       ALLOW       Anywhere                   # SSH via tailnet only
51820/udp                  ALLOW       Anywhere                   # natlab wireguard
80/tcp (v6)                ALLOW       Anywhere (v6)              # HTTP (nginx)
443/tcp (v6)               ALLOW       Anywhere (v6)              # HTTPS (nginx)
22/tcp (v6) on tailscale0  ALLOW       Anywhere (v6)              # SSH via tailnet only
51820/udp (v6)             ALLOW       Anywhere (v6)              # natlab wireguard

Anywhere on eth0           ALLOW FWD   Anywhere on wg0
Anywhere (v6) on eth0      ALLOW FWD   Anywhere (v6) on wg0

--- wg0.conf perms ---
total 12
drwx------  2 root root 4096 Aug  7 16:54 .
drwxr-xr-x 83 root root 4096 Aug  7 16:57 ..
-rw-------  1 root root  301 Aug  7 16:54 wg0.conf
```

## Using the lab (operator quick reference)

- Switch personality (over ssh to rarbox): `sudo natlab port-restricted`,
  `sudo natlab symmetric`, `sudo natlab full-cone 40420`, `sudo natlab off`,
  `sudo natlab status`. (The script self-elevates, so plain `natlab ...`
  works too.)
- port-restricted is the personality that killed the latch test on
  Tailscale exit nodes — and the one JJFlex's source latch must survive.
- full-cone takes the JJFlex fixed punch port as its argument (e.g.
  40420) and opens unsolicited inbound UDP on that port through to the
  tunnel client.
- `natlab off` leaves the tunnel up but tunnel clients cannot reach the
  internet (no masquerade) — expected, not a bug.

## Rollback plan (all additive — nothing existing was modified)

Run on rarbox, in order:

1. `sudo natlab off` — removes the `ip natlab` nft table and any scoped
   ufw route rule.
2. `sudo systemctl disable --now wg-quick@wg0` — tears down wg0.
3. `sudo ufw delete allow 51820/udp` and
   `sudo ufw route delete allow in on wg0 out on eth0` — removes the two
   natlab UFW rules (each deletes its v4+v6 pair).
4. `sudo rm /etc/sysctl.d/99-natlab-forward.conf && sudo /usr/sbin/sysctl --system`
   — note forwarding stays 1 via the pre-existing 99-tailscale.conf.
5. `sudo apt-get remove -y wireguard wireguard-tools`
6. `sudo rm -rf /etc/wireguard /etc/natlab /usr/local/sbin/natlab`
7. Windows side: delete
   `C:\Users\nrome\JJFlex-private\natlab\rarbox-natlab-client.conf` and
   remove the tunnel from the WireGuard app if imported.

## Validation recipe (verbatim from the briefing — Noel executes)

Noel-side: install the WireGuard Windows app, import the conf, connect,
`curl ifconfig.me` must return 178.156.204.128. Then per personality:
JJFlex remote connect to the 8600 over the tunnel, trace grep for
`source latch` (the latch fix `625bdbae` has never been validated —
port-restricted mode is the one that exercises it). Include this recipe
verbatim at the end of your report.

(Notes for the run: the conf to import is
`C:\Users\nrome\JJFlex-private\natlab\rarbox-natlab-client.conf`;
port-restricted is already active, so the curl check works immediately
after connect. Personality switches happen on rarbox via `sudo natlab
<mode>` over ssh — the tunnel stays up across switches.)

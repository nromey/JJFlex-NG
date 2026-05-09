using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Rung 1.5a — ARP / neighbor cache read. Windows already maintains a list
    /// of recently-seen LAN IP/MAC pairs via the ARP protocol. If the radio has
    /// communicated with anything on the LAN recently (the gateway, the JJ Flex
    /// machine itself, anything), its IP is sitting in the ARP table — free to
    /// read. Strictly cheaper than any active probe: no packets sent.
    ///
    /// Filters the ARP table by the FlexRadio MAC OUI prefix <c>00:1C:2D</c>
    /// (Q1 ACK 2026-05-08, confirmed against Don's 6300 MAC
    /// <c>00:1c:2d:02:07:1b</c>). The IEEE MA-L registry assigns this OUI to
    /// Dell — Flex radios contain Dell-OEM embedded computers — so technically
    /// some Dell devices on the LAN could match. The TCP probe to FlexLib's
    /// command port (4992) bounds false positives: Dell laptops / printers /
    /// monitors do not answer on 4992.
    ///
    /// Per Stream 5 §3.34 / §3.3, virtual adapters (Hyper-V, WSL2, Docker, VPN)
    /// can produce ARP entries that aren't on the physical LAN. Phase 1 ships
    /// without adapter-LUID filtering — the OUI + protocol-probe combo is
    /// already very tight. Adapter filtering is a Phase 2 refinement if false
    /// positives surface in tester traces.
    ///
    /// Per docs/planning/design/discovery-fallback-chain-v3.md §3 Rung 1.5a.
    /// </summary>
    public sealed class ArpNeighborCacheRung : IDiscoveryRung
    {
        private const int FlexLibCommandPort = 4992;
        private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(1500);

        // FlexRadio's MAC OUI prefix per Q1 ACK 2026-05-08 (Don's 6300:
        // 00:1c:2d:02:07:1b). Stored as bytes for direct comparison.
        private static readonly byte[] FlexOuiBytes = { 0x00, 0x1C, 0x2D };

        private readonly TimeSpan _probeTimeout;

        public ArpNeighborCacheRung(TimeSpan? probeTimeout = null)
        {
            _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        }

        public string Name => "ArpNeighborCache";
        public bool Enabled { get; init; } = true;

        public async Task<DiscoveryRungResult> TryAsync(DiscoveryContext ctx, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            List<IPAddress> candidates;
            int totalEntries;
            int afterTypeFilter;
            try
            {
                candidates = ReadArpTable(out totalEntries, out afterTypeFilter);
            }
            catch (Exception ex)
            {
                return Fail(sw, "arp_read_failed", ex.GetType().Name + ": " + ex.Message);
            }

            if (candidates.Count == 0)
            {
                return Fail(sw, "no_oui_match",
                    $"arp_total={totalEntries}, after_type_filter={afterTypeFilter}, after_oui_filter=0");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_probeTimeout);

            var probeTasks = candidates
                .Select(ip => ProbeIpAsync(ip, linked.Token))
                .ToList();

            try
            {
                while (probeTasks.Count > 0)
                {
                    var first = await Task.WhenAny(probeTasks).ConfigureAwait(false);
                    probeTasks.Remove(first);

                    var (ok, winningIp) = await first.ConfigureAwait(false);
                    if (!ok) continue;

                    linked.Cancel();

                    // We don't have model/version metadata for an ARP-discovered
                    // radio — Connect() will populate that after the FlexLib
                    // protocol handshake. CreateFromIp accepts empty strings.
                    var entry = ctx.Cache?.Lookup(ctx.Serial);
                    var radio = Radio.CreateFromIp(
                        entry?.Model ?? "",
                        ctx.Serial ?? "",
                        entry?.Nickname ?? "",
                        winningIp,
                        entry?.Version ?? "");

                    sw.Stop();
                    return new DiscoveryRungResult
                    {
                        Success = true,
                        OutcomeTag = "success",
                        Radio = radio,
                        Elapsed = sw.Elapsed,
                        DiagnosticDetail =
                            $"won at {winningIp} after {sw.ElapsedMilliseconds}ms; " +
                            $"arp_total={totalEntries}, after_type_filter={afterTypeFilter}, after_oui_filter={candidates.Count}"
                    };
                }
            }
            catch (Exception ex)
            {
                return Fail(sw, "exception", ex.GetType().Name + ": " + ex.Message);
            }

            return Fail(sw, "all_probes_failed",
                $"arp_total={totalEntries}, after_type_filter={afterTypeFilter}, after_oui_filter={candidates.Count}, none answered on TCP/{FlexLibCommandPort}");
        }

        private static async Task<(bool Ok, IPAddress Ip)> ProbeIpAsync(IPAddress ip, CancellationToken ct)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ip, FlexLibCommandPort, ct).ConfigureAwait(false);
                return (true, ip);
            }
            catch
            {
                return (false, ip);
            }
        }

        private static DiscoveryRungResult Fail(Stopwatch sw, string tag, string detail = "")
        {
            sw.Stop();
            return new DiscoveryRungResult
            {
                Success = false,
                OutcomeTag = tag,
                Elapsed = sw.Elapsed,
                DiagnosticDetail = detail
            };
        }

        // ────────────────────────────────────────────────────────────
        //  ARP table read via P/Invoke: GetIpNetTable (IPv4-only).
        // ────────────────────────────────────────────────────────────

        private const uint ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint MIB_IPNET_TYPE_INVALID = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_IPNETROW
        {
            public uint dwIndex;
            public uint dwPhysAddrLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] bPhysAddr;
            public uint dwAddr;   // IPv4 in network byte order
            public uint dwType;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetIpNetTable(IntPtr pIpNetTable, ref uint pdwSize, bool bOrder);

        /// <summary>
        /// Reads Windows ARP table, filters by Flex MAC OUI prefix, returns
        /// surviving IPs. <paramref name="totalEntries"/> reports the raw
        /// count; <paramref name="afterTypeFilter"/> reports survivors after
        /// dropping invalid entries — both useful for trace post-mortem.
        /// </summary>
        private static List<IPAddress> ReadArpTable(out int totalEntries, out int afterTypeFilter)
        {
            totalEntries = 0;
            afterTypeFilter = 0;
            var result = new List<IPAddress>();

            uint size = 0;
            int rc = GetIpNetTable(IntPtr.Zero, ref size, false);
            if (rc != ERROR_INSUFFICIENT_BUFFER || size == 0)
            {
                // Either no table or unexpected error. Return empty; caller
                // tags as no_oui_match (which is honest given empty input).
                return result;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = GetIpNetTable(buffer, ref size, false);
                if (rc != 0) return result;

                // Layout: DWORD dwNumEntries, then NumEntries x MIB_IPNETROW.
                int numEntries = Marshal.ReadInt32(buffer);
                totalEntries = numEntries;
                if (numEntries <= 0) return result;

                int rowSize = Marshal.SizeOf<MIB_IPNETROW>();
                IntPtr rowPtr = IntPtr.Add(buffer, sizeof(uint));

                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_IPNETROW>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);

                    if (row.dwType == MIB_IPNET_TYPE_INVALID) continue;
                    afterTypeFilter++;

                    if (row.dwPhysAddrLen != 6) continue; // not Ethernet
                    if (row.bPhysAddr == null || row.bPhysAddr.Length < 3) continue;
                    if (row.bPhysAddr[0] != FlexOuiBytes[0]) continue;
                    if (row.bPhysAddr[1] != FlexOuiBytes[1]) continue;
                    if (row.bPhysAddr[2] != FlexOuiBytes[2]) continue;

                    // dwAddr is IPv4 in network byte order. IPAddress(uint) treats
                    // the value as little-endian on Windows; we want the bytes
                    // as-stored, which IPAddress's byte-array constructor reads
                    // in big-endian order. Bytes from network byte order map
                    // straight into network-order MSB-first interpretation.
                    byte[] addrBytes = BitConverter.GetBytes(row.dwAddr);
                    var ip = new IPAddress(addrBytes);

                    // Skip obvious garbage: 0.0.0.0, 255.255.255.255, multicast.
                    if (IPAddress.Any.Equals(ip)) continue;
                    if (IPAddress.Broadcast.Equals(ip)) continue;
                    if (addrBytes[0] >= 224 && addrBytes[0] <= 239) continue; // multicast 224-239
                    if (IPAddress.IsLoopback(ip)) continue;

                    result.Add(ip);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }
    }
}

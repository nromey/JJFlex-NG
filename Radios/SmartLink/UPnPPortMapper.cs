#nullable enable

using System;
using System.Diagnostics;
using System.Reflection;
using JJTrace;

namespace Radios.SmartLink
{
    /// <summary>Which transport protocol a UPnP mapping covers.</summary>
    public enum UPnPProtocol { Tcp, Udp }

    /// <summary>
    /// Sprint 27 Track B — thin wrapper around the Windows UPnPNAT COM object
    /// (<c>HNetCfg.NATUPnP</c> ProgID / <c>IUPnPNAT</c>) for Tier 2 automatic
    /// router port mapping. Returns <c>bool</c> / nullable values for every
    /// operation so callers don't have to handle <see cref="System.Runtime.InteropServices.COMException"/>
    /// or null-collection edge cases — every failure funnels into a traced
    /// <c>false</c> or <c>null</c>.
    ///
    /// <para>
    /// <b>Library choice (Q27.1, resolved 2026-04-20):</b> native Windows COM
    /// instead of Open.NAT or Mono.Nat. Zero NuGet dependency, API stable
    /// since Windows XP, aligns with JJFlex's Windows-only posture.
    /// </para>
    ///
    /// <para>
    /// <b>Threading:</b> COM calls block synchronously on whatever thread
    /// invokes them. Callers on the UI thread should wrap in <c>Task.Run</c>
    /// or call from a background context (e.g. FlexBase.Connect's post-
    /// connect hook already runs off-UI).
    /// </para>
    ///
    /// <para>
    /// <b>Testability:</b> the class delegates to an internal
    /// <see cref="IUPnPBackend"/> so unit tests can inject a fake backend
    /// without invoking COM. Production wiring uses
    /// <see cref="ComUPnPBackend"/>.
    /// </para>
    /// </summary>
    public sealed class UPnPPortMapper
    {
        private readonly IUPnPBackend _backend;

        /// <summary>Production ctor — wires up the real COM backend.</summary>
        public UPnPPortMapper() : this(new ComUPnPBackend()) { }

        /// <summary>Test ctor — accepts an arbitrary backend.</summary>
        internal UPnPPortMapper(IUPnPBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        /// <summary>
        /// True when the system has a UPnP NAT service registered AND the
        /// router advertises a usable <c>StaticPortMappingCollection</c>.
        /// False when: ProgID not registered, Windows UPnP service disabled,
        /// router doesn't support UPnP, router has UPnP disabled.
        /// </summary>
        public bool IsAvailable => SafeCall(() => _backend.IsAvailable, false, "IsAvailable");

        /// <summary>
        /// Ask the router to forward <paramref name="externalPort"/> on
        /// <paramref name="protocol"/> to <paramref name="internalClient"/>:<paramref name="internalPort"/>.
        /// Returns false on invalid input or any COM failure. Check
        /// <see cref="IsAvailable"/> first when the caller wants to
        /// distinguish "router said no" from "router isn't speaking UPnP
        /// at all".
        /// </summary>
        public bool TryAddMapping(int externalPort, UPnPProtocol protocol, int internalPort, string internalClient, string description)
        {
            if (externalPort < 1 || externalPort > 65535) return false;
            if (internalPort < 1 || internalPort > 65535) return false;
            if (string.IsNullOrWhiteSpace(internalClient)) return false;
            string proto = ProtocolToComString(protocol);
            return SafeCall(() => _backend.TryAddMapping(externalPort, proto, internalPort, internalClient, description ?? string.Empty), false, "TryAddMapping");
        }

        /// <summary>
        /// Release a previously-added mapping. Safe to call on mappings that
        /// no longer exist — returns false but does not throw.
        /// </summary>
        public bool TryRemoveMapping(int externalPort, UPnPProtocol protocol)
        {
            if (externalPort < 1 || externalPort > 65535) return false;
            string proto = ProtocolToComString(protocol);
            return SafeCall(() => _backend.TryRemoveMapping(externalPort, proto), false, "TryRemoveMapping");
        }

        /// <summary>
        /// Best-effort fetch of the router's public IP address. Returns null
        /// when unavailable. Not used for probing connectivity (Track C's
        /// NetworkTest covers that); provided for diagnostic copy/save.
        /// </summary>
        public string? TryGetExternalIpAddress() =>
            SafeCall(() => _backend.TryGetExternalIpAddress(), null, "TryGetExternalIpAddress");

        internal static string ProtocolToComString(UPnPProtocol p) => p switch
        {
            UPnPProtocol.Tcp => "TCP",
            UPnPProtocol.Udp => "UDP",
            _ => throw new ArgumentOutOfRangeException(nameof(p), p, "Unknown UPnPProtocol"),
        };

        private static T SafeCall<T>(Func<T> fn, T fallback, string operation)
        {
            try { return fn(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"UPnPPortMapper.{operation}: {ex.GetType().Name}: {ex.Message}", TraceLevel.Warning);
                return fallback;
            }
        }
    }

    /// <summary>
    /// Internal seam over the UPnPNAT COM surface. Abstracted so tests can
    /// inject a fake without invoking COM. Production: see
    /// <see cref="ComUPnPBackend"/>.
    /// </summary>
    internal interface IUPnPBackend
    {
        bool IsAvailable { get; }
        bool TryAddMapping(int externalPort, string protocol, int internalPort, string internalClient, string description);
        bool TryRemoveMapping(int externalPort, string protocol);
        string? TryGetExternalIpAddress();
    }

    /// <summary>
    /// Real COM-backed <see cref="IUPnPBackend"/>. Uses reflection rather
    /// than <c>dynamic</c> so we don't need a Microsoft.CSharp reference
    /// and the call sites are explicit about which COM members we touch.
    /// </summary>
    internal sealed class ComUPnPBackend : IUPnPBackend
    {
        private const string UPnPProgId = "HNetCfg.NATUPnP";

        public bool IsAvailable
        {
            get
            {
                var (_, mappings) = TryGetMappingsCollection();
                return mappings != null;
            }
        }

        public bool TryAddMapping(int externalPort, string protocol, int internalPort, string internalClient, string description)
        {
            var (_, mappings) = TryGetMappingsCollection();
            if (mappings == null) return false;

            InvokeMember(mappings, "Add", BindingFlags.InvokeMethod, new object[]
            {
                externalPort, protocol, internalPort, internalClient, true, description
            });
            Tracing.TraceLine($"ComUPnPBackend: mapped external={externalPort}/{protocol} -> {internalClient}:{internalPort} description='{description}'", TraceLevel.Info);
            return true;
        }

        public bool TryRemoveMapping(int externalPort, string protocol)
        {
            var (_, mappings) = TryGetMappingsCollection();
            if (mappings == null) return false;

            InvokeMember(mappings, "Remove", BindingFlags.InvokeMethod, new object[] { externalPort, protocol });
            Tracing.TraceLine($"ComUPnPBackend: removed mapping external={externalPort}/{protocol}", TraceLevel.Info);
            return true;
        }

        public string? TryGetExternalIpAddress()
        {
            // External IP discovery lives on IUPnPDevice rather than IUPnPNAT;
            // enumerating the gateway device is more COM work than Sprint 27
            // Track B needs. Track C's NetworkTest path surfaces external IP
            // via WanServer.SslClientPublicIp already. Leaving this null is
            // a deliberate scope choice, not a bug.
            return null;
        }

        private static (object? Upnp, object? Mappings) TryGetMappingsCollection()
        {
            var type = Type.GetTypeFromProgID(UPnPProgId);
            if (type == null)
            {
                Tracing.TraceLine($"ComUPnPBackend: ProgID '{UPnPProgId}' not registered — UPnP unavailable on this system", TraceLevel.Info);
                return (null, null);
            }
            object? upnp;
            try
            {
                upnp = Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ComUPnPBackend: Activator.CreateInstance threw {ex.GetType().Name}: {ex.Message}", TraceLevel.Info);
                return (null, null);
            }
            if (upnp == null) return (null, null);

            object? mappings;
            try
            {
                mappings = InvokeMember(upnp, "StaticPortMappingCollection", BindingFlags.GetProperty, null);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ComUPnPBackend: StaticPortMappingCollection getter threw {ex.GetType().Name}: {ex.Message}", TraceLevel.Info);
                return (upnp, null);
            }
            if (mappings == null)
            {
                Tracing.TraceLine("ComUPnPBackend: StaticPortMappingCollection is null — UPnP disabled at router or Windows service not running", TraceLevel.Info);
            }
            return (upnp, mappings);
        }

        private static object? InvokeMember(object target, string name, BindingFlags flags, object?[]? args)
        {
            var type = target.GetType();
            return type.InvokeMember(name, flags, binder: null, target, args);
        }
    }
}

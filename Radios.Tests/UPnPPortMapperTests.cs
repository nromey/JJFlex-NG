#nullable enable

using System;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 27 Track B / Phase B.1. Verifies <see cref="UPnPPortMapper"/>
    /// input validation + exception swallowing + delegation to the backend.
    /// Real UPnP traffic against an actual router cannot be unit-tested
    /// deterministically; that path is covered in B.4 smoke testing.
    /// </summary>
    public class UPnPPortMapperTests
    {
        /// <summary>Controllable IUPnPBackend for unit tests.</summary>
        private sealed class FakeBackend : IUPnPBackend
        {
            public bool IsAvailable { get; set; }
            public bool AddReturns { get; set; } = true;
            public bool RemoveReturns { get; set; } = true;
            public string? ExternalIp { get; set; }
            public Exception? ThrowOnCall { get; set; }

            public int AddCallCount { get; private set; }
            public int RemoveCallCount { get; private set; }
            public int LastExternalPort { get; private set; }
            public string? LastProtocol { get; private set; }
            public int LastInternalPort { get; private set; }
            public string? LastInternalClient { get; private set; }
            public string? LastDescription { get; private set; }

            public bool TryAddMapping(int externalPort, string protocol, int internalPort, string internalClient, string description)
            {
                if (ThrowOnCall != null) throw ThrowOnCall;
                AddCallCount++;
                LastExternalPort = externalPort;
                LastProtocol = protocol;
                LastInternalPort = internalPort;
                LastInternalClient = internalClient;
                LastDescription = description;
                return AddReturns;
            }

            public bool TryRemoveMapping(int externalPort, string protocol)
            {
                if (ThrowOnCall != null) throw ThrowOnCall;
                RemoveCallCount++;
                LastExternalPort = externalPort;
                LastProtocol = protocol;
                return RemoveReturns;
            }

            public string? TryGetExternalIpAddress()
            {
                if (ThrowOnCall != null) throw ThrowOnCall;
                return ExternalIp;
            }
        }

        [Fact]
        public void IsAvailable_DelegatesToBackend()
        {
            var fake = new FakeBackend { IsAvailable = true };
            var mapper = new UPnPPortMapper(fake);
            Assert.True(mapper.IsAvailable);

            fake.IsAvailable = false;
            Assert.False(mapper.IsAvailable);
        }

        [Fact]
        public void IsAvailable_ReturnsFalseWhenBackendThrows()
        {
            var fake = new FakeBackend { ThrowOnCall = new InvalidOperationException("nope") };
            // ThrowOnCall applies to Try* methods, not IsAvailable in this fake — write a simpler
            // test: backend that throws from IsAvailable itself.
            var fake2 = new ThrowingIsAvailableBackend();
            var mapper = new UPnPPortMapper(fake2);
            Assert.False(mapper.IsAvailable);
        }

        private sealed class ThrowingIsAvailableBackend : IUPnPBackend
        {
            public bool IsAvailable => throw new InvalidOperationException("boom");
            public bool TryAddMapping(int e, string p, int i, string c, string d) => true;
            public bool TryRemoveMapping(int e, string p) => true;
            public string? TryGetExternalIpAddress() => null;
        }

        [Fact]
        public void TryAddMapping_ValidInputsDelegateWithCorrectProtocolString()
        {
            var fake = new FakeBackend();
            var mapper = new UPnPPortMapper(fake);

            Assert.True(mapper.TryAddMapping(5000, UPnPProtocol.Tcp, 5000, "192.168.1.10", "JJFlex radio"));
            Assert.Equal(1, fake.AddCallCount);
            Assert.Equal(5000, fake.LastExternalPort);
            Assert.Equal("TCP", fake.LastProtocol);
            Assert.Equal("192.168.1.10", fake.LastInternalClient);
            Assert.Equal("JJFlex radio", fake.LastDescription);

            Assert.True(mapper.TryAddMapping(5001, UPnPProtocol.Udp, 5001, "192.168.1.10", "JJFlex UDP"));
            Assert.Equal("UDP", fake.LastProtocol);
        }

        [Fact]
        public void TryAddMapping_RejectsPortBelowRange()
        {
            var fake = new FakeBackend();
            var mapper = new UPnPPortMapper(fake);
            Assert.False(mapper.TryAddMapping(0, UPnPProtocol.Tcp, 5000, "192.168.1.10", "x"));
            Assert.False(mapper.TryAddMapping(-1, UPnPProtocol.Tcp, 5000, "192.168.1.10", "x"));
            Assert.Equal(0, fake.AddCallCount); // backend never touched
        }

        [Fact]
        public void TryAddMapping_RejectsPortAboveRange()
        {
            var fake = new FakeBackend();
            var mapper = new UPnPPortMapper(fake);
            Assert.False(mapper.TryAddMapping(65536, UPnPProtocol.Tcp, 5000, "192.168.1.10", "x"));
            Assert.False(mapper.TryAddMapping(70000, UPnPProtocol.Tcp, 5000, "192.168.1.10", "x"));
            Assert.Equal(0, fake.AddCallCount);
        }

        [Fact]
        public void TryAddMapping_RejectsEmptyInternalClient()
        {
            var fake = new FakeBackend();
            var mapper = new UPnPPortMapper(fake);
            Assert.False(mapper.TryAddMapping(5000, UPnPProtocol.Tcp, 5000, "", "x"));
            Assert.False(mapper.TryAddMapping(5000, UPnPProtocol.Tcp, 5000, "   ", "x"));
            Assert.Equal(0, fake.AddCallCount);
        }

        [Fact]
        public void TryAddMapping_ReturnsFalseWhenBackendReturnsFalse()
        {
            var fake = new FakeBackend { AddReturns = false };
            var mapper = new UPnPPortMapper(fake);
            Assert.False(mapper.TryAddMapping(5000, UPnPProtocol.Tcp, 5000, "192.168.1.10", "x"));
        }

        [Fact]
        public void TryAddMapping_ReturnsFalseWhenBackendThrows()
        {
            var fake = new FakeBackend { ThrowOnCall = new InvalidOperationException("router refused") };
            var mapper = new UPnPPortMapper(fake);
            // Must not throw out of the wrapper; caller just sees false.
            Assert.False(mapper.TryAddMapping(5000, UPnPProtocol.Tcp, 5000, "192.168.1.10", "x"));
        }

        [Fact]
        public void TryRemoveMapping_ValidInputsDelegate()
        {
            var fake = new FakeBackend();
            var mapper = new UPnPPortMapper(fake);
            Assert.True(mapper.TryRemoveMapping(5000, UPnPProtocol.Tcp));
            Assert.Equal(1, fake.RemoveCallCount);
            Assert.Equal("TCP", fake.LastProtocol);

            Assert.True(mapper.TryRemoveMapping(5001, UPnPProtocol.Udp));
            Assert.Equal("UDP", fake.LastProtocol);
        }

        [Fact]
        public void TryRemoveMapping_RejectsInvalidPort()
        {
            var fake = new FakeBackend();
            var mapper = new UPnPPortMapper(fake);
            Assert.False(mapper.TryRemoveMapping(0, UPnPProtocol.Tcp));
            Assert.False(mapper.TryRemoveMapping(65536, UPnPProtocol.Tcp));
            Assert.Equal(0, fake.RemoveCallCount);
        }

        [Fact]
        public void TryRemoveMapping_ReturnsFalseWhenBackendThrows()
        {
            var fake = new FakeBackend { ThrowOnCall = new InvalidOperationException("no such mapping") };
            var mapper = new UPnPPortMapper(fake);
            Assert.False(mapper.TryRemoveMapping(5000, UPnPProtocol.Tcp));
        }

        [Fact]
        public void TryGetExternalIpAddress_DelegatesToBackend()
        {
            var fake = new FakeBackend { ExternalIp = "203.0.113.7" };
            var mapper = new UPnPPortMapper(fake);
            Assert.Equal("203.0.113.7", mapper.TryGetExternalIpAddress());
        }

        [Fact]
        public void TryGetExternalIpAddress_ReturnsNullWhenBackendThrows()
        {
            var fake = new FakeBackend { ThrowOnCall = new InvalidOperationException("unavailable") };
            var mapper = new UPnPPortMapper(fake);
            Assert.Null(mapper.TryGetExternalIpAddress());
        }

        [Fact]
        public void Ctor_NullBackend_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new UPnPPortMapper(null!));
        }

        [Fact]
        public void ProtocolToComString_ProducesCaseExpectedByWindowsUPnP()
        {
            // Windows UPnPNAT expects uppercase "TCP" / "UDP" — not "Tcp" or "tcp".
            // Regression guard: if someone "cleans up" ProtocolToComString to ToString()
            // this test fails immediately.
            Assert.Equal("TCP", UPnPPortMapper.ProtocolToComString(UPnPProtocol.Tcp));
            Assert.Equal("UDP", UPnPPortMapper.ProtocolToComString(UPnPProtocol.Udp));
        }
    }
}

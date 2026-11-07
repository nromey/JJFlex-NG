#nullable enable

using System.Collections;
using System.Reflection;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// What a rig's presence intake has CONSUMED, read from the record the
    /// intake itself keeps: the list each account's rows were last taken from
    /// (<c>FlexBase._wanRowsFrom</c>), written for every list it consumes,
    /// pushed or replayed, empty or not, and never for one it refuses.
    /// </summary>
    /// <remarks>
    /// Until the 2026-11-06 integration pass these tests read the rig-wide
    /// connect latch (<c>wanListReceived</c>) for the same question. No product
    /// code read that latch after #713's per-call latches, so it was removed;
    /// a test that pinned it was pinning a mechanism the product no longer
    /// used.
    /// </remarks>
    internal static class IntakeTook
    {
        private static readonly FieldInfo? RowsFromField =
            typeof(FlexBase).GetField("_wanRowsFrom", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? IntakeLockField =
            typeof(FlexBase).GetField("_wanIntakeLock", BindingFlags.NonPublic | BindingFlags.Static);

        private static (IDictionary map, object gate) Of(FlexBase rig)
        {
            Assert.True(RowsFromField != null && IntakeLockField != null,
                "FlexBase._wanRowsFrom or _wanIntakeLock is not where this test reads it, so every " +
                "assertion about what the intake consumed would be vacuous.");
            return ((IDictionary)RowsFromField!.GetValue(rig)!, IntakeLockField!.GetValue(null)!);
        }

        /// <summary>The intake has consumed a list for any account.</summary>
        public static bool AnyList(FlexBase rig)
        {
            var (map, gate) = Of(rig);
            lock (gate) return map.Count > 0;
        }

        /// <summary>The intake has consumed a list for <paramref name="account"/>.</summary>
        public static bool ListFor(FlexBase rig, string account)
        {
            var (map, gate) = Of(rig);
            lock (gate) return map.Contains(account);
        }

        /// <summary>Forget what was consumed, so the next assertion is about the next list.</summary>
        public static void Forget(FlexBase rig)
        {
            var (map, gate) = Of(rig);
            lock (gate) map.Clear();
        }
    }
}

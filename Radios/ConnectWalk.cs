using System;
using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// One connect attempt's itinerary: the ordered paths it may travel, and
    /// where in that list it currently stands.
    ///
    /// <para><b>A leg is not finished when the session connects. It is
    /// finished when the radio OPENS.</b> That sentence is the whole reason
    /// this type exists as an object rather than a <c>For</c> loop over a
    /// list. Task #284, reproduced twice on 2026-08-26: the walk
    /// <c>[SmartLink, Local]</c> never reached its Local leg, because the
    /// SmartLink leg SUCCEEDED at the session layer — a SmartLink session in
    /// 445 ms, the radio found, a remote connect sent — and the loop exited on
    /// that success. The open failed fifty-six seconds later, by which point
    /// the loop had returned, its list was a local variable in a finished
    /// scope, and the alternative path had never been tried. A fallback that
    /// is never reached is not a fallback.</para>
    ///
    /// <para>So the itinerary outlives the loop. It is plain data — a serial,
    /// a bandwidth flag, an ordered list and an index — which is exactly what
    /// makes it survivable: nothing in here is a radio, a socket, a session or
    /// a discovery task, so a failed open cannot take it down with it, and the
    /// caller that learns about the failure still holds the list of things
    /// left to try.</para>
    ///
    /// <para>Pure by construction. It decides nothing about availability and
    /// talks to nothing — the caller runs each leg and reports back. That
    /// keeps the ordering testable without a radio.</para>
    /// </summary>
    public sealed class ConnectWalk
    {
        private readonly List<ConnectPathKind> _legs;
        private readonly System.Collections.ObjectModel.ReadOnlyCollection<ConnectPathKind> _legsView;
        private int _index;

        private ConnectWalk(string serial, bool lowBW, bool forced, List<ConnectPathKind> legs)
        {
            Serial = serial ?? "";
            LowBW = lowBW;
            Forced = forced;
            _legs = legs;
            _legsView = legs.AsReadOnly();
            _index = 0;
        }

        /// <summary>The radio this walk is for.</summary>
        public string Serial { get; }

        /// <summary>Low-bandwidth connect, carried so a resumed leg asks for
        /// the same thing the first leg did.</summary>
        public bool LowBW { get; }

        /// <summary>
        /// True when the operator forced this path from the context menu.
        ///
        /// <para>A forced walk has exactly one leg BY CONSTRUCTION, not by
        /// convention — force-remote is the hole-punch test instrument, and a
        /// fallback that succeeded over the other path would invalidate the
        /// test while reporting success.</para>
        /// </summary>
        public bool Forced { get; }

        /// <summary>The itinerary, in order. Read-only: the paths are decided
        /// when the walk is built and a caller mid-walk must not be able to
        /// add one.</summary>
        public IReadOnlyList<ConnectPathKind> Legs => _legsView;

        /// <summary>Which leg is being attempted now, zero-based.</summary>
        public int LegIndex => _index;

        /// <summary>The path of the leg being attempted now.</summary>
        public ConnectPathKind Current => _legs[_index];

        /// <summary>True on the first leg — the one the selector chose.</summary>
        public bool IsFirstLeg => _index == 0;

        /// <summary>True when nothing follows this leg.</summary>
        public bool IsLastLeg => _index >= _legs.Count - 1;

        /// <summary>True when a path remains to be tried after this one.</summary>
        public bool HasNextLeg => _index < _legs.Count - 1;

        /// <summary>The path that would be tried next, or null at the end.</summary>
        public ConnectPathKind? PeekNext =>
            HasNextLeg ? _legs[_index + 1] : (ConnectPathKind?)null;

        /// <summary>
        /// Move to the next leg. False when the itinerary is exhausted, in
        /// which case the position does not change — an exhausted walk keeps
        /// naming the leg that failed last, so a caller can still report which
        /// path it gave up on.
        /// </summary>
        public bool MoveNext()
        {
            if (!HasNextLeg) return false;
            _index++;
            return true;
        }

        /// <summary>
        /// Build the itinerary the selector decided on: the chosen path first,
        /// then the chain entries that remained after it.
        ///
        /// <para>A forced walk drops the fallbacks. Duplicates are removed
        /// rather than trusted away — the chosen path is already leg zero, and
        /// a chain that named it again would otherwise make the walk retry the
        /// path that just failed.</para>
        /// </summary>
        public static ConnectWalk Build(
            string serial,
            bool lowBW,
            ConnectPathKind chosen,
            IEnumerable<ConnectPathKind>? fallbacks,
            bool forced)
        {
            var legs = new List<ConnectPathKind> { chosen };
            if (!forced && fallbacks != null)
            {
                foreach (var path in fallbacks)
                {
                    if (!legs.Contains(path)) legs.Add(path);
                }
            }
            return new ConnectWalk(serial, lowBW, forced, legs);
        }

        /// <summary>The itinerary as a trace-friendly string, e.g.
        /// <c>SmartLink,Local</c>.</summary>
        public string Describe() => string.Join(",", _legs);
    }
}

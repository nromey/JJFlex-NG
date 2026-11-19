using System;
using System.Collections.Generic;
using System.Linq;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// The shared receive delay (#873, Sprint 49 Track P). While computer noise
    /// reduction is on, a receive slot holds its output at one delay for every
    /// engine in a set, padding a faster engine with plain delay after it, so a
    /// switch between two engines in the set moves no time (Noel 2026-11-04
    /// 19:52 to 19:58; X5, 2026-11-19: "both", a shared delay and later the lag
    /// shifter, measured first).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which engines set it is NOT ruled.</b> Noel ruled only that an outlier
    /// must not (19:58: "If the slowest model is something like Nvidia ... I
    /// don't want it to throw it off"). So the set is an INPUT to one function
    /// here, <see cref="SharedSamples"/>, and <see cref="SetFor"/> supplies the
    /// assumption this round builds on: the receive favourites of the family,
    /// the engines the operator chose to keep at hand. Change that one function
    /// when he rules.
    /// </para>
    /// <para>
    /// <b>Figures.</b> An engine's figure is the one its running bank reports
    /// when it runs (measured: what the engine itself says, or what the helper
    /// answered when its channel opened), then the last such figure the noise
    /// controller saw for that engine and model, then the catalog's expected one.
    /// Expected figures for the in-process engines are exact (fixed by each
    /// build); a helper engine's is the catalog's rounded milliseconds, so it can
    /// sit up to <see cref="RoundingSamples"/> either side of the truth until
    /// the helper has opened that model once (measured 2026-11-19: NR2 reports
    /// 5,024 against 105 times 48, 5,040). The slot tolerates exactly that much
    /// at a switch (see <see cref="PcDenoiseSlot"/>), never more.
    /// </para>
    /// </remarks>
    public static class ReceiveSharedDelay
    {
        /// <summary>One engine in a model (null: whatever model the engine is set to).</summary>
        public readonly record struct Member(string EngineId, string? Model);

        /// <summary>Half a millisecond at 48 kHz: how far a figure rounded to whole milliseconds can sit from the truth.</summary>
        public const int RoundingSamples = 24;

        /// <summary>
        /// The catalog's expected figure in samples, before the engine has run:
        /// exact for RNNoise (by model), DeepFilterNet3 and NR4; the helper
        /// engines' rounded milliseconds times 48; null for anything else
        /// (Captured noise, whose delay depends on whether a profile is loaded,
        /// and NVIDIA NR, which this build cannot run).
        /// </summary>
        public static int? ExpectedSamples(string engineId, string? model) => engineId switch
        {
            "rnnoise" => RnnoiseModels.LatencySamples(model ?? RnnoiseModels.Default),
            "deepfilternet3" => DeepFilterNet3Engine.LatencyAtFrame(480),
            "nr4" => SpecbleachEngine.LiveLatencySamples,
            _ => DenoiseEngineCatalog.ExpectedHelperDelayMs(engineId, model) is int ms ? ms * 48 : null,
        };

        /// <summary>
        /// THE ASSUMPTION, until Noel rules which engines set the shared delay:
        /// the receive favourites that are computer engines of this family, each
        /// in its favourite's model. A favourite saved with no model keeps no
        /// model here, so it matches the engine in any model, as favourites do;
        /// its figure is read for the model the engine is set to now. Distinct,
        /// in favourites order.
        /// </summary>
        public static IReadOnlyList<Member> SetFor(DenoiseEngineCatalog catalog, IEnumerable<NoiseFavourite> favourites,
            DenoiseFamily family)
        {
            var set = new List<Member>();
            foreach (var f in favourites)
            {
                var e = catalog.Find(f.EngineId);
                if (e == null || e.Side != DenoiseSide.Pc || e.Family != family || e.Factory == null || !e.Serves(DenoiseDirection.Receive)) continue;
                var member = new Member(f.EngineId, string.IsNullOrEmpty(f.Model) ? null : f.Model);
                if (!set.Contains(member)) set.Add(member);
            }
            return set;
        }

        /// <summary>
        /// The shared delay for a set: the largest figure among its members, in
        /// samples; 0 for an empty set or one whose figures are all unknown. A
        /// member with no figure yet does not count (it is measured once it runs).
        /// </summary>
        public static int SharedSamples(IEnumerable<Member> set, Func<Member, int?> figure)
        {
            int shared = 0;
            foreach (var m in set)
                if (figure(m) is int samples && samples > shared) shared = samples;
            return shared;
        }

        /// <summary>Samples to whole milliseconds at 48 kHz, as every delay readout rounds them.</summary>
        public static int Milliseconds(int samples) => (int)Math.Round(samples / 48.0);
    }

    /// <summary>
    /// What a receive slot is told about its family's shared delay, published by
    /// reference from the control side and read by the audio thread at a switch
    /// or when the slot turns on: the set's members (an array, so asking whether
    /// an engine belongs allocates nothing) and the shared figure.
    /// </summary>
    public sealed class SharedDelayPlan
    {
        public static readonly SharedDelayPlan None = new(0, Array.Empty<ReceiveSharedDelay.Member>());

        public SharedDelayPlan(int samples, IReadOnlyList<ReceiveSharedDelay.Member> members)
        {
            Samples = Math.Max(0, samples);
            _members = members.ToArray();
        }

        private readonly ReceiveSharedDelay.Member[] _members;

        /// <summary>The shared delay, in samples per channel.</summary>
        public int Samples { get; }

        public IReadOnlyList<ReceiveSharedDelay.Member> Members => _members;

        /// <summary>
        /// Whether an engine, built in this model, is in the set. A member saved
        /// with no model matches any model. On the audio thread: a loop and
        /// ordinal compares, nothing else.
        /// </summary>
        public bool Contains(string engineId, string? model)
        {
            for (int i = 0; i < _members.Length; i++)
            {
                var m = _members[i];
                if (!string.Equals(m.EngineId, engineId, StringComparison.Ordinal)) continue;
                if (m.Model == null || string.Equals(m.Model, model ?? "", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}

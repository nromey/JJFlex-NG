using System;
using System.Collections.Generic;
using System.IO;
using Radios.Fixer.Evidence;

namespace Radios.Tests
{
    /// <summary>A throwaway folder that cleans up after itself. Evidence-layer
    /// tests write real files; they must never write anywhere shared.</summary>
    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; }

        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "jjflex-fixer-evidence-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    /// <summary>Hand-built records for tests that need one without running an
    /// engine.</summary>
    internal static class EvidenceRecords
    {
        public static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A two-stage record ("fill" 0, "boil" 1) with no results.</summary>
        public static FixerRunRecord TwoStages(string runId = "AAA-222", DateTime? started = null)
        {
            return new FixerRunRecord
            {
                RunId = runId,
                StageSetId = "kettle",
                StageSetName = "Kettle",
                StartedUtc = started ?? T0,
                LastRecordedUtc = started ?? T0,
                Stages =
                {
                    new RecordedStageInfo { Id = "fill", Number = 0, Title = "Fill" },
                    new RecordedStageInfo { Id = "boil", Number = 1, Title = "Boil", Transmits = true },
                },
            };
        }

        public static RecordedStage Ran(string stageId, int sequence,
                                        params RecordedSetting[] settings)
        {
            var r = new RecordedStage
            {
                StageId = stageId,
                AtUtc = T0.AddMinutes(sequence),
                Sequence = sequence,
                Status = "Ran",
                Answer = "Yes.",
            };
            r.Settings.AddRange(settings);
            return r;
        }

        public static RecordedSetting Setting(string key, string name, string value)
            => new RecordedSetting { Key = key, Name = name, Value = value };
    }

    /// <summary>A probe whose value the test can change mid-flight — the
    /// "operator altered a setting" lever.</summary>
    internal sealed class FakeSetting
    {
        public string Value;
        public FakeSetting(string value) { Value = value; }
        public FixerSettingProbe Probe(string key, string name)
            => new FixerSettingProbe(key, name, () => Value);
    }
}

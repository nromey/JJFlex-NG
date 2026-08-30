using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The reflected-power cut setting's persistence contract (#224): ON by
    /// default, and the operator's choice — either way — survives a restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These exist because the cut became defeatable on 2026-08-30 and the
    /// sprint's own lesson is that behaviour without assertions goes green
    /// while being wrong. Three things are pinned: the default (ruled ON by
    /// Noel 2026-08-26 — the errors are asymmetric, and a blind operator has
    /// no SWR meter to glance at), the round trip (an operator who switched
    /// the cut off for a reactive load must not find it silently re-armed
    /// tomorrow), and the upgrade path (a config file written before the
    /// setting existed arms the cut, not whatever the serializer felt like).
    /// </para>
    /// <para>
    /// No collection fixture: <see cref="PttConfig.Load"/> and
    /// <see cref="PttConfig.Save"/> take an explicit directory and touch no
    /// process-wide state, so each test gets its own throwaway directory.
    /// </para>
    /// </remarks>
    public sealed class PttConfigTests : IDisposable
    {
        private const string Operator = "testop";

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "JJFlexTests-PttConfig-" + Guid.NewGuid().ToString("N"));

        public PttConfigTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void The_cut_defaults_on_for_a_fresh_operator()
        {
            // Ruled ON 2026-08-26 and re-confirmed 2026-08-30. A wrong cut is
            // startling and recoverable; damaged finals are neither.
            Assert.True(new PttConfig().CutTransmitOnReflectedAlarm);
            Assert.True(PttConfig.Load(_dir, Operator).CutTransmitOnReflectedAlarm);
        }

        [Fact]
        public void Switching_the_cut_off_survives_a_restart()
        {
            // The operator running a reactive load or an experimental antenna
            // made a deliberate choice through a deliberate pipeline. An app
            // that quietly re-arms the cut has overruled them — the same
            // offence, mirrored, as cutting without permission.
            //
            // Self-controlling: the default is TRUE, so reading FALSE back
            // proves the file was genuinely read rather than defaulted.
            var config = PttConfig.Load(_dir, Operator);
            config.CutTransmitOnReflectedAlarm = false;
            config.Save(_dir, Operator);

            Assert.False(PttConfig.Load(_dir, Operator).CutTransmitOnReflectedAlarm);
        }

        [Fact]
        public void Switching_the_cut_back_on_survives_a_restart_too()
        {
            var config = PttConfig.Load(_dir, Operator);
            config.CutTransmitOnReflectedAlarm = false;
            config.Save(_dir, Operator);

            var reloaded = PttConfig.Load(_dir, Operator);
            reloaded.CutTransmitOnReflectedAlarm = true;
            reloaded.Save(_dir, Operator);

            Assert.True(PttConfig.Load(_dir, Operator).CutTransmitOnReflectedAlarm);
        }

        [Fact]
        public void A_config_saved_before_the_setting_existed_arms_the_cut()
        {
            // The upgrade path: every install older than #224's cut has a
            // pttConfig.xml with no CutTransmitOnReflectedAlarm element. The
            // serializer must leave the initializer's TRUE standing — the
            // protective default is exactly for the operator who has never
            // heard of the setting.
            //
            // The timeout is the positive control: 120 is not the default, so
            // reading it back proves this file was parsed rather than thrown
            // away for the all-defaults fallback — which would also answer
            // "true", for the wrong reason.
            File.WriteAllText(Path.Combine(_dir, Operator + "_pttConfig.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<PttConfig>\n" +
                "  <TimeoutSeconds>120</TimeoutSeconds>\n" +
                "</PttConfig>\n");

            var config = PttConfig.Load(_dir, Operator);

            Assert.Equal(120, config.TimeoutSeconds);
            Assert.True(config.CutTransmitOnReflectedAlarm);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using JJFlexUpdater;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The updater's preferences had never been saved. <see cref="UpdaterSettings"/>
    /// carried an enum view, <c>Channel</c>, beside the stored <c>ChannelWire</c>
    /// (JSON name "channel"), and its options read names without regard to case,
    /// so System.Text.Json refused the type and every <see cref="UpdaterSettings.Save"/>
    /// threw into a bare catch. The channel, both automatic-check choices, the last
    /// check time, a skipped version and the nightly consent all fell back to their
    /// defaults at the next start.
    /// <para>
    /// Everything here goes through the real <see cref="UpdaterSettings.Save"/> and
    /// <see cref="UpdaterSettings.Load"/> against a temporary folder, always with an
    /// explicit path, so the operator's <c>%APPDATA%\JJFlexRadio</c> is never read or
    /// written and no process-wide state is touched. No window, no network.
    /// </para>
    /// </summary>
    public sealed class UpdaterSettingsPersistenceTests
    {
        private static string ScratchFolder() =>
            Path.Combine(Path.GetTempPath(), "jjflex-updater-settings-" + Guid.NewGuid().ToString("N"));

        private static void InScratch(Action<string> body)
        {
            string folder = ScratchFolder();
            try
            {
                body(folder);
            }
            finally
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }
            }
        }

        private static readonly DateTimeOffset SomeCheck = new DateTimeOffset(2026, 11, 23, 21, 45, 12, TimeSpan.Zero);

        [Fact]
        public void Today_settings_save_without_failing()
        {
            InScratch(folder =>
            {
                string path = Path.Combine(folder, UpdaterSettings.DefaultFileName);
                bool saved = new UpdaterSettings { Channel = UpdateChannel.Beta }.TrySave(path, out string? failure);

                Assert.True(saved, failure);
                Assert.Null(failure);
                Assert.True(File.Exists(path));
            });
        }

        [Fact]
        public void Every_choice_survives_a_save_and_a_reload()
        {
            InScratch(folder =>
            {
                string path = Path.Combine(folder, UpdaterSettings.DefaultFileName);
                new UpdaterSettings
                {
                    Channel = UpdateChannel.Nightly,
                    AutoCheckOnLaunch = false,
                    PeriodicCheckWhileRunning = false,
                    LastCheckUtc = SomeCheck,
                    SkippedVersion = "4.2.0.1234",
                    NightlyConsentAcknowledged = true,
                }.Save(path);

                UpdaterSettings back = UpdaterSettings.Load(path);

                Assert.Equal(UpdateChannel.Nightly, back.Channel);
                Assert.False(back.AutoCheckOnLaunch);
                Assert.False(back.PeriodicCheckWhileRunning);
                Assert.Equal(SomeCheck, back.LastCheckUtc);
                Assert.Equal("4.2.0.1234", back.SkippedVersion);
                Assert.True(back.NightlyConsentAcknowledged);
            });
        }

        /// <summary>One persisted field moved off its default, and how to read it back.</summary>
        public sealed class FieldCase
        {
            public FieldCase(string name, Action<UpdaterSettings> set, Func<UpdaterSettings, object?> read, object? expected)
            {
                Name = name; Set = set; Read = read; Expected = expected;
            }
            public string Name { get; }
            public Action<UpdaterSettings> Set { get; }
            public Func<UpdaterSettings, object?> Read { get; }
            public object? Expected { get; }
            public override string ToString() => Name;
        }

        private static readonly FieldCase[] Fields =
        {
            new FieldCase("channel beta", s => s.Channel = UpdateChannel.Beta, s => s.Channel, UpdateChannel.Beta),
            new FieldCase("channel nightly", s => s.Channel = UpdateChannel.Nightly, s => s.Channel, UpdateChannel.Nightly),
            new FieldCase("check on launch off", s => s.AutoCheckOnLaunch = false, s => s.AutoCheckOnLaunch, false),
            new FieldCase("periodic check off", s => s.PeriodicCheckWhileRunning = false, s => s.PeriodicCheckWhileRunning, false),
            new FieldCase("last check time", s => s.LastCheckUtc = SomeCheck, s => s.LastCheckUtc, (DateTimeOffset?)SomeCheck),
            new FieldCase("skipped version", s => s.SkippedVersion = "4.2.1.77", s => s.SkippedVersion, "4.2.1.77"),
            new FieldCase("nightly consent", s => s.NightlyConsentAcknowledged = true, s => s.NightlyConsentAcknowledged, true),
        };

        public static IEnumerable<object[]> FieldNames() => Fields.Select(f => new object[] { f.Name });

        /// <summary>
        /// Each persisted field alone: moved off its default, saved, reloaded, and
        /// read back; every OTHER field must come back at its default. So a field
        /// the file stops carrying goes red here by name, and so does a field whose
        /// value leaks into another.
        /// </summary>
        [Theory]
        [MemberData(nameof(FieldNames))]
        public void Each_persisted_choice_survives_a_save_and_a_reload_on_its_own(string field)
        {
            FieldCase subject = Fields.Single(f => f.Name == field);
            InScratch(folder =>
            {
                string path = Path.Combine(folder, UpdaterSettings.DefaultFileName);
                var settings = new UpdaterSettings();
                subject.Set(settings);
                Assert.True(settings.TrySave(path, out string? failure), failure);

                UpdaterSettings back = UpdaterSettings.Load(path);
                Assert.Equal(subject.Expected, subject.Read(back));

                var defaults = new UpdaterSettings();
                foreach (FieldCase other in Fields)
                {
                    if (other.Name == subject.Name || (other.Name.StartsWith("channel") && subject.Name.StartsWith("channel")))
                        continue;
                    Assert.Equal(other.Read(defaults), other.Read(back));
                }
            });
        }

        /// <summary>
        /// The file's names are a contract from the first save onwards: the next
        /// version reads what this one wrote. The channel is stored once, as its
        /// wire word, and the enum view is not in the file at all.
        /// </summary>
        [Fact]
        public void The_saved_file_holds_each_choice_once_under_its_own_name()
        {
            InScratch(folder =>
            {
                string path = Path.Combine(folder, UpdaterSettings.DefaultFileName);
                Assert.True(new UpdaterSettings
                {
                    Channel = UpdateChannel.Beta,
                    LastCheckUtc = SomeCheck,
                    SkippedVersion = "4.2.0.1",
                }.TrySave(path, out string? failure), failure);

                JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                Assert.Equal(
                    new[]
                    {
                        "auto_check_on_launch", "channel", "last_check_utc",
                        "nightly_consent_acknowledged", "periodic_check_while_running", "skipped_version",
                    },
                    json.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
                Assert.Equal("beta", (string?)json["channel"]);
            });
        }

        [Fact]
        public void A_save_that_cannot_write_says_which_file_and_why_and_does_not_throw()
        {
            InScratch(folder =>
            {
                // The settings folder's place is taken by a FILE, so the folder
                // cannot be created: a deterministic failure on any machine, with
                // no attributes or permissions to restore afterwards.
                Directory.CreateDirectory(folder);
                string blocker = Path.Combine(folder, "not-a-folder");
                File.WriteAllText(blocker, "x");
                string path = Path.Combine(blocker, UpdaterSettings.DefaultFileName);
                var settings = new UpdaterSettings { Channel = UpdateChannel.Beta };

                bool saved = settings.TrySave(path, out string? failure);

                Assert.False(saved);
                Assert.NotNull(failure);
                Assert.Contains(path, failure);
                Assert.Contains("will be lost", failure);
                Assert.False(File.Exists(path));

                // Save itself still never throws on the same failure.
                settings.Save(path);
            });
        }

        /// <summary>
        /// Save traces what TrySave reports, at Error. Read from the source rather
        /// than captured from the trace: capturing means switching JJTrace's
        /// process-wide <c>Tracing.On</c>, which any parallel test can switch back,
        /// and a flaky guard is worse than a plain one.
        /// </summary>
        [Fact]
        public void A_failed_save_is_traced_not_swallowed()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexUpdater", "UpdaterSettings.cs"));
            Match save = Regex.Match(source, @"public void Save\(string\? path = null\)\s*\{(?<body>[^{}]*)\}");
            Assert.True(save.Success, "Save(string? path = null) was not found in UpdaterSettings.cs");
            string body = Regex.Replace(save.Groups["body"].Value, @"\s+", " ");

            Assert.Contains("if (!TrySave(path, out string? failure))", body);
            Assert.Contains("Tracing.TraceLine(failure!, System.Diagnostics.TraceLevel.Error)", body);

            // No catch anywhere in the class may swallow an exception unnamed.
            Assert.DoesNotMatch(@"catch\s*\{", source);
        }

        [Fact]
        public void A_missing_file_loads_the_defaults()
        {
            InScratch(folder =>
            {
                UpdaterSettings loaded = UpdaterSettings.Load(Path.Combine(folder, UpdaterSettings.DefaultFileName));
                AssertDefaults(loaded);
            });
        }

        [Fact]
        public void An_unreadable_file_loads_the_defaults_and_is_left_as_it_was()
        {
            InScratch(folder =>
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, UpdaterSettings.DefaultFileName);
                const string damaged = "{ \"channel\": \"beta\", this is not json";
                File.WriteAllText(path, damaged);

                UpdaterSettings loaded = UpdaterSettings.Load(path);

                AssertDefaults(loaded);
                Assert.Equal(damaged, File.ReadAllText(path));
            });
        }

        private static void AssertDefaults(UpdaterSettings s)
        {
            Assert.Equal(UpdateChannel.Stable, s.Channel);
            Assert.True(s.AutoCheckOnLaunch);
            Assert.True(s.PeriodicCheckWhileRunning);
            Assert.Null(s.LastCheckUtc);
            Assert.Null(s.SkippedVersion);
            Assert.False(s.NightlyConsentAcknowledged);
        }

        // ---- The class, not the instance --------------------------------------

        /// <summary>
        /// Every type in the updater whose properties carry a JSON name must have
        /// a JSON contract System.Text.Json accepts, read the strictest way the
        /// updater reads any file (names without regard to case). A collision here
        /// does not fail a build or a load: it throws at the first save and, behind
        /// a catch, nothing ever says so.
        /// </summary>
        [Fact]
        public void No_serialised_type_in_the_updater_has_two_properties_sharing_a_json_name()
        {
            Type[] types = typeof(UpdaterSettings).Assembly.GetTypes()
                .Where(t => !t.IsGenericTypeDefinition && !t.IsAbstract && !t.IsInterface)
                .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Any(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() != null))
                .ToArray();

            Assert.Contains(typeof(UpdaterSettings), types);
            Assert.True(types.Length >= 4, "The scan found " + types.Length + " serialised types; expected the settings, the manifests and the cache at least.");

            var failures = new List<string>();
            foreach (Type t in types)
            {
                string? problem = ContractProblem(t);
                if (problem != null) failures.Add(t.FullName + ": " + problem);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        /// <summary>The guard's positive control: the exact shape of the old defect
        /// is caught.</summary>
        [Fact]
        public void The_collision_scan_catches_the_shape_that_broke_the_settings()
        {
            string? problem = ContractProblem(typeof(CollidingShape));
            Assert.NotNull(problem);
            Assert.Contains("collides", problem);
            Assert.Null(ContractProblem(typeof(CollidingShapeRepaired)));
        }

        private static string? ContractProblem(Type t)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            };
            try
            {
                JsonTypeInfo info = options.GetTypeInfo(t);
                _ = info.Properties.Count;
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }

        private sealed class CollidingShape
        {
            [JsonPropertyName("channel")]
            public string Wire { get; set; } = "stable";
            public int Channel { get => 0; set { } }
        }

        private sealed class CollidingShapeRepaired
        {
            [JsonPropertyName("channel")]
            public string Wire { get; set; } = "stable";
            [JsonIgnore]
            public int Channel { get => 0; set { } }
        }

        private static string RepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}

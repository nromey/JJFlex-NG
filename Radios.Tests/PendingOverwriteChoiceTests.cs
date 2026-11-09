#nullable enable
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #999 round 3, ruled C5a by Noel 2026-11-08, with the orchestrator's
/// correction: a "warn before a recording on the radio is overwritten" choice
/// made before any radio had connected survives a restart, and is applied only
/// when a radio ACTUALLY CONNECTS, once, then cleared. A radio that was only
/// discovered never receives it. Against a temporary settings root
/// (<see cref="RadioConfigStaticsScope"/>) and a file-backed stand-in for the
/// app-wide store; the real store, audioConfig.xml, is JJFlexWpf's and is
/// tested there (<c>PendingOverwriteChoiceStoreTests</c>).
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class PendingOverwriteChoiceTests : IDisposable
{
    private const string First = "1234-5678-8600-0981", Second = "1234-5678-8600-0982";
    private readonly RadioConfigStaticsScope _scope = new(nameof(PendingOverwriteChoiceTests));

    public void Dispose()
    {
        try
        {
            RadioConfig.PendingWarnStore = null;
            RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null;
        }
        finally { _scope.Dispose(); }
    }

    /// <summary>A stand-in app-wide store: one text file, "", "true" or "false".</summary>
    private sealed class FileStore : IPendingChoiceStore
    {
        private readonly string _path;
        public int Writes;
        public FileStore(string path) => _path = path;
        public bool? Read() => File.Exists(_path) ? File.ReadAllText(_path) switch { "true" => true, "false" => false, _ => null } : null;
        public bool Write(bool? value)
        {
            Writes++;
            File.WriteAllText(_path, value switch { true => "true", false => "false", null => "" });
            return true;
        }
    }

    private string StorePath => _scope.PathTo("pending-choice.txt");

    /// <summary>
    /// A restart: the program's own memory of the choice is gone, and a fresh
    /// store is wired over the same file, as startup does.
    /// </summary>
    private FileStore Restart()
    {
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null; // the in-memory copy dies with the process
        var store = new FileStore(StorePath);
        RadioConfig.PendingWarnStore = store;
        return store;
    }

    private static FlexBase Rig() => new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };

    [Fact]
    public void A_choice_made_before_any_radio_connected_survives_a_restart()
    {
        var store = Restart();
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        Assert.Equal(1, store.Writes);
        Assert.Equal("false", File.ReadAllText(StorePath));

        Restart();
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void A_discovered_radio_does_not_receive_it_and_the_first_connect_does_once()
    {
        Restart();
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;

        // Discovered only: a sighting writes what the radio says about itself
        // into its config. That is not a connect, and the choice still waits.
        Assert.True(RadioConfig.Update(First, c => c.Nickname = "seen on the LAN"));
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.True(RadioConfig.LoadForRadio(First).WarnBeforeSliceRecordingOverwritten);

        Restart();
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);

        // The connect works: the radio takes the choice, and it is spent.
        Rig().ApplyChoicesWaitingForAConnect(First);
        Assert.False(RadioConfig.LoadForRadio(First).WarnBeforeSliceRecordingOverwritten);
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.Equal("", File.ReadAllText(StorePath));

        // Spent across a restart too: the next radio to connect keeps its own default.
        Restart();
        Rig().ApplyChoicesWaitingForAConnect(Second);
        Assert.True(RadioConfig.LoadForRadio(Second).WarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void A_choice_that_could_not_reach_its_radio_keeps_waiting()
    {
        Restart();
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = true;
        // A malformed serial is refused by the per-radio store; nothing is spent.
        Assert.False(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten("not-a-serial", lastConnectedBefore: ""));
        Assert.True(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
    }

    /// <summary>A store whose file reads but cannot be written: locked, or read-only.</summary>
    private sealed class StuckStore : IPendingChoiceStore
    {
        public bool? OnDisk;
        public bool Writable;
        public int Reads;
        public bool? Read() { Reads++; return OnDisk; }
        public bool Write(bool? value)
        {
            if (!Writable) return false;
            OnDisk = value;
            return true;
        }
    }

    [Fact]
    public void A_choice_the_store_could_not_save_holds_for_this_run_and_is_saved_once_it_can_be()
    {
        // Codex's check of round 3, N1: the getter read the disk back and lost the new choice.
        var store = new StuckStore();
        RadioConfig.PendingWarnStore = store;
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        Assert.Null(store.OnDisk);
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.Equal(0, store.Reads);   // memory answered, not the disk it could not change

        // The file comes free: the next read saves it, and from then on the disk is the truth again.
        store.Writable = true;
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.False(store.OnDisk);
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.Equal(1, store.Reads);

        // And the connect still receives it.
        Assert.True(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(First, lastConnectedBefore: ""));
        Assert.False(RadioConfig.LoadForRadio(First).WarnBeforeSliceRecordingOverwritten);
        Assert.Null(store.OnDisk);
    }

    [Fact]
    public void A_spent_choice_the_store_could_not_clear_is_not_applied_again_this_run()
    {
        var store = new StuckStore { OnDisk = false };
        RadioConfig.PendingWarnStore = store;
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);   // read from disk

        Assert.True(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(First, lastConnectedBefore: ""));
        Assert.False(RadioConfig.LoadForRadio(First).WarnBeforeSliceRecordingOverwritten);
        Assert.False(store.OnDisk);                                            // the clear was refused
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);   // but it is spent for this run

        Assert.False(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(Second, lastConnectedBefore: First));
        Assert.True(RadioConfig.LoadForRadio(Second).WarnBeforeSliceRecordingOverwritten);

        store.Writable = true;
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.Null(store.OnDisk);                                             // the retried clear landed
    }

    [Fact]
    public void Unwired_it_still_waits_in_memory_for_this_run()
    {
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        Assert.True(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(First, lastConnectedBefore: ""));
        Assert.False(RadioConfig.LoadForRadio(First).WarnBeforeSliceRecordingOverwritten);
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
    }

    /// <summary>
    /// The connect applies it the moment it works, and nothing that only
    /// discovers or remembers a radio may: every door that can spend the choice
    /// is named here, so a new one has to be argued for.
    /// </summary>
    [Fact]
    public void Only_a_connected_radio_can_spend_the_choice()
    {
        string root = FlexBasePartials.RepoRoot();
        string connect = File.ReadAllText(Path.Combine(root, "Radios", "FlexBase.cs"));
        int worked = connect.IndexOf("Tracing.TraceLine(\"Connect worked:\" + theRadio.Serial, TraceLevel.Info);", StringComparison.Ordinal);
        Assert.True(worked > 0);
        int applied = connect.IndexOf("ApplyChoicesWaitingForAConnect(theRadio.Serial);", worked, StringComparison.Ordinal);
        Assert.InRange(applied - worked, 1, 400);

        var callers = Directory.EnumerateFiles(Path.Combine(root, "Radios"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "JJFlexWpf"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        // Round 4: Settings no longer hands a choice to a connected radio. It
        // saves one shown against the radio last connected through its own edit
        // (SliceOverwriteSettingEdits.Commit), tested in JJFlexWpf.Tests.
        Assert.Equal(new[]
        {
            "Radios/FlexBase.SliceRecorder.cs",          // the connect, and the gate's facts for a slice of ours
        }, callers);

        // The retried connect is a connect too, and applies before it records the radio as last connected.
        int retried = connect.IndexOf("Seed the 4.2-line discovery-chain cache on retry success too.", StringComparison.Ordinal);
        Assert.True(retried > 0);
        int retryApplied = connect.LastIndexOf("ApplyChoicesWaitingForAConnect(theRadio.Serial);", retried, StringComparison.Ordinal);
        Assert.InRange(retried - retryApplied, 1, 200);
        // And the first connect applies before it records the radio, too.
        int recorded = connect.IndexOf("RecordConnectedRadioForCache();", applied, StringComparison.Ordinal);
        Assert.True(recorded > applied);
    }
}

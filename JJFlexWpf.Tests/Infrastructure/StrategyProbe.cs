using System.Windows;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>What one realization strategy did to one dialog.</summary>
public sealed record ProbeResult(
    string Dialog,
    RealizationStrategy Strategy,
    bool Constructed,
    bool LoadedFired,
    int PeerCount,
    int FocusableCount,
    bool FocusMovable,
    int TabStops,
    string Note);

/// <summary>
/// The focus-avoidance experiment, run as a test so its answer is a measured
/// result and not a claim in a document.
///
/// <para>The question is not academic. The operator is blind, works in NVDA, and
/// is at the keyboard while this suite runs. A suite that shows windows on the
/// interactive desktop takes the keyboard away from him, and a suite he cannot
/// run while working is a suite that does not get run.</para>
/// </summary>
public static class StrategyProbe
{
    /// <summary>
    /// Dialogs chosen to stress different things: a plain one, the one that
    /// already failed, and the one whose category host has the headerless
    /// template that broke the tree in the first place.
    /// </summary>
    public static readonly string[] Subjects = { "AboutProgramDialog", "SettingsDialog", "AudioWorkshopDialog" };

    public static IReadOnlyList<ProbeResult> RunAll()
    {
        var results = new List<ProbeResult>();

        foreach (var strategy in new[]
                 {
                     RealizationStrategy.LayoutOnly,
                     RealizationStrategy.HandleOnly,
                     RealizationStrategy.OffScreenNonActivated,
                 })
        {
            foreach (var subject in Subjects)
                results.Add(UiThread.Run(() => Probe(subject, strategy)));
        }

        // The private desktop must be established before the thread owns any
        // window, so it gets a thread of its own rather than contaminating the
        // shared one.
        foreach (var subject in Subjects)
        {
            try
            {
                results.Add(UiThread.RunOnPrivateThread(
                    () => Probe(subject, RealizationStrategy.PrivateDesktopShown),
                    privateDesktop: true,
                    timeout: TimeSpan.FromSeconds(90)));
            }
            catch (Exception ex)
            {
                results.Add(new ProbeResult(subject, RealizationStrategy.PrivateDesktopShown,
                    false, false, 0, 0, false, 0, "Failed: " + TreeWalk.Describe(ex)));
            }
        }

        return results;
    }

    private static ProbeResult Probe(string dialogName, RealizationStrategy strategy)
    {
        var entry = DialogCatalog.Discover().FirstOrDefault(e => string.Equals(e.Name, dialogName, StringComparison.Ordinal));
        if (entry == null)
            return new ProbeResult(dialogName, strategy, false, false, 0, 0, false, 0, "Dialog type not found.");

        Window? window = null;
        try
        {
            window = DialogCatalog.Construct(entry.Type, out var failure);
            if (window == null)
                return new ProbeResult(dialogName, strategy, false, false, 0, 0, false, 0,
                    "Could not construct: " + (failure?.Reason ?? "unknown"));

            using var realized = RealizedDialog.Realize(window, strategy);
            var snapshot = TreeWalk.Walk(realized.Root);
            var tab = FocusWalk.Walk(realized);
            FocusWalk.Release();

            return new ProbeResult(
                dialogName,
                strategy,
                Constructed: true,
                LoadedFired: realized.LoadedFired,
                PeerCount: snapshot.Peers.Count,
                FocusableCount: snapshot.VisualElements.Count(e => e is not Window && TreeWalk.IsFocusableStop(e)),
                FocusMovable: tab.Executed && tab.Order.Count > 0,
                TabStops: tab.Order.Count,
                Note: tab.Diagnostic);
        }
        catch (Exception ex)
        {
            return new ProbeResult(dialogName, strategy, window != null, false, 0, 0, false, 0,
                "Threw: " + TreeWalk.Describe(ex));
        }
    }
}

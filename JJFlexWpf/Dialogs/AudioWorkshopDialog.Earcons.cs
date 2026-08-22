using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, Earcon Explorer tab: every sound the application can make,
/// with somewhere to stand while you judge it.
///
/// Two things changed in Sprint 32 Track E.
///
/// The list is no longer written here. It is <see cref="EarconCatalog"/>,
/// built by reflection from the methods themselves, so a new earcon appears in
/// this tab the day it is written and nobody has to remember this file exists.
/// The hand-written version reached 18 of 45 sounds; the connect series was
/// missing entirely, including the app's most recognisable sound.
///
/// And the sections now mirror the six EarconCategory values, which are the
/// same six switches Settings offers. The old headings were their own
/// vocabulary — "Meter Tones" sat above a group of alert beeps that are not
/// meter tones and do not answer to the meter switch, so an operator who
/// turned off what the heading named would have found the sounds still
/// playing.
///
/// The bench half (#119) exists because a sound cannot be judged in a quiet
/// room. Real band noise is the environment that matters, and it moves — so
/// there is a level, a stereo position, a repeat, and a way to play a whole
/// family back to back rather than pressing one button and trying to remember
/// what the last one was like.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region Tab 3: Earcon Explorer

    /// <summary>Bench level, as a multiplier on each sound's own tier.</summary>
    private Slider? _earconBenchGain;

    /// <summary>Bench stereo position, added to whatever the sound does itself.</summary>
    private Slider? _earconBenchPan;

    /// <summary>How many times a single press plays the sound.</summary>
    private Slider? _earconBenchRepeat;

    /// <summary>Gap between repeats and between sounds in a series, in ms.</summary>
    private Slider? _earconBenchGap;

    private TextBlock? _earconBenchStatus;

    /// <summary>
    /// The series currently playing, so a second press stops it rather than
    /// starting a second one on top. A series is the one thing here that
    /// outlives the click that started it.
    /// </summary>
    private DispatcherTimer? _earconSeriesTimer;

    private void BuildEarconExplorerTab()
    {
        // Subscribed here rather than in the shell so the cleanup lives beside
        // the thing that needs cleaning up. Both continuous earcons and a
        // running series would otherwise outlive the window that started them,
        // and the ATU progress tone in particular has no other off switch an
        // operator would think to look for.
        Closed += (s, e) => StopEverythingOnTheBench(spoken: false);

        BuildEarconBenchSection();

        foreach (var category in EarconCatalog.Categories)
            BuildEarconCategorySection(EarconCatalog.CategoryLabel(category),
                EarconCatalog.CategoryDescription(category),
                EarconCatalog.InCategory(category));

        var loose = EarconCatalog.Uncategorised;
        if (loose.Count > 0)
            BuildEarconCategorySection(EarconCatalog.CategoryLabel(null),
                EarconCatalog.CategoryDescription(null), loose);

        BuildEarconAuditSection();
    }

    /// <summary>
    /// The bench controls, first because they govern everything below them.
    /// </summary>
    private void BuildEarconBenchSection()
    {
        AddSectionHeader(EarconExplorerContent, "Bench");
        var panel = _section ?? EarconExplorerContent;

        panel.Children.Add(new TextBlock
        {
            Text = "These settings apply to the sounds below while you audition them. "
                 + "They change nothing permanently and touch none of your saved audio settings.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        });

        _earconBenchGain = AddEarconBenchSlider(panel, "Bench level",
            10, 200, 100,
            "A multiplier on each sound's own level, as a percentage. 100 plays it exactly as it ships. "
            + "Turning this up is how you find out whether a sound is too quiet or simply too similar to the noise, "
            + "which are different problems with different fixes.");

        _earconBenchPan = AddEarconBenchSlider(panel, "Bench pan",
            -100, 100, 0,
            "Stereo position, negative left and positive right. Added to any panning the sound already does, "
            + "so a left-panned filter edge auditioned at pan right lands in the middle rather than jumping.");

        _earconBenchRepeat = AddEarconBenchSlider(panel, "Repeats",
            1, 10, 1,
            "How many times one press plays the sound. A repeat is how a short click stops being a guess: "
            + "one may or may not have got through the noise, four in a row tells you.");

        _earconBenchGap = AddEarconBenchSlider(panel, "Gap between sounds",
            100, 2000, 600,
            "Milliseconds between repeats, and between sounds when you play a whole family.");

        var stopButton = new Button
        {
            Content = "Stop anything playing",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 6, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(stopButton, "Stop anything playing");
        JJFlexHelp.SetText(stopButton,
            "Stops a running series and every continuous sound: the antenna tuner progress tone and "
            + "the transmit test-tone monitor. The one button to reach for when you have lost track.");
        stopButton.Click += (s, e) => StopEverythingOnTheBench(spoken: true);
        panel.Children.Add(stopButton);

        _earconBenchStatus = new TextBlock
        {
            Margin = new Thickness(2, 6, 2, 2),
            TextWrapping = TextWrapping.Wrap,
            Text = "Ready.",
        };
        AutomationProperties.SetName(_earconBenchStatus, "Bench status");
        AutomationProperties.SetLiveSetting(_earconBenchStatus, AutomationLiveSetting.Polite);
        panel.Children.Add(_earconBenchStatus);
    }

    private static Slider AddEarconBenchSlider(StackPanel parent, string label,
        int min, int max, int value, string help)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 2, 2, 2),
        };
        row.Children.Add(new TextBlock
        {
            Text = label + ":",
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 220,
            SmallChange = 1,
            LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, label);
        JJFlexHelp.SetText(slider, help);
        row.Children.Add(slider);
        parent.Children.Add(row);
        return slider;
    }

    /// <summary>
    /// One family: a "play the whole family" button, then a button per sound —
    /// or a Start and a Stop for the sounds that run until told otherwise.
    /// </summary>
    private void BuildEarconCategorySection(string title, string description,
        IReadOnlyList<EarconEntry> entries)
    {
        AddSectionHeader(EarconExplorerContent, title);
        var panel = _section ?? EarconExplorerContent;

        panel.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        });

        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No sounds in this family.",
                Margin = new Thickness(2),
            });
            return;
        }

        // Playing a family in order is the comparison that actually settles
        // things. Two sounds you hear a minute apart both seem fine; the same
        // two back to back are either distinguishable or they are not.
        var seriesButton = new Button
        {
            Content = $"Play all {entries.Count} in order",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 2, 2, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(seriesButton, $"Play all {entries.Count} sounds in {title} in order");
        JJFlexHelp.SetText(seriesButton,
            "Plays every sound in this family one after another, with the bench gap between them, "
            + "naming each as it goes. Press it again to stop.");
        seriesButton.Click += (s, e) => PlayEarconSeries(entries, title);
        panel.Children.Add(seriesButton);

        foreach (var entry in entries)
            AddEarconEntryControls(panel, entry);
    }

    private void AddEarconEntryControls(StackPanel parent, EarconEntry entry)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2),
        };

        if (entry.IsContinuous)
        {
            // A continuous earcon cannot honestly be offered as one Play
            // button. It runs until stopped, so the surface that auditions it
            // has to be able to stop it — a button that starts something it
            // cannot finish is a trap, and the ATU progress tone would still
            // be running long after the operator moved on.
            var start = MakeEarconButton($"Start: {entry.Label}", $"Start {entry.Label}", entry.Description,
                () => PlayEarconOnce(entry, announce: true));
            var stop = MakeEarconButton($"Stop: {entry.Label}", $"Stop {entry.Label}", null,
                () =>
                {
                    entry.Stop?.Invoke();
                    SayOnBench(Lexicon.Get("earcon.bench.sound_stopped", ("sound", entry.Label)));
                });
            row.Children.Add(start);
            row.Children.Add(stop);
        }
        else
        {
            row.Children.Add(MakeEarconButton($"Play: {entry.Label}", $"Play {entry.Label}",
                entry.Description, () => PlayEarconRepeats(entry)));
        }

        parent.Children.Add(row);
    }

    private static Button MakeEarconButton(string content, string name, string? help, Action onClick)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 240,
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, name);
        if (!string.IsNullOrWhiteSpace(help)) JJFlexHelp.SetText(button, help);
        button.Click += (s, e) => onClick();
        return button;
    }

    /// <summary>
    /// The audit, last, and usually empty. If it is not empty, somebody added
    /// an earcon and did not name it, and this tab is now reaching fewer
    /// sounds than the application can make — which is exactly the condition
    /// the registry exists to stop recurring silently.
    /// </summary>
    private void BuildEarconAuditSection()
    {
        var missing = EarconCatalog.UnregisteredMethods;
        if (missing.Count == 0) return;

        AddSectionHeader(EarconExplorerContent, "Sounds missing from this list");
        var panel = _section ?? EarconExplorerContent;
        panel.Children.Add(new TextBlock
        {
            Text = $"{missing.Count} earcon "
                 + (missing.Count == 1 ? "method has" : "methods have")
                 + " no name declared, so "
                 + (missing.Count == 1 ? "it is" : "they are")
                 + " not playable here: "
                 + string.Join(", ", missing)
                 + ". Adding an [Earcon] attribute to the method fixes this.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2),
        });
    }

    // ------------------------------------------------------------------
    // Playing
    // ------------------------------------------------------------------

    private float BenchGain => (float)((_earconBenchGain?.Value ?? 100) / 100.0);
    private float BenchPan => (float)((_earconBenchPan?.Value ?? 0) / 100.0);
    private int BenchRepeats => (int)Math.Round(_earconBenchRepeat?.Value ?? 1);
    private int BenchGapMs => (int)Math.Round(_earconBenchGap?.Value ?? 600);

    /// <summary>Play one sound once, at the bench settings.</summary>
    private void PlayEarconOnce(EarconEntry entry, bool announce)
    {
        EarconPlayer.PlayWithBenchSettings(entry.Play, BenchGain, BenchPan);
        if (announce) SayOnBench(entry.Label);
    }

    /// <summary>
    /// Play one sound the requested number of times. One repeat goes straight
    /// out; more than one runs on a timer, because a short click played four
    /// times in a row is the honest test of whether it survives the noise and
    /// a single one never is.
    /// </summary>
    private void PlayEarconRepeats(EarconEntry entry)
    {
        StopEarconSeries();
        int total = Math.Max(BenchRepeats, 1);
        if (total == 1)
        {
            PlayEarconOnce(entry, announce: true);
            return;
        }

        int played = 0;
        SayOnBench(Lexicon.Get("earcon.bench.playing_repeats",
            ("sound", entry.Label), ("count", total)));
        PlayEarconOnce(entry, announce: false);
        played++;

        _earconSeriesTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(BenchGapMs, 100)),
        };
        _earconSeriesTimer.Tick += (s, e) =>
        {
            if (played >= total) { StopEarconSeries(); return; }
            PlayEarconOnce(entry, announce: false);
            played++;
        };
        _earconSeriesTimer.Start();
    }

    /// <summary>
    /// Walk a whole family, naming each sound as it plays. Pressing the same
    /// button again stops it — a series that can only be started is a series
    /// you have to wait out.
    /// </summary>
    private void PlayEarconSeries(IReadOnlyList<EarconEntry> entries, string title)
    {
        if (_earconSeriesTimer != null)
        {
            StopEarconSeries();
            SayOnBench(Lexicon.Get("earcon.bench.series_stopped"));
            return;
        }
        if (entries.Count == 0) return;

        // A continuous sound in the middle of a series would run forever and
        // bury everything after it, so a series starts them and stops them
        // again at the next step. The Start and Stop buttons are still there
        // for hearing one properly.
        int index = 0;
        EarconEntry? running = null;

        void Step()
        {
            running?.Stop?.Invoke();
            running = null;

            if (index >= entries.Count)
            {
                StopEarconSeries();
                SayOnBench(Lexicon.Get("earcon.bench.series_finished",
                    ("family", title), ("count", entries.Count)));
                return;
            }

            var entry = entries[index++];
            SayOnBench(Lexicon.Get("earcon.bench.series_step",
                ("index", index), ("count", entries.Count), ("sound", entry.Label)));
            EarconPlayer.PlayWithBenchSettings(entry.Play, BenchGain, BenchPan);
            if (entry.IsContinuous) running = entry;
        }

        _earconSeriesTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(BenchGapMs, 100)),
        };
        _earconSeriesTimer.Tick += (s, e) => Step();
        Step();
        _earconSeriesTimer.Start();
    }

    private void StopEarconSeries()
    {
        if (_earconSeriesTimer == null) return;
        _earconSeriesTimer.Stop();
        _earconSeriesTimer = null;
    }

    /// <summary>
    /// Stop everything this tab can have running: a series, and both
    /// continuous earcons. Reachable as one button because losing track of
    /// which sound is still going is the normal state of a bench session.
    /// </summary>
    private void StopEverythingOnTheBench(bool spoken)
    {
        // Permanent: this line is the difference between "the tone outlived its
        // dialog" and "the operator closed the dialog, which stopped the tone" —
        // the two readings of the 2026-08-19 missing-farewell report. spoken:false
        // means the Closed handler fired; spoken:true means the bench button.
        JJTrace.Tracing.TraceLine(
            $"AudioWorkshop: StopEverythingOnTheBench (spoken={spoken}) — "
            + $"atuRunning={EarconPlayer.IsATUProgressEarconRunning}, "
            + $"benchRunning={EarconPlayer.IsBenchToneRunning}");
        StopEarconSeries();
        EarconPlayer.StopATUProgressEarcon();
        EarconPlayer.StopTxToneMonitor();
        EarconPlayer.StopBenchTone();
        if (spoken) SayOnBench(Lexicon.Get("earcon.bench.everything_stopped"));
    }

    private void SayOnBench(string text)
    {
        if (_earconBenchStatus != null) _earconBenchStatus.Text = text;
        ScreenReaderOutput.Speak(text, VerbosityLevel.Terse);
    }

    #endregion
}

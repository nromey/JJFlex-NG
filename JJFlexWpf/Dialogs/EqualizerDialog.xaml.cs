using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The radio's graphic equalizer, transmit or receive. Nine bands — 32, 63,
/// 125, 250, 500, 1000, 2000, 4000, 8000 Hz — each ±10 dB, plus whether the
/// equalizer is switched on at all.
///
/// All operations use delegates — no direct FlexBase or Equalizer reference —
/// so one dialog serves both the TX and the RX equalizer, and a test can drive
/// it with no radio.
///
/// <para><b>History (#457, #430).</b> Built in February for Sprint 9 Track B
/// as the WPF replacement for the deleted WinForms FlexEq, and then wired to
/// nothing at all for seven months. Both ends existed the whole time: this
/// dialog, and two buttons whose only handler assignment anywhere was
/// <c>= null</c>. Neither track was wrong and no merge conflicted, which is
/// exactly why it survived until a tester reported the equalizers "lost along
/// the way". Sprint 43 Track C connected it.</para>
///
/// <para><b>Why the band fields are ValueFieldControls</b> and not the text
/// boxes this dialog was born with: a TextBox whose Text is changed in code
/// does not reliably announce, so arrowing a band up and down told a blind
/// operator nothing. ValueFieldControl is the surface the operator already
/// uses everywhere else — it speaks each change, holds its name still while
/// focused so the radio's echo cannot talk over it, says so at the end stops,
/// and takes a typed number including a negative one.</para>
/// </summary>
public partial class EqualizerDialog : JJFlexDialog
{
    /// <summary>The nine band centre frequencies, from the radio's own
    /// table. Not a copy — a table that lived here as well would be free to
    /// disagree with the one the wrapper writes.</summary>
    private static int[] BandHz => FlexBase.EqBandHz;

    private static int BandCount => BandHz.Length;

    #region Delegates

    /// <summary>
    /// Gets the dialog title — which equalizer this is.
    /// </summary>
    public Func<string>? GetEqTitle { get; set; }

    /// <summary>
    /// Gets the current level for a band index (0 to eight), in dB.
    /// Band order is <see cref="FlexBase.EqBandHz"/>: 32 Hz first, 8 kHz last.
    /// </summary>
    public Func<int, int>? GetBandLevel { get; set; }

    /// <summary>
    /// Sets the level for a band index (0 to eight), in dB.
    /// </summary>
    public Action<int, int>? SetBandLevel { get; set; }

    /// <summary>
    /// Whether the equalizer is switched on. When this and
    /// <see cref="SetEqEnabled"/> are both unset the checkbox is hidden and
    /// the dialog is bands-only.
    /// </summary>
    public Func<bool>? GetEqEnabled { get; set; }

    /// <summary>Switches the equalizer on or off.</summary>
    public Action<bool>? SetEqEnabled { get; set; }

    #endregion

    private ValueFieldControl[] _bandFields = Array.Empty<ValueFieldControl>();
    private int[] _originals = Array.Empty<int>();
    private bool _originalEnabled;
    private bool _loading;
    private bool _committed;

    /// <summary>
    /// Whether the operator has actually moved anything. Opening the dialog,
    /// looking, and leaving must write nothing to the radio and must say
    /// nothing about a revert — a receipt for an undo that undid nothing is
    /// noise, and noise is how a real warning gets ignored.
    /// </summary>
    private bool _touched;

    public EqualizerDialog()
    {
        InitializeComponent();
        Loaded += EqualizerDialog_Loaded;
    }

    /// <summary>
    /// Take the title from <see cref="GetEqTitle"/> here, before Loaded.
    ///
    /// <para><b>This is a real bug, found by walking the dialog rather than
    /// reading the diff.</b> The base class speaks the Title aloud from its own
    /// Loaded handler, and that handler is subscribed in the base constructor —
    /// so it runs BEFORE any Loaded handler a subclass adds. Setting the title
    /// in our Loaded handler therefore set it correctly and one moment too
    /// late: the dialog had already announced itself as the XAML's placeholder
    /// "Equalizer", and an operator who cannot see the title bar had no way to
    /// tell the transmit equalizer from the receive one. SourceInitialized runs
    /// before Loaded, which is early enough. Nothing about the wrong title is
    /// visible in a diff, and nothing about it fails a build.</para>
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Title = GetEqTitle?.Invoke() ?? Lexicon.Get("audio.eq.title_generic");
    }

    private void EqualizerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded can fire more than once for a window that is hidden and shown
        // again; building the fields twice would stack eighteen of them.
        if (_bandFields.Length > 0) return;

        _loading = true;
        try
        {
            // Title is set in OnSourceInitialized — see there, and do not move
            // it back here.
            bool hasEnable = GetEqEnabled != null || SetEqEnabled != null;
            EnabledCheck.Visibility = hasEnable ? Visibility.Visible : Visibility.Collapsed;
            if (hasEnable)
            {
                string enableLabel = Lexicon.Get("audio.eq.enabled_label");
                EnabledCheck.Content = enableLabel;
                AutomationProperties.SetName(EnabledCheck, enableLabel);
                _originalEnabled = GetEqEnabled?.Invoke() ?? false;
                EnabledCheck.IsChecked = _originalEnabled;
            }

            _bandFields = new ValueFieldControl[BandCount];
            _originals = new int[BandCount];
            string unit = Lexicon.Get("audio.fields.unit_db");

            for (int i = 0; i < BandCount; i++)
            {
                int index = i;
                _originals[i] = GetBandLevel?.Invoke(i) ?? 0;

                var field = new ValueFieldControl();
                field.Setup(BandLabel(BandHz[i]),
                            FlexBase.EqLevelMin, FlexBase.EqLevelMax,
                            step: 1, initialValue: _originals[i], decimalPlaces: 0, unit: unit);
                field.ValueChanged += (s, v) =>
                {
                    if (_loading) return;
                    _touched = true;
                    SetBandLevel?.Invoke(index, v);
                };
                _bandFields[i] = field;
                BandsPanel.Children.Add(field);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// "32 Hz" through "8 kHz". The kHz form is how the bands are labelled on
    /// every equalizer an operator has ever met; spelling 8000 out in Hz would
    /// be accurate and unfamiliar at once.
    /// </summary>
    private static string BandLabel(int hz)
    {
        if (hz < 1000) return Lexicon.Get("audio.eq.band_hz", ("hz", hz.ToString()));
        return Lexicon.Get("audio.eq.band_khz", ("khz", (hz / 1000).ToString()));
    }

    /// <summary>
    /// Push a whole set of levels to the radio and to the fields at once, used
    /// by Restore and Clear.
    ///
    /// <para>It speaks its own receipt, and that is not optional. The field
    /// controls stay deliberately silent when their value is changed in code
    /// rather than by the operator — that silence is what stops the radio's
    /// polling echo from talking over a sweep. So a button that rewrites nine
    /// fields at once produces no speech whatsoever unless it says so
    /// itself.</para>
    /// </summary>
    private void ApplyLevels(int[] levels, string receiptKey)
    {
        _loading = true;
        try
        {
            for (int i = 0; i < BandCount && i < levels.Length; i++)
            {
                SetBandLevel?.Invoke(i, levels[i]);
                _bandFields[i].Value = levels[i];
            }
            _touched = true;
        }
        finally
        {
            _loading = false;
        }

        ScreenReaderOutput.Speak(Lexicon.Get(receiptKey), VerbosityLevel.Terse);
    }

    /// <summary>
    /// Put the radio back exactly as this dialog found it — every band and the
    /// on/off state.
    /// </summary>
    private void RevertToOriginals()
    {
        for (int i = 0; i < BandCount && i < _originals.Length; i++)
        {
            SetBandLevel?.Invoke(i, _originals[i]);
        }
        SetEqEnabled?.Invoke(_originalEnabled);
    }

    #region Event Handlers

    private void EnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _touched = true;
        SetEqEnabled?.Invoke(EnabledCheck.IsChecked == true);
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyLevels(_originals, "audio.eq.restored");

        // Restore means "as I found it", which includes the switch.
        if (EnabledCheck.Visibility == Visibility.Visible)
        {
            _loading = true;
            try { EnabledCheck.IsChecked = _originalEnabled; }
            finally { _loading = false; }
            SetEqEnabled?.Invoke(_originalEnabled);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyLevels(new int[BandCount], "audio.eq.cleared");
    }

    private void FinishedButton_Click(object sender, RoutedEventArgs e)
    {
        _committed = true;
        CloseWithResult(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // The revert itself happens in OnClosing, which is the only place that
        // catches every way out of this dialog — see there.
        CloseWithResult(false);
    }

    /// <summary>
    /// Undo everything unless the operator pressed Finished.
    ///
    /// <para><b>Why here and not in the Cancel handler.</b> Every level change
    /// in this dialog has already gone to the radio — that is the point, an
    /// equalizer is judged by ear while it is being set. So there are four ways
    /// out and only one of them may keep the changes. Escape is the one that
    /// matters: it is how a screen-reader operator leaves any dialog, and the
    /// base class handles it privately, so a revert written into the Cancel
    /// button alone would leave the radio changed by the most-used exit in the
    /// app. The window's close button and a forced close have the same
    /// problem. OnClosing is downstream of all four.</para>
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_committed && _touched && _bandFields.Length > 0)
        {
            RevertToOriginals();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.eq.reverted"), VerbosityLevel.Terse);
        }

        base.OnClosing(e);
    }

    #endregion
}

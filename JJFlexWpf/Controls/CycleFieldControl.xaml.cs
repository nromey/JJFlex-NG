using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Controls;

/// <summary>
/// Focusable cycle control for ScreenFieldsPanel and the Audio Workshop.
/// Shows "Label: Option" text, cycles through options via Up/Down arrow keys.
///
/// Track Speech (2026-08-13): the control announces itself through the
/// accessibility tree, not through Speak() calls. The automation peer is a
/// UIA Spinner whose Name is the label and whose Value pattern carries the
/// current option; changing the option raises a Value property-changed
/// event, so the screen reader announces the new value natively — in the
/// operator's own voice, rate and verbosity, and on the braille display too.
/// The old interrupting Speak on focus talked over whatever else was being
/// announced (it cut off the Audio Workshop's group names the day they
/// shipped); nothing here can talk over anything now, because nothing here
/// talks. The "arrows to change" interaction hint lives in
/// AutomationProperties.HelpText, available on request rather than pushed.
/// </summary>
public partial class CycleFieldControl : UserControl
{
    private string _label = "";
    private string[] _options = Array.Empty<string>();
    private int _selectedIndex;
    private bool _suppressEvents;

    /// <summary>Fired when user cycles to a new option.</summary>
    public event EventHandler<int>? SelectionChanged;

    public CycleFieldControl()
    {
        InitializeComponent();
        // Interaction hint the tree cannot otherwise carry. HelpText is the
        // on-request home for it (NVDA: object description), not a forced
        // utterance on every focus.
        AutomationProperties.SetHelpText(this, "Arrows to change");
    }

    /// <summary>Human-readable label (e.g., "AGC Mode").</summary>
    public string Label
    {
        get => _label;
        set { _label = value; UpdateDisplay(); }
    }

    /// <summary>Available options to cycle through.</summary>
    public string[] Options
    {
        get => _options;
        set
        {
            string oldValue = SelectedOption;
            _options = value ?? Array.Empty<string>();
            UpdateDisplay();
            RaiseValueAutomationEvent(oldValue);
        }
    }

    /// <summary>Index of the currently selected option.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_options.Length == 0) return;
            int clamped = Math.Clamp(value, 0, _options.Length - 1);
            if (_selectedIndex == clamped) return;
            string oldValue = SelectedOption;
            _selectedIndex = clamped;
            UpdateDisplay();
            // Poll-driven updates raise the UIA event too: the screen reader
            // only announces value changes on the focused control, so an
            // unfocused background update stays silent while a change under
            // the operator's focus (another client moved the setting) is
            // honestly announced.
            RaiseValueAutomationEvent(oldValue);
        }
    }

    /// <summary>The currently selected option text.</summary>
    public string SelectedOption =>
        _options.Length > 0 && _selectedIndex < _options.Length
            ? _options[_selectedIndex]
            : "";

    /// <summary>
    /// Set to true during poll updates to suppress SelectionChanged events.
    /// (UIA value-changed events still fire — the accessibility tree stays
    /// truthful; screen readers ignore value changes on unfocused controls.)
    /// </summary>
    public bool SuppressEvents
    {
        get => _suppressEvents;
        set => _suppressEvents = value;
    }

    /// <summary>
    /// Configure all properties at once. Use during initialization.
    /// </summary>
    public void Setup(string label, string[] options, int initialIndex = 0)
    {
        string oldValue = SelectedOption;
        _label = label;
        _options = options ?? Array.Empty<string>();
        _selectedIndex = Math.Clamp(initialIndex, 0, Math.Max(0, _options.Length - 1));
        UpdateDisplay();
        RaiseValueAutomationEvent(oldValue);
    }

    /// <summary>
    /// Replace the option list at runtime (e.g., dynamic antenna lists from the radio).
    /// Resets selection to 0.
    /// </summary>
    public void SetOptions(string[] options)
    {
        string oldValue = SelectedOption;
        _options = options ?? Array.Empty<string>();
        _selectedIndex = 0;
        UpdateDisplay();
        RaiseValueAutomationEvent(oldValue);
    }

    /// <summary>
    /// Refresh the visual text and the accessible name. The name is the label
    /// alone — the current option rides the UIA Value pattern instead, so the
    /// screen reader composes "label, spin button, value" itself and there is
    /// exactly one source of truth for each piece.
    /// </summary>
    private void UpdateDisplay()
    {
        string optionText = SelectedOption;
        DisplayText.Text = string.IsNullOrEmpty(optionText) ? _label : $"{_label}: {optionText}";
        AutomationProperties.SetName(this, _label);
    }

    /// <summary>
    /// Raise the UIA Value property-changed event if the option text actually
    /// changed. This is what makes arrowing announce natively: the screen
    /// reader hears the event on the focused control and speaks the new value
    /// itself. No-op when no automation client ever asked for a peer.
    /// </summary>
    private void RaiseValueAutomationEvent(string oldValue)
    {
        string newValue = SelectedOption;
        if (oldValue == newValue) return;
        if (UIElementAutomationPeer.FromElement(this) is CycleFieldAutomationPeer peer)
            peer.RaisePropertyChangedEvent(
                ValuePatternIdentifiers.ValueProperty, oldValue, newValue);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_options.Length == 0) return;

        switch (e.Key)
        {
            case Key.Up:
                CycleForward();
                e.Handled = true;
                break;

            case Key.Down:
                CycleBackward();
                e.Handled = true;
                break;
        }
    }

    private void CycleForward()
    {
        if (_options.Length == 0) return;
        int newIndex = (_selectedIndex + 1) % _options.Length;
        SetIndex(newIndex);
    }

    private void CycleBackward()
    {
        if (_options.Length == 0) return;
        int newIndex = (_selectedIndex - 1 + _options.Length) % _options.Length;
        SetIndex(newIndex);
    }

    private void SetIndex(int newIndex)
    {
        if (newIndex == _selectedIndex) return;
        string oldValue = SelectedOption;
        _selectedIndex = newIndex;
        UpdateDisplay();
        // Announce first (the screen reader speaks the new value from this
        // event), then let handlers react — any speech a handler adds queues
        // after the value announcement instead of racing it.
        RaiseValueAutomationEvent(oldValue);

        if (!_suppressEvents)
            SelectionChanged?.Invoke(this, _selectedIndex);
    }

    /// <summary>
    /// UIA peer: a Spinner (screen readers say "spin button") exposing the
    /// current option through the Value pattern. Children are hidden so the
    /// display TextBlock is not read in addition to the Name/Value this peer
    /// reports.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new CycleFieldAutomationPeer(this);
    }

    private sealed class CycleFieldAutomationPeer : FrameworkElementAutomationPeer, IValueProvider
    {
        public CycleFieldAutomationPeer(CycleFieldControl owner) : base(owner) { }

        private CycleFieldControl Control => (CycleFieldControl)Owner;

        // Leaf peer: the child TextBlock's text duplicates Name + Value.
        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override AutomationControlType GetAutomationControlTypeCore()
            => AutomationControlType.Spinner;

        protected override string GetClassNameCore() => nameof(CycleFieldControl);

        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Value
                ? this
                : base.GetPattern(patternInterface);

        bool IValueProvider.IsReadOnly => false;

        string IValueProvider.Value => Control.SelectedOption;

        void IValueProvider.SetValue(string value)
        {
            var ctl = Control;
            for (int i = 0; i < ctl._options.Length; i++)
            {
                if (string.Equals(ctl._options[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    // Same path as a user keystroke so the rig follows the change.
                    ctl.SetIndex(i);
                    return;
                }
            }
            throw new ArgumentException(
                $"'{value}' is not one of the available options.", nameof(value));
        }
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        // Visual focus ring only. The screen reader reads name, role and
        // value from the tree on focus; speaking here would talk over
        // whatever is already being announced (group names, for one).
        OuterBorder.BorderBrush = System.Windows.SystemColors.HighlightBrush;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        OuterBorder.BorderBrush = System.Windows.Media.Brushes.Gray;
    }
}

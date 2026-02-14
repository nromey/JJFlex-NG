using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf;

/// <summary>
/// WPF UserControl for the log entry panel.
/// Thin view layer — the VB.NET LogPanel adapter calls these methods.
/// </summary>
public partial class LogEntryControl : UserControl
{
    private readonly ObservableCollection<RecentQsoRow> _recentQsos = new();
    private int _maxRecentQSOs = 20;

    /// <summary>
    /// Set the maximum number of QSOs shown in the Recent QSOs grid.
    /// Called from LogPanel when the operator's setting is loaded.
    /// </summary>
    public void SetMaxRecentQSOs(int count)
    {
        _maxRecentQSOs = Math.Max(5, count);
    }

    /// <summary>Raised when the Call Sign field loses focus.</summary>
    public event EventHandler? CallSignLeave;

    /// <summary>Raised when Enter is pressed in any field.</summary>
    public event EventHandler? EnterPressed;

    /// <summary>Raised when Escape is pressed in any field.</summary>
    public event EventHandler? EscapePressed;

    /// <summary>Raised when Ctrl+Shift+L is pressed (toggle Logging Mode).</summary>
    public event EventHandler? ToggleLoggingModePressed;

    /// <summary>Raised when Ctrl+Shift+M is pressed (toggle UI Mode).</summary>
    public event EventHandler? ToggleUIModePressed;

    public LogEntryControl()
    {
        InitializeComponent();
        RecentGrid.ItemsSource = _recentQsos;
    }

    #region Field Access

    public string GetFieldText(string fieldName)
    {
        return fieldName.ToUpperInvariant() switch
        {
            "CALL" => CallSignBox.Text,
            "RSTSENT" => RSTSentBox.Text,
            "RSTRCVD" => RSTRcvdBox.Text,
            "NAME" => NameBox.Text,
            "QTH" => QTHBox.Text,
            "STATE" => StateBox.Text,
            "GRID" => GridBox.Text,
            "COMMENTS" => CommentsBox.Text,
            "PREVIOUS" => PreviousContactBox.Text,
            _ => ""
        };
    }

    public void SetFieldText(string fieldName, string value)
    {
        switch (fieldName.ToUpperInvariant())
        {
            case "CALL": CallSignBox.Text = value; break;
            case "RSTSENT": RSTSentBox.Text = value; break;
            case "RSTRCVD": RSTRcvdBox.Text = value; break;
            case "NAME": NameBox.Text = value; break;
            case "QTH": QTHBox.Text = value; break;
            case "STATE": StateBox.Text = value; break;
            case "GRID": GridBox.Text = value; break;
            case "COMMENTS": CommentsBox.Text = value; break;
        }
    }

    public void ClearAllFields()
    {
        CallSignBox.Text = "";
        RSTSentBox.Text = "";
        RSTRcvdBox.Text = "";
        NameBox.Text = "";
        QTHBox.Text = "";
        StateBox.Text = "";
        GridBox.Text = "";
        CommentsBox.Text = "";
    }

    #endregion

    #region Info Labels

    public void SetFreqLabel(string text) => FreqLabel.Text = text;
    public void SetModeLabel(string text) => ModeLabel.Text = text;
    public void SetBandLabel(string text) => BandLabel.Text = text;
    public void SetDateTimeLabel(string text) => DateTimeLabel.Text = text;
    public void SetSerialLabel(string text) => SerialLabel.Text = text;
    public void SetDupLabel(string text) => DupLabel.Text = text;

    #endregion

    #region Previous Contact

    public void SetPreviousContact(string displayText, string accessibleName)
    {
        PreviousContactBox.Text = displayText;
        PreviousContactBox.SetValue(AutomationProperties.NameProperty, accessibleName);
    }

    public void ClearPreviousContact()
    {
        PreviousContactBox.Text = "";
        PreviousContactBox.SetValue(AutomationProperties.NameProperty, "Previous contact: none");
    }

    #endregion

    #region Grid Operations

    public void AddQsoRow(RecentQsoRow row)
    {
        if (_recentQsos.Count >= _maxRecentQSOs)
            _recentQsos.RemoveAt(0);
        _recentQsos.Add(row);
        ScrollToEnd();
    }

    public void ClearGrid()
    {
        _recentQsos.Clear();
    }

    public void ScrollToEnd()
    {
        if (_recentQsos.Count > 0)
            RecentGrid.ScrollIntoView(_recentQsos[^1]);
    }

    public void SetGridAccessibleName(string name)
    {
        RecentGrid.SetValue(AutomationProperties.NameProperty, name);
    }

    public int GridRowCount => _recentQsos.Count;

    #endregion

    #region Focus

    public void FocusCallSign()
    {
        CallSignBox.Focus();
        Keyboard.Focus(CallSignBox);
    }

    /// <summary>
    /// Clear WPF keyboard focus so that the hidden ElementHost stops intercepting
    /// keystrokes after the parent SplitContainer is hidden.
    /// </summary>
    public void ClearFocus()
    {
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
    }

    public void FocusField(string fieldName)
    {
        var tb = fieldName.ToUpperInvariant() switch
        {
            "CALL" => (UIElement)CallSignBox,
            "RSTSENT" => RSTSentBox,
            "RSTRCVD" => RSTRcvdBox,
            "NAME" => NameBox,
            "QTH" => QTHBox,
            "STATE" => StateBox,
            "GRID" => GridBox,
            "COMMENTS" => CommentsBox,
            "PREVIOUS" => PreviousContactBox,
            "RECENTGRID" => RecentGrid,
            _ => (UIElement)CallSignBox
        };
        tb.Focus();
        if (tb is TextBox textBox)
            Keyboard.Focus(textBox);
    }

    #endregion

    #region Event Handlers

    private void CallSignBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CallSignLeave?.Invoke(this, EventArgs.Empty);
    }

    private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Mode-switching combos must be forwarded explicitly because
        // ElementHost doesn't reliably propagate Ctrl+Shift+letter
        // back to the WinForms ProcessCmdKey chain.
        // WPF may report the actual key or Key.System for modifier combos,
        // so check both e.Key and e.SystemKey.
        var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (mods.HasFlag(ModifierKeys.Control) && mods.HasFlag(ModifierKeys.Shift))
        {
            if (actualKey == Key.L)
            {
                e.Handled = true;
                ToggleLoggingModePressed?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (actualKey == Key.M)
            {
                e.Handled = true;
                ToggleUIModePressed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            EnterPressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EscapePressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    #endregion
}

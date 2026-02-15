using System;
using System.IO.Ports;
using System.Windows;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for ComInfo (JJFlexControl/ComInfo.cs).
/// COM port picker dialog for external knob controller.
/// Lists available serial ports and lets user select one.
///
/// Sprint 9 Track B.
/// </summary>
public partial class ComInfoDialog : JJFlexDialog
{
    /// <summary>
    /// The currently selected port. Set before showing to pre-select;
    /// read after dialog closes with DialogResult=true.
    /// </summary>
    public string? ThePort { get; set; }

    public ComInfoDialog()
    {
        InitializeComponent();
        Loaded += ComInfoDialog_Loaded;
    }

    private void ComInfoDialog_Loaded(object sender, RoutedEventArgs e)
    {
        string[] ports = SerialPort.GetPortNames();
        foreach (string port in ports)
        {
            ComPortList.Items.Add(port);
        }

        // Pre-select if ThePort was set
        if (!string.IsNullOrEmpty(ThePort))
        {
            for (int i = 0; i < ComPortList.Items.Count; i++)
            {
                if ((string)ComPortList.Items[i] == ThePort)
                {
                    ComPortList.SelectedIndex = i;
                    break;
                }
            }
        }

        ComPortList.Focus();
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ComPortList.SelectedIndex == -1)
        {
            MessageBox.Show("You must select a port", "Try again",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ComPortList.Focus();
            return;
        }

        ThePort = (string)ComPortList.Items[ComPortList.SelectedIndex];
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using JJTrace;

namespace JJFlexControl
{
    /// <summary>
    /// Get the com port to use.
    /// ThePort value is set if DialogResult.OK.
    /// </summary>
    public partial class ComInfo : Form
    {
        private static string mustSelect => Radios.Lexicon.Get("settings.com_port.must_select");
        private static string directive => Radios.Lexicon.Get("settings.com_port.try_again_title");
        private bool wasActive;

        public string ThePort = null;

        public ComInfo()
        {
            InitializeComponent();
        }

        private void ComInfo_Load(object sender, EventArgs e)
        {
            wasActive = false;
            processSelection = false;
            ComPortList.Items.AddRange(SerialPort.GetPortNames());

            // If ThePort specified, select it.
            ComPortList.SelectedIndex = -1;
            if (!string.IsNullOrEmpty(ThePort))
            {
                for(int i=0;i<ComPortList.Items.Count;i++)
                {
                    string port = (string)ComPortList.Items[i];
                    if (ThePort == port)
                    {
                        ComPortList.SelectedIndex = i;
                        break;
                    }
                }
            }

            DialogResult = DialogResult.None;
            // Start processing user selections.
            processSelection = true;
        }

        private void ComInfo_Activated(object sender, EventArgs e)
        {
            if (!wasActive)
            {
                wasActive = true;
                ComPortList.Focus();
            }
        }

        private bool processSelection;
        private void SelectButton_Click(object sender, EventArgs e)
        {
            if (!processSelection) return;

            if (ComPortList.SelectedIndex == -1)
            {
                MessageBox.Show(mustSelect, directive, MessageBoxButtons.OK);
                ComPortList.Focus();
                return;
            }

            ThePort = (string)ComPortList.Items[ComPortList.SelectedIndex];
            DialogResult = DialogResult.OK;
        }

        private void CnclButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
        }
    }
}

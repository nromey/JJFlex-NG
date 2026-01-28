using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using JJTrace;

namespace Radios
{
    public partial class UserEnteredRemoteRigInfo : Form
    {
        private const string invalidEntry = "Invalid entry";
        private const string mustHaveNickname = "You must provide a nickname.";
        private const string mustHaveModel = "You must provide a model.";
        private const string mustHaveSerial = "You must provide a serial#.";
        internal AllRadios.RadioDiscoveredEventArgs Arg;
        private bool wasEntered;

        public UserEnteredRemoteRigInfo(AllRadios.RadioDiscoveredEventArgs oldInfo = null)
        {
            InitializeComponent();

            wasEntered = false;
            Arg = null;
            if (oldInfo != null)
            {
                // Copy in the new info.
                Arg = new AllRadios.RadioDiscoveredEventArgs(oldInfo.Name, oldInfo.Model, oldInfo.Serial);
                NicknameBox.Text = oldInfo.Name;
                ModelBox.Text = oldInfo.Model;
                SerialBox.Text = oldInfo.Serial;
            }
        }

        private void DoneButton_Click(object sender, EventArgs e)
        {
            if (NicknameBox.Text == "")
            {
                MessageBox.Show(mustHaveNickname, invalidEntry, MessageBoxButtons.OK);
                NicknameBox.Focus();
                return;
            }
            if (ModelBox.Text == "")
            {
                MessageBox.Show(mustHaveModel, invalidEntry, MessageBoxButtons.OK);
                ModelBox.Focus();
                return;
            }
            if (SerialBox.Text == "")
            {
                MessageBox.Show(mustHaveSerial, invalidEntry, MessageBoxButtons.OK);
                SerialBox.Focus();
                return;
            }

            Arg = new AllRadios.RadioDiscoveredEventArgs(NicknameBox.Text, ModelBox.Text, SerialBox.Text);
            DialogResult = DialogResult.OK;
        }

        private void CnclButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
        }

        private void UserEnteredRemoteRigInfo_Activated(object sender, EventArgs e)
        {
            if (!wasEntered)
            {
                wasEntered = true;
                NicknameBox.Focus();
            }
        }
    }
}

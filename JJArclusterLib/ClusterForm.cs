using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Escapes;
using JJMinimalTelnet;
using JJTrace;
using MsgLib;

namespace JJArClusterLib
{
    public partial class ClusterForm : Form
    {
        private const string mustHaveLogin = "No cluster address or login name specified.";
        private const string loginFailed = "The login to {0}, login name {1}, failed.";
        private const string loginHdr = "Login";
        private TelnetSession tc;
        private bool closing;
        private Thread displayThread;
        private bool connected
        {
            get { return ((tc != null) && tc.IsConnected); }
        }

        public enum BeepType
        {
            Next=-1,
            Off=0,
            On,
            DXOnly
        }
        private string[] beepText =
        {
            "Beep On",
            "Beep On DX",
            "Beep Off"
        };
        private BeepType _beepSetting;
        private BeepType beepSetting
        {
            get { return _beepSetting; }
            set
            {
                if (value == BeepType.Next)
                {
                    _beepSetting = (BeepType)(((int)_beepSetting + 1) % (Enum.GetNames(typeof(BeepType)).Length - 1));
                }
                else
                {
                    _beepSetting = value;
                }
                BeepButton.Text = beepText[(int)_beepSetting];
            }
        }

        private const string trackLastPostOffText = "Track last post Off";
        private const string trackLastPostOnText = "Track last post On";
        private bool _trackOn;
        private bool trackOn
        {
            get { return _trackOn; }
            set
            {
                _trackOn = value;
                TrackButton.Text = (value) ? trackLastPostOffText : trackLastPostOnText;
            }
        }

        /// <summary>
        /// Cluster's address
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string ClusterAddress { get; set; }
        public string ClusterHostname
        {
            get
            {
                int id = ClusterAddress.IndexOf(':');
                return (id == -1) ?
                ClusterAddress :
                ClusterAddress.Substring(0, id);
            }
        }
        public int ClusterPort
        {
            get
            {
                int id = ClusterAddress.IndexOf(':')+1;
                int port = 23;
                if ((id >= 2) & (id < ClusterAddress.Length))
                {
                    if (!System.Int32.TryParse(ClusterAddress.Substring(id), out port))
                    {
                        port = 23;
                    }
                }
                return port;
            }
        }

        /// <summary>
        /// Login name, set initially to login automatically.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string LoginName { get; set; }

        /// <summary>
        /// Cluster login event complete argument
        /// </summary>
        public class ClusterLoginArg
        {
            public string Message;
            public bool Error;
            public ClusterLoginArg(string m, bool e)
            {
                Message = m;
                Error = e;
            }
        }
        public delegate void ClusterLoginDel(ClusterLoginArg e);
        /// <summary>
        /// Cluster login complete event.
        /// </summary>
        public event ClusterLoginDel ClusterLoginEvent;
        private void onClusterLogin(string msg, bool err)
        {
            if (ClusterLoginEvent != null)
            {
                Tracing.TraceLine("onClusterLogin:error=" + err.ToString(), TraceLevel.Info);
                ClusterLoginEvent(new ClusterLoginArg(msg, err));
            }
            else
            {
                Tracing.TraceLine("onClusterLogin:no event:"+ err.ToString() + ' ' + msg, TraceLevel.Error);
            }
        }

        /// <summary>
        /// Beep change interrupt argument
        /// </summary>
        public class BeepChangeArg
        {
            public BeepType BeepSetting;
            public BeepChangeArg(BeepType b)
            {
                BeepSetting = b;
            }
        }
        public delegate void BeepChangeDel(BeepChangeArg arg);
        /// <summary>
        /// Beep change event
        /// </summary>
        public event BeepChangeDel BeepChangeEvent;
        private void onBeepChange(BeepType b)
        {
            if (BeepChangeEvent != null)
            {
                BeepChangeEvent(new BeepChangeArg(b));
            }
        }

        /// <summary>
        /// Track change interrupt argument
        /// </summary>
        public class TrackChangeArg
        {
            public bool TrackOn;
            public TrackChangeArg(bool t)
            {
                TrackOn = t;
            }
        }
        public delegate void TrackChangeDel(TrackChangeArg arg);
        /// <summary>
        /// Track change event
        /// </summary>
        public event TrackChangeDel TrackChangeEvent;
        private void onTrackChange(bool t)
        {
            if (TrackChangeEvent != null)
            {
                TrackChangeEvent(new TrackChangeArg(t));
            }
        }

        /// <summary>
        /// Spot information argument
        /// </summary>
        public class SpotInfoArg
        {
            public double Frequency;
            public string Callsign;
            public SpotInfoArg(double f, string call)
            {
                Frequency = f;
                Callsign = call;
            }
        }
        public delegate void SpotInfoDel(SpotInfoArg e);
        /// <summary>
        /// Spot info event
        /// </summary>
        public event SpotInfoDel SpotInfoEvent;
        private void onSpotInfo(string line)
        {
            if (SpotInfoEvent == null)
            {
                Tracing.TraceLine("onSpotInfo:event not defined", TraceLevel.Error);
                return;
            }
            // Get the frequency
            string[] words = line.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 3)
            {
                Tracing.TraceLine("onSpotInfo:invalid spot", TraceLevel.Error);
                return;
            }
            double f = 0;
            if (!System.Double.TryParse(words[0], out f))
            {
                Tracing.TraceLine("onSpotInfo:invalid spot", TraceLevel.Error);
                return;
            }

            Tracing.TraceLine("onSpotInfo:" + f.ToString() + ' ' + words[1], TraceLevel.Info);
            SpotInfoEvent(new SpotInfoArg(f, words[1]));
        }

        public delegate void ClusterClosedDel(object sender);
        public event ClusterClosedDel ClusterClosedEvent;
        private void onClusterClosed()
        {
            if (ClusterClosedEvent != null)
            {
                Tracing.TraceLine("ClusterClosedEvent", TraceLevel.Info);
                ClusterClosedEvent(this);
            }
            else Tracing.TraceLine("ClusterClosedEvent not defined", TraceLevel.Error);
        }

        /// <summary>
        /// Cluster form
        /// </summary>
        /// <param name="h">string hostname</param>
        /// <param name="l">string login (may be null)</param>
        /// <param name="beep">true if beeping is to be on</param>
        /// <param name="track">True if tracking last line of text</param>
        public ClusterForm(string h, string l, BeepType beep, bool track)
        {
            InitializeComponent();

            ClusterAddress = h;
            LoginName = l;

            displayThread = new Thread(displayProc);
            displayThread.Name = "DisplayThread";
            displayThread.Start();
            beepSetting = beep;
            trackOn = track;
        }

        private Thread loginThread;
        public void Login()
        {
            loginThread = new Thread(loginProc);
            loginThread.Name = "loginThread";
            loginThread.Start();
        }

        public void LoginCancel()
        {
            try
            {
                if ((loginThread != null) && loginThread.IsAlive) loginThread.Abort();
            }
            catch { }
        }

        private void ClusterForm_Load(object sender, EventArgs e)
        {
        }

        private void CommandBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Return)
            {
                tc.WriteLine(CommandBox.Text);
                CommandBox.Text = "";
                e.Handled = true;
                OutputBox.Focus();
            }
        }

        private void loginProc()
        {
            // Incase of abort.
            try
            {
                OutputBox.Text = "";
                try
                {
                    tc = new TelnetSession(ClusterHostname, ClusterPort);
                }
                catch (Exception ex)
                {
                    onClusterLogin(ex.Message, true);
                    return;
                }

                string err = "";
                tc.StringEvent += readHandler;
                if (!tc.Login(LoginName, "", 5000, out err))
                {
                    onClusterLogin(err, true);
                    return;
                }

                // Indicate error only if not connected.
                onClusterLogin((connected) ? "Connected" : "Not connected", !connected);
            }
            catch (ThreadAbortException) { }
        }

        private void BeepButton_Click(object sender, EventArgs e)
        {
            beepSetting = BeepType.Next;
            onBeepChange(beepSetting);
        }

        private void TrackButton_Click(object sender, EventArgs e)
        {
            trackOn = !trackOn;
            onTrackChange(trackOn);
        }

        private Queue<List<string>> q;

        private void readHandler()
        {
            q.Enqueue(tc.Read());
        }

        private const int highWater = 256 * 1024;
        private const int lowWater = 64 * 1024;
        private void displayProc()
        {
            q = new Queue<List<string>>();
            int pos = 0;
            int nlLen = Environment.NewLine.Length;
            while (!closing)
            {
                if (q.Count == 0)
                {
                    Thread.Sleep(25);
                    continue;
                }
                // Get restore position if not tracking.
                if (!trackOn) MsgLib.TextOut.PerformGenericFunction(OutputBox, () => { pos = OutputBox.SelectionStart; });
                while (!closing && (q.Count > 0))
                {
                    List<string> buf = q.Dequeue();
                    foreach (string txt in buf)
                    {
                        // Get restore position if tracking.
                        if (trackOn) MsgLib.TextOut.PerformGenericFunction(OutputBox, () => { pos = OutputBox.Text.Length; });
                        if ((txt.Length > 5) && (txt.Substring(0, 5) == "DX de")) showDX(txt);
                        else MsgLib.TextOut.DisplayText(OutputBox, txt, false);
                    }
                    // restore to prior position
                    MsgLib.TextOut.PerformGenericFunction(OutputBox, () =>
                    {
                        OutputBox.SelectionStart = pos;
                        OutputBox.ScrollToCaret();
                    });
                    if (beepSetting == BeepType.On) Console.Beep();

                    // See if need to purge excess text.
                    int len = 0;
                    MsgLib.TextOut.PerformGenericFunction(OutputBox, () => { len = OutputBox.Text.Length; });
                    if (len >= highWater)
                    {
                        Tracing.TraceLine("displayProc purging:" + len.ToString() + ' ' + pos.ToString(), TraceLevel.Info);
                        MsgLib.TextOut.PerformGenericFunction(OutputBox, () =>
                        {
                            // Find end-of-line.
                            int id = highWater - lowWater;
                            try
                            {
                                while (OutputBox.Text.Substring(id, nlLen) != Environment.NewLine) { id++; }
                                id += nlLen;
                                OutputBox.Text = OutputBox.Text.Substring(id);
                                OutputBox.SelectionLength = 0;
                                // readjust cursor.
                                if (pos < id)
                                {
                                    OutputBox.SelectionStart = 0;
                                }
                                else
                                {
                                    OutputBox.SelectionStart = pos - id;
                                }
                            }
                            catch (Exception ex)
                            {
                                Tracing.TraceLine("displayProc purge error:" + ex.Message, TraceLevel.Error);
                            }
                        });
                    }
                }
            }
        }
        private void showDX(string line)
        {
            string[] words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            // Input:  DX de reporter: freq dx-station comments time
            // output: freq dx-station comments time reporter
            string buf = words[3] + ' ' + words[4] + ' '; // freq and dx-call
            if (words.Length > 6)
            {
                // comments
                for (int i = 5; i < words.Length - 1; i++)
                {
                    buf += words[i] + ' ';
                }
            }
            buf += words[words.Length - 1] + ' '; // time
            // reporting station
            if (words[2].Length > 1)
            {
                buf += words[2].Substring(0, words[2].Length - 1);
            }
            buf += Environment.NewLine;
            MsgLib.TextOut.DisplayText(OutputBox, buf, false);
            if (beepSetting == BeepType.DXOnly) Console.Beep();
        }

        private void ClusterForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Tracing.TraceLine("ClusterForm closing", TraceLevel.Info);
            if (tc != null) tc.Close();
            closing = true;
            if (displayThread != null)
            {
                // Give it up to a second to close.
                int sanity = 40;
                while ((sanity-- > 0) & (displayThread.IsAlive)) { Thread.Sleep(25); }
                if (sanity == 0) Tracing.TraceLine("ClusterForm closing time exceeded", TraceLevel.Error);
            }
            Tracing.TraceLine("ClusterForm closing done", TraceLevel.Info);
        }

        private void ClusterForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            onClusterClosed();
        }

        private void OutputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Return)
            {
                e.Handled = true;
                string line = null;
                MsgLib.TextOut.PerformGenericFunction(OutputBox, () =>
                {
                    int pos = OutputBox.GetFirstCharIndexFromLine(OutputBox.GetLineFromCharIndex(OutputBox.SelectionStart));
                    int id = OutputBox.Text.Substring(pos).IndexOf(Environment.NewLine);
                    if (id != -1) line = OutputBox.Text.Substring(pos, id);
                });
                if (line != null) onSpotInfo(line);
            }
        }
    }
}

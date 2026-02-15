using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// DX spot info passed when user selects a spot line.
    /// </summary>
    public class SpotInfo
    {
        public double Frequency { get; set; }
        public string CallSign { get; set; } = "";
    }

    public enum BeepMode
    {
        Off = 0,
        On = 1,
        DXOnly = 2
    }

    public partial class ClusterDialog : JJFlexDialog
    {
        private static readonly string[] BeepText = { "Beep On", "Beep On DX", "Beep Off" };
        private const int HighWater = 256 * 1024;
        private const int LowWater = 64 * 1024;

        private readonly ConcurrentQueue<List<string>> _queue = new();
        private readonly DispatcherTimer _displayTimer;
        private bool _closing;
        private BeepMode _beepMode;
        private bool _trackOn;

        // --- Delegates for external wiring ---

        /// <summary>Called to connect to the cluster. Receives no args.</summary>
        public Action? Connect { get; set; }

        /// <summary>Called to disconnect from the cluster.</summary>
        public Action? Disconnect { get; set; }

        /// <summary>Called to send a telnet command. Receives command text.</summary>
        public Action<string>? SendCommand { get; set; }

        /// <summary>Called when the user selects a DX spot (Enter on a spot line).</summary>
        public Action<SpotInfo>? OnSpotSelected { get; set; }

        /// <summary>Called when beep mode changes.</summary>
        public Action<BeepMode>? OnBeepChanged { get; set; }

        /// <summary>Called when track mode changes.</summary>
        public Action<bool>? OnTrackChanged { get; set; }

        /// <summary>Called when the cluster form is closed.</summary>
        public Action? OnClusterClosed { get; set; }

        /// <summary>Optional trace delegate.</summary>
        public Action<string>? TraceAction { get; set; }

        /// <summary>Initial beep mode setting.</summary>
        public BeepMode InitialBeepMode { get; set; } = BeepMode.Off;

        /// <summary>Initial track setting.</summary>
        public bool InitialTrackOn { get; set; }

        public ClusterDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;

            // Use a DispatcherTimer to pump the display queue on the UI thread
            _displayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _displayTimer.Tick += DisplayTimer_Tick;

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SetBeepMode(InitialBeepMode);
            SetTrackMode(InitialTrackOn);
            _displayTimer.Start();
            Connect?.Invoke();
        }

        // --- Public methods for external code to push data ---

        /// <summary>
        /// Called by external telnet handler when data arrives.
        /// Thread-safe — can be called from any thread.
        /// </summary>
        public void EnqueueData(List<string> lines)
        {
            if (_closing) return;
            _queue.Enqueue(lines);
        }

        /// <summary>
        /// Called by external code to display a status message.
        /// Thread-safe.
        /// </summary>
        public void DisplayStatus(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                AppendText(message + Environment.NewLine);
            });
        }

        // --- Display pump ---

        private void DisplayTimer_Tick(object? sender, EventArgs e)
        {
            if (_closing || _queue.IsEmpty) return;

            int cursorPos = _trackOn ? OutputBox.Text.Length : OutputBox.SelectionStart;

            while (_queue.TryDequeue(out var lines))
            {
                foreach (string txt in lines)
                {
                    if (txt.Length > 5 && txt.StartsWith("DX de", StringComparison.Ordinal))
                    {
                        ShowDX(txt);
                        if (_beepMode == BeepMode.DXOnly) Console.Beep();
                    }
                    else
                    {
                        AppendText(txt + Environment.NewLine);
                    }
                }

                if (_beepMode == BeepMode.On) Console.Beep();
            }

            // Memory management: purge excess text
            if (OutputBox.Text.Length >= HighWater)
            {
                int removeAt = OutputBox.Text.Length - (HighWater - LowWater);
                int newlinePos = OutputBox.Text.IndexOf('\n', removeAt);
                if (newlinePos > 0)
                {
                    OutputBox.Text = OutputBox.Text.Substring(newlinePos + 1);
                    cursorPos = Math.Max(0, cursorPos - (newlinePos + 1));
                }
            }

            // Restore cursor position
            if (_trackOn)
            {
                OutputBox.SelectionStart = OutputBox.Text.Length;
                OutputBox.ScrollToEnd();
            }
            else
            {
                OutputBox.SelectionStart = Math.Min(cursorPos, OutputBox.Text.Length);
            }
        }

        private void AppendText(string text)
        {
            OutputBox.AppendText(text);
        }

        /// <summary>
        /// Parse and reformat a DX spot line.
        /// Input:  "DX de reporter: freq dx-station comments time"
        /// Output: "freq dx-station comments time reporter"
        /// </summary>
        private void ShowDX(string line)
        {
            string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 5) { AppendText(line + Environment.NewLine); return; }

            string buf = words[3] + ' ' + words[4] + ' ';
            for (int i = 5; i < words.Length - 1; i++)
                buf += words[i] + ' ';
            buf += words[words.Length - 1] + ' ';

            // Reporter callsign (remove trailing colon)
            if (words[2].Length > 1)
                buf += words[2].Substring(0, words[2].Length - 1);

            AppendText(buf + Environment.NewLine);
        }

        // --- Beep/Track toggles ---

        private void SetBeepMode(BeepMode mode)
        {
            _beepMode = mode;
            BeepButton.Content = BeepText[(int)_beepMode];
        }

        private void SetTrackMode(bool on)
        {
            _trackOn = on;
            TrackButton.Content = on ? "Track last post Off" : "Track last post On";
        }

        private void BeepButton_Click(object sender, RoutedEventArgs e)
        {
            var next = (BeepMode)(((int)_beepMode + 1) % 3);
            SetBeepMode(next);
            OnBeepChanged?.Invoke(_beepMode);
        }

        private void TrackButton_Click(object sender, RoutedEventArgs e)
        {
            SetTrackMode(!_trackOn);
            OnTrackChanged?.Invoke(_trackOn);
        }

        // --- Command input ---

        private void CommandBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string cmd = CommandBox.Text.Trim();
                if (!string.IsNullOrEmpty(cmd))
                {
                    SendCommand?.Invoke(cmd);
                    CommandBox.Clear();
                }
                e.Handled = true;
            }
        }

        // --- Spot selection ---

        private void OutputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExtractSpotInfo();
                e.Handled = true;
            }
        }

        private void ExtractSpotInfo()
        {
            int lineIndex = OutputBox.GetLineIndexFromCharacterIndex(OutputBox.SelectionStart);
            if (lineIndex < 0) return;
            string? line = OutputBox.GetLineText(lineIndex);
            if (string.IsNullOrWhiteSpace(line)) return;

            string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return;
            if (!double.TryParse(words[0], out double freq)) return;

            OnSpotSelected?.Invoke(new SpotInfo { Frequency = freq, CallSign = words[1] });
        }

        // --- Cleanup ---

        private void OnFormClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _closing = true;
            _displayTimer.Stop();
            Disconnect?.Invoke();
            OnClusterClosed?.Invoke();
        }
    }
}

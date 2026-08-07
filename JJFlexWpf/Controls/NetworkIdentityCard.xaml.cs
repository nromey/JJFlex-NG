#nullable enable

using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Controls
{
    /// <summary>
    /// QB Track D (item 5) — reusable network identity card, read side.
    ///
    /// Shows who the radio is and how this computer reaches it: model,
    /// serial, firmware, LAN or SmartLink public address, forwarded-port or
    /// hole-punch path with the verbatim router rule, and the most recent
    /// SmartLink reachability test. Every line is a plain sentence in a
    /// ListBox — Tab reaches the card, arrows read it line by line.
    ///
    /// Hosts: the Status dialog (this track). Built for the radio picker
    /// detail area too — that surface is Track E's; drop this control in
    /// and call <see cref="Refresh"/>, nothing else needed.
    ///
    /// Read-only by design: this card never triggers probes or commands.
    /// The reachability line reads the session's cached test_connection
    /// report only, so the card is safe to open on a hole-punched session
    /// (a radio-side probe there can kill the live connection).
    /// </summary>
    public partial class NetworkIdentityCard : UserControl
    {
        private FlexBase? _rig;

        /// <summary>
        /// The radio to describe. Setting this refreshes the card. Null or
        /// disconnected is a normal state — the card says so instead of
        /// going blank.
        /// </summary>
        public FlexBase? Rig
        {
            get => _rig;
            set { _rig = value; Refresh(); }
        }

        public NetworkIdentityCard()
        {
            InitializeComponent();
            Refresh();
        }

        /// <summary>
        /// Rebuild the lines from current radio state. Safe to call on a
        /// timer; a rebuild is skipped while the user is arrowing through
        /// the list so the refresh never steals their place.
        /// </summary>
        public void Refresh()
        {
            if (IdentityList == null) return; // before InitializeComponent completes
            if (IdentityList.IsKeyboardFocusWithin) return;

            int savedIndex = IdentityList.SelectedIndex;
            IdentityList.Items.Clear();
            foreach (string line in NetworkIdentityInfo.BuildLines(_rig))
            {
                IdentityList.Items.Add(line);
            }
            if (savedIndex >= 0 && savedIndex < IdentityList.Items.Count)
                IdentityList.SelectedIndex = savedIndex;
        }
    }
}

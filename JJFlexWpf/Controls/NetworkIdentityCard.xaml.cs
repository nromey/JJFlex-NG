#nullable enable

using System;
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
    /// read-only TextBox — Tab reaches the card, arrows read it line by
    /// line, and text can be selected and copied. (Converted from ListBox
    /// in Phase 0.5d, 2026-08-10: prose in a list announces "item 1 of N"
    /// and arrow keys pretend to change a selection.)
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
        /// timer; a rebuild is skipped while the user's focus is inside the
        /// card so the refresh never steals their reading position — that
        /// guard is why arrowing through the card survives a refresh, and
        /// it must outlive any future control-type change.
        /// </summary>
        public void Refresh()
        {
            if (IdentityText == null) return; // before InitializeComponent completes
            if (IdentityText.IsKeyboardFocusWithin) return;

            IdentityText.Text = string.Join(Environment.NewLine,
                NetworkIdentityInfo.BuildLines(_rig));
        }
    }
}

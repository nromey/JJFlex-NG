namespace Radios
{
    partial class EscDialog
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.CheckBox escEnabledCheckBox;
        private System.Windows.Forms.Label phaseLabel;
        private System.Windows.Forms.TrackBar phaseTrackBar;
        private System.Windows.Forms.TextBox phaseValueTextBox;
        private System.Windows.Forms.Label phaseSuffixLabel;
        private System.Windows.Forms.Button preset90Button;
        private System.Windows.Forms.Button preset180Button;
        private System.Windows.Forms.Label gainLabel;
        private System.Windows.Forms.TrackBar gainTrackBar;
        private System.Windows.Forms.TextBox gainValueTextBox;
        private System.Windows.Forms.Label gainSuffixLabel;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.Button closeButton;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                components?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.escEnabledCheckBox = new System.Windows.Forms.CheckBox();
            this.phaseLabel = new System.Windows.Forms.Label();
            this.phaseTrackBar = new System.Windows.Forms.TrackBar();
            this.phaseValueTextBox = new System.Windows.Forms.TextBox();
            this.phaseSuffixLabel = new System.Windows.Forms.Label();
            this.preset90Button = new System.Windows.Forms.Button();
            this.preset180Button = new System.Windows.Forms.Button();
            this.gainLabel = new System.Windows.Forms.Label();
            this.gainTrackBar = new System.Windows.Forms.TrackBar();
            this.gainValueTextBox = new System.Windows.Forms.TextBox();
            this.gainSuffixLabel = new System.Windows.Forms.Label();
            this.statusLabel = new System.Windows.Forms.Label();
            this.closeButton = new System.Windows.Forms.Button();
            ((System.ComponentModel.ISupportInitialize)(this.phaseTrackBar)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.gainTrackBar)).BeginInit();
            this.SuspendLayout();
            //
            // escEnabledCheckBox
            //
            this.escEnabledCheckBox.AutoSize = true;
            this.escEnabledCheckBox.Location = new System.Drawing.Point(12, 12);
            this.escEnabledCheckBox.Name = "escEnabledCheckBox";
            this.escEnabledCheckBox.Size = new System.Drawing.Size(199, 17);
            this.escEnabledCheckBox.TabIndex = 0;
            this.escEnabledCheckBox.Text = "Enhanced Signal Clarity (ESC) enabled";
            this.escEnabledCheckBox.UseVisualStyleBackColor = true;
            this.escEnabledCheckBox.CheckedChanged += new System.EventHandler(this.EscEnabledCheckBox_CheckedChanged);
            //
            // phaseLabel
            //
            this.phaseLabel.AutoSize = true;
            this.phaseLabel.Location = new System.Drawing.Point(12, 39);
            this.phaseLabel.Name = "phaseLabel";
            this.phaseLabel.Size = new System.Drawing.Size(79, 13);
            this.phaseLabel.TabIndex = 1;
            this.phaseLabel.Text = "Phase shift (°):";
            //
            // phaseTrackBar
            //
            this.phaseTrackBar.Location = new System.Drawing.Point(12, 55);
            this.phaseTrackBar.Maximum = 360;
            this.phaseTrackBar.Name = "phaseTrackBar";
            this.phaseTrackBar.Size = new System.Drawing.Size(336, 45);
            this.phaseTrackBar.SmallChange = 1;
            this.phaseTrackBar.LargeChange = 10;
            this.phaseTrackBar.TabIndex = 2;
            this.phaseTrackBar.TickFrequency = 30;
            this.phaseTrackBar.TickStyle = System.Windows.Forms.TickStyle.BottomRight;
            this.phaseTrackBar.Scroll += new System.EventHandler(this.PhaseTrackBar_Scroll);
            //
            // phaseValueTextBox
            //
            this.phaseValueTextBox.Location = new System.Drawing.Point(12, 110);
            this.phaseValueTextBox.Name = "phaseValueTextBox";
            this.phaseValueTextBox.Size = new System.Drawing.Size(96, 20);
            this.phaseValueTextBox.TabIndex = 3;
            this.phaseValueTextBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.PhaseValueTextBox_KeyDown);
            this.phaseValueTextBox.Leave += new System.EventHandler(this.PhaseValueTextBox_Leave);
            //
            // phaseSuffixLabel
            //
            this.phaseSuffixLabel.AutoSize = true;
            this.phaseSuffixLabel.Location = new System.Drawing.Point(114, 113);
            this.phaseSuffixLabel.Name = "phaseSuffixLabel";
            this.phaseSuffixLabel.Size = new System.Drawing.Size(45, 13);
            this.phaseSuffixLabel.TabIndex = 4;
            this.phaseSuffixLabel.Text = "degrees";
            //
            // preset90Button
            //
            this.preset90Button.Location = new System.Drawing.Point(12, 140);
            this.preset90Button.Name = "preset90Button";
            this.preset90Button.Size = new System.Drawing.Size(60, 26);
            this.preset90Button.TabIndex = 5;
            this.preset90Button.Text = "90°";
            this.preset90Button.UseVisualStyleBackColor = true;
            this.preset90Button.Click += new System.EventHandler(this.Preset90Button_Click);
            //
            // preset180Button
            //
            this.preset180Button.Location = new System.Drawing.Point(78, 140);
            this.preset180Button.Name = "preset180Button";
            this.preset180Button.Size = new System.Drawing.Size(60, 26);
            this.preset180Button.TabIndex = 6;
            this.preset180Button.Text = "180°";
            this.preset180Button.UseVisualStyleBackColor = true;
            this.preset180Button.Click += new System.EventHandler(this.Preset180Button_Click);
            //
            // gainLabel
            //
            this.gainLabel.AutoSize = true;
            this.gainLabel.Location = new System.Drawing.Point(12, 175);
            this.gainLabel.Name = "gainLabel";
            this.gainLabel.Size = new System.Drawing.Size(65, 13);
            this.gainLabel.TabIndex = 7;
            this.gainLabel.Text = "ESC gain:";
            //
            // gainTrackBar
            //
            this.gainTrackBar.Location = new System.Drawing.Point(12, 191);
            this.gainTrackBar.Maximum = 80;
            this.gainTrackBar.Name = "gainTrackBar";
            this.gainTrackBar.Size = new System.Drawing.Size(336, 45);
            this.gainTrackBar.SmallChange = 1;
            this.gainTrackBar.LargeChange = 5;
            this.gainTrackBar.TabIndex = 8;
            this.gainTrackBar.TickFrequency = 8;
            this.gainTrackBar.TickStyle = System.Windows.Forms.TickStyle.BottomRight;
            this.gainTrackBar.Scroll += new System.EventHandler(this.GainTrackBar_Scroll);
            //
            // gainValueTextBox
            //
            this.gainValueTextBox.Location = new System.Drawing.Point(12, 242);
            this.gainValueTextBox.Name = "gainValueTextBox";
            this.gainValueTextBox.Size = new System.Drawing.Size(96, 20);
            this.gainValueTextBox.TabIndex = 9;
            this.gainValueTextBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.GainValueTextBox_KeyDown);
            this.gainValueTextBox.Leave += new System.EventHandler(this.GainValueTextBox_Leave);
            //
            // gainSuffixLabel
            //
            this.gainSuffixLabel.AutoSize = true;
            this.gainSuffixLabel.Location = new System.Drawing.Point(114, 245);
            this.gainSuffixLabel.Name = "gainSuffixLabel";
            this.gainSuffixLabel.Size = new System.Drawing.Size(30, 13);
            this.gainSuffixLabel.TabIndex = 10;
            this.gainSuffixLabel.Text = "gain";
            //
            // statusLabel
            //
            this.statusLabel.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.statusLabel.Location = new System.Drawing.Point(12, 272);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(336, 32);
            this.statusLabel.TabIndex = 11;
            this.statusLabel.Text = "status";
            this.statusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // closeButton
            //
            this.closeButton.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.closeButton.Location = new System.Drawing.Point(260, 310);
            this.closeButton.Name = "closeButton";
            this.closeButton.Size = new System.Drawing.Size(88, 26);
            this.closeButton.TabIndex = 12;
            this.closeButton.Text = "Close";
            this.closeButton.UseVisualStyleBackColor = true;
            //
            // EscDialog
            //
            this.AcceptButton = this.closeButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.closeButton;
            this.ClientSize = new System.Drawing.Size(360, 348);
            this.Controls.Add(this.closeButton);
            this.Controls.Add(this.statusLabel);
            this.Controls.Add(this.gainSuffixLabel);
            this.Controls.Add(this.gainValueTextBox);
            this.Controls.Add(this.gainTrackBar);
            this.Controls.Add(this.gainLabel);
            this.Controls.Add(this.preset180Button);
            this.Controls.Add(this.preset90Button);
            this.Controls.Add(this.phaseSuffixLabel);
            this.Controls.Add(this.phaseValueTextBox);
            this.Controls.Add(this.phaseTrackBar);
            this.Controls.Add(this.phaseLabel);
            this.Controls.Add(this.escEnabledCheckBox);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "EscDialog";
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "ESC Controls";
            ((System.ComponentModel.ISupportInitialize)(this.phaseTrackBar)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.gainTrackBar)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}

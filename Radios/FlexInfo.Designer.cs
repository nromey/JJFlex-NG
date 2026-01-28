namespace Radios
{
    partial class FlexInfo
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.ModelLabel = new System.Windows.Forms.Label();
            this.SerialLabel = new System.Windows.Forms.Label();
            this.ModelBox = new System.Windows.Forms.TextBox();
            this.VersionLabel = new System.Windows.Forms.Label();
            this.VersionBox = new System.Windows.Forms.TextBox();
            this.SerialBox = new System.Windows.Forms.TextBox();
            this.CallLabel = new System.Windows.Forms.Label();
            this.CallBox = new System.Windows.Forms.TextBox();
            this.NameBox = new System.Windows.Forms.TextBox();
            this.NameLabel = new System.Windows.Forms.Label();
            this.IPLabel = new System.Windows.Forms.Label();
            this.IPBox = new System.Windows.Forms.TextBox();
            this.DisplayControl = new RadioBoxes.Combo();
            this.DoneButton = new System.Windows.Forms.Button();
            this.InfoTabs = new System.Windows.Forms.TabControl();
            this.GeneralTab = new System.Windows.Forms.TabPage();
            this.FeatureTab = new System.Windows.Forms.TabPage();
            this.FeatureAvailabilityBox = new System.Windows.Forms.TextBox();
            this.RefreshLicenseButton = new System.Windows.Forms.Button();
            this.InfoTabs.SuspendLayout();
            this.GeneralTab.SuspendLayout();
            this.FeatureTab.SuspendLayout();
            this.SuspendLayout();
            // 
            // ModelLabel
            // 
            this.ModelLabel.AutoSize = true;
            this.ModelLabel.Location = new System.Drawing.Point(8, 20);
            this.ModelLabel.Name = "ModelLabel";
            this.ModelLabel.Size = new System.Drawing.Size(42, 13);
            this.ModelLabel.TabIndex = 10;
            this.ModelLabel.Text = "Model: ";
            // 
            // SerialLabel
            // 
            this.SerialLabel.AutoSize = true;
            this.SerialLabel.Location = new System.Drawing.Point(11, 50);
            this.SerialLabel.Name = "SerialLabel";
            this.SerialLabel.Size = new System.Drawing.Size(39, 13);
            this.SerialLabel.TabIndex = 100;
            this.SerialLabel.Text = "Serial: ";
            // 
            // ModelBox
            // 
            this.ModelBox.AccessibleName = "model";
            this.ModelBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.ModelBox.Location = new System.Drawing.Point(50, 20);
            this.ModelBox.Name = "ModelBox";
            this.ModelBox.ReadOnly = true;
            this.ModelBox.Size = new System.Drawing.Size(100, 20);
            this.ModelBox.TabIndex = 11;
            // 
            // VersionLabel
            // 
            this.VersionLabel.AutoSize = true;
            this.VersionLabel.Location = new System.Drawing.Point(172, 20);
            this.VersionLabel.Name = "VersionLabel";
            this.VersionLabel.Size = new System.Drawing.Size(48, 13);
            this.VersionLabel.TabIndex = 20;
            this.VersionLabel.Text = "Version: ";
            // 
            // VersionBox
            // 
            this.VersionBox.AccessibleName = "version";
            this.VersionBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.VersionBox.Location = new System.Drawing.Point(220, 20);
            this.VersionBox.Name = "VersionBox";
            this.VersionBox.ReadOnly = true;
            this.VersionBox.Size = new System.Drawing.Size(100, 20);
            this.VersionBox.TabIndex = 21;
            // 
            // SerialBox
            // 
            this.SerialBox.AccessibleName = "serial";
            this.SerialBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.SerialBox.Location = new System.Drawing.Point(50, 50);
            this.SerialBox.Name = "SerialBox";
            this.SerialBox.ReadOnly = true;
            this.SerialBox.Size = new System.Drawing.Size(200, 20);
            this.SerialBox.TabIndex = 101;
            // 
            // CallLabel
            // 
            this.CallLabel.AutoSize = true;
            this.CallLabel.Location = new System.Drawing.Point(20, 80);
            this.CallLabel.Name = "CallLabel";
            this.CallLabel.Size = new System.Drawing.Size(30, 13);
            this.CallLabel.TabIndex = 200;
            this.CallLabel.Text = "Call: ";
            // 
            // CallBox
            // 
            this.CallBox.AccessibleName = "call";
            this.CallBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.CallBox.Location = new System.Drawing.Point(50, 80);
            this.CallBox.Name = "CallBox";
            this.CallBox.Size = new System.Drawing.Size(100, 20);
            this.CallBox.TabIndex = 201;
            this.CallBox.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.CallBox_KeyPress);
            this.CallBox.Leave += new System.EventHandler(this.setCall);
            // 
            // NameBox
            // 
            this.NameBox.AccessibleName = "name";
            this.NameBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.NameBox.Location = new System.Drawing.Point(220, 80);
            this.NameBox.Name = "NameBox";
            this.NameBox.Size = new System.Drawing.Size(100, 20);
            this.NameBox.TabIndex = 211;
            this.NameBox.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.NameBox_KeyPress);
            this.NameBox.Leave += new System.EventHandler(this.setName);
            // 
            // NameLabel
            // 
            this.NameLabel.AutoSize = true;
            this.NameLabel.Location = new System.Drawing.Point(179, 80);
            this.NameLabel.Name = "NameLabel";
            this.NameLabel.Size = new System.Drawing.Size(41, 13);
            this.NameLabel.TabIndex = 210;
            this.NameLabel.Text = "Name: ";
            // 
            // IPLabel
            // 
            this.IPLabel.AutoSize = true;
            this.IPLabel.Location = new System.Drawing.Point(37, 110);
            this.IPLabel.Name = "IPLabel";
            this.IPLabel.Size = new System.Drawing.Size(23, 13);
            this.IPLabel.TabIndex = 300;
            this.IPLabel.Text = "IP: ";
            // 
            // IPBox
            // 
            this.IPBox.AccessibleName = "IP";
            this.IPBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.IPBox.Location = new System.Drawing.Point(50, 110);
            this.IPBox.Name = "IPBox";
            this.IPBox.ReadOnly = true;
            this.IPBox.Size = new System.Drawing.Size(100, 20);
            this.IPBox.TabIndex = 301;
            // 
            // DisplayControl
            // 
            this.DisplayControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.DisplayControl.ExpandedSize = new System.Drawing.Size(100, 56);
            this.DisplayControl.Header = "Display";
            this.DisplayControl.Location = new System.Drawing.Point(50, 140);
            this.DisplayControl.Name = "DisplayControl";
            this.DisplayControl.ReadOnly = false;
            this.DisplayControl.Size = new System.Drawing.Size(100, 36);
            this.DisplayControl.SmallSize = new System.Drawing.Size(100, 36);
            this.DisplayControl.TabIndex = 400;
            this.DisplayControl.Tag = "Display";
            this.DisplayControl.TheList = null;
            this.DisplayControl.UpdateDisplayFunction = null;
            this.DisplayControl.UpdateRigFunction = null;
            // 
            // InfoTabs
            // 
            this.InfoTabs.AccessibleName = "Radio information tabs";
            this.InfoTabs.AccessibleRole = System.Windows.Forms.AccessibleRole.PageTabList;
            this.InfoTabs.Controls.Add(this.GeneralTab);
            this.InfoTabs.Controls.Add(this.FeatureTab);
            this.InfoTabs.Location = new System.Drawing.Point(8, 8);
            this.InfoTabs.Name = "InfoTabs";
            this.InfoTabs.SelectedIndex = 0;
            this.InfoTabs.Size = new System.Drawing.Size(318, 200);
            this.InfoTabs.TabIndex = 10;
            // 
            // GeneralTab
            // 
            this.GeneralTab.AccessibleName = "General";
            this.GeneralTab.AccessibleRole = System.Windows.Forms.AccessibleRole.PageTab;
            this.GeneralTab.Controls.Add(this.DisplayControl);
            this.GeneralTab.Controls.Add(this.IPBox);
            this.GeneralTab.Controls.Add(this.IPLabel);
            this.GeneralTab.Controls.Add(this.NameLabel);
            this.GeneralTab.Controls.Add(this.NameBox);
            this.GeneralTab.Controls.Add(this.CallBox);
            this.GeneralTab.Controls.Add(this.CallLabel);
            this.GeneralTab.Controls.Add(this.SerialBox);
            this.GeneralTab.Controls.Add(this.VersionBox);
            this.GeneralTab.Controls.Add(this.VersionLabel);
            this.GeneralTab.Controls.Add(this.ModelBox);
            this.GeneralTab.Controls.Add(this.SerialLabel);
            this.GeneralTab.Controls.Add(this.ModelLabel);
            this.GeneralTab.Location = new System.Drawing.Point(4, 22);
            this.GeneralTab.Name = "GeneralTab";
            this.GeneralTab.Padding = new System.Windows.Forms.Padding(3);
            this.GeneralTab.Size = new System.Drawing.Size(310, 174);
            this.GeneralTab.TabIndex = 0;
            this.GeneralTab.Text = "General";
            this.GeneralTab.UseVisualStyleBackColor = true;
            // 
            // FeatureTab
            // 
            this.FeatureTab.AccessibleName = "Feature Availability";
            this.FeatureTab.AccessibleRole = System.Windows.Forms.AccessibleRole.PageTab;
            this.FeatureTab.Controls.Add(this.FeatureAvailabilityBox);
            this.FeatureTab.Controls.Add(this.RefreshLicenseButton);
            this.FeatureTab.Location = new System.Drawing.Point(4, 22);
            this.FeatureTab.Name = "FeatureTab";
            this.FeatureTab.Padding = new System.Windows.Forms.Padding(3);
            this.FeatureTab.Size = new System.Drawing.Size(310, 174);
            this.FeatureTab.TabIndex = 1;
            this.FeatureTab.Text = "Feature Availability";
            this.FeatureTab.UseVisualStyleBackColor = true;
            // 
            // FeatureAvailabilityBox
            // 
            this.FeatureAvailabilityBox.AccessibleName = "Feature availability list";
            this.FeatureAvailabilityBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.FeatureAvailabilityBox.Location = new System.Drawing.Point(8, 8);
            this.FeatureAvailabilityBox.Multiline = true;
            this.FeatureAvailabilityBox.Name = "FeatureAvailabilityBox";
            this.FeatureAvailabilityBox.ReadOnly = true;
            this.FeatureAvailabilityBox.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.FeatureAvailabilityBox.Size = new System.Drawing.Size(292, 128);
            this.FeatureAvailabilityBox.TabIndex = 10;
            // 
            // RefreshLicenseButton
            // 
            this.RefreshLicenseButton.AccessibleName = "Refresh license status";
            this.RefreshLicenseButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.RefreshLicenseButton.Location = new System.Drawing.Point(180, 142);
            this.RefreshLicenseButton.Name = "RefreshLicenseButton";
            this.RefreshLicenseButton.Size = new System.Drawing.Size(120, 23);
            this.RefreshLicenseButton.TabIndex = 20;
            this.RefreshLicenseButton.Text = "Refresh Licenses";
            this.RefreshLicenseButton.UseVisualStyleBackColor = true;
            this.RefreshLicenseButton.Click += new System.EventHandler(this.RefreshLicenseButton_Click);
            // 
            // DoneButton
            // 
            this.DoneButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.DoneButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.DoneButton.Location = new System.Drawing.Point(100, 220);
            this.DoneButton.Name = "DoneButton";
            this.DoneButton.Size = new System.Drawing.Size(75, 23);
            this.DoneButton.TabIndex = 900;
            this.DoneButton.Text = "OK";
            this.DoneButton.UseVisualStyleBackColor = true;
            this.DoneButton.Click += new System.EventHandler(this.DoneButton_Click);
            // 
            // FlexInfo
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.DoneButton;
            this.ClientSize = new System.Drawing.Size(334, 262);
            this.Controls.Add(this.InfoTabs);
            this.Controls.Add(this.DoneButton);
            this.Name = "FlexInfo";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Flex Info";
            this.Activated += new System.EventHandler(this.FlexInfo_Activated);
            this.Load += new System.EventHandler(this.FlexInfo_Load);
            this.InfoTabs.ResumeLayout(false);
            this.GeneralTab.ResumeLayout(false);
            this.GeneralTab.PerformLayout();
            this.FeatureTab.ResumeLayout(false);
            this.FeatureTab.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Label ModelLabel;
        private System.Windows.Forms.Label SerialLabel;
        private System.Windows.Forms.TextBox ModelBox;
        private System.Windows.Forms.Label VersionLabel;
        private System.Windows.Forms.TextBox VersionBox;
        private System.Windows.Forms.TextBox SerialBox;
        private System.Windows.Forms.Label CallLabel;
        private System.Windows.Forms.TextBox CallBox;
        private System.Windows.Forms.TextBox NameBox;
        private System.Windows.Forms.Label NameLabel;
        private System.Windows.Forms.Label IPLabel;
        private System.Windows.Forms.TextBox IPBox;
        private RadioBoxes.Combo DisplayControl;
        private System.Windows.Forms.Button DoneButton;
        private System.Windows.Forms.TabControl InfoTabs;
        private System.Windows.Forms.TabPage GeneralTab;
        private System.Windows.Forms.TabPage FeatureTab;
        private System.Windows.Forms.TextBox FeatureAvailabilityBox;
        private System.Windows.Forms.Button RefreshLicenseButton;
    }
}

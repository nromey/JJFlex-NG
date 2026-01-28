namespace Radios
{
    partial class TS590Filters
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

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.SWRBox = new System.Windows.Forms.TextBox();
            this.SWRLabel = new System.Windows.Forms.Label();
            this.CompLabel = new System.Windows.Forms.Label();
            this.CompBox = new System.Windows.Forms.TextBox();
            this.ALCLabel = new System.Windows.Forms.Label();
            this.ALCBox = new System.Windows.Forms.TextBox();
            this.AMHighControl = new RadioBoxes.Combo();
            this.AMLowControl = new RadioBoxes.Combo();
            this.SSBDWidthControl = new RadioBoxes.Combo();
            this.SSBDOffsetControl = new RadioBoxes.Combo();
            this.FSKWidthControl = new RadioBoxes.Combo();
            this.SSBFMFMDHighControl = new RadioBoxes.Combo();
            this.SSBFMFMDLowControl = new RadioBoxes.Combo();
            this.CWWidthControl = new RadioBoxes.Combo();
            this.CWOffsetControl = new RadioBoxes.Combo();
            this.FilterControl = new RadioBoxes.Combo();
            this.DataModeControl = new RadioBoxes.Combo();
            this.ToneCTCSSControl = new RadioBoxes.Combo();
            this.ToneFreqControl = new RadioBoxes.Combo();
            this.CTCSSFreqControl = new RadioBoxes.Combo();
            this.TXAntControl = new RadioBoxes.Combo();
            this.RXAntControl = new RadioBoxes.Combo();
            this.DriveAmpControl = new RadioBoxes.Combo();
            this.RFAttControl = new RadioBoxes.Combo();
            this.PreAmpControl = new RadioBoxes.Combo();
            this.ProcessorControl = new RadioBoxes.Combo();
            this.NRControl = new RadioBoxes.Combo();
            this.NotchControl = new RadioBoxes.Combo();
            this.NotchWidthControl = new RadioBoxes.Combo();
            this.NBControl = new RadioBoxes.Combo();
            this.NotchFreqControl = new RadioBoxes.NumberBox();
            this.BeatCancelControl = new RadioBoxes.Combo();
            this.AGCControl = new RadioBoxes.Combo();
            this.FMWidthControl = new RadioBoxes.Combo();
            this.SpeedControl = new RadioBoxes.NumberBox();
            this.BreakinDelayControl = new RadioBoxes.NumberBox();
            this.VoxDelayControl = new RadioBoxes.NumberBox();
            this.VoxGainControl = new RadioBoxes.NumberBox();
            this.MicGainControl = new RadioBoxes.NumberBox();
            this.CarrierLevelControl = new RadioBoxes.NumberBox();
            this.ProcessorInLevelControl = new RadioBoxes.NumberBox();
            this.ProcessorOutLevelControl = new RadioBoxes.NumberBox();
            this.XmitPowerControl = new RadioBoxes.NumberBox();
            this.NRLevel1Control = new RadioBoxes.NumberBox();
            this.NRLevel2Control = new RadioBoxes.NumberBox();
            this.AGCLevelControl = new RadioBoxes.NumberBox();
            this.NBLevelControl = new RadioBoxes.NumberBox();
            this.DecodeControl = new RadioBoxes.Combo();
            this.DecodeThresholdControl = new RadioBoxes.NumberBox();
            this.RXEQButton = new System.Windows.Forms.Button();
            this.TXEQButton = new System.Windows.Forms.Button();
            this.TXMonitorControl = new RadioBoxes.Combo();
            this.TXSourceControl = new RadioBoxes.Combo();
            this.SuspendLayout();
            // 
            // SWRBox
            // 
            this.SWRBox.AccessibleName = "SWR";
            this.SWRBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.SWRBox.Location = new System.Drawing.Point(187, 370);
            this.SWRBox.Margin = new System.Windows.Forms.Padding(4, 4, 4, 4);
            this.SWRBox.Name = "SWRBox";
            this.SWRBox.ReadOnly = true;
            this.SWRBox.Size = new System.Drawing.Size(65, 22);
            this.SWRBox.TabIndex = 1311;
            this.SWRBox.Tag = "SWR";
            this.SWRBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // SWRLabel
            // 
            this.SWRLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.SWRLabel.Location = new System.Drawing.Point(187, 351);
            this.SWRLabel.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.SWRLabel.Name = "SWRLabel";
            this.SWRLabel.Size = new System.Drawing.Size(67, 20);
            this.SWRLabel.TabIndex = 1310;
            this.SWRLabel.Text = "SWR";
            // 
            // CompLabel
            // 
            this.CompLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.CompLabel.Location = new System.Drawing.Point(280, 351);
            this.CompLabel.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.CompLabel.Name = "CompLabel";
            this.CompLabel.Size = new System.Drawing.Size(67, 20);
            this.CompLabel.TabIndex = 1320;
            this.CompLabel.Text = "Comp";
            // 
            // CompBox
            // 
            this.CompBox.AccessibleName = "compression";
            this.CompBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.CompBox.Location = new System.Drawing.Point(280, 370);
            this.CompBox.Margin = new System.Windows.Forms.Padding(4, 4, 4, 4);
            this.CompBox.Name = "CompBox";
            this.CompBox.ReadOnly = true;
            this.CompBox.Size = new System.Drawing.Size(65, 22);
            this.CompBox.TabIndex = 1321;
            this.CompBox.Tag = "Comp";
            this.CompBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // ALCLabel
            // 
            this.ALCLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.ALCLabel.Location = new System.Drawing.Point(373, 351);
            this.ALCLabel.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.ALCLabel.Name = "ALCLabel";
            this.ALCLabel.Size = new System.Drawing.Size(67, 20);
            this.ALCLabel.TabIndex = 1330;
            this.ALCLabel.Text = "ALC";
            // 
            // ALCBox
            // 
            this.ALCBox.AccessibleName = "ALC";
            this.ALCBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.ALCBox.Location = new System.Drawing.Point(373, 370);
            this.ALCBox.Margin = new System.Windows.Forms.Padding(4, 4, 4, 4);
            this.ALCBox.Name = "ALCBox";
            this.ALCBox.ReadOnly = true;
            this.ALCBox.Size = new System.Drawing.Size(65, 22);
            this.ALCBox.TabIndex = 1331;
            this.ALCBox.Tag = "ALC";
            this.ALCBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // AMHighControl
            // 
            this.AMHighControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AMHighControl.Enabled = false;
            this.AMHighControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.AMHighControl.Header = "high";
            this.AMHighControl.Location = new System.Drawing.Point(187, 203);
            this.AMHighControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.AMHighControl.Name = "AMHighControl";
            this.AMHighControl.ReadOnly = false;
            this.AMHighControl.Size = new System.Drawing.Size(67, 44);
            this.AMHighControl.SmallSize = new System.Drawing.Size(50, 36);
            this.AMHighControl.TabIndex = 1020;
            this.AMHighControl.Tag = "high";
            this.AMHighControl.TheList = null;
            this.AMHighControl.UpdateDisplayFunction = null;
            this.AMHighControl.UpdateRigFunction = null;
            this.AMHighControl.Visible = false;
            this.AMHighControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // AMLowControl
            // 
            this.AMLowControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AMLowControl.Enabled = false;
            this.AMLowControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.AMLowControl.Header = "low";
            this.AMLowControl.Location = new System.Drawing.Point(93, 203);
            this.AMLowControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.AMLowControl.Name = "AMLowControl";
            this.AMLowControl.ReadOnly = false;
            this.AMLowControl.Size = new System.Drawing.Size(67, 44);
            this.AMLowControl.SmallSize = new System.Drawing.Size(50, 36);
            this.AMLowControl.TabIndex = 1010;
            this.AMLowControl.Tag = "low";
            this.AMLowControl.TheList = null;
            this.AMLowControl.UpdateDisplayFunction = null;
            this.AMLowControl.UpdateRigFunction = null;
            this.AMLowControl.Visible = false;
            this.AMLowControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // SSBDWidthControl
            // 
            this.SSBDWidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.SSBDWidthControl.Enabled = false;
            this.SSBDWidthControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.SSBDWidthControl.Header = "width";
            this.SSBDWidthControl.Location = new System.Drawing.Point(187, 203);
            this.SSBDWidthControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.SSBDWidthControl.Name = "SSBDWidthControl";
            this.SSBDWidthControl.ReadOnly = false;
            this.SSBDWidthControl.Size = new System.Drawing.Size(67, 44);
            this.SSBDWidthControl.SmallSize = new System.Drawing.Size(50, 36);
            this.SSBDWidthControl.TabIndex = 1020;
            this.SSBDWidthControl.Tag = "width";
            this.SSBDWidthControl.TheList = null;
            this.SSBDWidthControl.UpdateDisplayFunction = null;
            this.SSBDWidthControl.UpdateRigFunction = null;
            this.SSBDWidthControl.Visible = false;
            this.SSBDWidthControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // SSBDOffsetControl
            // 
            this.SSBDOffsetControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.SSBDOffsetControl.Enabled = false;
            this.SSBDOffsetControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.SSBDOffsetControl.Header = "shift";
            this.SSBDOffsetControl.Location = new System.Drawing.Point(93, 203);
            this.SSBDOffsetControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.SSBDOffsetControl.Name = "SSBDOffsetControl";
            this.SSBDOffsetControl.ReadOnly = false;
            this.SSBDOffsetControl.Size = new System.Drawing.Size(67, 44);
            this.SSBDOffsetControl.SmallSize = new System.Drawing.Size(50, 36);
            this.SSBDOffsetControl.TabIndex = 1010;
            this.SSBDOffsetControl.Tag = "shift";
            this.SSBDOffsetControl.TheList = null;
            this.SSBDOffsetControl.UpdateDisplayFunction = null;
            this.SSBDOffsetControl.UpdateRigFunction = null;
            this.SSBDOffsetControl.Visible = false;
            this.SSBDOffsetControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // FSKWidthControl
            // 
            this.FSKWidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FSKWidthControl.Enabled = false;
            this.FSKWidthControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.FSKWidthControl.Header = "width";
            this.FSKWidthControl.Location = new System.Drawing.Point(187, 203);
            this.FSKWidthControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.FSKWidthControl.Name = "FSKWidthControl";
            this.FSKWidthControl.ReadOnly = false;
            this.FSKWidthControl.Size = new System.Drawing.Size(67, 44);
            this.FSKWidthControl.SmallSize = new System.Drawing.Size(50, 36);
            this.FSKWidthControl.TabIndex = 1020;
            this.FSKWidthControl.Tag = "width";
            this.FSKWidthControl.TheList = null;
            this.FSKWidthControl.UpdateDisplayFunction = null;
            this.FSKWidthControl.UpdateRigFunction = null;
            this.FSKWidthControl.Visible = false;
            this.FSKWidthControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // SSBFMFMDHighControl
            // 
            this.SSBFMFMDHighControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.SSBFMFMDHighControl.Enabled = false;
            this.SSBFMFMDHighControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.SSBFMFMDHighControl.Header = "high";
            this.SSBFMFMDHighControl.Location = new System.Drawing.Point(187, 203);
            this.SSBFMFMDHighControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.SSBFMFMDHighControl.Name = "SSBFMFMDHighControl";
            this.SSBFMFMDHighControl.ReadOnly = false;
            this.SSBFMFMDHighControl.Size = new System.Drawing.Size(67, 44);
            this.SSBFMFMDHighControl.SmallSize = new System.Drawing.Size(50, 36);
            this.SSBFMFMDHighControl.TabIndex = 1020;
            this.SSBFMFMDHighControl.Tag = "high";
            this.SSBFMFMDHighControl.TheList = null;
            this.SSBFMFMDHighControl.UpdateDisplayFunction = null;
            this.SSBFMFMDHighControl.UpdateRigFunction = null;
            this.SSBFMFMDHighControl.Visible = false;
            this.SSBFMFMDHighControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // SSBFMFMDLowControl
            // 
            this.SSBFMFMDLowControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.SSBFMFMDLowControl.Enabled = false;
            this.SSBFMFMDLowControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.SSBFMFMDLowControl.Header = "low";
            this.SSBFMFMDLowControl.Location = new System.Drawing.Point(93, 203);
            this.SSBFMFMDLowControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.SSBFMFMDLowControl.Name = "SSBFMFMDLowControl";
            this.SSBFMFMDLowControl.ReadOnly = false;
            this.SSBFMFMDLowControl.Size = new System.Drawing.Size(67, 44);
            this.SSBFMFMDLowControl.SmallSize = new System.Drawing.Size(50, 36);
            this.SSBFMFMDLowControl.TabIndex = 1010;
            this.SSBFMFMDLowControl.Tag = "low";
            this.SSBFMFMDLowControl.TheList = null;
            this.SSBFMFMDLowControl.UpdateDisplayFunction = null;
            this.SSBFMFMDLowControl.UpdateRigFunction = null;
            this.SSBFMFMDLowControl.Visible = false;
            this.SSBFMFMDLowControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // CWWidthControl
            // 
            this.CWWidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.CWWidthControl.Enabled = false;
            this.CWWidthControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.CWWidthControl.Header = "width";
            this.CWWidthControl.Location = new System.Drawing.Point(93, 203);
            this.CWWidthControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.CWWidthControl.Name = "CWWidthControl";
            this.CWWidthControl.ReadOnly = false;
            this.CWWidthControl.Size = new System.Drawing.Size(67, 44);
            this.CWWidthControl.SmallSize = new System.Drawing.Size(50, 36);
            this.CWWidthControl.TabIndex = 1010;
            this.CWWidthControl.Tag = "width";
            this.CWWidthControl.TheList = null;
            this.CWWidthControl.UpdateDisplayFunction = null;
            this.CWWidthControl.UpdateRigFunction = null;
            this.CWWidthControl.Visible = false;
            this.CWWidthControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // CWOffsetControl
            // 
            this.CWOffsetControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.CWOffsetControl.Enabled = false;
            this.CWOffsetControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.CWOffsetControl.Header = "offset";
            this.CWOffsetControl.Location = new System.Drawing.Point(187, 203);
            this.CWOffsetControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.CWOffsetControl.Name = "CWOffsetControl";
            this.CWOffsetControl.ReadOnly = false;
            this.CWOffsetControl.Size = new System.Drawing.Size(67, 44);
            this.CWOffsetControl.SmallSize = new System.Drawing.Size(50, 36);
            this.CWOffsetControl.TabIndex = 1020;
            this.CWOffsetControl.Tag = "offset";
            this.CWOffsetControl.TheList = null;
            this.CWOffsetControl.UpdateDisplayFunction = null;
            this.CWOffsetControl.UpdateRigFunction = null;
            this.CWOffsetControl.Visible = false;
            this.CWOffsetControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // FilterControl
            // 
            this.FilterControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FilterControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.FilterControl.Header = "filter";
            this.FilterControl.Location = new System.Drawing.Point(0, 203);
            this.FilterControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.FilterControl.Name = "FilterControl";
            this.FilterControl.ReadOnly = false;
            this.FilterControl.Size = new System.Drawing.Size(67, 44);
            this.FilterControl.SmallSize = new System.Drawing.Size(50, 36);
            this.FilterControl.TabIndex = 1000;
            this.FilterControl.Tag = "filter";
            this.FilterControl.TheList = null;
            this.FilterControl.UpdateDisplayFunction = null;
            this.FilterControl.UpdateRigFunction = null;
            this.FilterControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // DataModeControl
            // 
            this.DataModeControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.DataModeControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.DataModeControl.Header = "Datamode";
            this.DataModeControl.Location = new System.Drawing.Point(0, 0);
            this.DataModeControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.DataModeControl.Name = "DataModeControl";
            this.DataModeControl.ReadOnly = false;
            this.DataModeControl.Size = new System.Drawing.Size(67, 44);
            this.DataModeControl.SmallSize = new System.Drawing.Size(50, 36);
            this.DataModeControl.TabIndex = 20;
            this.DataModeControl.Tag = "Datamode";
            this.DataModeControl.TheList = null;
            this.DataModeControl.UpdateDisplayFunction = null;
            this.DataModeControl.UpdateRigFunction = null;
            this.DataModeControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // ToneCTCSSControl
            // 
            this.ToneCTCSSControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ToneCTCSSControl.Enabled = false;
            this.ToneCTCSSControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.ToneCTCSSControl.Header = "Tone/CT";
            this.ToneCTCSSControl.Location = new System.Drawing.Point(93, 0);
            this.ToneCTCSSControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.ToneCTCSSControl.Name = "ToneCTCSSControl";
            this.ToneCTCSSControl.ReadOnly = false;
            this.ToneCTCSSControl.Size = new System.Drawing.Size(67, 44);
            this.ToneCTCSSControl.SmallSize = new System.Drawing.Size(50, 36);
            this.ToneCTCSSControl.TabIndex = 30;
            this.ToneCTCSSControl.Tag = "Tone/CTCSS";
            this.ToneCTCSSControl.TheList = null;
            this.ToneCTCSSControl.UpdateDisplayFunction = null;
            this.ToneCTCSSControl.UpdateRigFunction = null;
            this.ToneCTCSSControl.Visible = false;
            this.ToneCTCSSControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // ToneFreqControl
            // 
            this.ToneFreqControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ToneFreqControl.Enabled = false;
            this.ToneFreqControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.ToneFreqControl.Header = "ToneFreq";
            this.ToneFreqControl.Location = new System.Drawing.Point(187, 0);
            this.ToneFreqControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.ToneFreqControl.Name = "ToneFreqControl";
            this.ToneFreqControl.ReadOnly = false;
            this.ToneFreqControl.Size = new System.Drawing.Size(67, 44);
            this.ToneFreqControl.SmallSize = new System.Drawing.Size(50, 36);
            this.ToneFreqControl.TabIndex = 35;
            this.ToneFreqControl.Tag = "Tone Freq";
            this.ToneFreqControl.TheList = null;
            this.ToneFreqControl.UpdateDisplayFunction = null;
            this.ToneFreqControl.UpdateRigFunction = null;
            this.ToneFreqControl.Visible = false;
            this.ToneFreqControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // CTCSSFreqControl
            // 
            this.CTCSSFreqControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.CTCSSFreqControl.Enabled = false;
            this.CTCSSFreqControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.CTCSSFreqControl.Header = "CT freq";
            this.CTCSSFreqControl.Location = new System.Drawing.Point(280, 0);
            this.CTCSSFreqControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.CTCSSFreqControl.Name = "CTCSSFreqControl";
            this.CTCSSFreqControl.ReadOnly = false;
            this.CTCSSFreqControl.Size = new System.Drawing.Size(67, 44);
            this.CTCSSFreqControl.SmallSize = new System.Drawing.Size(50, 36);
            this.CTCSSFreqControl.TabIndex = 40;
            this.CTCSSFreqControl.Tag = "CTCSS freq";
            this.CTCSSFreqControl.TheList = null;
            this.CTCSSFreqControl.UpdateDisplayFunction = null;
            this.CTCSSFreqControl.UpdateRigFunction = null;
            this.CTCSSFreqControl.Visible = false;
            this.CTCSSFreqControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // TXAntControl
            // 
            this.TXAntControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.TXAntControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.TXAntControl.Header = "Ant";
            this.TXAntControl.Location = new System.Drawing.Point(0, 49);
            this.TXAntControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.TXAntControl.Name = "TXAntControl";
            this.TXAntControl.ReadOnly = false;
            this.TXAntControl.Size = new System.Drawing.Size(67, 44);
            this.TXAntControl.SmallSize = new System.Drawing.Size(50, 36);
            this.TXAntControl.TabIndex = 50;
            this.TXAntControl.Tag = "Ant";
            this.TXAntControl.TheList = null;
            this.TXAntControl.UpdateDisplayFunction = null;
            this.TXAntControl.UpdateRigFunction = null;
            this.TXAntControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // RXAntControl
            // 
            this.RXAntControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.RXAntControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.RXAntControl.Header = "RX Ant";
            this.RXAntControl.Location = new System.Drawing.Point(93, 49);
            this.RXAntControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.RXAntControl.Name = "RXAntControl";
            this.RXAntControl.ReadOnly = false;
            this.RXAntControl.Size = new System.Drawing.Size(67, 44);
            this.RXAntControl.SmallSize = new System.Drawing.Size(50, 36);
            this.RXAntControl.TabIndex = 55;
            this.RXAntControl.Tag = "RX Ant";
            this.RXAntControl.TheList = null;
            this.RXAntControl.UpdateDisplayFunction = null;
            this.RXAntControl.UpdateRigFunction = null;
            this.RXAntControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // DriveAmpControl
            // 
            this.DriveAmpControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.DriveAmpControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.DriveAmpControl.Header = "Drv Amp";
            this.DriveAmpControl.Location = new System.Drawing.Point(187, 49);
            this.DriveAmpControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.DriveAmpControl.Name = "DriveAmpControl";
            this.DriveAmpControl.ReadOnly = false;
            this.DriveAmpControl.Size = new System.Drawing.Size(67, 44);
            this.DriveAmpControl.SmallSize = new System.Drawing.Size(50, 36);
            this.DriveAmpControl.TabIndex = 60;
            this.DriveAmpControl.Tag = "Drive Amp";
            this.DriveAmpControl.TheList = null;
            this.DriveAmpControl.UpdateDisplayFunction = null;
            this.DriveAmpControl.UpdateRigFunction = null;
            this.DriveAmpControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // RFAttControl
            // 
            this.RFAttControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.RFAttControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.RFAttControl.Header = "RF Att";
            this.RFAttControl.Location = new System.Drawing.Point(280, 49);
            this.RFAttControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.RFAttControl.Name = "RFAttControl";
            this.RFAttControl.ReadOnly = false;
            this.RFAttControl.Size = new System.Drawing.Size(67, 44);
            this.RFAttControl.SmallSize = new System.Drawing.Size(50, 36);
            this.RFAttControl.TabIndex = 70;
            this.RFAttControl.Tag = "RF Att";
            this.RFAttControl.TheList = null;
            this.RFAttControl.UpdateDisplayFunction = null;
            this.RFAttControl.UpdateRigFunction = null;
            this.RFAttControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // PreAmpControl
            // 
            this.PreAmpControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.PreAmpControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.PreAmpControl.Header = "Preamp";
            this.PreAmpControl.Location = new System.Drawing.Point(373, 49);
            this.PreAmpControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.PreAmpControl.Name = "PreAmpControl";
            this.PreAmpControl.ReadOnly = false;
            this.PreAmpControl.Size = new System.Drawing.Size(67, 44);
            this.PreAmpControl.SmallSize = new System.Drawing.Size(50, 36);
            this.PreAmpControl.TabIndex = 80;
            this.PreAmpControl.Tag = "Preamp";
            this.PreAmpControl.TheList = null;
            this.PreAmpControl.UpdateDisplayFunction = null;
            this.PreAmpControl.UpdateRigFunction = null;
            this.PreAmpControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // ProcessorControl
            // 
            this.ProcessorControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ProcessorControl.Enabled = false;
            this.ProcessorControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.ProcessorControl.Header = "SpProc";
            this.ProcessorControl.Location = new System.Drawing.Point(280, 98);
            this.ProcessorControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.ProcessorControl.Name = "ProcessorControl";
            this.ProcessorControl.ReadOnly = false;
            this.ProcessorControl.Size = new System.Drawing.Size(67, 44);
            this.ProcessorControl.SmallSize = new System.Drawing.Size(50, 36);
            this.ProcessorControl.TabIndex = 150;
            this.ProcessorControl.Tag = "Spch Proc";
            this.ProcessorControl.TheList = null;
            this.ProcessorControl.UpdateDisplayFunction = null;
            this.ProcessorControl.UpdateRigFunction = null;
            this.ProcessorControl.Visible = false;
            this.ProcessorControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // NRControl
            // 
            this.NRControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NRControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.NRControl.Header = "NReduc";
            this.NRControl.Location = new System.Drawing.Point(280, 203);
            this.NRControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.NRControl.Name = "NRControl";
            this.NRControl.ReadOnly = false;
            this.NRControl.Size = new System.Drawing.Size(67, 44);
            this.NRControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NRControl.TabIndex = 1030;
            this.NRControl.Tag = "NReduc";
            this.NRControl.TheList = null;
            this.NRControl.UpdateDisplayFunction = null;
            this.NRControl.UpdateRigFunction = null;
            this.NRControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // NotchControl
            // 
            this.NotchControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NotchControl.ExpandedSize = new System.Drawing.Size(60, 80);
            this.NotchControl.Header = "Notch";
            this.NotchControl.Location = new System.Drawing.Point(373, 252);
            this.NotchControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.NotchControl.Name = "NotchControl";
            this.NotchControl.ReadOnly = false;
            this.NotchControl.Size = new System.Drawing.Size(80, 44);
            this.NotchControl.SmallSize = new System.Drawing.Size(60, 36);
            this.NotchControl.TabIndex = 1120;
            this.NotchControl.Tag = "Notch";
            this.NotchControl.TheList = null;
            this.NotchControl.UpdateDisplayFunction = null;
            this.NotchControl.UpdateRigFunction = null;
            this.NotchControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // NotchWidthControl
            // 
            this.NotchWidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NotchWidthControl.ExpandedSize = new System.Drawing.Size(60, 50);
            this.NotchWidthControl.Header = "Ntch wdth";
            this.NotchWidthControl.Location = new System.Drawing.Point(560, 252);
            this.NotchWidthControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.NotchWidthControl.Name = "NotchWidthControl";
            this.NotchWidthControl.ReadOnly = false;
            this.NotchWidthControl.Size = new System.Drawing.Size(80, 44);
            this.NotchWidthControl.SmallSize = new System.Drawing.Size(60, 36);
            this.NotchWidthControl.TabIndex = 1140;
            this.NotchWidthControl.Tag = "Ntch wdth";
            this.NotchWidthControl.TheList = null;
            this.NotchWidthControl.UpdateDisplayFunction = null;
            this.NotchWidthControl.UpdateRigFunction = null;
            this.NotchWidthControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // NBControl
            // 
            this.NBControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NBControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.NBControl.Header = "N.B.";
            this.NBControl.Location = new System.Drawing.Point(187, 252);
            this.NBControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.NBControl.Name = "NBControl";
            this.NBControl.ReadOnly = false;
            this.NBControl.Size = new System.Drawing.Size(67, 44);
            this.NBControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NBControl.TabIndex = 1107;
            this.NBControl.Tag = "N.B.";
            this.NBControl.TheList = null;
            this.NBControl.UpdateDisplayFunction = null;
            this.NBControl.UpdateRigFunction = null;
            this.NBControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // NotchFreqControl
            // 
            this.NotchFreqControl.AccessibleName = "notch freq.";
            this.NotchFreqControl.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.NotchFreqControl.Header = "ntchfreq";
            this.NotchFreqControl.HighValue = 0;
            this.NotchFreqControl.Increment = 0;
            this.NotchFreqControl.Location = new System.Drawing.Point(467, 252);
            this.NotchFreqControl.LowValue = 0;
            this.NotchFreqControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.NotchFreqControl.Name = "NotchFreqControl";
            this.NotchFreqControl.ReadOnly = false;
            this.NotchFreqControl.Size = new System.Drawing.Size(67, 44);
            this.NotchFreqControl.TabIndex = 1130;
            this.NotchFreqControl.Tag = "ntchfreq";
            this.NotchFreqControl.UpdateDisplayFunction = null;
            this.NotchFreqControl.UpdateRigFunction = null;
            // 
            // BeatCancelControl
            // 
            this.BeatCancelControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.BeatCancelControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.BeatCancelControl.Header = "beat cncl";
            this.BeatCancelControl.Location = new System.Drawing.Point(653, 252);
            this.BeatCancelControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.BeatCancelControl.Name = "BeatCancelControl";
            this.BeatCancelControl.ReadOnly = false;
            this.BeatCancelControl.Size = new System.Drawing.Size(67, 44);
            this.BeatCancelControl.SmallSize = new System.Drawing.Size(50, 36);
            this.BeatCancelControl.TabIndex = 1150;
            this.BeatCancelControl.Tag = "beat cncl";
            this.BeatCancelControl.TheList = null;
            this.BeatCancelControl.UpdateDisplayFunction = null;
            this.BeatCancelControl.UpdateRigFunction = null;
            this.BeatCancelControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // AGCControl
            // 
            this.AGCControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AGCControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.AGCControl.Header = "AGC";
            this.AGCControl.Location = new System.Drawing.Point(0, 252);
            this.AGCControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.AGCControl.Name = "AGCControl";
            this.AGCControl.ReadOnly = false;
            this.AGCControl.Size = new System.Drawing.Size(67, 44);
            this.AGCControl.SmallSize = new System.Drawing.Size(50, 36);
            this.AGCControl.TabIndex = 1100;
            this.AGCControl.Tag = "AGC";
            this.AGCControl.TheList = null;
            this.AGCControl.UpdateDisplayFunction = null;
            this.AGCControl.UpdateRigFunction = null;
            this.AGCControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // FMWidthControl
            // 
            this.FMWidthControl.AccessibleName = "";
            this.FMWidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FMWidthControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.FMWidthControl.Header = "Width";
            this.FMWidthControl.Location = new System.Drawing.Point(373, 0);
            this.FMWidthControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.FMWidthControl.Name = "FMWidthControl";
            this.FMWidthControl.ReadOnly = false;
            this.FMWidthControl.Size = new System.Drawing.Size(67, 44);
            this.FMWidthControl.SmallSize = new System.Drawing.Size(50, 36);
            this.FMWidthControl.TabIndex = 45;
            this.FMWidthControl.Tag = "Width";
            this.FMWidthControl.TheList = null;
            this.FMWidthControl.UpdateDisplayFunction = null;
            this.FMWidthControl.UpdateRigFunction = null;
            this.FMWidthControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // SpeedControl
            // 
            this.SpeedControl.Header = "speed";
            this.SpeedControl.HighValue = 0;
            this.SpeedControl.Increment = 0;
            this.SpeedControl.Location = new System.Drawing.Point(0, 0);
            this.SpeedControl.LowValue = 0;
            this.SpeedControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.SpeedControl.Name = "SpeedControl";
            this.SpeedControl.ReadOnly = false;
            this.SpeedControl.Size = new System.Drawing.Size(67, 44);
            this.SpeedControl.TabIndex = 20;
            this.SpeedControl.Tag = "speed";
            this.SpeedControl.UpdateDisplayFunction = null;
            this.SpeedControl.UpdateRigFunction = null;
            // 
            // BreakinDelayControl
            // 
            this.BreakinDelayControl.Header = "BkinDel";
            this.BreakinDelayControl.HighValue = 0;
            this.BreakinDelayControl.Increment = 0;
            this.BreakinDelayControl.Location = new System.Drawing.Point(187, 0);
            this.BreakinDelayControl.LowValue = 0;
            this.BreakinDelayControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.BreakinDelayControl.Name = "BreakinDelayControl";
            this.BreakinDelayControl.ReadOnly = false;
            this.BreakinDelayControl.Size = new System.Drawing.Size(67, 44);
            this.BreakinDelayControl.TabIndex = 40;
            this.BreakinDelayControl.Tag = "BkinDel";
            this.BreakinDelayControl.UpdateDisplayFunction = null;
            this.BreakinDelayControl.UpdateRigFunction = null;
            // 
            // VoxDelayControl
            // 
            this.VoxDelayControl.Header = "VoxDelay";
            this.VoxDelayControl.HighValue = 0;
            this.VoxDelayControl.Increment = 0;
            this.VoxDelayControl.Location = new System.Drawing.Point(0, 98);
            this.VoxDelayControl.LowValue = 0;
            this.VoxDelayControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.VoxDelayControl.Name = "VoxDelayControl";
            this.VoxDelayControl.ReadOnly = false;
            this.VoxDelayControl.Size = new System.Drawing.Size(67, 44);
            this.VoxDelayControl.TabIndex = 100;
            this.VoxDelayControl.Tag = "VoxDelay";
            this.VoxDelayControl.UpdateDisplayFunction = null;
            this.VoxDelayControl.UpdateRigFunction = null;
            // 
            // VoxGainControl
            // 
            this.VoxGainControl.Header = "Vox gain";
            this.VoxGainControl.HighValue = 0;
            this.VoxGainControl.Increment = 0;
            this.VoxGainControl.Location = new System.Drawing.Point(93, 98);
            this.VoxGainControl.LowValue = 0;
            this.VoxGainControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.VoxGainControl.Name = "VoxGainControl";
            this.VoxGainControl.ReadOnly = false;
            this.VoxGainControl.Size = new System.Drawing.Size(67, 44);
            this.VoxGainControl.TabIndex = 110;
            this.VoxGainControl.Tag = "Vox gain";
            this.VoxGainControl.UpdateDisplayFunction = null;
            this.VoxGainControl.UpdateRigFunction = null;
            // 
            // MicGainControl
            // 
            this.MicGainControl.Header = "Mic gain";
            this.MicGainControl.HighValue = 0;
            this.MicGainControl.Increment = 0;
            this.MicGainControl.Location = new System.Drawing.Point(187, 98);
            this.MicGainControl.LowValue = 0;
            this.MicGainControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.MicGainControl.Name = "MicGainControl";
            this.MicGainControl.ReadOnly = false;
            this.MicGainControl.Size = new System.Drawing.Size(67, 44);
            this.MicGainControl.TabIndex = 120;
            this.MicGainControl.Tag = "Mic gain";
            this.MicGainControl.UpdateDisplayFunction = null;
            this.MicGainControl.UpdateRigFunction = null;
            // 
            // CarrierLevelControl
            // 
            this.CarrierLevelControl.Header = "car lvl";
            this.CarrierLevelControl.HighValue = 0;
            this.CarrierLevelControl.Increment = 0;
            this.CarrierLevelControl.Location = new System.Drawing.Point(93, 0);
            this.CarrierLevelControl.LowValue = 0;
            this.CarrierLevelControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.CarrierLevelControl.Name = "CarrierLevelControl";
            this.CarrierLevelControl.ReadOnly = false;
            this.CarrierLevelControl.Size = new System.Drawing.Size(67, 44);
            this.CarrierLevelControl.TabIndex = 30;
            this.CarrierLevelControl.Tag = "car lvl";
            this.CarrierLevelControl.UpdateDisplayFunction = null;
            this.CarrierLevelControl.UpdateRigFunction = null;
            // 
            // ProcessorInLevelControl
            // 
            this.ProcessorInLevelControl.Header = "proc in";
            this.ProcessorInLevelControl.HighValue = 0;
            this.ProcessorInLevelControl.Increment = 0;
            this.ProcessorInLevelControl.Location = new System.Drawing.Point(373, 98);
            this.ProcessorInLevelControl.LowValue = 0;
            this.ProcessorInLevelControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.ProcessorInLevelControl.Name = "ProcessorInLevelControl";
            this.ProcessorInLevelControl.ReadOnly = false;
            this.ProcessorInLevelControl.Size = new System.Drawing.Size(67, 44);
            this.ProcessorInLevelControl.TabIndex = 160;
            this.ProcessorInLevelControl.Tag = "proc in";
            this.ProcessorInLevelControl.UpdateDisplayFunction = null;
            this.ProcessorInLevelControl.UpdateRigFunction = null;
            // 
            // ProcessorOutLevelControl
            // 
            this.ProcessorOutLevelControl.Header = "proc out";
            this.ProcessorOutLevelControl.HighValue = 0;
            this.ProcessorOutLevelControl.Increment = 0;
            this.ProcessorOutLevelControl.Location = new System.Drawing.Point(467, 98);
            this.ProcessorOutLevelControl.LowValue = 0;
            this.ProcessorOutLevelControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.ProcessorOutLevelControl.Name = "ProcessorOutLevelControl";
            this.ProcessorOutLevelControl.ReadOnly = false;
            this.ProcessorOutLevelControl.Size = new System.Drawing.Size(67, 44);
            this.ProcessorOutLevelControl.TabIndex = 170;
            this.ProcessorOutLevelControl.Tag = "proc out";
            this.ProcessorOutLevelControl.UpdateDisplayFunction = null;
            this.ProcessorOutLevelControl.UpdateRigFunction = null;
            // 
            // XmitPowerControl
            // 
            this.XmitPowerControl.Header = "Power";
            this.XmitPowerControl.HighValue = 0;
            this.XmitPowerControl.Increment = 0;
            this.XmitPowerControl.Location = new System.Drawing.Point(93, 351);
            this.XmitPowerControl.LowValue = 0;
            this.XmitPowerControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.XmitPowerControl.Name = "XmitPowerControl";
            this.XmitPowerControl.ReadOnly = false;
            this.XmitPowerControl.Size = new System.Drawing.Size(67, 44);
            this.XmitPowerControl.TabIndex = 1305;
            this.XmitPowerControl.Tag = "Power";
            this.XmitPowerControl.UpdateDisplayFunction = null;
            this.XmitPowerControl.UpdateRigFunction = null;
            // 
            // NRLevel1Control
            // 
            this.NRLevel1Control.Header = "NR level";
            this.NRLevel1Control.HighValue = 0;
            this.NRLevel1Control.Increment = 0;
            this.NRLevel1Control.Location = new System.Drawing.Point(373, 203);
            this.NRLevel1Control.LowValue = 0;
            this.NRLevel1Control.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.NRLevel1Control.Name = "NRLevel1Control";
            this.NRLevel1Control.ReadOnly = false;
            this.NRLevel1Control.Size = new System.Drawing.Size(67, 44);
            this.NRLevel1Control.TabIndex = 1040;
            this.NRLevel1Control.Tag = "NR level";
            this.NRLevel1Control.UpdateDisplayFunction = null;
            this.NRLevel1Control.UpdateRigFunction = null;
            // 
            // NRLevel2Control
            // 
            this.NRLevel2Control.Header = "NR level";
            this.NRLevel2Control.HighValue = 0;
            this.NRLevel2Control.Increment = 0;
            this.NRLevel2Control.Location = new System.Drawing.Point(373, 203);
            this.NRLevel2Control.LowValue = 0;
            this.NRLevel2Control.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.NRLevel2Control.Name = "NRLevel2Control";
            this.NRLevel2Control.ReadOnly = false;
            this.NRLevel2Control.Size = new System.Drawing.Size(67, 44);
            this.NRLevel2Control.TabIndex = 1040;
            this.NRLevel2Control.Tag = "NR level";
            this.NRLevel2Control.UpdateDisplayFunction = null;
            this.NRLevel2Control.UpdateRigFunction = null;
            // 
            // AGCLevelControl
            // 
            this.AGCLevelControl.Header = "AGC level";
            this.AGCLevelControl.HighValue = 0;
            this.AGCLevelControl.Increment = 0;
            this.AGCLevelControl.Location = new System.Drawing.Point(93, 252);
            this.AGCLevelControl.LowValue = 0;
            this.AGCLevelControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.AGCLevelControl.Name = "AGCLevelControl";
            this.AGCLevelControl.ReadOnly = false;
            this.AGCLevelControl.Size = new System.Drawing.Size(67, 44);
            this.AGCLevelControl.TabIndex = 1101;
            this.AGCLevelControl.Tag = "AGC level";
            this.AGCLevelControl.UpdateDisplayFunction = null;
            this.AGCLevelControl.UpdateRigFunction = null;
            // 
            // NBLevelControl
            // 
            this.NBLevelControl.Header = "NB level";
            this.NBLevelControl.HighValue = 0;
            this.NBLevelControl.Increment = 0;
            this.NBLevelControl.Location = new System.Drawing.Point(280, 252);
            this.NBLevelControl.LowValue = 0;
            this.NBLevelControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.NBLevelControl.Name = "NBLevelControl";
            this.NBLevelControl.ReadOnly = false;
            this.NBLevelControl.Size = new System.Drawing.Size(67, 44);
            this.NBLevelControl.TabIndex = 1110;
            this.NBLevelControl.Tag = "NB level";
            this.NBLevelControl.UpdateDisplayFunction = null;
            this.NBLevelControl.UpdateRigFunction = null;
            // 
            // DecodeControl
            // 
            this.DecodeControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.DecodeControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.DecodeControl.Header = "Decode";
            this.DecodeControl.Location = new System.Drawing.Point(467, 351);
            this.DecodeControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.DecodeControl.Name = "DecodeControl";
            this.DecodeControl.ReadOnly = false;
            this.DecodeControl.Size = new System.Drawing.Size(67, 44);
            this.DecodeControl.SmallSize = new System.Drawing.Size(50, 36);
            this.DecodeControl.TabIndex = 1340;
            this.DecodeControl.Tag = "Decode";
            this.DecodeControl.TheList = null;
            this.DecodeControl.UpdateDisplayFunction = null;
            this.DecodeControl.UpdateRigFunction = null;
            this.DecodeControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // DecodeThresholdControl
            // 
            this.DecodeThresholdControl.Header = "Dec lvl";
            this.DecodeThresholdControl.HighValue = 0;
            this.DecodeThresholdControl.Increment = 0;
            this.DecodeThresholdControl.Location = new System.Drawing.Point(560, 351);
            this.DecodeThresholdControl.LowValue = 0;
            this.DecodeThresholdControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.DecodeThresholdControl.Name = "DecodeThresholdControl";
            this.DecodeThresholdControl.ReadOnly = false;
            this.DecodeThresholdControl.Size = new System.Drawing.Size(67, 44);
            this.DecodeThresholdControl.TabIndex = 1350;
            this.DecodeThresholdControl.Tag = "Dec lvl";
            this.DecodeThresholdControl.UpdateDisplayFunction = null;
            this.DecodeThresholdControl.UpdateRigFunction = null;
            this.DecodeThresholdControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // RXEQButton
            // 
            this.RXEQButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.RXEQButton.Location = new System.Drawing.Point(467, 203);
            this.RXEQButton.Margin = new System.Windows.Forms.Padding(4, 4, 4, 4);
            this.RXEQButton.Name = "RXEQButton";
            this.RXEQButton.Size = new System.Drawing.Size(100, 28);
            this.RXEQButton.TabIndex = 1050;
            this.RXEQButton.Text = "RX Eq";
            this.RXEQButton.UseVisualStyleBackColor = true;
            this.RXEQButton.Click += new System.EventHandler(this.RXEQButton_Click);
            this.RXEQButton.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // TXEQButton
            // 
            this.TXEQButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.TXEQButton.Location = new System.Drawing.Point(560, 98);
            this.TXEQButton.Margin = new System.Windows.Forms.Padding(4, 4, 4, 4);
            this.TXEQButton.Name = "TXEQButton";
            this.TXEQButton.Size = new System.Drawing.Size(100, 28);
            this.TXEQButton.TabIndex = 180;
            this.TXEQButton.Text = "TX Eq";
            this.TXEQButton.UseVisualStyleBackColor = true;
            this.TXEQButton.Click += new System.EventHandler(this.TXEQButton_Click);
            this.TXEQButton.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // TXMonitorControl
            // 
            this.TXMonitorControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.TXMonitorControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.TXMonitorControl.Header = "TXMon";
            this.TXMonitorControl.Location = new System.Drawing.Point(560, 98);
            this.TXMonitorControl.Margin = new System.Windows.Forms.Padding(5, 5, 5, 5);
            this.TXMonitorControl.Name = "TXMonitorControl";
            this.TXMonitorControl.ReadOnly = false;
            this.TXMonitorControl.Size = new System.Drawing.Size(67, 44);
            this.TXMonitorControl.SmallSize = new System.Drawing.Size(50, 36);
            this.TXMonitorControl.TabIndex = 180;
            this.TXMonitorControl.Tag = "TXMon";
            this.TXMonitorControl.TheList = null;
            this.TXMonitorControl.UpdateDisplayFunction = null;
            this.TXMonitorControl.UpdateRigFunction = null;
            this.TXMonitorControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // TXSourceControl
            // 
            this.TXSourceControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.TXSourceControl.ExpandedSize = new System.Drawing.Size(65, 80);
            this.TXSourceControl.Header = "TXSource";
            this.TXSourceControl.Location = new System.Drawing.Point(0, 285);
            this.TXSourceControl.Margin = new System.Windows.Forms.Padding(4, 4, 4, 4);
            this.TXSourceControl.Name = "TXSourceControl";
            this.TXSourceControl.ReadOnly = false;
            this.TXSourceControl.Size = new System.Drawing.Size(65, 36);
            this.TXSourceControl.SmallSize = new System.Drawing.Size(65, 36);
            this.TXSourceControl.TabIndex = 1300;
            this.TXSourceControl.Tag = "TXSource";
            this.TXSourceControl.TheList = null;
            this.TXSourceControl.UpdateDisplayFunction = null;
            this.TXSourceControl.UpdateRigFunction = null;
            this.TXSourceControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // TS590Filters
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.TXSourceControl);
            this.Controls.Add(this.TXMonitorControl);
            this.Controls.Add(this.TXEQButton);
            this.Controls.Add(this.RXEQButton);
            this.Controls.Add(this.DecodeThresholdControl);
            this.Controls.Add(this.DecodeControl);
            this.Controls.Add(this.NBLevelControl);
            this.Controls.Add(this.AGCLevelControl);
            this.Controls.Add(this.NRLevel2Control);
            this.Controls.Add(this.NRLevel1Control);
            this.Controls.Add(this.XmitPowerControl);
            this.Controls.Add(this.ProcessorOutLevelControl);
            this.Controls.Add(this.ProcessorInLevelControl);
            this.Controls.Add(this.CarrierLevelControl);
            this.Controls.Add(this.MicGainControl);
            this.Controls.Add(this.VoxGainControl);
            this.Controls.Add(this.VoxDelayControl);
            this.Controls.Add(this.BreakinDelayControl);
            this.Controls.Add(this.SpeedControl);
            this.Controls.Add(this.FMWidthControl);
            this.Controls.Add(this.AGCControl);
            this.Controls.Add(this.BeatCancelControl);
            this.Controls.Add(this.NotchFreqControl);
            this.Controls.Add(this.NBControl);
            this.Controls.Add(this.NotchWidthControl);
            this.Controls.Add(this.NotchControl);
            this.Controls.Add(this.NRControl);
            this.Controls.Add(this.DataModeControl);
            this.Controls.Add(this.ToneCTCSSControl);
            this.Controls.Add(this.ToneFreqControl);
            this.Controls.Add(this.CTCSSFreqControl);
            this.Controls.Add(this.TXAntControl);
            this.Controls.Add(this.RXAntControl);
            this.Controls.Add(this.DriveAmpControl);
            this.Controls.Add(this.RFAttControl);
            this.Controls.Add(this.PreAmpControl);
            this.Controls.Add(this.ProcessorControl);
            this.Controls.Add(this.ALCBox);
            this.Controls.Add(this.ALCLabel);
            this.Controls.Add(this.CompBox);
            this.Controls.Add(this.CompLabel);
            this.Controls.Add(this.SWRLabel);
            this.Controls.Add(this.SWRBox);
            this.Controls.Add(this.AMHighControl);
            this.Controls.Add(this.AMLowControl);
            this.Controls.Add(this.SSBDWidthControl);
            this.Controls.Add(this.SSBDOffsetControl);
            this.Controls.Add(this.FSKWidthControl);
            this.Controls.Add(this.SSBFMFMDHighControl);
            this.Controls.Add(this.SSBFMFMDLowControl);
            this.Controls.Add(this.CWWidthControl);
            this.Controls.Add(this.CWOffsetControl);
            this.Controls.Add(this.FilterControl);
            this.Margin = new System.Windows.Forms.Padding(4, 4, 4, 4);
            this.Name = "TS590Filters";
            this.Size = new System.Drawing.Size(800, 400);
            this.Load += new System.EventHandler(this.Filters_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private RadioBoxes.Combo FilterControl;
        private RadioBoxes.Combo CWOffsetControl;
        private RadioBoxes.Combo CWWidthControl;
        private RadioBoxes.Combo SSBFMFMDLowControl;
        private RadioBoxes.Combo SSBFMFMDHighControl;
        private RadioBoxes.Combo FSKWidthControl;
        private RadioBoxes.Combo SSBDOffsetControl;
        private RadioBoxes.Combo SSBDWidthControl;
        private RadioBoxes.Combo AMLowControl;
        private RadioBoxes.Combo AMHighControl;
        private System.Windows.Forms.TextBox SWRBox;
        private System.Windows.Forms.Label SWRLabel;
        private System.Windows.Forms.Label CompLabel;
        private System.Windows.Forms.TextBox CompBox;
        private System.Windows.Forms.Label ALCLabel;
        private System.Windows.Forms.TextBox ALCBox;
        private RadioBoxes.Combo DataModeControl;
        private RadioBoxes.Combo ToneCTCSSControl;
        private RadioBoxes.Combo ToneFreqControl;
        private RadioBoxes.Combo CTCSSFreqControl;
        private RadioBoxes.Combo TXAntControl;
        private RadioBoxes.Combo RXAntControl;
        private RadioBoxes.Combo DriveAmpControl;
        private RadioBoxes.Combo RFAttControl;
        private RadioBoxes.Combo PreAmpControl;
        private RadioBoxes.Combo ProcessorControl;
        private RadioBoxes.Combo NRControl;
        private RadioBoxes.Combo NotchControl;
        private RadioBoxes.Combo NotchWidthControl;
        private RadioBoxes.Combo NBControl;
        private RadioBoxes.NumberBox NotchFreqControl;
        private RadioBoxes.Combo BeatCancelControl;
        private RadioBoxes.Combo AGCControl;
        private RadioBoxes.Combo FMWidthControl;
        private RadioBoxes.NumberBox SpeedControl;
        private RadioBoxes.NumberBox BreakinDelayControl;
        private RadioBoxes.NumberBox VoxDelayControl;
        private RadioBoxes.NumberBox VoxGainControl;
        private RadioBoxes.NumberBox MicGainControl;
        private RadioBoxes.NumberBox CarrierLevelControl;
        private RadioBoxes.NumberBox ProcessorInLevelControl;
        private RadioBoxes.NumberBox ProcessorOutLevelControl;
        private RadioBoxes.NumberBox XmitPowerControl;
        private RadioBoxes.NumberBox NRLevel1Control;
        private RadioBoxes.NumberBox NRLevel2Control;
        private RadioBoxes.NumberBox AGCLevelControl;
        private RadioBoxes.NumberBox NBLevelControl;
        private RadioBoxes.Combo DecodeControl;
        private RadioBoxes.NumberBox DecodeThresholdControl;
        private System.Windows.Forms.Button RXEQButton;
        private System.Windows.Forms.Button TXEQButton;
        private RadioBoxes.Combo TXMonitorControl;
        private RadioBoxes.Combo TXSourceControl;
    }
}

namespace Radios
{
    partial class TS2000Filters
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
            this.ToneCTCSSControl = new RadioBoxes.Combo();
            this.ToneFreqControl = new RadioBoxes.Combo();
            this.CTCSSFreqControl = new RadioBoxes.Combo();
            this.TXAntControl = new RadioBoxes.Combo();
            this.RFAttControl = new RadioBoxes.Combo();
            this.PreAmpControl = new RadioBoxes.Combo();
            this.ProcessorControl = new RadioBoxes.Combo();
            this.RXAntControl = new RadioBoxes.Combo();
            this.NRControl = new RadioBoxes.Combo();
            this.AGCControl = new RadioBoxes.Combo();
            this.NBControl = new RadioBoxes.Combo();
            this.BCConntrol = new RadioBoxes.Combo();
            this.BCLevelControl = new RadioBoxes.NumberBox();
            this.NotchControl = new RadioBoxes.Combo();
            this.StepSizeSSBCWFSKControl = new RadioBoxes.Combo();
            this.StepSizeAMFMControl = new RadioBoxes.Combo();
            this.FMWidthControl = new RadioBoxes.Combo();
            this.SpeedControl = new RadioBoxes.NumberBox();
            this.OffsetFreqControl = new RadioBoxes.NumberBox();
            this.BreakinDelayControl = new RadioBoxes.NumberBox();
            this.VoxDelayControl = new RadioBoxes.NumberBox();
            this.VoxGainControl = new RadioBoxes.NumberBox();
            this.MicGainControl = new RadioBoxes.NumberBox();
            this.CarrierLevelControl = new RadioBoxes.NumberBox();
            this.ProcessorInLevelControl = new RadioBoxes.NumberBox();
            this.ProcessorOutLevelControl = new RadioBoxes.NumberBox();
            this.XmitPowerControl = new RadioBoxes.NumberBox();
            this.NRLevel2Control = new RadioBoxes.NumberBox();
            this.AGCLevelControl = new RadioBoxes.NumberBox();
            this.NBLevelControl = new RadioBoxes.NumberBox();
            this.NotchLevelControl = new RadioBoxes.NumberBox();
            this.NRLevel1Control = new RadioBoxes.Combo();
            this.ReverseControl = new RadioBoxes.Combo();
            this.TXMonitorControl = new RadioBoxes.Combo();
            this.SuspendLayout();
            // 
            // SWRBox
            // 
            this.SWRBox.AccessibleName = "SWR";
            this.SWRBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.SWRBox.Location = new System.Drawing.Point(70, 301);
            this.SWRBox.Name = "SWRBox";
            this.SWRBox.ReadOnly = true;
            this.SWRBox.Size = new System.Drawing.Size(50, 20);
            this.SWRBox.TabIndex = 1311;
            this.SWRBox.Tag = "SWR";
            this.SWRBox.Click += new System.EventHandler(this.SWRBox_Click);
            this.SWRBox.Enter += new System.EventHandler(this.SWRBox_Enter);
            this.SWRBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // SWRLabel
            // 
            this.SWRLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.SWRLabel.Location = new System.Drawing.Point(70, 285);
            this.SWRLabel.Name = "SWRLabel";
            this.SWRLabel.Size = new System.Drawing.Size(50, 16);
            this.SWRLabel.TabIndex = 1310;
            this.SWRLabel.Text = "SWR";
            // 
            // CompLabel
            // 
            this.CompLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.CompLabel.Location = new System.Drawing.Point(140, 285);
            this.CompLabel.Name = "CompLabel";
            this.CompLabel.Size = new System.Drawing.Size(50, 16);
            this.CompLabel.TabIndex = 1320;
            this.CompLabel.Text = "Comp";
            // 
            // CompBox
            // 
            this.CompBox.AccessibleName = "compression";
            this.CompBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.CompBox.Location = new System.Drawing.Point(140, 301);
            this.CompBox.Name = "CompBox";
            this.CompBox.ReadOnly = true;
            this.CompBox.Size = new System.Drawing.Size(50, 20);
            this.CompBox.TabIndex = 1321;
            this.CompBox.Tag = "Comp";
            this.CompBox.Click += new System.EventHandler(this.CompBox_Click);
            this.CompBox.Enter += new System.EventHandler(this.CompBox_Enter);
            this.CompBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // ALCLabel
            // 
            this.ALCLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.ALCLabel.Location = new System.Drawing.Point(210, 285);
            this.ALCLabel.Name = "ALCLabel";
            this.ALCLabel.Size = new System.Drawing.Size(50, 16);
            this.ALCLabel.TabIndex = 1330;
            this.ALCLabel.Text = "ALC";
            // 
            // ALCBox
            // 
            this.ALCBox.AccessibleName = "ALC";
            this.ALCBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.ALCBox.Location = new System.Drawing.Point(210, 301);
            this.ALCBox.Name = "ALCBox";
            this.ALCBox.ReadOnly = true;
            this.ALCBox.Size = new System.Drawing.Size(50, 20);
            this.ALCBox.TabIndex = 1331;
            this.ALCBox.Tag = "ALC";
            this.ALCBox.Click += new System.EventHandler(this.ALCBox_Click);
            this.ALCBox.Enter += new System.EventHandler(this.ALCBox_Enter);
            this.ALCBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // AMHighControl
            // 
            this.AMHighControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AMHighControl.Enabled = false;
            this.AMHighControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.AMHighControl.Header = "high";
            this.AMHighControl.Location = new System.Drawing.Point(70, 165);
            this.AMHighControl.Name = "AMHighControl";
            this.AMHighControl.ReadOnly = false;
            this.AMHighControl.Size = new System.Drawing.Size(50, 36);
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
            this.AMLowControl.Location = new System.Drawing.Point(0, 165);
            this.AMLowControl.Name = "AMLowControl";
            this.AMLowControl.ReadOnly = false;
            this.AMLowControl.Size = new System.Drawing.Size(50, 36);
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
            this.SSBDWidthControl.Location = new System.Drawing.Point(70, 165);
            this.SSBDWidthControl.Name = "SSBDWidthControl";
            this.SSBDWidthControl.ReadOnly = false;
            this.SSBDWidthControl.Size = new System.Drawing.Size(50, 36);
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
            this.SSBDOffsetControl.Location = new System.Drawing.Point(0, 165);
            this.SSBDOffsetControl.Name = "SSBDOffsetControl";
            this.SSBDOffsetControl.ReadOnly = false;
            this.SSBDOffsetControl.Size = new System.Drawing.Size(50, 36);
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
            this.FSKWidthControl.Location = new System.Drawing.Point(70, 165);
            this.FSKWidthControl.Name = "FSKWidthControl";
            this.FSKWidthControl.ReadOnly = false;
            this.FSKWidthControl.Size = new System.Drawing.Size(50, 36);
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
            this.SSBFMFMDHighControl.Location = new System.Drawing.Point(70, 165);
            this.SSBFMFMDHighControl.Name = "SSBFMFMDHighControl";
            this.SSBFMFMDHighControl.ReadOnly = false;
            this.SSBFMFMDHighControl.Size = new System.Drawing.Size(50, 36);
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
            this.SSBFMFMDLowControl.Location = new System.Drawing.Point(0, 165);
            this.SSBFMFMDLowControl.Name = "SSBFMFMDLowControl";
            this.SSBFMFMDLowControl.ReadOnly = false;
            this.SSBFMFMDLowControl.Size = new System.Drawing.Size(50, 36);
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
            this.CWWidthControl.Location = new System.Drawing.Point(0, 165);
            this.CWWidthControl.Name = "CWWidthControl";
            this.CWWidthControl.ReadOnly = false;
            this.CWWidthControl.Size = new System.Drawing.Size(50, 36);
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
            this.CWOffsetControl.Location = new System.Drawing.Point(70, 165);
            this.CWOffsetControl.Name = "CWOffsetControl";
            this.CWOffsetControl.ReadOnly = false;
            this.CWOffsetControl.Size = new System.Drawing.Size(50, 36);
            this.CWOffsetControl.SmallSize = new System.Drawing.Size(50, 36);
            this.CWOffsetControl.TabIndex = 1020;
            this.CWOffsetControl.Tag = "offset";
            this.CWOffsetControl.TheList = null;
            this.CWOffsetControl.UpdateDisplayFunction = null;
            this.CWOffsetControl.UpdateRigFunction = null;
            this.CWOffsetControl.Visible = false;
            this.CWOffsetControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // ToneCTCSSControl
            // 
            this.ToneCTCSSControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ToneCTCSSControl.Enabled = false;
            this.ToneCTCSSControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.ToneCTCSSControl.Header = "Tone/CT";
            this.ToneCTCSSControl.Location = new System.Drawing.Point(70, 0);
            this.ToneCTCSSControl.Name = "ToneCTCSSControl";
            this.ToneCTCSSControl.ReadOnly = false;
            this.ToneCTCSSControl.Size = new System.Drawing.Size(50, 36);
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
            this.ToneFreqControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.ToneFreqControl.Header = "ToneFreq";
            this.ToneFreqControl.Location = new System.Drawing.Point(140, 0);
            this.ToneFreqControl.Name = "ToneFreqControl";
            this.ToneFreqControl.ReadOnly = false;
            this.ToneFreqControl.Size = new System.Drawing.Size(50, 36);
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
            this.CTCSSFreqControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.CTCSSFreqControl.Header = "CTFreq";
            this.CTCSSFreqControl.Location = new System.Drawing.Point(210, 0);
            this.CTCSSFreqControl.Name = "CTCSSFreqControl";
            this.CTCSSFreqControl.ReadOnly = false;
            this.CTCSSFreqControl.Size = new System.Drawing.Size(50, 36);
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
            this.TXAntControl.ExpandedSize = new System.Drawing.Size(50, 100);
            this.TXAntControl.Header = "Ant";
            this.TXAntControl.Location = new System.Drawing.Point(0, 40);
            this.TXAntControl.Name = "TXAntControl";
            this.TXAntControl.ReadOnly = false;
            this.TXAntControl.Size = new System.Drawing.Size(50, 36);
            this.TXAntControl.SmallSize = new System.Drawing.Size(50, 36);
            this.TXAntControl.TabIndex = 50;
            this.TXAntControl.Tag = "Ant";
            this.TXAntControl.TheList = null;
            this.TXAntControl.UpdateDisplayFunction = null;
            this.TXAntControl.UpdateRigFunction = null;
            this.TXAntControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // RFAttControl
            // 
            this.RFAttControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.RFAttControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.RFAttControl.Header = "RF Att";
            this.RFAttControl.Location = new System.Drawing.Point(140, 40);
            this.RFAttControl.Name = "RFAttControl";
            this.RFAttControl.ReadOnly = false;
            this.RFAttControl.Size = new System.Drawing.Size(50, 36);
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
            this.PreAmpControl.Location = new System.Drawing.Point(210, 40);
            this.PreAmpControl.Name = "PreAmpControl";
            this.PreAmpControl.ReadOnly = false;
            this.PreAmpControl.Size = new System.Drawing.Size(50, 36);
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
            this.ProcessorControl.Location = new System.Drawing.Point(210, 80);
            this.ProcessorControl.Name = "ProcessorControl";
            this.ProcessorControl.ReadOnly = false;
            this.ProcessorControl.Size = new System.Drawing.Size(50, 36);
            this.ProcessorControl.SmallSize = new System.Drawing.Size(50, 36);
            this.ProcessorControl.TabIndex = 150;
            this.ProcessorControl.Tag = "Spch Proc";
            this.ProcessorControl.TheList = null;
            this.ProcessorControl.UpdateDisplayFunction = null;
            this.ProcessorControl.UpdateRigFunction = null;
            this.ProcessorControl.Visible = false;
            this.ProcessorControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // RXAntControl
            // 
            this.RXAntControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.RXAntControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.RXAntControl.Header = "RXAnt";
            this.RXAntControl.Location = new System.Drawing.Point(70, 40);
            this.RXAntControl.Name = "RXAntControl";
            this.RXAntControl.ReadOnly = false;
            this.RXAntControl.Size = new System.Drawing.Size(50, 36);
            this.RXAntControl.SmallSize = new System.Drawing.Size(50, 36);
            this.RXAntControl.TabIndex = 60;
            this.RXAntControl.Tag = "RXAnt";
            this.RXAntControl.TheList = null;
            this.RXAntControl.UpdateDisplayFunction = null;
            this.RXAntControl.UpdateRigFunction = null;
            this.RXAntControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // NRControl
            // 
            this.NRControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NRControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.NRControl.Header = "NReduc";
            this.NRControl.Location = new System.Drawing.Point(140, 165);
            this.NRControl.Name = "NRControl";
            this.NRControl.ReadOnly = false;
            this.NRControl.Size = new System.Drawing.Size(50, 36);
            this.NRControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NRControl.TabIndex = 1030;
            this.NRControl.Tag = "NReduc";
            this.NRControl.TheList = null;
            this.NRControl.UpdateDisplayFunction = null;
            this.NRControl.UpdateRigFunction = null;
            this.NRControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // AGCControl
            // 
            this.AGCControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AGCControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.AGCControl.Header = "AGC";
            this.AGCControl.Location = new System.Drawing.Point(0, 205);
            this.AGCControl.Name = "AGCControl";
            this.AGCControl.ReadOnly = false;
            this.AGCControl.Size = new System.Drawing.Size(50, 36);
            this.AGCControl.SmallSize = new System.Drawing.Size(50, 36);
            this.AGCControl.TabIndex = 1100;
            this.AGCControl.Tag = "AGC";
            this.AGCControl.TheList = null;
            this.AGCControl.UpdateDisplayFunction = null;
            this.AGCControl.UpdateRigFunction = null;
            this.AGCControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // NBControl
            // 
            this.NBControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NBControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.NBControl.Header = "N.B.";
            this.NBControl.Location = new System.Drawing.Point(140, 205);
            this.NBControl.Name = "NBControl";
            this.NBControl.ReadOnly = false;
            this.NBControl.Size = new System.Drawing.Size(50, 36);
            this.NBControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NBControl.TabIndex = 1120;
            this.NBControl.Tag = "N.B.";
            this.NBControl.TheList = null;
            this.NBControl.UpdateDisplayFunction = null;
            this.NBControl.UpdateRigFunction = null;
            this.NBControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // BCConntrol
            // 
            this.BCConntrol.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.BCConntrol.ExpandedSize = new System.Drawing.Size(50, 60);
            this.BCConntrol.Header = "B.C.";
            this.BCConntrol.Location = new System.Drawing.Point(280, 205);
            this.BCConntrol.Name = "BCConntrol";
            this.BCConntrol.ReadOnly = false;
            this.BCConntrol.Size = new System.Drawing.Size(50, 36);
            this.BCConntrol.SmallSize = new System.Drawing.Size(50, 36);
            this.BCConntrol.TabIndex = 1140;
            this.BCConntrol.Tag = "B.C.";
            this.BCConntrol.TheList = null;
            this.BCConntrol.UpdateDisplayFunction = null;
            this.BCConntrol.UpdateRigFunction = null;
            this.BCConntrol.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // BCLevelControl
            // 
            this.BCLevelControl.AccessibleName = "bc level";
            this.BCLevelControl.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.BCLevelControl.Header = "BCLvl";
            this.BCLevelControl.HighValue = 0;
            this.BCLevelControl.Increment = 0;
            this.BCLevelControl.Location = new System.Drawing.Point(350, 205);
            this.BCLevelControl.LowValue = 0;
            this.BCLevelControl.Name = "BCLevelControl";
            this.BCLevelControl.ReadOnly = false;
            this.BCLevelControl.Size = new System.Drawing.Size(50, 36);
            this.BCLevelControl.TabIndex = 1150;
            this.BCLevelControl.Tag = "BCLvl";
            this.BCLevelControl.UpdateDisplayFunction = null;
            this.BCLevelControl.UpdateRigFunction = null;
            // 
            // NotchControl
            // 
            this.NotchControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NotchControl.ExpandedSize = new System.Drawing.Size(50, 60);
            this.NotchControl.Header = "Notch";
            this.NotchControl.Location = new System.Drawing.Point(420, 205);
            this.NotchControl.Name = "NotchControl";
            this.NotchControl.ReadOnly = false;
            this.NotchControl.Size = new System.Drawing.Size(50, 36);
            this.NotchControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NotchControl.TabIndex = 1160;
            this.NotchControl.Tag = "notch";
            this.NotchControl.TheList = null;
            this.NotchControl.UpdateDisplayFunction = null;
            this.NotchControl.UpdateRigFunction = null;
            this.NotchControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // StepSizeSSBCWFSKControl
            // 
            this.StepSizeSSBCWFSKControl.AccessibleName = "";
            this.StepSizeSSBCWFSKControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.StepSizeSSBCWFSKControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.StepSizeSSBCWFSKControl.Header = "stepSZ";
            this.StepSizeSSBCWFSKControl.Location = new System.Drawing.Point(280, 165);
            this.StepSizeSSBCWFSKControl.Name = "StepSizeSSBCWFSKControl";
            this.StepSizeSSBCWFSKControl.ReadOnly = false;
            this.StepSizeSSBCWFSKControl.Size = new System.Drawing.Size(50, 36);
            this.StepSizeSSBCWFSKControl.SmallSize = new System.Drawing.Size(50, 36);
            this.StepSizeSSBCWFSKControl.TabIndex = 1050;
            this.StepSizeSSBCWFSKControl.Tag = "stepSZ";
            this.StepSizeSSBCWFSKControl.TheList = null;
            this.StepSizeSSBCWFSKControl.UpdateDisplayFunction = null;
            this.StepSizeSSBCWFSKControl.UpdateRigFunction = null;
            this.StepSizeSSBCWFSKControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // StepSizeAMFMControl
            // 
            this.StepSizeAMFMControl.AccessibleName = "";
            this.StepSizeAMFMControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.StepSizeAMFMControl.Enabled = false;
            this.StepSizeAMFMControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.StepSizeAMFMControl.Header = "StepSZ";
            this.StepSizeAMFMControl.Location = new System.Drawing.Point(280, 165);
            this.StepSizeAMFMControl.Name = "StepSizeAMFMControl";
            this.StepSizeAMFMControl.ReadOnly = false;
            this.StepSizeAMFMControl.Size = new System.Drawing.Size(50, 36);
            this.StepSizeAMFMControl.SmallSize = new System.Drawing.Size(50, 36);
            this.StepSizeAMFMControl.TabIndex = 1050;
            this.StepSizeAMFMControl.Tag = "StepSZ";
            this.StepSizeAMFMControl.TheList = null;
            this.StepSizeAMFMControl.UpdateDisplayFunction = null;
            this.StepSizeAMFMControl.UpdateRigFunction = null;
            this.StepSizeAMFMControl.Visible = false;
            this.StepSizeAMFMControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // FMWidthControl
            // 
            this.FMWidthControl.AccessibleName = "";
            this.FMWidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FMWidthControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.FMWidthControl.Header = "width";
            this.FMWidthControl.Location = new System.Drawing.Point(280, 0);
            this.FMWidthControl.Name = "FMWidthControl";
            this.FMWidthControl.ReadOnly = false;
            this.FMWidthControl.Size = new System.Drawing.Size(50, 36);
            this.FMWidthControl.SmallSize = new System.Drawing.Size(50, 36);
            this.FMWidthControl.TabIndex = 45;
            this.FMWidthControl.Tag = "width";
            this.FMWidthControl.TheList = null;
            this.FMWidthControl.UpdateDisplayFunction = null;
            this.FMWidthControl.UpdateRigFunction = null;
            this.FMWidthControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // SpeedControl
            // 
            this.SpeedControl.Header = "Speed";
            this.SpeedControl.HighValue = 0;
            this.SpeedControl.Increment = 0;
            this.SpeedControl.Location = new System.Drawing.Point(0, 0);
            this.SpeedControl.LowValue = 0;
            this.SpeedControl.Name = "SpeedControl";
            this.SpeedControl.ReadOnly = false;
            this.SpeedControl.Size = new System.Drawing.Size(50, 36);
            this.SpeedControl.TabIndex = 20;
            this.SpeedControl.Tag = "Speed";
            this.SpeedControl.UpdateDisplayFunction = null;
            this.SpeedControl.UpdateRigFunction = null;
            // 
            // OffsetFreqControl
            // 
            this.OffsetFreqControl.Header = "Ofst(KHZ)";
            this.OffsetFreqControl.HighValue = 0;
            this.OffsetFreqControl.Increment = 0;
            this.OffsetFreqControl.Location = new System.Drawing.Point(0, 0);
            this.OffsetFreqControl.LowValue = 0;
            this.OffsetFreqControl.Name = "OffsetFreqControl";
            this.OffsetFreqControl.ReadOnly = false;
            this.OffsetFreqControl.Size = new System.Drawing.Size(50, 36);
            this.OffsetFreqControl.TabIndex = 20;
            this.OffsetFreqControl.Tag = "Offset";
            this.OffsetFreqControl.UpdateDisplayFunction = null;
            this.OffsetFreqControl.UpdateRigFunction = null;
            // 
            // BreakinDelayControl
            // 
            this.BreakinDelayControl.Header = "BkDelay";
            this.BreakinDelayControl.HighValue = 0;
            this.BreakinDelayControl.Increment = 0;
            this.BreakinDelayControl.Location = new System.Drawing.Point(0, 80);
            this.BreakinDelayControl.LowValue = 0;
            this.BreakinDelayControl.Name = "BreakinDelayControl";
            this.BreakinDelayControl.ReadOnly = false;
            this.BreakinDelayControl.Size = new System.Drawing.Size(50, 36);
            this.BreakinDelayControl.TabIndex = 100;
            this.BreakinDelayControl.Tag = "BkDelay";
            this.BreakinDelayControl.UpdateDisplayFunction = null;
            this.BreakinDelayControl.UpdateRigFunction = null;
            // 
            // VoxDelayControl
            // 
            this.VoxDelayControl.Header = "VoxDel";
            this.VoxDelayControl.HighValue = 0;
            this.VoxDelayControl.Increment = 0;
            this.VoxDelayControl.Location = new System.Drawing.Point(0, 80);
            this.VoxDelayControl.LowValue = 0;
            this.VoxDelayControl.Name = "VoxDelayControl";
            this.VoxDelayControl.ReadOnly = false;
            this.VoxDelayControl.Size = new System.Drawing.Size(50, 36);
            this.VoxDelayControl.TabIndex = 100;
            this.VoxDelayControl.Tag = "VoxDel";
            this.VoxDelayControl.UpdateDisplayFunction = null;
            this.VoxDelayControl.UpdateRigFunction = null;
            // 
            // VoxGainControl
            // 
            this.VoxGainControl.Header = "Vox gain";
            this.VoxGainControl.HighValue = 0;
            this.VoxGainControl.Increment = 0;
            this.VoxGainControl.Location = new System.Drawing.Point(70, 80);
            this.VoxGainControl.LowValue = 0;
            this.VoxGainControl.Name = "VoxGainControl";
            this.VoxGainControl.ReadOnly = false;
            this.VoxGainControl.Size = new System.Drawing.Size(50, 36);
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
            this.MicGainControl.Location = new System.Drawing.Point(140, 80);
            this.MicGainControl.LowValue = 0;
            this.MicGainControl.Name = "MicGainControl";
            this.MicGainControl.ReadOnly = false;
            this.MicGainControl.Size = new System.Drawing.Size(50, 36);
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
            this.CarrierLevelControl.Location = new System.Drawing.Point(70, 0);
            this.CarrierLevelControl.LowValue = 0;
            this.CarrierLevelControl.Name = "CarrierLevelControl";
            this.CarrierLevelControl.ReadOnly = false;
            this.CarrierLevelControl.Size = new System.Drawing.Size(50, 36);
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
            this.ProcessorInLevelControl.Location = new System.Drawing.Point(280, 80);
            this.ProcessorInLevelControl.LowValue = 0;
            this.ProcessorInLevelControl.Name = "ProcessorInLevelControl";
            this.ProcessorInLevelControl.ReadOnly = false;
            this.ProcessorInLevelControl.Size = new System.Drawing.Size(50, 36);
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
            this.ProcessorOutLevelControl.Location = new System.Drawing.Point(350, 80);
            this.ProcessorOutLevelControl.LowValue = 0;
            this.ProcessorOutLevelControl.Name = "ProcessorOutLevelControl";
            this.ProcessorOutLevelControl.ReadOnly = false;
            this.ProcessorOutLevelControl.Size = new System.Drawing.Size(50, 36);
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
            this.XmitPowerControl.Location = new System.Drawing.Point(0, 285);
            this.XmitPowerControl.LowValue = 0;
            this.XmitPowerControl.Name = "XmitPowerControl";
            this.XmitPowerControl.ReadOnly = false;
            this.XmitPowerControl.Size = new System.Drawing.Size(50, 36);
            this.XmitPowerControl.TabIndex = 1300;
            this.XmitPowerControl.Tag = "Power";
            this.XmitPowerControl.UpdateDisplayFunction = null;
            this.XmitPowerControl.UpdateRigFunction = null;
            // 
            // NRLevel2Control
            // 
            this.NRLevel2Control.Header = "NRLevel";
            this.NRLevel2Control.HighValue = 0;
            this.NRLevel2Control.Increment = 0;
            this.NRLevel2Control.Location = new System.Drawing.Point(210, 165);
            this.NRLevel2Control.LowValue = 0;
            this.NRLevel2Control.Name = "NRLevel2Control";
            this.NRLevel2Control.ReadOnly = false;
            this.NRLevel2Control.Size = new System.Drawing.Size(50, 36);
            this.NRLevel2Control.TabIndex = 1040;
            this.NRLevel2Control.Tag = "NRLevel";
            this.NRLevel2Control.UpdateDisplayFunction = null;
            this.NRLevel2Control.UpdateRigFunction = null;
            // 
            // AGCLevelControl
            // 
            this.AGCLevelControl.Header = "AGCLvl";
            this.AGCLevelControl.HighValue = 0;
            this.AGCLevelControl.Increment = 0;
            this.AGCLevelControl.Location = new System.Drawing.Point(70, 205);
            this.AGCLevelControl.LowValue = 0;
            this.AGCLevelControl.Name = "AGCLevelControl";
            this.AGCLevelControl.ReadOnly = false;
            this.AGCLevelControl.Size = new System.Drawing.Size(50, 36);
            this.AGCLevelControl.TabIndex = 1110;
            this.AGCLevelControl.Tag = "AGCLvl";
            this.AGCLevelControl.UpdateDisplayFunction = null;
            this.AGCLevelControl.UpdateRigFunction = null;
            // 
            // NBLevelControl
            // 
            this.NBLevelControl.Header = "NBLvl";
            this.NBLevelControl.HighValue = 0;
            this.NBLevelControl.Increment = 0;
            this.NBLevelControl.Location = new System.Drawing.Point(210, 205);
            this.NBLevelControl.LowValue = 0;
            this.NBLevelControl.Name = "NBLevelControl";
            this.NBLevelControl.ReadOnly = false;
            this.NBLevelControl.Size = new System.Drawing.Size(50, 36);
            this.NBLevelControl.TabIndex = 1130;
            this.NBLevelControl.Tag = "NBLvl";
            this.NBLevelControl.UpdateDisplayFunction = null;
            this.NBLevelControl.UpdateRigFunction = null;
            // 
            // NotchLevelControl
            // 
            this.NotchLevelControl.Header = "NtchLvl";
            this.NotchLevelControl.HighValue = 0;
            this.NotchLevelControl.Increment = 0;
            this.NotchLevelControl.Location = new System.Drawing.Point(490, 205);
            this.NotchLevelControl.LowValue = 0;
            this.NotchLevelControl.Name = "NotchLevelControl";
            this.NotchLevelControl.ReadOnly = false;
            this.NotchLevelControl.Size = new System.Drawing.Size(50, 36);
            this.NotchLevelControl.TabIndex = 1170;
            this.NotchLevelControl.Tag = "NtchLvl";
            this.NotchLevelControl.UpdateDisplayFunction = null;
            this.NotchLevelControl.UpdateRigFunction = null;
            // 
            // NRLevel1Control
            // 
            this.NRLevel1Control.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NRLevel1Control.ExpandedSize = new System.Drawing.Size(50, 80);
            this.NRLevel1Control.Header = "NRLevel";
            this.NRLevel1Control.Location = new System.Drawing.Point(210, 165);
            this.NRLevel1Control.Name = "NRLevel1Control";
            this.NRLevel1Control.ReadOnly = false;
            this.NRLevel1Control.Size = new System.Drawing.Size(50, 36);
            this.NRLevel1Control.SmallSize = new System.Drawing.Size(50, 36);
            this.NRLevel1Control.TabIndex = 1040;
            this.NRLevel1Control.Tag = "NRLevel";
            this.NRLevel1Control.TheList = null;
            this.NRLevel1Control.UpdateDisplayFunction = null;
            this.NRLevel1Control.UpdateRigFunction = null;
            // 
            // ReverseControl
            // 
            this.ReverseControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ReverseControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.ReverseControl.Header = "reverse";
            this.ReverseControl.Location = new System.Drawing.Point(350, 0);
            this.ReverseControl.Name = "ReverseControl";
            this.ReverseControl.ReadOnly = false;
            this.ReverseControl.Size = new System.Drawing.Size(50, 36);
            this.ReverseControl.SmallSize = new System.Drawing.Size(50, 36);
            this.ReverseControl.TabIndex = 47;
            this.ReverseControl.Tag = "reverse";
            this.ReverseControl.TheList = null;
            this.ReverseControl.UpdateDisplayFunction = null;
            this.ReverseControl.UpdateRigFunction = null;
            this.ReverseControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // TXMonitorControl
            // 
            this.TXMonitorControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.TXMonitorControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.TXMonitorControl.Header = "TXMon";
            this.TXMonitorControl.Location = new System.Drawing.Point(420, 80);
            this.TXMonitorControl.Name = "TXMonitorControl";
            this.TXMonitorControl.ReadOnly = false;
            this.TXMonitorControl.Size = new System.Drawing.Size(50, 36);
            this.TXMonitorControl.SmallSize = new System.Drawing.Size(50, 36);
            this.TXMonitorControl.TabIndex = 180;
            this.TXMonitorControl.Tag = "TXMon";
            this.TXMonitorControl.TheList = null;
            this.TXMonitorControl.UpdateDisplayFunction = null;
            this.TXMonitorControl.UpdateRigFunction = null;
            this.TXMonitorControl.BoxKeydown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // TS2000Filters
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.TXMonitorControl);
            this.Controls.Add(this.ReverseControl);
            this.Controls.Add(this.NRLevel1Control);
            this.Controls.Add(this.NotchLevelControl);
            this.Controls.Add(this.NBLevelControl);
            this.Controls.Add(this.AGCLevelControl);
            this.Controls.Add(this.NRLevel2Control);
            this.Controls.Add(this.XmitPowerControl);
            this.Controls.Add(this.ProcessorOutLevelControl);
            this.Controls.Add(this.ProcessorInLevelControl);
            this.Controls.Add(this.CarrierLevelControl);
            this.Controls.Add(this.MicGainControl);
            this.Controls.Add(this.VoxGainControl);
            this.Controls.Add(this.VoxDelayControl);
            this.Controls.Add(this.BreakinDelayControl);
            this.Controls.Add(this.OffsetFreqControl);
            this.Controls.Add(this.SpeedControl);
            this.Controls.Add(this.FMWidthControl);
            this.Controls.Add(this.StepSizeAMFMControl);
            this.Controls.Add(this.StepSizeSSBCWFSKControl);
            this.Controls.Add(this.NotchControl);
            this.Controls.Add(this.BCLevelControl);
            this.Controls.Add(this.BCConntrol);
            this.Controls.Add(this.NBControl);
            this.Controls.Add(this.AGCControl);
            this.Controls.Add(this.NRControl);
            this.Controls.Add(this.RXAntControl);
            this.Controls.Add(this.ToneCTCSSControl);
            this.Controls.Add(this.ToneFreqControl);
            this.Controls.Add(this.CTCSSFreqControl);
            this.Controls.Add(this.TXAntControl);
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
            this.Name = "TS2000Filters";
            this.Size = new System.Drawing.Size(600, 325);
            this.Load += new System.EventHandler(this.Filters_Load);
            this.ControlRemoved += new System.Windows.Forms.ControlEventHandler(this.TS2000Filters_ControlRemoved);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

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
        private RadioBoxes.Combo ToneCTCSSControl;
        private RadioBoxes.Combo ToneFreqControl;
        private RadioBoxes.Combo CTCSSFreqControl;
        private RadioBoxes.Combo TXAntControl;
        private RadioBoxes.Combo RFAttControl;
        private RadioBoxes.Combo PreAmpControl;
        private RadioBoxes.Combo ProcessorControl;
        private RadioBoxes.Combo RXAntControl;
        private RadioBoxes.Combo NRControl;
        private RadioBoxes.Combo AGCControl;
        private RadioBoxes.Combo NBControl;
        private RadioBoxes.Combo BCConntrol;
        private RadioBoxes.NumberBox BCLevelControl;
        private RadioBoxes.Combo NotchControl;
        private RadioBoxes.Combo StepSizeSSBCWFSKControl;
        private RadioBoxes.Combo StepSizeAMFMControl;
        private RadioBoxes.Combo FMWidthControl;
        private RadioBoxes.NumberBox SpeedControl;
        private RadioBoxes.NumberBox OffsetFreqControl;
        private RadioBoxes.NumberBox BreakinDelayControl;
        private RadioBoxes.NumberBox VoxDelayControl;
        private RadioBoxes.NumberBox VoxGainControl;
        private RadioBoxes.NumberBox MicGainControl;
        private RadioBoxes.NumberBox CarrierLevelControl;
        private RadioBoxes.NumberBox ProcessorInLevelControl;
        private RadioBoxes.NumberBox ProcessorOutLevelControl;
        private RadioBoxes.NumberBox XmitPowerControl;
        private RadioBoxes.NumberBox NRLevel2Control;
        private RadioBoxes.NumberBox AGCLevelControl;
        private RadioBoxes.NumberBox NBLevelControl;
        private RadioBoxes.NumberBox NotchLevelControl;
        private RadioBoxes.Combo NRLevel1Control;
        private RadioBoxes.Combo ReverseControl;
        private RadioBoxes.Combo TXMonitorControl;
    }
}

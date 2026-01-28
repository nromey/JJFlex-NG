namespace Radios
{
	partial class Flex6300Filters
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
            if (disposing)
            {
                components?.Dispose();
                licenseToolTip?.Dispose();
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
            this.PanBox = new System.Windows.Forms.TextBox();
            this.PanLowLabel = new System.Windows.Forms.Label();
            this.PanLowBox = new System.Windows.Forms.TextBox();
            this.PanHighLabel = new System.Windows.Forms.Label();
            this.PanHighBox = new System.Windows.Forms.TextBox();
            this.ChangeButton = new System.Windows.Forms.Button();
            this.SaveButton = new System.Windows.Forms.Button();
            this.EraseButton = new System.Windows.Forms.Button();
            this.TNFButton = new System.Windows.Forms.Button();
            this.TNFEnableButton = new System.Windows.Forms.Button();
            this.EmphasisControl = new RadioBoxes.Combo();
            this.OffsetControl = new RadioBoxes.NumberBox();
            this.SquelchLevelControl = new RadioBoxes.NumberBox();
            this.SquelchControl = new RadioBoxes.Combo();
            this.ToneFrequencyControl = new RadioBoxes.Combo();
            this.ToneModeControl = new RadioBoxes.Combo();
            this.APFLevelControl = new RadioBoxes.NumberBox();
            this.APFControl = new RadioBoxes.Combo();
            this.ANFLevelControl = new RadioBoxes.NumberBox();
            this.ANFControl = new RadioBoxes.Combo();
            this.VoxDelayControl = new RadioBoxes.NumberBox();
            this.VoxGainControl = new RadioBoxes.NumberBox();
            this.XmitPowerControl = new RadioBoxes.NumberBox();
            this.ProcessorSettingControl = new RadioBoxes.Combo();
            this.ProcessorOnControl = new RadioBoxes.Combo();
            this.SWRControl = new RadioBoxes.InfoBox();
            this.MicPeakBox = new RadioBoxes.InfoBox();
            this.MicGainControl = new RadioBoxes.NumberBox();
            this.MonitorPanControl = new RadioBoxes.NumberBox();
            this.KeyerSpeedControl = new RadioBoxes.NumberBox();
            this.KeyerControl = new RadioBoxes.Combo();
            this.CWReverseControl = new RadioBoxes.Combo();
            this.SidetoneGainControl = new RadioBoxes.NumberBox();
            this.SidetonePitchControl = new RadioBoxes.NumberBox();
            this.BreakinDelayControl = new RadioBoxes.NumberBox();
            this.NoiseReductionLevelControl = new RadioBoxes.NumberBox();
            this.NoiseReductionControl = new RadioBoxes.Combo();
            this.NoiseBlankerLevelControl = new RadioBoxes.NumberBox();
            this.NoiseBlankerControl = new RadioBoxes.Combo();
            this.AGCThresholdControl = new RadioBoxes.NumberBox();
            this.AGCSpeedControl = new RadioBoxes.Combo();
            this.FilterHighControl = new RadioBoxes.NumberBox();
            this.FilterLowControl = new RadioBoxes.NumberBox();
            this.FM1750Control = new RadioBoxes.Combo();
            this.BinauralControl = new RadioBoxes.Combo();
            this.PlayControl = new RadioBoxes.Combo();
            this.RecordControl = new RadioBoxes.Combo();
            this.ExportButton = new System.Windows.Forms.Button();
            this.ImportButton = new System.Windows.Forms.Button();
            this.RXAntControl = new RadioBoxes.Combo();
            this.CWLControl = new RadioBoxes.Combo();
            this.CompanderControl = new RadioBoxes.Combo();
            this.CompanderLevelControl = new RadioBoxes.NumberBox();
            this.TXFilterLowControl = new RadioBoxes.NumberBox();
            this.TXFilterHighControl = new RadioBoxes.NumberBox();
            this.MicBoostControl = new RadioBoxes.Combo();
            this.MicBiasControl = new RadioBoxes.Combo();
            this.MonitorControl = new RadioBoxes.Combo();
            this.SBMonitorLevelControl = new RadioBoxes.NumberBox();
            this.SBMonitorPanControl = new RadioBoxes.NumberBox();
            this.RXEqButton = new System.Windows.Forms.Button();
            this.TXEqButton = new System.Windows.Forms.Button();
            this.AMCarrierLevelControl = new RadioBoxes.NumberBox();
            this.InfoButton = new System.Windows.Forms.Button();
            this.WidebandNoiseBlankerControl = new RadioBoxes.Combo();
            this.WidebandNoiseBlankerLevelControl = new RadioBoxes.NumberBox();
            this.DAXTXControl = new RadioBoxes.Combo();
            this.AutoprocControl = new System.Windows.Forms.ComboBox();
            this.AutoprocLabel = new System.Windows.Forms.Label();
            this.TunePowerControl = new RadioBoxes.NumberBox();
            this.PATempBox = new RadioBoxes.InfoBox();
            this.VoltsBox = new RadioBoxes.InfoBox();
            this.RFGainControl = new RadioBoxes.NumberBox();
            this.OffsetDirectionControl = new RadioBoxes.Combo();
            this.TXAntControl = new RadioBoxes.Combo();
            this.DiversityControl = new RadioBoxes.Combo();
            this.EscButton = new System.Windows.Forms.Button();
            this.DiversityStatusControl = new RadioBoxes.InfoBox();
            this.SuspendLayout();
            // 
            // PanBox
            // 
            this.PanBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Document;
            this.PanBox.Location = new System.Drawing.Point(0, 0);
            this.PanBox.Name = "PanBox";
            this.PanBox.Size = new System.Drawing.Size(300, 20);
            this.PanBox.TabIndex = 10;
            this.PanBox.MouseClick += new System.Windows.Forms.MouseEventHandler(this.PanBox_MouseClick);
            this.PanBox.Enter += new System.EventHandler(this.PanBox_Enter);
            this.PanBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.PanBox_KeyDown);
            this.PanBox.Leave += new System.EventHandler(this.PanBox_Leave);
            // 
            // PanLowLabel
            // 
            this.PanLowLabel.AutoSize = true;
            this.PanLowLabel.Location = new System.Drawing.Point(0, 20);
            this.PanLowLabel.Name = "PanLowLabel";
            this.PanLowLabel.Size = new System.Drawing.Size(55, 13);
            this.PanLowLabel.TabIndex = 20;
            this.PanLowLabel.Text = "Pan Low: ";
            // 
            // PanLowBox
            // 
            this.PanLowBox.Location = new System.Drawing.Point(57, 20);
            this.PanLowBox.Name = "PanLowBox";
            this.PanLowBox.Size = new System.Drawing.Size(100, 20);
            this.PanLowBox.TabIndex = 21;
            this.PanLowBox.Tag = "";
            this.PanLowBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.PanLowBox_KeyDown);
            // 
            // PanHighLabel
            // 
            this.PanHighLabel.AutoSize = true;
            this.PanHighLabel.Location = new System.Drawing.Point(200, 20);
            this.PanHighLabel.Name = "PanHighLabel";
            this.PanHighLabel.Size = new System.Drawing.Size(54, 13);
            this.PanHighLabel.TabIndex = 25;
            this.PanHighLabel.Text = "Pan High:";
            // 
            // PanHighBox
            // 
            this.PanHighBox.Location = new System.Drawing.Point(259, 20);
            this.PanHighBox.Name = "PanHighBox";
            this.PanHighBox.Size = new System.Drawing.Size(100, 20);
            this.PanHighBox.TabIndex = 26;
            this.PanHighBox.Tag = "";
            this.PanHighBox.KeyDown += new System.Windows.Forms.KeyEventHandler(this.PanHighBox_KeyDown);
            // 
            // ChangeButton
            // 
            this.ChangeButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.ChangeButton.AutoSize = true;
            this.ChangeButton.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.ChangeButton.Location = new System.Drawing.Point(420, 20);
            this.ChangeButton.Name = "ChangeButton";
            this.ChangeButton.Size = new System.Drawing.Size(54, 23);
            this.ChangeButton.TabIndex = 27;
            this.ChangeButton.Text = "Change";
            this.ChangeButton.UseVisualStyleBackColor = true;
            this.ChangeButton.Click += new System.EventHandler(this.ChangeButton_Click);
            this.ChangeButton.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // SaveButton
            // 
            this.SaveButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.SaveButton.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.SaveButton.Location = new System.Drawing.Point(490, 20);
            this.SaveButton.Name = "SaveButton";
            this.SaveButton.Size = new System.Drawing.Size(50, 20);
            this.SaveButton.TabIndex = 28;
            this.SaveButton.Text = "Save";
            this.SaveButton.UseVisualStyleBackColor = true;
            this.SaveButton.Click += new System.EventHandler(this.SaveButton_Click);
            this.SaveButton.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // EraseButton
            // 
            this.EraseButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.EraseButton.Location = new System.Drawing.Point(560, 20);
            this.EraseButton.Name = "EraseButton";
            this.EraseButton.Size = new System.Drawing.Size(50, 20);
            this.EraseButton.TabIndex = 29;
            this.EraseButton.Text = "Erase";
            this.EraseButton.UseVisualStyleBackColor = true;
            this.EraseButton.Click += new System.EventHandler(this.EraseButton_Click);
            this.EraseButton.KeyDown += new System.Windows.Forms.KeyEventHandler(this.BoxKeydownDefault);
            // 
            // TNFButton
            // 
            this.TNFButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.TNFButton.Location = new System.Drawing.Point(280, 80);
            this.TNFButton.Name = "TNFButton";
            this.TNFButton.Size = new System.Drawing.Size(50, 20);
            this.TNFButton.TabIndex = 140;
            this.TNFButton.Text = "TNF";
            this.TNFButton.UseVisualStyleBackColor = true;
            this.TNFButton.Click += new System.EventHandler(this.TNFButton_Click);
            // 
            // TNFEnableButton
            // 
            this.TNFEnableButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.TNFEnableButton.Location = new System.Drawing.Point(350, 80);
            this.TNFEnableButton.Name = "TNFEnableButton";
            this.TNFEnableButton.Size = new System.Drawing.Size(50, 20);
            this.TNFEnableButton.TabIndex = 150;
            this.TNFEnableButton.UseVisualStyleBackColor = true;
            this.TNFEnableButton.Click += new System.EventHandler(this.TNFEnableButton_Click);
            // 
            // EmphasisControl
            // 
            this.EmphasisControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.EmphasisControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.EmphasisControl.Header = "Emphasis";
            this.EmphasisControl.Location = new System.Drawing.Point(420, 200);
            this.EmphasisControl.Margin = new System.Windows.Forms.Padding(4);
            this.EmphasisControl.Name = "EmphasisControl";
            this.EmphasisControl.ReadOnly = false;
            this.EmphasisControl.Size = new System.Drawing.Size(50, 36);
            this.EmphasisControl.SmallSize = new System.Drawing.Size(50, 36);
            this.EmphasisControl.TabIndex = 460;
            this.EmphasisControl.Tag = "Emphasis";
            this.EmphasisControl.TheList = null;
            this.EmphasisControl.UpdateDisplayFunction = null;
            this.EmphasisControl.UpdateRigFunction = null;
            // 
            // OffsetControl
            // 
            this.OffsetControl.Header = "Ofst(KHZ)";
            this.OffsetControl.HighValue = 0;
            this.OffsetControl.Increment = 0;
            this.OffsetControl.Location = new System.Drawing.Point(350, 200);
            this.OffsetControl.LowValue = 0;
            this.OffsetControl.Margin = new System.Windows.Forms.Padding(4);
            this.OffsetControl.Name = "OffsetControl";
            this.OffsetControl.ReadOnly = false;
            this.OffsetControl.Size = new System.Drawing.Size(50, 36);
            this.OffsetControl.TabIndex = 450;
            this.OffsetControl.Tag = "Ofst(KHZ)";
            this.OffsetControl.UpdateDisplayFunction = null;
            this.OffsetControl.UpdateRigFunction = null;
            // 
            // SquelchLevelControl
            // 
            this.SquelchLevelControl.Header = "Sq level";
            this.SquelchLevelControl.HighValue = 0;
            this.SquelchLevelControl.Increment = 0;
            this.SquelchLevelControl.Location = new System.Drawing.Point(210, 200);
            this.SquelchLevelControl.LowValue = 0;
            this.SquelchLevelControl.Margin = new System.Windows.Forms.Padding(4);
            this.SquelchLevelControl.Name = "SquelchLevelControl";
            this.SquelchLevelControl.ReadOnly = false;
            this.SquelchLevelControl.Size = new System.Drawing.Size(50, 36);
            this.SquelchLevelControl.TabIndex = 430;
            this.SquelchLevelControl.Tag = "Sq level";
            this.SquelchLevelControl.UpdateDisplayFunction = null;
            this.SquelchLevelControl.UpdateRigFunction = null;
            // 
            // SquelchControl
            // 
            this.SquelchControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.SquelchControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.SquelchControl.Header = "Squelch";
            this.SquelchControl.Location = new System.Drawing.Point(140, 200);
            this.SquelchControl.Margin = new System.Windows.Forms.Padding(4);
            this.SquelchControl.Name = "SquelchControl";
            this.SquelchControl.ReadOnly = false;
            this.SquelchControl.Size = new System.Drawing.Size(50, 36);
            this.SquelchControl.SmallSize = new System.Drawing.Size(50, 36);
            this.SquelchControl.TabIndex = 420;
            this.SquelchControl.Tag = "Squelch";
            this.SquelchControl.TheList = null;
            this.SquelchControl.UpdateDisplayFunction = null;
            this.SquelchControl.UpdateRigFunction = null;
            // 
            // ToneFrequencyControl
            // 
            this.ToneFrequencyControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ToneFrequencyControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.ToneFrequencyControl.Header = "ToneFreq";
            this.ToneFrequencyControl.Location = new System.Drawing.Point(70, 200);
            this.ToneFrequencyControl.Margin = new System.Windows.Forms.Padding(4);
            this.ToneFrequencyControl.Name = "ToneFrequencyControl";
            this.ToneFrequencyControl.ReadOnly = false;
            this.ToneFrequencyControl.Size = new System.Drawing.Size(50, 36);
            this.ToneFrequencyControl.SmallSize = new System.Drawing.Size(50, 36);
            this.ToneFrequencyControl.TabIndex = 410;
            this.ToneFrequencyControl.Tag = "";
            this.ToneFrequencyControl.TheList = null;
            this.ToneFrequencyControl.UpdateDisplayFunction = null;
            this.ToneFrequencyControl.UpdateRigFunction = null;
            // 
            // ToneModeControl
            // 
            this.ToneModeControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ToneModeControl.ExpandedSize = new System.Drawing.Size(60, 56);
            this.ToneModeControl.Header = "ToneMode";
            this.ToneModeControl.Location = new System.Drawing.Point(0, 200);
            this.ToneModeControl.Margin = new System.Windows.Forms.Padding(4);
            this.ToneModeControl.Name = "ToneModeControl";
            this.ToneModeControl.ReadOnly = false;
            this.ToneModeControl.Size = new System.Drawing.Size(60, 36);
            this.ToneModeControl.SmallSize = new System.Drawing.Size(60, 36);
            this.ToneModeControl.TabIndex = 400;
            this.ToneModeControl.Tag = "";
            this.ToneModeControl.TheList = null;
            this.ToneModeControl.UpdateDisplayFunction = null;
            this.ToneModeControl.UpdateRigFunction = null;
            // 
            // APFLevelControl
            // 
            this.APFLevelControl.Header = "APF lvl";
            this.APFLevelControl.HighValue = 0;
            this.APFLevelControl.Increment = 0;
            this.APFLevelControl.Location = new System.Drawing.Point(350, 40);
            this.APFLevelControl.LowValue = 0;
            this.APFLevelControl.Margin = new System.Windows.Forms.Padding(4);
            this.APFLevelControl.Name = "APFLevelControl";
            this.APFLevelControl.ReadOnly = false;
            this.APFLevelControl.Size = new System.Drawing.Size(50, 36);
            this.APFLevelControl.TabIndex = 90;
            this.APFLevelControl.Tag = "APF lvl";
            this.APFLevelControl.UpdateDisplayFunction = null;
            this.APFLevelControl.UpdateRigFunction = null;
            // 
            // APFControl
            // 
            this.APFControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.APFControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.APFControl.Header = "APF";
            this.APFControl.Location = new System.Drawing.Point(280, 40);
            this.APFControl.Margin = new System.Windows.Forms.Padding(4);
            this.APFControl.Name = "APFControl";
            this.APFControl.ReadOnly = false;
            this.APFControl.Size = new System.Drawing.Size(50, 36);
            this.APFControl.SmallSize = new System.Drawing.Size(50, 36);
            this.APFControl.TabIndex = 80;
            this.APFControl.Tag = "APF";
            this.APFControl.TheList = null;
            this.APFControl.UpdateDisplayFunction = null;
            this.APFControl.UpdateRigFunction = null;
            // 
            // ANFLevelControl
            // 
            this.ANFLevelControl.Header = "ANF lvl";
            this.ANFLevelControl.HighValue = 0;
            this.ANFLevelControl.Increment = 0;
            this.ANFLevelControl.Location = new System.Drawing.Point(350, 40);
            this.ANFLevelControl.LowValue = 0;
            this.ANFLevelControl.Margin = new System.Windows.Forms.Padding(4);
            this.ANFLevelControl.Name = "ANFLevelControl";
            this.ANFLevelControl.ReadOnly = false;
            this.ANFLevelControl.Size = new System.Drawing.Size(50, 36);
            this.ANFLevelControl.TabIndex = 90;
            this.ANFLevelControl.Tag = "ANF lvl";
            this.ANFLevelControl.UpdateDisplayFunction = null;
            this.ANFLevelControl.UpdateRigFunction = null;
            // 
            // ANFControl
            // 
            this.ANFControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ANFControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.ANFControl.Header = "ANF";
            this.ANFControl.Location = new System.Drawing.Point(280, 40);
            this.ANFControl.Margin = new System.Windows.Forms.Padding(4);
            this.ANFControl.Name = "ANFControl";
            this.ANFControl.ReadOnly = false;
            this.ANFControl.Size = new System.Drawing.Size(50, 36);
            this.ANFControl.SmallSize = new System.Drawing.Size(50, 36);
            this.ANFControl.TabIndex = 80;
            this.ANFControl.Tag = "ANF";
            this.ANFControl.TheList = null;
            this.ANFControl.UpdateDisplayFunction = null;
            this.ANFControl.UpdateRigFunction = null;
            // 
            // VoxDelayControl
            // 
            this.VoxDelayControl.Header = "Vox del";
            this.VoxDelayControl.HighValue = 0;
            this.VoxDelayControl.Increment = 0;
            this.VoxDelayControl.Location = new System.Drawing.Point(0, 120);
            this.VoxDelayControl.LowValue = 0;
            this.VoxDelayControl.Margin = new System.Windows.Forms.Padding(4);
            this.VoxDelayControl.Name = "VoxDelayControl";
            this.VoxDelayControl.ReadOnly = false;
            this.VoxDelayControl.Size = new System.Drawing.Size(50, 36);
            this.VoxDelayControl.TabIndex = 200;
            this.VoxDelayControl.Tag = "Vox del";
            this.VoxDelayControl.UpdateDisplayFunction = null;
            this.VoxDelayControl.UpdateRigFunction = null;
            // 
            // VoxGainControl
            // 
            this.VoxGainControl.Header = "Vox gain";
            this.VoxGainControl.HighValue = 0;
            this.VoxGainControl.Increment = 0;
            this.VoxGainControl.Location = new System.Drawing.Point(70, 120);
            this.VoxGainControl.LowValue = 0;
            this.VoxGainControl.Margin = new System.Windows.Forms.Padding(4);
            this.VoxGainControl.Name = "VoxGainControl";
            this.VoxGainControl.ReadOnly = false;
            this.VoxGainControl.Size = new System.Drawing.Size(50, 36);
            this.VoxGainControl.TabIndex = 210;
            this.VoxGainControl.Tag = "Vox gain";
            this.VoxGainControl.UpdateDisplayFunction = null;
            this.VoxGainControl.UpdateRigFunction = null;
            // 
            // XmitPowerControl
            // 
            this.XmitPowerControl.Header = "Power";
            this.XmitPowerControl.HighValue = 0;
            this.XmitPowerControl.Increment = 0;
            this.XmitPowerControl.Location = new System.Drawing.Point(140, 240);
            this.XmitPowerControl.LowValue = 0;
            this.XmitPowerControl.Margin = new System.Windows.Forms.Padding(4);
            this.XmitPowerControl.Name = "XmitPowerControl";
            this.XmitPowerControl.ReadOnly = false;
            this.XmitPowerControl.Size = new System.Drawing.Size(50, 36);
            this.XmitPowerControl.TabIndex = 520;
            this.XmitPowerControl.Tag = "Power";
            this.XmitPowerControl.UpdateDisplayFunction = null;
            this.XmitPowerControl.UpdateRigFunction = null;
            // 
            // ProcessorSettingControl
            // 
            this.ProcessorSettingControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ProcessorSettingControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.ProcessorSettingControl.Header = "PrcLvl";
            this.ProcessorSettingControl.Location = new System.Drawing.Point(350, 120);
            this.ProcessorSettingControl.Margin = new System.Windows.Forms.Padding(4);
            this.ProcessorSettingControl.Name = "ProcessorSettingControl";
            this.ProcessorSettingControl.ReadOnly = false;
            this.ProcessorSettingControl.Size = new System.Drawing.Size(50, 36);
            this.ProcessorSettingControl.SmallSize = new System.Drawing.Size(50, 36);
            this.ProcessorSettingControl.TabIndex = 250;
            this.ProcessorSettingControl.Tag = "PrcLvl";
            this.ProcessorSettingControl.TheList = null;
            this.ProcessorSettingControl.UpdateDisplayFunction = null;
            this.ProcessorSettingControl.UpdateRigFunction = null;
            // 
            // ProcessorOnControl
            // 
            this.ProcessorOnControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ProcessorOnControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.ProcessorOnControl.Header = "SpProc";
            this.ProcessorOnControl.Location = new System.Drawing.Point(280, 120);
            this.ProcessorOnControl.Margin = new System.Windows.Forms.Padding(4);
            this.ProcessorOnControl.Name = "ProcessorOnControl";
            this.ProcessorOnControl.ReadOnly = false;
            this.ProcessorOnControl.Size = new System.Drawing.Size(50, 36);
            this.ProcessorOnControl.SmallSize = new System.Drawing.Size(50, 36);
            this.ProcessorOnControl.TabIndex = 240;
            this.ProcessorOnControl.Tag = "Spch Proc";
            this.ProcessorOnControl.TheList = null;
            this.ProcessorOnControl.UpdateDisplayFunction = null;
            this.ProcessorOnControl.UpdateRigFunction = null;
            // 
            // SWRControl
            // 
            this.SWRControl.Header = "SWR";
            this.SWRControl.Location = new System.Drawing.Point(210, 240);
            this.SWRControl.Margin = new System.Windows.Forms.Padding(4);
            this.SWRControl.Name = "SWRControl";
            this.SWRControl.ReadOnly = false;
            this.SWRControl.Size = new System.Drawing.Size(50, 36);
            this.SWRControl.TabIndex = 530;
            this.SWRControl.Tag = "SWR";
            this.SWRControl.UpdateDisplayFunction = null;
            this.SWRControl.UpdateRigFunction = null;
            // 
            // MicPeakBox
            // 
            this.MicPeakBox.Enabled = false;
            this.MicPeakBox.Header = "Micpeak";
            this.MicPeakBox.Location = new System.Drawing.Point(210, 120);
            this.MicPeakBox.Margin = new System.Windows.Forms.Padding(4);
            this.MicPeakBox.Name = "MicPeakBox";
            this.MicPeakBox.ReadOnly = false;
            this.MicPeakBox.Size = new System.Drawing.Size(50, 36);
            this.MicPeakBox.TabIndex = 230;
            this.MicPeakBox.Tag = "Micpeak";
            this.MicPeakBox.UpdateDisplayFunction = null;
            this.MicPeakBox.UpdateRigFunction = null;
            this.MicPeakBox.Visible = false;
            this.MicPeakBox.EnabledChanged += new System.EventHandler(this.MicPeakBox_EnabledChanged);
            // 
            // MicGainControl
            // 
            this.MicGainControl.Header = "Micgn";
            this.MicGainControl.HighValue = 0;
            this.MicGainControl.Increment = 0;
            this.MicGainControl.Location = new System.Drawing.Point(140, 120);
            this.MicGainControl.LowValue = 0;
            this.MicGainControl.Margin = new System.Windows.Forms.Padding(4);
            this.MicGainControl.Name = "MicGainControl";
            this.MicGainControl.ReadOnly = false;
            this.MicGainControl.Size = new System.Drawing.Size(50, 36);
            this.MicGainControl.TabIndex = 220;
            this.MicGainControl.Tag = "";
            this.MicGainControl.UpdateDisplayFunction = null;
            this.MicGainControl.UpdateRigFunction = null;
            // 
            // MonitorPanControl
            // 
            this.MonitorPanControl.Header = "monpan";
            this.MonitorPanControl.HighValue = 0;
            this.MonitorPanControl.Increment = 0;
            this.MonitorPanControl.Location = new System.Drawing.Point(420, 120);
            this.MonitorPanControl.LowValue = 0;
            this.MonitorPanControl.Margin = new System.Windows.Forms.Padding(4);
            this.MonitorPanControl.Name = "MonitorPanControl";
            this.MonitorPanControl.ReadOnly = false;
            this.MonitorPanControl.Size = new System.Drawing.Size(50, 36);
            this.MonitorPanControl.TabIndex = 260;
            this.MonitorPanControl.Tag = "";
            this.MonitorPanControl.UpdateDisplayFunction = null;
            this.MonitorPanControl.UpdateRigFunction = null;
            // 
            // KeyerSpeedControl
            // 
            this.KeyerSpeedControl.Header = "speed";
            this.KeyerSpeedControl.HighValue = 0;
            this.KeyerSpeedControl.Increment = 0;
            this.KeyerSpeedControl.Location = new System.Drawing.Point(140, 120);
            this.KeyerSpeedControl.LowValue = 0;
            this.KeyerSpeedControl.Margin = new System.Windows.Forms.Padding(4);
            this.KeyerSpeedControl.Name = "KeyerSpeedControl";
            this.KeyerSpeedControl.ReadOnly = false;
            this.KeyerSpeedControl.Size = new System.Drawing.Size(50, 36);
            this.KeyerSpeedControl.TabIndex = 220;
            this.KeyerSpeedControl.Tag = "";
            this.KeyerSpeedControl.UpdateDisplayFunction = null;
            this.KeyerSpeedControl.UpdateRigFunction = null;
            // 
            // KeyerControl
            // 
            this.KeyerControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.KeyerControl.ExpandedSize = new System.Drawing.Size(80, 56);
            this.KeyerControl.Header = "keyer";
            this.KeyerControl.Location = new System.Drawing.Point(70, 120);
            this.KeyerControl.Margin = new System.Windows.Forms.Padding(4);
            this.KeyerControl.Name = "KeyerControl";
            this.KeyerControl.ReadOnly = false;
            this.KeyerControl.Size = new System.Drawing.Size(60, 36);
            this.KeyerControl.SmallSize = new System.Drawing.Size(80, 36);
            this.KeyerControl.TabIndex = 210;
            this.KeyerControl.Tag = "keyer";
            this.KeyerControl.TheList = null;
            this.KeyerControl.UpdateDisplayFunction = null;
            this.KeyerControl.UpdateRigFunction = null;
            // 
            // CWReverseControl
            // 
            this.CWReverseControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.CWReverseControl.ExpandedSize = new System.Drawing.Size(80, 56);
            this.CWReverseControl.Header = "reverse";
            this.CWReverseControl.Location = new System.Drawing.Point(490, 120);
            this.CWReverseControl.Margin = new System.Windows.Forms.Padding(4);
            this.CWReverseControl.Name = "CWReverseControl";
            this.CWReverseControl.ReadOnly = false;
            this.CWReverseControl.Size = new System.Drawing.Size(60, 36);
            this.CWReverseControl.SmallSize = new System.Drawing.Size(80, 36);
            this.CWReverseControl.TabIndex = 270;
            this.CWReverseControl.Tag = "reverse";
            this.CWReverseControl.TheList = null;
            this.CWReverseControl.UpdateDisplayFunction = null;
            this.CWReverseControl.UpdateRigFunction = null;
            // 
            // SidetoneGainControl
            // 
            this.SidetoneGainControl.Header = "STVol";
            this.SidetoneGainControl.HighValue = 0;
            this.SidetoneGainControl.Increment = 0;
            this.SidetoneGainControl.Location = new System.Drawing.Point(280, 120);
            this.SidetoneGainControl.LowValue = 0;
            this.SidetoneGainControl.Margin = new System.Windows.Forms.Padding(4);
            this.SidetoneGainControl.Name = "SidetoneGainControl";
            this.SidetoneGainControl.ReadOnly = false;
            this.SidetoneGainControl.Size = new System.Drawing.Size(50, 36);
            this.SidetoneGainControl.TabIndex = 240;
            this.SidetoneGainControl.Tag = "";
            this.SidetoneGainControl.UpdateDisplayFunction = null;
            this.SidetoneGainControl.UpdateRigFunction = null;
            // 
            // SidetonePitchControl
            // 
            this.SidetonePitchControl.Header = "STPitch";
            this.SidetonePitchControl.HighValue = 0;
            this.SidetonePitchControl.Increment = 0;
            this.SidetonePitchControl.Location = new System.Drawing.Point(210, 120);
            this.SidetonePitchControl.LowValue = 0;
            this.SidetonePitchControl.Margin = new System.Windows.Forms.Padding(4);
            this.SidetonePitchControl.Name = "SidetonePitchControl";
            this.SidetonePitchControl.ReadOnly = false;
            this.SidetonePitchControl.Size = new System.Drawing.Size(50, 36);
            this.SidetonePitchControl.TabIndex = 230;
            this.SidetonePitchControl.Tag = "";
            this.SidetonePitchControl.UpdateDisplayFunction = null;
            this.SidetonePitchControl.UpdateRigFunction = null;
            // 
            // BreakinDelayControl
            // 
            this.BreakinDelayControl.Header = "BkinDel";
            this.BreakinDelayControl.HighValue = 0;
            this.BreakinDelayControl.Increment = 0;
            this.BreakinDelayControl.Location = new System.Drawing.Point(0, 120);
            this.BreakinDelayControl.LowValue = 0;
            this.BreakinDelayControl.Margin = new System.Windows.Forms.Padding(4);
            this.BreakinDelayControl.Name = "BreakinDelayControl";
            this.BreakinDelayControl.ReadOnly = false;
            this.BreakinDelayControl.Size = new System.Drawing.Size(50, 36);
            this.BreakinDelayControl.TabIndex = 200;
            this.BreakinDelayControl.Tag = "";
            this.BreakinDelayControl.UpdateDisplayFunction = null;
            this.BreakinDelayControl.UpdateRigFunction = null;
            // 
            // NoiseReductionLevelControl
            // 
            this.NoiseReductionLevelControl.Header = "N.R. lvl";
            this.NoiseReductionLevelControl.HighValue = 0;
            this.NoiseReductionLevelControl.Increment = 0;
            this.NoiseReductionLevelControl.Location = new System.Drawing.Point(210, 80);
            this.NoiseReductionLevelControl.LowValue = 0;
            this.NoiseReductionLevelControl.Margin = new System.Windows.Forms.Padding(4);
            this.NoiseReductionLevelControl.Name = "NoiseReductionLevelControl";
            this.NoiseReductionLevelControl.ReadOnly = false;
            this.NoiseReductionLevelControl.Size = new System.Drawing.Size(50, 36);
            this.NoiseReductionLevelControl.TabIndex = 130;
            this.NoiseReductionLevelControl.Tag = "";
            this.NoiseReductionLevelControl.UpdateDisplayFunction = null;
            this.NoiseReductionLevelControl.UpdateRigFunction = null;
            // 
            // NoiseReductionControl
            // 
            this.NoiseReductionControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NoiseReductionControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.NoiseReductionControl.Header = "N.R.";
            this.NoiseReductionControl.Location = new System.Drawing.Point(140, 80);
            this.NoiseReductionControl.Margin = new System.Windows.Forms.Padding(4);
            this.NoiseReductionControl.Name = "NoiseReductionControl";
            this.NoiseReductionControl.ReadOnly = false;
            this.NoiseReductionControl.Size = new System.Drawing.Size(50, 36);
            this.NoiseReductionControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NoiseReductionControl.TabIndex = 120;
            this.NoiseReductionControl.Tag = "N.R.";
            this.NoiseReductionControl.TheList = null;
            this.NoiseReductionControl.UpdateDisplayFunction = null;
            this.NoiseReductionControl.UpdateRigFunction = null;
            // 
            // NoiseBlankerLevelControl
            // 
            this.NoiseBlankerLevelControl.Header = "N.B. lvl";
            this.NoiseBlankerLevelControl.HighValue = 0;
            this.NoiseBlankerLevelControl.Increment = 0;
            this.NoiseBlankerLevelControl.Location = new System.Drawing.Point(70, 80);
            this.NoiseBlankerLevelControl.LowValue = 0;
            this.NoiseBlankerLevelControl.Margin = new System.Windows.Forms.Padding(4);
            this.NoiseBlankerLevelControl.Name = "NoiseBlankerLevelControl";
            this.NoiseBlankerLevelControl.ReadOnly = false;
            this.NoiseBlankerLevelControl.Size = new System.Drawing.Size(50, 36);
            this.NoiseBlankerLevelControl.TabIndex = 110;
            this.NoiseBlankerLevelControl.Tag = "";
            this.NoiseBlankerLevelControl.UpdateDisplayFunction = null;
            this.NoiseBlankerLevelControl.UpdateRigFunction = null;
            // 
            // NoiseBlankerControl
            // 
            this.NoiseBlankerControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NoiseBlankerControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.NoiseBlankerControl.Header = "N.B.";
            this.NoiseBlankerControl.Location = new System.Drawing.Point(0, 80);
            this.NoiseBlankerControl.Margin = new System.Windows.Forms.Padding(4);
            this.NoiseBlankerControl.Name = "NoiseBlankerControl";
            this.NoiseBlankerControl.ReadOnly = false;
            this.NoiseBlankerControl.Size = new System.Drawing.Size(50, 36);
            this.NoiseBlankerControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NoiseBlankerControl.TabIndex = 100;
            this.NoiseBlankerControl.Tag = "";
            this.NoiseBlankerControl.TheList = null;
            this.NoiseBlankerControl.UpdateDisplayFunction = null;
            this.NoiseBlankerControl.UpdateRigFunction = null;
            // 
            // AGCThresholdControl
            // 
            this.AGCThresholdControl.Header = "AGC level";
            this.AGCThresholdControl.HighValue = 0;
            this.AGCThresholdControl.Increment = 0;
            this.AGCThresholdControl.Location = new System.Drawing.Point(210, 40);
            this.AGCThresholdControl.LowValue = 0;
            this.AGCThresholdControl.Margin = new System.Windows.Forms.Padding(4);
            this.AGCThresholdControl.Name = "AGCThresholdControl";
            this.AGCThresholdControl.ReadOnly = false;
            this.AGCThresholdControl.Size = new System.Drawing.Size(50, 36);
            this.AGCThresholdControl.TabIndex = 70;
            this.AGCThresholdControl.Tag = "";
            this.AGCThresholdControl.UpdateDisplayFunction = null;
            this.AGCThresholdControl.UpdateRigFunction = null;
            // 
            // AGCSpeedControl
            // 
            this.AGCSpeedControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AGCSpeedControl.ExpandedSize = new System.Drawing.Size(65, 80);
            this.AGCSpeedControl.Header = "AGC";
            this.AGCSpeedControl.Location = new System.Drawing.Point(140, 40);
            this.AGCSpeedControl.Margin = new System.Windows.Forms.Padding(4);
            this.AGCSpeedControl.Name = "AGCSpeedControl";
            this.AGCSpeedControl.ReadOnly = false;
            this.AGCSpeedControl.Size = new System.Drawing.Size(65, 36);
            this.AGCSpeedControl.SmallSize = new System.Drawing.Size(65, 36);
            this.AGCSpeedControl.TabIndex = 60;
            this.AGCSpeedControl.Tag = "AGC";
            this.AGCSpeedControl.TheList = null;
            this.AGCSpeedControl.UpdateDisplayFunction = null;
            this.AGCSpeedControl.UpdateRigFunction = null;
            // 
            // FilterHighControl
            // 
            this.FilterHighControl.Header = "High";
            this.FilterHighControl.HighValue = 0;
            this.FilterHighControl.Increment = 0;
            this.FilterHighControl.Location = new System.Drawing.Point(70, 40);
            this.FilterHighControl.LowValue = 0;
            this.FilterHighControl.Margin = new System.Windows.Forms.Padding(4);
            this.FilterHighControl.Name = "FilterHighControl";
            this.FilterHighControl.ReadOnly = false;
            this.FilterHighControl.Size = new System.Drawing.Size(50, 36);
            this.FilterHighControl.TabIndex = 40;
            this.FilterHighControl.Tag = "High";
            this.FilterHighControl.UpdateDisplayFunction = null;
            this.FilterHighControl.UpdateRigFunction = null;
            // 
            // FilterLowControl
            // 
            this.FilterLowControl.Header = "Low";
            this.FilterLowControl.HighValue = 0;
            this.FilterLowControl.Increment = 0;
            this.FilterLowControl.Location = new System.Drawing.Point(0, 40);
            this.FilterLowControl.LowValue = 0;
            this.FilterLowControl.Margin = new System.Windows.Forms.Padding(4);
            this.FilterLowControl.Name = "FilterLowControl";
            this.FilterLowControl.ReadOnly = false;
            this.FilterLowControl.Size = new System.Drawing.Size(50, 36);
            this.FilterLowControl.TabIndex = 30;
            this.FilterLowControl.Tag = "Low";
            this.FilterLowControl.UpdateDisplayFunction = null;
            this.FilterLowControl.UpdateRigFunction = null;
            // 
            // FM1750Control
            // 
            this.FM1750Control.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FM1750Control.ExpandedSize = new System.Drawing.Size(50, 56);
            this.FM1750Control.Header = "FM1750";
            this.FM1750Control.Location = new System.Drawing.Point(490, 200);
            this.FM1750Control.Margin = new System.Windows.Forms.Padding(4);
            this.FM1750Control.Name = "FM1750Control";
            this.FM1750Control.ReadOnly = false;
            this.FM1750Control.Size = new System.Drawing.Size(50, 36);
            this.FM1750Control.SmallSize = new System.Drawing.Size(50, 36);
            this.FM1750Control.TabIndex = 470;
            this.FM1750Control.Tag = "FM1750";
            this.FM1750Control.TheList = null;
            this.FM1750Control.UpdateDisplayFunction = null;
            this.FM1750Control.UpdateRigFunction = null;
            // 
            // BinauralControl
            // 
            this.BinauralControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.BinauralControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.BinauralControl.Header = "Binaural";
            this.BinauralControl.Location = new System.Drawing.Point(0, 280);
            this.BinauralControl.Margin = new System.Windows.Forms.Padding(4);
            this.BinauralControl.Name = "BinauralControl";
            this.BinauralControl.ReadOnly = false;
            this.BinauralControl.Size = new System.Drawing.Size(50, 36);
            this.BinauralControl.SmallSize = new System.Drawing.Size(50, 36);
            this.BinauralControl.TabIndex = 600;
            this.BinauralControl.Tag = "Binaural";
            this.BinauralControl.TheList = null;
            this.BinauralControl.UpdateDisplayFunction = null;
            this.BinauralControl.UpdateRigFunction = null;
            // 
            // PlayControl
            // 
            this.PlayControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.PlayControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.PlayControl.Header = "Play";
            this.PlayControl.Location = new System.Drawing.Point(70, 280);
            this.PlayControl.Margin = new System.Windows.Forms.Padding(4);
            this.PlayControl.Name = "PlayControl";
            this.PlayControl.ReadOnly = false;
            this.PlayControl.Size = new System.Drawing.Size(50, 36);
            this.PlayControl.SmallSize = new System.Drawing.Size(50, 36);
            this.PlayControl.TabIndex = 610;
            this.PlayControl.Tag = "Play";
            this.PlayControl.TheList = null;
            this.PlayControl.UpdateDisplayFunction = null;
            this.PlayControl.UpdateRigFunction = null;
            // 
            // RecordControl
            // 
            this.RecordControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.RecordControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.RecordControl.Header = "Record";
            this.RecordControl.Location = new System.Drawing.Point(140, 280);
            this.RecordControl.Margin = new System.Windows.Forms.Padding(4);
            this.RecordControl.Name = "RecordControl";
            this.RecordControl.ReadOnly = false;
            this.RecordControl.Size = new System.Drawing.Size(50, 36);
            this.RecordControl.SmallSize = new System.Drawing.Size(50, 36);
            this.RecordControl.TabIndex = 620;
            this.RecordControl.Tag = "Record";
            this.RecordControl.TheList = null;
            this.RecordControl.UpdateDisplayFunction = null;
            this.RecordControl.UpdateRigFunction = null;
            // 
            // ExportButton
            // 
            this.ExportButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.ExportButton.Location = new System.Drawing.Point(210, 280);
            this.ExportButton.Name = "ExportButton";
            this.ExportButton.Size = new System.Drawing.Size(50, 23);
            this.ExportButton.TabIndex = 630;
            this.ExportButton.Text = "Export";
            this.ExportButton.UseVisualStyleBackColor = true;
            this.ExportButton.Click += new System.EventHandler(this.ExportButton_Click);
            // 
            // ImportButton
            // 
            this.ImportButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.ImportButton.Location = new System.Drawing.Point(280, 280);
            this.ImportButton.Name = "ImportButton";
            this.ImportButton.Size = new System.Drawing.Size(50, 23);
            this.ImportButton.TabIndex = 640;
            this.ImportButton.Text = "Import";
            this.ImportButton.UseVisualStyleBackColor = true;
            this.ImportButton.Click += new System.EventHandler(this.ImportButton_Click);
            // 
            // RXAntControl
            // 
            this.RXAntControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.RXAntControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.RXAntControl.Header = "RXAnt";
            this.RXAntControl.Location = new System.Drawing.Point(70, 240);
            this.RXAntControl.Margin = new System.Windows.Forms.Padding(4);
            this.RXAntControl.Name = "RXAntControl";
            this.RXAntControl.ReadOnly = false;
            this.RXAntControl.Size = new System.Drawing.Size(50, 36);
            this.RXAntControl.SmallSize = new System.Drawing.Size(50, 36);
            this.RXAntControl.TabIndex = 510;
            this.RXAntControl.Tag = "RXAnt";
            this.RXAntControl.TheList = null;
            this.RXAntControl.UpdateDisplayFunction = null;
            this.RXAntControl.UpdateRigFunction = null;
            // 
            // DiversityControl
            // 
            this.DiversityControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.DiversityControl.ExpandedSize = new System.Drawing.Size(60, 56);
            this.DiversityControl.Header = "Diversity";
            this.DiversityControl.Location = new System.Drawing.Point(140, 240);
            this.DiversityControl.Margin = new System.Windows.Forms.Padding(4);
            this.DiversityControl.Name = "DiversityControl";
            this.DiversityControl.ReadOnly = false;
            this.DiversityControl.Size = new System.Drawing.Size(60, 36);
            this.DiversityControl.SmallSize = new System.Drawing.Size(60, 36);
            this.DiversityControl.TabIndex = 520;
            this.DiversityControl.Tag = "Diversity";
            this.DiversityControl.TheList = null;
            this.DiversityControl.UpdateDisplayFunction = null;
            this.DiversityControl.UpdateRigFunction = null;
            // 
            // EscButton
            // 
            this.EscButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.EscButton.Location = new System.Drawing.Point(210, 240);
            this.EscButton.Name = "EscButton";
            this.EscButton.Size = new System.Drawing.Size(60, 36);
            this.EscButton.TabIndex = 525;
            this.EscButton.Text = "ESC";
            this.EscButton.UseVisualStyleBackColor = true;
            this.EscButton.Click += new System.EventHandler(this.EscButton_Click);
            // 
            // CWLControl
            // 
            this.CWLControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.CWLControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.CWLControl.Header = "CWL";
            this.CWLControl.Location = new System.Drawing.Point(350, 120);
            this.CWLControl.Margin = new System.Windows.Forms.Padding(4);
            this.CWLControl.Name = "CWLControl";
            this.CWLControl.ReadOnly = false;
            this.CWLControl.Size = new System.Drawing.Size(50, 36);
            this.CWLControl.SmallSize = new System.Drawing.Size(50, 36);
            this.CWLControl.TabIndex = 250;
            this.CWLControl.Tag = "CWL";
            this.CWLControl.TheList = null;
            this.CWLControl.UpdateDisplayFunction = null;
            this.CWLControl.UpdateRigFunction = null;
            // 
            // CompanderControl
            // 
            this.CompanderControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.CompanderControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.CompanderControl.Header = "Compander";
            this.CompanderControl.Location = new System.Drawing.Point(420, 120);
            this.CompanderControl.Margin = new System.Windows.Forms.Padding(4);
            this.CompanderControl.Name = "CompanderControl";
            this.CompanderControl.ReadOnly = false;
            this.CompanderControl.Size = new System.Drawing.Size(50, 36);
            this.CompanderControl.SmallSize = new System.Drawing.Size(50, 36);
            this.CompanderControl.TabIndex = 260;
            this.CompanderControl.Tag = "Compander";
            this.CompanderControl.TheList = null;
            this.CompanderControl.UpdateDisplayFunction = null;
            this.CompanderControl.UpdateRigFunction = null;
            // 
            // CompanderLevelControl
            // 
            this.CompanderLevelControl.Header = "Ca level";
            this.CompanderLevelControl.HighValue = 0;
            this.CompanderLevelControl.Increment = 0;
            this.CompanderLevelControl.Location = new System.Drawing.Point(490, 120);
            this.CompanderLevelControl.LowValue = 0;
            this.CompanderLevelControl.Margin = new System.Windows.Forms.Padding(4);
            this.CompanderLevelControl.Name = "CompanderLevelControl";
            this.CompanderLevelControl.ReadOnly = false;
            this.CompanderLevelControl.Size = new System.Drawing.Size(50, 36);
            this.CompanderLevelControl.TabIndex = 270;
            this.CompanderLevelControl.Tag = "Ca level";
            this.CompanderLevelControl.UpdateDisplayFunction = null;
            this.CompanderLevelControl.UpdateRigFunction = null;
            // 
            // TXFilterLowControl
            // 
            this.TXFilterLowControl.Header = "TXLow";
            this.TXFilterLowControl.HighValue = 0;
            this.TXFilterLowControl.Increment = 0;
            this.TXFilterLowControl.Location = new System.Drawing.Point(0, 160);
            this.TXFilterLowControl.LowValue = 0;
            this.TXFilterLowControl.Margin = new System.Windows.Forms.Padding(4);
            this.TXFilterLowControl.Name = "TXFilterLowControl";
            this.TXFilterLowControl.ReadOnly = false;
            this.TXFilterLowControl.Size = new System.Drawing.Size(50, 36);
            this.TXFilterLowControl.TabIndex = 300;
            this.TXFilterLowControl.Tag = "TXLow";
            this.TXFilterLowControl.UpdateDisplayFunction = null;
            this.TXFilterLowControl.UpdateRigFunction = null;
            // 
            // TXFilterHighControl
            // 
            this.TXFilterHighControl.Header = "TXHigh";
            this.TXFilterHighControl.HighValue = 0;
            this.TXFilterHighControl.Increment = 0;
            this.TXFilterHighControl.Location = new System.Drawing.Point(70, 160);
            this.TXFilterHighControl.LowValue = 0;
            this.TXFilterHighControl.Margin = new System.Windows.Forms.Padding(4);
            this.TXFilterHighControl.Name = "TXFilterHighControl";
            this.TXFilterHighControl.ReadOnly = false;
            this.TXFilterHighControl.Size = new System.Drawing.Size(50, 36);
            this.TXFilterHighControl.TabIndex = 310;
            this.TXFilterHighControl.Tag = "TXHigh";
            this.TXFilterHighControl.UpdateDisplayFunction = null;
            this.TXFilterHighControl.UpdateRigFunction = null;
            // 
            // MicBoostControl
            // 
            this.MicBoostControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.MicBoostControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.MicBoostControl.Header = "Mic+20";
            this.MicBoostControl.Location = new System.Drawing.Point(140, 160);
            this.MicBoostControl.Margin = new System.Windows.Forms.Padding(4);
            this.MicBoostControl.Name = "MicBoostControl";
            this.MicBoostControl.ReadOnly = false;
            this.MicBoostControl.Size = new System.Drawing.Size(50, 36);
            this.MicBoostControl.SmallSize = new System.Drawing.Size(50, 36);
            this.MicBoostControl.TabIndex = 320;
            this.MicBoostControl.Tag = "Mic+20";
            this.MicBoostControl.TheList = null;
            this.MicBoostControl.UpdateDisplayFunction = null;
            this.MicBoostControl.UpdateRigFunction = null;
            // 
            // MicBiasControl
            // 
            this.MicBiasControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.MicBiasControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.MicBiasControl.Header = "Mic bias";
            this.MicBiasControl.Location = new System.Drawing.Point(210, 160);
            this.MicBiasControl.Margin = new System.Windows.Forms.Padding(4);
            this.MicBiasControl.Name = "MicBiasControl";
            this.MicBiasControl.ReadOnly = false;
            this.MicBiasControl.Size = new System.Drawing.Size(50, 36);
            this.MicBiasControl.SmallSize = new System.Drawing.Size(50, 36);
            this.MicBiasControl.TabIndex = 330;
            this.MicBiasControl.Tag = "Mic bias";
            this.MicBiasControl.TheList = null;
            this.MicBiasControl.UpdateDisplayFunction = null;
            this.MicBiasControl.UpdateRigFunction = null;
            // 
            // MonitorControl
            // 
            this.MonitorControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.MonitorControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.MonitorControl.Header = "monitor";
            this.MonitorControl.Location = new System.Drawing.Point(280, 160);
            this.MonitorControl.Margin = new System.Windows.Forms.Padding(4);
            this.MonitorControl.Name = "MonitorControl";
            this.MonitorControl.ReadOnly = false;
            this.MonitorControl.Size = new System.Drawing.Size(50, 36);
            this.MonitorControl.SmallSize = new System.Drawing.Size(50, 36);
            this.MonitorControl.TabIndex = 340;
            this.MonitorControl.Tag = "monitor";
            this.MonitorControl.TheList = null;
            this.MonitorControl.UpdateDisplayFunction = null;
            this.MonitorControl.UpdateRigFunction = null;
            // 
            // SBMonitorLevelControl
            // 
            this.SBMonitorLevelControl.Header = "mon level";
            this.SBMonitorLevelControl.HighValue = 0;
            this.SBMonitorLevelControl.Increment = 0;
            this.SBMonitorLevelControl.Location = new System.Drawing.Point(350, 160);
            this.SBMonitorLevelControl.LowValue = 0;
            this.SBMonitorLevelControl.Margin = new System.Windows.Forms.Padding(4);
            this.SBMonitorLevelControl.Name = "SBMonitorLevelControl";
            this.SBMonitorLevelControl.ReadOnly = false;
            this.SBMonitorLevelControl.Size = new System.Drawing.Size(50, 36);
            this.SBMonitorLevelControl.TabIndex = 350;
            this.SBMonitorLevelControl.Tag = "mon level";
            this.SBMonitorLevelControl.UpdateDisplayFunction = null;
            this.SBMonitorLevelControl.UpdateRigFunction = null;
            // 
            // SBMonitorPanControl
            // 
            this.SBMonitorPanControl.Header = "mon pan";
            this.SBMonitorPanControl.HighValue = 0;
            this.SBMonitorPanControl.Increment = 0;
            this.SBMonitorPanControl.Location = new System.Drawing.Point(420, 160);
            this.SBMonitorPanControl.LowValue = 0;
            this.SBMonitorPanControl.Margin = new System.Windows.Forms.Padding(4);
            this.SBMonitorPanControl.Name = "SBMonitorPanControl";
            this.SBMonitorPanControl.ReadOnly = false;
            this.SBMonitorPanControl.Size = new System.Drawing.Size(50, 36);
            this.SBMonitorPanControl.TabIndex = 360;
            this.SBMonitorPanControl.Tag = "mon pan";
            this.SBMonitorPanControl.UpdateDisplayFunction = null;
            this.SBMonitorPanControl.UpdateRigFunction = null;
            // 
            // RXEqButton
            // 
            this.RXEqButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.RXEqButton.Location = new System.Drawing.Point(420, 80);
            this.RXEqButton.Name = "RXEqButton";
            this.RXEqButton.Size = new System.Drawing.Size(50, 23);
            this.RXEqButton.TabIndex = 160;
            this.RXEqButton.Text = "RXEq";
            this.RXEqButton.UseVisualStyleBackColor = true;
            this.RXEqButton.Click += new System.EventHandler(this.RXEqButton_Click);
            // 
            // TXEqButton
            // 
            this.TXEqButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.TXEqButton.Location = new System.Drawing.Point(490, 160);
            this.TXEqButton.Name = "TXEqButton";
            this.TXEqButton.Size = new System.Drawing.Size(50, 23);
            this.TXEqButton.TabIndex = 370;
            this.TXEqButton.Text = "TXEq";
            this.TXEqButton.UseVisualStyleBackColor = true;
            this.TXEqButton.Click += new System.EventHandler(this.TXEqButton_Click);
            // 
            // AMCarrierLevelControl
            // 
            this.AMCarrierLevelControl.Header = "Car lvl";
            this.AMCarrierLevelControl.HighValue = 0;
            this.AMCarrierLevelControl.Increment = 0;
            this.AMCarrierLevelControl.Location = new System.Drawing.Point(0, 200);
            this.AMCarrierLevelControl.LowValue = 0;
            this.AMCarrierLevelControl.Margin = new System.Windows.Forms.Padding(4);
            this.AMCarrierLevelControl.Name = "AMCarrierLevelControl";
            this.AMCarrierLevelControl.ReadOnly = false;
            this.AMCarrierLevelControl.Size = new System.Drawing.Size(50, 36);
            this.AMCarrierLevelControl.TabIndex = 400;
            this.AMCarrierLevelControl.Tag = "Car lvl";
            this.AMCarrierLevelControl.UpdateDisplayFunction = null;
            this.AMCarrierLevelControl.UpdateRigFunction = null;
            // 
            // InfoButton
            // 
            this.InfoButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.InfoButton.Location = new System.Drawing.Point(350, 280);
            this.InfoButton.Name = "InfoButton";
            this.InfoButton.Size = new System.Drawing.Size(50, 23);
            this.InfoButton.TabIndex = 650;
            this.InfoButton.Text = "Radio Info";
            this.InfoButton.UseVisualStyleBackColor = true;
            this.InfoButton.Click += new System.EventHandler(this.InfoButton_Click);
            // 
            // WidebandNoiseBlankerControl
            // 
            this.WidebandNoiseBlankerControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.WidebandNoiseBlankerControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.WidebandNoiseBlankerControl.Header = "Wide N.B.";
            this.WidebandNoiseBlankerControl.Location = new System.Drawing.Point(490, 40);
            this.WidebandNoiseBlankerControl.Margin = new System.Windows.Forms.Padding(4);
            this.WidebandNoiseBlankerControl.Name = "WidebandNoiseBlankerControl";
            this.WidebandNoiseBlankerControl.ReadOnly = false;
            this.WidebandNoiseBlankerControl.Size = new System.Drawing.Size(50, 36);
            this.WidebandNoiseBlankerControl.SmallSize = new System.Drawing.Size(50, 36);
            this.WidebandNoiseBlankerControl.TabIndex = 95;
            this.WidebandNoiseBlankerControl.Tag = "Wide N.B.";
            this.WidebandNoiseBlankerControl.TheList = null;
            this.WidebandNoiseBlankerControl.UpdateDisplayFunction = null;
            this.WidebandNoiseBlankerControl.UpdateRigFunction = null;
            // 
            // WidebandNoiseBlankerLevelControl
            // 
            this.WidebandNoiseBlankerLevelControl.Header = "WNB lvl";
            this.WidebandNoiseBlankerLevelControl.HighValue = 0;
            this.WidebandNoiseBlankerLevelControl.Increment = 0;
            this.WidebandNoiseBlankerLevelControl.Location = new System.Drawing.Point(560, 40);
            this.WidebandNoiseBlankerLevelControl.LowValue = 0;
            this.WidebandNoiseBlankerLevelControl.Margin = new System.Windows.Forms.Padding(4);
            this.WidebandNoiseBlankerLevelControl.Name = "WidebandNoiseBlankerLevelControl";
            this.WidebandNoiseBlankerLevelControl.ReadOnly = false;
            this.WidebandNoiseBlankerLevelControl.Size = new System.Drawing.Size(50, 36);
            this.WidebandNoiseBlankerLevelControl.TabIndex = 97;
            this.WidebandNoiseBlankerLevelControl.Tag = "WNB lvl";
            this.WidebandNoiseBlankerLevelControl.UpdateDisplayFunction = null;
            this.WidebandNoiseBlankerLevelControl.UpdateRigFunction = null;
            // 
            // DAXTXControl
            // 
            this.DAXTXControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.DAXTXControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.DAXTXControl.Header = "DAX TX";
            this.DAXTXControl.Location = new System.Drawing.Point(280, 240);
            this.DAXTXControl.Margin = new System.Windows.Forms.Padding(4);
            this.DAXTXControl.Name = "DAXTXControl";
            this.DAXTXControl.ReadOnly = false;
            this.DAXTXControl.Size = new System.Drawing.Size(50, 36);
            this.DAXTXControl.SmallSize = new System.Drawing.Size(50, 36);
            this.DAXTXControl.TabIndex = 540;
            this.DAXTXControl.Tag = "DAX TX";
            this.DAXTXControl.TheList = null;
            this.DAXTXControl.UpdateDisplayFunction = null;
            this.DAXTXControl.UpdateRigFunction = null;
            // 
            // AutoprocControl
            // 
            this.AutoprocControl.AccessibleName = "auto processor";
            this.AutoprocControl.AccessibleRole = System.Windows.Forms.AccessibleRole.ComboBox;
            this.AutoprocControl.DropDownHeight = 40;
            this.AutoprocControl.DropDownWidth = 65;
            this.AutoprocControl.FormattingEnabled = true;
            this.AutoprocControl.IntegralHeight = false;
            this.AutoprocControl.Location = new System.Drawing.Point(350, 255);
            this.AutoprocControl.Name = "AutoprocControl";
            this.AutoprocControl.Size = new System.Drawing.Size(65, 21);
            this.AutoprocControl.TabIndex = 551;
            this.AutoprocControl.Tag = "SpProcAuto";
            this.AutoprocControl.SelectedIndexChanged += new System.EventHandler(this.AutoprocControl_SelectedIndexChanged);
            // 
            // AutoprocLabel
            // 
            this.AutoprocLabel.AutoSize = true;
            this.AutoprocLabel.Location = new System.Drawing.Point(350, 240);
            this.AutoprocLabel.Name = "AutoprocLabel";
            this.AutoprocLabel.Size = new System.Drawing.Size(64, 13);
            this.AutoprocLabel.TabIndex = 550;
            this.AutoprocLabel.Text = "SpProcAuto";
            // 
            // TunePowerControl
            // 
            this.TunePowerControl.Header = "TunePWR";
            this.TunePowerControl.HighValue = 0;
            this.TunePowerControl.Increment = 0;
            this.TunePowerControl.Location = new System.Drawing.Point(420, 240);
            this.TunePowerControl.LowValue = 0;
            this.TunePowerControl.Name = "TunePowerControl";
            this.TunePowerControl.ReadOnly = false;
            this.TunePowerControl.Size = new System.Drawing.Size(60, 36);
            this.TunePowerControl.TabIndex = 560;
            this.TunePowerControl.Tag = "TunePWR";
            this.TunePowerControl.UpdateDisplayFunction = null;
            this.TunePowerControl.UpdateRigFunction = null;
            // 
            // PATempBox
            // 
            this.PATempBox.Header = "PATemp";
            this.PATempBox.Location = new System.Drawing.Point(490, 240);
            this.PATempBox.Name = "PATempBox";
            this.PATempBox.ReadOnly = false;
            this.PATempBox.Size = new System.Drawing.Size(60, 36);
            this.PATempBox.TabIndex = 570;
            this.PATempBox.Tag = "PATemp";
            this.PATempBox.UpdateDisplayFunction = null;
            this.PATempBox.UpdateRigFunction = null;
            // 
            // VoltsBox
            // 
            this.VoltsBox.Header = "Volts";
            this.VoltsBox.Location = new System.Drawing.Point(560, 240);
            this.VoltsBox.Name = "VoltsBox";
            this.VoltsBox.ReadOnly = false;
            this.VoltsBox.Size = new System.Drawing.Size(60, 36);
            this.VoltsBox.TabIndex = 580;
            this.VoltsBox.Tag = "Volts";
            this.VoltsBox.UpdateDisplayFunction = null;
            this.VoltsBox.UpdateRigFunction = null;
            // 
            // DiversityStatusControl
            // 
            this.DiversityStatusControl.Header = "Diversity";
            this.DiversityStatusControl.Location = new System.Drawing.Point(630, 240);
            this.DiversityStatusControl.Name = "DiversityStatusControl";
            this.DiversityStatusControl.ReadOnly = false;
            this.DiversityStatusControl.Size = new System.Drawing.Size(90, 36);
            this.DiversityStatusControl.TabIndex = 590;
            this.DiversityStatusControl.Tag = "DiversityStatus";
            this.DiversityStatusControl.UpdateDisplayFunction = null;
            this.DiversityStatusControl.UpdateRigFunction = null;
            // 
            // RFGainControl
            // 
            this.RFGainControl.Header = "RFGain";
            this.RFGainControl.HighValue = 0;
            this.RFGainControl.Increment = 0;
            this.RFGainControl.Location = new System.Drawing.Point(420, 40);
            this.RFGainControl.LowValue = 0;
            this.RFGainControl.Name = "RFGainControl";
            this.RFGainControl.ReadOnly = false;
            this.RFGainControl.Size = new System.Drawing.Size(50, 29);
            this.RFGainControl.TabIndex = 93;
            this.RFGainControl.Tag = "RFGain";
            this.RFGainControl.UpdateDisplayFunction = null;
            this.RFGainControl.UpdateRigFunction = null;
            // 
            // OffsetDirectionControl
            // 
            this.OffsetDirectionControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.OffsetDirectionControl.ExpandedSize = new System.Drawing.Size(70, 80);
            this.OffsetDirectionControl.Header = "ofstdir";
            this.OffsetDirectionControl.Location = new System.Drawing.Point(280, 200);
            this.OffsetDirectionControl.Name = "OffsetDirectionControl";
            this.OffsetDirectionControl.ReadOnly = false;
            this.OffsetDirectionControl.Size = new System.Drawing.Size(52, 36);
            this.OffsetDirectionControl.SmallSize = new System.Drawing.Size(70, 36);
            this.OffsetDirectionControl.TabIndex = 440;
            this.OffsetDirectionControl.Tag = "ofstdir";
            this.OffsetDirectionControl.TheList = null;
            this.OffsetDirectionControl.UpdateDisplayFunction = null;
            this.OffsetDirectionControl.UpdateRigFunction = null;
            // 
            // TXAntControl
            // 
            this.TXAntControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.TXAntControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.TXAntControl.Header = "TXAnt";
            this.TXAntControl.Location = new System.Drawing.Point(0, 240);
            this.TXAntControl.Name = "TXAntControl";
            this.TXAntControl.ReadOnly = false;
            this.TXAntControl.Size = new System.Drawing.Size(50, 36);
            this.TXAntControl.SmallSize = new System.Drawing.Size(50, 36);
            this.TXAntControl.TabIndex = 500;
            this.TXAntControl.Tag = "";
            this.TXAntControl.TheList = null;
            this.TXAntControl.UpdateDisplayFunction = null;
            this.TXAntControl.UpdateRigFunction = null;
            // 
            // Flex6300Filters
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.TXAntControl);
            this.Controls.Add(this.OffsetDirectionControl);
            this.Controls.Add(this.RFGainControl);
            this.Controls.Add(this.VoltsBox);
            this.Controls.Add(this.DiversityStatusControl);
            this.Controls.Add(this.PATempBox);
            this.Controls.Add(this.TunePowerControl);
            this.Controls.Add(this.AutoprocLabel);
            this.Controls.Add(this.AutoprocControl);
            this.Controls.Add(this.DAXTXControl);
            this.Controls.Add(this.WidebandNoiseBlankerLevelControl);
            this.Controls.Add(this.WidebandNoiseBlankerControl);
            this.Controls.Add(this.InfoButton);
            this.Controls.Add(this.AMCarrierLevelControl);
            this.Controls.Add(this.TXEqButton);
            this.Controls.Add(this.RXEqButton);
            this.Controls.Add(this.SBMonitorPanControl);
            this.Controls.Add(this.SBMonitorLevelControl);
            this.Controls.Add(this.MonitorControl);
            this.Controls.Add(this.MicBiasControl);
            this.Controls.Add(this.MicBoostControl);
            this.Controls.Add(this.TXFilterHighControl);
            this.Controls.Add(this.TXFilterLowControl);
            this.Controls.Add(this.CompanderLevelControl);
            this.Controls.Add(this.CompanderControl);
            this.Controls.Add(this.CWLControl);
            this.Controls.Add(this.RXAntControl);
            this.Controls.Add(this.DiversityControl);
            this.Controls.Add(this.EscButton);
            this.Controls.Add(this.ImportButton);
            this.Controls.Add(this.ExportButton);
            this.Controls.Add(this.RecordControl);
            this.Controls.Add(this.PlayControl);
            this.Controls.Add(this.BinauralControl);
            this.Controls.Add(this.FM1750Control);
            this.Controls.Add(this.EmphasisControl);
            this.Controls.Add(this.OffsetControl);
            this.Controls.Add(this.SquelchLevelControl);
            this.Controls.Add(this.SquelchControl);
            this.Controls.Add(this.ToneFrequencyControl);
            this.Controls.Add(this.ToneModeControl);
            this.Controls.Add(this.TNFEnableButton);
            this.Controls.Add(this.TNFButton);
            this.Controls.Add(this.APFLevelControl);
            this.Controls.Add(this.APFControl);
            this.Controls.Add(this.ANFLevelControl);
            this.Controls.Add(this.ANFControl);
            this.Controls.Add(this.VoxDelayControl);
            this.Controls.Add(this.VoxGainControl);
            this.Controls.Add(this.EraseButton);
            this.Controls.Add(this.SaveButton);
            this.Controls.Add(this.XmitPowerControl);
            this.Controls.Add(this.ChangeButton);
            this.Controls.Add(this.PanHighBox);
            this.Controls.Add(this.PanHighLabel);
            this.Controls.Add(this.PanLowBox);
            this.Controls.Add(this.PanLowLabel);
            this.Controls.Add(this.PanBox);
            this.Controls.Add(this.ProcessorSettingControl);
            this.Controls.Add(this.ProcessorOnControl);
            this.Controls.Add(this.SWRControl);
            this.Controls.Add(this.MicPeakBox);
            this.Controls.Add(this.MicGainControl);
            this.Controls.Add(this.MonitorPanControl);
            this.Controls.Add(this.KeyerSpeedControl);
            this.Controls.Add(this.KeyerControl);
            this.Controls.Add(this.CWReverseControl);
            this.Controls.Add(this.SidetoneGainControl);
            this.Controls.Add(this.SidetonePitchControl);
            this.Controls.Add(this.BreakinDelayControl);
            this.Controls.Add(this.NoiseReductionLevelControl);
            this.Controls.Add(this.NoiseReductionControl);
            this.Controls.Add(this.NoiseBlankerLevelControl);
            this.Controls.Add(this.NoiseBlankerControl);
            this.Controls.Add(this.AGCThresholdControl);
            this.Controls.Add(this.AGCSpeedControl);
            this.Controls.Add(this.FilterHighControl);
            this.Controls.Add(this.FilterLowControl);
            this.Name = "Flex6300Filters";
            this.Size = new System.Drawing.Size(700, 325);
            this.Load += new System.EventHandler(this.Filters_Load);
            this.ControlRemoved += new System.Windows.Forms.ControlEventHandler(this.Flex6300Filters_ControlRemoved);
            this.ResumeLayout(false);
            this.PerformLayout();

		}

		#endregion

        private RadioBoxes.NumberBox FilterLowControl;
        private RadioBoxes.NumberBox FilterHighControl;
        private RadioBoxes.Combo AGCSpeedControl;
        private RadioBoxes.NumberBox AGCThresholdControl;
        private RadioBoxes.Combo NoiseBlankerControl;
        private RadioBoxes.NumberBox NoiseBlankerLevelControl;
        private RadioBoxes.Combo NoiseReductionControl;
        private RadioBoxes.NumberBox NoiseReductionLevelControl;
        private RadioBoxes.NumberBox BreakinDelayControl;
        private RadioBoxes.NumberBox SidetonePitchControl;
        private RadioBoxes.NumberBox SidetoneGainControl;
        private RadioBoxes.Combo KeyerControl;
        private RadioBoxes.Combo CWReverseControl;
        private RadioBoxes.NumberBox KeyerSpeedControl;
        private RadioBoxes.NumberBox MonitorPanControl;
        private RadioBoxes.NumberBox MicGainControl;
        private RadioBoxes.InfoBox MicPeakBox;
        private RadioBoxes.InfoBox SWRControl;
        private RadioBoxes.Combo ProcessorOnControl;
        private RadioBoxes.Combo ProcessorSettingControl;
        private System.Windows.Forms.TextBox PanBox;
        private System.Windows.Forms.Label PanLowLabel;
        private System.Windows.Forms.TextBox PanLowBox;
        private System.Windows.Forms.Label PanHighLabel;
        private System.Windows.Forms.TextBox PanHighBox;
        private System.Windows.Forms.Button ChangeButton;
        private RadioBoxes.NumberBox XmitPowerControl;
        private System.Windows.Forms.Button SaveButton;
        private System.Windows.Forms.Button EraseButton;
        private RadioBoxes.NumberBox VoxGainControl;
        private RadioBoxes.NumberBox VoxDelayControl;
        private RadioBoxes.Combo ANFControl;
        private RadioBoxes.NumberBox ANFLevelControl;
        private RadioBoxes.Combo APFControl;
        private RadioBoxes.NumberBox APFLevelControl;
        private System.Windows.Forms.Button TNFButton;
        private System.Windows.Forms.Button TNFEnableButton;
        private RadioBoxes.Combo ToneModeControl;
        private RadioBoxes.Combo ToneFrequencyControl;
        private RadioBoxes.Combo SquelchControl;
        private RadioBoxes.NumberBox SquelchLevelControl;
        private RadioBoxes.NumberBox OffsetControl;
        private RadioBoxes.Combo EmphasisControl;
        private RadioBoxes.Combo FM1750Control;
        private RadioBoxes.Combo BinauralControl;
        private RadioBoxes.Combo PlayControl;
        private RadioBoxes.Combo RecordControl;
        private System.Windows.Forms.Button ExportButton;
        private System.Windows.Forms.Button ImportButton;
        private RadioBoxes.Combo RXAntControl;
        private RadioBoxes.Combo DiversityControl;
        private System.Windows.Forms.Button EscButton;
        private RadioBoxes.Combo CWLControl;
        private RadioBoxes.Combo CompanderControl;
        private RadioBoxes.NumberBox CompanderLevelControl;
        private RadioBoxes.NumberBox TXFilterLowControl;
        private RadioBoxes.NumberBox TXFilterHighControl;
        private RadioBoxes.Combo MicBoostControl;
        private RadioBoxes.Combo MicBiasControl;
        private RadioBoxes.Combo MonitorControl;
        private RadioBoxes.NumberBox SBMonitorLevelControl;
        private RadioBoxes.NumberBox SBMonitorPanControl;
        private System.Windows.Forms.Button RXEqButton;
        private System.Windows.Forms.Button TXEqButton;
        private RadioBoxes.NumberBox AMCarrierLevelControl;
        private System.Windows.Forms.Button InfoButton;
        private RadioBoxes.Combo WidebandNoiseBlankerControl;
        private RadioBoxes.NumberBox WidebandNoiseBlankerLevelControl;
        private RadioBoxes.Combo DAXTXControl;
        private System.Windows.Forms.ComboBox AutoprocControl;
        private System.Windows.Forms.Label AutoprocLabel;
        private RadioBoxes.NumberBox TunePowerControl;
        private RadioBoxes.InfoBox PATempBox;
        private RadioBoxes.InfoBox VoltsBox;
        private RadioBoxes.InfoBox DiversityStatusControl;
        private RadioBoxes.NumberBox RFGainControl;
        private RadioBoxes.Combo OffsetDirectionControl;
        private RadioBoxes.Combo TXAntControl;
    }
}

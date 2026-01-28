namespace Radios
{
    partial class ic9100filters
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
            this.TXBandwidthControl = new RadioBoxes.Combo();
            this.MonitorLevelControl = new RadioBoxes.NumberBox();
            this.MonitorControl = new RadioBoxes.Combo();
            this.VoiceDelayControl = new RadioBoxes.Combo();
            this.ManualNotchControl = new RadioBoxes.Combo();
            this.NotchWidthControl = new RadioBoxes.Combo();
            this.NotchPositionControl = new RadioBoxes.NumberBox();
            this.SSBNotchControl = new RadioBoxes.Combo();
            this.NBWidthControl = new RadioBoxes.NumberBox();
            this.NBDepthControl = new RadioBoxes.NumberBox();
            this.NBLevelControl = new RadioBoxes.NumberBox();
            this.NBControl = new RadioBoxes.Combo();
            this.AGCtcControl = new RadioBoxes.NumberBox();
            this.AGCControl = new RadioBoxes.Combo();
            this.NRLevelControl = new RadioBoxes.NumberBox();
            this.NRControl = new RadioBoxes.Combo();
            this.SSBTransmitBandwidthControl = new RadioBoxes.Combo();
            this.CompLevelControl = new RadioBoxes.NumberBox();
            this.CompControl = new RadioBoxes.Combo();
            this.MicGainControl = new RadioBoxes.NumberBox();
            this.AntivoxControl = new RadioBoxes.NumberBox();
            this.VoxDelayControl = new RadioBoxes.NumberBox();
            this.VoxGainControl = new RadioBoxes.NumberBox();
            this.OuterPBtControl = new RadioBoxes.NumberBox();
            this.InnerPBTControl = new RadioBoxes.NumberBox();
            this.FirstIFControl = new RadioBoxes.Combo();
            this.SidetoneGainControl = new RadioBoxes.NumberBox();
            this.KeyerControl = new RadioBoxes.Combo();
            this.CWPitchControl = new RadioBoxes.NumberBox();
            this.KeyerSpeedControl = new RadioBoxes.NumberBox();
            this.XmitPowerControl = new RadioBoxes.NumberBox();
            this.CWSSBWidthControl = new RadioBoxes.Combo();
            this.FilterControl = new RadioBoxes.Combo();
            this.BkinDelayControl = new RadioBoxes.NumberBox();
            this.FilterTypeControl = new RadioBoxes.Combo();
            this.OffsetControl = new RadioBoxes.NumberBox();
            this.ToneModeControl = new RadioBoxes.Combo();
            this.ToneFrequencyControl = new RadioBoxes.Combo();
            this.FMAGCControl = new RadioBoxes.Combo();
            this.AttenuatorControl = new RadioBoxes.Combo();
            this.UHFPreampControl = new RadioBoxes.Combo();
            this.HFPreampControl = new RadioBoxes.Combo();
            this.SWRControl = new RadioBoxes.InfoBox();
            this.ALCControl = new RadioBoxes.InfoBox();
            this.CompMeterControl = new RadioBoxes.InfoBox();
            this.FMNotchControl = new RadioBoxes.Combo();
            this.AFCControl = new RadioBoxes.Combo();
            this.AFCLimitControl = new RadioBoxes.Combo();
            this.XmitMonitorControl = new RadioBoxes.Combo();
            this.AMWidthControl = new RadioBoxes.Combo();
            this.TuningStepControl = new RadioBoxes.Combo();
            this.SuspendLayout();
            // 
            // TXBandwidthControl
            // 
            this.TXBandwidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.TXBandwidthControl.ExpandedSize = new System.Drawing.Size(120, 80);
            this.TXBandwidthControl.Header = "TX bandwidth";
            this.TXBandwidthControl.Location = new System.Drawing.Point(280, 40);
            this.TXBandwidthControl.Name = "TXBandwidthControl";
            this.TXBandwidthControl.ReadOnly = false;
            this.TXBandwidthControl.Size = new System.Drawing.Size(120, 36);
            this.TXBandwidthControl.SmallSize = new System.Drawing.Size(120, 36);
            this.TXBandwidthControl.TabIndex = 140;
            this.TXBandwidthControl.Tag = "TX bandwidth";
            this.TXBandwidthControl.TheList = null;
            this.TXBandwidthControl.UpdateDisplayFunction = null;
            this.TXBandwidthControl.UpdateRigFunction = null;
            // 
            // MonitorLevelControl
            // 
            this.MonitorLevelControl.Header = "Monlvl";
            this.MonitorLevelControl.HighValue = 0;
            this.MonitorLevelControl.Increment = 0;
            this.MonitorLevelControl.Location = new System.Drawing.Point(490, 40);
            this.MonitorLevelControl.LowValue = 0;
            this.MonitorLevelControl.Name = "MonitorLevelControl";
            this.MonitorLevelControl.ReadOnly = false;
            this.MonitorLevelControl.Size = new System.Drawing.Size(50, 36);
            this.MonitorLevelControl.TabIndex = 170;
            this.MonitorLevelControl.Tag = "Monlvl";
            this.MonitorLevelControl.UpdateDisplayFunction = null;
            this.MonitorLevelControl.UpdateRigFunction = null;
            // 
            // MonitorControl
            // 
            this.MonitorControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.MonitorControl.ExpandedSize = new System.Drawing.Size(65, 56);
            this.MonitorControl.Header = "Monitor";
            this.MonitorControl.Location = new System.Drawing.Point(420, 40);
            this.MonitorControl.Name = "MonitorControl";
            this.MonitorControl.ReadOnly = false;
            this.MonitorControl.Size = new System.Drawing.Size(65, 36);
            this.MonitorControl.SmallSize = new System.Drawing.Size(65, 36);
            this.MonitorControl.TabIndex = 160;
            this.MonitorControl.Tag = "Monitor";
            this.MonitorControl.TheList = null;
            this.MonitorControl.UpdateDisplayFunction = null;
            this.MonitorControl.UpdateRigFunction = null;
            // 
            // VoiceDelayControl
            // 
            this.VoiceDelayControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.VoiceDelayControl.ExpandedSize = new System.Drawing.Size(65, 80);
            this.VoiceDelayControl.Header = "VoDelay";
            this.VoiceDelayControl.Location = new System.Drawing.Point(210, 0);
            this.VoiceDelayControl.Name = "VoiceDelayControl";
            this.VoiceDelayControl.ReadOnly = false;
            this.VoiceDelayControl.Size = new System.Drawing.Size(65, 36);
            this.VoiceDelayControl.SmallSize = new System.Drawing.Size(65, 36);
            this.VoiceDelayControl.TabIndex = 40;
            this.VoiceDelayControl.Tag = "VoDelay";
            this.VoiceDelayControl.TheList = null;
            this.VoiceDelayControl.UpdateDisplayFunction = null;
            this.VoiceDelayControl.UpdateRigFunction = null;
            // 
            // ManualNotchControl
            // 
            this.ManualNotchControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ManualNotchControl.ExpandedSize = new System.Drawing.Size(65, 56);
            this.ManualNotchControl.Header = "Notch";
            this.ManualNotchControl.Location = new System.Drawing.Point(0, 160);
            this.ManualNotchControl.Name = "ManualNotchControl";
            this.ManualNotchControl.ReadOnly = false;
            this.ManualNotchControl.Size = new System.Drawing.Size(65, 36);
            this.ManualNotchControl.SmallSize = new System.Drawing.Size(65, 36);
            this.ManualNotchControl.TabIndex = 400;
            this.ManualNotchControl.Tag = "Notch";
            this.ManualNotchControl.TheList = null;
            this.ManualNotchControl.UpdateDisplayFunction = null;
            this.ManualNotchControl.UpdateRigFunction = null;
            // 
            // NotchWidthControl
            // 
            this.NotchWidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NotchWidthControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.NotchWidthControl.Header = "Ntchwdth";
            this.NotchWidthControl.Location = new System.Drawing.Point(140, 160);
            this.NotchWidthControl.Name = "NotchWidthControl";
            this.NotchWidthControl.ReadOnly = false;
            this.NotchWidthControl.Size = new System.Drawing.Size(50, 36);
            this.NotchWidthControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NotchWidthControl.TabIndex = 420;
            this.NotchWidthControl.Tag = "Ntchwdth";
            this.NotchWidthControl.TheList = null;
            this.NotchWidthControl.UpdateDisplayFunction = null;
            this.NotchWidthControl.UpdateRigFunction = null;
            // 
            // NotchPositionControl
            // 
            this.NotchPositionControl.Header = "Ntchpos";
            this.NotchPositionControl.HighValue = 0;
            this.NotchPositionControl.Increment = 0;
            this.NotchPositionControl.Location = new System.Drawing.Point(70, 160);
            this.NotchPositionControl.LowValue = 0;
            this.NotchPositionControl.Name = "NotchPositionControl";
            this.NotchPositionControl.ReadOnly = false;
            this.NotchPositionControl.Size = new System.Drawing.Size(50, 36);
            this.NotchPositionControl.TabIndex = 410;
            this.NotchPositionControl.Tag = "Ntchpos";
            this.NotchPositionControl.UpdateDisplayFunction = null;
            this.NotchPositionControl.UpdateRigFunction = null;
            // 
            // SSBNotchControl
            // 
            this.SSBNotchControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.SSBNotchControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.SSBNotchControl.Header = "Notch";
            this.SSBNotchControl.Location = new System.Drawing.Point(0, 160);
            this.SSBNotchControl.Name = "SSBNotchControl";
            this.SSBNotchControl.ReadOnly = false;
            this.SSBNotchControl.Size = new System.Drawing.Size(50, 36);
            this.SSBNotchControl.SmallSize = new System.Drawing.Size(50, 36);
            this.SSBNotchControl.TabIndex = 400;
            this.SSBNotchControl.Tag = "Notch";
            this.SSBNotchControl.TheList = null;
            this.SSBNotchControl.UpdateDisplayFunction = null;
            this.SSBNotchControl.UpdateRigFunction = null;
            // 
            // NBWidthControl
            // 
            this.NBWidthControl.Header = "NBwidth";
            this.NBWidthControl.HighValue = 0;
            this.NBWidthControl.Increment = 0;
            this.NBWidthControl.Location = new System.Drawing.Point(350, 120);
            this.NBWidthControl.LowValue = 0;
            this.NBWidthControl.Name = "NBWidthControl";
            this.NBWidthControl.ReadOnly = false;
            this.NBWidthControl.Size = new System.Drawing.Size(50, 36);
            this.NBWidthControl.TabIndex = 350;
            this.NBWidthControl.Tag = "NBwidth";
            this.NBWidthControl.UpdateDisplayFunction = null;
            this.NBWidthControl.UpdateRigFunction = null;
            // 
            // NBDepthControl
            // 
            this.NBDepthControl.Header = "NBdepth";
            this.NBDepthControl.HighValue = 0;
            this.NBDepthControl.Increment = 0;
            this.NBDepthControl.Location = new System.Drawing.Point(280, 120);
            this.NBDepthControl.LowValue = 0;
            this.NBDepthControl.Name = "NBDepthControl";
            this.NBDepthControl.ReadOnly = false;
            this.NBDepthControl.Size = new System.Drawing.Size(50, 36);
            this.NBDepthControl.TabIndex = 340;
            this.NBDepthControl.Tag = "NBdepth";
            this.NBDepthControl.UpdateDisplayFunction = null;
            this.NBDepthControl.UpdateRigFunction = null;
            // 
            // NBLevelControl
            // 
            this.NBLevelControl.Header = "NBlvl";
            this.NBLevelControl.HighValue = 0;
            this.NBLevelControl.Increment = 0;
            this.NBLevelControl.Location = new System.Drawing.Point(210, 120);
            this.NBLevelControl.LowValue = 0;
            this.NBLevelControl.Name = "NBLevelControl";
            this.NBLevelControl.ReadOnly = false;
            this.NBLevelControl.Size = new System.Drawing.Size(50, 36);
            this.NBLevelControl.TabIndex = 330;
            this.NBLevelControl.Tag = "NBlvl";
            this.NBLevelControl.UpdateDisplayFunction = null;
            this.NBLevelControl.UpdateRigFunction = null;
            // 
            // NBControl
            // 
            this.NBControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NBControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.NBControl.Header = "NB";
            this.NBControl.Location = new System.Drawing.Point(140, 120);
            this.NBControl.Name = "NBControl";
            this.NBControl.ReadOnly = false;
            this.NBControl.Size = new System.Drawing.Size(50, 36);
            this.NBControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NBControl.TabIndex = 320;
            this.NBControl.Tag = "NB";
            this.NBControl.TheList = null;
            this.NBControl.UpdateDisplayFunction = null;
            this.NBControl.UpdateRigFunction = null;
            // 
            // AGCtcControl
            // 
            this.AGCtcControl.Header = "AGCtc";
            this.AGCtcControl.HighValue = 0;
            this.AGCtcControl.Increment = 0;
            this.AGCtcControl.Location = new System.Drawing.Point(70, 120);
            this.AGCtcControl.LowValue = 0;
            this.AGCtcControl.Name = "AGCtcControl";
            this.AGCtcControl.ReadOnly = false;
            this.AGCtcControl.Size = new System.Drawing.Size(50, 36);
            this.AGCtcControl.TabIndex = 310;
            this.AGCtcControl.Tag = "AGCtc";
            this.AGCtcControl.UpdateDisplayFunction = null;
            this.AGCtcControl.UpdateRigFunction = null;
            // 
            // AGCControl
            // 
            this.AGCControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AGCControl.ExpandedSize = new System.Drawing.Size(60, 80);
            this.AGCControl.Header = "AGC";
            this.AGCControl.Location = new System.Drawing.Point(0, 120);
            this.AGCControl.Name = "AGCControl";
            this.AGCControl.ReadOnly = false;
            this.AGCControl.Size = new System.Drawing.Size(60, 36);
            this.AGCControl.SmallSize = new System.Drawing.Size(60, 36);
            this.AGCControl.TabIndex = 300;
            this.AGCControl.Tag = "AGC";
            this.AGCControl.TheList = null;
            this.AGCControl.UpdateDisplayFunction = null;
            this.AGCControl.UpdateRigFunction = null;
            // 
            // NRLevelControl
            // 
            this.NRLevelControl.Header = "NRlvl";
            this.NRLevelControl.HighValue = 0;
            this.NRLevelControl.Increment = 0;
            this.NRLevelControl.Location = new System.Drawing.Point(490, 80);
            this.NRLevelControl.LowValue = 0;
            this.NRLevelControl.Name = "NRLevelControl";
            this.NRLevelControl.ReadOnly = false;
            this.NRLevelControl.Size = new System.Drawing.Size(50, 36);
            this.NRLevelControl.TabIndex = 270;
            this.NRLevelControl.Tag = "NRlvl";
            this.NRLevelControl.UpdateDisplayFunction = null;
            this.NRLevelControl.UpdateRigFunction = null;
            // 
            // NRControl
            // 
            this.NRControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.NRControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.NRControl.Header = "NR";
            this.NRControl.Location = new System.Drawing.Point(420, 80);
            this.NRControl.Name = "NRControl";
            this.NRControl.ReadOnly = false;
            this.NRControl.Size = new System.Drawing.Size(50, 36);
            this.NRControl.SmallSize = new System.Drawing.Size(50, 36);
            this.NRControl.TabIndex = 260;
            this.NRControl.Tag = "NR";
            this.NRControl.TheList = null;
            this.NRControl.UpdateDisplayFunction = null;
            this.NRControl.UpdateRigFunction = null;
            // 
            // SSBTransmitBandwidthControl
            // 
            this.SSBTransmitBandwidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.SSBTransmitBandwidthControl.ExpandedSize = new System.Drawing.Size(65, 56);
            this.SSBTransmitBandwidthControl.Header = "XmitBW";
            this.SSBTransmitBandwidthControl.Location = new System.Drawing.Point(210, 40);
            this.SSBTransmitBandwidthControl.Name = "SSBTransmitBandwidthControl";
            this.SSBTransmitBandwidthControl.ReadOnly = false;
            this.SSBTransmitBandwidthControl.Size = new System.Drawing.Size(65, 36);
            this.SSBTransmitBandwidthControl.SmallSize = new System.Drawing.Size(65, 36);
            this.SSBTransmitBandwidthControl.TabIndex = 130;
            this.SSBTransmitBandwidthControl.Tag = "XmitBW";
            this.SSBTransmitBandwidthControl.TheList = null;
            this.SSBTransmitBandwidthControl.UpdateDisplayFunction = null;
            this.SSBTransmitBandwidthControl.UpdateRigFunction = null;
            // 
            // CompLevelControl
            // 
            this.CompLevelControl.Header = "Complvl";
            this.CompLevelControl.HighValue = 0;
            this.CompLevelControl.Increment = 0;
            this.CompLevelControl.Location = new System.Drawing.Point(140, 40);
            this.CompLevelControl.LowValue = 0;
            this.CompLevelControl.Name = "CompLevelControl";
            this.CompLevelControl.ReadOnly = false;
            this.CompLevelControl.Size = new System.Drawing.Size(50, 36);
            this.CompLevelControl.TabIndex = 120;
            this.CompLevelControl.Tag = "Complvl";
            this.CompLevelControl.UpdateDisplayFunction = null;
            this.CompLevelControl.UpdateRigFunction = null;
            // 
            // CompControl
            // 
            this.CompControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.CompControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.CompControl.Header = "Proc";
            this.CompControl.Location = new System.Drawing.Point(70, 40);
            this.CompControl.Name = "CompControl";
            this.CompControl.ReadOnly = false;
            this.CompControl.Size = new System.Drawing.Size(50, 36);
            this.CompControl.SmallSize = new System.Drawing.Size(50, 36);
            this.CompControl.TabIndex = 110;
            this.CompControl.Tag = "Proc";
            this.CompControl.TheList = null;
            this.CompControl.UpdateDisplayFunction = null;
            this.CompControl.UpdateRigFunction = null;
            // 
            // MicGainControl
            // 
            this.MicGainControl.Header = "Micgain";
            this.MicGainControl.HighValue = 0;
            this.MicGainControl.Increment = 0;
            this.MicGainControl.Location = new System.Drawing.Point(0, 40);
            this.MicGainControl.LowValue = 0;
            this.MicGainControl.Name = "MicGainControl";
            this.MicGainControl.ReadOnly = false;
            this.MicGainControl.Size = new System.Drawing.Size(50, 36);
            this.MicGainControl.TabIndex = 100;
            this.MicGainControl.Tag = "Micgain";
            this.MicGainControl.UpdateDisplayFunction = null;
            this.MicGainControl.UpdateRigFunction = null;
            // 
            // AntivoxControl
            // 
            this.AntivoxControl.Header = "Antivox";
            this.AntivoxControl.HighValue = 0;
            this.AntivoxControl.Increment = 0;
            this.AntivoxControl.Location = new System.Drawing.Point(140, 0);
            this.AntivoxControl.LowValue = 0;
            this.AntivoxControl.Name = "AntivoxControl";
            this.AntivoxControl.ReadOnly = false;
            this.AntivoxControl.Size = new System.Drawing.Size(50, 36);
            this.AntivoxControl.TabIndex = 30;
            this.AntivoxControl.Tag = "Antivox";
            this.AntivoxControl.UpdateDisplayFunction = null;
            this.AntivoxControl.UpdateRigFunction = null;
            // 
            // VoxDelayControl
            // 
            this.VoxDelayControl.Header = "Voxdelay";
            this.VoxDelayControl.HighValue = 0;
            this.VoxDelayControl.Increment = 0;
            this.VoxDelayControl.Location = new System.Drawing.Point(70, 0);
            this.VoxDelayControl.LowValue = 0;
            this.VoxDelayControl.Name = "VoxDelayControl";
            this.VoxDelayControl.ReadOnly = false;
            this.VoxDelayControl.Size = new System.Drawing.Size(50, 36);
            this.VoxDelayControl.TabIndex = 20;
            this.VoxDelayControl.Tag = "Voxdelay";
            this.VoxDelayControl.UpdateDisplayFunction = null;
            this.VoxDelayControl.UpdateRigFunction = null;
            // 
            // VoxGainControl
            // 
            this.VoxGainControl.Header = "Voxgain";
            this.VoxGainControl.HighValue = 0;
            this.VoxGainControl.Increment = 0;
            this.VoxGainControl.Location = new System.Drawing.Point(0, 0);
            this.VoxGainControl.LowValue = 0;
            this.VoxGainControl.Name = "VoxGainControl";
            this.VoxGainControl.ReadOnly = false;
            this.VoxGainControl.Size = new System.Drawing.Size(50, 36);
            this.VoxGainControl.TabIndex = 10;
            this.VoxGainControl.Tag = "Voxgain";
            this.VoxGainControl.UpdateDisplayFunction = null;
            this.VoxGainControl.UpdateRigFunction = null;
            // 
            // OuterPBtControl
            // 
            this.OuterPBtControl.Header = "PBTo";
            this.OuterPBtControl.HighValue = 0;
            this.OuterPBtControl.Increment = 0;
            this.OuterPBtControl.Location = new System.Drawing.Point(280, 80);
            this.OuterPBtControl.LowValue = 0;
            this.OuterPBtControl.Name = "OuterPBtControl";
            this.OuterPBtControl.ReadOnly = false;
            this.OuterPBtControl.Size = new System.Drawing.Size(50, 36);
            this.OuterPBtControl.TabIndex = 240;
            this.OuterPBtControl.Tag = "PBT";
            this.OuterPBtControl.UpdateDisplayFunction = null;
            this.OuterPBtControl.UpdateRigFunction = null;
            // 
            // InnerPBTControl
            // 
            this.InnerPBTControl.Header = "PBTi";
            this.InnerPBTControl.HighValue = 0;
            this.InnerPBTControl.Increment = 0;
            this.InnerPBTControl.Location = new System.Drawing.Point(210, 80);
            this.InnerPBTControl.LowValue = 0;
            this.InnerPBTControl.Name = "InnerPBTControl";
            this.InnerPBTControl.ReadOnly = false;
            this.InnerPBTControl.Size = new System.Drawing.Size(50, 36);
            this.InnerPBTControl.TabIndex = 230;
            this.InnerPBTControl.Tag = "PBTi";
            this.InnerPBTControl.UpdateDisplayFunction = null;
            this.InnerPBTControl.UpdateRigFunction = null;
            // 
            // FirstIFControl
            // 
            this.FirstIFControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FirstIFControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.FirstIFControl.Header = "1stIF";
            this.FirstIFControl.Location = new System.Drawing.Point(350, 80);
            this.FirstIFControl.Name = "FirstIFControl";
            this.FirstIFControl.ReadOnly = false;
            this.FirstIFControl.Size = new System.Drawing.Size(50, 36);
            this.FirstIFControl.SmallSize = new System.Drawing.Size(50, 36);
            this.FirstIFControl.TabIndex = 250;
            this.FirstIFControl.Tag = "1stIF";
            this.FirstIFControl.TheList = null;
            this.FirstIFControl.UpdateDisplayFunction = null;
            this.FirstIFControl.UpdateRigFunction = null;
            // 
            // SidetoneGainControl
            // 
            this.SidetoneGainControl.Header = "STvol";
            this.SidetoneGainControl.HighValue = 0;
            this.SidetoneGainControl.Increment = 0;
            this.SidetoneGainControl.Location = new System.Drawing.Point(280, 0);
            this.SidetoneGainControl.LowValue = 0;
            this.SidetoneGainControl.Name = "SidetoneGainControl";
            this.SidetoneGainControl.ReadOnly = false;
            this.SidetoneGainControl.Size = new System.Drawing.Size(50, 36);
            this.SidetoneGainControl.TabIndex = 50;
            this.SidetoneGainControl.Tag = "STvol";
            this.SidetoneGainControl.UpdateDisplayFunction = null;
            this.SidetoneGainControl.UpdateRigFunction = null;
            // 
            // KeyerControl
            // 
            this.KeyerControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.KeyerControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.KeyerControl.Header = "Keyer";
            this.KeyerControl.Location = new System.Drawing.Point(70, 0);
            this.KeyerControl.Name = "KeyerControl";
            this.KeyerControl.ReadOnly = false;
            this.KeyerControl.Size = new System.Drawing.Size(50, 36);
            this.KeyerControl.SmallSize = new System.Drawing.Size(50, 36);
            this.KeyerControl.TabIndex = 20;
            this.KeyerControl.Tag = "Keyer";
            this.KeyerControl.TheList = null;
            this.KeyerControl.UpdateDisplayFunction = null;
            this.KeyerControl.UpdateRigFunction = null;
            // 
            // CWPitchControl
            // 
            this.CWPitchControl.Header = "Pitch";
            this.CWPitchControl.HighValue = 0;
            this.CWPitchControl.Increment = 0;
            this.CWPitchControl.Location = new System.Drawing.Point(210, 0);
            this.CWPitchControl.LowValue = 0;
            this.CWPitchControl.Name = "CWPitchControl";
            this.CWPitchControl.ReadOnly = false;
            this.CWPitchControl.Size = new System.Drawing.Size(50, 36);
            this.CWPitchControl.TabIndex = 40;
            this.CWPitchControl.Tag = "Pitch";
            this.CWPitchControl.UpdateDisplayFunction = null;
            this.CWPitchControl.UpdateRigFunction = null;
            // 
            // KeyerSpeedControl
            // 
            this.KeyerSpeedControl.Header = "Speed";
            this.KeyerSpeedControl.HighValue = 0;
            this.KeyerSpeedControl.Increment = 0;
            this.KeyerSpeedControl.Location = new System.Drawing.Point(140, 0);
            this.KeyerSpeedControl.LowValue = 0;
            this.KeyerSpeedControl.Name = "KeyerSpeedControl";
            this.KeyerSpeedControl.ReadOnly = false;
            this.KeyerSpeedControl.Size = new System.Drawing.Size(50, 36);
            this.KeyerSpeedControl.TabIndex = 30;
            this.KeyerSpeedControl.Tag = "Speed";
            this.KeyerSpeedControl.UpdateDisplayFunction = null;
            this.KeyerSpeedControl.UpdateRigFunction = null;
            // 
            // XmitPowerControl
            // 
            this.XmitPowerControl.Header = "Power";
            this.XmitPowerControl.HighValue = 0;
            this.XmitPowerControl.Increment = 0;
            this.XmitPowerControl.Location = new System.Drawing.Point(140, 280);
            this.XmitPowerControl.LowValue = 0;
            this.XmitPowerControl.Name = "XmitPowerControl";
            this.XmitPowerControl.ReadOnly = false;
            this.XmitPowerControl.Size = new System.Drawing.Size(50, 36);
            this.XmitPowerControl.TabIndex = 620;
            this.XmitPowerControl.Tag = "Power";
            this.XmitPowerControl.UpdateDisplayFunction = null;
            this.XmitPowerControl.UpdateRigFunction = null;
            // 
            // CWSSBWidthControl
            // 
            this.CWSSBWidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.CWSSBWidthControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.CWSSBWidthControl.Header = "Width";
            this.CWSSBWidthControl.Location = new System.Drawing.Point(70, 80);
            this.CWSSBWidthControl.Name = "CWSSBWidthControl";
            this.CWSSBWidthControl.ReadOnly = false;
            this.CWSSBWidthControl.Size = new System.Drawing.Size(50, 36);
            this.CWSSBWidthControl.SmallSize = new System.Drawing.Size(50, 36);
            this.CWSSBWidthControl.TabIndex = 210;
            this.CWSSBWidthControl.Tag = "Width";
            this.CWSSBWidthControl.TheList = null;
            this.CWSSBWidthControl.UpdateDisplayFunction = null;
            this.CWSSBWidthControl.UpdateRigFunction = null;
            // 
            // FilterControl
            // 
            this.FilterControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FilterControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.FilterControl.Header = "Filter";
            this.FilterControl.Location = new System.Drawing.Point(0, 80);
            this.FilterControl.Name = "FilterControl";
            this.FilterControl.ReadOnly = false;
            this.FilterControl.Size = new System.Drawing.Size(50, 36);
            this.FilterControl.SmallSize = new System.Drawing.Size(50, 36);
            this.FilterControl.TabIndex = 200;
            this.FilterControl.Tag = "Filter";
            this.FilterControl.TheList = null;
            this.FilterControl.UpdateDisplayFunction = null;
            this.FilterControl.UpdateRigFunction = null;
            // 
            // BkinDelayControl
            // 
            this.BkinDelayControl.Header = "BkinDel";
            this.BkinDelayControl.HighValue = 0;
            this.BkinDelayControl.Increment = 0;
            this.BkinDelayControl.Location = new System.Drawing.Point(0, 0);
            this.BkinDelayControl.LowValue = 0;
            this.BkinDelayControl.Name = "BkinDelayControl";
            this.BkinDelayControl.ReadOnly = false;
            this.BkinDelayControl.Size = new System.Drawing.Size(50, 36);
            this.BkinDelayControl.TabIndex = 10;
            this.BkinDelayControl.Tag = "BkinDel";
            this.BkinDelayControl.UpdateDisplayFunction = null;
            this.BkinDelayControl.UpdateRigFunction = null;
            // 
            // FilterTypeControl
            // 
            this.FilterTypeControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FilterTypeControl.ExpandedSize = new System.Drawing.Size(60, 50);
            this.FilterTypeControl.Header = "Ftype";
            this.FilterTypeControl.Location = new System.Drawing.Point(140, 80);
            this.FilterTypeControl.Name = "FilterTypeControl";
            this.FilterTypeControl.ReadOnly = false;
            this.FilterTypeControl.Size = new System.Drawing.Size(60, 36);
            this.FilterTypeControl.SmallSize = new System.Drawing.Size(60, 36);
            this.FilterTypeControl.TabIndex = 220;
            this.FilterTypeControl.Tag = "Ftype";
            this.FilterTypeControl.TheList = null;
            this.FilterTypeControl.UpdateDisplayFunction = null;
            this.FilterTypeControl.UpdateRigFunction = null;
            // 
            // OffsetControl
            // 
            this.OffsetControl.Header = "Offset";
            this.OffsetControl.HighValue = 0;
            this.OffsetControl.Increment = 0;
            this.OffsetControl.Location = new System.Drawing.Point(0, 160);
            this.OffsetControl.LowValue = 0;
            this.OffsetControl.Name = "OffsetControl";
            this.OffsetControl.ReadOnly = false;
            this.OffsetControl.Size = new System.Drawing.Size(50, 36);
            this.OffsetControl.TabIndex = 400;
            this.OffsetControl.Tag = "Offset";
            this.OffsetControl.UpdateDisplayFunction = null;
            this.OffsetControl.UpdateRigFunction = null;
            // 
            // ToneModeControl
            // 
            this.ToneModeControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ToneModeControl.ExpandedSize = new System.Drawing.Size(60, 80);
            this.ToneModeControl.Header = "Tonemode";
            this.ToneModeControl.Location = new System.Drawing.Point(70, 160);
            this.ToneModeControl.Name = "ToneModeControl";
            this.ToneModeControl.ReadOnly = false;
            this.ToneModeControl.Size = new System.Drawing.Size(60, 36);
            this.ToneModeControl.SmallSize = new System.Drawing.Size(60, 36);
            this.ToneModeControl.TabIndex = 410;
            this.ToneModeControl.Tag = "Tonemode";
            this.ToneModeControl.TheList = null;
            this.ToneModeControl.UpdateDisplayFunction = null;
            this.ToneModeControl.UpdateRigFunction = null;
            // 
            // ToneFrequencyControl
            // 
            this.ToneFrequencyControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.ToneFrequencyControl.ExpandedSize = new System.Drawing.Size(60, 80);
            this.ToneFrequencyControl.Header = "Tonefreq";
            this.ToneFrequencyControl.Location = new System.Drawing.Point(140, 160);
            this.ToneFrequencyControl.Name = "ToneFrequencyControl";
            this.ToneFrequencyControl.ReadOnly = false;
            this.ToneFrequencyControl.Size = new System.Drawing.Size(60, 36);
            this.ToneFrequencyControl.SmallSize = new System.Drawing.Size(60, 36);
            this.ToneFrequencyControl.TabIndex = 420;
            this.ToneFrequencyControl.Tag = "Tonefreq";
            this.ToneFrequencyControl.TheList = null;
            this.ToneFrequencyControl.UpdateDisplayFunction = null;
            this.ToneFrequencyControl.UpdateRigFunction = null;
            // 
            // FMAGCControl
            // 
            this.FMAGCControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FMAGCControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.FMAGCControl.Header = "AGC";
            this.FMAGCControl.Location = new System.Drawing.Point(0, 120);
            this.FMAGCControl.Name = "FMAGCControl";
            this.FMAGCControl.ReadOnly = false;
            this.FMAGCControl.Size = new System.Drawing.Size(50, 36);
            this.FMAGCControl.SmallSize = new System.Drawing.Size(50, 36);
            this.FMAGCControl.TabIndex = 300;
            this.FMAGCControl.Tag = "AGC";
            this.FMAGCControl.TheList = null;
            this.FMAGCControl.UpdateDisplayFunction = null;
            this.FMAGCControl.UpdateRigFunction = null;
            // 
            // AttenuatorControl
            // 
            this.AttenuatorControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AttenuatorControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.AttenuatorControl.Header = "Atten";
            this.AttenuatorControl.Location = new System.Drawing.Point(0, 280);
            this.AttenuatorControl.Name = "AttenuatorControl";
            this.AttenuatorControl.ReadOnly = false;
            this.AttenuatorControl.Size = new System.Drawing.Size(50, 36);
            this.AttenuatorControl.SmallSize = new System.Drawing.Size(50, 36);
            this.AttenuatorControl.TabIndex = 600;
            this.AttenuatorControl.Tag = "Atten";
            this.AttenuatorControl.TheList = null;
            this.AttenuatorControl.UpdateDisplayFunction = null;
            this.AttenuatorControl.UpdateRigFunction = null;
            // 
            // UHFPreampControl
            // 
            this.UHFPreampControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.UHFPreampControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.UHFPreampControl.Header = "Preamp";
            this.UHFPreampControl.Location = new System.Drawing.Point(70, 280);
            this.UHFPreampControl.Name = "UHFPreampControl";
            this.UHFPreampControl.ReadOnly = false;
            this.UHFPreampControl.Size = new System.Drawing.Size(50, 36);
            this.UHFPreampControl.SmallSize = new System.Drawing.Size(50, 36);
            this.UHFPreampControl.TabIndex = 610;
            this.UHFPreampControl.Tag = "Preamp";
            this.UHFPreampControl.TheList = null;
            this.UHFPreampControl.UpdateDisplayFunction = null;
            this.UHFPreampControl.UpdateRigFunction = null;
            // 
            // HFPreampControl
            // 
            this.HFPreampControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.HFPreampControl.ExpandedSize = new System.Drawing.Size(50, 76);
            this.HFPreampControl.Header = "Preamp";
            this.HFPreampControl.Location = new System.Drawing.Point(70, 280);
            this.HFPreampControl.Name = "HFPreampControl";
            this.HFPreampControl.ReadOnly = false;
            this.HFPreampControl.Size = new System.Drawing.Size(50, 36);
            this.HFPreampControl.SmallSize = new System.Drawing.Size(50, 36);
            this.HFPreampControl.TabIndex = 610;
            this.HFPreampControl.Tag = "Preamp";
            this.HFPreampControl.TheList = null;
            this.HFPreampControl.UpdateDisplayFunction = null;
            this.HFPreampControl.UpdateRigFunction = null;
            // 
            // SWRControl
            // 
            this.SWRControl.Header = "SWR";
            this.SWRControl.Location = new System.Drawing.Point(210, 280);
            this.SWRControl.Name = "SWRControl";
            this.SWRControl.ReadOnly = false;
            this.SWRControl.Size = new System.Drawing.Size(50, 36);
            this.SWRControl.TabIndex = 630;
            this.SWRControl.Tag = "SWR";
            this.SWRControl.UpdateDisplayFunction = null;
            this.SWRControl.UpdateRigFunction = null;
            this.SWRControl.Enter += new System.EventHandler(this.SWRControl_Enter);
            this.SWRControl.Leave += new System.EventHandler(this.SWRControl_Leave);
            // 
            // ALCControl
            // 
            this.ALCControl.Header = "ALC";
            this.ALCControl.Location = new System.Drawing.Point(280, 280);
            this.ALCControl.Name = "ALCControl";
            this.ALCControl.ReadOnly = false;
            this.ALCControl.Size = new System.Drawing.Size(50, 36);
            this.ALCControl.TabIndex = 640;
            this.ALCControl.Tag = "ALC";
            this.ALCControl.UpdateDisplayFunction = null;
            this.ALCControl.UpdateRigFunction = null;
            this.ALCControl.Enter += new System.EventHandler(this.ALCControl_Enter);
            this.ALCControl.Leave += new System.EventHandler(this.ALCControl_Leave);
            // 
            // CompMeterControl
            // 
            this.CompMeterControl.Header = "Comp";
            this.CompMeterControl.Location = new System.Drawing.Point(350, 280);
            this.CompMeterControl.Name = "CompMeterControl";
            this.CompMeterControl.ReadOnly = false;
            this.CompMeterControl.Size = new System.Drawing.Size(50, 36);
            this.CompMeterControl.TabIndex = 650;
            this.CompMeterControl.Tag = "Comp";
            this.CompMeterControl.UpdateDisplayFunction = null;
            this.CompMeterControl.UpdateRigFunction = null;
            this.CompMeterControl.Enter += new System.EventHandler(this.CompMeterControl_Enter);
            this.CompMeterControl.Leave += new System.EventHandler(this.CompMeterControl_Leave);
            // 
            // FMNotchControl
            // 
            this.FMNotchControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.FMNotchControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.FMNotchControl.Header = "Notch";
            this.FMNotchControl.Location = new System.Drawing.Point(280, 120);
            this.FMNotchControl.Name = "FMNotchControl";
            this.FMNotchControl.ReadOnly = false;
            this.FMNotchControl.Size = new System.Drawing.Size(50, 36);
            this.FMNotchControl.SmallSize = new System.Drawing.Size(50, 36);
            this.FMNotchControl.TabIndex = 340;
            this.FMNotchControl.Tag = "Notch";
            this.FMNotchControl.TheList = null;
            this.FMNotchControl.UpdateDisplayFunction = null;
            this.FMNotchControl.UpdateRigFunction = null;
            // 
            // AFCControl
            // 
            this.AFCControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AFCControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.AFCControl.Header = "AFC";
            this.AFCControl.Location = new System.Drawing.Point(70, 40);
            this.AFCControl.Name = "AFCControl";
            this.AFCControl.ReadOnly = false;
            this.AFCControl.Size = new System.Drawing.Size(50, 36);
            this.AFCControl.SmallSize = new System.Drawing.Size(50, 36);
            this.AFCControl.TabIndex = 110;
            this.AFCControl.Tag = "AFC";
            this.AFCControl.TheList = null;
            this.AFCControl.UpdateDisplayFunction = null;
            this.AFCControl.UpdateRigFunction = null;
            // 
            // AFCLimitControl
            // 
            this.AFCLimitControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AFCLimitControl.ExpandedSize = new System.Drawing.Size(60, 56);
            this.AFCLimitControl.Header = "AfcLimit";
            this.AFCLimitControl.Location = new System.Drawing.Point(140, 40);
            this.AFCLimitControl.Name = "AFCLimitControl";
            this.AFCLimitControl.ReadOnly = false;
            this.AFCLimitControl.Size = new System.Drawing.Size(60, 36);
            this.AFCLimitControl.SmallSize = new System.Drawing.Size(60, 36);
            this.AFCLimitControl.TabIndex = 120;
            this.AFCLimitControl.Tag = "AfcLimit";
            this.AFCLimitControl.TheList = null;
            this.AFCLimitControl.UpdateDisplayFunction = null;
            this.AFCLimitControl.UpdateRigFunction = null;
            // 
            // XmitMonitorControl
            // 
            this.XmitMonitorControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.XmitMonitorControl.ExpandedSize = new System.Drawing.Size(50, 56);
            this.XmitMonitorControl.Header = "Monchk";
            this.XmitMonitorControl.Location = new System.Drawing.Point(210, 160);
            this.XmitMonitorControl.Name = "XmitMonitorControl";
            this.XmitMonitorControl.ReadOnly = false;
            this.XmitMonitorControl.Size = new System.Drawing.Size(50, 36);
            this.XmitMonitorControl.SmallSize = new System.Drawing.Size(50, 36);
            this.XmitMonitorControl.TabIndex = 430;
            this.XmitMonitorControl.Tag = "Monchk";
            this.XmitMonitorControl.TheList = null;
            this.XmitMonitorControl.UpdateDisplayFunction = null;
            this.XmitMonitorControl.UpdateRigFunction = null;
            // 
            // AMWidthControl
            // 
            this.AMWidthControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.AMWidthControl.ExpandedSize = new System.Drawing.Size(50, 80);
            this.AMWidthControl.Header = "Width";
            this.AMWidthControl.Location = new System.Drawing.Point(70, 80);
            this.AMWidthControl.Name = "AMWidthControl";
            this.AMWidthControl.ReadOnly = false;
            this.AMWidthControl.Size = new System.Drawing.Size(50, 36);
            this.AMWidthControl.SmallSize = new System.Drawing.Size(50, 36);
            this.AMWidthControl.TabIndex = 210;
            this.AMWidthControl.Tag = "Width";
            this.AMWidthControl.TheList = null;
            this.AMWidthControl.UpdateDisplayFunction = null;
            this.AMWidthControl.UpdateRigFunction = null;
            // 
            // TuningStepControl
            // 
            this.TuningStepControl.BoxStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.TuningStepControl.ExpandedSize = new System.Drawing.Size(60, 80);
            this.TuningStepControl.Header = "Tunestep";
            this.TuningStepControl.Location = new System.Drawing.Point(420, 280);
            this.TuningStepControl.Name = "TuningStepControl";
            this.TuningStepControl.ReadOnly = false;
            this.TuningStepControl.Size = new System.Drawing.Size(60, 36);
            this.TuningStepControl.SmallSize = new System.Drawing.Size(60, 36);
            this.TuningStepControl.TabIndex = 660;
            this.TuningStepControl.Tag = "Tunestep";
            this.TuningStepControl.TheList = null;
            this.TuningStepControl.UpdateDisplayFunction = null;
            this.TuningStepControl.UpdateRigFunction = null;
            // 
            // ic9100filters
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.TuningStepControl);
            this.Controls.Add(this.AMWidthControl);
            this.Controls.Add(this.XmitMonitorControl);
            this.Controls.Add(this.AFCLimitControl);
            this.Controls.Add(this.AFCControl);
            this.Controls.Add(this.FMNotchControl);
            this.Controls.Add(this.CompMeterControl);
            this.Controls.Add(this.ALCControl);
            this.Controls.Add(this.SWRControl);
            this.Controls.Add(this.HFPreampControl);
            this.Controls.Add(this.UHFPreampControl);
            this.Controls.Add(this.AttenuatorControl);
            this.Controls.Add(this.FMAGCControl);
            this.Controls.Add(this.ToneFrequencyControl);
            this.Controls.Add(this.ToneModeControl);
            this.Controls.Add(this.OffsetControl);
            this.Controls.Add(this.FilterTypeControl);
            this.Controls.Add(this.TXBandwidthControl);
            this.Controls.Add(this.MonitorLevelControl);
            this.Controls.Add(this.MonitorControl);
            this.Controls.Add(this.VoiceDelayControl);
            this.Controls.Add(this.ManualNotchControl);
            this.Controls.Add(this.NotchWidthControl);
            this.Controls.Add(this.NotchPositionControl);
            this.Controls.Add(this.SSBNotchControl);
            this.Controls.Add(this.NBWidthControl);
            this.Controls.Add(this.NBDepthControl);
            this.Controls.Add(this.NBLevelControl);
            this.Controls.Add(this.NBControl);
            this.Controls.Add(this.AGCtcControl);
            this.Controls.Add(this.AGCControl);
            this.Controls.Add(this.NRLevelControl);
            this.Controls.Add(this.NRControl);
            this.Controls.Add(this.SSBTransmitBandwidthControl);
            this.Controls.Add(this.CompLevelControl);
            this.Controls.Add(this.CompControl);
            this.Controls.Add(this.MicGainControl);
            this.Controls.Add(this.AntivoxControl);
            this.Controls.Add(this.VoxDelayControl);
            this.Controls.Add(this.VoxGainControl);
            this.Controls.Add(this.OuterPBtControl);
            this.Controls.Add(this.InnerPBTControl);
            this.Controls.Add(this.FirstIFControl);
            this.Controls.Add(this.SidetoneGainControl);
            this.Controls.Add(this.KeyerControl);
            this.Controls.Add(this.CWPitchControl);
            this.Controls.Add(this.KeyerSpeedControl);
            this.Controls.Add(this.XmitPowerControl);
            this.Controls.Add(this.CWSSBWidthControl);
            this.Controls.Add(this.FilterControl);
            this.Controls.Add(this.BkinDelayControl);
            this.Name = "ic9100filters";
            this.Size = new System.Drawing.Size(600, 325);
            this.ControlRemoved += new System.Windows.Forms.ControlEventHandler(this.ic9100filters_ControlRemoved);
            this.ResumeLayout(false);

        }

        #endregion

        private RadioBoxes.NumberBox BkinDelayControl;
        private RadioBoxes.Combo FilterControl;
        private RadioBoxes.Combo CWSSBWidthControl;
        private RadioBoxes.NumberBox XmitPowerControl;
        private RadioBoxes.NumberBox KeyerSpeedControl;
        private RadioBoxes.NumberBox CWPitchControl;
        private RadioBoxes.Combo KeyerControl;
        private RadioBoxes.NumberBox SidetoneGainControl;
        private RadioBoxes.Combo FirstIFControl;
        private RadioBoxes.NumberBox InnerPBTControl;
        private RadioBoxes.NumberBox OuterPBtControl;
        private RadioBoxes.NumberBox VoxGainControl;
        private RadioBoxes.NumberBox VoxDelayControl;
        private RadioBoxes.NumberBox AntivoxControl;
        private RadioBoxes.NumberBox MicGainControl;
        private RadioBoxes.Combo CompControl;
        private RadioBoxes.NumberBox CompLevelControl;
        private RadioBoxes.Combo SSBTransmitBandwidthControl;
        private RadioBoxes.Combo NRControl;
        private RadioBoxes.NumberBox NRLevelControl;
        private RadioBoxes.Combo AGCControl;
        private RadioBoxes.NumberBox AGCtcControl;
        private RadioBoxes.Combo NBControl;
        private RadioBoxes.NumberBox NBLevelControl;
        private RadioBoxes.NumberBox NBDepthControl;
        private RadioBoxes.NumberBox NBWidthControl;
        private RadioBoxes.Combo SSBNotchControl;
        private RadioBoxes.NumberBox NotchPositionControl;
        private RadioBoxes.Combo NotchWidthControl;
        private RadioBoxes.Combo ManualNotchControl;
        private RadioBoxes.Combo VoiceDelayControl;
        private RadioBoxes.Combo MonitorControl;
        private RadioBoxes.NumberBox MonitorLevelControl;
        private RadioBoxes.Combo TXBandwidthControl;
        private RadioBoxes.Combo FilterTypeControl;
        private RadioBoxes.NumberBox OffsetControl;
        private RadioBoxes.Combo ToneModeControl;
        private RadioBoxes.Combo ToneFrequencyControl;
        private RadioBoxes.Combo FMAGCControl;
        private RadioBoxes.Combo AttenuatorControl;
        private RadioBoxes.Combo UHFPreampControl;
        private RadioBoxes.Combo HFPreampControl;
        private RadioBoxes.InfoBox SWRControl;
        private RadioBoxes.InfoBox ALCControl;
        private RadioBoxes.InfoBox CompMeterControl;
        private RadioBoxes.Combo FMNotchControl;
        private RadioBoxes.Combo AFCControl;
        private RadioBoxes.Combo AFCLimitControl;
        private RadioBoxes.Combo XmitMonitorControl;
        private RadioBoxes.Combo AMWidthControl;
        private RadioBoxes.Combo TuningStepControl;
    }
}

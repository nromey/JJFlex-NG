namespace Radios
{
    partial class UserEnteredRemoteRigInfo
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
            this.NicknameLabel = new System.Windows.Forms.Label();
            this.ModelLabel = new System.Windows.Forms.Label();
            this.SerialLabel = new System.Windows.Forms.Label();
            this.NicknameBox = new System.Windows.Forms.TextBox();
            this.ModelBox = new System.Windows.Forms.TextBox();
            this.SerialBox = new System.Windows.Forms.TextBox();
            this.DoneButton = new System.Windows.Forms.Button();
            this.CnclButton = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // NicknameLabel
            // 
            this.NicknameLabel.AutoSize = true;
            this.NicknameLabel.Location = new System.Drawing.Point(22, 20);
            this.NicknameLabel.Name = "NicknameLabel";
            this.NicknameLabel.Size = new System.Drawing.Size(78, 17);
            this.NicknameLabel.TabIndex = 10;
            this.NicknameLabel.Text = "Nickname: ";
            // 
            // ModelLabel
            // 
            this.ModelLabel.AutoSize = true;
            this.ModelLabel.Location = new System.Drawing.Point(46, 50);
            this.ModelLabel.Name = "ModelLabel";
            this.ModelLabel.Size = new System.Drawing.Size(54, 17);
            this.ModelLabel.TabIndex = 20;
            this.ModelLabel.Text = "Model: ";
            // 
            // SerialLabel
            // 
            this.SerialLabel.AutoSize = true;
            this.SerialLabel.Location = new System.Drawing.Point(40, 80);
            this.SerialLabel.Name = "SerialLabel";
            this.SerialLabel.Size = new System.Drawing.Size(60, 17);
            this.SerialLabel.TabIndex = 30;
            this.SerialLabel.Text = "Serial#: ";
            // 
            // NicknameBox
            // 
            this.NicknameBox.AccessibleName = "nickname";
            this.NicknameBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.NicknameBox.Location = new System.Drawing.Point(100, 20);
            this.NicknameBox.Name = "NicknameBox";
            this.NicknameBox.Size = new System.Drawing.Size(200, 22);
            this.NicknameBox.TabIndex = 11;
            // 
            // ModelBox
            // 
            this.ModelBox.AccessibleName = "model";
            this.ModelBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.ModelBox.Location = new System.Drawing.Point(100, 50);
            this.ModelBox.Name = "ModelBox";
            this.ModelBox.Size = new System.Drawing.Size(200, 22);
            this.ModelBox.TabIndex = 21;
            // 
            // SerialBox
            // 
            this.SerialBox.AccessibleName = "serial#";
            this.SerialBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
            this.SerialBox.Location = new System.Drawing.Point(100, 80);
            this.SerialBox.Name = "SerialBox";
            this.SerialBox.Size = new System.Drawing.Size(200, 22);
            this.SerialBox.TabIndex = 31;
            // 
            // DoneButton
            // 
            this.DoneButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.DoneButton.Location = new System.Drawing.Point(100, 120);
            this.DoneButton.Name = "DoneButton";
            this.DoneButton.Size = new System.Drawing.Size(75, 23);
            this.DoneButton.TabIndex = 90;
            this.DoneButton.Text = "Done";
            this.DoneButton.UseVisualStyleBackColor = true;
            this.DoneButton.Click += new System.EventHandler(this.DoneButton_Click);
            // 
            // CnclButton
            // 
            this.CnclButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton;
            this.CnclButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.CnclButton.Location = new System.Drawing.Point(200, 120);
            this.CnclButton.Name = "CnclButton";
            this.CnclButton.Size = new System.Drawing.Size(75, 23);
            this.CnclButton.TabIndex = 98;
            this.CnclButton.Text = "Cancel";
            this.CnclButton.UseVisualStyleBackColor = true;
            this.CnclButton.Click += new System.EventHandler(this.CnclButton_Click);
            // 
            // UserEnteredRemoteRigInfo
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.CnclButton;
            this.ClientSize = new System.Drawing.Size(182, 303);
            this.Controls.Add(this.CnclButton);
            this.Controls.Add(this.DoneButton);
            this.Controls.Add(this.SerialBox);
            this.Controls.Add(this.ModelBox);
            this.Controls.Add(this.NicknameBox);
            this.Controls.Add(this.SerialLabel);
            this.Controls.Add(this.ModelLabel);
            this.Controls.Add(this.NicknameLabel);
            this.Name = "UserEnteredRemoteRigInfo";
            this.Text = "Rig Info";
            this.Activated += new System.EventHandler(this.UserEnteredRemoteRigInfo_Activated);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label NicknameLabel;
        private System.Windows.Forms.Label ModelLabel;
        private System.Windows.Forms.Label SerialLabel;
        private System.Windows.Forms.TextBox NicknameBox;
        private System.Windows.Forms.TextBox ModelBox;
        private System.Windows.Forms.TextBox SerialBox;
        private System.Windows.Forms.Button DoneButton;
        private System.Windows.Forms.Button CnclButton;
    }
}
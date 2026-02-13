<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class PersonalInfo
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.  
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Me.FullNameLabel = New System.Windows.Forms.Label()
        Me.FullNameBox = New System.Windows.Forms.TextBox()
        Me.CallLabel = New System.Windows.Forms.Label()
        Me.HandleLabel = New System.Windows.Forms.Label()
        Me.QTHLabel = New System.Windows.Forms.Label()
        Me.CallSignBox = New System.Windows.Forms.TextBox()
        Me.HandleBox = New System.Windows.Forms.TextBox()
        Me.QTHBox = New System.Windows.Forms.TextBox()
        Me.OKButton = New System.Windows.Forms.Button()
        Me.CnclButton = New System.Windows.Forms.Button()
        Me.DefaultBox = New System.Windows.Forms.CheckBox()
        Me.LogButton = New System.Windows.Forms.Button()
        Me.LicenseLabel = New System.Windows.Forms.Label()
        Me.LicenseList = New System.Windows.Forms.ListBox()
        Me.BRLSizeLabel = New System.Windows.Forms.Label()
        Me.BRLSizeBox = New System.Windows.Forms.TextBox()
        Me.ClusterLoginNameLabel = New System.Windows.Forms.Label()
        Me.ClusterLoginNameBox = New System.Windows.Forms.TextBox()
        Me.AddressLabel = New System.Windows.Forms.Label()
        Me.AddressBox = New System.Windows.Forms.TextBox()
        Me.CallbookSourceLabel = New System.Windows.Forms.Label()
        Me.CallbookSourceCombo = New System.Windows.Forms.ComboBox()
        Me.CallbookUsernameLabel = New System.Windows.Forms.Label()
        Me.CallbookUsernameBox = New System.Windows.Forms.TextBox()
        Me.CallbookPasswordLabel = New System.Windows.Forms.Label()
        Me.CallbookPasswordBox = New System.Windows.Forms.TextBox()
        Me.QrzLogbookEnabledBox = New System.Windows.Forms.CheckBox()
        Me.QrzLogbookApiKeyLabel = New System.Windows.Forms.Label()
        Me.QrzLogbookApiKeyBox = New System.Windows.Forms.TextBox()
        Me.QrzLogbookValidateButton = New System.Windows.Forms.Button()
        Me.GridSquareLabel = New System.Windows.Forms.Label()
        Me.GridSquareBox = New System.Windows.Forms.TextBox()
        Me.RecentQsoLabel = New System.Windows.Forms.Label()
        Me.RecentQsoUpDown = New System.Windows.Forms.NumericUpDown()
        CType(Me.RecentQsoUpDown, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SuspendLayout()
        '
        'FullNameLabel
        '
        Me.FullNameLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.FullNameLabel.AutoSize = True
        Me.FullNameLabel.Location = New System.Drawing.Point(55, 25)
        Me.FullNameLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.FullNameLabel.Name = "FullNameLabel"
        Me.FullNameLabel.Size = New System.Drawing.Size(79, 17)
        Me.FullNameLabel.TabIndex = 0
        Me.FullNameLabel.Text = "Full Name: "
        '
        'FullNameBox
        '
        Me.FullNameBox.AccessibleName = "Full Name"
        Me.FullNameBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.FullNameBox.Location = New System.Drawing.Point(135, 25)
        Me.FullNameBox.Margin = New System.Windows.Forms.Padding(4)
        Me.FullNameBox.Name = "FullNameBox"
        Me.FullNameBox.Size = New System.Drawing.Size(132, 22)
        Me.FullNameBox.TabIndex = 1
        '
        'CallLabel
        '
        Me.CallLabel.AutoSize = True
        Me.CallLabel.Location = New System.Drawing.Point(65, 53)
        Me.CallLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.CallLabel.Name = "CallLabel"
        Me.CallLabel.Size = New System.Drawing.Size(69, 17)
        Me.CallLabel.TabIndex = 10
        Me.CallLabel.Text = "Call sign: "
        '
        'HandleLabel
        '
        Me.HandleLabel.AutoSize = True
        Me.HandleLabel.Location = New System.Drawing.Point(72, 81)
        Me.HandleLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.HandleLabel.Name = "HandleLabel"
        Me.HandleLabel.Size = New System.Drawing.Size(61, 17)
        Me.HandleLabel.TabIndex = 20
        Me.HandleLabel.Text = "Handle: "
        '
        'QTHLabel
        '
        Me.QTHLabel.AutoSize = True
        Me.QTHLabel.Location = New System.Drawing.Point(87, 110)
        Me.QTHLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.QTHLabel.Name = "QTHLabel"
        Me.QTHLabel.Size = New System.Drawing.Size(46, 17)
        Me.QTHLabel.TabIndex = 30
        Me.QTHLabel.Text = "QTH: "
        '
        'CallSignBox
        '
        Me.CallSignBox.AccessibleName = "CallSign"
        Me.CallSignBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.CallSignBox.Location = New System.Drawing.Point(135, 53)
        Me.CallSignBox.Margin = New System.Windows.Forms.Padding(4)
        Me.CallSignBox.Name = "CallSignBox"
        Me.CallSignBox.Size = New System.Drawing.Size(132, 22)
        Me.CallSignBox.TabIndex = 11
        '
        'HandleBox
        '
        Me.HandleBox.AccessibleName = "Handle"
        Me.HandleBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.HandleBox.Location = New System.Drawing.Point(135, 81)
        Me.HandleBox.Margin = New System.Windows.Forms.Padding(4)
        Me.HandleBox.Name = "HandleBox"
        Me.HandleBox.Size = New System.Drawing.Size(132, 22)
        Me.HandleBox.TabIndex = 21
        '
        'QTHBox
        '
        Me.QTHBox.AccessibleName = "QTH"
        Me.QTHBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.QTHBox.Location = New System.Drawing.Point(135, 110)
        Me.QTHBox.Margin = New System.Windows.Forms.Padding(4)
        Me.QTHBox.Name = "QTHBox"
        Me.QTHBox.Size = New System.Drawing.Size(132, 22)
        Me.QTHBox.TabIndex = 31
        '
        'GridSquareLabel
        '
        Me.GridSquareLabel.AutoSize = True
        Me.GridSquareLabel.Location = New System.Drawing.Point(48, 138)
        Me.GridSquareLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.GridSquareLabel.Name = "GridSquareLabel"
        Me.GridSquareLabel.Size = New System.Drawing.Size(85, 17)
        Me.GridSquareLabel.TabIndex = 32
        Me.GridSquareLabel.Text = "Grid Square: "
        '
        'GridSquareBox
        '
        Me.GridSquareBox.AccessibleName = "Grid Square"
        Me.GridSquareBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.GridSquareBox.Location = New System.Drawing.Point(135, 138)
        Me.GridSquareBox.Margin = New System.Windows.Forms.Padding(4)
        Me.GridSquareBox.Name = "GridSquareBox"
        Me.GridSquareBox.Size = New System.Drawing.Size(70, 22)
        Me.GridSquareBox.TabIndex = 33
        '
        'OKButton
        '
        Me.OKButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.OKButton.AutoSize = True
        Me.OKButton.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink
        Me.OKButton.Location = New System.Drawing.Point(0, 593)
        Me.OKButton.Margin = New System.Windows.Forms.Padding(4)
        Me.OKButton.Name = "OKButton"
        Me.OKButton.Size = New System.Drawing.Size(6, 6)
        Me.OKButton.TabIndex = 900
        Me.OKButton.UseVisualStyleBackColor = True
        '
        'CnclButton
        '
        Me.CnclButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.CnclButton.AutoSize = True
        Me.CnclButton.DialogResult = System.Windows.Forms.DialogResult.Cancel
        Me.CnclButton.Location = New System.Drawing.Point(133, 593)
        Me.CnclButton.Margin = New System.Windows.Forms.Padding(4)
        Me.CnclButton.Name = "CnclButton"
        Me.CnclButton.Size = New System.Drawing.Size(100, 28)
        Me.CnclButton.TabIndex = 910
        Me.CnclButton.Text = "Cancel"
        Me.CnclButton.UseVisualStyleBackColor = True
        '
        'DefaultBox
        '
        Me.DefaultBox.AccessibleRole = System.Windows.Forms.AccessibleRole.CheckButton
        Me.DefaultBox.AutoSize = True
        Me.DefaultBox.Location = New System.Drawing.Point(0, 563)
        Me.DefaultBox.Margin = New System.Windows.Forms.Padding(4)
        Me.DefaultBox.Name = "DefaultBox"
        Me.DefaultBox.Size = New System.Drawing.Size(136, 21)
        Me.DefaultBox.TabIndex = 800
        Me.DefaultBox.Text = "Default Operator"
        Me.DefaultBox.UseVisualStyleBackColor = True
        '
        'LogButton
        '
        Me.LogButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.LogButton.Location = New System.Drawing.Point(0, 215)
        Me.LogButton.Margin = New System.Windows.Forms.Padding(4)
        Me.LogButton.Name = "LogButton"
        Me.LogButton.Size = New System.Drawing.Size(100, 28)
        Me.LogButton.TabIndex = 100
        Me.LogButton.Text = "Log Characteristics"
        Me.LogButton.UseVisualStyleBackColor = True
        '
        'LicenseLabel
        '
        Me.LicenseLabel.AutoSize = True
        Me.LicenseLabel.Location = New System.Drawing.Point(32, 166)
        Me.LicenseLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.LicenseLabel.Name = "LicenseLabel"
        Me.LicenseLabel.Size = New System.Drawing.Size(101, 17)
        Me.LicenseLabel.TabIndex = 40
        Me.LicenseLabel.Text = "License class: "
        '
        'LicenseList
        '
        Me.LicenseList.AccessibleName = "license class"
        Me.LicenseList.AccessibleRole = System.Windows.Forms.AccessibleRole.List
        Me.LicenseList.FormattingEnabled = True
        Me.LicenseList.ItemHeight = 16
        Me.LicenseList.Location = New System.Drawing.Point(135, 166)
        Me.LicenseList.Margin = New System.Windows.Forms.Padding(4)
        Me.LicenseList.Name = "LicenseList"
        Me.LicenseList.Size = New System.Drawing.Size(159, 36)
        Me.LicenseList.TabIndex = 41
        '
        'BRLSizeLabel
        '
        Me.BRLSizeLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.BRLSizeLabel.AutoSize = True
        Me.BRLSizeLabel.Location = New System.Drawing.Point(0, 256)
        Me.BRLSizeLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.BRLSizeLabel.Name = "BRLSizeLabel"
        Me.BRLSizeLabel.Size = New System.Drawing.Size(136, 17)
        Me.BRLSizeLabel.TabIndex = 110
        Me.BRLSizeLabel.Text = "Braille Display Size: "
        '
        'BRLSizeBox
        '
        Me.BRLSizeBox.AccessibleName = "braille display size"
        Me.BRLSizeBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.BRLSizeBox.Location = New System.Drawing.Point(135, 256)
        Me.BRLSizeBox.Margin = New System.Windows.Forms.Padding(4)
        Me.BRLSizeBox.Name = "BRLSizeBox"
        Me.BRLSizeBox.Size = New System.Drawing.Size(39, 22)
        Me.BRLSizeBox.TabIndex = 111
        '
        'RecentQsoLabel
        '
        Me.RecentQsoLabel.AutoSize = True
        Me.RecentQsoLabel.Location = New System.Drawing.Point(0, 286)
        Me.RecentQsoLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.RecentQsoLabel.Name = "RecentQsoLabel"
        Me.RecentQsoLabel.Size = New System.Drawing.Size(130, 17)
        Me.RecentQsoLabel.TabIndex = 112
        Me.RecentQsoLabel.Text = "Recent QSOs in grid: "
        '
        'RecentQsoUpDown
        '
        Me.RecentQsoUpDown.AccessibleName = "Number of recent QSOs to display"
        Me.RecentQsoUpDown.AccessibleRole = System.Windows.Forms.AccessibleRole.SpinButton
        Me.RecentQsoUpDown.Location = New System.Drawing.Point(135, 286)
        Me.RecentQsoUpDown.Margin = New System.Windows.Forms.Padding(4)
        Me.RecentQsoUpDown.Maximum = New Decimal(New Integer() {100, 0, 0, 0})
        Me.RecentQsoUpDown.Minimum = New Decimal(New Integer() {5, 0, 0, 0})
        Me.RecentQsoUpDown.Name = "RecentQsoUpDown"
        Me.RecentQsoUpDown.Size = New System.Drawing.Size(60, 22)
        Me.RecentQsoUpDown.TabIndex = 113
        Me.RecentQsoUpDown.Value = New Decimal(New Integer() {20, 0, 0, 0})
        '
        'ClusterLoginNameLabel
        '
        Me.ClusterLoginNameLabel.AutoSize = True
        Me.ClusterLoginNameLabel.Location = New System.Drawing.Point(0, 360)
        Me.ClusterLoginNameLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.ClusterLoginNameLabel.Name = "ClusterLoginNameLabel"
        Me.ClusterLoginNameLabel.Size = New System.Drawing.Size(92, 17)
        Me.ClusterLoginNameLabel.TabIndex = 130
        Me.ClusterLoginNameLabel.Text = "Login Name: "
        '
        'ClusterLoginNameBox
        '
        Me.ClusterLoginNameBox.AccessibleName = "login name"
        Me.ClusterLoginNameBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.ClusterLoginNameBox.Location = New System.Drawing.Point(135, 360)
        Me.ClusterLoginNameBox.Margin = New System.Windows.Forms.Padding(4)
        Me.ClusterLoginNameBox.Name = "ClusterLoginNameBox"
        Me.ClusterLoginNameBox.Size = New System.Drawing.Size(132, 22)
        Me.ClusterLoginNameBox.TabIndex = 131
        '
        'AddressLabel
        '
        Me.AddressLabel.AutoSize = True
        Me.AddressLabel.Location = New System.Drawing.Point(0, 323)
        Me.AddressLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.AddressLabel.Name = "AddressLabel"
        Me.AddressLabel.Size = New System.Drawing.Size(115, 17)
        Me.AddressLabel.TabIndex = 120
        Me.AddressLabel.Text = "Cluster address: "
        '
        'AddressBox
        '
        Me.AddressBox.AccessibleName = "cluster address"
        Me.AddressBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.AddressBox.Location = New System.Drawing.Point(135, 323)
        Me.AddressBox.Margin = New System.Windows.Forms.Padding(4)
        Me.AddressBox.Name = "AddressBox"
        Me.AddressBox.Size = New System.Drawing.Size(132, 22)
        Me.AddressBox.TabIndex = 121
        '
        'CallbookSourceLabel
        '
        Me.CallbookSourceLabel.AutoSize = True
        Me.CallbookSourceLabel.Location = New System.Drawing.Point(0, 396)
        Me.CallbookSourceLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.CallbookSourceLabel.Name = "CallbookSourceLabel"
        Me.CallbookSourceLabel.Size = New System.Drawing.Size(120, 17)
        Me.CallbookSourceLabel.TabIndex = 140
        Me.CallbookSourceLabel.Text = "Callbook lookup: "
        '
        'CallbookSourceCombo
        '
        Me.CallbookSourceCombo.AccessibleName = "Callbook lookup source"
        Me.CallbookSourceCombo.AccessibleRole = System.Windows.Forms.AccessibleRole.ComboBox
        Me.CallbookSourceCombo.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.CallbookSourceCombo.FormattingEnabled = True
        Me.CallbookSourceCombo.Items.AddRange(New Object() {"None", "QRZ", "HamQTH"})
        Me.CallbookSourceCombo.Location = New System.Drawing.Point(135, 396)
        Me.CallbookSourceCombo.Margin = New System.Windows.Forms.Padding(4)
        Me.CallbookSourceCombo.Name = "CallbookSourceCombo"
        Me.CallbookSourceCombo.Size = New System.Drawing.Size(132, 24)
        Me.CallbookSourceCombo.TabIndex = 141
        '
        'CallbookUsernameLabel
        '
        Me.CallbookUsernameLabel.AutoSize = True
        Me.CallbookUsernameLabel.Location = New System.Drawing.Point(0, 428)
        Me.CallbookUsernameLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.CallbookUsernameLabel.Name = "CallbookUsernameLabel"
        Me.CallbookUsernameLabel.Size = New System.Drawing.Size(130, 17)
        Me.CallbookUsernameLabel.TabIndex = 150
        Me.CallbookUsernameLabel.Text = "Callbook username: "
        '
        'CallbookUsernameBox
        '
        Me.CallbookUsernameBox.AccessibleName = "Callbook username"
        Me.CallbookUsernameBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.CallbookUsernameBox.Location = New System.Drawing.Point(135, 428)
        Me.CallbookUsernameBox.Margin = New System.Windows.Forms.Padding(4)
        Me.CallbookUsernameBox.Name = "CallbookUsernameBox"
        Me.CallbookUsernameBox.Size = New System.Drawing.Size(132, 22)
        Me.CallbookUsernameBox.TabIndex = 151
        '
        'CallbookPasswordLabel
        '
        Me.CallbookPasswordLabel.AutoSize = True
        Me.CallbookPasswordLabel.Location = New System.Drawing.Point(0, 458)
        Me.CallbookPasswordLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.CallbookPasswordLabel.Name = "CallbookPasswordLabel"
        Me.CallbookPasswordLabel.Size = New System.Drawing.Size(130, 17)
        Me.CallbookPasswordLabel.TabIndex = 160
        Me.CallbookPasswordLabel.Text = "Callbook password: "
        '
        'CallbookPasswordBox
        '
        Me.CallbookPasswordBox.AccessibleName = "Callbook password"
        Me.CallbookPasswordBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.CallbookPasswordBox.Location = New System.Drawing.Point(135, 458)
        Me.CallbookPasswordBox.Margin = New System.Windows.Forms.Padding(4)
        Me.CallbookPasswordBox.Name = "CallbookPasswordBox"
        Me.CallbookPasswordBox.Size = New System.Drawing.Size(132, 22)
        Me.CallbookPasswordBox.TabIndex = 161
        Me.CallbookPasswordBox.UseSystemPasswordChar = True
        '
        'QrzLogbookEnabledBox
        '
        Me.QrzLogbookEnabledBox.AccessibleRole = System.Windows.Forms.AccessibleRole.CheckButton
        Me.QrzLogbookEnabledBox.AutoSize = True
        Me.QrzLogbookEnabledBox.Location = New System.Drawing.Point(0, 493)
        Me.QrzLogbookEnabledBox.Margin = New System.Windows.Forms.Padding(4)
        Me.QrzLogbookEnabledBox.Name = "QrzLogbookEnabledBox"
        Me.QrzLogbookEnabledBox.Size = New System.Drawing.Size(200, 21)
        Me.QrzLogbookEnabledBox.TabIndex = 170
        Me.QrzLogbookEnabledBox.Text = "Log QSOs to QRZ Logbook"
        Me.QrzLogbookEnabledBox.UseVisualStyleBackColor = True
        '
        'QrzLogbookApiKeyLabel
        '
        Me.QrzLogbookApiKeyLabel.AutoSize = True
        Me.QrzLogbookApiKeyLabel.Location = New System.Drawing.Point(0, 523)
        Me.QrzLogbookApiKeyLabel.Margin = New System.Windows.Forms.Padding(4, 0, 4, 0)
        Me.QrzLogbookApiKeyLabel.Name = "QrzLogbookApiKeyLabel"
        Me.QrzLogbookApiKeyLabel.Size = New System.Drawing.Size(130, 17)
        Me.QrzLogbookApiKeyLabel.TabIndex = 180
        Me.QrzLogbookApiKeyLabel.Text = "QRZ Logbook API key: "
        '
        'QrzLogbookApiKeyBox
        '
        Me.QrzLogbookApiKeyBox.AccessibleName = "QRZ Logbook API key"
        Me.QrzLogbookApiKeyBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.QrzLogbookApiKeyBox.Location = New System.Drawing.Point(135, 523)
        Me.QrzLogbookApiKeyBox.Margin = New System.Windows.Forms.Padding(4)
        Me.QrzLogbookApiKeyBox.Name = "QrzLogbookApiKeyBox"
        Me.QrzLogbookApiKeyBox.Size = New System.Drawing.Size(200, 22)
        Me.QrzLogbookApiKeyBox.TabIndex = 181
        Me.QrzLogbookApiKeyBox.UseSystemPasswordChar = True
        '
        'QrzLogbookValidateButton
        '
        Me.QrzLogbookValidateButton.AccessibleName = "Validate QRZ Logbook key"
        Me.QrzLogbookValidateButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.QrzLogbookValidateButton.Location = New System.Drawing.Point(343, 521)
        Me.QrzLogbookValidateButton.Margin = New System.Windows.Forms.Padding(4)
        Me.QrzLogbookValidateButton.Name = "QrzLogbookValidateButton"
        Me.QrzLogbookValidateButton.Size = New System.Drawing.Size(100, 28)
        Me.QrzLogbookValidateButton.TabIndex = 182
        Me.QrzLogbookValidateButton.Text = "Validate"
        Me.QrzLogbookValidateButton.UseVisualStyleBackColor = True
        '
        'PersonalInfo
        '
        Me.AccessibleRole = System.Windows.Forms.AccessibleRole.Window
        Me.AutoScaleDimensions = New System.Drawing.SizeF(8.0!, 16.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.AutoSize = True
        Me.CancelButton = Me.CnclButton
        Me.ClientSize = New System.Drawing.Size(779, 658)
        Me.Controls.Add(Me.RecentQsoUpDown)
        Me.Controls.Add(Me.RecentQsoLabel)
        Me.Controls.Add(Me.QrzLogbookValidateButton)
        Me.Controls.Add(Me.QrzLogbookApiKeyBox)
        Me.Controls.Add(Me.QrzLogbookApiKeyLabel)
        Me.Controls.Add(Me.QrzLogbookEnabledBox)
        Me.Controls.Add(Me.CallbookPasswordBox)
        Me.Controls.Add(Me.CallbookPasswordLabel)
        Me.Controls.Add(Me.CallbookUsernameBox)
        Me.Controls.Add(Me.CallbookUsernameLabel)
        Me.Controls.Add(Me.CallbookSourceCombo)
        Me.Controls.Add(Me.CallbookSourceLabel)
        Me.Controls.Add(Me.AddressBox)
        Me.Controls.Add(Me.AddressLabel)
        Me.Controls.Add(Me.ClusterLoginNameBox)
        Me.Controls.Add(Me.ClusterLoginNameLabel)
        Me.Controls.Add(Me.BRLSizeBox)
        Me.Controls.Add(Me.BRLSizeLabel)
        Me.Controls.Add(Me.LicenseList)
        Me.Controls.Add(Me.LicenseLabel)
        Me.Controls.Add(Me.LogButton)
        Me.Controls.Add(Me.DefaultBox)
        Me.Controls.Add(Me.CnclButton)
        Me.Controls.Add(Me.OKButton)
        Me.Controls.Add(Me.GridSquareBox)
        Me.Controls.Add(Me.GridSquareLabel)
        Me.Controls.Add(Me.QTHBox)
        Me.Controls.Add(Me.HandleBox)
        Me.Controls.Add(Me.CallSignBox)
        Me.Controls.Add(Me.QTHLabel)
        Me.Controls.Add(Me.HandleLabel)
        Me.Controls.Add(Me.CallLabel)
        Me.Controls.Add(Me.FullNameBox)
        Me.Controls.Add(Me.FullNameLabel)
        Me.Margin = New System.Windows.Forms.Padding(4)
        Me.Name = "PersonalInfo"
        Me.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen
        Me.Text = "Personal Information"
        CType(Me.RecentQsoUpDown, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub
    Friend WithEvents FullNameLabel As System.Windows.Forms.Label
    Friend WithEvents FullNameBox As System.Windows.Forms.TextBox
    Friend WithEvents CallLabel As System.Windows.Forms.Label
    Friend WithEvents HandleLabel As System.Windows.Forms.Label
    Friend WithEvents QTHLabel As System.Windows.Forms.Label
    Friend WithEvents CallSignBox As System.Windows.Forms.TextBox
    Friend WithEvents HandleBox As System.Windows.Forms.TextBox
    Friend WithEvents QTHBox As System.Windows.Forms.TextBox
    Friend WithEvents OKButton As System.Windows.Forms.Button
    Friend WithEvents CnclButton As System.Windows.Forms.Button
    Friend WithEvents DefaultBox As System.Windows.Forms.CheckBox
    Friend WithEvents LogButton As System.Windows.Forms.Button
    Friend WithEvents LicenseLabel As System.Windows.Forms.Label
    Friend WithEvents LicenseList As System.Windows.Forms.ListBox
    Friend WithEvents BRLSizeLabel As System.Windows.Forms.Label
    Friend WithEvents BRLSizeBox As System.Windows.Forms.TextBox
    Friend WithEvents ClusterLoginNameLabel As System.Windows.Forms.Label
    Friend WithEvents ClusterLoginNameBox As System.Windows.Forms.TextBox
    Friend WithEvents AddressLabel As System.Windows.Forms.Label
    Friend WithEvents AddressBox As System.Windows.Forms.TextBox
    Friend WithEvents CallbookSourceLabel As System.Windows.Forms.Label
    Friend WithEvents CallbookSourceCombo As System.Windows.Forms.ComboBox
    Friend WithEvents CallbookUsernameLabel As System.Windows.Forms.Label
    Friend WithEvents CallbookUsernameBox As System.Windows.Forms.TextBox
    Friend WithEvents CallbookPasswordLabel As System.Windows.Forms.Label
    Friend WithEvents CallbookPasswordBox As System.Windows.Forms.TextBox
    Friend WithEvents QrzLogbookEnabledBox As System.Windows.Forms.CheckBox
    Friend WithEvents QrzLogbookApiKeyLabel As System.Windows.Forms.Label
    Friend WithEvents QrzLogbookApiKeyBox As System.Windows.Forms.TextBox
    Friend WithEvents QrzLogbookValidateButton As System.Windows.Forms.Button
    Friend WithEvents GridSquareLabel As System.Windows.Forms.Label
    Friend WithEvents GridSquareBox As System.Windows.Forms.TextBox
    Friend WithEvents RecentQsoLabel As System.Windows.Forms.Label
    Friend WithEvents RecentQsoUpDown As System.Windows.Forms.NumericUpDown
End Class

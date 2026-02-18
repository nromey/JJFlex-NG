<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class RigSelector
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()> _
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
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()
        Me.RadiosLabel = New System.Windows.Forms.Label()
        Me.RadiosBox = New System.Windows.Forms.ListBox()
        Me.RadiosBoxContextMenuStrip = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.ConnectMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.RadiosBoxLowBWMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.RadiosBoxAutoConnectMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.RemoteButton = New System.Windows.Forms.Button()
        Me.LoginButton = New System.Windows.Forms.Button()
        Me.ConnectButton = New System.Windows.Forms.Button()
        Me.CnclButton = New System.Windows.Forms.Button()
        Me.LowBWConnectButton = New System.Windows.Forms.Button()
        Me.AutoConnectTimer = New System.Windows.Forms.Timer(Me.components)
        Me.RadiosBoxContextMenuStrip.SuspendLayout()
        Me.SuspendLayout()
        '
        'RadiosLabel
        '
        Me.RadiosLabel.AutoSize = True
        Me.RadiosLabel.Location = New System.Drawing.Point(375, 20)
        Me.RadiosLabel.Name = "RadiosLabel"
        Me.RadiosLabel.Size = New System.Drawing.Size(52, 17)
        Me.RadiosLabel.TabIndex = 10
        Me.RadiosLabel.Text = "Radios"
        '
        'RadiosBox
        '
        Me.RadiosBox.AccessibleName = "radio list"
        Me.RadiosBox.AccessibleRole = System.Windows.Forms.AccessibleRole.List
        Me.RadiosBox.ContextMenuStrip = Me.RadiosBoxContextMenuStrip
        Me.RadiosBox.FormattingEnabled = True
        Me.RadiosBox.ItemHeight = 16
        Me.RadiosBox.Location = New System.Drawing.Point(0, 40)
        Me.RadiosBox.Name = "RadiosBox"
        Me.RadiosBox.Size = New System.Drawing.Size(750, 100)
        Me.RadiosBox.TabIndex = 11
        '
        'RadiosBoxContextMenuStrip
        '
        Me.RadiosBoxContextMenuStrip.AccessibleRole = System.Windows.Forms.AccessibleRole.MenuPopup
        Me.RadiosBoxContextMenuStrip.ImageScalingSize = New System.Drawing.Size(20, 20)
        Me.RadiosBoxContextMenuStrip.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.ConnectMenuItem, Me.RadiosBoxLowBWMenuItem, Me.RadiosBoxAutoConnectMenuItem})
        Me.RadiosBoxContextMenuStrip.Name = "RadiosBoxContextMenuStrip"
        Me.RadiosBoxContextMenuStrip.Size = New System.Drawing.Size(181, 76)
        '
        'ConnectMenuItem
        '
        Me.ConnectMenuItem.AccessibleRole = System.Windows.Forms.AccessibleRole.MenuItem
        Me.ConnectMenuItem.Name = "ConnectMenuItem"
        Me.ConnectMenuItem.Size = New System.Drawing.Size(180, 24)
        Me.ConnectMenuItem.Text = "Connect"
        '
        'RadiosBoxLowBWMenuItem
        '
        Me.RadiosBoxLowBWMenuItem.AccessibleRole = System.Windows.Forms.AccessibleRole.MenuItem
        Me.RadiosBoxLowBWMenuItem.Name = "RadiosBoxLowBWMenuItem"
        Me.RadiosBoxLowBWMenuItem.Size = New System.Drawing.Size(180, 24)
        Me.RadiosBoxLowBWMenuItem.Text = "Low bandwidth"
        '
        'RadiosBoxAutoConnectMenuItem
        '
        Me.RadiosBoxAutoConnectMenuItem.AccessibleRole = System.Windows.Forms.AccessibleRole.MenuItem
        Me.RadiosBoxAutoConnectMenuItem.Name = "RadiosBoxAutoConnectMenuItem"
        Me.RadiosBoxAutoConnectMenuItem.Size = New System.Drawing.Size(180, 24)
        Me.RadiosBoxAutoConnectMenuItem.Text = "AutoLogin"
        '
        'RemoteButton
        '
        Me.RemoteButton.AccessibleName = "Remote radios"
        Me.RemoteButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.RemoteButton.Location = New System.Drawing.Point(50, 200)
        Me.RemoteButton.Name = "RemoteButton"
        Me.RemoteButton.Size = New System.Drawing.Size(75, 23)
        Me.RemoteButton.TabIndex = 20
        Me.RemoteButton.Text = "Remote"
        Me.RemoteButton.UseVisualStyleBackColor = True
        '
        'LoginButton
        '
        Me.LoginButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.LoginButton.Location = New System.Drawing.Point(200, 200)
        Me.LoginButton.Name = "LoginButton"
        Me.LoginButton.Size = New System.Drawing.Size(75, 23)
        Me.LoginButton.TabIndex = 22
        Me.LoginButton.Text = "Login"
        Me.LoginButton.UseVisualStyleBackColor = True
        '
        'ConnectButton
        '
        Me.ConnectButton.AccessibleName = "Connect to radio"
        Me.ConnectButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.ConnectButton.Location = New System.Drawing.Point(400, 200)
        Me.ConnectButton.Name = "ConnectButton"
        Me.ConnectButton.Size = New System.Drawing.Size(75, 23)
        Me.ConnectButton.TabIndex = 24
        Me.ConnectButton.Text = "Connect"
        Me.ConnectButton.UseVisualStyleBackColor = True
        '
        'CnclButton
        '
        Me.CnclButton.AccessibleName = "Cancel"
        Me.CnclButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.CnclButton.DialogResult = System.Windows.Forms.DialogResult.Cancel
        Me.CnclButton.Location = New System.Drawing.Point(600, 200)
        Me.CnclButton.Name = "CnclButton"
        Me.CnclButton.Size = New System.Drawing.Size(75, 23)
        Me.CnclButton.TabIndex = 30
        Me.CnclButton.Text = "Cancel"
        Me.CnclButton.UseVisualStyleBackColor = True
        '
        'LowBWConnectButton
        '
        Me.LowBWConnectButton.AccessibleName = "low bandwidth connect"
        Me.LowBWConnectButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.LowBWConnectButton.Location = New System.Drawing.Point(500, 200)
        Me.LowBWConnectButton.Name = "LowBWConnectButton"
        Me.LowBWConnectButton.Size = New System.Drawing.Size(75, 23)
        Me.LowBWConnectButton.TabIndex = 25
        Me.LowBWConnectButton.Tag = ""
        Me.LowBWConnectButton.Text = "LowBW"
        Me.LowBWConnectButton.UseVisualStyleBackColor = True
        '
        'AutoConnectTimer
        '
        '
        'RigSelector
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(8.0!, 16.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.CancelButton = Me.CnclButton
        Me.ClientSize = New System.Drawing.Size(782, 253)
        Me.Controls.Add(Me.LowBWConnectButton)
        Me.Controls.Add(Me.CnclButton)
        Me.Controls.Add(Me.ConnectButton)
        Me.Controls.Add(Me.LoginButton)
        Me.Controls.Add(Me.RemoteButton)
        Me.Controls.Add(Me.RadiosBox)
        Me.Controls.Add(Me.RadiosLabel)
        Me.Name = "RigSelector"
        Me.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen
        Me.Text = "Rig Selector"
        Me.RadiosBoxContextMenuStrip.ResumeLayout(False)
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    Friend WithEvents RadiosLabel As Label
    Friend WithEvents RadiosBox As ListBox
    Friend WithEvents RemoteButton As Button
    Friend WithEvents LoginButton As Button
    Friend WithEvents ConnectButton As Button
    Friend WithEvents CnclButton As Button
    Friend WithEvents LowBWConnectButton As Button
    Friend WithEvents RadiosBoxContextMenuStrip As ContextMenuStrip
    Friend WithEvents ConnectMenuItem As ToolStripMenuItem
    Friend WithEvents RadiosBoxLowBWMenuItem As ToolStripMenuItem
    Friend WithEvents RadiosBoxAutoConnectMenuItem As ToolStripMenuItem
    Friend WithEvents AutoConnectTimer As Timer
End Class

<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class DefineCommands
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

    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()
        Me.ScopeTabControl = New System.Windows.Forms.TabControl()
        Me.GlobalTab = New System.Windows.Forms.TabPage()
        Me.RadioTab = New System.Windows.Forms.TabPage()
        Me.LoggingTab = New System.Windows.Forms.TabPage()
        Me.CommandsListView = New System.Windows.Forms.ListView()
        Me.KeyColumn = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.CommandColumn = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.GroupColumn = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.ValueBox = New System.Windows.Forms.TextBox()
        Me.OKButton = New System.Windows.Forms.Button()
        Me.CnclButton = New System.Windows.Forms.Button()
        Me.PressKeyLabel = New System.Windows.Forms.Label()
        Me.ConflictLabel = New System.Windows.Forms.Label()
        Me.ResetButton = New System.Windows.Forms.Button()
        Me.ResetAllButton = New System.Windows.Forms.Button()
        Me.ScopeTabControl.SuspendLayout()
        Me.SuspendLayout()
        '
        'ScopeTabControl
        '
        Me.ScopeTabControl.AccessibleName = "Hotkey scope tabs"
        Me.ScopeTabControl.AccessibleRole = System.Windows.Forms.AccessibleRole.PageTabList
        Me.ScopeTabControl.Controls.Add(Me.GlobalTab)
        Me.ScopeTabControl.Controls.Add(Me.RadioTab)
        Me.ScopeTabControl.Controls.Add(Me.LoggingTab)
        Me.ScopeTabControl.Location = New System.Drawing.Point(8, 8)
        Me.ScopeTabControl.Name = "ScopeTabControl"
        Me.ScopeTabControl.SelectedIndex = 0
        Me.ScopeTabControl.Size = New System.Drawing.Size(520, 28)
        Me.ScopeTabControl.TabIndex = 1
        '
        'GlobalTab
        '
        Me.GlobalTab.AccessibleName = "Global hotkeys"
        Me.GlobalTab.Text = "Global"
        '
        'RadioTab
        '
        Me.RadioTab.AccessibleName = "Radio hotkeys"
        Me.RadioTab.Text = "Radio"
        '
        'LoggingTab
        '
        Me.LoggingTab.AccessibleName = "Logging hotkeys"
        Me.LoggingTab.Text = "Logging"
        '
        'CommandsListView
        '
        Me.CommandsListView.AccessibleName = "Hotkey commands"
        Me.CommandsListView.AccessibleRole = System.Windows.Forms.AccessibleRole.List
        Me.CommandsListView.Columns.AddRange(New System.Windows.Forms.ColumnHeader() {Me.KeyColumn, Me.CommandColumn, Me.GroupColumn})
        Me.CommandsListView.FullRowSelect = True
        Me.CommandsListView.GridLines = True
        Me.CommandsListView.HideSelection = False
        Me.CommandsListView.Location = New System.Drawing.Point(8, 40)
        Me.CommandsListView.MultiSelect = False
        Me.CommandsListView.Name = "CommandsListView"
        Me.CommandsListView.Size = New System.Drawing.Size(520, 300)
        Me.CommandsListView.TabIndex = 10
        Me.CommandsListView.UseCompatibleStateImageBehavior = False
        Me.CommandsListView.View = System.Windows.Forms.View.Details
        '
        'KeyColumn
        '
        Me.KeyColumn.Text = "Key"
        Me.KeyColumn.Width = 140
        '
        'CommandColumn
        '
        Me.CommandColumn.Text = "Command"
        Me.CommandColumn.Width = 260
        '
        'GroupColumn
        '
        Me.GroupColumn.Text = "Group"
        Me.GroupColumn.Width = 100
        '
        'ValueBox
        '
        Me.ValueBox.AccessibleName = "Press key to change binding"
        Me.ValueBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.ValueBox.Location = New System.Drawing.Point(8, 350)
        Me.ValueBox.Name = "ValueBox"
        Me.ValueBox.Size = New System.Drawing.Size(200, 20)
        Me.ValueBox.TabIndex = 20
        Me.ValueBox.ReadOnly = True
        '
        'PressKeyLabel
        '
        Me.PressKeyLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.PressKeyLabel.AutoSize = True
        Me.PressKeyLabel.Location = New System.Drawing.Point(215, 353)
        Me.PressKeyLabel.Name = "PressKeyLabel"
        Me.PressKeyLabel.Size = New System.Drawing.Size(0, 13)
        Me.PressKeyLabel.TabIndex = 21
        '
        'ConflictLabel
        '
        Me.ConflictLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.ConflictLabel.AutoSize = True
        Me.ConflictLabel.ForeColor = System.Drawing.Color.Red
        Me.ConflictLabel.Location = New System.Drawing.Point(8, 378)
        Me.ConflictLabel.Name = "ConflictLabel"
        Me.ConflictLabel.Size = New System.Drawing.Size(0, 13)
        Me.ConflictLabel.TabIndex = 22
        '
        'ResetButton
        '
        Me.ResetButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.ResetButton.Location = New System.Drawing.Point(290, 400)
        Me.ResetButton.Name = "ResetButton"
        Me.ResetButton.Size = New System.Drawing.Size(110, 23)
        Me.ResetButton.TabIndex = 30
        Me.ResetButton.Text = "Reset Selected"
        Me.ResetButton.UseVisualStyleBackColor = True
        '
        'ResetAllButton
        '
        Me.ResetAllButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.ResetAllButton.Location = New System.Drawing.Point(410, 400)
        Me.ResetAllButton.Name = "ResetAllButton"
        Me.ResetAllButton.Size = New System.Drawing.Size(110, 23)
        Me.ResetAllButton.TabIndex = 31
        Me.ResetAllButton.Text = "Reset All in Tab"
        Me.ResetAllButton.UseVisualStyleBackColor = True
        '
        'OKButton
        '
        Me.OKButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.OKButton.Location = New System.Drawing.Point(8, 435)
        Me.OKButton.Name = "OKButton"
        Me.OKButton.Size = New System.Drawing.Size(75, 23)
        Me.OKButton.TabIndex = 90
        Me.OKButton.Text = "OK"
        Me.OKButton.UseVisualStyleBackColor = True
        '
        'CnclButton
        '
        Me.CnclButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.CnclButton.DialogResult = System.Windows.Forms.DialogResult.Cancel
        Me.CnclButton.Location = New System.Drawing.Point(100, 435)
        Me.CnclButton.Name = "CnclButton"
        Me.CnclButton.Size = New System.Drawing.Size(75, 23)
        Me.CnclButton.TabIndex = 91
        Me.CnclButton.Text = "Cancel"
        Me.CnclButton.UseVisualStyleBackColor = True
        '
        'DefineCommands
        '
        Me.AcceptButton = Me.OKButton
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.CancelButton = Me.CnclButton
        Me.ClientSize = New System.Drawing.Size(540, 470)
        Me.Controls.Add(Me.ScopeTabControl)
        Me.Controls.Add(Me.CommandsListView)
        Me.Controls.Add(Me.ValueBox)
        Me.Controls.Add(Me.PressKeyLabel)
        Me.Controls.Add(Me.ConflictLabel)
        Me.Controls.Add(Me.ResetButton)
        Me.Controls.Add(Me.ResetAllButton)
        Me.Controls.Add(Me.OKButton)
        Me.Controls.Add(Me.CnclButton)
        Me.Name = "DefineCommands"
        Me.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen
        Me.Text = "Define Keys"
        Me.ScopeTabControl.ResumeLayout(False)
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub
    Friend WithEvents ScopeTabControl As System.Windows.Forms.TabControl
    Friend WithEvents GlobalTab As System.Windows.Forms.TabPage
    Friend WithEvents RadioTab As System.Windows.Forms.TabPage
    Friend WithEvents LoggingTab As System.Windows.Forms.TabPage
    Friend WithEvents CommandsListView As System.Windows.Forms.ListView
    Friend WithEvents KeyColumn As System.Windows.Forms.ColumnHeader
    Friend WithEvents CommandColumn As System.Windows.Forms.ColumnHeader
    Friend WithEvents GroupColumn As System.Windows.Forms.ColumnHeader
    Friend WithEvents ValueBox As System.Windows.Forms.TextBox
    Friend WithEvents OKButton As System.Windows.Forms.Button
    Friend WithEvents CnclButton As System.Windows.Forms.Button
    Friend WithEvents PressKeyLabel As System.Windows.Forms.Label
    Friend WithEvents ConflictLabel As System.Windows.Forms.Label
    Friend WithEvents ResetButton As System.Windows.Forms.Button
    Friend WithEvents ResetAllButton As System.Windows.Forms.Button
End Class

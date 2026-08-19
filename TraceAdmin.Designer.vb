<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class TraceAdmin
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
        ' Sprint 30 Track D: the Tracing tab is gone — its job moved to
        ' Settings > Diagnostics, and with one tab left the TabControl was a
        ' tab strip around nothing. The browser is the whole window now, which
        ' is also why the window is called Saved Diagnostic Logs.
        Me.BrowserPanel = New System.Windows.Forms.Panel()

        ' Browser controls
        Me.FilterFromLabel = New System.Windows.Forms.Label()
        Me.FilterFromDate = New System.Windows.Forms.DateTimePicker()
        Me.FilterToLabel = New System.Windows.Forms.Label()
        Me.FilterToDate = New System.Windows.Forms.DateTimePicker()
        Me.FilterOutcomeLabel = New System.Windows.Forms.Label()
        Me.FilterOutcomeCombo = New System.Windows.Forms.ComboBox()
        Me.FilterSearchLabel = New System.Windows.Forms.Label()
        Me.FilterSearchBox = New System.Windows.Forms.TextBox()
        Me.FilterStatusLabel = New System.Windows.Forms.Label()
        Me.ArchiveListView = New System.Windows.Forms.ListView()
        Me.SelectionDetailLabel = New System.Windows.Forms.Label()
        Me.SelectionDetailBox = New System.Windows.Forms.TextBox()
        Me.ViewTraceButton = New System.Windows.Forms.Button()
        Me.CopyPathButton = New System.Windows.Forms.Button()
        Me.ExportSelectedButton = New System.Windows.Forms.Button()
        Me.DeleteSelectedButton = New System.Windows.Forms.Button()
        Me.FooterLabel = New System.Windows.Forms.Label()
        Me.AutoPruneInfoLabel = New System.Windows.Forms.Label()
        Me.PruneRetentionLabel = New System.Windows.Forms.Label()
        Me.PruneRetentionUpDown = New System.Windows.Forms.NumericUpDown()
        Me.PruneNowButton = New System.Windows.Forms.Button()
        Me.SearchDebounceTimer = New System.Windows.Forms.Timer(Me.components)

        ' Form-level
        Me.CnclButton = New System.Windows.Forms.Button()

        Me.BrowserPanel.SuspendLayout()
        CType(Me.PruneRetentionUpDown, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SuspendLayout()
        '
        'BrowserPanel
        '
        Me.BrowserPanel.AccessibleName = "Saved diagnostic logs"
        Me.BrowserPanel.Location = New System.Drawing.Point(8, 8)
        Me.BrowserPanel.Name = "BrowserPanel"
        Me.BrowserPanel.Size = New System.Drawing.Size(884, 540)
        Me.BrowserPanel.TabIndex = 0
        Me.BrowserPanel.Controls.Add(Me.FilterFromLabel)
        Me.BrowserPanel.Controls.Add(Me.FilterFromDate)
        Me.BrowserPanel.Controls.Add(Me.FilterToLabel)
        Me.BrowserPanel.Controls.Add(Me.FilterToDate)
        Me.BrowserPanel.Controls.Add(Me.FilterOutcomeLabel)
        Me.BrowserPanel.Controls.Add(Me.FilterOutcomeCombo)
        Me.BrowserPanel.Controls.Add(Me.FilterSearchLabel)
        Me.BrowserPanel.Controls.Add(Me.FilterSearchBox)
        Me.BrowserPanel.Controls.Add(Me.FilterStatusLabel)
        Me.BrowserPanel.Controls.Add(Me.ArchiveListView)
        Me.BrowserPanel.Controls.Add(Me.SelectionDetailLabel)
        Me.BrowserPanel.Controls.Add(Me.SelectionDetailBox)
        Me.BrowserPanel.Controls.Add(Me.ViewTraceButton)
        Me.BrowserPanel.Controls.Add(Me.CopyPathButton)
        Me.BrowserPanel.Controls.Add(Me.ExportSelectedButton)
        Me.BrowserPanel.Controls.Add(Me.DeleteSelectedButton)
        Me.BrowserPanel.Controls.Add(Me.FooterLabel)
        Me.BrowserPanel.Controls.Add(Me.AutoPruneInfoLabel)
        Me.BrowserPanel.Controls.Add(Me.PruneRetentionLabel)
        Me.BrowserPanel.Controls.Add(Me.PruneRetentionUpDown)
        Me.BrowserPanel.Controls.Add(Me.PruneNowButton)
        '
        '== Saved diagnostic log controls ==
        '
        'FilterFromLabel
        '
        Me.FilterFromLabel.AutoSize = True
        Me.FilterFromLabel.Location = New System.Drawing.Point(16, 16)
        Me.FilterFromLabel.Name = "FilterFromLabel"
        Me.FilterFromLabel.Size = New System.Drawing.Size(40, 13)
        Me.FilterFromLabel.TabIndex = 100
        Me.FilterFromLabel.Text = "From:"
        '
        'FilterFromDate
        '
        Me.FilterFromDate.AccessibleName = "filter from date"
        Me.FilterFromDate.Format = System.Windows.Forms.DateTimePickerFormat.Short
        Me.FilterFromDate.Location = New System.Drawing.Point(60, 12)
        Me.FilterFromDate.Name = "FilterFromDate"
        Me.FilterFromDate.Size = New System.Drawing.Size(120, 20)
        Me.FilterFromDate.TabIndex = 101
        Me.FilterFromDate.ShowCheckBox = True
        '
        'FilterToLabel
        '
        Me.FilterToLabel.AutoSize = True
        Me.FilterToLabel.Location = New System.Drawing.Point(196, 16)
        Me.FilterToLabel.Name = "FilterToLabel"
        Me.FilterToLabel.Size = New System.Drawing.Size(25, 13)
        Me.FilterToLabel.TabIndex = 102
        Me.FilterToLabel.Text = "To:"
        '
        'FilterToDate
        '
        Me.FilterToDate.AccessibleName = "filter to date"
        Me.FilterToDate.Format = System.Windows.Forms.DateTimePickerFormat.Short
        Me.FilterToDate.Location = New System.Drawing.Point(228, 12)
        Me.FilterToDate.Name = "FilterToDate"
        Me.FilterToDate.Size = New System.Drawing.Size(120, 20)
        Me.FilterToDate.TabIndex = 103
        Me.FilterToDate.ShowCheckBox = True
        '
        'FilterOutcomeLabel
        '
        Me.FilterOutcomeLabel.AutoSize = True
        Me.FilterOutcomeLabel.Location = New System.Drawing.Point(364, 16)
        Me.FilterOutcomeLabel.Name = "FilterOutcomeLabel"
        Me.FilterOutcomeLabel.Size = New System.Drawing.Size(60, 13)
        Me.FilterOutcomeLabel.TabIndex = 104
        Me.FilterOutcomeLabel.Text = "Outcome:"
        '
        'FilterOutcomeCombo
        '
        Me.FilterOutcomeCombo.AccessibleName = "filter outcome"
        Me.FilterOutcomeCombo.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.FilterOutcomeCombo.Location = New System.Drawing.Point(430, 12)
        Me.FilterOutcomeCombo.Name = "FilterOutcomeCombo"
        Me.FilterOutcomeCombo.Size = New System.Drawing.Size(160, 21)
        Me.FilterOutcomeCombo.TabIndex = 105
        '
        'FilterSearchLabel
        '
        Me.FilterSearchLabel.AutoSize = True
        Me.FilterSearchLabel.Location = New System.Drawing.Point(606, 16)
        Me.FilterSearchLabel.Name = "FilterSearchLabel"
        Me.FilterSearchLabel.Size = New System.Drawing.Size(45, 13)
        Me.FilterSearchLabel.TabIndex = 106
        Me.FilterSearchLabel.Text = "Search:"
        '
        'FilterSearchBox
        '
        Me.FilterSearchBox.AccessibleName = "search target or outcome reason"
        Me.FilterSearchBox.Location = New System.Drawing.Point(660, 12)
        Me.FilterSearchBox.Name = "FilterSearchBox"
        Me.FilterSearchBox.Size = New System.Drawing.Size(200, 20)
        Me.FilterSearchBox.TabIndex = 107
        '
        'FilterStatusLabel
        '
        Me.FilterStatusLabel.AccessibleName = "filter status"
        Me.FilterStatusLabel.AutoSize = True
        Me.FilterStatusLabel.Location = New System.Drawing.Point(16, 44)
        Me.FilterStatusLabel.Name = "FilterStatusLabel"
        Me.FilterStatusLabel.Size = New System.Drawing.Size(120, 13)
        Me.FilterStatusLabel.TabIndex = 108
        Me.FilterStatusLabel.Text = "0 total, 0 shown"
        '
        'ArchiveListView
        '
        Me.ArchiveListView.AccessibleName = "Archive entries"
        Me.ArchiveListView.AccessibleRole = System.Windows.Forms.AccessibleRole.List
        Me.ArchiveListView.FullRowSelect = True
        Me.ArchiveListView.GridLines = True
        Me.ArchiveListView.HideSelection = False
        Me.ArchiveListView.Location = New System.Drawing.Point(16, 64)
        Me.ArchiveListView.MultiSelect = True
        Me.ArchiveListView.Name = "ArchiveListView"
        Me.ArchiveListView.Size = New System.Drawing.Size(844, 220)
        Me.ArchiveListView.TabIndex = 109
        Me.ArchiveListView.UseCompatibleStateImageBehavior = False
        Me.ArchiveListView.View = System.Windows.Forms.View.Details
        '
        'SelectionDetailLabel
        '
        Me.SelectionDetailLabel.AutoSize = True
        Me.SelectionDetailLabel.Location = New System.Drawing.Point(16, 292)
        Me.SelectionDetailLabel.Name = "SelectionDetailLabel"
        Me.SelectionDetailLabel.Size = New System.Drawing.Size(40, 13)
        Me.SelectionDetailLabel.TabIndex = 110
        Me.SelectionDetailLabel.Text = "Details:"
        '
        'SelectionDetailBox
        '
        Me.SelectionDetailBox.AccessibleName = "selection details"
        Me.SelectionDetailBox.Location = New System.Drawing.Point(16, 308)
        Me.SelectionDetailBox.Multiline = True
        Me.SelectionDetailBox.Name = "SelectionDetailBox"
        Me.SelectionDetailBox.ReadOnly = True
        Me.SelectionDetailBox.ScrollBars = System.Windows.Forms.ScrollBars.Vertical
        Me.SelectionDetailBox.Size = New System.Drawing.Size(844, 80)
        Me.SelectionDetailBox.TabIndex = 111
        '
        'ViewTraceButton
        '
        Me.ViewTraceButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.ViewTraceButton.Location = New System.Drawing.Point(16, 400)
        Me.ViewTraceButton.Name = "ViewTraceButton"
        Me.ViewTraceButton.Size = New System.Drawing.Size(110, 25)
        Me.ViewTraceButton.TabIndex = 112
        Me.ViewTraceButton.Text = "View Trace"
        Me.ViewTraceButton.UseVisualStyleBackColor = True
        '
        'CopyPathButton
        '
        Me.CopyPathButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.CopyPathButton.Location = New System.Drawing.Point(132, 400)
        Me.CopyPathButton.Name = "CopyPathButton"
        Me.CopyPathButton.Size = New System.Drawing.Size(110, 25)
        Me.CopyPathButton.TabIndex = 113
        Me.CopyPathButton.Text = "Copy Path"
        Me.CopyPathButton.UseVisualStyleBackColor = True
        '
        'ExportSelectedButton
        '
        Me.ExportSelectedButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.ExportSelectedButton.Location = New System.Drawing.Point(248, 400)
        Me.ExportSelectedButton.Name = "ExportSelectedButton"
        Me.ExportSelectedButton.Size = New System.Drawing.Size(140, 25)
        Me.ExportSelectedButton.TabIndex = 114
        Me.ExportSelectedButton.Text = "Export Selected..."
        Me.ExportSelectedButton.UseVisualStyleBackColor = True
        '
        'DeleteSelectedButton
        '
        Me.DeleteSelectedButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.DeleteSelectedButton.Location = New System.Drawing.Point(394, 400)
        Me.DeleteSelectedButton.Name = "DeleteSelectedButton"
        Me.DeleteSelectedButton.Size = New System.Drawing.Size(140, 25)
        Me.DeleteSelectedButton.TabIndex = 115
        Me.DeleteSelectedButton.Text = "Delete Selected..."
        Me.DeleteSelectedButton.UseVisualStyleBackColor = True
        '
        'FooterLabel
        '
        Me.FooterLabel.AccessibleName = "archive total"
        Me.FooterLabel.AutoSize = True
        Me.FooterLabel.Location = New System.Drawing.Point(16, 440)
        Me.FooterLabel.Name = "FooterLabel"
        Me.FooterLabel.Size = New System.Drawing.Size(200, 13)
        Me.FooterLabel.TabIndex = 116
        Me.FooterLabel.Text = "Archive total: 0 bytes across 0 entries"
        '
        'AutoPruneInfoLabel
        '
        Me.AutoPruneInfoLabel.AutoSize = True
        Me.AutoPruneInfoLabel.Location = New System.Drawing.Point(16, 460)
        Me.AutoPruneInfoLabel.Name = "AutoPruneInfoLabel"
        Me.AutoPruneInfoLabel.Size = New System.Drawing.Size(360, 13)
        Me.AutoPruneInfoLabel.TabIndex = 117
        Me.AutoPruneInfoLabel.Text = "Auto-prune: entries older than 30 days are removed automatically."
        '
        'PruneRetentionLabel
        '
        Me.PruneRetentionLabel.AutoSize = True
        Me.PruneRetentionLabel.Location = New System.Drawing.Point(16, 484)
        Me.PruneRetentionLabel.Name = "PruneRetentionLabel"
        Me.PruneRetentionLabel.Size = New System.Drawing.Size(125, 13)
        Me.PruneRetentionLabel.TabIndex = 118
        Me.PruneRetentionLabel.Text = "Prune entries older than:"
        '
        'PruneRetentionUpDown
        '
        Me.PruneRetentionUpDown.AccessibleName = "prune retention days"
        Me.PruneRetentionUpDown.Location = New System.Drawing.Point(150, 482)
        Me.PruneRetentionUpDown.Maximum = New Decimal(New Integer() {365, 0, 0, 0})
        Me.PruneRetentionUpDown.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        Me.PruneRetentionUpDown.Name = "PruneRetentionUpDown"
        Me.PruneRetentionUpDown.Size = New System.Drawing.Size(60, 20)
        Me.PruneRetentionUpDown.TabIndex = 119
        Me.PruneRetentionUpDown.Value = New Decimal(New Integer() {30, 0, 0, 0})
        '
        'PruneNowButton
        '
        Me.PruneNowButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.PruneNowButton.Location = New System.Drawing.Point(216, 480)
        Me.PruneNowButton.Name = "PruneNowButton"
        Me.PruneNowButton.Size = New System.Drawing.Size(120, 25)
        Me.PruneNowButton.TabIndex = 120
        Me.PruneNowButton.Text = "Prune Now..."
        Me.PruneNowButton.UseVisualStyleBackColor = True
        '
        'SearchDebounceTimer
        '
        Me.SearchDebounceTimer.Interval = 300
        '
        '== Form-level controls ==
        '
        'CnclButton
        '
        Me.CnclButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.CnclButton.DialogResult = System.Windows.Forms.DialogResult.Cancel
        Me.CnclButton.Location = New System.Drawing.Point(810, 560)
        Me.CnclButton.Name = "CnclButton"
        Me.CnclButton.Size = New System.Drawing.Size(80, 25)
        Me.CnclButton.TabIndex = 201
        Me.CnclButton.AccessibleName = "Close"
        Me.CnclButton.Text = "Close"
        Me.CnclButton.UseVisualStyleBackColor = True
        '
        'TraceAdmin
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.CancelButton = Me.CnclButton
        Me.ClientSize = New System.Drawing.Size(900, 595)
        Me.Controls.Add(Me.BrowserPanel)
        Me.Controls.Add(Me.CnclButton)
        Me.MinimumSize = New System.Drawing.Size(700, 500)
        Me.Name = "TraceAdmin"
        ' The type keeps its name (every call site and partial stays put); the
        ' WINDOW is what the operator meets, and the operator is looking for
        ' saved logs, not for "tracing".
        Me.Text = "Saved Diagnostic Logs"
        Me.AccessibleName = "Saved Diagnostic Logs"
        Me.BrowserPanel.ResumeLayout(False)
        Me.BrowserPanel.PerformLayout()
        CType(Me.PruneRetentionUpDown, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    ' The whole body of the window
    Friend WithEvents BrowserPanel As System.Windows.Forms.Panel

    ' Saved diagnostic logs
    Friend WithEvents FilterFromLabel As System.Windows.Forms.Label
    Friend WithEvents FilterFromDate As System.Windows.Forms.DateTimePicker
    Friend WithEvents FilterToLabel As System.Windows.Forms.Label
    Friend WithEvents FilterToDate As System.Windows.Forms.DateTimePicker
    Friend WithEvents FilterOutcomeLabel As System.Windows.Forms.Label
    Friend WithEvents FilterOutcomeCombo As System.Windows.Forms.ComboBox
    Friend WithEvents FilterSearchLabel As System.Windows.Forms.Label
    Friend WithEvents FilterSearchBox As System.Windows.Forms.TextBox
    Friend WithEvents FilterStatusLabel As System.Windows.Forms.Label
    Friend WithEvents ArchiveListView As System.Windows.Forms.ListView
    Friend WithEvents SelectionDetailLabel As System.Windows.Forms.Label
    Friend WithEvents SelectionDetailBox As System.Windows.Forms.TextBox
    Friend WithEvents ViewTraceButton As System.Windows.Forms.Button
    Friend WithEvents CopyPathButton As System.Windows.Forms.Button
    Friend WithEvents ExportSelectedButton As System.Windows.Forms.Button
    Friend WithEvents DeleteSelectedButton As System.Windows.Forms.Button
    Friend WithEvents FooterLabel As System.Windows.Forms.Label
    Friend WithEvents AutoPruneInfoLabel As System.Windows.Forms.Label
    Friend WithEvents PruneRetentionLabel As System.Windows.Forms.Label
    Friend WithEvents PruneRetentionUpDown As System.Windows.Forms.NumericUpDown
    Friend WithEvents PruneNowButton As System.Windows.Forms.Button
    Friend WithEvents SearchDebounceTimer As System.Windows.Forms.Timer

    ' Form-level
    ' ToggleButton removed Sprint 31 (#103) with the retired trace dialog. It
    ' was the old Tracing tab's start/stop button and had already lost its
    ' InitializeComponent line when that tab went, so it was a WithEvents field
    ' that was permanently Nothing — a control that looked present to anyone
    ' reading this file and did not exist at runtime.
    Friend WithEvents CnclButton As System.Windows.Forms.Button
End Class

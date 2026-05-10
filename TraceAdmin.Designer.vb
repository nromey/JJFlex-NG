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
        Me.MainTabs = New System.Windows.Forms.TabControl()
        Me.TracingTab = New System.Windows.Forms.TabPage()
        Me.BrowserTab = New System.Windows.Forms.TabPage()

        ' Tracing tab controls
        Me.LevelListBox = New System.Windows.Forms.ListBox()
        Me.FileNameLabel = New System.Windows.Forms.Label()
        Me.FileNameBox = New System.Windows.Forms.TextBox()
        Me.BrowseButton = New System.Windows.Forms.Button()
        Me.OpenFileDialog = New System.Windows.Forms.OpenFileDialog()

        ' Browser tab controls
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
        Me.ToggleButton = New System.Windows.Forms.Button()
        Me.CnclButton = New System.Windows.Forms.Button()

        Me.MainTabs.SuspendLayout()
        Me.TracingTab.SuspendLayout()
        Me.BrowserTab.SuspendLayout()
        CType(Me.PruneRetentionUpDown, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SuspendLayout()
        '
        'MainTabs
        '
        Me.MainTabs.AccessibleName = "Trace admin sections"
        Me.MainTabs.AccessibleRole = System.Windows.Forms.AccessibleRole.PageTabList
        Me.MainTabs.Location = New System.Drawing.Point(8, 8)
        Me.MainTabs.Name = "MainTabs"
        Me.MainTabs.SelectedIndex = 0
        Me.MainTabs.Size = New System.Drawing.Size(884, 540)
        Me.MainTabs.TabIndex = 0
        Me.MainTabs.Controls.Add(Me.TracingTab)
        Me.MainTabs.Controls.Add(Me.BrowserTab)
        '
        'TracingTab
        '
        Me.TracingTab.AccessibleName = "Tracing"
        Me.TracingTab.Text = "Tracing"
        Me.TracingTab.Padding = New System.Windows.Forms.Padding(8)
        Me.TracingTab.UseVisualStyleBackColor = True
        Me.TracingTab.Controls.Add(Me.FileNameLabel)
        Me.TracingTab.Controls.Add(Me.FileNameBox)
        Me.TracingTab.Controls.Add(Me.BrowseButton)
        Me.TracingTab.Controls.Add(Me.LevelListBox)
        '
        'BrowserTab
        '
        Me.BrowserTab.AccessibleName = "Archive Browser"
        Me.BrowserTab.Text = "Archive Browser"
        Me.BrowserTab.Padding = New System.Windows.Forms.Padding(8)
        Me.BrowserTab.UseVisualStyleBackColor = True
        Me.BrowserTab.Controls.Add(Me.FilterFromLabel)
        Me.BrowserTab.Controls.Add(Me.FilterFromDate)
        Me.BrowserTab.Controls.Add(Me.FilterToLabel)
        Me.BrowserTab.Controls.Add(Me.FilterToDate)
        Me.BrowserTab.Controls.Add(Me.FilterOutcomeLabel)
        Me.BrowserTab.Controls.Add(Me.FilterOutcomeCombo)
        Me.BrowserTab.Controls.Add(Me.FilterSearchLabel)
        Me.BrowserTab.Controls.Add(Me.FilterSearchBox)
        Me.BrowserTab.Controls.Add(Me.FilterStatusLabel)
        Me.BrowserTab.Controls.Add(Me.ArchiveListView)
        Me.BrowserTab.Controls.Add(Me.SelectionDetailLabel)
        Me.BrowserTab.Controls.Add(Me.SelectionDetailBox)
        Me.BrowserTab.Controls.Add(Me.ViewTraceButton)
        Me.BrowserTab.Controls.Add(Me.CopyPathButton)
        Me.BrowserTab.Controls.Add(Me.ExportSelectedButton)
        Me.BrowserTab.Controls.Add(Me.DeleteSelectedButton)
        Me.BrowserTab.Controls.Add(Me.FooterLabel)
        Me.BrowserTab.Controls.Add(Me.AutoPruneInfoLabel)
        Me.BrowserTab.Controls.Add(Me.PruneRetentionLabel)
        Me.BrowserTab.Controls.Add(Me.PruneRetentionUpDown)
        Me.BrowserTab.Controls.Add(Me.PruneNowButton)
        '
        '== Tracing tab controls ==
        '
        'LevelListBox
        '
        Me.LevelListBox.AccessibleName = "trace level"
        Me.LevelListBox.AccessibleRole = System.Windows.Forms.AccessibleRole.List
        Me.LevelListBox.FormattingEnabled = True
        Me.LevelListBox.Items.AddRange(New Object() {"Off", "Error", "Warning", "Info", "Verbose"})
        Me.LevelListBox.Location = New System.Drawing.Point(16, 70)
        Me.LevelListBox.Name = "LevelListBox"
        Me.LevelListBox.Size = New System.Drawing.Size(160, 95)
        Me.LevelListBox.TabIndex = 30
        '
        'FileNameLabel
        '
        Me.FileNameLabel.AutoSize = True
        Me.FileNameLabel.Location = New System.Drawing.Point(16, 16)
        Me.FileNameLabel.Name = "FileNameLabel"
        Me.FileNameLabel.Size = New System.Drawing.Size(60, 13)
        Me.FileNameLabel.TabIndex = 10
        Me.FileNameLabel.Text = "File Name: "
        '
        'FileNameBox
        '
        Me.FileNameBox.AccessibleName = "file name"
        Me.FileNameBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.FileNameBox.Location = New System.Drawing.Point(16, 36)
        Me.FileNameBox.Name = "FileNameBox"
        Me.FileNameBox.Size = New System.Drawing.Size(420, 20)
        Me.FileNameBox.TabIndex = 11
        '
        'BrowseButton
        '
        Me.BrowseButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.BrowseButton.AutoSize = True
        Me.BrowseButton.Location = New System.Drawing.Point(450, 34)
        Me.BrowseButton.Name = "BrowseButton"
        Me.BrowseButton.Size = New System.Drawing.Size(75, 23)
        Me.BrowseButton.TabIndex = 15
        Me.BrowseButton.Text = "Browse"
        Me.BrowseButton.UseVisualStyleBackColor = True
        '
        'OpenFileDialog
        '
        Me.OpenFileDialog.CheckFileExists = False
        Me.OpenFileDialog.DefaultExt = "txt"
        Me.OpenFileDialog.Filter = "text file (*.txt)|*.txt"
        Me.OpenFileDialog.Title = "Trace File"
        '
        '== Browser tab controls ==
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
        'ToggleButton
        '
        Me.ToggleButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.ToggleButton.AutoSize = True
        Me.ToggleButton.Location = New System.Drawing.Point(720, 560)
        Me.ToggleButton.Name = "ToggleButton"
        Me.ToggleButton.Size = New System.Drawing.Size(75, 25)
        Me.ToggleButton.TabIndex = 200
        Me.ToggleButton.UseVisualStyleBackColor = True
        '
        'CnclButton
        '
        Me.CnclButton.AccessibleRole = System.Windows.Forms.AccessibleRole.PushButton
        Me.CnclButton.DialogResult = System.Windows.Forms.DialogResult.Cancel
        Me.CnclButton.Location = New System.Drawing.Point(810, 560)
        Me.CnclButton.Name = "CnclButton"
        Me.CnclButton.Size = New System.Drawing.Size(80, 25)
        Me.CnclButton.TabIndex = 201
        Me.CnclButton.Text = "Close"
        Me.CnclButton.UseVisualStyleBackColor = True
        '
        'TraceAdmin
        '
        Me.AcceptButton = Me.ToggleButton
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.CancelButton = Me.CnclButton
        Me.ClientSize = New System.Drawing.Size(900, 595)
        Me.Controls.Add(Me.MainTabs)
        Me.Controls.Add(Me.ToggleButton)
        Me.Controls.Add(Me.CnclButton)
        Me.MinimumSize = New System.Drawing.Size(700, 500)
        Me.Name = "TraceAdmin"
        Me.Text = "Tracing"
        Me.TracingTab.ResumeLayout(False)
        Me.TracingTab.PerformLayout()
        Me.BrowserTab.ResumeLayout(False)
        Me.BrowserTab.PerformLayout()
        Me.MainTabs.ResumeLayout(False)
        CType(Me.PruneRetentionUpDown, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    ' Tabs
    Friend WithEvents MainTabs As System.Windows.Forms.TabControl
    Friend WithEvents TracingTab As System.Windows.Forms.TabPage
    Friend WithEvents BrowserTab As System.Windows.Forms.TabPage

    ' Tracing tab
    Friend WithEvents LevelListBox As System.Windows.Forms.ListBox
    Friend WithEvents FileNameLabel As System.Windows.Forms.Label
    Friend WithEvents FileNameBox As System.Windows.Forms.TextBox
    Friend WithEvents BrowseButton As System.Windows.Forms.Button
    Friend WithEvents OpenFileDialog As System.Windows.Forms.OpenFileDialog

    ' Browser tab
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
    Friend WithEvents ToggleButton As System.Windows.Forms.Button
    Friend WithEvents CnclButton As System.Windows.Forms.Button
End Class

<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class CommandFinder
    Inherits System.Windows.Forms.Form

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

    Private components As System.ComponentModel.IContainer

    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.SearchBox = New System.Windows.Forms.TextBox()
        Me.SearchLabel = New System.Windows.Forms.Label()
        Me.ResultsListView = New System.Windows.Forms.ListView()
        Me.CommandColumn = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.KeyColumn = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.ScopeColumn = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.ResultCountLabel = New System.Windows.Forms.Label()
        Me.SuspendLayout()
        '
        'SearchLabel
        '
        Me.SearchLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.SearchLabel.AutoSize = True
        Me.SearchLabel.Location = New System.Drawing.Point(8, 12)
        Me.SearchLabel.Name = "SearchLabel"
        Me.SearchLabel.Size = New System.Drawing.Size(44, 13)
        Me.SearchLabel.TabIndex = 0
        Me.SearchLabel.Text = "Search"
        '
        'SearchBox
        '
        Me.SearchBox.AccessibleName = "Search commands"
        Me.SearchBox.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.SearchBox.Location = New System.Drawing.Point(60, 8)
        Me.SearchBox.Name = "SearchBox"
        Me.SearchBox.Size = New System.Drawing.Size(380, 20)
        Me.SearchBox.TabIndex = 1
        '
        'ResultsListView
        '
        Me.ResultsListView.AccessibleName = "Search results"
        Me.ResultsListView.AccessibleRole = System.Windows.Forms.AccessibleRole.List
        Me.ResultsListView.Columns.AddRange(New System.Windows.Forms.ColumnHeader() {Me.CommandColumn, Me.KeyColumn, Me.ScopeColumn})
        Me.ResultsListView.FullRowSelect = True
        Me.ResultsListView.GridLines = True
        Me.ResultsListView.HideSelection = False
        Me.ResultsListView.Location = New System.Drawing.Point(8, 38)
        Me.ResultsListView.MultiSelect = False
        Me.ResultsListView.Name = "ResultsListView"
        Me.ResultsListView.Size = New System.Drawing.Size(432, 300)
        Me.ResultsListView.TabIndex = 10
        Me.ResultsListView.UseCompatibleStateImageBehavior = False
        Me.ResultsListView.View = System.Windows.Forms.View.Details
        '
        'CommandColumn
        '
        Me.CommandColumn.Text = "Command"
        Me.CommandColumn.Width = 240
        '
        'KeyColumn
        '
        Me.KeyColumn.Text = "Key"
        Me.KeyColumn.Width = 120
        '
        'ScopeColumn
        '
        Me.ScopeColumn.Text = "Scope"
        Me.ScopeColumn.Width = 60
        '
        'ResultCountLabel
        '
        Me.ResultCountLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text
        Me.ResultCountLabel.AutoSize = True
        Me.ResultCountLabel.Location = New System.Drawing.Point(8, 345)
        Me.ResultCountLabel.Name = "ResultCountLabel"
        Me.ResultCountLabel.Size = New System.Drawing.Size(0, 13)
        Me.ResultCountLabel.TabIndex = 20
        '
        'CommandFinder
        '
        Me.AccessibleName = "Command Finder"
        Me.AccessibleRole = System.Windows.Forms.AccessibleRole.Dialog
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(450, 370)
        Me.Controls.Add(Me.SearchLabel)
        Me.Controls.Add(Me.SearchBox)
        Me.Controls.Add(Me.ResultsListView)
        Me.Controls.Add(Me.ResultCountLabel)
        Me.KeyPreview = True
        Me.Name = "CommandFinder"
        Me.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen
        Me.Text = "Command Finder"
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub
    Friend WithEvents SearchBox As System.Windows.Forms.TextBox
    Friend WithEvents SearchLabel As System.Windows.Forms.Label
    Friend WithEvents ResultsListView As System.Windows.Forms.ListView
    Friend WithEvents CommandColumn As System.Windows.Forms.ColumnHeader
    Friend WithEvents KeyColumn As System.Windows.Forms.ColumnHeader
    Friend WithEvents ScopeColumn As System.Windows.Forms.ColumnHeader
    Friend WithEvents ResultCountLabel As System.Windows.Forms.Label
End Class

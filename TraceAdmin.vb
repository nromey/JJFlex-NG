Imports JJTrace

''' <summary>
''' The Saved Diagnostic Logs window.
'''
''' Sprint 30 Track D repurposed this form. It used to be "Tracing", a two-tab
''' window whose first tab started and stopped traces — a job that now belongs to
''' Settings > Diagnostics, where it can be found. The second tab was an archive
''' browser built in Sprint 29 Track H that NOTHING in the app ever instantiated:
''' both menus routed Help > Tracing to the WPF dialog instead, so no tester
''' could ever have reached it and its entire test checklist sat unticked.
''' Opening this window from the Diagnostics tab is what makes it reachable.
'''
''' The TYPE keeps its old name so every partial and reference stays put. The
''' window title is what the operator meets, and it now says what the window is.
'''
''' The browser itself lives in TraceAdmin.Browser.vb.
''' </summary>
Public Class TraceAdmin

    Private Sub TraceAdmin_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        ' The browser is the whole window now, so it initializes on load rather
        ' than on a tab activation that can no longer happen.
        InitializeArchiveBrowser()
        _browserInitialized = True
    End Sub

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        DialogResult = System.Windows.Forms.DialogResult.Cancel
        Close()
    End Sub

    ''' <summary>
    ''' Escape closes, like every other dialog in the app. The Close button is
    ''' already the CancelButton, which handles it; this is here so the rule
    ''' survives anyone rearranging the buttons.
    ''' </summary>
    Protected Overrides Function ProcessCmdKey(ByRef msg As System.Windows.Forms.Message,
                                               keyData As System.Windows.Forms.Keys) As Boolean
        If keyData = System.Windows.Forms.Keys.Escape Then
            DialogResult = System.Windows.Forms.DialogResult.Cancel
            Close()
            Return True
        End If
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function
End Class

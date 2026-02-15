''' <summary>
''' Minimal empty form required by VB.NET My.Application framework.
''' All actual UI is in WPF MainWindow.
''' My.Application needs a MainForm for the Windows message loop.
''' </summary>
Public Class BridgeForm
    Inherits System.Windows.Forms.Form

    Public Sub New()
        ' Make invisible — WPF MainWindow is the real UI
        Me.ShowInTaskbar = False
        Me.WindowState = FormWindowState.Minimized
        Me.Opacity = 0
        Me.FormBorderStyle = FormBorderStyle.None
        Me.Size = New System.Drawing.Size(0, 0)
    End Sub
End Class

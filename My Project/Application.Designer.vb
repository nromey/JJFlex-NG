'------------------------------------------------------------------------------
' Sprint 8: Modified to launch WPF MainWindow instead of WinForms Form1.
' Keeps My.Application framework (MyType=WindowsForms) for My.* namespace compat.
' Uses a hidden WinForms bridge form; the real UI is WPF MainWindow.
'------------------------------------------------------------------------------

Option Strict On
Option Explicit On

Namespace My
    Partial Friend Class MyApplication
        <Global.System.Diagnostics.DebuggerStepThroughAttribute()>
        Public Sub New()
            MyBase.New(Global.Microsoft.VisualBasic.ApplicationServices.AuthenticationMode.Windows)
            Me.IsSingleInstance = True
            Me.EnableVisualStyles = True
            Me.SaveMySettingsOnExit = True
            Me.ShutDownStyle = Global.Microsoft.VisualBasic.ApplicationServices.ShutdownMode.AfterMainFormCloses
        End Sub

        <Global.System.Diagnostics.DebuggerStepThroughAttribute()>
        Protected Overrides Sub OnCreateMainForm()
            ' Sprint 8: Create a hidden WinForms bridge form as MainForm.
            ' The real UI is the WPF MainWindow, launched from ApplicationEvents.vb Startup.
            ' When the WPF window closes, it closes this bridge form, ending the app.
            Dim bridge As New System.Windows.Forms.Form()
            bridge.Text = "JJFlexRadio Bridge"
            bridge.ShowInTaskbar = False
            bridge.WindowState = System.Windows.Forms.FormWindowState.Minimized
            bridge.Opacity = 0
            bridge.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None
            bridge.Size = New System.Drawing.Size(0, 0)
            Me.MainForm = bridge
        End Sub
    End Class
End Namespace

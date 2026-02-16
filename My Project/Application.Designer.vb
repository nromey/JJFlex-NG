'------------------------------------------------------------------------------
' ShellForm is the My.Application MainForm — a visible WinForms Form
' that hosts the WPF MainWindow content via ElementHost.
' This provides proper HWND ownership, keyboard routing, and screen reader bridging.
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
            ' Use the ShellForm instance created in Startup (where callbacks are wired).
            Me.MainForm = TheShellForm
        End Sub
    End Class
End Namespace

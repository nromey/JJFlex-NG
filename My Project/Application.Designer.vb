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
            ' #171 silent verification channel: a render-off harness instance
            ' must NOT join the single-instance pipe. With IsSingleInstance
            ' unconditional, a second launch forwards its command line to the
            ' operator's running app and exits before its own Startup ever runs
            ' — the silent instance both pokes the app it was told not to touch
            ' AND dies without writing its transcript marker. Found live on
            ' 2026-08-21: the first verification launch did exactly that, and
            ' only the missing-marker tripwire made it visible. Normal launches
            ' (render on) keep single-instance behaviour unchanged.
            Me.IsSingleInstance = Global.Radios.OutputChannelRecorder.ParseStartupSwitches(
                Global.System.Environment.GetCommandLineArgs()).Render
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

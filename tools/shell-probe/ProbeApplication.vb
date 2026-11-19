Option Strict On
Imports System.Windows.Forms
Imports Microsoft.VisualBasic.ApplicationServices

Namespace My
    ''' <summary>
    ''' The VB My.Application loop the product runs on, with OnRun overridden so
    ''' there is NO main form: the loop runs on an ApplicationContext that ends
    ''' when the WPF window closes. (The base OnRun throws NoStartupFormException
    ''' when MainForm is Nothing, which is why it must be overridden.)
    '''
    ''' Command line:
    '''   (none)              the target: a WPF Window, no main form
    '''   --hybrid            the positive control: today's WinForms + ElementHost shape
    '''   --keymenu-fallback  the WPF window posts SC_KEYMENU itself for Alt+letter and F10
    ''' </summary>
    Partial Friend Class MyApplication

        Private _window As Global.ShellProbe.ProbeWindow
        Private _hybrid As Global.ShellProbe.HybridShell

        <Global.System.Diagnostics.DebuggerStepThrough()>
        Public Sub New()
            MyBase.New(AuthenticationMode.Windows)
            Me.IsSingleInstance = True
            Me.EnableVisualStyles = True
            Me.SaveMySettingsOnExit = False
            Me.ShutDownStyle = ShutdownMode.AfterMainFormCloses
        End Sub

        Private Function HasArg(name As String) As Boolean
            For Each a In Me.CommandLineArgs
                If String.Equals(a, name, StringComparison.OrdinalIgnoreCase) Then Return True
            Next
            Return False
        End Function

        Protected Overrides Sub OnRun()
            Dim hybrid = HasArg("--hybrid")
            ProbeLog.Log("---- launch: " & If(hybrid, "HYBRID (WinForms form + ElementHost)", "WPF window, no main form") &
                         ", args [" & String.Join(" ", Me.CommandLineArgs) & "]")
            AddHandler System.Windows.Forms.Application.ThreadException, Sub(s, e) ProbeLog.Log("ThreadException: " & e.Exception.ToString())

            Dim context As ApplicationContext
            If hybrid Then
                _hybrid = New Global.ShellProbe.HybridShell()
                context = New ApplicationContext(_hybrid)
                _hybrid.Show()
            Else
                _window = New Global.ShellProbe.ProbeWindow(HasArg("--keymenu-fallback"))
                _window.Prepare()
                context = New ApplicationContext()
                AddHandler _window.Closed, Sub(s, e) context.ExitThread()
                _window.Show()
            End If
            ProbeLog.Log("OnRun: shown; entering Application.Run on an ApplicationContext")
            System.Windows.Forms.Application.Run(context)
            ProbeLog.Log("OnRun: Application.Run returned")
        End Sub

        Private Sub ProbeApplication_StartupNextInstance(sender As Object, e As StartupNextInstanceEventArgs) Handles Me.StartupNextInstance
            ' The framework's own bring-to-front acts on MainForm, which is
            ' Nothing in the target; this handler is the replacement.
            e.BringToForeground = False
            ProbeLog.Log("StartupNextInstance: args [" & String.Join(" ", e.CommandLine) & "], thread " &
                         Environment.CurrentManagedThreadId.ToString())
            If _window IsNot Nothing Then
                _window.Dispatcher.Invoke(Sub() _window.BringForward())
            ElseIf _hybrid IsNot Nothing Then
                _hybrid.Invoke(Sub() _hybrid.BringForward())
            End If
        End Sub

        Private Sub ProbeApplication_Shutdown(sender As Object, e As EventArgs) Handles Me.Shutdown
            ProbeLog.Log("Shutdown event")
        End Sub
    End Class
End Namespace

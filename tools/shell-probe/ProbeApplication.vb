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
    '''
    ''' Round 2 (the menu; for-claude/2026-11-19-shell-probe-menu-research.md):
    '''   --no-dialog-keys    candidate 1: keys WPF declines are dispatched by a message
    '''                       filter, never run through IsDialogMessage
    '''   --focus-after-menu  candidate 2: after WM_EXITMENULOOP, raise WPF's focus
    '''                       notification on the element that already has focus
    '''   --dispatcher-loop   candidate 4: the WPF window on Dispatcher.Run, no WinForms
    '''                       loop and no EnableModelessKeyboardInterop
    '''   --hybrid --nohop    control: the hybrid without ShellForm's menu-close hop
    '''   --raw               reference: a plain Win32 window and Edit on a bare loop
    '''
    ''' Round 3 (JAWS's silent menu-bar entry):
    '''   --child-source      arm 3a: the WPF Window keeps the menu; its content is an
    '''                       HwndHost holding a child HwndSource
    '''   --raw --wpf-content arm 3b: the raw Win32 top level keeps the menu and hosts
    '''                       the same content in a child HwndSource; its loop runs
    '''                       ComponentDispatcher first, as WPF's own loop does
    ''' </summary>
    Partial Friend Class MyApplication

        Private _window As Global.ShellProbe.ProbeWindow
        Private _hybrid As Global.ShellProbe.HybridShell
        Private _raw As Global.ShellProbe.RawShell

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
            Dim raw = HasArg("--raw")
            Dim opts As New Global.ShellProbe.ProbeOptions With {
                .KeyMenuFallback = HasArg("--keymenu-fallback"),
                .NoDialogKeys = HasArg("--no-dialog-keys"),
                .FocusAfterMenu = HasArg("--focus-after-menu"),
                .DispatcherLoop = HasArg("--dispatcher-loop"),
                .ChildSource = HasArg("--child-source")}
            ProbeLog.Log("---- launch: " & If(hybrid, "HYBRID (WinForms form + ElementHost)", If(raw, "RAW Win32 window, bare loop", "WPF window, no main form")) &
                         ", args [" & String.Join(" ", Me.CommandLineArgs) & "]")
            AddHandler System.Windows.Forms.Application.ThreadException, Sub(s, e) ProbeLog.Log("ThreadException: " & e.Exception.ToString())

            If raw Then
                _raw = New Global.ShellProbe.RawShell(HasArg("--wpf-content"), opts.FocusAfterMenu)
                _raw.Run()
                Return
            End If

            Dim context As ApplicationContext
            If hybrid Then
                _hybrid = New Global.ShellProbe.HybridShell(HasArg("--nohop"))
                context = New ApplicationContext(_hybrid)
                _hybrid.Show()
            Else
                _window = New Global.ShellProbe.ProbeWindow(opts)
                _window.Prepare()
                If opts.DispatcherLoop Then
                    Dim d = System.Windows.Threading.Dispatcher.CurrentDispatcher
                    AddHandler _window.Closed, Sub(s, e) d.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal)
                    _window.Show()
                    ProbeLog.Log("OnRun: shown; entering Dispatcher.Run (no WinForms loop)")
                    System.Windows.Threading.Dispatcher.Run()
                    ProbeLog.Log("OnRun: Dispatcher.Run returned")
                    Return
                End If
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
            ElseIf _raw IsNot Nothing Then
                _raw.BringForward()
            End If
        End Sub

        Private Sub ProbeApplication_Shutdown(sender As Object, e As EventArgs) Handles Me.Shutdown
            ProbeLog.Log("Shutdown event")
        End Sub
    End Class
End Namespace

Namespace Global.ShellProbe
    ''' <summary>The WPF window's switches, round 1 and round 2.</summary>
    Friend NotInheritable Class ProbeOptions
        Friend KeyMenuFallback As Boolean
        Friend NoDialogKeys As Boolean
        Friend FocusAfterMenu As Boolean
        Friend DispatcherLoop As Boolean
        Friend ChildSource As Boolean

        Friend Function Describe() As String
            Return "keymenu-fallback=" & KeyMenuFallback.ToString() & " no-dialog-keys=" & NoDialogKeys.ToString() &
                   " focus-after-menu=" & FocusAfterMenu.ToString() & " dispatcher-loop=" & DispatcherLoop.ToString() & " child-source=" & ChildSource.ToString()
        End Function
    End Class
End Namespace

Imports System.Windows.Forms.Integration

''' <summary>
''' WinForms shell that hosts the WPF MainWindow content via ElementHost.
'''
''' This is the My.Application MainForm — it owns the HWND, the taskbar entry,
''' Alt+Tab presence, and the message loop. The WPF UserControl fills the entire
''' client area via a docked ElementHost, which provides:
'''   - Keyboard routing (IKeyboardInputSink) — Tab, arrows, Alt+F4 all work
'''   - Screen reader bridging (UI Automation ↔ MSAA)
'''   - Focus management between WinForms and WPF
'''
''' The menu bar is a native Win32 HMENU (via NativeMenuBar P/Invoke), NOT a
''' WinForms MenuStrip. Native menus use ROLE_SYSTEM_MENUBAR / ROLE_SYSTEM_MENUITEM
''' so JAWS/NVDA navigate them correctly without "collapsed/expanded" noise.
''' Windows handles Alt/F10 → menu activation automatically via DefWindowProc.
''' </summary>
Public Class ShellForm
    Inherits System.Windows.Forms.Form

    Private _elementHost As ElementHost
    Friend WpfContent As JJFlexWpf.MainWindow
    Private _nativeMenu As JJFlexWpf.NativeMenuBar

    Private Const WM_ENTERMENULOOP As Integer = &H211
    Private Const WM_EXITMENULOOP As Integer = &H212
    Private _inNativeMenuLoop As Boolean = False

    Public Sub New()
        Me.Text = "JJFlexRadio"
        Me.Width = 800
        Me.Height = 600
        Me.MinimumSize = New System.Drawing.Size(640, 400)
        Me.StartPosition = FormStartPosition.CenterScreen

        ' Create ElementHost filling the entire client area
        ' (native HMENU lives in the non-client area, doesn't need DockPanel space)
        _elementHost = New ElementHost()
        _elementHost.Dock = DockStyle.Fill
        Me.Controls.Add(_elementHost)

        ' Create WPF content and host it
        WpfContent = New JJFlexWpf.MainWindow()
        _elementHost.Child = WpfContent

        ' Build native Win32 menus (attached to HWND in HandleCreated)
        _nativeMenu = New JJFlexWpf.NativeMenuBar(WpfContent)
        WpfContent.MenuModeCallback = Sub(mode) _nativeMenu.ApplyUIMode(mode)
        WpfContent.RebuildMenuCallback = Sub() _nativeMenu.RebuildCurrentMenu()
    End Sub

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        ' Now that we have an HWND, attach the native menu bar
        _nativeMenu.AttachTo(Me.Handle)
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        SpeakWelcomeDelayed()
    End Sub

    Private Async Sub SpeakWelcomeDelayed()
        ' Wait for NVDA to finish its focus announcements (window title, focused control)
        ' before speaking the welcome. Task.Delay works reliably in WinForms+WPF hybrid
        ' (WinForms Timer WM_TIMER messages can get swallowed by ElementHost).
        Await Task.Delay(2000)
        WpfContent.SpeakWelcome()
    End Sub

    ''' <summary>
    ''' Route keys through DoCommandHandler BEFORE native menu processes them.
    ''' This preserves Sprint 6 BUG-010 fix: Alt+Letter hotkeys in Logging Mode
    ''' go to DoCommand, not to the menu bar.
    ''' </summary>
    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        ' Let DoCommandHandler try first (scope-aware key routing)
        If WpfContent?.DoCommandHandler IsNot Nothing Then
            If WpfContent.DoCommandHandler(keyData) Then
                Return True
            End If
        End If
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    ''' <summary>
    ''' Handle WM_COMMAND from native Win32 menus, and return focus to WPF
    ''' content when the menu loop exits (Escape or item selected).
    ''' </summary>
    Protected Overrides Sub WndProc(ByRef m As Message)
        ' Track native menu loop entry/exit for safe focus return
        If m.Msg = WM_ENTERMENULOOP Then
            _inNativeMenuLoop = True
        End If

        If m.Msg = WM_EXITMENULOOP AndAlso _inNativeMenuLoop Then
            _inNativeMenuLoop = False
            MyBase.WndProc(m)
            ' Defer focus restore so it doesn't re-enter during WndProc processing
            BeginInvoke(Sub() _elementHost?.Focus())
            Return
        End If

        ' WM_COMMAND with LParam=0 means it's from a menu (not a control notification)
        If m.Msg = JJFlexWpf.NativeMenuBar.WM_COMMAND AndAlso m.LParam = IntPtr.Zero Then
            If _nativeMenu IsNot Nothing AndAlso _nativeMenu.HandleWmCommand(m.WParam) Then
                Return
            End If
        End If

        MyBase.WndProc(m)
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        ' Delegate close decision to WPF content (which calls VB-side ExitApplication)
        If Not WpfContent.RequestShutdown() Then
            e.Cancel = True
            Return
        End If

        _nativeMenu?.Dispose()
        MyBase.OnFormClosing(e)
    End Sub
End Class

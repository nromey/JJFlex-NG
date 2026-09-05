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
        Me.Text = "JJ Flexible Radio Access"
        Me.Width = 800
        Me.Height = 600
        Me.MinimumSize = New System.Drawing.Size(640, 400)
        Me.StartPosition = FormStartPosition.CenterScreen

        ' Create ElementHost filling the entire client area
        ' (native HMENU lives in the non-client area, doesn't need DockPanel space)
        _elementHost = New ElementHost()
        _elementHost.Dock = DockStyle.Fill
        ' The host had no accessible name, so whenever focus rested on it — a
        ' dialog closing with nothing inside the WPF surface taking focus —
        ' screen readers announced a bare "pane" (#349). The focus-return path
        ' now lands somewhere named and says so; this is the backstop for any
        ' moment focus still passes through the host itself.
        _elementHost.AccessibleName = "JJ Flexible Radio Access"
        _elementHost.AccessibleRole = AccessibleRole.Client
        Me.Controls.Add(_elementHost)

        ' Create WPF content and host it
        WpfContent = New JJFlexWpf.MainWindow()
        _elementHost.Child = WpfContent

        ' Build native Win32 menus (attached to HWND in HandleCreated)
        _nativeMenu = New JJFlexWpf.NativeMenuBar(WpfContent)
        WpfContent.MenuModeCallback = Sub(mode) _nativeMenu.ApplyUIMode(mode)
        WpfContent.RebuildMenuCallback = Sub() _nativeMenu.RebuildCurrentMenu()
        WpfContent.SetNativeMenuFilterPresetsCallback = Sub(presets) _nativeMenu.FilterPresets = presets
        WpfContent.OpenSettingsCallback = Sub(tab) _nativeMenu.OpenSettings(tab)
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
        ' Short settle only. This USED to be a 2-second sleep whose comment said
        ' it was waiting for NVDA to finish its own focus announcements before
        ' speaking - which is a queue, hand-rolled, because the application had
        ' no way to say "queue this". It now does: the arrival announcement is
        ' SpeechIntent.Queue and lands behind whatever the screen reader is
        ' saying, however long that takes.
        '
        ' A fixed sleep was always wrong in both directions - too long on a fast
        ' machine, too short on a slow one. What remains is a brief yield so the
        ' window has finished settling before we move focus to FreqOut.
        Await Task.Delay(250)
        ' Task.Delay can resume on a thread pool thread when the SynchronizationContext
        ' isn't captured (WinForms+WPF hybrid). Marshal back to UI thread explicitly
        ' since SpeakWelcome calls WPF Focus() which requires STA.
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() WpfContent.SpeakWelcome())
        Else
            WpfContent.SpeakWelcome()
        End If
    End Sub

    Private Const WM_SYSCOMMAND As Integer = &H112
    Private Const SC_KEYMENU As Integer = &HF100

    <System.Runtime.InteropServices.DllImport("user32.dll")>
    Private Shared Function PostMessage(hWnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As Boolean
    End Function

    ' #538 - reaching a modal that Alt+Tab cannot see. See WndProc below.
    Private Const WM_ACTIVATE As Integer = &H6
    Private Const WA_INACTIVE As Integer = 0
    Private Const GW_ENABLEDPOPUP As UInteger = 6

    <System.Runtime.InteropServices.DllImport("user32.dll")>
    Private Shared Function GetWindow(hWnd As IntPtr, uCmd As UInteger) As IntPtr
    End Function

    <System.Runtime.InteropServices.DllImport("user32.dll")>
    Private Shared Function SetForegroundWindow(hWnd As IntPtr) As Boolean
    End Function

    <System.Runtime.InteropServices.DllImport("user32.dll")>
    Private Shared Function IsWindowEnabled(hWnd As IntPtr) As Boolean
    End Function

    ''' <summary>
    ''' Route keys through DoCommandHandler BEFORE native menu processes them.
    ''' BUG-010 fix: Alt+Letter hotkeys in Logging Mode go to DoCommand.
    ''' For Alt+letter combos NOT registered as commands, explicitly activate
    ''' the native Win32 menu via WM_SYSCOMMAND (ElementHost eats WM_SYSCHAR).
    ''' </summary>
    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        ' Let DoCommandHandler try first (scope-aware key routing)
        If WpfContent?.DoCommandHandler IsNot Nothing Then
            If WpfContent.DoCommandHandler(keyData) Then
                Return True
            End If
        End If

        ' If this is an Alt+letter key (no Ctrl, no Shift) and DoCommand didn't handle it,
        ' explicitly activate the native Win32 HMENU accelerator.
        ' WPF ElementHost intercepts WM_SYSCHAR so returning False doesn't work —
        ' we must post WM_SYSCOMMAND with SC_KEYMENU to trigger the menu.
        Dim isAltOnly = ((keyData And Keys.Alt) = Keys.Alt) AndAlso
                        ((keyData And Keys.Control) <> Keys.Control) AndAlso
                        ((keyData And Keys.Shift) <> Keys.Shift)
        If isAltOnly Then
            Dim letter = keyData And Keys.KeyCode
            If letter >= Keys.A AndAlso letter <= Keys.Z Then
                ' Post SC_KEYMENU with the letter character to activate the menu accelerator
                Dim ch As Integer = Asc(Chr(letter).ToString().ToLower()(0))
                PostMessage(Me.Handle, WM_SYSCOMMAND, New IntPtr(SC_KEYMENU), New IntPtr(ch))
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
        ' #538 - HAND ACTIVATION ON TO THE MODAL. Alt+Tab gives the foreground
        ' to THIS window even while a dialog owns input, and Windows does not
        ' pass it along. Measured 2026-09-05 with a foreground watcher: the
        ' foreground landed on this hwnd with enabled=False and stayed there
        ' for the rest of the sample, while 'Select Radio' - the only enabled
        ' window in the process - never received it once. What the operator
        ' gets is the window title read out, no reachable menu bar, and nothing
        ' that responds, because every control here is disabled by design while
        ' a modal is up. It reads exactly like a hang and is not one.
        '
        ' Why this was invisible to the focus watchdog (#529) and to
        ' StrandedFocusSentinel: both ask "does a FOREIGN window hold our
        ' foreground". Here the answer is no - JJ Flexible holds it, with the
        ' wrong window of its own. Their world model is us-versus-them and this
        ' failure is us-but-the-wrong-us, so both correctly did nothing while
        ' the operator was stuck. That is why it outlived a sprint aimed at
        ' focus.
        '
        ' Me.Enabled is NOT the test. WPF's ShowDialog disables the owner
        ' through Win32 EnableWindow, which never reaches the WinForms managed
        ' property, so Me.Enabled still reads True while the HWND is disabled.
        ' Ask the window itself.
        '
        ' GW_ENABLEDPOPUP asks precisely the right question - "the enabled
        ' popup this window owns" - so there is no enumeration and no guessing
        ' which dialog sits on top of which when several are stacked.
        If m.Msg = WM_ACTIVATE AndAlso (m.WParam.ToInt64() And &HFFFF) <> WA_INACTIVE Then
            If Not IsWindowEnabled(Me.Handle) Then
                Dim popup = GetWindow(Me.Handle, GW_ENABLEDPOPUP)
                If popup <> IntPtr.Zero AndAlso popup <> Me.Handle Then
                    MyBase.WndProc(m)
                    ' Deferred for the same reason the menu-loop focus restore
                    ' below is: moving the foreground from inside WndProc
                    ' re-enters the activation path we are standing in.
                    If Me.IsHandleCreated Then
                        Me.BeginInvoke(Sub() SetForegroundWindow(popup))
                    End If
                    Return
                End If
            End If
        End If

        ' Track native menu loop entry/exit for safe focus return.
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

        ' WM_INITMENUPOPUP: update checkmarks before menu is displayed
        If m.Msg = JJFlexWpf.NativeMenuBar.WM_INITMENUPOPUP Then
            _nativeMenu?.HandleInitMenuPopup(m.WParam)
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

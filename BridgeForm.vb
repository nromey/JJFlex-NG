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
''' The previous architecture (invisible BridgeForm + standalone WPF Window) failed
''' because the WinForms message pump ate all keyboard input. ElementHost is the
''' supported bridge for WPF-in-WinForms keyboard interop.
''' </summary>
Public Class ShellForm
    Inherits System.Windows.Forms.Form

    Private _elementHost As ElementHost
    Friend WpfContent As JJFlexWpf.MainWindow
    Friend _menuStrip As MenuStrip

    Public Sub New()
        Me.Text = "JJFlexRadio"
        Me.Width = 800
        Me.Height = 600
        Me.MinimumSize = New System.Drawing.Size(640, 400)
        Me.StartPosition = FormStartPosition.CenterScreen

        ' Create MenuStrip BEFORE ElementHost — DockPanel layout: Menu top, WPF fills rest
        _menuStrip = New MenuStrip()
        _menuStrip.Dock = DockStyle.Top
        Me.MainMenuStrip = _menuStrip
        Me.Controls.Add(_menuStrip)

        ' Create ElementHost filling the remaining space
        _elementHost = New ElementHost()
        _elementHost.Dock = DockStyle.Fill
        Me.Controls.Add(_elementHost)

        ' Create WPF content and host it
        WpfContent = New JJFlexWpf.MainWindow()
        _elementHost.Child = WpfContent

        ' Build WinForms menus from MenuStripBuilder
        Dim menuBuilder As New JJFlexWpf.MenuStripBuilder(WpfContent)
        menuBuilder.BuildAllMenus(_menuStrip)
        WpfContent.MenuModeCallback = Sub(mode) menuBuilder.ApplyUIMode(mode)
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        ' Speak welcome after the window is visible so the screen reader can hear it.
        ' (MainWindow_Loaded fires during the constructor before the form is shown.)
        WpfContent.SpeakWelcome()
    End Sub

    ''' <summary>
    ''' Route keys through DoCommandHandler BEFORE MenuStrip processes them.
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

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        ' Delegate close decision to WPF content (which calls VB-side ExitApplication)
        If Not WpfContent.RequestShutdown() Then
            e.Cancel = True
            Return
        End If
        MyBase.OnFormClosing(e)
    End Sub
End Class

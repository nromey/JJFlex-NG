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

    Public Sub New()
        Me.Text = "JJFlexRadio"
        Me.Width = 800
        Me.Height = 600
        Me.MinimumSize = New System.Drawing.Size(640, 400)
        Me.StartPosition = FormStartPosition.CenterScreen

        ' Create ElementHost filling the entire form
        _elementHost = New ElementHost()
        _elementHost.Dock = DockStyle.Fill
        Me.Controls.Add(_elementHost)

        ' Create WPF content and host it
        WpfContent = New JJFlexWpf.MainWindow()
        _elementHost.Child = WpfContent
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        ' Delegate close decision to WPF content (which calls VB-side ExitApplication)
        If Not WpfContent.RequestShutdown() Then
            e.Cancel = True
            Return
        End If
        MyBase.OnFormClosing(e)
    End Sub
End Class

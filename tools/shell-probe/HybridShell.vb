Option Strict On
Imports System.Windows.Forms
Imports System.Windows.Forms.Integration

''' <summary>
''' The POSITIVE CONTROL, run with --hybrid: today's architecture in miniature.
''' A WinForms form hosting the same content through an ElementHost that carries
''' an MSAA name, with the menu-close hop (_elementHost.Focus() on
''' WM_EXITMENULOOP) and the Alt+letter SC_KEYMENU post copied from
''' ShellForm (BridgeForm.vb). Its host focus event is the thing the cause
''' report heard after every menu Escape. The recorder and the readers must
''' see it here before a zero in the WPF shell can mean anything.
'''
''' The host's name is "JJ Shell Probe host" rather than the title, so a host
''' utterance cannot be mistaken for a title utterance.
''' </summary>
Friend NotInheritable Class HybridShell
    Inherits Form

    Private ReadOnly _host As ElementHost
    Friend ReadOnly Body As ProbeContent
    Private ReadOnly _menu As New NativeMenu()

    Friend Sub New()
        Text = "JJ Shell Probe"
        Width = 800
        Height = 600
        MinimumSize = New System.Drawing.Size(640, 400)
        StartPosition = FormStartPosition.CenterScreen

        _host = New ElementHost() With {.Dock = DockStyle.Fill}
        _host.AccessibleName = "JJ Shell Probe host"
        _host.AccessibleRole = AccessibleRole.Client
        Controls.Add(_host)

        Body = New ProbeContent()
        Body.OwnerHandle = Function() Handle
        _host.Child = Body
    End Sub

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        _menu.AttachTo(Handle)
        ProbeLog.Log("HybridShell: handle " & Win32.Hex(Handle))
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        BeginInvoke(Sub() Body.Field.Focus())
    End Sub

    Friend Sub BringForward()
        If WindowState = FormWindowState.Minimized Then WindowState = FormWindowState.Normal
        Activate()
        ProbeLog.Log("BringForward (hybrid): foreground is " & Win32.Hex(Win32.GetForegroundWindow()))
    End Sub

    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        ' As ShellForm: an Alt+letter nobody claimed opens the native menu,
        ' because "ElementHost eats WM_SYSCHAR".
        Dim key = keyData And Keys.KeyCode
        If (keyData And Keys.Alt) = Keys.Alt AndAlso key >= Keys.A AndAlso key <= Keys.Z Then
            ProbeLog.Log("Hybrid ProcessCmdKey: posting SC_KEYMENU for " & key.ToString())
            Win32.PostMessage(Handle, Win32.WM_SYSCOMMAND, New IntPtr(Win32.SC_KEYMENU),
                              New IntPtr(AscW(key.ToString().ToLowerInvariant()(0))))
            Return True
        End If
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    Protected Overrides Sub WndProc(ByRef m As Message)
        Dim name = Win32.Watched(m.Msg)
        If name IsNot Nothing Then
            ProbeLog.Log("HMSG " & name & " wParam=" & Win32.Hex(m.WParam) & " lParam=" & Win32.Hex(m.LParam) &
                         " GetFocus=" & Win32.Hex(Win32.GetFocus()))
        End If
        Select Case m.Msg
            Case Win32.WM_EXITMENULOOP
                MyBase.WndProc(m)
                ' ShellForm's unconditional hop, the cause of #550's host utterance.
                BeginInvoke(Sub() _host?.Focus())
                Return
            Case Win32.WM_COMMAND
                If m.LParam = IntPtr.Zero Then
                    Dim id = Win32.Low(m.WParam)
                    BeginInvoke(Sub() RunCommand(id))
                    Return
                End If
        End Select
        MyBase.WndProc(m)
    End Sub

    Private Sub RunCommand(id As Integer)
        ProbeLog.Log("Hybrid menu command " & id.ToString())
        Select Case id
            Case NativeMenu.IdOpenDialog : Body.OpenDialog()
            Case NativeMenu.IdMessageBox : Body.ShowMessageBox()
            Case NativeMenu.IdSpeak : Body.SpeakOnce()
            Case NativeMenu.IdExit : Close()
            Case NativeMenu.IdAbout
                MessageBox.Show(Me, "The shell probe, a throwaway test window.", "About the probe")
        End Select
    End Sub
End Class

Option Strict On
Imports System.Windows
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Windows.Forms.Integration

''' <summary>
''' The target architecture of the research's section 6: a WPF Window, run from
''' the WinForms message loop with no main form, made keyboard-capable with
''' ElementHost.EnableModelessKeyboardInterop, carrying a native Win32 menu bar
''' on its own handle, with the menu messages forwarded from an HwndSource hook.
''' </summary>
Friend NotInheritable Class ProbeWindow
    Inherits Window

    Friend ReadOnly Body As ProbeContent
    Private ReadOnly _menu As New NativeMenu()
    Private ReadOnly _keyMenuFallback As Boolean
    Private _handle As IntPtr

    Friend ReadOnly Property Handle As IntPtr
        Get
            Return _handle
        End Get
    End Property

    Friend Sub New(keyMenuFallback As Boolean)
        _keyMenuFallback = keyMenuFallback
        Title = "JJ Shell Probe"
        Width = 800
        Height = 600
        MinWidth = 640
        MinHeight = 400
        WindowStartupLocation = WindowStartupLocation.CenterScreen

        Body = New ProbeContent()
        Body.OwnerHandle = Function() _handle
        Content = Body

        ' The window's logical focus starts on the field, so activation lands
        ' there and not on the window's root.
        FocusManager.SetFocusedElement(Me, Body.Field)

        AddHandler PreviewKeyDown, AddressOf OnPreviewKey
        AddHandler Activated, Sub(s, e) ProbeLog.Log("WPF Activated, focus " & ProbeContent.Describe(Keyboard.FocusedElement))
        AddHandler Deactivated, Sub(s, e) ProbeLog.Log("WPF Deactivated")
        AddHandler Closing, Sub(s, e) ProbeLog.Log("WPF Closing")
        AddHandler Closed, Sub(s, e) ProbeLog.Log("WPF Closed")
    End Sub

    ''' <summary>Make the handle, hook it and attach the menu, before the first show.</summary>
    Friend Sub Prepare()
        _handle = New WindowInteropHelper(Me).EnsureHandle()
        HwndSource.FromHwnd(_handle).AddHook(AddressOf Hook)
        _menu.AttachTo(_handle)
        ElementHost.EnableModelessKeyboardInterop(Me)
        ProbeLog.Log("ProbeWindow: handle " & Win32.Hex(_handle) & ", EnableModelessKeyboardInterop called, keymenu fallback " &
                     If(_keyMenuFallback, "ON", "off"))
    End Sub

    ''' <summary>Restore and bring forward: the StartupNextInstance duty.</summary>
    Friend Sub BringForward()
        If WindowState = WindowState.Minimized Then WindowState = WindowState.Normal
        Dim ok = Activate()
        ProbeLog.Log("BringForward: Activate returned " & ok.ToString() & ", foreground is " & Win32.Hex(Win32.GetForegroundWindow()))
    End Sub

    Private Sub OnPreviewKey(sender As Object, e As KeyEventArgs)
        If Not _keyMenuFallback Then Return
        ' The research's fallback: if WPF keeps Alt, F10 or an unclaimed
        ' Alt+letter from DefWindowProc, open the native menu ourselves.
        Dim pressed = If(e.Key = Key.System, e.SystemKey, e.Key)
        Dim alt = (Keyboard.Modifiers And ModifierKeys.Alt) <> 0
        Dim lParam As IntPtr = IntPtr.Zero
        If alt AndAlso pressed >= Key.A AndAlso pressed <= Key.Z Then
            lParam = New IntPtr(AscW(pressed.ToString().ToLowerInvariant()(0)))
        ElseIf pressed = Key.F10 AndAlso Keyboard.Modifiers = ModifierKeys.None Then
            lParam = IntPtr.Zero
        Else
            Return
        End If
        ProbeLog.Log("Fallback: posting SC_KEYMENU for " & pressed.ToString() & " lParam=" & lParam.ToInt64().ToString())
        Win32.PostMessage(_handle, Win32.WM_SYSCOMMAND, New IntPtr(Win32.SC_KEYMENU), lParam)
        e.Handled = True
    End Sub

    Private Function Hook(hwnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr, ByRef handled As Boolean) As IntPtr
        Dim name = Win32.Watched(msg)
        If name IsNot Nothing Then
            ProbeLog.Log("MSG " & name & " wParam=" & Win32.Hex(wParam) & " lParam=" & Win32.Hex(lParam) &
                         " GetFocus=" & Win32.Hex(Win32.GetFocus()))
        End If
        Select Case msg
            Case Win32.WM_COMMAND
                If lParam = IntPtr.Zero Then
                    Dim id = Win32.Low(wParam)
                    ' Run after the menu loop has fully unwound, as a menu command does.
                    Dispatcher.BeginInvoke(Sub() RunCommand(id))
                    handled = True
                End If
            Case Win32.WM_EXITMENULOOP
                ' Target behaviour: nothing moves focus. Only reclaim it if it
                ' really escaped the window's content.
                Dispatcher.BeginInvoke(Sub()
                                           Dim f = Keyboard.FocusedElement
                                           ProbeLog.Log("After menu loop: focus " & ProbeContent.Describe(f))
                                           If f Is Nothing Then
                                               ProbeLog.Log("After menu loop: focus escaped; reclaiming the field")
                                               Body.Field.Focus()
                                           End If
                                       End Sub)
        End Select
        Return IntPtr.Zero
    End Function

    Private Sub RunCommand(id As Integer)
        ProbeLog.Log("Menu command " & id.ToString())
        Select Case id
            Case NativeMenu.IdOpenDialog : Body.OpenDialog()
            Case NativeMenu.IdMessageBox : Body.ShowMessageBox()
            Case NativeMenu.IdSpeak : Body.SpeakOnce()
            Case NativeMenu.IdExit : Close()
            Case NativeMenu.IdAbout
                System.Windows.Forms.MessageBox.Show(New Win32Owner(_handle), "The shell probe, a throwaway test window.", "About the probe")
        End Select
    End Sub
End Class

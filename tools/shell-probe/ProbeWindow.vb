Option Strict On
Imports System.Windows
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Windows.Forms.Integration
Imports System.Windows.Automation.Peers
Imports System.Windows.Threading

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
    Private ReadOnly _opts As ProbeOptions
    Private _handle As IntPtr

    Friend ReadOnly Property Handle As IntPtr
        Get
            Return _handle
        End Get
    End Property

    Friend Sub New(opts As ProbeOptions)
        _opts = opts
        _keyMenuFallback = opts.KeyMenuFallback
        Title = "JJ Shell Probe"
        Width = 800
        Height = 600
        MinWidth = 640
        MinHeight = 400
        WindowStartupLocation = WindowStartupLocation.CenterScreen

        Body = New ProbeContent()
        Body.OwnerHandle = Function() _handle
        If opts.ChildSource Then
            ' Round 3, arm 3a: the content in its own child HWND (a child HwndSource
            ' inside an HwndHost); this Window keeps the menu bar.
            Content = New ContentHost(Body)
        Else
            Content = Body
        End If

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
        If _opts.DispatcherLoop Then
            ' Candidate 4: WPF's own Dispatcher.Run loop (no WPF Application
            ' object), so no WinForms filters, no IsDialogMessage, and no
            ' EnableModelessKeyboardInterop.
            ProbeLog.Log("ProbeWindow: handle " & Win32.Hex(_handle) & ", DISPATCHER LOOP (no WinForms loop, no interop filter)")
        Else
            Dim root As Func(Of IntPtr) = Function() _handle
            ' Instruments (behaviour-neutral, return False): FIRST in the chain,
            ' then WPF's filter, then LAST, then (round 2) the bypass.
            System.Windows.Forms.Application.AddMessageFilter(New KeyLogFilter("FIRST", root))
            ElementHost.EnableModelessKeyboardInterop(Me)
            System.Windows.Forms.Application.AddMessageFilter(New KeyLogFilter("LAST", root))
            If _opts.NoDialogKeys Then
                System.Windows.Forms.Application.AddMessageFilter(New DialogKeyBypass(root))
            End If
            ProbeLog.Log("ProbeWindow: handle " & Win32.Hex(_handle) & ", EnableModelessKeyboardInterop called")
        End If
        ProbeLog.Log("ProbeWindow: options " & _opts.Describe())
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
            Case Win32.WM_SETFOCUS
                If _opts.ChildSource Then
                    ' The Win32 focus landed on the top level; hand it to the field in the child HwndSource.
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, New Action(Sub()
                                                                                    Dim ok = Body.Field.Focus()
                                                                                    ProbeLog.Log("3a: top WM_SETFOCUS, field focus " & ok.ToString() & ", GetFocus=" & Win32.Hex(Win32.GetFocus()))
                                                                                End Sub))
                End If
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
                If _opts.FocusAfterMenu AndAlso _opts.ChildSource Then
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, New Action(Sub() ChildContent.RaiseFocusInPlace("3a")))
                ElseIf _opts.FocusAfterMenu Then
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, New Action(AddressOf RaiseFocusInPlace))
                End If
        End Select
        Return IntPtr.Zero
    End Function

    ''' <summary>
    ''' Candidate 2 of the menu research (--focus-after-menu): after the menu
    ''' loop ends, raise WPF's own focus notification for the element that
    ''' ALREADY has focus, without moving focus (so no GotKeyboardFocus handler
    ''' runs). This is the call WPF makes itself on a focus change
    ''' (AutomationPeer.RaiseFocusChangedEventHelper raises
    ''' AutomationFocusChanged on the focused element's peer).
    ''' </summary>
    Private Sub RaiseFocusInPlace()
        Dim f = TryCast(Keyboard.FocusedElement, UIElement)
        If f Is Nothing OrElse Not IsAncestorOf(f) Then
            ProbeLog.Log("FocusAfterMenu: no focused element inside the window (" & ProbeContent.Describe(Keyboard.FocusedElement) & "); nothing raised")
            Return
        End If
        Dim peer = UIElementAutomationPeer.CreatePeerForElement(f)
        If peer Is Nothing Then
            ProbeLog.Log("FocusAfterMenu: no automation peer for " & ProbeContent.Describe(f))
            Return
        End If
        Dim listening = AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged)
        peer.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged)
        ProbeLog.Log("FocusAfterMenu: raised AutomationFocusChanged on " & ProbeContent.Describe(f) &
                     " (listener exists " & listening.ToString() & ", focus not moved)")
    End Sub

    Private Sub RunCommand(id As Integer)
        ProbeLog.Log("Menu command " & id.ToString())
        Select Case id
            Case NativeMenu.IdOpenDialog : Body.OpenDialog()
            Case NativeMenu.IdMessageBox : Body.ShowMessageBox()
            Case NativeMenu.IdSpeak : ProbeContent.SpeakShared()
            Case NativeMenu.IdExit : Close()
            Case NativeMenu.IdAbout
                System.Windows.Forms.MessageBox.Show(New Win32Owner(_handle), "The shell probe, a throwaway test window.", "About the probe")
        End Select
    End Sub
End Class

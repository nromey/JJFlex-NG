Option Strict On
Imports System.Runtime.InteropServices
Imports System.Windows
Imports System.Windows.Automation.Peers
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Windows.Media

''' <summary>
''' Round 3 (arms 3a and 3b): the WPF content in its OWN child HWND, a child
''' HwndSource, so the menu bar's owner window and the content are different
''' HWNDs, as in today's WinForms shell. Round 2 found JAWS never enters
''' menu-bar mode for a bar owned by a WPF window's HWND; the arms separate
''' "the owner is a separate HWND" (3a: the owner is still the WPF Window) from
''' "the owner is not a native UIA window" (3b: a plain Win32 top level).
''' </summary>
Friend Module ChildContent
    Private Const WS_CHILD As Integer = &H40000000
    Private Const WS_VISIBLE As Integer = &H10000000
    Private Const WS_CLIPCHILDREN As Integer = &H2000000

    ''' <summary>A child HwndSource holding the content, unnamed (so the HWND carries no MSAA name of its own).</summary>
    Friend Function Create(parent As IntPtr, content As ProbeContent, width As Integer, height As Integer) As HwndSource
        Dim p As New HwndSourceParameters("") With {
            .ParentWindow = parent,
            .WindowStyle = WS_CHILD Or WS_VISIBLE Or WS_CLIPCHILDREN,
            .PositionX = 0, .PositionY = 0,
            .Width = Math.Max(width, 100), .Height = Math.Max(height, 100)}
        Dim src As New HwndSource(p)
        content.Background = Brushes.White
        src.RootVisual = content
        ProbeLog.Log("ChildContent: child HwndSource " & Win32.Hex(src.Handle) & " under " & Win32.Hex(parent))
        Return src
    End Function

    ''' <summary>Round 2's candidate 2, shared: re-announce the focused element in place.</summary>
    Friend Sub RaiseFocusInPlace(label As String)
        Dim f = TryCast(Keyboard.FocusedElement, UIElement)
        If f Is Nothing Then
            ProbeLog.Log("FocusAfterMenu (" & label & "): no focused element; nothing raised")
            Return
        End If
        Dim peer = UIElementAutomationPeer.CreatePeerForElement(f)
        If peer Is Nothing Then
            ProbeLog.Log("FocusAfterMenu (" & label & "): no automation peer for " & ProbeContent.Describe(f))
            Return
        End If
        peer.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged)
        ProbeLog.Log("FocusAfterMenu (" & label & "): raised AutomationFocusChanged on " & ProbeContent.Describe(f) & " (focus not moved)")
    End Sub
End Module

''' <summary>
''' Arm 3a's host: the WPF Window's content is this HwndHost, whose window is
''' the child HwndSource. The Window keeps the menu and stays the dialogs' owner.
''' </summary>
Friend NotInheritable Class ContentHost
    Inherits HwndHost

    Private ReadOnly _content As ProbeContent
    Friend Source As HwndSource

    Friend Sub New(content As ProbeContent)
        _content = content
    End Sub

    Protected Overrides Function BuildWindowCore(hwndParent As HandleRef) As HandleRef
        Source = ChildContent.Create(hwndParent.Handle, _content, CInt(Math.Max(ActualWidth, 400)), CInt(Math.Max(ActualHeight, 300)))
        Return New HandleRef(Me, Source.Handle)
    End Function

    Protected Overrides Sub DestroyWindowCore(hwnd As HandleRef)
        Source?.Dispose()
    End Sub
End Class

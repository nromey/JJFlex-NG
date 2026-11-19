Option Strict On
Imports System.Runtime.InteropServices
Imports System.Windows.Forms

''' <summary>
''' Round 2 (the menu). WinForms' message loop runs every key message through
''' its message filters, and then, for a window that is not a WinForms control,
''' through IsDialogMessage on the window's top-level ancestor
''' (Application.ThreadContext.PreTranslateMessage). The WPF window is not a
''' WinForms control, so every key WPF leaves unhandled reaches the dialog
''' manager. See for-claude/2026-11-19-shell-probe-menu-research.md, failure 1.
''' </summary>
Friend Module FilterNative
    Friend Const WM_KEYFIRST As Integer = &H100
    Friend Const WM_KEYLAST As Integer = &H109
    Private Const GA_ROOT As Integer = 2

    <StructLayout(LayoutKind.Sequential)>
    Friend Structure NativeMsg
        Public hwnd As IntPtr
        Public message As Integer
        Public wParam As IntPtr
        Public lParam As IntPtr
        Public time As Integer
        Public ptX As Integer
        Public ptY As Integer
    End Structure

    <DllImport("user32.dll")> Friend Function GetAncestor(hwnd As IntPtr, flags As Integer) As IntPtr
    End Function
    <DllImport("user32.dll")> Friend Function TranslateMessage(ByRef msg As NativeMsg) As Boolean
    End Function
    <DllImport("user32.dll")> Friend Function DispatchMessage(ByRef msg As NativeMsg) As IntPtr
    End Function
    <DllImport("user32.dll")> Private Function GetMessageTime() As Integer
    End Function
    <DllImport("user32.dll")> Private Function GetMessagePos() As Integer
    End Function

    Friend Function RootOf(hwnd As IntPtr) As IntPtr
        Return GetAncestor(hwnd, GA_ROOT)
    End Function

    Friend Function KeyName(msg As Integer) As String
        Select Case msg
            Case &H100 : Return "WM_KEYDOWN"
            Case &H101 : Return "WM_KEYUP"
            Case &H102 : Return "WM_CHAR"
            Case &H103 : Return "WM_DEADCHAR"
            Case &H104 : Return "WM_SYSKEYDOWN"
            Case &H105 : Return "WM_SYSKEYUP"
            Case &H106 : Return "WM_SYSCHAR"
            Case &H107 : Return "WM_SYSDEADCHAR"
            Case Else : Return "0x" & msg.ToString("X")
        End Select
    End Function

    Friend Function ToNative(ByRef m As Message) As NativeMsg
        Dim pos = GetMessagePos()
        Return New NativeMsg With {
            .hwnd = m.HWnd, .message = m.Msg, .wParam = m.WParam, .lParam = m.LParam,
            .time = GetMessageTime(),
            .ptX = CShort(pos And &HFFFF), .ptY = CShort((pos >> 16) And &HFFFF)}
    End Function
End Module

''' <summary>
''' An INSTRUMENT, behaviour-neutral: logs every key message bound for the
''' probe window and returns False. One is added before
''' EnableModelessKeyboardInterop (FIRST in the chain) and one after WPF's
''' filter (LAST, before the bypass). A WM_SYSCHAR seen first but not last means
''' WPF claimed it; seen last but never in the window's hook means the dialog
''' manager ate it.
''' </summary>
Friend NotInheritable Class KeyLogFilter
    Implements IMessageFilter

    Private ReadOnly _label As String
    Private ReadOnly _root As Func(Of IntPtr)

    Friend Sub New(label As String, root As Func(Of IntPtr))
        _label = label
        _root = root
    End Sub

    Public Function PreFilterMessage(ByRef m As Message) As Boolean Implements IMessageFilter.PreFilterMessage
        If m.Msg < WM_KEYFIRST OrElse m.Msg > WM_KEYLAST Then Return False
        Dim r = _root()
        If r = IntPtr.Zero OrElse RootOf(m.HWnd) <> r Then Return False
        ProbeLog.Log("FILTER-" & _label & " " & KeyName(m.Msg) & " wParam=" & Win32.Hex(m.WParam) &
                     " hwnd=" & Win32.Hex(m.HWnd))
        Return False
    End Function
End Class

''' <summary>
''' Candidate 1 of the menu research (--no-dialog-keys): keys WPF declined are
''' translated and dispatched here, as WPF's own loop would, and never reach
''' IsDialogMessage. Added AFTER EnableModelessKeyboardInterop, so WPF's
''' ModelessWindowFilter runs first and keeps every key WPF handles; this one
''' only ever sees what WPF left. WPF's IsRepeatedKeyboardMessage check stops
''' the dispatched copy being processed twice, as under WPF's own loop.
''' </summary>
Friend NotInheritable Class DialogKeyBypass
    Implements IMessageFilter

    Private ReadOnly _root As Func(Of IntPtr)

    Friend Sub New(root As Func(Of IntPtr))
        _root = root
    End Sub

    Public Function PreFilterMessage(ByRef m As Message) As Boolean Implements IMessageFilter.PreFilterMessage
        If m.Msg < WM_KEYFIRST OrElse m.Msg > WM_KEYLAST Then Return False
        Dim r = _root()
        If r = IntPtr.Zero OrElse RootOf(m.HWnd) <> r Then Return False
        Dim n = ToNative(m)
        TranslateMessage(n)
        DispatchMessage(n)
        ProbeLog.Log("BYPASS dispatched " & KeyName(m.Msg) & " wParam=" & Win32.Hex(m.WParam) & " (not IsDialogMessage)")
        Return True
    End Function
End Class

Option Strict On
Imports System.Runtime.InteropServices

''' <summary>
''' A small native Win32 menu bar, built exactly the way JJFlexWpf.NativeMenuBar
''' builds the product's (CreateMenu, CreatePopupMenu, AppendMenuW, SetMenu,
''' DrawMenuBar on a bare window handle).
'''
''' The real NativeMenuBar is NOT used: its only constructor takes the product's
''' JJFlexWpf.MainWindow (a 9,000-line control that expects a running app), and
''' every handler reads that window. What the gate needs from it is only the
''' HMENU-on-a-handle shape, which is what Windows and the screen readers see.
''' </summary>
Friend NotInheritable Class NativeMenu
    <DllImport("user32.dll")> Private Shared Function CreateMenu() As IntPtr
    End Function
    <DllImport("user32.dll")> Private Shared Function CreatePopupMenu() As IntPtr
    End Function
    <DllImport("user32.dll", CharSet:=CharSet.Unicode)>
    Private Shared Function AppendMenuW(hMenu As IntPtr, uFlags As UInteger, uIDNewItem As UIntPtr, lpNewItem As String) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function SetMenu(hWnd As IntPtr, hMenu As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function DrawMenuBar(hWnd As IntPtr) As Boolean
    End Function

    Private Const MF_STRING As UInteger = &H0UI
    Private Const MF_POPUP As UInteger = &H10UI
    Private Const MF_SEPARATOR As UInteger = &H800UI

    Friend Const IdOpenDialog As Integer = 101
    Friend Const IdMessageBox As Integer = 102
    Friend Const IdSpeak As Integer = 103
    Friend Const IdExit As Integer = 104
    Friend Const IdAbout As Integer = 201

    Private _bar As IntPtr

    Friend Sub AttachTo(hwnd As IntPtr)
        _bar = CreateMenu()

        Dim file = CreatePopupMenu()
        AppendMenuW(file, MF_STRING, New UIntPtr(CUInt(IdOpenDialog)), "Open &dialog")
        AppendMenuW(file, MF_STRING, New UIntPtr(CUInt(IdMessageBox)), "&Message box")
        AppendMenuW(file, MF_STRING, New UIntPtr(CUInt(IdSpeak)), "&Speak")
        AppendMenuW(file, MF_SEPARATOR, UIntPtr.Zero, Nothing)
        AppendMenuW(file, MF_STRING, New UIntPtr(CUInt(IdExit)), "E&xit")
        AppendMenuW(_bar, MF_POPUP, New UIntPtr(CULng(file.ToInt64())), "&File")

        Dim help = CreatePopupMenu()
        AppendMenuW(help, MF_STRING, New UIntPtr(CUInt(IdAbout)), "&About the probe")
        AppendMenuW(_bar, MF_POPUP, New UIntPtr(CULng(help.ToInt64())), "&Help")

        Dim ok = SetMenu(hwnd, _bar)
        DrawMenuBar(hwnd)
        ProbeLog.Log("NativeMenu: SetMenu on 0x" & hwnd.ToString("X") & " returned " & ok.ToString())
    End Sub
End Class

''' <summary>Win32 pieces shared by both shells.</summary>
Friend Module Win32
    Friend Const WM_SETFOCUS As Integer = &H7
    Friend Const WM_KILLFOCUS As Integer = &H8
    Friend Const WM_ACTIVATE As Integer = &H6
    Friend Const WM_ACTIVATEAPP As Integer = &H1C
    Friend Const WM_COMMAND As Integer = &H111
    Friend Const WM_SYSCOMMAND As Integer = &H112
    Friend Const WM_INITMENUPOPUP As Integer = &H117
    Friend Const WM_MENUSELECT As Integer = &H11F
    Friend Const WM_ENTERMENULOOP As Integer = &H211
    Friend Const WM_EXITMENULOOP As Integer = &H212
    Friend Const WM_SYSKEYDOWN As Integer = &H104
    Friend Const WM_SYSKEYUP As Integer = &H105
    Friend Const WM_SYSCHAR As Integer = &H106
    Friend Const WM_CLOSE As Integer = &H10
    Friend Const SC_KEYMENU As Integer = &HF100
    Friend Const SC_CLOSE As Integer = &HF060

    <DllImport("user32.dll")> Friend Function GetFocus() As IntPtr
    End Function
    <DllImport("user32.dll")> Friend Function GetForegroundWindow() As IntPtr
    End Function
    <DllImport("user32.dll")> Friend Function SetForegroundWindow(hWnd As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Friend Function PostMessage(hWnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Friend Function IsIconic(hWnd As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Friend Function ShowWindow(hWnd As IntPtr, nCmdShow As Integer) As Boolean
    End Function

    Friend Function Hex(p As IntPtr) As String
        Return "0x" & p.ToString("X")
    End Function

    Friend Function Low(p As IntPtr) As Integer
        Return CInt(p.ToInt64() And &HFFFFL)
    End Function

    ''' <summary>A message's name for the trace, or Nothing when it is not one we follow.</summary>
    Friend Function Watched(msg As Integer) As String
        Select Case msg
            Case WM_ACTIVATE : Return "WM_ACTIVATE"
            Case WM_ACTIVATEAPP : Return "WM_ACTIVATEAPP"
            Case WM_SETFOCUS : Return "WM_SETFOCUS"
            Case WM_KILLFOCUS : Return "WM_KILLFOCUS"
            Case WM_SYSCOMMAND : Return "WM_SYSCOMMAND"
            Case WM_SYSKEYDOWN : Return "WM_SYSKEYDOWN"
            Case WM_SYSKEYUP : Return "WM_SYSKEYUP"
            Case WM_SYSCHAR : Return "WM_SYSCHAR"
            Case WM_ENTERMENULOOP : Return "WM_ENTERMENULOOP"
            Case WM_EXITMENULOOP : Return "WM_EXITMENULOOP"
            Case WM_INITMENUPOPUP : Return "WM_INITMENUPOPUP"
            Case WM_COMMAND : Return "WM_COMMAND"
            Case WM_CLOSE : Return "WM_CLOSE"
            Case Else : Return Nothing
        End Select
    End Function
End Module

''' <summary>
''' A bare window handle as a WinForms owner, which is how the WPF shell will
''' have to own message boxes (and the SmartLink login form).
''' </summary>
Friend NotInheritable Class Win32Owner
    Implements System.Windows.Forms.IWin32Window

    Private ReadOnly _h As IntPtr

    Friend Sub New(h As IntPtr)
        _h = h
    End Sub

    Public ReadOnly Property Handle As IntPtr Implements System.Windows.Forms.IWin32Window.Handle
        Get
            Return _h
        End Get
    End Property
End Class

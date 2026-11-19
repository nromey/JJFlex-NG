Option Strict On
Imports System.Runtime.InteropServices

''' <summary>
''' Round 2's reference (--raw): a plain Win32 top-level window carrying the same
''' HMENU, with a standard Edit child labelled like Home's field, on a bare
''' GetMessage / TranslateMessage / DispatchMessage loop. No WinForms loop, no
''' WPF, no IsDialogMessage. It is what NVDA and JAWS do with an untouched
''' Win32 menu, so the WPF window is judged against Windows itself rather than
''' against the hybrid (which carries the hop and WinForms' UIA provider).
''' Menu only: Tab and dialogs are not part of this shell.
''' </summary>
Friend NotInheritable Class RawShell
    Private Delegate Function WndProcFn(hwnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Private Structure WNDCLASSEX
        Public cbSize As Integer
        Public style As Integer
        Public lpfnWndProc As IntPtr
        Public cbClsExtra As Integer
        Public cbWndExtra As Integer
        Public hInstance As IntPtr
        Public hIcon As IntPtr
        Public hCursor As IntPtr
        Public hbrBackground As IntPtr
        Public lpszMenuName As String
        Public lpszClassName As String
        Public hIconSm As IntPtr
    End Structure

    <DllImport("user32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function RegisterClassExW(ByRef wc As WNDCLASSEX) As UShort
    End Function
    <DllImport("user32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function CreateWindowExW(exStyle As Integer, className As String, windowName As String, style As Integer,
                                            x As Integer, y As Integer, w As Integer, h As Integer,
                                            parent As IntPtr, menu As IntPtr, inst As IntPtr, param As IntPtr) As IntPtr
    End Function
    <DllImport("user32.dll")> Private Shared Function DefWindowProcW(h As IntPtr, m As Integer, w As IntPtr, l As IntPtr) As IntPtr
    End Function
    <DllImport("user32.dll")> Private Shared Function GetMessageW(ByRef m As NativeMsg, h As IntPtr, a As Integer, b As Integer) As Integer
    End Function
    <DllImport("user32.dll")> Private Shared Function DispatchMessageW(ByRef m As NativeMsg) As IntPtr
    End Function
    <DllImport("user32.dll")> Private Shared Sub PostQuitMessage(code As Integer)
    End Sub
    <DllImport("user32.dll")> Private Shared Function SetFocus(h As IntPtr) As IntPtr
    End Function
    <DllImport("user32.dll")> Private Shared Function DestroyWindow(h As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function UpdateWindow(h As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function LoadCursorW(inst As IntPtr, id As IntPtr) As IntPtr
    End Function
    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode)> Private Shared Function GetModuleHandleW(name As String) As IntPtr
    End Function

    Private Const WS_OVERLAPPEDWINDOW As Integer = &HCF0000
    Private Const WS_CHILD As Integer = &H40000000
    Private Const WS_VISIBLE As Integer = &H10000000
    Private Const WS_BORDER As Integer = &H800000
    Private Const WS_TABSTOP As Integer = &H10000
    Private Const ES_AUTOHSCROLL As Integer = &H80
    Private Const CW_USEDEFAULT As Integer = Integer.MinValue
    Private Const WM_DESTROY As Integer = &H2

    ' Held for the life of the process: the window class points at it.
    Private Shared _proc As WndProcFn
    Private _hwnd As IntPtr
    Private _edit As IntPtr

    Friend ReadOnly Property Handle As IntPtr
        Get
            Return _hwnd
        End Get
    End Property

    ''' <summary>Create the window and run the bare loop until it is destroyed.</summary>
    Friend Sub Run()
        Dim inst = GetModuleHandleW(Nothing)
        _proc = AddressOf WndProc
        Dim wc As New WNDCLASSEX With {
            .cbSize = Marshal.SizeOf(GetType(WNDCLASSEX)),
            .lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            .hInstance = inst,
            .hCursor = LoadCursorW(IntPtr.Zero, New IntPtr(32512)),
            .hbrBackground = New IntPtr(6),
            .lpszClassName = "JJShellProbeRaw"}
        Dim atom = RegisterClassExW(wc)
        ProbeLog.Log("RawShell: RegisterClassEx atom " & atom.ToString() & " err " & Marshal.GetLastWin32Error().ToString())

        _hwnd = CreateWindowExW(0, "JJShellProbeRaw", "JJ Shell Probe", WS_OVERLAPPEDWINDOW,
                                CW_USEDEFAULT, CW_USEDEFAULT, 800, 600, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero)
        Dim menu As New NativeMenu()
        menu.AttachTo(_hwnd)
        CreateWindowExW(0, "STATIC", "Home, frequency and VFO", WS_CHILD Or WS_VISIBLE,
                        10, 10, 300, 20, _hwnd, IntPtr.Zero, inst, IntPtr.Zero)
        _edit = CreateWindowExW(0, "EDIT", "A  14.100.000  USB  S5", WS_CHILD Or WS_VISIBLE Or WS_BORDER Or WS_TABSTOP Or ES_AUTOHSCROLL,
                                10, 34, 300, 24, _hwnd, New IntPtr(1), inst, IntPtr.Zero)
        ProbeLog.Log("RawShell: handle " & Win32.Hex(_hwnd) & ", edit " & Win32.Hex(_edit) & " (bare GetMessage loop, no IsDialogMessage)")
        Win32.ShowWindow(_hwnd, 5)
        UpdateWindow(_hwnd)
        Win32.SetForegroundWindow(_hwnd)

        Dim m As NativeMsg
        While GetMessageW(m, IntPtr.Zero, 0, 0) > 0
            TranslateMessage(m)
            DispatchMessageW(m)
        End While
        ProbeLog.Log("RawShell: loop ended")
    End Sub

    Friend Sub BringForward()
        If Win32.IsIconic(_hwnd) Then Win32.ShowWindow(_hwnd, 9)
        Dim ok = Win32.SetForegroundWindow(_hwnd)
        ProbeLog.Log("BringForward (raw): SetForegroundWindow returned " & ok.ToString())
    End Sub

    Private Function WndProc(hwnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
        Try
            Dim name = Win32.Watched(msg)
            If name IsNot Nothing Then
                ProbeLog.Log("RMSG " & name & " wParam=" & Win32.Hex(wParam) & " lParam=" & Win32.Hex(lParam) &
                             " GetFocus=" & Win32.Hex(Win32.GetFocus()))
            End If
            Select Case msg
                Case Win32.WM_SETFOCUS
                    If _edit <> IntPtr.Zero Then SetFocus(_edit)
                    Return IntPtr.Zero
                Case Win32.WM_COMMAND
                    If lParam = IntPtr.Zero Then
                        RunCommand(Win32.Low(wParam))
                        Return IntPtr.Zero
                    End If
                Case WM_DESTROY
                    PostQuitMessage(0)
                    Return IntPtr.Zero
            End Select
        Catch ex As Exception
            ProbeLog.Log("RawShell WndProc error: " & ex.ToString())
        End Try
        Return DefWindowProcW(hwnd, msg, wParam, lParam)
    End Function

    Private Sub RunCommand(id As Integer)
        ProbeLog.Log("Raw menu command " & id.ToString())
        Select Case id
            Case NativeMenu.IdSpeak : ProbeContent.SpeakShared()
            Case NativeMenu.IdExit : DestroyWindow(_hwnd)
            Case NativeMenu.IdMessageBox, NativeMenu.IdAbout
                System.Windows.Forms.MessageBox.Show(New Win32Owner(_hwnd),
                    "This is the probe's message box. Press Enter to close it.", "Probe message box")
            Case NativeMenu.IdOpenDialog
                ProbeLog.Log("Raw: no WPF dialog in the raw baseline")
        End Select
    End Sub
End Class

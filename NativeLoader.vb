Imports System.IO
Imports System.Reflection
Imports System.Runtime.InteropServices

''' <summary>
''' Resolves native library paths based on processor architecture.
''' Loads native DLLs from runtimes\win-x86 or win-x64\native folder.
''' </summary>
Public Module NativeLoader
    Private _initialized As Boolean = False

    ''' <summary>
    ''' Initializes the native library resolver for PortAudio and Opus.
    ''' Call this early in application startup, before any audio operations.
    ''' </summary>
    Public Sub Initialize()
        If _initialized Then Return

        Try
            ' Set resolver for PortAudioSharp assembly
            NativeLibrary.SetDllImportResolver(
                GetType(PortAudioSharp.PortAudio).Assembly,
                AddressOf ResolveNativeLibrary)

            ' Set resolver for POpusCodec assembly
            NativeLibrary.SetDllImportResolver(
                GetType(POpusCodec.OpusDecoder).Assembly,
                AddressOf ResolveNativeLibrary)

            _initialized = True
        Catch ex As Exception
            ' Log but don't throw - let default resolution attempt to work
            System.Diagnostics.Trace.WriteLine($"NativeLoader.Initialize warning: {ex.Message}")
        End Try
    End Sub

    Private Function ResolveNativeLibrary(
            libraryName As String,
            assembly As Assembly,
            searchPath As DllImportSearchPath?) As IntPtr

        Dim arch As String = If(Environment.Is64BitProcess, "x64", "x86")
        Dim basePath As String = AppContext.BaseDirectory

        ' Map library names to actual file names
        Dim mappedName As String
        Select Case libraryName.ToLowerInvariant()
            Case "portaudio.dll", "portaudio"
                mappedName = "portaudio.dll"
            Case "libopus.dll", "libopus", "opus"
                mappedName = "libopus.dll"
            Case Else
                mappedName = libraryName
        End Select

        ' Try architecture-specific runtimes folder first
        Dim runtimePath As String = Path.Combine(basePath, "runtimes", $"win-{arch}", "native", mappedName)
        If File.Exists(runtimePath) Then
            Dim handle As IntPtr
            If NativeLibrary.TryLoad(runtimePath, handle) Then
                Return handle
            End If
        End If

        ' Fall back to app directory (legacy location)
        Dim legacyPath As String = Path.Combine(basePath, mappedName)
        If File.Exists(legacyPath) Then
            Dim legacyHandle As IntPtr
            If NativeLibrary.TryLoad(legacyPath, legacyHandle) Then
                Return legacyHandle
            End If
        End If

        ' Return zero to let default search continue
        Return IntPtr.Zero
    End Function
End Module

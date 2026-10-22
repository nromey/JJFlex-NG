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

            ' Set resolver for the Radios assembly, which is where PrismNative's
            ' DllImports live. SetDllImportResolver is PER ASSEMBLY: without
            ' this, prism.dll is never looked for in runtimes\win-{arch}\native
            ' at all, the default probe fails, and speech silently does not come
            ' up. Registering the two audio assemblies and forgetting this one
            ' is exactly the kind of gap that reads as "Prism does not work".
            NativeLibrary.SetDllImportResolver(
                GetType(Radios.ScreenReaderOutput).Assembly,
                AddressOf ResolveNativeLibrary)

            ' Computer-recording playback decodes FLAC in JJFlexWpf.
            NativeLibrary.SetDllImportResolver(
                GetType(JJFlexWpf.RecordingPlayer).Assembly,
                AddressOf ResolveNativeLibrary)

            ' DeepFilterNet3 (#731, Sprint 48 Track G) is NOT a DllImport, so it
            ' takes no resolver: the engine loads deepfilter.dll by absolute
            ' path and binds its entry points from that handle, because its C
            ' API aborts the process on a bad model path and must never be
            ' reached by a search-order surprise. This module still decides
            ' WHERE it lives, the same way it does for every other native
            ' library; the engine asks through this locator.
            WireDeepFilterLocator()

            _initialized = True
        Catch ex As Exception
            ' Log but don't throw - let default resolution attempt to work
            System.Diagnostics.Trace.WriteLine($"NativeLoader.Initialize warning: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Kept out of Initialize's own body so that the JJFlexWpf reference is
    ''' resolved when this runs, inside Initialize's Try, and not when
    ''' Initialize itself is compiled on the first line of the process.
    ''' </summary>
    <System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>
    Private Sub WireDeepFilterLocator()
        JJFlexWpf.DeepFilterNet3Engine.LibraryLocator = AddressOf LocateDeepFilter
    End Sub

    Private Function ResolveNativeLibrary(
            libraryName As String,
            assembly As Assembly,
            searchPath As DllImportSearchPath?) As IntPtr

        Dim arch As String = If(Environment.Is64BitProcess, "x64", "x86")
        Dim basePath As String = AppContext.BaseDirectory

        Dim mappedName As String = MappedFileName(libraryName)

        ' Try architecture-specific runtimes folder first
        Dim runtimePath As String = Path.Combine(basePath, "runtimes", $"win-{arch}", "native", mappedName)
        If File.Exists(runtimePath) Then
            Dim handle As IntPtr
            If NativeLibrary.TryLoad(runtimePath, handle) Then
                Return handle
            End If
        End If

        ' Fall back to app directory (legacy location).
        '
        ' This path must never be taken silently. Until 2026-08-12 three
        ' projects copied their own bundled natives here — PortAudio
        ' V19.6.0-devel from 2016, shadowing the current build in runtimes\ —
        ' so a failure above would have quietly loaded a decade-old library
        ' with no announcement and subtly different device behaviour. Those
        ' copies are gone, but the fallback stays for legacy installs, and it
        ' now says so loudly. If you ever see this line in a trace, the
        ' runtimes\ layout is broken and the version actually in use is
        ' unknown — check it with Pa_GetVersionText / opus_get_version_string.
        Dim legacyPath As String = Path.Combine(basePath, mappedName)
        If File.Exists(legacyPath) Then
            Dim legacyHandle As IntPtr
            If NativeLibrary.TryLoad(legacyPath, legacyHandle) Then
                JJTrace.Tracing.TraceLine(
                    "NativeLoader: WARNING — loaded " & mappedName &
                    " from the app directory, NOT runtimes\win-" & arch &
                    "\native. The expected library is missing or failed to " &
                    "load, so the version in use is unverified.",
                    TraceLevel.Error)
                Return legacyHandle
            End If
        End If

        ' Return zero to let default search continue
        Return IntPtr.Zero
    End Function

    ''' <summary>
    ''' Map a library name as code asks for it to the file actually shipped.
    ''' </summary>
    Private Function MappedFileName(libraryName As String) As String
        Select Case libraryName.ToLowerInvariant()
            Case "portaudio.dll", "portaudio"
                Return "portaudio.dll"
            Case "libopus.dll", "libopus", "opus"
                Return "libopus.dll"
            Case "libflac.dll", "libflac", "flac"
                Return "libFLAC.dll"
            Case "prism.dll", "prism"
                ' Screen-reader and braille output (ethindp/prism). Talks to
                ' NVDA, JAWS and SAPI itself - one file, no per-reader clients.
                Return "prism.dll"
            Case "deepfilter.dll", "deepfilter", "df.dll", "df"
                ' DeepFilterNet3's C API, built from upstream as df.dll and
                ' shipped renamed (see runtimes\win-x64\native\README.md).
                Return "deepfilter.dll"
            Case Else
                Return libraryName
        End Select
    End Function

    ''' <summary>
    ''' Absolute path of this architecture's deepfilter.dll, or Nothing when it
    ''' is not there. No app-directory fallback: the library is new, so there
    ''' is no legacy location to honour, and a stray copy elsewhere is exactly
    ''' what must not be loaded.
    ''' </summary>
    Private Function LocateDeepFilter() As String
        Dim arch As String = If(Environment.Is64BitProcess, "x64", "x86")
        Dim p As String = Path.Combine(AppContext.BaseDirectory, "runtimes", $"win-{arch}", "native",
                                       MappedFileName("deepfilter.dll"))
        Return If(File.Exists(p), p, Nothing)
    End Function
End Module

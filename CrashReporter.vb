Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports System.Diagnostics
Imports JJTrace

Module CrashReporter

    ''' <summary>
    ''' Endpoint for crash bundle uploads. Standalone constant so it's trivial
    ''' to override in a test deploy or staging environment. Server is the
    ''' rarbox FastAPI receiver per memory/project_crash_triage_bundle_flow.md
    ''' (LIVE since 2026-05-08 11:51:34 CDT per Agent.md 05-08 seal).
    ''' </summary>
    Private Const CrashEndpoint As String = "https://crashes.jjflexible.radio/crashes"

    ''' <summary>
    ''' How many recent trace archives to include in each crash bundle. 3 is
    ''' the design memo number — covers the crashed session itself plus the
    ''' two before, so the triage agent can spot pre-crash patterns.
    ''' </summary>
    Private Const RecentTracesInBundle As Integer = 3

    ''' <summary>
    ''' Maximum number of upload attempts before giving up. Transient network
    ''' failures (timeouts, 5xx server errors) get retried; 4xx (client errors)
    ''' don't because retrying won't change the outcome.
    ''' </summary>
    Private Const MaxUploadAttempts As Integer = 3

    ''' <summary>
    ''' Reused HttpClient for crash bundle uploads. Static-style instance per
    ''' the .NET HttpClient guidance — repeated New HttpClient() risks socket
    ''' exhaustion. One per Module is fine here since uploads are infrequent
    ''' and never concurrent (one crash → one bundle → one upload).
    ''' </summary>
    Private ReadOnly SharedHttpClient As New HttpClient() With {
        .Timeout = TimeSpan.FromSeconds(30)
    }
    ' Catch WinForms UI exceptions.
    Public Sub OnThreadException(sender As Object, e As ThreadExceptionEventArgs)
        SaveCrash("UI thread exception", e.Exception, False)
    End Sub

    ' Catch non-UI exceptions.
    Public Sub OnUnhandledException(sender As Object, e As UnhandledExceptionEventArgs)
        Dim ex As Exception = TryCast(e.ExceptionObject, Exception)
        If ex Is Nothing Then
            ex = New Exception("Unhandled exception (non-Exception object): " & e.ExceptionObject?.ToString())
        End If
        SaveCrash("Unhandled domain exception", ex, e.IsTerminating)
    End Sub

    ' Catch WPF Dispatcher exceptions (event handlers, Dispatcher.BeginInvoke
    ' callbacks, deferred work). Without this, WPF dispatcher exceptions fall
    ' through to OnUnhandledException above which terminates the process; with
    ' it, we save the report AND set e.Handled = True so the app stays alive
    ' (matching the soft-recover behaviour of OnThreadException for WinForms).
    ' The user gets the standard crash-report MessageBox and can choose whether
    ' to keep using the app or restart.
    '
    ' This pattern explicitly does NOT silence the crash — the report is still
    ' written, the screen reader still announces, the MessageBox still shows.
    ' It just prevents the WPF exception from cascading into the AppDomain
    ' handler (which would write a duplicate report) and from terminating
    ' the process unconditionally.
    Public Sub OnDispatcherUnhandledException(sender As Object,
                                              e As System.Windows.Threading.DispatcherUnhandledExceptionEventArgs)
        SaveCrash("WPF dispatcher exception", e.Exception, False)
        e.Handled = True
    End Sub

    Private Sub SaveCrash(context As String, ex As Exception, isTerminating As Boolean)
        Try
            Dim baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JJFlexRadio", "Errors")
            Directory.CreateDirectory(baseDir)

            Dim stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss")
            Dim txtPath = Path.Combine(baseDir, $"JJFlexError-{stamp}.txt")
            Dim dmpPath = Path.Combine(baseDir, $"JJFlexError-{stamp}.dmp")
            Dim zipPath = Path.Combine(baseDir, $"JJFlexError-{stamp}.zip")

            File.WriteAllText(txtPath, BuildReport(context, ex, isTerminating), Encoding.UTF8)
            WriteMiniDump(dmpPath)

            ' Sprint 29 Track C: collect recent trace archives so the triage
            ' agent can correlate the crash with what the prior sessions
            ' looked like. Per memory/project_user_initiated_feedback_session.md.
            Dim recentTraces As List(Of String) = GetRecentTraceArchives(RecentTracesInBundle)

            Using zipStream = New FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None)
                Using zip = New ZipArchive(zipStream, ZipArchiveMode.Create)
                    zip.CreateEntryFromFile(txtPath, Path.GetFileName(txtPath), CompressionLevel.Optimal)
                    If File.Exists(dmpPath) Then
                        zip.CreateEntryFromFile(dmpPath, Path.GetFileName(dmpPath), CompressionLevel.Optimal)
                    End If
                    For Each tracePath As String In recentTraces
                        Try
                            If File.Exists(tracePath) Then
                                zip.CreateEntryFromFile(tracePath,
                                    "traces/" & Path.GetFileName(tracePath),
                                    CompressionLevel.NoCompression) ' already LZMA-compressed
                            End If
                        Catch
                            ' Best-effort — a single unreadable trace shouldn't fail the bundle.
                        End Try
                    Next
                End Using
            End Using

            Radios.ScreenReaderOutput.Speak(
                "JJ Flexible Radio Access hit an unexpected error. A crash report was saved.",
                Radios.VerbosityLevel.Critical, True)

            ' Per project_no_silent_phone_home.md: the bundle is local until
            ' the user explicitly chooses to upload. Show what's in the report,
            ' offer Yes/No, send only on Yes.
            PromptToUploadCrashBundle(zipPath, recentTraces)
        Catch reportEx As Exception
            ' Last-chance logging; do not rethrow.
            Try
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "JJFlexRadio-crash.txt"),
                                   $"{DateTime.Now:u} Failed to write crash report: {reportEx}{Environment.NewLine}")
            Catch
            End Try
        End Try
    End Sub

    ''' <summary>
    ''' Returns the file paths of the most recent N trace archives, ordered
    ''' most-recent-first. Pulls from the trace manifest at TraceArchiveDir
    ''' so we don't double-resolve filename → path. Returns an empty list
    ''' if the manifest doesn't exist or fails to read; never throws.
    ''' </summary>
    Private Function GetRecentTraceArchives(maxCount As Integer) As List(Of String)
        Dim result As New List(Of String)
        Try
            Dim manifestPath As String = Path.Combine(TraceArchiveDir, SessionArchive.ManifestFileName)
            If Not File.Exists(manifestPath) Then Return result

            Dim manifest As TraceManifest = TraceManifest.Load(manifestPath)
            If manifest Is Nothing OrElse manifest.Entries Is Nothing Then Return result

            Dim ordered = manifest.Entries _
                .Where(Function(e) Not String.IsNullOrEmpty(e.Filename)) _
                .OrderByDescending(Function(e) e.BootTime) _
                .Take(maxCount)

            For Each entry In ordered
                Dim fullPath As String = Path.Combine(TraceArchiveDir,
                    entry.Filename.Replace("/"c, Path.DirectorySeparatorChar))
                result.Add(fullPath)
            Next
        Catch
            ' Best-effort — failure to enumerate traces shouldn't block crash report.
        End Try
        Return result
    End Function

    ''' <summary>
    ''' Show the user what's in the crash bundle and offer to upload it. Only
    ''' POSTs to the receiver if they choose Yes. Honors the no-silent-phone-home
    ''' principle: nothing leaves the user's machine without explicit consent.
    ''' </summary>
    Private Sub PromptToUploadCrashBundle(zipPath As String, recentTraces As List(Of String))
        Try
            Dim sb As New StringBuilder()
            sb.AppendLine("JJ Flexible Radio Access hit an unexpected error.")
            sb.AppendLine()
            sb.AppendLine("A crash report was saved to:")
            sb.AppendLine(zipPath)
            sb.AppendLine()
            sb.AppendLine("The report contains:")
            sb.AppendLine($"  - Exception details ({FormatSize(zipPath)} total)")
            sb.AppendLine("  - Process minidump")
            If recentTraces.Count > 0 Then
                sb.AppendLine($"  - {recentTraces.Count} recent trace archive(s) for context")
            End If
            sb.AppendLine()
            sb.AppendLine("Send this report to the JJ Flexible Data Provider?")
            sb.AppendLine($"It will upload to {CrashEndpoint}")

            Dim choice = MessageBox.Show(AppShellForm, sb.ToString(),
                "JJ Flexible Radio Access crash report",
                MessageBoxButtons.YesNo, MessageBoxIcon.Error)

            If choice = DialogResult.Yes Then
                ' Fire-and-forget upload. The user has consented; we don't block
                ' the UI on a network round-trip. Result is announced via
                ' screen reader when the POST returns. Discard the Task to
                ' silence the unawaited-Task warning — UploadCrashBundleAsync
                ' is already async and self-pumping; an outer Task.Run wrapper
                ' would only add a redundant thread-pool bounce.
                Dim _ignored = UploadCrashBundleAsync(zipPath)
            Else
                Radios.ScreenReaderOutput.Speak(
                    "Crash report kept local. Not uploaded.",
                    Radios.VerbosityLevel.Critical, True)
            End If
        Catch promptEx As Exception
            Try
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "JJFlexRadio-crash.txt"),
                    $"{DateTime.Now:u} PromptToUploadCrashBundle failed: {promptEx}{Environment.NewLine}")
                ' If MessageBox itself failed, the user heard "crash report saved"
                ' from the SaveCrash speech but nothing about the upload offer.
                ' Tell them so they can manually retry / mail the bundle.
                Radios.ScreenReaderOutput.Speak(
                    "Crash report saved locally. Couldn't show the upload prompt.",
                    Radios.VerbosityLevel.Critical, True)
            Catch
            End Try
        End Try
    End Sub

    ''' <summary>
    ''' POST the crash bundle to the receiver as multipart/form-data with a
    ''' single 'file' field per the F3-G server contract. Retries up to
    ''' MaxUploadAttempts on transient failures (timeouts, 5xx). Does NOT retry
    ''' on 4xx — client errors mean the bundle is rejected; retrying won't
    ''' help. Speaks the final outcome via screen reader. Diagnostic detail
    ''' (status codes, exception names) goes to the temp log, never to the
    ''' user-facing speech. Best-effort — never throws.
    ''' </summary>
    Private Async Function UploadCrashBundleAsync(zipPath As String) As Task
        Dim lastError As String = "unknown"

        For attempt As Integer = 1 To MaxUploadAttempts
            Try
                Using fs As New FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Using form As New MultipartFormDataContent()
                        Dim fileContent As New StreamContent(fs)
                        fileContent.Headers.ContentType =
                            New System.Net.Http.Headers.MediaTypeHeaderValue("application/zip")
                        form.Add(fileContent, "file", Path.GetFileName(zipPath))

                        Using response As HttpResponseMessage = Await SharedHttpClient.PostAsync(CrashEndpoint, form)
                            If response.IsSuccessStatusCode Then
                                Radios.ScreenReaderOutput.Speak(
                                    "Crash report uploaded successfully. Thank you.",
                                    Radios.VerbosityLevel.Critical, True)
                                Return
                            End If

                            lastError = $"status {CInt(response.StatusCode)} {response.ReasonPhrase}"

                            ' 4xx is permanent (bad request, payload too large, auth) —
                            ' retrying won't change the outcome. Bail out of the loop.
                            If CInt(response.StatusCode) < 500 Then Exit For
                            ' 5xx is potentially transient — fall through to retry path.
                        End Using
                    End Using
                End Using
            Catch ex As TaskCanceledException
                ' HttpClient.Timeout produces TaskCanceledException, not TimeoutException.
                ' Treat as transient.
                lastError = "timeout"
            Catch ex As HttpRequestException
                ' Network-layer failure (DNS, refused, reset). Retry.
                lastError = $"{ex.GetType().Name}: {ex.Message}"
            Catch ex As Exception
                ' Unexpected — log and stop retrying. Likely a programming error,
                ' not transient.
                lastError = $"unexpected {ex.GetType().Name}: {ex.Message}"
                Exit For
            End Try

            If attempt < MaxUploadAttempts Then
                ' Backoff: 2s, then 4s. Total worst-case extra wait = 6s on top
                ' of three 30s timeouts = ~96s before user hears the failure.
                Try
                    Await Task.Delay(TimeSpan.FromSeconds(2 * attempt))
                Catch
                End Try
            End If
        Next

        ' All attempts exhausted (or hit a permanent error).
        Try
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "JJFlexRadio-crash.txt"),
                $"{DateTime.Now:u} UploadCrashBundleAsync failed after {MaxUploadAttempts} attempt(s): {lastError}{Environment.NewLine}")
            Radios.ScreenReaderOutput.Speak(
                "Crash report upload failed. The report is still saved locally.",
                Radios.VerbosityLevel.Critical, True)
        Catch
        End Try
    End Function

    Private Function FormatSize(filePath As String) As String
        Try
            Dim bytes As Long = New FileInfo(filePath).Length
            If bytes < 1024 Then Return $"{bytes} bytes"
            If bytes < 1024 * 1024 Then Return $"{bytes \ 1024} KB"
            Return $"{bytes \ (1024 * 1024)} MB"
        Catch
            Return "size unknown"
        End Try
    End Function

    Private Function BuildReport(context As String, ex As Exception, isTerminating As Boolean) As String
        Dim sb As New StringBuilder()
        sb.AppendLine("JJ Flexible Radio Access Crash Report")
        sb.AppendLine($"When: {DateTime.Now:u}")
        sb.AppendLine($"Context: {context}")
        sb.AppendLine($"Terminating: {isTerminating}")
        Try
            Dim asm = GetType(Form).Assembly.GetName()
            sb.AppendLine($"App: {asm.Name} {asm.Version}")
        Catch
        End Try
        sb.AppendLine($"OS: {Environment.OSVersion}")
        sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}")
        sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}")
        sb.AppendLine("Exception:")
        sb.AppendLine(ex.ToString())
        Return sb.ToString()
    End Function

    ' MiniDumpWriter based on dbghelp.dll
    Private Sub WriteMiniDump(path As String)
        Try
            Using fs As New FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
                Dim proc = Process.GetCurrentProcess()
                MiniDumpWriteDump(proc.Handle, proc.Id, fs.SafeFileHandle.DangerousGetHandle(), MiniDumpType.WithFullMemory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
            End Using
        Catch
        End Try
    End Sub

    <DllImport("dbghelp.dll", SetLastError:=True)>
    Private Function MiniDumpWriteDump(hProcess As IntPtr, processId As Integer, hFile As IntPtr, dumpType As MiniDumpType, exceptionParam As IntPtr, userStreamParam As IntPtr, callbackParam As IntPtr) As Boolean
    End Function

    <Flags>
    Private Enum MiniDumpType As Integer
        Normal = &H0
        WithDataSegs = &H1
        WithFullMemory = &H2
        WithHandleData = &H4
        FilterMemory = &H8
        ScanMemory = &H10
        WithUnloadedModules = &H20
        WithIndirectlyReferencedMemory = &H40
        FilterModulePaths = &H80
        WithProcessThreadData = &H100
        WithPrivateReadWriteMemory = &H200
        WithoutOptionalData = &H400
        WithFullMemoryInfo = &H800
        WithThreadInfo = &H1000
        WithCodeSegs = &H2000
        WithoutAuxiliaryState = &H4000
        WithFullAuxiliaryState = &H8000
        WithPrivateWriteCopyMemory = &H10000
        IgnoreInaccessibleMemory = &H20000
        WithTokenInformation = &H40000
        WithModuleHeaders = &H80000
        FilterTriage = &H100000
        ValidTypeFlags = &H1FFFFF
    End Enum
End Module

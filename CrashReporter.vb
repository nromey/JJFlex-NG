Imports System.IO
Imports System.IO.Compression
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Windows.Forms
Imports System.Diagnostics

Module CrashReporter
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

            Using zipStream = New FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None)
                Using zip = New ZipArchive(zipStream, ZipArchiveMode.Create)
                    zip.CreateEntryFromFile(txtPath, Path.GetFileName(txtPath), CompressionLevel.Optimal)
                    If File.Exists(dmpPath) Then
                        zip.CreateEntryFromFile(dmpPath, Path.GetFileName(dmpPath), CompressionLevel.Optimal)
                    End If
                End Using
            End Using

            MessageBox.Show($"JJFlexRadio hit an unexpected error.{Environment.NewLine}A report was saved to:{Environment.NewLine}{zipPath}", "JJFlexRadio crash report", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Catch reportEx As Exception
            ' Last-chance logging; do not rethrow.
            Try
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "JJFlexRadio-crash.txt"),
                                   $"{DateTime.Now:u} Failed to write crash report: {reportEx}{Environment.NewLine}")
            Catch
            End Try
        End Try
    End Sub

    Private Function BuildReport(context As String, ex As Exception, isTerminating As Boolean) As String
        Dim sb As New StringBuilder()
        sb.AppendLine("JJFlexRadio Crash Report")
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

Imports System.IO
Imports System.Windows.Forms
Imports System.IO.Compression
Imports JJTrace

Friend Class DebugInfo
    Private Const openDialogTitle As String = "Debug info archive"
    Private Const mustHaveFile As String = "You must specify a debug file."
    Private Const infoGathered As String = "Debug info gathered."

    ''' <summary>
    ''' How many archived trace SESSIONS ride along in the debug bundle
    ''' (newest part of each, most recent first — the crash bundle's
    ''' selection logic). The archive directory holds up to 30 days of
    ''' sessions and used to go into the bundle whole, which could dwarf
    ''' everything else in it (QB Track L bounding).
    ''' </summary>
    Private Const RecentTraceSessionsInBundle As Integer = 5

    ''' <summary>
    ''' Honest failure text for the debug archive. This routine builds a bundle
    ''' out of whatever is in AppData, so it is exposed to the same size class
    ''' that produced an unexplained framework dialog about a stream of that
    ''' size — it ran with no Try/Catch at all, so any such failure escaped as a
    ''' raw exception. It now says what happened and what the user can do,
    ''' rather than surfacing framework text or (worse) nothing.
    ''' </summary>
    Private Const gatherFailed As String =
        "The debug archive could not be completed." & vbCrLf & vbCrLf &
        "This usually means there wasn't room for it, or a file in the settings" & vbCrLf &
        "folder was too large to include. Nothing was lost — your settings and" & vbCrLf &
        "traces are untouched. Try again with a different destination, or send" & vbCrLf &
        "the most recent trace from the Trace Archive instead."

    Friend Shared Sub GetDebugInfo()
        Dim openDialog = New OpenFileDialog()
        openDialog.AddExtension = True
        openDialog.CheckFileExists = False
        openDialog.DefaultExt = "zip"
        openDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        openDialog.Title = openDialogTitle
        If openDialog.ShowDialog() <> DialogResult.OK Then
            openDialog.Dispose()
            MessageBox.Show(mustHaveFile, ErrorHdr, MessageBoxButtons.OK)
            Return
        End If

        If Tracing.On Then
            Tracing.TraceLine("GetDebugInfo:Tracing turned off")
            Tracing.On = False
        End If

        Try
            File.Delete(openDialog.FileName)
            Using archive As ZipArchive = ZipFile.Open(openDialog.FileName, ZipArchiveMode.Create)
                ' get application data — minus the archived trace sessions.
                ' The Traces directory holds up to 30 days of per-session
                ' zips; whole, it can dwarf everything else in the bundle.
                ' The most recent sessions are added back individually below.
                ZipUtils.AddDirectoryToArchive(archive, BaseConfigDir, ProgramName, "trace-*.zip")

                ' The most recent trace sessions (newest part of each), at
                ' their real Traces/yyyy/MM paths so the bundled
                ' manifest.json still points at them. The manifest also
                ' lists older sessions that are deliberately NOT bundled —
                ' it is an index, and a triage read knows more from a full
                ' index than a truncated one.
                Dim traceRoot = Path.GetFullPath(TraceArchiveDir).TrimEnd(Path.DirectorySeparatorChar) &
                    Path.DirectorySeparatorChar
                For Each tracePath As String In CrashReporter.GetRecentTraceArchives(RecentTraceSessionsInBundle)
                    Try
                        Dim fullPath = Path.GetFullPath(tracePath)
                        If File.Exists(fullPath) AndAlso
                           fullPath.StartsWith(traceRoot, StringComparison.OrdinalIgnoreCase) Then
                            Dim relative = fullPath.Substring(traceRoot.Length).Replace(Path.DirectorySeparatorChar, "/"c)
                            ' Session zips are already LZMA-compressed — store as-is.
                            archive.CreateEntryFromFile(fullPath,
                                ProgramName & "/Traces/" & relative,
                                CompressionLevel.NoCompression)
                        End If
                    Catch
                        ' Best-effort — one unreadable archive must not sink the bundle.
                    End Try
                Next

                ' The program itself rides along as a fingerprint manifest plus
                ' a self-verification report, not as 190 MB of binaries. The
                ' whole-directory zip predates the self-contained runtime; once
                ' the .NET runtime moved into the install directory it was
                ' mostly Microsoft's files, identical on every machine, and it
                ' guaranteed the upload limit tripped. The manifest diff answers
                ' the same diagnostic question (stale / corrupt / mixed
                ' install?) and answers it by name.
                AddInstallVerification(archive)

                Dim tempFileName = My.Computer.FileSystem.GetTempFileName
                Try
                    File.Move(tempFileName, tempFileName & ".txt")
                    tempFileName = tempFileName & ".txt"
                Catch
                    ' won't rename
                End Try
                If RigControl IsNot Nothing Then
                    ' get rig info.
                    Using sw = New StreamWriter(tempFileName)
                        Dim infoList = RigControl.RigInfo
                        For Each txt As String In infoList
                            'MsgBox(txt)
                            sw.WriteLine(txt)
                        Next
                    End Using
                    ZipUtils.AddFileToArchive(archive, tempFileName, "riginfo")
                End If

                If LastUserTraceFile <> vbNullString Then
                    ZipUtils.AddFileToArchive(archive, LastUserTraceFile, "")
                End If
                File.Delete(tempFileName)
            End Using

            Tracing.TraceLine($"GetDebugInfo: wrote {openDialog.FileName}", TraceLevel.Info)
            Try
                Radios.ScreenReaderOutput.Speak(infoGathered, Radios.VerbosityLevel.Critical, True)
            Catch
            End Try
            MessageBox.Show(infoGathered, MessageHdr, MessageBoxButtons.OK)
        Catch ex As Exception
            ' Never suppress: trace the real exception for diagnosis, tell the
            ' user something they can act on, and speak it. What must NOT happen
            ' is the raw framework message reaching them as an unexplained
            ' dialog — that is the failure mode this whole track exists to kill.
            Tracing.ErrTraceOnly(ex)
            Try
                Radios.ScreenReaderOutput.Speak(
                    "The debug archive could not be completed. Nothing was lost.",
                    Radios.VerbosityLevel.Critical, True)
            Catch
            End Try
            MessageBox.Show(gatherFailed, ErrorHdr, MessageBoxButtons.OK)
        Finally
            openDialog.Dispose()
        End Try
    End Sub

    ''' <summary>
    ''' QB Track M: add the install's self-verification to the bundle in place
    ''' of the old whole-program zip. Three entries at the bundle root:
    '''  - program-manifest.json: live manifest of the actual install directory
    '''    (path, size, fingerprint per file — same schema the build writes)
    '''  - install-manifest.json: the shipped known-good manifest, when present,
    '''    included verbatim so support can diff against the exact release even
    '''    if the live machine's copy is the thing that's corrupt
    '''  - install-verification.txt: the live-vs-shipped diff in plain prose —
    '''    verified clean, or every mismatched, missing, unexpected, and
    '''    unreadable file by name
    ''' Returns a one-clause summary of the outcome for the completion message.
    ''' A missing shipped manifest is reported plainly and never blocks the
    ''' bundle (dev trees and pre-manifest installs are normal).
    ''' </summary>
    Private Shared Function AddInstallVerification(archive As ZipArchive) As String
        ' The install directory is where the program actually runs from, not
        ' the process's current directory (the old "." could drift with cwd).
        Dim installDir As String = AppContext.BaseDirectory
        Dim live = InstallManifest.BuildLive(installDir)
        WriteTextEntry(archive, "program-manifest.json", InstallManifest.ToJson(live))

        Dim summary As String
        Dim reportText As String
        Dim shippedPath As String = Path.Combine(installDir, InstallManifest.ShippedManifestName)
        If File.Exists(shippedPath) Then
            ' Ship the known-good manifest itself alongside the live one.
            ZipUtils.AddFileToArchive(archive, shippedPath, "")
            Dim known = InstallManifest.Load(shippedPath)
            Dim result = InstallManifest.Verify(known, live)
            reportText = InstallManifest.FormatReport(result, known, installDir)
            If result.DifferenceCount = 0 Then
                summary = "Install verified clean."
            Else
                summary = $"Install verification found {result.DifferenceCount} difference{If(result.DifferenceCount = 1, "", "s")} — see install-verification.txt."
            End If
        Else
            reportText = InstallManifest.FormatMissingManifestReport(installDir)
            summary = "Install check skipped — no shipped manifest to compare against."
        End If
        WriteTextEntry(archive, "install-verification.txt", reportText)
        Tracing.TraceLine("GetDebugInfo:install verification: " & summary, TraceLevel.Info)
        Return summary
    End Function

    ''' <summary>Write a text entry into the bundle. UTF-8 without a byte order
    ''' mark — plain enough for Notepad, screen readers, and support scripts.</summary>
    Private Shared Sub WriteTextEntry(archive As ZipArchive, entryName As String, text As String)
        Dim entry As ZipArchiveEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal)
        Using writer As New StreamWriter(entry.Open())
            writer.Write(text)
        End Using
    End Sub
End Class

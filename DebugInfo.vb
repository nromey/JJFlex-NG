Imports System.IO
Imports System.Windows.Forms
Imports System.IO.Compression
Imports JJTrace

Friend Class DebugInfo
    Private Const openDialogTitle As String = "Debug info archive"
    Private Const mustHaveFile As String = "You must specify a debug file."
    Private Const infoGathered As String = "Debug info gathered."

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
                ' get application data
                ZipUtils.AddDirectoryToArchive(archive, BaseConfigDir, ProgramName)

                ' get the program
                ZipUtils.AddDirectoryToArchive(archive, ".", "program")

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
End Class

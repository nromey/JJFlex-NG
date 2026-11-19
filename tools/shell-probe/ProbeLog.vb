Option Strict On
Imports System.IO
Imports System.Text

''' <summary>
''' The probe's own trace. Every line carries the wall clock and
''' Environment.TickCount, so it lines up with the WinEvent recorder and with
''' JAWS's capture (whose mono offset plus the tick in its file name is on the
''' same clock). Written to %TEMP%\jjshellprobe\probe-trace.txt, appended.
''' </summary>
Friend Module ProbeLog
    Private ReadOnly _gate As New Object()
    Private _path As String

    Friend ReadOnly Property LogPath As String
        Get
            If _path Is Nothing Then
                Dim dir = Path.Combine(Path.GetTempPath(), "jjshellprobe")
                Directory.CreateDirectory(dir)
                _path = Path.Combine(dir, "probe-trace.txt")
            End If
            Return _path
        End Get
    End Property

    Friend Sub Log(text As String)
        Dim line = DateTime.Now.ToString("HH:mm:ss.fff") & " tick=" & Environment.TickCount.ToString() &
                   " pid=" & Environment.ProcessId.ToString() & " " & text
        SyncLock _gate
            Try
                File.AppendAllText(LogPath, line & Environment.NewLine, Encoding.UTF8)
            Catch
                ' A trace that cannot be written must never take the probe down.
            End Try
        End SyncLock
    End Sub
End Module

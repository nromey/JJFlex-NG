Imports System.IO
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Diagnostics
Imports System.Threading
Imports adif
Imports JJLogIO
Imports JJLogLib
Imports JJTrace

Public Class LogClass
    ' Log fields are colon-separated.
    ' The header is the first record.
    ' The header format is:
    '   version (hdrnnn)
    '   creating operator data: call, name, qth
    '   starting serial no.
    '   dupCheck no/call/callAndBand/callAndBandAndMode
    '   formName the form's name.
    '   CallLookup YES/NO
    ' The log format is specified by the log form.
    ' The fields are in the order they're added to the form's field dictionary.

    Private Const cantUseLog As String = "Unable to process log file "

    Public Class HeaderReservedTagsType
        Public Tag As String
        Public value As String
        Public Sub New(t As String, v As String)
            Tag = t
            value = v
        End Sub
    End Class
    Public Shared HeaderReservedTags As HeaderReservedTagsType() = _
        { _
            New HeaderReservedTagsType(AdifTags.HDR_ADIFVersion, "3.0.3"), _
            New HeaderReservedTagsType(AdifTags.HDR_ProgramID, "JJRadio"), _
            New HeaderReservedTagsType(AdifTags.HDR_ProgramVersion, My.Application.Info.Version.ToString) _
        }

    ''' <summary>
    ''' log header ADIF tags used by this program.
    ''' The tag values must be in this order in the header.
    ''' </summary>
    Public Shared HeaderADIFTags As String() = _
        { _
            AdifTags.HDR_LogHeaderVersion, _
            AdifTags.HDR_StartingSerial, _
            AdifTags.HDR_DupCheck, _
            AdifTags.HDR_FormNAME, _
            AdifTags.HDR_CallLookup}

    ' Prior header versions must be lexically disending.
    Public Const CurrentHeaderVersion As String = "hdr004"
    ' HeaderVersion 4 adds the HDR_CallLookup tag.
    Public Const HeaderVersion3NFields As Integer = 4
    Public Const HeaderVersion3 As String = "hdr003"
    ' HeaderVersion2 is the same as 3, except frequency fixup is needed.
    Public Const HeaderVersion2NFields As Integer = 4
    Public Const HeaderVersion2 As String = "hdr002"
    Public Const HeaderVersion1NFields As Integer = 3
    Public Const HeaderVersion1 As String = "hdr001"
    Friend InitialVersion As String = vbNullString

    ' log record version 1 doesn't have the dateout and timeout fields.
    ' version 2 doesn't have the serial numbers, band, and rsvd fields.
    ' Any log prior to version 3 is converted to v3.
    Public Const V1 As Integer = 1
    Public Const V2 As Integer = 2
    Public Const currentVersion As String = "3"
    Public Const FLDSEP As Char = ":"c
    Public Const escChar As Char = "\"c

    Private theLog As LogIO
    Private logLock As Mutex
    Private sessions As Integer
    Friend ReadOnly Property OnlySession As Boolean
        Get
            Return (sessions = 1)
        End Get
    End Property

    ''' <summary>
    ''' log file name
    ''' </summary>
    Public Name As String

    ''' <summary>
    ''' Dup checking for this log file.
    ''' </summary>
    ''' <remarks>setup by the session</remarks>
    Public Dups As LogDupChecking

    ''' <summary>
    ''' setup a log object
    ''' </summary>
    ''' <param name="fileName">can be Nothing</param>
    Public Sub New(fileName As String)
        Tracing.TraceLine("LogClass setup:" + fileName, TraceLevel.Info)
        Name = fileName
        theLog = Nothing
        logLock = New Mutex
        sessions = 0
        cleanups = Nothing
    End Sub

    Public Delegate Function clzrtn() As Boolean
    ''' <summary>
    ''' Info for a log file cleanup routine
    ''' </summary>
    Public Class cleanupClass
        ''' <summary>
        ''' cleanup name
        ''' </summary>
        Public Name As String
        ''' <summary>
        ''' routine to call
        ''' </summary>
        Public rtn As clzrtn
        Public id As Integer ' session number
        Public Sub New(n As String, r As clzrtn)
            Name = n
            rtn = r
        End Sub
    End Class
    Private cleanups As List(Of cleanupClass)

    ''' <summary>
    ''' open a log file session.
    ''' </summary>
    ''' <param name="operater">if the file may be unspecified</param>
    ''' <param name="cleanup">LogClass.cleanupClass data</param>
    ''' <returns>the session number on success, 0 on error</returns>
    Friend Function Open(operater As PersonalData.personal_v1, cleanup As cleanupClass) As Integer
        Dim fn As String = ""
        Dim n As String = ""
        If operater IsNot Nothing Then
            fn = operater.LogFile
        End If
        If cleanup IsNot Nothing Then
            n = cleanup.Name
        End If
        Tracing.TraceLine("LogClass open:" & fn & " " & n & " " & sessions.ToString, _
                          TraceLevel.Info)
        If sessions = 0 Then
            If ((Name = vbNullString) OrElse (Not File.Exists(Name))) And _
                (operater IsNot Nothing) Then
                ' #814: no log file. Say so and offer to open an existing log
                ' or create a new one, then the standard Windows Open or Save
                ' dialog - not Log Characteristics, which this used to open on
                ' every attempt, so every Alt+letter field jump in Logging mode
                ' put the same window up again. A cancel is remembered for this
                ' file until an explicit log action (see AskForLogFileAgain).
                Dim chosen As String = ChooseLogFile(operater)
                If chosen Is Nothing Then
                    Return 0
                End If
                If Operators IsNot Nothing Then
                    Operators.UpdateLogfile(operater, chosen)
                Else
                    operater.LogFile = chosen
                End If
                Name = chosen
            End If
            ' Open the log.
            Try
                theLog = New LogIO(Name)
                sessions = 1
                cleanups = New List(Of cleanupClass)
            Catch ex As Exception
                Tracing.ErrMessageTrace(ex, False, False)
                MsgBox(cantUseLog & Name & ". Error type " & ex.GetType().Name & ". " & ex.Message,
                       MsgBoxStyle.OkOnly, "Log File Error")
                Return 0
            End Try
        Else
            ' At least 1 session exists, the actual file is open.
            sessions += 1
        End If
        If cleanup IsNot Nothing Then
            cleanup.id = sessions
            cleanups.Add(cleanup)
        End If
        Return sessions
    End Function

    ' #814: whether to ask about a missing log file. One per log; see
    ' Radios.LogFilePromptGate for the rule.
    Private ReadOnly filePrompt As New Radios.LogFilePromptGate()

    ''' <summary>
    ''' True when the operator cancelled the no-log-file choice for this log and
    ''' has not since taken an explicit log action, and the log is still
    ''' missing. A caller about to hide the main window for the log form checks
    ''' this first, so the sentence saying why is not lost in the window change.
    ''' </summary>
    Friend ReadOnly Property LogFileDeclined As Boolean
        Get
            Return filePrompt.IsDeclined(Name) AndAlso ((Name = vbNullString) OrElse (Not File.Exists(Name)))
        End Get
    End Property

    ''' <summary>
    ''' An explicit log action (opening the full log form): the next attempt
    ''' to open a missing log asks again. A field jump is not one.
    ''' </summary>
    Friend Sub AskForLogFileAgain()
        filePrompt.AskAgain()
    End Sub

    ''' <summary>
    ''' #814, ruled by Noel 2026-10-23: no log file was found, so say so and
    ''' offer Open an existing log, Create a new one, or Cancel; then the
    ''' standard Windows Open dialog, or a Save dialog for a new file.
    ''' </summary>
    ''' <returns>The chosen file's full path, or Nothing if the operator cancelled.</returns>
    ''' <remarks>
    ''' The choice is Windows' own task dialog: ConfirmActionDialog has two
    ''' answers and Escape presses its No, so it cannot tell "create" from
    ''' "cancel". Escape and the close box both cancel here. Every sentence is
    ''' DRAFT PROSE in the logging lexicon.
    ''' </remarks>
    Private Function ChooseLogFile(operater As PersonalData.personal_v1) As String
        If Not filePrompt.ShouldAsk(Name) Then
            Tracing.TraceLine("LogClass open: no log file, and the choice was cancelled; not asking again until a log action", TraceLevel.Info)
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.full_form.no_log_file"),
                Radios.Speech.SpeechIntent.Queue, Radios.VerbosityLevel.Critical,
                subject:=Radios.Speech.SpeechSubject.NoLogFile)
            Return Nothing
        End If

        Dim openButton As New System.Windows.Forms.TaskDialogButton(Radios.Lexicon.Get("logging.no_log_file.open_button"))
        Dim createButton As New System.Windows.Forms.TaskDialogButton(Radios.Lexicon.Get("logging.no_log_file.create_button"))
        Dim page As New System.Windows.Forms.TaskDialogPage() With {
            .Caption = Radios.Lexicon.Get("logging.no_log_file.title"),
            .Heading = Radios.Lexicon.Get("logging.no_log_file.heading"),
            .Text = Radios.Lexicon.Get("logging.no_log_file.body"),
            .AllowCancel = True
        }
        page.Buttons.Add(openButton)
        page.Buttons.Add(createButton)
        page.Buttons.Add(System.Windows.Forms.TaskDialogButton.Cancel)
        page.DefaultButton = openButton

        Dim owner As System.Windows.Forms.IWin32Window = LogDialogOwner()
        Dim answer As System.Windows.Forms.TaskDialogButton = If(owner Is Nothing,
            System.Windows.Forms.TaskDialog.ShowDialog(page),
            System.Windows.Forms.TaskDialog.ShowDialog(owner, page))
        Dim chosen As String = Nothing
        If answer Is openButton Then
            Tracing.TraceLine("LogClass open: no log file; operator chose to open one", TraceLevel.Info)
            chosen = AskForFile(operater, False)
        ElseIf answer Is createButton Then
            Tracing.TraceLine("LogClass open: no log file; operator chose to create one", TraceLevel.Info)
            chosen = AskForFile(operater, True)
        Else
            Tracing.TraceLine("LogClass open: no log file; operator cancelled the choice", TraceLevel.Info)
        End If

        If chosen Is Nothing Then
            filePrompt.Declined(Name)
        Else
            filePrompt.Chosen()
        End If
        Return chosen
    End Function

    ''' <summary>
    ''' The window the no-log-file dialogs belong to (#850). Shown without an
    ''' owner, each one's closing let Windows activate whatever program sat
    ''' behind the app, and the screen reader announced that program before
    ''' focus came back. Nothing when the shell cannot own a dialog from this
    ''' thread; the dialogs then show unowned, as before.
    ''' </summary>
    Private Shared Function LogDialogOwner() As System.Windows.Forms.IWin32Window
        Dim shell As ShellForm = AppShellForm
        If shell Is Nothing OrElse shell.IsDisposed OrElse Not shell.IsHandleCreated OrElse shell.InvokeRequired Then
            Return Nothing
        End If
        Return shell
    End Function

    ''' <summary>
    ''' The standard Windows Open dialog for an existing log, or a Save dialog
    ''' naming a new one. Nothing on cancel.
    ''' </summary>
    ''' <remarks>
    ''' A new log is created by opening it: LogIO makes the file, and the first
    ''' session writes its header with the defaults. Naming a file that
    ''' already exists in the Save dialog opens that log rather than replacing
    ''' it, so nothing already logged can be lost here.
    ''' </remarks>
    Private Function AskForFile(operater As PersonalData.personal_v1, createNew As Boolean) As String
        Dim folder As String = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        If operater.LogFile <> vbNullString Then
            Try
                Dim parent = Directory.GetParent(operater.LogFile)
                If parent IsNot Nothing AndAlso parent.Exists Then folder = parent.FullName
            Catch ex As Exception
                ' A malformed stored name: start in Documents.
            End Try
        End If
        Dim dialog As System.Windows.Forms.FileDialog
        If createNew Then
            dialog = New System.Windows.Forms.SaveFileDialog() With {
                .OverwritePrompt = False,
                .FileName = "myLog.jrl",
                .Title = Radios.Lexicon.Get("logging.no_log_file.create_title")
            }
        Else
            dialog = New System.Windows.Forms.OpenFileDialog() With {
                .CheckFileExists = True,
                .Title = Radios.Lexicon.Get("logging.no_log_file.open_title")
            }
        End If
        Using dialog
            dialog.Filter = Radios.Lexicon.Get("logging.no_log_file.file_filter")
            dialog.DefaultExt = "jrl"
            dialog.AddExtension = True
            dialog.CheckPathExists = True
            dialog.InitialDirectory = folder
            If dialog.ShowDialog(LogDialogOwner()) = System.Windows.Forms.DialogResult.OK Then
                Return dialog.FileName
            End If
        End Using
        Return Nothing
    End Function

    ''' <summary>
    ''' Solicited cleanup.
    ''' </summary>
    ''' <returns>true on success.</returns>
    Public Function Close(id As Integer) As Boolean
        Tracing.TraceLine("LogClass Close: id=" & id.ToString & " sessions=" & sessions.ToString &
                          " file=" & If(Name, "null"), TraceLevel.Info)
        If sessions = 0 Then
            Return False
        End If
        sessions -= 1
        If sessions = 0 Then
            Try
                theLog.Close()
                theLog = Nothing
            Catch ex As Exception
                Tracing.ErrMessageTrace(ex)
            End Try
        End If
        If cleanups IsNot Nothing Then
            For Each c As cleanupClass In cleanups
                If c.id = id Then
                    Tracing.TraceLine("  removing cleanup:" & c.Name, TraceLevel.Info)
                    cleanups.Remove(c)
                    Exit For
                End If
            Next
        End If
        Return (True)
    End Function

    ''' <summary>
    ''' Perform unsolicited cleanup for each session.
    ''' </summary>
    ''' <returns>false if a session refuses the cleanup.</returns>
    ''' <remarks>If a routine returns false, the cleanup still executes remaining routines.</remarks>
    Public Function Cleanup() As Boolean
        Tracing.TraceLine("Cleanup:", TraceLevel.Info)
        Dim rv As Boolean = True
        If cleanups Is Nothing Then
            Return rv
        End If
        Dim i As Integer = 0
        While i < cleanups.Count
            Dim c As cleanupClass = cleanups(i)
            If c.rtn IsNot Nothing Then
                If c.rtn() Then
                    Tracing.TraceLine("  " & c.Name & " returned true", TraceLevel.Info)
                    ' session cleaned up.
                    cleanups.Remove(c)
                Else
                    Tracing.TraceLine("  " & c.Name & " returned false", TraceLevel.Info)
                    ' session rejected the cleanup.
                    rv = False
                    i += 1
                End If
            Else
                ' No cleanup routine provided.
                cleanups.Remove(c)
            End If
        End While
        If rv Then
            ' All sessions cleaned up or didn't need to.
            cleanups = Nothing
        End If
        Return rv
    End Function

    Public ReadOnly Property EOF As Boolean
        Get
            Return theLog.EOF
        End Get
    End Property

    Public Sub Lock()
        Tracing.TraceLine("log lock", TraceLevel.Verbose)
        logLock.WaitOne()
    End Sub
    Public Sub Unlock()
        Tracing.TraceLine("log unlock", TraceLevel.Verbose)
        logLock.ReleaseMutex()
    End Sub

    Public Function Position() As Long
        Dim pos As Long = theLog.Position
        Tracing.TraceLine("LogPosition:" & pos.ToString, TraceLevel.Verbose)
        ' This won't throw an exception.
        Return (pos)
    End Function
    Public Function SeekToFirst() As Long
        Tracing.TraceLine("SeekToFirst", TraceLevel.Info)
        ' This won't throw an exception.
        Return theLog.SeekToFirst
    End Function
    Public Function SeekToLast() As Long
        Tracing.TraceLine("SeekToLast", TraceLevel.Info)
        ' This won't throw an exception.
        Return theLog.SeekToLast
    End Function
    Public Function SeekToPosition(pos As Long) As Long
        Tracing.TraceLine("SeekToPosition:" & pos.ToString, TraceLevel.Verbose)
        Try
            theLog.SeekToPosition(pos)
        Catch ex As Exception
            Throw (ex)
        End Try
        Return pos
    End Function
    Public Function SeekToPrevious() As Long
        Dim pos As Long
        Try
            pos = theLog.SeekToPrevious()
        Catch ex As Exception
            Throw (ex)
        End Try
        Tracing.TraceLine("SeekToPrevious returning:" & pos.ToString, TraceLevel.Info)
        Return pos
    End Function
    Public Function SeekToNext() As Long
        Dim pos As Long
        Try
            pos = theLog.SeekToNext()
        Catch ex As Exception
            Throw (ex)
        End Try
        Tracing.TraceLine("SeekToNext returning:" & pos.ToString, TraceLevel.Info)
        Return pos
    End Function
    Public Function Read() As String
        Dim str As String = ""
        Try
            str = theLog.Read
        Catch ex As Exception
            Throw (ex)
        End Try
        Tracing.TraceLine("log read returning:" & str, TraceLevel.Verbose)
        Return str
    End Function
    Public Function Append(str As String) As Boolean
        Tracing.TraceLine("log append:" & str, TraceLevel.Info)
        Dim rv As Boolean = True
        Try
            theLog.Append(str)
        Catch ex As Exception
            Throw (ex)
        End Try
        Return rv
    End Function
    Public Function Update(str As String) As Boolean
        Tracing.TraceLine("log update:" & str, TraceLevel.Info)
        Dim rv As Boolean = True
        Try
            theLog.Update(str)
        Catch ex As Exception
            Throw (ex)
        End Try
        Return rv
    End Function
    Public Function Delete() As Boolean
        Tracing.TraceLine("delete record at current position", TraceLevel.Info)
        Dim rv As Boolean = True
        Try
            theLog.Delete()
        Catch ex As Exception
            Throw (ex)
        End Try
        Return rv
    End Function
End Class

Imports System.IO
Imports adif
Imports JJLogLib
Imports JJRadio.LogClass
Imports JJRadio.LogSession

Public Class LogCharacteristics
    Public theOp As PersonalData.personal_v1

    Private Const validFile As String = "You must specify a valid file name."
    Private Const serialNotNumeric As String = "The first serial number must be numeric."
    Private Const NeedForm As String = "You must select a log form."
    Private wasActive As Boolean
    Private startupError As Boolean
    Private oldFileName As String

    Private Const nameSize As Integer = 23
    ''' <summary>
    ''' (overloaded) Trim a name to the specified size, 23 by default.
    ''' Also remove the extention.
    ''' </summary>
    ''' <param name="name">name to trim</param>
    ''' <param name="sz">optional size</param>
    ''' <returns>trimmed name</returns>
    Friend Function TrimmedFilename(name As String, sz As Integer) As String
        If name = vbNullString Then
            Return name
        End If
        Dim txt As String
        ' First remove the extention.
        Dim id As Integer = name.LastIndexOf("."c)
        If id > 0 Then
            name = name.Substring(0, id)
        End If
        Dim len As Integer = name.Length
        If len > sz Then
            txt = "..." & name.Substring(len - (sz - 3) - 1)
        Else
            txt = name
        End If
        Return txt
    End Function
    Friend Function TrimmedFilename(name As String) As String
        Return TrimmedFilename(name, nameSize)
    End Function

    Private Function setup() As Boolean
        Static setupFlag As Boolean = False
        NameBox.Text = theOp.LogFile

        If Not setupFlag Then
            setupFlag = True
            Dim logNames As String() = Logs.LogNames
            For Each Name As String In logNames
                FormList.Items.Add(Name)
            Next
            For Each d As String In [Enum].GetNames(GetType(LogDupChecking.DupTypes))
                DupList.Items.Add(d)
            Next
            For Each d As String In [Enum].GetNames(GetType(Logs.LookupChoices))
                LookupList.Items.Add(d)
            Next
            LookupList.SelectedIndex = Logs.DefaultLookupChoice
        End If

        ' Setup the recent files menu.
        FileMenuItem.DropDownItems.Clear()
        If theOp.LogfileStack.Count > 0 Then
            For i As Integer = 0 To Math.Min(theOp.LogfileStack.Count, theOp.NumberOfLogs) - 1
                Dim item = New ToolStripMenuItem
                item.Tag = i
                item.AutoSize = True
                item.Text = i.ToString & " " & TrimmedFilename(theOp.LogfileStack(i))
                AddHandler item.Click, AddressOf MenuItem_Click
                FileMenuItem.DropDownItems.Add(item)
            Next
        End If

        ' Get info from the log if there is one.  Set default values if not.
        Dim rv As Boolean = setLogInfo(theOp.LogFile)
        Return rv
    End Function

    Private Function setLogInfo(fileName As String) As Boolean
        Dim rv As Boolean
        oldFileName = fileName ' see NameBox_Leave
        ' Get info from the file if there is one.
        If (fileName <> vbNullString) AndAlso File.Exists(fileName) Then
            ' The FormList is disabled when the file exists.
            FormList.Enabled = False
            Dim session = New LogSession(fileName)
            ' session.start() reads the header.
            rv = session.Start()
            If rv Then
                Dim formName As String = session.GetHeaderFieldText(AdifTags.HDR_FormNAME)
                For i As Integer = 0 To FormList.Items.Count - 1
                    If FormList.Items(i) = formName Then
                        FormList.SelectedIndex = i
                        Exit For
                    End If
                Next
                DupList.SelectedIndex = CInt(session.GetHeaderFieldText(AdifTags.HDR_DupCheck))
                FirstSerialBox.Text = CInt(session.GetHeaderFieldText(AdifTags.HDR_StartingSerial))
                LookupList.SelectedIndex = session.GetHeaderFieldText(AdifTags.HDR_CallLookup)
                session.EndSession() ' closes the file
            End If
        Else
            FormList.Enabled = True
            FormList.SelectedIndex = Logs.DefaultLogID
            DupList.SelectedIndex = LogDupChecking.DupTypes.none
            FirstSerialBox.Text = "1"
            LookupList.SelectedIndex = 1
            rv = True
        End If
        Return rv
    End Function

    Private Sub LogCharacteristics_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        wasActive = False
        DialogResult = System.Windows.Forms.DialogResult.None
        If Not setup() Then
            startupError = True
        End If
    End Sub

    Private Function getLogFileName() As String
        Static defaultLogFile As String = _
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) & "\myLog.jrl"
        ' Get the log file name.  Start with name as the default.
        ' Name must be the full pathname, and we return the full pathname.
        ' Return nothing on error or cancel.
        Dim outName As String = vbNullString
        With OpenFileDialog1
            .AddExtension = True
            .DefaultExt = "JRL"
            .Filter = "Log files (*.JRL)|*.JrL"
            .CheckFileExists = False
            .CheckPathExists = True
            If theOp.LogFile = vbNullString Then
                .FileName = defaultLogFile
            Else
                .FileName = theOp.LogFile
            End If
            Dim di As DirectoryInfo
            di = Directory.GetParent(.FileName)
            If di IsNot Nothing Then
                .InitialDirectory = di.FullName
            End If
            .FileName = .SafeFileName()
            .Title = "Log Filename"
            .ValidateNames = True
            If .ShowDialog = DialogResult.OK Then
                outName = .FileName
            End If
        End With
        ' Get info from the log, or create one with the defaults.
        If Not setLogInfo(outName) Then
            outName = vbNullString
        End If
        Return outName
    End Function

    Private Sub BrowseButton_Click(sender As System.Object, e As System.EventArgs) Handles BrowseButton.Click
        Dim str As String = getLogFileName()
        If str <> vbNullString Then
            theOp.LogFile = str
            NameBox.Text = str
            NameBox.Focus()
        End If
    End Sub

    Private Sub OkButton_Click(sender As System.Object, e As System.EventArgs) Handles OkButton.Click
        ' Ensure entered data is valid.
        If Not IsValidFileNameOrPath(NameBox.Text) Then
            MsgBox(validFile)
            NameBox.Focus()
            Return
        End If
        If Not checkSerial() Then
            Return
        End If
        If FormList.SelectedIndex = -1 Then
            MsgBox(NeedForm)
            FormList.Focus()
            Return
        End If
        If DupList.SelectedIndex = -1 Then
            DupList.SelectedIndex = LogDupChecking.DupTypes.none
        End If
        If LookupList.SelectedIndex = -1 Then
            LookupList.SelectedIndex = Logs.DefaultLookupChoice
        End If

        ' The log file is kept with the operator.
        ' Carefull:  There may not be any operators!
        If Operators IsNot Nothing Then
            Operators.UpdateLogfile(theOp, NameBox.Text)
        End If

        ' Set the header from provided info.
        Dim session = New LogSession(theOp.LogFile)
        session.Start()
        session.setHeaderFieldText(AdifTags.HDR_FormNAME, FormList.Items(FormList.SelectedIndex))
        session.setHeaderFieldText(AdifTags.HDR_DupCheck, CStr(DupList.SelectedIndex))
        session.setHeaderFieldText(AdifTags.HDR_StartingSerial, FirstSerialBox.Text)
        session.setHeaderFieldText(AdifTags.HDR_CallLookup, LookupList.SelectedIndex)
        session.UpdateLogHeader()
        session.EndSession() ' closes the file.

        DialogResult = System.Windows.Forms.DialogResult.OK
    End Sub

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        DialogResult = System.Windows.Forms.DialogResult.Cancel
    End Sub

    Private Sub FirstSerialBox_Leave(sender As System.Object, e As System.EventArgs) Handles FirstSerialBox.Leave
        checkSerial()
    End Sub
    Private Function checkSerial() As Boolean
        Dim rv As Boolean = True
        If FirstSerialBox.Text = "" Then
            FirstSerialBox.Text = "0"
        ElseIf Not IsNumeric(FirstSerialBox.Text) Then
            MsgBox(serialNotNumeric)
            FirstSerialBox.Focus()
            rv = False
        End If
        Return rv
    End Function

    Private Sub LogCharacteristics_Activated(sender As System.Object, e As System.EventArgs) Handles MyBase.Activated
        If startupError Or (Not wasActive) Then
            wasActive = True
            startupError = False
            NameBox.Focus()
        End If
    End Sub

    Private Sub NameBox_Leave(sender As System.Object, e As System.EventArgs) Handles NameBox.Leave
        If NameBox.Text <> oldFileName Then
            If Not setLogInfo(NameBox.Text) Then
                NameBox.Text = oldFileName
            End If
        End If
    End Sub

    Private Sub MenuItem_Click(sender As System.Object, e As System.EventArgs)
        Dim item As ToolStripMenuItem = sender
        NameBox.Text = theOp.LogfileStack(CType(item.Tag, Integer))
        NameBox.Focus()
    End Sub
End Class
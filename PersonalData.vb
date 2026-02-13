Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.IO.Directory
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Xml.Serialization
Imports HamBands
Imports JJArClusterLib
Imports JJRadio.CWMessages
Imports JJTrace
Imports MsgLib
Imports Radios

Public Class PersonalData
    Const subDir As String = "Operators"
    Public Const BrailleDisplaySizeDefault As Integer = 40
    Private Shared opsDir As String
    Public Class personal
        Public fileName As String
        Public DefaultFlag As Boolean
        Public callSign As String
        Public handl As String
        Public fullName As String
        Public qth As String
        Public LogFile As String
        Public BrailleDisplaySize As Integer
        Public CWText As MessageItem()
        Public NumericLicense As Integer
    End Class

    Public Class personal_v1
        Public fileName As String
        ''' <summary>
        ''' (ReadOnly) User name passed to libraries.
        ''' </summary>
        <XmlIgnore()> Public ReadOnly Property UserBasename
            Get
                Return fileName.Substring(0, fileName.Length - 4)
            End Get
        End Property
        <XmlIgnore()> Public ReadOnly Property pathName As String
            Get
                Return opsDir & "\" & fileName
            End Get
        End Property
        Public DefaultFlag As Boolean
        Public callSign As String
        Public handl As String
        Public fullName As String
        ''' <summary> display value for a list </summary>
        ''' <returns>string to display</returns>
        <XmlIgnore()> Public ReadOnly Property Display As String
            Get
                Return handl & " (" & fullName & ")"
            End Get
        End Property
        Public qth As String
        ''' <summary>
        ''' Operator's Maidenhead grid square (e.g., "FN31pr").
        ''' Used for distance/bearing calculation in Station Lookup.
        ''' </summary>
        Public GridSquare As String = ""
        <XmlIgnore()> Public Const NumberOfLogsDefault As Integer = 4
        Public NumberOfLogs As Integer
        Public LogFiles As String()
        <XmlIgnore()> Public LogfileStack As Stack(Of String)
        ''' <summary>
        ''' The log file in use.
        ''' Set this to push onto the stack.
        ''' </summary>
        ''' <value>the log file name</value>
        ''' <returns>top of log stack or the empty string.</returns>
        <XmlIgnore()> Public Property LogFile As String
            Get
                If (LogFiles Is Nothing) OrElse (LogFiles.Length = 0) OrElse (LogFiles(0) = vbNullString) Then
                    Return vbNullString
                Else
                    Return LogFiles(0)
                End If
            End Get
            Set(value As String)
                If value = vbNullString Then
                    Return
                End If
                If LogfileStack.Count > 0 Then
                    Dim valueStr As String = value.ToUpper
                    For i As Integer = 0 To LogfileStack.Count - 1
                        If LogfileStack(i).ToUpper = valueStr Then
                            Dim tempStack = New Stack(Of String)
                            ' pop preceding elements
                            For j As Integer = 0 To i - 1
                                tempStack.Push(LogfileStack.Pop)
                            Next
                            ' pop the dup element
                            LogfileStack.Pop()
                            ' push the other ones just popped.
                            For j As Integer = 0 To tempStack.Count - 1
                                LogfileStack.Push(tempStack.Pop)
                            Next
                            Exit For
                        End If
                    Next
                End If
                ' push new value
                LogfileStack.Push(value)
                LogFiles = LogfileStack.ToArray
                If LogFiles.Length > NumberOfLogs Then
                    ReDim Preserve LogFiles(NumberOfLogs)
                End If
            End Set
        End Property
        Public ClusterHostname As String = "dxc.nc7j.com"
        Public ClusterLoginName As String
        Public ClusterBeepSetting As ClusterForm.BeepType
        Public ClusterBeep As Boolean ' depricated
        Public ClusterTrackPosition As Boolean
        Public HamqthID As String = ""
        Public HamqthPassword As String = ""
        Public BrailleDisplaySize As Integer
        Public CWText As MessageItem()
        Public NumericLicense As Integer
        ' This is necessary for the XMLSerializer and enums on Vista.
        <XmlIgnore()> Public Property License As Bands.Licenses
            Get
                Return NumericLicense
            End Get
            Set(value As Bands.Licenses)
                NumericLicense = CInt(value)
            End Set
        End Property
        ''' <summary>
        ''' true if constraining the decoded output
        ''' </summary>
        Public ConstrainedDecode As Boolean
        ''' <summary>
        ''' Number of characters to constrain decoded text to
        ''' </summary>
        Public CWDecodeCells As Integer
        ''' <summary>
        ''' Optional message tags and values
        ''' </summary>
        Public OptionalMessages As OptionalMessageConfig()
        ''' <summary>
        ''' User's profiles
        ''' </summary>
        Public Profiles As List(Of Profile_t) = Nothing
        ''' <summary>
        ''' When true, keep daily trace logs (JJFlexRadioTraceYYYYMMDDHHMMSS.txt) and archive old days.
        ''' </summary>
        Public KeepDailyTraceLogs As Boolean = False

        ''' <summary>
        ''' Persisted UI mode (0=Classic, 1=Modern, 2=Logging).
        ''' Stored as Integer for forward-compatible XML serialization.
        ''' Missing field in old XML files defaults to 0 (Classic).
        ''' </summary>
        Public UIModeSetting As Integer = 0

        ''' <summary>
        ''' True after the one-time "Try Modern UI?" upgrade prompt has been shown.
        ''' Prevents the prompt from appearing on every launch.
        ''' </summary>
        Public UIModeDismissed As Boolean = False

        ''' <summary>
        ''' When True, pressing Escape in Logging Mode clears the form without
        ''' a confirmation dialog. Screen reader just says "Entry cleared".
        ''' Handy during pileups when you don't want a dialog slowing you down.
        ''' </summary>
        Public SuppressClearConfirm As Boolean = False

        ''' <summary>
        ''' Number of recent QSOs to display in the Logging Mode grid.
        ''' Range: 5–100. Default: 20.
        ''' </summary>
        Public Const RecentQsoCountDefault As Integer = 20
        Public RecentQsoCount As Integer = RecentQsoCountDefault

        ''' <summary>
        ''' Callbook lookup source: "None", "QRZ", or "HamQTH".
        ''' Controls which service is used for auto-fill in Logging Mode.
        ''' </summary>
        Public CallbookLookupSource As String = "None"

        ''' <summary>
        ''' Username for the selected callbook service (QRZ or HamQTH).
        ''' </summary>
        Public CallbookUsername As String = ""

        ''' <summary>
        ''' Plaintext password — kept for backward compatibility with old XML files.
        ''' On load: if this is non-empty and CallbookPasswordEncrypted is empty,
        ''' it's migrated to encrypted form and wiped. After migration, this field
        ''' is always empty on disk. At runtime, use DecryptedCallbookPassword instead.
        ''' </summary>
        Public CallbookPassword As String = ""

        ''' <summary>
        ''' DPAPI-encrypted password stored in the operator XML file.
        ''' Tied to the Windows user account — useless if copied to another machine/user.
        ''' </summary>
        Public CallbookPasswordEncrypted As String = ""

        ''' <summary>
        ''' Runtime plaintext password (decrypted from DPAPI on load).
        ''' NOT serialized — use this property for all runtime access.
        ''' </summary>
        <XmlIgnore()> Public DecryptedCallbookPassword As String = ""

        ''' <summary>
        ''' When True, each QSO logged in Logging Mode is uploaded to the
        ''' operator's QRZ.com logbook in real-time (fire-and-forget).
        ''' </summary>
        Public QrzLogbookEnabled As Boolean = False

        ''' <summary>
        ''' Plaintext API key — kept for backward compatibility with old XML files.
        ''' On load: migrated to encrypted form and wiped, just like CallbookPassword.
        ''' </summary>
        Public QrzLogbookApiKey As String = ""

        ''' <summary>
        ''' DPAPI-encrypted API key stored in the operator XML file.
        ''' Tied to the Windows user account.
        ''' </summary>
        Public QrzLogbookApiKeyEncrypted As String = ""

        ''' <summary>
        ''' Runtime plaintext API key (decrypted from DPAPI on load).
        ''' NOT serialized — use this property for all runtime access.
        ''' </summary>
        <XmlIgnore()> Public DecryptedQrzLogbookApiKey As String = ""

        ''' <summary>
        ''' Validated UI mode. Returns Classic for unknown or not-yet-implemented values.
        ''' </summary>
        <XmlIgnore()> Friend Property CurrentUIMode As UIMode
            Get
                If UIModeSetting = CInt(UIMode.Modern) Then
                    Return UIMode.Modern
                End If
                ' Unknown values (including stale Logging=2) fall back to Classic.
                Return UIMode.Classic
            End Get
            Set(value As UIMode)
                UIModeSetting = CInt(value)
            End Set
        End Property

        ''' <summary>
        ''' Encrypt a plaintext string using Windows DPAPI (CurrentUser scope).
        ''' Tied to the Windows login — copying the file to another user/machine is useless.
        ''' </summary>
        Friend Shared Function EncryptWithDpapi(plainText As String) As String
            If String.IsNullOrEmpty(plainText) Then Return String.Empty
            Dim plainBytes = Encoding.UTF8.GetBytes(plainText)
            Dim encBytes = ProtectedData.Protect(plainBytes, Nothing, DataProtectionScope.CurrentUser)
            Return Convert.ToBase64String(encBytes)
        End Function

        ''' <summary>
        ''' Decrypt a DPAPI-encrypted Base64 string. Returns empty string on failure
        ''' (wrong user, wrong machine, corrupted data).
        ''' </summary>
        Friend Shared Function DecryptWithDpapi(encryptedBase64 As String) As String
            If String.IsNullOrEmpty(encryptedBase64) Then Return String.Empty
            Try
                Dim encBytes = Convert.FromBase64String(encryptedBase64)
                Dim plainBytes = ProtectedData.Unprotect(encBytes, Nothing, DataProtectionScope.CurrentUser)
                Return Encoding.UTF8.GetString(plainBytes)
            Catch
                ' Decryption failed — different user, different machine, or corrupted data.
                Return String.Empty
            End Try
        End Function

        ''' <summary>
        ''' After deserialization, decrypt credentials and handle migration from plaintext.
        ''' Call this after XmlSerializer.Deserialize() loads the object.
        ''' Returns True if a write is needed (migration occurred).
        ''' </summary>
        Friend Function DecryptCredentials() As Boolean
            Dim needsWrite As Boolean = False

            If Not String.IsNullOrEmpty(CallbookPasswordEncrypted) Then
                ' Normal path: decrypt the DPAPI blob into the runtime field.
                DecryptedCallbookPassword = DecryptWithDpapi(CallbookPasswordEncrypted)
            ElseIf Not String.IsNullOrEmpty(CallbookPassword) Then
                ' Migration path: old plaintext password found, encrypt it.
                DecryptedCallbookPassword = CallbookPassword
                CallbookPasswordEncrypted = EncryptWithDpapi(CallbookPassword)
                CallbookPassword = ""  ' Wipe plaintext from the object.
                needsWrite = True
                Tracing.TraceLine("PersonalData: migrated plaintext callbook password to DPAPI",
                                  TraceLevel.Info)
            End If

            ' QRZ Logbook API key — same pattern as callbook password.
            If Not String.IsNullOrEmpty(QrzLogbookApiKeyEncrypted) Then
                DecryptedQrzLogbookApiKey = DecryptWithDpapi(QrzLogbookApiKeyEncrypted)
            ElseIf Not String.IsNullOrEmpty(QrzLogbookApiKey) Then
                DecryptedQrzLogbookApiKey = QrzLogbookApiKey
                QrzLogbookApiKeyEncrypted = EncryptWithDpapi(QrzLogbookApiKey)
                QrzLogbookApiKey = ""
                needsWrite = True
                Tracing.TraceLine("PersonalData: migrated plaintext QRZ logbook API key to DPAPI",
                                  TraceLevel.Info)
            End If

            Return needsWrite
        End Function

        ''' <summary>
        ''' Before serialization, encrypt the runtime password into the storage field
        ''' and ensure the plaintext field is empty (never written to disk).
        ''' </summary>
        Friend Sub EncryptCredentials()
            If Not String.IsNullOrEmpty(DecryptedCallbookPassword) Then
                CallbookPasswordEncrypted = EncryptWithDpapi(DecryptedCallbookPassword)
            Else
                CallbookPasswordEncrypted = ""
            End If
            ' Never write plaintext to disk.
            CallbookPassword = ""

            ' QRZ Logbook API key — same pattern.
            If Not String.IsNullOrEmpty(DecryptedQrzLogbookApiKey) Then
                QrzLogbookApiKeyEncrypted = EncryptWithDpapi(DecryptedQrzLogbookApiKey)
            Else
                QrzLogbookApiKeyEncrypted = ""
            End If
            QrzLogbookApiKey = ""
        End Sub

        Public Sub New()
            NumberOfLogs = NumberOfLogsDefault
            LogfileStack = New Stack(Of String)(NumberOfLogs)
            ConstrainedDecode = False
            CWDecodeCells = 0
        End Sub
        Friend Sub New(p As personal_v1)
            fileName = p.fileName
            DefaultFlag = p.DefaultFlag
            callSign = p.callSign
            fullName = p.fullName
            handl = p.handl
            qth = p.qth
            GridSquare = p.GridSquare
            License = p.License
            CWText = p.CWText
            NumberOfLogs = p.NumberOfLogs
            LogfileStack = p.LogfileStack
            LogFiles = LogfileStack.ToArray
            BrailleDisplaySize = p.BrailleDisplaySize
            KeepDailyTraceLogs = p.KeepDailyTraceLogs
            UIModeSetting = p.UIModeSetting
            UIModeDismissed = p.UIModeDismissed
            SuppressClearConfirm = p.SuppressClearConfirm
            RecentQsoCount = p.RecentQsoCount
            CallbookLookupSource = p.CallbookLookupSource
            CallbookUsername = p.CallbookUsername
            CallbookPassword = p.CallbookPassword
            CallbookPasswordEncrypted = p.CallbookPasswordEncrypted
            DecryptedCallbookPassword = p.DecryptedCallbookPassword
            QrzLogbookEnabled = p.QrzLogbookEnabled
            QrzLogbookApiKey = p.QrzLogbookApiKey
            QrzLogbookApiKeyEncrypted = p.QrzLogbookApiKeyEncrypted
            DecryptedQrzLogbookApiKey = p.DecryptedQrzLogbookApiKey
        End Sub
        Friend Sub New(p As personal)
            fileName = p.fileName
            DefaultFlag = p.DefaultFlag
            callSign = p.callSign
            fullName = p.fullName
            handl = p.handl
            qth = p.qth
            NumericLicense = p.NumericLicense
            CWText = p.CWText
            NumberOfLogs = NumberOfLogsDefault
            LogfileStack = New Stack(Of String)(NumberOfLogs)
            LogFile = p.LogFile
            BrailleDisplaySize = p.BrailleDisplaySize
            KeepDailyTraceLogs = False
        End Sub
    End Class

    Public Class OptionalMessageConfig
        Public Tag As String
        Public Result As DialogResult
        Public Ignore As Boolean
        Public Sub New()
        End Sub
        Public Sub New(t As String, rslt As System.Windows.Forms.DialogResult, i As Boolean)
            Tag = t
            Result = rslt
            Ignore = i
        End Sub
    End Class

    Public ops As List(Of personal_v1)
    Default Public ReadOnly Property Items(ByVal id As Integer) As personal_v1
        Get
            Return ops(id)
        End Get
    End Property
    Public ReadOnly Property Length As Integer
        Get
            Return ops.Count
        End Get
    End Property

    Private dfltID As Integer
    ''' <summary>
    ''' Index of the default operator.
    ''' </summary>
    Public Property DefaultID As Integer
        Get
            Return dfltID
        End Get
        Set(ByVal value As Integer)
            ' Reset any previously set value.
            If dfltID <> -1 Then
                ops(dfltID).DefaultFlag = False
                write(ops(dfltID))
            End If
            dfltID = value
        End Set
    End Property
    ''' <summary>
    ''' The default item
    ''' </summary>
    Public ReadOnly Property TheDefault As personal_v1
        Get
            If dfltID = -1 Then
                Return Nothing
            Else
                Return ops(dfltID)
            End If
        End Get
    End Property

    Private curID As Integer
    ''' <summary>
    ''' ID of the current operator.
    ''' </summary>
    Public Property CurrentID As Integer
        Get
            Return curID
        End Get
        Set(value As Integer)
            curID = value
            If curID <> -1 Then
                CWText = New CWMessages(ops(curID).CWText)
                ' One-time migration: F5-F11 → Ctrl+1..Ctrl+7
                CWText.MigrateFKeysToCtrlNumber()
            End If
        End Set
    End Property
    ''' <summary>
    ''' (ReadOnly) The current operator or Nothing if none.
    ''' </summary>
    Public ReadOnly Property CurrentItem As personal_v1
        Get
            If CurrentID = -1 Then
                Return Nothing
            Else
                Return ops(CurrentID)
            End If
        End Get
    End Property

    Public Sub New(ByVal baseDir As String)
        Tracing.TraceLine("PersonalData new(" & baseDir & ")", TraceLevel.Info)
        opsDir = baseDir & "\" & subDir
        ops = New List(Of personal_v1)
        dfltID = -1
        CurrentID = -1
        If Not Exists(opsDir) Then
            Try
                CreateDirectory(opsDir)
            Catch ex As Exception
                Tracing.ErrMessageTrace(ex)
                Exit Sub
            End Try
        End If
        Dim opFiles As String() = GetFiles(opsDir, "*.xml")
        If opFiles.Length = 0 Then
            ' Create initial operator.
            Add()
        Else
            ' Get operators
            Dim err As Boolean = False
            For Each fn As String In opFiles
                Dim cfgFile As Stream = Nothing
                Dim p As personal_v1 = Nothing
                Try
                    cfgFile = File.Open(fn, FileMode.Open)
                    Dim xs As New XmlSerializer(GetType(personal_v1))
                    p = xs.Deserialize(cfgFile)
                    setupOpData(p, False)
                Catch ex As InvalidOperationException
                    ' See if an old format file.
                    Tracing.TraceLine("PersonalData new exception:" & ex.Message, TraceLevel.Error)
                    cfgFile.Close()
                    cfgFile = File.Open(fn, FileMode.Open)
                    Dim xs As New XmlSerializer(GetType(personal))
                    Dim pOld As personal
                    pOld = xs.Deserialize(cfgFile)
                    cfgFile.Dispose()
                    cfgFile = Nothing
                    ' Convert to the current format.
                    p = New personal_v1(pOld)
                    setupOpData(p, True)
                Catch ex As Exception
                    Tracing.ErrMessageTrace(ex)
                    err = True
                End Try
                If cfgFile IsNot Nothing Then
                    cfgFile.Dispose()
                End If
                If err Then
                    Exit For
                End If

                ' Decrypt callbook credentials (or migrate plaintext → DPAPI).
                Dim credentialsMigrated = p.DecryptCredentials()

                ' Migrate old HamQTH credentials to new callbook fields if present.
                If (p.HamqthID <> vbNullString) Or (p.HamqthPassword <> vbNullString) Then
                    If String.IsNullOrEmpty(p.CallbookUsername) AndAlso
                       Not String.IsNullOrEmpty(p.HamqthID) Then
                        p.CallbookLookupSource = "HamQTH"
                        p.CallbookUsername = p.HamqthID
                        ' Set the runtime password and encrypt it.
                        p.DecryptedCallbookPassword = p.HamqthPassword
                    End If
                    p.HamqthID = ""
                    p.HamqthPassword = ""
                    p.CallbookPassword = ""  ' Wipe any plaintext.
                    credentialsMigrated = True
                End If

                ' Write if any migration occurred (DPAPI encryption or HamQTH migration).
                If credentialsMigrated Then
                    write(p)
                End If
            Next
        End If
    End Sub
    Private Sub setupOpData(p As personal_v1, save As Boolean)
        ' Setup the log file stack.
        p.LogfileStack = New Stack(Of String)(p.NumberOfLogs)
        If (p.LogFiles Is Nothing) OrElse (p.LogFiles.Length = 0) Then
            ' no log file.
        Else
            For i As Integer = (Math.Min(p.LogFiles.Length, p.NumberOfLogs) - 1) To 0 Step -1
                p.LogfileStack.Push(p.LogFiles(i))
            Next
        End If
        ' set any defaults
        If p.BrailleDisplaySize = 0 Then
            p.BrailleDisplaySize = BrailleDisplaySizeDefault
        End If
        ops.Add(p)
        If p.DefaultFlag Then
            DefaultID = ops.Count - 1
        End If
        If save Then
            write(p)
        End If
    End Sub

    Friend Shared Function UniqueOpName(ByVal op As personal_v1) As String
        Dim tst As New Regex("[^0-9a-zA-Z]")
        Dim fn As String = op.fullName & "_" & op.handl
        Return tst.Replace(fn, "_")
    End Function

    Private Function opFileName(ByVal op As personal_v1) As String
        Return UniqueOpName(op) & ".xml"
    End Function

    ' We need to know if we're doing an add.
    Private addFlag As Boolean = False
    ''' <summary>
    ''' Add an operator
    ''' </summary>
    ''' <returns>id of the operator added or -1</returns>
    Public Function Add() As Integer
        addFlag = True
        Dim rv As Integer = -1
        PersonalInfo.theOp = Nothing ' Indicates an add.
        If PersonalInfo.ShowDialog = DialogResult.OK Then
            ' Create the file name using the handle.
            Dim theOp As personal_v1 = PersonalInfo.theOp
            theOp.fileName = opFileName(theOp)
            rv = ops.Count ' new id
            If theOp.DefaultFlag Then
                DefaultID = rv
            End If
            ops.Add(theOp)
            ' Write the new file.
            write(theOp)
        End If
        ' No event is reported.
        addFlag = False
        Return rv
    End Function

    Public Function Update(ByVal id As Integer) As Boolean
        PersonalInfo.theOp = ops(id)
        Dim rv As Boolean = (PersonalInfo.ShowDialog = DialogResult.OK)
        If rv Then
            Dim theOp As personal_v1 = PersonalInfo.theOp
            ' Note theOp won't be the same as the original personalInfo.theOp.
            theOp.fileName = opFileName(theOp)
            ' See if the filename has changed.
            If ops(id).fileName <> theOp.fileName Then
                File.Delete(ops(id).pathName)
            End If
            ops.RemoveAt(id)
            ops.Insert(id, theOp)
            ' Reset the default if changed.
            ' Note we only allow for this one becoming the default.
            If (dfltID <> id) And theOp.DefaultFlag Then
                DefaultID = id ' handles the reset.
            End If
            write(theOp)
            ' Report the change if the current one.
            If id = CurrentID Then
                reportEvent()
            End If
        End If
        Return rv
    End Function

    Public Function RemoveAt(ByVal id As Integer) As Boolean
        File.Delete(ops(id).pathName)
        ops.RemoveAt(id)
        If ops.Count = 0 Then
            dfltID = -1
            CurrentID = -1
        ElseIf dfltID = id Then
            ' We removed the default; use the first one.
            dfltID = 0
            ops(0).DefaultFlag = True
            write(ops(0))
        ElseIf dfltID > id Then
            ' Allow for the deletion.
            dfltID = dfltID - 1
        End If
        ' Set current id, if must be -1, that's already done.
        If id = CurrentID Then
            ' removing the current, set it to the default.
            CurrentID = DefaultID
            ' Report the change, current is changed.
            reportEvent()
        ElseIf id < CurrentID Then
            ' Allow for deletion
            CurrentID -= 1
            ' No report since the current item itself didn't change.
        End If
        Return True
    End Function

    ''' <summary>
    ''' Update the log file for the specified operator
    ''' </summary>
    ''' <param name="op">usually CurrentOp</param>
    ''' <param name="name">file name</param>
    Friend Sub UpdateLogfile(op As personal_v1, name As String)
        op.LogFile = name
        ' We might be in mid-add, don't write.
        If Not addFlag Then
            write(op)
        End If
    End Sub

    ''' <summary>
    ''' Update the cw text for the specified operator
    ''' </summary>
    ''' <param name="op">usually CurrentOP</param>
    ''' <param name="cwt">MessageItem array</param>
    Friend Sub UpdateCWText(op As personal_v1, cwt As MessageItem())
        op.CWText = cwt
        write(op)
    End Sub

    Friend Sub UpdateCWDecode(op As personal_v1, con As Boolean, cells As Integer)
        op.ConstrainedDecode = con
        op.CWDecodeCells = cells
        write(op)
    End Sub

    ''' <summary>
    ''' Update the current operator's optional messages info
    ''' </summary>
    ''' <param name="items">OptionalMessageElements array</param>
    Friend Function UpdateOptionalMessages(items As OptionalMessageElement()) As Boolean
        Dim op As personal_v1 = CurrentItem
        If (items Is Nothing) OrElse (items.Length = 0) Then
            op.OptionalMessages = Nothing
        Else
            ReDim op.OptionalMessages(items.Length - 1)
            For i As Integer = 0 To items.Length - 1
                op.OptionalMessages(i) = New OptionalMessageConfig(items(i).Tag, items(i).Result, items(i).Ignore)
            Next
        End If
        write(op)
        Return True
    End Function

    ''' <summary>
    ''' Get current operator's optional message info
    ''' </summary>
    ''' <returns>array of OptionalMessageElements</returns>
    Friend Function RetrieveOptionalMessages() As OptionalMessageElement()
        Dim op As personal_v1 = CurrentItem
        If op.OptionalMessages Is Nothing Then
            Return Nothing
        End If
        Dim rv(op.OptionalMessages.Length - 1) As OptionalMessageElement
        For i As Integer = 0 To rv.Length - 1
            rv(i) = New OptionalMessageElement(op.OptionalMessages(i).Tag, op.OptionalMessages(i).Result, op.OptionalMessages(i).Ignore)
        Next
        Return rv
    End Function

    ''' <summary>
    ''' Update the current operator's config info.
    ''' </summary>
    Friend Sub UpdateCurrentOp()
        write(CurrentOp)
    End Sub

    Friend Sub Write(ByVal op As personal_v1)
        ' Encrypt credentials before writing to disk.
        op.EncryptCredentials()

        Dim cfgFile As Stream = Nothing
        Try
            cfgFile = File.Open(op.pathName, FileMode.Create)
            Dim xs As New XmlSerializer(GetType(personal_v1))
            xs.Serialize(cfgFile, op)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        End Try
        If cfgFile IsNot Nothing Then
            cfgFile.Dispose()
        End If
    End Sub

    Friend Event ConfigEvent As EventHandler(Of ConfigArgs)
    Friend Sub reportEvent()
        RaiseEvent ConfigEvent(Me, New ConfigArgs(ConfigEvents.OperatorChanged, CurrentItem))
    End Sub
End Class

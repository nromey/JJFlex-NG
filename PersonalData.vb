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
        ''' Optional message tags and values. RETIRED (#1108): nothing shows
        ''' one of Jim's optional messages any more, and every "don't show
        ''' again" goes through AdvisorySuppression. Kept, and never written,
        ''' so an operator file that carries it still loads and re-saves with
        ''' it intact.
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
        ''' When True, the "no local radios found" guidance dialog is suppressed.
        ''' Instead, a brief screen reader announcement plays.
        ''' </summary>
        Public SuppressNoRadiosHint As Boolean = False

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
        ''' Modern mode coarse tuning step in Hz. Default 5000 (5 kHz).
        ''' Used with Up/Down arrow keys in Modern mode.
        ''' </summary>
        Public CoarseTuneStep As Integer = 5000

        ''' <summary>
        ''' Modern mode fine tuning step in Hz. Default 100 (100 Hz).
        ''' Used with Shift+Up/Down arrow keys in Modern mode.
        ''' </summary>
        Public FineTuneStep As Integer = 100

        ''' <summary>
        ''' The operator's own coarse step list in Hz (#346 part two, ruled by
        ''' Noel 2026-11-09 20:39 and 2026-11-11 11:45). Nothing (the default,
        ''' and every file written before this) means the starting list; it is
        ''' edited in the list S opens and fed to Radios.TuningSteps, never kept
        ''' as a second table.
        ''' </summary>
        Public CoarseStepList As Integer() = Nothing

        ''' <summary>
        ''' The operator's own fine step list in Hz; see CoarseStepList.
        ''' </summary>
        Public FineStepList As Integer() = Nothing

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
            SuppressNoRadiosHint = p.SuppressNoRadiosHint
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
            ' The tuning steps travel with an edited copy (#346 J2). Before this,
            ' saving any edit in the Operators dialog (a callsign) wrote the
            ' 5000 Hz and 100 Hz defaults over the operator's remembered steps.
            CoarseTuneStep = p.CoarseTuneStep
            FineTuneStep = p.FineTuneStep
            ' ...and the step lists (#346 part two), for the same reason: an
            ' Operators dialog save writes this copy back over the record.
            CoarseStepList = If(p.CoarseStepList Is Nothing, Nothing, CType(p.CoarseStepList.Clone(), Integer()))
            FineStepList = If(p.FineStepList Is Nothing, Nothing, CType(p.FineStepList.Clone(), Integer()))
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

    ''' <summary>
    ''' One saved optional-message answer. RETIRED (#1108), kept only so old
    ''' operator files still read; see personal_v1.OptionalMessages.
    ''' </summary>
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
        ' Less any old file a rename could not remove (#1241).
        Dim opFiles As String() = OperatorFilesToLoad(opsDir)
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
        ' Use callsign as primary key when available, fall back to fullName_handle
        If Not String.IsNullOrWhiteSpace(op.callSign) Then
            Dim fn As String = op.callSign & "_" & op.fullName
            Return tst.Replace(fn, "_")
        End If
        Dim fn2 As String = op.fullName & "_" & op.handl
        Return tst.Replace(fn2, "_")
    End Function

    ''' <summary>
    ''' Legacy operator name format (pre-Sprint 18) for config file migration.
    ''' </summary>
    Friend Shared Function LegacyOpName(ByVal op As personal_v1) As String
        Dim tst As New Regex("[^0-9a-zA-Z]")
        Dim fn As String = op.fullName & "_" & op.handl
        Return tst.Replace(fn, "_")
    End Function

    ''' <summary>
    ''' Migrate config files from legacy naming (fullName_handle) to callsign naming.
    ''' Renames files in the config directory. Safe to call multiple times.
    ''' </summary>
    Friend Shared Sub MigrateConfigFiles(op As personal_v1, configDir As String)
        If String.IsNullOrWhiteSpace(op.callSign) Then Return
        Dim newName = UniqueOpName(op)
        Dim legacyName = LegacyOpName(op)
        If newName = legacyName Then Return ' no change needed

        Try
            For Each legacyFile In IO.Directory.GetFiles(configDir, legacyName & "_*")
                Dim suffix = IO.Path.GetFileName(legacyFile).Substring(legacyName.Length)
                Dim newFile = IO.Path.Combine(configDir, newName & suffix)
                If Not IO.File.Exists(newFile) Then
                    IO.File.Move(legacyFile, newFile)
                    JJTrace.Tracing.TraceLine($"Config migrated: {IO.Path.GetFileName(legacyFile)} → {IO.Path.GetFileName(newFile)}", Diagnostics.TraceLevel.Info)
                End If
            Next
            ' Also check the Radios subdirectory (filter presets)
            Dim radiosDir = IO.Path.Combine(configDir, "Radios")
            If IO.Directory.Exists(radiosDir) Then
                For Each legacyFile In IO.Directory.GetFiles(radiosDir, legacyName & "_*")
                    Dim suffix = IO.Path.GetFileName(legacyFile).Substring(legacyName.Length)
                    Dim newFile = IO.Path.Combine(radiosDir, newName & suffix)
                    If Not IO.File.Exists(newFile) Then
                        IO.File.Move(legacyFile, newFile)
                        JJTrace.Tracing.TraceLine($"Config migrated: Radios/{IO.Path.GetFileName(legacyFile)} → {IO.Path.GetFileName(newFile)}", Diagnostics.TraceLevel.Info)
                    End If
                Next
            End If
        Catch ex As Exception
            JJTrace.Tracing.TraceLine($"Config migration error: {ex.Message}", Diagnostics.TraceLevel.Error)
        End Try
    End Sub

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
            ' #1195: saved first, and the new operator joins the list, and
            ' becomes the default, only when the save succeeded (SaveThenAdd),
            ' as an edit does (SaveThenReplace, #1175). Added first, a failed
            ' save left an operator in the list that was not on disk, and an
            ' "Exception" box read the .NET message and stack trace aloud.
            rv = SaveThenAdd(ops, theOp, Function() TryWrite(theOp), OperatorNotSavedNotice)
            If rv >= 0 AndAlso theOp.DefaultFlag Then
                DefaultID = rv
            End If
        End If
        ' No event is reported.
        addFlag = False
        Return rv
    End Function

    ''' <summary>
    ''' Save a new entry, and only once the save has succeeded add it to
    ''' <paramref name="entries"/>. A save that fails leaves the list as it
    ''' was and says so once, through <paramref name="notSaved"/> (#1195).
    ''' </summary>
    ''' <returns>The new entry's index, or -1 when the save failed.</returns>
    Friend Shared Function SaveThenAdd(Of T)(entries As IList(Of T), added As T,
                                            save As Func(Of Boolean), notSaved As Action) As Integer
        If Not save() Then
            Tracing.TraceLine("PersonalData: the new operator was not saved; the list is unchanged", TraceLevel.Error)
            If notSaved IsNot Nothing Then notSaved()
            Return -1
        End If
        entries.Add(added)
        Return entries.Count - 1
    End Function

    ''' <summary>
    ''' Tells the operator plainly that an operator they added or edited was
    ''' not saved (#1195): a plain box over the operator list, carrying one
    ''' sentence and no exception text. Replaceable so a test never opens it.
    ''' </summary>
    Friend Shared OperatorNotSavedNotice As Action = AddressOf ShowOperatorNotSaved

    Private Shared Sub ShowOperatorNotSaved()
        ' DRAFT wording, settings.operator.not_saved. No owner, as the
        ' operator list's own boxes have none: it sits over the list.
        MessageBox.Show(Radios.Lexicon.Get("settings.operator.not_saved"),
                        Radios.Lexicon.Get("settings.lister.operator_title"), MessageBoxButtons.OK)
    End Sub

    Public Function Update(ByVal id As Integer) As Boolean
        PersonalInfo.theOp = ops(id)
        Dim rv As Boolean = (PersonalInfo.ShowDialog = DialogResult.OK)
        If rv Then
            Dim theOp As personal_v1 = PersonalInfo.theOp
            ' Note theOp won't be the same as the original personalInfo.theOp.
            theOp.fileName = opFileName(theOp)
            ' The file the operator was in. A changed name moves the operator to
            ' a new file, and the old one goes only once the new one is written
            ' (SaveThenRetireOld): deleted first, a failed write lost the record.
            Dim oldPath As String = ops(id).pathName
            ' #1175: the edited operator replaces the old one in memory only
            ' after the save succeeds (SaveThenReplace). Swapped first, a failed
            ' save left the session holding the new name, so the next good save
            ' took the NEW path as the old one and never retired the old file,
            ' and both loaded at the next start.
            If SaveThenReplace(ops, id, theOp, oldPath, theOp.pathName, Function() TryWrite(theOp)) Then
                ' Reset the default if changed.
                ' Note we only allow for this one becoming the default.
                If (dfltID <> id) And theOp.DefaultFlag Then
                    DefaultID = id ' handles the reset.
                End If
                ' Report the change if the current one.
                If id = CurrentID Then
                    reportEvent()
                End If
            Else
                ' #1195: said once, plainly; TryWrite no longer opens the box.
                If OperatorNotSavedNotice IsNot Nothing Then OperatorNotSavedNotice()
            End If
        End If
        Return rv
    End Function

    ''' <summary>
    ''' Save an edited entry, retiring the file it used to be in
    ''' (<see cref="SaveThenRetireOld"/>), and only once the save has succeeded
    ''' put it in place of the old entry in <paramref name="entries"/>. A save
    ''' that fails leaves the old entry in memory and its file on disk, so the
    ''' session and the disk still agree about where the operator lives (#1175).
    ''' </summary>
    ''' <returns>True when the save succeeded and the entry was replaced.</returns>
    Friend Shared Function SaveThenReplace(Of T)(entries As IList(Of T), id As Integer, edited As T,
                                                 oldPath As String, newPath As String, save As Func(Of Boolean)) As Boolean
        If Not SaveThenRetireOld(oldPath, newPath, save) Then Return False
        entries(id) = edited
        Return True
    End Function

    ''' <summary>
    ''' Save an edited operator, then remove the file it used to be in when the
    ''' edit moved it to a new one. The old file is removed ONLY after the save
    ''' reports success, so a save that fails leaves the operator's record on
    ''' disk where it was. Paths are compared ignoring case: a callsign whose
    ''' letters only changed case names the same file on Windows, and removing
    ''' the "old" one would remove the file just written.
    ''' </summary>
    ''' <param name="oldPath">The file the operator was in before the edit.</param>
    ''' <param name="newPath">The file the edited operator is saved to.</param>
    ''' <param name="save">Writes the edited operator to newPath; true when it was written.</param>
    ''' <returns>True when the save succeeded.</returns>
    Friend Shared Function SaveThenRetireOld(oldPath As String, newPath As String, save As Func(Of Boolean)) As Boolean
        If Not save() Then
            Tracing.TraceLine("PersonalData: the edited operator was not saved; its old file is kept", TraceLevel.Error)
            Return False
        End If
        If Not String.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) Then
            ' An earlier rename's old file, still on record, now belongs to the
            ' newest file (#1241).
            RetargetRetiredFiles(oldPath, newPath)
            Try
                File.Delete(oldPath)
            Catch ex As Exception
                ' The edit is saved; an old file left behind costs a stale copy, not the record.
                ' Traced only (#1195): the "Exception" box read the .NET message and
                ' stack trace to the operator about a save that had succeeded.
                Tracing.TraceLine("PersonalData: the operator was saved, but the old file was not removed: " & ex.Message, TraceLevel.Error)
                ' Recorded, so the next start neither loads it as a second
                ' operator nor forgets it (#1241).
                RecordRetiredFile(oldPath, newPath)
            End Try
        End If
        Return True
    End Function

    ''' <summary>
    ''' The record of operator files a rename saved past but could not remove
    ''' (#1241). One line per file: the old file's name, the renamed file's
    ''' name and the time the delete failed (UTC ticks), separated by tabs.
    ''' It sits beside the operator files and is settled at the next start by
    ''' <see cref="SettleRetiredFiles"/>.
    '''
    ''' <para><b>Never an .xml file.</b> The loader reads every *.xml in the
    ''' folder as an operator; a file that is not one fails both of its reads,
    ''' and the second failure is not caught, so an .xml record would stop
    ''' startup.</para>
    ''' </summary>
    Friend Const RetiredFilesRecordName As String = "retired-operator-files.txt"

    ''' <summary>
    ''' Note an old operator file that could not be removed after its renamed
    ''' copy was saved, so the next start neither loads it as a second operator
    ''' nor forgets to remove it. A failure to write the note is traced only:
    ''' the save it follows has succeeded.
    ''' </summary>
    Friend Shared Sub RecordRetiredFile(oldPath As String, newPath As String)
        Try
            Dim line As String = Path.GetFileName(oldPath) & vbTab & Path.GetFileName(newPath) & vbTab &
                DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(oldPath), RetiredFilesRecordName), line & vbCrLf)
            Tracing.TraceLine("PersonalData: the old operator file is recorded, to be removed at the next start: " & oldPath, TraceLevel.Info)
        Catch ex As Exception
            Tracing.TraceLine("PersonalData: the old operator file could not be recorded: " & ex.Message, TraceLevel.Error)
        End Try
    End Sub

    ''' <summary>
    ''' A later rename moved an operator whose earlier old file is still on
    ''' record: the record now names the newest file, so the old one is still
    ''' known to be a stale copy of a saved operator.
    ''' </summary>
    Private Shared Sub RetargetRetiredFiles(oldPath As String, newPath As String)
        Try
            Dim recordPath As String = Path.Combine(Path.GetDirectoryName(oldPath), RetiredFilesRecordName)
            If Not File.Exists(recordPath) Then Return
            Dim was As String = Path.GetFileName(oldPath)
            Dim lines As String() = File.ReadAllLines(recordPath)
            Dim changed As Boolean = False
            For i As Integer = 0 To lines.Length - 1
                Dim parts As String() = lines(i).Split(vbTab)
                If parts.Length = 3 AndAlso String.Equals(parts(1), was, StringComparison.OrdinalIgnoreCase) Then
                    parts(1) = Path.GetFileName(newPath)
                    lines(i) = String.Join(vbTab, parts)
                    changed = True
                End If
            Next
            If changed Then AtomicFile.WriteAllText(recordPath, String.Join(vbCrLf, lines) & vbCrLf)
        Catch ex As Exception
            Tracing.TraceLine("PersonalData: the retired operator record could not be updated: " & ex.Message, TraceLevel.Error)
        End Try
    End Sub

    ''' <summary>
    ''' Settle the retired-file record at startup (#1241), before any operator
    ''' is read, and return the names of the files NOT to load.
    '''
    ''' For each entry: the old file is gone, so the entry is dropped; the old
    ''' file was written after the record, so it is a new operator who reuses
    ''' the name, and it is kept and loaded; the renamed file is missing, so the
    ''' old file is the only copy, and it is kept and loaded; otherwise the
    ''' delete is tried again, and a file that still cannot be removed is
    ''' skipped, never loaded, and kept on record. Only a file the record names,
    ''' in this folder, is ever touched. Each outcome is traced. Anything that
    ''' goes wrong here is traced and startup carries on.
    ''' </summary>
    Friend Shared Function SettleRetiredFiles(dir As String) As HashSet(Of String)
        Dim skip As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Try
            Dim recordPath As String = Path.Combine(dir, RetiredFilesRecordName)
            If Not File.Exists(recordPath) Then Return skip
            Dim keep As New List(Of String)
            For Each line As String In File.ReadAllLines(recordPath)
                If String.IsNullOrWhiteSpace(line) Then Continue For
                Dim parts As String() = line.Split(vbTab)
                Dim ticks As Long
                If parts.Length <> 3 OrElse
                   Not Long.TryParse(parts(2), System.Globalization.NumberStyles.None,
                                     System.Globalization.CultureInfo.InvariantCulture, ticks) OrElse
                   Not IsOperatorFileName(parts(0)) OrElse Not IsOperatorFileName(parts(1)) OrElse
                   String.Equals(parts(0), parts(1), StringComparison.OrdinalIgnoreCase) Then
                    Tracing.TraceLine("PersonalData: an unreadable retired-file entry was dropped: " & line, TraceLevel.Warning)
                    Continue For
                End If
                Dim oldFile As String = Path.Combine(dir, parts(0))
                Dim newFile As String = Path.Combine(dir, parts(1))
                If Not File.Exists(oldFile) Then
                    Tracing.TraceLine("PersonalData: retired operator file already gone: " & oldFile, TraceLevel.Info)
                    Continue For
                End If
                If File.GetLastWriteTimeUtc(oldFile).Ticks > ticks Then
                    ' Written after the record, so not the stale copy it names: an
                    ' operator saved since under the same name. Kept and loaded.
                    Tracing.TraceLine("PersonalData: a newer operator file reuses a retired name; kept: " & oldFile, TraceLevel.Info)
                    Continue For
                End If
                If Not File.Exists(newFile) Then
                    ' The renamed copy is gone, so this is the operator's only
                    ' record. Never lose it: load it as it is.
                    Tracing.TraceLine("PersonalData: the renamed operator file is missing; the old one is kept and loaded: " & oldFile, TraceLevel.Warning)
                    Continue For
                End If
                Try
                    File.Delete(oldFile)
                    Tracing.TraceLine("PersonalData: removed the retired operator file: " & oldFile, TraceLevel.Info)
                Catch ex As Exception
                    Tracing.TraceLine("PersonalData: the retired operator file still cannot be removed, and is not loaded: " &
                                      oldFile & " (" & ex.Message & ")", TraceLevel.Warning)
                    skip.Add(parts(0))
                    keep.Add(line)
                End Try
            Next
            If keep.Count = 0 Then
                File.Delete(recordPath)
            Else
                AtomicFile.WriteAllText(recordPath, String.Join(vbCrLf, keep) & vbCrLf)
            End If
        Catch ex As Exception
            Tracing.TraceLine("PersonalData: the retired operator record could not be settled: " & ex.Message, TraceLevel.Error)
        End Try
        Return skip
    End Function

    ''' <summary>A bare operator file name: no folder part, an .xml file.</summary>
    Private Shared Function IsOperatorFileName(name As String) As Boolean
        Return Not String.IsNullOrWhiteSpace(name) AndAlso
            String.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) AndAlso
            name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
    End Function

    ''' <summary>
    ''' The operator files to read at startup: every *.xml in the folder, less
    ''' any a rename retired and could not remove (<see cref="SettleRetiredFiles"/>).
    ''' </summary>
    Friend Shared Function OperatorFilesToLoad(dir As String) As String()
        Dim skip As HashSet(Of String) = SettleRetiredFiles(dir)
        Dim files As New List(Of String)
        For Each fn As String In GetFiles(dir, "*.xml")
            If Not skip.Contains(Path.GetFileName(fn)) Then files.Add(fn)
        Next
        Return files.ToArray()
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
    ''' Update the current operator's config info.
    ''' </summary>
    Friend Sub UpdateCurrentOp()
        write(CurrentOp)
    End Sub

    ''' <summary>
    ''' Write, showing today's error box when the write fails. Every caller
    ''' other than Add and Update comes here (#1195 left them as they were;
    ''' the box's general fate is a separate entry).
    ''' </summary>
    Friend Sub Write(ByVal op As personal_v1)
        If Not TryWrite(op) Then
            Tracing.ErrMessageTrace(LastWriteFailure)
        End If
    End Sub

    ''' <summary>
    ''' What the last failed <see cref="TryWrite"/> caught; Nothing after a
    ''' write that succeeded.
    ''' </summary>
    Friend Property LastWriteFailure As Exception

    ''' <summary>
    ''' Write, saying whether the operator was written: false when the write
    ''' failed (traced, never shown: the caller decides what the operator is
    ''' told, and <see cref="LastWriteFailure"/> holds the reason). For a caller
    ''' that must not go on as though it had been, such as removing the
    ''' operator's old file.
    ''' </summary>
    Friend Function TryWrite(ByVal op As personal_v1) As Boolean
        ' Encrypt credentials before writing to disk.
        op.EncryptCredentials()
        LastWriteFailure = Nothing

        ' Whole or not at all: an operator file cut short by a failure part-way
        ' through would lose the operator.
        Try
            Dim xs As New XmlSerializer(GetType(personal_v1))
            AtomicFile.Write(op.pathName, Sub(cfgFile) xs.Serialize(cfgFile, op))
            Return True
        Catch ex As Exception
            ' No box here (#1195): Write shows it for its callers, and Add and
            ' Update say one plain sentence instead of the exception's text.
            LastWriteFailure = ex
            Tracing.TraceLine("PersonalData.TryWrite: the operator file was not written: " & ex.ToString(), TraceLevel.Error)
            Return False
        End Try
    End Function

    Friend Event ConfigEvent As EventHandler(Of ConfigArgs)
    Friend Sub reportEvent()
        RaiseEvent ConfigEvent(Me, New ConfigArgs(ConfigEvents.OperatorChanged, CurrentItem))
    End Sub
End Class

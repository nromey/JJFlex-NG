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

    Private createdOnLoad As Boolean = False
    ''' <summary>
    ''' True when loading found no operator and the operator form, which the
    ''' constructor opens itself, added one (#1306). The first run goes this
    ''' way: the new operator is already the default when the constructor
    ''' returns, so the caller's own "no default operator" loop never runs.
    ''' </summary>
    Public ReadOnly Property CreatedOperatorOnLoad As Boolean
        Get
            Return createdOnLoad
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
            ' Create initial operator. Remembered for the startup reminder (#1306).
            createdOnLoad = (Add() >= 0)
        Else
            ' Get operators. A file that cannot be read is traced, skipped and
            ' left where it is, and the rest still load (#1253): one bad file
            ' used to show an "Exception" box with the stack and stop the loop,
            ' so no operator loaded at all.
            '
            ' Every file is read BEFORE any operator is set up, because setting
            ' up writes (the old-format conversion, the credential migration)
            ' and an operator's write goes to the path named INSIDE its file,
            ' which can be another file's: a file that failed to read must be
            ' known as such before the first write can reach it (TryWrite).
            Dim notLoaded As New List(Of String)
            Dim reads As New List(Of OperatorFileRead)
            For Each fn As String In opFiles
                Dim read As OperatorFileRead = ReadOperatorFile(fn)
                If read.Op Is Nothing Then NoteNotLoaded(fn)
                reads.Add(read)
            Next
            For Each read As OperatorFileRead In reads
                Dim fn As String = read.FilePath
                Dim p As personal_v1 = read.Op
                If p IsNot Nothing Then
                    Try
                        setupOpData(p, read.Converted)
                    Catch ex As Exception
                        Tracing.TraceLine("PersonalData: the operator file " & fn &
                                          " was read but could not be set up, so it is not loaded and is left where it is: " &
                                          ex.Message, TraceLevel.Error)
                        p = Nothing
                    End Try
                End If
                If p Is Nothing Then
                    NoteNotLoaded(fn)
                    notLoaded.Add(Path.GetFileName(fn))
                    Continue For
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
            ' Said once, after the loop, naming every file that was skipped;
            ' no exception text (#1253). The trace has each file's reason.
            If notLoaded.Count > 0 AndAlso OperatorFilesNotLoadedNotice IsNot Nothing Then
                OperatorFilesNotLoadedNotice(notLoaded)
            End If
        End If
    End Sub

    ''' <summary>
    ''' What reading one operator file produced (#1253): the operator, or
    ''' Nothing and the reason it could not be read.
    ''' </summary>
    Friend NotInheritable Class OperatorFileRead
        ''' <summary>The file that was read.</summary>
        Public ReadOnly FilePath As String
        ''' <summary>The operator, or Nothing when the file could not be read.</summary>
        Public ReadOnly Op As personal_v1
        ''' <summary>True when the file was in the old format and was converted.</summary>
        Public ReadOnly Converted As Boolean
        ''' <summary>Why the file could not be read; Nothing when it was.</summary>
        Public ReadOnly Failure As String

        Friend Sub New(filePath As String, op As personal_v1, converted As Boolean, failure As String)
            Me.FilePath = filePath
            Me.Op = op
            Me.Converted = converted
            Me.Failure = failure
        End Sub
    End Class

    ''' <summary>
    ''' Read one operator file, in the current format or the old one, and say
    ''' what happened. Never throws and never shows anything: a file that
    ''' cannot be opened (permissions deny it, or another program holds it),
    ''' does not parse, or fails the old format's read as well comes back with
    ''' no operator and its reason, traced (#1253).
    '''
    ''' <para>Opened for reading only and shared for reading, so a read-only
    ''' file loads. It used to be opened read-write, which a read-only file
    ''' refuses.</para>
    ''' </summary>
    Friend Shared Function ReadOperatorFile(fn As String) As OperatorFileRead
        Try
            Using cfgFile As New FileStream(fn, FileMode.Open, FileAccess.Read, FileShare.Read)
                Dim xs As New XmlSerializer(GetType(personal_v1))
                Return New OperatorFileRead(fn, CType(xs.Deserialize(cfgFile), personal_v1), False, Nothing)
            End Using
        Catch ex As InvalidOperationException
            ' Not the current format: see if it is the old one.
            Tracing.TraceLine("PersonalData: " & fn & " is not in the current operator format (" & ex.Message &
                              "); trying the old format", TraceLevel.Warning)
            Try
                Dim pOld As personal
                Using cfgFile As New FileStream(fn, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Dim xs As New XmlSerializer(GetType(personal))
                    pOld = CType(xs.Deserialize(cfgFile), personal)
                End Using
                ' Convert to the current format.
                Return New OperatorFileRead(fn, New personal_v1(pOld), True, Nothing)
            Catch exOld As Exception
                Return NotRead(fn, "it is in neither operator format (" & ex.Message & "; " & exOld.Message & ")")
            End Try
        Catch ex As Exception
            Return NotRead(fn, "it could not be opened (" & ex.GetType().Name & ": " & ex.Message & ")")
        End Try
    End Function

    Private Shared Function NotRead(fn As String, reason As String) As OperatorFileRead
        Tracing.TraceLine("PersonalData: the operator file " & fn & " was not loaded, and is left where it is: " & reason,
                          TraceLevel.Error)
        Return New OperatorFileRead(fn, Nothing, False, reason)
    End Function

    ''' <summary>
    ''' The operator files that could not be loaded this session (#1253), by
    ''' full path, ignoring case. An operator's write goes to the path named
    ''' inside its file (<see cref="personal_v1.pathName"/>), so a save can
    ''' reach one of these: <see cref="TryWrite"/> keeps a copy first, and
    ''' <see cref="RemoveAt"/> and <see cref="Update"/> never delete one.
    ''' </summary>
    Private ReadOnly notLoadedFiles As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

    Private Sub NoteNotLoaded(fn As String)
        notLoadedFiles.Add(FullPathOrSelf(fn))
    End Sub

    ''' <summary>True when <paramref name="path"/> is a file that could not be loaded this session.</summary>
    Friend Function FailedToLoadThisSession(path As String) As Boolean
        Return Not String.IsNullOrEmpty(path) AndAlso notLoadedFiles.Contains(FullPathOrSelf(path))
    End Function

    Private Shared Function FullPathOrSelf(p As String) As String
        Try
            Return Path.GetFullPath(p)
        Catch
            Return p
        End Try
    End Function

    ''' <summary>
    ''' Tells the operator once, after start-up's load, which operator files
    ''' could not be loaded and that each was left where it is (#1253): a
    ''' plain box carrying one sentence and no exception text. Replaceable so a
    ''' test never opens it. Receives the files' names.
    ''' </summary>
    Friend Shared OperatorFilesNotLoadedNotice As Action(Of IReadOnlyList(Of String)) = AddressOf ShowOperatorFilesNotLoaded

    Private Shared Sub ShowOperatorFilesNotLoaded(names As IReadOnlyList(Of String))
        ' DRAFT wording, settings.operator.not_loaded. No owner, as the
        ' operator list's own boxes have none.
        MessageBox.Show(Radios.Lexicon.Get("settings.operator.not_loaded", ("files", CObj(String.Join(", ", names)))),
                        Radios.Lexicon.Get("settings.lister.operator_title"), MessageBoxButtons.OK)
    End Sub

    ''' <summary>
    ''' Tells the operator that changes to an operator cannot be kept while its
    ''' file is read-only (#1253): once per file per session, for a save. A
    ''' failed delete has its own notice. Replaceable so a test
    ''' never opens it. Receives the file's name.
    ''' </summary>
    Friend Shared OperatorFileReadOnlyNotice As Action(Of String) = AddressOf ShowOperatorFileReadOnly

    Private Shared Sub ShowOperatorFileReadOnly(name As String)
        ' DRAFT wording, settings.operator.read_only.
        MessageBox.Show(Radios.Lexicon.Get("settings.operator.read_only", ("file", CObj(name))),
                        Radios.Lexicon.Get("settings.lister.operator_title"), MessageBoxButtons.OK)
    End Sub

    ''' <summary>
    ''' Tells the operator that Delete could not remove an operator file
    ''' (#1253 repair 2): for any failure, read-only or held open by another
    ''' program, so it never claims a cause. Once per failed Delete, with no
    ''' once-per-file gate. Replaceable so a test never opens it. Receives the
    ''' file's name.
    ''' </summary>
    Friend Shared OperatorFileNotDeletedNotice As Action(Of String) = AddressOf ShowOperatorFileNotDeleted

    Private Shared Sub ShowOperatorFileNotDeleted(name As String)
        ' DRAFT wording, settings.operator.delete_failed.
        MessageBox.Show(Radios.Lexicon.Get("settings.operator.delete_failed", ("file", CObj(name))),
                        Radios.Lexicon.Get("settings.lister.operator_title"), MessageBoxButtons.OK)
    End Sub

    ''' <summary>The read-only files the operator has already been told about this session.</summary>
    Private ReadOnly toldReadOnly As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

    Private Shared Function IsReadOnlyFile(p As String) As Boolean
        Try
            Return File.Exists(p) AndAlso (File.GetAttributes(p) And FileAttributes.ReadOnly) = FileAttributes.ReadOnly
        Catch
            Return False
        End Try
    End Function
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

    ' ---------------- one licence class per operator (#1239) ----------------

    ''' <summary>
    ''' The licence class the operator form opens on (#1239): Settings' class,
    ''' from the licence file the transmit lockout reads, because Settings wins
    ''' where the two disagree (Noel, 2026-11-23). <c>Stored</c> is what that file
    ''' holds (Extra when there is no file, or it does not read, as it always
    ''' has); <c>Shown</c> is it as one of the four offered classes. A new
    ''' operator (<paramref name="opName"/> Nothing) opens on General.
    ''' </summary>
    Friend Shared Function LicenseClassAtOpen(configDir As String, legacyDir As String, opName As String) As (Stored As Bands.Licenses, Shown As Bands.Licenses)
        If opName Is Nothing Then Return (LicenseConfig.NewOperatorClass, LicenseConfig.NewOperatorClass)
        Dim firstRun As Boolean
        Dim stored As Bands.Licenses = LicenseConfig.LoadOrCarry(configDir, legacyDir, opName, firstRun).LicenseClass
        Return (stored, LicenseConfig.ShownClass(stored))
    End Function

    ''' <summary>
    ''' The class the operator record's own <c>License</c> carries after a form
    ''' save (#1239), kept in step with the licence file, which the band display
    ''' (ShowBands) reads: the chosen class when it was chosen (or the operator
    ''' is new), otherwise the class the licence file already holds.
    ''' </summary>
    Friend Shared Function RecordLicenseClass(stored As Bands.Licenses, shown As Bands.Licenses, chosen As Bands.Licenses,
                                              isNew As Boolean) As Bands.Licenses
        Return If(isNew OrElse chosen <> shown, chosen, stored)
    End Function

    ''' <summary>
    ''' Write the class chosen on the operator form into the operator's licence
    ''' file (#1239), called only once the operator itself has been saved, and
    ''' before any change is reported, so the reload reads the new class. The
    ''' rest of the file (the transmit lockout, boundary notices, country and
    ''' the 60 m rules) is kept as it is (<see cref="LicenseConfig.SaveClass"/>).
    ''' A new operator gets a file; an existing one is written only when its
    ''' class was changed, or its name was, in which case the file is carried to
    ''' the new name. An untouched class leaves the file, and a stored class
    ''' the form cannot offer, exactly as they were. A file that does not read
    ''' is left in place.
    ''' </summary>
    Friend Shared Function SaveOperatorLicense(configDir As String, legacyDir As String, oldName As String, newName As String,
                                               shown As Bands.Licenses, chosen As Bands.Licenses,
                                               isNew As Boolean) As LicenseConfig.ClassSave
        Dim classChanged As Boolean = isNew OrElse chosen <> shown
        Dim renamed As Boolean = Not isNew AndAlso Not String.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)
        If Not classChanged AndAlso Not renamed Then Return LicenseConfig.ClassSave.Unchanged
        Dim cls As Bands.Licenses? = Nothing
        If classChanged Then cls = chosen
        Dim result As LicenseConfig.ClassSave =
            LicenseConfig.SaveClass(configDir, legacyDir, If(isNew, newName, oldName), newName, cls, classChanged)
        Tracing.TraceLine("PersonalData: the operator form's licence class for " & newName & ": " & result.ToString() &
                          If(classChanged, " (" & chosen.ToString() & ")", " (carried to the new name)"),
                          If(result = LicenseConfig.ClassSave.Saved OrElse result = LicenseConfig.ClassSave.Unchanged OrElse
                             result = LicenseConfig.ClassSave.NothingToCarry, TraceLevel.Info, TraceLevel.Warning))
        Return result
    End Function

    ''' <summary>
    ''' The operator form's licence list position for <paramref name="cls"/>
    ''' (#1239): the list holds <see cref="LicenseConfig.OfferedClasses"/> in
    ''' that order, so the position is no longer the enum value. A class the
    ''' list does not hold opens on the first, Extra.
    ''' </summary>
    Friend Shared Function LicenseListIndex(cls As Bands.Licenses) As Integer
        For i As Integer = 0 To LicenseConfig.OfferedClasses.Count - 1
            If LicenseConfig.OfferedClasses(i) = cls Then Return i
        Next
        Return 0
    End Function

    ''' <summary>
    ''' The class at the operator form's licence list position
    ''' <paramref name="index"/> (#1239), the inverse of
    ''' <see cref="LicenseListIndex"/>; no selection (-1) keeps the class the
    ''' list opened on, <paramref name="shown"/>.
    ''' </summary>
    Friend Shared Function LicenseAtListIndex(index As Integer, shown As Bands.Licenses) As Bands.Licenses
        If index < 0 OrElse index >= LicenseConfig.OfferedClasses.Count Then Return shown
        Return LicenseConfig.OfferedClasses(index)
    End Function

    ''' <summary>The form's licence class for <paramref name="theOp"/>, saved after the operator was (#1239).</summary>
    Private Shared Sub SaveFormLicense(oldName As String, theOp As personal_v1, isNew As Boolean)
        ' Named, so the class the list opened on and the class it closed on
        ' cannot trade places: traded, every class change is dropped.
        SaveOperatorLicense(BaseConfigDir & "\Radios", BaseConfigDir, oldName, UniqueOpName(theOp),
                            shown:=PersonalInfo.LicenseShown, chosen:=PersonalInfo.LicenseChosen, isNew:=isNew)
    End Sub

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
            ' The licence class chosen on the form, only once the operator is
            ' saved: a failed save leaves no licence file behind (#1239).
            If rv >= 0 Then SaveFormLicense(Nothing, theOp, isNew:=True)
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
            ' The name its per-operator files go by, for the licence (#1239).
            Dim oldName As String = UniqueOpName(ops(id))
            ' #1253: an old file that could not be loaded this session is never
            ' retired (deleted) by the rename; it is left where it is, so the
            ' step is given the new path as the old one and removes nothing.
            If FailedToLoadThisSession(oldPath) AndAlso
               Not String.Equals(oldPath, theOp.pathName, StringComparison.OrdinalIgnoreCase) Then
                Tracing.TraceLine("PersonalData: the edit moves the operator, but its old file " & oldPath &
                                  " could not be loaded at start-up, so it is not removed", TraceLevel.Warning)
                oldPath = theOp.pathName
            End If
            ' #1175: the edited operator replaces the old one in memory only
            ' after the save succeeds (SaveThenReplace). Swapped first, a failed
            ' save left the session holding the new name, so the next good save
            ' took the NEW path as the old one and never retired the old file,
            ' and both loaded at the next start.
            If SaveThenReplace(ops, id, theOp, oldPath, theOp.pathName, Function() TryWrite(theOp)) Then
                ' The licence class chosen on the form, after the operator is
                ' saved and BEFORE the change is reported, so the reload below
                ' reads the new class into the live lockout (#1239).
                SaveFormLicense(oldName, theOp, isNew:=False)
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
    ''' (#1241). One line per file, its fields separated by tabs, read and
    ''' written only through <see cref="RetiredFileEntry"/>. It sits beside the
    ''' operator files and is settled at the next start by
    ''' <see cref="SettleRetiredFiles"/>.
    '''
    ''' <para><b>Never an .xml file.</b> The loader reads every *.xml in the
    ''' folder as an operator; a file that is not one fails both of its reads,
    ''' and the second failure is not caught, so an .xml record would stop
    ''' startup.</para>
    ''' </summary>
    Friend Const RetiredFilesRecordName As String = "retired-operator-files.txt"

    ''' <summary>
    ''' One entry of the retired-file record (#1241, #1254). The ONE reader and
    ''' writer of a record line: <see cref="SettleRetiredFiles"/>,
    ''' <see cref="RetargetRetiredFiles"/> and
    ''' <see cref="MarkRetiredFilesDeleted"/> all go through it, so a change of
    ''' format cannot leave one of them reading the old shape.
    '''
    ''' <para>A line is five fields: the old file's name, the renamed file's
    ''' name, the old file's OWN last write time (UTC ticks) and length in
    ''' bytes as they were when its delete failed, and <c>renamed</c> or
    ''' <c>deleted</c>. The old file is removed only while it still matches
    ''' that write time and length exactly, so no clock is ever compared: a
    ''' different operator saved later under the old name does not match,
    ''' whatever the clock said when it was written (#1254). <c>deleted</c>
    ''' means the renamed operator was later deleted in the Operators list, so
    ''' the old file is a copy of an operator that no longer exists and must
    ''' not come back.</para>
    '''
    ''' <para>The earlier format, three fields (old name, renamed name, the
    ''' time the delete failed), carries no fingerprint of the old file, so
    ''' nothing can show the file still there is the stale copy. Such a line
    ''' is read (<see cref="IsEarlierFormat"/>) only so that settling it keeps
    ''' and loads the old file: the safe failure is a second copy of an
    ''' operator, never a lost one.</para>
    ''' </summary>
    Friend NotInheritable Class RetiredFileEntry
        Friend Const RenamedState As String = "renamed"
        Friend Const DeletedState As String = "deleted"

        ''' <summary>The old file's name, in the operators folder.</summary>
        Public ReadOnly OldName As String
        ''' <summary>The renamed file's name, in the operators folder.</summary>
        Public NewName As String
        ''' <summary>The old file's own last write time (UTC ticks); for an earlier-format line, the time the delete failed.</summary>
        Public ReadOnly Ticks As Long
        ''' <summary>The old file's length in bytes; -1 for an earlier-format line.</summary>
        Public ReadOnly Length As Long
        ''' <summary>True once the renamed operator has been deleted.</summary>
        Public OperatorDeleted As Boolean

        Friend Sub New(oldName As String, newName As String, ticks As Long, length As Long, operatorDeleted As Boolean)
            Me.OldName = oldName
            Me.NewName = newName
            Me.Ticks = ticks
            Me.Length = length
            Me.OperatorDeleted = operatorDeleted
        End Sub

        ''' <summary>True for a three-field line from the earlier format, which carries no fingerprint of the old file.</summary>
        Public ReadOnly Property IsEarlierFormat As Boolean
            Get
                Return Length < 0
            End Get
        End Property

        ''' <summary>
        ''' True when <paramref name="oldFile"/> is still exactly the file that
        ''' was recorded: the same last write time and the same length. Never
        ''' true for an earlier-format line.
        ''' </summary>
        Friend Function StillMatches(oldFile As String) As Boolean
            If IsEarlierFormat Then Return False
            Dim info As New FileInfo(oldFile)
            Return info.LastWriteTimeUtc.Ticks = Ticks AndAlso info.Length = Length
        End Function

        ''' <summary>
        ''' Read one record line; Nothing for a line that is not a well-formed
        ''' entry naming two different bare operator files.
        ''' </summary>
        Friend Shared Function Parse(line As String) As RetiredFileEntry
            If String.IsNullOrWhiteSpace(line) Then Return Nothing
            Dim parts As String() = line.Split(vbTab)
            If parts.Length <> 3 AndAlso parts.Length <> 5 Then Return Nothing
            If Not IsOperatorFileName(parts(0)) OrElse Not IsOperatorFileName(parts(1)) OrElse
               String.Equals(parts(0), parts(1), StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim inv As System.Globalization.CultureInfo = System.Globalization.CultureInfo.InvariantCulture
            Dim ticks As Long
            If Not Long.TryParse(parts(2), System.Globalization.NumberStyles.None, inv, ticks) Then Return Nothing
            If parts.Length = 3 Then Return New RetiredFileEntry(parts(0), parts(1), ticks, -1, False)
            Dim length As Long
            If Not Long.TryParse(parts(3), System.Globalization.NumberStyles.None, inv, length) Then Return Nothing
            Dim deleted As Boolean
            If String.Equals(parts(4), DeletedState, StringComparison.Ordinal) Then
                deleted = True
            ElseIf Not String.Equals(parts(4), RenamedState, StringComparison.Ordinal) Then
                Return Nothing
            End If
            Return New RetiredFileEntry(parts(0), parts(1), ticks, length, deleted)
        End Function

        ''' <summary>The line for this entry; an earlier-format entry keeps its own three-field shape.</summary>
        Friend Function ToLine() As String
            Dim inv As System.Globalization.CultureInfo = System.Globalization.CultureInfo.InvariantCulture
            If IsEarlierFormat Then Return OldName & vbTab & NewName & vbTab & Ticks.ToString(inv)
            Return OldName & vbTab & NewName & vbTab & Ticks.ToString(inv) & vbTab & Length.ToString(inv) & vbTab &
                If(OperatorDeleted, DeletedState, RenamedState)
        End Function
    End Class

    ''' <summary>
    ''' Note an old operator file that could not be removed after its renamed
    ''' copy was saved, so the next start neither loads it as a second operator
    ''' nor forgets to remove it. The file's own last write time and length are
    ''' recorded, never the clock (#1254). A failure to write the note is
    ''' traced only: the save it follows has succeeded.
    ''' </summary>
    Friend Shared Sub RecordRetiredFile(oldPath As String, newPath As String)
        Try
            Dim info As New FileInfo(oldPath)
            Dim entry As New RetiredFileEntry(Path.GetFileName(oldPath), Path.GetFileName(newPath),
                                              info.LastWriteTimeUtc.Ticks, info.Length, False)
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(oldPath), RetiredFilesRecordName), entry.ToLine() & vbCrLf)
            Tracing.TraceLine("PersonalData: the old operator file is recorded, to be removed at the next start: " & oldPath, TraceLevel.Info)
        Catch ex As Exception
            Tracing.TraceLine("PersonalData: the old operator file could not be recorded: " & ex.Message, TraceLevel.Error)
        End Try
    End Sub

    ''' <summary>
    ''' Rewrite the record in <paramref name="dir"/>, passing each entry that
    ''' reads to <paramref name="change"/>; true from it means the entry
    ''' changed. A line that does not read is kept as it is, for
    ''' <see cref="SettleRetiredFiles"/> to drop and trace.
    ''' </summary>
    Private Shared Sub RewriteRetiredFiles(dir As String, change As Func(Of RetiredFileEntry, Boolean), what As String)
        Try
            Dim recordPath As String = Path.Combine(dir, RetiredFilesRecordName)
            If Not File.Exists(recordPath) Then Return
            Dim lines As String() = File.ReadAllLines(recordPath)
            Dim changed As Boolean = False
            For i As Integer = 0 To lines.Length - 1
                Dim entry As RetiredFileEntry = RetiredFileEntry.Parse(lines(i))
                If entry IsNot Nothing AndAlso change(entry) Then
                    lines(i) = entry.ToLine()
                    changed = True
                End If
            Next
            If changed Then AtomicFile.WriteAllText(recordPath, String.Join(vbCrLf, lines) & vbCrLf)
        Catch ex As Exception
            Tracing.TraceLine("PersonalData: the retired operator record could not be updated (" & what & "): " & ex.Message, TraceLevel.Error)
        End Try
    End Sub

    ''' <summary>
    ''' A later rename moved an operator whose earlier old file is still on
    ''' record: the record now names the newest file, so the old one is still
    ''' known to be a stale copy of a saved operator.
    ''' </summary>
    Private Shared Sub RetargetRetiredFiles(oldPath As String, newPath As String)
        Dim was As String = Path.GetFileName(oldPath)
        Dim now As String = Path.GetFileName(newPath)
        RewriteRetiredFiles(Path.GetDirectoryName(oldPath),
            Function(entry)
                If entry.OperatorDeleted OrElse Not String.Equals(entry.NewName, was, StringComparison.OrdinalIgnoreCase) Then Return False
                entry.NewName = now
                Return True
            End Function, "a later rename")
    End Sub

    ''' <summary>
    ''' The operator in <paramref name="deletedPath"/> was deleted (#1254): an
    ''' old file still on record for it is now a copy of an operator that no
    ''' longer exists, so the next start removes it rather than loading it as
    ''' the operator's only copy. An earlier-format entry is left as it is: it
    ''' cannot show which file it means, and keeping a file is the safe failure.
    ''' </summary>
    Friend Shared Sub MarkRetiredFilesDeleted(deletedPath As String)
        Dim gone As String = Path.GetFileName(deletedPath)
        RewriteRetiredFiles(Path.GetDirectoryName(deletedPath),
            Function(entry)
                If entry.IsEarlierFormat OrElse entry.OperatorDeleted OrElse
                   Not String.Equals(entry.NewName, gone, StringComparison.OrdinalIgnoreCase) Then Return False
                entry.OperatorDeleted = True
                Tracing.TraceLine("PersonalData: the deleted operator's old file " & entry.OldName &
                                  " is on record, and is now marked to be removed rather than loaded", TraceLevel.Info)
                Return True
            End Function, "an operator was deleted")
    End Sub

    ''' <summary>
    ''' Settle the retired-file record at startup (#1241, #1254), before any
    ''' operator is read, and return the names of the files NOT to load.
    '''
    ''' For each entry: the old file is gone, so the entry is dropped. An
    ''' earlier-format entry cannot show the old file is the stale copy, so the
    ''' file is kept and loaded and the entry dropped. The old file no longer
    ''' matches the write time and length recorded for it, so it is a
    ''' different operator saved since under that name: kept and loaded, and
    ''' the entry dropped. The operator was deleted since: the delete is tried.
    ''' Otherwise the renamed file must still hold an operator that reads: when
    ''' it is missing, the old file is the only copy, kept and loaded and the
    ''' entry dropped; when it does not read, the old file is the last good
    ''' copy, kept and loaded, and the entry kept for a start at which the
    ''' renamed file reads again. Otherwise the delete is tried. A file that
    ''' still cannot be removed is skipped, never loaded, and kept on record.
    ''' Only a file the record names, in this folder, is ever touched, and no
    ''' clock is ever compared. Each outcome is traced. Anything that goes
    ''' wrong here is traced and startup carries on.
    ''' </summary>
    Friend Shared Function SettleRetiredFiles(dir As String) As HashSet(Of String)
        Dim skip As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Try
            Dim recordPath As String = Path.Combine(dir, RetiredFilesRecordName)
            If Not File.Exists(recordPath) Then Return skip
            Dim keep As New List(Of String)
            For Each line As String In File.ReadAllLines(recordPath)
                If String.IsNullOrWhiteSpace(line) Then Continue For
                Dim entry As RetiredFileEntry = RetiredFileEntry.Parse(line)
                If entry Is Nothing Then
                    Tracing.TraceLine("PersonalData: an unreadable retired-file entry was dropped: " & line, TraceLevel.Warning)
                    Continue For
                End If
                Dim oldFile As String = Path.Combine(dir, entry.OldName)
                Dim newFile As String = Path.Combine(dir, entry.NewName)
                If Not File.Exists(oldFile) Then
                    Tracing.TraceLine("PersonalData: retired operator file already gone: " & oldFile, TraceLevel.Info)
                    Continue For
                End If
                If entry.IsEarlierFormat Then
                    ' Nothing in the line shows the file there now is the stale
                    ' copy, so it is never deleted on its strength.
                    Tracing.TraceLine("PersonalData: a retired-file entry in the earlier format cannot show the old file is a stale copy; kept and loaded: " &
                                      oldFile, TraceLevel.Warning)
                    Continue For
                End If
                If Not entry.StillMatches(oldFile) Then
                    ' Not the file the record names: an operator saved since
                    ' under the same name, whatever the clock said. Kept and loaded.
                    Tracing.TraceLine("PersonalData: a different operator file now has a retired name; kept: " & oldFile, TraceLevel.Info)
                    Continue For
                End If
                If Not entry.OperatorDeleted Then
                    If Not File.Exists(newFile) Then
                        ' The renamed copy is gone, so this is the operator's only
                        ' record. Never lose it: load it as it is.
                        Tracing.TraceLine("PersonalData: the renamed operator file is missing; the old one is kept and loaded: " & oldFile, TraceLevel.Warning)
                        Continue For
                    End If
                    If ReadOperatorFile(newFile).Op Is Nothing Then
                        ' The renamed copy does not read, so the old file is the
                        ' operator's last good copy: loaded, and still on record
                        ' for a start at which the renamed copy reads again.
                        Tracing.TraceLine("PersonalData: the renamed operator file " & newFile &
                                          " cannot be read; the old one is kept and loaded: " & oldFile, TraceLevel.Warning)
                        keep.Add(line)
                        Continue For
                    End If
                End If
                Try
                    File.Delete(oldFile)
                    Tracing.TraceLine("PersonalData: removed the retired operator file: " & oldFile &
                                      If(entry.OperatorDeleted, " (its operator was deleted)", ""), TraceLevel.Info)
                Catch ex As Exception
                    Tracing.TraceLine("PersonalData: the retired operator file still cannot be removed, and is not loaded: " &
                                      oldFile & " (" & ex.Message & ")", TraceLevel.Warning)
                    skip.Add(entry.OldName)
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
        ' #1253: the delete never throws out of the operator list's Delete
        ' button, and never removes a file that could not be loaded this
        ' session. Either way the operator leaves the list for this session
        ' and the file stays, so it is back at the next start; the operator
        ' is told plainly why.
        Dim fileName As String = ops(id).pathName
        If FailedToLoadThisSession(fileName) Then
            Tracing.TraceLine("PersonalData: the operator was removed from the list, but its file " & fileName &
                              " could not be loaded at start-up, so it is not deleted and is left where it is", TraceLevel.Warning)
            If OperatorFilesNotLoadedNotice IsNot Nothing Then
                OperatorFilesNotLoadedNotice(New String() {Path.GetFileName(fileName)})
            End If
        Else
            Try
                File.Delete(fileName)
                ' An old file a rename could not remove, still on record for
                ' this operator, must not come back as the deleted operator at
                ' the next start (#1254).
                MarkRetiredFilesDeleted(fileName)
            Catch ex As Exception
                Tracing.TraceLine("PersonalData: the operator was removed from the list, but its file could not be deleted: " &
                                  fileName & " (" & ex.Message & ")", TraceLevel.Error)
                ' Its own notice (repair 2): the failure may be read-only or a
                ' file another program holds, so the read-only sentence would
                ' be untrue for the second.
                If OperatorFileNotDeletedNotice IsNot Nothing Then
                    OperatorFileNotDeletedNotice(Path.GetFileName(fileName))
                End If
            End Try
        End If
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
            ' A file that could not be loaded, which could not be copied
            ' first: TryWrite traced why the write did not happen (#1253).
            If LastWriteKeptUnreadableFile Then Return
            ' A read-only operator file (it loads, since #1253): traced, and
            ' the operator told once, never the "Exception" box.
            If IsReadOnlyFile(op.pathName) Then
                Tracing.TraceLine("PersonalData: the operator file " & op.pathName &
                                  " is read-only, so this change to the operator was not kept", TraceLevel.Warning)
                If toldReadOnly.Add(FullPathOrSelf(op.pathName)) AndAlso OperatorFileReadOnlyNotice IsNot Nothing Then
                    OperatorFileReadOnlyNotice(Path.GetFileName(op.pathName))
                End If
                Return
            End If
            Tracing.ErrMessageTrace(LastWriteFailure)
        End If
    End Sub

    ''' <summary>
    ''' What the last failed <see cref="TryWrite"/> caught; Nothing after a
    ''' write that succeeded.
    ''' </summary>
    Friend Property LastWriteFailure As Exception

    ''' <summary>
    ''' True when the last <see cref="TryWrite"/> did not write because its
    ''' target is a file that could not be loaded this session and no copy of
    ''' it could be kept first (#1253). Traced there; nothing is shown.
    ''' </summary>
    Friend Property LastWriteKeptUnreadableFile As Boolean

    ''' <summary>
    ''' Before a write to <paramref name="target"/>: when it is a file that
    ''' could not be loaded this session, COPY it beside itself under
    ''' <see cref="KeptCopyPath"/> first (a copy; it is never moved, renamed or
    ''' deleted here), so whatever it holds survives the write. False when the
    ''' copy could not be made, and the write must not happen. True for every
    ''' other target, and for one that is no longer there.
    ''' </summary>
    Private Function KeepUnloadedFileBeforeWrite(target As String) As Boolean
        If Not FailedToLoadThisSession(target) Then Return True
        Dim full As String = FullPathOrSelf(target)
        If Not File.Exists(full) Then
            notLoadedFiles.Remove(full)
            Tracing.TraceLine("PersonalData: the operator file that could not be loaded is gone, so there is nothing to keep before writing " &
                              full, TraceLevel.Info)
            Return True
        End If
        Dim kept As String = KeptCopyPath(full, DateTime.UtcNow)
        Try
            File.Copy(full, kept, overwrite:=False)
        Catch ex As Exception
            Tracing.TraceLine("PersonalData: not written: " & full & " could not be loaded at start-up and could not be copied first (" &
                              ex.Message & "), so it is left exactly as it is", TraceLevel.Error)
            Return False
        End Try
        notLoadedFiles.Remove(full)
        Tracing.TraceLine("PersonalData: the operator file that could not be loaded was copied to " & kept &
                          " before being written over", TraceLevel.Warning)
        Return True
    End Function

    ''' <summary>
    ''' Where a file that could not be loaded is kept before a write replaces
    ''' it: beside it, named <c>&lt;name&gt;.unreadable-&lt;UTC time&gt;.bak</c>.
    ''' <para><b>Never an .xml name</b>, because the loader reads every *.xml
    ''' in the folder as an operator, and <b>never AtomicFile's temporary
    ''' pattern</b> (<c>&lt;name&gt;.&lt;32 hex&gt;.tmp</c>), which a later save
    ''' deletes once it is ten minutes old.</para>
    ''' </summary>
    Friend Shared Function KeptCopyPath(path As String, utc As DateTime) As String
        Return path & ".unreadable-" &
            utc.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture) & ".bak"
    End Function

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
        LastWriteKeptUnreadableFile = False

        ' A file that could not be loaded this session is never written over
        ' unkept (#1253): a copy goes beside it first, and without one the
        ' write does not happen.
        If Not KeepUnloadedFileBeforeWrite(op.pathName) Then
            LastWriteKeptUnreadableFile = True
            Return False
        End If

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

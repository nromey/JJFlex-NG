Imports System.Collections
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
'Imports System.Collections.Specialized
'Imports System.Configuration
Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Globalization
Imports System.IO.Ports
Imports System.Net
Imports System.Reflection
Imports System.Threading
Imports System.Xml.Serialization
Imports adif
Imports JJCountriesDB
Imports JJArClusterLib
Imports JJFlexControl
Imports JJLogLib
Imports JJPortaudio
Imports JJTrace
Imports JJW2WattMeter
Imports MsgLib
Imports System.Linq
Imports Radios

Module globals
    Public Const CopyRight As String = "Copyright 2013 by J.J. Shaffer"

    ''' <summary>
    ''' UI mode: Classic preserves legacy menus, Modern provides reorganized slice-centric menus.
    ''' Logging mode is reserved for a future sprint.
    ''' Persisted as an integer in operator XML so adding values never breaks existing files.
    ''' </summary>
    Public Enum UIMode
        Classic = 0
        Modern = 1
        Logging = 2   ' Reserved — falls back to Classic until the Logging sprint ships.
    End Enum

    ''' <summary>
    ''' Session-only flag — True while in Logging Mode overlay.
    ''' Never touches UIModeSetting so the persisted Classic/Modern choice is preserved.
    ''' </summary>
    Private _isInLoggingMode As Boolean = False

    ''' <summary>
    ''' The active UI mode for the current operator.
    ''' Logging is a session-only overlay that never writes to the operator config.
    ''' Classic/Modern persist normally.
    ''' </summary>
    Friend Property ActiveUIMode As UIMode
        Get
            If _isInLoggingMode Then Return UIMode.Logging
            If CurrentOp Is Nothing Then Return UIMode.Classic
            Return CurrentOp.CurrentUIMode
        End Get
        Set(value As UIMode)
            If value = UIMode.Logging Then
                ' Logging is session-only — just set the flag, don't touch persisted settings.
                _isInLoggingMode = True
                Return
            End If
            ' Leaving Logging (or switching Classic/Modern) — clear the flag and persist.
            _isInLoggingMode = False
            If CurrentOp Is Nothing Then Return
            CurrentOp.UIModeSetting = CInt(value)
            Operators.UpdateCurrentOp()
        End Set
    End Property

    ''' <summary>
    ''' Remembers Classic or Modern so toggling out of Logging returns to the right mode.
    ''' Not persisted — resets to the operator's saved mode on startup.
    ''' </summary>
    Friend LastNonLogMode As UIMode = UIMode.Classic
    ' Lexicon-backed, so these are properties rather than Const — a Const must
    ' be a compile-time literal. Every consumer reads them by name and is
    ' unaffected.
    Friend ReadOnly Property ErrorHdr As String
        Get
            Return Radios.Lexicon.Get("connect.dialog.error_header")
        End Get
    End Property
    Friend ReadOnly Property MessageHdr As String
        Get
            Return Radios.Lexicon.Get("connect.dialog.message_header")
        End Get
    End Property
    Friend ReadOnly Property ExceptionHdr As String
        Get
            Return Radios.Lexicon.Get("connect.dialog.exception_header")
        End Get
    End Property
    Friend ReadOnly Property OnWord As String
        Get
            Return Radios.Lexicon.Get("connect.status.on")
        End Get
    End Property
    Friend ReadOnly Property OffWord As String
        Get
            Return Radios.Lexicon.Get("connect.status.off")
        End Get
    End Property
    Friend ReadOnly Property Running As String
        Get
            Return Radios.Lexicon.Get("connect.status.running")
        End Get
    End Property
    Friend ReadOnly Property Paused As String
        Get
            Return Radios.Lexicon.Get("connect.status.paused")
        End Get
    End Property
    Friend ReadOnly Property NoRig As String
        Get
            Return Radios.Lexicon.Get("connect.radio.no_rig")
        End Get
    End Property
    Friend ReadOnly Property Rebooting As String
        Get
            Return Radios.Lexicon.Get("connect.radio.rebooting")
        End Get
    End Property
    Friend ReadOnly Property mustHaveLog As String
        Get
            Return Radios.Lexicon.Get("logging.log_file.must_be_defined")
        End Get
    End Property
    Friend ReadOnly Property RequiresBrailleDisplay As String
        Get
            Return Radios.Lexicon.Get("connect.session.requires_braille")
        End Get
    End Property
    Friend ReadOnly Property NotValidHost As String
        Get
            Return Radios.Lexicon.Get("connect.session.not_valid_host")
        End Get
    End Property

    ' NOT extracted, and deliberately so — Track F found these declared and
    ' referenced NOWHERE. Putting dead text in the operator-editable store
    ' would advertise wording the program never says.
    Friend Const LockedWord As String = "Locked"
    Friend Const NoneWord As String = "none"
    Friend Const Loaded As String = "loaded"
    Friend Const Loading As String = "loading"
    Friend Const RebootHdr As String = "Reboot"
    Friend Const msgReboot As String = "Reboot the radio?"
    Friend Const NotSupportedForThisRig As String = "This function is not supported on this radio."
    Friend Const NotSupportedForThisInstance As String = "This function is not supported in this situation."
    Friend Const NotSupportedForRemoteRig As String = "This function is not supported on a remote radio."
    Friend Const NoLongerSupported As String = "This function is no longer supported."
    Friend Const NoAudioDevice As String = "No output audio device is configured."

#If 0 Then
    Friend AppSettings As AppSettingsSection
    Friend Function GetConfigValue(key As String) As String
        Dim rv As String
        If (AppSettings IsNot Nothing) AndAlso (AppSettings.Settings(key) IsNot Nothing) Then
            rv = AppSettings.Settings(key).Value
        Else
            rv = vbNullString
        End If
        Return rv
    End Function
#End If

    Friend ProgramInstance As Integer = 0
    Friend BootTrace As Boolean
    Friend ProgramDirectory As String ' This program's directory.
    Friend Commands As JJFlexWpf.KeyCommands
    Friend ContactLog As LogClass
    Friend LookupStation As JJFlexWpf.StationLookupWindow = Nothing
    Friend ClusterScreens As New List(Of ClusterForm)

    Friend Dups As LogDupChecking
    ''' <summary>
    ''' dup checking type
    ''' </summary>
    Friend ReadOnly Property DupType As LogDupChecking.DupTypes
        Get
            If Dups Is Nothing Then
                Return LogDupChecking.DupTypes.none
            Else
                Return Dups.dupType
            End If
        End Get
    End Property
    ''' <summary>
    ''' True if dup checking
    ''' </summary>
    Friend ReadOnly Property isDupChecking As Boolean
        Get
            Return DupType <> LogDupChecking.DupTypes.none
        End Get
    End Property

    Friend FindDialog As Boolean = False

    Friend DirectCW As Boolean = False
    Friend CWText As CWMessages
    Enum WindowIDs
        ReceiveDataOut
        SendDataOut
    End Enum
    Delegate Sub WrtTxt(ByVal TextboxID As WindowIDs, ByVal text As String, ByVal clearFlag As Boolean)
    Friend WriteText As WrtTxt
    Delegate Sub WrtTxtX(ByVal tbid As WindowIDs, ByVal s As String, _
                         ByVal cur As Integer, ByVal c As Boolean)
    Friend WriteTextX As WrtTxtX
    Delegate Sub tbrtn(ByVal tbid As WindowIDs)
    ''' <summary>
    ''' True if ending the program.
    ''' Access with volatile read and write.
    ''' </summary>
    Friend Ending As Boolean = False

    ''' <summary>
    ''' SMeter raw and calibrated values.
    ''' </summary>
    Friend SMeter As Levels

    ' region - Config data stuff.
#Region "config"
    Friend myAssembly As Assembly
    Friend myAssemblyName As AssemblyName
    Friend myVersion As Version
    ''' <summary>
    ''' configuration event types
    ''' </summary>
    Friend Enum ConfigEvents
        OperatorChanged
        RigChanged
    End Enum
    ''' <summary>
    ''' type of the config event argument.
    ''' </summary>
    Friend Class ConfigArgs
        Inherits EventArgs
        Public TheEvent As ConfigEvents
        Public TheData As Object
        ''' <summary>
        ''' define a config event
        ''' </summary>
        ''' <param name="e">the event from ConfigEvents</param>
        ''' <param name="d">Event dependent data</param>
        Public Sub New(ByVal e As ConfigEvents, ByVal d As Object)
            TheEvent = e
            TheData = d
        End Sub
    End Class

    Friend Const ProgramName = "JJ Flexible Radio Access"
    Friend Const InternalName = "JJFlexRadio"
    Friend Const DocName As String = InternalName & "Readme.htm"
    ReadOnly Property reqOpMsgTitle As String
        Get
            Return Radios.Lexicon.Get("connect.startup.require_operator_title")
        End Get
    End Property
    ReadOnly Property reqOpMsg As String
        Get
            Return Radios.Lexicon.Get("connect.startup.require_operator_body", ("newline", vbCrLf))
        End Get
    End Property
    Const noDefaultRigTitle As String = "No default rig"
    Const noDefaultRig As String = _
        "You must define a default rig." & vbCrLf & _
        "Do you wish to define one?"
    Friend MenusLoaded As Boolean
    Friend MemoriesLoaded As Boolean
    Friend BaseConfigDir As String

    ''' <summary>
    ''' True when JJFLEX_CONFIG_DIR moved this run's whole settings tree
    ''' somewhere throwaway. For automated runs and parallel agents, so they
    ''' never touch the operator's live folder.
    ''' </summary>
    ''' <remarks>
    ''' Kept as state rather than re-read, because anything reporting the app's
    ''' condition — the diagnostic log, About, a support conversation — has to
    ''' be able to say "these are not your normal settings." A run quietly using
    ''' someone else's configuration is the failure that looks like success.
    ''' </remarks>
    Friend UsingTemporaryConfigDir As Boolean

    ''' <summary>
    ''' Why an offered JJFLEX_CONFIG_DIR was refused, or Nothing. Held until the
    ''' trace subsystem is up: this is decided before tracing starts, and a
    ''' message written then would go nowhere.
    ''' </summary>
    Friend ConfigDirRefusal As String

    ''' <summary>
    ''' What to say at the START of the next radio-picker session, carried by
    ''' the discovering window's own title.
    '''
    ''' Exists because an utterance made just before a window opens is flushed
    ''' by the screen reader when focus moves. Anything the operator must hear
    ''' across that boundary has to be owned by the window arriving, not spoken
    ''' by the code that opens it.
    ''' </summary>
    Friend PendingDisconnectLead As String = Nothing

    ''' <summary>
    ''' The verdict on a connect attempt that failed, held until the connecting
    ''' window has gone and the shell window is back in front.
    '''
    ''' Same reasoning as <see cref="PendingDisconnectLead"/> and the same
    ''' failure it prevents: an utterance made while a window is closing is
    ''' flushed by the screen reader, so "Connection failed" — the one sentence
    ''' that distinguishes a dead connect from a slow one — could be issued,
    ''' traced, and never heard. Drained in openTheRadio.
    ''' </summary>
    Friend PendingConnectVerdict As String = Nothing
    Friend ReadOnly Property BootTraceFileName As String
        Get
            Dim rv As String = BaseConfigDir & "\" & InternalName
            If ProgramInstance > 1 Then
                rv &= ProgramInstance.ToString
            End If
            Return rv & "Trace.txt"
        End Get
    End Property
    Friend ReadOnly Property OldTraceFileName As String
        Get
            Dim rv As String = BaseConfigDir & "\" & InternalName
            If ProgramInstance > 1 Then
                rv &= ProgramInstance.ToString
            End If
            Return rv & "TraceOld.txt"
        End Get
    End Property
    Friend ReadOnly Property TraceArchiveDir As String
        Get
            Return BaseConfigDir & "\Traces"
        End Get
    End Property
    Friend ReadOnly Property DailyTraceFilePrefix As String
        Get
            Return InternalName & "Trace"
        End Get
    End Property

    ''' <summary>
    ''' Stem shared by this instance's live trace and every part file it rotates
    ''' out — "JJFlexRadioTrace" for instance 1, "JJFlexRadio2Trace" for the
    ''' second instance. Part files are "&lt;stem&gt;-&lt;stamp&gt;-part-NNN.txt".
    ''' </summary>
    Private ReadOnly Property LiveTraceStem As String
        Get
            Return Path.GetFileNameWithoutExtension(BootTraceFileName)
        End Get
    End Property

    Private Sub RotateBootTraceIfNeeded()
        Dim tracePath As String = BootTraceFileName
        If Not File.Exists(tracePath) Then
            ' No live leftover, but a previous run may still have left part
            ' files whose background compression never finished.
            ArchiveLeftoverTraceChains(Nothing)
            Return
        End If

        ' A leftover boot trace at this point means the previous app run did
        ' NOT clean-exit — clean exit calls ArchiveCurrentTraceSession which
        ' archives + deletes the source file. So the file's existence is
        ' evidence of a killed session. Archive it as outcome=killed BEFORE
        ' the legacy rotate-to-old logic runs, so stuck-session forensics
        ' (the original motivating scenario for trace persistence per
        ' memory/project_trace_persistence_design.md) survives next boot.
        Try
            Dim leftoverInfo As New FileInfo(tracePath)
            ' File creation time approximates when the prior app booted —
            ' good enough for the manifest's boot_time so duration_ms
            ' reflects roughly how long the prior app ran before being
            ' killed. Failing back to UtcNow would make every killed
            ' session look like duration_ms ≈ 0.
            ' Sweep any leftover part files first. If the killed run had rotated,
            ' its chain is adopted here and the live leftover becomes that
            ' chain's final part — so a killed marathon session reads as one
            ' sequence in the archive instead of a pile of unrelated files.
            Dim adopted As LeftoverChain = ArchiveLeftoverTraceChains(tracePath)
            If adopted IsNot Nothing Then
                Dim finalPart As Integer = adopted.HighestPart + 1
                Dim partPath As String = RenameTraceToStampedPart(tracePath, adopted.Session.BootTimeUtc, finalPart)
                If Not String.IsNullOrEmpty(partPath) Then
                    SessionArchive.ArchiveSession(TraceArchiveDir, partPath, adopted.Session,
                        deleteSourceAfter:=False, partNumber:=finalPart, isFinalPart:=True)
                    Return
                End If
            End If

            Dim killedSession As New TraceSession(leftoverInfo.CreationTimeUtc.ToUniversalTime())
            killedSession.MarkOutcome(TraceSessionOutcome.Killed,
                "Inferred from leftover boot trace at next launch (no clean exit observed)")
            Dim relName As String = SessionArchive.ArchiveSession(
                TraceArchiveDir, tracePath, killedSession, deleteSourceAfter:=False)
            If Not String.IsNullOrEmpty(relName) Then
                ' Archive succeeded. Rename source to a stamp-named .txt so the
                ' next session opens a fresh JJFlexRadioTrace.txt, but the
                ' killed session's plain text is still readable in Notepad /
                ' screen reader for the plain-text retention window
                ' (PrunePlainTextTracesOlderThan, default 1 day). The LZMA
                ' .zip stays for the full 30-day archive retention regardless.
                RenameTraceToStamped(tracePath, killedSession.BootTimeUtc)
                Return
            End If
            ' If archive returned null, fall through to legacy rotate so we
            ' don't drop the trace entirely.
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            ' Fall through to legacy rotate — better to keep the trace as
            ' OldTraceFileName than lose it because of an archive bug.
        End Try

        Try
            If File.Exists(OldTraceFileName) Then
                File.Delete(OldTraceFileName)
            End If
            File.Move(tracePath, OldTraceFileName)
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    Private Sub ArchiveTraceFile(tracePath As String, traceDate As Date)
        Try
            Dim yearDir As String = Path.Combine(TraceArchiveDir, traceDate.ToString("yyyy"))
            Dim monthDir As String = Path.Combine(yearDir, traceDate.ToString("MM"))
            Directory.CreateDirectory(monthDir)
            Dim zipName As String = Path.Combine(monthDir, $"trace{traceDate:MMddyyyy}.zip")
            If File.Exists(zipName) Then
                File.Delete(zipName)
            End If
            Using archive As ZipArchive = ZipFile.Open(zipName, ZipArchiveMode.Create)
                ZipUtils.AddFileToArchive(archive, tracePath, "")
            End Using
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>
    ''' SUNSET SWEEP — remove after the release that follows 4.1.17.
    '''
    ''' The daily-trace feature is retired (see the removal of
    ''' StartDailyTraceIfEnabled and the design's answered question 4). Nothing
    ''' writes date-stamped daily files any more, but a machine that ran an
    ''' older build may still have some sitting at the root of the settings
    ''' folder. This sweep archives and removes them once, so they age out
    ''' instead of living forever, and then it has no work left to do.
    '''
    ''' The gate on CurrentOp.KeepDailyTraceLogs is deliberately GONE: that
    ''' field is no longer read anywhere, and leaving the gate would have meant
    ''' the cleanup never ran on the machines that need it.
    '''
    ''' Pattern safety, since the glob looks alarming: it matches
    ''' "JJFlexRadioTrace*.txt", which also catches the live log and the
    ''' stamp-named plain-text files. Both fail the exact yyyyMMddHHmmss parse
    ''' below and are skipped. Only a true daily file gets touched.
    ''' </summary>
    Private Sub ArchiveOldDailyTraces()
        Try
            If Not Directory.Exists(BaseConfigDir) Then Return
            Dim today As Date = Date.Now.Date
            For Each tracePath As String In Directory.GetFiles(BaseConfigDir, DailyTraceFilePrefix & "*.txt")
                If tracePath.IndexOf("BootTrace", StringComparison.OrdinalIgnoreCase) >= 0 Then Continue For
                Dim name As String = Path.GetFileNameWithoutExtension(tracePath)
                Dim stampPart As String = name.Substring(DailyTraceFilePrefix.Length)
                Dim stamp As DateTime
                If Not DateTime.TryParseExact(stampPart, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, stamp) Then
                    Continue For
                End If
                If stamp.Date < today Then
                    ArchiveTraceFile(tracePath, stamp.Date)
                    Try
                        File.Delete(tracePath)
                    Catch ex As Exception
                        Tracing.ErrTraceOnly(ex)
                    End Try
                End If
            Next
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>
    ''' The diagnostic log's persisted settings, loaded once at GetConfigInfo
    ''' before the log opens. App-level, not per-operator — the log has to be
    ''' running long before anyone picks an operator.
    '''
    ''' Never Nothing after GetConfigInfo; defaults to "on at normal detail",
    ''' which is exactly what this app did before the setting existed.
    ''' </summary>
    Friend DiagnosticsSettings As Radios.DiagnosticsConfig = New Radios.DiagnosticsConfig()

    ''' <summary>
    ''' True while a detailed capture is running. A capture is NOT a second
    ''' trace — there is exactly one trace stream — it is a temporary elevation
    ''' of the standing log to maximum detail, marked off as its own session.
    ''' </summary>
    Friend ReadOnly Property DetailedCaptureRunning As Boolean
        Get
            Return _captureStartedLocal.HasValue
        End Get
    End Property

    ''' <summary>When the running capture began, or Nothing.</summary>
    Private _captureStartedLocal As Date? = Nothing

    ''' <summary>
    ''' Where the capture that just STOPPED was archived to, so the surface can
    ''' offer "Export this capture..." without walking the archive. Cleared when
    ''' the next capture starts.
    ''' </summary>
    Friend LastCaptureArchivePath As String = Nothing

    ''' <summary>
    ''' Tell every diagnostics surface that the log's state changed — on, off,
    ''' detail level, capture started or stopped. The status line subscribes so
    ''' it re-reads reality rather than caching a copy of it. That caching is
    ''' exactly how the retired trace dialog (deleted in Sprint 31) ended up
    ''' announcing "Start tracing" for a trace that was already running.
    ''' </summary>
    Friend Sub RaiseDiagnosticLogStateChanged()
        Try
            JJFlexWpf.DiagnosticsBridge.NotifyStateChanged()
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>
    ''' Hand the WPF diagnostics surface its delegates. Called once at startup,
    ''' right after the config directory is known. JJFlexWpf cannot call into
    ''' this project by name — it is referenced BY it — so this is the seam, and
    ''' having exactly one seam is what stops the surface re-implementing the
    ''' plumbing the way the retired trace dialog did.
    ''' </summary>
    Friend Sub WireDiagnosticsBridge()
        Try
            JJFlexWpf.DiagnosticsBridge.DescribeState = Function() DescribeDiagnosticLogState()
            JJFlexWpf.DiagnosticsBridge.IsCapturing = Function() DetailedCaptureRunning
            JJFlexWpf.DiagnosticsBridge.KeepLog = Function() DiagnosticsSettings.KeepDiagnosticLog
            JJFlexWpf.DiagnosticsBridge.DetailLevel = Function() CInt(DiagnosticsSettings.DetailLevel)
            JJFlexWpf.DiagnosticsBridge.StartCapture = Sub(reason) StartDetailedCapture(reason)
            JJFlexWpf.DiagnosticsBridge.StopCapture = Sub() StopDetailedCapture()
            JJFlexWpf.DiagnosticsBridge.ApplySettings =
                Sub(keep, detail) ApplyDiagnosticLogSettings(keep, CType(detail, Radios.DiagnosticDetail))
            JJFlexWpf.DiagnosticsBridge.MeterStream = Function() DiagnosticsSettings.RecordMeterStream
            JJFlexWpf.DiagnosticsBridge.ApplyMeterStream = Sub(record) ApplyMeterStreamSetting(record)
            JJFlexWpf.DiagnosticsBridge.SpokenTranscript = Function() Radios.OutputChannelRecorder.RecordEnabled
            JJFlexWpf.DiagnosticsBridge.ApplySpokenTranscript = Sub(record) ApplySpokenTranscriptSetting(record)
            JJFlexWpf.DiagnosticsBridge.LiveLogPath =
                Function() If(Tracing.TraceFile, If(BootTrace, BootTraceFileName, String.Empty))
            JJFlexWpf.DiagnosticsBridge.LogFolder = Function() BaseConfigDir
            JJFlexWpf.DiagnosticsBridge.LastCaptureArchivePath =
                Function() If(LastCaptureArchivePath, String.Empty)
            JJFlexWpf.DiagnosticsBridge.DescribeStorage = Function() DescribeDiagnosticStorage()
            JJFlexWpf.DiagnosticsBridge.DescribeCrashReports = Function() CrashReporter.DescribeCrashReports()
            JJFlexWpf.DiagnosticsBridge.DeleteLooseLogs = Function() DeleteLoosePlainTextTraces()
            JJFlexWpf.DiagnosticsBridge.DeleteResolvedCrashReports =
                Function() CrashReporter.DeleteResolvedCrashReports()
            JJFlexWpf.DiagnosticsBridge.DescribeBytes = Function(b) DescribeBytes(b)
            JJFlexWpf.DiagnosticsBridge.OpenSavedLogs = Sub() ShowSavedDiagnosticLogs()
            JJFlexWpf.DiagnosticsBridge.SaveProblemReport = Sub() DebugInfo.GetDebugInfo()
            JJFlexWpf.DiagnosticsBridge.Speak = Sub(msg) SpeakDiagnostics(msg)

            ' The failure-moment offer. Installed here because this runs on the
            ' UI thread at startup, and the dispatcher it captures is the one
            ' that can actually show a window later, from whatever thread the
            ' failure happened on.
            JJFlexWpf.DiagnosticOffer.IsTransmitting =
                Function() RigControl IsNot Nothing AndAlso RigControl.Transmit
            JJFlexWpf.DiagnosticOffer.Install()
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

#Region "The running-cost register (#253)"

    ''' <summary>
    ''' The sampler behind the threshold read. Thirty seconds, and it does
    ''' nothing but LOOK — see WireRunningCostRegister for why that is not the
    ''' timer Noel ruled out.
    ''' </summary>
    Private _runningCostSampler As System.Threading.Timer

    ''' <summary>
    ''' Declare everything expensive to <see cref="Radios.RunningCostRegister"/>
    ''' — task #253, "one register, not scattered per-feature reminders".
    ''' </summary>
    ''' <remarks>
    ''' <para><b>Why the standing registrations live here rather than inside
    ''' each feature.</b> Every one of them is a PREDICATE over state that
    ''' already exists — DetailedCaptureRunning, MeterTraceStream.Enabled,
    ''' OutputChannelRecorder.RecordEnabled, MeterToneEngine.Enabled. Nothing
    ''' has to be told when they start or stop, so nothing can forget to tell
    ''' it, and the whole inventory of what this application does that costs
    ''' something is one readable table instead of five call sites nobody can
    ''' find. Transient things — the meter test tone — still register
    ''' themselves at the point they start, because a two-second tone has no
    ''' state anywhere to ask about.</para>
    '''
    ''' <para><b>Why the always-on log and the meter tones are Routine.</b> The
    ''' register exists for things a reasonable operator could be UNAWARE of.
    ''' The diagnostic log is on for everybody by default and the meter tones
    ''' are audible by definition, so neither raises the exit prompt — but both
    ''' answer the on-demand read, because "what is running" that leaves out
    ''' things that are running is not an answer. Routine is a statement about
    ''' noticeability, not about size.</para>
    '''
    ''' <para><b>Why the sampler is not the timer Noel ruled out.</b> The ruling
    ''' was "on a threshold, never on a timer", and it is about ANNOUNCING:
    ''' periodic nagging trains the operator to ignore the channel, which costs
    ''' more than it saves. Poll only measures. A poll that finds nothing
    ''' crossed says nothing, however often it runs, and each bound speaks at
    ''' most once per run. Something has to sample a growing file for a bound to
    ''' be noticed at all.</para>
    ''' </remarks>
    Friend Sub WireRunningCostRegister()
        Try
            ' ── The always-on diagnostic log ──────────────────────────────
            ' Routine: on for every operator since install. #194's closing
            ' point is that none of them were ever told, and this read is where
            ' they can now find out.
            Dim standingLog As New Radios.RunningCost("diagnostic-log", "The diagnostic log")
            standingLog.IsRunning = Function() DiagnosticsSettings.KeepDiagnosticLog _
                                                AndAlso Tracing.On _
                                                AndAlso Not DetailedCaptureRunning
            standingLog.DescribeCost = Function() DescribeBytes(LiveLogBytes())
            standingLog.StopHow = "go to Settings, then Diagnostics"
            standingLog.SurvivesRestart = True
            standingLog.Weight = Radios.RunningCostWeight.Routine
            Radios.RunningCostRegister.Register(standingLog)

            ' ── A detailed capture ────────────────────────────────────────
            ' Notable, and the one registrant that already had a stop the
            ' operator can reach from anywhere.
            Dim capture As New Radios.RunningCost("detailed-capture", "Detailed diagnostic capture")
            capture.IsRunning = Function() DetailedCaptureRunning
            capture.DescribeCost = Function() DescribeCaptureCost()
            capture.Measure = Function() LiveLogBytes()
            ' 10 MB is already larger than any session ever measured: the
            ' biggest on record ran 08:41 to 09:56 and came to 3.65 MB, and the
            ' 2026-08-25 capture that started all this was 4.7 MB. So the first
            ' bound means "this is bigger than a normal day", not "this is big".
            capture.Thresholds = New Long() {10L * 1024L * 1024L, 50L * 1024L * 1024L, 200L * 1024L * 1024L}
            capture.DescribeThreshold = Function(b) DescribeBytes(b)
            capture.Stop = Sub() StopDetailedCapture()
            capture.StopHow = "press Control J, then Control D"
            capture.Weight = Radios.RunningCostWeight.Notable
            Radios.RunningCostRegister.Register(capture)

            ' ── Meter stream recording ────────────────────────────────────
            ' The registrant this whole feature was built for. Persisted, silent,
            ' and the measured firehose: 418,004 lines in one 50-minute capture
            ' on 2026-08-21, 25.7 MB of its 52.4 MB.
            Dim meterStream As New Radios.RunningCost("meter-stream", "Meter stream recording")
            meterStream.IsRunning = Function() Radios.MeterTraceStream.Enabled
            meterStream.DescribeCost = Function() DescribeMeterLines(Radios.MeterTraceStream.LinesWritten)
            meterStream.Measure = Function() Radios.MeterTraceStream.LinesWritten
            meterStream.Thresholds = New Long() {100000L, 500000L, 2000000L}
            meterStream.DescribeThreshold = Function(n) DescribeMeterLines(n)
            meterStream.Stop = Sub() ApplyMeterStreamSetting(False)
            meterStream.StopHow = "go to Settings, then Diagnostics"
            meterStream.SurvivesRestart = True
            meterStream.Weight = Radios.RunningCostWeight.Notable
            Radios.RunningCostRegister.Register(meterStream)

            ' ── The spoken transcript ─────────────────────────────────────
            ' Cheap on disk and still Notable: it persists, it records
            ' everything the operator heard, and nothing else in the app says
            ' it is on.
            Dim transcript As New Radios.RunningCost("spoken-transcript", "Spoken transcript recording")
            transcript.IsRunning = Function() Radios.OutputChannelRecorder.RecordEnabled
            transcript.DescribeCost = Function() DescribeFileBytes(Radios.OutputChannelRecorder.TranscriptPath)
            transcript.Stop = Sub() ApplySpokenTranscriptSetting(False)
            transcript.StopHow = "go to Settings, then Diagnostics"
            transcript.SurvivesRestart = True
            transcript.Weight = Radios.RunningCostWeight.Notable
            Radios.RunningCostRegister.Register(transcript)

            ' ── Meter tones ───────────────────────────────────────────────
            ' Routine because it is audible. It is here so the on-demand read
            ' is complete, and because the switch persists — which is worth
            ' hearing when the radio is disconnected and the tones are
            ' therefore silent while still switched on.
            Dim meterTones As New Radios.RunningCost("meter-tones", "Meter tones")
            meterTones.IsRunning = Function() JJFlexWpf.MeterToneEngine.Enabled
            meterTones.DescribeCost = Function() DescribeSoundingMeters()
            meterTones.Stop = Sub()
                                  JJFlexWpf.MeterToneEngine.Enabled = False
                              End Sub
            meterTones.StopHow = "press Control J, then T"
            meterTones.SurvivesRestart = True
            meterTones.Weight = Radios.RunningCostWeight.Routine
            Radios.RunningCostRegister.Register(meterTones)

            AddHandler Radios.RunningCostRegister.ThresholdCrossed, AddressOf OnRunningCostThreshold

            _runningCostSampler = New System.Threading.Timer(
                Sub(state) SampleRunningCosts(),
                Nothing,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30))
        Catch ex As Exception
            ' A register that cannot be wired must not stop the app booting. The
            ' cost of losing it is that nothing announces instrumentation, which
            ' is exactly where we were before it existed.
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>Stop sampling. Called on the way out so a tick cannot land mid-teardown.</summary>
    Friend Sub StopRunningCostSampler()
        Try
            _runningCostSampler?.Dispose()
            _runningCostSampler = Nothing
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    Private Sub SampleRunningCosts()
        If Ending Then Return
        Try
            ' Not mid-over. The bound is not going anywhere, and a spoken
            ' warning while the operator is transmitting is one their own
            ' microphone may well pick up. SKIPPING the poll rather than
            ' swallowing its result is the load-bearing half: nothing gets
            ' marked as announced, so the warning arrives on the first poll
            ' after unkeying instead of being lost.
            If RigControl IsNot Nothing AndAlso RigControl.Transmit Then Return
            Radios.RunningCostRegister.Poll()
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>
    ''' Say that something crossed a bound. Critical, because the operator
    ''' cannot see it and by definition did not ask; NOT interrupting, because
    ''' a size warning is never more urgent than the sentence already in
    ''' flight.
    ''' </summary>
    Private Sub OnRunningCostThreshold(sender As Object, e As Radios.RunningCostThresholdEventArgs)
        Try
            Tracing.TraceLine("RunningCost: " & e.Reading.Id & " crossed " &
                              e.Threshold.ToString(CultureInfo.InvariantCulture))
            Radios.ScreenReaderOutput.Speak(e.Sentence, VerbosityLevel.Critical)
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>
    ''' The exit read of the register — Noel's ruled priority boundary. Returns
    ''' False to cancel the exit.
    ''' </summary>
    ''' <remarks>
    ''' Only Notable registrations reach the prompt, so an ordinary exit is
    ''' still a silent exit. Any failure here returns True: a prompt that breaks
    ''' must never trap the operator inside the application.
    ''' </remarks>
    Friend Function ConfirmStillRunningAtExit() As Boolean
        Try
            Dim notable = Radios.RunningCostRegister.Snapshot().
                Where(Function(r) r.Weight = Radios.RunningCostWeight.Notable).ToList()
            If notable.Count = 0 Then Return True

            Dim dlg As New JJFlexWpf.Dialogs.StillRunningDialog(notable)
            dlg.ShowDialog()

            Select Case dlg.Choice
                Case JJFlexWpf.Dialogs.StillRunningChoice.StayOpen
                    Tracing.TraceLine("ExitApplication: cancelled at the still-running prompt")
                    Return False

                Case JJFlexWpf.Dialogs.StillRunningChoice.StopThenClose
                    Dim stopped = Radios.RunningCostRegister.StopAll(True)
                    ' SpeakAndWait, not Speak: this is the app changing the
                    ' operator's persisted settings on their behalf, and a
                    ' queued utterance does not survive process exit. If it is
                    ' worth doing it is worth being heard.
                    Dim msg As String = If(stopped.Count > 0,
                        Radios.Lexicon.Get("logging.running.stopped",
                                           ("names", String.Join(", ", stopped))),
                        Radios.Lexicon.Get("logging.running.stopped_none"))
                    Radios.ScreenReaderOutput.SpeakAndWait(msg)
                    Return True

                Case Else
                    Tracing.TraceLine("ExitApplication: closing with instrumentation left on")
                    Return True
            End Select
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            Return True
        End Try
    End Function

    ''' <summary>Size of the log file currently being written, or zero.</summary>
    Private Function LiveLogBytes() As Long
        Try
            Dim path As String = If(Tracing.TraceFile, If(BootTrace, BootTraceFileName, String.Empty))
            If String.IsNullOrEmpty(path) Then Return 0
            Dim fi As New FileInfo(path)
            Return If(fi.Exists, fi.Length, 0L)
        Catch
            Return 0
        End Try
    End Function

    Private Function DescribeCaptureCost() As String
        Dim size As String = DescribeBytes(LiveLogBytes())
        If _captureStartedLocal.HasValue Then
            Return size & ", " & Radios.Lexicon.Get("logging.running.since",
                                                    ("clock", FormatClock(_captureStartedLocal.Value)))
        End If
        Return size
    End Function

    ''' <summary>
    ''' The meter stream's cost, or Nothing before it has written anything.
    ''' </summary>
    ''' <remarks>
    ''' Nothing rather than "0 meter lines into the log", which is what an
    ''' operator hears when meter recording is left on and no radio ever
    ''' connected — a number that adds nothing to the sentence it lengthens.
    ''' The switch being on is the fact worth speaking; the count is only worth
    ''' speaking once there is one.
    ''' </remarks>
    Private Function DescribeMeterLines(count As Long) As String
        If count <= 0 Then Return Nothing
        Return Radios.Lexicon.Get("logging.running.meter_lines",
                                  ("count", count.ToString("N0", CultureInfo.CurrentCulture)))
    End Function

    Private Function DescribeSoundingMeters() As String
        Try
            ' Enumerable.Count explicitly: VB binds a bare .Count on List(Of T)
            ' to the PROPERTY and then reports the lambda as an index, which is
            ' a confusing error for a line that reads perfectly in C#.
            Dim n As Integer = Enumerable.Count(JJFlexWpf.MeterToneEngine.Slots, Function(s) s.Enabled)
            Return Radios.Lexicon.Get("logging.running.tone_slots",
                                      ("count", n.ToString(CultureInfo.CurrentCulture)))
        Catch
            Return Nothing
        End Try
    End Function

    Private Function DescribeFileBytes(path As String) As String
        Try
            If String.IsNullOrEmpty(path) Then Return Nothing
            Dim fi As New FileInfo(path)
            If Not fi.Exists Then Return Nothing
            Return DescribeBytes(fi.Length)
        Catch
            Return Nothing
        End Try
    End Function

#End Region

    ' StartLogAtPath / StopLogSessionAware removed Sprint 31 (#103) along with
    ' the retired trace dialog they served. Nothing else called either one:
    ' redirecting the log to a file of the operator's choosing was that
    ' dialog's whole idea, and it is the idea the diagnostic-log surface
    ' replaced. The log now lives in one place, rotates there, archives there,
    ' and is exported by copying — not by being pointed somewhere else while it
    ' runs. The rule those two existed to protect still holds and is now
    ' enforced by there being no way to break it: nothing flips Tracing.On
    ' without archiving what was already open.

    ''' <summary>
    ''' Open the Saved Diagnostic Logs window — the repurposed TraceAdmin form,
    ''' which held a working archive browser that nothing in the app had
    ''' instantiated since it was built. This is the entrance that makes it
    ''' reachable again.
    ''' </summary>
    Friend Sub ShowSavedDiagnosticLogs()
        Try
            Using dlg As New TraceAdmin()
                dlg.ShowDialog(AppShellForm)
            End Using
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        End Try
    End Sub

    ''' <summary>
    ''' Outcome detail prefix stamped on a capture's manifest entry so the
    ''' browser and any future bundle picker can label it as a capture rather
    ''' than as an ordinary session.
    ''' </summary>
    Friend Const CaptureOutcomeDetailPrefix As String = "Detailed capture: "

    ''' <summary>
    ''' CaptureState — the one line an outside reader can trust about this log.
    '''
    ''' Written at every transition: boot, capture start, capture stop, standing
    ''' log resume, detail-level change, and (with level=Off) as the last line
    ''' of any session being sealed for archive. The LAST CaptureState line in a
    ''' file is therefore the truth about that file: capture=on means a detailed
    ''' capture is writing it right now, level names the trace detail, and a
    ''' sealed file always ends saying capture=off level=Off.
    '''
    ''' This exists because inferring the state from outside was proven wrong on
    ''' 2026-08-21: jjprobe judged "is a capture running" by sniffing the last
    ''' 64 KB of the log for Verbose utterances — which the meter firehose had
    ''' long since pushed out of any 64 KB window — pressed Ctrl+J, Ctrl+D to
    ''' "start" a capture, toggled the one already running, then read old
    ''' Verbose lines out of the archived file and reported the speech channel
    ''' healthy. The app knows its own state; from now on it says so where any
    ''' reader of the file can find it.
    '''
    ''' The wording is a CONTRACT with tools/uia-probe (TraceLog.ParseStateMarker
    ''' in Observe.cs): one line, key=value, scalar fields first and the two
    ''' paths last because paths contain spaces —
    ''' CaptureState: capture=on|off level=[TraceLevel] instance=[N]
    ''' started=[session start, ISO 8601 UTC] version=[app version]
    ''' app=[full path of the app assembly] file=[full path being written]
    ''' Change either side only in step with the other.
    ''' </summary>
    ''' <param name="captureOn">Explicit state, for the moments when
    ''' DetailedCaptureRunning has not caught up with the transition being
    ''' recorded (sealing a capture that is still nominally running).</param>
    ''' <param name="levelOverride">Explicit level, for the seal marker —
    ''' TraceLevel.Off means "this file is finished, nobody is writing it".</param>
    Friend Sub TraceCaptureStateMarker(Optional captureOn As Boolean? = Nothing,
                                       Optional levelOverride As TraceLevel? = Nothing)
        Try
            If Not Tracing.On Then Return
            Dim isOn As Boolean = If(captureOn, DetailedCaptureRunning)
            Dim lvl As TraceLevel = If(levelOverride, Tracing.TheSwitch.Level)
            Dim asmPath As String = If(myAssembly IsNot Nothing,
                                       myAssembly.Location,
                                       Assembly.GetEntryAssembly()?.Location)
            Dim sess As TraceSession = TraceSessionContext.Current
            Dim startedIso As String = If(sess IsNot Nothing,
                sess.BootTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                Date.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            ' The no-level TraceLine overload on purpose: a state line that only
            ' appears at some detail levels is a state line a reader cannot rely
            ' on finding.
            Tracing.TraceLine(
                "CaptureState: capture=" & If(isOn, "on", "off") &
                " level=" & lvl.ToString() &
                " instance=" & ProgramInstance.ToString(CultureInfo.InvariantCulture) &
                " started=" & startedIso &
                " version=" & If(myVersion IsNot Nothing, myVersion.ToString(), "unknown") &
                " app=" & If(asmPath, String.Empty) &
                " file=" & If(Tracing.TraceFile, String.Empty))
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>
    ''' Start a detailed capture: archive whatever session is open, begin a
    ''' fresh one at maximum detail, and remember the standing level so Stop can
    ''' put it back.
    '''
    ''' There is no level choice here on purpose. A capture exists to hand the
    ''' developer maximum evidence; offering less is a trap dressed as a
    ''' courtesy. Four callers share this one implementation: the Diagnostics
    ''' tab, the Command Finder command, the Ctrl+J Ctrl+D chord, and — later —
    ''' the feedback dialog's "detailed trace" toggle.
    '''
    ''' Idempotent: starting a capture that is already running is a no-op that
    ''' still speaks, so the operator is never left guessing.
    ''' </summary>
    ''' <param name="reason">Short phrase recorded in the session manifest.</param>
    Friend Sub StartDetailedCapture(Optional reason As String = "user requested")
        If DetailedCaptureRunning Then
            SpeakDiagnostics(Radios.Lexicon.Get("logging.capture.already_running",
                                                 ("started", FormatClock(_captureStartedLocal.Value))))
            Return
        End If

        Try
            ' Settle the current session BEFORE touching Tracing.On. Nothing may
            ' ever flip the switch without archiving first — that bypass is what
            ' made the old dialog's traces invisible to the browser and got the
            ' leftover file falsely tagged "killed" at the next boot.
            ArchiveCurrentTraceSession(TraceSessionOutcome.CleanExit,
                "Standing diagnostic log closed to begin a detailed capture")

            Tracing.TheSwitch.Level = TraceLevel.Verbose
            Tracing.TraceFile = BootTraceFileName
            Tracing.On = True
            BeginNewTraceSession()
            _captureStartedLocal = Date.Now
            LastCaptureArchivePath = Nothing
            LastUserTraceFile = Tracing.TraceFile
            Tracing.TraceLine(
                $"Detailed capture started {Date.Now:O} reason={reason} level={Tracing.TheSwitch.Level}")
            TraceCaptureStateMarker()
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            _captureStartedLocal = Nothing
            SpeakDiagnostics(Radios.Lexicon.Get("logging.capture.start_failed"))
            ' The reporting pipeline failing is the one case where the offer is
            ' also the fallback: if the capture will not start, the standing log
            ' is the only evidence there is going to be.
            Radios.OperationFailure.Report(Radios.FailureKind.ReportingFailed,
                Radios.Lexicon.Get("logging.capture.start_failed_what"),
                Radios.Lexicon.Get("logging.capture.start_failed_detail"))
            RaiseDiagnosticLogStateChanged()
            Return
        End Try

        SpeakDiagnostics(Radios.Lexicon.Get("logging.capture.started"))
        RaiseDiagnosticLogStateChanged()
    End Sub

    ''' <summary>
    ''' Stop a detailed capture: archive the capture as its own session, restore
    ''' the standing detail level, and keep the always-on log running.
    '''
    ''' That last clause is the repair. Stopping a manual trace used to turn
    ''' tracing off entirely, and the machine then flew unrecorded until the
    ''' next launch — so the one moment an operator had proved they were hunting
    ''' a problem was the moment the app stopped watching.
    ''' </summary>
    Friend Sub StopDetailedCapture()
        If Not DetailedCaptureRunning Then
            SpeakDiagnostics(Radios.Lexicon.Get("logging.capture.not_running"))
            Return
        End If

        Dim started As Date = _captureStartedRequired()
        Dim spoken As String
        Try
            Dim minutes As Integer = CInt(Math.Max(0, Math.Round((Date.Now - started).TotalMinutes)))
            Tracing.TraceLine($"Detailed capture stopped {Date.Now:O} after about {minutes} minute(s)")

            ' Archive under a capture-flavoured outcome detail so the browser
            ' can say "Detailed capture, tonight at 8:14 PM" instead of listing
            ' it as one more anonymous session.
            LastCaptureArchivePath = ArchiveCurrentTraceSessionReturningPath(
                TraceSessionOutcome.CleanExit,
                CaptureOutcomeDetailPrefix & $"{FormatClock(started)}, about {DescribeMinutes(minutes)}")

            _captureStartedLocal = Nothing

            ' Resume the standing log at the standing level, if the operator
            ' keeps one at all.
            If DiagnosticsSettings.KeepDiagnosticLog Then
                Tracing.TheSwitch.Level = DiagnosticsSettings.TraceLevel
                Tracing.TraceFile = BootTraceFileName
                Tracing.On = True
                BeginNewTraceSession()
                Tracing.TraceLine(
                    $"Diagnostic log resumed at {Tracing.TheSwitch.Level} after a detailed capture")
                TraceCaptureStateMarker()
            End If

            spoken = Radios.Lexicon.Get("logging.capture.saved",
                                        ("started", FormatClock(started)), ("duration", DescribeMinutes(minutes)))
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            _captureStartedLocal = Nothing
            spoken = Radios.Lexicon.Get("logging.capture.save_problem")
        End Try

        SpeakDiagnostics(spoken)
        RaiseDiagnosticLogStateChanged()
    End Sub

    Private Function _captureStartedRequired() As Date
        Return If(_captureStartedLocal.HasValue, _captureStartedLocal.Value, Date.Now)
    End Function

    ''' <summary>Toggle the detailed capture. The chord and the button share this.</summary>
    Friend Sub ToggleDetailedCapture(Optional reason As String = "user requested")
        If DetailedCaptureRunning Then StopDetailedCapture() Else StartDetailedCapture(reason)
    End Sub

    ''' <summary>
    ''' Apply a changed KeepDiagnosticLog / DetailLevel immediately and persist
    ''' it. Settings are intents: the operator's choice takes effect now AND
    ''' next launch, without an OK button in between.
    ''' </summary>
    Friend Sub ApplyDiagnosticLogSettings(keepLog As Boolean, detail As Radios.DiagnosticDetail)
        Dim wasOn As Boolean = DiagnosticsSettings.KeepDiagnosticLog
        DiagnosticsSettings.KeepDiagnosticLog = keepLog
        DiagnosticsSettings.DetailLevel = detail
        If Not DiagnosticsSettings.Save(BaseConfigDir) Then
            ' The choice is live for this session either way — refusing an intent
            ' because the disk was busy hands the disk's problem to the operator.
            ' But say plainly that it will not survive a restart, and offer the
            ' evidence, because otherwise they find out at the next launch when
            ' the setting is quietly back where it was.
            Radios.OperationFailure.Report(Radios.FailureKind.SettingNotSaved,
                "Your diagnostic settings could not be saved",
                "The change is in effect right now, but it will not be there the next time you start JJ Flex. " &
                "Something stopped the settings file from being written.")
        End If

        Try
            If DetailedCaptureRunning Then
                ' A capture outranks the standing level while it runs; the new
                ' level lands when the capture stops. Say so rather than
                ' silently appearing to do nothing.
                Tracing.TraceLine(
                    $"Diagnostic settings changed during a capture: keepLog={keepLog} detail={detail}",
                    TraceLevel.Info)
            ElseIf keepLog Then
                If Not wasOn OrElse Not Tracing.On Then
                    Tracing.TheSwitch.Level = DiagnosticsSettings.TraceLevel
                    Tracing.TraceFile = BootTraceFileName
                    Tracing.On = True
                    BeginNewTraceSession()
                    Tracing.TraceLine($"Diagnostic log turned on at {Tracing.TheSwitch.Level}")
                    TraceCaptureStateMarker()
                Else
                    Tracing.TraceLine($"Diagnostic log detail is now {Tracing.TheSwitch.Level}", TraceLevel.Info)
                    Tracing.TheSwitch.Level = DiagnosticsSettings.TraceLevel
                    ' Level changed mid-session, so the state line at the top of
                    ' this file is stale. Re-stamp: the last CaptureState line
                    ' in a file is the one a reader trusts.
                    TraceCaptureStateMarker()
                End If
            ElseIf wasOn Then
                Tracing.TraceLine("Diagnostic log turned off by the operator")
                ArchiveCurrentTraceSession(TraceSessionOutcome.CleanExit,
                    "User turned diagnostic log off")
            End If
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try

        RaiseDiagnosticLogStateChanged()
    End Sub

    ''' <summary>
    ''' Apply a changed "record the meter stream" choice immediately and persist
    ''' it — same intent semantics as ApplyDiagnosticLogSettings, separate sub
    ''' because it governs a different thing: not how much the app records about
    ''' ITSELF, but whether the radio's continuous meter readings go into the
    ''' log at all (coalesced, one line per meter per second — MeterTraceStream
    ''' holds the design and the 2026-08-21 numbers that forced it).
    '''
    ''' The transition is traced unconditionally, in both directions: a reader
    ''' of the file needs to know that meter lines stopping mid-session means
    ''' the operator turned them off, not that the meters died.
    ''' </summary>
    ''' <summary>
    ''' Turn the spoken-output transcript on or off for this session, and
    ''' remember the choice.
    ''' </summary>
    ''' <remarks>
    ''' The diagnostic log records what the program DID. This records what the
    ''' operator HEARD — every spoken message with its verbosity and origin,
    ''' every earcon, every CW notification, one JSON line each. When somebody
    ''' can reproduce a problem with what the app said, that is the evidence,
    ''' and nothing else in a problem report carries it.
    '''
    ''' Applied live rather than at next start: Configure closes any open
    ''' transcript and opens a fresh one, writing the session-start marker. So
    ''' the operator turns it on, reproduces the fault, and the file is already
    ''' waiting — which is the whole point, and the opposite of asking somebody
    ''' to restart before they can capture what just happened.
    ''' </remarks>
    Friend Sub ApplySpokenTranscriptSetting(record As Boolean)
        DiagnosticsSettings.RecordSpokenOutput = record
        ' Render is left exactly as it is: this switch decides whether output is
        ' WRITTEN DOWN, never whether it is heard. Silencing the app from a
        ' diagnostics checkbox would be a trap.
        Radios.OutputChannelRecorder.Configure(
            Radios.OutputChannelRecorder.RenderEnabled, record, Nothing)
        If Not DiagnosticsSettings.Save(BaseConfigDir) Then
            Radios.OperationFailure.Report(Radios.FailureKind.SettingNotSaved,
                "Your transcript setting could not be saved",
                "The change is in effect right now, but it will not be there the next time you start JJ Flex. " &
                "Something stopped the settings file from being written.")
        End If
        Try
            Tracing.TraceLine("SpokenTranscript: recording=" & If(record, "on", "off"))
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
        RaiseDiagnosticLogStateChanged()
    End Sub

    Friend Sub ApplyMeterStreamSetting(record As Boolean)
        DiagnosticsSettings.RecordMeterStream = record
        Radios.MeterTraceStream.Enabled = record
        If Not DiagnosticsSettings.Save(BaseConfigDir) Then
            Radios.OperationFailure.Report(Radios.FailureKind.SettingNotSaved,
                "Your meter stream setting could not be saved",
                "The change is in effect right now, but it will not be there the next time you start JJ Flex. " &
                "Something stopped the settings file from being written.")
        End If
        Try
            Tracing.TraceLine("MeterStream: recording=" & If(record, "on", "off"))
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
        RaiseDiagnosticLogStateChanged()
    End Sub

    ''' <summary>
    ''' One sentence that answers "what is being recorded, at what detail, and
    ''' is a capture running" — the question the old dialog could not answer.
    ''' Used by the Diagnostics tab's status line and by the Command Finder
    ''' command's spoken confirmation, so the two cannot disagree.
    ''' </summary>
    Friend Function DescribeDiagnosticLogState() As String
        Try
            If DetailedCaptureRunning Then
                Return $"Detailed capture in progress, started {FormatClock(_captureStartedLocal.Value)}."
            End If
            If Not DiagnosticsSettings.KeepDiagnosticLog OrElse Not Tracing.On Then
                Return "Diagnostic log is off."
            End If
            Dim since As String = ""
            Try
                Dim session As TraceSession = TraceSessionContext.Current
                If session IsNot Nothing Then
                    since = $", running since {FormatClock(session.BootTimeUtc.ToLocalTime())}"
                End If
            Catch
            End Try
            Return $"Diagnostic log is on at {DiagnosticsSettings.DetailWord} detail{since}. No capture in progress."
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            Return "Diagnostic log state is not available."
        End Try
    End Function

    ''' <summary>Local clock in the app's spoken style, e.g. "8:14 PM".</summary>
    Friend Function FormatClock(moment As Date) As String
        Return moment.ToString("h:mm tt")
    End Function

    Private Function DescribeMinutes(minutes As Integer) As String
        If minutes <= 0 Then Return Radios.Lexicon.Get("logging.time.under_a_minute")
        Return If(minutes = 1, Radios.Lexicon.Get("logging.time.minutes_one",
            ("minutes", minutes)), Radios.Lexicon.Get("logging.time.minutes_many",
            ("minutes", minutes)))
    End Function

    ''' <summary>
    ''' Speak a diagnostics message at Critical verbosity. Every action in this
    ''' surface speaks its outcome (no-silent-keystrokes), and these are all
    ''' user-initiated, so none of them is chatter.
    ''' </summary>
    Friend Sub SpeakDiagnostics(msg As String)
        Try
            Radios.ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical)
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' Close the current session and open a fresh one at the standing level.
    ''' Used where something has to release the live file for a moment — the
    ''' problem-report bundler above all — so that the log resumes instead of
    ''' staying dead for the rest of the session.
    ''' </summary>
    Friend Sub RestartDiagnosticLog(reason As String)
        Try
            If Not DiagnosticsSettings.KeepDiagnosticLog Then Return
            If Tracing.On Then Return ' already running; nothing to restart
            Tracing.TheSwitch.Level = If(DetailedCaptureRunning, TraceLevel.Verbose, DiagnosticsSettings.TraceLevel)
            Tracing.TraceFile = BootTraceFileName
            Tracing.On = True
            BeginNewTraceSession()
            Tracing.TraceLine($"Diagnostic log resumed ({reason}) at {Tracing.TheSwitch.Level}")
            TraceCaptureStateMarker()
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
        RaiseDiagnosticLogStateChanged()
    End Sub

    ''' <summary>
    ''' Begin a new trace session. Captures session id, boot time, and current verbosity
    ''' so that on archive we can write a structured manifest entry. Idempotent — calling
    ''' twice without an intervening archive overwrites the previous session pointer.
    ''' Per memory/project_trace_persistence_design.md, Sprint 29 Track A.
    ''' </summary>
    Friend Sub BeginNewTraceSession()
        Try
            Dim session As TraceSession = TraceSessionContext.BeginSession()
            session.VerbosityLevel = Tracing.TheSwitch.Level.ToString()
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>
    ''' Archive the active trace session (if any) into the per-session archive: compress
    ''' the trace file, write a manifest entry, and delete the source. Captures the trace
    ''' path BEFORE closing the listener (since Tracing.On = False clears Tracing.TraceFile).
    ''' Idempotent — if no session is active, no-op. Called at clean exit and from the
    ''' shutdown event for belt-and-suspenders.
    ''' </summary>
    ''' <param name="outcome">Outcome tag for the manifest entry. Defaults to clean_exit.</param>
    ''' <param name="detail">Optional outcome detail string.</param>
    Friend Sub ArchiveCurrentTraceSession(Optional outcome As String = Nothing, Optional detail As String = Nothing)
        ArchiveCurrentTraceSessionReturningPath(outcome, detail)
    End Sub

    ''' <summary>
    ''' Same work as <see cref="ArchiveCurrentTraceSession"/>, but hands back the
    ''' full path of the archive it just wrote (or Nothing).
    '''
    ''' Exists so stopping a detailed capture can offer "Export this capture..."
    ''' immediately — the common next act after a capture is getting the file
    ''' somewhere sendable, and making the operator go find it in a browse list
    ''' is the friction this whole surface exists to remove.
    ''' </summary>
    Friend Function ArchiveCurrentTraceSessionReturningPath(Optional outcome As String = Nothing,
                                                            Optional detail As String = Nothing) As String
        Dim archivedRelName As String = Nothing
        Try
            Dim session As TraceSession = TraceSessionContext.Current
            If session Is Nothing Then Return Nothing

            If Not String.IsNullOrEmpty(outcome) Then
                session.MarkOutcome(outcome, detail)
            End If

            ' Seal the file with a state line saying nobody writes it any more.
            ' A finished capture is full of Verbose lines that look exactly like
            ' a running one — on 2026-08-21 jjprobe read such a corpse moments
            ' after the capture was toggled off and reported the speech channel
            ' live. capture=off level=Off as the file's last CaptureState line
            ' is what makes a corpse distinguishable from a capture in flight.
            ' Written BEFORE the rotation snapshot below: this line is itself a
            ' write, and a write can rotate, which would stale the part number.
            TraceCaptureStateMarker(captureOn:=False, levelOverride:=TraceLevel.Off)

            ' Capture rotation state before closing the listener — Tracing.On =
            ' False disposes it and the part number goes with it.
            Dim hadParts As Boolean = Tracing.SessionHasParts
            Dim finalPartNumber As Integer = Tracing.CurrentPartNumber

            Dim tracePath As String = Tracing.TraceFile
            Tracing.On = False ' flushes + closes; Tracing.TraceFile becomes null

            TraceSessionContext.EndSession()

            If Not String.IsNullOrEmpty(tracePath) Then
                If hadParts Then
                    ' This session rotated, so its tail is the FINAL PART of a
                    ' chain, not a standalone whole-session archive. Rename to
                    ' the part name first so the plain-text chain in AppData is
                    ' complete and consistently named, then archive from there.
                    Dim partPath As String = RenameTraceToStampedPart(tracePath, session.BootTimeUtc, finalPartNumber)
                    If Not String.IsNullOrEmpty(partPath) Then
                        archivedRelName = SessionArchive.ArchiveSession(TraceArchiveDir, partPath, session,
                            deleteSourceAfter:=False, partNumber:=finalPartNumber, isFinalPart:=True)
                    End If
                Else
                    Dim relName As String = SessionArchive.ArchiveSession(
                        TraceArchiveDir, tracePath, session, deleteSourceAfter:=False)
                    If Not String.IsNullOrEmpty(relName) Then
                        archivedRelName = relName
                        ' Archive succeeded; preserve source as stamp-named .txt
                        ' for the plain-text retention window. See
                        ' RenameTraceToStamped / PrunePlainTextTracesOlderThan.
                        RenameTraceToStamped(tracePath, session.BootTimeUtc)
                    End If
                End If
            End If

            ' Let queued part compressions finish so a rotated session doesn't
            ' leave uncompressed parts behind. Bounded: exit must not hang on a
            ' slow disk. Anything still queued gets picked up by the leftover
            ' sweep at next boot, so the worst case is a delay, not a loss.
            If hadParts Then
                Tracing.WaitForPendingArchives(TimeSpan.FromSeconds(30))
            End If
        Catch ex As Exception
            ' Trace-only: this runs during shutdown, where a modal dialog
            ' carrying a raw framework message is the worst possible outcome.
            Tracing.ErrTraceOnly(ex)
        End Try

        If String.IsNullOrEmpty(archivedRelName) Then Return Nothing
        Try
            Return Path.Combine(TraceArchiveDir,
                archivedRelName.Replace("/"c, Path.DirectorySeparatorChar))
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' Plain-text trace retention window in days. After this many days, the
    ''' stamp-named .txt files at BaseConfigDir get pruned at next boot; the
    ''' LZMA .zip archives in TraceArchiveDir keep their full 30-day retention.
    ''' Rationale: testers and screen-reader users open plain text in Notepad
    ''' directly; the .zip is the durable record but needs 7-Zip to extract.
    ''' 1 day balances "emergent crashes are directly readable" against
    ''' "AppData stays tidy."
    ''' </summary>
    Private Const PlainTextTraceRetentionDays As Integer = 1

    ''' <summary>
    ''' After SessionArchive.ArchiveSession compresses a trace, rename the
    ''' plain-text source to a stamp-named sibling so the next session opens
    ''' a fresh JJFlexRadioTrace.txt but the just-archived plain text remains
    ''' directly readable in Notepad / NVDA / JAWS. Solves the Sprint 29
    ''' Track A regression where deleteSourceAfter=True removed the testers'
    ''' familiar JJFlexRadioTrace.txt workflow. Stamp uses the session's boot
    ''' time so the filename matches the .zip manifest entry. Collision-safe
    ''' (appends -1, -2, ... if multiple sessions share a boot second).
    ''' On failure the source is deleted as a last resort so the next session
    ''' isn't blocked from opening a clean trace file.
    ''' </summary>
    Private Sub RenameTraceToStamped(tracePath As String, bootTimeUtc As DateTime)
        Try
            Dim dir As String = Path.GetDirectoryName(tracePath)
            Dim baseName As String = Path.GetFileNameWithoutExtension(tracePath)
            Dim ext As String = Path.GetExtension(tracePath)
            Dim stamp As DateTime = bootTimeUtc.ToLocalTime()
            Dim target As String = Path.Combine(dir, $"{baseName}-{stamp:yyyyMMdd-HHmmss}{ext}")
            Dim suffix As Integer = 1
            While File.Exists(target)
                target = Path.Combine(dir, $"{baseName}-{stamp:yyyyMMdd-HHmmss}-{suffix}{ext}")
                suffix += 1
            End While
            File.Move(tracePath, target)
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            Try
                File.Delete(tracePath)
            Catch
            End Try
        End Try
    End Sub

    ''' <summary>
    ''' Rename a closed trace segment to the part-file name its chain uses, so
    ''' the clean-exit tail (and an adopted killed tail) lands beside the parts
    ''' rotation already produced. Same shape rotation itself uses:
    ''' "&lt;stem&gt;-&lt;boot stamp&gt;-part-NNN.txt". Returns the new path, or
    ''' empty on failure.
    ''' </summary>
    Private Function RenameTraceToStampedPart(tracePath As String, bootTimeUtc As DateTime, partNumber As Integer) As String
        Try
            Dim dir As String = Path.GetDirectoryName(tracePath)
            Dim baseName As String = Path.GetFileNameWithoutExtension(tracePath)
            Dim ext As String = Path.GetExtension(tracePath)
            Dim stamp As DateTime = bootTimeUtc.ToLocalTime()
            Dim target As String = Path.Combine(dir, $"{baseName}-{stamp:yyyyMMdd-HHmmss}-part-{partNumber:D3}{ext}")
            Dim suffix As Integer = 1
            While File.Exists(target)
                target = Path.Combine(dir, $"{baseName}-{stamp:yyyyMMdd-HHmmss}-part-{partNumber:D3}-{suffix}{ext}")
                suffix += 1
            End While
            File.Move(tracePath, target)
            Return target
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            Return String.Empty
        End Try
    End Function

    ''' <summary>
    ''' A chain of leftover part files from a previous run, grouped by the boot
    ''' stamp baked into their file names.
    ''' </summary>
    Private Class LeftoverChain
        Public Session As TraceSession
        Public HighestPart As Integer
        Public StampLocal As DateTime
    End Class

    ''' <summary>
    ''' Archive part files left in AppData by a previous run. Rotation hands each
    ''' closed part to a background compressor; if the app is killed (or exits)
    ''' before that finishes, the plain-text part survives but has no manifest
    ''' entry — and the 24h plain-text sweep would eventually delete unread
    ''' evidence. This closes that hole at boot.
    '''
    ''' Parts are grouped by the boot stamp in their names, so one prior session's
    ''' chain is reconstructed as one TraceSession and its parts keep a shared
    ''' archive stem. Parts already archived (matched by source_name in the
    ''' manifest) are skipped, so this is idempotent across boots.
    '''
    ''' Returns the chain matching the still-present live trace — the caller
    ''' attaches that trace as the chain's final part — or Nothing.
    ''' </summary>
    Private Function ArchiveLeftoverTraceChains(liveTracePath As String) As LeftoverChain
        Dim newest As LeftoverChain = Nothing
        Try
            If Not Directory.Exists(BaseConfigDir) Then Return Nothing
            Dim stem As String = LiveTraceStem
            Dim chains As New Dictionary(Of DateTime, List(Of Tuple(Of String, Integer)))

            For Each partFile As String In Directory.GetFiles(BaseConfigDir, stem & "-*-part-*.txt")
                Dim name As String = Path.GetFileNameWithoutExtension(partFile)
                ' <stem>-yyyyMMdd-HHmmss-part-NNN[-collisionSuffix]
                Dim tail As String = name.Substring(stem.Length + 1)
                Dim bits As String() = tail.Split("-"c)
                If bits.Length < 4 Then Continue For
                Dim stamp As DateTime
                If Not DateTime.TryParseExact(bits(0) & "-" & bits(1), "yyyyMMdd-HHmmss",
                                              CultureInfo.InvariantCulture, DateTimeStyles.None, stamp) Then
                    Continue For
                End If
                Dim partNo As Integer
                If Not Integer.TryParse(bits(3), partNo) Then Continue For

                If Not chains.ContainsKey(stamp) Then chains(stamp) = New List(Of Tuple(Of String, Integer))
                chains(stamp).Add(Tuple.Create(partFile, partNo))
            Next

            For Each kvp In chains
                Dim stampLocal As DateTime = kvp.Key
                Dim session As New TraceSession(stampLocal.ToUniversalTime())
                session.MarkOutcome(TraceSessionOutcome.Killed,
                    "Leftover trace parts adopted at next launch (no clean exit observed)")

                Dim highest As Integer = 0
                For Each item In kvp.Value.OrderBy(Function(t) t.Item2)
                    If item.Item2 > highest Then highest = item.Item2
                    Dim fileName As String = Path.GetFileName(item.Item1)
                    If SessionArchive.IsSourceArchived(TraceArchiveDir, fileName) Then Continue For
                    SessionArchive.ArchiveSession(TraceArchiveDir, item.Item1, session,
                        deleteSourceAfter:=False, partNumber:=item.Item2, isFinalPart:=False)
                Next

                If newest Is Nothing OrElse stampLocal > newest.StampLocal Then
                    newest = New LeftoverChain With {
                        .Session = session,
                        .HighestPart = highest,
                        .StampLocal = stampLocal
                    }
                End If
            Next
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            Return Nothing
        End Try

        ' Only claim the live leftover for a chain when there IS a live leftover.
        If String.IsNullOrEmpty(liveTracePath) Then Return Nothing
        Return newest
    End Function

    ''' <summary>
    ''' Prune stamp-named plain-text trace files older than retentionDays. Only
    ''' files matching DailyTraceFilePrefix &amp; "-*.txt" are considered — the
    ''' live JJFlexRadioTrace.txt (no hyphen) is left alone, as is the legacy
    ''' JJFlexRadioTraceOld.txt, and the no-hyphen daily-trace files created by
    ''' StartDailyTraceIfEnabled (those have their own ArchiveOldDailyTraces).
    ''' The compressed .zip archives in TraceArchiveDir are not touched here.
    '''
    ''' Rotation parts ("&lt;stem&gt;-&lt;stamp&gt;-part-NNN.txt") share this
    ''' shape and age out on the same 24h convenience window — the .zip in the
    ''' archive is the durable copy of every part, so nothing is lost. The second
    ''' pattern covers instance 2+ ("JJFlexRadio2Trace-..."), whose stem doesn't
    ''' match DailyTraceFilePrefix and would otherwise accumulate forever.
    ''' </summary>
    Private Sub PrunePlainTextTracesOlderThan(retentionDays As Integer)
        If retentionDays <= 0 Then Return
        Try
            If Not Directory.Exists(BaseConfigDir) Then Return
            Dim cutoffUtc As DateTime = DateTime.UtcNow.AddDays(-retentionDays)
            Dim patterns As New List(Of String) From {$"{DailyTraceFilePrefix}-*.txt"}
            Dim instanceStem As String = $"{LiveTraceStem}-*.txt"
            If Not patterns.Contains(instanceStem) Then patterns.Add(instanceStem)

            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each pattern As String In patterns
                For Each path As String In Directory.GetFiles(BaseConfigDir, pattern)
                    If Not seen.Add(path) Then Continue For
                    Try
                        Dim fi As New FileInfo(path)
                        If fi.LastWriteTimeUtc < cutoffUtc Then
                            File.Delete(path)
                        End If
                    Catch ex As Exception
                        Tracing.ErrTraceOnly(ex)
                    End Try
                Next
            Next
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>
    ''' Archive maintenance at app boot: reconcile manifest with disk (drop entries
    ''' whose archive file is gone) and prune entries older than the retention window
    ''' (default 30 days per spec). Cheap idempotent housekeeping; safe to call before
    ''' any trace work begins. Also prunes stamp-named plain-text traces older than
    ''' PlainTextTraceRetentionDays.
    ''' </summary>
    Friend Sub TraceArchiveBootMaintenance()
        Try
            SessionArchive.Reconcile(TraceArchiveDir)
            SessionArchive.PruneOlderThan(TraceArchiveDir, SessionArchive.DefaultRetentionDays)
            ' Order matters: adopt leftover parts into the archive BEFORE the
            ' plain-text sweep can delete them. Rotation is what creates parts,
            ' so this only finds anything after a run that rotated and then died
            ' before its background compression finished.
            ArchiveLeftoverTraceChains(Nothing)
            PrunePlainTextTracesOlderThan(PlainTextTraceRetentionDays)
            ' One-release sunset sweep for the retired daily-trace files.
            ArchiveOldDailyTraces()
            ' Crash dumps get the same boot-time housekeeping the trace archive
            ' has had since Sprint 29 — without it the Errors folder grew by a
            ' full-memory dump per crash, forever.
            PruneCrashReports()
            ' Downloaded firmware images are a pure cache. Nothing ever removed
            ' them, and on the developer's own machine they had reached 369 MB.
            PruneFirmwareCache(DiagnosticsSettings.FirmwareCacheDays)
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
    End Sub

    ''' <summary>The folder downloaded radio firmware images land in.</summary>
    Friend ReadOnly Property FirmwareCacheDir As String
        Get
            Return Path.Combine(BaseConfigDir, "firmware")
        End Get
    End Property

    ''' <summary>
    ''' Age out downloaded firmware images. Safe to be aggressive here in a way
    ''' it is NOT safe to be with crash dumps: a firmware image is re-downloadable
    ''' by definition, so the worst case is one download, whereas a deleted dump
    ''' is evidence that cannot be recreated. Returns bytes reclaimed.
    ''' Never throws — housekeeping must not take the app down.
    ''' </summary>
    Friend Function PruneFirmwareCache(retentionDays As Integer) As Long
        Dim freed As Long = 0
        Try
            If retentionDays <= 0 Then Return 0
            If Not Directory.Exists(FirmwareCacheDir) Then Return 0
            Dim cutoffUtc As DateTime = DateTime.UtcNow.AddDays(-retentionDays)
            For Each path As String In Directory.GetFiles(FirmwareCacheDir, "*", SearchOption.AllDirectories)
                Try
                    Dim fi As New FileInfo(path)
                    If fi.LastWriteTimeUtc < cutoffUtc Then
                        Dim len As Long = fi.Length
                        fi.Delete()
                        freed += len
                    End If
                Catch
                    ' A locked image just stays; the next boot retries.
                End Try
            Next
            If freed > 0 Then
                Tracing.TraceLine(
                    $"PruneFirmwareCache: reclaimed {freed \ (1024 * 1024)} MB of downloaded firmware older than {retentionDays} days",
                    TraceLevel.Info)
            End If
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
        Return freed
    End Function

    ''' <summary>
    ''' Total bytes under a folder, recursively. Best-effort; unreadable files
    ''' are skipped rather than aborting the walk.
    ''' </summary>
    Friend Function FolderSizeBytes(dir As String) As Long
        Dim total As Long = 0
        Try
            If String.IsNullOrEmpty(dir) OrElse Not Directory.Exists(dir) Then Return 0
            For Each path As String In Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                Try
                    total += New FileInfo(path).Length
                Catch
                End Try
            Next
        Catch
        End Try
        Return total
    End Function

    ''' <summary>
    ''' Human size for speech and labels. "About 2.2 gigabytes" reads far better
    ''' than a byte count, and the operator hearing this has no way to see a
    ''' folder listing.
    ''' </summary>
    Friend Function DescribeBytes(bytes As Long) As String
        If bytes < 1024 Then Return Radios.Lexicon.Get("logging.size.bytes", ("value", bytes))
        If bytes < 1024L * 1024 Then Return Radios.Lexicon.Get("logging.size.kilobytes", ("value", bytes \ 1024))
        If bytes < 1024L * 1024 * 1024 Then Return Radios.Lexicon.Get("logging.size.megabytes", ("value", bytes \ (1024L * 1024)))
        Return Radios.Lexicon.Get("logging.size.gigabytes", ("value", (bytes / (1024.0 * 1024 * 1024)).ToString("F1")))
    End Function

    ''' <summary>
    ''' What the settings folder is costing, broken down by what it is costing
    ''' it ON. Nothing in the app ever mentioned any of this, which lands
    ''' hardest on the operator least able to notice a folder quietly growing.
    ''' </summary>
    Friend Function DescribeDiagnosticStorage() As String
        Try
            Dim errorsDir As String = Path.Combine(BaseConfigDir, "Errors")
            Dim crashBytes As Long = FolderSizeBytes(errorsDir)
            Dim firmwareBytes As Long = FolderSizeBytes(FirmwareCacheDir)
            Dim archiveBytes As Long = FolderSizeBytes(TraceArchiveDir)
            Dim looseBytes As Long = LoosePlainTextTraceBytes()
            Dim totalBytes As Long = FolderSizeBytes(BaseConfigDir)

            Return Radios.Lexicon.Get("logging.storage.summary",
                                      ("total", DescribeBytes(totalBytes)),
                                      ("crash", DescribeBytes(crashBytes)),
                                      ("firmware", DescribeBytes(firmwareBytes)),
                                      ("archive", DescribeBytes(archiveBytes)),
                                      ("loose", DescribeBytes(looseBytes)))
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            Return Radios.Lexicon.Get("logging.storage.unmeasurable")
        End Try
    End Function

    ''' <summary>
    ''' Every loose plain-text log file at the root of the settings folder —
    ''' the stamp-named siblings the live log leaves behind, plus rotation
    ''' parts. Excludes the live file itself.
    ''' </summary>
    Friend Function LoosePlainTextTraceFiles() As List(Of String)
        Dim result As New List(Of String)
        Try
            If Not Directory.Exists(BaseConfigDir) Then Return result
            Dim live As String = BootTraceFileName
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each pattern As String In {$"{DailyTraceFilePrefix}-*.txt", $"{LiveTraceStem}-*.txt"}
                For Each path As String In Directory.GetFiles(BaseConfigDir, pattern)
                    If String.Equals(path, live, StringComparison.OrdinalIgnoreCase) Then Continue For
                    If seen.Add(path) Then result.Add(path)
                Next
            Next
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
        End Try
        Return result
    End Function

    Friend Function LoosePlainTextTraceBytes() As Long
        Dim total As Long = 0
        For Each path As String In LoosePlainTextTraceFiles()
            Try
                total += New FileInfo(path).Length
            Catch
            End Try
        Next
        Return total
    End Function

    ''' <summary>
    ''' Delete every loose plain-text log file at the settings-folder root,
    ''' regardless of age. This is the MANUAL control Noel asked for, and the
    ''' "regardless of age" is the point: automatic pruning deliberately keeps
    ''' the last day, and the operator who just filled the disk with a Verbose
    ''' capture should not have to wait a day to get it back.
    '''
    ''' The compressed sessions in the Traces folder are NOT touched — they are
    ''' the durable copy, and every one of these files has one. So this reclaims
    ''' space without discarding evidence. Returns (filesRemoved, bytesFreed).
    ''' </summary>
    Friend Function DeleteLoosePlainTextTraces() As (Files As Integer, Bytes As Long)
        Dim files As Integer = 0
        Dim bytes As Long = 0
        For Each path As String In LoosePlainTextTraceFiles()
            Try
                Dim len As Long = New FileInfo(path).Length
                File.Delete(path)
                files += 1
                bytes += len
            Catch ex As Exception
                Tracing.ErrTraceOnly(ex)
            End Try
        Next
        Tracing.TraceLine(
            $"DeleteLoosePlainTextTraces: removed {files} file(s), {bytes} bytes", TraceLevel.Info)
        Return (files, bytes)
    End Function

    ''' <summary>
    ''' User-initiated trace archive prune. Removes entries + archive files older than
    ''' <paramref name="retentionDays"/> (or the default 30 if &lt;= 0). Returns the
    ''' number of entries pruned. Speaks the result so the user gets confirmation
    ''' (per project_no_silent_keystrokes_rule.md). Safe to call from any UI thread.
    ''' Per Sprint 29 Track A Phase 2; spec at memory/project_trace_persistence_design.md.
    ''' </summary>
    Friend Function PerformTraceArchivePrune(Optional retentionDays As Integer = 0) As Integer
        Dim days As Integer = If(retentionDays > 0, retentionDays, SessionArchive.DefaultRetentionDays)
        Dim pruned As Integer = 0
        Try
            pruned = SessionArchive.PruneOlderThan(TraceArchiveDir, days)
            Tracing.TraceLine($"PerformTraceArchivePrune: removed {pruned} entries older than {days} days", TraceLevel.Info)
        Catch ex As Exception
            Tracing.ErrTraceOnly(ex)
            Try
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.trace.prune_failed"), VerbosityLevel.Critical)
            Catch
            End Try
            Return 0
        End Try

        Try
            Dim msg As String
            If pruned = 0 Then
                msg = Radios.Lexicon.Get("logging.trace.prune_none", ("days", days))
            Else
                msg = If(pruned = 1, Radios.Lexicon.Get("logging.trace.prune_one",
                    ("pruned", pruned), ("days", days)), Radios.Lexicon.Get("logging.trace.prune_many",
                    ("pruned", pruned), ("days", days)))
            End If
            Radios.ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical)
        Catch
        End Try

        Return pruned
    End Function

    Friend Power As Boolean = False
    Friend LastUserTraceFile As String ' Last user-started trace file (see DebugInfo)
    Friend WithEvents Operators As PersonalData = Nothing
    Friend WithEvents Knob As FlexKnob = Nothing
    ''' <summary>
    ''' (ReadOnly) the current operator
    ''' </summary>
    Friend ReadOnly Property CurrentOp As PersonalData.personal_v1
        Get
            Return Operators.CurrentItem
        End Get
    End Property
    ''' <summary>
    ''' ID of the current operator
    ''' </summary>
    Friend Property CurrentOpID As Integer
        Get
            Return Operators.CurrentID
        End Get
        Set(ByVal value As Integer)
            Operators.CurrentID = value
        End Set
    End Property
    Friend CurrentRig As FlexBase.RigData
    Friend CurrentRigID As Integer
    Friend WithEvents RigControl As FlexBase
    ''' <summary>
    ''' Current rig's open parameters.
    ''' </summary>
    Friend OpenParms As FlexBase.OpenParms
    Friend StationName As String

    ''' <summary>
    ''' Convenience accessor for the WPF MainWindow UserControl.
    ''' Used by VB.NET code that previously referenced Form1 directly.
    ''' Returns the WPF MainWindow instance from ApplicationEvents.
    ''' </summary>
    Friend ReadOnly Property WpfMainWindow As JJFlexWpf.MainWindow
        Get
            Return My.MyApplication.WpfMainWindow
        End Get
    End Property

    ''' <summary>
    ''' Convenience accessor for the ShellForm (WinForms host).
    ''' Used for window-level operations (Title, Activate, etc.).
    ''' </summary>
    Friend ReadOnly Property AppShellForm As ShellForm
        Get
            Return My.MyApplication.TheShellForm
        End Get
    End Property

    Friend Const HamqthLookupID As String = "JJRadio"
    Friend Const HamqthLookupPassword As String = "JJRadio"

    ''' <summary>
    ''' Create a new StationLookupWindow, passing current operator callbook settings.
    ''' WPF windows can't be reshown after closing, so create a new one each time.
    ''' </summary>
    Friend Function CreateStationLookupWindow() As JJFlexWpf.StationLookupWindow
        Dim source As String = ""
        Dim username As String = ""
        Dim password As String = ""
        Dim opCall As String = ""
        Dim opGrid As String = ""

        If CurrentOp IsNot Nothing Then
            source = If(CurrentOp.CallbookLookupSource, "")
            username = If(CurrentOp.CallbookUsername, "")
            password = If(CurrentOp.DecryptedCallbookPassword, "")
            opCall = If(CurrentOp.callSign, "")
            opGrid = If(CurrentOp.GridSquare, "")
        End If

        Return New JJFlexWpf.StationLookupWindow(source, username, password, opCall, opGrid)
    End Function

    ''' <summary>
    ''' Send a frequency string (in Hz) to the radio. Returns the parsed Hz
    ''' so the caller can drive boundary/sub-band checks against the
    ''' authoritative new value (RigControl.VirtualRXFrequency lags behind
    ''' a FlexLib round-trip and gave stale-band reads on Ctrl+F jumps).
    ''' Restored from pre-Sprint-24 KeyCommands.vb.
    ''' </summary>
    Friend Function WriteFreq(ByVal str As String) As Long
        Dim hz As Long = CLng(str)
        Tracing.TraceLine($"WriteFreq: input='{str}' parsed={hz} Hz", TraceLevel.Info)
        RigControl.Frequency = hz
        Dim display As String = RigControl.Callouts.FormatFreq(CULng(hz))
        If display IsNot Nothing AndAlso display.Length > 0 Then
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.tune.tuned_to", ("display", display)), True)
        End If
        JJFlexWpf.EarconPlayer.ConfirmTone()
        Return hz
    End Function

    ''' <summary>
    ''' Show DX cluster. Placeholder — cluster UI requires reimplementation after key migration.
    ''' </summary>
    Friend Sub ShowArCluster()
        Tracing.TraceLine("ShowArCluster: not yet reimplemented after key migration", TraceLevel.Warning)
    End Sub

    ''' <summary>
    ''' W2 wattmeter.
    ''' </summary>
    Friend W2WattMeter As W2
    Friend W2ConfigFile As String
    Private W2ConfigFileBasename As String = "w2.xml"

    Friend Sub GetConfigInfo()
        myAssembly = Assembly.GetEntryAssembly
        myAssemblyName = myAssembly.GetName
        myVersion = myAssemblyName.Version
        BaseConfigDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) &
            "\" & InternalName

        ' A run can be pointed at a throwaway settings tree with JJFLEX_CONFIG_DIR.
        ' For automated runs and parallel agents: without it, every instance from
        ' every build reads and writes the operator's ONE live folder, which on
        ' 2026-08-21 is how a background agent rewrote his KeyDefs.xml.
        '
        ' Refusals are announced rather than swallowed. An override that was set
        ' but rejected means someone believes they are isolated while they are
        ' writing live settings, which is worse than either outcome by itself.
        BaseConfigDir = Radios.RadioConfig.ResolveStartupDirectory(
            BaseConfigDir,
            Environment.GetEnvironmentVariable(Radios.RadioConfig.ConfigDirOverrideVariable),
            UsingTemporaryConfigDir,
            ConfigDirRefusal)

        Try
            If Not Directory.Exists(BaseConfigDir) Then
                ' The welcome screen is for a first-run OPERATOR. A temporary
                ' tree is a fresh directory by definition, so prompting there
                ' would block every automated run on a dialog nobody can see.
                If Not UsingTemporaryConfigDir Then
                    ' show welcome screen.
                    Dim rslt As DialogResult
                    Do
                        rslt = Welcome.ShowDialog
                        If rslt = DialogResult.Abort Then
                            End
                        End If
                    Loop While rslt = DialogResult.Cancel
                End If
                Directory.CreateDirectory(BaseConfigDir)
            End If
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
            Exit Sub
        End Try

        ProgramInstance = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length

        ' Tell the trace subsystem where rotated parts get compressed to. MUST
        ' happen before any trace file opens: a live trace that rotates without
        ' an archive root keeps its part as plain text only. Set unconditionally
        ' (not inside the BootTrace branch) because a detailed capture can open a
        ' live trace with the standing log switched off.
        Tracing.RotationArchiveRootDir = TraceArchiveDir

        ' The diagnostic log's own settings, read before the log opens because
        ' they decide whether it opens at all. App-level by necessity: this runs
        ' long before an operator has been chosen, so a per-operator preference
        ' could never govern boot. Absent file = defaults = exactly the previous
        ' behaviour, so upgrading changes nobody's experience.
        DiagnosticsSettings = Radios.DiagnosticsConfig.Load(BaseConfigDir)
        ' The meter stream flag rides with the settings load: the flag lives as
        ' a static in Radios (FlexBase's handlers read it at meter rate on
        ' FlexLib's packet thread) and this is the one place boot knows what the
        ' operator chose. Off by default — see DiagnosticsConfig.RecordMeterStream.
        Radios.MeterTraceStream.Enabled = DiagnosticsSettings.RecordMeterStream
        WireDiagnosticsBridge()
        ' Immediately after the bridge, and for the same reason: this is the
        ' first moment the diagnostics settings are known, and every standing
        ' registration is a predicate over them (#253).
        WireRunningCostRegister()

        ' The debugger guard stays an AND-term: attach-time behaviour is
        ' unchanged, and the operator's KeepDiagnosticLog choice is the new
        ' half. This is the control that never existed for BootTrace.
        BootTrace = (Not Debugger.IsAttached) AndAlso DiagnosticsSettings.KeepDiagnosticLog
        If BootTrace Then
            RotateBootTraceIfNeeded()
            TraceArchiveBootMaintenance()
            ' Boot detail level. Three sources, in increasing priority:
            '   1. Normal (Info) — the default this app has always used
            '   2. the operator's standing DetailLevel from Settings > Diagnostics
            '   3. JJFLEX_BOOT_TRACE_LEVEL=Verbose, per launch
            '
            ' Info is right for every day. Verbose adds every spoken utterance
            ' ("ScreenReaderOutput: Spoke '...'") and other high-frequency
            ' detail, which is what you need to answer "what exactly did it
            ' say" — and which no in-app control could give you on its own,
            ' because reaching any control means startup and connect have
            ' already happened. Noel hit exactly that wall on 2026-08-17
            ' chasing a stray SmartLink announcement that fires during connect.
            ' The environment variable stays for precisely that case.
            '
            ' Unrecognised values fall back rather than failing: a typo in an
            ' environment variable must not cost someone their diagnostic log.
            Dim bootLevel As TraceLevel = DiagnosticsSettings.TraceLevel
            Dim lvlRaw As String = Environment.GetEnvironmentVariable("JJFLEX_BOOT_TRACE_LEVEL")
            If Not String.IsNullOrWhiteSpace(lvlRaw) Then
                Dim parsed As TraceLevel
                If [Enum].TryParse(Of TraceLevel)(lvlRaw.Trim(), ignoreCase:=True, result:=parsed) Then
                    bootLevel = parsed
                End If
            End If
            Tracing.TheSwitch.Level = bootLevel
            Tracing.TraceFile = BootTraceFileName
            Tracing.On = True
            BeginNewTraceSession()
            Tracing.TraceLine("Boot Tracing on instance:" & ProgramInstance & " " & myAssembly.Location & " " & myVersion.ToString() & " " & Date.Now & " level=" & bootLevel.ToString)
            ' Where this run's settings actually came from. Decided before
            ' tracing existed, so it is reported here or not at all — and a run
            ' using settings that are not the operator's must never be silent
            ' about it, in either direction.
            If UsingTemporaryConfigDir Then
                Tracing.TraceLine(
                    "ConfigLocation: TEMPORARY settings tree in use at " & BaseConfigDir &
                    " (" & Radios.RadioConfig.ConfigDirOverrideVariable & " is set). " &
                    "The operator's normal settings are NOT being read or written by this run.",
                    TraceLevel.Warning)
            End If
            If ConfigDirRefusal IsNot Nothing Then
                Tracing.TraceLine("ConfigLocation: " & ConfigDirRefusal, TraceLevel.Warning)
            End If
            ' The boot header above identifies the build; this states the log's
            ' state in the machine-readable form every later session also gets.
            ' Post-boot sessions (captures, resumes) have ONLY the CaptureState
            ' line — the boot header is written exactly once per launch, which
            ' is how capture files ended up anonymous until 2026-08-21.
            TraceCaptureStateMarker()
        End If

        Tracing.TraceLine("GetConfigInfo:" & BaseConfigDir, TraceLevel.Info)

        ' Which speech backend is driving the user's ears, and whether braille
        ' is reachable. ScreenReaderOutput picks
        ' this in ApplicationEvents, which runs BEFORE tracing exists, so
        ' without this line the fact appears in no trace file anyone could send
        ' us. It is the first thing to check on any "it stopped speaking"
        ' report, and on 2026-08-17 it was the one thing the trace could not
        ' answer.
        Radios.ScreenReaderOutput.TraceBackend()

        ' Per-radio serial-keyed store root. This MUST happen here, at true
        ' startup, not in any radio-window wiring: the store is read and
        ' written at CONNECT time (profile resolution in sendRemoteConnect,
        ' known-radio recording in Connect), which runs long before any
        ' window wires up. Found live 2026-08-06: the assignment sat in
        ' FreqOutHandlersWireCallback, so every connect-time load returned
        ' defaults and every save silently declined - the per-radio feature
        ' was inert exactly where it mattered.
        Radios.RadioConfig.BaseDirectory = BaseConfigDir

        ' Where radioConnectionCacheV1.xml lives - the same folder FlexBase gets
        ' as OpenParms.ConfigDirectory. Set here for the same reason as the line
        ' above: the radio selector reads the roster before any radio window
        ' exists, so it cannot wait for radio wiring to hand it a path.
        Radios.KnownRadioRoster.CacheDirectory = BaseConfigDir & "\Radios"

        ' Audio device selection file name.
        AudioDevicesFile = BaseConfigDir & "\" & audioDevicesBasename

        ' Load keyboard command config data.
        ' Set key config file path before constructing Commands.
        KeyConfigType_V1.PathName = BaseConfigDir & "\" & "KeyDefs.xml"

        ' Create the context for C# KeyCommands.
        Dim keyContext = New JJFlexWpf.KeyCommandContext With {
            .GetRigControl = Function() RigControl,
            .GetPower = Function() Power,
            .GetActiveUIMode = Function() CInt(ActiveUIMode),
            .GetMainWindow = Function() WpfMainWindow,
            .Trace = Sub(msg) Tracing.TraceLine(msg, TraceLevel.Info),
            .GetScanRunning = Function() ScanInProcess,
            .StopScan = Sub() If scan IsNot Nothing Then scan.StopScan(),
            .BeginScan = Sub()
                If (RigControl IsNot Nothing) AndAlso Power AndAlso
                   (Not RigControl.Transmit) AndAlso (scanstate <> scans.memory) Then
                    If scanstate = scans.linear Then
                        If scan IsNot Nothing Then scan.StopScan()
                    Else
                        scan.ShowDialog()
                    End If
                End If
            End Sub,
            .ResumeScan = Sub()
                If (scanstate <> scans.none) AndAlso (Not ScanInProcess) AndAlso
                   (RigControl IsNot Nothing) AndAlso (Not RigControl.Transmit) Then
                    scan.resumeScan()
                End If
            End Sub,
            .UseSavedScan = Sub(name)
                If (RigControl IsNot Nothing) AndAlso Power Then
                    If SelectScan.ShowDialog() = DialogResult.OK Then
                        Dim sd = SavedScans.Item(SelectScan.ItemIndex)
                        Select Case sd.Type
                            Case SavedScanData.ScanTypes.linear
                                scan.doPreset(sd, True)
                        End Select
                    End If
                End If
            End Sub,
            .MemoryScan = Sub()
                Try
                    If (RigControl IsNot Nothing) AndAlso (Not RigControl.Transmit) AndAlso (scanstate <> scans.linear) Then
                        If (scanstate = scans.memory) Then
                            If scan IsNot Nothing Then scan.StopScan()
                        Else
                            If MemoryGroupControl Is Nothing Then MemoryGroupControl = New MemoryGroup
                            MemoryScan.ShowDialog()
                        End If
                    End If
                Catch ex As Exception
                    Tracing.TraceLine("memScan:" & ex.Message, TraceLevel.Error)
                End Try
            End Sub,
            .BringUpLogForm = Sub(adifTag)
                If ActiveUIMode = UIMode.Logging AndAlso WpfMainWindow.LoggingLogPanel IsNot Nothing Then
                    Dim fieldName As String = Nothing
                    Select Case adifTag
                        Case "CALL" : fieldName = "CALL"
                        Case "RST_SENT" : fieldName = "RSTSENT"
                        Case "RST_RCVD" : fieldName = "RSTRCVD"
                        Case "NAME" : fieldName = "NAME"
                        Case "QTH" : fieldName = "QTH"
                        Case "STATE" : fieldName = "STATE"
                        Case "GRIDSQUARE" : fieldName = "GRID"
                        Case "COMMENT" : fieldName = "COMMENTS"
                        Case "MODE" : fieldName = "MODE"
                        Case "RIG" : fieldName = "RIG"
                        Case "ANTENNA" : fieldName = "ANTENNA"
                    End Select
                    If fieldName IsNot Nothing Then
                        WpfMainWindow.LoggingLogPanel.FocusField(fieldName)
                    ElseIf adifTag = JJFlexWpf.KeyCommands.IADIF_LogNewEntry Then
                        WpfMainWindow.LoggingLogPanel.NewEntry()
                        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.log_entry.new_entry"), VerbosityLevel.Terse, True)
                    End If
                    Return
                End If
                LogEntry.FieldID = adifTag
                Dim saveVisible = WpfMainWindow.Visible
                WpfMainWindow.Visible = False
                LogEntry.ShowDialog()
                WpfMainWindow.Visible = saveVisible
            End Sub,
            .FinalizeLog = Sub()
                If ActiveUIMode = UIMode.Logging AndAlso WpfMainWindow.LoggingLogPanel IsNot Nothing Then
                    WpfMainWindow.LoggingLogPanel.WriteEntry()
                    Return
                End If
                LogEntry.Write()
            End Sub,
            .SetLogDateTime = Sub() LogEntry.SetLogDateTime(),
            .GetLogFileName = Sub()
                ' The dialog reads theOp on load, and the other two callers
                ' (LogClass.Open, PersonalInfo) set it first. This one
                ' historically did not — on a session where neither of those
                ' had run yet, theOp was Nothing, the dialog died on load, and
                ' the exception was swallowed upstream, so the command looked
                ' like it did nothing at all. Now that the Log Characteristics
                ' menu items route here too, set it the way the other callers
                ' do (stub audit, 2026-08-21).
                LogCharacteristics.theOp = CurrentOp
                If LogCharacteristics.ShowDialog() = DialogResult.OK Then ConfigContactLog()
            End Sub,
            .SearchLog = Sub()
                Dim thrd As New Threading.Thread(Sub() FindLogEntry.ShowDialog())
                thrd.SetApartmentState(Threading.ApartmentState.STA)
                thrd.Start()
            End Sub,
            .LogStats = Sub()
                Dim obj As New LogStats()
                obj.ShowLogStats()
            End Sub,
            .GetCWText = Function()
                If CWText Is Nothing OrElse CWText.Length = 0 Then Return Array.Empty(Of CWMessageItem)()
                Dim items(CWText.Length - 1) As CWMessageItem
                For i = 0 To CWText.Length - 1
                    items(i) = New CWMessageItem(CWText(i).key, CWText(i).message, CWText(i).Label)
                Next
                Return items
            End Function,
            .SendCW = Sub(msg)
                If (RigControl IsNot Nothing) AndAlso Power Then RigControl.SendCW(msg)
            End Sub,
            .WriteTextX = Sub(windowId, text, disposition, clear) WriteTextX(CType(windowId, WindowIDs), text, disposition, clear),
            .DisplayFreq = Sub() WpfMainWindow.FreqOut.FocusFrequencyField(),
            .WriteFreq = Sub()
                If FreqInput.ShowDialog() = DialogResult.OK Then
                    Dim input = FreqInput.Buffer.Trim()
                    Tracing.TraceLine($"WriteFreq: FreqInput.Buffer='{input}'", TraceLevel.Info)
                    If input.Equals("cqtest", StringComparison.OrdinalIgnoreCase) Then
                        WpfMainWindow.ShowEarconScratchpad()
                        Return
                    End If
                    ' Check for calibration reference
                    Tracing.TraceLine($"WriteFreq: checking calibration for '{input}'", TraceLevel.Info)
                    Dim calibRef = JJFlexWpf.CalibrationEngine.VerifyCalibration(input)
                    Tracing.TraceLine($"WriteFreq: calibRef={If(calibRef, "null")}", TraceLevel.Info)
                    If calibRef IsNot Nothing Then
                        WpfMainWindow.HandleCalibrationFromFreqInput(calibRef)
                        Return
                    End If
                    If RigControl IsNot Nothing Then
                        Dim newHz As Long = WriteFreq(input)
                        ' Check band boundary against the value we just wrote.
                        ' RigControl.VirtualRXFrequency lags behind a FlexLib
                        ' round-trip so reading it back here saw the OLD freq
                        ' and the boundary speech was deferred to the next
                        ' tune step (BUG-054).
                        Dim handlers = WpfMainWindow.FreqHandlers
                        If handlers IsNot Nothing Then
                            handlers.CheckBandBoundary(CULng(newHz))
                        End If
                    Else
                        ' SetFreq opts in to RunsWithoutRadio so the dialog
                        ' opens with no radio (easter egg + calibration paths).
                        ' If the user actually typed a frequency to tune, the
                        ' apply-time speech tells them why nothing happened —
                        ' silent failure violates the no-silent-keystrokes rule.
                        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.tune.no_radio"), Radios.VerbosityLevel.Critical, True)
                    End If
                End If
            End Sub,
            .GotoReceive = Sub() WpfMainWindow.ReceivedTextBox.Focus(),
            .GotoSend = Sub()
                DirectCW = False
                WpfMainWindow.SentTextBox.Focus()
            End Sub,
            .GotoSendDirect = Sub()
                DirectCW = True
                WpfMainWindow.SentTextBox.Focus()
            End Sub,
            .StartPanning = Sub()
                If CurrentOp.BrailleDisplaySize = 0 Then
                    MsgBox(RequiresBrailleDisplay)
                    Return
                End If
                If (RigControl IsNot Nothing) AndAlso RigControl.myCaps.HasCap(Radios.RigCaps.Caps.Pan) Then
                    If OpenParms IsNot Nothing AndAlso OpenParms.PanField IsNot Nothing Then
                        OpenParms.PanField.Focus()
                    End If
                End If
            End Sub,
            .CycleContinuous = Sub() MsgBox(Radios.Lexicon.Get("connect.session.feature_no_longer_supported")),
            .DisplayMemory = Sub()
                If RigControl Is Nothing Then
                    ' ShowMemory opts in to RunsWithoutRadio. Memories live
                    ' radio-side, so we can't open the dialog with meaningful
                    ' data when disconnected — but going silent would violate
                    ' the no-silent-keystrokes rule. Speak the action-aware
                    ' no-radio message in place of the dispatcher's generic
                    ' announcement.
                    Radios.ScreenReaderOutput.SpeakNoRadioConnected("show memories")
                    Return
                End If
                Try
                    RigControl.ShowMemoriesDialog?.Invoke()
                Catch ex As Exception
                    Tracing.TraceLine("memory display:" & ex.Message, TraceLevel.Error)
                End Try
            End Sub,
            .ShowMenus = Sub() MsgBox(Radios.Lexicon.Get("connect.session.menus_not_available")),
            .ShowReverseBeacon = Sub() ReverseBeacon.ShowDialog(),
            .ShowDXCluster = Sub() ShowArCluster(),
            .StationLookup = Sub()
                If LookupStation IsNot Nothing Then LookupStation.Finished()
                LookupStation = CreateStationLookupWindow()
                LookupStation.ShowDialog()
                WpfMainWindow.HandleLogContactResult()
            End Sub,
            .GatherDebug = Sub() DebugInfo.GetDebugInfo(),
            .ShowATUMemories = Sub()
                Try
                    If RigControl IsNot Nothing AndAlso RigControl.myCaps.HasCap(RigCaps.Caps.ATMems) Then
                        RigControl.AntennaTunerMemories()
                    Else
                        MsgBox(Radios.Lexicon.Get("connect.session.not_supported_for_radio"))
                    End If
                Catch
                End Try
            End Sub,
            .RebootRadio = Sub()
                ' Shared with the Settings → Radio Setup → Restart button. The
                ' helper owns the no-radio announcement, the confirmation dialog
                ' that names the other stations about to be dropped, and the
                ' deliberate absence of a presence gate — see RadioMaintenance for
                ' why reboot is ungated.
                JJFlexWpf.RadioMaintenance.RebootWithConfirmation(
                    RigControl, AddressOf WpfMainWindow.powerNowOff)
            End Sub,
            .ShowTXControls = Sub()
                ' Sprint 33 Track J (#109). Both exits from here used to be
                ' silent: no radio returned without a word, and the
                ' ?.Invoke() below was a guaranteed no-op because nothing
                ' ever assigned ShowTXControlsDialog. The command is
                ' reachable from Command Finder, so an operator could run it
                ' and get nothing at all — no window, no speech, no way to
                ' tell a bug from a feature that was never built. That
                ' violates the no-silent-keystrokes rule twice over.
                If RigControl Is Nothing Then
                    Radios.ScreenReaderOutput.SpeakNoRadioConnected("show transmit controls")
                    Return
                End If
                If RigControl.ShowTXControlsDialog Is Nothing Then
                    ' Assigned in MainWindow.OnRadioStarted. Reaching here
                    ' means the command ran before post-start wiring.
                    Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.tx.controls_not_ready"),
                                                    Radios.VerbosityLevel.Critical, True)
                    Return
                End If
                Try
                    RigControl.ShowTXControlsDialog.Invoke()
                Catch ex As Exception
                    Tracing.TraceLine("TX controls display:" & ex.Message, TraceLevel.Error)
                End Try
                WpfMainWindow.FreqOut.FocusDisplay()
            End Sub,
            .AudioSetup = Sub() GetNewAudioDevices(),
            .ShowLogCharacteristics = Sub() WpfMainWindow.LogCharacteristicsForHotkey(),
            .LogOpenFullForm = Sub() WpfMainWindow.OpenFullLogEntryForHotkey(),
            .PCAudioToggle = Sub()
                If RigControl IsNot Nothing Then
                    Dim wanted As Boolean = Not RigControl.PCAudio
                    RigControl.PCAudio = wanted
                    ' Threads Track (2026-08-12): remember the operator's
                    ' choice per radio (intent, not outcome) so the
                    ' remember-last on-connect mode can restore it.
                    Radios.RadioConfig.RecordPcAudioUserChoice(RigControl.SelectedRadioSerial, wanted)
                    ' Sprint 32 Track E, #128. PC audio is reachable three ways
                    ' -- this hotkey, the Audio menu, and the Settings checkbox
                    ' -- and every one of them was silent, which is what
                    ' surfaced the whole toggle sweep. Read the radio back
                    ' rather than trusting the request: turning PC audio on
                    ' fails when there is no usable sound device, and a rising
                    ' tone over a toggle that did not happen is a confident lie.
                    ' The other two roads already read back for their speech;
                    ' this one did not read back at all.
                    JJFlexWpf.EarconPlayer.ToggleTone(RigControl.PCAudio)
                End If
            End Sub,
            .AudioMenuString = Function()
                If (RigControl IsNot Nothing) AndAlso RigControl.PCAudio Then
                    Return "Turn off PC audio"
                Else
                    Return "Turn on PC audio"
                End If
            End Function,
            .SMeterMenuString = Function()
                If (RigControl IsNot Nothing) AndAlso RigControl.SmeterInDBM Then
                    Return "SMeter in S-units"
                Else
                    Return "SMeter in dBm"
                End If
            End Function,
            .LogPaneSwitch = Sub() WpfMainWindow.ToggleLoggingPaneFocusForHotkey(),
            .GetConfigDirectory = Function() BaseConfigDir,
            .FormatKey = Function(k) KeyString(k),
            .Toggle1 = Sub()
                If (OpenParms IsNot Nothing) AndAlso (OpenParms.NextValue1 IsNot Nothing) AndAlso Power Then
                    OpenParms.NextValue1()
                End If
            End Sub,
            .ClusterShutdown = Sub()
                If (ClusterScreens Is Nothing) OrElse (ClusterScreens.Count = 0) Then Return
                Tracing.TraceLine("ClusterShutdown", TraceLevel.Info)
                For i = 0 To ClusterScreens.Count - 1
                    Dim cluster = ClusterScreens(i)
                    Try
                        If cluster IsNot Nothing Then
                            cluster.LoginCancel()
                            cluster.Close()
                            cluster.Dispose()
                        End If
                    Catch ex As Exception
                        Tracing.TraceLine("ClusterShutdown:" & ex.Message, TraceLevel.Error)
                    End Try
                Next
                ClusterScreens.Clear()
            End Sub,
            .DisplayDecodedText = Sub(text)
                Dim disposition As Integer = 0
                If CurrentOp.ConstrainedDecode Then disposition = -CurrentOp.CWDecodeCells
                WriteTextX(WindowIDs.ReceiveDataOut, text, disposition, False)
            End Sub
        }
        Commands = New JJFlexWpf.KeyCommands(keyContext)

        ' Wire SaveDefaultSmartLinkAccount early so menu → Manage SmartLink works
        WpfMainWindow.SaveDefaultSmartLinkAccount = Sub(email)
                                                         Dim opName = PersonalData.UniqueOpName(CurrentOp)
                                                         Dim cfg = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                                                         cfg.SmartLinkAccountEmail = email
                                                         cfg.Save(BaseConfigDir, opName)
                                                         Tracing.TraceLine($"SaveDefaultSmartLinkAccount: saved {email}", TraceLevel.Info)
                                                     End Sub
        WpfMainWindow.GetDefaultSmartLinkEmail = Function() As String
                                                     Dim opName = PersonalData.UniqueOpName(CurrentOp)
                                                     Dim cfg = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                                                     Return If(cfg.SmartLinkAccountEmail, "")
                                                 End Function
        WpfMainWindow.SetSessionSmartLinkAccount = Sub(email)
                                                       SessionSmartLinkEmail = If(email, "")
                                                       Tracing.TraceLine($"SetSessionSmartLinkAccount: session override = '{SessionSmartLinkEmail}'", TraceLevel.Info)
                                                   End Sub

        ' One resolver for "which SmartLink account is in play". FlexBase's
        ' account loading (registration preflight and the background
        ' registration query on local connects) asks this hook instead of
        ' guessing most-recently-used — the 2026-08-10 bug where every launch
        ' silently opened a SmartLink session on another operator's account.
        ' The hook is Shared on FlexBase, so this single wiring covers every
        ' instance: wpfSelectorProc's and auto-connect's alike (GetConfigInfo
        ' runs in InitializeApplication, before either path can construct one).
        Radios.FlexBase.ResolveCurrentAccountHook = Function() ResolveSmartLinkAccount()

        ' Load operator and rig data.
        Operators = New PersonalData(BaseConfigDir)
        ' There must be a default operator!
        While CurrentOp Is Nothing
            SetCurrentOp(Operators.TheDefault, Operators.DefaultID)
            If CurrentOp Is Nothing Then
                If MessageBox.Show(AppShellForm, reqOpMsg, reqOpMsgTitle, MessageBoxButtons.YesNo) <> DialogResult.Yes Then
                    End
                End If
                Lister.TheList = Operators
                Lister.ShowDialog()
            End If
        End While

        ' setup log file
        ConfigContactLog()

        ' Now that the operator is known, rebuild the operations menu (for trace toggle).
        ' Sprint 10: Route to WPF MainWindow instead of Form1.
        If WpfMainWindow IsNot Nothing Then WpfMainWindow.SetupOperationsMenu()

        ' Check for W2 watt meter.
        W2ConfigFile = BaseConfigDir & "\" & W2ConfigFileBasename
        ConfigW2(True) ' only setup if already configured.
    End Sub

    Friend Sub SetCurrentOp(ByVal op As PersonalData.personal_v1,
                            ByVal id As Integer)
        If op IsNot Nothing Then
            Tracing.TraceLine("SetCurrentOp(" & op.fullName & "," & id.ToString & ")", TraceLevel.Info)
        Else
            Tracing.TraceLine("SetCurrentOp:no operator", TraceLevel.Error)
            id = -1
        End If
        CurrentOpID = id
        If id = -1 Then
            Return
        End If

        ' Initialize the optional message processor
        OptionalMessage.Setup(AddressOf Operators.UpdateOptionalMessages, AddressOf Operators.RetrieveOptionalMessages)
        ' Update the key dictionaries.
        Commands.UpdateCWText()
        ' Setup macros
        MacroItems.Items(MacroItems.MacroIDS.myCallSign).Acquire =
            Function()
                Return op.callSign
            End Function
        MacroItems.Items(MacroItems.MacroIDS.myName).Acquire =
            Function()
                Return op.handl
            End Function
        MacroItems.Items(MacroItems.MacroIDS.myQTH).Acquire =
            Function()
                Return op.qth
            End Function

        ' Get the default profile for legacy users.
        If CurrentOp.Profiles Is Nothing Then
            CurrentOp.Profiles = New List(Of Profile_t)
        End If
        If (CurrentOp.Profiles.Count = 0) Then
            ' no profile defined with the user's data.
            ' Old default was a global and tx profile.
            CurrentOp.Profiles.Add(New Profile_t(Profile_t.GenerateProfileName(CurrentOp.callSign), ProfileTypes.global, True))
            CurrentOp.Profiles.Add(New Profile_t("Default", ProfileTypes.tx, True))
        End If
    End Sub

    Friend Sub ConfigContactLog()
#If 0 Then
        Logs.NewLog(CurrentOp.HamqthID, CurrentOp.HamqthPassword)
#End If
        Logs.NewLog(HamqthLookupID, HamqthLookupPassword)
        ContactLog = New LogClass(CurrentOp.LogFile)
        logSetup()
        ' Setup macros
        MacroItems.Items(MacroItems.MacroIDS.callSign).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_Call)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.name).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_Name)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.QTH).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_QTH)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.myRST).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_MyRST)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.RST).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_HisRST)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.mySerial).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_SentSerial)
            End Function
    End Sub

    ''' <summary>
    ''' start dup checking
    ''' </summary>
    Private Sub logSetup()
        ' Can't do this if there's no log.
        ' Sprint 10: Route to WPF StatusBar via StatusBox adapter.
        StatusBox?.Write("LogFile", " ")
        If (ContactLog.Name = vbNullString) Or (Not File.Exists(ContactLog.Name)) Then
            Return
        End If
        Dim session = New LogSession(ContactLog)
        If Not session.Start() Then
            Tracing.TraceLine("startDupChecking couldn't start session", TraceLevel.Error)
            Return
        End If
        StatusBox?.Write("LogFile", LogCharacteristics.TrimmedFilename(ContactLog.Name, 20))
        ' Set the keys from the log form.
        Dim defs = New Collection(Of KeyDefType)
        For Each fld As LogField In session.FormData.Fields.Values
            If fld.KeyName <> vbNullString Then
                ' First use the name to get the id.
                fld.KeyID = JJFlexWpf.KeyCommands.GetKeyFromTypename(fld.KeyName)
                ' Get the entry to set in my keyTable.
                Dim t As KeyTableEntry = Commands.Lookup(CType(fld.KeyID, CommandValues))
                If (t IsNot Nothing) Then
                    defs.Add(t.KeyDef)
                End If
            End If
        Next
        ' Add any keys for use when logging.
        For Each ktbl As KeyTableEntry In Commands.KeyTable
            If ktbl.UseWhenLogging Then
                defs.Add(ktbl.KeyDef)
            End If
        Next
        Commands.SetValues(defs.ToArray, KeyTypes.Log, False)

        ' Setup dup checking and other log calculations and fixup.
        Dim dupCheck As LogDupChecking.DupTypes
        dupCheck = CType(CInt(session.GetHeaderFieldText(AdifTags.HDR_DupCheck)), LogDupChecking.DupTypes)
        Dups = Nothing
        Tracing.TraceLine("startDupChecking:" & dupCheck.ToString, TraceLevel.Info)
        If dupCheck <> LogDupChecking.DupTypes.none Then
            Dups = New LogDupChecking(dupCheck)
        End If

        Dim countriesdb As CountriesDB = Nothing
        ' For each log record...
        While (Not session.EOF) AndAlso session.NextRecord()
            Dim needUpdate As Boolean = False ' set if need to update the record.
            If session.NeedFrequencyFix Then
                ' Fix bogus frequencies.
                Dim item As LogFieldElement
                item = session.getField(AdifTags.ADIF_RXFreq, False, session.FieldDictionary)
                If (item IsNot Nothing) AndAlso (item.Data <> vbNullString) Then
                    item.Data = fixFreq(item.Data)
                End If
                item = session.getField(AdifTags.ADIF_TXFreq, False, session.FieldDictionary)
                If (item IsNot Nothing) AndAlso (item.Data <> vbNullString) Then
                    item.Data = fixFreq(item.Data)
                End If
                needUpdate = True
            End If

            ' maintain dup checking
            If dupCheck <> LogDupChecking.DupTypes.none Then
                Dim key As New LogDupChecking.keyElement(session, DupType)
                Dups.AddToDictionary(key)
            End If

            ' See if need to update the DXCC info.
            If session.FormData.NeedCountryInfo Then
                Dim callItem As LogFieldElement = session.getField(AdifTags.ADIF_Call, False, session.FieldDictionary)
                If (callItem IsNot Nothing) AndAlso (callItem.Data <> vbNullString) Then
                    Dim dxccItem As LogFieldElement = session.getField(AdifTags.ADIF_DXCC, False, session.FieldDictionary)
                    If (dxccItem IsNot Nothing) AndAlso (dxccItem.Data = vbNullString) Then
                        ' no DXCC info.
                        If countriesdb Is Nothing Then
                            countriesdb = New CountriesDB
                        End If
                        Dim rec As Record = countriesdb.LookupByCall(callItem.Data)
                        If rec IsNot Nothing Then
                            dxccItem.Data = rec.CountryID
                            needUpdate = True
                        End If
                    End If
                End If
            End If

            ' Perform any housekeeping such as score calculation.
            If session.FormData.WriteEntry IsNot Nothing Then
                session.FormData.WriteEntry(session.FieldDictionary, Nothing)
            End If

            If needUpdate Then
                session.Update()
            End If
        End While

        session.EndSession()
    End Sub

    ''' <summary>
    ''' Get the station name.
    ''' </summary>
    Friend Function getStationName() As String
        Dim rv As String = CurrentOp.callSign
        If rv = vbNullString Then
            rv = Dns.GetHostName()
        End If
        If rv = vbNullString Then
            rv = "unknown"
        End If
        Return rv
    End Function

    ' FlexControl knob stuff
    Private knobThread As Thread = Nothing
    Friend Sub SetupKnob()
        knobThread = New Thread(AddressOf knobThreadProc)
        knobThread.Name = "knob thread"
        knobThread.Start()
    End Sub

    Friend Sub StopKnob()
        If knobThread IsNot Nothing Then
            knobThread.Interrupt()
            Try
                If knobThread.IsAlive Then
                    knobThread.Join()
                End If
            Catch ex As Exception
                ' ignore
            End Try
        End If
    End Sub

    Private Sub knobThreadProc()
        Try
            ' setup the knob and let it run
            Knob = New FlexKnob
            Thread.Sleep(Timeout.Infinite)
        Catch ex As ThreadInterruptedException
            ' done with the knob
            If Knob IsNot Nothing Then
                Knob.Dispose()
                Knob = Nothing
            End If
        End Try
    End Sub

    ' Remove consequtive periods
    Private Function fixFreq(inFreq As String) As String
        Dim rv As String = vbNullString
        Dim wasPeriod As Boolean = False
        For i As Integer = 0 To inFreq.Length - 1
            If inFreq(i) = "." Then
                If wasPeriod Then
                    Continue For
                Else
                    wasPeriod = True
                End If
            Else
                wasPeriod = False
            End If
            rv &= inFreq(i)
        Next
        Return rv
    End Function

    Friend Sub ConfigW2(suppressDialog As Boolean)
        Tracing.TraceLine("ConfigW2:" & suppressDialog, TraceLevel.Info)
        If W2WattMeter IsNot Nothing Then
            W2WattMeter.Dispose()
        End If
        W2WattMeter = New W2(W2ConfigFile)
        If suppressDialog Then
            ' Called from GetConfigInfo().
            ' Only setup if already configured.
            If W2WattMeter.IsConfigured Then
                W2WattMeter.Setup() ' no config dialogue
            End If
        Else
            ' User wants to configure.
            W2WattMeter.Setup(True)
        End If
    End Sub
    ''' <summary>
    ''' Configure W2 wattmeter.
    ''' </summary>
    Friend Sub ConfigW2()
        ConfigW2(False)
    End Sub

    ''' <summary>
    ''' Validatte a path or file name
    ''' </summary>
    ''' <param name="name"></param>
    ''' <returns>true if good</returns>
    Friend Function IsValidFileNameOrPath(ByVal name As String) As Boolean
        ' Determines if the name is empty or all white space.
        If (name = vbNullString) OrElse (name.Trim = vbNullString) Then
            Return False
        End If

        ' Determines if there are bad characters in the name. 
        For Each badChar As Char In System.IO.Path.GetInvalidPathChars
            If InStr(name, badChar) > 0 Then
                Return False
            End If
        Next

        ' The name passes basic validation. 
        Return True
    End Function
#End Region

    ' region - Scan stuff
#Region "scan"
    Friend SavedScans As SavedScanData
    Friend Enum scans
        none
        linear
        memory
    End Enum
    Friend scanstate As scans = scans.none
    Friend ReadOnly Property ScanInProcess As Boolean
        Get
            Return If(WpfMainWindow IsNot Nothing, WpfMainWindow.ScanTimerEnabled, False)
        End Get
    End Property
    Friend MemoryGroupControl As MemoryGroup
    Friend speechStatus, autoModeStatus As Boolean
    Friend modeStatus As String
    ''' <summary>
    ''' Sprint 10: Scan timer routed to WPF MainWindow.ScanTimer (DispatcherTimer).
    ''' Replaces old Form1.ScanTmr (WinForms Timer) dependency.
    ''' </summary>
    Friend ReadOnly Property scanTimer As System.Windows.Threading.DispatcherTimer
        Get
            Return If(WpfMainWindow IsNot Nothing, WpfMainWindow.ScanTimer, Nothing)
        End Get
    End Property
    ''' <summary>
    ''' Sprint 10: Status writer routed to WPF MainWindow.WriteStatus().
    ''' Replaces old Form1.StatusBox (RadioBoxes.MainBox) dependency.
    ''' </summary>
    Private _statusBoxAdapter As StatusBoxAdapter
    Friend ReadOnly Property StatusBox As StatusBoxAdapter
        Get
            If _statusBoxAdapter Is Nothing AndAlso WpfMainWindow IsNot Nothing Then
                _statusBoxAdapter = New StatusBoxAdapter(WpfMainWindow)
            End If
            Return _statusBoxAdapter
        End Get
    End Property
#End Region

    Friend Const MHZSIZE As Integer = 5
    Friend Const KHZSIZE As Integer = 6
    Friend Const FREQSIZE As Integer = MHZSIZE + KHZSIZE
    Friend Const SMETERSIZE As Integer = 4
    Friend Const RITOFFSETSIZE As Integer = 4 ' 4 digits

    Private Function iFormatFreq(ByVal str As String) As String
        ' Format the frequency for display.
        If str.Length <> FREQSIZE OrElse Not IsNumeric(str) Then
            Return Nothing
        End If
        Dim mhzi As Integer = CInt(str.Substring(0, MHZSIZE))
        ' note that CStr(mhzi) removes leading zeros.
        Dim khz As String = str.Substring(MHZSIZE, KHZSIZE)
        str = khz.Insert(3, ".")
        Return CStr(mhzi) & "." & str
    End Function
    ''' <summary>
    ''' (Overloaded) format the frequency for display
    ''' </summary>
    ''' <param name="IFText">Text from the IF command</param>
    ''' <returns>displayable frequency</returns>
    Friend Function FormatFreq(ByVal IFText As String) As String
        ' Format from "IF" data, or just a frequency.
        Dim freq, rit As String
        Dim vfo As String = ""
        Dim split As String = ""
        Dim i As Integer = 0
        Try
            freq = iFormatFreq(IFText.Substring(i, FREQSIZE))
        Catch ex As Exception
            Tracing.TraceLine("FormatFreq bogus string:" & IFText & " " & ex.Message, TraceLevel.Error)
            Return ""
        End Try
        ' Get RIT offset
        i += 16
        If i >= IFText.Length Then
            Return freq
        End If
        Try
            rit = IFText(i)
            If Not ((rit = " ") OrElse (rit = "+") OrElse (rit = "-")) Then
                ' bogus IF packet
                Return freq
            End If
            If rit = " " Then
                rit = "+"
            End If
            i += 1
            rit &= IFText.Substring(i, RITOFFSETSIZE)
            If ((IFText.Substring(i + RITOFFSETSIZE, 1) = "1") Or _
                (IFText.Substring(i + RITOFFSETSIZE + 1, 1) = "1")) Then
                ' RIT/XIT enabled
                freq &= rit
                If (IFText.Substring(i + RITOFFSETSIZE + 1, 1) = "1") Then
                    ' Xit
                    freq &= "x"
                End If
            End If
            i += RITOFFSETSIZE + 2 + 1
            ' Get the VFO.
            If IFText(i + 4) = "0" Then
                vfo = "A"
            Else
                vfo = "B"
            End If
            If (IFText.Substring(i + 6, 1) = "1") Then
                ' split
                split = "S"
            End If
        Catch ex As Exception
            ' Can happen if just the frequency is passed or the data is bogus.
            ' Return the frequency so far.
            Tracing.TraceLine("FormatFreq exception:" & IFText & " " & ex.Message, TraceLevel.Error)
        End Try
        ' Note that split and vfo are empty if not applicable.
        Return split & vfo & freq
    End Function
    ''' <summary>
    ''' (Overloaded) format the frequency for display
    ''' </summary>
    ''' <param name="freq">64-bit frequency</param>
    ''' <returns>displayable frequency</returns>
    Friend Function FormatFreq(ByVal freq As ULong) As String
        Return FormatFreqUlong(freq)
    End Function

    Private Const threeZeros As String = "000"
    Friend Function FormatFreqUlong(ByVal freq As ULong) As String
        Dim rv As String = ""
        Dim str As String = freq.ToString
        ' Make string at least 7 characters long.
        For i As Integer = 0 To 6 - str.Length
            str = "0" & str
        Next
        Dim len = str.Length
        rv = str.Substring(0, len - 6) & "."c & str.Substring(len - 6, 3) &
                "."c & str.Substring(len - 3)
        Return rv
    End Function

    ''' <summary>
    ''' get numeric frequency string
    ''' </summary>
    ''' <param name="str">string containing frequency as mm.kkk.hhh </param>
    ''' <returns>int64 value</returns>
    Friend Function FreqInt64(ByVal str As String) As ULong
        Dim str2 As String = "0"
        For Each c As Char In str
            If IsNumeric(c) Then
                str2 &= c
            End If
        Next
        Return CLng(str2)
    End Function
    ''' <summary>
    ''' get numeric frequency string
    ''' </summary>
    ''' <param name="str">frequency string</param>
    ''' <returns>numeric frequency as a double </returns>
    Friend Function FreqDouble(ByVal str As String) As Double
        Dim str2 As String = ""
        Dim decSW As Boolean = False
        For Each c As Char In str
            If IsNumeric(c) Then
                str2 &= c
            ElseIf c = "."c Then
                If Not decSW Then
                    decSW = True
                    str2 &= c
                End If
            End If
        Next
        Return CDbl(str2)
    End Function

    Friend Function FormatSMeter(ByVal str As String) As String
        Return str
    End Function

    Friend ReadOnly Property DupEntryMsg As String
        Get
            Return Radios.Lexicon.Get("logging.log_entry.duplicate_suffix")
        End Get
    End Property
    Friend ReadOnly Property BadFreqMSG As String
        Get
            Return Radios.Lexicon.Get("logging.log_entry.bad_freq_suffix")
        End Get
    End Property
    Friend Function FormatFreqForRadio(ByVal str As String) As String
        ' Return 11-digit freq or nothing.
        Dim s() As String
        Dim st As String = ""
        Dim i As Integer = 0
        Dim err As Boolean = (str Is Nothing)
        If Not err Then
            Dim sep() As Char = {"."c}

            s = str.Split(sep, 3, StringSplitOptions.None)
            For Each st In s
                If st = "" OrElse Not IsNumeric(st) Then
                    err = True
                End If
                i += 1
            Next
            If (i = 3) AndAlso (s(2).IndexOf("."c) > -1) Then
                err = True
            End If
            If Not err Then
                ' They're all numeric.
                Select Case i
                    Case 1
                        ' just khz
                        st = s(0) & "000" ' hz = 0
                        If st.Length > FREQSIZE Then
                            err = True
                        End If
                    Case 2
                        ' khz, s(1), must be 1 to KHZSIZE (6) digits.  Pad to 3 if less.
                        st = s(1)
                        For i = st.Length + 1 To KHZSIZE
                            st &= "0"
                        Next
                        If st.Length > KHZSIZE OrElse s(0).Length > MHZSIZE Then
                            err = True
                        Else
                            st = st.Insert(0, s(0))
                        End If
                    Case 3
                        If s(0).Length > MHZSIZE OrElse s(1).Length <> 3 OrElse s(2).Length > 3 Then
                            err = True
                        Else
                            st = s(1) & s(2)
                            ' May need to expand this to KHZSIZE digits.
                            For i = st.Length + 1 To KHZSIZE
                                st &= "0"
                            Next
                            st = st.Insert(0, s(0))
                        End If
                End Select
                If Not err Then
                    ' pad with leading zeros.
                    For i = 1 To FREQSIZE - st.Length
                        st = st.Insert(0, "0")
                    Next
                End If
            End If
        End If
        If err Then
            Tracing.TraceLine($"FormatFreqForRadio: input='{str}' → error", TraceLevel.Info)
            Return Nothing
        Else
            Tracing.TraceLine($"FormatFreqForRadio: input='{str}' → '{st}'", TraceLevel.Info)
            Return st
        End If
    End Function
    ''' <summary>
    ''' convert a numeric frequency to a string for the radio
    ''' </summary>
    ''' <param name="intFreq">long integer frequency</param>
    ''' <returns>the number</returns>
    Friend Function FormatUlongFreqForRadio(ByVal intFreq As ULong) As String
        Dim str As String = CStr(intFreq)
        ' Needs to be 11 digits, pad on left with 0's.
        Dim pad As String = ""
        For i As Integer = str.Length To 11 - 1
            pad &= "0"
        Next
        Return pad & str
    End Function
    Friend Function UlongFreq(ByVal str As String) As ULong
        If str Is Nothing Then
            Return 0
        End If
        Dim rv As ULong
        Try
            rv = CULng(FormatFreqForRadio(str))
        Catch ex As Exception
            Tracing.TraceLine("ulongFreq error:" & str, TraceLevel.Error)
            rv = 0
        End Try
        Return rv
    End Function

    Friend Delegate Function awaitFuncDel() As Boolean
    Friend Function Await(func As awaitFuncDel, ms As Integer)
        Return Await(func, ms, 25)
    End Function
    Friend Function Await(func As awaitFuncDel, ms As Integer, waitMS As Integer)
        Dim iterations As Integer = ms / waitMS
        Dim rv As Boolean = func()
        While (Not rv) And (iterations > 0)
            Thread.Sleep(waitMS)
            iterations -= 1
            rv = func()
        End While
        Return rv
    End Function

    ''' <summary>
    ''' (overloaded) Return true if hostname is valid.
    ''' It may be host colon port.
    ''' </summary>
    ''' <param name="host">the entire hostname</param>
    ''' <param name="name">returned hostname</param>
    ''' <param name="port">returned integer port (default is 23)</param>
    ''' <returns>true on success</returns>
    Friend Function IsValidHostname(host As String, ByRef name As String, ByRef port As Integer) As Boolean
        Dim rv As Boolean = (host <> vbNullString)
        name = host
        port = 23 ' default to telnet port#
        If Not rv Then
            Return rv
        End If

        Dim id As Integer = host.IndexOf(":") + 1
        If id > 0 Then
            If ((id < 2) Or (id >= host.Length)) OrElse _
               Not System.Int32.TryParse(host.Substring(id), port) Then
                rv = False
            Else
                name = host.Substring(0, id - 1)
            End If
        End If
        Return rv
    End Function
    Friend Function IsValidHostname(host As String) As Boolean
        Dim dummy1 As String = vbNullString
        Dim dummy2 As Integer = 0
        Return IsValidHostname(host, dummy1, dummy2)
    End Function

    ''' <summary>
    ''' If the value isn't empty, set the field to it.
    ''' Select all the text in the fld.
    ''' </summary>
    ''' <param name="fld">the screen field</param>
    ''' <param name="val">the string value</param>
    Friend Sub SelectFieldText(ByVal fld As TextBox, ByVal val As String)
        If val <> vbNullString Then
            fld.Text = val
        End If
        fld.SelectionStart = 0
        fld.SelectionLength = fld.Text.Length
    End Sub

    ''' <summary>
    ''' get the descriptive string for this key
    ''' </summary>
    ''' <param name="k">the key</param>
    ''' <returns>the string</returns>
    ''' <remarks></remarks>
    Friend Function KeyString(ByVal k As Keys) As String
        Dim str As String
        Dim n As String = k.ToString
        Dim id As Integer = n.IndexOf(", ")
        If id > -1 Then
            ' Reformat the string.
            str = n.Substring(id + 2) & "-"
            str &= n.Substring(0, id)
        Else
            str = n
        End If
        Return str
    End Function

#Region "split VFOs"
    ''' <summary>
    ''' Split VFOs
    ''' </summary>
    ''' <remarks>
    ''' If set to false, the TXVFO is set to the RXVFO.
    ''' If set to true:
    ''' If already split, no action.
    ''' If RXVFO is 1 (was VFOB), TXVFO = 0 (was VFOA).
    ''' Otherwise TXVFO = 1, (was VFOB).
    ''' Thus if RXVFO is A or B, it works like it used to, otherwise TXVFO is B.
    ''' </remarks>
    Friend Property SplitVFOs As Boolean
        Get
            Return RigControl.CanTransmit And RigControl.ValidVFO(RigControl.TXVFO) And
                (RigControl.RXVFO <> RigControl.TXVFO)
        End Get
        Set(value As Boolean)
            If (value = SplitVFOs) Or Not RigControl.CanTransmit Then
                Return
            End If
            If value Then
                If RigControl.RXVFO = 1 Then
                    ' RX is 1, use 0
                    RigControl.TXVFO = 0
                Else
                    ' use 1 regardless.
                    RigControl.TXVFO = 1
                End If
            Else
                RigControl.TXVFO = RigControl.RXVFO
            End If
        End Set
    End Property

    ''' <summary>
    ''' Show XMIT frequency
    ''' </summary>
    Friend Property ShowXMITFrequency As Boolean
        Get
            Return RigControl.ShowingXmitFrequency
        End Get
        Set(value As Boolean)
            ' Inform the rig
            RigControl.ShowingXmitFrequency = value
        End Set
    End Property

    ''' <summary>
    ''' Virtual Receive frequency using ShowXMITFrequency
    ''' </summary>
    Property RXFrequency As ULong
        Get
            Return RigControl.VirtualRXFrequency
        End Get
        Set(value As ULong)
            RigControl.VirtualRXFrequency = value
        End Set
    End Property

    Friend Sub changeSliceAudio(oldval As Integer, newval As Integer)
        ' unmute the new slice
        RigControl.SetVFOAudio(newval, True)

        ' mute the old slice if not being used.
        If (RigControl.RXVFO <> oldval) And (RigControl.TXVFO <> oldval) Then
            RigControl.SetVFOAudio(oldval, False)
        End If
    End Sub

    Friend Property MemoryMode As Boolean
#End Region

    ' Region remote audio
#Region "remote audio"
    Private Const audioDevicesBasename As String = "audioDevices.xml"
    Friend AudioDevicesFile As String
    ' InputAudioDevice / OutputAudioDevice removed 2026-08-07 (QB Track B).
    ' They were write-only: the old picker assigned them and nothing ever read
    ' them back — FlexBase always re-reads audioDevices.xml through its own
    ' Devices instance. Two module-level fields that looked like the current
    ' selection but were not.

    ''' <summary>
    ''' Open the Audio Devices picker.
    ''' </summary>
    ''' <returns>True when radio audio has usable input and output devices afterwards.</returns>
    ''' <remarks>
    ''' QB Track B, 2026-08-07. This used to show the legacy devList form twice
    ''' in a row — input, then output — with no announcement that a second
    ''' dialog was coming, and cancelling the first still marched you into the
    ''' second. One accessible dialog now covers both, plus the alert and meter
    ''' devices, so there is one place to answer "which sound device does what".
    ''' </remarks>
    Friend Function GetNewAudioDevices() As Boolean
        Try
            Dim cfg = JJFlexWpf.AudioOutputConfig.Load(BaseConfigDir)
            Return JJFlexWpf.Dialogs.AudioDevicesDialog.ShowPicker(
                Nothing, AudioDevicesFile, cfg, Sub() cfg.Save(BaseConfigDir))
        Catch ex As Exception
            Tracing.TraceLine("GetNewAudioDevices failed: " & ex.Message, TraceLevel.Error)
            ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.startup.picker_failed", ("message", ex.Message)),
                                     VerbosityLevel.Critical, True)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' PC audio
    ''' </summary>
    Friend Property PCAudio As Boolean
        Get
            Dim rv As Boolean = False
            If RigControl IsNot Nothing Then
                rv = RigControl.PCAudio
            End If
            Return rv
        End Get
        Set(value As Boolean)
            If RigControl Is Nothing Then
                Return
            End If
            If RigControl.PCAudio <> value Then
                If value AndAlso Not EnsureAudioDevicesConfigured(True) Then
                    Return
                End If
                RigControl.PCAudio = value
            End If
        End Set
    End Property

    ''' <summary>
    ''' Gate every road to PC-audio-on through the same check: are there sound
    ''' devices for it to use?
    ''' </summary>
    ''' <remarks>
    ''' QB Track B, 2026-08-07. Three things changed here. The question is now
    ''' asked in words that say what is wrong and what pressing Yes will do —
    ''' "Audio devices are not configured" told an operator nothing about which
    ''' devices or why it mattered. The yes-branch opens the one accessible
    ''' picker instead of chaining two naked WinForms modals. And every outcome
    ''' speaks, including the one where the user says no, because a keystroke
    ''' that turns PC audio on and then silently does not is worse than one that
    ''' fails out loud.
    ''' </remarks>
    Friend Function EnsureAudioDevicesConfigured(prompt As Boolean) As Boolean
        If String.IsNullOrEmpty(AudioDevicesFile) Then
            Return True
        End If

        Try
            Dim devices As New JJPortaudio.Devices(AudioDevicesFile)
            Dim status As JJPortaudio.Devices.EnumerationStatus
            Dim enumMessage As String = Nothing
            If Not devices.Setup(status, enumMessage) Then
                ' No devices at all, or PortAudio would not start. There is
                ' nothing the picker could offer, so say the real reason.
                Tracing.TraceLine("EnsureAudioDevicesConfigured: enumeration failed, " & status.ToString(),
                                  TraceLevel.Error)
                ScreenReaderOutput.Speak(
                    If(String.IsNullOrEmpty(enumMessage),
                       Radios.Lexicon.Get("audio.startup.enumeration_failed"),
                       Radios.Lexicon.Get("audio.startup.enumeration_failed_detail", ("enumMessage", enumMessage))),
                    VerbosityLevel.Critical, True)
                Return False
            End If

            Dim inputDev = devices.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.input)
            Dim outputDev = devices.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.output)

            If (inputDev IsNot Nothing) AndAlso (outputDev IsNot Nothing) Then
                Return True
            End If

            ' Name what is missing. "Your headset is unplugged" and "you have
            ' never chosen a device" are different problems with different fixes.
            Dim savedInputName As String = Nothing, savedOutputName As String = Nothing
            Dim inputMissing = devices.IsSavedDeviceMissing(JJPortaudio.Devices.DeviceTypes.input, savedInputName)
            Dim outputMissing = devices.IsSavedDeviceMissing(JJPortaudio.Devices.DeviceTypes.output, savedOutputName)

            Dim detail As String
            If inputMissing OrElse outputMissing Then
                Dim gone = If(outputMissing, savedOutputName, savedInputName)
                detail = Radios.Lexicon.Get("audio.startup.device_missing", ("gone", gone))
            Else
                detail = Radios.Lexicon.Get("audio.startup.no_device_chosen")
            End If

            If Not prompt Then
                ScreenReaderOutput.Speak(detail, VerbosityLevel.Critical, True)
                Return False
            End If

            Dim msg = Radios.Lexicon.Get("audio.startup.choose_prompt", ("detail", detail), ("newline", vbCrLf))
            If MessageBox.Show(AppShellForm, msg, MessageHdr, MessageBoxButtons.YesNo, MessageBoxIcon.Information) <> DialogResult.Yes Then
                ScreenReaderOutput.Speak(
                    Radios.Lexicon.Get("audio.startup.declined"),
                    VerbosityLevel.Critical, True)
                Return False
            End If

            Return GetNewAudioDevices()
        Catch ex As Exception
            Tracing.TraceLine("Audio device check failed: " & ex.Message, TraceLevel.Error)
            ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.startup.could_not_start", ("message", ex.Message)),
                                     VerbosityLevel.Critical, True)
            Return False
        End Try
    End Function
#End Region

    ' region - internal errors
#Region "internal errors"
    ''' <summary>
    ''' Show an internal error
    ''' </summary>
    ''' <param name="num">internal error number</param>
    Friend Sub ShowInternalError(num As Integer)
        Dim text As String = InternalError & num
        Tracing.TraceLine("InternalError error:" & text, TraceLevel.Error)
        MessageBox.Show(AppShellForm, text, Radios.Lexicon.Get("connect.dialog.app_error_title"), MessageBoxButtons.OK)
    End Sub

    ' Internal errors.
    Friend ReadOnly Property InternalError As String
        Get
            Return Radios.Lexicon.Get("connect.session.internal_error_prefix")
        End Get
    End Property
    Friend Const MSReplace As Integer = 1 ' MemoryScan replace.
    Friend Const MSRemove As Integer = 2 ' MemoryScan remove
    Friend Const ScanReplace As Integer = 3 ' Scan replace.
    Friend Const ScanRemove As Integer = 4 ' Scan remove
    Friend Const MSReplaceAdd As Integer = 5 ' MemoryScan replace add.
    Friend Const ScanReplaceAdd As Integer = 6 ' Scan replace add.
    Friend Const MySplitERR As Integer = 7 ' LogEntry, escape at end of string
    Friend Const LogFldMismatch1 As Integer = 8 ' LogEntry.ShowEntries, field mismatch
    Friend Const LogFldMismatch2 As Integer = 9 ' LogEntry.Read, field mismatch
    Friend Const LogVersionErr As Integer = 10 ' bad data version.
    Friend Const ImportHangup As Integer = 11 ' Excessive looping in import().
    'Friend Const NoReadB4Update As Integer = 12
    Friend Const NoSession As Integer = 13 ' No log sessions are established.
    Friend Const MenuMalfunction As Integer = 14 ' the menu should be setup
    Friend Const LogHeaderVersionError As Integer = 15 ' bad log header version
    Friend Const BandProblem As Integer = 16 ' can't get a known band's data.
    Friend Const NoRigError As Integer = 17 ' no rig defined
    Friend Const DupValueError As Integer = 18 ' adding a duplicate CommandValue.
    Friend Const BadMessageIDError As Integer = 19 ' bad cw message id when sending.
    Friend Const DupNotFoundError As Integer = 20 ' dup key element not found.
    Friend Const SessionADIFNotFound As Integer = 21 ' required session.FieldDictionary item not found.
    Friend Const BadCommandID As Integer = 22 ' invalid CommandID.
#End Region

#Region "Form1 → globals migration (Sprint 11 Phase 11.8)"

    ' Constants moved from Form1
    Private ReadOnly Property notConnected As String
        Get
            Return Radios.Lexicon.Get("connect.radio.not_connected")
        End Get
    End Property

    ' Screen saver state — saved on startup, restored on exit.
    Private onExitScreenSaver As Boolean

    Private Function setScreenSaver(val As Boolean) As Boolean
        Dim orig As Boolean = JJLogIO.ScreenSaver.GetScreenSaverActive
        JJLogIO.ScreenSaver.SetScreenSaverActive(val)
        Return orig
    End Function

    Private Sub turnTracingOff()
        If BootTrace Then
            ' Keep tracing on for connection debugging
            'Tracing.TraceLine("Boot tracing off")
            'Tracing.On = False
            'BootTrace = False
        End If
    End Sub

    Private Function currentOperatorName() As String
        Return CurrentOp.UserBasename
    End Function

    ''' <summary>
    ''' Show the one-time "Try Modern UI?" prompt for existing operators
    ''' who predate the UIMode feature.
    ''' </summary>
    Friend Sub CheckUIModUpgradePrompt()
        If CurrentOp Is Nothing Then Return
        If CurrentOp.UIModeDismissed Then Return

        CurrentOp.UIModeDismissed = True

        Dim msg As String = Radios.Lexicon.Get("connect.ui_mode.prompt_body", ("newline", vbCrLf))
        Dim result = MessageBox.Show(AppShellForm, msg, Radios.Lexicon.Get("connect.ui_mode.prompt_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Information)
        If result = DialogResult.Yes Then
            CurrentOp.UIModeSetting = CInt(UIMode.Modern)
        Else
            CurrentOp.UIModeSetting = CInt(UIMode.Classic)
        End If

        Operators.UpdateCurrentOp()
    End Sub

    ''' <summary>
    ''' Operator change handler — wired to Operators.ConfigEvent.
    ''' Moved from Form1 during Sprint 11 Phase 11.8.
    ''' </summary>
    Private Sub operatorChanged(sender As Object, e As ConfigArgs)
        If CurrentOp IsNot Nothing Then
            While (ContactLog IsNot Nothing) AndAlso (Not ContactLog.Cleanup)
            End While
            ConfigContactLog()
        End If

        If RigControl IsNot Nothing Then
            RigControl.OperatorChangeHandler()
        End If

        ' Apply the new operator's UI mode preference via WPF.
        If WpfMainWindow IsNot Nothing Then
            WpfMainWindow.ApplyUIMode(CType(ActiveUIMode, JJFlexWpf.MainWindow.UIMode))
        End If
    End Sub

    ' ── Radio open / close ──────────────────────────────────

    Private Enum AutoConnectStartupResult
        ShowSelector
        Connected
        Failed
        UserCancelled
    End Enum

    Private _autoConnectConfig As Radios.AutoConnectConfig

    ''' <summary>
    ''' Attempts to auto-connect to a saved radio on startup.
    ''' </summary>
    Private Function TryAutoConnectOnStartup() As AutoConnectStartupResult
        Try
            Dim operatorName = PersonalData.UniqueOpName(CurrentOp)
            _autoConnectConfig = Radios.AutoConnectConfig.Load(BaseConfigDir, operatorName)

            If Not _autoConnectConfig.ShouldAutoConnect Then
                Tracing.TraceLine("TryAutoConnectOnStartup: no auto-connect configured", TraceLevel.Info)
                Return AutoConnectStartupResult.ShowSelector
            End If

            Tracing.TraceLine("TryAutoConnectOnStartup: attempting " & _autoConnectConfig.RadioName, TraceLevel.Info)

            ' The other way the launch wait ends. Auto-connect skips the
            ' discovery window entirely and speaks for itself immediately
            ' below, so the startup progress voice has done its job and must
            ' not talk over the announcement that names the radio.
            Radios.ProgressVoice.Stop("auto-connect taking over")

            ' Announce only when auto-connect actually fires; when it's off the
            ' selector opens silently. Critical + interrupt so it always speaks --
            ' the connecting window's own phase speech is verbosity-gated and
            ' won't reliably name the radio.
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("connect.autoconnect.startup_announce", ("radioName", _autoConnectConfig.RadioName)),
                VerbosityLevel.Critical, True)

            ' Same connecting window as a manual connect. Without it, auto-connect
            ' was seconds of pure silence -- no phase announcements, no counting
            ' earcons -- and a blind user has no evidence the app is doing anything.
            ' The form also gives the attempt a cancel target while it runs.
            Radios.ConnectionProfiler.Current = New Radios.ConnectionProfiler()
            ShowConnectingFormOnOwnThread(_autoConnectConfig.RadioName, Radios.ConnectionProfiler.Current)

            RigControl = New FlexBase(OpenParms)

            ' Wire account selector for SmartLink remote auto-connect
            RigControl.ShowAccountSelector = Function(mgr)
                                                 Dim accounts = mgr.Accounts
                                                 If accounts.Count = 0 Then
                                                     Return (True, Nothing, True)
                                                 End If
                                                 Dim best = accounts.OrderByDescending(Function(a) a.LastUsed).First()
                                                 Return (False, best, True)
                                             End Function

            Dim connected = RigControl.TryAutoConnect(_autoConnectConfig)
            _connectingForm?.CloseForm()
            _connectingForm = Nothing

            If connected Then
                Tracing.TraceLine("TryAutoConnectOnStartup: success", TraceLevel.Info)
                Return AutoConnectStartupResult.Connected
            End If

            Tracing.TraceLine("TryAutoConnectOnStartup: failed, showing dialog", TraceLevel.Info)
            ' QB Track L: hand the dialog the classified failure evidence
            ' (Track D's report) so it states WHY, not just who. Bare wording
            ' only when no report was filed.
            Dim dialogResult = Radios.AutoConnectFailedDialog.ShowDialog(
                Nothing, _autoConnectConfig.RadioName, RigControl?.LastConnectFailureAdvice)

            Select Case dialogResult
                Case Radios.AutoConnectFailedResult.TryAgain
                    RigControl.Dispose()
                    RigControl = New FlexBase(OpenParms)
                    Radios.ConnectionProfiler.Current = New Radios.ConnectionProfiler()
                    ShowConnectingFormOnOwnThread(_autoConnectConfig.RadioName, Radios.ConnectionProfiler.Current)
                    connected = RigControl.TryAutoConnect(_autoConnectConfig)
                    _connectingForm?.CloseForm()
                    _connectingForm = Nothing
                    If connected Then
                        Return AutoConnectStartupResult.Connected
                    End If
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.Failed

                Case Radios.AutoConnectFailedResult.DisableAutoConnect
                    _autoConnectConfig.Enabled = False
                    _autoConnectConfig.Save(BaseConfigDir, operatorName)
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.ShowSelector

                Case Radios.AutoConnectFailedResult.ChooseAnotherRadio
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.Failed

                Case Else
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.UserCancelled
            End Select

            Return AutoConnectStartupResult.ShowSelector
        Catch ex As Exception
            Tracing.TraceLine("TryAutoConnectOnStartup exception: " & ex.Message, TraceLevel.Error)
            _connectingForm?.CloseForm()
            _connectingForm = Nothing
            If RigControl IsNot Nothing Then
                RigControl.Dispose()
                RigControl = Nothing
            End If
            Return AutoConnectStartupResult.ShowSelector
        End Try
    End Function

    Private radioSelected As DialogResult

    ''' <summary>
    ''' Handler for FlexBase.RadioFound events, dispatches to the WPF dialog.
    ''' </summary>
    Private _wpfRadioFoundCallback As Action(Of JJFlexWpf.Dialogs.RadioListItem)
    Private _connectingForm As ConnectingForm
    Private _connectingFormThread As Threading.Thread

    ''' <summary>
    ''' Spin up the connecting modal on its own STA message-pump thread so Escape /
    ''' X / Alt+F4 respond even while RigControl.Start() blocks the main UI thread
    ''' in its station-name-wait loop. The cancel callback flips a thread-safe flag
    ''' (FlexBase.RequestCancel) that Start() polls and exits fast.
    ''' Returns once the form's Shown event has fired so subsequent UpdateStatus
    ''' / CloseForm calls have a valid window handle.
    ''' </summary>
    ''' <param name="lead">
    ''' The picker's own "Connecting to X over SmartLink" sentence, carried into
    ''' this window's opening line. See ConnectingForm and task #93.
    ''' </param>
    Private Sub ShowConnectingFormOnOwnThread(radioName As String, profiler As Radios.ConnectionProfiler,
                                              Optional lead As String = Nothing)
        Dim ready As New Threading.ManualResetEventSlim(False)
        Dim cancelCallback As Action = Sub()
                                           Try
                                               Dim rc = RigControl
                                               If rc IsNot Nothing Then rc.RequestCancel()
                                           Catch ex As Exception
                                               Tracing.TraceLine("ConnectingForm cancel callback: " & ex.Message, TraceLevel.Error)
                                           End Try
                                       End Sub

        _connectingFormThread = New Threading.Thread(
            Sub()
                Try
                    Dim form = New ConnectingForm(radioName, cancelCallback, profiler, lead)
                    AddHandler form.Shown, Sub(sender, e) ready.Set()
                    _connectingForm = form
                    Application.Run(form)
                Catch ex As Exception
                    Tracing.TraceLine("ConnectingForm thread: " & ex.Message, TraceLevel.Error)
                Finally
                    ready.Set()
                    _connectingForm = Nothing
                End Try
            End Sub)
        _connectingFormThread.SetApartmentState(Threading.ApartmentState.STA)
        _connectingFormThread.IsBackground = True
        _connectingFormThread.Name = "ConnectingForm"
        _connectingFormThread.Start()
        ' Wait briefly so the form is shown (handle created) before any
        ' UpdateStatus / CloseForm BeginInvoke calls land. 2 s ceiling avoids
        ' deadlock if something goes wrong constructing the form.
        ready.Wait(2000)
    End Sub

    Private Sub wpfRadioFoundHandler(sender As Object, e As FlexBase.RigData)
        Tracing.TraceLine("wpfRadioFoundHandler:" & e.Serial, TraceLevel.Info)
        ' Both homes, not one verdict. IsRemote is now derived from these two
        ' flags plus the operator's path choice, so a radio that is on the LAN
        ' and registered with SmartLink no longer has its remote identity
        ' thrown away by whichever announcement arrived last.
        Dim item As New JJFlexWpf.Dialogs.RadioListItem() With {
            .Serial = e.Serial,
            .Name = e.Name,
            .ModelName = e.ModelName,
            .LanAvailable = e.LanAvailable,
            .WanAvailable = e.WanAvailable,
            .RigData = e
        }
        _wpfRadioFoundCallback?.Invoke(item)
    End Sub

    Private _wpfRadioRemovedCallback As Action(Of String, String)

    Private Sub wpfRadioRemovedHandler(sender As Object, serial As String, name As String)
        _wpfRadioRemovedCallback?.Invoke(serial, name)
    End Sub

    ''' <summary>
    ''' Session-scoped SmartLink account override, set by "Use Now" in the
    ''' account manager. ShowAccountSelector honors it AHEAD of the saved
    ''' default; it is never persisted, so an app restart is back to the
    ''' default. Empty = no override.
    ''' </summary>
    Private SessionSmartLinkEmail As String = ""

    ''' <summary>
    ''' The SmartLink account setupRemote would actually use, resolved the same
    ''' way ShowAccountSelector resolves it: a lone saved account wins outright,
    ''' then the session "Use Now" override, then the saved default. Nothing
    ''' resolves when several accounts exist and none has been chosen — that is
    ''' the case where the account picker appears, and pretending otherwise
    ''' would let the selector name an account the connect never uses.
    ''' Re-read on every call so the account manager's changes are visible
    ''' while the selector is still open.
    ''' </summary>
    Friend Function ResolveSmartLinkAccount() As Radios.SmartLinkAccount
        Dim slAccounts = Radios.FlexBase.SharedAccountManager.Accounts
        If slAccounts.Count = 0 Then Return Nothing
        If slAccounts.Count = 1 Then Return slAccounts(0)

        If Not String.IsNullOrEmpty(SessionSmartLinkEmail) Then
            Dim sessAcct = slAccounts.FirstOrDefault(Function(a) a.Email.Equals(SessionSmartLinkEmail, StringComparison.OrdinalIgnoreCase))
            If sessAcct IsNot Nothing Then Return sessAcct
        End If

        Dim opName = PersonalData.UniqueOpName(CurrentOp)
        Dim cfg = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
        If Not String.IsNullOrEmpty(cfg.SmartLinkAccountEmail) Then
            Return slAccounts.FirstOrDefault(Function(a) a.Email.Equals(cfg.SmartLinkAccountEmail, StringComparison.OrdinalIgnoreCase))
        End If
        Return Nothing
    End Function

    ''' <summary>
    ''' Saved-account count plus the account in play, for the radio selector's
    ''' account button, its accessible name, and the readable account line.
    ''' </summary>
    Friend Function ResolveSmartLinkAccountState() As JJFlexWpf.Dialogs.SmartLinkAccountState
        Dim acct = ResolveSmartLinkAccount()
        Return New JJFlexWpf.Dialogs.SmartLinkAccountState() With {
            .Count = Radios.FlexBase.SharedAccountManager.Accounts.Count,
            .Email = If(acct?.Email, ""),
            .FriendlyName = If(acct?.FriendlyName, "")
        }
    End Function

    Private Sub wpfSelectorProc(initialCall As Boolean)
        RigControl = New FlexBase(OpenParms)

        ' Wire account selector to auto-use most recent saved account.
        ' If credentials are expired, setupRemote falls through to PerformNewLogin
        ' automatically. New accounts can be added via SmartLink Account Manager menu.
        RigControl.ShowAccountSelector = Function(mgr)
                                             Dim accounts = mgr.Accounts
                                             Tracing.TraceLine($"ShowAccountSelector: {accounts.Count} saved account(s)", TraceLevel.Info)
                                             If accounts.Count = 0 Then
                                                 Tracing.TraceLine("ShowAccountSelector: no accounts, triggering new login", TraceLevel.Info)
                                                 Return (True, Nothing, True) ' trigger new login
                                             End If
                                             ' If only one account, auto-select it
                                             If accounts.Count = 1 Then
                                                 Dim only = accounts(0)
                                                 Tracing.TraceLine($"ShowAccountSelector: single account '{only.FriendlyName}' ({only.Email}), auto-selected", TraceLevel.Info)
                                                 Return (False, only, True)
                                             End If
                                             ' Session override ("Use Now") outranks the saved default. Never
                                             ' persisted — module state only, gone at app exit.
                                             If Not String.IsNullOrEmpty(SessionSmartLinkEmail) Then
                                                 Dim sessAcct = accounts.FirstOrDefault(Function(a) a.Email.Equals(SessionSmartLinkEmail, StringComparison.OrdinalIgnoreCase))
                                                 If sessAcct IsNot Nothing Then
                                                     Tracing.TraceLine($"ShowAccountSelector: using session-override account '{sessAcct.FriendlyName}' ({sessAcct.Email})", TraceLevel.Info)
                                                     Return (False, sessAcct, True)
                                                 End If
                                             End If
                                             ' Multiple accounts — try to use the saved default from auto-connect config
                                             Dim opName = PersonalData.UniqueOpName(CurrentOp)
                                             Dim savedConfig = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                                             If Not String.IsNullOrEmpty(savedConfig.SmartLinkAccountEmail) Then
                                                 Dim defaultAcct = accounts.FirstOrDefault(Function(a) a.Email.Equals(savedConfig.SmartLinkAccountEmail, StringComparison.OrdinalIgnoreCase))
                                                 If defaultAcct IsNot Nothing Then
                                                     Tracing.TraceLine($"ShowAccountSelector: using default account '{defaultAcct.FriendlyName}' ({defaultAcct.Email}) from auto-connect config", TraceLevel.Info)
                                                     Return (False, defaultAcct, True)
                                                 End If
                                             End If
                                             ' No default found — show picker dialog on UI thread
                                             Dim selectedAccount As Radios.SmartLinkAccount = Nothing
                                             Dim newLogin As Boolean = False
                                             Dim cancelled As Boolean = False
                                             Dim useOnce As Boolean = False
                                             WpfMainWindow.Dispatcher.Invoke(
                                                 Sub()
                                                     Dim acctCallbacks As New JJFlexWpf.Dialogs.SmartLinkAccountCallbacks() With {
                                                         .GetAccounts = Function() mgr.Accounts.
                                                             OrderBy(Function(a) a.FriendlyName, StringComparer.CurrentCultureIgnoreCase).
                                                             Select(Function(a) New JJFlexWpf.Dialogs.SmartLinkAccountInfo() With {
                                                                 .FriendlyName = a.FriendlyName,
                                                                 .Email = a.Email,
                                                                 .LastUsed = a.LastUsed,
                                                                 .AccountData = a,
                                                                 .AutoStartRemote = a.AutoStartRemote
                                                             }).ToList(),
                                                         .RenameAccount = Function(oldName, newName) mgr.RenameAccount(oldName, newName),
                                                         .DeleteAccount = Sub(name) mgr.DeleteAccount(name),
                                                         .SetAutoStartRemote = Sub(name, enabled)
                                                                                   Dim acct = mgr.Accounts.FirstOrDefault(Function(a) a.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase))
                                                                                   If acct IsNot Nothing Then
                                                                                       acct.AutoStartRemote = enabled
                                                                                       mgr.SaveAccounts()
                                                                                   End If
                                                                               End Sub,
                                                         .ScreenReaderSpeak = Sub(msg, interrupt) Radios.ScreenReaderOutput.Speak(msg, interrupt)
                                                     }
                                                     Dim dlg As New JJFlexWpf.Dialogs.SmartLinkAccountDialog(acctCallbacks)
                                                     Dim result = dlg.ShowDialog()
                                                     If result <> True Then
                                                         cancelled = True
                                                     ElseIf dlg.NewLoginRequested Then
                                                         newLogin = True
                                                     Else
                                                         selectedAccount = TryCast(dlg.SelectedAccountData, Radios.SmartLinkAccount)
                                                         useOnce = dlg.UseOnceRequested
                                                     End If
                                                 End Sub)
                                             If cancelled Then
                                                 Tracing.TraceLine("ShowAccountSelector: user cancelled", TraceLevel.Info)
                                                 Return (False, Nothing, False)
                                             End If
                                             If newLogin Then
                                                 Tracing.TraceLine("ShowAccountSelector: user requested new login", TraceLevel.Info)
                                                 Return (True, Nothing, True)
                                             End If
                                             If selectedAccount IsNot Nothing Then
                                                 If useOnce Then
                                                     ' Session only; the saved default stays as-is.
                                                     SessionSmartLinkEmail = selectedAccount.Email
                                                     Tracing.TraceLine($"ShowAccountSelector: use-now '{selectedAccount.FriendlyName}' ({selectedAccount.Email}), default unchanged", TraceLevel.Info)
                                                 Else
                                                     ' The button says "Set Default" — make that true from this
                                                     ' picker too, so next time no picker is needed at all.
                                                     savedConfig.SmartLinkAccountEmail = selectedAccount.Email
                                                     savedConfig.Save(BaseConfigDir, opName)
                                                     Tracing.TraceLine($"ShowAccountSelector: user selected '{selectedAccount.FriendlyName}' ({selectedAccount.Email}), saved as default", TraceLevel.Info)
                                                 End If
                                             End If
                                             Return (False, selectedAccount, True)
                                         End Function

        ' Load auto-connect config for this operator
        Dim operatorName = PersonalData.UniqueOpName(CurrentOp)
        Dim autoConfig = Radios.AutoConnectConfig.Load(BaseConfigDir, operatorName)

        ' Remote-first startup: resolve the account setupRemote WOULD use,
        ' mirroring ShowAccountSelector's order (single account → session
        ' override → saved default), and honor its AutoStartRemote flag.
        ' Ambiguous cases (multiple accounts, nothing resolved) would show
        ' the account picker anyway, so they never auto-start.
        Dim resolvedAcct As Radios.SmartLinkAccount = ResolveSmartLinkAccount()
        Dim autoStartRemote As Boolean = resolvedAcct IsNot Nothing AndAlso resolvedAcct.AutoStartRemote
        If autoStartRemote Then
            Tracing.TraceLine($"wpfSelectorProc: remote-first startup for account '{resolvedAcct.FriendlyName}' ({resolvedAcct.Email})", TraceLevel.Info)
        End If

        ' Guards against starting local discovery twice.
        '
        ' RigControl.LocalRadios() calls apiInit(True), which DISPOSES and
        ' re-creates the radio API. Calling it a second time while discovery is
        ' already running tears the live session down underneath itself: the
        ' trace shows "apiInit:True" followed immediately by "Discovery.Receive:
        ' task exited cleanly", and a connect made afterwards reached TCP and
        ' then never finished Start - 56 seconds to a timeout. Found 2026-08-18,
        ' the same day discovery was moved ahead of the picker and quietly
        ' acquired a second caller.
        Dim localDiscoveryRunning As Boolean = False

        ' Build the callbacks for the WPF dialog
        Dim callbacks As New JJFlexWpf.Dialogs.RigSelectorCallbacks() With {
            .StartLocalDiscovery = Sub()
                                       If localDiscoveryRunning Then
                                           Tracing.TraceLine(
                                               "StartLocalDiscovery: already running, not re-initialising",
                                               TraceLevel.Info)
                                           Return
                                       End If
                                       RigControl.LocalRadios()
                                       localDiscoveryRunning = True
                                   End Sub,
            .ReplayDiscoveredRadios = Sub() RigControl.ReplayDiscoveredRadios(),
            .StartRemoteDiscovery = Sub(onComplete As Action(Of Boolean))
                                        ' Run on background thread — WebView2 auth can take seconds
                                        Dim t As New Thread(
                                            Sub()
                                                RigControl.RemoteRadios()
                                                ' Notify completion immediately. Session-level success:
                                                ' IsConnected is RADIO-level and is still False after a
                                                ' successful radio-LIST pass, so it can't carry this flag.
                                                Tracing.TraceLine("StartRemoteDiscovery: calling onComplete", TraceLevel.Info)
                                                onComplete?.Invoke(RigControl.IsSmartLinkSessionLive)
                                                Tracing.TraceLine("StartRemoteDiscovery: onComplete returned", TraceLevel.Info)
                                            End Sub)
                                        t.IsBackground = True
                                        t.SetApartmentState(ApartmentState.STA)
                                        t.Name = "SmartLink"
                                        t.Start()
                                    End Sub,
            .RegisterRadioFound = Sub(callback)
                                      _wpfRadioFoundCallback = callback
                                      AddHandler FlexBase.RadioFound, AddressOf wpfRadioFoundHandler
                                  End Sub,
            .UnregisterRadioFound = Sub()
                                        RemoveHandler FlexBase.RadioFound, AddressOf wpfRadioFoundHandler
                                        _wpfRadioFoundCallback = Nothing
                                    End Sub,
            .RegisterRadioRemoved = Sub(callback)
                                        _wpfRadioRemovedCallback = callback
                                        AddHandler FlexBase.RadioRemoved, AddressOf wpfRadioRemovedHandler
                                    End Sub,
            .UnregisterRadioRemoved = Sub()
                                          RemoveHandler FlexBase.RadioRemoved, AddressOf wpfRadioRemovedHandler
                                          _wpfRadioRemovedCallback = Nothing
                                      End Sub,
            .StartRemoteRefresh = Sub(onComplete As Action(Of Boolean))
                                      ' Same shape as StartRemoteDiscovery, but cycles the
                                      ' SmartLink session first so the server sends a fresh
                                      ' radio list (it sends one per TLS session, ever).
                                      Dim t As New Thread(
                                          Sub()
                                              RigControl.RefreshRemoteRadios()
                                              Tracing.TraceLine("StartRemoteRefresh: calling onComplete", TraceLevel.Info)
                                              onComplete?.Invoke(RigControl.IsSmartLinkSessionLive)
                                              Tracing.TraceLine("StartRemoteRefresh: onComplete returned", TraceLevel.Info)
                                          End Sub)
                                      t.IsBackground = True
                                      t.SetApartmentState(ApartmentState.STA)
                                      t.Name = "SmartLink"
                                      t.Start()
                                  End Sub,
            .SaveAutoConnectSettings = Sub(serial, radioName, isRemote, lowBW, enabled)
                                           Dim opName = PersonalData.UniqueOpName(CurrentOp)
                                           Dim cfg = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                                           If enabled Then
                                               cfg.SetAutoConnectRadio(serial, radioName, isRemote,
                                                   RigControl.CurrentSmartLinkEmail, lowBW)
                                           Else
                                               If cfg.RadioSerial = serial Then
                                                   cfg.ClearAutoConnectRadio()
                                               End If
                                           End If
                                           cfg.Save(BaseConfigDir, opName)
                                       End Sub,
            .SaveGlobalAutoConnect = Sub(enabled)
                                         Dim opName = PersonalData.UniqueOpName(CurrentOp)
                                         Dim cfg = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                                         cfg.GlobalAutoConnectEnabled = enabled
                                         cfg.Save(BaseConfigDir, opName)
                                     End Sub,
            .CheckOtherAutoConnect = Function(serial)
                                         Dim opName = PersonalData.UniqueOpName(CurrentOp)
                                         Dim cfg = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                                         Return (cfg.HasDifferentAutoConnectRadio(serial), cfg.RadioName)
                                     End Function,
            .ScreenReaderSpeak = Sub(msg, interrupt)
                                     Radios.ScreenReaderOutput.Speak(msg, interrupt)
                                 End Sub,
            .AutoConnectSerial = If(autoConfig.RadioSerial, ""),
            .AutoConnectDesired = autoConfig.Enabled,
            .AutoConnectLowBW = autoConfig.LowBandwidth,
            .IsInitialBringup = initialCall,
            .GlobalAutoConnectEnabled = autoConfig.GlobalAutoConnectEnabled,
            .CurrentSmartLinkEmail = RigControl.CurrentSmartLinkEmail,
            .OpenParms = OpenParms,
            .ShowConnecting = Function(msg)
                                  Dim frm = New ConnectingForm(msg)
                                  frm.Show()
                                  Return Sub() frm.CloseForm()
                              End Function,
            .ShowSmartLinkAccountManager = Sub() WpfMainWindow.ShowSmartLinkAccountManager(),
            .AutoStartRemote = autoStartRemote,
            .GetRadioAvailability = Function(serial) RigControl.RadioAvailability(serial),
            .GetSmartLinkAccountState = Function() ResolveSmartLinkAccountState(),
            .GetCurrentRig = Function() RigControl,
            .SetSessionAccount = Sub(email)
                                     SessionSmartLinkEmail = If(email, "")
                                     Tracing.TraceLine($"RigSelector.SetSessionAccount: session override = '{SessionSmartLinkEmail}'", TraceLevel.Info)
                                 End Sub
        }

        ' Wire the save-default delegate so ShowSmartLinkAccountManager can persist the selection
        WpfMainWindow.SaveDefaultSmartLinkAccount = Sub(email)
                                                         Dim opName = PersonalData.UniqueOpName(CurrentOp)
                                                         Dim cfg = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                                                         cfg.SmartLinkAccountEmail = email
                                                         cfg.Save(BaseConfigDir, opName)
                                                         Tracing.TraceLine($"SaveDefaultSmartLinkAccount: saved {email} to auto-connect config", TraceLevel.Info)
                                                     End Sub
        WpfMainWindow.GetDefaultSmartLinkEmail = Function() As String
                                                     Dim opName = PersonalData.UniqueOpName(CurrentOp)
                                                     Dim cfg = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                                                     Return If(cfg.SmartLinkAccountEmail, "")
                                                 End Function

        ' Let discovery settle BEFORE the picker exists.
        '
        ' Opening the picker immediately meant the operator met a list that was
        ' still assembling itself. A sighted user never notices that - rows
        ' appear and they glance once at the end - but every update is an event
        ' a screen reader may voice, so the churn IS the experience through
        ' speech. On 2026-08-18 it came out as "no radios online" followed a
        ' second later by "1 radio online": the dialog correcting itself aloud.
        '
        ' Rewording the churn only narrates it more precisely. Settling first
        ' removes it, because nothing changes after the operator arrives. The
        ' window holds for at most two seconds, ends early once radios have
        ' answered and gone quiet, and Escape skips it.
        Try
            Dim settling As New JJFlexWpf.Dialogs.DiscoveringRadiosWindow(PendingDisconnectLead)
            PendingDisconnectLead = Nothing

            ' THIS is where the wait actually is, and it is here on BOTH routes
            ' — launch, and Radio menu then Connect. Measured 2026-08-25:
            ' the window is constructed, StartLocalDiscovery blocks for about
            ' five and a half seconds, and only then does ShowDialog announce
            ' anything. The launch-time voice started in ApplicationEvents
            ' covers the run-up to here; this one covers the block itself and
            ' is the only cover the manual route gets, because on that route
            ' there is no launch to have started one.
            '
            ' Start supersedes rather than stacks, so starting again when one
            ' is already running is deliberate and harmless.
            Radios.ProgressVoice.Start(
                "local discovery",
                "Looking for radios.",
                "Looking for radios on your network.",
                "Still looking.",
                "Still looking for radios.")

            ' Through the callback, NOT RigControl.LocalRadios() directly, so
            ' the picker's own start finds discovery already running.
            callbacks.StartLocalDiscovery.Invoke()
            settling.ShowDialog()
        Catch ex As Exception
            ' Never let the waiting room stop the operator reaching the picker.
            Tracing.TraceLine("DiscoveringRadiosWindow failed: " & ex.Message, TraceLevel.Warning)
        End Try

        ' Show the WPF selector dialog
        Dim dialog As New JJFlexWpf.Dialogs.RigSelectorDialog(callbacks)
        Dim wpfResult = dialog.ShowDialog()

        If wpfResult = True Then
            radioSelected = DialogResult.OK

            ' Start profiling the manual connect path
            Radios.ConnectionProfiler.Current = New Radios.ConnectionProfiler()
            Radios.ConnectionProfiler.Current.RecordEvent("dialog_closed")

            ' Set CurrentRig from the dialog's selected radio data
            Dim rigData = TryCast(dialog.SelectedRigData, FlexBase.RigData)
            If rigData IsNot Nothing Then
                CurrentRig = rigData
            End If

            ' Show connecting window IMMEDIATELY — the RigSelectorDialog just closed
            ' and there's no JJFlex window visible. Without this, focus drops to Explorer
            ' during Connect() which can take several seconds for SmartLink.
            ' Stuck-modal escape (2026-05-04): the modal runs on its own message-pump
            ' thread so Escape and the X close button respond even while Start()
            ' blocks the main UI thread in its station-name-wait loop.
            Dim radioName = If(CurrentRig?.Name, "radio")
            ' Task #93: the picker names the radio and the leg it is about to try
            ' one statement before it closes, into the exact window change that
            ' flushes a screen reader's queue. The same sentence arrives WITH
            ' this window instead, where it cannot be cut — the WHICH PATH half
            ' is what tells the operator whether to expect three seconds or
            ' thirty, and it was the half most reliably lost.
            ShowConnectingFormOnOwnThread(radioName, Radios.ConnectionProfiler.Current,
                                          dialog.SelectedConnectingLine)
            Radios.ConnectionProfiler.Current?.RecordEvent("connecting_form_shown")

            ' For remote radios: use ReconnectRemote which establishes a fresh SmartLink
            ' session before connecting. The existing session from RemoteRadios() has a
            ' stale GUIClient lifecycle that causes client removal without re-add ~1.2s
            ' into Start(), triggering early abort. A fresh session doesn't have this issue.
            ' For local radios: just discover and connect directly — no SmartLink needed.
            Dim serial = dialog.SelectedSerial
            Dim lowBW = dialog.SelectedLowBW
            Dim isRemote = dialog.SelectedIsRemote
            ' Set when the connect must travel SmartLink specifically (the
            ' operator forced it, or the chain chose SmartLink for a radio
            ' also on the local network). The connect layer must not quietly
            ' substitute the LAN path underneath that choice.
            Dim preferWan = dialog.SelectedPreferRemotePath
            Dim pathForced = dialog.SelectedPathForced
            Dim connectOk As Boolean = False

            ' The walk: the chosen path first, then the chain's remaining
            ' entries when the attempt fails — announcing every move, never
            ' silently. A forced path has no fallbacks by construction
            ' (force-remote is the hole-punch test instrument; succeeding
            ' over the wrong path would invalidate the test).
            Dim walk As New List(Of Radios.ConnectPathKind)
            walk.Add(If(isRemote, Radios.ConnectPathKind.SmartLink, Radios.ConnectPathKind.Local))
            If Not pathForced AndAlso dialog.SelectedFallbackPaths IsNot Nothing Then
                walk.AddRange(dialog.SelectedFallbackPaths)
            End If

            Tracing.TraceLine($"wpfSelectorProc: connecting {serial} lowBW={lowBW} walk=[{String.Join(",", walk)}] preferWanPath={preferWan} forced={pathForced}", TraceLevel.Info)
            Radios.ConnectionProfiler.Current?.RecordEvent("connect_call_begin")

            ' Auth ladder, rung 5: while another path remains in the chain, a
            ' SmartLink auth failure walks on instead of prompting. Only an
            ' exhausted chain earns the native sign-in form — tracked here so
            ' the retry-with-form below knows the failure was auth-shaped.
            Dim suppressedAuthFailure As Boolean = False

            For legIndex = 0 To walk.Count - 1
                Dim legPath = walk(legIndex)
                Dim lastLeg = (legIndex = walk.Count - 1)
                Dim legName = If(legPath = Radios.ConnectPathKind.SmartLink, Radios.Lexicon.Get("connect.walk.leg_smartlink"), Radios.Lexicon.Get("connect.walk.leg_local"))
                Dim legSw = System.Diagnostics.Stopwatch.StartNew()

                If legPath = Radios.ConnectPathKind.SmartLink Then
                    connectOk = RigControl.ReconnectRemote(serial, lowBW,
                        forceWanPath:=(preferWan OrElse legIndex > 0),
                        allowInteractiveLogin:=lastLeg)
                    Dim failReport = RigControl.LastConnectFailureReport
                    If Not connectOk AndAlso Not lastLeg AndAlso failReport IsNot Nothing AndAlso
                       failReport.Class = Radios.ConnectFailureClass.AuthenticationFailed Then
                        suppressedAuthFailure = True
                    End If
                Else
                    ' A fallback local leg only makes sense when a LAN object
                    ' actually exists — Connect() would otherwise resolve the
                    ' WAN object and quietly travel SmartLink under a leg that
                    ' announced itself as local.
                    Dim avail = RigControl.RadioAvailability(serial)
                    If legIndex > 0 AndAlso Not avail.lan Then
                        legSw.Stop()
                        Tracing.TraceLine($"wpfSelectorProc: leg {legIndex} local skipped — radio not on the LAN", TraceLevel.Info)
                        Radios.ConnectionHistory.Record(serial, legPath.ToString(), "not_found", legSw.ElapsedMilliseconds)
                        If Not lastLeg Then Continue For
                        connectOk = False
                        Exit For
                    End If
                    connectOk = RigControl.Connect(serial, lowBW)
                End If

                legSw.Stop()
                Radios.ConnectionHistory.Record(serial, legPath.ToString(),
                    If(connectOk, "connected", If(RigControl.LastConnectFailureReport?.Class.ToString(), "failed")),
                    legSw.ElapsedMilliseconds)

                If connectOk Then Exit For

                If Not lastLeg Then
                    ' No silent path substitution: the fallback says so.
                    Dim nextName = If(walk(legIndex + 1) = Radios.ConnectPathKind.SmartLink, Radios.Lexicon.Get("connect.walk.leg_smartlink"), Radios.Lexicon.Get("connect.walk.leg_local"))
                    Tracing.TraceLine($"wpfSelectorProc: leg {legIndex} ({legName}) failed; walking to {nextName}", TraceLevel.Info)
                    Radios.ScreenReaderOutput.Speak(
                        Radios.Lexicon.Get("connect.walk.falling_back", ("legName", legName), ("nextName", nextName)),
                        VerbosityLevel.Critical, True)
                End If
            Next

            ' The chain is exhausted and a SmartLink leg failed on auth while
            ' its prompt was suppressed: NOW the native sign-in is earned.
            If Not connectOk AndAlso suppressedAuthFailure Then
                Tracing.TraceLine("wpfSelectorProc: chain exhausted after suppressed auth failure — retrying SmartLink with sign-in allowed", TraceLevel.Info)
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.walk.signing_in"), VerbosityLevel.Critical, True)
                Dim retrySw = System.Diagnostics.Stopwatch.StartNew()
                connectOk = RigControl.ReconnectRemote(serial, lowBW, forceWanPath:=True, allowInteractiveLogin:=True)
                retrySw.Stop()
                Radios.ConnectionHistory.Record(serial, Radios.ConnectPathKind.SmartLink.ToString(),
                    If(connectOk, "connected", If(RigControl.LastConnectFailureReport?.Class.ToString(), "failed")),
                    retrySw.ElapsedMilliseconds)
            End If

            If Not connectOk Then
                _connectingForm?.CloseForm()
                _connectingForm = Nothing
                ' QB Track D: speak the classified evidence, not a bare verdict.
                ' FlexBase files LastConnectFailureReport at every failure site
                ' (auth vs server vs radio-missing vs refused vs timed out),
                ' including the verbatim router rule when the evidence points
                ' at the router. Bare "Connection failed" only when the report
                ' is genuinely absent. Track C's pre-attempt refusal rides in as
                ' ConnectFailureClass.PreflightRefused via the same property.
                Dim advice = RigControl.LastConnectFailureAdvice
                Dim failMsg = If(String.IsNullOrEmpty(advice),
                                 Radios.Lexicon.Get("connect.walk.failed"),
                                 Radios.Lexicon.Get("connect.walk.failed_with_advice", ("advice", advice)))

                ' DO NOT SPEAK THE VERDICT HERE. Task #212: the connecting
                ' window has just been asked to close, and a screen reader
                ' flushes its queue when the focused window changes — so this
                ' sentence was being issued into the exact transition that
                ' destroys it, which is how a failed connect can sound like a
                ' hang. It is held instead and spoken by openTheRadio once the
                ' shell window is back in front, which is the same shape as
                ' PendingDisconnectLead: information belongs to the surface that
                ' has focus, not to the moment before it.
                '
                ' AND NOT AT ALL WHEN THE OPERATOR STOPPED IT. Pressing Escape
                ' or Alt+F4 already said "Connection attempt cancelled" at
                ' Critical, and a cancel arrives here indistinguishable from a
                ' failure because both simply leave connectOk false. Following
                ' the operator's own decision with "Connection failed" would tell
                ' them something went wrong when what actually happened is that
                ' they stopped it — and holding the verdict until the shell is in
                ' front is exactly what would make that reliably audible for the
                ' first time. A cancel has to sound like stopping.
                If RigControl.CancelRequested Then
                    Tracing.TraceLine("wpfSelectorProc: connect ended by operator cancel — no failure verdict", TraceLevel.Info)
                Else
                    PendingConnectVerdict = failMsg
                    Tracing.TraceLine("wpfSelectorProc: holding the connect verdict until the shell is back in front — " & failMsg, TraceLevel.Info)
                End If

                ' Sprint 30 Track D's failure-moment offer (#78), wired here
                ' rather than in MainWindow — the track's note named that file,
                ' but the chain walk lives here and this is the only place a
                ' NAMED radio's connect is finally known to have failed.
                '
                ' Auth failures are deliberately excluded, per DiagnosticOffer's
                ' own reasoning: signing in fixes them, and the diagnostic log
                ' carries the SmartLink email and JWT fragments, so exporting
                ' one costs privacy and buys no diagnosis. Everything else — a
                ' refusal, a timeout, a radio that was not there — is exactly
                ' the case where the log holds the whole handshake and the
                ' evidence goes stale fastest.
                '
                ' A CANCEL IS EXCLUDED FOR THE SAME REASON, and it was not
                ' before: an operator who pressed Escape got a Problems-list
                ' entry reading "The connection to K5NER failed" and heard the
                ' offer to send a diagnostic about it. Nothing failed. Two of the
                ' three attempts in the 2026-08-26 field trace ended this way, so
                ' this is the common case rather than a corner of one.
                Dim failClass = RigControl.LastConnectFailureReport?.Class
                If Not RigControl.CancelRequested AndAlso
                   (Not failClass.HasValue OrElse
                    failClass.Value <> Radios.ConnectFailureClass.AuthenticationFailed) Then
                    Dim failedName = RigControl.RadioNickname
                    If String.IsNullOrEmpty(failedName) Then failedName = serial
                    Radios.OperationFailure.Report(
                        Radios.FailureKind.ConnectFailed,
                        $"The connection to {failedName} failed",
                        "JJ Flex recorded the whole connection attempt. Sending that " &
                        "record is the fastest way to find out why.")
                End If

                radioSelected = DialogResult.Cancel
                RigControl.Dispose()
                RigControl = Nothing
                Return
            End If
            Radios.ConnectionProfiler.Current?.RecordEvent("connect_call_end", New Dictionary(Of String, Object) From {
                {"success", True}
            })
        Else
            radioSelected = DialogResult.Cancel
            RigControl.Dispose()
            RigControl = Nothing
        End If
    End Sub

    ''' <summary>
    ''' Open the radio — builds OpenParms, runs selector, wires MainWindow.
    ''' Moved from Form1 during Sprint 11 Phase 11.8.
    ''' </summary>
    Friend Function openTheRadio(initialCall As Boolean) As Boolean
        Try
            Dim rv As Boolean
            OpenParms = New FlexBase.OpenParms()
            OpenParms.ProgramName = ProgramName
            OpenParms.ParentWindow = AppShellForm
            OpenParms.CWTextReceiver = AddressOf Commands.DisplayDecodedText
            OpenParms.FormatFreqForRadio = AddressOf UlongFreq
            OpenParms.FormatFreq = AddressOf FormatFreqUlong
            OpenParms.GotoHome = AddressOf WpfMainWindow.gotoHome
            OpenParms.ConfigDirectory = BaseConfigDir & "\Radios"
            OpenParms.AudioDevicesFile = AudioDevicesFile
            OpenParms.GetOperatorName = AddressOf currentOperatorName
            OpenParms.StationName = StationName
            OpenParms.BrailleCells = CurrentOp.BrailleDisplaySize
            OpenParms.License = CurrentOp.License
            OpenParms.Profiles = CurrentOp.Profiles
            ' Sprint 32 Track H: the Profiles dialog can now add and update
            ' entries in that list, and the Radios layer has no idea where the
            ' operator's record is stored. This is how such a change survives
            ' the session — and it is also why deleting a profile stopped coming
            ' back on the next launch.
            OpenParms.SaveOperator = AddressOf Operators.UpdateCurrentOp

            ' Check for auto-connect on initial startup
            If initialCall Then
                Dim autoConnectResult = TryAutoConnectOnStartup()
                If autoConnectResult = AutoConnectStartupResult.Connected Then
                    rv = True
                    radioSelected = DialogResult.OK
                    AppShellForm?.Activate()
                    GoTo RadioConnected
                ElseIf autoConnectResult = AutoConnectStartupResult.UserCancelled Then
                    rv = False
                    radioSelected = DialogResult.Cancel
                    Return rv
                End If
            End If

            ' Run WPF RigSelector on T1 (main UI thread) directly.
            wpfSelectorProc(initialCall)
            AppShellForm?.Activate()

            ' The connecting window is gone and the shell is in front again, so
            ' this is the first moment a verdict on a failed connect can survive
            ' being said. Critical and interrupting: a connect that did not
            ' happen is a state change the operator has to hear at any
            ' verbosity, and by now nothing else is mid-sentence.
            If Not String.IsNullOrEmpty(PendingConnectVerdict) Then
                Dim verdict = PendingConnectVerdict
                PendingConnectVerdict = Nothing
                Radios.ScreenReaderOutput.Speak(verdict, VerbosityLevel.Critical, True)
            End If

            rv = (radioSelected = DialogResult.OK)

RadioConnected:
            If rv Then
                Radios.ConnectionProfiler.Current?.RecordEvent("wiring_begin")
                WpfMainWindow.RigControl = RigControl
                WpfMainWindow.OpenParms = OpenParms
                WpfMainWindow.CloseRadioCallback = AddressOf CloseTheRadio
                WpfMainWindow.ShowErrorCallback = Sub(msg, title)
                                                      MessageBox.Show(AppShellForm, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error)
                                                  End Sub
                ' The daily-trace call that used to sit here is gone. Nothing in
                ' the app ever set KeepDailyTraceLogs, and the always-on log with
                ' per-session archiving IS the daily-trace idea done properly —
                ' see docs/planning/active/diagnostic-log-surface.md §8.3.
                WpfMainWindow.PowerOnCallback = Sub()
                                                    SetupKnob()
                                                End Sub
                WpfMainWindow.UpdateTitleBar = Sub(title)
                                                   If AppShellForm IsNot Nothing Then
                                                       AppShellForm.Text = title
                                                   End If
                                               End Sub
                WpfMainWindow.WireRadioEvents()

                ' Ensure ShellForm is visible before Start() so error dialogs
                ' have a parent window and screen readers can announce them.
                If AppShellForm IsNot Nothing AndAlso Not AppShellForm.Visible Then
                    AppShellForm.Show()
                    AppShellForm.Activate()
                    Radios.ConnectionProfiler.Current?.RecordEvent("shellform_shown")
                    Threading.Thread.Sleep(500) ' Let window settle before any error dialogs
                End If

                ' ConnectingForm is already up from wpfSelectorProc or TryAutoConnect.
                ' Stuck-modal escape (2026-05-04): the modal runs on its own thread,
                ' so we no longer set Owner = AppShellForm (cross-thread Owner is not
                ' allowed). TopMost on the form keeps it visible without an owner.

                ' Create connection profiler if one doesn't already exist
                ' (wpfSelectorProc creates one for manual connect; auto-connect may not)
                If Radios.ConnectionProfiler.Current Is Nothing Then
                    Radios.ConnectionProfiler.Current = New Radios.ConnectionProfiler()
                End If

                Radios.ConnectionProfiler.Current?.RecordEvent("start_call_begin")
                Tracing.TraceLine("OpenTheRadio:rig is starting", TraceLevel.Info)
                ' Capture the instance: an error dialog inside Start() pumps messages,
                ' and a user cancel can run CloseTheRadio (nulling RigControl) before
                ' Start() returns — reading the global here lost the failure reason.
                Dim startingRig = RigControl
                rv = startingRig.Start()
                Radios.ConnectionProfiler.Current?.RecordEvent("start_call_end", New Dictionary(Of String, Object) From {
                    {"success", rv},
                    {"failureReason", If(startingRig?.LastStartFailureReason, "")}
                })

                ' If Start() failed because SmartLink connection was too slow or dropped
                ' during the guiClient re-add cycle, retry with a fresh connection.
                ' A fresh connection bypasses the slow re-add and usually succeeds quickly.
                ' Sprint 15.5: Two retry paths — remote manual and auto-connect.
                ' Stuck-modal escape: if the user pressed Escape / X, skip the retry path
                ' entirely. The cancel was deliberate; retrying would defeat the purpose.
                If Not rv AndAlso RigControl IsNot Nothing AndAlso
                   Not RigControl.IsConnected AndAlso
                   Not RigControl.CancelRequested Then

                    Dim retrySerial = RigControl.ConnectedSerial
                    Dim retryLowBW = RigControl.ConnectedLowBW
                    Dim isRemote = (CurrentRig IsNot Nothing AndAlso CurrentRig.Remote)

                    If Not String.IsNullOrEmpty(retrySerial) AndAlso isRemote Then
                        ' Remote retry: lightweight reconnect using existing WAN session.
                        ' No re-auth, no WebView2, no new FlexBase. Up to 3 attempts total.
                        Dim maxAttempts = 3
                        For attempt = 2 To maxAttempts
                            Tracing.TraceLine($"OpenTheRadio:retry attempt {attempt}/{maxAttempts} (serial={retrySerial})", TraceLevel.Info)
                            TraceSessionContext.AddKeyEvent($"as_retry_attempt_{attempt}_remote")

                            If RigControl.RetryConnect() Then
                                Tracing.TraceLine($"OpenTheRadio:retry {attempt} - RetryConnect succeeded, calling Start", TraceLevel.Info)
                                rv = RigControl.Start()
                                If rv Then
                                    TraceSessionContext.AddKeyEvent($"as_retry_then_success_{attempt}_remote")
                                    TraceSessionContext.MarkOutcome(TraceSessionOutcome.AsRetryThenSuccess,
                                        $"Remote retry attempt {attempt} succeeded")
                                    Exit For
                                End If
                            Else
                                Tracing.TraceLine($"OpenTheRadio:retry {attempt} - RetryConnect failed", TraceLevel.Error)
                            End If
                        Next

                        If Not rv Then
                            Tracing.TraceLine("OpenTheRadio:all retry attempts failed", TraceLevel.Error)
                            TraceSessionContext.MarkOutcome(TraceSessionOutcome.AsRetryFailed,
                                $"All {maxAttempts} remote retry attempts exhausted")
                            ' QB Track D: name the reason the app already knows.
                            ' Start()-stage failures carry LastStartFailureReason
                            ' (populated since 2026-08-05); connect-stage retries
                            ' carry LastConnectFailureAdvice. Say whichever we have.
                            Dim retryReason = RigControl?.LastConnectFailureAdvice
                            If String.IsNullOrEmpty(retryReason) Then
                                Dim startReason = RigControl?.LastStartFailureReason
                                If Not String.IsNullOrEmpty(startReason) Then retryReason = startReason & "."
                            End If
                            Dim retryMsg = If(String.IsNullOrEmpty(retryReason),
                                Radios.Lexicon.Get("connect.walk.retry_failed"),
                                Radios.Lexicon.Get("connect.walk.retry_failed_with_reason", ("retryReason", retryReason)))
                            Radios.ScreenReaderOutput.Speak(retryMsg, VerbosityLevel.Critical)
                        End If

                    ElseIf _autoConnectConfig IsNot Nothing AndAlso _autoConnectConfig.ShouldAutoConnect Then
                        ' Auto-connect retry — uses TryAutoConnect with saved config
                        Tracing.TraceLine("OpenTheRadio:Start failed with auto-connect config, retrying via TryAutoConnect", TraceLevel.Info)
                        TraceSessionContext.AddKeyEvent("as_retry_attempt_autoconnect")

                        Threading.Thread.Sleep(2000)

                        WpfMainWindow?.UnwireRadioEvents()
                        RigControl.Dispose()

                        RigControl = New FlexBase(OpenParms)
                        WpfMainWindow.RigControl = RigControl

                        If RigControl.TryAutoConnect(_autoConnectConfig) Then
                            WpfMainWindow.WireRadioEvents()
                            Tracing.TraceLine("OpenTheRadio:retry - rig is starting", TraceLevel.Info)
                            rv = RigControl.Start()
                            If rv Then
                                TraceSessionContext.AddKeyEvent("as_retry_then_success_autoconnect")
                                TraceSessionContext.MarkOutcome(TraceSessionOutcome.AsRetryThenSuccess,
                                    "Auto-connect retry succeeded")
                            End If
                        End If

                        If Not rv Then
                            Tracing.TraceLine("OpenTheRadio:retry also failed", TraceLevel.Error)
                            TraceSessionContext.MarkOutcome(TraceSessionOutcome.AsRetryFailed,
                                "Auto-connect retry attempt exhausted")
                            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.walk.failed"), VerbosityLevel.Critical)
                        End If
                    Else
                        Tracing.TraceLine("OpenTheRadio:Start failed, no retry path available (local or no serial)", TraceLevel.Info)
                    End If
                End If

                If Not rv Then
                    radioSelected = DialogResult.Abort
                End If
            End If

            ' Close the connecting window
            _connectingForm?.CloseForm()
            _connectingForm = Nothing

            If rv Then
                WpfMainWindow.OnRadioStarted()
            Else
                Tracing.TraceLine("OpenTheRadio:rig's open failed", TraceLevel.Error)
                If radioSelected = DialogResult.Abort Then
                    CloseTheRadio()
                ElseIf radioSelected = DialogResult.No Then
                    MessageBox.Show(AppShellForm, notConnected, ErrorHdr, MessageBoxButtons.OK)
                Else
#If LeaveBootTraceOn = 0 Then
                    turnTracingOff()
#End If
                End If
            End If
            Return rv
        Catch ex As Exception
            Tracing.TraceLine("openTheRadio exception:" & ex.Message & Environment.NewLine & ex.StackTrace, TraceLevel.Error)
            ' Tag the active trace session with the exception so the manifest
            ' entry reflects this was a crash, not a clean exit. Per Sprint 29
            ' Track A Phase 2 / memory/project_trace_persistence_design.md.
            TraceSessionContext.MarkOutcome(TraceSessionOutcome.Crashed,
                $"openTheRadio exception: {ex.GetType().Name}: {ex.Message}")
            TraceSessionContext.AddKeyEvent("opentheradio_exception")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Close the radio and unwire all events.
    ''' Moved from Form1 during Sprint 11 Phase 11.8.
    ''' </summary>
    Friend Sub CloseTheRadio()
        Tracing.TraceLine("CloseTheRadio", TraceLevel.Info)

        WpfMainWindow?.UnwireRadioEvents()

        StopKnob()
        If SMeter IsNot Nothing Then
            SMeter.Peak = False
        End If
        If RigControl IsNot Nothing Then
            Power = False
            RigControl.Dispose()
            RigControl = Nothing
            If WpfMainWindow IsNot Nothing Then
                WpfMainWindow.RigControl = Nothing
            End If
        End If

        ' Tear down the WAN SmartLink session that FlexBase opened against the
        ' user's account. Sprint 26 Phase 4 moved session ownership to the
        ' Coordinator and removed teardown from FlexBase.Dispose, which left the
        ' WAN session alive across user-initiated radio disconnects. The next
        ' setupRemote then reuses the existing session, sends ReRegister to an
        ' already-registered SmartLink server, and gets back the protocol-level
        ' "error| Invalid state for application registration" frame that the
        ' dispatcher silently drops — followed by a 10s wait for a radio_list
        ' that never arrives, the tri-state retry hitting the same dead end,
        ' and the user seeing "no radios" for ~27s after Disconnect-then-Remote.
        ' See traces 190104 / 190405 on 2026-05-11. Reconnecting the WAN
        ' session on the next Remote click costs ~200 ms, well below the cost
        ' of the timeout cascade it replaces.
        Try
            Dim activeSession = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession
            If activeSession IsNot Nothing Then
                Tracing.TraceLine($"CloseTheRadio: disconnecting WAN session {activeSession.SessionId}", TraceLevel.Info)
                Radios.SmartLink.SmartLinkServices.Coordinator.DisconnectSession(activeSession.SessionId)
            End If
        Catch ex As Exception
            Tracing.TraceLine($"CloseTheRadio: WAN session teardown threw: {ex.Message}", TraceLevel.Warning)
        End Try
    End Sub

    ''' <summary>
    ''' Select a different radio — disconnect current, open new.
    ''' Moved from Form1.SelectRigMenuItem_Click during Sprint 11 Phase 11.8.
    ''' </summary>
    Friend Sub SelectRadio()
        Tracing.TraceLine("SelectRadio", TraceLevel.Info)
        Try
            ' Sprint 30 Track A: the lead is only earned by a radio that was
            ' actually CONNECTED. A cancelled picker leaves a live-but-unstarted
            ' FlexBase behind (only the Abort path closes it), so this used to
            ' hand the arriving picker "Disconnected from radio" for a radio the
            ' operator had never reached. Harmless-looking, and a lie the
            ' operator hears in the first sentence of the window they just
            ' opened. Newly likely rather than newly possible: Connect is now the
            ' first button on the rescue page, so the never-connected path is the
            ' ORDINARY one instead of a corner of the Radio menu.
            '
            ' CloseTheRadio still runs either way - the stale object has to be
            ' disposed before openTheRadio builds another one.
            If RigControl IsNot Nothing AndAlso RigControl.IsConnected Then
                Dim radioName = RigControl.RadioNickname

                ' Do NOT speak here. Anything said at this moment is destroyed
                ' by the picker opening a beat later - a screen reader flushes
                ' on window change, and it makes no difference whether the
                ' utterance was queued or interrupting. Both were tried on
                ' 2026-08-18; the operator heard the new window's title and
                ' nothing else.
                '
                ' The disconnect is instead handed to the window that ARRIVES,
                ' which announces it as part of its own title. Information has
                ' to belong to the surface that has focus, not to the moment
                ' before it.
                PendingDisconnectLead = If(String.IsNullOrEmpty(radioName),
                                           "Disconnected from radio",
                                           "Disconnected from " & radioName)
                ' Our announcement above covers this disconnect, so keep the
                ' radio layer quiet rather than having both speak and race. Its
                ' own message is for UNEXPECTED drops, where nothing else is
                ' explaining what happened.
                Try
                    RigControl.SuppressSpeech = True
                Catch
                End Try
            End If

            If RigControl IsNot Nothing Then
                CloseTheRadio()
            End If
            openTheRadio(False)

            ' Sprint 31 Track R - the lead is a PREFERENCE for an arriving
            ' window, not a dependency on one.
            '
            ' As written, the lead only ever reached the operator if
            ' DiscoveringRadiosWindow was actually constructed. Several paths
            ' skip it: openTheRadio returns early when auto-connect is
            ' cancelled, its outer Catch returns False on any exception before
            ' the picker, and wpfSelectorProc wraps the waiting room in its own
            ' Try that deliberately swallows a failure so nothing can stop the
            ' operator reaching the picker. On any of those the message was set
            ' and silently dropped - and worse, SuppressSpeech had already muted
            ' the radio layer's own message, so the result was total silence
            ' where there used to be at least something.
            '
            ' Speaking it here cannot double up: the consumer clears the field
            ' the instant it takes it, so a non-Nothing value at this point
            ' means nothing carried it. Critical, because a disconnect is a
            ' state change the operator has to hear at any verbosity.
            If Not String.IsNullOrEmpty(PendingDisconnectLead) Then
                Dim stranded = PendingDisconnectLead
                PendingDisconnectLead = Nothing
                Tracing.TraceLine(
                    "SelectRadio: no window carried the disconnect lead, speaking it directly",
                    TraceLevel.Info)
                Radios.ScreenReaderOutput.Speak(stranded, VerbosityLevel.Critical, True)
            End If
        Catch ex As Exception
            Tracing.TraceLine("SelectRadio:exception " & ex.Message, TraceLevel.Error)
        End Try
    End Sub

    ''' <summary>
    ''' Show past Connection Test results. Sprint 15.5.
    ''' </summary>
    Friend Sub ShowTestResults()
        Tracing.TraceLine("ShowTestResults", TraceLevel.Info)
        Try
            Dim dates = Radios.ConnectionTestReport.GetAvailableDates()
            If dates.Count = 0 Then
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.tester.results_none"))
                Return
            End If

            ' Generate report for the most recent date
            Dim latestDate = dates(0)
            Dim report = Radios.ConnectionTestReport.GenerateFromProfiles(latestDate)
            Dim reportPath = Radios.ConnectionTestReport.SaveReport(report, latestDate & "_analysis")

            If reportPath IsNot Nothing Then
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.tester.results_saved",
                                                                    ("latestDate", latestDate), ("reportPath", reportPath)))
                ' Open the file in the default text editor
                Process.Start(New ProcessStartInfo(reportPath) With {.UseShellExecute = True})
            Else
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.tester.results_save_failed"), VerbosityLevel.Critical)
            End If
        Catch ex As Exception
            Tracing.TraceLine("ShowTestResults:exception " & ex.Message, TraceLevel.Error)
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.tester.results_load_failed"), VerbosityLevel.Critical)
        End Try
    End Sub

    ''' <summary>
    ''' Application exit sequence — cleanup and shutdown.
    ''' Moved from Form1.FileExitToolStripMenuItem_Click during Sprint 11 Phase 11.8.
    ''' Returns False to cancel exit, True to proceed.
    ''' </summary>
    Friend Function ExitApplication() As Boolean
        Ending = True

        ' Check for unsaved QSO
        If Not LogEntry.optionalWrite() Then
            Ending = False
            Return False
        End If

        ' The register's exit read (#253) — Noel's ruled priority boundary.
        ' Second, after the unsaved QSO: losing a contact is worse than leaving
        ' instrumentation on, so the higher-stakes question is asked first and
        ' this one is never reached if that one cancelled. Silent unless
        ' something Notable is actually running.
        If Not ConfirmStillRunningAtExit() Then
            Ending = False
            Return False
        End If

        ' Nothing may cancel the exit past this point, so stop sampling: a
        ' threshold announcement landing during teardown would speak over the
        ' farewell, and there is nothing useful left to say about a cost that
        ' is about to stop existing.
        StopRunningCostSampler()

        ' The exit is now certain - the two prompts above are the only things
        ' that can cancel it - so this is the first safe point to say so.
        Try
            ' Let any in-flight CW finish its character. Tearing the audio stack
            ' down mid-element truncated it audibly, and a half-sent character
            ' is worse than a slightly slower exit: the operator cannot tell a
            ' clipped exit from a crash. Bounded, so a wedged audio device can
            ' never stop the application closing.
            WpfMainWindow?.WaitForCwIdle(1500)

            ' Say goodbye and WAIT for it. A queued utterance does not survive
            ' process exit - the same reason the greeting had to move to launch.
            ' SpeakAndWait estimates the duration from the text and is itself
            ' capped, so this cannot hang either.
            '
            ' There was never an exit announcement before today: RequestShutdown
            ' held nothing but trace lines, and the "Disconnecting from X" line
            ' that sounds like it should cover this belongs to SelectRadio, the
            ' SWITCH-radios path. The application simply closed in silence.
            Dim radioName As String = Nothing
            If RigControl IsNot Nothing Then radioName = RigControl.RadioNickname

            If Not String.IsNullOrWhiteSpace(radioName) Then
                Radios.ScreenReaderOutput.SpeakAndWait("Disconnecting from " & radioName & ", goodbye")
            Else
                Radios.ScreenReaderOutput.SpeakAndWait("Closing JJ Flexible Radio Access, goodbye")
            End If
        Catch ex As Exception
            ' Never let the farewell stop the exit.
            Tracing.TraceLine("ExitApplication:farewell " & ex.Message, TraceLevel.Warning)
        End Try

        Try
            LogEntry.Close()
            Logs.Done()
            If LookupStation IsNot Nothing Then
                LookupStation.Finished()
            End If
            If Commands IsNot Nothing Then
                Commands.ClusterShutdown()
            End If
            CloseTheRadio()
            If W2WattMeter IsNot Nothing Then
                W2WattMeter.Dispose()
            End If
            setScreenSaver(onExitScreenSaver)
            Tracing.TraceLine("exit:screen saver set:" & onExitScreenSaver.ToString, TraceLevel.Info)
        Catch ex As Exception
            Tracing.TraceLine("ExitApplication:" & ex.Message, TraceLevel.Error)
        End Try
        Tracing.TraceLine("End.")
        ' Archive the current trace session before closing the listener.
        ' ArchiveCurrentTraceSession handles Tracing.On = False internally so
        ' the file is fully flushed before compression. Per memory/project_trace_persistence_design.md.
        ArchiveCurrentTraceSession(TraceSessionOutcome.CleanExit, "ExitApplication clean shutdown")
        Return True
    End Function

    ''' <summary>
    ''' Application initialization — runs after WPF MainWindow is loaded.
    ''' Moved from Form1_Load during Sprint 11 Phase 11.8.
    ''' </summary>
    Friend Sub InitializeApplication()
        Tracing.TraceLine("InitializeApplication: starting", TraceLevel.Info)

        GetConfigInfo()
        CheckUIModUpgradePrompt()

        StationName = getStationName()
        Tracing.TraceLine("StationName:" & StationName, TraceLevel.Info)

        ' Set window title to include station name (was Form1.Text in Form1_Load)
        Dim pgmName = StationName
        If ProgramInstance > 1 Then
            pgmName &= ProgramInstance.ToString
        End If
        If AppShellForm IsNot Nothing Then
            AppShellForm.Text &= " " & pgmName
        End If

        ' Wire operator change handler
        AddHandler Operators.ConfigEvent, AddressOf operatorChanged

        ' Wire WriteText delegates to MainWindow
        WriteText = Sub(tbid, text, clearFlag)
                        WpfMainWindow.WriteText(CType(tbid, JJFlexWpf.MainWindow.WindowIDs), text, 0, clearFlag)
                    End Sub
        WriteTextX = Sub(tbid, s, cur, c)
                         WpfMainWindow.WriteText(CType(tbid, JJFlexWpf.MainWindow.WindowIDs), s, cur, c)
                     End Sub

        ProgramDirectory = IO.Directory.GetCurrentDirectory()
        onExitScreenSaver = setScreenSaver(False)

        ' Apply the correct UI mode now that operators are loaded
        If WpfMainWindow IsNot Nothing AndAlso CurrentOp IsNot Nothing Then
            WpfMainWindow.ApplyUIMode(CType(ActiveUIMode, JJFlexWpf.MainWindow.UIMode))
        End If

        ' Migrate config files from legacy naming to callsign-based naming.
        ' Must run BEFORE openTheRadio so auto-connect finds renamed config files.
        If CurrentOp IsNot Nothing Then
            PersonalData.MigrateConfigFiles(CurrentOp, BaseConfigDir)
        End If

        openTheRadio(True)

        ' Sprint 30 Track A — the arriving window carries the state.
        '
        ' Startup finished with no radio, so Home is about to be the rescue
        ' page. A screen reader FLUSHES its speech queue on every window
        ' change, and the whole connect flow is window changes, so an
        ' utterance made here would never survive to be heard. What DOES
        ' survive is the title of the window that arrives, which is read on
        ' arrival by definition. Set it once, here, where the title already
        ' lives; a successful connect replaces the whole title through
        ' MainWindow.UpdateTitleBar, so this can never go stale.
        '
        ' Tested against WpfMainWindow.RigControl, NOT the module-level
        ' RigControl: a cancelled picker leaves the module's FlexBase object
        ' alive (only the Abort path calls CloseTheRadio), while the window's
        ' RigControl is assigned only on a connect that actually succeeded.
        ' The window's copy is what "is a radio connected" means everywhere
        ' else, including MainWindow.EnterRescueModeIfNoRadio.
        If WpfMainWindow IsNot Nothing AndAlso WpfMainWindow.RigControl Is Nothing _
           AndAlso AppShellForm IsNot Nothing Then
            AppShellForm.Text &= " — no radio connected"
        End If

        Tracing.TraceLine("InitializeApplication: complete", TraceLevel.Info)
    End Sub

#End Region
End Module

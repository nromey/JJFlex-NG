Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports System.Diagnostics
Imports JJTrace

Module CrashReporter

    ''' <summary>
    ''' Endpoint for crash bundle uploads. Standalone constant so it's trivial
    ''' to override in a test deploy or staging environment. Server is the
    ''' rarbox FastAPI receiver per memory/project_crash_triage_bundle_flow.md
    ''' (LIVE since 2026-05-08 11:51:34 CDT per Agent.md 05-08 seal).
    ''' </summary>
    Private Const CrashEndpoint As String = "https://crashes.jjflexible.radio/crashes"

    ''' <summary>
    ''' How many recent trace archives to include in each crash bundle. 3 is
    ''' the design memo number — covers the crashed session itself plus the
    ''' two before, so the triage agent can spot pre-crash patterns.
    ''' </summary>
    Private Const RecentTracesInBundle As Integer = 3

    ''' <summary>
    ''' Largest bundle the receiver will accept, minus headroom.
    '''
    ''' The real limit is 50 MB, enforced twice on the rarbox FastAPI receiver:
    ''' nginx's client_max_body_size 50M and a FastAPI-layer check, with a 413
    ''' on the way out (see docs/planning/active/rarbox-claude-F3-G-briefing.md
    ''' and rarbox-setup-runbook-for-claude.md F5). The receiver exposes no
    ''' endpoint that reports its own limit — /healthz returns status only — so
    ''' this is a hardcoded conservative constant, deliberately 5 MB under, to
    ''' cover multipart framing and the Cloudflare proxy in front of it.
    ''' If the receiver's limit ever moves, this constant has to move with it.
    ''' </summary>
    Private Const UploadMaxBytes As Long = 45L * 1024 * 1024

    ''' <summary>
    ''' Raw trace bytes to attach to an UPLOAD bundle when the full local bundle
    ''' is too big to send. A part caps at 256 MB by rotation, which deflates to
    ''' roughly 15-25 MB — enough on its own to threaten the budget — so the
    ''' upload gets the last 64 MB of it instead. That is a deep scrollback of
    ''' the moments before the crash and lands around 4-6 MB compressed.
    ''' The complete part is always in the LOCAL bundle regardless.
    ''' </summary>
    Private Const UploadTraceTailMaxBytes As Long = 64L * 1024 * 1024

    ''' <summary>
    ''' Headroom reserved so the bundle manifest — the thing that says which
    ''' evidence was withheld — always fits, no matter what else got dropped.
    ''' </summary>
    Private Const BundleManifestReserveBytes As Long = 64L * 1024

    ''' <summary>
    ''' Cap on attaching the live trace whole to the LOCAL bundle. Rotation
    ''' keeps parts at 256 MB so this is normally unreachable; it exists for
    ''' traces rotation can't help (older builds, rotation disabled or failing).
    ''' The 2026-08-07 bundle had no trace at all because the only candidate was
    ''' an 11.7 GB file — a bounded tail is worth vastly more than nothing.
    ''' </summary>
    Private Const LocalTraceAttachMaxBytes As Long = 512L * 1024 * 1024

    ''' <summary>
    ''' Maximum number of upload attempts before giving up. Transient network
    ''' failures (timeouts, 5xx server errors) get retried; 4xx (client errors)
    ''' don't because retrying won't change the outcome.
    ''' </summary>
    Private Const MaxUploadAttempts As Integer = 3

    ''' <summary>
    ''' Crash artifacts older than this are pruned. Mirrors the trace archive's
    ''' 30-day window.
    ''' </summary>
    Private Const CrashRetentionDays As Integer = 30

    ''' <summary>
    ''' Total size cap for the Errors folder, newest kept first. Full-memory
    ''' minidumps run 200-700 MB compressed, so this holds roughly the last
    ''' 3-8 crashes — the age window alone let the folder reach gigabytes.
    ''' </summary>
    Private Const CrashFolderMaxBytes As Long = 2L * 1024 * 1024 * 1024

    ''' <summary>
    ''' A crash report that has never been sent AND never been dismissed is not
    ''' deleted by age or by the folder cap — the whole value of the crash
    ''' reporter is having the dump when support asks for it, and a retention
    ''' policy that eats the evidence defeats the feature it is protecting.
    '''
    ''' This is the one backstop on that rule, and without SOME ceiling a
    ''' machine whose upload prompt keeps failing grows without bound — which
    ''' is exactly how %AppData% reached 2.2 GB, 1.8 GB of it dumps (#92).
    ''' When it fires it says so by name in the log, because deleting
    ''' unresolved evidence should never happen quietly.
    '''
    ''' **One week, ruled by Noel 2026-08-19** (shipped at 90 days for a few
    ''' hours before he weighed in). A week is short only in isolation: rule 1
    ''' below keeps the newest <see cref="DiagnosticsConfig.KeepCrashReports"/>
    ''' bundles — three by default — regardless of age, verdict or cap. So this
    ''' window never governs the crash you just had, or the two before it. It
    ''' governs the FOURTH unresolved report and older, which is the pile-up
    ''' case: evidence nobody acted on while more kept arriving on top of it.
    ''' At 200-700 MB per dump, a week of that is the difference between a
    ''' folder you never notice and one that eats a laptop's free space.
    ''' </summary>
    Private Const UnresolvedCrashGraceDays As Integer = 7

    ''' <summary>
    ''' Suffix of the sidecar that records what the operator decided about a
    ''' bundle: "sent" or "dismissed". Its absence means "no verdict yet", which
    ''' is the state <see cref="PruneCrashReports"/> protects.
    ''' </summary>
    Private Const VerdictSuffix As String = ".verdict"

    ''' <summary>
    ''' Reused HttpClient for crash bundle uploads. Static-style instance per
    ''' the .NET HttpClient guidance — repeated New HttpClient() risks socket
    ''' exhaustion. One per Module is fine here since uploads are infrequent
    ''' and never concurrent (one crash → one bundle → one upload).
    ''' </summary>
    Private ReadOnly SharedHttpClient As New HttpClient() With {
        .Timeout = TimeSpan.FromSeconds(30)
    }
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

    ' Catch WPF Dispatcher exceptions (event handlers, Dispatcher.BeginInvoke
    ' callbacks, deferred work). Without this, WPF dispatcher exceptions fall
    ' through to OnUnhandledException above which terminates the process; with
    ' it, we save the report AND set e.Handled = True so the app stays alive
    ' (matching the soft-recover behaviour of OnThreadException for WinForms).
    ' The user gets the standard crash-report MessageBox and can choose whether
    ' to keep using the app or restart.
    '
    ' This pattern explicitly does NOT silence the crash — the report is still
    ' written, the screen reader still announces, the MessageBox still shows.
    ' It just prevents the WPF exception from cascading into the AppDomain
    ' handler (which would write a duplicate report) and from terminating
    ' the process unconditionally.
    Public Sub OnDispatcherUnhandledException(sender As Object,
                                              e As System.Windows.Threading.DispatcherUnhandledExceptionEventArgs)
        SaveCrash("WPF dispatcher exception", e.Exception, False)
        e.Handled = True
    End Sub

    Private Sub SaveCrash(context As String, ex As Exception, isTerminating As Boolean)
        Try
            Dim baseDir = Path.Combine(Radios.RadioConfig.AppDataRoot, "Errors")
            Directory.CreateDirectory(baseDir)

            Dim stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss")
            Dim txtPath = Path.Combine(baseDir, $"JJFlexError-{stamp}.txt")
            Dim dmpPath = Path.Combine(baseDir, $"JJFlexError-{stamp}.dmp")
            Dim zipPath = Path.Combine(baseDir, $"JJFlexError-{stamp}.zip")

            File.WriteAllText(txtPath, BuildReport(context, ex, isTerminating), Encoding.UTF8)
            WriteMiniDump(dmpPath)

            ' Sprint 29 Track C: collect recent trace archives so the triage
            ' agent can correlate the crash with what the prior sessions
            ' looked like. Per memory/project_user_initiated_feedback_session.md.
            Dim recentTraces As List(Of String) = GetRecentTraceArchives(RecentTracesInBundle)

            ' The evidence that actually matters is the tail of the session that
            ' just crashed — the CURRENT rotation part. Rotation bounds it by
            ' construction, so unlike the 11.7 GB whole-file case it is always
            ' attachable. Flush first: the last lines before a crash are the
            ' ones worth reading.
            ' Put rotation health into the trace before the flush, so the part
            ' we are about to attach says which part it is and whether any
            ' rotation attempt failed. A silently-failing rotation would
            ' otherwise be invisible in the very evidence meant to explain it.
            Try : Tracing.TraceRotationHealth() : Catch : End Try
            Try : Trace.Flush() : Catch : End Try
            Dim currentPart As String = Tracing.TraceFile
            ' If the crash lands moments after a rotation the current part is
            ' nearly empty, so carry the previous part too when it fits.
            Dim previousPart As String = Tracing.LastCompletedPartPath

            Dim bundle As New BundleContents With {
                .PartNumber = Tracing.CurrentPartNumber,
                .SessionHasParts = Tracing.SessionHasParts
            }

            Using zipStream = New FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None)
                Using zip = New ZipArchive(zipStream, ZipArchiveMode.Create)
                    zip.CreateEntryFromFile(txtPath, Path.GetFileName(txtPath), CompressionLevel.Optimal)
                    bundle.Included.Add(Radios.Lexicon.Get("logging.crash.item_report_text", ("fileName", Path.GetFileName(txtPath))))

                    If File.Exists(dmpPath) Then
                        zip.CreateEntryFromFile(dmpPath, Path.GetFileName(dmpPath), CompressionLevel.Optimal)
                        bundle.DumpIncludedLocally = True
                        bundle.Included.Add(Radios.Lexicon.Get("logging.crash.item_dump",
                                                              ("fileName", Path.GetFileName(dmpPath)), ("size", FormatSize(dmpPath))))
                    End If

                    AddTraceToBundle(zip, currentPart, "trace-current-part.txt",
                                     LocalTraceAttachMaxBytes, bundle, Radios.Lexicon.Get("logging.crash.label_current_trace"))
                    If Not String.IsNullOrEmpty(previousPart) AndAlso
                       Not String.Equals(previousPart, currentPart, StringComparison.OrdinalIgnoreCase) Then
                        AddTraceToBundle(zip, previousPart, "trace-previous-part.txt",
                                         LocalTraceAttachMaxBytes, bundle, Radios.Lexicon.Get("logging.crash.label_previous_trace"))
                    End If

                    For Each tracePath As String In recentTraces
                        Try
                            If File.Exists(tracePath) Then
                                zip.CreateEntryFromFile(tracePath,
                                    "traces/" & Path.GetFileName(tracePath),
                                    CompressionLevel.NoCompression) ' already LZMA-compressed
                                bundle.Included.Add(Radios.Lexicon.Get("logging.crash.item_archived_trace", ("fileName", Path.GetFileName(tracePath))))
                            End If
                        Catch
                            ' Best-effort — a single unreadable trace shouldn't fail the bundle.
                        End Try
                    Next

                    WriteBundleManifest(zip, bundle, zipPath, isUploadCopy:=False)
                End Using
            End Using

            ' The zip now holds both loose files. The .dmp alone is a 200-700 MB
            ' full-memory dump; leaving it beside its own zipped copy is how the
            ' Errors folder reached gigabytes per machine (Jan-Apr 2026).
            Try
                File.Delete(txtPath)
                File.Delete(dmpPath)
            Catch
            End Try

            ' Bound the folder even mid-session: a teardown crash storm can write
            ' several bundles a minute, and boot-time pruning alone would let it
            ' fill the disk until the next restart. Newest-first, so the bundle
            ' just written always survives.
            PruneCrashReports()

            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("logging.crash.saved_speech"),
                Radios.VerbosityLevel.Critical, True)

            ' Per project_no_silent_phone_home.md: the bundle is local until
            ' the user explicitly chooses to upload. Show what's in the report,
            ' offer Yes/No, send only on Yes.
            PromptToUploadCrashBundle(zipPath, bundle)
        Catch reportEx As Exception
            ' Last-chance logging; do not rethrow.
            Try
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "JJFlexRadio-crash.txt"),
                                   $"{DateTime.Now:u} Failed to write crash report: {reportEx}{Environment.NewLine}")
            Catch
            End Try
        End Try
    End Sub

    ''' <summary>
    ''' What went into a bundle and — just as important — what existed but was
    ''' withheld for size. Rendered into bundle-manifest.txt inside the zip and
    ''' summarized in the upload prompt, so nothing is silently missing.
    ''' </summary>
    Private Class BundleContents
        Public ReadOnly Included As New List(Of String)
        Public ReadOnly Withheld As New List(Of String)
        Public DumpIncludedLocally As Boolean
        Public DumpUploaded As Boolean
        Public PartNumber As Integer
        Public SessionHasParts As Boolean
        Public LocalBundlePath As String
    End Class

    ''' <summary>
    ''' Add a plain-text trace to a bundle, whole if it fits under
    ''' <paramref name="maxBytes"/>, otherwise its tail. Records what happened in
    ''' the bundle contents either way. A truncated attachment is honest and
    ''' useful; a missing one is neither.
    ''' </summary>
    Private Sub AddTraceToBundle(zip As ZipArchive, sourcePath As String, entryName As String,
                                 maxBytes As Long, bundle As BundleContents, label As String)
        Try
            If String.IsNullOrEmpty(sourcePath) OrElse Not File.Exists(sourcePath) Then
                bundle.Withheld.Add(Radios.Lexicon.Get("logging.crash.withheld_not_available", ("label", label)))
                Return
            End If

            Dim info As New FileInfo(sourcePath)
            Dim entry As ZipArchiveEntry = zip.CreateEntry(entryName, CompressionLevel.Optimal)
            Dim keptBytes As Long

            ' FileShare.ReadWrite: the current part is open for writing by the
            ' live trace listener right now. Without this the read fails with a
            ' sharing violation and the session trace never makes it in — which
            ' is exactly what used to happen.
            Using src As New FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                                        FileShare.ReadWrite Or FileShare.Delete)
                Using dest = entry.Open()
                    If info.Length > maxBytes Then
                        src.Seek(info.Length - maxBytes, SeekOrigin.Begin)
                        SkipToLineStart(src)
                        Dim notice As Byte() = Encoding.UTF8.GetBytes(
                            Radios.Lexicon.Get("logging.crash.truncate_notice",
                                               ("kept", FormatBytes(info.Length - src.Position)), ("total", FormatBytes(info.Length))) &
                            Environment.NewLine)
                        dest.Write(notice, 0, notice.Length)
                    End If
                    keptBytes = info.Length - src.Position
                    src.CopyTo(dest)
                End Using
            End Using

            If keptBytes < info.Length Then
                bundle.Included.Add(Radios.Lexicon.Get("logging.crash.item_trace_partial",
                                                      ("label", label), ("entryName", entryName),
                                                      ("kept", FormatBytes(keptBytes)), ("total", FormatBytes(info.Length))))
                bundle.Withheld.Add(Radios.Lexicon.Get("logging.crash.withheld_trace_head",
                                                      ("label", label), ("dropped", FormatBytes(info.Length - keptBytes))))
            Else
                bundle.Included.Add(Radios.Lexicon.Get("logging.crash.item_trace_whole",
                                                      ("label", label), ("entryName", entryName), ("size", FormatBytes(info.Length))))
            End If
        Catch attachEx As Exception
            ' Never let one unreadable trace take down the bundle — but say so.
            bundle.Withheld.Add(Radios.Lexicon.Get("logging.crash.withheld_unreadable",
                                                   ("label", label), ("error", attachEx.GetType().Name)))
        End Try
    End Sub

    ''' <summary>Advance past the remainder of a partial line.</summary>
    Private Sub SkipToLineStart(s As Stream)
        Dim guard As Long = 0
        Dim b As Integer = s.ReadByte()
        While b >= 0
            If b = 10 Then Return
            guard += 1
            If guard > 1024 * 1024 Then Return
            b = s.ReadByte()
        End While
    End Sub

    ''' <summary>
    ''' Write bundle-manifest.txt: plain prose and bullets, no tables, so a
    ''' screen reader reads it straight through. Says which trace parts are in
    ''' the bundle and which exist but were withheld for size, and where the
    ''' withheld material lives on this machine.
    ''' </summary>
    Private Sub WriteBundleManifest(zip As ZipArchive, bundle As BundleContents,
                                    localBundlePath As String, isUploadCopy As Boolean)
        Try
            Dim sb As New StringBuilder()
            sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_title"))
            sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_written", ("timestamp", DateTime.Now.ToString("u"))))
            sb.AppendLine(If(isUploadCopy, Radios.Lexicon.Get("logging.crash.manifest_upload_copy"), Radios.Lexicon.Get("logging.crash.manifest_local_copy")))
            sb.AppendLine()

            If bundle.SessionHasParts Then
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_parts", ("partNumber", bundle.PartNumber.ToString("D3"))))
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_parts_note"))
                sb.AppendLine()
            End If

            sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_included_header"))
            If bundle.Included.Count = 0 Then
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_included_none"))
            Else
                For Each item As String In bundle.Included
                    sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_bullet", ("item", item)))
                Next
            End If
            sb.AppendLine()

            sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_withheld_header"))
            If bundle.Withheld.Count = 0 Then
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_withheld_none"))
            Else
                For Each item As String In bundle.Withheld
                    sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_bullet", ("item", item)))
                Next
            End If
            sb.AppendLine()

            If isUploadCopy Then
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_upload_where"))
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_path", ("path", localBundlePath)))
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_upload_why"))
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_upload_ask"))
            Else
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_saved_at"))
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_path", ("path", localBundlePath)))
            End If

            Dim entry As ZipArchiveEntry = zip.CreateEntry("bundle-manifest.txt", CompressionLevel.Optimal)
            Using dest = entry.Open()
                Dim bytes As Byte() = Encoding.UTF8.GetBytes(sb.ToString())
                dest.Write(bytes, 0, bytes.Length)
            End Using
        Catch
            ' A missing manifest must not cost us the bundle.
        End Try
    End Sub

    ''' <summary>The folder crash artifacts live in.</summary>
    Friend ReadOnly Property CrashReportDir As String
        Get
            Return Path.Combine(
                Radios.RadioConfig.AppDataRoot, "Errors")
        End Get
    End Property

    ''' <summary>
    ''' Record what the operator decided about a bundle, so retention can tell
    ''' "evidence support may still ask for" apart from "a copy of something
    ''' already delivered". Best-effort: a missing sidecar simply means the
    ''' bundle stays protected, which is the safe direction to fail in.
    ''' </summary>
    Private Sub RecordCrashVerdict(zipPath As String, verdict As String)
        Try
            If String.IsNullOrEmpty(zipPath) Then Return
            File.WriteAllText(zipPath & VerdictSuffix,
                $"{verdict} {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}")
        Catch
            ' Not recording a verdict costs disk space, never evidence.
        End Try
    End Sub

    ''' <summary>True when the operator has sent or dismissed this bundle.</summary>
    Private Function HasCrashVerdict(zipPath As String) As Boolean
        Try
            Return File.Exists(zipPath & VerdictSuffix)
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Retention for %AppData%\JJFlexRadio\Errors.
    '''
    ''' The design tension here must NOT be resolved by pruning hard: the crash
    ''' reporter's entire value is having the dump when support asks for it, and
    ''' a full-memory dump cannot be recreated after the fact. So the rule is
    ''' "keep the most recent N, and never delete one the operator has not
    ''' either sent or explicitly dismissed" — the same shape
    ''' backup-claude-state-to-nas.ps1 uses with keep-last-12.
    '''
    ''' In order:
    '''   1. the newest N bundles are kept unconditionally (N from
    '''      DiagnosticsConfig.KeepCrashReports, default 3);
    '''   2. beyond N, a bundle is removed only once it is past the age window
    '''      or the folder cap AND the operator has recorded a verdict on it;
    '''   3. an unresolved bundle survives all of that until it passes
    '''      UnresolvedCrashGraceDays, and its removal is logged by name.
    '''
    ''' Loose .txt and .dmp files from older builds (before SaveCrash started
    ''' deleting them after zipping) get the plain age sweep — the zip beside
    ''' them holds the same content.
    '''
    ''' Called at boot (TraceArchiveBootMaintenance) and after each SaveCrash.
    ''' Never throws.
    ''' </summary>
    Friend Sub PruneCrashReports()
        Try
            Dim baseDir = CrashReportDir
            If Not Directory.Exists(baseDir) Then Return

            Dim keepNewest As Integer = 3
            Try
                keepNewest = Math.Max(1, DiagnosticsSettings.KeepCrashReports)
            Catch
                ' DiagnosticsSettings is populated in GetConfigInfo; a crash
                ' before that still gets the sane default.
            End Try

            Dim removed As Integer = 0
            Dim reclaimed As Long = 0
            Dim cutoffUtc = DateTime.UtcNow.AddDays(-CrashRetentionDays)
            Dim unresolvedCutoffUtc = DateTime.UtcNow.AddDays(-UnresolvedCrashGraceDays)

            ' --- Bundles: the artifacts worth protecting. ---
            Dim bundles = Directory.GetFiles(baseDir, "JJFlexError-*.zip") _
                .Select(Function(p) New FileInfo(p)) _
                .OrderByDescending(Function(fi) fi.LastWriteTimeUtc) _
                .ToList()

            Dim index As Integer = 0
            Dim keptBytes As Long = 0
            For Each fi In bundles
                index += 1
                Dim resolved As Boolean = HasCrashVerdict(fi.FullName)
                Dim overCap As Boolean = (keptBytes + fi.Length) > CrashFolderMaxBytes
                Dim tooOld As Boolean = fi.LastWriteTimeUtc < cutoffUtc

                Dim remove As Boolean = False
                Dim why As String = ""
                If index <= keepNewest Then
                    ' Rule 1 — the recent ones stay no matter what.
                    remove = False
                ElseIf resolved AndAlso (tooOld OrElse overCap) Then
                    remove = True
                    why = If(tooOld, "past the age window", "over the folder cap")
                ElseIf Not resolved AndAlso fi.LastWriteTimeUtc < unresolvedCutoffUtc Then
                    remove = True
                    why = $"never sent and never dismissed, and older than {UnresolvedCrashGraceDays} days"
                End If

                If remove Then
                    Dim len As Long = fi.Length
                    Try
                        fi.Delete()
                        Try
                            If File.Exists(fi.FullName & VerdictSuffix) Then File.Delete(fi.FullName & VerdictSuffix)
                        Catch
                        End Try
                        removed += 1
                        reclaimed += len
                        If Not resolved Then
                            ' Deleting unresolved evidence must never be quiet.
                            Tracing.TraceLine(
                                $"PruneCrashReports: removed UNRESOLVED crash report {fi.Name} ({why})",
                                TraceLevel.Warning)
                        End If
                    Catch
                        ' A locked or unreadable file just stays; the next prune retries.
                        keptBytes += len
                    End Try
                Else
                    keptBytes += fi.Length
                End If
            Next

            ' --- Loose leftovers from older builds: plain age sweep. ---
            For Each path As String In Directory.GetFiles(baseDir, "JJFlexError-*")
                If path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then Continue For
                If path.EndsWith(VerdictSuffix, StringComparison.OrdinalIgnoreCase) Then Continue For
                Try
                    Dim fi As New FileInfo(path)
                    If fi.LastWriteTimeUtc < cutoffUtc Then
                        Dim len As Long = fi.Length
                        fi.Delete()
                        removed += 1
                        reclaimed += len
                    End If
                Catch
                End Try
            Next

            ' --- Orphan verdict sidecars whose bundle is gone. ---
            For Each path As String In Directory.GetFiles(baseDir, "*" & VerdictSuffix)
                Try
                    Dim owner As String = path.Substring(0, path.Length - VerdictSuffix.Length)
                    If Not File.Exists(owner) Then File.Delete(path)
                Catch
                End Try
            Next

            If removed > 0 Then
                Tracing.TraceLine(
                    $"PruneCrashReports: removed {removed} crash file(s), reclaimed {reclaimed \ (1024 * 1024)} MB, kept {keptBytes \ (1024 * 1024)} MB in {Math.Min(bundles.Count, keepNewest)}+ bundle(s)",
                    TraceLevel.Info)
            End If
        Catch
            ' Housekeeping must never take the app down.
        End Try
    End Sub

    ''' <summary>
    ''' How many crash reports are on this machine and how many of them the
    ''' operator has never acted on. The Diagnostics surface says this out loud,
    ''' because nothing in the app has ever mentioned that these files exist.
    ''' </summary>
    Friend Function DescribeCrashReports() As String
        Try
            Dim baseDir = CrashReportDir
            If Not Directory.Exists(baseDir) Then Return Radios.Lexicon.Get("logging.crash.reports_none")

            Dim bundles = Directory.GetFiles(baseDir, "JJFlexError-*.zip")
            If bundles.Length = 0 Then Return Radios.Lexicon.Get("logging.crash.reports_none")

            Dim unresolved As Integer = 0
            Dim total As Long = 0
            For Each p As String In bundles
                If Not HasCrashVerdict(p) Then unresolved += 1
                Try : total += New FileInfo(p).Length : Catch : End Try
            Next

            Dim keepNewest As Integer = 3
            Try : keepNewest = Math.Max(1, DiagnosticsSettings.KeepCrashReports) : Catch : End Try

            Dim sizeText As String = DescribeBytes(total)
            Dim head As String = If(bundles.Length = 1, Radios.Lexicon.Get("logging.crash.reports_head_one",
                ("count", bundles.Length), ("size", sizeText)), Radios.Lexicon.Get("logging.crash.reports_head_many",
                ("count", bundles.Length), ("size", sizeText)))
            Dim tail As String = Radios.Lexicon.Get("logging.crash.reports_tail", ("keepNewest", keepNewest))
            If unresolved > 0 Then
                tail = If(unresolved = 1, Radios.Lexicon.Get("logging.crash.reports_unresolved_one",
                    ("count", unresolved), ("tail", tail)), Radios.Lexicon.Get("logging.crash.reports_unresolved_many",
                    ("count", unresolved), ("tail", tail)))
            End If
            Return head & tail
        Catch
            Return Radios.Lexicon.Get("logging.crash.reports_count_failed")
        End Try
    End Function

    ''' <summary>
    ''' Remove every crash report the operator has already sent or dismissed.
    ''' The manual counterpart to the automatic policy: an operator who has
    ''' just sent a 500 MB bundle should be able to get the disk space back
    ''' without waiting thirty days. Returns (filesRemoved, bytesFreed).
    ''' </summary>
    Friend Function DeleteResolvedCrashReports() As (Files As Integer, Bytes As Long)
        Dim files As Integer = 0
        Dim bytes As Long = 0
        Try
            Dim baseDir = CrashReportDir
            If Not Directory.Exists(baseDir) Then Return (0, 0)
            For Each p As String In Directory.GetFiles(baseDir, "JJFlexError-*.zip")
                If Not HasCrashVerdict(p) Then Continue For
                Try
                    Dim len As Long = New FileInfo(p).Length
                    File.Delete(p)
                    Try : File.Delete(p & VerdictSuffix) : Catch : End Try
                    files += 1
                    bytes += len
                Catch
                End Try
            Next
            Tracing.TraceLine(
                $"DeleteResolvedCrashReports: removed {files} report(s), {bytes} bytes", TraceLevel.Info)
        Catch
        End Try
        Return (files, bytes)
    End Function

    ''' <summary>
    ''' Returns the file paths of the most recent N trace archives, ordered
    ''' most-recent-first. Pulls from the trace manifest at TraceArchiveDir
    ''' so we don't double-resolve filename → path. Returns an empty list
    ''' if the manifest doesn't exist or fails to read; never throws.
    ''' Friend (QB Track L): DebugInfo bounds its bundle with the same
    ''' one-archive-per-session selection.
    ''' </summary>
    Friend Function GetRecentTraceArchives(maxCount As Integer) As List(Of String)
        Dim result As New List(Of String)
        Try
            Dim manifestPath As String = Path.Combine(TraceArchiveDir, SessionArchive.ManifestFileName)
            If Not File.Exists(manifestPath) Then Return result

            Dim manifest As TraceManifest = TraceManifest.Load(manifestPath)
            If manifest Is Nothing OrElse manifest.Entries Is Nothing Then Return result

            ' One archive per SESSION, newest part of each. Without the grouping,
            ' rotation would fill all three slots with parts of the session that
            ' just crashed — whose tail is already attached as plain text —
            ' instead of the prior sessions this is here to provide.
            Dim ordered = manifest.Entries _
                .Where(Function(e) Not String.IsNullOrEmpty(e.Filename)) _
                .GroupBy(Function(e) If(e.SessionId, e.Filename)) _
                .Select(Function(g) g.OrderByDescending(Function(e) If(e.PartNumber, 0)).First()) _
                .OrderByDescending(Function(e) e.BootTime) _
                .Take(maxCount)

            For Each entry In ordered
                Dim fullPath As String = Path.Combine(TraceArchiveDir,
                    entry.Filename.Replace("/"c, Path.DirectorySeparatorChar))
                result.Add(fullPath)
            Next
        Catch
            ' Best-effort — failure to enumerate traces shouldn't block crash report.
        End Try
        Return result
    End Function

    ''' <summary>
    ''' Show the user what's in the crash bundle and offer to upload it. Only
    ''' POSTs to the receiver if they choose Yes. Honors the no-silent-phone-home
    ''' principle: nothing leaves the user's machine without explicit consent.
    ''' </summary>
    Private Sub PromptToUploadCrashBundle(zipPath As String, bundle As BundleContents)
        Try
            bundle.LocalBundlePath = zipPath
            Dim localBytes As Long = SafeLength(zipPath)
            Dim oversize As Boolean = localBytes > UploadMaxBytes

            Dim sb As New StringBuilder()
            sb.AppendLine(Radios.Lexicon.Get("logging.crash.prompt_intro"))
            sb.AppendLine()
            sb.AppendLine(Radios.Lexicon.Get("logging.crash.prompt_saved_to"))
            sb.AppendLine(zipPath)
            sb.AppendLine()
            sb.AppendLine(Radios.Lexicon.Get("logging.crash.prompt_contains", ("size", FormatSize(zipPath))))
            For Each item As String In bundle.Included
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_bullet", ("item", item)))
            Next
            If bundle.Withheld.Count > 0 Then
                sb.AppendLine()
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.prompt_not_included"))
                For Each item As String In bundle.Withheld
                    sb.AppendLine(Radios.Lexicon.Get("logging.crash.manifest_bullet", ("item", item)))
                Next
            End If
            sb.AppendLine()
            If oversize Then
                ' Honest, and stated before the user chooses — not after a
                ' failed POST comes back with a raw server error.
                sb.AppendLine(Radios.Lexicon.Get("logging.crash.prompt_oversize", ("newline", Environment.NewLine)))
                sb.AppendLine()
            End If
            sb.AppendLine(Radios.Lexicon.Get("logging.crash.prompt_send_question"))
            sb.AppendLine(Radios.Lexicon.Get("logging.crash.prompt_endpoint", ("endpoint", CrashEndpoint)))

            Dim choice = MessageBox.Show(AppShellForm, sb.ToString(),
                Radios.Lexicon.Get("logging.crash.prompt_title"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Error)

            If choice = DialogResult.Yes Then
                ' Report text and trace tail ALWAYS upload. The memory dump only
                ' rides along when the whole bundle fits under the server limit;
                ' otherwise it is held locally and the user is told so plainly.
                Dim uploadPath As String = zipPath
                Dim reduced As Boolean = False
                If oversize Then
                    uploadPath = BuildUploadBundle(zipPath, bundle)
                    reduced = Not String.Equals(uploadPath, zipPath, StringComparison.OrdinalIgnoreCase)
                End If

                If String.IsNullOrEmpty(uploadPath) Then
                    Radios.ScreenReaderOutput.Speak(
                        Radios.Lexicon.Get("logging.crash.prepare_failed"),
                        Radios.VerbosityLevel.Critical, True)
                    Return
                End If

                bundle.DumpUploaded = bundle.DumpIncludedLocally AndAlso Not reduced

                ' Fire-and-forget upload. The user has consented; we don't block
                ' the UI on a network round-trip. Result is announced via
                ' screen reader when the POST returns. Discard the Task to
                ' silence the unawaited-Task warning — UploadCrashBundleAsync
                ' is already async and self-pumping; an outer Task.Run wrapper
                ' would only add a redundant thread-pool bounce.
                Dim _ignored = UploadCrashBundleAsync(uploadPath, reduced, zipPath) _
                    .ContinueWith(Sub(t) DiscardUploadCopy(uploadPath, zipPath))
            Else
                ' An explicit "no" is a verdict: the operator has decided about
                ' this report, so retention may eventually reclaim it. Saying no
                ' to sending is not the same as saying it may be deleted today —
                ' the newest-N floor still protects it.
                RecordCrashVerdict(zipPath, "dismissed")
                Radios.ScreenReaderOutput.Speak(
                    "Crash report kept local. Not uploaded.",
                    Radios.VerbosityLevel.Critical, True)
            End If
        Catch promptEx As Exception
            Try
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "JJFlexRadio-crash.txt"),
                    $"{DateTime.Now:u} PromptToUploadCrashBundle failed: {promptEx}{Environment.NewLine}")
                ' If MessageBox itself failed, the user heard "crash report saved"
                ' from the SaveCrash speech but nothing about the upload offer.
                ' Tell them so they can manually retry / mail the bundle.
                Radios.ScreenReaderOutput.Speak(
                    Radios.Lexicon.Get("logging.crash.prompt_failed"),
                    Radios.VerbosityLevel.Critical, True)
            Catch
            End Try
        End Try
    End Sub

    ''' <summary>
    ''' Build the reduced copy that actually gets uploaded when the full bundle
    ''' exceeds the receiver's limit. Entries are written in priority order and
    ''' each optional one is only kept if it still fits the budget:
    '''
    '''   1. the crash report text — always
    '''   2. the tail of the current trace part — always
    '''   3. the previous trace part's tail — if it fits
    '''   4. recent archived traces — while they fit
    '''   5. the bundle manifest, saying what was withheld and where it lives
    '''
    ''' The process memory dump is never in this copy. It is the one piece that
    ''' reliably blows the limit, and it is the one piece that is useless without
    ''' a human on the other end asking for it anyway.
    '''
    ''' Returns the reduced bundle's path, or the original path if a reduced copy
    ''' couldn't be built (the caller then attempts the original and gets an
    ''' honest 413 message rather than nothing).
    ''' </summary>
    Private Function BuildUploadBundle(localZipPath As String, bundle As BundleContents) As String
        Dim uploadPath As String = Path.Combine(
            Path.GetDirectoryName(localZipPath),
            Path.GetFileNameWithoutExtension(localZipPath) & "-upload.zip")

        Try
            Dim reduced As New BundleContents With {
                .PartNumber = bundle.PartNumber,
                .SessionHasParts = bundle.SessionHasParts,
                .LocalBundlePath = localZipPath,
                .DumpIncludedLocally = bundle.DumpIncludedLocally
            }
            If bundle.DumpIncludedLocally Then
                reduced.Withheld.Add(Radios.Lexicon.Get("logging.crash.withheld_dump"))
            End If
            For Each item As String In bundle.Withheld
                reduced.Withheld.Add(item)
            Next

            Dim budget As Long = UploadMaxBytes - BundleManifestReserveBytes

            Using srcZipStream As New FileStream(localZipPath, FileMode.Open, FileAccess.Read, FileShare.Read)
                Using srcZip As New ZipArchive(srcZipStream, ZipArchiveMode.Read)
                    Using destStream As New FileStream(uploadPath, FileMode.Create, FileAccess.Write, FileShare.None)
                        Using destZip As New ZipArchive(destStream, ZipArchiveMode.Create)

                            ' 1 + 2: always, in this order.
                            CopyEntryIfPresent(srcZip, destZip, reduced,
                                Function(n) n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) AndAlso
                                            n.StartsWith("JJFlexError-", StringComparison.OrdinalIgnoreCase))
                            CopyTraceTail(srcZip, destZip, reduced, "trace-current-part.txt")

                            ' 3 + 4: only while there is room.
                            If destStream.Position < budget Then
                                CopyTraceTail(srcZip, destZip, reduced, "trace-previous-part.txt")
                            ElseIf srcZip.GetEntry("trace-previous-part.txt") IsNot Nothing Then
                                reduced.Withheld.Add(Radios.Lexicon.Get("logging.crash.withheld_previous_no_room"))
                            End If

                            For Each srcEntry As ZipArchiveEntry In srcZip.Entries
                                If Not srcEntry.FullName.StartsWith("traces/", StringComparison.OrdinalIgnoreCase) Then Continue For
                                If destStream.Position + srcEntry.CompressedLength > budget Then
                                    reduced.Withheld.Add(Radios.Lexicon.Get("logging.crash.withheld_archived_no_room", ("name", srcEntry.Name)))
                                    Continue For
                                End If
                                CopyEntry(srcEntry, destZip, reduced, Radios.Lexicon.Get("logging.crash.item_archived_trace", ("fileName", srcEntry.Name)))
                            Next

                            WriteBundleManifest(destZip, reduced, localZipPath, isUploadCopy:=True)
                        End Using
                    End Using
                End Using
            End Using

            ' Belt and braces: if the reduced copy still doesn't fit, the trace
            ' tail alone is over budget. Rebuild with only report + a hard-capped
            ' tail rather than shipping something the server will reject.
            If SafeLength(uploadPath) > UploadMaxBytes Then
                Tracing.TraceLine("BuildUploadBundle: reduced bundle still over limit; nothing further to drop", TraceLevel.Warning)
            End If

            Return uploadPath
        Catch buildEx As Exception
            Tracing.ErrTraceOnly(buildEx)
            Try : If File.Exists(uploadPath) Then File.Delete(uploadPath)
            Catch : End Try
            Return localZipPath
        End Try
    End Function

    Private Sub CopyEntryIfPresent(srcZip As ZipArchive, destZip As ZipArchive, bundle As BundleContents,
                                   match As Func(Of String, Boolean))
        For Each srcEntry As ZipArchiveEntry In srcZip.Entries
            If srcEntry.FullName.Contains("/"c) Then Continue For
            If Not match(srcEntry.Name) Then Continue For
            CopyEntry(srcEntry, destZip, bundle, "Crash report text: " & srcEntry.Name)
            Return
        Next
    End Sub

    Private Sub CopyEntry(srcEntry As ZipArchiveEntry, destZip As ZipArchive,
                          bundle As BundleContents, label As String)
        Try
            Dim destEntry As ZipArchiveEntry = destZip.CreateEntry(srcEntry.FullName, CompressionLevel.Optimal)
            Using inStream = srcEntry.Open()
                Using outStream = destEntry.Open()
                    inStream.CopyTo(outStream)
                End Using
            End Using
            bundle.Included.Add(label)
        Catch
            bundle.Withheld.Add(label & ": could not be copied into the upload")
        End Try
    End Sub

    ''' <summary>
    ''' Copy a trace entry into the upload copy, keeping at most
    ''' UploadTraceTailMaxBytes of its tail. The tail is the evidence.
    ''' </summary>
    Private Sub CopyTraceTail(srcZip As ZipArchive, destZip As ZipArchive,
                              bundle As BundleContents, entryName As String)
        Dim srcEntry As ZipArchiveEntry = srcZip.GetEntry(entryName)
        If srcEntry Is Nothing Then Return
        Try
            If srcEntry.Length <= UploadTraceTailMaxBytes Then
                CopyEntry(srcEntry, destZip, bundle, $"{entryName}: {FormatBytes(srcEntry.Length)}")
                Return
            End If

            Dim skip As Long = srcEntry.Length - UploadTraceTailMaxBytes
            Dim destEntry As ZipArchiveEntry = destZip.CreateEntry(entryName, CompressionLevel.Optimal)
            Using inStream = srcEntry.Open()
                ' Deflate streams aren't seekable — read forward and discard.
                Dim scratch(65535) As Byte
                Dim skipped As Long = 0
                While skipped < skip
                    Dim want As Integer = CInt(Math.Min(CLng(scratch.Length), skip - skipped))
                    Dim got As Integer = inStream.Read(scratch, 0, want)
                    If got <= 0 Then Exit While
                    skipped += got
                End While
                Using outStream = destEntry.Open()
                    Dim notice As Byte() = Encoding.UTF8.GetBytes(
                        Radios.Lexicon.Get("logging.crash.upload_truncate_notice",
                                           ("kept", FormatBytes(srcEntry.Length - skipped)), ("total", FormatBytes(srcEntry.Length))) &
                        Environment.NewLine)
                    outStream.Write(notice, 0, notice.Length)
                    inStream.CopyTo(outStream)
                End Using
            End Using
            bundle.Included.Add(Radios.Lexicon.Get("logging.crash.item_upload_partial",
                                                   ("entryName", entryName),
                                                   ("kept", FormatBytes(srcEntry.Length - skip)), ("total", FormatBytes(srcEntry.Length))))
            bundle.Withheld.Add(Radios.Lexicon.Get("logging.crash.withheld_upload_head",
                                                   ("entryName", entryName), ("skipped", FormatBytes(skip))))
        Catch
            bundle.Withheld.Add(Radios.Lexicon.Get("logging.crash.withheld_upload_failed", ("entryName", entryName)))
        End Try
    End Sub

    ''' <summary>
    ''' POST the crash bundle to the receiver as multipart/form-data with a
    ''' single 'file' field per the F3-G server contract. Retries up to
    ''' MaxUploadAttempts on transient failures (timeouts, 5xx). Does NOT retry
    ''' on 4xx — client errors mean the bundle is rejected; retrying won't
    ''' help. Speaks the final outcome via screen reader. Diagnostic detail
    ''' (status codes, exception names) goes to the temp log, never to the
    ''' user-facing speech. Best-effort — never throws.
    ''' </summary>
    ''' <param name="zipPath">The bundle actually being sent.</param>
    ''' <param name="reduced">True when this is the trimmed copy and the memory dump stayed home.</param>
    ''' <param name="localBundlePath">Where the complete bundle lives on this machine.</param>
    Private Async Function UploadCrashBundleAsync(zipPath As String, reduced As Boolean, localBundlePath As String) As Task
        Dim lastError As String = "unknown"

        ' Pre-flight. A bundle over the receiver's limit gets a 413 and, before
        ' this check existed, the user heard a bare failure — or saw a raw
        ' framework dialog about a stream of that size — with no idea their
        ' report was safe on disk. Never hand the network something we already
        ' know will be refused.
        Dim sendBytes As Long = SafeLength(zipPath)
        If sendBytes > UploadMaxBytes Then
            Tracing.TraceLine($"UploadCrashBundleAsync: {sendBytes} bytes exceeds the {UploadMaxBytes} byte receiver limit; not attempted", TraceLevel.Warning)
            SpeakHeldLocally(localBundlePath)
            Return
        End If

        For attempt As Integer = 1 To MaxUploadAttempts
            Try
                Using fs As New FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Using form As New MultipartFormDataContent()
                        Dim fileContent As New StreamContent(fs)
                        fileContent.Headers.ContentType =
                            New System.Net.Http.Headers.MediaTypeHeaderValue("application/zip")
                        form.Add(fileContent, "file", Path.GetFileName(zipPath))

                        Using response As HttpResponseMessage = Await SharedHttpClient.PostAsync(CrashEndpoint, form)
                            If response.IsSuccessStatusCode Then
                                ' It reached the receiver. From here on the local
                                ' copy is a convenience, not the only copy, so
                                ' retention is allowed to reclaim it eventually.
                                RecordCrashVerdict(localBundlePath, "sent")
                                If reduced Then
                                    ' The report DID get through — say so first.
                                    ' The dump staying behind is a detail, not a
                                    ' failure, and must never read like one.
                                    Radios.ScreenReaderOutput.Speak(
                                        Radios.Lexicon.Get("logging.crash.uploaded_reduced"),
                                        Radios.VerbosityLevel.Critical, True)
                                Else
                                    Radios.ScreenReaderOutput.Speak(
                                        Radios.Lexicon.Get("logging.crash.uploaded"),
                                        Radios.VerbosityLevel.Critical, True)
                                End If
                                Return
                            End If

                            lastError = $"status {CInt(response.StatusCode)} {response.ReasonPhrase}"

                            ' 413 Payload Too Large is the receiver saying the
                            ' bundle is over its 50 MB limit. That is a size
                            ' outcome, not a failure the user can retry, so it
                            ' gets the honest held-locally message rather than
                            ' the generic "upload failed" — and never a raw
                            ' framework dialog.
                            If CInt(response.StatusCode) = 413 Then
                                Try
                                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "JJFlexRadio-crash.txt"),
                                        $"{DateTime.Now:u} UploadCrashBundleAsync: receiver returned 413 for {sendBytes} bytes{Environment.NewLine}")
                                Catch
                                End Try
                                SpeakHeldLocally(localBundlePath)
                                Return
                            End If

                            ' Other 4xx is permanent (bad request, auth) —
                            ' retrying won't change the outcome. Bail out of the loop.
                            If CInt(response.StatusCode) < 500 Then Exit For
                            ' 5xx is potentially transient — fall through to retry path.
                        End Using
                    End Using
                End Using
            Catch ex As TaskCanceledException
                ' HttpClient.Timeout produces TaskCanceledException, not TimeoutException.
                ' Treat as transient.
                lastError = "timeout"
            Catch ex As HttpRequestException
                ' Network-layer failure (DNS, refused, reset). Retry.
                lastError = $"{ex.GetType().Name}: {ex.Message}"
            Catch ex As Exception
                ' Unexpected — log and stop retrying. Likely a programming error,
                ' not transient.
                lastError = $"unexpected {ex.GetType().Name}: {ex.Message}"
                Exit For
            End Try

            If attempt < MaxUploadAttempts Then
                ' Backoff: 2s, then 4s. Total worst-case extra wait = 6s on top
                ' of three 30s timeouts = ~96s before user hears the failure.
                Try
                    Await Task.Delay(TimeSpan.FromSeconds(2 * attempt))
                Catch
                End Try
            End If
        Next

        ' All attempts exhausted (or hit a permanent error).
        Try
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "JJFlexRadio-crash.txt"),
                $"{DateTime.Now:u} UploadCrashBundleAsync failed after {MaxUploadAttempts} attempt(s): {lastError}{Environment.NewLine}")
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("logging.crash.upload_failed"),
                Radios.VerbosityLevel.Critical, True)
        Catch
        End Try
    End Function

    ''' <summary>
    ''' Remove the trimmed upload copy once the attempt is over. The complete
    ''' bundle beside it is the durable record; two copies of the same evidence
    ''' just eat into the Errors folder's 2 GB cap.
    ''' </summary>
    Private Sub DiscardUploadCopy(uploadPath As String, localBundlePath As String)
        Try
            If String.IsNullOrEmpty(uploadPath) Then Return
            If String.Equals(uploadPath, localBundlePath, StringComparison.OrdinalIgnoreCase) Then Return
            If File.Exists(uploadPath) Then File.Delete(uploadPath)
        Catch
            ' A leftover upload copy is harmless; PruneCrashReports sweeps it.
        End Try
    End Sub

    ''' <summary>
    ''' The honest over-limit message, spoken and traced. Leads with what the
    ''' user got (their report is safe), not with what the software couldn't do.
    ''' Replaces the raw framework dialog about a stream of that size, which told
    ''' the user nothing they could act on and implied their evidence was lost.
    ''' </summary>
    Private Sub SpeakHeldLocally(localBundlePath As String)
        Try
            Tracing.TraceLine($"Crash bundle held locally (over receiver limit): {localBundlePath}", TraceLevel.Warning)
        Catch
        End Try
        Try
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("logging.crash.held_locally"),
                Radios.VerbosityLevel.Critical, True)
        Catch
        End Try
    End Sub

    Private Function SafeLength(filePath As String) As Long
        Try
            Return New FileInfo(filePath).Length
        Catch
            Return 0
        End Try
    End Function

    Private Function FormatSize(filePath As String) As String
        Try
            Return FormatBytes(New FileInfo(filePath).Length)
        Catch
            Return Radios.Lexicon.Get("logging.crash.size_unknown")
        End Try
    End Function

    Private Function FormatBytes(bytes As Long) As String
        If bytes < 1024 Then Return Radios.Lexicon.Get("logging.crash.bytes", ("value", bytes))
        If bytes < 1024 * 1024 Then Return Radios.Lexicon.Get("logging.crash.kb", ("value", bytes \ 1024))
        If bytes < 1024L * 1024 * 1024 Then Return Radios.Lexicon.Get("logging.crash.mb", ("value", bytes \ (1024 * 1024)))
        Return Radios.Lexicon.Get("logging.crash.gb", ("value", (bytes / (1024.0 * 1024 * 1024)).ToString("0.0")))
    End Function

    Private Function BuildReport(context As String, ex As Exception, isTerminating As Boolean) As String
        Dim sb As New StringBuilder()
        sb.AppendLine("JJ Flexible Radio Access Crash Report")
        ' Local time with a real offset. This used to be DateTime.Now formatted
        ' with "u", which appends a literal "Z" — stamping every report with a
        ' UTC marker on a local timestamp (a 5-hour lie during CDT). Triage
        ' correlates crash reports against trace files and NAS build history,
        ' so the clock has to be honest about which clock it is.
        sb.AppendLine($"When: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}")
        sb.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z")
        sb.AppendLine($"Context: {context}")
        sb.AppendLine($"Terminating: {isTerminating}")
        sb.AppendLine()
        Try
            ' The full runtime picture — app identity, every component's
            ' self-reported version, environment, trace file location — comes
            ' from the SAME DiagnosticSnapshot the About page renders. One
            ' assembler, so the crash report and the About page can never
            ' disagree about what was running. The snapshot reads the ENTRY
            ' assembly (jjflexible.exe): a library-relative lookup here once
            ' stamped every report "App: System.Windows.Forms 10.0.0.0" with no
            ' JJFlex version at all (caught live 2026-08-08). It also carries
            ' Build ("4.1.16+<git sha>", the only precise identifier on a plain
            ' dotnet build) and FileVersion (the 4-part build number the NAS
            ' historical tree and tester zips are keyed by), under the same
            ' labels this report has always used. Capture() never throws, and
            ' every probe inside it is individually guarded — safe in a crash
            ' handler.
            sb.AppendLine(Radios.DiagnosticSnapshot.Capture().ToPlainText())
        Catch
            ' Belt and braces: a crash report with a bare OS line still beats
            ' no report.
            sb.AppendLine($"Diagnostic snapshot unavailable. OS: {Environment.OSVersion}")
        End Try
        sb.AppendLine()
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

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
            Dim baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JJFlexRadio", "Errors")
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
                    bundle.Included.Add("Crash report text: " & Path.GetFileName(txtPath))

                    If File.Exists(dmpPath) Then
                        zip.CreateEntryFromFile(dmpPath, Path.GetFileName(dmpPath), CompressionLevel.Optimal)
                        bundle.DumpIncludedLocally = True
                        bundle.Included.Add($"Process memory dump: {Path.GetFileName(dmpPath)} ({FormatSize(dmpPath)})")
                    End If

                    AddTraceToBundle(zip, currentPart, "trace-current-part.txt",
                                     LocalTraceAttachMaxBytes, bundle, "current session trace")
                    If Not String.IsNullOrEmpty(previousPart) AndAlso
                       Not String.Equals(previousPart, currentPart, StringComparison.OrdinalIgnoreCase) Then
                        AddTraceToBundle(zip, previousPart, "trace-previous-part.txt",
                                         LocalTraceAttachMaxBytes, bundle, "previous session trace part")
                    End If

                    For Each tracePath As String In recentTraces
                        Try
                            If File.Exists(tracePath) Then
                                zip.CreateEntryFromFile(tracePath,
                                    "traces/" & Path.GetFileName(tracePath),
                                    CompressionLevel.NoCompression) ' already LZMA-compressed
                                bundle.Included.Add("Archived trace: " & Path.GetFileName(tracePath))
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
                "JJ Flexible Radio Access hit an unexpected error. A crash report was saved.",
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
                bundle.Withheld.Add($"{label}: not available")
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
                            $"--- truncated: this is the last {FormatBytes(info.Length - src.Position)} of a {FormatBytes(info.Length)} trace ---" &
                            Environment.NewLine)
                        dest.Write(notice, 0, notice.Length)
                    End If
                    keptBytes = info.Length - src.Position
                    src.CopyTo(dest)
                End Using
            End Using

            If keptBytes < info.Length Then
                bundle.Included.Add($"{label} ({entryName}): last {FormatBytes(keptBytes)} of {FormatBytes(info.Length)}")
                bundle.Withheld.Add($"{label}: the first {FormatBytes(info.Length - keptBytes)} was too large to include")
            Else
                bundle.Included.Add($"{label} ({entryName}): {FormatBytes(info.Length)}")
            End If
        Catch attachEx As Exception
            ' Never let one unreadable trace take down the bundle — but say so.
            bundle.Withheld.Add($"{label}: could not be read ({attachEx.GetType().Name})")
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
            sb.AppendLine("JJ Flexible Radio Access crash bundle contents")
            sb.AppendLine($"Written: {DateTime.Now:u}")
            sb.AppendLine(If(isUploadCopy,
                "This is the copy that was uploaded. It is a reduced version of the bundle saved on the user's machine.",
                "This is the complete bundle as saved on the user's machine."))
            sb.AppendLine()

            If bundle.SessionHasParts Then
                sb.AppendLine($"The crashed session's trace was rotated into parts; it was writing part {bundle.PartNumber:D3} when the crash happened.")
                sb.AppendLine("Earlier parts of the same session are in the trace archive under the same file stem.")
                sb.AppendLine()
            End If

            sb.AppendLine("Included in this bundle:")
            If bundle.Included.Count = 0 Then
                sb.AppendLine("  - nothing (bundle assembly failed)")
            Else
                For Each item As String In bundle.Included
                    sb.AppendLine("  - " & item)
                Next
            End If
            sb.AppendLine()

            sb.AppendLine("Exists but withheld from this bundle:")
            If bundle.Withheld.Count = 0 Then
                sb.AppendLine("  - nothing was withheld")
            Else
                For Each item As String In bundle.Withheld
                    sb.AppendLine("  - " & item)
                Next
            End If
            sb.AppendLine()

            If isUploadCopy Then
                sb.AppendLine("The complete bundle, including the process memory dump, is on the user's computer at:")
                sb.AppendLine("  " & localBundlePath)
                sb.AppendLine("It was not uploaded because it exceeds the receiver's size limit.")
                sb.AppendLine("Ask the user for it if the dump is needed.")
            Else
                sb.AppendLine("Saved at:")
                sb.AppendLine("  " & localBundlePath)
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

    ''' <summary>
    ''' Retention for %AppData%\JJFlexRadio\Errors: delete crash artifacts older
    ''' than CrashRetentionDays, and keep the folder under CrashFolderMaxBytes
    ''' newest-first. The trace archive has pruned itself since Sprint 29; error
    ''' dumps never did. Called at boot (TraceArchiveBootMaintenance) and after
    ''' each SaveCrash. Never throws.
    ''' </summary>
    Friend Sub PruneCrashReports()
        Try
            Dim baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JJFlexRadio", "Errors")
            If Not Directory.Exists(baseDir) Then Return

            Dim files = Directory.GetFiles(baseDir, "JJFlexError-*") _
                .Select(Function(p) New FileInfo(p)) _
                .OrderByDescending(Function(fi) fi.LastWriteTimeUtc) _
                .ToList()

            Dim cutoffUtc = DateTime.UtcNow.AddDays(-CrashRetentionDays)
            Dim keptBytes As Long = 0
            Dim removed As Integer = 0
            For Each fi In files
                If fi.LastWriteTimeUtc < cutoffUtc OrElse keptBytes + fi.Length > CrashFolderMaxBytes Then
                    Try
                        fi.Delete()
                        removed += 1
                    Catch
                        ' A locked or unreadable file just stays; the next prune retries.
                    End Try
                Else
                    keptBytes += fi.Length
                End If
            Next

            If removed > 0 Then
                Tracing.TraceLine($"PruneCrashReports: removed {removed} crash file(s), kept {keptBytes \ (1024 * 1024)} MB", TraceLevel.Info)
            End If
        Catch
            ' Housekeeping must never take the app down.
        End Try
    End Sub

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
            sb.AppendLine("JJ Flexible Radio Access hit an unexpected error.")
            sb.AppendLine()
            sb.AppendLine("A crash report was saved to:")
            sb.AppendLine(zipPath)
            sb.AppendLine()
            sb.AppendLine($"The report contains ({FormatSize(zipPath)} total):")
            For Each item As String In bundle.Included
                sb.AppendLine("  - " & item)
            Next
            If bundle.Withheld.Count > 0 Then
                sb.AppendLine()
                sb.AppendLine("Not included:")
                For Each item As String In bundle.Withheld
                    sb.AppendLine("  - " & item)
                Next
            End If
            sb.AppendLine()
            If oversize Then
                ' Honest, and stated before the user chooses — not after a
                ' failed POST comes back with a raw server error.
                sb.AppendLine("This report is larger than the support server accepts, so the")
                sb.AppendLine("large crash file will not be sent. The report text and the trace")
                sb.AppendLine("from the moments before the crash will be sent — those are what")
                sb.AppendLine("diagnosis needs. The full report stays saved on this computer if")
                sb.AppendLine("support asks for it.")
                sb.AppendLine()
            End If
            sb.AppendLine("Send this report to the JJ Flexible Data Provider?")
            sb.AppendLine($"It will upload to {CrashEndpoint}")

            Dim choice = MessageBox.Show(AppShellForm, sb.ToString(),
                "JJ Flexible Radio Access crash report",
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
                        "The crash report is saved on this computer. It could not be prepared for sending.",
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
                    "Crash report saved locally. Couldn't show the upload prompt.",
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
                reduced.Withheld.Add("Process memory dump: too large to send; held on the user's computer")
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
                                reduced.Withheld.Add("Previous trace part: no room under the send limit")
                            End If

                            For Each srcEntry As ZipArchiveEntry In srcZip.Entries
                                If Not srcEntry.FullName.StartsWith("traces/", StringComparison.OrdinalIgnoreCase) Then Continue For
                                If destStream.Position + srcEntry.CompressedLength > budget Then
                                    reduced.Withheld.Add($"Archived trace {srcEntry.Name}: no room under the send limit")
                                    Continue For
                                End If
                                CopyEntry(srcEntry, destZip, reduced, "Archived trace: " & srcEntry.Name)
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
                        $"--- truncated for upload: last {FormatBytes(srcEntry.Length - skipped)} of {FormatBytes(srcEntry.Length)} ---" &
                        Environment.NewLine)
                    outStream.Write(notice, 0, notice.Length)
                    inStream.CopyTo(outStream)
                End Using
            End Using
            bundle.Included.Add($"{entryName}: last {FormatBytes(srcEntry.Length - skip)} of {FormatBytes(srcEntry.Length)}")
            bundle.Withheld.Add($"{entryName}: earlier {FormatBytes(skip)} kept only in the local copy")
        Catch
            bundle.Withheld.Add($"{entryName}: could not be copied into the upload")
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
                                If reduced Then
                                    ' The report DID get through — say so first.
                                    ' The dump staying behind is a detail, not a
                                    ' failure, and must never read like one.
                                    Radios.ScreenReaderOutput.Speak(
                                        "Your report was sent. The large crash file is saved on this computer if support asks for it.",
                                        Radios.VerbosityLevel.Critical, True)
                                Else
                                    Radios.ScreenReaderOutput.Speak(
                                        "Crash report uploaded successfully. Thank you.",
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
                "Crash report upload failed. The report is still saved locally.",
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
                "Your report was saved on this computer. It is too large to send automatically. Support can ask you for it if it is needed.",
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
            Return "size unknown"
        End Try
    End Function

    Private Function FormatBytes(bytes As Long) As String
        If bytes < 1024 Then Return $"{bytes} bytes"
        If bytes < 1024 * 1024 Then Return $"{bytes \ 1024} KB"
        If bytes < 1024L * 1024 * 1024 Then Return $"{bytes \ (1024 * 1024)} MB"
        Return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") & " GB"
    End Function

    Private Function BuildReport(context As String, ex As Exception, isTerminating As Boolean) As String
        Dim sb As New StringBuilder()
        sb.AppendLine("JJ Flexible Radio Access Crash Report")
        sb.AppendLine($"When: {DateTime.Now:u}")
        sb.AppendLine($"Context: {context}")
        sb.AppendLine($"Terminating: {isTerminating}")
        Try
            Dim asm = GetType(Form).Assembly.GetName()
            sb.AppendLine($"App: {asm.Name} {asm.Version}")
        Catch
        End Try
        sb.AppendLine($"OS: {Environment.OSVersion}")
        sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}")
        sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}")
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

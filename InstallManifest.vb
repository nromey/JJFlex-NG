Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Windows.Forms

''' <summary>
''' QB Track M — install manifests and self-verification for the debug bundle.
'''
''' The build writes a known-good install-manifest.json into the output tree
''' (generate-install-manifest.ps1, run by the GenerateInstallManifest target),
''' listing every shipped file's relative path, size, and SHA-256 fingerprint.
''' At debug-bundle time this class builds a LIVE manifest of the actual
''' install directory with the same schema, diffs the two, and writes a
''' plain-prose verification report. That replaces zipping the entire program
''' directory (~190 MB of self-contained runtime, identical on every machine)
''' into every bundle — the manifest answers the same diagnostic question
''' (stale / corrupt / mixed install?) in a few hundred KB, and answers it
''' better, because the diff names the exact files instead of leaving support
''' to compare 364 binaries by hand.
'''
''' Report style rules: prose and bullets only, never tables — the report is
''' read by screen reader users and by support. User-facing text says
''' "fingerprint", not "SHA-256"; the full hex fingerprints live in the two
''' JSON manifests riding in the same bundle.
''' </summary>
Friend Class InstallManifest
    ''' <summary>Filename of the build-time known-good manifest, shipped in the install directory.</summary>
    Friend Const ShippedManifestName As String = "install-manifest.json"

    ''' <summary>One file in a manifest. Property names match the JSON emitted
    ''' by generate-install-manifest.ps1 — the two writers share the schema.</summary>
    Friend Class ManifestFileEntry
        <JsonPropertyName("path")> Public Property Path As String
        <JsonPropertyName("size")> Public Property Size As Long
        <JsonPropertyName("sha256")> Public Property Sha256 As String
        <JsonPropertyName("fileVersion"), JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property FileVersion As String
    End Class

    ''' <summary>A file the live scan could not read. Live manifests only —
    ''' at build time an unreadable file fails the build instead.</summary>
    Friend Class UnreadableFileEntry
        <JsonPropertyName("path")> Public Property Path As String
        <JsonPropertyName("error")> Public Property ErrorText As String
    End Class

    ''' <summary>Whole manifest: schema jjflex-install-manifest/1.</summary>
    Friend Class ManifestData
        <JsonPropertyName("schema")> Public Property Schema As String = "jjflex-install-manifest/1"
        <JsonPropertyName("source")> Public Property Source As String
        <JsonPropertyName("product")> Public Property Product As String
        <JsonPropertyName("version"), JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Version As String
        <JsonPropertyName("generated")> Public Property Generated As String
        <JsonPropertyName("fileCount")> Public Property FileCount As Integer
        <JsonPropertyName("totalBytes")> Public Property TotalBytes As Long
        <JsonPropertyName("configuration"), JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Configuration As String
        <JsonPropertyName("platform"), JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Platform As String
        <JsonPropertyName("unreadable"), JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Unreadable As List(Of UnreadableFileEntry)
        <JsonPropertyName("files")> Public Property Files As List(Of ManifestFileEntry)
    End Class

    ''' <summary>Outcome of diffing live install against the known-good manifest.
    ''' The category lists hold preformatted report bullets (prose, no tables).</summary>
    Friend Class VerificationResult
        Public Property MatchCount As Integer
        ''' <summary>Files present in both but with a different size or fingerprint.</summary>
        Public ReadOnly Property Mismatched As New List(Of String)
        ''' <summary>Files the shipped manifest lists that the install doesn't have.</summary>
        Public ReadOnly Property Missing As New List(Of String)
        ''' <summary>Files in the install that the shipped manifest doesn't list.</summary>
        Public ReadOnly Property Unexpected As New List(Of String)
        ''' <summary>Files the live scan could not read, so they could not be checked.</summary>
        Public ReadOnly Property Unreadable As New List(Of String)

        Public ReadOnly Property DifferenceCount As Integer
            Get
                Return Mismatched.Count + Missing.Count + Unexpected.Count + Unreadable.Count
            End Get
        End Property
    End Class

    Private Shared ReadOnly jsonOptions As New JsonSerializerOptions With {
        .WriteIndented = True,
        .PropertyNameCaseInsensitive = True
    }

    ''' <summary>
    ''' Walk the install directory and build a live manifest — same schema and
    ''' same exclusion (the shipped manifest itself) as the build-time writer.
    ''' A file that cannot be read is recorded under Unreadable and the walk
    ''' continues; one locked file must never sink the bundle.
    ''' </summary>
    Friend Shared Function BuildLive(installDir As String) As ManifestData
        Dim root As String = IO.Path.GetFullPath(installDir).TrimEnd(IO.Path.DirectorySeparatorChar) &
            IO.Path.DirectorySeparatorChar
        Dim files As New List(Of ManifestFileEntry)
        Dim unreadable As New List(Of UnreadableFileEntry)
        Dim totalBytes As Long = 0

        Dim allPaths = Directory.GetFiles(root, "*", SearchOption.AllDirectories).
            OrderBy(Function(p) p, StringComparer.OrdinalIgnoreCase)
        Using sha As SHA256 = SHA256.Create()
            For Each fullPath As String In allPaths
                Dim relative As String = fullPath.Substring(root.Length).Replace(IO.Path.DirectorySeparatorChar, "/"c)
                ' Same exclusion as the build-time writer: the manifest never lists itself.
                If String.Equals(relative, ShippedManifestName, StringComparison.OrdinalIgnoreCase) Then Continue For
                Try
                    Dim info As New FileInfo(fullPath)
                    Dim hashHex As String
                    ' ReadWrite+Delete sharing: the running exe and its DLLs are
                    ' open for execution; reading them for hashing is still fine.
                    Using stream As New FileStream(fullPath, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite Or FileShare.Delete)
                        hashHex = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant()
                    End Using
                    Dim fv As String = Nothing
                    Try
                        fv = FileVersionInfo.GetVersionInfo(fullPath).FileVersion
                    Catch
                        ' No version resource — the field is simply omitted.
                    End Try
                    If String.IsNullOrWhiteSpace(fv) Then fv = Nothing Else fv = fv.Trim()
                    files.Add(New ManifestFileEntry With {
                        .Path = relative, .Size = info.Length, .Sha256 = hashHex, .FileVersion = fv})
                    totalBytes += info.Length
                Catch ex As Exception
                    unreadable.Add(New UnreadableFileEntry With {.Path = relative, .ErrorText = ex.Message})
                End Try
            Next
        End Using

        ' Version of the running program, not of whatever exe happens to sit in
        ' the directory — FileVersion is the clean 4-part number (ProductVersion
        ' can carry a +commit-hash suffix; same reasoning as install.bat).
        Dim liveVersion As String = Nothing
        Try
            Dim exePath As String = Environment.ProcessPath
            If String.IsNullOrEmpty(exePath) Then exePath = Application.ExecutablePath
            liveVersion = FileVersionInfo.GetVersionInfo(exePath).FileVersion
        Catch
        End Try

        Return New ManifestData With {
            .Source = "live",
            .Product = ProgramName,
            .Version = liveVersion,
            .Generated = Date.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            .FileCount = files.Count,
            .TotalBytes = totalBytes,
            .Unreadable = If(unreadable.Count > 0, unreadable, Nothing),
            .Files = files}
    End Function

    Friend Shared Function ToJson(manifest As ManifestData) As String
        Return JsonSerializer.Serialize(manifest, jsonOptions)
    End Function

    Friend Shared Function Load(manifestPath As String) As ManifestData
        Using stream As FileStream = File.OpenRead(manifestPath)
            Return JsonSerializer.Deserialize(Of ManifestData)(stream, jsonOptions)
        End Using
    End Function

    ''' <summary>
    ''' Diff the live install against the known-good manifest. Categories:
    ''' mismatched (wrong size or fingerprint), missing (shipped but absent),
    ''' unexpected (present but not shipped), unreadable (couldn't check).
    ''' </summary>
    Friend Shared Function Verify(knownGood As ManifestData, live As ManifestData) As VerificationResult
        Dim result As New VerificationResult
        Dim expected As New Dictionary(Of String, ManifestFileEntry)(StringComparer.OrdinalIgnoreCase)
        If knownGood.Files IsNot Nothing Then
            For Each entry As ManifestFileEntry In knownGood.Files
                expected(entry.Path) = entry
            Next
        End If

        If live.Files IsNot Nothing Then
            For Each actual As ManifestFileEntry In live.Files
                Dim want As ManifestFileEntry = Nothing
                If expected.TryGetValue(actual.Path, want) Then
                    expected.Remove(actual.Path)
                    If want.Size = actual.Size AndAlso
                       String.Equals(want.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase) Then
                        result.MatchCount += 1
                    ElseIf want.Size <> actual.Size Then
                        result.Mismatched.Add(
                            $"{actual.Path} — expected {want.Size:N0} bytes with fingerprint beginning {FingerprintOpening(want.Sha256)}; found {actual.Size:N0} bytes with fingerprint beginning {FingerprintOpening(actual.Sha256)}.")
                    Else
                        result.Mismatched.Add(
                            $"{actual.Path} — size matches ({actual.Size:N0} bytes) but the fingerprint differs; expected beginning {FingerprintOpening(want.Sha256)}, found {FingerprintOpening(actual.Sha256)}.")
                    End If
                Else
                    result.Unexpected.Add(
                        $"{actual.Path} ({actual.Size:N0} bytes) — present in the install but not listed in the shipped manifest.")
                End If
            Next
        End If

        ' Unreadable live files: report them as unreadable, not as missing.
        If live.Unreadable IsNot Nothing Then
            For Each blocked As UnreadableFileEntry In live.Unreadable
                expected.Remove(blocked.Path)
                result.Unreadable.Add(
                    $"{blocked.Path} — could not be read for fingerprinting: {blocked.ErrorText}")
            Next
        End If

        For Each leftover As ManifestFileEntry In expected.Values
            result.Missing.Add(
                $"{leftover.Path} — listed in the shipped manifest ({leftover.Size:N0} bytes) but not found in the install.")
        Next

        Return result
    End Function

    ''' <summary>The verification report (install-verification.txt). Prose and
    ''' bullets only — read by screen reader users and by support.</summary>
    Friend Shared Function FormatReport(result As VerificationResult, knownGood As ManifestData,
                                        installDir As String) As String
        Dim report As New StringBuilder
        AppendReportHeader(report, installDir)
        Dim pedigree As String = DescribeManifest(knownGood)
        report.AppendLine($"Shipped manifest: {pedigree}.")
        report.AppendLine()

        If result.DifferenceCount = 0 Then
            report.AppendLine($"Install verified clean — {result.MatchCount} files match the shipped manifest.")
        Else
            report.AppendLine(
                $"Install verification found {result.DifferenceCount} {Plural(result.DifferenceCount, "difference", "differences")}. {result.MatchCount} {Plural(result.MatchCount, "file matches", "files match")} the shipped manifest.")
            AppendSection(report, "Files that do not match the shipped manifest:", result.Mismatched)
            AppendSection(report, "Files the shipped manifest lists that are missing from the install:", result.Missing)
            AppendSection(report, "Files present in the install that the shipped manifest does not list:", result.Unexpected)
            AppendSection(report, "Files that could not be read, so they could not be checked:", result.Unreadable)
            report.AppendLine()
            report.AppendLine("The full fingerprints are in install-manifest.json (shipped) and program-manifest.json (live), both included in this bundle.")
        End If
        Return report.ToString()
    End Function

    ''' <summary>Report text when no shipped manifest exists — a dev tree or an
    ''' install from before manifests shipped. Plain statement, never an error.</summary>
    Friend Shared Function FormatMissingManifestReport(installDir As String) As String
        Dim report As New StringBuilder
        AppendReportHeader(report, installDir)
        report.AppendLine("No shipped manifest (install-manifest.json) was found in the install directory, so the installation could not be checked against a known-good list.")
        report.AppendLine()
        report.AppendLine("This is normal for a developer tree, or for an install made before JJ Flexible Radio Access started shipping manifests. It is not an error.")
        report.AppendLine()
        report.AppendLine("A live manifest of the install directory was still collected — see program-manifest.json in this bundle. Support can compare it against the manifest of the matching release.")
        Return report.ToString()
    End Function

    ''' <summary>Report text when the shipped manifest exists but cannot be read
    ''' or parsed — which is itself a symptom worth reporting.</summary>
    Friend Shared Function FormatUnreadableManifestReport(installDir As String, reason As String) As String
        Dim report As New StringBuilder
        AppendReportHeader(report, installDir)
        report.AppendLine("A shipped manifest (install-manifest.json) is present but could not be read, so the installation could not be checked against it.")
        report.AppendLine()
        report.AppendLine($"The problem reading it: {reason}")
        report.AppendLine()
        report.AppendLine("A damaged manifest can itself be a sign of a damaged install. The manifest file and a live manifest of the install directory (program-manifest.json) are both included in this bundle so support can take a look.")
        Return report.ToString()
    End Function

    Private Shared Sub AppendReportHeader(report As StringBuilder, installDir As String)
        report.AppendLine("JJ Flexible Radio Access — install verification")
        report.AppendLine($"Checked: {Date.Now:yyyy-MM-dd HH:mm} local time")
        report.AppendLine($"Install directory: {installDir}")
    End Sub

    Private Shared Sub AppendSection(report As StringBuilder, heading As String, bullets As List(Of String))
        If bullets.Count = 0 Then Return
        report.AppendLine()
        report.AppendLine(heading)
        For Each bullet As String In bullets
            report.AppendLine("- " & bullet)
        Next
    End Sub

    ''' <summary>Pedigree line for the shipped manifest: version, build flavor, timestamp.</summary>
    Private Shared Function DescribeManifest(manifest As ManifestData) As String
        Dim parts As New List(Of String)
        If Not String.IsNullOrEmpty(manifest.Version) Then parts.Add("version " & manifest.Version)
        Dim flavor As String = String.Join(" ",
            {manifest.Configuration, manifest.Platform}.Where(Function(s) Not String.IsNullOrEmpty(s)))
        If flavor.Length > 0 Then parts.Add(flavor & " build")
        If Not String.IsNullOrEmpty(manifest.Generated) Then parts.Add("generated " & manifest.Generated)
        If parts.Count = 0 Then Return "no version information"
        Return String.Join(", ", parts)
    End Function

    ''' <summary>First 12 hex characters — enough to talk about a fingerprint
    ''' without making a screen reader speak all 64 characters. The full values
    ''' are in the JSON manifests.</summary>
    Private Shared Function FingerprintOpening(hex As String) As String
        If String.IsNullOrEmpty(hex) Then Return "(none)"
        If hex.Length <= 12 Then Return hex
        Return hex.Substring(0, 12)
    End Function

    Private Shared Function Plural(count As Integer, one As String, many As String) As String
        Return If(count = 1, one, many)
    End Function
End Class

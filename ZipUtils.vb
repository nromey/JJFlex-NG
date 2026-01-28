Imports System
Imports System.IO
Imports System.IO.Compression

Public Module ZipUtils
    Public Sub ExtractZipSecure(zipPath As String, destinationDir As String)
        If String.IsNullOrEmpty(zipPath) OrElse String.IsNullOrEmpty(destinationDir) Then Return
        Directory.CreateDirectory(destinationDir)
        Using zip As ZipArchive = ZipFile.OpenRead(zipPath)
            Dim destRoot As String = Path.GetFullPath(destinationDir) & Path.DirectorySeparatorChar
            For Each entry As ZipArchiveEntry In zip.Entries
                ' Skip directory entries
                If String.IsNullOrEmpty(entry.Name) Then Continue For

                Dim fullPath As String = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName))
                ' Prevent Zip Slip (path traversal)
                If Not fullPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase) Then
                    ' Unsafe entry — skip (could log if desired)
                    Continue For
                End If

                Dim dirName As String = Path.GetDirectoryName(fullPath)
                If Not String.IsNullOrEmpty(dirName) Then
                    Directory.CreateDirectory(dirName)
                End If
                entry.ExtractToFile(fullPath, True)
            Next
        End Using
    End Sub

    Public Sub AddDirectoryToArchive(archive As ZipArchive, sourceDir As String, entryRoot As String, Optional excludePattern As String = Nothing)
        If archive Is Nothing OrElse String.IsNullOrEmpty(sourceDir) Then Return
        If Not Directory.Exists(sourceDir) Then Return
        Dim root As String = If(entryRoot, String.Empty)
        Dim rootPrefix As String = If(String.IsNullOrEmpty(root), String.Empty, root.TrimEnd(Path.DirectorySeparatorChar, "/"c))
        Dim sourceRoot As String = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        For Each file As String In Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
            Dim name As String = Path.GetFileName(file)
            If Not String.IsNullOrEmpty(excludePattern) AndAlso name Like excludePattern Then
                Continue For
            End If
            Dim fullFile As String = Path.GetFullPath(file)
            If Not fullFile.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) Then
                Continue For
            End If
            Dim relative As String = fullFile.Substring(sourceRoot.Length)
            Dim entryName As String = If(String.IsNullOrEmpty(rootPrefix), relative, Path.Combine(rootPrefix, relative))
            entryName = entryName.Replace(Path.DirectorySeparatorChar, "/"c)
            archive.CreateEntryFromFile(fullFile, entryName, CompressionLevel.Optimal)
        Next
    End Sub

    Public Sub AddFileToArchive(archive As ZipArchive, filePath As String, entryDirectory As String)
        If archive Is Nothing OrElse String.IsNullOrEmpty(filePath) Then Return
        If Not File.Exists(filePath) Then Return
        Dim entryName As String = Path.GetFileName(filePath)
        If Not String.IsNullOrEmpty(entryDirectory) Then
            Dim dir As String = entryDirectory.TrimEnd(Path.DirectorySeparatorChar, "/"c)
            entryName = Path.Combine(dir, entryName)
        End If
        entryName = entryName.Replace(Path.DirectorySeparatorChar, "/"c)
        archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal)
    End Sub
End Module

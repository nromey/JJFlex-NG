Imports System.IO
Imports System.Windows.Forms
Imports System.IO.Compression

Friend Class ImportSetup
    Friend Shared Function ImportSetup() As Boolean
        Dim rv As Boolean = False
        Dim openDialog = New OpenFileDialog()
        openDialog.AddExtension = True
        openDialog.CheckFileExists = True
        openDialog.DefaultExt = "zip"
        openDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        openDialog.Title = Radios.Lexicon.Get("settings.archive.file_dialog_title")
        If openDialog.ShowDialog() <> DialogResult.OK Then
            openDialog.Dispose()
            MessageBox.Show(Radios.Lexicon.Get("settings.archive.import_needs_input_file"), ErrorHdr, MessageBoxButtons.OK)
            Return rv
        End If
        Dim outDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)

        Try
            ' Secure extraction: only files, prevent path traversal
            ZipUtils.ExtractZipSecure(openDialog.FileName, outDir)
            rv = True
        Catch ex As Exception
            MessageBox.Show(ex.Message, ExceptionHdr, MessageBoxButtons.OK)
        End Try
        If rv Then
            MessageBox.Show(Radios.Lexicon.Get("settings.archive.imported"), MessageHdr, MessageBoxButtons.OK)
        End If
        openDialog.Dispose()
        Return rv
    End Function
End Class

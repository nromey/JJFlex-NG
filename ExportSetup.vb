Imports System.IO
Imports System.Windows.Forms
Imports System.IO.Compression

Friend Class ExportSetup
    Friend Shared Sub ExportSetup()
        Dim openDialog = New OpenFileDialog()
        openDialog.AddExtension = True
        openDialog.CheckFileExists = False
        openDialog.DefaultExt = "zip"
        openDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        openDialog.Title = Radios.Lexicon.Get("settings.archive.file_dialog_title")
        If openDialog.ShowDialog() <> DialogResult.OK Then
            openDialog.Dispose()
            MessageBox.Show(Radios.Lexicon.Get("settings.archive.export_needs_output_file"), ErrorHdr, MessageBoxButtons.OK)
            Return
        End If

        File.Delete(openDialog.FileName)
        Using archive As ZipArchive = ZipFile.Open(openDialog.FileName, ZipArchiveMode.Create)
            ' get application data, exclude trace files
            ZipUtils.AddDirectoryToArchive(archive, BaseConfigDir, ProgramName, "*trace*.txt")
        End Using
        MessageBox.Show(Radios.Lexicon.Get("settings.archive.exported"), MessageHdr, MessageBoxButtons.OK)
        openDialog.Dispose()
    End Sub
End Class

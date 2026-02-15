''' <summary>
''' Sprint 10: Lightweight adapter that exposes a Write(key, value) API
''' matching RadioBoxes.MainBox, but routes to WPF MainWindow.WriteStatus().
''' This allows scan.vb, MemoryScan.vb, globals.vb etc. to call
''' StatusBox.Write("Scan", "Running") without referencing RadioBoxes or Form1.
''' </summary>
Friend Class StatusBoxAdapter
    Private ReadOnly _mainWindow As JJFlexWpf.MainWindow

    Friend Sub New(mainWindow As JJFlexWpf.MainWindow)
        _mainWindow = mainWindow
    End Sub

    ''' <summary>
    ''' Write a named status field. Matches RadioBoxes.MainBox.Write(key, text).
    ''' </summary>
    Friend Sub Write(key As String, text As String)
        _mainWindow?.WriteStatus(key, text)
    End Sub
End Class

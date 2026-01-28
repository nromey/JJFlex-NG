Imports System.Reflection
Imports Flex.Smoothlake.FlexLib
Imports JJTrace

Public NotInheritable Class About
    ' DLL's whose versions are shown.
    Private Shared dllList As String() = { _
        "flexlib",
        "jjloglib",
        "radios",
        "radioboxes"
        }
    ' just to get a reference
    Private ReadOnly Property radioList As List(Of Radio)
        Get
            Try
                Return API.RadioList
            Catch ex As Exception
                Return Nothing
            End Try
        End Get
    End Property

    Private Sub About_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        ' Set the title of the form.
        Dim ApplicationTitle As String
        If My.Application.Info.Title <> "" Then
            ApplicationTitle = My.Application.Info.Title
        Else
            ApplicationTitle = System.IO.Path.GetFileNameWithoutExtension(My.Application.Info.AssemblyName)
        End If
        Me.LogoPictureBox.Enabled = False
        Me.LogoPictureBox.Visible = False
        Me.Text = String.Format("About {0}", ApplicationTitle)
        ' Initialize all of the text displayed on the About Box.
        ' TODO: Customize the application's assembly information in the "Application" pane of the project 
        '    properties dialog (under the "Project" menu).
        Me.LabelProductName.Text = My.Application.Info.ProductName
        Me.LabelVersion.Text = String.Format("Version {0}", My.Application.Info.Version.ToString)
        Me.LabelCopyright.Text = My.Application.Info.Copyright
        Me.LabelCompanyName.Text = My.Application.Info.CompanyName
        'Me.TextBoxDescription.Text = My.Application.Info.Description
        Me.TextBoxDescription.Text = "dll versions:"
        Dim a As Assembly = Assembly.GetExecutingAssembly
        For Each an As AssemblyName In a.GetReferencedAssemblies
            If dllList.Contains(an.Name.ToLower) Then
                Me.TextBoxDescription.Text &= vbCrLf & an.Name & ":" & an.Version.ToString
            End If
        Next
        Me.TextBoxDescription.Text &= vbCrLf & "Contributors: J.J. Shaffer, K5NER"
#If 0 Then
        Me.TextBoxDescription.Text = "dll versions:" & vbCrLf & _
            "Escapes.dll: " & Escapes.Escapes.Version.ToString & vbCrLf & _
            "HamBands.dll: " & HamBands.Bands.Version.ToString & vbCrLf & _
            "JJLogio.dll: " & JJLogIO.LogIO.Version.ToString & vbCrLf & _ 
            "JJLogLib.dll: " & JJLogLib.Logs.Version.ToString & vbCrLf & _
            "RadioBoxes.dll: " & RadioBoxes.MainBox.Version.ToString & vbCrLf & _
            "Radios.dll: " & Radios.AllRadios.Version.ToString
#End If
    End Sub

    Private Sub OKButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles OKButton.Click
        Me.Close()
    End Sub

End Class

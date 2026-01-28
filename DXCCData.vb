Imports JJCountriesDB
Imports JJTrace

''' <summary> Countries data object </summary>
Friend Class DXCCData
    Private Const fileBaseName As String = "DXList.txt"
    Private countryDB As CountriesDB
    Friend countryRecs() As CountriesDB.CountryInfo
    ''' <summary> ReadOnly array of country names </summary>
    ''' <returns>string array</returns>
    Friend ReadOnly Property CountryNames As String()
        Get
            If countryRecs Is Nothing Then
                Return {""}
            End If
            Dim n(countryRecs.Length - 1) As String
            For i As Integer = 0 To countryRecs.Length - 1
                n(i) = countryRecs(i).country
            Next
            Return n
        End Get
    End Property
    ''' <summary> Latitude and Longitude of the first country </summary>
    ''' <returns>Latitude/Longitude as a string</returns>
    Friend ReadOnly Property FirstLatLong As String
        Get
            If countryRecs Is Nothing Then
                Return ""
            End If
            Return countryRecs(0).latitude & "/" & countryRecs(0).longitude
        End Get
    End Property

    Friend Sub New()
        Dim fn As String = ProgramDirectory & "\" & fileBaseName
        Try
            countryDB = New CountriesDB()
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
            countryDB = Nothing
        End Try
    End Sub

    ''' <summary> Get the countries for this call </summary>
    ''' <param name="cs">call sign string</param>
    ''' <returns>true on success</returns>
    Friend Function LookupByCall(ByVal cs As String) As Boolean
        countryRecs = Nothing
        Dim cs0 As String = cs.Trim
        If (countryDB Is Nothing) Or (cs0 = "") Or (cs0 = " ") Then
            Return False
        End If
        countryRecs = countryDB.CountryLookup(cs0)
        If countryRecs Is Nothing Then
            Return False
        End If
        Return True
    End Function

    ''' <summary>
    ''' Case independent country lookup
    ''' </summary>
    ''' <param name="name">name string</param>
    ''' <returns>true on success</returns>
    Friend Function LookupByName(ByVal name As String) As Boolean
        countryRecs = Nothing
        Dim name0 As String = name.Trim.ToUpper
        If (countryDB Is Nothing) Or (name0 = "") Or (name0 = " ") Then
            Return False
        End If
        For i As Integer = 0 To countryDB.Countries.Length - 1
            Dim ci As CountriesDB.CountryInfo = countryDB.Countries(i)
            If name0 = ci.country.ToUpper Then
                ReDim countryRecs(0)
                countryRecs(0) = ci
                Return True
            End If
        Next
        Return False
    End Function
End Class

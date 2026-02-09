''' <summary>
''' Unified result from any callbook lookup source (QRZ, HamQTH, etc.).
''' LogPanel uses this class regardless of which service provided the data.
''' </summary>
Public Class CallbookResult
    ''' <summary>"QRZ" or "HamQTH"</summary>
    Public Property Source As String = ""

    ''' <summary>
    ''' First name / handle (ham standard). QRZ: fname field, HamQTH: nick field.
    ''' Hams exchange first names on the air — this is what goes in the Name box.
    ''' </summary>
    Public Property Name As String = ""

    ''' <summary>City (QTH).</summary>
    Public Property QTH As String = ""

    ''' <summary>US state or province.</summary>
    Public Property State As String = ""

    ''' <summary>Maidenhead grid square.</summary>
    Public Property Grid As String = ""

    ''' <summary>Country name.</summary>
    Public Property Country As String = ""
End Class

using System.Windows;
using System.Windows.Controls;
using HamQTHLookup;
using JJCountriesDB;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// WPF replacement for the WinForms StationLookup form.
/// Modal dialog for looking up amateur radio station information.
/// </summary>
public partial class StationLookupWindow : Window
{
    private CallbookLookup? hamqthLookup;
    private QrzLookup.QrzCallbookLookup? qrzLookup;
    private CountriesDB? countriesdb;
    private string lookupSource = "";
    private string operatorCountry = "";

    /// <summary>
    /// Built-in fallback credentials for HamQTH lookups.
    /// </summary>
    private const string DefaultHamqthID = "JJRadio";
    private const string DefaultHamqthPassword = "JJRadio";

    // Caller-provided operator settings (set before ShowDialog).
    private readonly string _callbookSource;
    private readonly string _callbookUsername;
    private readonly string _callbookPassword;
    private readonly string _operatorCallSign;

    /// <summary>
    /// Creates a StationLookupWindow with operator callbook settings.
    /// Pass the operator's callbook source, username, password, and call sign.
    /// If any are empty, falls back to built-in HamQTH account.
    /// </summary>
    public StationLookupWindow(string callbookSource = "", string callbookUsername = "",
                                string callbookPassword = "", string operatorCallSign = "")
    {
        _callbookSource = callbookSource;
        _callbookUsername = callbookUsername;
        _callbookPassword = callbookPassword;
        _operatorCallSign = operatorCallSign;
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Use operator's callbook settings if available; fall back to built-in HamQTH account.
        string source = "HamQTH";
        string username = DefaultHamqthID;
        string password = DefaultHamqthPassword;

        if (_callbookSource != "None" && _callbookSource != "" &&
            _callbookUsername != "" && _callbookPassword != "")
        {
            source = _callbookSource;
            username = _callbookUsername;
            password = _callbookPassword;
        }

        lookupSource = source;
        switch (source)
        {
            case "QRZ":
                qrzLookup = new QrzLookup.QrzCallbookLookup(username, password);
                qrzLookup.CallsignSearchEvent += QrzResultHandler;
                break;
            default: // "HamQTH" or fallback
                hamqthLookup = new CallbookLookup(username, password);
                hamqthLookup.CallsignSearchEvent += HamQTHResultHandler;
                break;
        }

        countriesdb = new CountriesDB();

        // Look up operator's own country for comparison in SR announcements.
        if (!string.IsNullOrEmpty(_operatorCallSign))
        {
            var opRec = countriesdb.LookupByCall(_operatorCallSign);
            if (opRec != null)
                operatorCountry = opRec.Country ?? "";
        }

        CallsignBox.Focus();
    }

    public void Finished()
    {
        hamqthLookup?.Finished();
        qrzLookup?.Finished();
    }

    private void LookupButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CallsignBox.Text))
        {
            CallsignBox.Focus();
            return;
        }

        // Clear result fields.
        NameBox.Text = "";
        QTHBox.Text = "";
        StateBox.Text = "";
        CountryBox.Text = "";
        LatlongBox.Text = "";
        CQBox.Text = "";
        ITUBox.Text = "";
        GMTBox.Text = "";

        // Callbook lookup (async — results arrive via handler).
        if (qrzLookup != null && qrzLookup.CanLookup)
        {
            NameBox.Focus();
            qrzLookup.LookupCall(CallsignBox.Text);
        }
        else if (hamqthLookup != null && hamqthLookup.CanLookup)
        {
            NameBox.Focus();
            hamqthLookup.LookupCall(CallsignBox.Text);
        }
        else
        {
            CountryBox.Focus();
        }

        // Country database lookup (synchronous, offline).
        Record? rec = countriesdb?.LookupByCall(CallsignBox.Text);
        if (rec != null)
        {
            CountryBox.Text = rec.Country ?? "";
            if (!string.IsNullOrEmpty(rec.CountryID))
                CountryBox.Text += " (" + rec.CountryID + ")";
            LatlongBox.Text = rec.Latitude + "/" + rec.Longitude;
            CQBox.Text = rec.CQZone ?? "";
            ITUBox.Text = rec.ITUZone ?? "";
            GMTBox.Text = rec.TimeZone ?? "";
        }
    }

    private void HamQTHResultHandler(CallbookLookup.HamQTH item)
    {
        if (item?.search == null) return;

        Dispatcher.BeginInvoke(() =>
        {
            string name = item.search.nick ?? "";
            string qth = item.search.qth ?? "";
            string grid = item.search.grid ?? "";
            string state = item.search.State ?? "";
            string country = item.search.country ?? "";

            NameBox.Text = name;
            QTHBox.Text = qth;
            if (grid != "") QTHBox.Text += " (" + grid + ")";
            StateBox.Text = state;

            AnnounceResult(name, qth, state, country);
        });
    }

    private void QrzResultHandler(QrzLookup.QrzCallbookLookup.QrzDatabase result)
    {
        if (result?.Callsign == null) return;

        Dispatcher.BeginInvoke(() =>
        {
            string name = result.Callsign.FirstName ?? "";
            string qth = result.Callsign.City ?? "";
            string grid = result.Callsign.Grid ?? "";
            string state = result.Callsign.State ?? "";
            string country = result.Callsign.Country ?? "";

            NameBox.Text = name;
            QTHBox.Text = qth;
            if (grid != "") QTHBox.Text += " (" + grid + ")";
            StateBox.Text = state;

            AnnounceResult(name, qth, state, country);
        });
    }

    private void AnnounceResult(string name, string qth, string state, string country)
    {
        var parts = new List<string>();
        if (name != "") parts.Add(name);
        if (qth != "") parts.Add(qth);
        if (state != "") parts.Add(state);

        // Include country only when it differs from operator's country (DX station).
        if (country != "" && !country.Equals(operatorCountry, StringComparison.OrdinalIgnoreCase))
            parts.Add(country);

        if (parts.Count > 0)
        {
            string msg = lookupSource + ": " + string.Join(", ", parts);
            ScreenReaderOutput.Speak(msg, true);
        }
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void TextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Text.Length > 0)
            tb.SelectAll();
    }
}

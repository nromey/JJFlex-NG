using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using JJTrace;

namespace JJCountriesDB
{
    public partial class CountriesDB
    {
        private const string dbName = "cty_wt.dat";
        public List<Record> CountryRecords;
        internal List<KeyData> TheKeys;
        // Map first letter to TheKeys index.
        Dictionary<char, int> hasher;

        /// <summary> Not a K2DI countries text file </summary>
        public class NotCountries : Exception
        {
            /// <summary> Not a K2DI countries text file </summary>
            public NotCountries() : base("This file contains no country data.") { }
        }

        // The main line has 8 fields that are the first 8 fields in Record.
        private const int mainLineNFields = 8;
        private const string countryIDHdr = "# ADIF ";
        private const string emptyComment = "#";
        private string initialRec = emptyComment; // ignore on first read.
        /// <summary>
        /// Read records from the country prefixes database.
        /// </summary>
        /// <param name="sr">the streamReader object</param>
        /// <returns>a Record object</returns>
        /// <remarks>
        /// The batch of records for each country consists of a leading record with country number, "# ADIF nnn"
        /// Followed by a record containing country info.
        /// Followed by other prefixes, with interspersed comment records, "#".
        /// </remarks>
        private Record readRecord(StreamReader sr)
        {
            Record rv = new Record();
            // Find a new batch of records.
            while (!string.IsNullOrEmpty(initialRec) &&
                !((initialRec.Length > countryIDHdr.Length) &&
                  (initialRec.Substring(0, countryIDHdr.Length) == countryIDHdr)))
            {
                initialRec = sr.ReadLine();
            }
            if (string.IsNullOrEmpty(initialRec))
            {
                // EOF.
                rv.State = Record.states.EOF;
                return rv;
            }

            // Get countryID.
            rv[(int)Record.fieldIDs.countryID] = initialRec.Substring(countryIDHdr.Length);

            // Get main fields.
            string rec = sr.ReadLine();
            if (string.IsNullOrEmpty(rec))
            {
                // treate like EOF.
                rv.State = Record.states.EOF;
                return rv;
            }
            string[] flds = rec.Split(new char[] {':'},StringSplitOptions.RemoveEmptyEntries);
            if (flds.Length != mainLineNFields)
            {
                Tracing.TraceLine("readRecord fields don't match:" + rec, TraceLevel.Error);
                // main record's fields don't match.
                initialRec = rec;
                rv.State = Record.states.bad;
                return rv;
            }
            for (int i = 0; i < mainLineNFields; i++)
            {
                // This does some formatting and builds the initial key.
                rv[i] = flds[i];
            }

            // It's good at this point.
            rv.State = Record.states.good;

            // Get other prefixes.
            bool skipThisRecord = false;
            while (true)
            {
                initialRec = sr.ReadLine().Trim();
                if (string.IsNullOrEmpty(initialRec))
                {
                    // EOF, last entry.
                    rv.State = Record.states.goodEOF;
                    return rv;
                }
                if ((initialRec.Length > countryIDHdr.Length) &&
                    (initialRec.Substring(0, countryIDHdr.Length) == countryIDHdr))
                {
                    // end of this batch.
                    return rv;
                }
                // Ignore these.
                if (initialRec[0] == '#') continue;

                // Get other prefixes.
                int i = initialRec.IndexOf(';');
                if (i != -1)
                {
                    initialRec = initialRec.Substring(0, i);
                    skipThisRecord = true; // finished after this.
                }
                flds = initialRec.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                // comma-separated prefixes.
                for (i = 0; i < flds.Length; i++)
                {
                    if (string.IsNullOrEmpty(rv[(int)Record.fieldIDs.otherPrefix])) rv[(int)Record.fieldIDs.otherPrefix] = flds[i];
                    else rv[(int)Record.fieldIDs.otherPrefix] += ',' + flds[i];
                }
                if (skipThisRecord)
                {
                    // Last record in this batch.
                    initialRec = emptyComment;
                    break;
                }
            }
            // initialRec will be the next line processed.

            return rv;
        }

        public CountriesDB()
        {
            Tracing.TraceLine("CountriesDB constructor", TraceLevel.Info);
            StreamReader sr = null;
            CountryRecords = new List<Record>();
            TheKeys = new List<KeyData>();
            List<KeyData> myKeys = new List<KeyData>();
            hasher = new Dictionary<char, int>();
            try
            {
                sr = new StreamReader(dbName);
                while (true)
                {
                    Record rr = readRecord(sr);
                    if ((rr.State == Record.states.good) | (rr.State == Record.states.goodEOF))
                    {
                        rr.MainPrefix = rr.MainPrefix.ToUpper();
                        if (!string.IsNullOrEmpty(rr.OtherPrefix)) rr.OtherPrefix = rr.OtherPrefix.ToUpper();
                        CountryRecords.Add(rr);

                        // Get prefixes
                        List<KeyData> keyList = Record.buildKeys(rr.MainPrefix);
                        keyList.AddRange(Record.buildKeys(rr.OtherPrefix));
                        foreach (KeyData k in keyList)
                        {
                            k.Record = rr;
                            myKeys.Add(k);
                        }
                    }
                    if ((rr.State == Record.states.goodEOF) | (rr.State == Record.states.EOF))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CountriesDB exception:" + ex.Message, TraceLevel.Error);
                throw ex;
            }
            finally
            {
                if (sr != null) sr.Dispose();
            }
            Tracing.TraceLine("CountriesDB #read:" + CountryRecords.Count.ToString(), TraceLevel.Info);
            if (CountryRecords.Count == 0) throw new CountriesDB.NotCountries();

            // Now sort TheKeys.
            myKeys.Sort(KeyData.SortProc);

            // eliminate dups and build the hash.
            TheKeys.Add(myKeys[0]);
            hasher.Add(myKeys[0].ID, 0);
            for (int i = 1; i < myKeys.Count; i++)
            {
                if (myKeys[i].Key.ToString() != myKeys[i-1].Key.ToString())
                {
                    // different key.
                    TheKeys.Add(myKeys[i]);
                    int j = TheKeys.Count - 1;
                    // test code if (TheKeys[j].ID == 'K') continue;
                    // New hash entry if ID changed.
                    if (TheKeys[j - 1].ID != TheKeys[j].ID)
                    {
                        try { hasher.Add(TheKeys[j].ID, j); }
                        catch { Tracing.TraceLine("CountriesDB:hasher.add:" + TheKeys[j - 1].ID.ToString() + ' ' + TheKeys[j].ID.ToString(), TraceLevel.Error); }
                    }
                }
            }

            // Sort the countries by DXCC number.
            CountryRecords.Sort(Record.SortByDXCC);
        }

        // Used for slash processing.
        private static Regex slashLeft = new Regex(".+.\\/");
        private static Regex slashRight = new Regex("\\/..+$");
        /// <summary>
        /// Lookup a country by call.
        /// </summary>
        /// <param name="cs">call sign</param>
        /// <returns>Record object</returns>
        public Record LookupByCall(string cs)
        {
            Tracing.TraceLine("LookupByCall:" + cs, TraceLevel.Info);
            int id;
            // See if form xxx/yyy.
            if (slashRight.IsMatch(cs))
            {
                // favor the shorter of the sides.
                id = cs.IndexOf('/') + 1; // 1 - length
                // Change the form xxx/yyy to xxx or vice versa.
                cs = (id > (cs.Length / 2)) ? slashLeft.Replace(cs, "") : slashRight.Replace(cs, "");
            }
            if (string.IsNullOrEmpty(cs) | (CountryRecords == null)) return null;
            cs = cs.ToUpper();

            // Hash to starting point.
            if (!hasher.TryGetValue(cs[0], out id))
            {
                Tracing.TraceLine("LookupByCall:no hash found.", TraceLevel.Info);
                //return null;
                id = 0;
            }

            bool match = false;
            for (; (id < TheKeys.Count); id++)
            {
                match = TheKeys[id].Key.IsMatch(cs);
                if (match) break;
            }

            Record rv = null;
            if (match)
            {
                // Check for alternate zones specified in KeyData.
                if (TheKeys[id].AlternateZones)
                {
                    // Return a copy of the record.
                    rv = new Record(TheKeys[id].Record);
                    if (!string.IsNullOrEmpty(TheKeys[id].CQZone)) rv[(int)Record.fieldIDs.cq] = TheKeys[id].CQZone;
                    if (!string.IsNullOrEmpty(TheKeys[id].ItUZone)) rv[(int)Record.fieldIDs.itu] = TheKeys[id].ItUZone;
                }
                else rv = TheKeys[id].Record;
            }
            return rv;
        }

        /// <summary>
        /// Country Lookup By DXCC.
        /// </summary>
        /// <param name="arg">string argument - DXCC number</param>
        /// <returns>Record object or null</returns>
        public Record LookupByDXCC(string arg)
        {
            Tracing.TraceLine("LookupByName:" + arg, TraceLevel.Info);
            if (string.IsNullOrEmpty(arg)) return null;
            //name = name.ToUpper();
            Record rv = null;
            int L = 0;
            int H = CountryRecords.Count - 1;
            int M = (int)(((Single)(H - L) / 2) + 0.5);
            int d = 0;
            int sanity = H;
            while (((M >= L) & (M <= H)) & (rv == null) & (sanity-- > 0))
            {
                int c = String.Compare(arg, CountryRecords[M].CountryID);
                if (c == 0) rv = CountryRecords[M];
                else if (c > 0)
                {
                    L = M;
                    d = (int)(((Single)(H - M) / 2) + 0.5); // posative
                }
                else
                {
                    H = M;
                    d = (int)(((Single)(L - M) / 2) - 0.5); // negative
                }
                M += d;
            }
            return rv;
        }
    }
}

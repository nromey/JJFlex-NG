using System;
using System.Linq;

namespace JJCountriesDB
{
    public partial class CountriesDB
    {
        public class CountryInfo
        {
            public string mainPrefix { get; internal set; }
            public string ituPrefix { get; internal set; }
            public string otherPrefix { get; internal set; }
            public string country { get; internal set; }
            public string continents { get; internal set; }
            public string itu { get; internal set; }
            public string cq { get; internal set; }
            public string timezone { get; internal set; }
            public string latitude { get; internal set; }
            public string longitude { get; internal set; }
        }

        private static CountryInfo FromRecord(Record record)
        {
            if (record == null) return null;

            return new CountryInfo
            {
                mainPrefix = record.MainPrefix,
                // The legacy CountryInfo did not distinguish ITU prefix from main prefix,
                // so reuse the same value to keep callers happy.
                ituPrefix = record.MainPrefix,
                otherPrefix = record.OtherPrefix,
                country = record.Country,
                continents = record.Continent,
                itu = record.ITUZone,
                cq = record.CQZone,
                timezone = record.TimeZone,
                latitude = record.Latitude,
                longitude = record.Longitude
            };
        }

        public CountryInfo[] CountryLookup(string cs)
        {
            var match = LookupByCall(cs);
            return match == null ? null : new[] { FromRecord(match) };
        }

        public CountryInfo[] Countries
        {
            get
            {
                if (CountryRecords == null || CountryRecords.Count == 0) return Array.Empty<CountryInfo>();
                return CountryRecords.Select(FromRecord)
                    .Where(ci => ci != null)
                    .ToArray();
            }
        }
    }
}

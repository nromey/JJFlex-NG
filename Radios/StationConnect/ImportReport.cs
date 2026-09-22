using System;

namespace Radios.StationConnect
{
    /// <summary>
    /// What to tell the operator after a database import, keeping two facts
    /// apart: whether the IMPORT completed, and whether the station that
    /// followed it could be CONFIRMED. Track G chose "import failed" from
    /// StationEstablished, so a successful import on the fail-closed
    /// defaults reported failure (review section 8, first bullet).
    /// </summary>
    public sealed class ImportReport
    {
        public bool ImportCompleted;
        public bool StationEstablished;
        public StationOutcome StationOutcome;

        /// <summary>The message: the import's own result first, then the
        /// station's when it could not be established, in the operator's
        /// terms.</summary>
        public string Message
        {
            get
            {
                if (!ImportCompleted) return Lexicon.Get("settings.flexdb.import_failed");
                if (StationEstablished) return Lexicon.Get("settings.flexdb.imported");
                return Lexicon.Get("settings.flexdb.imported_station_unconfirmed");
            }
        }

        public static ImportReport For(bool importCompleted, StationResult station) => new ImportReport
        {
            ImportCompleted = importCompleted,
            StationEstablished = station != null && station.StationEstablished,
            StationOutcome = station?.Outcome ?? StationOutcome.Unconfirmed,
        };
    }
}

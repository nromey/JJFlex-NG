using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Timers;
using System.Windows.Forms;
using System.Xml.Serialization;
using JJTrace;
using adif;
using HamQTHLookup;
using SKCC;

namespace JJLogLib
{
    public static class Logs
    {
        /// <summary>
        /// JJLogio.dll version
        /// </summary>
        public static Version Version
        {
            get
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                AssemblyName asmName = asm.GetName();
                return asmName.Version;
            }
        }

        /// <summary>
        /// HamQTH ID, usually callsign.
        /// </summary>
        public static string HamqthID;
        /// <summary>
        /// HamQTH Password
        /// </summary>
        public static string HamqthPassword;

        /// <summary>
        /// The LogElement defines a log form.
        /// </summary>
        /// <remarks>LogProc contains common processing elements.</remarks>
        public class LogElement : LogProc
        {
            /// <summary>
            /// Form's name
            /// </summary>
            public string Name;
            /// <summary>
            /// Dictionary mapping ADIF tag to the LogField class for each field.
            /// </summary>
            public Dictionary<string, LogField> Fields;
            private string recVersion = null;
            /// <summary>
            /// Log record version string.
            /// If null, the log records have no version.
            /// If non-null, the first field of each record is the version with
            /// the internal tag, iADIF_Version.
            /// </summary>
            public string RecordVersion
            {
                get { return recVersion; }
                internal set { recVersion = value; }
            }
            public delegate string ConversionDel(string record);
            /// <summary>
            /// Routine to handle any data format conversions.
            /// </summary>
            public ConversionDel RecordConverter = null;
            public int NumberLogged;
            public int NumberDisplayed;
            /// <summary>
            /// Form control
            /// </summary>
            public Control TheForm;
            internal System.Type Control;

            /// <summary>
            /// Called when record is to be written.
            /// </summary>
            /// <param name="fields">dictionary of fields</param>
            /// <param name="oldFields">old fields</param>
            public delegate void WriteEntryDel(
                Dictionary<string, LogFieldElement> fields,
                Dictionary<string, LogFieldElement> oldFields);
            public WriteEntryDel WriteEntry;

            /// <summary>
            /// Special case for delayed SKCC info lookup, see callLookupDoneHandler in logproc.cs.
            /// </summary>
            public SKCCType SKCCDB = null;
            public bool DelayedSKCCLookup { get { return (SKCCDB != null); } }

            public LogElement(string n, System.Type t)
            {
                Name = n;
                // Most fields are setup when the log is selected, see GetLog().
                Control = t;
            }

            public LogElement(LogElement el)
            {
                Name = el.Name;
                Control = el.Control;
            }

            /// <summary>
            /// Add a field to this log.
            /// </summary>
            /// <param name="fld">the LogField item</param>
            internal void addField(LogField fld)
            {
                Fields.Add(fld.ADIFTag, fld);
                if (fld.IsDisplayed) NumberDisplayed += 1;
                if (fld.IsLogged) NumberLogged += 1;
            }

            /// <summary>
            /// Close the form if we're using one.
            /// </summary>
            public void Close()
            {
                Tracing.TraceLine("LogElement.Close", TraceLevel.Info);
                if (TheForm != null)
                {
                    try
                    {
                        TheForm.Dispose();
                        TheForm = null;
                    }
                    catch (Exception ex)
                    { Tracing.ErrMessageTrace(ex); }
                }
            }
        }

        public const int DefaultLogID = 0;
        public const string DefaultLogname = "DefaultLog";
        public const string DefaultLogname4skcc = "DefaultLog for SKCC";
        public const string SKCCConfigFile = "skcc.txt";
        /// <summary>
        /// The list of LogElements.
        /// </summary>
        private static Dictionary<string, LogElement> lognameDictionary =
            new Dictionary<string, LogElement>()
            {
                { DefaultLogname, new LogElement(DefaultLogname, typeof(DefaultLog))},
                { DefaultLogname4skcc, new LogElement(DefaultLogname4skcc, typeof(DefaultLog))},
                { "Field Day Log", new LogElement("Field Day Log", typeof(FieldDay))},
                { "NA Sprint Log", new LogElement("NA Sprint Log", typeof(NASprint))},
                // SKCCWESLog removed in Sprint 4 — Logging Mode replaces contest-specific forms.
                // The SKCCWESLog.cs and SKCCWESLog.designer.cs files are still in the project
                // but no longer registered. Remove the files in a future cleanup pass.
            };
        /// <summary>
        /// Get an string array containing the log names.
        /// </summary>
        /// <returns>string array</returns>
        public static string[] LogNames()
        {
            string[] rv = new string[lognameDictionary.Values.Count];
            int i = 0;
            foreach (string key in lognameDictionary.Keys)
            {
                rv[i++] = key;
            }
            return rv;
        }

        /// <summary>
        /// For persistent items such as scores and stats.
        /// </summary>
        internal static object persist;

        public delegate void ShowStatsDel();
        /// <summary>
        /// Routine to show log statistics.
        /// </summary>
        public static ShowStatsDel ShowStats;

        /// <summary>
        /// Called when configuring a log file.
        /// </summary>
        /// <param name="id">HamQTH user id or null</param>
        /// <param name="password">HamQTH password or null</param>
        public static void NewLog(string id,string password)
        {
            Tracing.TraceLine("NewLog:" + id + ' ' + password, TraceLevel.Info);
            Done(); // perform any cleanup for the prior log.
            persist = null;
            ShowStats = null;
            NewLoginInfo(id, password);
        }

        /// <summary>
        /// Called when finished with the Logs object.
        /// </summary>
        public static void Done()
        {
            Tracing.TraceLine("Logs.Done", TraceLevel.Info);
            if (lookup != null) lookup.Finished();
        }

        /// <summary>
        /// Configuration directory
        /// </summary>
        internal static string ConfigDirectory;

        /// <summary>
        /// Return the LogElement
        /// </summary>
        /// <param name="name">of the log form</param>
        /// <param.name="configDir">base configuration directory</param>
        /// <returns>null on error</returns>
        public static LogElement GetLog(string name, string configDir)
        {
            ConfigDirectory = configDir;

            LogElement le = null;
            if (lognameDictionary.TryGetValue(name, out le))
            {
                // Return a copy of the element.
                le = new LogElement(le);
                Tracing.TraceLine("GetLog:" + le.Name, TraceLevel.Info);
                le.Fields = new Dictionary<string, LogField>();
                le.NumberLogged = le.NumberDisplayed = 0;
                try
                {
                    le.TheForm = (Control)Activator.CreateInstance(le.Control, new object[] { le });
                    le.procSetup(le);
                }
                catch (Exception ex)
                {
                    Tracing.ErrMessageTrace(ex, true);
                    le = null;
                }
            }
            else
            {
                Tracing.TraceLine("GetLog:bad name:" + name, TraceLevel.Error);
                le = null;
            }
            return le;
        }

        // region - static search items.
        #region lookup
        internal static CallbookLookup lookup;

        /// <summary>
        /// User interface choices, yes or no.
        /// </summary>
        public enum LookupChoices
        {
            Yes,
            No
        }
        public const LookupChoices DefaultLookupChoice = LookupChoices.No;

        /// <summary>
        /// Login info for this user.
        /// Must be called even if user has no HamQTH id or password.
        /// </summary>
        /// <param name="id">string user id</param>
        /// <param name="password">string password</param>
        private static void NewLoginInfo(string id, string password)
        {
            Tracing.TraceLine("newLoginInfo:" + id, TraceLevel.Info);
            lookup = new CallbookLookup(id, password);
        }
        #endregion
    }
}

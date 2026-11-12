using System;
using System.IO;
using System.Xml.Serialization;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The license Settings edits when no radio has been connected since
    /// launch, so Home holds no license of its own. Settings are intents: the
    /// operator's own license is loaded from the one file startup reads, and a
    /// change is saved there, to be used by the next connect.
    /// </summary>
    /// <remarks>
    /// <para><b>An unchanged license is never saved here.</b> The license
    /// file's absence is how the first connect knows this is the operator's
    /// first run (<see cref="LicenseConfig.LoadOrCarry"/>'s <c>firstRun</c>),
    /// and it then asks for a license class. Writing the defaults back just
    /// because Settings was opened and closed would silence that question for
    /// good.</para>
    /// <para><b>With no operator chosen there is no file to save to</b>, so a
    /// change is not saved, and the commit says so, the way it does for push
    /// to talk with no radio.</para>
    /// </remarks>
    public sealed class LicenseWithoutRadio
    {
        private readonly string? _radiosFolder;
        private readonly string? _operatorName;
        private string _asSaved;

        /// <summary>The license Settings edits.</summary>
        public LicenseConfig License { get; }

        /// <summary>True when there is an operator, and so a file, to save to.</summary>
        public bool HasAHome => _radiosFolder != null && _operatorName != null;

        private LicenseWithoutRadio(LicenseConfig license, string? radiosFolder, string? operatorName)
        {
            License = license;
            _radiosFolder = radiosFolder;
            _operatorName = operatorName;
            _asSaved = Snapshot(license);
        }

        /// <summary>
        /// The operator's license, read as startup reads it: the settings
        /// root's <c>Radios</c> folder, or the root's older copy. With no
        /// operator (or no settings root) a default license nothing will save.
        /// </summary>
        public static LicenseWithoutRadio Open(string? settingsRoot, string? operatorName)
        {
            if (string.IsNullOrEmpty(settingsRoot) || string.IsNullOrEmpty(operatorName))
                return new LicenseWithoutRadio(new LicenseConfig(), null, null);

            string radios = Path.Combine(settingsRoot, "Radios");
            var license = LicenseConfig.LoadOrCarry(radios, settingsRoot, operatorName, out _);
            return new LicenseWithoutRadio(license, radios, operatorName);
        }

        /// <summary>For the operator in use now (<see cref="OperatorChange.Current"/>).</summary>
        public static LicenseWithoutRadio ForCurrentOperator()
        {
            var op = OperatorChange.Current;
            return Open(op.ConfigDirectory, op.OperatorName);
        }

        /// <summary>What a commit did with the license.</summary>
        public enum Outcome
        {
            /// <summary>Nothing changed, so nothing was written.</summary>
            Unchanged,
            /// <summary>Changed, and saved to the operator's license file.</summary>
            Saved,
            /// <summary>Changed, and there is no operator to save it for.</summary>
            NotSaved,
        }

        /// <summary>
        /// Save the license if, and only if, it changed since it was opened
        /// (or last saved here).
        /// </summary>
        public Outcome Commit()
        {
            string now = Snapshot(License);
            if (string.Equals(now, _asSaved, StringComparison.Ordinal)) return Outcome.Unchanged;
            if (!HasAHome)
            {
                Tracing.TraceLine("Settings: a license change with no operator to keep it; not saved",
                    System.Diagnostics.TraceLevel.Info);
                return Outcome.NotSaved;
            }
            License.Save(_radiosFolder!, _operatorName!);
            _asSaved = now;
            Tracing.TraceLine("Settings: license saved for " + _operatorName + " with no radio connected; the next connect uses it",
                System.Diagnostics.TraceLevel.Info);
            return Outcome.Saved;
        }

        /// <summary>
        /// The sentence the commit adds for an outcome, or null when there is
        /// nothing to say (the license's own save is reported with the others).
        /// </summary>
        public static string? CommitNote(Outcome outcome) =>
            outcome == Outcome.NotSaved ? Lexicon.Get("settings.commit.license_not_saved") : null;   // DRAFT

        private static string Snapshot(LicenseConfig license)
        {
            try
            {
                using var sw = new StringWriter();
                new XmlSerializer(typeof(LicenseConfig)).Serialize(sw, license);
                return sw.ToString();
            }
            catch { return ""; }
        }
    }
}

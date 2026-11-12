#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Whether the operator in use is offered a save of their setup to the
    /// radio when they disconnect: one setting per operator (#1113).
    /// </summary>
    /// <remarks>
    /// <para><b>Ruled by Noel 2026-11-12 08:05 (J3): per operator.</b> Until
    /// then it lived only in the Windows account's audioConfig.xml, so after
    /// #1073 made every "don't show again" follow the operator, the disconnect
    /// question's "don't ask" box was the one choice still shared by every
    /// operator on the account.</para>
    /// <para><b>Each operator's own file</b>, under the settings root, named
    /// the way every per-operator file is: the operator's unique name, then
    /// <see cref="OperatorFileSuffix"/>. Read on every ask, so it follows an
    /// operator change with nothing to register, as the advisory store does.
    /// No file means the default, off (#759, #499).</para>
    /// <para><b>The account value</b> (<see cref="AccountValue"/>, the old
    /// setting in audioConfig.xml) is the one in use while no operator has been
    /// chosen, and is what each operator that existed at the upgrade starts
    /// from, once (<see cref="CarryOverAtUpgrade(IEnumerable{string}, bool)"/>).
    /// An operator added later starts at the default, the same rule as the
    /// silenced messages (#1106).</para>
    /// </remarks>
    public static class StationSaveOffer
    {
        /// <summary>The per-operator file's suffix, after the operator's unique name.</summary>
        internal const string OperatorFileSuffix = "_stationSaveOffer.json";

        /// <summary>Written once the account value has been carried to the operators listed at the upgrade.</summary>
        internal const string CarriedOverMarkerFileName = "stationSaveOffer-carried-over.json";

        private static readonly object _gate = new();
        private static bool _accountValue;

        /// <summary>One operator's file under a settings root.</summary>
        internal static string OperatorFile(string root, string operatorName) =>
            Path.Combine(root, operatorName + OperatorFileSuffix);

        /// <summary>The marker under a settings root.</summary>
        internal static string MarkerFile(string root) => Path.Combine(root, CarriedOverMarkerFileName);

        /// <summary>
        /// The Windows account's value, from audioConfig.xml: the one in use
        /// while no operator is chosen. Set where that file is applied; never
        /// written into an operator's file except by the carry-over.
        /// </summary>
        public static bool AccountValue
        {
            get { lock (_gate) return _accountValue; }
            set { lock (_gate) _accountValue = value; }
        }

        /// <summary>Whether the operator in use asked to be offered the save.</summary>
        public static bool Offer
        {
            get
            {
                string? op = OperatorChange.Current.OperatorName;
                return string.IsNullOrEmpty(op) ? AccountValue : Read(RadioConfig.AppDataRoot, op);
            }
        }

        /// <summary>
        /// The operator in use chose: their own file, written at once. With no
        /// operator chosen, the account value (audioConfig.xml keeps it, as it
        /// always has).
        /// </summary>
        public static void Choose(bool offer)
        {
            string? op = OperatorChange.Current.OperatorName;
            if (string.IsNullOrEmpty(op)) { AccountValue = offer; return; }
            Write(RadioConfig.AppDataRoot, op, offer);
        }

        /// <summary>One operator's setting; the default (off) with no file or an unreadable one.</summary>
        internal static bool Read(string root, string operatorName)
        {
            string path = OperatorFile(root, operatorName);
            try
            {
                if (!File.Exists(path)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("offer", out JsonElement v)
                    && v.ValueKind == JsonValueKind.True;
            }
            catch (Exception ex)
            {
                // Off is the default and the safe direction: the question
                // simply is not asked.
                Tracing.TraceLine("StationSaveOffer: could not read " + path + ": " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>Write one operator's setting, whole or not at all.</summary>
        internal static void Write(string root, string operatorName, bool offer)
        {
            string path = OperatorFile(root, operatorName);
            try
            {
                Directory.CreateDirectory(root);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new { version = 1, offer },
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, path, overwrite: true);
                Tracing.TraceLine("StationSaveOffer: " + operatorName + (offer
                    ? " is offered the save on disconnect" : " is not offered the save on disconnect"),
                    System.Diagnostics.TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("StationSaveOffer: could not save " + path + ": " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
            }
        }

        /// <summary>
        /// At startup, before the operator in use is recorded: the first time
        /// a build with per-operator offers runs, every operator listed now
        /// starts from the account value. Never again after that.
        /// </summary>
        public static void CarryOverAtUpgrade(IEnumerable<string> operatorNames, bool accountValue) =>
            CarryOverAtUpgrade(RadioConfig.AppDataRoot, operatorNames, accountValue);

        /// <returns>True when the carry-over ran now; false when it had run before.</returns>
        internal static bool CarryOverAtUpgrade(string root, IEnumerable<string> operatorNames, bool accountValue)
        {
            if (File.Exists(MarkerFile(root))) return false;
            var names = (operatorNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (string name in names)
                if (!File.Exists(OperatorFile(root, name)))
                    Write(root, name, accountValue);
            UpgradeMarker.Write(MarkerFile(root), names, "StationSaveOffer");
            Tracing.TraceLine("StationSaveOffer: the account's setting (" + (accountValue ? "offer" : "no offer")
                + ") carried to " + names.Count.ToString(CultureInfo.InvariantCulture)
                + " operator(s) listed at the upgrade; operators added later start with no offer",
                System.Diagnostics.TraceLevel.Info);
            return true;
        }
    }
}

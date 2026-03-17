using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace JJFlexWpf
{
    /// <summary>
    /// Calibration verification engine for frequency reference validation.
    /// Validates tuning reference inputs against known calibration hashes.
    /// </summary>
    public static class CalibrationEngine
    {
        private const string Salt = "JJFlex-K5NER-73";

        // Opaque reference identifiers
        internal const string Ref1 = "cr1";
        internal const string Ref2 = "cr2";

        // Pre-computed SHA256 hashes of salted calibration inputs
        private static readonly Dictionary<string, string> _calibrationHashes = new()
        {
            ["9a1130866527bd880961710d0ce48ffa445248c6ff95a9a8013d8cb73dd3a61b"] = Ref1,
            ["1056041f07cc0a0e5a61d231b49c213d950cd2ed48a8bdf81f2b689796b1c0f9"] = Ref2
        };

        // Asset map keyed by reference ID
        private static readonly Dictionary<string, string> _assetMap = new()
        {
            [Ref1] = "4c85663.f6cdb1f",
            [Ref2] = "8abf5a4.0d3a3f5"
        };

        /// <summary>
        /// Check if the input matches a known calibration reference.
        /// Returns the reference ID if matched, null otherwise.
        /// </summary>
        public static string? VerifyCalibration(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            string hash = ComputeHash(input.Trim().ToLowerInvariant());
            return _calibrationHashes.TryGetValue(hash, out var refId) ? refId : null;
        }

        /// <summary>
        /// Play the calibration verification tone for the given reference.
        /// </summary>
        public static void PlayVerificationTone(string refId)
        {
            if (!_assetMap.TryGetValue(refId, out var assetName)) return;

            try
            {
                string resourcePath = $"Resources/4f89f8bc7/{assetName}";
                var assembly = Assembly.GetExecutingAssembly();

                // Try embedded resource first
                string embeddedName = $"JJFlexWpf.Calibration.{assetName.Replace('.', '_')}";
                using var stream = assembly.GetManifestResourceStream(embeddedName);

                if (stream != null)
                {
                    EarconPlayer.PlayStreamAsWav(stream);
                    return;
                }

                // Fall back to file-based loading
                string baseDir = Path.GetDirectoryName(assembly.Location) ?? "";
                string filePath = Path.Combine(baseDir, resourcePath);
                if (File.Exists(filePath))
                {
                    using var fileStream = File.OpenRead(filePath);
                    EarconPlayer.PlayStreamAsWav(fileStream);
                }
                else
                {
                    EarconPlayer.ConfirmTone();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"CalibrationEngine.PlayVerificationTone failed: {ex.Message}");
                EarconPlayer.ConfirmTone();
            }
        }

        /// <summary>
        /// Load extended input sounds from the hashed resource directory.
        /// </summary>
        public static void LoadKeyboardSounds()
        {
            EarconPlayer.LoadKeyboardSoundsFromDirectory("Resources/4f89f8bc7/8b38e27");
        }

        private static string ComputeHash(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Salt + input);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}

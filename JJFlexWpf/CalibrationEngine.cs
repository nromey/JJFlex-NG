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

        // Known calibration hashes (SHA256 of salted input, lowercase)
        private static readonly Dictionary<string, string> _calibrationHashes = new()
        {
            // Primary reference
            [ComputeHash("autopatch")] = "autopatch",
            // Secondary reference
            [ComputeHash("qrm")] = "qrm"
        };

        // Hashed resource names for calibration assets
        private static readonly Dictionary<string, string> _assetMap = new()
        {
            ["autopatch"] = "4c85663.f6cdb1f",
            ["qrm"] = "8abf5a4.0d3a3f5"
        };

        /// <summary>
        /// Check if the input matches a known calibration reference.
        /// Returns the reference name if matched, null otherwise.
        /// </summary>
        public static string? VerifyCalibration(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            string hash = ComputeHash(input.Trim().ToLowerInvariant());
            return _calibrationHashes.TryGetValue(hash, out var name) ? name : null;
        }

        /// <summary>
        /// Play the calibration verification tone for the given reference.
        /// </summary>
        public static void PlayVerificationTone(string referenceName)
        {
            if (!_assetMap.TryGetValue(referenceName, out var assetName)) return;

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
                    // No asset found — play a fun confirmation tone instead
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
        /// Load mechanical keyboard sounds from the hashed resource directory.
        /// Returns an array of CachedSound objects for random selection during typing.
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

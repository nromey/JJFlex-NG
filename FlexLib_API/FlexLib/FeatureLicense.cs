using Flex.UiWpfFramework.Mvvm;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Flex.Smoothlake.FlexLib
{

    public record Feature(string FeatureName, bool FeatureEnabled, FeatureStatusReason FeatureStatusReason)
    {
        // Tooltip generated upon feature instantiation. If disabled, a tooltip is set based on the reason
        public string FeatureGatedMessage => FeatureEnabled ? null :
            FeatureStatusReason switch
            {
                FeatureStatusReason.LicenseFile => FeatureName == "alpha" ? "This feature is not available" : "Purchase this feature!",

                FeatureStatusReason.Plus => "Subscribe to SmartSDR+ to use this feature!",

                FeatureStatusReason.Ea => "Subscribe to SmartSDR+ Early Access to use this feature!",

                FeatureStatusReason.BuiltIn => "Subscribe to use this feature!",

                _ => "Subscribe to use this feature!"
            };
    }

    public enum FeatureStatusReason
    {
        LicenseFile,
        Plus,
        Ea,
        BuiltIn,
        Unknown
    }

    public class FeatureLicense : ObservableObject
    {
        private Radio _radio;

        public FeatureLicense(Radio radio)
        {
            _radio = radio;
        }

        private FeatureStatusReason FeatureStatusStrToEnum(string featureStatus)
        {
            switch (featureStatus.ToLower())
            {
                case "license_file":
                    return FeatureStatusReason.LicenseFile;
                case "ea":
                    return FeatureStatusReason.Ea;
                case "plus":
                    return FeatureStatusReason.Plus;
                case "built_in":
                    return FeatureStatusReason.BuiltIn;
                default:
                    return FeatureStatusReason.Unknown;
            }
        }

        private void ParseLicenseFeature(string feature)
        {
            string[] attributes = feature?.Split(' ');
            if (attributes is null || attributes.Length < 3)
            {
                Debug.WriteLine($"FeatureLicense::ParseLicenseFeature: Malformed feature status message. Usage: feature name=<name> enabled=<0|1> reason=<reason>");
                return;
            }

            string featureName = attributes.FirstOrDefault(t => t.StartsWith("name="))?.Split('=')[1];
            string featureEnabled = attributes.FirstOrDefault(t => t.StartsWith("enabled="))?.Split('=')[1];
            string featureReason = attributes.FirstOrDefault(t => t.StartsWith("reason="))?.Split('=')[1];

            if (featureName is null || featureEnabled is null || featureReason is null)
            {
                Debug.WriteLine($"FeatureLicense::ParseLicenseFeature: Malformed feature status message. Usage: feature name=<name> enabled=<0|1> reason=<reason>");
                return;
            }

            if (!uint.TryParse(featureEnabled, out uint enabled) || enabled > 1)
            {
                Debug.WriteLine($"FeatureLicense::ParseLicenseFeature: Invaid value {enabled}");
                return;
            }

            switch (featureName)
            {
                case "alpha":
                    {
                        _licenseFeatAlpha = new Feature(featureName, Convert.ToBoolean(uint.Parse(featureEnabled)), FeatureStatusStrToEnum(featureReason));
                        RaisePropertyChanged(nameof(LicenseFeatAlpha)); 
                        break;
                    }

                case "auto_tune":
                    {
                        _licenseFeatAutotune = new Feature(featureName, Convert.ToBoolean(uint.Parse(featureEnabled)), FeatureStatusStrToEnum(featureReason));
                        RaisePropertyChanged(nameof(LicenseFeatAutotune));
                        break;
                    }

                case "digital_voice_keyer":
                    {
                        _licenseFeatDVK = new Feature(featureName, Convert.ToBoolean(uint.Parse(featureEnabled)), FeatureStatusStrToEnum(featureReason));
                        RaisePropertyChanged(nameof(LicenseFeatDVK));
                        break;
                    }

                case "div_esc":
                    {
                        _licenseFeatDivEsc = new Feature(featureName, Convert.ToBoolean(uint.Parse(featureEnabled)), FeatureStatusStrToEnum(featureReason));
                        RaisePropertyChanged(nameof(LicenseFeatDivEsc));
                        break;
                    }

                case "multiflex":
                    {
                        _licenseFeatMultiflex = new Feature(featureName, Convert.ToBoolean(uint.Parse(featureEnabled)), FeatureStatusStrToEnum(featureReason));
                        RaisePropertyChanged(nameof(LicenseFeatMultiflex));
                        break;
                    }

                case "noise_floor":
                    {
                        _licenseFeatNoiseFloor = new Feature(featureName, Convert.ToBoolean(uint.Parse(featureEnabled)), FeatureStatusStrToEnum(featureReason));
                        RaisePropertyChanged(nameof(LicenseFeatNoiseFloor));
                        break;
                    }

                case "noise_reduction":
                    {
                        _licenseFeatNoiseReduction = new Feature(featureName, Convert.ToBoolean(uint.Parse(featureEnabled)), FeatureStatusStrToEnum(featureReason));
                        RaisePropertyChanged(nameof(LicenseFeatNoiseReduction));
                        break;
                    }

                case "smartlink":
                    {
                        _licenseFeatSmartlink = new Feature(featureName, Convert.ToBoolean(uint.Parse(featureEnabled)), FeatureStatusStrToEnum(featureReason));
                        RaisePropertyChanged(nameof(LicenseFeatSmartlink));
                        break;
                    }

                case "wfp":
                    {
                        _licenseFeatWfp = new Feature(featureName, Convert.ToBoolean(uint.Parse(featureEnabled)), FeatureStatusStrToEnum(featureReason));
                        RaisePropertyChanged(nameof(LicenseFeatWFP));
                        break;
                    }
            }
        }

        private void ParseLicenseSubscription(string subscription)
        {
            string[] words = subscription.ToLower().Split(' ');
            if (words.Length < 2)
            {
                Debug.WriteLine($"FeatureLicense::ParseLicenseFeaturesStatus: Incorrect number of arguments. Usage: license subscription name=<subscription name> expiration=<expiration date>");
                return;
            }

            string subscriptionName = words.FirstOrDefault(t => t.StartsWith("name"))?.Split('=')[1];
            string expirationDate = words.FirstOrDefault(t => t.StartsWith("expiration"))?.Split('=')[1];

            if (subscriptionName is null || expirationDate is null)
            {
                Debug.WriteLine($"FeatureLicense::ParseLicenseFeaturesStatus: Invalid key/value pair. Usage: license subscription name=<subscription name> expiration=<expiration date>");
                return;
            }

            if (subscriptionName == "smartsdr+")
            {
                if (!DateTime.TryParse(expirationDate, out DateTime temp))
                {
                    Debug.WriteLine($"FeatureLicense::ParseLicenseStatus: Invalid expiration date ({temp})");
                    return;
                }

                // IsSmartSDRPlus will only be set to trur if the SmartSDR+ license is NOT expired.
                // We need to do this here for now since we expect there may be multiple subscription status messages of any kind (SSDR+ / EA),
                // and we only want to show a user's active (non-expired) subscription in the license info panel
                IsSmartSDRPlus = (temp - DateTime.Now).TotalDays > 0;
                SsdrPlusExpiration = temp;
            }

            else if (subscriptionName == "smartsdr+_early_access")
            {
                if (!DateTime.TryParse(expirationDate, out DateTime temp))
                {
                    Debug.WriteLine($"FeatureLicense::ParseLicenseStatus: Invalid expiration date ({temp})");
                    return;
                }
                // See comment in SmartSDR+ snippet above
                IsSmartSDRPlusEA = (temp - DateTime.Now).TotalDays > 0;
                SsdrEarlyAccessExpiration = temp;
            }

            else
            {
                // invalid subscription name
                Debug.WriteLine($"FeatureLicense::ParseLicenseFeaturesStatus: Invalid subscription name");
                return;
            }

        }

        public void ParseLicenseStatus(string s)
        {
            s = s.ToLower();
            string[] words = s.Split(' ');

            if (words.Length == 0) return;

            if (words[0] == "feature")
            {
                ParseLicenseFeature(s.Substring("feature ".Length));
                return;
            }

            if (words[0] == "subscription")
            {
                ParseLicenseSubscription(s.Substring("subscription ".Length));
                return;
            }

            foreach (string kv in words)
            {
                string[] tokens = kv.Split('=');

                if (tokens.Length != 2)
                {
                    if (!string.IsNullOrEmpty(kv)) Debug.WriteLine($"FeatureLicense::ParseLicenseStatus: Invalid key/value pair ({kv})");
                    continue;
                }

                string key = tokens[0];
                string value = tokens[1];

                switch (key)
                {
                    case "radio_id":
                        {
                            if (string.IsNullOrEmpty(value))
                            {
                                Debug.WriteLine($"FeatureLicense::ParseLicenseStatus: radio_id is empty");
                                continue;
                            }

                            _radioID = value.ToUpper();
                            RaisePropertyChanged(nameof(RadioID));
                            break;
                        }

                    case "issued":
                        {
                            if (!DateTime.TryParse(value, out DateTime temp))
                            {
                                Debug.WriteLine($"FeatureLicense::ParseLicenseStatus - license issued: Invalid value ({kv})");
                                continue;
                            }

                            _licenseIssueDate = temp;
                            RaisePropertyChanged(nameof(LicenseIssueDate));
                            break;
                        }

                    case "last_refreshed_date":
                        {
                            if (!DateTime.TryParse(value, out DateTime temp))
                            {
                                Debug.WriteLine($"FeatureLicense::ParseLicenseStatus - last license refreshed: Invalid value ({kv})");
                                continue;
                            }

                            _licenseLastRefreshDate = temp;
                            RaisePropertyChanged(nameof(LicenseLastRefreshDate));
                            break;
                        }

                    case "highest_major_version":
                        {
                            if (!int.TryParse(new string(value.Where(char.IsDigit).ToArray()), out int temp))
                            {
                                Debug.WriteLine($"FeatureLicense::ParseLicenseStatus - highest major version: Invalid value ({kv})");
                                continue;
                            }

                            _licenseHighestMajorVersion = temp;
                            RaisePropertyChanged(nameof(LicenseHighestMajorVersion));
                            break;
                        }

                    case "region":
                        {
                            if (string.IsNullOrEmpty(value))
                            {
                                Debug.WriteLine($"FeatureLicense::ParseLicenseStatus: region is empty");
                                continue;
                            }

                            _licenseRegion = value;
                            RaisePropertyChanged(nameof(LicenseRegion));
                            break;
                        }

                }

            }

        }

        private string _radioID = string.Empty;
        public string RadioID
        {
            get => _radioID;
            set
            {
                if (_radioID == value) return;
                _radioID = value;
                RaisePropertyChanged(nameof(RadioID));
            }
        }

        private DateTime _licenseIssueDate = DateTime.MaxValue;
        public DateTime LicenseIssueDate
        {
            get => _licenseIssueDate;
            set
            {
                if (_licenseIssueDate == value) return;
                _licenseIssueDate = value;
                RaisePropertyChanged(nameof(LicenseIssueDate));
            }
        }

        private DateTime _licenseLastRefreshDate = DateTime.MaxValue;
        public DateTime LicenseLastRefreshDate
        {
            get => _licenseLastRefreshDate;
            set
            {
                if (_licenseLastRefreshDate == value) return;
                _licenseLastRefreshDate = value;
                RaisePropertyChanged(nameof(LicenseLastRefreshDate));
            }
        }

        private int _licenseHighestMajorVersion;
        public int LicenseHighestMajorVersion
        {
            get => _licenseHighestMajorVersion;
            set
            {
                if (_licenseHighestMajorVersion == value) return;
                _licenseHighestMajorVersion = value;
                RaisePropertyChanged(nameof(LicenseHighestMajorVersion));
            }
        }

        private string _licenseRegion = string.Empty;
        public string LicenseRegion
        {
            get => _licenseRegion;
            set
            {
                if (_licenseRegion == value) return;
                _licenseRegion = value;
                RaisePropertyChanged(nameof(LicenseRegion));
            }
        }

        private Feature _licenseFeatAlpha;
        public Feature LicenseFeatAlpha
        {
            get => _licenseFeatAlpha;
            set
            {
                if (_licenseFeatAlpha == value) return;
                _licenseFeatAlpha = value;
                RaisePropertyChanged(nameof(LicenseFeatAlpha));
            }
        }

        private Feature _licenseFeatSmartlink;
        public Feature LicenseFeatSmartlink
        {
            get => _licenseFeatSmartlink;
            set
            {
                if (_licenseFeatSmartlink == value) return;
                _licenseFeatSmartlink = value;
                RaisePropertyChanged(nameof(LicenseFeatSmartlink));
            }
        }

        private Feature _licenseFeatMultiflex;
        public Feature LicenseFeatMultiflex
        {
            get => _licenseFeatMultiflex;
            set
            {
                if (_licenseFeatMultiflex == value) return;
                _licenseFeatMultiflex = value;
                RaisePropertyChanged(nameof(LicenseFeatMultiflex));
            }
        }

        private Feature _licenseFeatWfp;
        public Feature LicenseFeatWFP
        {
            get => _licenseFeatWfp;
            set
            {
                if (_licenseFeatWfp == value) return;
                _licenseFeatWfp = value;
                RaisePropertyChanged(nameof(LicenseFeatWFP));
            }
        }

        private Feature _licenseFeatNoiseFloor;
        public Feature LicenseFeatNoiseFloor
        {
            get => _licenseFeatNoiseFloor;
            set
            {
                if (_licenseFeatNoiseFloor == value) return;
                _licenseFeatNoiseFloor = value;
                RaisePropertyChanged(nameof(LicenseFeatNoiseFloor));
            }
        }

        private Feature _licenseFeatNoiseReduction;
        public Feature LicenseFeatNoiseReduction
        {
            get => _licenseFeatNoiseReduction;
            set
            {
                if (_licenseFeatNoiseReduction == value) return;
                _licenseFeatNoiseReduction = value;
                RaisePropertyChanged(nameof(LicenseFeatNoiseReduction));
            }
        }

        private Feature _licenseFeatDVK;
        public Feature LicenseFeatDVK
        {
            get => _licenseFeatDVK;
            set
            {
                if (_licenseFeatDVK == value) return;
                _licenseFeatDVK = value;
                RaisePropertyChanged(nameof(LicenseFeatDVK));
            }
        }

        private Feature _licenseFeatAutotune;
        public Feature LicenseFeatAutotune
        {
            get => _licenseFeatAutotune;
            set
            {
                if (_licenseFeatAutotune == value) return;
                _licenseFeatAutotune = value;
                RaisePropertyChanged(nameof(LicenseFeatAutotune));
            }
        }

        private Feature _licenseFeatDivEsc;
        public Feature LicenseFeatDivEsc
        {
            get => _licenseFeatDivEsc;
            set
            {
                if (_licenseFeatDivEsc == value) return;
                _licenseFeatDivEsc = value;
                RaisePropertyChanged(nameof(LicenseFeatDivEsc));
            }
        }

        private bool _isSmartSDRPlus;
        public bool IsSmartSDRPlus
        {
            get => _isSmartSDRPlus;
            set
            {
                if (value == _isSmartSDRPlus) return;
                _isSmartSDRPlus = value;
                RaisePropertyChanged(nameof(IsSmartSDRPlus));
            }
        }

        private bool _isSmartSDRPlusEA;
        public bool IsSmartSDRPlusEA
        {
            get => _isSmartSDRPlusEA;
            set
            {
                if (value == _isSmartSDRPlusEA) return;
                _isSmartSDRPlusEA = value;
                RaisePropertyChanged(nameof(IsSmartSDRPlusEA));
            }
        }

        private DateTime _ssdrPlusExpiration = DateTime.MinValue;
        public DateTime SsdrPlusExpiration
        {
            get => _ssdrPlusExpiration;
            set
            {
                if (_ssdrPlusExpiration == value) return;
                _ssdrPlusExpiration = value;
                RaisePropertyChanged(nameof(SsdrPlusExpiration));
            }
        }

        private DateTime _ssdrEarlyAccessExpiration;
        public DateTime SsdrEarlyAccessExpiration
        {
            get => _ssdrEarlyAccessExpiration;
            set
            {
                if (_ssdrEarlyAccessExpiration == value) return;
                _ssdrEarlyAccessExpiration = value;
                RaisePropertyChanged(nameof(SsdrEarlyAccessExpiration));
            }
        }

        public void SendFeatureLicenseCommand(string cmd)
        {
            _radio.SendCommand(cmd);
        }
    }
}

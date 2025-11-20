// ****************************************************************************
///*!	\file IPAddressValidator.xaml.cs
// *	\brief ValidationRule for IP Address
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2016-08-23
// *	\author Abed Haque, AB5ED
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Windows.Controls;

namespace Flex.UiWpfFramework.Utils
{
    public class IPAddressValidator : ValidationRule
    {
        public override ValidationResult Validate (object value, System.Globalization.CultureInfo cultureInfo)
        {
            if (value == null)
            {
                return new ValidationResult(false, "value cannot be empty.");
            }
            else
            {
                string ipString = value.ToString();
                IPAddress ip;

                if (!HasFourQuartets(ipString))
                    return new ValidationResult(false, "4 quartets required.");
                else if (!QuartetsAreNumbers(ipString))
                    return new ValidationResult(false, "IP address only contain numbers.");
                else if (!QuartetsAreInValidRange(ipString))
                    return new ValidationResult(false, "Quartets must be between 0 and 255.");
                else if (!IPAddress.TryParse(value.ToString(), out ip))
                    return new ValidationResult(false, "Not a valid IP Address.");
            }
            return ValidationResult.ValidResult;
        }

        private bool HasFourQuartets(string ip)
        {
            return (ip.Split('.').Length == 4);
        }
        
        private bool QuartetsAreNumbers(string ipString)
        {
            List<string> quartets = ipString.Split('.').ToList<string>();
            return quartets.All<string>(x => IsInt(x));
        }

        private bool QuartetsAreInValidRange(string ipString)
        {
            List<string> quartets = ipString.Split('.').ToList<string>();
            List<int> quartetsInts = quartets.Select(int.Parse).ToList();

            return quartetsInts.All<int>(x => (x >= 0 && x <=255));
            
        }

        private bool IsInt(string s)
        {
            int x = 0;
            return int.TryParse(s, out x);
        }
    }
}

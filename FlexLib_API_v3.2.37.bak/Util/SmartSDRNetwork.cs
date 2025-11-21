
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Flex.UiWpfFramework.Mvvm;

namespace Util
{
    public class SmartSDRNetwork : ObservableObject
    {
        public SmartSDRNetwork()
        {
            NetworkChange.NetworkAddressChanged += new NetworkAddressChangedEventHandler(AddressChangedHandler);
        }

        private bool _isInternetAvailable = false;
        public bool IsInternetAvailable
        {
            get { return _isInternetAvailable; }                
            set
            {
                if(_isInternetAvailable != value)
                {
                    _isInternetAvailable = value;
                    RaisePropertyChanged("IsInternetAvailable");
                }
            }
        }

        public void UpdateIsInternetAvailable()
        {
            IsInternetAvailable = GetIsUriAvailble("https://www.amazon.com", timeout_ms: 3000);
        }

        private bool GetIsUriAvailble(string uri, int timeout_ms)
        {
            HttpWebResponse response = null;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Timeout = timeout_ms;
            request.Method = "GET";

            bool isAvailable = true;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException)
            {
                isAvailable = false;
            }
            finally
            {
                if (response != null)
                {
                    response.Close();
                }
            }
            return isAvailable;
        }

        private void AddressChangedHandler(object sender, EventArgs e)
        {
            UpdateIsInternetAvailable();
        }
    }
}

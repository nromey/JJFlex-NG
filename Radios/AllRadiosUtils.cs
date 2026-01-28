using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using JJTrace;

namespace Radios
{
    public partial class AllRadios
    {
        internal static byte[] Str2Bytes(string str)
        {
            if (string.IsNullOrEmpty(str)) return (null);
            int len = str.Length;
            byte[] rv = new byte[len];
            for (int i = 0; i < len; i++)
            {
                rv[i] = (byte)str[i];
            }
            return rv;
        }

        internal static string Bytes2Str(byte[] bytes)
        {
            int len = bytes.Length;
            if (len == 0) return ("");
            StringBuilder strb = new StringBuilder(len);
            foreach (byte b in bytes)
            {
                strb.Append((char)b);
            }
            return strb.ToString();
        }

        private object realizedToken;
        private const int defaultWaitTime = 1000; // 1 second
        internal delegate void sendCmdDel();
        /// <summary>
        /// await this token
        /// </summary>
        /// <param name="cmd">command to send</param>
        /// <param name="token">object to await</param>
        /// <param name="ms">optional time in ms</param>
        /// <returns>true on success</returns>
        internal bool AwaitToken(sendCmdDel cmd, object token, int ms)
        {
            realizedToken = null;
            cmd();
            bool rv = await(() => { return token.Equals(realizedToken); }, ms);
            if (!rv) Tracing.TraceLine("AwaitToken:not found", TraceLevel.Error);
            return rv;
        }
        internal bool AwaitToken(sendCmdDel cmd, object token)
        {
            return AwaitToken(cmd, token, defaultWaitTime);
        }

        /// <summary>
        /// set awaited token.
        /// </summary>
        /// <param name="token"></param>
        internal void Realized(object token)
        {
            realizedToken = token;
        }
    }
}

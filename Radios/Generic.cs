using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Generic radio communications terminal
    /// </summary>
    class Generic : AllRadios
    {
        // capabilities
        private RigCaps.Caps[] capsList = { };

        private void IHandler(string str)
        {
            Tracing.TraceLine("IHandler:" + str, TraceLevel.Info);
            Callouts.DirectDataReceiver(str);
        }

        public Generic()
        {
            Tracing.TraceLine("Generic", TraceLevel.Info);
            myCaps = new RigCaps(capsList);
        }

        public override bool Open(OpenParms p)
        {
            bool rv = base.Open(p);
            if (rv) InterruptHandler = IHandler;
            return rv;
        }
    }

    class GenericBinary : AllRadios
    {
        // capabilities
        private RigCaps.Caps[] capsList = { };

        private void IHandler(byte[] bytes, int len)
        {
            Tracing.TraceLine("IHandler:" + Escapes.Escapes.Decode(bytes, len), TraceLevel.Info);
            StringBuilder sb = new StringBuilder(len);
            for (int i = 0; i < len; i++)
            {
                sb.Append((char)bytes[i]);
            }
            Callouts.DirectDataReceiver(sb.ToString());
        }

        public GenericBinary()
        {
            Tracing.TraceLine("Generic", TraceLevel.Info);
            myCaps = new RigCaps(capsList);
        }

        public override bool Open(OpenParms p)
        {
            p.RawIO = true;
            Escapes.Escapes.HexOnly = true;
            bool rv = base.Open(p);
            if (rv) IBytesHandler = IHandler;
            return rv;
        }
    }
}

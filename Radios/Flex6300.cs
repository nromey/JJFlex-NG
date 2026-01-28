//#define GetMemoriez
//#define MemoryDebug
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    public class Flex6300 : Flex
    {
        // Region - capabilities
        #region capabilities
        private RigCaps.Caps[] capsList =
        {
            RigCaps.Caps.AFGet,
            RigCaps.Caps.AFSet,
            RigCaps.Caps.AGGet,
            RigCaps.Caps.AGSet,
            RigCaps.Caps.FrGet,
            RigCaps.Caps.FrSet,
            RigCaps.Caps.IDGet,
            RigCaps.Caps.KSGet,
            RigCaps.Caps.KSSet,
            RigCaps.Caps.ModeGet,
            RigCaps.Caps.ModeSet,
            RigCaps.Caps.RITGet,
            RigCaps.Caps.RITSet,
            RigCaps.Caps.SMGet,
            RigCaps.Caps.TXITGet,
            RigCaps.Caps.TXITSet,
            RigCaps.Caps.VFOGet,
            RigCaps.Caps.VFOSet,
            //RigCaps.Caps.ATGet,
            //RigCaps.Caps.ATSet,
            RigCaps.Caps.ManualATGet,
            RigCaps.Caps.ManualATSet,
            RigCaps.Caps.AGTimeGet,
            RigCaps.Caps.AGTimeSet,
            RigCaps.Caps.ALCGet,
            RigCaps.Caps.ANGet,
            RigCaps.Caps.ANSet,
            RigCaps.Caps.BCGet,
            RigCaps.Caps.BCSet,
            RigCaps.Caps.CLGet,
            RigCaps.Caps.CLSet,
            RigCaps.Caps.CTSSFreqGet,
            RigCaps.Caps.CTSSFreqSet,
            RigCaps.Caps.CTModeGet,
            RigCaps.Caps.CTModeSet,
            RigCaps.Caps.CWAutoTuneGet,
            RigCaps.Caps.CWAutoTuneSet,
            RigCaps.Caps.CWDelayGet,
            RigCaps.Caps.CWDelaySet,
            RigCaps.Caps.DMGet,
            RigCaps.Caps.DMSet,
            RigCaps.Caps.EQRGet,
            RigCaps.Caps.EQRSet,
            RigCaps.Caps.EQTGet,
            RigCaps.Caps.EQTSet,
            RigCaps.Caps.FSGet,
            RigCaps.Caps.FSSet,
            RigCaps.Caps.FWGet,
            RigCaps.Caps.FWSet,
            RigCaps.Caps.LKGet,
            RigCaps.Caps.LKSet,
            RigCaps.Caps.MemGet,
            RigCaps.Caps.MemSet,
            RigCaps.Caps.MGGet,
            RigCaps.Caps.MGSet,
            RigCaps.Caps.NBGet,
            RigCaps.Caps.NBSet,
            RigCaps.Caps.NFGet,
            RigCaps.Caps.NFSet,
            RigCaps.Caps.NTGet,
            RigCaps.Caps.NTSet,
            RigCaps.Caps.PAGet,
            RigCaps.Caps.PASet,
            RigCaps.Caps.RAGet,
            RigCaps.Caps.RASet,
            RigCaps.Caps.RFGet,
            RigCaps.Caps.RFSet,
            RigCaps.Caps.SPGet,
            RigCaps.Caps.SPSet,
            RigCaps.Caps.SQGet,
            RigCaps.Caps.SQSet,
            RigCaps.Caps.SWRGet,
            RigCaps.Caps.TOGet,
            RigCaps.Caps.TOSet,
            RigCaps.Caps.TXMonGet,
            RigCaps.Caps.TXMonSet,
            RigCaps.Caps.VDGet,
            RigCaps.Caps.VDSet,
            RigCaps.Caps.VGGet,
            RigCaps.Caps.VGSet,
            RigCaps.Caps.VSGet,
            RigCaps.Caps.VSSet,
            RigCaps.Caps.XFGet,
            RigCaps.Caps.XFSet,
            RigCaps.Caps.XPGet,
            RigCaps.Caps.XPSet,
            RigCaps.Caps.Pan,
            RigCaps.Caps.ManualTransmit,
            // Note that RemoteAudio is not supported on the Flex6300Remote, since it must be assumed.
            RigCaps.Caps.RemoteAudio,
            //RigCaps.Caps.ATMems,
        };
        #endregion

        // region - rig-specific properties
        #region RigSpecificProperties
        public override int RigID
        {
            get { return RadioSelection.RIGIDFlex; }
        }

        public override RigCaps.VFOs RXVFO
        {
            get
            {
                return base.RXVFO;
            }
            set
            {
                if (!Transmit)
                {
                    // If split, TXVFO switched to other VFO and muted according to RigField2.
                    // otherwise, old RXVFO is muted as per rigField2, and new VFO is set to transmit.
                    // RXVFO should be muted as per rigField1, and set active.
                    bool mute1 = (Callouts.RigField1.value == SliceControlChars[muteCharID]) ? true : false;
                    bool mute2 = (Callouts.RigField2.value == SliceControlChars[muteCharID]) ? true : false;
                    if (Split) // Checks _ VFOs
                    {
                        TXVFO = nextVFO(value);
                        q.Enqueue((FunctionDel)(() => { VFOToSlice(TXVFO).Mute = mute2; }));
                    }
                    else
                    {
                        q.Enqueue((FunctionDel)(() => { VFOToSlice(nextVFO(value)).Mute = mute2; }));
                        TXVFO = value;
                    }
                    q.Enqueue((FunctionDel)(() => { VFOToSlice(value).Active = true; }));
                    q.Enqueue((FunctionDel)(() => { VFOToSlice(value).Mute = mute1; }));
                }
            }
        }

        public override RigCaps.VFOs TXVFO
        {
            get
            {
                return base.TXVFO;
            }
            set
            {
                if (!Split) q.Enqueue((FunctionDel)(() => { VFOToSlice(value).Active = true; }));
                q.Enqueue((FunctionDel)(() => { VFOToSlice(value).IsTransmitSlice = true; }));
                _TXVFO = value;
            }
        }

        /// <summary>
        /// Calibrated S-Meter/power
        /// </summary>
        // Smeter and forward power are in DBM.
        public override int SMeter
        {
            get
            {
                if (Transmit)
                {
                    // Show forward power = exp(10, (dbm/10)) / 1000
                    return (int)((Math.Pow(10d, (double)(_PowerDBM / 10)) / 1000) + 0.5);
                }
                else
                {
                    int val = _SMeter + 127 - 3; // puts s0 at 0.
                    if (val < 0) val = 0;
                    int s = val / 6; // S-unit
                    // Perhaps indicate over S9.
                    val = (s <= 9) ? s : val - (9 * 6) + 9;
                    return val;
                }
            }
        }
        #endregion

        public Flex6300()
        {
            Tracing.TraceLine("Flex6300 constructor", TraceLevel.Info);

            myCaps = new RigCaps(capsList);
            // Using the Default of 2 VFOs.

            // default tuner type.
            setFlexTunerTypeNotAuto();
        }

        public override bool Open(OpenParms p)
        {
            Tracing.TraceLine("Flex6300 Open", TraceLevel.Info);
            // These fields are always active.
            p.RigField1 = new RigDependent(SliceControlChars, rigFieldsRXID,
                muteGet, muteSet,
                () => true, (bool val) => { });
            p.RigField2 = new RigDependent(SliceControlChars, rigFieldsTXID,
                muteGet, muteSet,
                () => true, (bool val) => { });

            bool rv = base.Open(p);
            IsOpen = rv;
            if (IsOpen) FilterObj = new Flex6300Filters(this, p); // Sets up RigFields.

            return rv;
        }

        private const int rigFieldsRXID = 1;
        private const int rigFieldsTXID = 2;
        private const int muteCharID = 0;
        private const int unMuteCharID = 1;
        private const int farLeftCharID = 2;
        private const int someLeftCharID = 3;
        private const int farRightCharID = 4;
        private const int someRightCharID = 5;
        private const int centerCharID = 6;
        // Note that the first two chars, mute and sound, are selected with up/down arrow keys.
        private static char[] SliceControlChars = { 'm', 's', 'l', (char)Keys.PageUp, 'r', (char)Keys.PageDown, 'c' };
        // Note the returned state is either muted or not.
        private char muteGet(int id)
        {
            char rv = ' ';
            if (!Power) return rv;
            // If field1, return mute state of RXVFO.
            // otherwise if id is field2,
            //   if split, return mute state of TXVFO,
            //   else return mute state of the next VFO from RXVFO.
            if (id == rigFieldsRXID) rv = (VFOToSlice(RXVFO).Mute) ? SliceControlChars[muteCharID] : SliceControlChars[unMuteCharID];
            else if (id == rigFieldsTXID)
            {
                if (Split) rv = (VFOToSlice(TXVFO).Mute) ? SliceControlChars[muteCharID] : SliceControlChars[unMuteCharID];
                else rv = (VFOToSlice(nextVFO(RXVFO)).Mute) ? SliceControlChars[muteCharID] : SliceControlChars[unMuteCharID];
            }
            return rv;
        }
        private void muteSet(char c, int id)
        {
            if (!Power) return;
            // Get Slice to use.
            RigCaps.VFOs v = (id == rigFieldsRXID) ? RXVFO :
                ((Split) ? TXVFO : nextVFO(RXVFO));
            Slice s = VFOToSlice(v);
            // If muted, then only unmute is recognized.
            if (s.Mute && (c != SliceControlChars[unMuteCharID])) return;
            // otherwise set indicated action.
            switch (c)
            {
                case 'm':
                    q.Enqueue((FunctionDel)(() => { s.Mute = true; }));
                    break;
                case 's':
                    q.Enqueue((FunctionDel)(() => { s.Mute = false; }));
                    break;
                case 'l':
                    q.Enqueue((FunctionDel)(() => { s.AudioPan = 0; }));
                    break;
                case (char)Keys.PageUp:
                    q.Enqueue((FunctionDel)(() => { s.AudioPan -= 10; }));
                    break;
                case 'r':
                    q.Enqueue((FunctionDel)(() => { s.AudioPan = 100; }));
                    break;
                case (char)Keys.PageDown:
                    q.Enqueue((FunctionDel)(() => { s.AudioPan += 10; }));
                    break;
                case 'c':
                    q.Enqueue((FunctionDel)(() => { s.AudioPan = 50; }));
                    break;
            }
        }

        public override void close()
        {
            Tracing.TraceLine("Flex6300 close", TraceLevel.Info);

            FilterObj = null;
            base.close();

            // This part of close is postponed.
            if (RigFields != null)
            {
                // The caller should have removed the user control from their form.
                ((Flex6300Filters)RigFields.RigControl).Close(); // Remove int handlers
                RigFields.Close();
                RigFields = null;
            }
        }
    }
}

#define CWMonitor
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the public surface of the tracking notch filters.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        #region Tracking Notch Filters — the public surface (#482)

        // Jim's FlexTNF.cs drove the TNF list straight off FlexLib's own TNF
        // objects. Sprint 9 replaced that form with TNFDialog, which takes
        // plain int/string/bool delegates and deliberately references neither
        // FlexBase nor TNF — but nothing ever supplied those delegates, and
        // the WinForms original was deleted in 074b2c78. Between the two the
        // feature left the product: FlexBase still subscribes to TNFAdded and
        // TNFRemoved and still sets TNFEnabled on connect, so tracking notches
        // are live against the radio with no way for a person to place one.
        //
        // These wrappers are what the dialog was written to be given. They are
        // public because JJFlexWpf is a separate assembly with no
        // InternalsVisibleTo, and they speak only in Hz, indexes and booleans
        // so the FlexLib TNF type stays on this side of the wall.

        /// <summary>Narrowest and widest notch the operator may ask for, in Hz.</summary>
        /// <remarks>
        /// Jim's WinForms box ran 50 Hz to 5000 Hz in 50 Hz steps and these are
        /// his numbers. FlexLib itself will take 5 Hz to 6000 Hz, but a notch
        /// narrower than the radio can actually place is a control that appears
        /// to move and does nothing — and outside its own range FlexLib
        /// discards the write with a <c>Debug.WriteLine</c> and no error, which
        /// without sight is indistinguishable from a dead key.
        /// </remarks>
        public const int TNFWidthMinHz = 50;
        public const int TNFWidthMaxHz = 5000;

        /// <summary>Shallowest and deepest notch. FlexLib ignores anything outside 1 to 3.</summary>
        public const int TNFDepthMin = 1;
        public const int TNFDepthMax = 3;

        /// <summary>
        /// The notch at this position, or null when that position no longer
        /// exists.
        ///
        /// <para><b>Snapshot, then index.</b> <c>theRadio.TNFList</c> is
        /// mutated by the status-parsing thread whenever the radio reports a
        /// notch added or dropped, so a position that was valid when the
        /// operator arrowed onto it can be gone by the time its width is read.
        /// On a MultiFlex radio the other operator's removal is exactly that
        /// event. Jim's dialog indexed the live list directly; every caller
        /// here goes through this instead.</para>
        /// </summary>
        private TNF TNFAt(int index)
        {
            if (index < 0) return null;
            List<TNF> list = theRadio?.TNFList;
            if (list == null) return null;
            TNF[] snapshot;
            try { snapshot = list.ToArray(); }
            catch (Exception ex)
            {
                // ToArray can still tear if the list is written mid-copy.
                Tracing.TraceLine("TNFAt:snapshot failed:" + ex.Message, TraceLevel.Error);
                return null;
            }
            return (index < snapshot.Length) ? snapshot[index] : null;
        }

        /// <summary>How many tracking notch filters the radio currently holds.</summary>
        public int TNFCount()
        {
            try { return theRadio?.TNFList?.Count ?? 0; }
            catch { return 0; }
        }

        /// <summary>
        /// Whether a notch can be placed right now. False when there is no
        /// receive slice or no panadapter behind it — <c>RequestTNF</c> needs a
        /// panadapter stream to hang the notch on, and callers must be able to
        /// say why nothing happened rather than letting Add fail quietly.
        /// </summary>
        public bool CanAddTNF()
        {
            return (theRadio != null) && (VFOToSlice(RXVFO) != null) && (Panadapter != null);
        }

        /// <summary>The notch's frequency, formatted the way every other frequency in the app is.</summary>
        public string TNFFrequencyDisplay(int index)
        {
            TNF tnf = TNFAt(index);
            if (tnf == null) return string.Empty;
            ulong hz = LibFreqtoLong(tnf.Frequency);
            FormatFreqDel fmt = Callouts?.FormatFreq;
            return (fmt != null) ? fmt(hz) : hz.ToString();
        }

        /// <summary>The notch's width in Hz. FlexLib holds bandwidth in MHz.</summary>
        public int TNFWidthHz(int index)
        {
            TNF tnf = TNFAt(index);
            return (tnf == null) ? 0 : (int)Math.Round(tnf.Bandwidth * 1e6);
        }

        /// <summary>Set the notch's width in Hz, clamped to what the radio will place.</summary>
        public void SetTNFWidthHz(int index, int hz)
        {
            TNF tnf = TNFAt(index);
            if (tnf == null) return;
            int clamped = Math.Max(TNFWidthMinHz, Math.Min(TNFWidthMaxHz, hz));
            Tracing.TraceLine("SetTNFWidthHz:" + index + '=' + clamped, TraceLevel.Info);
            q.Enqueue((FunctionDel)(() => { tnf.Bandwidth = clamped * 1e-6; }));
        }

        /// <summary>The notch's depth, 1 to 3.</summary>
        public int TNFDepth(int index)
        {
            TNF tnf = TNFAt(index);
            return (tnf == null) ? 0 : (int)tnf.Depth;
        }

        /// <summary>Set the notch's depth, clamped to 1 through 3.</summary>
        public void SetTNFDepth(int index, int depth)
        {
            TNF tnf = TNFAt(index);
            if (tnf == null) return;
            int clamped = Math.Max(TNFDepthMin, Math.Min(TNFDepthMax, depth));
            Tracing.TraceLine("SetTNFDepth:" + index + '=' + clamped, TraceLevel.Info);
            q.Enqueue((FunctionDel)(() => { tnf.Depth = (uint)clamped; }));
        }

        /// <summary>
        /// Whether the radio keeps this notch across a band change and a power
        /// cycle. A permanent notch is how an operator kills a birdie that
        /// lives at one frequency forever.
        /// </summary>
        public bool TNFPermanent(int index)
        {
            TNF tnf = TNFAt(index);
            return (tnf != null) && tnf.Permanent;
        }

        /// <summary>Set whether the radio keeps this notch.</summary>
        public void SetTNFPermanent(int index, bool permanent)
        {
            TNF tnf = TNFAt(index);
            if (tnf == null) return;
            Tracing.TraceLine("SetTNFPermanent:" + index + '=' + permanent, TraceLevel.Info);
            q.Enqueue((FunctionDel)(() => { tnf.Permanent = permanent; }));
        }

        /// <summary>
        /// Place a notch at the receive slice's current frequency and return
        /// its formatted frequency, or null if the radio did not report one.
        ///
        /// <para><b>Why this waits.</b> A notch is created by the RADIO, not by
        /// us: <c>RequestTNF</c> asks, and the notch only exists once the
        /// status parser has read it back and raised TNFAdded. There is nothing
        /// to select, name or adjust until then, so the caller is told what
        /// happened rather than handed an empty list to guess at. Jim's dialog
        /// waited the same second and, on a timeout, traced an error and
        /// returned — leaving the operator with a button that had done nothing
        /// and said nothing. Returning null is what lets the caller SAY so.</para>
        /// </summary>
        public string AddTNFAtReceiveFrequency()
        {
            if (!CanAddTNF())
            {
                Tracing.TraceLine("AddTNFAtReceiveFrequency:no slice or panadapter", TraceLevel.Warning);
                return null;
            }

            Slice slice = VFOToSlice(RXVFO);
            Panadapter pan = Panadapter;
            if ((slice == null) || (pan == null)) return null;

            int before = TNFCount();
            Tracing.TraceLine("AddTNFAtReceiveFrequency:" + slice.Freq.ToString("f6"), TraceLevel.Info);
            q.Enqueue((FunctionDel)(() => { theRadio.RequestTNF(slice.Freq, pan.StreamID); }));

            if (!await(() => { return TNFCount() > before; }, 1000))
            {
                Tracing.TraceLine("AddTNFAtReceiveFrequency:radio did not report a new TNF", TraceLevel.Error);
                return null;
            }
            return TNFFrequencyDisplay(TNFCount() - 1);
        }

        /// <summary>
        /// Remove the notch at this position. Returns false when the radio did
        /// not drop it, so the caller can say so rather than removing a row
        /// from a list while the notch stays on the air.
        /// </summary>
        public bool RemoveTNFAt(int index)
        {
            TNF tnf = TNFAt(index);
            if (tnf == null) return false;

            Tracing.TraceLine("RemoveTNFAt:" + index, TraceLevel.Info);
            q.Enqueue((FunctionDel)(() => { tnf.Close(); }));
            bool gone = await(() =>
            {
                List<TNF> list = theRadio?.TNFList;
                return (list == null) || !list.Contains(tnf);
            }, 1000);
            if (!gone) Tracing.TraceLine("RemoveTNFAt:radio did not drop it", TraceLevel.Error);
            return gone;
        }

        #endregion
    }
}

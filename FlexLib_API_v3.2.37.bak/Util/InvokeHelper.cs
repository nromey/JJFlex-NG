// ****************************************************************************
///*!	\file InvokeHelper.cs
// *	\brief Invoke Helper functions
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2017-04-19
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Windows.Threading;


namespace Util
{
    public class InvokeHelper
    {
        public static void BeginInvokeIfNeeded(Dispatcher dispatcher, Action action)
        {
            // are we already on the Dispatcher thread?
            if (dispatcher.CheckAccess())
            {
                // yes -- just execute the action
                action();
            }
            else
            {
                // no -- need to BeginInvoke
                dispatcher.BeginInvoke(action);
            }
        }

        public static void InvokeIfNeeded(Dispatcher dispatcher, Action action)
        {
            // are we already on the Dispatcher thread?
            if (dispatcher.CheckAccess())
            {
                // yes -- just execute the action
                action();
            }
            else
            {
                // no -- need to Invoke
                dispatcher.Invoke(action);
            }
        }
    }
}

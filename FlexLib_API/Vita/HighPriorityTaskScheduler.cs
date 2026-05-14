// ****************************************************************************
///*!	\file HighPriorityTaskScheduler.cs
// *	\brief A TaskScheduler that runs tasks on threads with MMCSS "Pro Audio" priority
// *
// *	\copyright	Copyright 2012-2026 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2026-02-27
// *	\author Annaliese McDermond, NH6Z
// */
// ****************************************************************************

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vita;

public sealed class HighPriorityTaskScheduler : TaskScheduler
{
    public static readonly HighPriorityTaskScheduler Instance = new();

    [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(nint avrtHandle);

    protected override IEnumerable<Task>? GetScheduledTasks() => null;

    protected override void QueueTask(Task task)
    {
        var thread = new Thread(() =>
        {
            uint taskIndex = 0;
            var handle = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
            try
            {
                TryExecuteTask(task);
            }
            finally
            {
                if (handle != 0)
                    AvRevertMmThreadCharacteristics(handle);
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
    }

    // Never inline — force all work onto a dedicated MMCSS thread so that callers
    // (e.g. ActionBlock.Post on a ThreadPool thread) don't accidentally run audio
    // processing at normal priority.
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
}

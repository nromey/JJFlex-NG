// ****************************************************************************
///*!	\file MmcssPipelineScheduler.cs
// *	\brief A TaskScheduler backed by persistent MMCSS "Pro Audio" threads
// *
// *	\copyright	Copyright 2012-2026 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2026-03-16
// *	\author Annaliese McDermond, NH6Z
// */
// ****************************************************************************

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vita;

/// <summary>
/// A TaskScheduler backed by a pool of persistent MMCSS "Pro Audio" threads.
/// All threads are created once at startup with MMCSS registration, eliminating
/// per-task thread creation churn.  The pool size defaults to the number of
/// processors so that parallel ActionBlock tasks can scale across cores while
/// maintaining real-time priority.
/// </summary>
public sealed class MmcssPipelineScheduler : TaskScheduler
{
    // === R5 DIAGNOSTIC PATCH (2026-05-04, JJFlexRadio) ===
    // Hypothesis: the eager singleton's constructor spawns 4 persistent MMCSS
    // "Pro Audio" threads at startup via avrt.dll, and those idle real-time
    // reservations starve the UDP receive callback for the discovery broadcast
    // on Don's machine. By substituting TaskScheduler.Default for Instance,
    // we keep the audio pipeline functional (regular ThreadPool scheduling)
    // while eliminating the MMCSS thread reservations entirely.
    //
    // R5 outcome A (discovery succeeds): MMCSS is the culprit. Permanent fix
    //   replaces this scheduler with Default in JJFlex's wrapper, possibly
    //   upstreaming to Flex.
    // R5 outcome B (discovery still silent-fails): MMCSS exonerated.
    //   Investigation pivots to other FlexLib 4.2.18-internal candidates.
    //
    // Revert this patch once the diagnostic completes. Original:
    //   public static readonly MmcssPipelineScheduler Instance = new();
    public static readonly TaskScheduler Instance = TaskScheduler.Default;
    // === end R5 patch ===

    [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(nint avrtHandle);

    private readonly BlockingCollection<Task> _tasks = new();
    private readonly Thread[] _threads;

    public override int MaximumConcurrencyLevel => _threads.Length;

    private MmcssPipelineScheduler()
    {
        // Keep pool small to stay within MMCSS "Pro Audio" thread limit (~20-30 per process).
        // Pipe.EventLoop threads also register with MMCSS, and there can be 14+ pipes.
        const int threadCount = 4;
        _threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            _threads[i] = new Thread(Run)
            {
                IsBackground = true,
                Name = $"MMCSS Pipeline {i} (Pro Audio)"
            };
            _threads[i].Start();
        }
    }

    private void Run()
    {
        uint taskIndex = 0;
        var handle = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        if (handle == 0)
            Debug.WriteLine($"MmcssPipelineScheduler: AvSetMmThreadCharacteristics failed ({Marshal.GetLastWin32Error()})");

        try
        {
            foreach (var task in _tasks.GetConsumingEnumerable())
                TryExecuteTask(task);
        }
        finally
        {
            if (handle != 0)
                AvRevertMmThreadCharacteristics(handle);
        }
    }

    protected override void QueueTask(Task task) => _tasks.Add(task);

    // Never inline — force all work onto a dedicated MMCSS thread.
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task>? GetScheduledTasks() => _tasks.ToArray();
}

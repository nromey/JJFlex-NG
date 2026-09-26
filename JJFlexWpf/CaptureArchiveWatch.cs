using System;
using System.Diagnostics;
using System.Windows.Threading;
using JJTrace;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Listens for a capture archived by a connection drop and puts the window in
    /// front of the operator (#566's bridge, Sprint 45 Track H).
    ///
    /// <para>Same shape and same reasoning as <see cref="DiagnosticOffer"/>:
    /// installed once, on the UI thread, at startup, so the dispatcher captured
    /// here is the one that can show a window later from whatever thread the
    /// event arrives on. The archive runs on a worker (it compresses a log that can
    /// be megabytes), so it never arrives on the UI thread by itself.</para>
    ///
    /// <para><b>BeginInvoke, not Invoke.</b> The archive's worker must not block
    /// waiting for a modal window to be dismissed — that thread is inside a
    /// radio teardown, and a dialog the operator leaves open for a minute would
    /// hold it for a minute.</para>
    /// </summary>
    public static class CaptureArchiveWatch
    {
        private static readonly object _gate = new();
        private static bool _installed;
        private static Dispatcher? _ui;

        /// <summary>Wire the watch. Idempotent.</summary>
        public static void Install()
        {
            lock (_gate)
            {
                if (_installed) return;
                _installed = true;
                _ui = Dispatcher.CurrentDispatcher;
            }
            CaptureArchive.ArchivedAfterDrop += OnArchived;
        }

        private static void OnArchived(CaptureArchiveNotice notice)
        {
            // The notice is handed over unrendered, on purpose (Sol's review
            // of H10, blocker 2). Its text asks the recording state at the
            // moment it is composed, and the dialog composes it inside the
            // dispatched action — so nothing here may read Explanation,
            // AsText or RecordingNow and carry the answer across the queue.
            try
            {
                var ui = _ui;
                if (ui == null) Dialogs.CaptureArchivedDialog.Show(notice);
                else ui.BeginInvoke(new Action(() => Dialogs.CaptureArchivedDialog.Show(notice)));
            }
            catch (Exception ex)
            {
                // The file is archived and on disk either way. A window that will
                // not open must not become a second failure on top of a radio
                // that has already gone.
                try
                {
                    Tracing.TraceLine(
                        "CaptureArchiveWatch: could not show the archived-recording notice: " + ex.Message
                        + " — the recording is still at " + notice?.ArchivePath,
                        TraceLevel.Warning);
                }
                catch { }
            }
        }
    }
}

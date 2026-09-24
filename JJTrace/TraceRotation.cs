using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace JJTrace
{
    /// <summary>
    /// Size-based rotation of the LIVE trace file. Ratified 2026-08-07 after a
    /// marathon session grew the active JJFlexRadioTrace.txt to 11.7 GB: boot
    /// maintenance prunes archives, but nothing capped a live trace mid-session,
    /// and the day's crash bundle shipped without its trace because attaching an
    /// 11.7 GB file was impossible.
    ///
    /// The model: a long session becomes a CHAIN OF PARTS. At the threshold the
    /// active file is closed, renamed to a stamped part file, and a fresh live
    /// file opens at the same path with a one-line "continues from part NN"
    /// breadcrumb. The closed part is compressed into the normal session archive
    /// on a background worker, so tracing never stalls behind LZMA. Nothing ever
    /// needs splitting after the fact, and the crash bundler always has a
    /// bounded, attachable tail.
    ///
    /// Rotation is plumbing: it is silent (no speech, no dialog), it does not
    /// change the trace format, and it does not touch the Operations → Tracing
    /// controls.
    /// </summary>
    public static partial class Tracing
    {
        /// <summary>
        /// Rotate the live trace at 256 MB.
        ///
        /// The ratified band was 250-500 MB. The low end wins because the cost
        /// of a part is paid twice: LZMA compression of the closed part (single
        /// background worker, minutes at the top of the band on a big text file)
        /// and Deflate of the current part into a crash bundle at crash time,
        /// where the whole point is producing something small enough to upload
        /// against a 50 MB receiver limit. 256 MB of trace text lands around
        /// 15-25 MB deflated, which fits a bundle; 500 MB would not reliably.
        ///
        /// Deliberately a constant, not a user setting, for v1.
        /// </summary>
        public const long DefaultRotationThresholdBytes = 256L * 1024 * 1024;

        private static long _rotationThresholdBytes = DefaultRotationThresholdBytes;

        /// <summary>
        /// Bytes written into one part before rotation fires. Zero or negative
        /// disables rotation entirely (the pre-2026-08-07 behaviour — an
        /// unbounded live file). Settable so the rotation test harness can force
        /// a rotation without writing a quarter of a gigabyte.
        /// </summary>
        public static long RotationThresholdBytes
        {
            get { return _rotationThresholdBytes; }
            set
            {
                _rotationThresholdBytes = value;
                RotatingTraceListener live = LiveListener;
                if (live != null) live.RotationThresholdBytes = value;
            }
        }

        /// <summary>
        /// Where closed parts get compressed to — normally
        /// %AppData%\JJFlexRadio\Traces. Null means "rotate but don't archive":
        /// the live file still stays bounded and the plain-text part survives,
        /// it just doesn't get a manifest entry. Set once at boot.
        ///
        /// <para>One root for parts and for sealed sessions, because they go
        /// through one worker into one manifest now. It lives on the
        /// coordinator; this stays as the name boot already uses.</para>
        /// </summary>
        public static string RotationArchiveRootDir
        {
            get { return TraceCoordinator.ArchiveRootDir; }
            set { TraceCoordinator.ArchiveRootDir = value; }
        }

        /// <summary>1-based number of the part currently being written; 1 when nothing has rotated.</summary>
        public static int CurrentPartNumber
        {
            get
            {
                RotatingTraceListener live = LiveListener;
                return live != null ? live.PartNumber : 1;
            }
        }

        /// <summary>True once this session has rotated at least once.</summary>
        public static bool SessionHasParts
        {
            get
            {
                RotatingTraceListener live = LiveListener;
                return live != null && live.HasRotated;
            }
        }

        /// <summary>
        /// Plain-text path of the most recently closed part, or null. The crash
        /// bundler attaches this alongside the current part when a crash lands
        /// moments after a rotation and the current part is nearly empty.
        /// </summary>
        public static string LastCompletedPartPath { get; private set; }

        /// <summary>Bytes in the part currently being written.</summary>
        public static long BytesInCurrentPart
        {
            get
            {
                RotatingTraceListener live = LiveListener;
                return live != null ? live.BytesInCurrentPart : 0;
            }
        }

        /// <summary>
        /// Record the plain-text path of a part the listener just closed.
        /// Called from inside the sink's own lock, so it does nothing that can
        /// block and nothing that reaches back into the coordinator.
        /// </summary>
        internal static void NotePartClosed(string partPath)
        {
            LastCompletedPartPath = partPath;
        }

        /// <summary>
        /// Block until queued archives finish, up to <paramref name="timeout"/>.
        /// One queue now carries rotation parts AND sealed sessions, so this
        /// bound really is the whole outstanding backlog rather than half of it.
        /// Returns true if the queue drained. Never throws.
        /// </summary>
        public static bool WaitForPendingArchives(TimeSpan timeout)
        {
            return TraceArchiveWorker.Drain(timeout);
        }

        /// <summary>
        /// Report an exception to the trace only — no modal dialog.
        ///
        /// <see cref="ErrMessageTrace(Exception)"/> defaults to msg:true, which
        /// pops a MessageBox titled "Exception" containing the raw framework
        /// message. That is how a housekeeping failure deep in trace archiving
        /// reached the user as an unexplained dialog about a stream being too
        /// large. Background and boot-time housekeeping uses this instead: the
        /// error is never suppressed, it just goes where errors belong rather
        /// than into a modal box the user can't act on.
        /// </summary>
        public static void ErrTraceOnly(Exception ex)
        {
            if (ex == null) return;
            ErrMessageTrace(ex, false, false);
        }

        /// <summary>
        /// Emit any rotation failure the listener recorded. Called from boot
        /// maintenance and the crash path so a silently-failing rotation shows
        /// up in the evidence rather than nowhere.
        /// </summary>
        public static void TraceRotationHealth()
        {
            try
            {
                RotatingTraceListener live = LiveListener;
                if (live == null) return;
                if (!string.IsNullOrEmpty(live.LastRotationError))
                {
                    TraceLine("Trace rotation: last attempt failed: " + live.LastRotationError, TraceLevel.Error);
                }
                if (live.HasRotated)
                {
                    TraceLine(string.Format(CultureInfo.InvariantCulture,
                        "Trace rotation: writing part {0:D3}, {1} bytes in part, threshold {2} bytes",
                        live.PartNumber, live.BytesInCurrentPart, _rotationThresholdBytes), TraceLevel.Info);
                }
            }
            catch { }
        }
    }
}

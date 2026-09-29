using System;
using System.Diagnostics;
using System.Globalization;

namespace JJTrace
{
    /// <summary>
    /// The one renderer of the <c>CaptureState:</c> line.
    ///
    /// <para>The wording is a CONTRACT with <c>tools/uia-probe</c>
    /// (<c>TraceLog.ParseStateMarker</c> in <c>Observe.cs</c>): one line,
    /// key=value, scalar fields first and the two paths last because paths
    /// contain spaces. Change either side only in step with the other.</para>
    ///
    /// <para><b>Why it moved here.</b> The line is written from two places that
    /// must not be able to disagree: ordinary transitions, which go through
    /// <c>Tracing.TraceLine</c> from the application, and the TERMINAL record,
    /// which the coordinator writes directly into the session's own sink while
    /// it holds the boundary. The boundary cannot call back into the
    /// application to have a string formatted — that would take the framework's
    /// trace lock in the wrong order — so the format has to be reachable from
    /// inside JJTrace. One renderer, two writers, no drift.</para>
    /// </summary>
    public static class TraceStateMarker
    {
        /// <summary>
        /// The kind of the <c>CaptureState:</c> line, so it introduces itself
        /// where it first appears in each part (#625). Both writers of the
        /// line — the application's transitions and the coordinator's terminal
        /// record — hand this to the sink, because this class is the one
        /// renderer and therefore the one place that can truthfully describe
        /// the line. DRAFT for Noel.
        /// </summary>
        public static readonly TraceRecordKind Record = new TraceRecordKind(
            "CaptureState",
            "lines that begin 'CaptureState:' record whether a detailed capture was on, the detail level being"
            + " recorded, which copy of JJ Flexible wrote this file and where the file was. The last such line in"
            + " a finished file marks the capture off and the level Off: it is written as the recording is closed.");

        /// <summary>
        /// Render the line. Every field is passed explicitly: nothing here
        /// reads live state, so a terminal record describes the session being
        /// archived rather than whatever happens to be current.
        /// </summary>
        /// <param name="captureOn">Whether a detailed capture is writing this
        /// file. False with <paramref name="level"/> Off is the archive marker:
        /// this file is finished, nobody is writing it.</param>
        /// <param name="level">Detail level to report.</param>
        /// <param name="instance">This app instance's number.</param>
        /// <param name="sessionStartedUtc">The session's own boot time, which
        /// is what <c>started=</c> means.</param>
        /// <param name="appVersion">App version string.</param>
        /// <param name="appPath">Full path of the app assembly.</param>
        /// <param name="file">Full path of the file being described.</param>
        public static string Render(bool captureOn,
                                    TraceLevel level,
                                    int instance,
                                    DateTime sessionStartedUtc,
                                    string appVersion,
                                    string appPath,
                                    string file)
        {
            return "CaptureState: capture=" + (captureOn ? "on" : "off")
                 + " level=" + level.ToString()
                 + " instance=" + instance.ToString(CultureInfo.InvariantCulture)
                 + " started=" + sessionStartedUtc.ToString("O", CultureInfo.InvariantCulture)
                 + " version=" + (string.IsNullOrEmpty(appVersion) ? "unknown" : appVersion)
                 + " app=" + (appPath ?? string.Empty)
                 + " file=" + (file ?? string.Empty);
        }

        /// <summary>
        /// The terminal marker for a session being detached: capture off, level
        /// Off. The LAST CaptureState line in a file is the truth about that
        /// file, and this is the line that makes a finished capture
        /// distinguishable from one still in flight.
        /// </summary>
        internal static string RenderTerminal(TraceEnvironment env, DateTime sessionStartedUtc, string file)
        {
            env = env ?? new TraceEnvironment();
            return Render(false, TraceLevel.Off, env.Instance, sessionStartedUtc,
                          env.AppVersion, env.AppPath, file);
        }
    }
}

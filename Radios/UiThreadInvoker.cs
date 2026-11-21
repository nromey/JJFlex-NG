#nullable enable
using System;
using System.Windows.Forms;

namespace Radios
{
    /// <summary>
    /// The app's way onto its UI thread, handed to the radio layer explicitly
    /// (<see cref="FlexBase.OpenParms.UiThread"/>), so nothing there has to
    /// discover the UI thread by casting the parent window to a WinForms
    /// <see cref="Control"/> (#1205).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SmartLink sign-in runs on the SmartLink thread and shows and
    /// disposes its browser form on the UI thread. It used to find that thread
    /// by casting <see cref="FlexBase.OpenParms.ParentWindow"/> to
    /// <see cref="Control"/>, and every cast had a silent fallback: had the
    /// parent ever stopped being a form, the browser form would have been shown
    /// on whatever thread asked, with nothing to say so. The parent window
    /// stays an <see cref="IWin32Window"/>, which is all an owner and a handle
    /// need; the thread comes from here.
    /// </para>
    /// <para>
    /// Only the code that builds the shell knows what the shell is, so only it
    /// builds one of these (<see cref="ForControl"/> today). A shell of another
    /// kind supplies its own four delegates and the radio layer does not change.
    /// </para>
    /// </remarks>
    public sealed class UiThreadInvoker
    {
        private readonly Func<bool> _isReady;
        private readonly Func<bool> _invokeRequired;
        private readonly Action<Action> _invoke;
        private readonly Action<Action> _post;

        /// <param name="isReady">True once the UI thread can take work (a WinForms
        /// control's handle exists).</param>
        /// <param name="invokeRequired">True when the caller is on another thread.</param>
        /// <param name="invoke">Runs work on the UI thread and waits for it.</param>
        /// <param name="post">Queues work on the UI thread and returns at once.</param>
        public UiThreadInvoker(Func<bool> isReady, Func<bool> invokeRequired,
                               Action<Action> invoke, Action<Action> post)
        {
            _isReady = isReady ?? throw new ArgumentNullException(nameof(isReady));
            _invokeRequired = invokeRequired ?? throw new ArgumentNullException(nameof(invokeRequired));
            _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        /// <summary>The invoker for a WinForms control's thread, the shell today.</summary>
        public static UiThreadInvoker ForControl(Control control)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            return new UiThreadInvoker(
                () => control.IsHandleCreated,
                () => control.InvokeRequired,
                work => control.Invoke(work),
                work => control.BeginInvoke(work));
        }

        /// <summary>True when the UI thread can take work and the caller is not on it.</summary>
        public bool IsOffUiThread => _isReady() && _invokeRequired();

        /// <summary>
        /// Queues <paramref name="work"/> on the UI thread. False, and nothing
        /// queued, when the UI thread cannot take work yet.
        /// </summary>
        public bool Post(Action work)
        {
            if (!_isReady()) return false;
            _post(work);
            return true;
        }

        /// <summary>
        /// True when <paramref name="ui"/> is set and the caller is off its UI thread,
        /// so work must be marshalled there.
        /// </summary>
        public static bool MustMarshal(UiThreadInvoker? ui) => ui != null && ui.IsOffUiThread;

        /// <summary>
        /// Runs <paramref name="work"/> on the UI thread through <paramref name="ui"/>
        /// when one is set and the caller is off that thread, waiting for it;
        /// otherwise runs it here. True when it was marshalled.
        /// </summary>
        public static bool Run(UiThreadInvoker? ui, Action work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (MustMarshal(ui))
            {
                ui!._invoke(work);
                return true;
            }
            work();
            return false;
        }
    }
}

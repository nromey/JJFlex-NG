namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// The exceptions that escaped to the UI thread's dispatcher while a
/// containment was open (task #907). Opened by <see cref="UiThread.Contain"/>;
/// the sweep takes what was caught after each dialog and reports it as that
/// dialog's finding, so one dialog that throws cannot end the run for the rest.
/// </summary>
public sealed class ExceptionContainment : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Exception> _caught = new();

    internal ExceptionContainment() { }

    internal void Record(Exception exception)
    {
        lock (_gate) _caught.Add(exception);
    }

    /// <summary>Every exception caught since the last call, oldest first; the list is then emptied.</summary>
    public IReadOnlyList<Exception> Take()
    {
        lock (_gate)
        {
            var taken = _caught.ToList();
            _caught.Clear();
            return taken;
        }
    }

    public void Dispose() => UiThread.EndContainment(this);
}

namespace JJFlexUpdaterHelper;

// Step 11 of the helper flow. A named OS mutex prevents two helper
// instances from racing on the same install. Two parallel helpers writing
// to target_dir is the worst-case scenario the .new+rename pattern
// CAN'T defend against — both helpers might rename their .new on top of
// each other and the install ends up holding a Frankenstein mix.
//
// Local\ scope (rather than Global\) keeps multi-user-on-one-machine
// scenarios independent: one user's update doesn't block another user's
// terminal-server session.
//
// AbandonedMutexException is treated as success: a previous owner crashed
// holding the mutex, the OS released it to us, and we should proceed
// (not block forever waiting for a never-coming release). The crash that
// abandoned it is unrelated to anything we're about to do.
internal sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\JJFlexUpdaterHelper-Singleton";

    private readonly Mutex? _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex? mutex, bool owns)
    {
        _mutex = mutex;
        _ownsMutex = owns;
    }

    public bool IsOwner => _ownsMutex;

    public static SingleInstanceGuard TryAcquire(string mutexName = DefaultMutexName)
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            var got = mutex.WaitOne(TimeSpan.Zero);
            if (got)
            {
                return new SingleInstanceGuard(mutex, owns: true);
            }
            mutex.Dispose();
            return new SingleInstanceGuard(null, owns: false);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died holding the mutex — kernel handed it to us
            // along with this exception. Take it and proceed.
            return new SingleInstanceGuard(mutex, owns: true);
        }
    }

    public void Dispose()
    {
        if (_ownsMutex && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Already released; ignore.
            }
            _mutex.Dispose();
            _ownsMutex = false;
        }
    }
}

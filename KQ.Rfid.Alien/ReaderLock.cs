namespace KQ.Rfid.Alien;

/// <summary>
/// Cross-process lock so only one app streams from a reader at a time (both would bind the stream port and fight
/// over AutoMode). A named semaphore is used rather than a mutex because it has no thread affinity, which matters
/// with async code.
/// </summary>
public sealed class ReaderLock : IDisposable
{
    private readonly Semaphore _semaphore;
    private bool _held;

    private ReaderLock(Semaphore semaphore)
    {
        _semaphore = semaphore;
        _held = true;
    }

    /// <summary>Returns the lock, or null if another app is already streaming from this reader.</summary>
    public static ReaderLock? TryAcquire(string readerHost)
    {
        var semaphore = new Semaphore(1, 1, $@"Local\KQ.Rfid.Alien.Stream.{readerHost}");
        if (semaphore.WaitOne(0))
            return new ReaderLock(semaphore);
        semaphore.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (!_held) return;
        _held = false;
        try { _semaphore.Release(); } catch { }
        _semaphore.Dispose();
    }
}

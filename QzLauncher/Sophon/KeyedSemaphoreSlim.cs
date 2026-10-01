using System.Collections.Concurrent;

namespace FufuLauncher.Helpers;

public sealed class KeyedSemaphoreSlim
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.Ordinal);
    
    public async ValueTask<IDisposable> LockAsync(string key, CancellationToken token = default)
    {
        SemaphoreSlim semaphore = _semaphores.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(token).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }
        }
    }
}

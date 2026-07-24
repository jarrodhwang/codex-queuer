namespace CodexQueue.Api.Services;

public interface IProviderConcurrencyGate
{
    bool TryAcquire(string resourceKey, int maximumConcurrency, out IDisposable? lease);
    int ActiveCount(string resourceKey);
}

public sealed class ProviderConcurrencyGate : IProviderConcurrencyGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(string resourceKey, int maximumConcurrency, out IDisposable? lease)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            throw new ArgumentException("Provider resource key is required.", nameof(resourceKey));
        }

        if (maximumConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency),
                "Provider maximum concurrency must be at least one.");
        }

        var normalizedKey = resourceKey.Trim();
        lock (_sync)
        {
            var active = _active.GetValueOrDefault(normalizedKey);
            if (active >= maximumConcurrency)
            {
                lease = null;
                return false;
            }

            _active[normalizedKey] = active + 1;
        }

        lease = new ProviderLease(this, normalizedKey);
        return true;
    }

    public int ActiveCount(string resourceKey)
    {
        lock (_sync)
        {
            return _active.GetValueOrDefault(resourceKey.Trim());
        }
    }

    private void Release(string resourceKey)
    {
        lock (_sync)
        {
            var active = _active.GetValueOrDefault(resourceKey);
            if (active <= 1)
            {
                _active.Remove(resourceKey);
            }
            else
            {
                _active[resourceKey] = active - 1;
            }
        }
    }

    private sealed class ProviderLease(ProviderConcurrencyGate owner, string resourceKey) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(resourceKey);
            }
        }
    }
}

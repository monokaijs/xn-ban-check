using System.Collections.Concurrent;

namespace BanCheckPlugin.Utils;

public sealed class TimedCache<TKey, TValue> where TKey : notnull
{
    private sealed record Entry(TValue Value, DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<TKey, Entry> _map = new();
    private readonly TimeSpan _ttl;

    public TimedCache(TimeSpan ttl)
    {
        _ttl = ttl;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        value = default!;
        if (!_map.TryGetValue(key, out var entry)) return false;

        if (DateTime.UtcNow > entry.ExpiresAtUtc)
        {
            _map.TryRemove(key, out _);
            return false;
        }

        value = entry.Value;
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        _map[key] = new Entry(value, DateTime.UtcNow.Add(_ttl));
    }

    public void Remove(TKey key) => _map.TryRemove(key, out _);

    public void Clear() => _map.Clear();
}

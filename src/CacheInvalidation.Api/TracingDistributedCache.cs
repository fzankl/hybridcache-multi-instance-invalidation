using Microsoft.Extensions.Caching.Distributed;

namespace CacheInvalidation.Api;

// Records every L2 call made while a request runs, so a read can say where its value came from.
public sealed record L2Call(string Operation, string Key, bool Hit);

public static class L2Trace
{
    public static readonly AsyncLocal<List<L2Call>?> Current = new();

    private static void Record(string operation, string key, bool hit) =>
        Current.Value?.Add(new L2Call(operation, key, hit));

    public sealed class Cache(IDistributedCache inner) : IDistributedCache
    {
        public byte[]? Get(string key)
        {
            var value = inner.Get(key);
            Record("get", key, value is not null);
            return value;
        }

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            var value = await inner.GetAsync(key, token);
            Record("get", key, value is not null);
            return value;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            inner.Set(key, value, options);
            Record("set", key, true);
        }

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            await inner.SetAsync(key, value, options, token);
            Record("set", key, true);
        }

        public void Refresh(string key)
        {
            inner.Refresh(key);
            Record("refresh", key, true);
        }

        public async Task RefreshAsync(string key, CancellationToken token = default)
        {
            await inner.RefreshAsync(key, token);
            Record("refresh", key, true);
        }

        public void Remove(string key)
        {
            inner.Remove(key);
            Record("remove", key, true);
        }

        public async Task RemoveAsync(string key, CancellationToken token = default)
        {
            await inner.RemoveAsync(key, token);
            Record("remove", key, true);
        }
    }
}

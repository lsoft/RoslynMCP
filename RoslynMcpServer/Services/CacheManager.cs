using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoslynMcpServer.Services
{
    public interface IPersistentCache
    {
        // Found tells a miss apart from a stored default value (0, false, null)
        Task<(bool Found, T? Value)> TryGetAsync<T>(string key);
        Task SetAsync<T>(string key, T value, TimeSpan? expiry = null);
        Task RemoveAsync(string key);
    }

    public class FilePersistentCache : IPersistentCache
    {
        private readonly string _cacheDirectory;

        private sealed class CacheEntry<T>
        {
            public DateTimeOffset? ExpiresAt { get; set; }
            public T? Value { get; set; }
        }

        public FilePersistentCache(string cacheDirectory = "cache")
        {
            _cacheDirectory = cacheDirectory;
            Directory.CreateDirectory(_cacheDirectory);
        }

        public async Task<(bool Found, T? Value)> TryGetAsync<T>(string key)
        {
            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
                return (false, default);

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var entry = JsonSerializer.Deserialize<CacheEntry<T>>(json);
                if (entry == null)
                    return (false, default);

                if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    await RemoveAsync(key);
                    return (false, default);
                }

                return (true, entry.Value);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return (false, default);
            }
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
        {
            var filePath = GetFilePath(key);
            var entry = new CacheEntry<T>
            {
                ExpiresAt = expiry.HasValue ? DateTimeOffset.UtcNow + expiry.Value : null,
                Value = value
            };
            var json = JsonSerializer.Serialize(entry);
            await File.WriteAllTextAsync(filePath, json);
        }

        public Task RemoveAsync(string key)
        {
            var filePath = GetFilePath(key);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            return Task.CompletedTask;
        }

        private string GetFilePath(string key)
        {
            // Hash instead of replacing invalid characters: "a/b" and "a:b" must not share a file,
            // and keys may be longer than a file name can be
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            return Path.Combine(_cacheDirectory, $"{hash}.cache");
        }
    }

    public class MultiLevelCacheManager
    {
        private readonly IMemoryCache _l1Cache; // Hot data - in memory
        private readonly IDistributedCache? _l2Cache; // Warm data - Redis/SQL (optional)
        private readonly IPersistentCache _l3Cache; // Cold data - file system
        
        public MultiLevelCacheManager(
            IMemoryCache memoryCache,
            IDistributedCache? distributedCache = null,
            IPersistentCache? persistentCache = null)
        {
            _l1Cache = memoryCache;
            _l2Cache = distributedCache;
            _l3Cache = persistentCache ?? new FilePersistentCache();
        }
        
        public async Task<T?> GetOrComputeAsync<T>(
            string key, 
            Func<Task<T>> computeFunc,
            TimeSpan? l1Expiry = null,
            TimeSpan? l2Expiry = null)
        {
            // L1 Cache check
            if (_l1Cache.TryGetValue(key, out T? value) && value != null)
            {
                return value;
            }

            // L2 Cache check (if available)
            if (_l2Cache != null)
            {
                var serializedValue = await _l2Cache.GetStringAsync(key);
                if (serializedValue != null)
                {
                    try
                    {
                        value = JsonSerializer.Deserialize<T>(serializedValue);
                    }
                    catch (JsonException)
                    {
                        // Corrupted or written by an incompatible version, treat as a miss
                        value = default;
                    }

                    if (value != null)
                    {
                        _l1Cache.Set(key, value, l1Expiry ?? TimeSpan.FromMinutes(10));
                        return value;
                    }
                }
            }

            // L3 Persistent cache check
            var (found, persisted) = await _l3Cache.TryGetAsync<T>(key);
            if (found && persisted != null)
            {
                await StoreInUpperCaches(key, persisted, l1Expiry, l2Expiry);
                return persisted;
            }
            
            // Compute and store at all levels
            value = await computeFunc();
            if (value != null)
            {
                await StoreInAllCaches(key, value, l1Expiry, l2Expiry);
            }
            
            return value;
        }
        
        private async Task StoreInUpperCaches<T>(string key, T value, TimeSpan? l1Expiry, TimeSpan? l2Expiry)
        {
            _l1Cache.Set(key, value, l1Expiry ?? TimeSpan.FromMinutes(10));
            
            if (_l2Cache != null)
            {
                var serializedValue = JsonSerializer.Serialize(value);
                await _l2Cache.SetStringAsync(key, serializedValue, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = l2Expiry ?? TimeSpan.FromHours(1)
                });
            }
        }
        
        private async Task StoreInAllCaches<T>(string key, T value, TimeSpan? l1Expiry, TimeSpan? l2Expiry)
        {
            await StoreInUpperCaches(key, value, l1Expiry, l2Expiry);
            await _l3Cache.SetAsync(key, value, TimeSpan.FromDays(7));
        }
        
        public async Task InvalidateAsync(string keyPattern)
        {
            // For simplicity, this implementation removes exact keys
            // A more sophisticated implementation would support pattern matching
            _l1Cache.Remove(keyPattern);
            
            if (_l2Cache != null)
            {
                await _l2Cache.RemoveAsync(keyPattern);
            }
            
            await _l3Cache.RemoveAsync(keyPattern);
        }
    }
}
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tests
{
    public class CacheManagerTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "RoslynMcpServer.Tests", Guid.NewGuid().ToString("N"));
        private readonly FilePersistentCache _fileCache;

        public CacheManagerTests()
        {
            _fileCache = new FilePersistentCache(_directory);
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }

        private static MemoryCache NewMemoryCache() => new(new MemoryCacheOptions());

        [Fact]
        public async Task FileCache_RoundTripsValues()
        {
            await _fileCache.SetAsync("key", new List<string> { "a", "b" });

            var (found, value) = await _fileCache.TryGetAsync<List<string>>("key");

            Assert.True(found);
            Assert.Equal(new[] { "a", "b" }, value);
        }

        [Fact]
        public async Task FileCache_MissIsNotFound()
        {
            var (found, value) = await _fileCache.TryGetAsync<int>("missing");

            Assert.False(found);
            Assert.Equal(0, value);
        }

        [Fact]
        public async Task FileCache_StoredDefaultValueIsFound()
        {
            await _fileCache.SetAsync("zero", 0);

            var (found, value) = await _fileCache.TryGetAsync<int>("zero");

            Assert.True(found);
            Assert.Equal(0, value);
        }

        [Theory]
        [InlineData("a/b", "a:b")]
        [InlineData("a/b", "a_b")]
        [InlineData("A", "a")]
        public async Task FileCache_KeysDoNotCollide(string first, string second)
        {
            // Regression: invalid file name characters were replaced with "_", so these shared a file
            await _fileCache.SetAsync(first, 1);
            await _fileCache.SetAsync(second, 2);

            Assert.Equal(1, (await _fileCache.TryGetAsync<int>(first)).Value);
            Assert.Equal(2, (await _fileCache.TryGetAsync<int>(second)).Value);
        }

        [Fact]
        public async Task FileCache_AcceptsVeryLongKeys()
        {
            var key = new string('k', 1000);
            await _fileCache.SetAsync(key, 42);

            Assert.Equal(42, (await _fileCache.TryGetAsync<int>(key)).Value);
        }

        [Fact]
        public async Task FileCache_HonoursExpiry()
        {
            // Regression: the expiry argument was ignored and entries lived forever
            await _fileCache.SetAsync("expired", 1, TimeSpan.FromMilliseconds(-1));
            await _fileCache.SetAsync("fresh", 2, TimeSpan.FromHours(1));

            Assert.False((await _fileCache.TryGetAsync<int>("expired")).Found);
            Assert.True((await _fileCache.TryGetAsync<int>("fresh")).Found);
        }

        [Fact]
        public async Task FileCache_ExpiredEntryIsDeleted()
        {
            await _fileCache.SetAsync("expired", 1, TimeSpan.FromMilliseconds(-1));
            await _fileCache.TryGetAsync<int>("expired");

            Assert.Empty(Directory.GetFiles(_directory));
        }

        [Fact]
        public async Task FileCache_RemoveDeletesEntry()
        {
            await _fileCache.SetAsync("key", 1);
            await _fileCache.RemoveAsync("key");
            await _fileCache.RemoveAsync("never-existed");

            Assert.False((await _fileCache.TryGetAsync<int>("key")).Found);
        }

        [Fact]
        public async Task FileCache_CorruptFileIsAMiss()
        {
            await _fileCache.SetAsync("key", 1);
            File.WriteAllText(Directory.GetFiles(_directory).Single(), "{ not json");

            Assert.False((await _fileCache.TryGetAsync<int>("key")).Found);
        }

        [Fact]
        public async Task GetOrCompute_ComputesValueTypesOnMiss()
        {
            // Regression: a persistent-cache miss returned default(int), so 0 came back without computing
            var manager = new MultiLevelCacheManager(NewMemoryCache(), persistentCache: _fileCache);

            var value = await manager.GetOrComputeAsync("answer", () => Task.FromResult(42));

            Assert.Equal(42, value);
        }

        [Fact]
        public async Task GetOrCompute_ComputesOnlyOnce()
        {
            var manager = new MultiLevelCacheManager(NewMemoryCache(), persistentCache: _fileCache);
            var calls = 0;

            await manager.GetOrComputeAsync("key", () => Task.FromResult(++calls));
            var second = await manager.GetOrComputeAsync("key", () => Task.FromResult(++calls));

            Assert.Equal(1, second);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task GetOrCompute_PersistentCacheSurvivesNewMemoryCache()
        {
            await new MultiLevelCacheManager(NewMemoryCache(), persistentCache: _fileCache)
                .GetOrComputeAsync("key", () => Task.FromResult("stored"));

            var restarted = new MultiLevelCacheManager(NewMemoryCache(), persistentCache: _fileCache);
            var value = await restarted.GetOrComputeAsync<string>("key", () => throw new InvalidOperationException("should not compute"));

            Assert.Equal("stored", value);
        }

        [Fact]
        public async Task GetOrCompute_UsesDistributedCache()
        {
            var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            await distributed.SetStringAsync("key", "\"from-l2\"");
            var manager = new MultiLevelCacheManager(NewMemoryCache(), distributed, _fileCache);

            var value = await manager.GetOrComputeAsync<string>("key", () => throw new InvalidOperationException("should not compute"));

            Assert.Equal("from-l2", value);
        }

        [Fact]
        public async Task GetOrCompute_CorruptDistributedEntryIsAMiss()
        {
            var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            await distributed.SetStringAsync("key", "{ not json");
            var manager = new MultiLevelCacheManager(NewMemoryCache(), distributed, _fileCache);

            var value = await manager.GetOrComputeAsync("key", () => Task.FromResult("computed"));

            Assert.Equal("computed", value);
            Assert.Equal("\"computed\"", await distributed.GetStringAsync("key"));
        }

        [Fact]
        public async Task GetOrCompute_NullResultIsNotCached()
        {
            var manager = new MultiLevelCacheManager(NewMemoryCache(), persistentCache: _fileCache);
            var calls = 0;

            await manager.GetOrComputeAsync<string?>("key", () => { calls++; return Task.FromResult<string?>(null); });
            await manager.GetOrComputeAsync<string?>("key", () => { calls++; return Task.FromResult<string?>(null); });

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task Invalidate_RemovesFromAllLevels()
        {
            var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            var manager = new MultiLevelCacheManager(NewMemoryCache(), distributed, _fileCache);
            await manager.GetOrComputeAsync("key", () => Task.FromResult("old"));

            await manager.InvalidateAsync("key");
            var value = await manager.GetOrComputeAsync("key", () => Task.FromResult("new"));

            Assert.Equal("new", value);
        }
    }
}

using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class StableFileHashCacheTests
{
    [Fact]
    public async Task Reuses_hash_until_revalidation_then_hashes_again()
    {
        using var library = new TemporaryLibrary();
        var path = library.Write("one");
        var clock = new TestClock();
        var hasher = new CountingHasher();
        var cache = new StableFileHashCache(clock);

        Assert.Equal("hash-1", await cache.GetHashAsync(path, hasher, default));
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal("hash-1", await cache.GetHashAsync(path, hasher, default));
        Assert.Equal(1, hasher.Count);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("hash-2", await cache.GetHashAsync(path, hasher, default));
        Assert.Equal(2, hasher.Count);
    }

    [Fact]
    public async Task Changed_fingerprint_rehashes_and_removed_paths_are_evicted()
    {
        using var library = new TemporaryLibrary();
        var first = library.Write("one");
        var second = library.Write("two", "Second.epub");
        var cache = new StableFileHashCache(new TestClock());
        var hasher = new CountingHasher();

        await cache.GetHashAsync(first, hasher, default);
        await cache.GetHashAsync(second, hasher, default);
        File.AppendAllText(first, " changed");
        Assert.Equal("hash-3", await cache.GetHashAsync(first, hasher, default));

        cache.Retain(new HashSet<string> { second });
        Assert.Equal("hash-4", await cache.GetHashAsync(first, hasher, default));
        Assert.Equal(4, hasher.Count);
    }

    [Fact]
    public async Task Failed_or_cancelled_hashes_are_not_cached()
    {
        using var library = new TemporaryLibrary();
        var path = library.Write("one");
        var clock = new TestClock();
        var cache = new StableFileHashCache(clock);
        var hasher = new CountingHasher { FailNext = true };

        await Assert.ThrowsAsync<IOException>(() => cache.GetHashAsync(path, hasher, default));
        Assert.Equal("hash-2", await cache.GetHashAsync(path, hasher, default));

        clock.Advance(TimeSpan.FromMinutes(5));
        using var cancellation = new CancellationTokenSource();
        var blockingHasher = new BlockingHasher();
        var pending = cache.GetHashAsync(path, blockingHasher, cancellation.Token);
        await blockingHasher.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("hash-3", await cache.GetHashAsync(path, hasher, default));
        Assert.Equal(3, hasher.Count);
    }

    [Fact]
    public async Task File_changed_during_hash_is_not_cached_or_returned_to_scanner()
    {
        using var library = new TemporaryLibrary();
        var path = library.Write("one");
        var cache = new StableFileHashCache(new TestClock());
        var mutationHasher = new CallbackHasher(() => File.AppendAllText(path, " changed"));

        await Assert.ThrowsAsync<IOException>(() => cache.GetHashAsync(path, mutationHasher, default));
        var hasher = new CountingHasher();
        Assert.Equal("hash-1", await cache.GetHashAsync(path, hasher, default));
        Assert.Equal(1, hasher.Count);
    }

    [Fact]
    public async Task Same_size_rewrite_with_preserved_timestamp_is_seen_after_revalidation()
    {
        using var library = new TemporaryLibrary();
        var path = library.Write("first");
        var file = new FileInfo(path);
        var originalModified = file.LastWriteTimeUtc;
        var clock = new TestClock();
        var cache = new StableFileHashCache(clock);
        var hasher = new FileHashService();

        var originalHash = await cache.GetHashAsync(path, hasher, default);
        File.WriteAllText(path, "other");
        File.SetLastWriteTimeUtc(path, originalModified);
        Assert.Equal(originalHash, await cache.GetHashAsync(path, hasher, default));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.NotEqual(originalHash, await cache.GetHashAsync(path, hasher, default));
    }

    [Fact]
    public async Task Capacity_evicts_oldest_insertion_even_when_clock_timestamps_tie()
    {
        using var library = new TemporaryLibrary();
        var first = library.Write("one");
        var second = library.Write("two", "Second.epub");
        var third = library.Write("three", "Third.epub");
        var cache = new StableFileHashCache(new TestClock(), capacity: 2);
        var hasher = new CountingHasher();

        await cache.GetHashAsync(first, hasher, default);
        await cache.GetHashAsync(second, hasher, default);
        await cache.GetHashAsync(third, hasher, default);
        Assert.Equal("hash-4", await cache.GetHashAsync(first, hasher, default));
        Assert.Equal("hash-3", await cache.GetHashAsync(third, hasher, default));
        Assert.Equal(4, hasher.Count);
    }

    private sealed class CountingHasher : IFileHashService
    {
        public int Count { get; private set; }
        public bool FailNext { get; set; }

        public Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Controlled hash failure");
            }
            return Task.FromResult($"hash-{Count}");
        }
    }

    private sealed class CallbackHasher(Action callback) : IFileHashService
    {
        public Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            callback();
            return Task.FromResult("changed-during-hash");
        }
    }

    private sealed class BlockingHasher : IFileHashService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "unreachable";
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now = now.Add(amount);
    }
}

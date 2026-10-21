namespace Paparr.API.Services;

public sealed class StableFileHashCache(TimeProvider clock, int capacity = 512)
{
    private static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(PathComparer);
    private long nextSequence;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public async Task<string> GetHashAsync(string path, IFileHashService hashes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        file.Refresh();
        var size = file.Length;
        var modified = file.LastWriteTimeUtc;
        var now = clock.GetUtcNow();

        lock (gate)
        {
            if (entries.TryGetValue(fullPath, out var cached))
            {
                if (cached.Size == size && cached.Modified == modified && now - cached.HashedAt < RevalidationInterval)
                    return cached.Hash;
                entries.Remove(fullPath);
            }
        }

        var hash = await hashes.ComputeHashAsync(fullPath, ct);
        ct.ThrowIfCancellationRequested();

        file.Refresh();
        if (!file.Exists || file.Length != size || file.LastWriteTimeUtc != modified)
            throw new IOException("The file changed while its hash was being computed.");

        lock (gate)
        {
            entries[fullPath] = new Entry(size, modified, clock.GetUtcNow(), hash, ++nextSequence);
            while (entries.Count > Math.Max(0, capacity))
            {
                var oldest = entries.MinBy(pair => pair.Value.Sequence).Key;
                entries.Remove(oldest);
            }
        }

        return hash;
    }

    public void Retain(ISet<string> paths)
    {
        var retained = new HashSet<string>(paths.Select(Path.GetFullPath), PathComparer);
        lock (gate)
        {
            foreach (var path in entries.Keys.Where(path => !retained.Contains(path)).ToArray())
                entries.Remove(path);
        }
    }

    private sealed record Entry(long Size, DateTime Modified, DateTimeOffset HashedAt, string Hash, long Sequence);
}

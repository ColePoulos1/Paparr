using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Paparr.API.Services;

var parsed = ParsedArguments.Parse(args);
if (parsed.Error is not null)
{
    Console.Error.WriteLine(parsed.Error);
    Console.Error.WriteLine("Usage: Paparr.Benchmarks [--files 1..256] [--size-mib 1..16] [--passes 2..30] [--output <json-path>]");
    return 2;
}

var options = parsed.Options!;
var tempRoot = Path.Combine(Path.GetTempPath(), $"PaparrDiscoveryBenchmark-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempRoot);

try
{
    var files = CreateFiles(tempRoot, options.Files, options.SizeMiB);
    var warmup = Path.Combine(tempRoot, "warmup.bin");
    await File.WriteAllBytesAsync(warmup, new byte[64 * 1024]);
    var realHasher = new FileHashService();
    await realHasher.ComputeHashAsync(warmup);

    var uncachedHasher = new CountingHasher(realHasher);
    var uncached = await MeasureAsync(files, options.Passes, uncachedHasher, cache: null);

    var cachedHasher = new CountingHasher(realHasher);
    var cache = new StableFileHashCache(TimeProvider.System);
    var cached = await MeasureAsync(files, options.Passes, cachedHasher, cache);

    var result = new BenchmarkResult(
        Tool: "Paparr discovery hash comparison",
        Runtime: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
        ProcessorCount: Environment.ProcessorCount,
        Configuration: new Configuration(options.Files, options.SizeMiB, options.Passes,
            (long)options.Files * options.SizeMiB * 1024 * 1024),
        Correctness: new Correctness(uncached.Aggregate, cached.Aggregate,
            string.Equals(uncached.Aggregate, cached.Aggregate, StringComparison.Ordinal)),
        Uncached: new Measurement(uncached.Elapsed.TotalMilliseconds, uncachedHasher.HashCalls,
            uncachedHasher.BytesHashed),
        Cached: new Measurement(cached.Elapsed.TotalMilliseconds, cachedHasher.HashCalls,
            cachedHasher.BytesHashed));

    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (options.Output is not null)
    {
        var outputPath = Path.GetFullPath(options.Output);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, result, new JsonSerializerOptions { WriteIndented = true });
        await output.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine));
    }

    return result.Correctness.HashesMatch ? 0 : 1;
}
finally
{
    var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var fullTempRoot = Path.GetFullPath(tempRoot);
    if (fullTempRoot.StartsWith(expectedParent, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal))
        Directory.Delete(fullTempRoot, recursive: true);
}

static string[] CreateFiles(string root, int count, int sizeMiB)
{
    var paths = new string[count];
    var buffer = new byte[64 * 1024];
    for (var index = 0; index < count; index++)
    {
        var path = Path.Combine(root, $"synthetic-{index:D3}.bin");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var random = new Random(index + 1729);
        var remaining = (long)sizeMiB * 1024 * 1024;
        while (remaining > 0)
        {
            random.NextBytes(buffer);
            var length = (int)Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, length);
            remaining -= length;
        }
        paths[index] = path;
    }
    return paths;
}

static async Task<RunResult> MeasureAsync(string[] paths, int passes, IFileHashService hasher,
    StableFileHashCache? cache)
{
    var stopwatch = Stopwatch.StartNew();
    using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var encodedHash = new byte[64];
    for (var pass = 0; pass < passes; pass++)
    {
        if (cache is not null) cache.Retain(paths.ToHashSet());
        foreach (var path in paths)
        {
            var hash = cache is null
                ? await hasher.ComputeHashAsync(path)
                : await cache.GetHashAsync(path, hasher, CancellationToken.None);
            Encoding.ASCII.GetBytes(hash, encodedHash);
            aggregate.AppendData(encodedHash);
        }
    }
    stopwatch.Stop();
    return new RunResult(stopwatch.Elapsed, Convert.ToHexString(aggregate.GetHashAndReset()));
}

sealed class CountingHasher(IFileHashService inner) : IFileHashService
{
    private long hashCalls;
    private long bytesHashed;
    public long HashCalls => Interlocked.Read(ref hashCalls);
    public long BytesHashed => Interlocked.Read(ref bytesHashed);

    public async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var size = new FileInfo(filePath).Length;
        var hash = await inner.ComputeHashAsync(filePath, cancellationToken);
        Interlocked.Increment(ref hashCalls);
        Interlocked.Add(ref bytesHashed, size);
        return hash;
    }
}

sealed record RunResult(TimeSpan Elapsed, string Aggregate);
sealed record Configuration(int Files, int SizeMiB, int Passes, long SyntheticBytes);
sealed record Correctness(string UncachedAggregate, string CachedAggregate, bool HashesMatch);
sealed record Measurement(double ElapsedMilliseconds, long HashCalls, long BytesHashed);
sealed record BenchmarkResult(string Tool, string Runtime, string OperatingSystem, string Architecture,
    int ProcessorCount, Configuration Configuration, Correctness Correctness, Measurement Uncached, Measurement Cached);

sealed record Options(int Files, int SizeMiB, int Passes, string? Output);
sealed record ParsedArguments(Options? Options, string? Error)
{
    public static ParsedArguments Parse(string[] args)
    {
        var files = 32;
        var sizeMiB = 1;
        var passes = 6;
        string? output = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (index + 1 >= args.Length) return new(null, $"Missing value for {args[index]}.");
            var value = args[++index];
            switch (args[index - 1])
            {
                case "--files" when int.TryParse(value, out files) && files is >= 1 and <= 256:
                case "--size-mib" when int.TryParse(value, out sizeMiB) && sizeMiB is >= 1 and <= 16:
                case "--passes" when int.TryParse(value, out passes) && passes is >= 2 and <= 30:
                    break;
                case "--output" when !string.IsNullOrWhiteSpace(value):
                    output = value;
                    break;
                case "--files": return new(null, "--files must be from 1 through 256.");
                case "--size-mib": return new(null, "--size-mib must be from 1 through 16.");
                case "--passes": return new(null, "--passes must be from 2 through 30.");
                case "--output": return new(null, "--output requires a path.");
                default: return new(null, $"Unknown option or invalid value: {args[index - 1]} {value}.");
            }
        }

        if ((long)files * sizeMiB > 512) return new(null, "The total synthetic file size must not exceed 512 MiB.");
        return new(new Options(files, sizeMiB, passes, output), null);
    }
}

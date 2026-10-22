namespace Paparr.IngestionBenchmarks;

public sealed record BenchmarkOptions(int Files, int ContentMiB, string? Output)
{
    public const long MaximumInputBytes = 512L * 1024 * 1024;
    public int ContentBytesPerFile => ContentMiB * 1024 * 1024;
    public long RequestedContentBytes => (long)Files * ContentBytesPerFile;
}

public sealed record BenchmarkArguments(BenchmarkOptions? Options, string? Error)
{
    public static BenchmarkArguments Parse(string[] args)
    {
        var files = 10;
        var contentMiB = 0;
        string? output = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var flag = args[index];
            if (flag is not ("--files" or "--content-mib" or "--output"))
                return new(null, $"Unknown option: {flag}.");
            if (!seen.Add(flag)) return new(null, $"Duplicate option: {flag}.");
            if (++index >= args.Length) return new(null, $"Missing value for {flag}.");
            var value = args[index];
            if (flag == "--files" && int.TryParse(value, out files) && files is >= 2 and <= 100) continue;
            if (flag == "--content-mib" && int.TryParse(value, out contentMiB) && contentMiB is >= 0 and <= 4) continue;
            if (flag == "--output" && !string.IsNullOrWhiteSpace(value)) { output = value; continue; }
            return new(null, $"Invalid value for {flag}.");
        }
        var options = new BenchmarkOptions(files, contentMiB, output);
        if (options.RequestedContentBytes > BenchmarkOptions.MaximumInputBytes)
            return new(null, "Requested input content exceeds the 512 MiB limit.");
        return new(options, null);
    }
}

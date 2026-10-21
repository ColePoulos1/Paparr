using System.Text;
using System.Text.Json;

namespace Paparr.API.Services;

public sealed class MetadataProviderGateway(TimeProvider clock, ILogger<MetadataProviderGateway> logger)
{
    private const int MaximumCacheEntries = 128;
    private const int MaximumInFlightRequests = 8;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const long MaximumCacheBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumCooldown = TimeSpan.FromMinutes(5);

    private readonly object gate = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<string?>> inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private long cacheBytes;
    private long cacheSequence;

    public async Task<string?> GetAsync(HttpClient client, string url, string provider, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskCompletionSource<string?> pending;
        var startFetch = false;
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (cache.TryGetValue(url, out var cached))
            {
                if (cached.ExpiresAt > now) return cached.Body;
                RemoveCacheEntry(url);
            }

            if (inFlight.TryGetValue(url, out pending!))
            {
                // A caller can stop waiting without canceling work shared with other callers.
            }
            else
            {
                if (inFlight.Count >= MaximumInFlightRequests)
                {
                    logger.LogWarning("Metadata provider request limit reached; skipping {Provider} request", provider);
                    return null;
                }
                if (cooldowns.TryGetValue(provider, out var until))
                {
                    if (until > now) return null;
                    cooldowns.Remove(provider);
                }

                pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                inFlight.Add(url, pending);
                startFetch = true;
            }
        }

        if (startFetch) _ = FetchAndCompleteAsync(client, url, provider, pending);
        return await pending.Task.WaitAsync(ct);
    }

    private async Task FetchAndCompleteAsync(HttpClient client, string url, string provider,
        TaskCompletionSource<string?> pending)
    {
        string? body = null;
        using var timeout = new CancellationTokenSource(RequestTimeout, clock);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                SetCooldown(provider, RetryAfter(response));
                logger.LogWarning("Metadata provider {Provider} is rate limited", provider);
            }
            else if (response.IsSuccessStatusCode)
            {
                body = await ReadBoundedBodyAsync(response, timeout.Token);
                if (body is not null)
                {
                    try
                    {
                        using var _ = JsonDocument.Parse(body);
                        Cache(url, body);
                    }
                    catch (JsonException ex)
                    {
                        body = null;
                        SetCooldown(provider, DefaultCooldown);
                        logger.LogWarning(ex, "Metadata provider {Provider} returned invalid JSON", provider);
                    }
                }
            }
            else
            {
                if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
                    SetCooldown(provider, DefaultCooldown);
                logger.LogWarning("Metadata provider {Provider} returned {Status}", provider, response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            SetCooldown(provider, DefaultCooldown);
            logger.LogWarning(ex, "Metadata provider {Provider} request failed", provider);
        }
        catch (OperationCanceledException ex)
        {
            SetCooldown(provider, DefaultCooldown);
            logger.LogWarning(ex, "Metadata provider {Provider} request timed out", provider);
        }
        catch (IOException ex)
        {
            SetCooldown(provider, DefaultCooldown);
            logger.LogWarning(ex, "Metadata provider {Provider} response could not be read", provider);
        }
        catch (Exception ex)
        {
            SetCooldown(provider, DefaultCooldown);
            logger.LogWarning(ex, "Metadata provider {Provider} request failed unexpectedly", provider);
        }
        finally
        {
            lock (gate)
            {
                if (inFlight.TryGetValue(url, out var current) && ReferenceEquals(current, pending))
                    inFlight.Remove(url);
            }
            pending.TrySetResult(body);
        }
    }

    private TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta) return Clamp(delta);
        if (retry?.Date is { } date) return Clamp(date - clock.GetUtcNow());
        return DefaultCooldown;
    }

    private static TimeSpan Clamp(TimeSpan value) =>
        value <= TimeSpan.Zero ? TimeSpan.Zero : value > MaximumCooldown ? MaximumCooldown : value;

    private async Task<string?> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            logger.LogWarning("Metadata provider response exceeded the size limit");
            return null;
        }

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
            {
                logger.LogWarning("Metadata provider response exceeded the size limit");
                return null;
            }
            output.Write(buffer, 0, read);
        }

        var encoding = Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { }
        }
        var bytes = output.ToArray();
        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private void Cache(string url, string body)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in cache.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                RemoveCacheEntry(key);

            if (cache.TryGetValue(url, out var previous))
            {
                cacheBytes -= previous.SizeBytes;
                cache.Remove(url);
            }

            var sizeBytes = (long)body.Length * sizeof(char);
            while (cache.Count >= MaximumCacheEntries || cacheBytes + sizeBytes > MaximumCacheBytes)
            {
                var oldest = cache.MinBy(pair => pair.Value.Sequence).Key;
                RemoveCacheEntry(oldest);
            }

            cache[url] = new CacheEntry(body, now + CacheLifetime, sizeBytes, ++cacheSequence);
            cacheBytes += sizeBytes;
        }
    }

    private void RemoveCacheEntry(string url)
    {
        if (cache.Remove(url, out var removed)) cacheBytes -= removed.SizeBytes;
    }

    private void SetCooldown(string provider, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        lock (gate)
        {
            var until = clock.GetUtcNow() + duration;
            if (!cooldowns.TryGetValue(provider, out var existing) || existing < until)
                cooldowns[provider] = until;
        }
    }

    private sealed record CacheEntry(string Body, DateTimeOffset ExpiresAt, long SizeBytes, long Sequence);
}

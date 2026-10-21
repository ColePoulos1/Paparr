using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class MetadataProviderGatewayTests
{
    [Fact]
    public async Task Successful_body_is_cached_until_ttl_expires()
    {
        var clock = new TestClock();
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            return Ok("{\"version\":1}");
        });
        var gateway = Gateway(clock);

        Assert.Equal("{\"version\":1}", await gateway.GetAsync(client, "https://provider.test/book", "books", default));
        Assert.Equal("{\"version\":1}", await gateway.GetAsync(client, "https://provider.test/book", "books", default));
        Assert.Equal(1, requests);

        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal("{\"version\":1}", await gateway.GetAsync(client, "https://provider.test/book", "books", default));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Cache_byte_budget_evicts_oldest_large_response_and_keeps_recent_entry()
    {
        var largeJson = System.Text.Json.JsonSerializer.Serialize(new string('x', 1_100_000));
        var requests = new Dictionary<string, int>();
        using var client = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests[path] = requests.GetValueOrDefault(path) + 1;
            return Ok(largeJson);
        });
        var gateway = Gateway(new TestClock());

        for (var i = 0; i < 8; i++)
            Assert.Equal(largeJson, await gateway.GetAsync(client, $"https://provider.test/{i}", "books", default));

        Assert.Equal(largeJson, await gateway.GetAsync(client, "https://provider.test/0", "books", default));
        Assert.Equal(largeJson, await gateway.GetAsync(client, "https://provider.test/7", "books", default));
        Assert.Equal(2, requests["/0"]);
        Assert.Equal(1, requests["/7"]);
    }

    [Fact]
    public async Task Invalid_json_is_not_cached()
    {
        var clock = new TestClock();
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            return Ok(requests == 1 ? "not-json" : "{}");
        });
        var gateway = Gateway(clock);

        Assert.Null(await gateway.GetAsync(client, "https://provider.test/book", "books", default));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("{}", await gateway.GetAsync(client, "https://provider.test/book", "books", default));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Cache_keys_are_urls_and_providers_have_independent_cooldowns()
    {
        var clock = new TestClock();
        var requests = new List<string>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath == "/limited"
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : Ok(Json(request.RequestUri.AbsolutePath));
        });
        var gateway = Gateway(clock);

        Assert.Null(await gateway.GetAsync(client, "https://provider.test/limited", "alpha", default));
        Assert.Null(await gateway.GetAsync(client, "https://provider.test/another", "alpha", default));
        Assert.Equal(Json("/other-provider"), await gateway.GetAsync(client, "https://provider.test/other-provider", "beta", default));
        Assert.Equal(new[] { "/limited", "/other-provider" }, requests);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(Json("/another"), await gateway.GetAsync(client, "https://provider.test/another", "alpha", default));
        Assert.Equal(Json("/another"), await gateway.GetAsync(client, "https://provider.test/another", "beta", default));
        Assert.Equal(new[] { "/limited", "/other-provider", "/another" }, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rate_limit_honors_retry_after_and_expires_without_retries(bool useDate)
    {
        var clock = new TestClock();
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            if (requests == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = useDate
                    ? new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(8))
                    : new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return limited;
            }
            return Ok(Json("ready"));
        });
        var gateway = Gateway(clock);

        Assert.Null(await gateway.GetAsync(client, "https://provider.test/book", "books", default));
        Assert.Null(await gateway.GetAsync(client, "https://provider.test/different", "books", default));
        Assert.Equal(1, requests);

        clock.Advance(TimeSpan.FromSeconds(useDate ? 8 : 3));
        Assert.Equal(Json("ready"), await gateway.GetAsync(client, "https://provider.test/different", "books", default));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Transient_server_failure_puts_only_its_provider_into_default_cooldown()
    {
        var clock = new TestClock();
        var requests = 0;
        using var client = Client(request =>
        {
            requests++;
            return requests == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Ok(Json(request.RequestUri!.AbsolutePath));
        });
        var gateway = Gateway(clock);

        Assert.Null(await gateway.GetAsync(client, "https://provider.test/fails", "alpha", default));
        Assert.Null(await gateway.GetAsync(client, "https://provider.test/blocked", "alpha", default));
        Assert.Equal(Json("/independent"), await gateway.GetAsync(client, "https://provider.test/independent", "beta", default));
        Assert.Equal(2, requests);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(Json("/blocked"), await gateway.GetAsync(client, "https://provider.test/blocked", "alpha", default));
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task Simultaneous_requests_share_fetch_and_one_caller_can_cancel_its_wait()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var client = new HttpClient(new AsyncHandler(async (_, _) =>
        {
            Interlocked.Increment(ref requests);
            started.TrySetResult();
            return await release.Task;
        }));
        var gateway = Gateway(new TestClock());
        using var canceled = new CancellationTokenSource();

        var canceledWait = gateway.GetAsync(client, "https://provider.test/shared", "books", canceled.Token);
        await started.Task;
        var survivingWait = gateway.GetAsync(client, "https://provider.test/shared", "books", default);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        release.SetResult(Ok(Json("shared response")));

        Assert.Equal(Json("shared response"), await survivingWait);
        Assert.Equal(Json("shared response"), await gateway.GetAsync(client, "https://provider.test/shared", "books", default));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Hanging_response_body_is_canceled_by_the_overall_timeout()
    {
        var clock = new TestClock();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new SyncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HangingStream(readStarted))
        }));
        var gateway = Gateway(clock);

        var fetch = gateway.GetAsync(client, "https://provider.test/hangs", "books", default);
        await readStarted.Task;
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Null(await fetch.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Gateway_bounds_the_number_of_independent_in_flight_fetches()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var client = new HttpClient(new AsyncHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 8) started.TrySetResult();
            await release.Task;
            return Ok("{} ");
        }));
        var gateway = Gateway(new TestClock());
        var fetches = Enumerable.Range(0, 8)
            .Select(i => gateway.GetAsync(client, $"https://provider.test/{i}", "books", default))
            .ToArray();
        await started.Task;

        Assert.Null(await gateway.GetAsync(client, "https://provider.test/overflow", "books", default));
        Assert.Equal(8, requestCount);
        release.SetResult();
        Assert.All(await Task.WhenAll(fetches), body => Assert.Equal("{} ", body));
    }

    [Fact]
    public async Task Oversized_unknown_length_body_is_rejected_and_not_cached()
    {
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableStream(new MemoryStream(new byte[2 * 1024 * 1024 + 1])))
            };
        });
        var gateway = Gateway(new TestClock());

        Assert.Null(await gateway.GetAsync(client, "https://provider.test/large", "books", default));
        Assert.Null(await gateway.GetAsync(client, "https://provider.test/large", "books", default));
        Assert.Equal(2, requests);
    }

    private static MetadataProviderGateway Gateway(TestClock clock) =>
        new(clock, NullLogger<MetadataProviderGateway>.Instance);

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new SyncHandler(respond));

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private sealed class SyncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            now += duration;
            foreach (var timer in timers.ToArray()) timer.FireIfDue(now);
        }

        private sealed class ManualTimer(TestClock clock, TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period) : ITimer
        {
            private DateTimeOffset? due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
            private bool disposed;
            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (disposed) return false;
                due = newDueTime == Timeout.InfiniteTimeSpan ? null : clock.now + newDueTime;
                period = newPeriod;
                return true;
            }
            public void FireIfDue(DateTimeOffset now)
            {
                if (disposed || due is not { } scheduled || scheduled > now) return;
                if (period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero) due = null;
                else due = now + period;
                callback(state);
            }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class HangingStream(TaskCompletionSource readStarted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

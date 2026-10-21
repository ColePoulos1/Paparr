using Microsoft.Extensions.Options;
using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class WorkerDiagnosticsTests
{
    [Fact]
    public void Disabled_worker_reports_all_stages_in_stable_order()
    {
        var diagnostics = Create(enabled: false, new MutableClock());

        var snapshot = diagnostics.Snapshot();

        Assert.False(snapshot.Enabled);
        Assert.Equal("Disabled", snapshot.State);
        Assert.Equal(60, snapshot.StaleAfterSeconds);
        Assert.Equal(["queuedWork", "sourceCleanup", "libraryAudit", "discovery"],
            snapshot.Stages.Select(stage => stage.Key));
        Assert.All(snapshot.Stages, stage => Assert.Equal("NotRun", stage.State));
    }

    [Fact]
    public async Task Running_stage_updates_progress_and_uses_monotonic_duration()
    {
        var clock = new MutableClock();
        var diagnostics = Create(enabled: true, clock, pollingInterval: 7);
        diagnostics.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = diagnostics.RunStageAsync(WorkerStage.QueuedWork, default, async () =>
        {
            entered.SetResult();
            await finish.Task;
        });

        await entered.Task;
        var running = diagnostics.Snapshot();
        Assert.Equal("Running", running.State);
        Assert.Equal("queuedWork", running.CurrentStage);
        Assert.Equal("Running", running.Stages[0].State);
        clock.Advance(TimeSpan.FromMilliseconds(1250));
        finish.SetResult();
        await run;

        var succeeded = diagnostics.Snapshot();
        Assert.Equal("Idle", succeeded.State);
        Assert.Null(succeeded.CurrentStage);
        Assert.Equal("Succeeded", succeeded.Stages[0].State);
        Assert.Equal(1250, succeeded.Stages[0].LastDurationMs);
        Assert.Equal(clock.GetUtcNow(), succeeded.LastProgressAt);
        Assert.Equal(60, succeeded.StaleAfterSeconds);
    }

    [Fact]
    public async Task Failures_are_sanitized_and_success_preserves_failure_history()
    {
        var clock = new MutableClock();
        var diagnostics = Create(enabled: true, clock);
        var privatePath = Path.Combine(Path.GetTempPath(), "private-reader", "book.pdf");

        await Assert.ThrowsAsync<IOException>(() => diagnostics.RunStageAsync(WorkerStage.Discovery, default,
            () => throw new IOException($"Could not open {privatePath}")));
        var failed = diagnostics.Snapshot().Stages[3];
        Assert.Equal("Failed", failed.State);
        Assert.Equal("A required file or directory could not be accessed. Check the path and permissions.", failed.Error);
        Assert.DoesNotContain(privatePath, failed.Error, StringComparison.OrdinalIgnoreCase);
        var failureAt = failed.LastFailureAt;

        clock.Advance(TimeSpan.FromSeconds(2));
        await diagnostics.RunStageAsync(WorkerStage.Discovery, default, () => Task.CompletedTask);
        var recovered = diagnostics.Snapshot().Stages[3];
        Assert.Equal("Succeeded", recovered.State);
        Assert.Null(recovered.Error);
        Assert.Equal(failureAt, recovered.LastFailureAt);
        Assert.Equal(clock.GetUtcNow(), recovered.LastSuccessAt);
    }

    [Fact]
    public async Task Canceled_stage_is_not_reported_as_a_failure_and_stale_worker_is_delayed()
    {
        var clock = new MutableClock();
        var diagnostics = Create(enabled: true, clock);
        diagnostics.Start();
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("Delayed", diagnostics.Snapshot().State);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostics.RunStageAsync(
            WorkerStage.SourceCleanup, cancellation.Token, () => Task.CompletedTask));

        var snapshot = diagnostics.Snapshot();
        Assert.Equal("Canceled", snapshot.Stages[1].State);
        Assert.Null(snapshot.Stages[1].LastFailureAt);
        Assert.Null(snapshot.Stages[1].Error);
        Assert.Equal("Idle", snapshot.State);
    }

    [Fact]
    public void Delayed_state_uses_monotonic_time_even_before_first_start()
    {
        var clock = new MutableClock();
        var diagnostics = Create(enabled: true, clock);

        clock.JumpUtc(TimeSpan.FromHours(2));
        Assert.Equal("Starting", diagnostics.Snapshot().State);
        clock.JumpUtc(TimeSpan.FromHours(-4));
        Assert.Equal("Starting", diagnostics.Snapshot().State);
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(61));

        Assert.Equal("Delayed", diagnostics.Snapshot().State);
    }

    private static WorkerDiagnostics Create(bool enabled, TimeProvider clock, int pollingInterval = 20) =>
        new(Options.Create(new IngestionOptions
        {
            WorkerEnabled = enabled,
            PollingIntervalSeconds = pollingInterval
        }), clock);

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan amount)
        {
            JumpUtc(amount);
            AdvanceMonotonic(amount);
        }

        public void JumpUtc(TimeSpan amount) => now = now.Add(amount);
        public void AdvanceMonotonic(TimeSpan amount) => timestamp += amount.Ticks;
    }
}

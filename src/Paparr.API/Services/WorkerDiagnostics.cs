using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Paparr.API.Services;

public enum WorkerStage
{
    QueuedWork,
    SourceCleanup,
    LibraryAudit,
    Discovery
}

public sealed record WorkerStageSnapshot(string Key, string State, DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, double? LastDurationMs, string? Error);

public sealed record WorkerDiagnosticsSnapshot(bool Enabled, string State, DateTimeOffset StartedAt,
    DateTimeOffset? LastProgressAt, int StaleAfterSeconds, string? CurrentStage,
    IReadOnlyList<WorkerStageSnapshot> Stages);

public sealed class WorkerDiagnostics(IOptions<IngestionOptions> options, TimeProvider clock)
{
    private static readonly (WorkerStage Stage, string Key)[] StageDefinitions =
    [
        (WorkerStage.QueuedWork, "queuedWork"),
        (WorkerStage.SourceCleanup, "sourceCleanup"),
        (WorkerStage.LibraryAudit, "libraryAudit"),
        (WorkerStage.Discovery, "discovery")
    ];

    private readonly object gate = new();
    private readonly Dictionary<WorkerStage, StageState> stages = StageDefinitions
        .ToDictionary(item => item.Stage, _ => new StageState());
    private readonly bool enabled = options.Value.WorkerEnabled;
    private readonly int staleAfterSeconds = Math.Max(60, options.Value.PollingIntervalSeconds * 3);
    private DateTimeOffset startedAt = clock.GetUtcNow();
    private long startedTimestamp = clock.GetTimestamp();
    private DateTimeOffset? lastProgressAt;
    private long? lastProgressTimestamp;
    private string? currentStage;
    private bool started;

    public void Start()
    {
        lock (gate)
        {
            if (started) return;
            startedAt = clock.GetUtcNow();
            startedTimestamp = clock.GetTimestamp();
            started = true;
        }
    }

    public async Task RunStageAsync(WorkerStage stage, CancellationToken cancellationToken, Func<Task> action)
    {
        var startTimestamp = BeginStage(stage);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await action();
            CompleteStage(stage, startTimestamp, WorkerStageResult.Succeeded, null);
        }
        catch (OperationCanceledException)
        {
            CompleteStage(stage, startTimestamp, WorkerStageResult.Canceled, null);
            throw;
        }
        catch (Exception ex)
        {
            CompleteStage(stage, startTimestamp, WorkerStageResult.Failed, SafeError(ex));
            throw;
        }
    }

    public WorkerDiagnosticsSnapshot Snapshot()
    {
        lock (gate)
        {
            var referenceTimestamp = lastProgressTimestamp ?? startedTimestamp;
            var delayed = enabled && clock.GetElapsedTime(referenceTimestamp, clock.GetTimestamp()) >=
                TimeSpan.FromSeconds(staleAfterSeconds);
            var state = !enabled ? "Disabled"
                : delayed ? "Delayed"
                : !started ? "Starting"
                : currentStage is not null ? "Running"
                : lastProgressAt is null ? "Starting"
                : "Idle";

            var stageSnapshots = StageDefinitions.Select(definition =>
            {
                var value = stages[definition.Stage];
                return new WorkerStageSnapshot(definition.Key, value.State.ToString(), value.LastAttemptAt,
                    value.LastSuccessAt, value.LastFailureAt, value.LastDurationMs, value.Error);
            }).ToArray();

            return new WorkerDiagnosticsSnapshot(enabled, state, startedAt, lastProgressAt,
                staleAfterSeconds, currentStage, Array.AsReadOnly(stageSnapshots));
        }
    }

    private long BeginStage(WorkerStage stage)
    {
        var definition = StageDefinitions.FirstOrDefault(item => item.Stage == stage);
        if (definition.Key is null) throw new ArgumentOutOfRangeException(nameof(stage));
        lock (gate)
        {
            if (!started)
            {
                startedAt = clock.GetUtcNow();
                startedTimestamp = clock.GetTimestamp();
                started = true;
            }
            var now = clock.GetUtcNow();
            var value = stages[stage];
            value.State = WorkerStageResult.Running;
            value.LastAttemptAt = now;
            value.Error = null;
            var startTimestamp = clock.GetTimestamp();
            currentStage = definition.Key;
            lastProgressAt = now;
            lastProgressTimestamp = startTimestamp;
            return startTimestamp;
        }
    }

    private void CompleteStage(WorkerStage stage, long startTimestamp, WorkerStageResult result, string? error)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var value = stages[stage];
            value.State = result;
            value.LastDurationMs = Math.Max(0, clock.GetElapsedTime(startTimestamp).TotalMilliseconds);
            value.Error = error;
            if (result == WorkerStageResult.Succeeded) value.LastSuccessAt = now;
            if (result == WorkerStageResult.Failed) value.LastFailureAt = now;
            currentStage = null;
            lastProgressAt = now;
            lastProgressTimestamp = clock.GetTimestamp();
        }
    }

    private static string SafeError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException or DbUpdateException) return "A database operation failed. Check the server logs for details.";
            if (current is UnauthorizedAccessException) return "Access was denied. Check directory and database permissions.";
            if (current is IOException) return "A required file or directory could not be accessed. Check the path and permissions.";
        }
        return "An unexpected worker error occurred. Check server logs.";
    }

    private sealed class StageState
    {
        public WorkerStageResult State { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public DateTimeOffset? LastFailureAt { get; set; }
        public double? LastDurationMs { get; set; }
        public string? Error { get; set; }
    }

    private enum WorkerStageResult { NotRun, Running, Succeeded, Failed, Canceled }
}

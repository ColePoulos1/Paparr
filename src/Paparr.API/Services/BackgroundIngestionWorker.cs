using Paparr.API.Data;
using Paparr.API.Domain;
using Microsoft.EntityFrameworkCore;

namespace Paparr.API.Services;

public class BackgroundIngestionWorker : IBackgroundIngestionWorker
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackgroundIngestionWorker> _logger;
    private readonly string _ingestPath;
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerTask;

    public BackgroundIngestionWorker(
        IServiceProvider serviceProvider,
        ILogger<BackgroundIngestionWorker> logger,
        IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _ingestPath = config["IngestPath"] ?? "/ingest";
        _pollInterval = TimeSpan.FromSeconds(int.Parse(config["PollingIntervalSeconds"] ?? "30"));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = RunWorkerAsync(_cancellationTokenSource.Token);
        
        _logger.LogInformation("Background ingestion worker started. Polling {Path} every {Interval}s", 
            _ingestPath, _pollInterval.TotalSeconds);

        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cancellationTokenSource?.Cancel();
        if (_workerTask != null)
        {
            await _workerTask;
        }
        _logger.LogInformation("Background ingestion worker stopped");
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessIngestDirectoryAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in background worker poll cycle");
                }

                await Task.Delay(_pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Background worker cancellation requested");
        }
    }

    private async Task ProcessIngestDirectoryAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_ingestPath))
        {
            Directory.CreateDirectory(_ingestPath);
            return;
        }

        var files = Directory.GetFiles(_ingestPath, "*.{epub,pdf}", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) || 
                       f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Found {Count} files to process in {Path}", files.Count, _ingestPath);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hashService = scope.ServiceProvider.GetRequiredService<IFileHashService>();
        var ingestionService = scope.ServiceProvider.GetRequiredService<IEbookIngestionService>();

        foreach (var filePath in files)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var fileHash = await hashService.ComputeHashAsync(filePath);

                // Check if already imported
                var existingJob = await db.ImportJobs
                    .FirstOrDefaultAsync(j => j.FileHash == fileHash, cancellationToken);

                if (existingJob != null)
                {
                    _logger.LogWarning("File {Path} already imported (hash: {Hash})", filePath, fileHash);
                    File.Delete(filePath);
                    continue;
                }

                // Create new import job
                var job = new ImportJob
                {
                    FilePath = filePath,
                    FileHash = fileHash,
                    Status = ImportStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                db.ImportJobs.Add(job);
                await db.SaveChangesAsync(cancellationToken);

                // Process import job
                await ingestionService.ProcessImportJobAsync(job);

                _logger.LogInformation("Processed file: {FileName} (JobId: {JobId})", 
                    Path.GetFileName(filePath), job.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing file {Path}", filePath);
            }
        }
    }
}

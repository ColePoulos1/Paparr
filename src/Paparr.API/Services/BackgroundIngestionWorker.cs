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
        // Resolve ingest path to an absolute path
        string resolvedPath;
        try
        {
            if (Path.IsPathRooted(_ingestPath))
                resolvedPath = Path.GetFullPath(_ingestPath);
            else
                resolvedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _ingestPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve ingest path '{Path}'", _ingestPath);
            return;
        }

        _logger.LogDebug("Resolved ingest path: {ResolvedPath} (original: {Original})", resolvedPath, _ingestPath);

        if (!Directory.Exists(resolvedPath))
        {
            Directory.CreateDirectory(resolvedPath);
            _logger.LogInformation("Created ingest directory at {Path}", resolvedPath);
            return;
        }

        // Enumerate all files and filter by extension
        var allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".epub", ".pdf" };
        var files = Directory.EnumerateFiles(resolvedPath, "*", SearchOption.AllDirectories)
            .Where(f => allowedExts.Contains(Path.GetExtension(f)))
            .ToList();

        _logger.LogInformation("Found {Count} files to process in {Path}", files.Count, resolvedPath);

        if (files.Count == 0)
        {
            return;
        }

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
                // Check if already imported
                var fileHash = await hashService.ComputeHashAsync(filePath);
                var job = await db.ImportJobs
                    .FirstOrDefaultAsync(j => j.FileHash == fileHash, cancellationToken);

                // Delete file and ignore if already completed
                if (job?.Status == ImportStatus.Completed)
                {
                    _logger.LogWarning("File {Path} already imported (hash: {Hash})", filePath, fileHash);
                    File.Delete(filePath);
                    continue;
                }
                // Retry failed import jobs
                else if (job?.Status == ImportStatus.Failed)
                {
                    job.Status = ImportStatus.Pending;
                    job.FilePath = filePath;
                    job.UpdatedAt = DateTime.UtcNow;
                    db.ImportJobs.Update(job);
                }
                // Do nothing for Pending / Processing / Awaiting import jobs
                else if (job != null)
                {
                    continue;
                }
                // Create new job if none existing
                else
                {
                    job = new ImportJob
                    {
                        FilePath = filePath,
                        FileHash = fileHash,
                        Status = ImportStatus.Pending,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    db.ImportJobs.Add(job);
                }
                    
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

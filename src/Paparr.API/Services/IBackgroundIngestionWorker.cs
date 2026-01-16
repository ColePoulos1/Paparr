namespace Paparr.API.Services;

public interface IBackgroundIngestionWorker
{
    /// <summary>
    /// Starts the background worker that polls the ingest directory.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the background worker gracefully.
    /// </summary>
    Task StopAsync();
}

namespace Paparr.API.Services;

public interface IFileHashService
{
    Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default);
}

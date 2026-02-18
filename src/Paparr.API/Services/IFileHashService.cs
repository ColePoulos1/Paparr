namespace Paparr.API.Services;

public interface IFileHashService
{
    /// <summary>
    /// Computes SHA256 hash of a file.
    /// </summary>
    Task<string> ComputeHashAsync(string filePath);
}

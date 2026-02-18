using System.Security.Cryptography;

namespace Paparr.API.Services;

public class FileHashService : IFileHashService
{
    public async Task<string> ComputeHashAsync(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        
        var hash = await Task.Run(() => sha256.ComputeHash(stream));
        return Convert.ToHexString(hash);
    }
}

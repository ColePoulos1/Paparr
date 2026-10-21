namespace Paparr.API.Domain;

public sealed class DuplicateSource
{
    private DuplicateSource() { }

    public DuplicateSource(long importJobId, string filePath, DateTime discoveredAt)
    {
        ImportJobId = importJobId;
        FilePath = SourcePathIdentity.Canonicalize(filePath);
        PathKey = SourcePathIdentity.CreateKey(FilePath);
        DiscoveredAt = discoveredAt;
    }

    public long Id { get; private set; }
    public long ImportJobId { get; private set; }
    public ImportJob ImportJob { get; private set; } = null!;
    public string FilePath { get; private set; } = "";
    public string PathKey { get; private set; } = "";
    public DateTime DiscoveredAt { get; private set; }
}

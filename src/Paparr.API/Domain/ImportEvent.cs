namespace Paparr.API.Domain;

public class ImportEvent
{
    public long Id { get; set; }
    public long ImportJobId { get; set; }
    public required string Message { get; set; }
    public DateTime CreatedAt { get; set; }
}

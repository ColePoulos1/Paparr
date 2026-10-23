namespace Paparr.API.Domain;

public sealed class AuthSession
{
    public required string Id { get; set; }
    public required byte[] Ticket { get; set; }
    public DateTime ExpiresAt { get; set; }
}

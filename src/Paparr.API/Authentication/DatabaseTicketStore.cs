using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.API.Authentication;

public sealed class DatabaseTicketStore(IServiceScopeFactory scopes, TimeProvider clock) : ITicketStore
{
    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);
    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);
    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);
    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        await db.AuthSessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var session = new AuthSession
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Ticket = TicketSerializer.Default.Serialize(ticket),
            ExpiresAt = ticket.Properties.ExpiresUtc!.Value.UtcDateTime
        };
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bytes = TicketSerializer.Default.Serialize(ticket);
        var expiresAt = ticket.Properties.ExpiresUtc!.Value.UtcDateTime;
        // Renewal must not resurrect a session that another request signed out.
        await db.AuthSessions.Where(s => s.Id == key).ExecuteUpdateAsync(setters => setters
            .SetProperty(s => s.Ticket, bytes).SetProperty(s => s.ExpiresAt, expiresAt), cancellationToken);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.AuthSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == key, cancellationToken);
        if (session is null) return null;
        if (session.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
        {
            await db.AuthSessions.Where(s => s.Id == key).ExecuteDeleteAsync(cancellationToken);
            return null;
        }
        return TicketSerializer.Default.Deserialize(session.Ticket);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuthSessions
            .Where(s => s.Id == key).ExecuteDeleteAsync(cancellationToken);
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Authentication;
using Paparr.API.Data;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class TicketStoreTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Renewal_updates_identity_and_expiry_and_logout_removes_the_session()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<ITicketStore>();
        var firstExpiry = DateTimeOffset.UtcNow.AddHours(1);
        var key = await store.StoreAsync(CreateTicket("first owner", firstExpiry));

        Assert.Equal(64, key.Length);
        Assert.All(key, character => Assert.True(Uri.IsHexDigit(character)));
        var retrieved = await store.RetrieveAsync(key);
        Assert.NotNull(retrieved);
        Assert.Equal("first owner", retrieved.Principal.Identity?.Name);

        var renewal = CreateTicket("renewed owner", DateTimeOffset.UtcNow.AddHours(4));
        await store.RenewAsync(key, renewal);
        var renewed = await store.RetrieveAsync(key);
        Assert.NotNull(renewed);
        Assert.Equal("renewed owner", renewed.Principal.Identity?.Name);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.AuthSessions.AsNoTracking().SingleAsync(session => session.Id == key);
            Assert.Equal(renewal.Properties.ExpiresUtc!.Value.UtcDateTime, row.ExpiresAt);
        }

        await store.RemoveAsync(key);
        Assert.Null(await store.RetrieveAsync(key));
        using var finalScope = app.Services.CreateScope();
        Assert.False(await finalScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .AuthSessions.AnyAsync(session => session.Id == key));
    }

    [Fact]
    public async Task Expired_retrieval_deletes_the_persisted_row()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<ITicketStore>();
        var key = await store.StoreAsync(CreateTicket("expired owner", DateTimeOffset.UtcNow.AddHours(1)));

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.AuthSessions.SingleAsync(session => session.Id == key);
            row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.Null(await store.RetrieveAsync(key));
        using var verificationScope = app.Services.CreateScope();
        Assert.False(await verificationScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .AuthSessions.AnyAsync(session => session.Id == key));
    }

    [Fact]
    public async Task Renewal_after_remove_cannot_resurrect_a_session_even_when_requests_race()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<ITicketStore>();
        var key = await store.StoreAsync(CreateTicket("owner", DateTimeOffset.UtcNow.AddHours(1)));

        await Task.WhenAll(
            store.RemoveAsync(key),
            store.RenewAsync(key, CreateTicket("renewed owner", DateTimeOffset.UtcNow.AddHours(5))));

        Assert.Null(await store.RetrieveAsync(key));
        await store.RenewAsync(key, CreateTicket("late renewal", DateTimeOffset.UtcNow.AddHours(6)));
        Assert.Null(await store.RetrieveAsync(key));
        using var scope = app.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .AuthSessions.AnyAsync(session => session.Id == key));
    }

    private static AuthenticationTicket CreateTicket(string name, DateTimeOffset expiresAt)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties { ExpiresUtc = expiresAt };
        return new AuthenticationTicket(new ClaimsPrincipal(identity), properties,
            CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

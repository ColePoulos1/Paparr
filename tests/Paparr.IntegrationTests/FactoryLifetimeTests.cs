using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paparr.API.Data;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class FactoryLifetimeTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposing_a_factory_closes_its_database_connections_after_child_hosts_stop(bool asynchronous)
    {
        var app = await PaparrFactory.CreateAsync(postgres);
        var database = new NpgsqlConnectionStringBuilder(app.ConnectionString).Database!;
        await using var monitorSource = NpgsqlDataSource.Create(postgres.Container.GetConnectionString());
        await using var monitor = await monitorSource.OpenConnectionAsync();
        try
        {
            var id = await app.SeedAsync();
            using (var firstChild = app.WithWebHostBuilder(_ => { }))
            {
                using var client = firstChild.CreateClient();
                using var response = await client.GetAsync($"/api/imports/{id}");
                response.EnsureSuccessStatusCode();
            }
            // A child borrows the parent's data source; disposing it must leave the parent usable.
            Assert.Equal(id, (await app.ReadAsync(id)).Id);
            using var liveChild = app.WithWebHostBuilder(_ => { });
            using var liveClient = liveChild.CreateClient();
            using (var parentScope = app.Services.CreateScope())
            using (var childScope = liveChild.Services.CreateScope())
            {
                var parentDb = parentScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var childDb = childScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await parentDb.Database.OpenConnectionAsync();
                await childDb.Database.OpenConnectionAsync();
                Assert.Equal(1, await parentDb.ImportJobs.CountAsync());
                Assert.Equal(1, await childDb.ImportJobs.CountAsync());
                Assert.True(await CountConnectionsAsync(monitor, database) >= 2);
            }

            if (asynchronous) await app.DisposeAsync();
            else app.Dispose();

            await AssertConnectionsClosedAsync(monitor, database);
            Assert.False(Directory.Exists(app.Root));
        }
        finally { await app.DisposeAsync(); }
    }

    private static async Task<int> CountConnectionsAsync(NpgsqlConnection monitor, string database)
    {
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @database", monitor);
        command.Parameters.AddWithValue("database", database);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AssertConnectionsClosedAsync(NpgsqlConnection monitor, string database)
    {
        var remaining = await CountConnectionsAsync(monitor, database);
        if (remaining == 0) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try
        {
            while (remaining != 0 && await timer.WaitForNextTickAsync(timeout.Token))
                remaining = await CountConnectionsAsync(monitor, database);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        Assert.Equal(0, remaining);
    }
}

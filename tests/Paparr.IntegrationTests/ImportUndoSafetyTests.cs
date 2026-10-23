using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class ImportUndoSafetyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_retained_undo_retries_only_after_the_original_source_is_restored()
    {
        using var app = await FactoryAsync();
        var job = await ImportAsync(app);
        var original = await File.ReadAllBytesAsync(job.FilePath);
        var failure = new DestinationReadFailure(job.DestinationPath!);
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileHashService>();
            services.AddSingleton<IFileHashService>(failure);
        }));
        using var client = host.CreateClient();
        var operationId = Guid.NewGuid();
        using var started = await client.PostAsJsonAsync($"/api/imports/{job.Id}/undo", new { revision = job.Revision, operationId });
        started.EnsureSuccessStatusCode();
        Assert.Equal(ImportStatus.Undoing, (await app.ReadAsync(job.Id)).Status);
        await File.WriteAllTextAsync(job.FilePath, "changed source that must survive undo retries");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var operation = await db.ImportUndos.SingleAsync();
            operation.RetryAfter = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ImportUndoService>().RunBatchAsync(default);
        }
        using (var scope = host.Services.CreateScope())
        {
            var operation = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ImportUndos.SingleAsync();
            Assert.Equal(ImportUndoStatus.Retained, operation.Status);
            Assert.Equal(3, operation.AttemptCount);
            Assert.NotNull(operation.Error);
        }
        var blocked = (await client.GetFromJsonAsync<UndoPreviewDto>($"/api/imports/{job.Id}/undo-preview"))!;
        Assert.False(blocked.CanRetry);
        Assert.Equal("changed source that must survive undo retries", await File.ReadAllTextAsync(job.FilePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(job.DestinationPath!));

        await File.WriteAllBytesAsync(job.FilePath, original);
        var ready = (await client.GetFromJsonAsync<UndoPreviewDto>($"/api/imports/{job.Id}/undo-preview"))!;
        Assert.True(ready.CanRetry);
        Assert.Equal(operationId, ready.OperationId);
        using var retried = await client.PostAsJsonAsync($"/api/imports/{job.Id}/undo/retry", new { operationId });
        retried.EnsureSuccessStatusCode();
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(job.Id)).Status);
        Assert.False(File.Exists(job.DestinationPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(job.FilePath));
        using var verify = host.Services.CreateScope();
        var state = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await state.Books.ToListAsync());
        Assert.Equal(ImportUndoStatus.Completed, (await state.ImportUndos.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_rejects_a_replacement_directory_or_an_outside_library_path(bool outside)
    {
        using var app = await FactoryAsync();
        var job = await ImportAsync(app);
        var destination = job.DestinationPath!;
        string protectedPath;
        if (outside)
        {
            protectedPath = Path.Combine(app.Root, "outside.pdf");
            File.Copy(destination, protectedPath);
            await ChangeDestinationAsync(app, job.Id, protectedPath);
        }
        else
        {
            File.Delete(destination);
            Directory.CreateDirectory(destination);
            protectedPath = Path.Combine(destination, "keep.pdf");
            File.Copy(job.FilePath, protectedPath);
        }
        var revision = (await app.ReadAsync(job.Id)).Revision;
        using var client = app.CreateClient();

        var preview = (await client.GetFromJsonAsync<UndoPreviewDto>($"/api/imports/{job.Id}/undo-preview"))!;
        using var response = await client.PostAsJsonAsync($"/api/imports/{job.Id}/undo", new { revision, operationId = Guid.NewGuid() });

        Assert.False(preview.CanUndo);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(protectedPath));
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(job.FilePath));
        using var verify = app.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.ImportUndos.ToListAsync());
        Assert.Equal(1, await db.Books.CountAsync());
    }

    [Fact]
    public async Task Undo_does_not_follow_a_reparse_directory_to_a_matching_library_copy()
    {
        using var app = await FactoryAsync();
        var job = await ImportAsync(app);
        var target = Path.Combine(app.Root, "outside-target");
        Directory.CreateDirectory(target);
        var originalTarget = Path.Combine(target, "keep.pdf");
        File.Copy(job.FilePath, originalTarget);
        var link = Path.Combine(app.Library, "undo-link");
        await CreateDirectoryLinkAsync(link, target);
        try
        {
            await ChangeDestinationAsync(app, job.Id, Path.Combine(link, "keep.pdf"));
            var current = await app.ReadAsync(job.Id);
            using var client = app.CreateClient();
            var preview = (await client.GetFromJsonAsync<UndoPreviewDto>($"/api/imports/{job.Id}/undo-preview"))!;
            using var response = await client.PostAsJsonAsync($"/api/imports/{job.Id}/undo",
                new { revision = current.Revision, operationId = Guid.NewGuid() });

            Assert.False(preview.CanUndo);
            Assert.Null(preview.LibraryRelativePath);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(originalTarget));
            Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(job.FilePath));
            Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(job.DestinationPath!));
        }
        finally { Directory.Delete(link); }
    }

    private async Task<PaparrFactory> FactoryAsync()
    {
        var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["DeleteSourceAfterImport"] = "false";
        return app;
    }

    private static async Task<ImportJob> ImportAsync(PaparrFactory app)
    {
        var id = await app.SeedAsync();
        var job = await app.ReadAsync(id);
        using var client = app.CreateClient();
        using var response = await client.PostAsync($"/api/imports/{id}/accept/{job.Candidates.Single().Id}", null);
        response.EnsureSuccessStatusCode();
        return await app.ReadAsync(id);
    }

    private static async Task ChangeDestinationAsync(PaparrFactory app, long id, string destination)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ImportJobs.Include(j => j.AcceptedBook).SingleAsync(j => j.Id == id);
        job.DestinationPath = destination;
        job.AcceptedBook!.FilePath = destination;
        await db.SaveChangesAsync();
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    private sealed class DestinationReadFailure(string destination) : IFileHashService
    {
        private int calls;
        public Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default)
        {
            if (path == destination && Interlocked.Increment(ref calls) == 2)
                throw new IOException("Injected destination read failure after undo admission.");
            return new FileHashService().ComputeHashAsync(path, cancellationToken);
        }
    }
}

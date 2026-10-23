using Microsoft.EntityFrameworkCore;
using Npgsql;
using Paparr.API;
using Paparr.API.Authentication;
using Paparr.API.Data;
using Paparr.API.Services;
using Serilog;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

if (args is ["--hash-password"])
{
    Environment.ExitCode = OwnerPassword.RunHashCommand();
    return;
}

string? schemaPath = null;
if (args.Contains("--export-openapi"))
{
    if (args is not ["--export-openapi", var outputPath])
    {
        Console.Error.WriteLine("Usage: --export-openapi <output.json>");
        Environment.ExitCode = 1;
        return;
    }
    schemaPath = Path.GetFullPath(outputPath);
}
var builder = WebApplication.CreateBuilder(schemaPath is null ? args : []);
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});
builder.Host.UseSerilog((context, configuration) => configuration.ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console().Enrich.FromLogContext());
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SupportNonNullableReferenceTypes();
    options.NonNullableReferenceTypesAsRequired();
    options.UseAllOfToExtendReferenceSchemas();
    options.SchemaFilter<ResponseSchemaFilter>();
    options.SchemaFilter<RequestSchemaFilter>();
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOwnerAuthentication(builder.Configuration);
builder.Services.AddOptions<IngestionOptions>().Bind(builder.Configuration).ValidateDataAnnotations()
    .Validate(o => o.HasSeparateRoots(), "IngestPath and LibraryPath must be separate, non-overlapping directories.")
    .Validate(o => o.HasValidFolderProfiles(), "FolderProfiles must use unique names and relative ingest folders, portable collection names, valid filename orders, and paths without symbolic links or case aliases.")
    .ValidateOnStart();
builder.Services.AddDbContext<AppDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("DefaultConnection") ?? new NpgsqlConnectionStringBuilder
    {
        Host = configuration["Database:Host"] ?? "localhost",
        Database = configuration["Database:Name"] ?? "paparr",
        Username = configuration["Database:User"] ?? "paparr",
        Password = configuration["Database:Password"] ?? throw new InvalidOperationException(
            "Set Database__Password or ConnectionStrings__DefaultConnection.")
    }.ConnectionString;
    options.UseNpgsql(connectionString);
});
builder.Services.AddScoped<IMetadataService, MetadataService>();
builder.Services.AddScoped<IEbookIngestionService, EbookIngestionService>();
builder.Services.AddScoped<IFileHashService, FileHashService>();
builder.Services.AddScoped<ILibraryPublisher, LibraryPublisher>();
builder.Services.AddScoped<ImportScanner>();
builder.Services.AddScoped<SourceCleanupService>();
builder.Services.AddScoped<StagingService>();
builder.Services.AddScoped<ImportUndoService>();
builder.Services.AddScoped<LibraryAuditService>();
builder.Services.AddScoped<LibraryDownloadService>();
builder.Services.AddScoped<CalibreExportService>();
builder.Services.AddSingleton<FileStabilityTracker>();
builder.Services.AddSingleton<StableFileHashCache>();
builder.Services.AddSingleton<MetadataProviderGateway>();
builder.Services.AddSingleton<WorkerDiagnostics>();
builder.Services.AddHostedService<BackgroundIngestionWorker>();
builder.Services.AddHttpClient<IMetadataEnricherService, MetadataEnricherService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Paparr/0.2");
});
await using var app = builder.Build();
if (schemaPath is not null)
{
    // Export before host startup: no migrations, workers, or configured library access.
    var document = app.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
    await using var stream = File.CreateText(schemaPath);
    document.SerializeAsV3(new OpenApiJsonWriter(stream));
    return;
}
app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseRouting();
app.UseMiddleware<BrowserRequestGuard>();
app.UseMiddleware<SecureTransportGuard>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<CsrfGuard>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503)).AllowAnonymous();
if (app.Configuration.GetValue("Database:ApplyMigrations", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}
await app.RunAsync();

public partial class Program;

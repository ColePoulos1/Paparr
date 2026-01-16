using Paparr.API.Data;
using Paparr.API.Services;
using Serilog;

var builder = WebApplicationBuilder.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string not configured");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// Services
builder.Services.AddScoped<IMetadataService, MetadataService>();
builder.Services.AddScoped<IMetadataEnricherService, MetadataEnricherService>();
builder.Services.AddScoped<IEbookIngestionService, EbookIngestionService>();
builder.Services.AddScoped<IFileHashService, FileHashService>();
builder.Services.AddSingleton<IBackgroundIngestionWorker, BackgroundIngestionWorker>();

// HTTP client for external APIs
builder.Services.AddHttpClient<IMetadataEnricherService, MetadataEnricherService>();

// CORS for frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowUI", policy =>
    {
        policy.WithOrigins(builder.Configuration["AllowedOrigins"]?.Split(";") ?? ["http://localhost:5173", "http://localhost:3000"])
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowUI");
app.UseAuthorization();
app.MapControllers();

// Database migration
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Log.Information("Running database migrations...");
    await db.Database.MigrateAsync();
    Log.Information("Database migrations completed");
}

// Start background worker
var worker = app.Services.GetRequiredService<IBackgroundIngestionWorker>();
var cts = new CancellationTokenSource();
_ = worker.StartAsync(cts.Token);

app.Lifetime.ApplicationStopping.Register(async () =>
{
    await worker.StopAsync();
});

await app.RunAsync();

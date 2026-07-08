using System.Text.Json.Serialization;
using CodexQueue.Api.Data;
using CodexQueue.Api.Endpoints;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;

SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var connectionString = builder.Configuration.GetConnectionString("CodexQueue")
    ?? "Data Source=data/codex-queue.db";
var dataSource = connectionString.Split("Data Source=", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .FirstOrDefault();
if (!string.IsNullOrWhiteSpace(dataSource))
{
    var directory = Path.GetDirectoryName(dataSource);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }
}

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddCors(options =>
{
    options.AddPolicy("dev", policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod());
});
builder.Services.AddSingleton<ITargetCommandRunner, TargetCommandRunner>();
builder.Services.AddScoped<IProjectFileService, ProjectFileService>();
builder.Services.AddSingleton<QueueWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<QueueWorker>());
builder.Services.AddSingleton<IQueueCoordinator>(sp => sp.GetRequiredService<QueueWorker>());

var app = builder.Build();

app.UseCors("dev");
app.UseWebSockets();
app.UseMiddleware<ApiTokenMiddleware>();

await DbInitializer.InitializeAsync(app.Services);

app.MapCodexQueueApi();

app.Run();

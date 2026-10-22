using Karry.BuildingBlocks.Modules;
using Karry.Host;
using Karry.Host.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Karry");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
}

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

builder.Services.AddModules(builder.Configuration, ModuleCatalog.All);

var app = builder.Build();

// Liveness: the process is up. Readiness: dependencies are reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapModules();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can host the app with <c>WebApplicationFactory</c>.</summary>
public partial class Program;

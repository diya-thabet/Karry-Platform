using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Observability;
using Karry.Host;
using Karry.Host.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddKarryObservability("karry-api");

var connectionString = builder.Configuration.GetConnectionString("Karry");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
}

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

builder.Services.AddKarryApiConventions();
builder.Services.AddOpenApi("v1");
builder.Services.AddModules(builder.Configuration, ModuleCatalog.All);

var app = builder.Build();

app.UseKarryObservability();
app.UseKarryApiConventions();

// Liveness: the process is up. Readiness: dependencies are reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapModules();

// The API description is published outside production (CI diffs it from P1.5; clients are generated from it).
if (!app.Environment.IsProduction())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can host the app with <c>WebApplicationFactory</c>.</summary>
public partial class Program;

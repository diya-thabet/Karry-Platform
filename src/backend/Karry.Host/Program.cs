using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Observability;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
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
    builder.Services.AddIdempotency(connectionString);
}

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

builder.Services.AddKarryApiConventions();
builder.Services.AddKarryTenancy(builder.Configuration);
builder.Services.AddOpenApi("v1");
builder.Services.AddModules(builder.Configuration, ModuleCatalog.All);

var app = builder.Build();

// `Karry.Host migrate`: apply every module's migrations as the schema owner, then exit (deploy step, P1.17).
if (args is ["migrate", ..])
{
    var ownerConnectionString = app.Configuration.GetConnectionString("KarryOwner");
    if (string.IsNullOrWhiteSpace(ownerConnectionString))
    {
        await Console.Error.WriteLineAsync("ConnectionStrings:KarryOwner is required to migrate.").ConfigureAwait(false);
        return 2;
    }

    await MigrationRunner.MigrateAsync(app.Services, ownerConnectionString, CancellationToken.None).ConfigureAwait(false);
    return 0;
}

app.UseKarryObservability();
app.UseKarryApiConventions();
app.UseKarryTenancy();
if (!string.IsNullOrWhiteSpace(connectionString))
{
    app.UseIdempotency();
}

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
return 0;

/// <summary>Entry point; public so integration tests can host the app with <c>WebApplicationFactory</c>.</summary>
public partial class Program;

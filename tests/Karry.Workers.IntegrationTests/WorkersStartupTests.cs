using System.Reflection;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Observability;
using Karry.BuildingBlocks.Persistence;
using Karry.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace Karry.Workers.IntegrationTests;

/// <summary>One PostgreSQL server and one migrated database for the class (the workers read; each test writes nothing it asserts on).</summary>
public sealed class WorkersDatabase : IAsyncLifetime
{
    private PostgresServer? server;

    public KarryDatabase Database { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PostgresServer.StartAsync();
        Database = await server.NewKarryDatabaseAsync();
        using var host = WorkersStartupTests.Build(Environments.Production, Database.App);
        await MigrationRunner.MigrateAsync(host.Services, Database.Owner, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }
}

/// <summary>
/// The background host as it is deployed (<c>Karry.Workers</c>): its container is complete (validated on build as in
/// Development, so a module whose infrastructure needs a service only the API registers fails here, not at 3 a.m.), every
/// scheduled job runs against a freshly migrated database, its one-shot commands answer with the documented exit codes,
/// and its permission catalogue has every module that declares permissions (the import runner fails closed otherwise).
/// </summary>
[Trait("Story", "US-P1-PLT-03")]
public sealed class WorkersStartupTests(WorkersDatabase fixture) : IClassFixture<WorkersDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Unreachable = "Host=127.0.0.1;Port=1;Database=karry;Username=karry_app;Password=unused";

    /// <summary>The host exactly as Program composes it, without appsettings files, with the given settings on top.</summary>
    internal static IHost Build(string environment, string connectionString, IReadOnlyDictionary<string, string?>? settings = null, bool jobs = true)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = false,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Karry"] = connectionString });
        builder.Configuration.AddInMemoryCollection(settings ?? new Dictionary<string, string?>());
        builder.AddKarryObservability("karry-workers");
        builder.Services.AddWorkerModules(builder.Configuration);
        if (jobs)
        {
            builder.Services.AddWorkerJobs(builder.Configuration);
        }

        return builder.Build();
    }

    [Fact]
    public void The_container_is_complete_as_validated_in_development_without_optional_services()
    {
        var build = () => Build(Environments.Development, Unreachable).Dispose();

        build.Should().NotThrow("every service a worker resolves is registered (ValidateOnBuild), and no singleton captures a scoped one (ValidateScopes)");
    }

    [Fact]
    public void The_container_is_complete_with_anchoring_keycloak_storage_and_the_server_signer_configured()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), $"karry-anchor-{Guid.NewGuid():N}.pem");
        var settings = new Dictionary<string, string?>
        {
            ["Trust:Anchoring:ServiceUrl"] = "http://127.0.0.1:1",
            ["Trust:Anchoring:Bucket"] = "karry-anchors",
            ["Trust:Anchoring:AccessKey"] = "test-only",
            ["Trust:Anchoring:SecretKey"] = "test-only-secret",
            ["Trust:Anchoring:SigningKeyFile"] = keyFile,
            ["Trust:Anchoring:GenerateKeyIfMissing"] = "true",
            ["Trust:ServerSigning:Address"] = "http://127.0.0.1:1",
            ["Trust:ServerSigning:Token"] = "test-only",
            ["Trust:ServerSigning:KeyName"] = "karry-server",
            ["Storage:ServiceUrl"] = "http://127.0.0.1:1",
            ["Storage:Bucket"] = "karry-files",
            ["Storage:AccessKey"] = "test-only",
            ["Storage:SecretKey"] = "test-only-secret",
            ["Storage:Evidence:Bucket"] = "karry-evidence",
            ["Identity:Keycloak:BaseUrl"] = "http://127.0.0.1:1",
            ["Identity:Keycloak:ClientSecret"] = "test-only",
        };
        try
        {
            using var host = Build(Environments.Development, Unreachable, settings);

            var scheduler = host.Services.GetRequiredService<ISchedulerFactory>();
            scheduler.Should().NotBeNull();
            WorkerServices.Jobs(anchoring: true, keycloak: true).Select(j => j.Name).Should().Contain(["ledger-anchoring", "sign-in-failure-import"]);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public void Every_schedule_is_a_valid_cron_expression_with_a_unique_name_and_optional_jobs_only_when_configured()
    {
        var all = WorkerServices.Jobs(anchoring: true, keycloak: true);

        all.Should().AllSatisfy(j => FluentActions.Invoking(() => new CronExpression(j.Cron)).Should().NotThrow("Quartz would refuse to start on an invalid schedule"));
        all.Select(j => j.Name).Should().OnlyHaveUniqueItems();
        all.Select(j => j.JobType).Should().OnlyContain(t => typeof(IJob).IsAssignableFrom(t));
        WorkerServices.Jobs(anchoring: false, keycloak: false).Select(j => j.Name).Should().NotContain(["ledger-anchoring", "sign-in-failure-import"])
            .And.HaveCount(all.Count - 2);
    }

    [Fact]
    public async Task Every_scheduled_job_runs_twice_on_a_fresh_database_without_error()
    {
        using var host = Build(Environments.Production, fixture.Database.App);

        foreach (var job in WorkerServices.Jobs(anchoring: false, keycloak: false))
        {
            for (var run = 0; run < 2; run++)
            {
                await using var scope = host.Services.CreateAsyncScope();
                var instance = (IJob)ActivatorUtilities.CreateInstance(scope.ServiceProvider, job.JobType);
                var execute = async () => await instance.Execute(null!, Ct);
                await execute.Should().NotThrowAsync($"job {job.Name} (run {run + 1}) must cope with a company-less database");
            }
        }
    }

    [Theory]
    [InlineData("dispatch-now")]
    [InlineData("advance-subscriptions")]
    [InlineData("snapshot-usage")]
    [InlineData("measure-sync-gaps")]
    [InlineData("generate-shifts")]
    [InlineData("delete-import-files")]
    public async Task A_one_shot_command_on_a_fresh_database_exits_0(string command)
    {
        (await RunAsync(command, fixture.Database.App)).Should().Be(0);
    }

    [Theory]
    [InlineData("anchor-now")]
    [InlineData("import-sign-ins")]
    public async Task A_command_whose_service_is_not_configured_exits_2(string command)
    {
        (await RunAsync(command, fixture.Database.App)).Should().Be(2, "the operator is told what is missing instead of a stack trace");
    }

    [Fact]
    public async Task Without_a_connection_string_the_host_refuses_to_start()
    {
        var run = () => RunAsync("dispatch-now", connectionString: null);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ConnectionStrings:Karry*", "an unset deployment variable is refused before any module uses it");
    }

    [Fact]
    public void The_permission_catalogue_has_every_module_that_declares_permissions()
    {
        var declaring = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } name && name.StartsWith("Karry.Modules.", StringComparison.Ordinal) && name.EndsWith(".Api", StringComparison.Ordinal))
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (IModule)Activator.CreateInstance(t)!)
            .Where(m => m.Permissions.Count > 0)
            .Select(m => m.Name)
            .ToList();

        declaring.Should().NotBeEmpty();
        WorkerModules.All.Select(m => m.Name).Should().Contain(declaring, "a module missing here grants nothing to an import's requester (fail closed, but broken)");
        WorkerModules.Catalog.Should().NotBeNull();
    }

    /// <summary>Runs the real entry point (<c>Program</c>) with the command and a "Testing" environment (no appsettings file applies).</summary>
    private static async Task<int> RunAsync(string command, string? connectionString)
    {
        var entry = typeof(WorkerModules).Assembly.EntryPoint!;
        string[] args = [command, "--environment", "Testing", $"--ConnectionStrings:Karry={connectionString}"];

        // The compiler's entry point of an async Main blocks on it: run it off the test thread.
        return await Task.Run(() =>
        {
            try
            {
                return entry.Invoke(null, [args]) switch
                {
                    int code => code,
                    Task<int> task => task.GetAwaiter().GetResult(),
                    var other => throw new InvalidOperationException($"Unexpected entry point result {other}."),
                };
            }
            catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(invocation.InnerException);
                throw;
            }
        });
    }
}

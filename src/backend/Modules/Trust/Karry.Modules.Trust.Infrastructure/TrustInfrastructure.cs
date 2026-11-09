using System.Security.Cryptography;
using Karry.BuildingBlocks.Ledger;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Trust.Infrastructure;

public static class TrustInfrastructure
{
    public static IServiceCollection AddTrustInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddModuleDbContext<TrustDbContext>(connectionString);
        services.AddSingleton<ILedgerWriter, PostgresLedgerWriter>();
        services.AddScoped<ILedgerReader, EfLedgerReader>();
        services.AddScoped<IAuditLog, EfAuditLog>();
        services.AddScoped<AuditLogQueries>();
        services.AddScoped<IAuditExportStore, EfAuditExportStore>();
        services.AddScoped<AuditExports>();
        services.AddScoped<IAnchorBookkeeping, EfAnchorBookkeeping>();
        // The process's one pool for this database (KarryDataSources, D50), so the lock costs no extra pool.
        services.TryAddSingleton<KarryDataSources>();
        services.AddSingleton<IAnchoringLock>(sp => new PostgresAnchoringLock(sp.GetRequiredService<KarryDataSources>().For(connectionString)));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<VerifyLedgerHandler>();
        services.AddScoped<IVerificationRecords, EfVerificationRecords>();
        services.AddScoped<Contracts.IEnvelopeRecorder, PostgresEnvelopeRecorder>();
        services.AddScoped<ICountersignBook, EfCountersignBook>();
        services.AddScoped<IServerKeyBook, EfServerKeyBook>();
        services.AddScoped<IEnvelopeEvidence, EfEnvelopeEvidence>();
        services.AddScoped<VerifyEnvelope>();
        services.AddScoped<BuildingBlocks.Tenancy.ITenantSummarySource, LedgerActivity>();
        services.AddScoped<IntegrityChecks>();
        services.AddSingleton<ILedgerSubjectBackfill, LedgerSubjectBackfill>();
        // The event type registry (every module's events in a host): the backfill and the audit viewer read entries
        // back as their typed events.
        services.AddOutboxDispatcher<TrustDbContext>(eventAssemblies: typeof(AuditExport).Assembly);
        return services;
    }

    /// <summary>
    /// WORM anchoring (US-P1-AUD-02) from <c>Trust:Anchoring</c>; nothing is registered when no bucket is configured.
    /// A configured bucket without a signing key is a startup error: anchors are never written unsigned.
    /// </summary>
    public static IServiceCollection AddTrustAnchoring(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new AnchoringOptions();
        configuration.GetSection(AnchoringOptions.Section).Bind(options);
        if (string.IsNullOrWhiteSpace(options.Bucket))
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(_ => new SoftwareAnchorSigner(LoadKey(options), options.TrustedPublicKeys));
        services.AddSingleton<IAnchorSigner>(sp => sp.GetRequiredService<SoftwareAnchorSigner>());
        services.AddSingleton<IAnchorVerifier>(sp => sp.GetRequiredService<SoftwareAnchorSigner>());
        services.AddSingleton<IAnchorStore, S3AnchorStore>();
        services.AddMetrics();
        services.TryAddSingleton<AnchoringMetrics>();
        services.AddSingleton<AnchorLedgers>();
        return services;
    }

    /// <summary>
    /// The server signing key (ADR-0033 §3) from <c>Trust:ServerSigning</c>: OpenBao transit when an address is set,
    /// otherwise <see cref="UnavailableServerSigner"/> (nothing is countersigned; envelopes stay pending).
    /// </summary>
    public static IServiceCollection AddTrustServerSigning(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.TryAddSingleton<CountersignMetrics>();
        services.TryAddSingleton<CountersignEnvelopes>();
        var options = new ServerSigningOptions();
        configuration.GetSection(ServerSigningOptions.Section).Bind(options);
        if (string.IsNullOrWhiteSpace(options.Address))
        {
            services.TryAddSingleton<IServerSigner, UnavailableServerSigner>();
            return services;
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException($"{ServerSigningOptions.Section}: an address is configured but no token.");
        }

        services.AddSingleton(options);
        services.TryAddSingleton<IServerSigner>(_ => new OpenBaoTransitSigner(options));
        return services;
    }

    private static ECDsa LoadKey(AnchoringOptions options)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (!string.IsNullOrWhiteSpace(options.SigningKeyPem))
        {
            key.ImportFromPem(options.SigningKeyPem);
            return key;
        }

        if (string.IsNullOrWhiteSpace(options.SigningKeyFile))
        {
            key.Dispose();
            throw new InvalidOperationException($"{AnchoringOptions.Section}: a bucket is configured but no signing key (SigningKeyPem or SigningKeyFile).");
        }

        var path = options.SigningKeyFile.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), options.SigningKeyFile[2..])
            : options.SigningKeyFile;
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }

        if (!options.GenerateKeyIfMissing)
        {
            key.Dispose();
            throw new InvalidOperationException($"{AnchoringOptions.Section}: signing key file '{path}' not found.");
        }

        // Local development only: a key that survives restarts, so earlier anchors stay verifiable.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }
}

using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Storage;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.Documents.Api;
using Karry.Modules.FieldOps.Contracts;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Api;
using Karry.Modules.Trust.Contracts;
using Karry.Modules.Verification.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Documents.Tests;

/// <summary>
/// The Documents module over HTTP on real PostgreSQL, with tenants and the ledger as in production; what the other
/// modules know (sites, entries, reviews, names, machines, signature images, evidence) is <see cref="Field"/>.
/// </summary>
public sealed class DocumentsHost : IAsyncLifetime
{
    private PostgresServer? server;
    private WebApplication? app;

    public Field Field { get; } = new();

    /// <summary>The server's signing service (Trust): down until a test brings it up.</summary>
    public SigningService Signer { get; } = new();

    /// <summary>The verification address of each issued document (the token is only in its QR).</summary>
    public Renderer Renderer { get; } = new();

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => app!.Services;

    public async ValueTask InitializeAsync()
    {
        server = await PostgresServer.StartAsync();
        Database = await server.NewKarryDatabaseAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Karry"] = Database.App,
            ["Documents:VerifyOrigin"] = "https://verify.karry.test",
        });
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryTenancy(builder.Configuration);
        builder.Services.TrustTestIssuer();
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new DocumentsModule(), new Trust.Api.TrustModule()]);
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin);
        builder.Services.AddSingleton<ISiteDirectory>(Field);
        builder.Services.AddSingleton<IShiftEntriesOfShift>(Field);
        builder.Services.AddSingleton<IReviewOutcomes>(Field);
        builder.Services.AddSingleton<IUserNames>(Field);
        builder.Services.AddSingleton<IAssetDirectory>(Field);
        builder.Services.AddSingleton<IEnvelopeImages>(Field);
        builder.Services.AddSingleton<IEvidenceStore>(Field);
        builder.Services.AddSingleton<IDigestSigner>(Signer);
        builder.Services.AddSingleton<Karry.Modules.Documents.Application.IShiftReportRenderer>(Renderer);
        builder.Services.AddSingleton<Karry.Modules.Documents.Application.SignDocuments>();
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
        app.MapModules();

        await MigrationRunner.MigrateAsync(app.Services, Database.Owner, CancellationToken.None);
        await app.StartAsync();
        Client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }

        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }
}

/// <summary>What the field modules hold, per site: its entries, their reviews and signature images, and the evidence.</summary>
public sealed class Field : ISiteDirectory, IShiftEntriesOfShift, IReviewOutcomes, IUserNames, IAssetDirectory, IEnvelopeImages, IEvidenceStore
{
    public static readonly Guid Machine = Guid.Parse("0192f0a1-0000-7000-8000-0000000000e1");

    public static readonly Guid Operator = Guid.Parse("0192f0a1-0000-7000-8000-0000000000f1");

    public ConcurrentDictionary<Guid, SiteSummary> Sites { get; } = new();

    public ConcurrentDictionary<Guid, List<ShiftReportEntry>> Entries { get; } = new();

    public ConcurrentDictionary<Guid, string> Images { get; } = new();

    /// <summary>Evidence objects by key.</summary>
    public ConcurrentDictionary<string, byte[]> Evidence { get; } = new();

    public bool Available => true;

    public Task<SiteSummary?> FindAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(Sites.GetValueOrDefault(siteId));

    public Task<IReadOnlyList<SiteSummary>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SiteSummary>>([.. Sites.Values]);

    public Task<IReadOnlyList<ShiftReportEntry>> OfShiftAsync(Guid siteId, DateOnly shiftDate, string shiftCode, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShiftReportEntry>>(Entries.GetValueOrDefault(siteId) ?? []);

    public Task<IReadOnlyList<ReviewOutcome>> OfRecordsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReviewOutcome>>([.. recordIds.Select(id => new ReviewOutcome(id, 1, "approved", "ctl-1", DateTimeOffset.UtcNow, null))]);

    public Task<IReadOnlyDictionary<string, string>> BySubjectAsync(IReadOnlyCollection<string> subjects, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(subjects.ToDictionary(s => s, _ => "Awa Camara"));

    public Task<IReadOnlyDictionary<Guid, string>> ByIdAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(userIds.ToDictionary(id => id, _ => "Mamadou Diallo"));

    Task<AssetSummary?> IAssetDirectory.FindAsync(Guid assetId, CancellationToken cancellationToken) =>
        Task.FromResult<AssetSummary?>(new AssetSummary(assetId, "PEL-01", "excavator", []));

    public Task<IReadOnlyDictionary<Guid, string>> ImageHashesAsync(IReadOnlyCollection<Guid> envelopeIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(envelopeIds.Where(Images.ContainsKey).ToDictionary(id => id, id => Images[id]));

    public Task<BlobHead?> HeadAsync(string key, CancellationToken cancellationToken) => Task.FromResult<BlobHead?>(null);

    public PresignedPut PresignPut(string key, string sha256, string contentType, DateTimeOffset expiresAt) => throw new NotSupportedException();

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Evidence.TryGetValue(key, out var bytes) ? new StoredObject(bytes, "image/png") : null);
}

/// <summary>The signing service as Trust's contract answers it: null while down; it keeps the digests it signed.</summary>
public sealed class SigningService : IDigestSigner
{
    public bool Up { get; set; }

    public ConcurrentQueue<byte[]> Digests { get; } = new();

    public Task<DigestSignature?> TrySignAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken)
    {
        if (!Up)
        {
            return Task.FromResult<DigestSignature?>(null);
        }

        Digests.Enqueue(sha256Digest.ToArray());
        return Task.FromResult<DigestSignature?>(new DigestSignature("karry-server-v1", Convert.ToBase64String(sha256Digest.Span)));
    }
}

/// <summary>The real PDF renderer, keeping the verification address (with its token) of every document it drew.</summary>
public sealed class Renderer : Karry.Modules.Documents.Application.IShiftReportRenderer
{
    public ConcurrentDictionary<Guid, Uri> Addresses { get; } = new();

    public byte[] Render(Karry.Modules.Documents.Domain.ShiftReportContent content, Uri verification, IReadOnlyDictionary<string, byte[]> images, string language)
    {
        ArgumentNullException.ThrowIfNull(verification);
        Addresses[Guid.Parse(verification.Segments[^1])] = verification;
        return Karry.Modules.Documents.Infrastructure.ShiftReportPdf.Render(content, verification, images, language);
    }
}

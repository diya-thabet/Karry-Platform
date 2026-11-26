using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.Documents.Api;
using Karry.Modules.Documents.Application;
using Karry.Modules.Documents.Domain;
using Karry.Modules.FieldOps.Contracts;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Trust.Contracts;
using Karry.Modules.Verification.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Documents.Tests;

/// <summary>
/// The shift report's content (ADR-0040 §2, §3, US-P2-SIG-05 AC1): a site's entries of a shift read through the owning
/// modules' contracts, only for a reader of that site; masked for the reader (TC-P2-DOC-003); its canonical hash.
/// </summary>
[Trait("Story", "US-P2-SIG-05")]
public sealed class ShiftReportContentsTests
{
    private static readonly TenantId Company = new(Guid.Parse("0192f0a1-0000-7000-8000-0000000000a1"));
    private static readonly SiteSummary Site = new(Guid.Parse("0192f0a1-0000-7000-8000-000000000051"), "CAR-KAK", "Carrière de Kakoulima", "Africa/Conakry", false);
    private static readonly DateOnly Day = new(2026, 11, 26);
    private static readonly Guid Excavator = Guid.Parse("0192f0a1-0000-7000-8000-0000000000e1");
    private static readonly Guid Operator = Guid.Parse("0192f0a1-0000-7000-8000-0000000000f1");

    private readonly Contracts world = new();

    [Fact]
    public async Task A_shift_report_lists_each_entry_with_machine_operator_values_delta_review_controller_flags_and_signature()
    {
        var (reviewed, open) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        world.Entries.Add(Entry(reviewed, 1250.0m, 1258.5m, ["RULE-FO-02"], Guid.Parse("0192f0a1-0000-7000-8000-00000000e001")));
        world.Entries.Add(Entry(open, 1258.5m, null, [], Guid.Parse("0192f0a1-0000-7000-8000-00000000e002")));
        world.Outcomes.Add(new ReviewOutcome(reviewed, 1, "approved", "ctl-1", DateTimeOffset.UtcNow, null));
        world.Images[Guid.Parse("0192f0a1-0000-7000-8000-00000000e001")] = "sha256:" + new string('9', 64);

        var content = (await Contents(Reader(Roles.SiteManager, Site.Id)).OfShiftAsync(Site.Id, Day, "M", CancellationToken.None)).Value;

        (content.Company, content.SiteCode, content.ShiftDate, content.ShiftCode).Should().Be(("Carrière Démo B", "CAR-KAK", Day, "M"));
        content.Columns.Should().Equal("machine", "operator", "start", "end", "delta", "unit", "status", "review", "controller", "flags", "signature");
        string[][] expected =
        [
            ["PEL-01", "Mamadou Diallo", "1250.0", "1258.5", "8.5", "hours", "closed", "approved", "Awa Camara", "RULE-FO-02", "sha256:" + new string('9', 64)],
            ["PEL-01", "Mamadou Diallo", "1258.5", "", "", "hours", "open", "not_reviewed", "", "", ""],
        ];
        content.Rows.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task A_reader_of_another_site_gets_the_same_not_found_as_an_unknown_site_and_a_shift_code_is_required()
    {
        var elsewhere = await Contents(Reader(Roles.SiteManager, Guid.CreateVersion7())).OfShiftAsync(Site.Id, Day, "M", CancellationToken.None);
        var unknown = await Contents(Reader(Roles.SiteManager, null)).OfShiftAsync(Guid.CreateVersion7(), Day, "M", CancellationToken.None);
        var noShift = await Contents(Reader(Roles.SiteManager, null)).OfShiftAsync(Site.Id, Day, " ", CancellationToken.None);

        (elsewhere.Error, unknown.Error, noShift.Error).Should().Be((ShiftReportContents.NotFound, ShiftReportContents.NotFound, ShiftReportContents.ShiftInvalid));
    }

    [Fact]
    public async Task A_shift_beyond_the_limit_is_refused_rather_than_printed_short()
    {
        for (var i = 0; i <= IShiftEntriesOfShift.MaxEntries; i++)
        {
            world.Entries.Add(Entry(Guid.CreateVersion7(), i, i + 1, [], Guid.CreateVersion7()));
        }

        (await Contents(Reader(Roles.TenantAdmin, null)).OfShiftAsync(Site.Id, Day, "M", CancellationToken.None)).Error.Should().Be(ShiftReportContents.TooLong);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-DOC-003")]
    public void A_column_the_readers_role_may_not_see_is_absent_from_their_report_and_present_for_a_role_that_may()
    {
        var rows = new[] { new ShiftReportRow(Guid.CreateVersion7(), Site.Id, "PEL-01", "Mamadou Diallo", "1.0", "2.0", "1.0", "hours", "closed", "approved", "Awa", "", "") };
        var columns = (IReadOnlyList<ExportColumn<ShiftReportRow>>)[.. ShiftReportColumns.All, new("fuel_cost", _ => "1 250 GNF", Probe.FuelCost)];

        var field = ShiftReportContents.Compose(Reader(Roles.Operator, Site.Id).Current, columns, rows, "C", Site, Day, "M");
        var manager = ShiftReportContents.Compose(Reader(Roles.SiteManager, Site.Id).Current, columns, rows, "C", Site, Day, "M");

        field.Columns.Should().NotContain("fuel_cost", "a masked column is absent, not blanked");
        field.Rows[0].Should().NotContain("1 250 GNF");
        manager.Columns.Should().Contain("fuel_cost");
        manager.Rows[0].Should().Contain("1 250 GNF");
        field.Hash().Should().NotBe(manager.Hash(), "each reader's document is bound to what it shows");
    }

    [Fact]
    public void The_content_hash_is_the_sha256_of_its_canonical_form_and_any_change_changes_it()
    {
        var content = new ShiftReportContent("C", "S", "Site", Day, "M", ["machine", "start"], [["PEL-01", "1.0"]]);
        var same = new ShiftReportContent("C", "S", "Site", Day, "M", ["machine", "start"], [["PEL-01", "1.0"]]);
        var changed = content with { Rows = [["PEL-01", "1.1"]] };

        content.Hash().Should().MatchRegex("^sha256:[0-9a-f]{64}$").And.Be(same.Hash());
        changed.Hash().Should().NotBe(content.Hash());
        System.Text.Encoding.UTF8.GetString(content.Canonical()).Should().StartWith("{\"columns\":[\"machine\",\"start\"],\"company\":\"C\"", "RFC 8785 sorts the keys");
    }

    private static ShiftReportEntry Entry(Guid root, decimal start, decimal? end, string[] flags, Guid envelope) =>
        new(root, 1, Excavator, Operator, "hours", DateTimeOffset.UtcNow.AddHours(-8), start, end is null ? null : DateTimeOffset.UtcNow, end,
            end is null ? "open" : "closed", flags, envelope, null, false);

    private ShiftReportContents Contents(ICallerAccess reader)
    {
        var tenant = new TenantContext();
        tenant.Set(Company);
        return new ShiftReportContents(reader, tenant, world, world, world, world, world, world, world);
    }

    /// <summary>A reader holding <paramref name="role"/> on <paramref name="site"/> (or company-wide), with the role's default field access.</summary>
    private static Holder Reader(string role, Guid? site)
    {
        var catalog = AuthorizationCatalog.From([new DocumentsModule(), new Probe()]);
        var permissions = catalog.Permissions.Where(p => p.DefaultRoles.Contains(role)).Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
        var fields = catalog.SensitiveFields.ToDictionary(f => f.Code, f => f.Defaults.GetValueOrDefault(role, f.Fallback), StringComparer.Ordinal);
        return new Holder(CallerAccess.From(catalog, [new AccessAssignment(role, permissions, fields, site)]));
    }

    private sealed class Holder(CallerAccess access) : ICallerAccess
    {
        public CallerAccess Current { get; private set; } = access;

        public string? Subject => "reader-1";

        public DateTimeOffset? SignedInAt => null;

        public void Set(CallerAccess access, string? subject, DateTimeOffset? signedInAt) => Current = access;
    }

    /// <summary>A module declaring a sensitive field (as a fuel cost would be): read by managers, hidden from field roles.</summary>
    private sealed class Probe : IModule
    {
        public const string FuelCost = "probe.fuel.cost";

        public string Name => "probe";

        public IReadOnlyList<PermissionDefinition> Permissions => [];

        public IReadOnlyList<SensitiveFieldDefinition> SensitiveFields =>
            [new(FuelCost, new Dictionary<string, FieldAccess> { [Roles.SiteManager] = FieldAccess.Read, [Roles.Operator] = FieldAccess.Hidden })];

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }

    /// <summary>The other modules as their contracts answer, for one company with one site.</summary>
    private sealed class Contracts : ITenantDirectory, ISiteDirectory, IShiftEntriesOfShift, IReviewOutcomes, IUserNames, IAssetDirectory, IEnvelopeImages
    {
        public List<ShiftReportEntry> Entries { get; } = [];

        public List<ReviewOutcome> Outcomes { get; } = [];

        public Dictionary<Guid, string> Images { get; } = [];

        public Task<IReadOnlyList<TenantId>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TenantId>>([Company]);

        public Task<TenantSummary?> FindAsync(TenantId tenant, CancellationToken cancellationToken) =>
            Task.FromResult<TenantSummary?>(tenant == Company ? new TenantSummary(Company, "demo-carriere-b", "Carrière Démo B") : null);

        public Task<SiteSummary?> FindAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(siteId == Site.Id ? Site : null);

        Task<IReadOnlyList<SiteSummary>> ISiteDirectory.ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SiteSummary>>([Site]);

        public Task<IReadOnlyList<ShiftReportEntry>> OfShiftAsync(Guid siteId, DateOnly shiftDate, string shiftCode, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ShiftReportEntry>>(siteId == Site.Id ? Entries : []);

        public Task<IReadOnlyList<ReviewOutcome>> OfRecordsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReviewOutcome>>([.. Outcomes.Where(o => recordIds.Contains(o.RecordId))]);

        public Task<IReadOnlyDictionary<string, string>> BySubjectAsync(IReadOnlyCollection<string> subjects, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(subjects.Contains("ctl-1") ? new Dictionary<string, string> { ["ctl-1"] = "Awa Camara" } : []);

        public Task<IReadOnlyDictionary<Guid, string>> ByIdAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(userIds.Contains(Operator) ? new Dictionary<Guid, string> { [Operator] = "Mamadou Diallo" } : []);

        Task<AssetSummary?> IAssetDirectory.FindAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult<AssetSummary?>(assetId == Excavator ? new AssetSummary(Excavator, "PEL-01", "excavator", []) : null);

        public Task<IReadOnlyDictionary<Guid, string>> ImageHashesAsync(IReadOnlyCollection<Guid> envelopeIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(Images.Where(i => envelopeIds.Contains(i.Key)).ToDictionary());
    }
}
